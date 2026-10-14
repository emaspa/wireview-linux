using System.Net;
using WireView2.Device;
using WireView2.Services;

namespace WireView2.Net
{
    /// <summary>
    /// Bridges the local devices of one <see cref="DeviceManager"/> to the LAN
    /// through a <see cref="SensorPublisher"/>: builds the <c>GET /sensors</c>
    /// snapshot, serves <c>GET /config</c>, and relays authenticated
    /// <c>POST /command</c> writes to the matching device. One implementation for
    /// the GUI's publisher (Windows) and the headless agent. Devices reached over
    /// the LAN (<see cref="NetworkDevice"/>) are never re-published or relayed to.
    /// Limits and the network secret come from <see cref="AppSettings.Current"/>.
    /// </summary>
    public sealed class LocalPublishHost : IDisposable
    {
        private readonly DeviceManager _manager;
        private readonly string _appVersion;
        private readonly object _gate = new();
        private SensorPublisher? _publisher;
        // Devices as last seen before the serial port was handed over (agent only).
        private List<WireViewSensorDto>? _frozen;

        /// <param name="manager">Whose devices to publish.</param>
        /// <param name="appVersion">Sent as the snapshot's <c>appVersion</c>.</param>
        public LocalPublishHost(DeviceManager manager, string appVersion)
        {
            _manager = manager;
            _appVersion = appVersion;
        }

        /// <summary>Sent as the snapshot's <c>agent</c> field (the agent sets its
        /// version; the GUI leaves it null).</summary>
        public string? AgentVersion { get; init; }

        /// <summary>Consulted before the device relay: returns the result for a
        /// command it handles (the agent's serial handover), or null to relay it.</summary>
        public Func<WireViewCommand, DaemonResult?>? CommandHook { get; init; }

        /// <summary>A second secret accepted for commands from this machine only
        /// (see <see cref="SensorPublisher"/>).</summary>
        public Func<string?>? LocalSecretProvider { get; init; }

        /// <summary>Answers requests the publisher's own routes do not (the agent's
        /// control panel), on the same port; null passes them on (404).</summary>
        public Func<HttpRequestInfo, HttpResponseInfo?>? ExtraRoutes { get; init; }

        public bool IsRunning
        {
            get { lock (_gate) return _publisher?.IsRunning == true; }
        }

        /// <summary>The port and address listened on, or null while stopped.</summary>
        public (int Port, IPAddress Address)? Endpoint
        {
            get { lock (_gate) return _publisher is { IsRunning: true } p ? (p.Port, p.BindAddress) : null; }
        }

        /// <summary>Opens the listener. Throws (a <see cref="System.Net.Sockets.SocketException"/>)
        /// when the port cannot be bound; nothing is left running then.</summary>
        public void Start(int port, IPAddress bindAddress)
        {
            lock (_gate)
            {
                if (_publisher?.IsRunning == true) return;
                var s = AppSettings.Current;
                var publisher = new SensorPublisher(
                    port,
                    BuildSnapshot,
                    secretProvider: () => AppSettings.Current.NetworkSecret,
                    commandSink: ExecuteCommand,
                    configReader: ReadLocalConfig,
                    maxConnections: s.MaxHttpConnections,
                    maxRequestBytes: s.MaxRequestBytes,
                    rateLimitPerMinute: s.RateLimitPerMinute,
                    bindAddress: bindAddress,
                    localSecretProvider: LocalSecretProvider,
                    extraRoutes: ExtraRoutes);
                try
                {
                    publisher.Start();
                }
                catch
                {
                    publisher.Dispose();
                    throw;
                }
                _publisher = publisher;
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                _publisher?.Dispose();
                _publisher = null;
            }
        }

        // ---- Serial handover view (agent) ----

        /// <summary>True between <see cref="FreezeDevices"/> and <see cref="Unfreeze"/>.</summary>
        public bool SerialSuspended
        {
            get { lock (_gate) return _frozen != null; }
        }

        /// <summary>Keeps publishing the devices as they are now, marked not
        /// connected, while the manager is stopped for a serial handover. Remote
        /// readers (and the GUI) keep the device listed instead of dropping it.</summary>
        public void FreezeDevices()
        {
            var devices = CurrentDevices();
            foreach (var d in devices) d.Connected = false;
            lock (_gate) _frozen ??= devices;
        }

        /// <summary>Back to publishing the manager's live devices.</summary>
        public void Unfreeze()
        {
            lock (_gate) _frozen = null;
        }

        // ---- The publisher's callbacks ----

        /// <summary>The <c>GET /sensors</c> payload.</summary>
        public WireViewHostSnapshot BuildSnapshot()
        {
            List<WireViewSensorDto>? frozen;
            lock (_gate) frozen = _frozen;
            return new WireViewHostSnapshot
            {
                Host = Environment.MachineName,
                AppVersion = _appVersion,
                Agent = AgentVersion,
                SerialSuspended = frozen != null,
                Devices = frozen != null ? new List<WireViewSensorDto>(frozen) : CurrentDevices(),
            };
        }

