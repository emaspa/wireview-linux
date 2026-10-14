using WireView2.Device;

namespace WireView2.Mini;

/// <summary>A copy of some of <see cref="HistoryBuffer"/>'s samples, oldest
/// first, in the buffer's layout (six voltages and currents, four temperatures
/// per sample). <paramref name="FirstSeq"/>: the sequence number of the first
/// one (samples ever added before it); with a step, every sample's number is a
/// multiple of it.</summary>
internal sealed record HistorySlice(
    int Count, int Step, long FirstSeq, long[] Ticks, float[] Volts, float[] Amps, float[] Temps,
    byte[] Fan, ushort[] Fault, double[] EnergyJ, string?[] Hw, string?[] Fw)
{
    public static HistorySlice Empty { get; } = new(0, 1, 0, [], [], [], [], [], [], [], [], []);

    /// <summary>Sample <paramref name="i"/>'s time in Unix milliseconds.</summary>
    public long UnixMs(int i) => (Ticks[i] - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond;

    /// <summary>Sample <paramref name="i"/> as the app's <see cref="DeviceData"/>
    /// (for the CSV export, which formats it with the app's code).</summary>
    public DeviceData ToDeviceData(int i)
    {
        var d = new DeviceData
        {
            Timestamp = new DateTime(Ticks[i], DateTimeKind.Utc),
            Connected = true,
            HardwareRevision = Hw[i] ?? "",
            FirmwareVersion = Fw[i] ?? "",
            OnboardTempInC = Temps[i * 4],
            OnboardTempOutC = Temps[i * 4 + 1],
            ExternalTemp1C = Temps[i * 4 + 2],
            ExternalTemp2C = Temps[i * 4 + 3],
            FanDuty = Fan[i],
            FaultStatus = Fault[i],
            EnergyJ = double.IsNaN(EnergyJ[i]) ? null : EnergyJ[i],
        };
        for (int p = 0; p < 6; p++)
        {
            d.PinVoltage[p] = Volts[i * 6 + p];
            d.PinCurrent[p] = Amps[i * 6 + p];
        }
        return d;
    }
}

/// <summary>
/// The last <see cref="Capacity"/> readings of this PC's device, for the control
/// panel's charts and history CSV: a ring of preallocated arrays (the UTC time in
/// ticks, as the device stamped it, the six pin voltages and currents and the four temperatures as
/// floats, the fan duty, the fault status, the energy counter, and references to
/// the hardware and firmware strings, shared while they do not change), so adding
/// a sample allocates nothing. About 99 bytes a sample: 356,400 bytes for an hour
/// at one sample a second. Not persisted. Thread-safe.
/// </summary>
internal sealed class HistoryBuffer
{
    /// <summary>The bytes one sample takes (see the class summary).</summary>
    public const int BytesPerSample = 8 + 6 * 4 + 6 * 4 + 4 * 4 + 1 + 2 + 8 + 2 * 8;

    private readonly object _gate = new();
    private int _capacity;
    private int _start;            // index of the oldest sample
    private int _count;
    private long _seq;             // samples ever added (the next one's number)
    private long _lastTicks = long.MinValue;
    private long[] _time = [];
    private float[] _volts = [], _amps = [], _temps = [];
    private byte[] _fan = [];
    private ushort[] _fault = [];
    private double[] _energy = [];
    private string?[] _hw = [], _fw = [];

    /// <summary>The most samples kept (0: off).</summary>
    public int Capacity
    {
        get { lock (_gate) return _capacity; }
    }

    public int Count
    {
        get { lock (_gate) return _count; }
    }

    /// <summary>The arrays' size in bytes, for the log.</summary>
    public long ResidentBytes => (long)Capacity * BytesPerSample;

    /// <summary>Sets the capacity, keeping the newest samples that fit; 0 frees
    /// the arrays. Does nothing when it does not change.</summary>
    public void Resize(int capacity)
    {
        capacity = Math.Max(0, capacity);
        lock (_gate)
        {
            if (capacity == _capacity) return;
            var keep = capacity == 0 ? HistorySlice.Empty : CopyLocked(Math.Max(0, _count - capacity), _count, 1);
            _capacity = capacity;
            _time = new long[capacity];
            _volts = new float[capacity * 6];
            _amps = new float[capacity * 6];
            _temps = new float[capacity * 4];
            _fan = new byte[capacity];
            _fault = new ushort[capacity];
            _energy = new double[capacity];
            _hw = new string?[capacity];
            _fw = new string?[capacity];
            _start = 0;
            _count = 0;
            long seq = _seq;
            _seq = seq - keep.Count;
            for (int i = 0; i < keep.Count; i++) AddLocked(keep.ToDeviceData(i), keep.Ticks[i]);
            _seq = seq;
        }
    }

    /// <summary>Forgets every sample (another device took over).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _start = 0;
            _count = 0;
            _lastTicks = long.MinValue;
            Array.Clear(_hw);
            Array.Clear(_fw);
        }
    }

    /// <summary>Adds a reading, unless it is not newer than the last one (the same
    /// reading seen on the next tick). False when not added.</summary>
    public bool Add(DeviceData d)
    {
        // The devices stamp their readings with DateTime.UtcNow.
        long ticks = (d.Timestamp.Kind == DateTimeKind.Local ? d.Timestamp.ToUniversalTime() : d.Timestamp).Ticks;
        lock (_gate)
        {
            if (_capacity == 0 || ticks <= _lastTicks) return false;
            AddLocked(d, ticks);
            return true;
        }
    }

    private void AddLocked(DeviceData d, long ticks)
    {
        int slot;
        if (_count < _capacity) slot = (_start + _count++) % _capacity;
        else { slot = _start; _start = (_start + 1) % _capacity; }
        int prev = (slot + _capacity - 1) % _capacity;

        _time[slot] = ticks;
        for (int p = 0; p < 6; p++)
        {
            _volts[slot * 6 + p] = (float)d.PinVoltage[p];
            _amps[slot * 6 + p] = (float)d.PinCurrent[p];
        }
        _temps[slot * 4] = (float)d.OnboardTempInC;
        _temps[slot * 4 + 1] = (float)d.OnboardTempOutC;
        _temps[slot * 4 + 2] = (float)d.ExternalTemp1C;
        _temps[slot * 4 + 3] = (float)d.ExternalTemp2C;
        _fan[slot] = (byte)Math.Clamp(d.FanDuty, 0, 255);
        _fault[slot] = d.FaultStatus;
        _energy[slot] = d.EnergyJ ?? double.NaN;
        // The device hands out a new string with every reading: keep one per value.
        _hw[slot] = _count > 1 && _hw[prev] == d.HardwareRevision ? _hw[prev] : d.HardwareRevision;
        _fw[slot] = _count > 1 && _fw[prev] == d.FirmwareVersion ? _fw[prev] : d.FirmwareVersion;
        _lastTicks = ticks;
        _seq++;
    }

    /// <summary>The samples newer than <paramref name="sinceMs"/> (Unix milliseconds, 0 for all), every
    /// <paramref name="step"/>-th by sequence number (so repeated reads with the
    /// same step line up), the step raised so that at most
    /// <paramref name="maxPoints"/> come back.</summary>
    public HistorySlice Read(long sinceMs, int step, int maxPoints)
    {
        lock (_gate)
        {
            // Samples are in time order: find the first newer one from the end.
            // Whole milliseconds, as the answers carry them: a sample is newer when
            // its millisecond is.
            long since = sinceMs <= 0 ? long.MinValue : DateTime.UnixEpoch.Ticks + (sinceMs + 1) * TimeSpan.TicksPerMillisecond - 1;
            int from = _count;
            while (from > 0 && _time[(_start + from - 1) % Math.Max(1, _capacity)] > since) from--;
            int n = _count - from;
            step = Math.Max(1, step);
            if (maxPoints > 0 && n / step > maxPoints) step = (n + maxPoints - 1) / maxPoints;
            return CopyLocked(from, _count, step);
        }
    }

    /// <summary>Copies samples <paramref name="from"/> (inclusive) to
    /// <paramref name="to"/> (exclusive), counted from the oldest, whose sequence
    /// number is a multiple of <paramref name="step"/>.</summary>
    private HistorySlice CopyLocked(int from, int to, int step)
    {
        if (to <= from || _capacity == 0) return HistorySlice.Empty with { Step = step };
        long seq0 = _seq - _count;        // number of the oldest sample
        long first = seq0 + from;
        long aligned = (first + step - 1) / step * step;
        int n = aligned >= seq0 + to ? 0 : (int)((seq0 + to - 1 - aligned) / step + 1);
        var s = new HistorySlice(n, step, aligned, new long[n], new float[n * 6], new float[n * 6], new float[n * 4],
            new byte[n], new ushort[n], new double[n], new string?[n], new string?[n]);
        for (int k = 0; k < n; k++)
        {
            int slot = (int)((_start + (aligned - seq0) + (long)k * step) % _capacity);
            s.Ticks[k] = _time[slot];
            Array.Copy(_volts, slot * 6, s.Volts, k * 6, 6);
            Array.Copy(_amps, slot * 6, s.Amps, k * 6, 6);
            Array.Copy(_temps, slot * 4, s.Temps, k * 4, 4);
            s.Fan[k] = _fan[slot];
            s.Fault[k] = _fault[slot];
            s.EnergyJ[k] = _energy[slot];
            s.Hw[k] = _hw[slot];
            s.Fw[k] = _fw[slot];
        }
        return s;
    }
}
