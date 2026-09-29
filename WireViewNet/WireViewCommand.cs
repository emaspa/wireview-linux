using System.Text;
using System.Text.Json;

namespace WireView2.Net
{
    /// <summary>
    /// A write command relayed over the LAN via POST /command. The client builds
    /// the JSON body with <see cref="ToJson"/> and signs those exact bytes; the
    /// server HMAC-verifies the received bytes and parses them with
    /// <see cref="Parse"/>. Mirrors the daemon's command schema (no bootloader).
    /// </summary>
    public sealed record WireViewCommand
    {
        public string DeviceId { get; init; } = "";
        public string Op { get; init; } = "";          // screen | nvm | clearFaults | writeConfig
        public int Cmd { get; init; }                   // screen / nvm
        /// <summary>clearFaults: KEEP-mask for the active faults (the firmware does
        /// <c>fault &amp;= mask</c>; 0 clears everything). Sent as "statusMask", the
        /// field name wireviewd's HTTP API uses; an omitted mask means 0 there and here.</summary>
        public int StatusMask { get; init; }
        /// <summary>clearFaults: KEEP-mask for the fault log, same semantics
        /// (sent as "logMask").</summary>
        public int LogMask { get; init; }
        public int ConfigVersion { get; init; }         // writeConfig
        public byte[]? ConfigData { get; init; }        // writeConfig (raw bytes)

        public static WireViewCommand Screen(string deviceId, int cmd)
            => new() { DeviceId = deviceId, Op = "screen", Cmd = cmd };
        public static WireViewCommand Nvm(string deviceId, int cmd)
            => new() { DeviceId = deviceId, Op = "nvm", Cmd = cmd };
        /// <summary>clearFaults with keep-masks: a set bit keeps that fault, 0 clears
        /// everything, <c>~(1 &lt;&lt; fault)</c> clears one fault.</summary>
        public static WireViewCommand Faults(string deviceId, int keepStatusMask, int keepLogMask)
            => new() { DeviceId = deviceId, Op = "clearFaults", StatusMask = keepStatusMask, LogMask = keepLogMask };
        public static WireViewCommand WriteConfig(string deviceId, int version, byte[] data)
            => new() { DeviceId = deviceId, Op = "writeConfig", ConfigVersion = version, ConfigData = data };

        /// <summary>Canonical JSON body the client signs and sends.</summary>
        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"deviceId\":\"").Append(DeviceId).Append("\",\"op\":\"").Append(Op).Append('"');
            switch (Op)
            {
                case "screen":
                case "nvm":
                    sb.Append(",\"cmd\":").Append(Cmd);
                    break;
                case "clearFaults":
                    sb.Append(",\"statusMask\":").Append(StatusMask).Append(",\"logMask\":").Append(LogMask);
                    break;
                case "writeConfig":
                    sb.Append(",\"version\":").Append(ConfigVersion)
                      .Append(",\"data\":\"").Append(Convert.ToBase64String(ConfigData ?? Array.Empty<byte>())).Append('"');
                    break;
            }
            sb.Append('}');
            return sb.ToString();
        }

        public static WireViewCommand? Parse(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                string op = r.TryGetProperty("op", out var o) ? o.GetString() ?? "" : "";
                if (op.Length == 0) return null;
                return new WireViewCommand
                {
                    DeviceId = r.TryGetProperty("deviceId", out var d) ? d.GetString() ?? "" : "",
                    Op = op,
                    Cmd = r.TryGetProperty("cmd", out var c) ? c.GetInt32() : 0,
                    // Omitted keep-masks default to 0 (clear), as in wireviewd's relay.
                    StatusMask = r.TryGetProperty("statusMask", out var s) ? s.GetInt32() : 0,
                    LogMask = r.TryGetProperty("logMask", out var l) ? l.GetInt32() : 0,
                    ConfigVersion = r.TryGetProperty("version", out var v) ? v.GetInt32() : 0,
                    ConfigData = r.TryGetProperty("data", out var da) && da.GetString() is string b64 && b64.Length > 0
                        ? Convert.FromBase64String(b64) : null,
                };
            }
            catch { return null; }
        }
    }
}
