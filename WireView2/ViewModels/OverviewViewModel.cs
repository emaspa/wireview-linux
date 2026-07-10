using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media;
using WireView2.Controls;
using WireView2.Device;

namespace WireView2.ViewModels;

public partial class OverviewViewModel : ViewModelBase, IDisposable
{
    private readonly DeviceAutoConnector _connector;
    private readonly bool _ownsConnector;

    // --------------- Totals ---------------

    private double _totalCurrentA;
    private double _totalPowerW;
    private double _avgVoltageV;
    private string _powerCableRatingText = "N/A";

    // --------------- Temperatures ---------------

    private double _onboardTempInC;
    private double _onboardTempOutC;
    private double _externalTemp1C;
    private double _externalTemp2C;

    // --------------- Per-wire bar chart (rendered by SimpleBarChart) ---------------

    // In-place element updates raise CollectionChanged(Replace), which is what
    // SimpleBarChart listens for; reassigning Values every poll would leak
    // subscriptions instead.
    private readonly SimpleBarSeries _seriesVoltage;
    private readonly SimpleBarSeries _seriesCurrent;
    private readonly SimpleBarSeries _seriesPower;

    private bool _showCurrent = true;
    private bool _showVoltage;
    private bool _showPower;

    private DeviceData? _pendingDeviceData;
    private readonly object _pendingGate = new();
    private bool _isViewVisible = true;
    private bool _disposed;

    // --------------- Constants ---------------

    public double PowerMaxW => 600.0;
    public double CurrentMaxA => 50.0;
    public double VoltageMaxV => 13.0;
    public double TempMaxC => 100.0;
    private const double PerWireVoltageMaxV = 15.0;
    private const double PerWireCurrentMaxA = 10.0;
    private const double PerWirePowerMaxW = 150.0;

    private int _wiresCount = 6;

    // --------------- Properties ---------------

    public ConnectionStatusViewModel ConnectionStatus { get; }

    public double TotalCurrentA
    {
        get => _totalCurrentA;
        private set => Set(ref _totalCurrentA, value);
    }

    public double TotalPowerW
    {
        get => _totalPowerW;
        private set => Set(ref _totalPowerW, value);
    }

    public double AvgVoltageV
    {
        get => _avgVoltageV;
        private set => Set(ref _avgVoltageV, value);
    }

    public string PowerCableRatingText
    {
        get => _powerCableRatingText;
        private set => Set(ref _powerCableRatingText, value);
    }

    public double OnboardTempInC
    {
        get => _onboardTempInC;
        private set { if (Set(ref _onboardTempInC, value)) { OnPropertyChanged(nameof(TempInText)); OnPropertyChanged(nameof(TempInAvailable)); } }
    }

    public double OnboardTempOutC
    {
        get => _onboardTempOutC;
        private set { if (Set(ref _onboardTempOutC, value)) { OnPropertyChanged(nameof(TempOutText)); OnPropertyChanged(nameof(TempOutAvailable)); } }
    }

    public double ExternalTemp1C
    {
        get => _externalTemp1C;
        private set { if (Set(ref _externalTemp1C, value)) { OnPropertyChanged(nameof(TempExt1Text)); OnPropertyChanged(nameof(TempExt1Available)); } }
    }

    public double ExternalTemp2C
    {
        get => _externalTemp2C;
        private set { if (Set(ref _externalTemp2C, value)) { OnPropertyChanged(nameof(TempExt2Text)); OnPropertyChanged(nameof(TempExt2Available)); } }
    }

    // Temperature display helpers — sensors read ~-3276.8°C when disconnected
    private static bool IsTempValid(double t) => t > -100.0 && t < 200.0;

    public bool TempInAvailable => IsTempValid(OnboardTempInC);
    public bool TempOutAvailable => IsTempValid(OnboardTempOutC);
    public bool TempExt1Available => IsTempValid(ExternalTemp1C);
    public bool TempExt2Available => IsTempValid(ExternalTemp2C);

