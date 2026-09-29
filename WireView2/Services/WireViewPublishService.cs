using System;
using System.Collections.Generic;
using System.Reflection;
using WireView2.Device;
using WireView2.Net;

namespace WireView2.Services
{
    /// <summary>
    /// Bridges all local devices to the LAN: serves every device managed by
    /// <see cref="DeviceManager.Shared"/> via <see cref="SensorPublisher"/>
    /// (GET /sensors). Read-only — no command surface is exposed over the network.
    /// Remote instances reach this endpoint via their configured host list; there
    /// is no mDNS advertisement.
    /// </summary>
    public sealed class WireViewPublishService : IDisposable
    {
        public static WireViewPublishService Shared { get; } = new WireViewPublishService();

        private SensorPublisher? _publisher;

        private static readonly string AppVersion =
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

        public bool IsRunning { get; private set; }

        public void Start()
        {
            if (IsRunning) return;
            if (!AppSettings.Current.PublishEnabled) return;
            // On Linux the wireviewd daemon owns LAN publishing (and works headless on
            // servers); the GUI only publishes where there's no daemon (Windows/macOS).
            if (OperatingSystem.IsLinux()) return;

            int port = AppSettings.Current.PublishPort;
            try
            {
                _publisher = new SensorPublisher(
                    port,
                    BuildSnapshot,
                    secretProvider: () => AppSettings.Current.NetworkSecret,
                    commandSink: ExecuteCommand,
                    configReader: ReadLocalConfig,
                    maxConnections: AppSettings.Current.MaxHttpConnections,
                    maxRequestBytes: AppSettings.Current.MaxRequestBytes,
                    rateLimitPerMinute: AppSettings.Current.RateLimitPerMinute);
                _publisher.Start();

                IsRunning = true;
            }
            catch
            {
                // Port in use / restricted — leave publishing off, app keeps working.
                Stop();
            }
        }

        public void Stop()
        {
            _publisher?.Dispose();
            _publisher = null;
            IsRunning = false;
        }

        /// <summary>Read a local device's config (for GET /config), serialized to the
        /// device's native version layout. deviceId null selects the first local device.</summary>
        private ConfigSnapshot? ReadLocalConfig(string? deviceId)
        {
            foreach (var md in DeviceManager.Shared.Devices)
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
        private DaemonResult ExecuteCommand(WireViewCommand cmd)
        {
            foreach (var md in DeviceManager.Shared.Devices)
            {
                if (md.Device is NetworkDevice) continue; // never relay back out to a remote
                if (!string.IsNullOrEmpty(cmd.DeviceId) && md.Device.UniqueId != cmd.DeviceId) continue;
                return ExecuteOn(md.Device, cmd);
            }
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

        private WireViewHostSnapshot BuildSnapshot()
        {
            var snapshot = new WireViewHostSnapshot
            {
                Host = Environment.MachineName,
                AppVersion = AppVersion,
                Devices = new List<WireViewSensorDto>(),
            };

            foreach (var md in DeviceManager.Shared.Devices)
            {
                // Publish only locally-attached devices. Re-exporting devices we
                // discovered over the LAN would advertise another host's device as
                // ours, duplicating it across the network (and risking relay loops).
                if (md.Device is NetworkDevice) continue;
                if (md.Latest != null)
                    snapshot.Devices.Add(WireViewSensorDto.FromDevice(md.Device, md.Latest));
            }

            return snapshot;
        }

        public void Dispose() => Stop();
    }
}
