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

    /// <summary>One selectable telemetry channel. Rendering moved to
    /// <see cref="Controls.SimpleLineChart"/> — this only carries selection,
    /// color, and how to extract the value from a <see cref="DeviceData"/>.</summary>
    public sealed class TelemetryItem : ViewModelBase
    {
        private bool _isEnabled;
        private Color _color;

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
                    OnPropertyChanged(nameof(ColorBrush));
                    ColorChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public IBrush ColorBrush => new SolidColorBrush(Color);

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
        }
    }

    // ======================== Fields ========================

    private readonly DeviceAutoConnector _connector;
    private readonly bool _ownsConnector;
    private readonly object _gate = new();
    private bool _isApplyingSettings;

    /// <summary>Per-channel sample buffers (window-trimmed) — the source of truth
    /// the chart series, Y autoscale, and CSV export are all built from.</summary>
    private readonly Dictionary<string, List<SimpleChartViewModel.DataPoint>> _buffersByKey =
        new(StringComparer.OrdinalIgnoreCase);

    private StreamWriter? _exportWriter;
    private bool _exportHeaderWritten;
    private double _lastExportX = double.NegativeInfinity;

    private bool _isExportingCsv;
    private int _xWindowSeconds = 30;
    private int _updateIntervalMs = 1000;
    private bool _isConnected;
    private readonly DateTime _t0Utc = DateTime.UtcNow;
    private bool _disposed;
    private bool _isViewVisible = true;

    // ======================== Properties ========================

    public bool IsExportingCsv
    {
        get => _isExportingCsv;
        private set { if (Set(ref _isExportingCsv, value)) OnPropertyChanged(nameof(ExportCsvButtonText)); }
    }

    public string ExportCsvButtonText => IsExportingCsv ? "Stop Exporting" : "Export CSV…";

    public SimpleChartViewModel Chart { get; } = new();

    /// <summary>Enabled channels → color; doubles as the chart's series filter.</summary>
    public IReadOnlyDictionary<string, Color> SeriesColorMap =>
        Items.Where(i => i.IsEnabled)
            .ToDictionary(i => i.Key, i => i.Color, StringComparer.OrdinalIgnoreCase);

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
    public AxisConfig YC { get; } = new("°C");

    public ObservableCollection<TelemetryItem> Items { get; } = new();

    public bool IsViewVisible
    {
        get => _isViewVisible;
        set => Set(ref _isViewVisible, value);
    }

    // ======================== Constructor ========================

    public MonitoringViewModel(DeviceAutoConnector? connector = null)
    {
        _connector = connector ?? DeviceAutoConnector.Shared;
        _ownsConnector = connector != null && connector != DeviceAutoConnector.Shared;

        double now = (DateTime.UtcNow - _t0Utc).TotalSeconds;
        Chart.SetXWindow(now - Math.Max(1, XWindowSeconds), now);

        YV.LimitsChanged += delegate { UpdateChartYScale(); PersistMonitoringSettings(); };
        YA.LimitsChanged += delegate { UpdateChartYScale(); PersistMonitoringSettings(); };
        YW.LimitsChanged += delegate { UpdateChartYScale(); PersistMonitoringSettings(); };
        YC.LimitsChanged += delegate { UpdateChartYScale(); PersistMonitoringSettings(); };

        BuildItems();
        ApplyMonitoringSettings();

        foreach (var item in Items.Where(i => i.IsEnabled))
            Chart.EnsureSeries(item.Key, item.Label);

        foreach (var it in Items)
        {
            it.EnabledChanged += (_, enabled) =>
            {
                if (enabled)
                {
                    Chart.EnsureSeries(it.Key, it.Label);
                    RebuildChartSeriesFromBuffer(it.Key);
                }
                OnPropertyChanged(nameof(SeriesColorMap));
                UpdateChartYScale();
                PersistMonitoringSettings();
            };
            it.ColorChanged += delegate
            {
                OnPropertyChanged(nameof(SeriesColorMap));
                PersistMonitoringSettings();
            };
        }

        UpdateChartYScale();

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
        App.MainWindowVisibilityChanged += OnMainWindowVisibilityChanged;
    }

    private void OnMainWindowVisibilityChanged(object? sender, bool isVisible)
    {
        if (!_disposed)
            IsViewVisible = isVisible;
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

        double minX = Chart.XMin;
        double maxX = Chart.XMax;
        if (writeOnlyNewPoints) minX = Math.Max(minX, _lastExportX);

        TimeZoneInfo local = TimeZoneInfo.Local;

        lock (_gate)
        {
            var allX = enabled
                .SelectMany(it => BufferFor(it.Key))
                .Select(p => p.X)
                .Where(x => x >= minX && x <= maxX)
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            if (allX.Count == 0) return;

            var lookup = enabled.ToDictionary(
                it => it,
                it => BufferFor(it.Key)
                    .Where(p => p.X >= minX && p.X <= maxX)
                    .GroupBy(p => p.X)
                    .ToDictionary(g => g.Key, g => g.Last().Y));

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

            UpdateChartYScale();
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

        Add("Tin",  "Onboard In (°C)",  "°C", d => d.OnboardTempInC);
        Add("Tout", "Onboard Out (°C)", "°C", d => d.OnboardTempOutC);
        Add("T1",   "External 1 (°C)",  "°C", d => d.ExternalTemp1C);
        Add("T2",   "External 2 (°C)",  "°C", d => d.ExternalTemp2C);

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
            "°C" => 3,
            _ => 0,
        };
    }

    // ======================== Chart helpers ========================

    private List<SimpleChartViewModel.DataPoint> BufferFor(string key)
    {
        if (!_buffersByKey.TryGetValue(key, out var list))
        {
            list = new List<SimpleChartViewModel.DataPoint>();
            _buffersByKey[key] = list;
        }
        return list;
    }

    private void RebuildChartSeriesFromBuffer(string key)
    {
        var series = Chart.SeriesItems.FirstOrDefault(
            s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
        if (series == null) return;

        List<SimpleChartViewModel.DataPoint> snapshot;
        lock (_gate) { snapshot = BufferFor(key).ToList(); }

        void Apply()
        {
            series.Points.Clear();
            foreach (var pt in snapshot)
                series.Points.Add(pt);
            series.RaiseChanged();
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply, DispatcherPriority.Background);
    }

    /// <summary>The line chart has one global Y scale. Auto (any axis) = min/max over
    /// all enabled channels' buffered points ±10%; all-manual = union of the per-unit
    /// configured ranges (matches upstream 1.0.7 behavior).</summary>
    private void UpdateChartYScale()
    {
        var enabled = Items.Where(i => i.IsEnabled).ToList();
        if (enabled.Count == 0) return;

        if (YV.Auto || YA.Auto || YW.Auto || YC.Auto)
        {
            List<double> ys;
            lock (_gate)
            {
                ys = enabled
                    .Select(it => it.Key)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .SelectMany(k => _buffersByKey.TryGetValue(k, out var buf)
                        ? (IEnumerable<SimpleChartViewModel.DataPoint>)buf
                        : Array.Empty<SimpleChartViewModel.DataPoint>())
                    .Select(p => p.Y)
                    .Where(double.IsFinite)
                    .ToList();
            }
            if (ys.Count == 0) return;

            double min = ys.Min();
            double max = ys.Max();
            if (Math.Abs(max - min) < 1e-9)
                max = min + 1.0;
            double pad = (max - min) * 0.1;
            Chart.SetYRange(min - pad, max + pad);
            return;
        }

        double rangeMin = double.PositiveInfinity;
        double rangeMax = double.NegativeInfinity;
        foreach (var item in enabled)
        {
            var (lo, hi) = item.YAxisIndex switch
            {
                0 => (YV.Min, YV.Max),
                1 => (YA.Min, YA.Max),
                2 => (YW.Min, YW.Max),
                3 => (YC.Min, YC.Max),
                _ => (0.0, 1.0),
            };
            rangeMin = Math.Min(rangeMin, lo);
            rangeMax = Math.Max(rangeMax, hi);
        }
        if (double.IsFinite(rangeMin) && double.IsFinite(rangeMax) && rangeMax > rangeMin)
            Chart.SetYRange(rangeMin, rangeMax);
    }

    // ======================== Data handler ========================

    private void OnDeviceData(DeviceData d)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnDeviceData(d), DispatcherPriority.Background);
            return;
        }

        double x = (((d.Timestamp.Kind == DateTimeKind.Utc) ? d.Timestamp : d.Timestamp.ToUniversalTime()) - _t0Utc).TotalSeconds;
        int win = Math.Max(1, XWindowSeconds);
        double cutoff = x - win;

        if (IsViewVisible)
            Chart.SetXWindow(cutoff, x);

        lock (_gate)
        {
            foreach (var item in Items.Where(i => i.IsEnabled))
            {
                double val = item.Selector(d);
                if (!double.IsFinite(val)) continue;

                var buffer = BufferFor(item.Key);
                buffer.Add(new SimpleChartViewModel.DataPoint(x, val));
                while (buffer.Count > 0 && buffer[0].X < cutoff)
                    buffer.RemoveAt(0);

                Chart.AddPoint(item.Key, x, val);
            }
            if (IsExportingCsv)
                ExportVisibleToCsvInternal(writeOnlyNewPoints: true);
        }

        UpdateChartYScale();
    }

    // ======================== Dispose ========================

    public void Dispose()
    {
        _disposed = true;
        App.MainWindowVisibilityChanged -= OnMainWindowVisibilityChanged;
        StopCsvExport();
        if (_ownsConnector)
        {
            _connector.ConnectionChanged -= delegate { };
            _connector.DataUpdated -= delegate { };
            _connector.Dispose();
        }
    }
}
