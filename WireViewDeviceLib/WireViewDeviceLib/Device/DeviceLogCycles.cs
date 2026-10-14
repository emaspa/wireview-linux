namespace WireView2.Device;

/// <summary>What one log entry means for <see cref="DeviceLogTimeline"/>'s caller.</summary>
[Flags]
public enum DeviceLogStep
{
    /// <summary>Not a sample, no boundary (system time, empty).</summary>
    None = 0,
    /// <summary>A power cycle ends before this entry (when it has samples).</summary>
    Break = 1,
    /// <summary>A sample, at <see cref="DeviceLogTimeline.Timestamp"/>.</summary>
    Sample = 2,
}

/// <summary>
/// The Logging page's reading of the on-device log's clock, shared with WireView
/// Mini's control panel. A cycle ends at an explicit POWER_ON marker or when the
/// MCU tick counter wraps backwards (the counter resets on power-up). Timestamps
/// are synthetic: each cycle restarts at <see cref="DeviceLogCycles.CycleStart"/>
/// with its first sample, and advances 4 ms per tick from there. Feed the
/// entries in flash order.
/// </summary>
public sealed class DeviceLogTimeline
{
    private DateTime _timestamp = DeviceLogCycles.CycleStart;
    private uint _prevTick;
    // Samples placed since the cycle started: 0 before its first, 1 after it.
    // The first sample of a cycle is the origin whatever its tick says, and the
    // second may re-base (see MaxGapTicks).
    private int _placed;

    /// <summary>The longest gap taken as time between two samples of one cycle:
    /// one hour at 4 ms a tick. The device logs a sample a minute. Its first
    /// sample after power-up carries the time since boot (about a second), and
    /// from the second sample on the tick is its cumulative operating time, so
    /// the second sample jumps by that total (days). That jump is a change of
    /// counter, not time, and the cycle re-bases on it; a jump this long later in
    /// a cycle is a power cycle whose boot sample is missing.</summary>
    public const int MaxGapTicks = 60 * 60 * 1000 / 4;

    /// <summary>The time of the last <see cref="DeviceLogStep.Sample"/>.</summary>
    public DateTime Timestamp => _timestamp;

    /// <summary>Takes one entry's Data word (type and 30-bit tick).</summary>
    public DeviceLogStep Next(uint data)
    {
        switch (DeviceLogParser.DecodeType(data))
        {
            case DeviceLogParser.ENTRY_TYPE.ENTRY_TYPE_POWER_ON:
                _timestamp = DeviceLogCycles.CycleStart;
                _placed = 0;
                return DeviceLogStep.Break; // boundary marker, not a sample

            case DeviceLogParser.ENTRY_TYPE.ENTRY_TYPE_MCU_TICK:
                uint tick30 = DeviceLogParser.DecodeTimestamp30(data);
                var step = DeviceLogStep.Sample;
                int delta = _placed == 0 ? 0 : (int)(tick30 - _prevTick);
                if (delta < 0 || (delta > MaxGapTicks && _placed > 1))
                {
                    // The counter went back (power-up), or jumped with no boot
                    // sample before it: a new cycle, this sample its origin.
                    step |= DeviceLogStep.Break;
                    _timestamp = DeviceLogCycles.CycleStart;
                    _placed = 0;
                    delta = 0;
                }
                else if (delta > MaxGapTicks)
                {
                    // The second sample: the counter changed from time since
                    // boot to cumulative operating time. Same instant as far as
                    // the cycle's clock knows.
                    delta = 0;
                }
                _timestamp = _timestamp.AddMilliseconds(delta * 4);
                _prevTick = tick30;
                _placed++;
                return step;

            default: // SYSTEM_TIME, EMPTY
                return DeviceLogStep.None;
        }
    }
}

/// <summary>The on-device log as the Logging page shows it: power cycles of
/// <see cref="DeviceData"/> samples (shared with WireView Mini's control panel).</summary>
public static class DeviceLogCycles
{
    /// <summary>Where every cycle's synthetic clock starts (the log has no wall
    /// clock): 2026-01-01 00:00, kind unspecified, as the Logging page always had it.</summary>
    public static readonly DateTime CycleStart = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    /// <summary>The Logging page's sample: temperatures in whole degrees, voltages
    /// and currents in 100 mV and 100 mA steps; everything else at its default
    /// (not connected, hardware A0, firmware 0.0.0), which is what its CSV export shows.</summary>
    public static DeviceData ToDeviceData(DateTime timestamp, ReadOnlySpan<byte> temps, ReadOnlySpan<byte> volts, ReadOnlySpan<byte> amps)
    {
        var dd = new DeviceData
        {
            Timestamp = timestamp,
            OnboardTempInC = temps[0],
            OnboardTempOutC = temps[1],
            ExternalTemp1C = temps[2],
            ExternalTemp2C = temps[3],
        };
        for (int i = 0; i < 6; i++)
        {
            dd.PinVoltage[i] = (float)volts[i] / 10f;
            dd.PinCurrent[i] = (float)amps[i] / 10f;
        }
        return dd;
    }

    /// <summary>A sample from an entry's raw bytes (<see cref="DeviceLogParser.Scan"/>).</summary>
    public static DeviceData ToDeviceData(DateTime timestamp, ReadOnlySpan<byte> entry) =>
        ToDeviceData(timestamp,
            entry.Slice(DeviceLogParser.TsOffset, 4),
            entry.Slice(DeviceLogParser.VoltageOffset, 6),
            entry.Slice(DeviceLogParser.CurrentOffset, 6));

    /// <summary>Parsed entries to per-power-cycle sample lists (no empty cycle).</summary>
    public static List<List<DeviceData>> ToCycles(IReadOnlyList<DeviceLogParser.DATALOGGER_Entry> entries)
    {
        var cycles = new List<List<DeviceData>>();
        var current = new List<DeviceData>();
        var timeline = new DeviceLogTimeline();

        foreach (var entry in entries)
        {
            var step = timeline.Next(entry.Data);
            if ((step & DeviceLogStep.Break) != 0 && current.Count > 0)
            {
                cycles.Add(current);
                current = new List<DeviceData>();
            }
            if ((step & DeviceLogStep.Sample) == 0) continue;
            current.Add(ToDeviceData(timeline.Timestamp, entry.Ts, entry.Voltage, entry.Current));
        }

        if (current.Count > 0)
            cycles.Add(current);
        return cycles;
    }

    /// <summary>The Logging page's cycle label: "Cycle #3 (1200 samples, 1:40)".</summary>
    public static string Label(int index, int sampleCount, TimeSpan? duration)
    {
        if (sampleCount == 0) return $"Cycle #{index + 1} (empty)";
        string text = duration is { } d
            ? (d.TotalHours >= 1
                ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}"
                : $"{d.Minutes}:{d.Seconds:00}")
            : "?";
        return $"Cycle #{index + 1} ({sampleCount} samples, {text})";
    }
}
