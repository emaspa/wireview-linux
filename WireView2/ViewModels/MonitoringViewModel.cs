using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using WireView2.Device;
using WireView2.Services;

namespace WireView2.ViewModels;

public sealed partial class MonitoringViewModel : ViewModelBase, IDisposable
{
    // ======================== Nested types ========================

    public sealed class AxisConfig : ViewModelBase
    {
        private bool _auto = true;
        private double _min;
        private double _max = 100.0;

        public string Unit { get; }

        public bool Auto
        {
            get => _auto;
            set { if (Set(ref _auto, value)) LimitsChanged?.Invoke(this, EventArgs.Empty); }
        }

        public double Min
        {
            get => _min;
            set { if (Set(ref _min, value)) LimitsChanged?.Invoke(this, EventArgs.Empty); }
        }

        public double Max
        {
            get => _max;
            set { if (Set(ref _max, value)) LimitsChanged?.Invoke(this, EventArgs.Empty); }
        }

        public event EventHandler? LimitsChanged;

        public AxisConfig(string unit) => Unit = unit;
    }

    public sealed class TelemetryItem : ViewModelBase
    {
        private readonly struct XY
        {
            public double X { get; }
            public double Y { get; }
            public XY(double x, double y) { X = x; Y = y; }
        }

        private sealed class RollingMax
        {
            private readonly LinkedList<XY> _dq = new();

            public double Max => _dq.Count > 0 ? _dq.First!.Value.Y : 0.0;

            public void Add(double x, double y)
            {
                while (_dq.Count > 0 && _dq.Last!.Value.Y <= y)
                    _dq.RemoveLast();
                _dq.AddLast(new XY(x, y));
            }

            public void Trim(double cutoffX)
            {
                while (_dq.Count > 0 && _dq.First!.Value.X < cutoffX)
                    _dq.RemoveFirst();
            }
        }

        private bool _isEnabled;
        private Color _color;
        private readonly ObservableCollection<ObservablePoint> _points = new();
        private readonly Queue<ObservablePoint> _queue = new();
        private readonly RollingMax _rollingMax = new();
        private int _batchCounter;

        public string Key { get; }
        public string Label { get; }
        public string Unit { get; }
        public Func<DeviceData, double> Selector { get; }
        public int YAxisIndex { get; }

        public bool IsEnabled
        {
            get => _isEnabled;
            set { if (Set(ref _isEnabled, value)) EnabledChanged?.Invoke(this, value); }
        }

