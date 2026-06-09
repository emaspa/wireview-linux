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
    /// (GET /sensors) + <see cref="MdnsAdvertiser"/> (_wireview._tcp). Read-only —
    /// no command surface is exposed over the network.
    /// </summary>
    public sealed class WireViewPublishService : IDisposable
    {
        public static WireViewPublishService Shared { get; } = new WireViewPublishService();

        private SensorPublisher? _publisher;
        private MdnsAdvertiser? _advertiser;

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
                _publisher = new SensorPublisher(port, BuildSnapshot);
                _publisher.Start();

                _advertiser = new MdnsAdvertiser();
                _advertiser.Start(
                    instanceName: $"WireView@{Environment.MachineName}",
                    port: port,
                    hostName: Environment.MachineName,
                    deviceCount: DeviceManager.Shared.Devices.Count,
                    appVersion: AppVersion);

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
            _advertiser?.Dispose();
            _advertiser = null;
            _publisher?.Dispose();
            _publisher = null;
            IsRunning = false;
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
