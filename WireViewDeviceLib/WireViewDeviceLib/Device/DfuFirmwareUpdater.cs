using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using LibUsbDotNet;
using LibUsbDotNet.Main;

namespace WireView2.Device
{
    public static class DfuFirmwareUpdater
    {
        public const int DfuVid = 0x0483;
        public const int DfuPid = 0xDF11;

        public static async Task UpdateAsync(Stream firmwareImage)
        {
            using var ms = new MemoryStream();
            await firmwareImage.CopyToAsync(ms).ConfigureAwait(false);
            var payload = ms.ToArray();

            // Open DFU
            using var dfu = DfuDevice.Open(DfuVid, DfuPid);
            var func = dfu.GetFunctionalDescriptor() ?? new DfuFunctionalDescriptor { wTransferSize = 1024 };
            var xfer = Math.Max(64, Math.Min(4096, (int)func.wTransferSize));

            // Parse ELF or program as BIN
            if (Elf32Image.TryParse(payload, out var elf))
            {
                await dfu.ClearStatusIfErrorAsync().ConfigureAwait(false);

                ushort blockNum = 2;
                foreach (var seg in elf.Segments)
                {
                    if (seg.Data.Length == 0)
                    {
                        continue;
                    }

                    await dfu.SetAddressPointerAsync(seg.Address).ConfigureAwait(false);

                    int offset = 0;
                    while (offset < seg.Data.Length)
                    {
                        int len = Math.Min(xfer, seg.Data.Length - offset);
                        var chunk = new byte[len];
                        Buffer.BlockCopy(seg.Data, offset, chunk, 0, len);

                        await dfu.DownloadAsync(blockNum, chunk).ConfigureAwait(false);
                        await dfu.PollUntilReadyAsync().ConfigureAwait(false);

                        offset += len;
                        blockNum++;
                    }
                }

                await dfu.DownloadAsync(0, Array.Empty<byte>()).ConfigureAwait(false);
                await dfu.PollUntilReadyAsync().ConfigureAwait(false);
            }
            else
            {
                const uint baseAddr = 0x08000000;
                await dfu.ClearStatusIfErrorAsync().ConfigureAwait(false);
                await dfu.SetAddressPointerAsync(baseAddr).ConfigureAwait(false);

                ushort blockNum = 2;
                int offset = 0;
                while (offset < payload.Length)
                {
                    int len = Math.Min(xfer, payload.Length - offset);
                    var chunk = new byte[len];
                    Buffer.BlockCopy(payload, offset, chunk, 0, len);

                    await dfu.DownloadAsync(blockNum, chunk).ConfigureAwait(false);
                    await dfu.PollUntilReadyAsync().ConfigureAwait(false);

                    offset += len;
                    blockNum++;
                }

                await dfu.DownloadAsync(0, Array.Empty<byte>()).ConfigureAwait(false);
                await dfu.PollUntilReadyAsync().ConfigureAwait(false);
            }
        }

        // ===== ELF parsing (32-bit little-endian) =====
        private sealed class Elf32Image
        {
            public readonly List<Segment> Segments = new();

            public sealed class Segment
            {
                public uint Address;
                public byte[] Data = Array.Empty<byte>();
            }

            public static bool TryParse(byte[] file, out Elf32Image image)
            {
                image = new Elf32Image();

                if (file.Length < 52)
                {
                    return false;
                }

                if (!(file[0] == 0x7F && file[1] == (byte)'E' && file[2] == (byte)'L' && file[3] == (byte)'F'))
                {
                    return false;
                }

                if (file[4] != 1 || file[5] != 1)
                {
                    return false;
                }

                ushort e_phentsize = ReadU16(file, 42);
                ushort e_phnum = ReadU16(file, 44);
                uint e_phoff = ReadU32(file, 28);

                if (e_phentsize < 32 || e_phnum == 0)
                {
                    return false;
                }

                for (int i = 0; i < e_phnum; i++)
                {
                    int off = checked((int)(e_phoff + (uint)(i * e_phentsize)));
                    if (off + e_phentsize > file.Length)
                    {
                        break;
                    }

                    uint p_type = ReadU32(file, off + 0);
                    const uint PT_LOAD = 1;
                    if (p_type != PT_LOAD)
                    {
                        continue;
                    }

                    uint p_offset = ReadU32(file, off + 4);
                    uint p_vaddr = ReadU32(file, off + 8);
                    uint p_paddr = ReadU32(file, off + 12);
                    uint p_filesz = ReadU32(file, off + 16);

                    if (p_filesz == 0)
                    {
                        continue;
                    }

                    if (p_offset + p_filesz > file.Length)
                    {
                        throw new InvalidDataException("ELF segment beyond file size.");
                    }

                    var seg = new Segment
                    {
                        Address = p_paddr != 0 ? p_paddr : p_vaddr,
                        Data = new byte[p_filesz]
                    };
                    Buffer.BlockCopy(file, (int)p_offset, seg.Data, 0, (int)p_filesz);
                    image.Segments.Add(seg);
                }

                image.Segments.Sort((a, b) => a.Address.CompareTo(b.Address));
                return image.Segments.Count > 0;
            }

