using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace WireView2.Device
{
    /// <summary>
    /// Native STM32 DFU firmware download over WinUSB, ported from the official
    /// 1.0.7 Windows client (raw winusb.dll control transfers; no external tools
    /// or USB libraries). Windows only: Linux flashes via dfu-util instead.
    /// Accepts an ELF32 image (flashes each PT_LOAD segment at its address) or a
    /// flat binary (flashed at 0x08000000). Ends with the zero-length download +
    /// manifest poll that makes the bootloader jump into the new firmware.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class DfuFirmwareUpdater
    {
        public const int DfuVid = 0x0483;  // 1155
        public const int DfuPid = 0xDF11;  // 57105

        public static async Task UpdateAsync(Stream firmwareImage, IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            // The Guillemot "guistdfudev" driver hijacks the DFU interface; remove
            // it so the WinUSB interface enumerates (same as the official client).
            if (await DfuHelper.RemoveGuiStDfuDevDriverIfPresentAsync(cancellationToken).ConfigureAwait(false))
                await Task.Delay(2000, cancellationToken).ConfigureAwait(false);

            if (!await DfuHelper.WaitForWinUsbDeviceAsync(DfuVid, DfuPid, TimeSpan.FromSeconds(10)).ConfigureAwait(false))
                throw new InvalidOperationException(
                    "The DFU device could not be reached in WinUSB mode. Please check the connection or existing drivers.");

            using var ms = new MemoryStream();
            await firmwareImage.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            byte[] payload = ms.ToArray();

            using var dfu = DfuDevice.Open(DfuVid, DfuPid);
            var descriptor = dfu.GetFunctionalDescriptor() ?? new DfuFunctionalDescriptor { wTransferSize = 1024 };
            int transferSize = Math.Max(64, Math.Min(4096, (int)descriptor.wTransferSize));

            if (Elf32Image.TryParse(payload, out var elf))
            {
                long total = 0;
                foreach (var seg in elf.Segments) total += seg.Data.Length;
                long done = 0;

                await dfu.ClearStatusIfErrorAsync(cancellationToken).ConfigureAwait(false);
                ushort blockNum = 2;
                foreach (var segment in elf.Segments)
                {
                    if (segment.Data.Length == 0) continue;
                    await dfu.SetAddressPointerAsync(segment.Address, cancellationToken).ConfigureAwait(false);
                    int offset = 0;
                    while (offset < segment.Data.Length)
                    {
                        int len = Math.Min(transferSize, segment.Data.Length - offset);
                        var block = new byte[len];
                        Buffer.BlockCopy(segment.Data, offset, block, 0, len);
                        await dfu.DownloadAsync(blockNum, block).ConfigureAwait(false);
                        await dfu.PollUntilReadyAsync(cancellationToken).ConfigureAwait(false);
                        offset += len;
                        done += len;
                        blockNum++;
                        if (total > 0) progress?.Report((double)done / total);
                    }
                }
                await dfu.DownloadAsync(0, Array.Empty<byte>()).ConfigureAwait(false);
                await dfu.PollUntilReadyAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await dfu.ClearStatusIfErrorAsync(cancellationToken).ConfigureAwait(false);
                await dfu.SetAddressPointerAsync(0x08000000u, cancellationToken).ConfigureAwait(false);
                ushort blockNum = 2;
                int offset = 0;
                while (offset < payload.Length)
                {
                    int len = Math.Min(transferSize, payload.Length - offset);
                    var block = new byte[len];
                    Buffer.BlockCopy(payload, offset, block, 0, len);
                    await dfu.DownloadAsync(blockNum, block).ConfigureAwait(false);
                    await dfu.PollUntilReadyAsync(cancellationToken).ConfigureAwait(false);
                    offset += len;
                    blockNum++;
                    if (payload.Length > 0) progress?.Report((double)offset / payload.Length);
                }
                await dfu.DownloadAsync(0, Array.Empty<byte>()).ConfigureAwait(false);
                await dfu.PollUntilReadyAsync(cancellationToken).ConfigureAwait(false);
            }
            progress?.Report(1.0);
        }

        /// <summary>Minimal ELF32 (little-endian) loader: PT_LOAD segments only.</summary>
        private sealed class Elf32Image
        {
            public sealed class Segment
            {
                public uint Address;
                public byte[] Data = Array.Empty<byte>();
            }

            public List<Segment> Segments { get; } = new();

            public static bool TryParse(byte[] file, out Elf32Image image)
            {
                image = new Elf32Image();
                if (file.Length < 52)
                    return false;
                if (file[0] != 0x7F || file[1] != (byte)'E' || file[2] != (byte)'L' || file[3] != (byte)'F')
                    return false;
                if (file[4] != 1 || file[5] != 1) // ELFCLASS32, little endian
                    return false;

                ushort phEntSize = ReadU16(file, 42);
                ushort phNum = ReadU16(file, 44);
                uint phOff = ReadU32(file, 28);
                if (phEntSize < 32 || phNum == 0)
                    return false;

                for (int i = 0; i < phNum; i++)
                {
                    int entry = checked((int)(phOff + (uint)(i * phEntSize)));
                    if (entry + phEntSize > file.Length)
                        break;
                    if (ReadU32(file, entry) != 1) // PT_LOAD
                        continue;

                    uint fileOffset = ReadU32(file, entry + 4);
                    uint vaddr = ReadU32(file, entry + 8);
                    uint paddr = ReadU32(file, entry + 12);
                    uint fileSize = ReadU32(file, entry + 16);
                    if (fileSize == 0) continue;
                    if (fileOffset + fileSize > file.Length)
                        throw new InvalidDataException("ELF segment beyond file size.");

                    var segment = new Segment
                    {
                        Address = paddr != 0 ? paddr : vaddr,
                        Data = new byte[fileSize],
                    };
                    Buffer.BlockCopy(file, (int)fileOffset, segment.Data, 0, (int)fileSize);
                    image.Segments.Add(segment);
                }
                image.Segments.Sort((a, b) => a.Address.CompareTo(b.Address));
                return image.Segments.Count > 0;
            }

            private static ushort ReadU16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));

            private static uint ReadU32(byte[] b, int o) =>
                (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        }

        /// <summary>Driver/device presence helpers used around the DFU flow.</summary>
        public static class DfuHelper
        {
            private const string GuiStDfuDevInfFileName = "guistdfudev.inf";
            private const string ExpectedDfuDeviceDescription = "DFU in FS Mode";

            public static Task<bool> WaitForDeviceAsync(ushort vid, ushort pid, TimeSpan timeout) =>
                WindowsDriverHelper.WaitForDevicePresentAsync(vid, pid, timeout);

            public static Task<bool> WaitForWinUsbDeviceAsync(ushort vid, ushort pid, TimeSpan timeout) =>
                WindowsDriverHelper.WaitForWinUsbDeviceInterfaceAsync(vid, pid, timeout);

            public static bool IsDevicePresent(ushort vid, ushort pid) =>
                WindowsDriverHelper.IsDevicePresent(vid, pid);

            public static bool IsWinUsbDeviceInstalled(ushort vid, ushort pid) =>
                WindowsDriverHelper.IsWinUsbDeviceInterfacePresent(vid, pid);

            public static string? TryGetConnectedDeviceDescription(ushort vid, ushort pid) =>
                WindowsDriverHelper.TryGetDeviceDescription(vid, pid);

            public static bool IsExpectedDfuDeviceName(ushort vid, ushort pid, out string? actualName)
            {
                actualName = TryGetConnectedDeviceDescription(vid, pid);
                if (string.IsNullOrWhiteSpace(actualName))
                    return false;
                return actualName.Equals(ExpectedDfuDeviceDescription, StringComparison.OrdinalIgnoreCase);
            }

            public static Task<bool> EnsureWinUsbDriverInstalledAsync(ushort vid, ushort pid, string infPath,
                TimeSpan postInstallWait, CancellationToken cancellationToken = default) =>
                WindowsDriverHelper.EnsureDriverInstalledAsync(infPath, cancellationToken);

            public static Task<bool> IsWinUsbDriverInstalledAsync(string infPath, CancellationToken cancellationToken = default) =>
                WindowsDriverHelper.IsDriverInfInstalledAsync(infPath, cancellationToken);

            public static Task<bool> IsGuiStDfuDevDriverInstalledAsync(CancellationToken cancellationToken = default) =>
                WindowsDriverHelper.IsDriverInstalledByOriginalInfNameAsync(GuiStDfuDevInfFileName, cancellationToken);

            public static Task<bool> RemoveGuiStDfuDevDriverIfPresentAsync(CancellationToken cancellationToken = default) =>
                WindowsDriverHelper.RemoveDriverByOriginalInfNameIfPresentAsync(GuiStDfuDevInfFileName, cancellationToken);
        }

        // ---- DFU protocol over WinUSB ----

        private sealed class DfuDevice : IDisposable
        {
            private const byte DFU_DNLOAD = 1;
            private const byte DFU_GETSTATUS = 3;
            private const byte DFU_CLRSTATUS = 4;
            private const byte ST_DFU_SET_ADDRESS_POINTER = 0x21;

            private readonly WinUsbDevice _usb;
            // The STM32 bootloader exposes DFU on interface 0 (as upstream assumes).
            private const byte _interfaceIndex = 0;

            public static DfuDevice Open(ushort vid, ushort pid) =>
                new(WinUsbDevice.OpenByVidPid(vid, pid));

            private DfuDevice(WinUsbDevice usb)
            {
                _usb = usb;
            }

            public DfuFunctionalDescriptor? GetFunctionalDescriptor()
            {
                var buffer = new byte[9];
                // GET_DESCRIPTOR, type 0x21 (DFU functional), on the interface.
                int read = _usb.ControlIn(0x81, 6, 0x2100, _interfaceIndex, buffer, 0, buffer.Length);
                if (read >= 7 && buffer[1] == 0x21)
                {
                    return new DfuFunctionalDescriptor
                    {
                        bLength = buffer[0],
                        bDescriptorType = buffer[1],
                        bmAttributes = buffer[2],
                        wDetachTimeOut = (ushort)(buffer[3] | (buffer[4] << 8)),
                        wTransferSize = (ushort)(buffer[5] | (buffer[6] << 8)),
                        bcdDFUVersion = (ushort)(read >= 9 ? buffer[7] | (buffer[8] << 8) : 0x011A),
                    };
                }
                return null;
            }

            public async Task ClearStatusIfErrorAsync(CancellationToken cancellationToken = default)
            {
                if (GetStatus().bState == DfuState.dfuERROR)
                {
                    _usb.ControlOut(0x21, DFU_CLRSTATUS, 0, _interfaceIndex, null, 0);
                    await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                }
            }

            public async Task SetAddressPointerAsync(uint address, CancellationToken cancellationToken = default)
            {
                Download(0, new[]
                {
                    (byte)ST_DFU_SET_ADDRESS_POINTER,
                    (byte)(address & 0xFF),
                    (byte)((address >> 8) & 0xFF),
                    (byte)((address >> 16) & 0xFF),
                    (byte)((address >> 24) & 0xFF),
                });
                await PollUntilReadyAsync(cancellationToken).ConfigureAwait(false);
            }

            public Task DownloadAsync(ushort blockNum, byte[] data)
            {
                Download(blockNum, data);
                return Task.CompletedTask;
            }

            private void Download(ushort blockNum, byte[]? data)
            {
                _usb.ControlOut(0x21, DFU_DNLOAD, blockNum, _interfaceIndex, data, data?.Length ?? 0);
            }

            public async Task PollUntilReadyAsync(CancellationToken cancellationToken = default)
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var status = GetStatus();
                    if (status.bStatus != 0)
                        throw new InvalidOperationException(
                            $"DFU error status: 0x{status.bStatus:X2}, state: {status.bState}");

                    int pollMs = status.bwPollTimeout[0] | (status.bwPollTimeout[1] << 8) | (status.bwPollTimeout[2] << 16);
                    switch (status.bState)
                    {
                        case DfuState.dfuDNBUSY:
                        case DfuState.dfuMANIFEST:
                            await Task.Delay(Math.Min(pollMs, 1000), cancellationToken).ConfigureAwait(false);
                            break;
                        case DfuState.dfuIDLE:
                        case DfuState.dfuDNLOAD_IDLE:
                        case DfuState.dfuMANIFEST_SYNC:
                        case DfuState.dfuMANIFEST_WAIT_RESET:
                            return;
                        default:
                            await Task.Delay(Math.Min(Math.Max(pollMs, 1), 100), cancellationToken).ConfigureAwait(false);
                            break;
                    }
                }
            }

            private DfuStatus GetStatus()
            {
                var buffer = new byte[6];
                _usb.ControlIn(0xA1, DFU_GETSTATUS, 0, _interfaceIndex, buffer, 0, buffer.Length);
                return new DfuStatus
                {
                    bStatus = buffer[0],
                    bwPollTimeout = new[] { buffer[1], buffer[2], buffer[3] },
                    bState = (DfuState)buffer[4],
                    iString = buffer[5],
                };
            }

            public void Dispose() => _usb.Dispose();
        }

        private sealed class DfuStatus
        {
            public byte bStatus;
            public byte[] bwPollTimeout = new byte[3];
            public DfuState bState;
            public byte iString;
        }

        private enum DfuState : byte
        {
            appIDLE,
            appDETACH,
            dfuIDLE,
            dfuDNLOAD_SYNC,
            dfuDNBUSY,
            dfuDNLOAD_IDLE,
            dfuMANIFEST_SYNC,
            dfuMANIFEST,
            dfuMANIFEST_WAIT_RESET,
            dfuUPLOAD_IDLE,
            dfuERROR,
        }

        private struct DfuFunctionalDescriptor
        {
            public byte bLength;
            public byte bDescriptorType;
            public byte bmAttributes;
            public ushort wDetachTimeOut;
            public ushort wTransferSize;
            public ushort bcdDFUVersion;
        }

        private sealed class WinUsbDevice : IDisposable
        {
            [StructLayout(LayoutKind.Sequential, Pack = 1)]
            private struct WINUSB_SETUP_PACKET
            {
                public byte RequestType;
                public byte Request;
                public ushort Value;
                public ushort Index;
                public ushort Length;
            }

            internal static readonly Guid GUID_DEVINTERFACE_WINUSB = new("dee824ef-729b-4a0e-9c14-b7117d33a817");

            private SafeFileHandle _deviceHandle = null!;
            private nint _winUsbHandle = IntPtr.Zero;

            public static WinUsbDevice OpenByVidPid(ushort vid, ushort pid)
            {
                string path = FindDevicePath(vid, pid)
                    ?? throw new FileNotFoundException("DFU WinUSB interface not found.");
                var handle = CreateFile(path, 0xC0000000u /* GENERIC_READ|WRITE */, 3u /* share rw */,
                    IntPtr.Zero, 3u /* OPEN_EXISTING */, 0x80u /* FILE_ATTRIBUTE_NORMAL */, IntPtr.Zero);
                if (handle.IsInvalid)
                    throw new InvalidOperationException($"CreateFile failed: {Marshal.GetLastWin32Error()}");
                if (!WinUsb_Initialize(handle, out nint interfaceHandle))
                {
                    handle.Dispose();
                    throw new InvalidOperationException($"WinUsb_Initialize failed: {Marshal.GetLastWin32Error()}");
                }
                return new WinUsbDevice { _deviceHandle = handle, _winUsbHandle = interfaceHandle };
            }

            public int ControlIn(byte bmRequest, byte bRequest, ushort wValue, ushort wIndex,
                byte[] buffer, int offset, int length)
            {
                var setup = new WINUSB_SETUP_PACKET
                {
                    RequestType = bmRequest,
                    Request = bRequest,
                    Value = wValue,
                    Index = wIndex,
                    Length = (ushort)length,
                };
                var temp = new byte[length];
                if (!WinUsb_ControlTransfer(_winUsbHandle, setup, temp, length, out int transferred, IntPtr.Zero))
                    throw new InvalidOperationException($"Control IN failed: {Marshal.GetLastWin32Error()}");
                Buffer.BlockCopy(temp, 0, buffer, offset, transferred);
                return transferred;
            }

            public void ControlOut(byte bmRequest, byte bRequest, ushort wValue, ushort wIndex,
                byte[]? buffer, int length)
            {
                var setup = new WINUSB_SETUP_PACKET
                {
                    RequestType = bmRequest,
                    Request = bRequest,
                    Value = wValue,
                    Index = wIndex,
                    Length = (ushort)length,
                };
                var data = buffer ?? Array.Empty<byte>();
                if (!WinUsb_ControlTransfer(_winUsbHandle, setup, data, length, out _, IntPtr.Zero))
                    throw new InvalidOperationException($"Control OUT failed: {Marshal.GetLastWin32Error()}");
            }

            public void Dispose()
            {
                if (_winUsbHandle != IntPtr.Zero)
                {
                    WinUsb_Free(_winUsbHandle);
                    _winUsbHandle = IntPtr.Zero;
                }
                _deviceHandle?.Dispose();
            }

            private static string? FindDevicePath(ushort vid, ushort pid)
            {
                string needle = $"vid_{vid:X4}&pid_{pid:X4}";
                foreach (string path in WindowsSetupApi.EnumerateDeviceInterfacePaths(GUID_DEVINTERFACE_WINUSB))
                {
                    if (path.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                        return path;
                }
                return null;
            }

            [DllImport("winusb.dll", SetLastError = true)]
            private static extern bool WinUsb_Initialize(SafeFileHandle deviceHandle, out nint interfaceHandle);

            [DllImport("winusb.dll", SetLastError = true)]
            private static extern bool WinUsb_ControlTransfer(nint interfaceHandle, WINUSB_SETUP_PACKET setupPacket,
                byte[] buffer, int bufferLength, out int lengthTransferred, nint overlapped);

            [DllImport("winusb.dll", SetLastError = true)]
            private static extern bool WinUsb_Free(nint interfaceHandle);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess,
                uint dwShareMode, nint lpSecurityAttributes, uint dwCreationDisposition,
                uint dwFlagsAndAttributes, nint hTemplateFile);
        }
    }
}
