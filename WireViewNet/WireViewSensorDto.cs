using System.Text.Json;
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

        // Convenience totals (also recomputable from the pin arrays).
        public double SumCurrentA { get; set; }
        public double SumPowerW { get; set; }

        public static WireViewSensorDto FromDevice(IWireViewDevice dev, DeviceData d) => new()
        {
            Id = dev.UniqueId,
            Name = dev.DeviceName,
            Connected = d.Connected,
            HwRev = d.HardwareRevision,
            FwVer = d.FirmwareVersion,
            BuildString = dev.BuildString,
            Timestamp = d.Timestamp,
            PinVoltage = (double[])d.PinVoltage.Clone(),
            PinCurrent = (double[])d.PinCurrent.Clone(),
            TempInC = d.OnboardTempInC,
            TempOutC = d.OnboardTempOutC,
            Ext1C = d.ExternalTemp1C,
            Ext2C = d.ExternalTemp2C,
            PsuCapW = d.PsuCapabilityW,
            FaultStatus = d.FaultStatus,
            FaultLog = d.FaultLog,
            Fan = d.FanDuty,
            SumCurrentA = d.SumCurrentA,
            SumPowerW = d.SumPowerW,
        };

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
        };
    }

    /// <summary>Full <c>GET /sensors</c> payload: this host and all its WireView devices.</summary>
    public sealed class WireViewHostSnapshot
    {
        public string Host { get; set; } = "";
        public string AppVersion { get; set; } = "";
        public List<WireViewSensorDto> Devices { get; set; } = new();
    }

    /// <summary>Shared JSON options for the publish/consume contract (camelCase).</summary>
    public static class WireViewJson
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
    }
}
