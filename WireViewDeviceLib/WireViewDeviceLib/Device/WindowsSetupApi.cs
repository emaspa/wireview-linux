using System.Runtime.InteropServices;

namespace WireView2.Device
{
    internal static class WindowsSetupApi
    {
        public const uint DIGCF_PRESENT = 0x00000002;
        public const uint DIGCF_ALLCLASSES = 0x00000004;
        public const uint DIGCF_DEVICEINTERFACE = 0x00000010;

        public const uint SPDRP_HARDWAREID = 0x00000001;
        public const uint SPDRP_FRIENDLYNAME = 0x0000000C;
        public const uint SPDRP_DEVICEDESC = 0x00000000;

        private static readonly nint InvalidHandleValue = new(-1);

        public static nint SetupDiGetClassDevsAllClassesPresent()
        {
            return SetupDiGetClassDevsW(nint.Zero, null, nint.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        }

        public static nint SetupDiGetClassDevsDeviceInterfacePresent(ref Guid interfaceGuid)
        {
            return SetupDiGetClassDevs(ref interfaceGuid, null, nint.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        }

        public static bool IsInvalidHandle(nint h) => h == nint.Zero || h == InvalidHandleValue;

        public static IEnumerable<string> EnumerateDeviceInstanceIds()
        {
            if (!OperatingSystem.IsWindows())
            {
                yield break;
            }

            nint h = SetupDiGetClassDevsAllClassesPresent();
            if (IsInvalidHandle(h))
            {
                yield break;
            }

            try
            {
                var devInfoData = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                for (uint i = 0; SetupDiEnumDeviceInfo(h, i, ref devInfoData); i++)
                {
                    var id = TryGetDeviceInstanceId(devInfoData.DevInst);
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        yield return id!;
                    }
                }
            }
            finally
            {
                _ = SetupDiDestroyDeviceInfoList(h);
            }
        }

        public static IEnumerable<(string InstanceId, string? DeviceDesc, string? FriendlyName)> EnumeratePresentDevicesWithNames()
        {
            if (!OperatingSystem.IsWindows())
            {
                yield break;
            }

            nint h = SetupDiGetClassDevsAllClassesPresent();
            if (IsInvalidHandle(h))
            {
                yield break;
            }

            try
            {
                var devInfoData = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                for (uint i = 0; SetupDiEnumDeviceInfo(h, i, ref devInfoData); i++)
                {
                    var id = TryGetDeviceInstanceId(devInfoData.DevInst);
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        continue;
                    }

                    var deviceDesc = TryGetDeviceRegistryPropertyString(h, ref devInfoData, SPDRP_DEVICEDESC);
                    var friendlyName = TryGetDeviceRegistryPropertyString(h, ref devInfoData, SPDRP_FRIENDLYNAME);

                    yield return (id!, deviceDesc, friendlyName);
                }
            }
            finally
            {
                _ = SetupDiDestroyDeviceInfoList(h);
            }
        }

        public static string? TryGetDeviceRegistryPropertyString(nint devInfoSet, ref SP_DEVINFO_DATA devInfoData, uint property)
        {
            _ = SetupDiGetDeviceRegistryPropertyW(
                devInfoSet,
                ref devInfoData,
                property,
                out _,
                null,
                0,
                out uint requiredSize);

            if (requiredSize == 0)
            {
                return null;
            }

            var buffer = new byte[requiredSize];
            if (!SetupDiGetDeviceRegistryPropertyW(
                devInfoSet,
                ref devInfoData,
                property,
                out _,
                buffer,
                (uint)buffer.Length,
                out _))
            {
                return null;
            }

            var s = DecodeUtf16String(buffer);
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        public static string[]? TryGetDeviceRegistryPropertyMultiSz(nint devInfoSet, ref SP_DEVINFO_DATA devInfoData, uint property)
        {
            _ = SetupDiGetDeviceRegistryPropertyW(
                devInfoSet,
                ref devInfoData,
                property,
                out _,
                null,
                0,
                out uint requiredSize);

            if (requiredSize == 0)
            {
                return null;
            }

            var buffer = new byte[requiredSize];
            if (!SetupDiGetDeviceRegistryPropertyW(
                devInfoSet,
                ref devInfoData,
                property,
                out _,
                buffer,
                (uint)buffer.Length,
                out _))
            {
                return null;
            }

            return DecodeMultiSzUtf16(buffer);
        }

        public static IEnumerable<string> EnumerateDeviceInterfacePaths(Guid interfaceGuid)
        {
            if (!OperatingSystem.IsWindows())
            {
                yield break;
            }

            nint h = SetupDiGetClassDevsDeviceInterfacePresent(ref interfaceGuid);
            if (IsInvalidHandle(h))
            {
                yield break;
            }

            try
            {
                int index = 0;
                var idd = new SP_DEVICE_INTERFACE_DATA
                {
                    cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>()
                };

                while (SetupDiEnumDeviceInterfaces(h, nint.Zero, ref interfaceGuid, (uint)index, ref idd))
                {
                    var path = TryGetDevicePath(h, ref idd);
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        yield return path!;
                    }

                    index++;
                }
            }
            finally
            {
                _ = SetupDiDestroyDeviceInfoList(h);
            }
        }

