using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
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
    private const double TempMaxC = 100.0;

    // --------------- Bar chart series ---------------

    private readonly ColumnSeries<double> _seriesCurrent = new()
    {
        Name = "Current (A)",
        ScalesYAt = 1
    };
    private readonly ColumnSeries<double> _seriesVoltage = new()
    {
        Name = "Voltage (V)",
        ScalesYAt = 0
    };
    private readonly ColumnSeries<double> _seriesPower = new()
    {
        Name = "Power (W)",
        ScalesYAt = 2
    };

    private double[] _lastVoltages = Array.Empty<double>();
    private double[] _lastCurrents = Array.Empty<double>();
    private double[] _lastPowers   = Array.Empty<double>();

    private bool _showCurrent = true;
    private bool _showVoltage;
    private bool _showPower;

    // --------------- Pie/gauge series ---------------

    private readonly PieSeries<double> _tempInValue  = MakeGaugeSlice("Onboard In",  new SKColor(33, 150, 243), 25);
    private readonly PieSeries<double> _tempOutValue = MakeGaugeSlice("Onboard Out", new SKColor(76, 175, 80), 25);
    private readonly PieSeries<double> _tempE1Value  = MakeGaugeSlice("External 1",  new SKColor(255, 152, 0), 25);
    private readonly PieSeries<double> _tempE2Value  = MakeGaugeSlice("External 2",  new SKColor(244, 67, 54), 25);

    private readonly PieSeries<double> _tempInRest  = MakeRemainderSlice(25);
    private readonly PieSeries<double> _tempOutRest = MakeRemainderSlice(25);
    private readonly PieSeries<double> _tempE1Rest  = MakeRemainderSlice(25);
    private readonly PieSeries<double> _tempE2Rest  = MakeRemainderSlice(25);

    private readonly PieSeries<double> _totalPowerValue   = MakeGaugeSlice("Total Power",   new SKColor(103, 58, 183), 35);
    private readonly PieSeries<double> _totalCurrentValue  = MakeGaugeSlice("Total Current",  new SKColor(0, 188, 212), 35);
    private readonly PieSeries<double> _avgVoltageValue    = MakeGaugeSlice("Avg Voltage",    new SKColor(255, 193, 7), 35);

    private readonly PieSeries<double> _totalPowerRest   = MakeRemainderSlice(35);
    private readonly PieSeries<double> _totalCurrentRest  = MakeRemainderSlice(35);
    private readonly PieSeries<double> _avgVoltageRest    = MakeRemainderSlice(35);

    // --------------- Constants ---------------

    private const double PowerMaxW        = 600.0;
    private const double CurrentMaxA      = 50.0;
    private const double VoltageMaxV      = 13.0;
    private const double PerWireVoltageMaxV = 15.0;
    private const double PerWireCurrentMaxA = 10.0;
    private const double PerWirePowerMaxW   = 150.0;

    private int _wiresCount = 6;

    // --------------- Properties ---------------

    public ConnectionStatusViewModel ConnectionStatus { get; }

    public double TotalCurrentA
    {
        get => _totalCurrentA;
        private set { if (Set(ref _totalCurrentA, value)) UpdateTotalsSeries(); }
    }

    public double TotalPowerW
    {
        get => _totalPowerW;
        private set { if (Set(ref _totalPowerW, value)) UpdateTotalsSeries(); }
    }

    public double AvgVoltageV
    {
        get => _avgVoltageV;
        private set { if (Set(ref _avgVoltageV, value)) UpdateTotalsSeries(); }
    }

    public string PowerCableRatingText
    {
        get => _powerCableRatingText;
        private set => Set(ref _powerCableRatingText, value);
    }

    public double OnboardTempInC
    {
        get => _onboardTempInC;
        private set { if (Set(ref _onboardTempInC, value)) { UpdateTempSeries(); OnPropertyChanged(nameof(TempInText)); OnPropertyChanged(nameof(TempInAvailable)); } }
    }

    public double OnboardTempOutC
    {
        get => _onboardTempOutC;
        private set { if (Set(ref _onboardTempOutC, value)) { UpdateTempSeries(); OnPropertyChanged(nameof(TempOutText)); OnPropertyChanged(nameof(TempOutAvailable)); } }
    }

    public double ExternalTemp1C
    {
        get => _externalTemp1C;
        private set { if (Set(ref _externalTemp1C, value)) { UpdateTempSeries(); OnPropertyChanged(nameof(TempExt1Text)); OnPropertyChanged(nameof(TempExt1Available)); } }
    }

    public double ExternalTemp2C
    {
        get => _externalTemp2C;
        private set { if (Set(ref _externalTemp2C, value)) { UpdateTempSeries(); OnPropertyChanged(nameof(TempExt2Text)); OnPropertyChanged(nameof(TempExt2Available)); } }
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

    public ObservableCollection<ISeries> BarSeries { get; } = new();
    public Axis[] XAxes { get; }
    public Axis[] YAxes { get; }

    public bool ShowCurrent
    {
        get => _showCurrent;
        set
        {
            if (Set(ref _showCurrent, value))
            {
                _seriesCurrent.IsVisible = value;
                UpdateAxisVisibility();
                RefreshXLabels();
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
                UpdateAxisVisibility();
                RefreshXLabels();
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
                UpdateAxisVisibility();
                RefreshXLabels();
            }
        }
    }

    public ObservableCollection<ISeries> TempInSeries   { get; } = new();
    public ObservableCollection<ISeries> TempOutSeries  { get; } = new();
    public ObservableCollection<ISeries> TempExt1Series { get; } = new();
    public ObservableCollection<ISeries> TempExt2Series { get; } = new();

    public ObservableCollection<ISeries> TotalPowerSeries   { get; } = new();
    public ObservableCollection<ISeries> TotalCurrentSeries  { get; } = new();
    public ObservableCollection<ISeries> AvgVoltageSeries    { get; } = new();

    // --------------- Constructor ---------------

    public OverviewViewModel(ConnectionStatusViewModel connectionStatus, DeviceAutoConnector? connector = null)
    {
        ConnectionStatus = connectionStatus ?? throw new ArgumentNullException(nameof(connectionStatus));
        _connector = connector ?? DeviceAutoConnector.Shared;
        _ownsConnector = connector != null && connector != DeviceAutoConnector.Shared;

        XAxes = new Axis[]
        {
            new Axis
            {
                Labels = Enumerable.Repeat(string.Empty, _wiresCount).ToArray(),
                TicksPaint = null,
                SeparatorsPaint = null,
                LabelsPaint = new SolidColorPaint(SKColors.White) { SKTypeface = SKTypeface.FromFamilyName(null, SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright) },
                TextSize = 14
            }
        };

        YAxes = new Axis[]
        {
            new Axis
            {
                Name = "V",
                Labeler = v => $"{v:0.##} V",
                Position = AxisPosition.Start,
                MinLimit = 0.0,
                MaxLimit = PerWireVoltageMaxV,
                SeparatorsPaint = null,
                NamePaint = null
            },
            new Axis
            {
                Name = "A",
                Labeler = v => $"{v:0.##} A",
                Position = AxisPosition.End,
                MinLimit = 0.0,
                MaxLimit = PerWireCurrentMaxA,
                SeparatorsPaint = null,
                NamePaint = null
            },
            new Axis
            {
                Name = "W",
                Labeler = v => $"{v:0.##} W",
                Position = AxisPosition.End,
                MinLimit = 0.0,
                MaxLimit = PerWirePowerMaxW,
                SeparatorsPaint = null,
                NamePaint = null
            }
        };

        _seriesCurrent.IsVisible = ShowCurrent;
        _seriesVoltage.IsVisible = ShowVoltage;
        _seriesPower.IsVisible   = ShowPower;

        BarSeries.Add(_seriesVoltage);
        BarSeries.Add(_seriesCurrent);
        BarSeries.Add(_seriesPower);
        UpdateAxisVisibility();

        TempInSeries.Add(_tempInValue);
        TempInSeries.Add(_tempInRest);
        TempOutSeries.Add(_tempOutValue);
        TempOutSeries.Add(_tempOutRest);
        TempExt1Series.Add(_tempE1Value);
        TempExt1Series.Add(_tempE1Rest);
        TempExt2Series.Add(_tempE2Value);
        TempExt2Series.Add(_tempE2Rest);
        UpdateTempSeries();

        TotalPowerSeries.Add(_totalPowerValue);
        TotalPowerSeries.Add(_totalPowerRest);
        TotalCurrentSeries.Add(_totalCurrentValue);
        TotalCurrentSeries.Add(_totalCurrentRest);
        AvgVoltageSeries.Add(_avgVoltageValue);
        AvgVoltageSeries.Add(_avgVoltageRest);
        UpdateTotalsSeries();

        _connector.DataUpdated += (_, data) => OnDeviceData(data);
        _connector.Start();
    }

    public void Dispose()
    {
        if (_ownsConnector)
        {
            _connector.DataUpdated -= delegate { };
            _connector.Dispose();
        }
    }

    // --------------- Pie helpers ---------------

    private static PieSeries<double> MakeGaugeSlice(string name, SKColor color, double innerRadius)
    {
        return new PieSeries<double>
        {
            Name = name,
            Stroke = null,
            Fill = new SolidColorPaint(color),
            DataLabelsPaint = null,
            InnerRadius = innerRadius,
            IsHoverable = false
        };
    }

    private static PieSeries<double> MakeRemainderSlice(double innerRadius)
    {
        return new PieSeries<double>
        {
            Name = "",
            Stroke = null,
            Fill = new SolidColorPaint(new SKColor(220, 220, 220)),
            DataLabelsPaint = null,
            InnerRadius = innerRadius,
            IsHoverable = false
        };
    }

    // --------------- Axis / label helpers ---------------

    private void UpdateAxisVisibility()
    {
        if (YAxes.Length >= 3)
        {
            YAxes[0].IsVisible = ShowVoltage;
            YAxes[1].IsVisible = ShowCurrent;
            YAxes[2].IsVisible = ShowPower;
        }
    }

    private void RefreshXLabels()
    {
        if (XAxes.Length == 0) return;

        double[] active = ShowCurrent ? _lastCurrents
                        : ShowVoltage ? _lastVoltages
                        : ShowPower   ? _lastPowers
                        : Array.Empty<double>();

        int count = Math.Max(_wiresCount, active.Length);
        if (count <= 0)
        {
            XAxes[0].Labels = Array.Empty<string>();
            return;
        }

        var labels = new string[count];
        for (int i = 0; i < count; i++)
        {
            double val = i < active.Length ? active[i] : double.NaN;
            labels[i] = double.IsFinite(val) ? $"{val:0.##}" : string.Empty;
        }
        XAxes[0].Labels = labels;
    }

    // --------------- Gauge updates ---------------

    private void UpdateTempSeries()
    {
        static double SafeClamp(double v) => IsTempValid(v) ? Math.Clamp(v, 0.0, TempMaxC) : 0.0;

        double tIn  = SafeClamp(OnboardTempInC);
        double tOut = SafeClamp(OnboardTempOutC);
        double tE1  = SafeClamp(ExternalTemp1C);
        double tE2  = SafeClamp(ExternalTemp2C);

        _tempInValue.Values  = new[] { tIn };
        _tempOutValue.Values = new[] { tOut };
        _tempE1Value.Values  = new[] { tE1 };
        _tempE2Value.Values  = new[] { tE2 };

        _tempInRest.Values  = new[] { Math.Max(0.0, TempMaxC - tIn) };
        _tempOutRest.Values = new[] { Math.Max(0.0, TempMaxC - tOut) };
        _tempE1Rest.Values  = new[] { Math.Max(0.0, TempMaxC - tE1) };
        _tempE2Rest.Values  = new[] { Math.Max(0.0, TempMaxC - tE2) };
    }

    private void UpdateTotalsSeries()
    {
        double p = Math.Clamp(TotalPowerW, 0.0, PowerMaxW);
        double i = Math.Clamp(TotalCurrentA, 0.0, CurrentMaxA);
        double v = Math.Clamp(AvgVoltageV, 0.0, VoltageMaxV);

        _totalPowerValue.Values   = new[] { p };
        _totalPowerRest.Values    = new[] { Math.Max(0.0, PowerMaxW - p) };
        _totalCurrentValue.Values = new[] { i };
        _totalCurrentRest.Values  = new[] { Math.Max(0.0, CurrentMaxA - i) };
        _avgVoltageValue.Values   = new[] { v };
        _avgVoltageRest.Values    = new[] { Math.Max(0.0, VoltageMaxV - v) };
    }

    // --------------- Device data handler ---------------

    private void OnDeviceData(DeviceData d)
    {
        double[] pinVoltage = d.PinVoltage;
        double[] pinCurrent = d.PinCurrent;
        bool[] overflows = new bool[YAxes.Length];
        double[] powers = new double[pinVoltage.Length];

        for (int i = 0; i < powers.Length; i++)
        {
            double vv = pinVoltage[i];
            double ii = pinCurrent[i];
            powers[i] = vv * ii;

            if (vv > PerWireVoltageMaxV) { YAxes[0].MaxLimit = null; overflows[0] = true; }
            if (ii > PerWireCurrentMaxA) { YAxes[1].MaxLimit = null; overflows[1] = true; }
            if (powers[i] > PerWirePowerMaxW) { YAxes[2].MaxLimit = null; overflows[2] = true; }
        }

        if (!overflows[0] && !YAxes[0].MaxLimit.HasValue) YAxes[0].MaxLimit = PerWireVoltageMaxV;
        if (!overflows[1] && !YAxes[1].MaxLimit.HasValue) YAxes[1].MaxLimit = PerWireCurrentMaxA;
        if (!overflows[2] && !YAxes[2].MaxLimit.HasValue) YAxes[2].MaxLimit = PerWirePowerMaxW;

        _lastVoltages = pinVoltage;
        _lastCurrents = pinCurrent;
        _lastPowers   = powers;

        _seriesVoltage.Values = pinVoltage;
        _seriesCurrent.Values = pinCurrent;
        _seriesPower.Values   = powers;
        RefreshXLabels();

        TotalCurrentA = d.SumCurrentA;
        TotalPowerW   = d.SumPowerW;
        AvgVoltageV   = pinVoltage.Average();

        OnboardTempInC  = d.OnboardTempInC;
        OnboardTempOutC = d.OnboardTempOutC;
        ExternalTemp1C  = d.ExternalTemp1C;
        ExternalTemp2C  = d.ExternalTemp2C;

        PowerCableRatingText = TryResolveCableRatingText(d);
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
