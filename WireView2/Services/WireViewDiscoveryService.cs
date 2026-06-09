using System;
using System.Collections.Generic;
using WireView2.Device;
using WireView2.Net;

namespace WireView2.Services
{
    /// <summary>
    /// Wires LAN discovery into the app: browses mDNS for WireView publishers,
    /// combines them with manually-configured hosts, and registers a remote
    /// device probe so discovered devices appear in <see cref="DeviceManager"/>
    /// alongside local ones. A host's view of its own published devices is
    /// dropped by DeviceManager's UniqueId dedup.
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
            return endpoints;
        }

        public void Dispose()
        {
            _browser?.Dispose();
            _browser = null;
            _started = false;
        }
    }
}
