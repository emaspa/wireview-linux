using System;
using System.Net;
using System.Reflection;
using WireView2.Device;
using WireView2.Net;

namespace WireView2.Services
{
    /// <summary>
    /// Bridges all local devices to the LAN: serves every device managed by
    /// <see cref="DeviceManager.Shared"/> through a <see cref="LocalPublishHost"/>
    /// (GET /sensors, GET /config, and with a network secret the signed
    /// POST /command). Remote instances reach this endpoint via their configured
    /// host list; there is no mDNS advertisement. Skipped when wireview-agent
    /// already publishes on the port (it owns the device then).
    /// </summary>
    public sealed class WireViewPublishService : IDisposable
    {
        public static WireViewPublishService Shared { get; } = new WireViewPublishService();

        private static readonly string AppVersion =
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

        private readonly LocalPublishHost _host = new(DeviceManager.Shared, AppVersion);

        public bool IsRunning { get; private set; }

        public void Start()
        {
            if (IsRunning) return;
            if (!AppSettings.Current.PublishEnabled) return;
            // On Linux the wireviewd daemon owns LAN publishing (and works headless on
            // servers); the GUI only publishes where there's no daemon (Windows/macOS).
            if (OperatingSystem.IsLinux()) return;

            int port = AppSettings.Current.PublishPort;
            // The agent publishes this machine's device itself (on all interfaces
            // when publishing is on); a second listener on its port would fail or,
            // on Windows, could share the port with it.
            if (LocalAgentProbe.IsAgentRunning(port))
            {
                FileLog.Info($"LAN publishing left to wireview-agent, which already listens on port {port}");
                return;
            }
            try
            {
                _host.Start(port, IPAddress.Any);
                IsRunning = true;
            }
            catch (Exception ex)
            {
                // Port in use / restricted: leave publishing off, app keeps working.
                FileLog.Warn($"LAN publishing on port {port} failed: {ex.Message}");
                Stop();
            }
        }

        public void Stop()
        {
            _host.Stop();
            IsRunning = false;
        }

        public void Dispose() => Stop();
    }
}