            private static ushort ReadU16(byte[] b, int o) => (ushort)(b[o] | b[o + 1] << 8);
            private static uint ReadU32(byte[] b, int o) => (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24);
        }

        // ===== DFU over USB (platform-abstracted) =====

        public static class DfuHelper
        {
            private const string GuiStDfuDevInfFileName = "guistdfudev.inf";
            private const string ExpectedDfuDeviceDescription = "DFU in FS Mode";

            public static Task<bool> WaitForDeviceAsync(ushort vid, ushort pid, TimeSpan timeout) =>
                WindowsDriverHelper.WaitForDevicePresentAsync(vid, pid, timeout);

            public static Task<bool> WaitForWinUsbDeviceAsync(ushort vid, ushort pid, TimeSpan timeout) =>
                OperatingSystem.IsLinux()
                    ? WindowsDriverHelper.WaitForDevicePresentAsync(vid, pid, timeout)
                    : WindowsDriverHelper.WaitForWinUsbDeviceInterfaceAsync(vid, pid, timeout);

            public static bool IsDevicePresent(ushort vid, ushort pid) =>
                WindowsDriverHelper.IsDevicePresent(vid, pid);

            public static bool IsWinUsbDeviceInstalled(ushort vid, ushort pid) =>
                OperatingSystem.IsLinux()
                    ? WindowsDriverHelper.IsDevicePresent(vid, pid)
                    : WindowsDriverHelper.IsWinUsbDeviceInterfacePresent(vid, pid);

            public static string? TryGetConnectedDeviceDescription(ushort vid, ushort pid) =>
                WindowsDriverHelper.TryGetDeviceDescription(vid, pid);

            public static bool IsExpectedDfuDeviceName(ushort vid, ushort pid, out string? actualName)
            {
                actualName = TryGetConnectedDeviceDescription(vid, pid);
                if (string.IsNullOrWhiteSpace(actualName))
                {
                    return false;
                }

                return actualName.Equals(ExpectedDfuDeviceDescription, StringComparison.OrdinalIgnoreCase);
            }

            public static Task<bool> EnsureWinUsbDriverInstalledAsync(
                ushort vid,
                ushort pid,
                string infPath,
                TimeSpan postInstallWait,
                CancellationToken cancellationToken = default)
            {
                if (OperatingSystem.IsLinux())
                {
                    // On Linux, kernel drivers handle USB — no driver installation needed.
                    return Task.FromResult(true);
                }

                return WindowsDriverHelper.EnsureDriverInstalledAsync(infPath, cancellationToken);
            }

            public static Task<bool> IsWinUsbDriverInstalledAsync(
                string infPath,
                CancellationToken cancellationToken = default)
            {
                if (OperatingSystem.IsLinux())
                {
                    return Task.FromResult(true);
                }

                return WindowsDriverHelper.IsDriverInfInstalledAsync(infPath, cancellationToken);
            }

            public static Task<bool> IsGuiStDfuDevDriverInstalledAsync(CancellationToken cancellationToken = default)
            {
                if (OperatingSystem.IsLinux())
                {
                    return Task.FromResult(false);
                }

                return WindowsDriverHelper.IsDriverInstalledByOriginalInfNameAsync(GuiStDfuDevInfFileName, cancellationToken);
            }

            public static Task<bool> RemoveGuiStDfuDevDriverIfPresentAsync(CancellationToken cancellationToken = default)
            {
                if (OperatingSystem.IsLinux())
                {
                    return Task.FromResult(false);
                }

                return WindowsDriverHelper.RemoveDriverByOriginalInfNameIfPresentAsync(GuiStDfuDevInfFileName, cancellationToken);
            }
        }

