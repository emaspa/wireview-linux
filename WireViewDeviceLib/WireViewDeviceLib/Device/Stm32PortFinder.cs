using System.Runtime.InteropServices;

namespace WireView2.Device
{
    /// <summary>A WireView serial port. <paramref name="UsbSerial"/> is the USB
    /// serial number (derived from the chip UID, see
    /// <see cref="Stm32PortFinder.UsbSerialFromUid"/>) and <paramref name="UsbPath"/>
    /// the USB port path (e.g. "3-4"); both null where unknown.</summary>
    public sealed record WireViewPort(string Port, string? UsbSerial, string? UsbPath);

    public static class Stm32PortFinder
    {
        public static List<string> FindMatchingComPorts() =>
            FindMatchingPorts().Select(p => p.Port).ToList();

        private static List<WireViewPort> FindMatchingPortsWindows()
        {
            var ports = new List<WireViewPort>();
            if (!OperatingSystem.IsWindows())
                return ports;

            nint devInfo = WindowsSetupApi.SetupDiGetClassDevsAllClassesPresent();
            if (WindowsSetupApi.IsInvalidHandle(devInfo))
            {
                return ports;
            }

            try
            {
                var devInfoData = new WindowsSetupApi.SP_DEVINFO_DATA
                {
                    cbSize = (uint)Marshal.SizeOf<WindowsSetupApi.SP_DEVINFO_DATA>()
                };

                for (uint index = 0; WindowsSetupApi.SetupDiEnumDeviceInfo(devInfo, index, ref devInfoData); index++)
                {
                    var hardwareIds = WindowsSetupApi.TryGetDeviceRegistryPropertyMultiSz(devInfo, ref devInfoData, WindowsSetupApi.SPDRP_HARDWAREID);
                    if (hardwareIds is null || hardwareIds.Length == 0)
                    {
                        continue;
                    }

                    if (!HardwareIdsContainVidPid(hardwareIds, "VID_0483", "PID_5740"))
                    {
                        continue;
                    }

                    var friendlyName = WindowsSetupApi.TryGetDeviceRegistryPropertyString(devInfo, ref devInfoData, WindowsSetupApi.SPDRP_FRIENDLYNAME);
                    if (string.IsNullOrWhiteSpace(friendlyName))
                    {
                        continue;
                    }

                    var comPort = TryExtractComPortFromFriendlyName(friendlyName);
                    if (string.IsNullOrWhiteSpace(comPort))
                    {
                        continue;
                    }

                    // The USB serial number is the last part of the USB device's
                    // instance id; the location path names its physical USB port.
                    string? usbSerial = null, usbPath = null;
                    if (WindowsSetupApi.UsbDeviceOf(devInfoData.DevInst) is { } usb)
                    {
                        usbSerial = WindowsSetupApi.UsbSerialFromInstanceId(usb.InstanceId);
                        usbPath = WindowsSetupApi.TryGetLocationPath(usb.DevInst);
                    }
                    ports.Add(new WireViewPort(comPort, usbSerial, usbPath));
                }
            }
            finally
            {
                _ = WindowsSetupApi.SetupDiDestroyDeviceInfoList(devInfo);
            }

            // Only ports positively identified as the WireView (VID_0483/PID_5740)
            // are returned. We deliberately do NOT fall back to "all COM ports": the
            // connect probe asserts RTS and blocks up to a second reading on each
            // port it tries, so sweeping every serial device — while holding the
            // machine-wide sensor mutex — can disturb other devices and wedge the
            // WireView when the SetupAPI match transiently misses at startup. An
            // empty result here simply means the caller retries on its next tick,
            // by which point enumeration has settled (matching the original client).
            return ports;
        }

