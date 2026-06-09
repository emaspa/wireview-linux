using System.Net.Http;
using System.Text.Json;
using WireView2.Device;

namespace WireView2.Net
{
    /// <summary>
    /// A device probe (for <c>DeviceManager</c>) that turns remote WireView
    /// publishers into <see cref="NetworkDevice"/> instances. Endpoints come from
    /// a provider (mDNS discovery + manually-configured hosts). For each endpoint
    /// it periodically fetches <c>/sensors</c> to learn which devices it exposes,
    /// then yields a NetworkDevice per remote device not already held.
    /// Endpoints pointing back at this host are filtered upstream (the endpoints
    /// provider), so a host never re-discovers its own locally-attached devices.
    /// </summary>
    public sealed class RemoteDeviceProbe
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

        private readonly Func<IEnumerable<string>> _endpoints;
        private readonly TimeSpan _refresh = TimeSpan.FromSeconds(5);
        private readonly object _gate = new();
        private readonly Dictionary<string, DateTime> _lastFetch = new();
        private readonly Dictionary<string, List<(string Id, string Name)>> _cache = new();
        private readonly Func<string?>? _secretProvider;

        public RemoteDeviceProbe(Func<IEnumerable<string>> endpointsProvider, Func<string?>? secretProvider = null)
        {
            _endpoints = endpointsProvider;
            _secretProvider = secretProvider;
        }

        /// <summary>The probe delegate to register with <c>DeviceManager.RegisterProbe</c>.</summary>
        public IEnumerable<(IWireViewDevice device, string source)> Probe(ISet<string> held)
        {
            foreach (var baseUrl in _endpoints().Select(Normalize).Distinct())
            {
                RefreshIfStale(baseUrl);

                List<(string Id, string Name)>? devs;
                lock (_gate) { _cache.TryGetValue(baseUrl, out devs); }
                if (devs == null) continue;

                foreach (var (id, name) in devs)
                {
                    if (string.IsNullOrEmpty(id)) continue;
                    string source = $"remote:{baseUrl}:{id}";
                    if (held.Contains(source)) continue;
                    yield return (new NetworkDevice(baseUrl, id, name, _secretProvider), source);
                }
            }
        }

        private void RefreshIfStale(string baseUrl)
        {
            lock (_gate)
            {
                if (_lastFetch.TryGetValue(baseUrl, out var t) && DateTime.UtcNow - t < _refresh)
                    return;
                _lastFetch[baseUrl] = DateTime.UtcNow;
            }

            try
            {
                string json = Http.GetStringAsync($"{baseUrl}/sensors").GetAwaiter().GetResult();
                var snap = JsonSerializer.Deserialize<WireViewHostSnapshot>(json, WireViewJson.Options);
                var list = snap?.Devices.Select(d => (d.Id, d.Name)).ToList() ?? new();
                lock (_gate) { _cache[baseUrl] = list; }
            }
            catch
            {
                lock (_gate) { _cache.Remove(baseUrl); } // unreachable host -> drop its devices
            }
        }

        /// <summary>Accepts "host", "host:port", or a full URL; returns "http://host:port".</summary>
        public static string Normalize(string endpoint)
        {
            endpoint = endpoint.Trim();
            if (endpoint.StartsWith("http://") || endpoint.StartsWith("https://"))
                return endpoint.TrimEnd('/');
            if (!endpoint.Contains(':'))
                endpoint += ":9876";
            return "http://" + endpoint;
        }
    }
}