        // Platform-agnostic USB control transfer interface
        private interface IUsbControlDevice : IDisposable
        {
            int ControlIn(byte bmRequest, byte bRequest, ushort wValue, ushort wIndex, byte[] buffer, int offset, int length);
            void ControlOut(byte bmRequest, byte bRequest, ushort wValue, ushort wIndex, byte[]? buffer, int length);
        }

        private sealed class DfuDevice : IDisposable
        {
            private const byte DFU_DNLOAD = 1;
            private const byte DFU_GETSTATUS = 3;
            private const byte DFU_CLRSTATUS = 4;

            private const byte REQ_GET_DESCRIPTOR = 0x06;
            private const ushort DESC_TYPE_DFU_FUNCTIONAL = 0x21;
            private const byte RECIP_INTERFACE = 0x01;
            private const byte TYPE_STANDARD = 0x00;
            private const byte TYPE_CLASS = 0x20;
            private const byte DIR_DEVICE_TO_HOST = 0x80;
            private const byte DIR_HOST_TO_DEVICE = 0x00;

            private const byte ST_DFU_SET_ADDRESS_POINTER = 0x21;

            private readonly IUsbControlDevice _usb;
            private readonly byte _ifIndex = 0;

            public static DfuDevice Open(ushort vid, ushort pid)
            {
                IUsbControlDevice usb;
                if (OperatingSystem.IsLinux())
                {
                    usb = LinuxUsbDevice.OpenByVidPid(vid, pid);
                }
                else
                {
                    usb = WinUsbDevice.OpenByVidPid(vid, pid);
                }

                return new DfuDevice(usb);
            }

            private DfuDevice(IUsbControlDevice usb)
            {
                _usb = usb;
            }

            public DfuFunctionalDescriptor? GetFunctionalDescriptor()
            {
                var buf = new byte[9];
                int read = _usb.ControlIn(
                    DIR_DEVICE_TO_HOST | TYPE_STANDARD | RECIP_INTERFACE,
                    REQ_GET_DESCRIPTOR,
                    (ushort)(DESC_TYPE_DFU_FUNCTIONAL << 8 | 0),
                    _ifIndex,
                    buf,
                    0,
                    buf.Length);

                if (read >= 7 && buf[1] == DESC_TYPE_DFU_FUNCTIONAL)
                {
                    return new DfuFunctionalDescriptor
                    {
                        bLength = buf[0],
                        bDescriptorType = buf[1],
                        bmAttributes = buf[2],
                        wDetachTimeOut = (ushort)(buf[3] | buf[4] << 8),
                        wTransferSize = (ushort)(buf[5] | buf[6] << 8),
                        bcdDFUVersion = read >= 9 ? (ushort)(buf[7] | buf[8] << 8) : (ushort)0x011A
                    };
                }

                return null;
            }

            public async Task ClearStatusIfErrorAsync()
            {
                var st = GetStatus();
                if (st.bState == DfuState.dfuERROR)
                {
                    _usb.ControlOut(DIR_HOST_TO_DEVICE | TYPE_CLASS | RECIP_INTERFACE,
                        DFU_CLRSTATUS, 0, _ifIndex, null, 0);
                    await Task.Delay(1).ConfigureAwait(false);
                }
            }

            public async Task SetAddressPointerAsync(uint address)
            {
                var payload = new byte[5];
                payload[0] = ST_DFU_SET_ADDRESS_POINTER;
                payload[1] = (byte)(address & 0xFF);
                payload[2] = (byte)(address >> 8 & 0xFF);
                payload[3] = (byte)(address >> 16 & 0xFF);
                payload[4] = (byte)(address >> 24 & 0xFF);

                Download(0, payload);
                await PollUntilReadyAsync().ConfigureAwait(false);
            }

            public Task DownloadAsync(ushort blockNum, byte[] data)
            {
                Download(blockNum, data);
                return Task.CompletedTask;
            }

            private void Download(ushort blockNum, byte[] data)
            {
                _usb.ControlOut(DIR_HOST_TO_DEVICE | TYPE_CLASS | RECIP_INTERFACE,
                    DFU_DNLOAD, blockNum, _ifIndex, data, data?.Length ?? 0);
            }

