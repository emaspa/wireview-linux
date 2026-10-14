using System.Text.Json.Serialization;
using WireView2.Device;

namespace WireView2.Net
{
    /// <summary>
    /// Wire schema for a single WireView device, published over the LAN at
    /// <c>GET /sensors</c>. Mirrors <see cref="DeviceData"/> plus identity so a
    /// remote <c>NetworkDevice</c> can reconstruct it. Read-only — no commands.
    /// </summary>
    public sealed class WireViewSensorDto
    {
        /// <summary>Stable 12-byte chip UID (hex). Used as the network-wide device key.</summary>
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Connected { get; set; }
        public string HwRev { get; set; } = "";
        public string FwVer { get; set; } = "";
        /// <summary>Firmware build string (e.g. "TG-WV-PRO2-FW_20260225_1902"), empty if unknown.</summary>
        public string BuildString { get; set; } = "";
        public DateTime Timestamp { get; set; }

        public double[] PinVoltage { get; set; } = new double[6];
        public double[] PinCurrent { get; set; } = new double[6];

        public double TempInC { get; set; }
        public double TempOutC { get; set; }
        public double Ext1C { get; set; }
        public double Ext2C { get; set; }

        public int PsuCapW { get; set; }
        public int FaultStatus { get; set; }
        public int FaultLog { get; set; }
        public int Fan { get; set; }   // live fan duty %

        /// <summary>Energy in joules since wireviewd started (hwmon energy1_input);
        /// the daemon's own /sensors carries it too. Omitted when unknown (direct
        /// serial devices, older daemons and app versions).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? EnergyJ { get; set; }

        // Convenience totals (also recomputable from the pin arrays).
        public double SumCurrentA { get; set; }
        public double SumPowerW { get; set; }

        public static WireViewSensorDto FromDevice(IWireViewDevice dev, DeviceData d) => new()
        {
            Id = dev.UniqueId,
            // The edition name ("WireView Pro II" / "... Noctua Edition"), as
            // wireviewd publishes it; the transport suffix of DeviceName is local.
            Name = WireViewEditions.DisplayName(dev.Edition),
            Connected = d.Connected,
            // Upstream's "EF05"/"EF06"; the device's own identity, not the per-sample
            // copy (which older builds left at the "A0" default for hwmon).
            HwRev = string.IsNullOrEmpty(dev.HardwareRevision) ? d.HardwareRevision : dev.HardwareRevision,
            FwVer = d.FirmwareVersion,
            BuildString = dev.BuildString,
            Timestamp = d.Timestamp,
            PinVoltage = d.PinVoltage.Select(v => Finite(v, 0.0)).ToArray(),
            PinCurrent = d.PinCurrent.Select(v => Finite(v, 0.0)).ToArray(),
            TempInC = Finite(d.OnboardTempInC, AbsentTempC),
            TempOutC = Finite(d.OnboardTempOutC, AbsentTempC),
            Ext1C = Finite(d.ExternalTemp1C, AbsentTempC),
            Ext2C = Finite(d.ExternalTemp2C, AbsentTempC),
            PsuCapW = d.PsuCapabilityW,
            FaultStatus = d.FaultStatus,
            FaultLog = d.FaultLog,
            Fan = d.FanDuty,
            EnergyJ = d.EnergyJ is { } e && double.IsFinite(e) ? e : null,
            SumCurrentA = Finite(d.SumCurrentA, 0.0),
            SumPowerW = Finite(d.SumPowerW, 0.0),
        };

        /// <summary>How an absent temperature probe goes on the wire: wireviewd's
        /// convention, which readers already treat as "no probe".</summary>
        public const double AbsentTempC = -100.0;

        // JSON has no NaN or infinity (System.Text.Json refuses to write them);
        // hwmon reports an absent probe as NaN.
        private static double Finite(double value, double absent) => double.IsFinite(value) ? value : absent;

        /// <summary>Reconstruct a <see cref="DeviceData"/> from this DTO (used by NetworkDevice in Phase 3).</summary>
        public DeviceData ToDeviceData() => new()
        {
            Timestamp = Timestamp,
            Connected = Connected,
            HardwareRevision = HwRev,
            FirmwareVersion = FwVer,
            PinVoltage = PinVoltage.Length == 6 ? (double[])PinVoltage.Clone() : new double[6],
            PinCurrent = PinCurrent.Length == 6 ? (double[])PinCurrent.Clone() : new double[6],
            OnboardTempInC = TempInC,
            OnboardTempOutC = TempOutC,
            ExternalTemp1C = Ext1C,
            ExternalTemp2C = Ext2C,
            PsuCapabilityW = PsuCapW,
            FaultStatus = (ushort)FaultStatus,
            FaultLog = (ushort)FaultLog,
            FanDuty = Fan,
            EnergyJ = EnergyJ,
        };
    }

    /// <summary>Full <c>GET /sensors</c> payload: this host and all its WireView devices.</summary>
    public sealed class WireViewHostSnapshot
    {
        public string Host { get; set; } = "";
        public string AppVersion { get; set; } = "";
        public List<WireViewSensorDto> Devices { get; set; } = new();

        /// <summary>WireView Mini's version when the publisher is wireview-mini;
        /// absent for the GUI and wireviewd. The GUI's localhost probe uses it to tell
        /// WireView Mini from any other publisher on the port.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Mini { get; set; }

        /// <summary>wireview-mini only: true while WireView Mini has handed its serial
        /// port to a local client (the devices are then its last readings, with
        /// <c>connected</c> false).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool SerialSuspended { get; set; }
    }

    /// <summary>Source-generated serializer for the LAN contract (the /sensors
    /// snapshot, and strings spliced into hand-written answers): no reflection,
    /// so it also works in WireView Mini's NativeAOT build. camelCase names,
    /// case-insensitive reading, nulls written. Unknown members are skipped (the
    /// System.Text.Json default), so newer publishers can add fields without
    /// breaking older readers. WireView Mini's control panel has its own context
    /// with the same options (MiniJsonContext; its types are internal to it).</summary>
    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
    [JsonSerializable(typeof(WireViewHostSnapshot))]
    [JsonSerializable(typeof(string))]
    public sealed partial class WireViewJsonContext : JsonSerializerContext
    {
    }
}
