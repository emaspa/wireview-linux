using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WireView2.Device;

public partial class WireViewPro2Device
{
    // Matches firmware
    private const uint SpiFlashSizeBytes = 0x1000000;          // 16MB
    private const uint SpiFlashSectorSizeBytes = 0x001000;     // 4KB
    private const uint DataloggerStartAddr = 8u * 1024 * 1024; // second 8MB
    private const uint DataloggerEndAddr = SpiFlashSizeBytes;

    private const int SpiFlashMaxReadLen = 256; // firmware buffer size (page)

    // Must match your device command enum value.
    private const byte CmdSpiFlashReadPage = (byte)UsbCmd.CMD_SPI_FLASH_READ_PAGE;

    // Firmware: CMD_SPI_FLASH_READ_PAGE expects payload: [cmd][addr:4][len:4] and returns len bytes.
    private async Task<byte[]> SpiFlashReadPageAsync(uint addr, uint len, CancellationToken ct)
    {
        if (!Connected || _port == null) throw new InvalidOperationException("Device not connected.");
        if (len == 0) return Array.Empty<byte>();
        if (len > SpiFlashMaxReadLen) len = SpiFlashMaxReadLen;

        var frame = new byte[1 + 4 + 4];
        frame[0] = CmdSpiFlashReadPage;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1, 4), addr);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(5, 4), len);


        byte[] result = await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            byte[]? rx = null;
            lock (_port)
            {
                _port!.Open();
                _port!.Write(frame, 0, frame.Length);
                rx = ReadExact((int)len);
                _port!.Close();
            }

            if (rx is null)
                throw new TimeoutException("SPI flash read timed out.");

            return Task.FromResult(rx);
        }, ct).ConfigureAwait(false);

        return result;

    }

    private byte[]? SpiFlashReadPageNoLock(uint addr, uint len)
    {
        if (!Connected || _port == null) throw new InvalidOperationException("Device not connected.");
        if (len == 0) return Array.Empty<byte>();
        if (len > SpiFlashMaxReadLen) len = SpiFlashMaxReadLen;

        var frame = new byte[1 + 4 + 4];
        frame[0] = CmdSpiFlashReadPage;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1, 4), addr);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(5, 4), len);

        byte[]? rx = null;
        _port!.Write(frame, 0, frame.Length);
        rx = ReadExact((int)len);
        return rx;

    }

    /// <summary>
    /// Reads exactly <paramref name="len"/> bytes from SPI flash starting at <paramref name="addr"/>.
    /// Internally performs multiple page reads (<= 256 bytes each).
    /// </summary>
    private async Task<byte[]> SpiFlashReadBytesAsync(
        uint addr,
        uint len,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        if (!Connected || _port == null) throw new InvalidOperationException("Device not connected.");
        if (len == 0) return Array.Empty<byte>();

        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var result = new byte[len];
            uint read = 0;

            var frame = new byte[1 + 4 + 4];
            frame[0] = CmdSpiFlashReadPage;
            byte[]? rx = null;

            // Disable UI updates
            lock (_port) {
                _port!.Open();
                _port!.DiscardInBuffer();
                _port!.Write(new byte[] { (byte)UsbCmd.CMD_SCREEN_CHANGE, (byte)SCREEN_CMD.SCREEN_PAUSE_UPDATES }, 0, 2);

                while (read < len)
                {
                    uint remaining = len - read;
                    uint toRead = (uint)Math.Min(SpiFlashMaxReadLen, remaining);

                    //var chunk = SpiFlashReadPageNoLock(addr + read, toRead);
                    BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1, 4), addr + read);
                    BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(5, 4), toRead);

                    _port!.Write(frame, 0, frame.Length);
                    var chunk = ReadExact((int)toRead);

                    if (chunk is null || chunk.Length != toRead)
                        throw new TimeoutException("SPI flash read error.");

                    Buffer.BlockCopy(chunk, 0, result, (int)read, (int)toRead);

                    read += toRead;
                    progress?.Report((double)read / len);
                }

                // Enable UI updates
                _port!.Write(new byte[] { (byte)UsbCmd.CMD_SCREEN_CHANGE, (byte)SCREEN_CMD.SCREEN_RESUME_UPDATES }, 0, 2);
                _port!.Close();
            }

            return result;

        }, ct).ConfigureAwait(false);
    }

    public async Task<byte[]> ReadDeviceLogAsync(
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {

        try
        {
            uint len = DataloggerEndAddr - DataloggerStartAddr;

            var bytes = await SpiFlashReadBytesAsync(
                addr: DataloggerStartAddr,
                len: len,
                progress: progress,
                ct: ct).ConfigureAwait(false);

            // Parse off-device
            return bytes;
        }
        finally
        {
            progress?.Report(1.0);
        }
    }

    public async Task<IReadOnlyList<DeviceLogParser.DATALOGGER_Entry>> ReadHistoryFromSpiFlashAsync(
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            uint len = DataloggerEndAddr - DataloggerStartAddr;

            var bytes = await SpiFlashReadBytesAsync(
                addr: DataloggerStartAddr,
                len: len,
                progress: progress,
                ct: ct).ConfigureAwait(false);

            // Parse off-device
            return DeviceLogParser.Parse(
                bytes);
        }
        finally
        {
            progress?.Report(1.0);
        }
    }
}