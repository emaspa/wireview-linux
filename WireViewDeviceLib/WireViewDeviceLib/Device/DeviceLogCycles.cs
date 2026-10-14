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
/// and advances 4 ms per tick. Feed the entries in flash order.
/// </summary>
public sealed class DeviceLogTimeline
{
    private DateTime _timestamp = DeviceLogCycles.CycleStart;
    private uint _prevTick;

    /// <summary>The time of the last <see cref="DeviceLogStep.Sample"/>.</summary>
    public DateTime Timestamp => _timestamp;

    /// <summary>Takes one entry's Data word (type and 30-bit tick).</summary>
    public DeviceLogStep Next(uint data)
    {
        switch (DeviceLogParser.DecodeType(data))
        {
            case DeviceLogParser.ENTRY_TYPE.ENTRY_TYPE_POWER_ON:
                _timestamp = DeviceLogCycles.CycleStart;
                return DeviceLogStep.Break; // boundary marker, not a sample

            case DeviceLogParser.ENTRY_TYPE.ENTRY_TYPE_MCU_TICK:
                uint tick30 = DeviceLogParser.DecodeTimestamp30(data);
                var step = DeviceLogStep.Sample;
                int delta = (int)(tick30 - _prevTick);
                if (delta < 0)
                {
                    step |= DeviceLogStep.Break;
                    _timestamp = DeviceLogCycles.CycleStart;
                    delta = 0;
                }
                _timestamp = _timestamp.AddMilliseconds(delta * 4);
                _prevTick = tick30;
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
