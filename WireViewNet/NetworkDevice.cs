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

        /// <summary>Send an authenticated write command to this remote device. The
        /// command's DeviceId is set to this device. Returns false if no secret is
        /// configured or the request fails / is rejected.</summary>
        public async Task<bool> SendCommandAsync(WireViewCommand cmd, CancellationToken ct = default)
        {
            string? secret = _secretProvider?.Invoke();
            if (string.IsNullOrEmpty(secret)) return false;

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
                return r.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        public void Dispose() => Disconnect();
    }
}
