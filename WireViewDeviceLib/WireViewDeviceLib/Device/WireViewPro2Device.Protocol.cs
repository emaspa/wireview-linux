using System;
using System.Runtime.InteropServices;

namespace WireView2.Device
{
    public partial class WireViewPro2Device
    {
        // Keep in sync with firmware DEVICE_STR_LEN
        public const int DEVICE_STR_LEN = 32;

        private enum UsbCmd : byte
        {
            CMD_WELCOME,
            CMD_READ_VENDOR_DATA,
            CMD_READ_UID,
            CMD_READ_DEVICE_DATA,
            CMD_READ_SENSOR_VALUES,
            CMD_READ_CONFIG,
            CMD_WRITE_CONFIG,
            CMD_READ_CALIBRATION,
            CMD_WRITE_CALIBRATION,
            CMD_SPI_FLASH_WRITE_PAGE,
            CMD_SPI_FLASH_READ_PAGE,
            CMD_SPI_FLASH_ERASE_SECTOR,
            CMD_SCREEN_CHANGE,
            CMD_READ_BUILD_INFO,
            CMD_CLEAR_FAULTS,
            CMD_RESET = 0xF0,
            CMD_BOOTLOADER = 0xF1,
            CMD_NVM_CONFIG = 0xF2,
            CMD_NOP = 0xFF
        }

        private enum SensorTs
        {
            SENSOR_TS_IN,
            SENSOR_TS_OUT,
            SENSOR_TS3,
            SENSOR_TS4
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct VendorDataStruct
        {
            public byte VendorId;
            public byte ProductId;
            public byte FwVersion;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct BuildStruct
        {
            public VendorDataStruct VendorData;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = DEVICE_STR_LEN)]
            public string ProductName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = DEVICE_STR_LEN)]
            public string BuildInfo;
            public byte ProductNameLength;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PowerSensor
        {
            public short Voltage;
            public uint Current;
            public uint Power;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct SensorStruct
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public short[] Ts; // 0.1 °C
            public ushort Vdd; // mV
            public byte FanDuty; // %

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
            public PowerSensor[] PowerReadings;

            public uint TotalPower; // mW
            public uint TotalCurrent; // mA
            public ushort AvgVoltage; // mV
            public HpwrCapability HpwrCapability; // 8-bit enum
            public ushort FaultStatus;
            public ushort FaultLog;
        }

        private enum HpwrCapability : byte
        {
            PSU_CAP_600W = 0,
            PSU_CAP_450W = 1,
            PSU_CAP_300W = 2,
            PSU_CAP_150W = 3
        }

        // ===== Device config (matches firmware) =====

        public enum FanMode : byte
        {
            FanModeCurve = 0,
            FanModeFixed = 1
        }

        public enum TempSource : byte
        {
            TempSourceTsIn = 0,
            TempSourceTsOut = 1,
            TempSourceTs1 = 2,
            TempSourceTs2 = 3,
            TempSourceTmax = 4
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct FanConfigStruct
        {
            public FanMode Mode;
            public TempSource TempSource;
            public byte DutyMin;
            public byte DutyMax;
            public short TempMin;
            public short TempMax;
        }

        public enum CurrentScale : byte
        {
            CurrentScale5A = 0,
            CurrentScale10A = 1,
            CurrentScale15A = 2,
            CurrentScale20A = 3
        }

        public enum PowerScale : byte
        {
            PowerScaleAuto = 0,
            PowerScale300W = 1,
            PowerScale600W = 2
        }

        public enum Theme : byte
        {
            ThemeTg1 = 0,
            ThemeTg2 = 1,
            ThemeTg3 = 2
        }

        public enum DisplayRotation : byte
        {
            DisplayRotation0 = 0,
            DisplayRotation180 = 1
        }

        public enum TimeoutMode : byte
        {
            TimeoutModeStatic = 0,
            TimeoutModeCycle = 1,
            TimeoutModeSleep = 2
        }

        public enum Screen : byte
        {
            ScreenMain = 0,
            ScreenSimple = 1,
            ScreenCurrent = 2,
            ScreenTemp = 3,
            ScreenStatus = 4
        }

        public enum FAULT : byte {
            FAULT_OTP_TCHIP,
            FAULT_OTP_TS,
            FAULT_OCP,
            FAULT_WIRE_OCP,
            FAULT_OPP,
            FAULT_CURRENT_IMBALANCE
        }

        public enum NVM_CMD : byte
        {
            NVM_CMD_NONE,
            NVM_CMD_LOAD,
            NVM_CMD_STORE,
            NVM_CMD_RESET,
            NVM_CMD_LOAD_CAL,
            NVM_CMD_STORE_CAL,
            NVM_CMD_LOAD_CAL_FACTORY,
            NVM_CMD_STORE_CAL_FACTORY
        }

        public enum SCREEN_CMD : byte
        {
            SCREEN_GOTO_MAIN = 0xE0,
            SCREEN_GOTO_SIMPLE = 0xE1,
            SCREEN_GOTO_CURRENT = 0xE2,
            SCREEN_GOTO_TEMP = 0xE3,
            SCREEN_GOTO_STATUS = 0xE4,
            SCREEN_GOTO_SAME = 0xEF,
            SCREEN_PAUSE_UPDATES = 0xF0,
            SCREEN_RESUME_UPDATES = 0xF1
        }

