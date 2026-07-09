using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using WireView2.Device;

namespace WireView2.ViewModels;

public sealed partial class LoggingViewModel : ViewModelBase, IDisposable
{
    private readonly DeviceAutoConnector _connector;
    private readonly object _gate = new();
    private DateTime _t0Utc = DateTime.Parse("2026-01-01 00:00");

    private bool _isReading;
    private double _readProgress;
    private string _statusText = "No data loaded.";
    private CancellationTokenSource? _cts;
    private readonly List<DeviceData> _history = new();
    private readonly List<List<DeviceData>> _measurementCycles = new();
    private MeasurementCycleItem? _selectedMeasurementCycle;
    private byte[]? _deviceLogBuffer;

    /// <summary>One power-on-to-power-off span of the on-device log.</summary>
    public sealed record MeasurementCycleItem(int Index, int SampleCount, DateTime? StartUtc, DateTime? EndUtc)
    {
        public TimeSpan? Duration =>
            StartUtc.HasValue && EndUtc.HasValue && EndUtc >= StartUtc ? EndUtc - StartUtc : null;

        public string Label
        {
            get
            {
                if (SampleCount == 0) return $"Cycle #{Index + 1} (empty)";
                var d = Duration;
                string duration = d.HasValue
                    ? (d.Value.TotalHours >= 1
                        ? $"{(int)d.Value.TotalHours}:{d.Value.Minutes:00}:{d.Value.Seconds:00}"
                        : $"{d.Value.Minutes}:{d.Value.Seconds:00}")
                    : "?";
                return $"Cycle #{Index + 1} ({SampleCount} samples, {duration})";
            }
        }

        public override string ToString() => Label;
    }

    // ======================== Properties ========================

    public ObservableCollection<MeasurementCycleItem> MeasurementCycles { get; } = new();

    public bool HasMeasurementCycles => MeasurementCycles.Count > 0;

    public MeasurementCycleItem? SelectedMeasurementCycle
    {
        get => _selectedMeasurementCycle;
        set
        {
            if (Set(ref _selectedMeasurementCycle, value))
                ApplySelectedPowerCycle();
        }
    }

    public SimpleChartViewModel Chart { get; } = new();

    /// <summary>Enabled channels → color; doubles as the chart's series filter.</summary>
    public IReadOnlyDictionary<string, Color> SeriesColorMap =>
        Items.Where(i => i.IsEnabled)
            .ToDictionary(i => i.Key, i => i.Color, StringComparer.OrdinalIgnoreCase);

    public MonitoringViewModel.AxisConfig YV { get; } = new("V");
    public MonitoringViewModel.AxisConfig YA { get; } = new("A");
    public MonitoringViewModel.AxisConfig YW { get; } = new("W");
    public MonitoringViewModel.AxisConfig YC { get; } = new("\u00b0C");

    public ObservableCollection<MonitoringViewModel.TelemetryItem> Items { get; } = new();

    public bool IsReading
    {
        get => _isReading;
        private set => Set(ref _isReading, value);
    }

