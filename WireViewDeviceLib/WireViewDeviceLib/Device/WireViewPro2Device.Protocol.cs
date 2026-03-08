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
            public short[] Ts; // 0.1 �C
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

        public enum DISPLAY_INVERSION : byte
        {
            DISPLAY_INVERSION_OFF,
            DISPLAY_INVERSION_ON,
            DISPLAY_INVERSION_NUM
        }

        public enum THEME_BACKGROUND : byte
        {
            ThermalGrizzlyOrange = 1,
            ThermalGrizzlyDark = 2,
            Disabled = byte.MaxValue
        }

        public enum THEME_FAN : byte
        {
            ThermalGrizzlyOrange = 100,
            ThermalGrizzlyDark = 117,
            ThermalGrizzlyBlackWhite = 152
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct UiConfigStructV1
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
            public short TsFaultThreshold; // 0.1 �C
            public byte OcpFaultThreshold; // A
            public byte WireOcpFaultThreshold; // 0.1A
            public ushort OppFaultThreshold; // W
            public byte CurrentImbalanceFaultThreshold; // %
            public byte CurrentImbalanceFaultMinLoad; // A
            public byte ShutdownWaitTime; // seconds
            public byte LoggingInterval; // seconds
            public UiConfigStructV1 Ui;
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
            public short TsFaultThreshold; // 0.1 �C
            public byte OcpFaultThreshold; // A
            public byte WireOcpFaultThreshold; // 0.1A
            public ushort OppFaultThreshold; // W
            public byte CurrentImbalanceFaultThreshold; // %
            public byte CurrentImbalanceFaultMinLoad; // A
            public byte ShutdownWaitTime; // seconds
            public byte LoggingInterval; // seconds
            public AVG Average;
            public UiConfigStructV1 Ui;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct UiConfigStructV2
        {
            public Screen DefaultScreen;
            public CurrentScale CurrentScale;
            public PowerScale PowerScale;
            public DisplayRotation DisplayRotation;
            public TimeoutMode TimeoutMode;
            public byte CycleScreens;
            public byte CycleTime;
            public byte Timeout;
            public uint PrimaryColor;
            public uint SecondaryColor;
            public uint HighlightColor;
            public uint BackgroundColor;
            public byte BackgroundBitmapId;
            public byte FanBitmapId;
            public DISPLAY_INVERSION DisplayInversion;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Ansi)]
        public struct DeviceConfigStructV3
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
            public short TsFaultThreshold;
            public byte OcpFaultThreshold;
            public byte WireOcpFaultThreshold;
            public ushort OppFaultThreshold;
            public byte CurrentImbalanceFaultThreshold;
            public byte CurrentImbalanceFaultMinLoad;
            public byte ShutdownWaitTime;
            public byte LoggingInterval;
            public AVG Average;
            public UiConfigStructV2 Ui;
        }

        // Theme color constants (ARGB)
        public const uint THEME_PRIMARY_COLOR_TG1 = uint.MaxValue;
        public const uint THEME_SECONDARY_COLOR_TG1 = 4284769380u;
        public const uint THEME_HIGHLIGHT_COLOR_TG1 = 4293280033u;
        public const uint THEME_BACKGROUND_COLOR_TG1 = 4278190080u;

        public const uint THEME_PRIMARY_COLOR_TG2 = uint.MaxValue;
        public const uint THEME_SECONDARY_COLOR_TG2 = 4284769380u;
        public const uint THEME_HIGHLIGHT_COLOR_TG2 = 4290690750u;
        public const uint THEME_BACKGROUND_COLOR_TG2 = 4278190080u;

        public const uint THEME_PRIMARY_COLOR_TG3 = 4288059030u;
        public const uint THEME_SECONDARY_COLOR_TG3 = 4283453520u;
        public const uint THEME_HIGHLIGHT_COLOR_TG3 = uint.MaxValue;
        public const uint THEME_BACKGROUND_COLOR_TG3 = 4278190080u;

        // Default DeviceConfigStruct = V2
        internal static DeviceConfigStructV1 ConvertConfigV2ToV1(DeviceConfigStructV2 configV2)
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

        internal static DeviceConfigStructV2 ConvertConfigV1ToV2(DeviceConfigStructV1 configV1)
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

        internal static DeviceConfigStructV3 ConvertConfigV2ToV3(DeviceConfigStructV2 configV2)
        {
            return new DeviceConfigStructV3
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
                Average = configV2.Average,
                Ui = new UiConfigStructV2
                {
                    DefaultScreen = Screen.ScreenMain,
                    CurrentScale = configV2.Ui.CurrentScale,
                    PowerScale = configV2.Ui.PowerScale,
                    DisplayRotation = configV2.Ui.DisplayRotation,
                    TimeoutMode = configV2.Ui.TimeoutMode,
                    CycleScreens = configV2.Ui.CycleScreens,
                    CycleTime = configV2.Ui.CycleTime,
                    Timeout = configV2.Ui.Timeout,
                    PrimaryColor = configV2.Ui.Theme == Theme.ThemeTg1 ? THEME_PRIMARY_COLOR_TG1 : configV2.Ui.Theme == Theme.ThemeTg2 ? THEME_PRIMARY_COLOR_TG2 : THEME_PRIMARY_COLOR_TG3,
                    SecondaryColor = configV2.Ui.Theme == Theme.ThemeTg1 ? THEME_SECONDARY_COLOR_TG1 : configV2.Ui.Theme == Theme.ThemeTg2 ? THEME_SECONDARY_COLOR_TG2 : THEME_SECONDARY_COLOR_TG3,
                    HighlightColor = configV2.Ui.Theme == Theme.ThemeTg1 ? THEME_HIGHLIGHT_COLOR_TG1 : configV2.Ui.Theme == Theme.ThemeTg2 ? THEME_HIGHLIGHT_COLOR_TG2 : THEME_HIGHLIGHT_COLOR_TG3,
                    BackgroundColor = configV2.Ui.Theme == Theme.ThemeTg1 ? THEME_BACKGROUND_COLOR_TG1 : configV2.Ui.Theme == Theme.ThemeTg2 ? THEME_BACKGROUND_COLOR_TG2 : THEME_BACKGROUND_COLOR_TG3,
                    BackgroundBitmapId = (byte)(configV2.Ui.Theme == Theme.ThemeTg1 ? 1 : configV2.Ui.Theme == Theme.ThemeTg2 ? 2 : byte.MaxValue),
                    FanBitmapId = (byte)(configV2.Ui.Theme == Theme.ThemeTg1 ? 100 : configV2.Ui.Theme == Theme.ThemeTg2 ? 117 : 152),
                    DisplayInversion = DISPLAY_INVERSION.DISPLAY_INVERSION_OFF
                }
            };
        }

        internal static DeviceConfigStructV2 ConvertConfigV3ToV2(DeviceConfigStructV3 configV3)
        {
            return new DeviceConfigStructV2
            {
                Crc = configV3.Crc,
                Version = configV3.Version,
                FriendlyName = configV3.FriendlyName,
                FanConfig = configV3.FanConfig,
                BacklightDuty = configV3.BacklightDuty,
                FaultDisplayEnable = configV3.FaultDisplayEnable,
                FaultBuzzerEnable = configV3.FaultBuzzerEnable,
                FaultSoftPowerEnable = configV3.FaultSoftPowerEnable,
                FaultHardPowerEnable = configV3.FaultHardPowerEnable,
                TsFaultThreshold = configV3.TsFaultThreshold,
                OcpFaultThreshold = configV3.OcpFaultThreshold,
                WireOcpFaultThreshold = configV3.WireOcpFaultThreshold,
                OppFaultThreshold = configV3.OppFaultThreshold,
                CurrentImbalanceFaultThreshold = configV3.CurrentImbalanceFaultThreshold,
                CurrentImbalanceFaultMinLoad = configV3.CurrentImbalanceFaultMinLoad,
                ShutdownWaitTime = configV3.ShutdownWaitTime,
                LoggingInterval = configV3.LoggingInterval,
                Average = configV3.Average,
                Ui = new UiConfigStructV1
                {
                    Theme = configV3.Ui.BackgroundBitmapId == 1 ? Theme.ThemeTg1 : configV3.Ui.BackgroundBitmapId == 2 ? Theme.ThemeTg2 : Theme.ThemeTg3,
                    CurrentScale = configV3.Ui.CurrentScale,
                    PowerScale = configV3.Ui.PowerScale,
                    DisplayRotation = configV3.Ui.DisplayRotation,
                    TimeoutMode = configV3.Ui.TimeoutMode,
                    CycleScreens = configV3.Ui.CycleScreens,
                    CycleTime = configV3.Ui.CycleTime,
                    Timeout = configV3.Ui.Timeout
                }
            };
        }

        internal static DeviceConfigStructV1 ConvertConfigV3ToV1(DeviceConfigStructV3 configV3)
        {
            return ConvertConfigV2ToV1(ConvertConfigV3ToV2(configV3));
        }

        internal static DeviceConfigStructV3 ConvertConfigV1ToV3(DeviceConfigStructV1 configV1)
        {
            return ConvertConfigV2ToV3(ConvertConfigV1ToV2(configV1));
        }

    }
}