    public string TempInText => IsTempValid(OnboardTempInC) ? $"{OnboardTempInC:0.#} °C" : "N/A";
    public string TempOutText => IsTempValid(OnboardTempOutC) ? $"{OnboardTempOutC:0.#} °C" : "N/A";
    public string TempExt1Text => IsTempValid(ExternalTemp1C) ? $"{ExternalTemp1C:0.#} °C" : "N/A";
    public string TempExt2Text => IsTempValid(ExternalTemp2C) ? $"{ExternalTemp2C:0.#} °C" : "N/A";

    // Gauge-clamped values (SimpleGaugeChart clamps too, but invalid sensor
    // readings like -3276.8 should render as zero, not full arc)
    public double TempInGauge => IsTempValid(OnboardTempInC) ? Math.Clamp(OnboardTempInC, 0, TempMaxC) : 0;
    public double TempOutGauge => IsTempValid(OnboardTempOutC) ? Math.Clamp(OnboardTempOutC, 0, TempMaxC) : 0;
    public double TempExt1Gauge => IsTempValid(ExternalTemp1C) ? Math.Clamp(ExternalTemp1C, 0, TempMaxC) : 0;
    public double TempExt2Gauge => IsTempValid(ExternalTemp2C) ? Math.Clamp(ExternalTemp2C, 0, TempMaxC) : 0;

    /// <summary>One row of the fault table: live status + latched log bit for a
    /// fault, with its Clear command.</summary>
    public sealed class FaultItem : ViewModelBase
    {
        private bool _statusFault;
        private bool _logFault;

        public string Label { get; }
        public WireViewPro2Device.FAULT Fault { get; }
        public IRelayCommand ClearCommand { get; }

        public bool StatusFault
        {
            get => _statusFault;
            set => Set(ref _statusFault, value);
        }

        public bool LogFault
        {
            get => _logFault;
            set => Set(ref _logFault, value);
        }

        public FaultItem(string label, WireViewPro2Device.FAULT fault, Action<WireViewPro2Device.FAULT> clear)
        {
            Label = label;
            Fault = fault;
            ClearCommand = new RelayCommand(() => clear(fault));
        }
    }

    public ObservableCollection<FaultItem> Faults { get; } = new();

    public SimpleBarSeries[] BarSeries { get; }
    public SimpleAxis[] XAxes { get; }
    public SimpleAxis[] YAxes { get; }

    public bool ShowCurrent
    {
        get => _showCurrent;
        set
        {
            if (Set(ref _showCurrent, value))
            {
                _seriesCurrent.IsVisible = value;
            }
        }
    }

    public bool ShowVoltage
    {
        get => _showVoltage;
        set
        {
            if (Set(ref _showVoltage, value))
            {
                _seriesVoltage.IsVisible = value;
            }
        }
    }

    public bool ShowPower
    {
        get => _showPower;
        set
        {
            if (Set(ref _showPower, value))
            {
                _seriesPower.IsVisible = value;
            }
        }
    }

    // --------------- Constructor ---------------