        public enum AVG : byte
        { 
            AVG_22MS,
            AVG_44MS,
            AVG_89MS,
            AVG_177MS,
            AVG_354MS,
            AVG_709MS,
            AVG_1417MS,
            AVG_NUM
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct UiConfigStruct
        {
            public CurrentScale CurrentScale;
            public PowerScale PowerScale;
            public Theme Theme;
            public DisplayRotation DisplayRotation;
            public TimeoutMode TimeoutMode;
            public byte CycleScreens; // bitmask of SCREEN_*
            public byte CycleTime; // seconds
            public byte Timeout; // seconds
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Ansi)]
        public struct DeviceConfigStructV1
        {
            public ushort Crc;
            public byte Version;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = DEVICE_STR_LEN)]
            public byte[] FriendlyName;

            public FanConfigStruct FanConfig;
            public byte BacklightDuty;

            public ushort FaultDisplayEnable;
            public ushort FaultBuzzerEnable;
            public ushort FaultSoftPowerEnable;
            public ushort FaultHardPowerEnable;
            public short TsFaultThreshold; // 0.1 °C
            public byte OcpFaultThreshold; // A
            public byte WireOcpFaultThreshold; // 0.1A
            public ushort OppFaultThreshold; // W
            public byte CurrentImbalanceFaultThreshold; // %
            public byte CurrentImbalanceFaultMinLoad; // A
            public byte ShutdownWaitTime; // seconds
            public byte LoggingInterval; // seconds
            public UiConfigStruct Ui;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Ansi)]
        public struct DeviceConfigStructV2
        {
            public ushort Crc;
            public byte Version;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = DEVICE_STR_LEN)]
            public byte[] FriendlyName;

            public FanConfigStruct FanConfig;
            public byte BacklightDuty;

            public ushort FaultDisplayEnable;
            public ushort FaultBuzzerEnable;
            public ushort FaultSoftPowerEnable;
            public ushort FaultHardPowerEnable;
            public short TsFaultThreshold; // 0.1 °C
            public byte OcpFaultThreshold; // A
            public byte WireOcpFaultThreshold; // 0.1A
            public ushort OppFaultThreshold; // W
            public byte CurrentImbalanceFaultThreshold; // %
            public byte CurrentImbalanceFaultMinLoad; // A
            public byte ShutdownWaitTime; // seconds
            public byte LoggingInterval; // seconds
            public AVG Average;
            public UiConfigStruct Ui;
        }

        // Default DeviceConfigStruct = V2
        DeviceConfigStructV1 ConvertConfigV2ToV1(DeviceConfigStructV2 configV2)
        {
            DeviceConfigStructV1 configV1 = new DeviceConfigStructV1
            {
                Crc = configV2.Crc,
                Version = configV2.Version,
                FriendlyName = configV2.FriendlyName,
                FanConfig = configV2.FanConfig,
                BacklightDuty = configV2.BacklightDuty,
                FaultDisplayEnable = configV2.FaultDisplayEnable,
                FaultBuzzerEnable = configV2.FaultBuzzerEnable,
                FaultSoftPowerEnable = configV2.FaultSoftPowerEnable,
                FaultHardPowerEnable = configV2.FaultHardPowerEnable,
                TsFaultThreshold = configV2.TsFaultThreshold,
                OcpFaultThreshold = configV2.OcpFaultThreshold,
                WireOcpFaultThreshold = configV2.WireOcpFaultThreshold,
                OppFaultThreshold = configV2.OppFaultThreshold,
                CurrentImbalanceFaultThreshold = configV2.CurrentImbalanceFaultThreshold,
                CurrentImbalanceFaultMinLoad = configV2.CurrentImbalanceFaultMinLoad,
                ShutdownWaitTime = configV2.ShutdownWaitTime,
                LoggingInterval = configV2.LoggingInterval,
                Ui = configV2.Ui
            };
            return configV1;
        }

        DeviceConfigStructV2 ConvertConfigV1ToV2(DeviceConfigStructV1 configV1)
        {
            DeviceConfigStructV2 configV2 = new DeviceConfigStructV2
            {
                Crc = configV1.Crc,
                Version = configV1.Version,
                FriendlyName = configV1.FriendlyName,
                FanConfig = configV1.FanConfig,
                BacklightDuty = configV1.BacklightDuty,
                FaultDisplayEnable = configV1.FaultDisplayEnable,
                FaultBuzzerEnable = configV1.FaultBuzzerEnable,
                FaultSoftPowerEnable = configV1.FaultSoftPowerEnable,
                FaultHardPowerEnable = configV1.FaultHardPowerEnable,
                TsFaultThreshold = configV1.TsFaultThreshold,
                OcpFaultThreshold = configV1.OcpFaultThreshold,
                WireOcpFaultThreshold = configV1.WireOcpFaultThreshold,
                OppFaultThreshold = configV1.OppFaultThreshold,
                CurrentImbalanceFaultThreshold = configV1.CurrentImbalanceFaultThreshold,
                CurrentImbalanceFaultMinLoad = configV1.CurrentImbalanceFaultMinLoad,
                ShutdownWaitTime = configV1.ShutdownWaitTime,
                LoggingInterval = configV1.LoggingInterval,
                Average = AVG.AVG_1417MS, // Default value
                Ui = configV1.Ui
            };
            return configV2;
        }

    }
}