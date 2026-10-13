using System;
using WireView2.Device;
using WireView2.Net;

namespace WireView2.Services
{
    /// <summary>
    /// Wires LAN reading into the app: registers a remote device probe that reads
    /// each host listed in <see cref="AppSettings.RemoteHosts"/> (configured in
    /// Settings as a comma-separated list) over its <c>GET /sensors</c> endpoint, so
    /// those devices appear in <see cref="DeviceManager"/> alongside local ones.
    /// There is no mDNS auto-discovery: remote hosts are entered explicitly.
    /// Endpoints that resolve to this machine are filtered out (see
    /// <see cref="RemoteHostList.IsLocalEndpoint"/>) so a host never re-reads its own devices.
    /// Also registers the probe for wireview-agent on this machine
    /// (<see cref="LocalAgentProbe"/>), which runs ahead of the serial probe:
    /// while the agent answers, the GUI reaches the device through it.
    /// Call <see cref="Start"/> before <see cref="DeviceManager.Start"/>.
    /// </summary>
    public sealed class WireViewDiscoveryService : IDisposable
    {
        public static WireViewDiscoveryService Shared { get; } = new WireViewDiscoveryService();

        private bool _started;
        private LocalAgentProbe? _agentProbe;

        /// <summary>True while wireview-agent answers on this machine's publish port.</summary>
        public bool AgentPresent => _agentProbe?.AgentPresent ?? false;

        public void Start()
        {
            if (_started) return;
            _started = true;

            _agentProbe = new LocalAgentProbe(
                () => AppSettings.Current.PublishPort,
                () => DeviceManager.Shared.PollIntervalMs);
            DeviceManager.Shared.SetLocalOwnerProbe(_agentProbe.Probe, () => _agentProbe.AgentPresent);

            // The configured host list is re-read every probe tick, so edits in
            // Settings take effect without restarting.
            var probe = new RemoteDeviceProbe(RemoteHostList.Endpoints, () => AppSettings.Current.NetworkSecret);
            DeviceManager.Shared.RegisterProbe(probe.Probe);
        }

        public void Dispose()
        {
            _started = false;
        }
    }
}
