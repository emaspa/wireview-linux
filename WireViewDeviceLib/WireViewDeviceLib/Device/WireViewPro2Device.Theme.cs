using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;

namespace WireView2.Device
{
    /// <summary>
    /// SPI-flash write/erase primitives and theme asset (background / fan bitmap)
    /// read-write API, ported from the upstream 1.0.7 Windows client. Wire protocol:
    /// CMD_SPI_FLASH_WRITE_PAGE (9) and CMD_SPI_FLASH_ERASE_SECTOR (11), both framed
    /// as [cmd, addr LE32, len LE32] and acknowledged with a single status byte == 1.
    /// Writes go through a sector-preserving read-modify-erase-write cycle.
    /// </summary>
    public partial class WireViewPro2Device
    {
        private const byte CmdSpiFlashWritePage = (byte)UsbCmd.CMD_SPI_FLASH_WRITE_PAGE;
        private const byte CmdSpiFlashEraseSector = (byte)UsbCmd.CMD_SPI_FLASH_ERASE_SECTOR;
        private const int SpiFlashPageSize = 256;
        private const int SpiFlashSectorSize = 4096;
        private const uint SpiFlashTotalSizeBytes = 16 * 1024 * 1024;

        // Theme asset geometry (RGB565, stored column-major and horizontally
        // mirrored on the device's external flash).
        public const int ThemeBackgroundWidth = 320;
        public const int ThemeBackgroundHeight = 170;
        public const int ThemeBackgroundSizeBytes = ThemeBackgroundWidth * ThemeBackgroundHeight * 2; // 108800
        public const int ThemeFanWidth = 73;
        public const int ThemeFanHeight = 73;
        public const int ThemeFanFrameSizeBytes = ThemeFanWidth * ThemeFanHeight * 2; // 10658

        // ---- Low-level write/erase (port must already be open; caller holds the lock) ----

        private void SpiFlashWriteAllChunked(ReadOnlySpan<byte> data, int chunkSize = 64, int interChunkDelayMs = 1)
        {
            if (_port == null) throw new InvalidOperationException("Device not connected.");

            int offset = 0;
            while (offset < data.Length)
            {
                int count = Math.Min(chunkSize, data.Length - offset);
                byte[] chunk = data.Slice(offset, count).ToArray();
                _port.Write(chunk, 0, chunk.Length);
                offset += count;
                if (interChunkDelayMs > 0)
                    Thread.Sleep(interChunkDelayMs);
            }
        }

        private bool SpiFlashReadResult(uint timeoutMs)
        {
            int status = 0;
            DateTime start = DateTime.UtcNow;
            while (status == 0 && DateTime.UtcNow < start.AddMilliseconds(timeoutMs))
            {
                if (_port!.BytesToRead > 0)
                {
                    var buf = new byte[1];
                    _port.Read(buf, 0, 1);
                    status = buf[0];
                }
                else
                {
                    Thread.Sleep(1);
                }
            }
            return status == 1;
        }

