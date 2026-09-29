using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using MsgBox;
using WireView2.Device;
using WireView2.Net;
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
    private WireViewEdition _deviceEdition;
    private string? _deviceBuildString;
    private string _bundledFirmwareVersion = "-";
    private string? _bundledBuildString;
    private bool _isBundledFirmwareNewerThanDevice;
    private bool _isFirmwareUpdateSupported = true;
    private FirmwareImageInfo? _bundledFirmware;
    private int? _lastKnownDeviceFirmwareVersionNumber;
    private DateTime? _lastKnownDeviceFirmwareBuildTime;
    // Identity of the connected device, captured on connect (0 when unknown).
    private byte _deviceVendorId;
    private byte _deviceProductId;
    private bool _isFirmwareUpdating;
    private double _firmwareUpdateProgress;
    private string _firmwareUpdateStatus = string.Empty;
    private bool _awaitingPostFlashReconnect;
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
    // Upstream 1.0.8: all nine averaging windows on firmware v05 and newer, the
    // first seven (up to 1417 ms) before.
    private const int ExtendedAveragingMinProFirmwareVersion = 5;
    private static readonly WireViewPro2Device.AVG[] AllAveragingOptions = Enum.GetValues<WireViewPro2Device.AVG>();
    private static readonly WireViewPro2Device.AVG[] LegacyAveragingOptions =
        AllAveragingOptions.Where(a => (int)a <= (int)WireViewPro2Device.AVG.AVG_1417MS).ToArray();
    private WireViewPro2Device.AVG[] _averagingOptions = LegacyAveragingOptions;

    /// <summary>The averaging windows offered for the connected firmware, plus the
    /// device's current value when it is not among them (an unknown value, or an
    /// extended one on older firmware), so it is shown and kept as it is.</summary>
    public WireViewPro2Device.AVG[] AveragingOptions
    {
        get => _averagingOptions;
        private set => Set(ref _averagingOptions, value);
    }
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

    /// <summary>Edition of the connected device (Unknown while disconnected).</summary>
    public WireViewEdition DeviceEdition
    {
        get => _deviceEdition;
        private set
        {
            if (Set(ref _deviceEdition, value))
                OnPropertyChanged(nameof(IsNoctuaEdition));
        }
    }

    public bool IsNoctuaEdition => DeviceEdition == WireViewEdition.Pro2Noctua;

    public string? DeviceBuildString
    {
        get => _deviceBuildString;
        private set => Set(ref _deviceBuildString, value);
    }

    public string BundledFirmwareVersion
    {
        get => _bundledFirmwareVersion;
        private set => Set(ref _bundledFirmwareVersion, value);
    }

    public string? BundledBuildString
    {
        get => _bundledBuildString;
        private set => Set(ref _bundledBuildString, value);
    }

    public bool IsBundledFirmwareNewerThanDevice
    {
        get => _isBundledFirmwareNewerThanDevice;
        private set
        {
            if (Set(ref _isBundledFirmwareNewerThanDevice, value))
                OnPropertyChanged(nameof(BundledFirmwareUpdateNotice));
        }
    }

    public string BundledFirmwareUpdateNotice =>
        IsBundledFirmwareNewerThanDevice ? "Newer firmware version available." : string.Empty;

    /// <summary>False while connected to a device the bundled image is not for
    /// (vendor or aliased product differ, or the device is unidentified).
    /// Upstream 1.0.8 hides the update panels on it; here it disables the button.</summary>
    public bool IsFirmwareUpdateSupported
    {
        get => _isFirmwareUpdateSupported;
        private set
        {
            if (Set(ref _isFirmwareUpdateSupported, value))
                OnPropertyChanged(nameof(CanStartFirmwareUpdate));
        }
    }

    public bool IsFirmwareUpdating
    {
        get => _isFirmwareUpdating;
        private set
        {
            if (Set(ref _isFirmwareUpdating, value))
                OnPropertyChanged(nameof(CanStartFirmwareUpdate));
        }
    }

    /// <summary>0..1 while a DFU download is running.</summary>
    public double FirmwareUpdateProgress
    {
        get => _firmwareUpdateProgress;
        private set => Set(ref _firmwareUpdateProgress, value);
    }

    public string FirmwareUpdateStatus
    {
        get => _firmwareUpdateStatus;
        private set => Set(ref _firmwareUpdateStatus, value);
    }

    /// <summary>Flashing needs the device on this host's USB — remote (LAN) devices
    /// can't be flashed from here.</summary>
    public bool CanStartFirmwareUpdate =>
        !IsFirmwareUpdating && IsConnected && IsFirmwareUpdateSupported
        && _device is WireViewPro2Device or HwmonDevice;

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
        LoadBundledFirmwareVersion();
        InitializeThemeEditor();
        OnConnectionChanged(_connector, _connector.Device?.Connected ?? false);
        RefreshProfileList();
    }

    // ======================== Bundled firmware ========================

    /// <summary>Path of the firmware image shipped alongside the app, if any.</summary>
    public static string GetBundledFirmwarePath() =>
        Path.Combine(AppContext.BaseDirectory, "TG-WV-PRO2-FW.hex");

    public string FirmwareUpdateHint => OperatingSystem.IsWindows()
        ? "Flashes the firmware image shipped with this app (TG-WV-PRO2-FW.hex) over USB, using " +
          "the same native DFU method as the official client. The device restarts into its " +
          "bootloader, is flashed, and reboots on its own. Do not unplug it during the update. " +
          "Requires a WinUSB driver for the DFU device (0483:df11). " +
          "Unofficial software: flash at your own risk."
        : "Flashes the firmware image shipped with this app (TG-WV-PRO2-FW.hex) over USB using " +
          "dfu-util. The device restarts into its bootloader, is flashed, and reboots on its own. " +
          "Do not unplug it during the update. Requires the dfu-util package and the bundled udev rules. " +
          "Unofficial software: flash at your own risk.";

    private static string FormatFirmwareVersion(int? version) =>
        version.HasValue ? "v" + version.Value.ToString().PadLeft(2, '0') : "-";

    private const string BundledFirmwareMismatchText = "The bundled firmware does not match this device.";

    private static string FormatBuild(string? build) =>
        string.IsNullOrWhiteSpace(build) ? string.Empty : "(" + build.Trim() + ")";

    /// <summary>Upstream 1.0.8's UpdateBundledFirmwareComparison: the image must be
    /// for this device (vendor, aliased product); the notice needs it newer by
    /// version, then by build date. Here it also stays off when the image predates
    /// Noctua Edition support on a Noctua device, since the flash would be refused.</summary>
    private void UpdateBundledFirmwareComparison()
    {
        bool match = IsConnected && _bundledFirmware != null
            && FirmwareCompatibility.ProductMatches(_bundledFirmware, _deviceVendorId, _deviceProductId);
        IsFirmwareUpdateSupported = !IsConnected || match;
        if (IsConnected && !match && _bundledFirmware != null && !IsFirmwareUpdating)
            FirmwareUpdateStatus = BundledFirmwareMismatchText;
        else if (FirmwareUpdateStatus == BundledFirmwareMismatchText)
            FirmwareUpdateStatus = string.Empty;
        IsBundledFirmwareNewerThanDevice = match
            && !FirmwareCompatibility.IsBelowNoctuaFloor(_bundledFirmware!, _deviceVendorId, _deviceProductId)
            && FirmwareCompatibility.Compare(_bundledFirmware!, _lastKnownDeviceFirmwareVersionNumber,
                _lastKnownDeviceFirmwareBuildTime) > 0;
    }

    private void LoadBundledFirmwareVersion()
    {
        string path = GetBundledFirmwarePath();
        if (File.Exists(path) && FirmwareHexInfo.TryReadInfo(path, out var info, out _) && info != null)
        {
            _bundledFirmware = info;
            BundledFirmwareVersion = FormatFirmwareVersion(info.Version);
            BundledBuildString = string.IsNullOrWhiteSpace(info.BuildString) ? null : FormatBuild(info.BuildString);
        }
        else
        {
            _bundledFirmware = null;
            BundledFirmwareVersion = "-";
            BundledBuildString = null;
        }
        UpdateBundledFirmwareComparison();
    }

    // ======================== Firmware update (DFU via dfu-util) ========================

    [RelayCommand]
    private async Task UpdateFirmware()
    {
        if (IsFirmwareUpdating) return;
        if (_device == null || !_device.Connected)
        {
            FirmwareUpdateStatus = "Not connected.";
            return;
        }
        if (_device is not (WireViewPro2Device or HwmonDevice))
        {
            FirmwareUpdateStatus = "Remote devices can only be flashed from the host they are plugged into.";
            return;
        }
        // The gates and the dialogs are about this device; the picker may switch
        // the selection while a dialog is open.
        var device = _device;

        string hexPath = GetBundledFirmwarePath();
        if (!File.Exists(hexPath))
        {
            FirmwareUpdateStatus = "No bundled firmware image (TG-WV-PRO2-FW.hex) next to the application.";
            return;
        }

        // Re-read the bundled image: it may have been swapped on disk since startup.
        // The gates below judge exactly the bytes that get flashed, so a custom
        // image dropped in its place goes through the same checks.
        LoadBundledFirmwareVersion();
        if (!FirmwareHexInfo.TryReadImage(hexPath, out uint baseAddress, out byte[] image,
                out FirmwareImageInfo? imageInfo, out string? imageError) || imageInfo == null)
        {
            FirmwareUpdateStatus = "Could not parse the bundled firmware image: " + imageError;
            return;
        }
        string imageLabel = $"{FormatFirmwareVersion(imageInfo.Version)} {FormatBuild(imageInfo.BuildString)}".Trim();
        string deviceLabel = _lastKnownDeviceFirmwareVersionNumber.HasValue
            ? $"{FormatFirmwareVersion(_lastKnownDeviceFirmwareVersionNumber)} {FormatBuild(_device.BuildString)}".Trim()
            : "an unknown firmware version";

        var verdict = FirmwareCompatibility.Evaluate(imageInfo, device.VendorId, device.ProductId,
            _lastKnownDeviceFirmwareVersionNumber, _lastKnownDeviceFirmwareBuildTime);
        if (verdict == FlashVerdict.ProductMismatch)
        {
            FirmwareUpdateStatus =
                $"The firmware image is for product {WireViewEditions.FormatHardwareRevision(imageInfo.VendorId, imageInfo.ProductId)}, " +
                $"not for this device ({WireViewEditions.FormatHardwareRevision(device.VendorId, device.ProductId)}). Nothing was flashed.";
            return;
        }
        if (verdict == FlashVerdict.TooOldForNoctua)
        {
            FirmwareUpdateStatus =
                $"The firmware image {imageLabel} predates Noctua Edition support (builds from " +
                $"{FirmwareCompatibility.NoctuaMinimumBuildDate:yyyy-MM-dd} on) and cannot be flashed to a " +
                $"{WireViewEditions.Pro2NoctuaName}. Nothing was flashed.";
            return;
        }

        // Windows flashes natively over WinUSB like the official client;
        // dfu-util is only the Linux path.
        if (!OperatingSystem.IsWindows() && await DfuUtilFlasher.GetDfuUtilVersionAsync() == null)
        {
            FirmwareUpdateStatus = "dfu-util not found. Install the 'dfu-util' package and try again.";
            return;
        }

        // Same rules as `wireviewctl flash`: an older or identical build is refused
        // unless the user explicitly overrides it here.
        if (verdict is FlashVerdict.Downgrade or FlashVerdict.Same)
        {
            var overrideGate = await MessageBox.Show(null,
                verdict == FlashVerdict.Downgrade
                    ? $"The bundled firmware ({imageLabel}) is older than " +
                      $"the device firmware ({deviceLabel}).\n\n" +
                      "Flashing older firmware can remove features or fixes. Do you want to continue?"
                    : $"The device already runs this firmware build ({deviceLabel}).\n\n" +
                      "Flash it again anyway?",
                verdict == FlashVerdict.Downgrade ? "Older firmware warning" : "Same firmware",
                MessageBox.MessageBoxButtons.YesNo);
            if (overrideGate != MessageBox.MessageBoxResult.Yes)
            {
                FirmwareUpdateStatus = "Firmware update cancelled.";
                return;
            }
        }

        var confirm = await MessageBox.Show(null,
            $"Flash firmware {imageLabel} to {DeviceName}?\n\n" +
            $"The device runs {deviceLabel}.\n\n" +
            "The device restarts into its bootloader and is flashed over USB. " +
            "Do not unplug it until the update finishes.\n\n" +
            "This is unofficial software, not affiliated with Thermal Grizzly. " +
            "Flashing is at your own risk.",
            "Firmware update", MessageBox.MessageBoxButtons.YesNo);
        if (confirm != MessageBox.MessageBoxResult.Yes)
        {
            FirmwareUpdateStatus = "Firmware update cancelled.";
            return;
        }

        if (!ReferenceEquals(device, _device) || !device.Connected)
        {
            FirmwareUpdateStatus = "The selected device changed or disconnected. Nothing was flashed.";
            return;
        }

        IsFirmwareUpdating = true;
        FirmwareUpdateProgress = 0;
        // The flash makes the device drop off and re-enumerate, which knocks the
        // picker over to whatever source survives — remember what was selected so
        // it can be restored once the device is back.
        string? selectionToRestore = DeviceManager.Shared.SelectedId;
        string binPath = Path.Combine(Path.GetTempPath(), $"wireview2-fw-{Guid.NewGuid():N}.bin");
        try
        {
            FirmwareUpdateStatus = "Restarting device into DFU bootloader…";
            switch (device)
            {
                case WireViewPro2Device pro2: pro2.EnterBootloader(); break;
                case HwmonDevice hwmon:
                    // Denied/Error/NotConnected mean the daemon never sent the
                    // command, so the device is still running: stop here. With no
                    // answer at all (Unavailable) the command may have gone through;
                    // fall through to waiting for the DFU device as before.
                    var boot = hwmon.EnterBootloader();
                    if (boot == DaemonResult.Denied)
                    {
                        FirmwareUpdateStatus = DaemonResults.DeniedMessage;
                        return;
                    }
                    if (boot is DaemonResult.Error or DaemonResult.NotConnected)
                    {
                        FirmwareUpdateStatus = $"Could not restart the device into its bootloader: {boot.Describe()}.";
                        return;
                    }
                    break;
            }

            var progress = new Progress<double>(p => FirmwareUpdateProgress = p);
            if (OperatingSystem.IsWindows())
            {
                // Native WinUSB DFU, same method as the official Windows client
                // (it waits for the DFU interface and handles the Guillemot
                // driver conflict internally). The flat image is flashed at
                // 0x08000000, which is where the bundled hex is based.
                if (baseAddress != 0x08000000u)
                {
                    FirmwareUpdateStatus = $"Unexpected firmware base address 0x{baseAddress:X8}.";
                    return;
                }
                FirmwareUpdateStatus = $"Flashing {imageLabel}…";
                using var imageStream = new MemoryStream(image);
                await DfuFirmwareUpdater.UpdateAsync(imageStream, progress, CancellationToken.None);
            }
            else
            {
                await File.WriteAllBytesAsync(binPath, image);
                if (!await DfuUtilFlasher.WaitForDfuDeviceAsync(TimeSpan.FromSeconds(20), CancellationToken.None))
                {
                    FirmwareUpdateStatus =
                        "The DFU bootloader did not appear. Check that the udev rule for 0483:df11 is " +
                        "installed, then power-cycle the device and try again.";
                    return;
                }
                FirmwareUpdateStatus = $"Flashing {imageLabel}…";
                await DfuUtilFlasher.FlashAsync(binPath, baseAddress, progress, CancellationToken.None);
            }

            FirmwareUpdateProgress = 1.0;
            FirmwareUpdateStatus = "Firmware update complete. The device is restarting.";
            _awaitingPostFlashReconnect = true;
        }
        catch (Exception ex)
        {
            FirmwareUpdateStatus = "Firmware update failed: " + ex.Message;
        }
        finally
        {
            try { File.Delete(binPath); } catch { }
            IsFirmwareUpdating = false;
            if (selectionToRestore != null)
                _ = RestoreDeviceSelectionAsync(selectionToRestore);
        }
    }

    /// <summary>Re-selects the device source that was active before a firmware flash,
    /// once discovery re-adds it (the device may take a while to re-enumerate; give
    /// up after ~30 s). Backs off if the user picks a different device meanwhile.</summary>
    private static async Task RestoreDeviceSelectionAsync(string sourceId)
    {
        var mgr = DeviceManager.Shared;
        string? fallback = mgr.SelectedId;
        for (int i = 0; i < 60; i++)
        {
            string? current = mgr.SelectedId;
            if (current == sourceId) return;
            if (current != fallback)
            {
                // A null→id flip is the manager auto-picking the first survivor as
                // entries churn back in; keep going. An id→id flip is the user
                // choosing a different device — don't override them.
                if (fallback != null) return;
                fallback = current;
            }
            if (mgr.Devices.Any(d => d.Id == sourceId))
            {
                mgr.SelectedId = sourceId;
                return;
            }
            await Task.Delay(500);
        }
    }

    // ======================== Device dispatch helpers ========================

    private bool IsDeviceCommandCapable =>
        _device is WireViewPro2Device ||
        _device is HwmonDevice { DaemonAvailable: true } ||
        _device is NetworkDevice;

    // Reports the command outcome. A direct-serial device acts synchronously
    // (Success unless it throws); the hwmon device relays wireviewd's answer
    // (which may be a group-permission denial); a remote NetworkDevice returns the
    // signed-POST result, which distinguishes no-local-secret, a remote rejection
    // (401/403), and unreachable.
    private async Task<CommandResult> DeviceScreenCmdAsync(WireViewPro2Device.SCREEN_CMD cmd)
    {
        if (_device is WireViewPro2Device pro2) { pro2.ScreenCmd(cmd); return CommandResult.Success; }
        if (_device is HwmonDevice { DaemonAvailable: true } hwmon) return CommandResult.FromDaemon(hwmon.ScreenCmd(cmd));
        if (_device is NetworkDevice nd) return await nd.SendCommandAsync(WireViewCommand.Screen(nd.UniqueId, (int)cmd));
        return new CommandResult(CommandOutcome.HttpError);
    }

    private async Task<CommandResult> DeviceNvmCmdAsync(WireViewPro2Device.NVM_CMD cmd)
    {
        if (_device is WireViewPro2Device pro2) { pro2.NvmCmd(cmd); return CommandResult.Success; }
        if (_device is HwmonDevice { DaemonAvailable: true } hwmon) return CommandResult.FromDaemon(hwmon.NvmCmd(cmd));
        if (_device is NetworkDevice nd) return await nd.SendCommandAsync(WireViewCommand.Nvm(nd.UniqueId, (int)cmd));
        return new CommandResult(CommandOutcome.HttpError);
    }

    /// <summary>The device config as stored, with its layout version (0 = V1,
    /// 1 = V2, 2 = V3), or null when it cannot be read.</summary>
    private (int Version, byte[] Data)? DeviceReadConfigRaw()
    {
        if (_device is WireViewPro2Device pro2)
            return pro2.ReadConfigRaw() is { } b ? (pro2.ConfigVersion, b) : null;
        if (_device is HwmonDevice { DaemonAvailable: true } hwmon) return hwmon.ReadConfigRaw();
        if (_device is NetworkDevice nd)
            return nd.ReadConfigRaw() is { } r ? (r.version, r.data) : null;
        return null;
    }

    private async Task<CommandResult> DeviceWriteConfigRawAsync(int version, byte[] bytes)
    {
        if (_device is WireViewPro2Device pro2) { pro2.WriteConfigRaw(bytes); return CommandResult.Success; }
        if (_device is HwmonDevice { DaemonAvailable: true } hwmon) return CommandResult.FromDaemon(hwmon.WriteConfigRaw(version, bytes));
        if (_device is NetworkDevice nd)
            return await nd.SendCommandAsync(WireViewCommand.WriteConfig(nd.UniqueId, version, bytes));
        return new CommandResult(CommandOutcome.HttpError);
    }

    /// <summary>Writes the fields edited since the last load (or apply) onto the
    /// device's current raw config and records the result as the new baseline.
    /// Refuses when the config cannot be read: writing editor values over an
    /// unread config would replace everything the app does not model.</summary>
    private async Task<CommandResult> WriteEditedConfigAsync()
    {
        if (_configBaseline is not { } baseline)
            throw new InvalidOperationException("The device config has not been loaded. Press Reload first.");
        var raw = DeviceReadConfigRaw()
            ?? throw new InvalidOperationException("Could not read the device config to apply the changes to.");
        var current = CaptureEditorValues();
        var edited = ApplyEditorChanges(WireViewPro2Device.DeserializeConfig(raw.Version, raw.Data),
            baseline, current, IsAveragingSupported, IsUiV2Supported);
        byte[] bytes = WireViewPro2Device.MergeConfigChanges(raw.Version, raw.Data, edited);
        var result = await DeviceWriteConfigRawAsync(raw.Version, bytes);
        if (result.Ok) _configBaseline = current;
        return result;
    }

    /// <summary>Status line for a failed command. A wireviewd permission denial,
    /// local or on the host relaying a remote device, gets the how-to-fix message
    /// instead of a terse reason.</summary>
    private static string CommandFailureText(string action, CommandResult r) => r.FailureText(action);

    private string? DeviceReadBuildString()
    {
        if (_device is WireViewPro2Device pro2) return pro2.ReadBuildString();
        if (_device is HwmonDevice { DaemonAvailable: true } hwmon) return hwmon.ReadBuildString();
        // Remote devices carry the build string in the published snapshot.
        if (_device is NetworkDevice nd) return string.IsNullOrWhiteSpace(nd.BuildString) ? null : nd.BuildString;
        return null;
    }

    // ======================== Connection event ========================

    private void OnConnectionChanged(object? sender, bool connected)
    {
        IsConnected = connected;
        OnPropertyChanged(nameof(IsConnected));
        _device = (sender as DeviceAutoConnector)?.Device;
        OnPropertyChanged(nameof(CanStartFirmwareUpdate));

        if (!connected)
        {
            DeviceName = "Not Connected";
            DeviceEdition = WireViewEdition.Unknown;
            FirmwareVersion = string.Empty;
            UniqueId = string.Empty;
            DeviceBuildString = null;
            _lastKnownDeviceFirmwareVersionNumber = null;
            _lastKnownDeviceFirmwareBuildTime = null;
            _deviceVendorId = 0;
            _deviceProductId = 0;
            UpdateBundledFirmwareComparison();
            ConfigLoaded = false;
            IsAveragingSupported = false;
            IsUiV2Supported = false;
            _isApplyingThemePreset = true;
            SelectedUiThemePreset = UiThemePreset.Custom;
            _isApplyingThemePreset = false;
        }
        else if (_device != null)
        {
            DeviceName = _device is NetworkDevice nd
                ? $"{_device.DeviceName}  ·  remote @ {nd.Endpoint.Replace("http://", string.Empty)}"
                : _device.DeviceName;
            DeviceEdition = _device.Edition;
            FirmwareVersion = string.IsNullOrEmpty(_device.FirmwareVersion)
                ? "N/A" : "v" + _device.FirmwareVersion.ToString().PadLeft(2, '0');
            UniqueId = string.IsNullOrEmpty(_device.UniqueId) ? "N/A" : _device.UniqueId;
            DeviceBuildString = null;

            _lastKnownDeviceFirmwareVersionNumber =
                int.TryParse(_device.FirmwareVersion, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int deviceFw) ? deviceFw : null;
            // Every device type has its build string at connect (serial caches it,
            // wireviewd reports it, LAN publishes it), so the date comparison can run
            // now; ReadBuildStringAsync refreshes it.
            _lastKnownDeviceFirmwareBuildTime = FirmwareHexInfo.TryParseBuildTimestamp(_device.BuildString);
            _deviceVendorId = _device.VendorId;
            _deviceProductId = _device.ProductId;
            UpdateBundledFirmwareComparison();

            if (_awaitingPostFlashReconnect)
            {
                _awaitingPostFlashReconnect = false;
                FirmwareUpdateStatus = FirmwareVersion != "N/A"
                    ? $"Firmware update complete. Device reconnected ({FirmwareVersion})."
                    : "Firmware update complete. Device reconnected.";
            }

            if (AppSettings.Current.ScreenAfterConnection != AppSettings.StartupScreen.NoChange
                && IsDeviceCommandCapable)
            {
                var target = AppSettings.Current.ScreenAfterConnection switch
                {
                    AppSettings.StartupScreen.Simple      => WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_SIMPLE,
                    AppSettings.StartupScreen.Current     => WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_CURRENT,
                    AppSettings.StartupScreen.Temperature => WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_TEMP,
                    AppSettings.StartupScreen.Status      => WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_STATUS,
                    _                                     => WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_MAIN,
                };
                _lastCommandedScreen = target;
                _ = DeviceScreenCmdAsync(target);
            }

            if (IsDeviceCommandCapable)
            {
                TryReloadConfig();
                _ = ReadBuildStringAsync();
            }
        }
    }

    // ======================== Screen navigation ========================

    private async void GoToSelectedScreen()
    {
        try
        {
            if (_device == null || !_device.Connected) { ConfigStatus = "Not connected."; return; }
            if (!IsDeviceCommandCapable) { ConfigStatus = "Unsupported device."; return; }
            if (SelectedDeviceScreenTarget == AppSettings.StartupScreen.NoChange) { ConfigStatus = "Select a screen."; return; }

            var screenTarget = SelectedDeviceScreenTarget switch
            {
                AppSettings.StartupScreen.Main        => WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_MAIN,
                AppSettings.StartupScreen.Simple      => WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_SIMPLE,
                AppSettings.StartupScreen.Current     => WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_CURRENT,
                AppSettings.StartupScreen.Temperature => WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_TEMP,
                AppSettings.StartupScreen.Status      => WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_STATUS,
                _                                     => WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_MAIN,
            };
            _lastCommandedScreen = screenTarget;
            var r = await DeviceScreenCmdAsync(screenTarget);
            ConfigStatus = r.Ok
                ? $"Switched to {SelectedDeviceScreenTarget}."
                : CommandFailureText("Screen change", r);
        }
        catch (Exception ex)
        {
            ConfigStatus = "Screen change failed: " + ex.Message;
        }
    }

    private async Task ReadBuildStringAsync()
    {
        var device = _device;
        string? text = await Task.Run(() => DeviceReadBuildString()).ConfigureAwait(false);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!ReferenceEquals(device, _device)) return; // device switched meanwhile
            DeviceBuildString = string.IsNullOrWhiteSpace(text) ? null : "(" + text.Trim() + ")";
            var built = FirmwareHexInfo.TryParseBuildTimestamp(text);
            if (built.HasValue)
            {
                _lastKnownDeviceFirmwareBuildTime = built;
                UpdateBundledFirmwareComparison();
            }
        });
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

            var raw = DeviceReadConfigRaw();
            if (raw is not { } r)
            {
                ConfigLoaded = false;
                _configBaseline = null;
                ConfigStatus = "Failed to read config.";
                return;
            }
            var cfg = WireViewPro2Device.DeserializeConfig(r.Version, r.Data);

            int configVersion = r.Version;
            IsAveragingSupported = configVersion >= 1;
            IsUiV2Supported = configVersion >= 2;
            UpdateAveragingOptions(cfg.Average);
            OnPropertyChanged(nameof(IsLegacyThemeSelectionVisible));
            OnPropertyChanged(nameof(IsThemePresetSelectionVisible));
            ApplyToEditor(cfg);
            _configBaseline = CaptureEditorValues();
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
    private async Task ApplyConfig()
    {
        try
        {
            if (_device == null || !_device.Connected) { ConfigStatus = "Not connected."; return; }
            if (!IsDeviceCommandCapable) { ConfigStatus = "Unsupported device."; return; }
            // A staged custom background (and its tinted fan frames) uploads first;
            // throws with a descriptive message if the device can't take it.
            await UploadPendingThemeAssetsAsync();
            var w = await WriteEditedConfigAsync();
            if (!w.Ok) { ConfigStatus = CommandFailureText("Apply", w); return; }
            await DeviceScreenCmdAsync(WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_SAME);
            ConfigStatus = "Config applied.";
            RequestThemePreviewRefresh();
        }
        catch (Exception ex)
        {
            ConfigStatus = "Apply failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task StoreConfig()
    {
        try
        {
            if (_device == null || !_device.Connected) { ConfigStatus = "Not connected."; return; }
            if (!IsDeviceCommandCapable) { ConfigStatus = "Unsupported device."; return; }

            var r = await DeviceNvmCmdAsync(WireViewPro2Device.NVM_CMD.NVM_CMD_STORE);
            ConfigStatus = r.Ok
                ? "Config stored (NVM)."
                : CommandFailureText("Store", r);
        }
        catch (Exception ex)
        {
            ConfigStatus = "Store failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task ResetConfig()
    {
        try
        {
            if (_device == null || !_device.Connected) { ConfigStatus = "Not connected."; return; }
            if (!IsDeviceCommandCapable) { ConfigStatus = "Unsupported device."; return; }

            var r = await DeviceNvmCmdAsync(WireViewPro2Device.NVM_CMD.NVM_CMD_RESET);
            if (!r.Ok)
            {
                ConfigStatus = CommandFailureText("Reset", r);
                return;
            }
            if (_device is not NetworkDevice)
            {
                await Task.Delay(75);
                TryReloadConfig();
            }
            ConfigStatus = "Config reset.";
        }
        catch (Exception ex)
        {
            ConfigStatus = "Reset failed: " + ex.Message;
        }
    }

    // ======================== Averaging options ========================

    /// <summary>Upstream's condition, verbatim: <c>(_lastKnownDeviceFirmwareVersionNumber
    /// &gt;= 5) ? AllAveragingOptions : LegacyAveragingOptions</c>.</summary>
    private WireViewPro2Device.AVG[] BaseAveragingOptions =>
        _lastKnownDeviceFirmwareVersionNumber >= ExtendedAveragingMinProFirmwareVersion
            ? AllAveragingOptions
            : LegacyAveragingOptions;

    private void UpdateAveragingOptions(WireViewPro2Device.AVG deviceValue)
    {
        var options = BaseAveragingOptions;
        if (!options.Contains(deviceValue))
            options = options.Append(deviceValue).ToArray();
        // Replacing the list resets the ComboBox selection; only do it on a change.
        if (!options.SequenceEqual(AveragingOptions))
            AveragingOptions = options;
    }

    /// <summary>A profile saved on a v05 device may carry 2834/5668 ms; older
    /// firmware only knows up to 1417 ms, so clamp to the longest window offered.</summary>
    private WireViewPro2Device.AVG ClampAveragingForDevice(WireViewPro2Device.AVG value)
    {
        var options = BaseAveragingOptions;
        if (options.Contains(value)) return value;
        var longest = options.Max();
        return (int)value > (int)longest ? longest : value;
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
        if (IsAveragingSupported)
        {
            Averaging = cfg.Average;
            // Re-announce even when unchanged: a new AveragingOptions list may have
            // cleared the ComboBox selection.
            OnPropertyChanged(nameof(Averaging));
        }
        if (IsUiV2Supported)
        {
            _isApplyingThemePreset = true;
            SelectedUiThemePreset = DetectThemePresetFromCurrentUi();
            _isApplyingThemePreset = false;
        }
    }

    /// <summary>The editor's values, compared field by field against the baseline
    /// taken when the config was loaded so that only edited fields are written.</summary>
    internal readonly record struct ConfigEditorValues(
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

    /// <summary>Editor values as last loaded from, or applied to, the device; null
    /// until a config has been read.</summary>
    private ConfigEditorValues? _configBaseline;

    private ConfigEditorValues CaptureEditorValues() => new(
        FriendlyName, BacklightDuty, FanMode, FanTempSource, FanDutyMin, FanDutyMax, FanTempMinC, FanTempMaxC,
        UiDefaultScreen, UiCurrentScale, UiPowerScale, UiTheme, UiRotation, UiTimeoutMode,
        UiCycleTimeSeconds, UiTimeoutSeconds,
        ColorToArgb(UiPrimaryColor), ColorToArgb(UiSecondaryColor), ColorToArgb(UiHighlightColor), ColorToArgb(UiBackgroundColor),
        UiBackgroundBitmap, UiDisplayInversionEnabled,
        FaultDisplayEnableMask, FaultBuzzerEnableMask, FaultSoftPowerEnableMask, FaultHardPowerEnableMask,
        TsFaultThresholdC, OcpFaultThresholdA, WireOcpFaultThresholdA, OppFaultThresholdW,
        CurrentImbalanceFaultThresholdPercent, CurrentImbalanceFaultMinLoadA, ShutdownWaitTimeSeconds,
        LoggingIntervalSeconds, Averaging);

    /// <summary>Puts the fields that differ between <paramref name="was"/> and
    /// <paramref name="now"/> into <paramref name="cfg"/> (the device's current
    /// config) and leaves every other field as the device has it. The editor maps
    /// values it cannot show (an unknown background id reads as Disabled, a clamped
    /// backlight, an unknown enum), so writing unchanged fields back would replace
    /// what the device holds, e.g. a Noctua Edition theme value.</summary>
    internal static WireViewPro2Device.DeviceConfigStructV3 ApplyEditorChanges(
        WireViewPro2Device.DeviceConfigStructV3 cfg, ConfigEditorValues was, ConfigEditorValues now,
        bool averagingSupported, bool uiV2Supported)
    {
        if (now.FriendlyName != was.FriendlyName) cfg.FriendlyName = EncodeDeviceString(now.FriendlyName, 32);
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
            cfg.Ui.FanBitmapId = (byte)MapFanBitmapFromBackground(now.UiBackgroundBitmap);
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
        Averaging = ClampAveragingForDevice((WireViewPro2Device.AVG)p.Averaging);
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