        public Color Color
        {
            get => _color;
            set
            {
                if (Set(ref _color, value))
                {
                    ApplyColorToSeries();
                    ColorChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public ISeries Series { get; }
        public ObservableCollection<ObservablePoint> Points => _points;
        public double VisibleMaxY => _rollingMax.Max;

        public event EventHandler<bool>? EnabledChanged;
        public event EventHandler? ColorChanged;

        public TelemetryItem(string key, string label, string unit,
            Func<DeviceData, double> selector, Color color, int yAxisIndex)
        {
            Key = key;
            Label = label;
            Unit = unit;
            Selector = selector;
            YAxisIndex = yAxisIndex;
            _color = color;

            Series = new LineSeries<ObservablePoint>
            {
                Name = label,
                Values = _points,
                GeometryFill = null,
                GeometryStroke = null,
                Fill = null,
                LineSmoothness = 0.0,
                ScalesYAt = yAxisIndex
            };
            ApplyColorToSeries();
        }

        public void ApplyColorToSeries()
        {
            var skColor = new SKColor(Color.R, Color.G, Color.B, Color.A);
            if (Series is LineSeries<ObservablePoint> line)
            {
                line.Stroke = new SolidColorPaint(skColor) { StrokeThickness = 2f };
            }
        }

        public void AddPoint(double xSeconds, double value, double cutoffSeconds)
        {
            var pt = new ObservablePoint(xSeconds, value);
            _queue.Enqueue(pt);
            _rollingMax.Add(xSeconds, value);

            while (_queue.Count > 0 && _queue.Peek().X < cutoffSeconds)
                _queue.Dequeue();
            _rollingMax.Trim(cutoffSeconds);

            _batchCounter++;
            if (_batchCounter < 5)
            {
                _points.Add(pt);
                return;
            }
            _batchCounter = 0;
            _points.Clear();
            foreach (var p in _queue)
                _points.Add(p);
        }
    }

    // ======================== Fields ========================

    private readonly DeviceAutoConnector _connector;
    private readonly bool _ownsConnector;
    private readonly object _gate = new();
    private bool _isApplyingSettings;

    private StreamWriter? _exportWriter;
    private bool _exportHeaderWritten;
    private double _lastExportX = double.NegativeInfinity;

    private bool _isExportingCsv;
    private int _xWindowSeconds = 30;
    private int _updateIntervalMs = 1000;
    private bool _isConnected;
    private readonly DateTime _t0Utc = DateTime.UtcNow;

    // ======================== Properties ========================

    public bool IsExportingCsv
    {
        get => _isExportingCsv;
        private set { if (Set(ref _isExportingCsv, value)) OnPropertyChanged(nameof(ExportCsvButtonText)); }
    }

    public string ExportCsvButtonText => IsExportingCsv ? "Stop Exporting" : "Export CSV\u2026";

    public ObservableCollection<ISeries> Series { get; } = new();
    public Axis[] XAxes { get; }
    public Axis[] YAxes { get; }

    public int XWindowSeconds
    {
        get => _xWindowSeconds;
        set { if (Set(ref _xWindowSeconds, Math.Max(1, value))) PersistMonitoringSettings(); }
    }

    public int UpdateIntervalMs
    {
        get => _updateIntervalMs;
        set
        {
            if (Set(ref _updateIntervalMs, Math.Clamp(value, 50, 5000)))
            {
                _connector.SetPollInterval(_updateIntervalMs);
                PersistMonitoringSettings();
            }
        }
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set => Set(ref _isConnected, value);
    }

    public string ConnectionText => IsConnected ? "Connected" : "Disconnected";

    public AxisConfig YV { get; } = new("V");
    public AxisConfig YA { get; } = new("A");
    public AxisConfig YW { get; } = new("W");
    public AxisConfig YC { get; } = new("\u00b0C");

    public ObservableCollection<TelemetryItem> Items { get; } = new();

    // ======================== Constructor ========================

    public MonitoringViewModel(DeviceAutoConnector? connector = null)
    {
        _connector = connector ?? DeviceAutoConnector.Shared;
        _ownsConnector = connector != null && connector != DeviceAutoConnector.Shared;

        XAxes = new Axis[]
        {
            new Axis
            {
                UnitWidth = 1.0,
                MinStep = 1.0,
                Labeler = v => (double.IsNaN(v) || double.IsInfinity(v))
                    ? string.Empty
                    : TimeZoneInfo.ConvertTimeFromUtc(_t0Utc.AddSeconds(v), TimeZoneInfo.Local)
                        .ToString("HH:mm:ss")
            }
        };

        double now = (DateTime.UtcNow - _t0Utc).TotalSeconds;
        int win = Math.Max(1, XWindowSeconds);
        XAxes[0].MinLimit = now - win;
        XAxes[0].MaxLimit = now;

        YAxes = new Axis[]
        {
            MakeYAxis("V", 0),
            MakeYAxis("A", 1),
            MakeYAxis("W", 2),
            MakeYAxis("\u00b0C", 3)
        };

        YV.LimitsChanged += delegate { ApplyAxisLimits(0, YV); PersistMonitoringSettings(); };
        YA.LimitsChanged += delegate { ApplyAxisLimits(1, YA); PersistMonitoringSettings(); };
        YW.LimitsChanged += delegate { ApplyAxisLimits(2, YW); PersistMonitoringSettings(); };
        YC.LimitsChanged += delegate { ApplyAxisLimits(3, YC); PersistMonitoringSettings(); };

        ApplyAxisLimits(0, YV);
        ApplyAxisLimits(1, YA);
        ApplyAxisLimits(2, YW);
        ApplyAxisLimits(3, YC);

        BuildItems();
        ApplyMonitoringSettings();

        foreach (var item in Items.Where(i => i.IsEnabled))
            Series.Add(item.Series);

        foreach (var it in Items)
        {
            it.EnabledChanged += (_, enabled) =>
            {
                if (enabled)
                {
                    if (!Series.Contains(it.Series))
                        Series.Add(it.Series);
                }
                else
                {
                    Series.Remove(it.Series);
                }
                UpdateYAxisVisibility();
                UpdateAutoAxisScalesFast();
                PersistMonitoringSettings();
            };
            it.ColorChanged += delegate
            {
                it.ApplyColorToSeries();
                PersistMonitoringSettings();
            };
        }

        UpdateYAxisVisibility();
        UpdateAutoAxisScalesFast();

        _connector.ConnectionChanged += (_, connected) =>
        {
            void Apply()
            {
                IsConnected = connected;
                OnPropertyChanged(nameof(IsConnected));
                OnPropertyChanged(nameof(ConnectionText));
            }
            if (Dispatcher.UIThread.CheckAccess()) Apply();
            else Dispatcher.UIThread.Post(Apply, DispatcherPriority.Background);
        };
        _connector.DataUpdated += (_, data) => OnDeviceData(data);
        _connector.SetPollInterval(UpdateIntervalMs);
        _connector.Start();

        Dispatcher.UIThread.Post(() =>
        {
            IsConnected = _connector.Device?.Connected ?? false;
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(ConnectionText));
        });
    }

    // ======================== CSV Export ========================

    public void StartCsvExport(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        StopCsvExport();
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        _exportWriter = new StreamWriter(filePath, append: false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true))
        { AutoFlush = true };
        _exportHeaderWritten = false;
        _lastExportX = double.NegativeInfinity;
        IsExportingCsv = true;
        ExportVisibleToCsvInternal(writeOnlyNewPoints: false);
    }

