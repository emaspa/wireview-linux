using System;
using System.Collections.Generic;

namespace WireView2.Device
{
    /// <summary>What <see cref="FaultMonitor.Observe"/> made of one reading.</summary>
    /// <param name="Status">Active fault bits to show (0 while the debounce holds a
    /// fresh fault back for one poll).</param>
    /// <param name="Log">Fault-log bits to show, debounced with <paramref name="Status"/>.</param>
    /// <param name="Raised">True on the reading where an alert should fire: the shown
    /// status went from clear to faulted and the cooldown since the last alert has
    /// passed. Callers show the notification and decide on shutdown from this.</param>
    public readonly record struct FaultEvent(ushort Status, ushort Log, bool Raised)
    {
        /// <summary>Notification text for <see cref="Status"/> (see <see cref="FaultMonitor.FormatFaults"/>).</summary>
        public string Text => FaultMonitor.FormatFaults(Status);
    }

    /// <summary>
    /// Fault edge detection for one device's reading stream, shared by the GUI and
    /// the WireView Mini so both alert on exactly the same readings.
    ///
    /// Debounce: the serial protocol has no framing/CRC, so a desynced read can
    /// pulse garbage into the fault fields for a single poll. Real faults persist
    /// until cleared (the device latches them in the fault log), so only fault bits
    /// seen on two consecutive polls are honoured. An alert is raised when the
    /// shown status goes from 0 to non-zero, at most once per <see cref="Cooldown"/>.
    /// Not thread-safe: feed one device's readings from one thread.
    /// </summary>
    public sealed class FaultMonitor
    {
        /// <summary>Minimum time between two alerts (the GUI's toast cooldown).</summary>
        public static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(10.0);

        private readonly Func<DateTime> _utcNow;
        private bool _prevRawFaultAny;
        private ushort _lastShownFaultStatus;
        private DateTime _lastRaisedUtc = DateTime.MinValue;

        /// <param name="cooldown">Minimum time between alerts; <see cref="DefaultCooldown"/> when null.</param>
        /// <param name="utcNow">Clock, for tests; <see cref="DateTime.UtcNow"/> when null.</param>
        public FaultMonitor(TimeSpan? cooldown = null, Func<DateTime>? utcNow = null)
        {
            Cooldown = cooldown ?? DefaultCooldown;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public TimeSpan Cooldown { get; }

        /// <summary>Feeds one reading; returns the fault state to show and whether
        /// an alert should fire now.</summary>
        public FaultEvent Observe(DeviceData data)
        {
            ushort rawStatus = data.FaultStatus;
            ushort rawLog = data.FaultLog;
            bool prevRawAny = _prevRawFaultAny;
            _prevRawFaultAny = (rawStatus | rawLog) != 0;
            if ((rawStatus | rawLog) != 0 && !prevRawAny)
            {
                rawStatus = 0;
                rawLog = 0;
            }

            ushort prevShown = _lastShownFaultStatus;
            _lastShownFaultStatus = rawStatus;

            bool raised = false;
            if (prevShown == 0 && rawStatus != 0)
            {
                DateTime now = _utcNow();
                if (now - _lastRaisedUtc > Cooldown)
                {
                    _lastRaisedUtc = now;
                    raised = true;
                }
            }
            return new FaultEvent(rawStatus, rawLog, raised);
        }

        /// <summary>Notification and banner text for an active-fault mask, e.g.
        /// "FAULT: Over-Current, Over-Power". 0xFFFF is hwmon's "faulted, no
        /// details" marker.</summary>
        public static string FormatFaults(ushort faultStatus)
        {
            if (faultStatus == 0xFFFF)
                return "FAULT (details unavailable via hwmon)";

            var parts = new List<string>();
            foreach (var fault in Enum.GetValues<WireViewPro2Device.FAULT>())
            {
                int bit = 1 << (int)fault;
                if ((faultStatus & bit) != 0)
                    parts.Add(FriendlyFaultName(fault));
            }
            return parts.Count > 0
                ? "FAULT: " + string.Join(", ", parts)
                : "FAULT";
        }

        /// <summary>User-facing name of one fault bit.</summary>
        public static string FriendlyFaultName(WireViewPro2Device.FAULT f) => f switch
        {
            WireViewPro2Device.FAULT.FAULT_OTP_TCHIP      => "Chip Temperature",
            WireViewPro2Device.FAULT.FAULT_OTP_TS          => "Connector Temperature",
            WireViewPro2Device.FAULT.FAULT_OCP             => "Over-Current",
            WireViewPro2Device.FAULT.FAULT_WIRE_OCP        => "Wire Over-Current",
            WireViewPro2Device.FAULT.FAULT_OPP             => "Over-Power",
            WireViewPro2Device.FAULT.FAULT_CURRENT_IMBALANCE => "Current Imbalance",
            _ => f.ToString(),
        };
    }
}