        private List<WireViewSensorDto> CurrentDevices()
        {
            var list = new List<WireViewSensorDto>();
            foreach (var md in _manager.Devices)
            {
                // Publish only locally-attached devices. Re-exporting devices we
                // discovered over the LAN would advertise another host's device as
                // ours, duplicating it across the network (and risking relay loops).
                if (md.Device is NetworkDevice) continue;
                if (md.Latest != null)
                    list.Add(WireViewSensorDto.FromDevice(md.Device, md.Latest));
            }
            return list;
        }

        /// <summary>Read a local device's config (for GET /config), serialized to the
        /// device's native version layout. deviceId null selects the first local device.</summary>
        public ConfigSnapshot? ReadLocalConfig(string? deviceId)
        {
            foreach (var md in _manager.Devices)
            {
                if (md.Device is NetworkDevice) continue;
                if (!string.IsNullOrEmpty(deviceId) && md.Device.UniqueId != deviceId) continue;

                // The stored bytes, not a decode/encode round trip: remote editors
                // patch their changes onto exactly what the device holds.
                (int Version, byte[] Data)? raw;
                if (md.Device is WireViewPro2Device p) raw = p.ReadConfigRaw() is { } b ? (p.ConfigVersion, b) : null;
                else if (md.Device is HwmonDevice { DaemonAvailable: true } h) raw = h.ReadConfigRaw();
                else continue;

                if (raw is not { } r) return null;
                return new ConfigSnapshot(md.Device.UniqueId, r.Version, r.Data);
            }
            return null;
        }

        /// <summary>Relay an authenticated remote write to the matching local device.
        /// Returns wireviewd's answer for a daemon-backed device, so a permission
        /// denial reaches the remote client as such (HTTP 403 "denied").</summary>
        public DaemonResult ExecuteCommand(WireViewCommand cmd)
        {
            if (CommandHook?.Invoke(cmd) is { } handled) return handled;
            if (cmd.LocalOnly) return DaemonResult.Error; // nobody here implements it

            // Never relay back out to a remote. With no device id, prefer a device
            // that takes commands: with the hwmon module loaded but wireviewd
            // stopped the manager holds a read-only hwmon entry next to the
            // serial port, and the first entry would answer "not connected".
            var candidates = _manager.Devices
                .Where(md => md.Device is not NetworkDevice)
                .Where(md => string.IsNullOrEmpty(cmd.DeviceId) || md.Device.UniqueId == cmd.DeviceId)
                .OrderByDescending(md => md.Device is WireViewPro2Device || md.Device is HwmonDevice { DaemonAvailable: true })
                .ThenByDescending(md => md.Device.Connected);
            foreach (var md in candidates)
                return ExecuteOn(md.Device, cmd);
            return DaemonResult.NotConnected;
        }

        private static DaemonResult ExecuteOn(IWireViewDevice dev, WireViewCommand cmd)
        {
            try
            {
                switch (cmd.Op)
                {
                    case "screen":
                        if (dev is WireViewPro2Device s1) s1.ScreenCmd((WireViewPro2Device.SCREEN_CMD)cmd.Cmd);
                        else if (dev is HwmonDevice { DaemonAvailable: true } h1) return h1.ScreenCmd((WireViewPro2Device.SCREEN_CMD)cmd.Cmd);
                        else return DaemonResult.NotConnected;
                        return DaemonResult.Ok;
                    case "nvm":
                        if (dev is WireViewPro2Device s2) s2.NvmCmd((WireViewPro2Device.NVM_CMD)cmd.Cmd);
                        else if (dev is HwmonDevice { DaemonAvailable: true } h2) return h2.NvmCmd((WireViewPro2Device.NVM_CMD)cmd.Cmd);
                        else return DaemonResult.NotConnected;
                        return DaemonResult.Ok;
                    case "clearFaults":
                        // Keep-masks, passed through unchanged (same as wireviewd's relay).
                        if (dev is WireViewPro2Device s3) s3.ClearFaults(keepStatusMask: cmd.StatusMask, keepLogMask: cmd.LogMask);
                        else if (dev is HwmonDevice { DaemonAvailable: true } h3) return h3.ClearFaults(keepStatusMask: cmd.StatusMask, keepLogMask: cmd.LogMask);
                        else return DaemonResult.NotConnected;
                        return DaemonResult.Ok;
                    case "writeConfig":
                        if (cmd.ConfigData == null) return DaemonResult.Error;
                        if (dev is WireViewPro2Device s4) s4.WriteConfigRaw(cmd.ConfigData);
                        else if (dev is HwmonDevice { DaemonAvailable: true } h4) return h4.WriteConfigRaw(cmd.ConfigVersion, cmd.ConfigData);
                        else return DaemonResult.NotConnected;
                        return DaemonResult.Ok;
                    default:
                        return DaemonResult.Error;
                }
            }
            catch { return DaemonResult.Error; }
        }

        public void Dispose() => Stop();
    }
}