    public void StopCsvExport()
    {
        _exportWriter?.Dispose();
        _exportWriter = null;
        _exportHeaderWritten = false;
        _lastExportX = double.NegativeInfinity;
        IsExportingCsv = false;
    }

    [RelayCommand]
    public void ToggleCsvExport(string? filePathIfStarting)
    {
        if (IsExportingCsv) StopCsvExport();
        else if (!string.IsNullOrWhiteSpace(filePathIfStarting)) StartCsvExport(filePathIfStarting!);
    }

    public void ExportVisibleToCsv(string filePath)
    {
        StartCsvExport(filePath);
        StopCsvExport();
    }

    private void ExportVisibleToCsvInternal(bool writeOnlyNewPoints)
    {
        if (_exportWriter == null) return;

        var enabled = Items.Where(i => i.IsEnabled).ToList();
        if (enabled.Count == 0) return;

        double minX = XAxes is { Length: > 0 }
            ? XAxes[0].MinLimit ?? double.NegativeInfinity
            : double.NegativeInfinity;
        double maxX = XAxes is { Length: > 0 }
            ? XAxes[0].MaxLimit ?? double.PositiveInfinity
            : double.PositiveInfinity;
        if (writeOnlyNewPoints) minX = Math.Max(minX, _lastExportX);

        TimeZoneInfo local = TimeZoneInfo.Local;

        lock (_gate)
        {
            var allX = enabled
                .SelectMany(it => it.Points)
                .Where(p => p?.X != null)
                .Select(p => p.X!.Value)
                .Where(x => x >= minX && x <= maxX)
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            if (allX.Count == 0) return;

            var lookup = enabled.ToDictionary(
                it => it,
                it => it.Points
                    .Where(p => p?.X != null && p?.Y != null)
                    .Where(p => p.X!.Value >= minX && p.X!.Value <= maxX)
                    .GroupBy(p => p.X!.Value)
                    .ToDictionary(g => g.Key, g => g.Last().Y!.Value));

            if (!_exportHeaderWritten)
            {
                _exportWriter.Write(CsvEscape("TimeLocal"));
                _exportWriter.Write(",");
                _exportWriter.Write(CsvEscape("Seconds"));
                foreach (var item in enabled)
                {
                    _exportWriter.Write(",");
                    _exportWriter.Write(CsvEscape(item.Label));
                }
                _exportWriter.WriteLine();
                _exportHeaderWritten = true;
            }

            foreach (double x in allX)
            {
                DateTime dt = TimeZoneInfo.ConvertTimeFromUtc(_t0Utc.AddSeconds(x), local);
                _exportWriter.Write(CsvEscape(dt.ToString("o", CultureInfo.InvariantCulture)));
                _exportWriter.Write(",");
                _exportWriter.Write(F(x));
                foreach (var item in enabled)
                {
                    _exportWriter.Write(",");
                    if (lookup[item].TryGetValue(x, out double val) && double.IsFinite(val))
                        _exportWriter.Write(F(val));
                }
                _exportWriter.WriteLine();
                _lastExportX = Math.Max(_lastExportX, x);
            }
        }

        static string CsvEscape(string? s)
        {
            s ??= string.Empty;
            if (s.Contains('"') || s.Contains(',') || s.Contains('\n') || s.Contains('\r'))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }

    // ======================== Settings persistence ========================

    private void ApplyMonitoringSettings()
    {
        _isApplyingSettings = true;
        try
        {
            var s = AppSettings.Current;
            XWindowSeconds = Math.Max(1, s.MonitoringXWindowSeconds);
            UpdateIntervalMs = Math.Clamp(s.MonitoringUpdateIntervalMs, 50, 5000);

            if (s.MonitoringYV != null) ApplyAxisConfigFromSettings(YV, s.MonitoringYV);
            if (s.MonitoringYA != null) ApplyAxisConfigFromSettings(YA, s.MonitoringYA);
            if (s.MonitoringYW != null) ApplyAxisConfigFromSettings(YW, s.MonitoringYW);
            if (s.MonitoringYC != null) ApplyAxisConfigFromSettings(YC, s.MonitoringYC);

            var keys = (s.MonitoringEnabledSeriesKeys ?? new List<string>())
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (keys.Count > 0)
            {
                foreach (var item in Items)
                    item.IsEnabled = keys.Contains(item.Key);
            }

            if (s.MonitoringSeries is { Count: > 0 })
            {
                var colorMap = s.MonitoringSeries
                    .Where(x => !string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.Color))
                    .ToDictionary(x => x.Key, x => x.Color!, StringComparer.OrdinalIgnoreCase);

                foreach (var item in Items)
                {
                    if (colorMap.TryGetValue(item.Key, out var hex) && TryParseHexColor(hex, out var color))
                        item.Color = color;
                }
            }

            ApplyAxisLimits(0, YV);
            ApplyAxisLimits(1, YA);
            ApplyAxisLimits(2, YW);
            ApplyAxisLimits(3, YC);
            UpdateYAxisVisibility();
            UpdateAutoAxisScales(null);
        }
        finally
        {
            _isApplyingSettings = false;
        }
    }

