using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using WireView2.Device;
using WireView2.Net;

namespace WireView2.Services
{
    /// <summary>
    /// Wires LAN reading into the app: registers a remote device probe that reads
    /// each host listed in <see cref="AppSettings.RemoteHosts"/> (configured in
    /// Settings as a comma-separated list) over its <c>GET /sensors</c> endpoint, so
    /// those devices appear in <see cref="DeviceManager"/> alongside local ones.
    /// There is no mDNS auto-discovery — remote hosts are entered explicitly.
    /// Endpoints that resolve to this machine are filtered out (see
    /// <see cref="IsLocalEndpoint"/>) so a host never re-reads its own devices.
    /// </summary>
    public sealed class WireViewDiscoveryService : IDisposable
    {
        public static WireViewDiscoveryService Shared { get; } = new WireViewDiscoveryService();

        private bool _started;

        public void Start()
        {
            if (_started) return;
            _started = true;

            var probe = new RemoteDeviceProbe(GetEndpoints);
            DeviceManager.Shared.RegisterProbe(probe.Probe);
        }

        private IEnumerable<string> GetEndpoints()
        {
            // The configured host list is re-read every probe tick, so edits in
            // Settings take effect without restarting. Drop blanks and any endpoint
            // that points back at this host (its devices are already local).
            var manual = AppSettings.Current.RemoteHosts;
            if (manual == null) return Array.Empty<string>();
            return manual.Where(e => !string.IsNullOrWhiteSpace(e) && !IsLocalEndpoint(e));
        }

        private static volatile HashSet<string>? _localHosts;
        private static DateTime _localHostsAt;

        /// <summary>True if the endpoint points back at this machine (loopback,
        /// one of its NIC addresses, or its own hostname). Best-effort.</summary>
        private static bool IsLocalEndpoint(string endpoint)
        {
            try
            {
                var host = new Uri(RemoteDeviceProbe.Normalize(endpoint)).Host;
                if (IPAddress.TryParse(host, out var ip))
                    return IPAddress.IsLoopback(ip) || LocalHosts().Contains(ip.ToString());

                return host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                    || host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static HashSet<string> LocalHosts()
        {
            var cached = _localHosts;
            if (cached != null && DateTime.UtcNow - _localHostsAt < TimeSpan.FromSeconds(60))
                return cached;

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        set.Add(ua.Address.ToString());
                }
            }
            catch { /* ignore — best-effort self detection */ }
            set.Add(IPAddress.Loopback.ToString());
            set.Add(IPAddress.IPv6Loopback.ToString());

            _localHosts = set;
            _localHostsAt = DateTime.UtcNow;
            return set;
        }

        public void Dispose()
        {
            _started = false;
        }
    }
}
