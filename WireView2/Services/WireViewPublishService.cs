using System;
using System.Collections.Generic;
using System.Reflection;
using WireView2.Device;
using WireView2.Net;

namespace WireView2.Services
{
    /// <summary>
    /// Bridges the local device(s) to the LAN: caches the latest readings from
    /// <see cref="DeviceAutoConnector.Shared"/> and serves them via
    /// <see cref="SensorPublisher"/> (GET /sensors) + <see cref="MdnsAdvertiser"/>
    /// (_wireview._tcp). Read-only — no command surface is exposed over the network.
    /// Phase 1: single local device.
    /// </summary>
    public sealed class WireViewPublishService : IDisposable
    {
        public static WireViewPublishService Shared { get; } = new WireViewPublishService();

        private readonly object _gate = new();
        private DeviceData? _latest;
        private SensorPublisher? _publisher;
        private MdnsAdvertiser? _advertiser;

        private static readonly string AppVersion =
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

        public bool IsRunning { get; private set; }

        public void Start()
        {
            if (IsRunning) return;
            if (!AppSettings.Current.PublishEnabled) return;

            DeviceAutoConnector.Shared.DataUpdated += OnDataUpdated;

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
                    deviceCount: DeviceAutoConnector.Shared.Device != null ? 1 : 0,
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
            DeviceAutoConnector.Shared.DataUpdated -= OnDataUpdated;
            _advertiser?.Dispose();
            _advertiser = null;
            _publisher?.Dispose();
            _publisher = null;
            IsRunning = false;
        }

        private void OnDataUpdated(object? sender, DeviceData data)
        {
            lock (_gate) { _latest = data; }
        }

        private WireViewHostSnapshot BuildSnapshot()
        {
            var snapshot = new WireViewHostSnapshot
            {
                Host = Environment.MachineName,
                AppVersion = AppVersion,
                Devices = new List<WireViewSensorDto>(),
            };

            var device = DeviceAutoConnector.Shared.Device;
            DeviceData? latest;
            lock (_gate) { latest = _latest; }

            if (device != null && latest != null)
            {
                snapshot.Devices.Add(WireViewSensorDto.FromDevice(device, latest));
            }

            return snapshot;
        }

        public void Dispose() => Stop();
    }
}
