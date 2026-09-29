using WireView2.Device;

namespace WireView2.Services;

/// <summary>Which of the four temperature sensors (onboard in/out, external 1/2)
/// are connected, for hiding absent ones (upstream 1.0.8 hides them on the
/// Overview; the port hides them in every list of temperatures).
///
/// An absent sensor reads about -3276.8 °C over serial and NaN through hwmon. The
/// first sample after <see cref="Reset"/> decides at once; after that a sensor
/// reappears on its first valid reading and disappears only after
/// <see cref="MissesToHide"/> invalid readings in a row, so one bad frame does not
/// make the layout jump. Thread-safe.</summary>
public sealed class TemperatureSensorPresence
{
    public const int Count = 4;
    private const int MissesToHide = 3;

    private readonly object _gate = new();
    private readonly bool[] _present = { true, true, true, true };
    private readonly int[] _misses = new int[Count];
    private bool _fresh = true;

    public static bool IsValid(double tempC) => tempC > -100.0 && tempC < 200.0;

    /// <summary>The reading, or NaN when the sensor reports no temperature.</summary>
    public static double ValueOrNaN(double tempC) => IsValid(tempC) ? tempC : double.NaN;

    public static double Read(DeviceData d, int sensor) => sensor switch
    {
        0 => d.OnboardTempInC,
        1 => d.OnboardTempOutC,
        2 => d.ExternalTemp1C,
        _ => d.ExternalTemp2C,
    };

    public bool IsPresent(int sensor)
    {
        lock (_gate) return _present[sensor];
    }

    /// <summary>Back to "all present, undecided" (connect, disconnect, device switch).
    /// Returns true when a sensor's presence changed.</summary>
    public bool Reset()
    {
        lock (_gate)
        {
            bool changed = false;
            for (int i = 0; i < Count; i++)
            {
                changed |= !_present[i];
                _present[i] = true;
                _misses[i] = 0;
            }
            _fresh = true;
            return changed;
        }
    }

    /// <summary>Feeds one sample. Returns true when a sensor's presence changed.</summary>
    public bool Update(DeviceData d)
    {
        lock (_gate)
        {
            bool changed = false;
            for (int i = 0; i < Count; i++)
            {
                if (IsValid(Read(d, i)))
                {
                    _misses[i] = 0;
                    if (!_present[i]) { _present[i] = true; changed = true; }
                }
                else
                {
                    _misses[i]++;
                    if (_present[i] && (_fresh || _misses[i] >= MissesToHide))
                    {
                        _present[i] = false;
                        changed = true;
                    }
                }
            }
            _fresh = false;
            return changed;
        }
    }
}
