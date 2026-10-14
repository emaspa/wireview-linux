using System.Text;

namespace WireView2.Device
{
    /// <summary>Editable config values in the units the app shows (°C, A, W, s).
    /// An edit is the difference between two of these: the values as loaded and
    /// the values now. Colours are ARGB.</summary>
    public readonly record struct ConfigEditorValues(
        string FriendlyName, int BacklightDuty,
        WireViewPro2Device.FanMode FanMode, WireViewPro2Device.TempSource FanTempSource,
        int FanDutyMin, int FanDutyMax, double FanTempMinC, double FanTempMaxC,
        WireViewPro2Device.Screen UiDefaultScreen, WireViewPro2Device.CurrentScale UiCurrentScale,
        WireViewPro2Device.PowerScale UiPowerScale, WireViewPro2Device.Theme UiTheme,
        WireViewPro2Device.DisplayRotation UiRotation, WireViewPro2Device.TimeoutMode UiTimeoutMode,
        int UiCycleTimeSeconds, int UiTimeoutSeconds,
        uint UiPrimaryColor, uint UiSecondaryColor, uint UiHighlightColor, uint UiBackgroundColor,
        WireViewPro2Device.THEME_BACKGROUND UiBackgroundBitmap, bool UiDisplayInversionEnabled,
        ushort FaultDisplayEnableMask, ushort FaultBuzzerEnableMask,
        ushort FaultSoftPowerEnableMask, ushort FaultHardPowerEnableMask,
        double TsFaultThresholdC, int OcpFaultThresholdA, double WireOcpFaultThresholdA,
        int OppFaultThresholdW, int CurrentImbalanceFaultThresholdPercent,
        int CurrentImbalanceFaultMinLoadA, int ShutdownWaitTimeSeconds, int LoggingIntervalSeconds,
        WireViewPro2Device.AVG Averaging);