            public async Task PollUntilReadyAsync()
            {
                while (true)
                {
                    var st = GetStatus();
                    if (st.bStatus != 0)
                    {
                        throw new InvalidOperationException($"DFU error status: 0x{st.bStatus:X2}, state: {st.bState}");
                    }

                    var wait = st.bwPollTimeout[0] | st.bwPollTimeout[1] << 8 | st.bwPollTimeout[2] << 16;
                    var state = st.bState;

                    if (state == DfuState.dfuDNBUSY || state == DfuState.dfuMANIFEST)
                    {
                        await Task.Delay(Math.Min(wait, 1000)).ConfigureAwait(false);
                        continue;
                    }

                    if (state == DfuState.dfuDNLOAD_IDLE || state == DfuState.dfuIDLE ||
                        state == DfuState.dfuMANIFEST_SYNC || state == DfuState.dfuMANIFEST_WAIT_RESET)
                    {
                        return;
                    }

                    await Task.Delay(Math.Min(Math.Max(wait, 1), 100)).ConfigureAwait(false);
                }
            }

            private DfuStatus GetStatus()
            {
                var buf = new byte[6];
                _usb.ControlIn(DIR_DEVICE_TO_HOST | TYPE_CLASS | RECIP_INTERFACE,
                    DFU_GETSTATUS, 0, _ifIndex, buf, 0, buf.Length);
                return new DfuStatus
                {
                    bStatus = buf[0],
                    bwPollTimeout = new[] { buf[1], buf[2], buf[3] },
                    bState = (DfuState)buf[4],
                    iString = buf[5]
                };
            }

            public void Dispose() => _usb.Dispose();
        }

        private class DfuStatus
        {
            public byte bStatus;
            public byte[] bwPollTimeout = new byte[3];
            public DfuState bState;
            public byte iString;
        }

        private enum DfuState : byte
        {
            appIDLE = 0,
            appDETACH = 1,
            dfuIDLE = 2,
            dfuDNLOAD_SYNC = 3,
            dfuDNBUSY = 4,
            dfuDNLOAD_IDLE = 5,
            dfuMANIFEST_SYNC = 6,
            dfuMANIFEST = 7,
            dfuMANIFEST_WAIT_RESET = 8,
            dfuUPLOAD_IDLE = 9,
            dfuERROR = 10
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

        // Linux USB device using LibUsbDotNet
        private sealed class LinuxUsbDevice : IUsbControlDevice
        {
            private UsbDevice _device;

            public static LinuxUsbDevice OpenByVidPid(ushort vid, ushort pid)
            {
                var finder = new UsbDeviceFinder(vid, pid);
                var device = UsbDevice.OpenUsbDevice(finder)
                    ?? throw new FileNotFoundException($"USB device {vid:X4}:{pid:X4} not found.");

                // For whole-device drivers (libusb), claim the DFU interface
                if (device is IUsbDevice wholeDevice)
                {
                    wholeDevice.SetConfiguration(1);
                    wholeDevice.ClaimInterface(0);
                }

                return new LinuxUsbDevice { _device = device };
            }

            public int ControlIn(byte bmRequest, byte bRequest, ushort wValue, ushort wIndex, byte[] buffer, int offset, int length)
            {
                var tmp = new byte[length];
                var setup = new UsbSetupPacket(bmRequest, bRequest, wValue, wIndex, (short)length);
                int transferred = 0;
                if (!_device.ControlTransfer(ref setup, tmp, length, out transferred))
                {
                    throw new InvalidOperationException("LibUsb control IN transfer failed.");
                }

                Buffer.BlockCopy(tmp, 0, buffer, offset, transferred);
                return transferred;
            }

            public void ControlOut(byte bmRequest, byte bRequest, ushort wValue, ushort wIndex, byte[]? buffer, int length)
            {
                var tmp = buffer ?? Array.Empty<byte>();
                var setup = new UsbSetupPacket(bmRequest, bRequest, wValue, wIndex, (short)length);
                int transferred = 0;
                if (!_device.ControlTransfer(ref setup, tmp, length, out transferred))
                {
                    throw new InvalidOperationException("LibUsb control OUT transfer failed.");
                }
            }

            public void Dispose()
            {
                if (_device is IUsbDevice wholeDevice)
                {
                    wholeDevice.ReleaseInterface(0);
                }

                _device?.Close();
            }
        }

        // Minimal WinUSB wrapper + SetupAPI enumeration (Windows only)
        private sealed class WinUsbDevice : IUsbControlDevice
        {
            internal static readonly Guid GUID_DEVINTERFACE_WINUSB = new("dee824ef-729b-4a0e-9c14-b7117d33a817");

