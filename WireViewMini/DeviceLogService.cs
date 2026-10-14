using System.Runtime;
using WireView2.Device;
using WireView2.Net;

namespace WireView2.Mini;

/// <summary>What <see cref="DeviceLogService"/> needs from WireView Mini: the
/// local device's state and a way to read its log over the serial port. A test
/// harness can fake it.</summary>
internal interface IDeviceLogHost
{
    /// <summary>The serial port is lent to the GUI (suspendSerial).</summary>
    bool HandoverActive { get; }

    /// <summary>A firmware flash is running.</summary>
    bool FlashRunning { get; }

    /// <summary>A theme transfer uses the serial port.</summary>
    bool ThemeBusy { get; }

    /// <summary>This PC's device (its route tells serial from wireviewd), or null
    /// when there is none or the port is out of use.</summary>
    FlashTarget? LogTarget();

    /// <summary>The raw on-device log (the second 8 MB of the SPI flash), read
    /// on WireView Mini's own port (direct serial) or on the port borrowed from
    /// wireviewd (<see cref="DirectSerialSession"/>, which throws
    /// <see cref="DaemonDeniedException"/> when the user is not in the group).</summary>
    Task<byte[]> ReadLogAsync(IProgress<double> progress, CancellationToken ct);
}

/// <summary>The steps of a device log read; <see cref="Done"/> and
/// <see cref="Failed"/> are final until the next one.</summary>
internal enum DeviceLogPhase { Idle, Reading, Parsing, Done, Failed }

/// <summary>A device log read's state, for the panel.</summary>
/// <param name="Progress">0 to 1 while reading.</param>
/// <param name="Message">The status line, null while idle.</param>
internal sealed record DeviceLogState(DeviceLogPhase Phase, double Progress, string? Message)
{
    public static DeviceLogState Idle { get; } = new(DeviceLogPhase.Idle, 0, null);

    public bool Running => Phase is DeviceLogPhase.Reading or DeviceLogPhase.Parsing;
}

/// <summary>A read request's answer: 202 started, else the refusal's status and message.</summary>
internal readonly record struct DeviceLogStart(int Status, string? Error, DeviceLogState State);

/// <summary>One power cycle of a <see cref="DeviceLogStore"/>: its samples are
/// <paramref name="First"/> to <paramref name="First"/> + <paramref name="Count"/> - 1.</summary>
internal readonly record struct DeviceLogCycle(int Index, int First, int Count, DateTime Start, DateTime End)
{
    public TimeSpan Duration => End >= Start ? End - Start : TimeSpan.Zero;

    /// <summary>The Logging page's label.</summary>
    public string Label => DeviceLogCycles.Label(Index, Count, Duration);
}

/// <summary>
/// The on-device log as the Logging page splits it (<see cref="DeviceLogTimeline"/>),
/// kept compactly: per sample its time (4 ms steps since the cycle's synthetic
/// start) and the 16 logged bytes (four temperatures in whole degrees, six
/// voltages in 100 mV, six currents in 100 mA), 20 bytes in all. A full log (the
/// 8 MB log area holds at most 393,216 entries) is 7.5 MB; the app keeps the same
/// log as objects, several times that. Samples come back as the app's
/// <see cref="DeviceData"/>, through the shared conversion, for the CSV export.
/// </summary>
internal sealed class DeviceLogStore
{
    private const int Bytes = 16;               // temps 4, volts 6, amps 6
    private const long TicksPer4Ms = 4 * TimeSpan.TicksPerMillisecond;

    private readonly int[] _time;               // 4 ms steps since DeviceLogCycles.CycleStart
    private readonly byte[] _data;
    private readonly DeviceLogCycle[] _cycles;

    private DeviceLogStore(int[] time, byte[] data, DeviceLogCycle[] cycles, int rawBytes)
    {
        _time = time;
        _data = data;
        _cycles = cycles;
        RawBytes = rawBytes;
        ReadAt = DateTime.UtcNow;
    }

    public int Samples => _time.Length;
    public IReadOnlyList<DeviceLogCycle> Cycles => _cycles;
    public DateTime ReadAt { get; }
    /// <summary>The size of the log area read from the device.</summary>
    public int RawBytes { get; }
    /// <summary>What this store keeps in memory.</summary>
    public long ResidentBytes => (long)_time.Length * (4 + Bytes) + _cycles.Length * 40L;