    public double ReadProgress
    {
        get => _readProgress;
        private set => Set(ref _readProgress, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    // ======================== Constructor ========================

    public LoggingViewModel(DeviceAutoConnector? connector = null)
    {
        _connector = connector ?? DeviceAutoConnector.Shared;

        YV.LimitsChanged += delegate { UpdateChartYScale(); };
        YA.LimitsChanged += delegate { UpdateChartYScale(); };
        YW.LimitsChanged += delegate { UpdateChartYScale(); };
        YC.LimitsChanged += delegate { UpdateChartYScale(); };

        BuildItems();

        foreach (var item in Items.Where(i => i.IsEnabled))
            Chart.EnsureSeries(item.Key, item.Label);

        foreach (var it in Items)
        {
            it.EnabledChanged += (_, enabled) =>
            {
                if (enabled)
                {
                    Chart.EnsureSeries(it.Key, it.Label);
                    if (_history.Count > 0)
                        RebuildSeriesPointsFor(it);
                }
                OnPropertyChanged(nameof(SeriesColorMap));
                UpdateChartYScale();
            };
            it.ColorChanged += delegate { OnPropertyChanged(nameof(SeriesColorMap)); };
        }

        _connector.Start();
    }

    // ======================== Chart helpers ========================

    /// <summary>One global Y scale: any axis on Auto = min/max over the loaded rows
    /// of enabled channels \u00b110%; all-manual = union of per-unit configured ranges.</summary>
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
                    .SelectMany(it => _history.Select(d => it.Selector(d)))
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

    // ======================== Build telemetry items ========================

    private void BuildItems()
    {
        Avalonia.Media.Color[] palette =
        {
            Avalonia.Media.Colors.LimeGreen, Avalonia.Media.Colors.OrangeRed,
            Avalonia.Media.Colors.DodgerBlue, Avalonia.Media.Colors.Gold,
            Avalonia.Media.Colors.Violet, Avalonia.Media.Colors.Turquoise,
            Avalonia.Media.Colors.DeepPink, Avalonia.Media.Colors.Coral,
            Avalonia.Media.Colors.MediumSeaGreen, Avalonia.Media.Colors.SlateBlue,
            Avalonia.Media.Colors.Tomato, Avalonia.Media.Colors.MediumOrchid
        };
        int p = 0;

        Add("Psum", "Total Power (W)", "W", d => d.SumPowerW);
        Add("Isum", "Total Current (A)", "A", d => d.SumCurrentA);

        for (int n = 0; n < 6; n++)
        {
            int idx = n;
            Add($"V{idx + 1}", $"V{idx + 1} (V)", "V", d => d.PinVoltage[idx]);
        }
        for (int n = 0; n < 6; n++)
        {
            int idx = n;
            Add($"I{idx + 1}", $"I{idx + 1} (A)", "A", d => d.PinCurrent[idx], enabled: true);
        }

        Add("Tin",  "Onboard In (\u00b0C)",  "\u00b0C", d => d.OnboardTempInC,  enabled: true);
        Add("Tout", "Onboard Out (\u00b0C)", "\u00b0C", d => d.OnboardTempOutC, enabled: true);
        Add("T1",   "External 1 (\u00b0C)",  "\u00b0C", d => d.ExternalTemp1C);
        Add("T2",   "External 2 (\u00b0C)",  "\u00b0C", d => d.ExternalTemp2C);

        MonitoringViewModel.TelemetryItem Add(string key, string label, string unit,
            Func<DeviceData, double> sel, bool enabled = false)
        {
            var color = palette[p++ % palette.Length];
            var item = new MonitoringViewModel.TelemetryItem(key, label, unit, sel, color, AxisForUnit(unit))
            {
                IsEnabled = enabled
            };
            Items.Add(item);
            return item;
        }

        static int AxisForUnit(string u) => u switch
        {
            "V" => 0, "A" => 1, "W" => 2, "\u00b0C" => 3, _ => 0,
        };
    }

    // ======================== Commands ========================

    [RelayCommand]
    private async Task ReadAsync()
    {
        var serialDevice = _connector.Device as WireViewPro2Device;
        var hwmonDevice = _connector.Device as HwmonDevice;
        if (serialDevice is not { Connected: true }
            && hwmonDevice is not { Connected: true, DaemonAvailable: true })
        {
            StatusText = "Not connected.";
            return;
        }

        Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsReading = true;
        ReadProgress = 0.0;
        StatusText = "Reading history from SPI flash...";

        try
        {
            var progress = new Progress<double>(p => ReadProgress = p);
            // Via the daemon-backed device, borrow the port for the bulk SPI read
            // (the daemon pauses its polling and resumes afterwards).
            _deviceLogBuffer = serialDevice != null
                ? await serialDevice.ReadDeviceLogAsync(progress, token).ConfigureAwait(false)
                : await DirectSerialSession.RunAsync(hwmonDevice!,
                    d => d.ReadDeviceLogAsync(progress, token)).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var entries = DeviceLogParser.Parse(_deviceLogBuffer);
                var cycles = DataLoggerEntryToDeviceData(entries);
                Load(cycles);
                int samples = cycles.Sum(c => c.Count);
                StatusText = samples == 0
                    ? "No history found."
                    : $"Loaded {samples} samples in {cycles.Count} power cycle(s).";
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Read canceled.";
        }
        catch (Exception ex)
        {
            StatusText = "Read failed: " + ex.Message;
        }
        finally
        {
            IsReading = false;
            ReadProgress = 0.0;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>Converts raw log entries into per-power-cycle sample lists. A cycle
    /// ends at an explicit POWER_ON marker or when the MCU tick counter wraps
    /// backwards (the counter resets on power-up). Timestamps are synthetic —
    /// each cycle restarts at the epoch and advances 4 ms per tick.</summary>
    private List<List<DeviceData>> DataLoggerEntryToDeviceData(
        IReadOnlyList<DeviceLogParser.DATALOGGER_Entry> entries)
    {
        var cycles = new List<List<DeviceData>>();
        var current = new List<DeviceData>();
        DateTime timestamp = DateTime.Parse("2026-01-01 00:00");
        uint prevTick = 0u;

        foreach (var entry in entries)
        {
            var entryType = DeviceLogParser.DecodeType(entry.Data);
            uint tick30 = DeviceLogParser.DecodeTimestamp30(entry.Data);

            switch (entryType)
            {
                case DeviceLogParser.ENTRY_TYPE.ENTRY_TYPE_POWER_ON:
                    if (current.Count > 0)
                    {
                        cycles.Add(current);
                        current = new List<DeviceData>();
                    }
                    timestamp = DateTime.Parse("2026-01-01 00:00");
                    continue; // boundary marker, not a sample

                case DeviceLogParser.ENTRY_TYPE.ENTRY_TYPE_MCU_TICK:
                    int delta = (int)(tick30 - prevTick);
                    if (delta < 0)
                    {
                        if (current.Count > 0)
                        {
                            cycles.Add(current);
                            current = new List<DeviceData>();
                        }
                        timestamp = DateTime.Parse("2026-01-01 00:00");
                        delta = 0;
                    }
                    timestamp = timestamp.AddMilliseconds(delta * 4);
                    prevTick = tick30;
                    break;

                case DeviceLogParser.ENTRY_TYPE.ENTRY_TYPE_SYSTEM_TIME:
                case DeviceLogParser.ENTRY_TYPE.ENTRY_TYPE_EMPTY:
                    continue;
            }

            var dd = new DeviceData
            {
                Timestamp = timestamp,
                OnboardTempInC  = entry.Ts[0],
                OnboardTempOutC = entry.Ts[1],
                ExternalTemp1C  = entry.Ts[2],
                ExternalTemp2C  = entry.Ts[3]
            };

            for (int i = 0; i < 6; i++)
            {
                dd.PinVoltage[i] = (float)entry.Voltage[i] / 10f;
                dd.PinCurrent[i] = (float)entry.Current[i] / 10f;
            }

            current.Add(dd);
        }

        if (current.Count > 0)
            cycles.Add(current);
        return cycles;
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void Clear()
    {
        ClearUiAndHistory();
        _measurementCycles.Clear();
        MeasurementCycles.Clear();
        _selectedMeasurementCycle = null;
        OnPropertyChanged(nameof(SelectedMeasurementCycle));
        OnPropertyChanged(nameof(HasMeasurementCycles));
        _t0Utc = DateTime.Parse("2026-01-01 00:00");
        Chart.SetXWindow(0.0, 1.0);
        StatusText = "Cleared.";
    }

    private void ClearUiAndHistory()
    {
        lock (_gate)
        {
            _history.Clear();
        }
        void ClearPoints()
        {
            foreach (var series in Chart.SeriesItems)
            {
                series.Points.Clear();
                series.RaiseChanged();
            }
        }
        if (Dispatcher.UIThread.CheckAccess()) ClearPoints();
        else Dispatcher.UIThread.Post(ClearPoints, DispatcherPriority.Background);
    }

    private void Load(IReadOnlyList<IReadOnlyList<DeviceData>>? cycles)
    {
        ClearUiAndHistory();
        _measurementCycles.Clear();
        MeasurementCycles.Clear();
        _selectedMeasurementCycle = null;
        OnPropertyChanged(nameof(SelectedMeasurementCycle));

        if (cycles != null)
        {
            foreach (var cycle in cycles)
            {
                if (cycle == null || cycle.Count == 0) continue;

                var sorted = cycle.OrderBy(d => d.Timestamp).ToList();
                _measurementCycles.Add(sorted);
                MeasurementCycles.Add(new MeasurementCycleItem(
                    _measurementCycles.Count - 1,
                    sorted.Count,
                    ToUtc(sorted[0].Timestamp),
                    ToUtc(sorted[^1].Timestamp)));
            }
        }

        OnPropertyChanged(nameof(HasMeasurementCycles));

        if (MeasurementCycles.Count > 0)
        {
            // Auto-select the most recent cycle; the setter applies it to the UI.
            SelectedMeasurementCycle = MeasurementCycles[^1];
        }
        else
        {
            UpdateChartYScale();
        }

        static DateTime ToUtc(DateTime t) => t.Kind == DateTimeKind.Utc ? t : t.ToUniversalTime();
    }

    private void ApplySelectedPowerCycle()
    {
        ClearUiAndHistory();

        var item = _selectedMeasurementCycle;
        if (item == null || item.Index < 0 || item.Index >= _measurementCycles.Count)
        {
            UpdateChartYScale();
            return;
        }

        var rows = _measurementCycles[item.Index];
        _t0Utc = rows[0].Timestamp.Kind == DateTimeKind.Utc
            ? rows[0].Timestamp
            : rows[0].Timestamp.ToUniversalTime();

        var lastTs = rows[^1].Timestamp.Kind == DateTimeKind.Utc
            ? rows[^1].Timestamp
            : rows[^1].Timestamp.ToUniversalTime();

        double span = (lastTs - _t0Utc).TotalSeconds;
        Chart.SetXWindow(0.0, Math.Max(1.0, span));

        lock (_gate) { _history.AddRange(rows); }

        foreach (var item2 in Items.Where(i => i.IsEnabled))
            RebuildSeriesPointsFor(item2);

        UpdateChartYScale();
    }

    private void RebuildSeriesPointsFor(MonitoringViewModel.TelemetryItem it)
    {
        List<SimpleChartViewModel.DataPoint> newPoints;
        lock (_gate)
        {
            newPoints = new List<SimpleChartViewModel.DataPoint>(_history.Count + 16);
            foreach (var d in _history)
            {
                double x = ((d.Timestamp.Kind == DateTimeKind.Utc ? d.Timestamp : d.Timestamp.ToUniversalTime()) - _t0Utc).TotalSeconds;
                double y = it.Selector(d);
                if (double.IsFinite(y))
                    newPoints.Add(new SimpleChartViewModel.DataPoint(x, y));
            }
        }

        void Apply()
        {
            var series = Chart.SeriesItems.FirstOrDefault(
                s => string.Equals(s.Key, it.Key, StringComparison.OrdinalIgnoreCase));
            if (series == null) return;
            series.Points.Clear();
            foreach (var pt in newPoints)
                series.Points.Add(pt);
            series.RaiseChanged();
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply, DispatcherPriority.Background);
    }

    // ======================== File I/O commands ========================

    [RelayCommand]
    public async Task LoadFromFileAsync(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            StatusText = "No file path specified.";
            return;
        }
        try
        {
            StatusText = "Loading...";
            byte[] payload = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
            var rows = await Task.Run(() => DeviceLogParser.Parse(payload)).ConfigureAwait(false);
            var cycles = DataLoggerEntryToDeviceData(rows);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Load(cycles);
                int samples = cycles.Sum(c => c.Count);
                StatusText = samples == 0
                    ? "No samples in file."
                    : $"Loaded {samples} samples in {cycles.Count} power cycle(s).";
            }, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                StatusText = "Load failed: " + ex.Message);
        }
    }

    [RelayCommand]
    public async Task SaveToFileAsync(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            StatusText = "No file path specified.";
            return;
        }
        if (_deviceLogBuffer == null || _deviceLogBuffer.Length == 0)
        {
            StatusText = "No data to save (read from device first).";
            return;
        }
        try
        {
            StatusText = "Saving...";
            await File.WriteAllBytesAsync(filePath, _deviceLogBuffer).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
                StatusText = $"Saved {_deviceLogBuffer.Length} bytes.");
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                StatusText = "Save failed: " + ex.Message);
        }
    }

    [RelayCommand]
    public async Task ExportCsvAsync(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            StatusText = "No file path specified.";
            return;
        }

        List<DeviceData> snapshot;
        lock (_gate) { snapshot = _history.ToList(); }

        if (snapshot.Count == 0)
        {
            StatusText = "No data to export (read from device or load a log file first).";
            return;
        }

        try
        {
            StatusText = "Exporting CSV...";
            await Task.Run(() => WriteCsv(filePath!, snapshot)).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
                StatusText = $"Exported {snapshot.Count} rows.");
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                StatusText = "CSV export failed: " + ex.Message);
        }
    }

    private static void WriteCsv(string filePath, IReadOnlyList<DeviceData> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        var headers = new List<string>
        {
            "Timestamp", "Connected", "HW", "FW",
            "SumPowerW", "SumCurrentA",
            "OnboardInC", "OnboardOutC", "Ext1C", "Ext2C"
        };
        for (int i = 1; i <= 6; i++) headers.Add($"V{i}");
        for (int j = 1; j <= 6; j++) headers.Add($"I{j}");

        using var writer = new StreamWriter(filePath, append: false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.WriteLine(string.Join(",", headers));

        foreach (var row in rows)
        {
            var vals = new string[headers.Count];
            int c = 0;
            vals[c++] = row.Timestamp.ToString("o", CultureInfo.InvariantCulture);
            vals[c++] = row.Connected ? "True" : "False";
            vals[c++] = EscapeCsv(row.HardwareRevision ?? "");
            vals[c++] = EscapeCsv(row.FirmwareVersion ?? "");
            vals[c++] = row.SumPowerW.ToString("F3", CultureInfo.InvariantCulture);
            vals[c++] = row.SumCurrentA.ToString("F3", CultureInfo.InvariantCulture);
            vals[c++] = row.OnboardTempInC.ToString("F2", CultureInfo.InvariantCulture);
            vals[c++] = row.OnboardTempOutC.ToString("F2", CultureInfo.InvariantCulture);
            vals[c++] = row.ExternalTemp1C.ToString("F2", CultureInfo.InvariantCulture);
            vals[c++] = row.ExternalTemp2C.ToString("F2", CultureInfo.InvariantCulture);
            for (int k = 0; k < 6; k++)
                vals[c++] = row.PinVoltage[k].ToString("F3", CultureInfo.InvariantCulture);
            for (int l = 0; l < 6; l++)
                vals[c++] = row.PinCurrent[l].ToString("F3", CultureInfo.InvariantCulture);
            writer.WriteLine(string.Join(",", vals));
        }

        static string EscapeCsv(string s) =>
            s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0
                ? s
                : "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    // ======================== Dispose ========================

    public void Dispose()
    {
        Cancel();
    }
}