    public OverviewViewModel(ConnectionStatusViewModel connectionStatus, DeviceAutoConnector? connector = null)
    {
        ConnectionStatus = connectionStatus ?? throw new ArgumentNullException(nameof(connectionStatus));
        _connector = connector ?? DeviceAutoConnector.Shared;
        _ownsConnector = connector != null && connector != DeviceAutoConnector.Shared;

        // Explicit fills keep the toggled series distinct: SimpleBarChart uses
        // the fill as its gradient base and for the per-bar value labels
        // (voltage = orange, current = blue, power = red).
        _seriesVoltage = new SimpleBarSeries
        {
            Name = "Voltage (V)",
            ScalesYAt = 0,
            Fill = Color.FromRgb(255, 152, 0),
            IsVisible = _showVoltage,
        };
        _seriesCurrent = new SimpleBarSeries
        {
            Name = "Current (A)",
            ScalesYAt = 1,
            Fill = Color.FromRgb(33, 150, 243),
            IsVisible = _showCurrent,
        };
        _seriesPower = new SimpleBarSeries
        {
            Name = "Power (W)",
            ScalesYAt = 2,
            Fill = Color.FromRgb(244, 67, 54),
            IsVisible = _showPower,
        };
        BarSeries = new[] { _seriesVoltage, _seriesCurrent, _seriesPower };
        for (int i = 0; i < _wiresCount; i++)
        {
            _seriesVoltage.Values.Add(0.0);
            _seriesCurrent.Values.Add(0.0);
            _seriesPower.Values.Add(0.0);
        }

        XAxes = new[] { new SimpleAxis() };
        YAxes = new[]
        {
            new SimpleAxis { MinLimit = 0.0, MaxLimit = PerWireVoltageMaxV },
            new SimpleAxis { MinLimit = 0.0, MaxLimit = PerWireCurrentMaxA },
            new SimpleAxis { MinLimit = 0.0, MaxLimit = PerWirePowerMaxW },
        };

        Faults.Add(new FaultItem("Chip Over-Temp", WireViewPro2Device.FAULT.FAULT_OTP_TCHIP, ClearFault));
        Faults.Add(new FaultItem("Sensor Over-Temp", WireViewPro2Device.FAULT.FAULT_OTP_TS, ClearFault));
        Faults.Add(new FaultItem("Over-Current", WireViewPro2Device.FAULT.FAULT_OCP, ClearFault));
        Faults.Add(new FaultItem("Wire Over-Current", WireViewPro2Device.FAULT.FAULT_WIRE_OCP, ClearFault));
        Faults.Add(new FaultItem("Over-Power", WireViewPro2Device.FAULT.FAULT_OPP, ClearFault));
        Faults.Add(new FaultItem("Current Imbalance", WireViewPro2Device.FAULT.FAULT_CURRENT_IMBALANCE, ClearFault));

        _connector.DataUpdated += (_, data) => OnDeviceData(data);
        _connector.Start();
        App.MainWindowVisibilityChanged += OnMainWindowVisibilityChanged;
    }

    public void Dispose()
    {
        _disposed = true;
        App.MainWindowVisibilityChanged -= OnMainWindowVisibilityChanged;
        if (_ownsConnector)
        {
            _connector.DataUpdated -= delegate { };
            _connector.Dispose();
        }
    }

    private void OnMainWindowVisibilityChanged(object? sender, bool isVisible)
    {
        if (_disposed) return;
        _isViewVisible = isVisible;
        if (!isVisible) return;

        // Apply the newest sample that arrived while hidden.
        DeviceData? pending;
        lock (_pendingGate)
        {
            pending = _pendingDeviceData;
            _pendingDeviceData = null;
        }
        if (pending != null)
            ApplyDeviceData(pending);
    }

    // --------------- Device data handler ---------------

    private void OnDeviceData(DeviceData d)
    {
        // Don't burn CPU updating gauges/bars nobody can see; stash the latest
        // sample and apply it when the window becomes visible again.
        if (!_isViewVisible)
        {
            lock (_pendingGate) { _pendingDeviceData = d; }
            return;
        }
        ApplyDeviceData(d);
    }

    private void ApplyDeviceData(DeviceData d)
    {
        double[] pinVoltage = d.PinVoltage;
        double[] pinCurrent = d.PinCurrent;
        var powers = new double[pinVoltage.Length];
        for (int i = 0; i < powers.Length; i++)
            powers[i] = pinVoltage[i] * pinCurrent[i];

        // Fixed per-wire ceilings unless a value overflows, then autoscale.
        YAxes[0].MaxLimit = pinVoltage.Any(v => v > PerWireVoltageMaxV) ? null : PerWireVoltageMaxV;
        YAxes[1].MaxLimit = pinCurrent.Any(v => v > PerWireCurrentMaxA) ? null : PerWireCurrentMaxA;
        YAxes[2].MaxLimit = powers.Any(v => v > PerWirePowerMaxW) ? null : PerWirePowerMaxW;

        CopyInto(_seriesVoltage.Values, pinVoltage);
        CopyInto(_seriesCurrent.Values, pinCurrent);
        CopyInto(_seriesPower.Values, powers);

        TotalCurrentA = d.SumCurrentA;
        TotalPowerW   = d.SumPowerW;
        AvgVoltageV   = pinVoltage.Average();

        OnboardTempInC  = d.OnboardTempInC;
        OnboardTempOutC = d.OnboardTempOutC;
        ExternalTemp1C  = d.ExternalTemp1C;
        ExternalTemp2C  = d.ExternalTemp2C;
        OnPropertyChanged(nameof(TempInGauge));
        OnPropertyChanged(nameof(TempOutGauge));
        OnPropertyChanged(nameof(TempExt1Gauge));
        OnPropertyChanged(nameof(TempExt2Gauge));

        PowerCableRatingText = TryResolveCableRatingText(d);

        foreach (var fault in Faults)
        {
            int bit = 1 << (int)fault.Fault;
            fault.StatusFault = (d.FaultStatus & bit) != 0;
            fault.LogFault = (d.FaultLog & bit) != 0;
        }
    }