    /// <summary>Splits a raw log read (<see cref="WireViewPro2Device.ReadDeviceLogAsync"/>)
    /// into cycles. Two passes over the bytes, so the arrays are allocated once at
    /// their final size.</summary>
    public static DeviceLogStore Build(byte[] raw)
    {
        int samples = 0;
        var timeline = new DeviceLogTimeline();
        DeviceLogParser.Scan(raw, entry =>
        {
            if ((timeline.Next(DeviceLogParser.ReadData(entry)) & DeviceLogStep.Sample) != 0) samples++;
        });

        var time = new int[samples];
        var data = new byte[samples * Bytes];
        var cycles = new List<DeviceLogCycle>();
        int n = 0, first = 0;
        timeline = new DeviceLogTimeline();
        void Close()
        {
            if (n > first)
                cycles.Add(new DeviceLogCycle(cycles.Count, first, n - first, TimeAt(time[first]), TimeAt(time[n - 1])));
            first = n;
        }
        DeviceLogParser.Scan(raw, entry =>
        {
            var step = timeline.Next(DeviceLogParser.ReadData(entry));
            if ((step & DeviceLogStep.Break) != 0) Close();
            if ((step & DeviceLogStep.Sample) == 0 || n >= samples) return;
            long steps = (timeline.Timestamp - DeviceLogCycles.CycleStart).Ticks / TicksPer4Ms;
            time[n] = (int)Math.Clamp(steps, int.MinValue, int.MaxValue);
            entry.Slice(DeviceLogParser.TsOffset, Bytes).CopyTo(data.AsSpan(n * Bytes, Bytes));
            n++;
        });
        Close();
        return new DeviceLogStore(time, data, cycles.ToArray(), raw.Length);
    }

    private static DateTime TimeAt(int steps) => DeviceLogCycles.CycleStart.AddTicks(steps * TicksPer4Ms);

    /// <summary>Sample <paramref name="i"/>'s synthetic time.</summary>
    public DateTime Time(int i) => TimeAt(_time[i]);

    /// <summary>Sample <paramref name="i"/>'s logged bytes: temperatures (4),
    /// voltages (6, 100 mV), currents (6, 100 mA).</summary>
    public ReadOnlySpan<byte> Raw(int i) => _data.AsSpan(i * Bytes, Bytes);

    /// <summary>Sample <paramref name="i"/> as the Logging page has it.</summary>
    public DeviceData ToDeviceData(int i)
    {
        var raw = Raw(i);
        return DeviceLogCycles.ToDeviceData(Time(i), raw[..4], raw.Slice(4, 6), raw.Slice(10, 6));
    }
}

/// <summary>
/// The control panel's device log, the app's Logging page read (LoggingViewModel)
/// without the chart, which the page draws: on request, reads the log from the
/// device's SPI flash over the serial port (WireView Mini's own port in direct
/// serial mode, borrowed from wireviewd in daemon mode, as the app does), splits
/// it into power cycles with the app's code and keeps the result in memory until
/// the next read or the end of WireView Mini. One read at a time; refused while a
/// firmware flash or a theme transfer runs or the app holds the port. Nothing
/// here runs until asked.
/// </summary>
internal sealed class DeviceLogService
{
    private readonly IDeviceLogHost _host;
    private readonly object _lock = new();   // _state, _generation, _task, _log
    private DeviceLogState _state = DeviceLogState.Idle;
    private int _generation;
    private Task? _task;
    private DeviceLogStore? _log;
    private int _busy;                       // 1 while a read uses the port

    public DeviceLogService(IDeviceLogHost host) => _host = host;

    public DeviceLogState State
    {
        get { lock (_lock) return _state; }
    }

    /// <summary>A read is using the serial port (the app's handover, a theme
    /// transfer and a firmware flash are refused meanwhile).</summary>
    public bool Busy => Volatile.Read(ref _busy) != 0;

    /// <summary>The last log read, null before the first.</summary>
    public DeviceLogStore? Log
    {
        get { lock (_lock) return _log; }
    }

    /// <summary>The read task (for a test harness to wait on), null before the first.</summary>
    internal Task? CurrentTask
    {
        get { lock (_lock) return _task; }
    }

