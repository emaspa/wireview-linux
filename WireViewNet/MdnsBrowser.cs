using System.Collections.Concurrent;
using System.Net;
using Makaretu.Dns;

namespace WireView2.Net
{
    /// <summary>
    /// Discovers WireView publishers on the LAN via mDNS/DNS-SD (_wireview._tcp)
    /// and exposes their base URLs (http://host:port). Best-effort: if multicast
    /// is unavailable, callers fall back to manually-configured hosts.
    /// </summary>
    public sealed class MdnsBrowser : IDisposable
    {
        private ServiceDiscovery? _sd;
        private readonly ConcurrentDictionary<string, string> _endpoints = new(); // instance -> baseUrl

        public IReadOnlyCollection<string> Endpoints => _endpoints.Values.Distinct().ToList();

        public void Start()
        {
            try
            {
                _sd = new ServiceDiscovery();
                _sd.ServiceInstanceDiscovered += OnInstanceDiscovered;
                _sd.QueryServiceInstances(MdnsAdvertiser.ServiceName);
            }
            catch
            {
                Dispose();
            }
        }

        private void OnInstanceDiscovered(object? sender, ServiceInstanceDiscoveryEventArgs e)
        {
            try
            {
                var records = e.Message.AdditionalRecords.Concat(e.Message.Answers).ToList();
                var srv = records.OfType<SRVRecord>().FirstOrDefault();
                if (srv == null) return;

                int port = srv.Port;
                IPAddress? ip = records.OfType<ARecord>().Select(r => r.Address).FirstOrDefault()
                                ?? records.OfType<AAAARecord>().Select(r => r.Address).FirstOrDefault();

                string host = ip?.ToString() ?? srv.Target.ToString().TrimEnd('.');
                if (string.IsNullOrWhiteSpace(host)) return;

                _endpoints[e.ServiceInstanceName.ToString()] = $"http://{host}:{port}";
            }
            catch
            {
                // ignore malformed announcements
            }
        }

        public void Dispose()
        {
            try { if (_sd != null) _sd.ServiceInstanceDiscovered -= OnInstanceDiscovered; } catch { /* ignore */ }
            try { _sd?.Dispose(); } catch { /* ignore */ }
            _sd = null;
        }
    }
}
