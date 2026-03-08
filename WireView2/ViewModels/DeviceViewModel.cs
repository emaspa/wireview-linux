using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using MsgBox;
using WireView2.Device;
using WireView2.Services;

namespace WireView2.ViewModels;

public enum UiThemePreset
{
    ThemeTg1,
    ThemeTg2,
    ThemeTg3,
    ThemeTg4,
    ThemeTg5,
    ThemeTg6,
    Custom
}

public record struct ThemePresetDefinition(
    uint PrimaryColor, uint SecondaryColor, uint HighlightColor, uint BackgroundColor,
    WireViewPro2Device.THEME_BACKGROUND BackgroundBitmap, WireViewPro2Device.THEME_FAN FanBitmap,
    bool DisplayInversion);

public sealed partial class DeviceViewModel : ViewModelBase, IDisposable
{
    private AppSettings.StartupScreen _selectedDeviceScreenTarget;
    private readonly DeviceAutoConnector _connector;
    private readonly bool _ownsConnector;
    private IWireViewDevice? _device;

    private string _firmwareVersion = string.Empty;
    private string _uniqueId = string.Empty;
    private bool _isConnected;
    private string _deviceName = "Not Connected";
    private string? _deviceBuildString;
    private bool _isAveragingSupported;
    private bool _isUiV2Supported;
    private bool _isApplyingThemePreset;

    private string _friendlyName = string.Empty;
    private int _backlightDuty = 50;
    private WireViewPro2Device.FanMode _fanMode;
    private WireViewPro2Device.TempSource _fanTempSource;
    private int _fanDutyMin = 20;
    private int _fanDutyMax = 80;
    private double _fanTempMinC = 30.0;
    private double _fanTempMaxC = 60.0;
    private WireViewPro2Device.CurrentScale _uiCurrentScale;
    private WireViewPro2Device.PowerScale _uiPowerScale;
    private WireViewPro2Device.Theme _uiTheme;
    private WireViewPro2Device.DisplayRotation _uiRotation;
    private WireViewPro2Device.TimeoutMode _uiTimeoutMode;
    private int _uiCycleTimeSeconds = 10;
    private int _uiTimeoutSeconds = 60;
    private WireViewPro2Device.AVG _averaging;
    private WireViewPro2Device.Screen _uiDefaultScreen;
    private Color _uiPrimaryColor;
    private Color _uiSecondaryColor;
    private Color _uiHighlightColor;
    private Color _uiBackgroundColor;
    private bool _uiDisplayInversionEnabled;
    private WireViewPro2Device.THEME_BACKGROUND _uiBackgroundBitmap;
    private WireViewPro2Device.THEME_FAN _uiFanBitmap;
    private UiThemePreset _selectedUiThemePreset;
    private ushort _faultDisplayEnableMask;
    private ushort _faultBuzzerEnableMask;
    private ushort _faultSoftPowerEnableMask;
    private ushort _faultHardPowerEnableMask;
    private double _tsFaultThresholdC;
    private int _ocpFaultThresholdA;
    private double _wireOcpFaultThresholdA;
    private int _oppFaultThresholdW;
    private int _currentImbalanceFaultThresholdPercent;
    private int _currentImbalanceFaultMinLoadA;
    private int _shutdownWaitTimeSeconds;
    private int _loggingIntervalSeconds = 5;
    private bool _configLoaded;
    private string _configStatus = string.Empty;

    // Profile fields
    private string _newProfileName = string.Empty;
    private string? _selectedProfileName;
    private List<string> _profileNames = new();

    // ======================== Enum arrays for ComboBoxes ========================

    public Array DeviceScreenTargets { get; } = Enum.GetValues(typeof(AppSettings.StartupScreen));
    public WireViewPro2Device.FanMode[] FanModes { get; } = Enum.GetValues<WireViewPro2Device.FanMode>();
    public WireViewPro2Device.TempSource[] TempSources { get; } = Enum.GetValues<WireViewPro2Device.TempSource>();
    public WireViewPro2Device.CurrentScale[] CurrentScales { get; } = Enum.GetValues<WireViewPro2Device.CurrentScale>();
    public WireViewPro2Device.PowerScale[] PowerScales { get; } = Enum.GetValues<WireViewPro2Device.PowerScale>();
    public WireViewPro2Device.Theme[] Themes { get; } = Enum.GetValues<WireViewPro2Device.Theme>();
    public WireViewPro2Device.DisplayRotation[] DisplayRotations { get; } = Enum.GetValues<WireViewPro2Device.DisplayRotation>();
    public WireViewPro2Device.TimeoutMode[] TimeoutModes { get; } = Enum.GetValues<WireViewPro2Device.TimeoutMode>();
    public WireViewPro2Device.FAULT[] Faults { get; } = Enum.GetValues<WireViewPro2Device.FAULT>();
    public WireViewPro2Device.AVG[] AveragingOptions { get; } = Enum.GetValues<WireViewPro2Device.AVG>();
    public WireViewPro2Device.Screen[] Screens { get; } = Enum.GetValues<WireViewPro2Device.Screen>();
    public WireViewPro2Device.THEME_BACKGROUND[] ThemeBackgrounds { get; } = Enum.GetValues<WireViewPro2Device.THEME_BACKGROUND>();
    public UiThemePreset[] UiThemePresets { get; } = Enum.GetValues<UiThemePreset>();

    // ======================== Properties ========================

    public AppSettings.StartupScreen SelectedDeviceScreenTarget
    {
        get => _selectedDeviceScreenTarget;
        set { if (Set(ref _selectedDeviceScreenTarget, value)) GoToSelectedScreen(); }
    }

    public string FirmwareVersion
    {
        get => _firmwareVersion;
        private set => Set(ref _firmwareVersion, value);
    }