    /// <summary>Starts a read: 202 (the progress is in /api/status), 409 busy,
    /// 503 no device or a read-only one.</summary>
    public DeviceLogStart Start()
    {
        if (_host.FlashRunning) return Refused(409, "A firmware update is running. Try again when it has finished.");
        if (_host.HandoverActive) return Refused(409, "The serial port is lent to the WireView app. Try again when it hands the port back.");
        if (_host.ThemeBusy) return Refused(409, "A theme transfer is using the serial port. Try again when it has finished.");
        var target = _host.LogTarget();
        if (target == null || !target.Connected) return Refused(503, "Not connected.");
        if (target.Route == FlashRoute.ReadOnly)
            return Refused(503, "The device is reached through hwmon without wireviewd, which is read only. Start wireviewd and try again.");
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return Refused(409, "The device log is being read. Try again when it has finished.");
        // A theme transfer that started in between keeps the port.
        if (_host.ThemeBusy)
        {
            Volatile.Write(ref _busy, 0);
            return Refused(409, "A theme transfer is using the serial port. Try again when it has finished.");
        }

        FileLog.Info($"device log: read starting ({target.Route} route)");
        lock (_lock)
        {
            int generation = ++_generation;
            // The app's status line while it reads.
            _state = new DeviceLogState(DeviceLogPhase.Reading, 0, "Reading history from SPI flash...");
            _task = Task.Run(async () =>
            {
                try { await ReadAsync(generation).ConfigureAwait(false); }
                catch (Exception ex) { Fail(generation, "Read failed: " + ex.Message); }
                finally { Volatile.Write(ref _busy, 0); }
            });
            return new DeviceLogStart(202, null, _state);
        }
    }

    private async Task ReadAsync(int generation)
    {
        var started = DateTime.UtcNow;
        byte[] raw;
        try
        {
            raw = await _host.ReadLogAsync(new DirectProgress(v => SetProgress(generation, v)), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // As the app: "Read failed: " and the reason (the group hint when wireviewd refuses).
            Fail(generation, "Read failed: " + ex.Message);
            return;
        }
        Set(generation, DeviceLogPhase.Parsing, 1, "Splitting the log into power cycles...");
        var log = DeviceLogStore.Build(raw);
        raw = [];
        lock (_lock)
        {
            if (generation != _generation) return;
            _log = log;
        }
        // The 8 MB read buffer, the 32,768 page buffers of the read and the previous
        // log are garbage now. An aggressive collection decommits what the read made
        // the heap grow to (a forced one leaves it resident, about 19 MB measured),
        // so a read once in a while costs nothing afterwards.
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        FileLog.Info($"device log: {log.Samples} samples in {log.Cycles.Count} power cycle(s), " +
                     $"{log.RawBytes / 1024} KB read in {(DateTime.UtcNow - started).TotalSeconds:0.0} s, {log.ResidentBytes / 1024} KB kept");
        Set(generation, DeviceLogPhase.Done, 1, log.Samples == 0
            ? "No history found."
            : $"Loaded {log.Samples} samples in {log.Cycles.Count} power cycle(s).");
    }

    private DeviceLogStart Refused(int status, string message)
    {
        if (status != 409) FileLog.Warn($"device log: refused: {message}");
        return new DeviceLogStart(status, message, State);
    }

    private void Set(int generation, DeviceLogPhase phase, double progress, string? message)
    {
        lock (_lock)
        {
            if (generation != _generation) return;
            _state = new DeviceLogState(phase, Math.Clamp(progress, 0, 1), message);
        }
    }

    private void SetProgress(int generation, double progress)
    {
        lock (_lock)
        {
            if (generation != _generation || _state.Phase != DeviceLogPhase.Reading) return;
            _state = _state with { Progress = Math.Clamp(progress, 0, 1) };
        }
    }

    private void Fail(int generation, string message)
    {
        FileLog.Warn($"device log: {message}");
        Set(generation, DeviceLogPhase.Failed, State.Progress, message);
    }

    /// <summary>Reports on the reporting thread: <see cref="Progress{T}"/> would
    /// post to the thread pool, which can reorder the reports.</summary>
    private sealed class DirectProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