        private static string? TryGetDeviceInstanceId(uint devInst)
        {
            const uint CR_SUCCESS = 0x00000000;

            var buffer = new char[512];
            var cr = CM_Get_Device_ID(devInst, buffer, (uint)buffer.Length, 0);
            if (cr == CR_SUCCESS)
            {
                return new string(buffer).TrimEnd('\0');
            }

            if (CM_Get_Device_ID_Size(out var required, devInst, 0) == CR_SUCCESS && required > 0)
            {
                buffer = new char[required + 1];
                if (CM_Get_Device_ID(devInst, buffer, (uint)buffer.Length, 0) == CR_SUCCESS)
                {
                    return new string(buffer).TrimEnd('\0');
                }
            }

            return null;
        }

        private static string DecodeUtf16String(byte[] bytes)
        {
            var s = System.Text.Encoding.Unicode.GetString(bytes);
            var nul = s.IndexOf('\0', StringComparison.Ordinal);
            return nul >= 0 ? s.Substring(0, nul) : s;
        }

        private static string[] DecodeMultiSzUtf16(byte[] bytes)
        {
            var s = System.Text.Encoding.Unicode.GetString(bytes);
            return s.Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        private static string? TryGetDevicePath(nint h, ref SP_DEVICE_INTERFACE_DATA idd)
        {
            uint requiredSize = 0;
            _ = SetupDiGetDeviceInterfaceDetail(h, ref idd, nint.Zero, 0, ref requiredSize, nint.Zero);
            if (requiredSize == 0)
            {
                return null;
            }

            var detailDataBuffer = Marshal.AllocHGlobal((int)requiredSize);
            try
            {
                // cbSize is 8 on x64, 6 on x86 (documented SetupAPI quirk)
                int cbSize = nint.Size == 8 ? 8 : 6;
                Marshal.WriteInt32(detailDataBuffer, cbSize);

                if (!SetupDiGetDeviceInterfaceDetail(h, ref idd, detailDataBuffer, requiredSize, ref requiredSize, nint.Zero))
                {
                    return null;
                }

                // DevicePath starts immediately after cbSize
                var pDevicePath = detailDataBuffer + cbSize;
                var devicePath = Marshal.PtrToStringAuto(pDevicePath);
                return string.IsNullOrWhiteSpace(devicePath) ? null : devicePath;
            }
            finally
            {
                Marshal.FreeHGlobal(detailDataBuffer);
            }
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint SetupDiGetClassDevsW(
            nint classGuid,
            string? enumerator,
            nint hwndParent,
            uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        internal static extern bool SetupDiEnumDeviceInfo(
            nint deviceInfoSet,
            uint memberIndex,
            ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        internal static extern bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern nint SetupDiGetClassDevs(ref Guid ClassGuid, string? Enumerator, nint hwndParent, uint Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(
            nint DeviceInfoSet,
            nint DeviceInfoData,
            ref Guid InterfaceClassGuid,
            uint MemberIndex,
            ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(
            nint DeviceInfoSet,
            ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
            nint DeviceInterfaceDetailData,
            uint DeviceInterfaceDetailDataSize,
            ref uint RequiredSize,
            nint DeviceInfoData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceRegistryPropertyW(
            nint deviceInfoSet,
            ref SP_DEVINFO_DATA deviceInfoData,
            uint property,
            out uint propertyRegDataType,
            byte[]? propertyBuffer,
            uint propertyBufferSize,
            out uint requiredSize);

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVINFO_DATA
        {
            public uint cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public nint Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public uint cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public nint Reserved;
        }

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_Device_ID(uint dnDevInst, [Out] char[] Buffer, uint BufferLen, uint ulFlags);

        [DllImport("cfgmgr32.dll")]
        private static extern uint CM_Get_Device_ID_Size(out uint pulLen, uint dnDevInst, uint ulFlags);
    }
}