    private static void CopyInto(ObservableCollection<double> target, double[] source)
    {
        while (target.Count < source.Length)
            target.Add(0.0);
        for (int i = 0; i < source.Length && i < target.Count; i++)
        {
            if (Math.Abs(target[i] - source[i]) > 1e-12)
                target[i] = source[i];
        }
    }

    // --------------- Cable rating resolution ---------------

    private static string TryResolveCableRatingText(DeviceData d)
    {
        Type type = d.GetType();

        // Try text properties first
        foreach (string name in new[] { "CableRatingText", "DetectedCableRatingText", "PowerCableRatingText" })
        {
            PropertyInfo? prop = type.GetProperty(name);
            if (prop != null && prop.PropertyType == typeof(string))
            {
                string? text = (string?)prop.GetValue(d);
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }

        // Try watt-value properties
        foreach (string name in new[] { "CableRatingW", "DetectedCableRatingW", "PowerCableRatingW", "PsuCapabilityW" })
        {
            PropertyInfo? prop = type.GetProperty(name);
            if (prop != null && typeof(double).IsAssignableFrom(prop.PropertyType))
            {
                double val = Convert.ToDouble(prop.GetValue(d) ?? 0);
                if (val > 0.0)
                    return $"{val:0} W";
            }
            if (prop != null && typeof(int).IsAssignableFrom(prop.PropertyType))
            {
                int val = Convert.ToInt32(prop.GetValue(d) ?? 0);
                if (val > 0)
                    return $"{val:0} W";
            }
        }

        // Try amp-value properties
        foreach (string name in new[] { "CableRatingA", "DetectedCableRatingA", "PowerCableRatingA" })
        {
            PropertyInfo? prop = type.GetProperty(name);
            if (prop != null && typeof(double).IsAssignableFrom(prop.PropertyType))
            {
                double val = Convert.ToDouble(prop.GetValue(d) ?? 0);
                if (val > 0.0)
                    return $"{val:0.##} A";
            }
        }

        return "N/A";
    }

    // --------------- Fault clearing commands ---------------

    [RelayCommand]
    private void ClearFaultOtpTchip() => ClearFault(WireViewPro2Device.FAULT.FAULT_OTP_TCHIP);

    [RelayCommand]
    private void ClearFaultOtpTs() => ClearFault(WireViewPro2Device.FAULT.FAULT_OTP_TS);

    [RelayCommand]
    private void ClearFaultOcp() => ClearFault(WireViewPro2Device.FAULT.FAULT_OCP);

    [RelayCommand]
    private void ClearFaultWireOcp() => ClearFault(WireViewPro2Device.FAULT.FAULT_WIRE_OCP);

    [RelayCommand]
    private void ClearFaultOpp() => ClearFault(WireViewPro2Device.FAULT.FAULT_OPP);

    [RelayCommand]
    private void ClearFaultCurrentImbalance() => ClearFault(WireViewPro2Device.FAULT.FAULT_CURRENT_IMBALANCE);

    private void ClearFault(WireViewPro2Device.FAULT fault)
    {
        ushort mask = (ushort)(~(1 << (int)fault));
        if (_connector.Device is WireViewPro2Device device)
        {
            device.ClearFaults(mask, mask);
            device.ScreenCmd(WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_SAME);
        }
        else if (_connector.Device is HwmonDevice { DaemonAvailable: true } hwmon)
        {
            hwmon.ClearFaults(mask, mask);
            hwmon.ScreenCmd(WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_SAME);
        }
    }
}