        private static bool HardwareIdsContainVidPid(string[] hardwareIds, string vid, string pid)
        {
            for (int i = 0; i < hardwareIds.Length; i++)
            {
                var s = hardwareIds[i];
                if (string.IsNullOrWhiteSpace(s))
                {
                    continue;
                }

                if (s.Contains(vid, StringComparison.OrdinalIgnoreCase) &&
                    s.Contains(pid, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The WireView serial ports with their USB identity: the device's
        /// USB serial number and its USB port path: on Linux the sysfs name of the
        /// USB device (e.g. "3-4", which dfu-util takes as <c>-p</c>), on Windows
        /// its location path (e.g. "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(4)", which
        /// the native DFU flasher matches). Empty elsewhere.</summary>
        public static List<WireViewPort> FindMatchingPorts()
        {
            if (OperatingSystem.IsLinux())
                return FindMatchingPortsLinux();
            if (OperatingSystem.IsWindows())
                return FindMatchingPortsWindows();
            return new List<WireViewPort>();
        }

        /// <summary>A USB port path as people read it: unchanged on Linux ("3-4");
        /// a Windows location path shortened to its hub port chain after the root
        /// hub ("PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(4)#USB(2)" is "4-2").</summary>
        public static string? DescribeUsbPath(string? usbPath)
        {
            if (usbPath == null) return null;
            int root = usbPath.IndexOf("USBROOT(", StringComparison.OrdinalIgnoreCase);
            if (root < 0) return usbPath;
            var hops = usbPath[root..].Split('#').Skip(1)
                .Select(h => h.StartsWith("USB(", StringComparison.OrdinalIgnoreCase) && h.EndsWith(')') ? h[4..^1] : h)
                .ToList();
            return hops.Count > 0 ? string.Join('-', hops) : usbPath;
        }

        /// <summary>The USB serial number the firmware reports for the device with
        /// chip UID <paramref name="uid"/> (24 hex digits, as
        /// <see cref="IWireViewDevice.UniqueId"/>): the STM32 USB library derives it
        /// from the UID as hex8(w0 + w2) + hex4(w1 >> 16), w0..w2 the UID's
        /// little-endian words. Null when <paramref name="uid"/> is not a UID.</summary>
        public static string? UsbSerialFromUid(string? uid)
        {
            if (uid == null || uid.Length != 24) return null;
            byte[] b;
            try { b = Convert.FromHexString(uid); }
            catch (FormatException) { return null; }
            uint w0 = BitConverter.ToUInt32(b, 0), w1 = BitConverter.ToUInt32(b, 4), w2 = BitConverter.ToUInt32(b, 8);
            return unchecked(w0 + w2).ToString("X8") + (w1 >> 16).ToString("X4");
        }

        /// <summary>The port of the device with chip UID <paramref name="uid"/>,
        /// matched by its USB serial number without opening any port. When no port
        /// matches (no UID, USB serials not known as on Windows, or a firmware that
        /// reports another serial), the only WireView port when there is exactly one;
        /// null when it cannot be told which port it is.</summary>
        public static string? FindPortForUid(string? uid) => FindPortInfoForUid(uid)?.Port;

        /// <summary>As <see cref="FindPortForUid"/>, with the port's USB identity.</summary>
        public static WireViewPort? FindPortInfoForUid(string? uid)
        {
            var ports = FindMatchingPorts();
            string? serial = UsbSerialFromUid(uid);
            if (serial != null && ports.FirstOrDefault(p => string.Equals(p.UsbSerial, serial, StringComparison.OrdinalIgnoreCase)) is { } match)
                return match;
            return ports.Count == 1 ? ports[0] : null;
        }

        private static List<WireViewPort> FindMatchingPortsLinux()
        {
            var ports = new List<WireViewPort>();

            const string sysClassTty = "/sys/class/tty";
            if (!Directory.Exists(sysClassTty))
                return ports;

            foreach (var ttyDir in Directory.GetDirectories(sysClassTty, "ttyACM*"))
            {
                try
                {
                    // Resolve the symlink to get the real sysfs device path.
                    // Path.GetFullPath does NOT resolve symlinks on Linux, so use ResolveLinkTarget.
                    var dirInfo = new DirectoryInfo(ttyDir);
                    var resolvedTarget = dirInfo.ResolveLinkTarget(returnFinalTarget: true);
                    var resolvedPath = resolvedTarget?.FullName ?? ttyDir;

                    // Walk up from the resolved path to find the USB device with idVendor/idProduct
                    var searchDir = resolvedPath;

                    while (!string.IsNullOrEmpty(searchDir) && searchDir != "/")
                    {
                        var vendorFile = Path.Combine(searchDir, "idVendor");
                        var productFile = Path.Combine(searchDir, "idProduct");

                        if (File.Exists(vendorFile) && File.Exists(productFile))
                        {
                            var vendor = File.ReadAllText(vendorFile).Trim();
                            var product = File.ReadAllText(productFile).Trim();

                            if (vendor.Equals("0483", StringComparison.OrdinalIgnoreCase) &&
                                product.Equals("5740", StringComparison.OrdinalIgnoreCase))
                            {
                                var ttyName = Path.GetFileName(ttyDir);
                                var serialFile = Path.Combine(searchDir, "serial");
                                string? serial = File.Exists(serialFile) ? File.ReadAllText(serialFile).Trim() : null;
                                ports.Add(new WireViewPort($"/dev/{ttyName}",
                                    string.IsNullOrEmpty(serial) ? null : serial, Path.GetFileName(searchDir)));
                            }

                            break;
                        }

                        searchDir = Path.GetDirectoryName(searchDir);
                    }
                }
                catch
                {
                    // Skip devices we can't read
                }
            }

            ports.Sort((a, b) => TtyNumber(a.Port).CompareTo(TtyNumber(b.Port)));
            return ports;
        }

        private static int TtyNumber(string port) =>
            int.TryParse(port.AsSpan(port.LastIndexOf("ACM", StringComparison.Ordinal) + 3), out int n) ? n : int.MaxValue;

        private static string? TryExtractComPortFromFriendlyName(string friendlyName)
        {
            var start = friendlyName.LastIndexOf("(COM", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return null;
            }

            var end = friendlyName.IndexOf(')', start);
            if (end <= start)
            {
                return null;
            }

            return friendlyName.Substring(start + 1, end - start - 1);
        }
    }
}