            private SafeFileHandle _deviceHandle = null!;
            private nint _winUsbHandle = nint.Zero;

            public static WinUsbDevice OpenByVidPid(ushort vid, ushort pid)
            {
                var path = FindDevicePath(vid, pid) ?? throw new FileNotFoundException("DFU WinUSB interface not found.");
                const uint GENERIC_READ_WRITE = 0x80000000u | 0x40000000u;
                const uint SHARE_READ_WRITE = 0x00000001u | 0x00000002u;
                const uint OPEN_EXISTING = 3u;
                const uint FILE_ATTRIBUTE_NORMAL = 0x00000080u;

                var handle = CreateFile(path, GENERIC_READ_WRITE, SHARE_READ_WRITE, nint.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nint.Zero);
                if (handle.IsInvalid)
                {
                    throw new InvalidOperationException($"CreateFile failed: {Marshal.GetLastWin32Error()}");
                }

                if (!WinUsb_Initialize(handle, out var usbHandle))
                {
                    handle.Dispose();
                    throw new InvalidOperationException($"WinUsb_Initialize failed: {Marshal.GetLastWin32Error()}");
                }

                return new WinUsbDevice { _deviceHandle = handle, _winUsbHandle = usbHandle };
            }

            public int ControlIn(byte bmRequest, byte bRequest, ushort wValue, ushort wIndex, byte[] buffer, int offset, int length)
            {
                var setup = new WINUSB_SETUP_PACKET
                {
                    RequestType = bmRequest,
                    Request = bRequest,
                    Value = wValue,
                    Index = wIndex,
                    Length = (ushort)length
                };
                var tmp = new byte[length];
                if (!WinUsb_ControlTransfer(_winUsbHandle, setup, tmp, length, out var read, nint.Zero))
                {
                    throw new InvalidOperationException($"Control IN failed: {Marshal.GetLastWin32Error()}");
                }

                Buffer.BlockCopy(tmp, 0, buffer, offset, read);
                return read;
            }

            public void ControlOut(byte bmRequest, byte bRequest, ushort wValue, ushort wIndex, byte[]? buffer, int length)
            {
                var setup = new WINUSB_SETUP_PACKET
                {
                    RequestType = bmRequest,
                    Request = bRequest,
                    Value = wValue,
                    Index = wIndex,
                    Length = (ushort)length
                };
                var tmp = buffer ?? Array.Empty<byte>();
                if (!WinUsb_ControlTransfer(_winUsbHandle, setup, tmp, length, out var _, nint.Zero))
                {
                    throw new InvalidOperationException($"Control OUT failed: {Marshal.GetLastWin32Error()}");
                }
            }

            public void Dispose()
            {
                if (_winUsbHandle != nint.Zero)
                {
                    WinUsb_Free(_winUsbHandle);
                    _winUsbHandle = nint.Zero;
                }

                _deviceHandle?.Dispose();
            }

            private static string? FindDevicePath(ushort vid, ushort pid)
            {
                var needle = $"vid_{vid:X4}&pid_{pid:X4}";
                foreach (var path in WindowsSetupApi.EnumerateDeviceInterfacePaths(GUID_DEVINTERFACE_WINUSB))
                {
                    if (path.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return path;
                    }
                }

                return null;
            }

            [DllImport("winusb.dll", SetLastError = true)]
            private static extern bool WinUsb_Initialize(SafeFileHandle deviceHandle, out nint interfaceHandle);

            [DllImport("winusb.dll", SetLastError = true)]
            private static extern bool WinUsb_ControlTransfer(
                nint interfaceHandle,
                WINUSB_SETUP_PACKET setupPacket,
                byte[] buffer,
                int bufferLength,
                out int lengthTransferred,
                nint overlapped);

            [DllImport("winusb.dll", SetLastError = true)]
            private static extern bool WinUsb_Free(nint interfaceHandle);

            [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
            private static extern SafeFileHandle CreateFile(
                string lpFileName,
                uint dwDesiredAccess,
                uint dwShareMode,
                nint lpSecurityAttributes,
                uint dwCreationDisposition,
                uint dwFlagsAndAttributes,
                nint hTemplateFile);

            [StructLayout(LayoutKind.Sequential, Pack = 1)]
            private struct WINUSB_SETUP_PACKET
            {
                public byte RequestType;
                public byte Request;
                public ushort Value;
                public ushort Index;
                public ushort Length;
            }
        }
    }
}