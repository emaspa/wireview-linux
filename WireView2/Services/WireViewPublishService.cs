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

        /// <summary>Relay an authenticated remote write to the matching local device.</summary>
        private bool ExecuteCommand(WireViewCommand cmd)
        {
            foreach (var md in DeviceManager.Shared.Devices)
            {
                if (md.Device is NetworkDevice) continue; // never relay back out to a remote
                if (!string.IsNullOrEmpty(cmd.DeviceId) && md.Device.UniqueId != cmd.DeviceId) continue;
                return ExecuteOn(md.Device, cmd);
            }
            return false;
        }

        private static bool ExecuteOn(IWireViewDevice dev, WireViewCommand cmd)
        {
            try
            {
                switch (cmd.Op)
                {
                    case "screen":
                        if (dev is WireViewPro2Device s1) s1.ScreenCmd((WireViewPro2Device.SCREEN_CMD)cmd.Cmd);
                        else if (dev is HwmonDevice { DaemonAvailable: true } h1) h1.ScreenCmd((WireViewPro2Device.SCREEN_CMD)cmd.Cmd);
                        else return false;
                        return true;
                    case "nvm":
                        if (dev is WireViewPro2Device s2) s2.NvmCmd((WireViewPro2Device.NVM_CMD)cmd.Cmd);
                        else if (dev is HwmonDevice { DaemonAvailable: true } h2) h2.NvmCmd((WireViewPro2Device.NVM_CMD)cmd.Cmd);
                        else return false;
                        return true;
                    case "clearFaults":
                        if (dev is WireViewPro2Device s3) s3.ClearFaults(cmd.StatusMask, cmd.LogMask);
                        else if (dev is HwmonDevice { DaemonAvailable: true } h3) h3.ClearFaults(cmd.StatusMask, cmd.LogMask);
                        else return false;
                        return true;
                    case "writeConfig":
                        if (cmd.ConfigData == null) return false;
                        if (dev is WireViewPro2Device s4) s4.WriteConfigRaw(cmd.ConfigData);
                        else if (dev is HwmonDevice { DaemonAvailable: true } h4) h4.WriteConfigRaw(cmd.ConfigVersion, cmd.ConfigData);
                        else return false;
                        return true;
                    default:
                        return false;
                }
            }
            catch { return false; }
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
                // ours, duplicating it across the fleet (and risking relay loops).
                if (md.Device is NetworkDevice) continue;
                if (md.Latest != null)
                    snapshot.Devices.Add(WireViewSensorDto.FromDevice(md.Device, md.Latest));
            }

            return snapshot;
        }

        public void Dispose() => Stop();
    }
}