        private bool SpiFlashEraseRangeNoLock(uint addr, uint len)
        {
            var frame = new byte[9];
            frame[0] = CmdSpiFlashEraseSector;
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1, 4), addr);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(5, 4), len);
            SpiFlashWriteAllChunked(frame, 64, 0);
            return SpiFlashReadResult(len / SpiFlashSectorSize * 100);
        }

        private void SpiFlashWritePageNoLock(uint addr, ReadOnlySpan<byte> data)
        {
            if (data.Length == 0) return;
            if (data.Length > SpiFlashPageSize)
                throw new ArgumentOutOfRangeException(nameof(data), $"Max write length is {SpiFlashPageSize} bytes.");

            var frame = new byte[9];
            frame[0] = CmdSpiFlashWritePage;
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1, 4), addr);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(5, 4), (uint)data.Length);
            SpiFlashWriteAllChunked(frame, 64, 0);
            SpiFlashWriteAllChunked(data, 64, 0);
            if (!SpiFlashReadResult((uint)(data.Length * 100)))
                throw new InvalidOperationException($"Device reported error on page write at 0x{addr:X8}.");
        }

        // ---- Public bulk API ----

        /// <summary>Reads an arbitrary SPI-flash range (screen updates paused during
        /// the transfer). Public wrapper over the datalogger read path.</summary>
        public Task<byte[]> ReadSpiFlashBytesAsync(uint addr, uint len,
            IProgress<double>? progress = null, CancellationToken ct = default)
        {
            return SpiFlashReadBytesAsync(addr, len, progress, ct);
        }

        /// <summary>Writes a byte range to SPI flash preserving the untouched parts
        /// of the affected 4 KiB sectors: reads them back, patches in the data,
        /// erases the range, and rewrites it page by page. Screen updates are paused
        /// for the duration.</summary>
        public async Task WriteSpiFlashBytesPreserveSectorsAsync(uint addr, ReadOnlyMemory<byte> data,
            IProgress<double>? progress = null, CancellationToken ct = default)
        {
            if (!Connected || _port == null)
                throw new InvalidOperationException("Device not connected.");
            if (data.Length == 0) return;

            uint len = (uint)data.Length;
            if (addr + len > SpiFlashTotalSizeBytes)
                throw new ArgumentOutOfRangeException(nameof(addr), "Write exceeds SPI flash size.");

            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                uint firstSector = addr / SpiFlashSectorSize * SpiFlashSectorSize;
                uint endSector = (addr + len - 1) / SpiFlashSectorSize * SpiFlashSectorSize + SpiFlashSectorSize;
                uint lastPage = endSector - SpiFlashPageSize;
                uint rangeLen = endSector - firstSector;
                var buffer = new byte[rangeLen];

                lock (_port)
                {
                    _port.Open();
                    try
                    {
                        _port.DiscardInBuffer();
                        _port.Write(new byte[] { (byte)UsbCmd.CMD_SCREEN_CHANGE, (byte)SCREEN_CMD.SCREEN_PAUSE_UPDATES }, 0, 2);

                        // 1/3: read back the sectors we are about to erase
                        for (uint page = firstSector; page <= lastPage; page += SpiFlashPageSize)
                        {
                            ct.ThrowIfCancellationRequested();
                            byte[] read = SpiFlashReadPageNoLock(page, SpiFlashPageSize)
                                ?? throw new TimeoutException($"SPI flash read error at 0x{page:X8}.");
                            Buffer.BlockCopy(read, 0, buffer, (int)(page - firstSector), SpiFlashPageSize);
                            progress?.Report(Math.Clamp((double)(page - firstSector + SpiFlashPageSize) / (3.0 * rangeLen), 0.0, 1.0));
                        }

                        // 2/3: patch in the payload and erase
                        data.CopyTo(buffer.AsMemory((int)(addr - firstSector), (int)len));
                        if (!SpiFlashEraseRangeNoLock(firstSector, rangeLen))
                            throw new InvalidOperationException($"Device reported error erasing 0x{firstSector:X8}..0x{endSector:X8}.");
                        progress?.Report(0.66);

                        // 3/3: rewrite the whole range
                        for (uint page = firstSector; page <= lastPage; page += SpiFlashPageSize)
                        {
                            ct.ThrowIfCancellationRequested();
                            SpiFlashWritePageNoLock(page, buffer.AsSpan((int)(page - firstSector), SpiFlashPageSize));
                            progress?.Report(Math.Clamp(0.66 + (double)(page - firstSector + SpiFlashPageSize) / (3.0 * rangeLen), 0.0, 1.0));
                        }
                    }
                    finally
                    {
                        _port.Write(new byte[] { (byte)UsbCmd.CMD_SCREEN_CHANGE, (byte)SCREEN_CMD.SCREEN_RESUME_UPDATES }, 0, 2);
                        _port.Close();
                        progress?.Report(1.0);
                    }
                }
            }, ct).ConfigureAwait(false);
        }

        // ---- Theme asset offsets (on-device external flash addresses) ----

        private static uint GetThemeBackgroundOffset(THEME_BACKGROUND background) => background switch
        {
            THEME_BACKGROUND.ThermalGrizzlyOrange => 12288u,
            THEME_BACKGROUND.ThermalGrizzlyDark => 121088u,
            _ => throw new ArgumentOutOfRangeException(nameof(background), background, "Unsupported theme background"),
        };

        private static (uint Frame1Offset, uint Frame2Offset) GetThemeFanOffsets(THEME_FAN fan) => fan switch
        {
            THEME_FAN.ThermalGrizzlyOrange => (353140u, 374460u),
            THEME_FAN.ThermalGrizzlyDark => (363800u, 385120u),
            THEME_FAN.ThermalGrizzlyBlackWhite => (395780u, 406440u),
            _ => throw new ArgumentOutOfRangeException(nameof(fan), fan, "Unsupported theme fan"),
        };

        // ---- Theme asset API ----

        /// <summary>Returns the RGB565 background stored in the given slot, or null
        /// for <see cref="THEME_BACKGROUND.Disabled"/>.</summary>
        public Task<byte[]?> ReadThemeBackgroundRgb565Async(THEME_BACKGROUND background, CancellationToken ct = default)
        {
            if (background == THEME_BACKGROUND.Disabled)
                return Task.FromResult<byte[]?>(null);
            uint offset = GetThemeBackgroundOffset(background);
            return ReadSpiFlashBytesAsync(offset, ThemeBackgroundSizeBytes, null, ct)!;
        }

        public async Task<(byte[] Frame1, byte[] Frame2)> ReadThemeFanRgb565Async(THEME_FAN fan, CancellationToken ct = default)
        {
            var (frame1Addr, frame2Addr) = GetThemeFanOffsets(fan);
            byte[] frame1 = await ReadSpiFlashBytesAsync(frame1Addr, ThemeFanFrameSizeBytes, null, ct).ConfigureAwait(false);
            byte[] frame2 = await ReadSpiFlashBytesAsync(frame2Addr, ThemeFanFrameSizeBytes, null, ct).ConfigureAwait(false);
            return (frame1, frame2);
        }

        public Task WriteThemeBackgroundRgb565Async(THEME_BACKGROUND background, ReadOnlyMemory<byte> rgb565Bytes,
            IProgress<double>? progress = null, CancellationToken ct = default)
        {
            if (background == THEME_BACKGROUND.Disabled)
                throw new ArgumentOutOfRangeException(nameof(background), "Cannot write Disabled background.");
            if (rgb565Bytes.Length != ThemeBackgroundSizeBytes)
                throw new ArgumentException(
                    $"Background must be exactly {ThemeBackgroundSizeBytes} bytes (RGB565 {ThemeBackgroundWidth}x{ThemeBackgroundHeight}).",
                    nameof(rgb565Bytes));

            uint offset = GetThemeBackgroundOffset(background);
            return WriteSpiFlashBytesPreserveSectorsAsync(offset, rgb565Bytes, progress, ct);
        }

        public async Task WriteThemeFanRgb565Async(THEME_FAN fan, ReadOnlyMemory<byte> frame1Rgb565Bytes,
            ReadOnlyMemory<byte> frame2Rgb565Bytes, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            if (frame1Rgb565Bytes.Length != ThemeFanFrameSizeBytes)
                throw new ArgumentException(
                    $"Fan frame must be exactly {ThemeFanFrameSizeBytes} bytes (RGB565 {ThemeFanWidth}x{ThemeFanHeight}).",
                    nameof(frame1Rgb565Bytes));
            if (frame2Rgb565Bytes.Length != ThemeFanFrameSizeBytes)
                throw new ArgumentException(
                    $"Fan frame must be exactly {ThemeFanFrameSizeBytes} bytes (RGB565 {ThemeFanWidth}x{ThemeFanHeight}).",
                    nameof(frame2Rgb565Bytes));

            var (frame1Addr, frame2Addr) = GetThemeFanOffsets(fan);
            var progress1 = progress == null ? null : new Progress<double>(p => progress.Report(p * 0.5));
            var progress2 = progress == null ? null : new Progress<double>(p => progress.Report(0.5 + p * 0.5));
            await WriteSpiFlashBytesPreserveSectorsAsync(frame1Addr, frame1Rgb565Bytes, progress1, ct).ConfigureAwait(false);
            await WriteSpiFlashBytesPreserveSectorsAsync(frame2Addr, frame2Rgb565Bytes, progress2, ct).ConfigureAwait(false);
        }
    }
}
