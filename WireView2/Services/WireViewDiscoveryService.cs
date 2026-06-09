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
    /// Wires LAN discovery into the app: browses mDNS for WireView publishers,
    /// combines them with manually-configured hosts, and registers a remote
    /// device probe so discovered devices appear in <see cref="DeviceManager"/>
    /// alongside local ones. Endpoints that point back at this host are filtered
    /// out (see <see cref="IsLocalEndpoint"/>) so a host never re-discovers its
    /// own devices over the LAN — those are already present locally.
    /// </summary>
    public sealed class WireViewDiscoveryService : IDisposable
    {
        public static WireViewDiscoveryService Shared { get; } = new WireViewDiscoveryService();

        private MdnsBrowser? _browser;
        private bool _started;

        public void Start()
        {
            if (_started) return;
            _started = true;

            _browser = new MdnsBrowser();
            _browser.Start();

            var probe = new RemoteDeviceProbe(GetEndpoints);
            DeviceManager.Shared.RegisterProbe(probe.Probe);
        }

        private IEnumerable<string> GetEndpoints()
        {
            var endpoints = new List<string>();
            if (_browser != null) endpoints.AddRange(_browser.Endpoints);
            var manual = AppSettings.Current.RemoteHosts;
            if (manual != null) endpoints.AddRange(manual);

            // Drop endpoints that resolve to this host: its WireViews are already
            // available locally (serial/hwmon), so reading them again over the LAN
            // would list every local device a second time as a "lan @ self" entry.
            return endpoints.Where(e => !IsLocalEndpoint(e));
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
            _browser?.Dispose();
            _browser = null;
            _started = false;
        }
    }
}
