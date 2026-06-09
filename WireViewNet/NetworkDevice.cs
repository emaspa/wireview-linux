using System.Net.Http;
using System.Text;
using System.Text.Json;
using WireView2.Device;

namespace WireView2.Net
{
    /// <summary>
    /// A WireView on another host, read over the LAN via its <c>GET /sensors</c>
    /// endpoint. Implements <see cref="IWireViewDevice"/> so it lives in the same
    /// <c>DeviceManager</c> collection as local devices — the rest of the app
    /// treats it identically. Read-only: it has no command surface.
    /// </summary>
    public sealed class NetworkDevice : IWireViewDevice, IDisposable
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

        private readonly string _baseUrl;   // e.g. http://host:9876
        private readonly Func<string?>? _secretProvider;
        private CancellationTokenSource? _cts;
        private Task? _loop;

        public NetworkDevice(string baseUrl, string deviceId, string name, Func<string?>? secretProvider = null)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            UniqueId = deviceId;
            DeviceName = string.IsNullOrWhiteSpace(name) ? "WireView" : name;
            _secretProvider = secretProvider;
        }

        public bool Connected { get; private set; }
        public string DeviceName { get; private set; }
        public string HardwareRevision { get; private set; } = "";
        public string FirmwareVersion { get; private set; } = "";
        public string UniqueId { get; }
        public int PollIntervalMs { get; set; } = 1000;

        /// <summary>The host:port this device is read from (for UI display).</summary>
        public string Endpoint => _baseUrl;

        /// <summary>Config version learned from the last ReadConfigRaw (-1 until read).</summary>
        public int ConfigVersion { get; private set; } = -1;

        public event EventHandler<DeviceData>? DataUpdated;
        public event EventHandler<bool>? ConnectionChanged;

        public void Connect()
        {
            // Synchronous first fetch so DeviceManager.TryAdd sees Connected immediately.
            bool ok = PollOnce();
            SetConnected(ok);
            if (!ok) return;

            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => LoopAsync(_cts.Token));
        }

        public void Disconnect()
        {
            try { _cts?.Cancel(); } catch { /* ignore */ }
            try { _loop?.Wait(500); } catch { /* ignore */ }
            _loop = null;
            _cts = null;
            SetConnected(false);
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(PollIntervalMs, ct).ConfigureAwait(false);
                bool ok = PollOnce();
                SetConnected(ok);
                if (!ok) return; // device gone from remote -> drop; manager prunes & may re-add
            }
        }

        private bool PollOnce()
        {
            try
            {
                string json = Http.GetStringAsync($"{_baseUrl}/sensors").GetAwaiter().GetResult();
                var snap = JsonSerializer.Deserialize<WireViewHostSnapshot>(json, WireViewJson.Options);
                var dto = snap?.Devices.FirstOrDefault(d => d.Id == UniqueId);
                if (dto == null) return false;

                DeviceName = string.IsNullOrWhiteSpace(dto.Name) ? DeviceName : dto.Name;
                HardwareRevision = dto.HwRev;
                FirmwareVersion = dto.FwVer;
                DataUpdated?.Invoke(this, dto.ToDeviceData());
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void SetConnected(bool value)
        {
            if (Connected == value) return;
            Connected = value;
            ConnectionChanged?.Invoke(this, value);
        }

        /// <summary>Send an authenticated write command to this remote device (its
        /// DeviceId is set to this device). The result distinguishes no-local-secret,
        /// a remote rejection (401/403), an unreachable host, and other HTTP errors.</summary>
        public async Task<CommandResult> SendCommandAsync(WireViewCommand cmd, CancellationToken ct = default)
        {
            string? secret = _secretProvider?.Invoke();
            if (string.IsNullOrEmpty(secret)) return new CommandResult(CommandOutcome.NoLocalSecret);

            string body = (cmd with { DeviceId = UniqueId }).ToJson();
            var (ts, nonce, sig) = HmacAuth.Sign(secret, body);
            var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/command")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.Add(HmacAuth.TsHeader, ts.ToString());
            req.Headers.Add(HmacAuth.NonceHeader, nonce);
            req.Headers.Add(HmacAuth.SigHeader, sig);
            try
            {
                var r = await Http.SendAsync(req, ct).ConfigureAwait(false);
                if (r.IsSuccessStatusCode) return CommandResult.Success;
                return (int)r.StatusCode switch
                {
                    401 => new CommandResult(CommandOutcome.Unauthorized, 401),
                    403 => new CommandResult(CommandOutcome.WritesDisabled, 403),
                    var code => new CommandResult(CommandOutcome.HttpError, code),
                };
            }
            catch
            {
                // HttpRequestException (refused/DNS), TaskCanceledException (timeout), etc.
                return new CommandResult(CommandOutcome.Unreachable);
            }
        }

        /// <summary>Fetch this host's device config via GET /config. Returns the raw
        /// config bytes in the device's version layout (decode with
        /// WireViewPro2Device.DeserializeConfig), or null if unavailable. Synchronous,
        /// matching the polling path; callers invoke it off the UI thread or accept a
        /// brief block on a user-initiated reload.</summary>
        public (int version, byte[] data)? ReadConfigRaw()
        {
            try
            {
                string json = Http.GetStringAsync($"{_baseUrl}/config").GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                int version = r.TryGetProperty("version", out var v) ? v.GetInt32() : -1;
                string? b64 = r.TryGetProperty("data", out var d) ? d.GetString() : null;
                if (version < 0 || string.IsNullOrEmpty(b64)) return null;
                ConfigVersion = version;
                return (version, Convert.FromBase64String(b64));
            }
            catch { return null; }
        }

        public void Dispose() => Disconnect();
    }
}