    /// <summary>
    /// The config editor's pure logic, shared by the app's Device page and the
    /// mini's control panel (<see cref="DeviceConfigModel"/>): device strings,
    /// the theme's fan bitmap, and <see cref="ApplyChanges"/>, which writes only
    /// the fields that were edited.
    /// </summary>
    public static class DeviceConfigEditor
    {
        /// <summary>Puts the fields that differ between <paramref name="was"/> and
        /// <paramref name="now"/> into <paramref name="cfg"/> (the device's current
        /// config) and leaves every other field as the device has it. The editor maps
        /// values it cannot show (an unknown background id reads as Disabled, a clamped
        /// backlight, an unknown enum), so writing unchanged fields back would replace
        /// what the device holds, e.g. a Noctua Edition theme value.</summary>
        public static WireViewPro2Device.DeviceConfigStructV3 ApplyChanges(
            WireViewPro2Device.DeviceConfigStructV3 cfg, ConfigEditorValues was, ConfigEditorValues now,
            bool averagingSupported, bool uiV2Supported)
        {
            if (now.FriendlyName != was.FriendlyName) cfg.FriendlyName = EncodeDeviceString(now.FriendlyName, WireViewPro2Device.DEVICE_STR_LEN);
            if (now.BacklightDuty != was.BacklightDuty) cfg.BacklightDuty = (byte)Math.Clamp(now.BacklightDuty, 0, 100);
            if (now.FanMode != was.FanMode) cfg.FanConfig.Mode = now.FanMode;
            if (now.FanTempSource != was.FanTempSource) cfg.FanConfig.TempSource = now.FanTempSource;
            if (now.FanDutyMin != was.FanDutyMin) cfg.FanConfig.DutyMin = (byte)Math.Clamp(now.FanDutyMin, 0, 100);
            if (now.FanDutyMax != was.FanDutyMax) cfg.FanConfig.DutyMax = (byte)Math.Clamp(now.FanDutyMax, 0, 100);
            if (now.FanTempMinC != was.FanTempMinC) cfg.FanConfig.TempMin = (short)Math.Clamp((int)Math.Round(now.FanTempMinC * 10.0), -32768, 32767);
            if (now.FanTempMaxC != was.FanTempMaxC) cfg.FanConfig.TempMax = (short)Math.Clamp((int)Math.Round(now.FanTempMaxC * 10.0), -32768, 32767);
            if (now.UiDefaultScreen != was.UiDefaultScreen) cfg.Ui.DefaultScreen = now.UiDefaultScreen;
            if (now.UiCurrentScale != was.UiCurrentScale) cfg.Ui.CurrentScale = now.UiCurrentScale;
            if (now.UiPowerScale != was.UiPowerScale) cfg.Ui.PowerScale = now.UiPowerScale;
            if (now.UiRotation != was.UiRotation) cfg.Ui.DisplayRotation = now.UiRotation;
            if (now.UiTimeoutMode != was.UiTimeoutMode) cfg.Ui.TimeoutMode = now.UiTimeoutMode;
            if (now.UiCycleTimeSeconds != was.UiCycleTimeSeconds) cfg.Ui.CycleTime = (byte)Math.Clamp(now.UiCycleTimeSeconds, 1, 60);
            if (now.UiTimeoutSeconds != was.UiTimeoutSeconds) cfg.Ui.Timeout = (byte)Math.Clamp(now.UiTimeoutSeconds, 0, 255);
            if (now.UiPrimaryColor != was.UiPrimaryColor) cfg.Ui.PrimaryColor = now.UiPrimaryColor;
            if (now.UiSecondaryColor != was.UiSecondaryColor) cfg.Ui.SecondaryColor = now.UiSecondaryColor;
            if (now.UiHighlightColor != was.UiHighlightColor) cfg.Ui.HighlightColor = now.UiHighlightColor;
            if (now.UiBackgroundColor != was.UiBackgroundColor) cfg.Ui.BackgroundColor = now.UiBackgroundColor;
            if (now.UiBackgroundBitmap != was.UiBackgroundBitmap)
            {
                cfg.Ui.BackgroundBitmapId = (byte)now.UiBackgroundBitmap;
                cfg.Ui.FanBitmapId = (byte)FanBitmapFor(now.UiBackgroundBitmap);
            }
            // Config V1/V2 devices store a legacy theme, derived from the bitmap id on
            // the way to the device (ConvertConfigV3ToV2).
            if (!uiV2Supported && now.UiTheme != was.UiTheme)
                cfg.Ui.BackgroundBitmapId = now.UiTheme switch
                {
                    WireViewPro2Device.Theme.ThemeTg1 => 1,
                    WireViewPro2Device.Theme.ThemeTg2 => 2,
                    _ => byte.MaxValue,
                };
            if (now.UiDisplayInversionEnabled != was.UiDisplayInversionEnabled)
                cfg.Ui.DisplayInversion = now.UiDisplayInversionEnabled
                    ? WireViewPro2Device.DISPLAY_INVERSION.DISPLAY_INVERSION_ON
                    : WireViewPro2Device.DISPLAY_INVERSION.DISPLAY_INVERSION_OFF;
            if (now.FaultDisplayEnableMask != was.FaultDisplayEnableMask) cfg.FaultDisplayEnable = now.FaultDisplayEnableMask;
            if (now.FaultBuzzerEnableMask != was.FaultBuzzerEnableMask) cfg.FaultBuzzerEnable = now.FaultBuzzerEnableMask;
            if (now.FaultSoftPowerEnableMask != was.FaultSoftPowerEnableMask) cfg.FaultSoftPowerEnable = now.FaultSoftPowerEnableMask;
            if (now.FaultHardPowerEnableMask != was.FaultHardPowerEnableMask) cfg.FaultHardPowerEnable = now.FaultHardPowerEnableMask;
            if (now.TsFaultThresholdC != was.TsFaultThresholdC) cfg.TsFaultThreshold = (short)Math.Clamp((int)Math.Round(now.TsFaultThresholdC * 10.0), -32768, 32767);
            if (now.OcpFaultThresholdA != was.OcpFaultThresholdA) cfg.OcpFaultThreshold = (byte)Math.Clamp(now.OcpFaultThresholdA, 0, 255);
            if (now.WireOcpFaultThresholdA != was.WireOcpFaultThresholdA) cfg.WireOcpFaultThreshold = (byte)Math.Clamp((int)Math.Round(now.WireOcpFaultThresholdA * 10.0), 0, 255);
            if (now.OppFaultThresholdW != was.OppFaultThresholdW) cfg.OppFaultThreshold = (ushort)Math.Clamp(now.OppFaultThresholdW, 0, 65535);
            if (now.CurrentImbalanceFaultThresholdPercent != was.CurrentImbalanceFaultThresholdPercent) cfg.CurrentImbalanceFaultThreshold = (byte)Math.Clamp(now.CurrentImbalanceFaultThresholdPercent, 0, 100);
            if (now.CurrentImbalanceFaultMinLoadA != was.CurrentImbalanceFaultMinLoadA) cfg.CurrentImbalanceFaultMinLoad = (byte)Math.Clamp(now.CurrentImbalanceFaultMinLoadA, 0, 255);
            if (now.ShutdownWaitTimeSeconds != was.ShutdownWaitTimeSeconds) cfg.ShutdownWaitTime = (byte)Math.Clamp(now.ShutdownWaitTimeSeconds, 0, 255);
            if (now.LoggingIntervalSeconds != was.LoggingIntervalSeconds) cfg.LoggingInterval = (byte)Math.Clamp(now.LoggingIntervalSeconds, 0, 255);
            if (averagingSupported && now.Averaging != was.Averaging) cfg.Average = now.Averaging;
            return cfg;
        }

        /// <summary>The fan animation that goes with a background bitmap.</summary>
        public static WireViewPro2Device.THEME_FAN FanBitmapFor(WireViewPro2Device.THEME_BACKGROUND background) => background switch
        {
            WireViewPro2Device.THEME_BACKGROUND.ThermalGrizzlyOrange => WireViewPro2Device.THEME_FAN.ThermalGrizzlyOrange,
            WireViewPro2Device.THEME_BACKGROUND.ThermalGrizzlyDark => WireViewPro2Device.THEME_FAN.ThermalGrizzlyDark,
            _ => WireViewPro2Device.THEME_FAN.ThermalGrizzlyBlackWhite,
        };

        /// <summary>A NUL-terminated ASCII device string (the friendly name).</summary>
        public static string DecodeDeviceString(byte[]? bytes)
        {
            if (bytes == null || bytes.Length == 0) return string.Empty;
            int len = Array.IndexOf(bytes, (byte)0);
            if (len < 0) len = bytes.Length;
            return Encoding.ASCII.GetString(bytes, 0, len).Trim();
        }

        /// <summary>Encodes a device string into <paramref name="len"/> bytes, at most
        /// len - 1 characters followed by NUL padding.</summary>
        public static byte[] EncodeDeviceString(string? str, int len)
        {
            byte[] buf = new byte[len];
            string s = (str ?? string.Empty).Trim();
            byte[] ascii = Encoding.ASCII.GetBytes(s);
            int count = Math.Min(ascii.Length, len - 1);
            Array.Copy(ascii, 0, buf, 0, count);
            buf[count] = 0;
            return buf;
        }
    }
}