    private static void ApplyAxisConfigFromSettings(AxisConfig axis, AppSettings.MonitoringAxisSettings s)
    {
        axis.Auto = s.Auto;
        axis.Min = s.Min;
        axis.Max = s.Max;
    }

    private void PersistMonitoringSettings()
    {
        if (_isApplyingSettings) return;

        var s = AppSettings.Current;
        s.MonitoringXWindowSeconds = Math.Max(1, XWindowSeconds);
        s.MonitoringUpdateIntervalMs = Math.Clamp(UpdateIntervalMs, 50, 5000);
        s.MonitoringYV = ToAxisSettings(YV);
        s.MonitoringYA = ToAxisSettings(YA);
        s.MonitoringYW = ToAxisSettings(YW);
        s.MonitoringYC = ToAxisSettings(YC);
        s.MonitoringEnabledSeriesKeys = Items.Where(i => i.IsEnabled).Select(i => i.Key).ToList();
        s.MonitoringSeries = Items.Select(i => new AppSettings.MonitoringSeriesSettings
        {
            Key = i.Key,
            Color = ToHex(i.Color)
        }).ToList();
        AppSettings.SaveCurrent();
    }

    private static AppSettings.MonitoringAxisSettings ToAxisSettings(AxisConfig cfg) => new()
    {
        Auto = cfg.Auto,
        Min = cfg.Min,
        Max = cfg.Max
    };

