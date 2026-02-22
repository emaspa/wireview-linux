using System.Runtime.InteropServices;

namespace WireView2.Device
{
    public static class Stm32PortFinder
    {
        public static List<string> FindMatchingComPorts()
        {
            if (OperatingSystem.IsLinux())
                return FindMatchingComPortsLinux();

            if (!OperatingSystem.IsWindows())
                return new List<string>();

            var ports = new List<string>();

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
                    if (!string.IsNullOrWhiteSpace(comPort))
                    {
                        ports.Add(comPort);
                    }
                }
            }
            finally
            {
                _ = WindowsSetupApi.SetupDiDestroyDeviceInfoList(devInfo);
            }

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

        private static List<string> FindMatchingComPortsLinux()
        {
            var ports = new List<string>();

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
                                ports.Add($"/dev/{ttyName}");
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

            return ports;
        }

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