    public string UniqueId
    {
        get => _uniqueId;
        private set => Set(ref _uniqueId, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set => Set(ref _isConnected, value);
    }

    public string DeviceName
    {
        get => _deviceName;
        set => Set(ref _deviceName, value);
    }

    public string? DeviceBuildString
    {
        get => _deviceBuildString;
        private set => Set(ref _deviceBuildString, value);
    }

    public bool IsAveragingSupported
    {
        get => _isAveragingSupported;
        private set => Set(ref _isAveragingSupported, value);
    }

    public bool IsUiV2Supported
    {
        get => _isUiV2Supported;
        private set => Set(ref _isUiV2Supported, value);
    }

    public bool IsLegacyThemeSelectionVisible => !IsUiV2Supported;
    public bool IsThemePresetSelectionVisible => IsUiV2Supported;

    public string FriendlyName
    {
        get => _friendlyName;
        set => Set(ref _friendlyName, value);
    }

    public int BacklightDuty
    {
        get => _backlightDuty;
        set => Set(ref _backlightDuty, Math.Clamp(value, 0, 100));
    }

    public WireViewPro2Device.FanMode FanMode { get => _fanMode; set => Set(ref _fanMode, value); }
    public WireViewPro2Device.TempSource FanTempSource { get => _fanTempSource; set => Set(ref _fanTempSource, value); }
    public int FanDutyMin { get => _fanDutyMin; set => Set(ref _fanDutyMin, Math.Clamp(value, 0, 100)); }
    public int FanDutyMax { get => _fanDutyMax; set => Set(ref _fanDutyMax, Math.Clamp(value, 0, 100)); }
    public double FanTempMinC { get => _fanTempMinC; set => Set(ref _fanTempMinC, Math.Round(value, 1)); }
    public double FanTempMaxC { get => _fanTempMaxC; set => Set(ref _fanTempMaxC, Math.Round(value, 1)); }

    public WireViewPro2Device.CurrentScale UiCurrentScale { get => _uiCurrentScale; set => Set(ref _uiCurrentScale, value); }
    public WireViewPro2Device.PowerScale UiPowerScale { get => _uiPowerScale; set => Set(ref _uiPowerScale, value); }
    public WireViewPro2Device.Theme UiTheme { get => _uiTheme; set => Set(ref _uiTheme, value); }
    public WireViewPro2Device.DisplayRotation UiRotation { get => _uiRotation; set => Set(ref _uiRotation, value); }
    public WireViewPro2Device.TimeoutMode UiTimeoutMode { get => _uiTimeoutMode; set => Set(ref _uiTimeoutMode, value); }
    public int UiCycleTimeSeconds { get => _uiCycleTimeSeconds; set => Set(ref _uiCycleTimeSeconds, Math.Clamp(value, 1, 60)); }
    public int UiTimeoutSeconds { get => _uiTimeoutSeconds; set => Set(ref _uiTimeoutSeconds, Math.Clamp(value, 0, 255)); }
    public WireViewPro2Device.AVG Averaging { get => _averaging; set => Set(ref _averaging, value); }

    // --------------- V3 UI config (UiConfigStructV2) ---------------

    public WireViewPro2Device.Screen UiDefaultScreen { get => _uiDefaultScreen; set => Set(ref _uiDefaultScreen, value); }

    public Color UiPrimaryColor
    {
        get => _uiPrimaryColor;
        set { if (Set(ref _uiPrimaryColor, value)) { OnPropertyChanged(nameof(UiPrimaryColorDisplay)); SetThemePresetToCustomIfEdited(); } }
    }
    public Color UiSecondaryColor
    {
        get => _uiSecondaryColor;
        set { if (Set(ref _uiSecondaryColor, value)) { OnPropertyChanged(nameof(UiSecondaryColorDisplay)); SetThemePresetToCustomIfEdited(); } }
    }
    public Color UiHighlightColor
    {
        get => _uiHighlightColor;
        set { if (Set(ref _uiHighlightColor, value)) { OnPropertyChanged(nameof(UiHighlightColorDisplay)); SetThemePresetToCustomIfEdited(); } }
    }
    public Color UiBackgroundColor
    {
        get => _uiBackgroundColor;
        set { if (Set(ref _uiBackgroundColor, value)) { OnPropertyChanged(nameof(UiBackgroundColorDisplay)); SetThemePresetToCustomIfEdited(); } }
    }

    public Color UiPrimaryColorDisplay => _uiDisplayInversionEnabled ? InvertColor(_uiPrimaryColor) : _uiPrimaryColor;
    public Color UiSecondaryColorDisplay => _uiDisplayInversionEnabled ? InvertColor(_uiSecondaryColor) : _uiSecondaryColor;
    public Color UiHighlightColorDisplay => _uiDisplayInversionEnabled ? InvertColor(_uiHighlightColor) : _uiHighlightColor;
    public Color UiBackgroundColorDisplay => _uiDisplayInversionEnabled ? InvertColor(_uiBackgroundColor) : _uiBackgroundColor;

    public bool UiDisplayInversionEnabled
    {
        get => _uiDisplayInversionEnabled;
        set
        {
            if (Set(ref _uiDisplayInversionEnabled, value))
            {
                OnPropertyChanged(nameof(UiPrimaryColorDisplay));
                OnPropertyChanged(nameof(UiSecondaryColorDisplay));
                OnPropertyChanged(nameof(UiHighlightColorDisplay));
                OnPropertyChanged(nameof(UiBackgroundColorDisplay));
                SetThemePresetToCustomIfEdited();
            }
        }
    }

    public WireViewPro2Device.THEME_BACKGROUND UiBackgroundBitmap
    {
        get => _uiBackgroundBitmap;
        set
        {
            if (Set(ref _uiBackgroundBitmap, value))
            {
                SyncFanBitmapToBackground();
                SetThemePresetToCustomIfEdited();
            }
        }
    }

    public WireViewPro2Device.THEME_FAN UiFanBitmap
    {
        get => _uiFanBitmap;
        set => Set(ref _uiFanBitmap, value);
    }

    public UiThemePreset SelectedUiThemePreset
    {
        get => _selectedUiThemePreset;
        set
        {
            if (Set(ref _selectedUiThemePreset, value) && !_isApplyingThemePreset)
                ApplyThemePreset(value);
        }
    }

    // --------------- Fault masks ---------------

    public ushort FaultDisplayEnableMask
    {
        get => _faultDisplayEnableMask;
        set { if (Set(ref _faultDisplayEnableMask, value)) RaiseFaultMaskDependentPropertiesChanged(); }
    }

    public ushort FaultBuzzerEnableMask
    {
        get => _faultBuzzerEnableMask;
        set { if (Set(ref _faultBuzzerEnableMask, value)) RaiseFaultMaskDependentPropertiesChanged(); }
    }

    public ushort FaultSoftPowerEnableMask
    {
        get => _faultSoftPowerEnableMask;
        set { if (Set(ref _faultSoftPowerEnableMask, value)) RaiseFaultMaskDependentPropertiesChanged(); }
    }

    public ushort FaultHardPowerEnableMask
    {
        get => _faultHardPowerEnableMask;
        set { if (Set(ref _faultHardPowerEnableMask, value)) RaiseFaultMaskDependentPropertiesChanged(); }
    }

    // --------------- Individual fault bit properties ---------------

    public bool DisplayOtpTs              { get => GetFaultEnabled(FaultDisplayEnableMask, WireViewPro2Device.FAULT.FAULT_OTP_TS);            set => SetFaultEnabled(ref _faultDisplayEnableMask,    nameof(FaultDisplayEnableMask),    WireViewPro2Device.FAULT.FAULT_OTP_TS, value); }
    public bool DisplayOcp                { get => GetFaultEnabled(FaultDisplayEnableMask, WireViewPro2Device.FAULT.FAULT_OCP);               set => SetFaultEnabled(ref _faultDisplayEnableMask,    nameof(FaultDisplayEnableMask),    WireViewPro2Device.FAULT.FAULT_OCP, value); }
    public bool DisplayWireOcp            { get => GetFaultEnabled(FaultDisplayEnableMask, WireViewPro2Device.FAULT.FAULT_WIRE_OCP);          set => SetFaultEnabled(ref _faultDisplayEnableMask,    nameof(FaultDisplayEnableMask),    WireViewPro2Device.FAULT.FAULT_WIRE_OCP, value); }
    public bool DisplayOpp                { get => GetFaultEnabled(FaultDisplayEnableMask, WireViewPro2Device.FAULT.FAULT_OPP);               set => SetFaultEnabled(ref _faultDisplayEnableMask,    nameof(FaultDisplayEnableMask),    WireViewPro2Device.FAULT.FAULT_OPP, value); }
    public bool DisplayCurrentImbalance   { get => GetFaultEnabled(FaultDisplayEnableMask, WireViewPro2Device.FAULT.FAULT_CURRENT_IMBALANCE); set => SetFaultEnabled(ref _faultDisplayEnableMask,    nameof(FaultDisplayEnableMask),    WireViewPro2Device.FAULT.FAULT_CURRENT_IMBALANCE, value); }

    public bool BuzzerOtpTs               { get => GetFaultEnabled(FaultBuzzerEnableMask,  WireViewPro2Device.FAULT.FAULT_OTP_TS);            set => SetFaultEnabled(ref _faultBuzzerEnableMask,     nameof(FaultBuzzerEnableMask),     WireViewPro2Device.FAULT.FAULT_OTP_TS, value); }
    public bool BuzzerOcp                 { get => GetFaultEnabled(FaultBuzzerEnableMask,  WireViewPro2Device.FAULT.FAULT_OCP);               set => SetFaultEnabled(ref _faultBuzzerEnableMask,     nameof(FaultBuzzerEnableMask),     WireViewPro2Device.FAULT.FAULT_OCP, value); }
    public bool BuzzerWireOcp             { get => GetFaultEnabled(FaultBuzzerEnableMask,  WireViewPro2Device.FAULT.FAULT_WIRE_OCP);          set => SetFaultEnabled(ref _faultBuzzerEnableMask,     nameof(FaultBuzzerEnableMask),     WireViewPro2Device.FAULT.FAULT_WIRE_OCP, value); }
    public bool BuzzerOpp                 { get => GetFaultEnabled(FaultBuzzerEnableMask,  WireViewPro2Device.FAULT.FAULT_OPP);               set => SetFaultEnabled(ref _faultBuzzerEnableMask,     nameof(FaultBuzzerEnableMask),     WireViewPro2Device.FAULT.FAULT_OPP, value); }
    public bool BuzzerCurrentImbalance    { get => GetFaultEnabled(FaultBuzzerEnableMask,  WireViewPro2Device.FAULT.FAULT_CURRENT_IMBALANCE); set => SetFaultEnabled(ref _faultBuzzerEnableMask,     nameof(FaultBuzzerEnableMask),     WireViewPro2Device.FAULT.FAULT_CURRENT_IMBALANCE, value); }

    public bool SoftPowerOtpTs            { get => GetFaultEnabled(FaultSoftPowerEnableMask, WireViewPro2Device.FAULT.FAULT_OTP_TS);            set => SetFaultEnabled(ref _faultSoftPowerEnableMask,  nameof(FaultSoftPowerEnableMask),  WireViewPro2Device.FAULT.FAULT_OTP_TS, value); }
    public bool SoftPowerOcp              { get => GetFaultEnabled(FaultSoftPowerEnableMask, WireViewPro2Device.FAULT.FAULT_OCP);               set => SetFaultEnabled(ref _faultSoftPowerEnableMask,  nameof(FaultSoftPowerEnableMask),  WireViewPro2Device.FAULT.FAULT_OCP, value); }
    public bool SoftPowerWireOcp          { get => GetFaultEnabled(FaultSoftPowerEnableMask, WireViewPro2Device.FAULT.FAULT_WIRE_OCP);          set => SetFaultEnabled(ref _faultSoftPowerEnableMask,  nameof(FaultSoftPowerEnableMask),  WireViewPro2Device.FAULT.FAULT_WIRE_OCP, value); }
    public bool SoftPowerOpp              { get => GetFaultEnabled(FaultSoftPowerEnableMask, WireViewPro2Device.FAULT.FAULT_OPP);               set => SetFaultEnabled(ref _faultSoftPowerEnableMask,  nameof(FaultSoftPowerEnableMask),  WireViewPro2Device.FAULT.FAULT_OPP, value); }
    public bool SoftPowerCurrentImbalance { get => GetFaultEnabled(FaultSoftPowerEnableMask, WireViewPro2Device.FAULT.FAULT_CURRENT_IMBALANCE); set => SetFaultEnabled(ref _faultSoftPowerEnableMask,  nameof(FaultSoftPowerEnableMask),  WireViewPro2Device.FAULT.FAULT_CURRENT_IMBALANCE, value); }

    public bool HardPowerOtpTs            { get => GetFaultEnabled(FaultHardPowerEnableMask, WireViewPro2Device.FAULT.FAULT_OTP_TS);            set => SetFaultEnabled(ref _faultHardPowerEnableMask,  nameof(FaultHardPowerEnableMask),  WireViewPro2Device.FAULT.FAULT_OTP_TS, value); }
    public bool HardPowerOcp              { get => GetFaultEnabled(FaultHardPowerEnableMask, WireViewPro2Device.FAULT.FAULT_OCP);               set => SetFaultEnabled(ref _faultHardPowerEnableMask,  nameof(FaultHardPowerEnableMask),  WireViewPro2Device.FAULT.FAULT_OCP, value); }
    public bool HardPowerWireOcp          { get => GetFaultEnabled(FaultHardPowerEnableMask, WireViewPro2Device.FAULT.FAULT_WIRE_OCP);          set => SetFaultEnabled(ref _faultHardPowerEnableMask,  nameof(FaultHardPowerEnableMask),  WireViewPro2Device.FAULT.FAULT_WIRE_OCP, value); }
    public bool HardPowerOpp              { get => GetFaultEnabled(FaultHardPowerEnableMask, WireViewPro2Device.FAULT.FAULT_OPP);               set => SetFaultEnabled(ref _faultHardPowerEnableMask,  nameof(FaultHardPowerEnableMask),  WireViewPro2Device.FAULT.FAULT_OPP, value); }
    public bool HardPowerCurrentImbalance { get => GetFaultEnabled(FaultHardPowerEnableMask, WireViewPro2Device.FAULT.FAULT_CURRENT_IMBALANCE); set => SetFaultEnabled(ref _faultHardPowerEnableMask,  nameof(FaultHardPowerEnableMask),  WireViewPro2Device.FAULT.FAULT_CURRENT_IMBALANCE, value); }

    // --------------- Fault thresholds ---------------

    public double TsFaultThresholdC                     { get => _tsFaultThresholdC;                     set => Set(ref _tsFaultThresholdC, Math.Round(value, 1)); }
    public int    OcpFaultThresholdA                    { get => _ocpFaultThresholdA;                    set => Set(ref _ocpFaultThresholdA, Math.Clamp(value, 0, 255)); }
    public double WireOcpFaultThresholdA                { get => _wireOcpFaultThresholdA;                set => Set(ref _wireOcpFaultThresholdA, Math.Round(value, 1)); }
    public int    OppFaultThresholdW                    { get => _oppFaultThresholdW;                    set => Set(ref _oppFaultThresholdW, Math.Clamp(value, 0, 65535)); }
    public int    CurrentImbalanceFaultThresholdPercent  { get => _currentImbalanceFaultThresholdPercent;  set => Set(ref _currentImbalanceFaultThresholdPercent, Math.Clamp(value, 0, 100)); }
    public int    CurrentImbalanceFaultMinLoadA          { get => _currentImbalanceFaultMinLoadA;          set => Set(ref _currentImbalanceFaultMinLoadA, Math.Clamp(value, 0, 255)); }
    public int    ShutdownWaitTimeSeconds                { get => _shutdownWaitTimeSeconds;                set => Set(ref _shutdownWaitTimeSeconds, Math.Clamp(value, 0, 255)); }
    public int    LoggingIntervalSeconds                 { get => _loggingIntervalSeconds;                 set => Set(ref _loggingIntervalSeconds, Math.Clamp(value, 0, 255)); }

    public bool ConfigLoaded
    {
        get => _configLoaded;
        private set => Set(ref _configLoaded, value);
    }

    public string ConfigStatus
    {
        get => _configStatus;
        private set => Set(ref _configStatus, value);
    }

    // --------------- Profiles ---------------

    public string NewProfileName
    {
        get => _newProfileName;
        set => Set(ref _newProfileName, value);
    }

    public string? SelectedProfileName
    {
        get => _selectedProfileName;
        set => Set(ref _selectedProfileName, value);
    }

    public List<string> ProfileNames
    {
        get => _profileNames;
        private set => Set(ref _profileNames, value);
    }

    // ======================== Constructor ========================

    public DeviceViewModel(DeviceAutoConnector? connector = null)
    {
        _connector = connector ?? DeviceAutoConnector.Shared;
        _ownsConnector = connector != null && connector != DeviceAutoConnector.Shared;
        _connector.ConnectionChanged += OnConnectionChanged;
        _connector.Start();
        OnConnectionChanged(_connector, _connector.Device?.Connected ?? false);
        RefreshProfileList();
    }

    // ======================== Device dispatch helpers ========================

    private bool IsDeviceCommandCapable =>
        _device is WireViewPro2Device ||
        _device is HwmonDevice { DaemonAvailable: true };

    private void DeviceScreenCmd(WireViewPro2Device.SCREEN_CMD cmd)
    {
        if (_device is WireViewPro2Device pro2) pro2.ScreenCmd(cmd);
        else if (_device is HwmonDevice { DaemonAvailable: true } hwmon) hwmon.ScreenCmd(cmd);
    }

    private void DeviceNvmCmd(WireViewPro2Device.NVM_CMD cmd)
    {
        if (_device is WireViewPro2Device pro2) pro2.NvmCmd(cmd);
        else if (_device is HwmonDevice { DaemonAvailable: true } hwmon) hwmon.NvmCmd(cmd);
    }

    private WireViewPro2Device.DeviceConfigStructV3? DeviceReadConfig()
    {
        if (_device is WireViewPro2Device pro2) return pro2.ReadConfig();
        if (_device is HwmonDevice { DaemonAvailable: true } hwmon) return hwmon.ReadConfig();
        return null;
    }

    private void DeviceWriteConfig(WireViewPro2Device.DeviceConfigStructV3 config)
    {
        if (_device is WireViewPro2Device pro2) pro2.WriteConfig(config);
        else if (_device is HwmonDevice { DaemonAvailable: true } hwmon) hwmon.WriteConfig(config);
    }

    private string? DeviceReadBuildString()
    {
        if (_device is WireViewPro2Device pro2) return pro2.ReadBuildString();
        if (_device is HwmonDevice { DaemonAvailable: true } hwmon) return hwmon.ReadBuildString();
        return null;
    }

    // ======================== Connection event ========================

    private void OnConnectionChanged(object? sender, bool connected)
    {
        IsConnected = connected;
        OnPropertyChanged(nameof(IsConnected));
        _device = (sender as DeviceAutoConnector)?.Device;

        if (!connected)
        {
            DeviceName = "Not Connected";
            FirmwareVersion = string.Empty;
            UniqueId = string.Empty;
            DeviceBuildString = null;
            ConfigLoaded = false;
            IsAveragingSupported = false;
            IsUiV2Supported = false;
            _isApplyingThemePreset = true;
            SelectedUiThemePreset = UiThemePreset.Custom;
            _isApplyingThemePreset = false;
        }
        else if (_device != null)
        {
            DeviceName = _device.DeviceName;
            FirmwareVersion = string.IsNullOrEmpty(_device.FirmwareVersion)
                ? "N/A" : "v" + _device.FirmwareVersion.ToString().PadLeft(2, '0');
            UniqueId = string.IsNullOrEmpty(_device.UniqueId) ? "N/A" : _device.UniqueId;
            DeviceBuildString = null;

            if (AppSettings.Current.ScreenAfterConnection != AppSettings.StartupScreen.NoChange
                && IsDeviceCommandCapable)
            {
                DeviceScreenCmd(AppSettings.Current.ScreenAfterConnection switch
                {
                    AppSettings.StartupScreen.Simple      => WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_SIMPLE,
                    AppSettings.StartupScreen.Current     => WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_CURRENT,
                    AppSettings.StartupScreen.Temperature => WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_TEMP,
                    AppSettings.StartupScreen.Status      => WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_STATUS,
                    _                                     => WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_MAIN,
                });
            }

            if (IsDeviceCommandCapable)
            {
                TryReloadConfig();
                _ = ReadBuildStringAsync();
            }
        }
    }

    // ======================== Screen navigation ========================

    private void GoToSelectedScreen()
    {
        try
        {
            if (_device == null || !_device.Connected) { ConfigStatus = "Not connected."; return; }
            if (!IsDeviceCommandCapable) { ConfigStatus = "Unsupported device."; return; }
            if (SelectedDeviceScreenTarget == AppSettings.StartupScreen.NoChange) { ConfigStatus = "Select a screen."; return; }

            DeviceScreenCmd(SelectedDeviceScreenTarget switch
            {
                AppSettings.StartupScreen.Main        => WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_MAIN,
                AppSettings.StartupScreen.Simple      => WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_SIMPLE,
                AppSettings.StartupScreen.Current     => WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_CURRENT,
                AppSettings.StartupScreen.Temperature => WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_TEMP,
                AppSettings.StartupScreen.Status      => WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_STATUS,
                _                                     => WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_MAIN,
            });
            ConfigStatus = $"Switched to {SelectedDeviceScreenTarget}.";
        }
        catch (Exception ex)
        {
            ConfigStatus = "Screen change failed: " + ex.Message;
        }
    }

    private async Task ReadBuildStringAsync()
    {
        string? text = await Task.Run(() => DeviceReadBuildString()).ConfigureAwait(false);
        DeviceBuildString = string.IsNullOrWhiteSpace(text) ? null : "(" + text.Trim() + ")";
    }

    // ======================== Fault mask helpers ========================

    private static bool GetFaultEnabled(ushort mask, WireViewPro2Device.FAULT fault)
    {
        return (mask & (1 << (int)fault)) != 0;
    }

    private void SetFaultEnabled(ref ushort maskField, string maskPropertyName,
        WireViewPro2Device.FAULT fault, bool enabled)
    {
        ushort bit = (ushort)(1 << (int)fault);
        ushort newVal = enabled ? (ushort)(maskField | bit) : (ushort)(maskField & ~bit);
        if (maskField != newVal)
        {
            maskField = newVal;
            OnPropertyChanged(maskPropertyName);
            RaiseFaultMaskDependentPropertiesChanged();
        }
    }

    private void RaiseFaultMaskDependentPropertiesChanged()
    {
        OnPropertyChanged(nameof(DisplayOtpTs));
        OnPropertyChanged(nameof(DisplayOcp));
        OnPropertyChanged(nameof(DisplayWireOcp));
        OnPropertyChanged(nameof(DisplayOpp));
        OnPropertyChanged(nameof(DisplayCurrentImbalance));
        OnPropertyChanged(nameof(BuzzerOtpTs));
        OnPropertyChanged(nameof(BuzzerOcp));
        OnPropertyChanged(nameof(BuzzerWireOcp));
        OnPropertyChanged(nameof(BuzzerOpp));
        OnPropertyChanged(nameof(BuzzerCurrentImbalance));
        OnPropertyChanged(nameof(SoftPowerOtpTs));
        OnPropertyChanged(nameof(SoftPowerOcp));
        OnPropertyChanged(nameof(SoftPowerWireOcp));
        OnPropertyChanged(nameof(SoftPowerOpp));
        OnPropertyChanged(nameof(SoftPowerCurrentImbalance));
        OnPropertyChanged(nameof(HardPowerOtpTs));
        OnPropertyChanged(nameof(HardPowerOcp));
        OnPropertyChanged(nameof(HardPowerWireOcp));
        OnPropertyChanged(nameof(HardPowerOpp));
        OnPropertyChanged(nameof(HardPowerCurrentImbalance));
    }

    // ======================== Config commands ========================

    [RelayCommand]
    private void ReloadConfig() => TryReloadConfig();

    private void TryReloadConfig()
    {
        try
        {
            if (_device == null || !_device.Connected)
            {
                ConfigLoaded = false;
                ConfigStatus = "Not connected.";
                IsAveragingSupported = false;
                return;
            }
            if (!IsDeviceCommandCapable)
            {
                ConfigLoaded = false;
                ConfigStatus = "Unsupported device.";
                IsAveragingSupported = false;
                return;
            }

            var cfg = DeviceReadConfig();
            if (!cfg.HasValue)
            {
                ConfigLoaded = false;
                ConfigStatus = "Failed to read config.";
                return;
            }

            int configVersion = 0;
            if (_device is WireViewPro2Device pro2Dev) configVersion = pro2Dev.ConfigVersion;
            else if (_device is HwmonDevice hwmonDev) configVersion = hwmonDev.ConfigVersion;
            IsAveragingSupported = configVersion >= 1;
            IsUiV2Supported = configVersion >= 2;
            OnPropertyChanged(nameof(IsLegacyThemeSelectionVisible));
            OnPropertyChanged(nameof(IsThemePresetSelectionVisible));
            ApplyToEditor(cfg.Value);
            ConfigLoaded = true;
            ConfigStatus = "Config loaded.";
        }
        catch (Exception ex)
        {
            ConfigLoaded = false;
            ConfigStatus = "Config load failed: " + ex.Message;
            IsAveragingSupported = false;
        }
    }

    [RelayCommand]
    private void ApplyConfig()
    {
        try
        {
            if (_device == null || !_device.Connected) { ConfigStatus = "Not connected."; return; }
            if (!IsDeviceCommandCapable) { ConfigStatus = "Unsupported device."; return; }

            var config = BuildConfigFromEditor();
            DeviceWriteConfig(config);
            DeviceScreenCmd(WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_SAME);
            ConfigStatus = "Config applied.";
        }
        catch (Exception ex)
        {
            ConfigStatus = "Apply failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private void StoreConfig()
    {
        try
        {
            if (_device == null || !_device.Connected) { ConfigStatus = "Not connected."; return; }
            if (!IsDeviceCommandCapable) { ConfigStatus = "Unsupported device."; return; }

            DeviceNvmCmd(WireViewPro2Device.NVM_CMD.NVM_CMD_STORE);
            ConfigStatus = "Config stored (NVM).";
        }
        catch (Exception ex)
        {
            ConfigStatus = "Store failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private void ResetConfig()
    {
        try
        {
            if (_device == null || !_device.Connected) { ConfigStatus = "Not connected."; return; }
            if (!IsDeviceCommandCapable) { ConfigStatus = "Unsupported device."; return; }

            DeviceNvmCmd(WireViewPro2Device.NVM_CMD.NVM_CMD_RESET);
            Task.Delay(75).Wait();
            TryReloadConfig();
            ConfigStatus = "Config reset.";
        }
        catch (Exception ex)
        {
            ConfigStatus = "Reset failed: " + ex.Message;
        }
    }

    // ======================== Config editor mapping ========================

    private void ApplyToEditor(WireViewPro2Device.DeviceConfigStructV3 cfg)
    {
        FriendlyName = DecodeDeviceString(cfg.FriendlyName);
        BacklightDuty = cfg.BacklightDuty;
        FanMode = cfg.FanConfig.Mode;
        FanTempSource = cfg.FanConfig.TempSource;
        FanDutyMin = cfg.FanConfig.DutyMin;
        FanDutyMax = cfg.FanConfig.DutyMax;
        FanTempMinC = cfg.FanConfig.TempMin / 10.0;
        FanTempMaxC = cfg.FanConfig.TempMax / 10.0;
        UiDefaultScreen = cfg.Ui.DefaultScreen;
        UiCurrentScale = cfg.Ui.CurrentScale;
        UiPowerScale = cfg.Ui.PowerScale;
        UiRotation = cfg.Ui.DisplayRotation;
        UiTimeoutMode = cfg.Ui.TimeoutMode;
        UiCycleTimeSeconds = cfg.Ui.CycleTime;
        UiTimeoutSeconds = cfg.Ui.Timeout;
        UiPrimaryColor = ArgbToColor(cfg.Ui.PrimaryColor);
        UiSecondaryColor = ArgbToColor(cfg.Ui.SecondaryColor);
        UiHighlightColor = ArgbToColor(cfg.Ui.HighlightColor);
        UiBackgroundColor = ArgbToColor(cfg.Ui.BackgroundColor);
        UiDisplayInversionEnabled = cfg.Ui.DisplayInversion == WireViewPro2Device.DISPLAY_INVERSION.DISPLAY_INVERSION_ON;
        var bgBitmap = (WireViewPro2Device.THEME_BACKGROUND)cfg.Ui.BackgroundBitmapId;
        UiBackgroundBitmap = Enum.IsDefined(bgBitmap) ? bgBitmap : WireViewPro2Device.THEME_BACKGROUND.Disabled;
        // Derive legacy theme from background bitmap for V1/V2 devices
        UiTheme = cfg.Ui.BackgroundBitmapId == 1 ? WireViewPro2Device.Theme.ThemeTg1
            : cfg.Ui.BackgroundBitmapId == 2 ? WireViewPro2Device.Theme.ThemeTg2
            : WireViewPro2Device.Theme.ThemeTg3;
        FaultDisplayEnableMask = cfg.FaultDisplayEnable;
        FaultBuzzerEnableMask = cfg.FaultBuzzerEnable;
        FaultSoftPowerEnableMask = cfg.FaultSoftPowerEnable;
        FaultHardPowerEnableMask = cfg.FaultHardPowerEnable;
        TsFaultThresholdC = cfg.TsFaultThreshold / 10.0;
        OcpFaultThresholdA = cfg.OcpFaultThreshold;
        WireOcpFaultThresholdA = (int)cfg.WireOcpFaultThreshold / 10.0;
        OppFaultThresholdW = cfg.OppFaultThreshold;
        CurrentImbalanceFaultThresholdPercent = cfg.CurrentImbalanceFaultThreshold;
        CurrentImbalanceFaultMinLoadA = cfg.CurrentImbalanceFaultMinLoad;
        ShutdownWaitTimeSeconds = cfg.ShutdownWaitTime;
        LoggingIntervalSeconds = cfg.LoggingInterval;
        if (IsAveragingSupported) Averaging = cfg.Average;
        if (IsUiV2Supported)
        {
            _isApplyingThemePreset = true;
            SelectedUiThemePreset = DetectThemePresetFromCurrentUi();
            _isApplyingThemePreset = false;
        }
    }

    private WireViewPro2Device.DeviceConfigStructV3 BuildConfigFromEditor()
    {
        var cfg = (DeviceReadConfig()).GetValueOrDefault();
        cfg.FriendlyName = EncodeDeviceString(FriendlyName, 32);
        cfg.BacklightDuty = (byte)Math.Clamp(BacklightDuty, 0, 100);
        cfg.FanConfig.Mode = FanMode;
        cfg.FanConfig.TempSource = FanTempSource;
        cfg.FanConfig.DutyMin = (byte)Math.Clamp(FanDutyMin, 0, 100);
        cfg.FanConfig.DutyMax = (byte)Math.Clamp(FanDutyMax, 0, 100);
        cfg.FanConfig.TempMin = (short)Math.Clamp((int)Math.Round(FanTempMinC * 10.0), -32768, 32767);
        cfg.FanConfig.TempMax = (short)Math.Clamp((int)Math.Round(FanTempMaxC * 10.0), -32768, 32767);
        cfg.Ui.DefaultScreen = UiDefaultScreen;
        cfg.Ui.CurrentScale = UiCurrentScale;
        cfg.Ui.PowerScale = UiPowerScale;
        cfg.Ui.DisplayRotation = UiRotation;
        cfg.Ui.TimeoutMode = UiTimeoutMode;
        cfg.Ui.CycleTime = (byte)Math.Clamp(UiCycleTimeSeconds, 1, 60);
        cfg.Ui.Timeout = (byte)Math.Clamp(UiTimeoutSeconds, 0, 255);
        cfg.Ui.PrimaryColor = ColorToArgb(UiPrimaryColor);
        cfg.Ui.SecondaryColor = ColorToArgb(UiSecondaryColor);
        cfg.Ui.HighlightColor = ColorToArgb(UiHighlightColor);
        cfg.Ui.BackgroundColor = ColorToArgb(UiBackgroundColor);
        cfg.Ui.BackgroundBitmapId = (byte)UiBackgroundBitmap;
        cfg.Ui.FanBitmapId = (byte)MapFanBitmapFromBackground(UiBackgroundBitmap);
        cfg.Ui.DisplayInversion = UiDisplayInversionEnabled
            ? WireViewPro2Device.DISPLAY_INVERSION.DISPLAY_INVERSION_ON
            : WireViewPro2Device.DISPLAY_INVERSION.DISPLAY_INVERSION_OFF;
        cfg.FaultDisplayEnable = FaultDisplayEnableMask;
        cfg.FaultBuzzerEnable = FaultBuzzerEnableMask;
        cfg.FaultSoftPowerEnable = FaultSoftPowerEnableMask;
        cfg.FaultHardPowerEnable = FaultHardPowerEnableMask;
        cfg.TsFaultThreshold = (short)Math.Clamp((int)Math.Round(TsFaultThresholdC * 10.0), -32768, 32767);
        cfg.OcpFaultThreshold = (byte)Math.Clamp(OcpFaultThresholdA, 0, 255);
        cfg.WireOcpFaultThreshold = (byte)Math.Clamp((int)Math.Round(WireOcpFaultThresholdA * 10.0), 0, 255);
        cfg.OppFaultThreshold = (ushort)Math.Clamp(OppFaultThresholdW, 0, 65535);
        cfg.CurrentImbalanceFaultThreshold = (byte)Math.Clamp(CurrentImbalanceFaultThresholdPercent, 0, 100);
        cfg.CurrentImbalanceFaultMinLoad = (byte)Math.Clamp(CurrentImbalanceFaultMinLoadA, 0, 255);
        cfg.ShutdownWaitTime = (byte)Math.Clamp(ShutdownWaitTimeSeconds, 0, 255);
        cfg.LoggingInterval = (byte)Math.Clamp(LoggingIntervalSeconds, 0, 255);
        if (IsAveragingSupported) cfg.Average = Averaging;
        return cfg;
    }

    // ======================== Theme preset helpers ========================

    private UiThemePreset DetectThemePresetFromCurrentUi()
    {
        uint primary = ColorToArgb(UiPrimaryColor);
        uint secondary = ColorToArgb(UiSecondaryColor);
        uint highlight = ColorToArgb(UiHighlightColor);
        uint background = ColorToArgb(UiBackgroundColor);
        foreach (var preset in UiThemePresets)
        {
            if (preset == UiThemePreset.Custom) continue;
            var def = GetThemePresetDefinition(preset);
            if (primary == def.PrimaryColor && secondary == def.SecondaryColor
                && highlight == def.HighlightColor && background == def.BackgroundColor
                && UiBackgroundBitmap == def.BackgroundBitmap && UiFanBitmap == def.FanBitmap
                && UiDisplayInversionEnabled == def.DisplayInversion)
                return preset;
        }
        return UiThemePreset.Custom;
    }

    private void ApplyThemePreset(UiThemePreset preset)
    {
        if (preset == UiThemePreset.Custom) return;
        var def = GetThemePresetDefinition(preset);
        _isApplyingThemePreset = true;
        try
        {
            UiPrimaryColor = ArgbToColor(def.PrimaryColor);
            UiSecondaryColor = ArgbToColor(def.SecondaryColor);
            UiHighlightColor = ArgbToColor(def.HighlightColor);
            UiBackgroundColor = ArgbToColor(def.BackgroundColor);
            UiBackgroundBitmap = def.BackgroundBitmap;
            UiDisplayInversionEnabled = def.DisplayInversion;
        }
        finally { _isApplyingThemePreset = false; }
    }

    private void SetThemePresetToCustomIfEdited()
    {
        if (!IsUiV2Supported || _isApplyingThemePreset || SelectedUiThemePreset == UiThemePreset.Custom)
            return;
        _isApplyingThemePreset = true;
        try { SelectedUiThemePreset = UiThemePreset.Custom; }
        finally { _isApplyingThemePreset = false; }
    }

    private static ThemePresetDefinition GetThemePresetDefinition(UiThemePreset preset) => preset switch
    {
        UiThemePreset.ThemeTg1 => new(0xFFFFFFFF, 0xFF646464, 0xFFE64121, 0xFF000000,
            WireViewPro2Device.THEME_BACKGROUND.ThermalGrizzlyOrange, WireViewPro2Device.THEME_FAN.ThermalGrizzlyOrange, false),
        UiThemePreset.ThemeTg2 => new(0xFFFFFFFF, 0xFF646464, 0xFFBEBEBE, 0xFF000000,
            WireViewPro2Device.THEME_BACKGROUND.ThermalGrizzlyDark, WireViewPro2Device.THEME_FAN.ThermalGrizzlyDark, false),
        UiThemePreset.ThemeTg3 => new(0xFF969696, 0xFF505050, 0xFFFFFFFF, 0xFF000000,
            WireViewPro2Device.THEME_BACKGROUND.Disabled, WireViewPro2Device.THEME_FAN.ThermalGrizzlyBlackWhite, false),
        UiThemePreset.ThemeTg4 => new(0xFFFFFFFF, 0xFF646464, 0xFFE64121, 0xFF000000,
            WireViewPro2Device.THEME_BACKGROUND.ThermalGrizzlyOrange, WireViewPro2Device.THEME_FAN.ThermalGrizzlyOrange, true),
        UiThemePreset.ThemeTg5 => new(0xFFFFFFFF, 0xFF646464, 0xFFBEBEBE, 0xFF000000,
            WireViewPro2Device.THEME_BACKGROUND.ThermalGrizzlyDark, WireViewPro2Device.THEME_FAN.ThermalGrizzlyDark, true),
        UiThemePreset.ThemeTg6 => new(0xFF969696, 0xFF505050, 0xFFFFFFFF, 0xFF000000,
            WireViewPro2Device.THEME_BACKGROUND.Disabled, WireViewPro2Device.THEME_FAN.ThermalGrizzlyBlackWhite, true),
        _ => new(0xFF969696, 0xFF505050, 0xFFFFFFFF, 0xFF000000,
            WireViewPro2Device.THEME_BACKGROUND.Disabled, WireViewPro2Device.THEME_FAN.ThermalGrizzlyBlackWhite, false),
    };

    private static WireViewPro2Device.THEME_FAN MapFanBitmapFromBackground(WireViewPro2Device.THEME_BACKGROUND background) => background switch
    {
        WireViewPro2Device.THEME_BACKGROUND.ThermalGrizzlyOrange => WireViewPro2Device.THEME_FAN.ThermalGrizzlyOrange,
        WireViewPro2Device.THEME_BACKGROUND.ThermalGrizzlyDark => WireViewPro2Device.THEME_FAN.ThermalGrizzlyDark,
        _ => WireViewPro2Device.THEME_FAN.ThermalGrizzlyBlackWhite,
    };

    private void SyncFanBitmapToBackground()
    {
        var fan = MapFanBitmapFromBackground(_uiBackgroundBitmap);
        if (!_uiFanBitmap.Equals(fan))
        {
            _uiFanBitmap = fan;
            OnPropertyChanged(nameof(UiFanBitmap));
        }
    }

    private static Color ArgbToColor(uint argb) =>
        Color.FromArgb((byte)((argb >> 24) & 0xFF), (byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));

    private static uint ColorToArgb(Color c) =>
        (uint)((c.A << 24) | (c.R << 16) | (c.G << 8) | c.B);

    private static Color InvertColor(Color c) =>
        Color.FromArgb(c.A, (byte)(255 - c.R), (byte)(255 - c.G), (byte)(255 - c.B));

    // ======================== String encoding ========================

    private static string DecodeDeviceString(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return string.Empty;
        int len = Array.IndexOf(bytes, (byte)0);
        if (len < 0) len = bytes.Length;
        return Encoding.ASCII.GetString(bytes, 0, len).Trim();
    }

    private static byte[] EncodeDeviceString(string? str, int len)
    {
        byte[] buf = new byte[len];
        string s = (str ?? string.Empty).Trim();
        byte[] ascii = Encoding.ASCII.GetBytes(s);
        int count = Math.Min(ascii.Length, len - 1);
        Array.Copy(ascii, 0, buf, 0, count);
        buf[count] = 0;
        return buf;
    }

    // ======================== Profile commands ========================

    [RelayCommand]
    private void SaveProfile()
    {
        string name = string.IsNullOrWhiteSpace(NewProfileName)
            ? SelectedProfileName ?? string.Empty
            : NewProfileName.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            ConfigStatus = "Enter a profile name to save.";
            return;
        }

        var profile = BuildProfileFromEditor(name);
        DeviceProfileService.SaveProfile(profile);
        NewProfileName = string.Empty;
        RefreshProfileList();
        SelectedProfileName = name;
        ConfigStatus = $"Profile \"{name}\" saved.";
    }

    [RelayCommand]
    private void LoadProfile()
    {
        if (string.IsNullOrWhiteSpace(SelectedProfileName))
        {
            ConfigStatus = "Select a profile to load.";
            return;
        }

        var profile = DeviceProfileService.LoadProfile(SelectedProfileName);
        if (profile == null)
        {
            ConfigStatus = $"Failed to load profile \"{SelectedProfileName}\".";
            return;
        }

        ApplyProfileToEditor(profile);
        ConfigStatus = $"Profile \"{SelectedProfileName}\" loaded. Press Apply to send to device.";
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (string.IsNullOrWhiteSpace(SelectedProfileName))
        {
            ConfigStatus = "Select a profile to delete.";
            return;
        }

        string name = SelectedProfileName;
        DeviceProfileService.DeleteProfile(name);
        SelectedProfileName = null;
        RefreshProfileList();
        ConfigStatus = $"Profile \"{name}\" deleted.";
    }

    private void RefreshProfileList()
    {
        ProfileNames = DeviceProfileService.ListProfiles();
    }

    private DeviceProfile BuildProfileFromEditor(string name)
    {
        return new DeviceProfile
        {
            Name = name,
            FriendlyName = FriendlyName,
            BacklightDuty = BacklightDuty,
            FanMode = (int)FanMode,
            FanTempSource = (int)FanTempSource,
            FanDutyMin = FanDutyMin,
            FanDutyMax = FanDutyMax,
            FanTempMinC = FanTempMinC,
            FanTempMaxC = FanTempMaxC,
            UiCurrentScale = (int)UiCurrentScale,
            UiPowerScale = (int)UiPowerScale,
            UiTheme = (int)UiTheme,
            UiRotation = (int)UiRotation,
            UiTimeoutMode = (int)UiTimeoutMode,
            UiCycleTimeSeconds = UiCycleTimeSeconds,
            UiTimeoutSeconds = UiTimeoutSeconds,
            Averaging = (int)Averaging,
            FaultDisplayEnableMask = FaultDisplayEnableMask,
            FaultBuzzerEnableMask = FaultBuzzerEnableMask,
            FaultSoftPowerEnableMask = FaultSoftPowerEnableMask,
            FaultHardPowerEnableMask = FaultHardPowerEnableMask,
            TsFaultThresholdC = TsFaultThresholdC,
            OcpFaultThresholdA = OcpFaultThresholdA,
            WireOcpFaultThresholdA = WireOcpFaultThresholdA,
            OppFaultThresholdW = OppFaultThresholdW,
            CurrentImbalanceFaultThresholdPercent = CurrentImbalanceFaultThresholdPercent,
            CurrentImbalanceFaultMinLoadA = CurrentImbalanceFaultMinLoadA,
            ShutdownWaitTimeSeconds = ShutdownWaitTimeSeconds,
            LoggingIntervalSeconds = LoggingIntervalSeconds,
        };
    }

    private void ApplyProfileToEditor(DeviceProfile p)
    {
        FriendlyName = p.FriendlyName;
        BacklightDuty = p.BacklightDuty;
        FanMode = (WireViewPro2Device.FanMode)p.FanMode;
        FanTempSource = (WireViewPro2Device.TempSource)p.FanTempSource;
        FanDutyMin = p.FanDutyMin;
        FanDutyMax = p.FanDutyMax;
        FanTempMinC = p.FanTempMinC;
        FanTempMaxC = p.FanTempMaxC;
        UiCurrentScale = (WireViewPro2Device.CurrentScale)p.UiCurrentScale;
        UiPowerScale = (WireViewPro2Device.PowerScale)p.UiPowerScale;
        UiTheme = (WireViewPro2Device.Theme)p.UiTheme;
        UiRotation = (WireViewPro2Device.DisplayRotation)p.UiRotation;
        UiTimeoutMode = (WireViewPro2Device.TimeoutMode)p.UiTimeoutMode;
        UiCycleTimeSeconds = p.UiCycleTimeSeconds;
        UiTimeoutSeconds = p.UiTimeoutSeconds;
        Averaging = (WireViewPro2Device.AVG)p.Averaging;
        FaultDisplayEnableMask = p.FaultDisplayEnableMask;
        FaultBuzzerEnableMask = p.FaultBuzzerEnableMask;
        FaultSoftPowerEnableMask = p.FaultSoftPowerEnableMask;
        FaultHardPowerEnableMask = p.FaultHardPowerEnableMask;
        TsFaultThresholdC = p.TsFaultThresholdC;
        OcpFaultThresholdA = p.OcpFaultThresholdA;
        WireOcpFaultThresholdA = p.WireOcpFaultThresholdA;
        OppFaultThresholdW = p.OppFaultThresholdW;
        CurrentImbalanceFaultThresholdPercent = p.CurrentImbalanceFaultThresholdPercent;
        CurrentImbalanceFaultMinLoadA = p.CurrentImbalanceFaultMinLoadA;
        ShutdownWaitTimeSeconds = p.ShutdownWaitTimeSeconds;
        LoggingIntervalSeconds = p.LoggingIntervalSeconds;
    }

    // ======================== Dispose ========================

    public void Dispose()
    {
        _connector.ConnectionChanged -= OnConnectionChanged;
        if (_ownsConnector)
            _connector.Dispose();
    }
}