    private static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    private static bool TryParseHexColor(string text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string hex = text.Trim();
        if (hex.StartsWith("#", StringComparison.Ordinal))
            hex = hex[1..];

        if (hex.Length == 6)
        {
            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint val))
                return false;
            byte r = (byte)((val >> 16) & 0xFF);
            byte g = (byte)((val >> 8) & 0xFF);
            byte b = (byte)(val & 0xFF);
            color = Color.FromArgb(255, r, g, b);
            return true;
        }

        if (hex.Length == 8)
        {
            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint val))
                return false;
            byte a = (byte)((val >> 24) & 0xFF);
            byte r = (byte)((val >> 16) & 0xFF);
            byte g = (byte)((val >> 8) & 0xFF);
            byte b = (byte)(val & 0xFF);
            color = Color.FromArgb(a, r, g, b);
            return true;
        }

        return false;
    }

    // ======================== Telemetry items ========================

    private void BuildItems()
    {
        Color[] palette =
        {
            Colors.LimeGreen, Colors.OrangeRed, Colors.DodgerBlue, Colors.Gold,
            Colors.Violet, Colors.Turquoise, Colors.DeepPink, Colors.Coral,
            Colors.MediumSeaGreen, Colors.SlateBlue, Colors.Tomato, Colors.MediumOrchid
        };
        int p = 0;

        Add("Psum", "Total Power (W)", "W", d => d.SumPowerW, enabled: true);
        Add("Isum", "Total Current (A)", "A", d => d.SumCurrentA, enabled: true);

        for (int n = 0; n < 6; n++)
        {
            int idx = n;
            Add($"V{idx + 1}", $"V{idx + 1} (V)", "V", d => d.PinVoltage[idx]);
        }
        for (int n = 0; n < 6; n++)
        {
            int idx = n;
            Add($"I{idx + 1}", $"I{idx + 1} (A)", "A", d => d.PinCurrent[idx]);
        }

        Add("Tin",  "Onboard In (\u00b0C)",  "\u00b0C", d => d.OnboardTempInC);
        Add("Tout", "Onboard Out (\u00b0C)", "\u00b0C", d => d.OnboardTempOutC);
        Add("T1",   "External 1 (\u00b0C)",  "\u00b0C", d => d.ExternalTemp1C);
        Add("T2",   "External 2 (\u00b0C)",  "\u00b0C", d => d.ExternalTemp2C);

        TelemetryItem Add(string key, string label, string unit,
            Func<DeviceData, double> sel, bool enabled = false)
        {
            var color = palette[p++ % palette.Length];
            var item = new TelemetryItem(key, label, unit, sel, color, AxisForUnit(unit))
            {
                IsEnabled = enabled
            };
            Items.Add(item);
            return item;
        }

        static int AxisForUnit(string u) => u switch
        {
            "V" => 0,
            "A" => 1,
            "W" => 2,
            "\u00b0C" => 3,
            _ => 0,
        };
    }

    // ======================== Axis helpers ========================

    private Axis MakeYAxis(string unit, int index) => new()
    {
        Name = unit,
        Labeler = v => $"{v:0.###} {unit}",
        Position = index % 2 != 0 ? AxisPosition.End : AxisPosition.Start
    };

    private void ApplyAxisLimits(int axisIndex, AxisConfig cfg)
    {
        var axis = YAxes[axisIndex];
        if (cfg.Auto)
        {
            axis.MinLimit = 0.0;
            axis.MaxLimit = null;
            UpdateAutoAxisScales(null);
        }
        else
        {
            axis.MinLimit = cfg.Min;
            axis.MaxLimit = cfg.Max > cfg.Min ? cfg.Max : cfg.Min + 1e-9;
        }
    }

    private void UpdateYAxisVisibility()
    {
        if (YAxes is not { Length: >= 4 }) return;
        YAxes[0].IsVisible = AnyEnabledAt(0);
        YAxes[1].IsVisible = AnyEnabledAt(1);
        YAxes[2].IsVisible = AnyEnabledAt(2);
        YAxes[3].IsVisible = AnyEnabledAt(3);

        bool AnyEnabledAt(int idx) => Items.Any(it => it.IsEnabled && it.YAxisIndex == idx);
    }

    private static double NiceCeiling(double v)
    {
        if (v <= 0.0 || !double.IsFinite(v)) return 0.0;
        double mag = Math.Pow(10.0, Math.Floor(Math.Log10(v)));
        double norm = v / mag;
        double nice = norm <= 1.0 ? 1.0 : norm <= 2.0 ? 2.0 : norm <= 5.0 ? 5.0 : 10.0;
        return nice * mag;
    }

    private void UpdateAutoAxisScalesFast()
    {
        if (YAxes is not { Length: >= 4 }) return;

        double maxV = 0, maxPerWireA = 0, maxSumA = 0, maxW = 0, maxC = 0;

        foreach (var item in Items)
        {
            if (!item.IsEnabled) continue;
            double m = item.VisibleMaxY;
            if (!double.IsFinite(m)) continue;

            switch (item.YAxisIndex)
            {
                case 0: maxV = Math.Max(maxV, m); break;
                case 1:
                    if (string.Equals(item.Key, "Isum", StringComparison.OrdinalIgnoreCase))
                        maxSumA = Math.Max(maxSumA, m);
                    else
                        maxPerWireA = Math.Max(maxPerWireA, m);
                    break;
                case 2: maxW = Math.Max(maxW, m); break;
                case 3: maxC = Math.Max(maxC, m); break;
            }
        }

        if (YV.Auto && Items.Any(it => it.IsEnabled && it.YAxisIndex == 0))
        {
            YAxes[0].MinLimit = 0.0;
            YAxes[0].MaxLimit = Math.Max(13.0, NiceCeiling(maxV));
        }
        if (YA.Auto && Items.Any(it => it.IsEnabled && it.YAxisIndex == 1))
        {
            YAxes[1].MinLimit = 0.0;
            bool hasPerWire = Items.Any(it => it.IsEnabled && it.YAxisIndex == 1 && it.Key != "Isum");
            bool hasSumI = Items.Any(it => it.IsEnabled && it.YAxisIndex == 1 && it.Key == "Isum");
            double floor = Math.Max(hasPerWire ? 10.0 : 0.0, hasSumI ? 30.0 : 0.0);
            YAxes[1].MaxLimit = Math.Max(floor, NiceCeiling(Math.Max(maxPerWireA, maxSumA)));
        }
        if (YW.Auto && Items.Any(it => it.IsEnabled && it.YAxisIndex == 2))
        {
            YAxes[2].MinLimit = 0.0;
            YAxes[2].MaxLimit = Math.Max(300.0, NiceCeiling(maxW));
        }
        if (YC.Auto && Items.Any(it => it.IsEnabled && it.YAxisIndex == 3))
        {
            YAxes[3].MinLimit = 0.0;
            YAxes[3].MaxLimit = Math.Max(50.0, NiceCeiling(maxC));
        }
    }

    private void UpdateAutoAxisScales(DeviceData? d)
    {
        // Same logic as UpdateAutoAxisScalesFast; kept as separate method for clarity.
        UpdateAutoAxisScalesFast();
    }

    // ======================== Data handler ========================

    private void OnDeviceData(DeviceData d)
    {
        double x = (((d.Timestamp.Kind == DateTimeKind.Utc) ? d.Timestamp : d.Timestamp.ToUniversalTime()) - _t0Utc).TotalSeconds;
        int win = Math.Max(1, XWindowSeconds);
        double cutoff = x - win;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnDeviceData(d), DispatcherPriority.Background);
            return;
        }

        XAxes[0].MinLimit = cutoff;
        XAxes[0].MaxLimit = x;

        lock (_gate)
        {
            foreach (var item in Items.Where(i => i.IsEnabled))
            {
                double val = item.Selector(d);
                if (double.IsFinite(val))
                    item.AddPoint(x, val, cutoff);
            }
            if (IsExportingCsv)
                ExportVisibleToCsvInternal(writeOnlyNewPoints: true);
        }

        UpdateAutoAxisScalesFast();
    }

    // ======================== Dispose ========================

    public void Dispose()
    {
        StopCsvExport();
        if (_ownsConnector)
        {
            _connector.ConnectionChanged -= delegate { };
            _connector.DataUpdated -= delegate { };
            _connector.Dispose();
        }
    }
}
