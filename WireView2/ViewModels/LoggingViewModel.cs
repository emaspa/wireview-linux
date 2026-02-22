using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
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
    private byte[]? _deviceLogBuffer;

    // ======================== Properties ========================

    public ObservableCollection<ISeries> Series { get; } = new();
    public Axis[] XAxes { get; }
    public Axis[] YAxes { get; }

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

        XAxes = new Axis[]
        {
            new Axis
            {
                UnitWidth = 1.0,
                MinStep = 1.0,
                Labeler = v => (double.IsNaN(v) || double.IsInfinity(v))
                    ? string.Empty
                    : TimeZoneInfo.ConvertTimeFromUtc(_t0Utc.AddSeconds(v), TimeZoneInfo.Local)
                        .ToString("MM-dd HH:mm:ss")
            }
        };

        YAxes = new Axis[]
        {
            new Axis { Name = "V" },
            new Axis { Name = "A" },
            new Axis { Name = "W" },
            new Axis { Name = "\u00b0C" }
        };

        YV.LimitsChanged += delegate { ApplyAxisLimits(0, YV); };
        YA.LimitsChanged += delegate { ApplyAxisLimits(1, YA); };
        YW.LimitsChanged += delegate { ApplyAxisLimits(2, YW); };
        YC.LimitsChanged += delegate { ApplyAxisLimits(3, YC); };

        ApplyAxisLimits(0, YV);
        ApplyAxisLimits(1, YA);
        ApplyAxisLimits(2, YW);
        ApplyAxisLimits(3, YC);

        BuildItems();

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
                    if (_history.Count > 0)
                        RebuildSeriesPointsFor(it);
                }
                else
                {
                    Series.Remove(it.Series);
                    lock (_gate) { it.Points.Clear(); }
                }
                UpdateYAxisVisibility();
            };
        }

        UpdateYAxisVisibility();
        _connector.Start();
    }

    // ======================== Axis helpers ========================

    private void UpdateYAxisVisibility()
    {
        if (YAxes is not { Length: >= 4 }) return;
        YAxes[0].IsVisible = AnyEnabledAt(0);
        YAxes[1].IsVisible = AnyEnabledAt(1);
        YAxes[2].IsVisible = AnyEnabledAt(2);
        YAxes[3].IsVisible = AnyEnabledAt(3);

        bool AnyEnabledAt(int idx) =>
            Items.Any(it => it.IsEnabled && it.YAxisIndex == idx);
    }

    private void ApplyAxisLimits(int axisIndex, MonitoringViewModel.AxisConfig cfg)
    {
        var axis = YAxes[axisIndex];
        if (cfg.Auto)
        {
            axis.MinLimit = 0.0;
            axis.MaxLimit = null;
        }
        else
        {
            axis.MinLimit = cfg.Min;
            axis.MaxLimit = cfg.Max > cfg.Min ? cfg.Max : cfg.Min + 1e-9;
        }
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
        if (_connector.Device is not WireViewPro2Device { Connected: true } device)
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
            _deviceLogBuffer = await device.ReadDeviceLogAsync(progress, token)
                .ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var entries = DeviceLogParser.Parse(_deviceLogBuffer);
                var list = DataLoggerEntryToDeviceData(entries);
                Load(list);
                StatusText = list.Count == 0
                    ? "No history found."
                    : $"Loaded {list.Count} samples.";
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

    private List<DeviceData> DataLoggerEntryToDeviceData(
        IReadOnlyList<DeviceLogParser.DATALOGGER_Entry> entries)
    {
        var list = new List<DeviceData>();
        DateTime timestamp = DateTime.Parse("2026-01-01 00:00");
        uint prevTick = 0u;

        foreach (var entry in entries)
        {
            var entryType = DeviceLogParser.DecodeType(entry.Data);
            uint tick30 = DeviceLogParser.DecodeTimestamp30(entry.Data);

            switch (entryType)
            {
                case DeviceLogParser.ENTRY_TYPE.ENTRY_TYPE_POWER_ON:
                    timestamp = timestamp.AddDays(1.0);
                    timestamp = timestamp.Subtract(timestamp.TimeOfDay);
                    break;

                case DeviceLogParser.ENTRY_TYPE.ENTRY_TYPE_MCU_TICK:
                    int delta = (int)(tick30 - prevTick);
                    if (delta < 0)
                    {
                        timestamp = timestamp.AddDays(1.0);
                        timestamp = timestamp.Subtract(timestamp.TimeOfDay);
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

            list.Add(dd);
        }
        return list;
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void Clear()
    {
        ClearUiAndHistory();
        _t0Utc = DateTime.Parse("2026-01-01 00:00");
        XAxes[0].MinLimit = null;
        XAxes[0].MaxLimit = null;
        StatusText = "Cleared.";
        UpdateYAxisVisibility();
    }

    private void ClearUiAndHistory()
    {
        lock (_gate)
        {
            _history.Clear();
            foreach (var item in Items)
                item.Points.Clear();
        }
    }

    private void Load(IReadOnlyList<DeviceData>? rows)
    {
        ClearUiAndHistory();
        if (rows == null || rows.Count == 0)
        {
            UpdateYAxisVisibility();
            return;
        }

        var sorted = rows.ToList();
        _t0Utc = sorted[0].Timestamp.Kind == DateTimeKind.Utc
            ? sorted[0].Timestamp
            : sorted[0].Timestamp.ToUniversalTime();

        var lastTs = sorted[^1].Timestamp.Kind == DateTimeKind.Utc
            ? sorted[^1].Timestamp
            : sorted[^1].Timestamp.ToUniversalTime();

        double span = (lastTs - _t0Utc).TotalSeconds;
        XAxes[0].MinLimit = 0.0;
        XAxes[0].MaxLimit = Math.Max(1.0, span);

        lock (_gate) { _history.AddRange(sorted); }

        foreach (var item in Items.Where(i => i.IsEnabled))
            RebuildSeriesPointsFor(item);

        UpdateYAxisVisibility();
    }

    private void RebuildSeriesPointsFor(MonitoringViewModel.TelemetryItem it)
    {
        List<ObservablePoint> newPoints;
        lock (_gate)
        {
            newPoints = new List<ObservablePoint>(_history.Count + 16);
            foreach (var d in _history)
            {
                double x = ((d.Timestamp.Kind == DateTimeKind.Utc ? d.Timestamp : d.Timestamp.ToUniversalTime()) - _t0Utc).TotalSeconds;
                double y = it.Selector(d);
                if (double.IsFinite(y))
                    newPoints.Add(new ObservablePoint(x, y));
            }
        }

        Dispatcher.UIThread.Post(() =>
        {
            lock (_gate)
            {
                it.Points.Clear();
                foreach (var pt in newPoints)
                    it.Points.Add(pt);
            }
        }, DispatcherPriority.Background);
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
            var rowsDeviceData = DataLoggerEntryToDeviceData(rows);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Load(rowsDeviceData);
                StatusText = rows.Count == 0
                    ? "No samples in file."
                    : $"Loaded {rows.Count} samples.";
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
