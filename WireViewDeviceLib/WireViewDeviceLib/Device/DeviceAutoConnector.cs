using System;
using System.Threading;
using System.Threading.Tasks;

namespace WireView2.Device
{
    public sealed class DeviceAutoConnector : IDisposable
    {
        // Shared singleton for the whole app
        public static DeviceAutoConnector Shared { get; } = new DeviceAutoConnector();

        private readonly object _gate = new();
        private CancellationTokenSource? _cts;
        private Task? _worker;

        private WireViewPro2Device? _device;
        private int _pollMs = 1000;

        public event EventHandler<bool>? ConnectionChanged; // true=connected
        public event EventHandler<DeviceData>? DataUpdated;

        // Keep the handler we attach so we can detach the exact same delegate
        private EventHandler<DeviceData>? _dataForwardHandler;

        public IWireViewDevice? Device
        {
            get
            {
                lock (_gate)
                {
                    return _device;
                }
            }
        }

        public void Start()
        {
            if (_worker != null) return;
            _cts = new CancellationTokenSource();
            _worker = Task.Run(() => LoopAsync(_cts.Token));
        }

        public void Stop()
        {
            _cts?.Cancel();
            try { _worker?.Wait(500); } catch { }
            _worker = null;
            _cts = null;
            DisconnectInternal();
        }

        public void SetPollInterval(int ms)
        {
            _pollMs = Math.Clamp(ms, 50, 5000);
            lock (_gate)
            {
                if (_device != null) _device.PollIntervalMs = _pollMs;
            }
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    EnsureDevice();
                }
                catch
                {
                    // ignore and retry
                }

                await Task.Delay(_pollMs, ct).ConfigureAwait(false);
            }
        }

        private void EnsureDevice()
        {
            lock (_gate)
            {
                if (_device is { Connected: true })
                {
                    return;
                }

                // If we have a stale/disconnected instance, drop it so we can reconnect cleanly.
                if (_device != null)
                {
                    DisconnectInternal();
                }

                var ports = Stm32PortFinder.FindMatchingComPorts();
                if (ports.Count == 0)
                {
                    return;
                }

                // Try connect to all matching ports
                foreach(var port in ports) { 

                    var dev = new WireViewPro2Device(port)
                    {
                        PollIntervalMs = _pollMs
                    };
                    try
                    {
                        dev.Connect();
                        if (dev.Connected)
                        {
                            // success
                            _device = dev;
                            dev.ConnectionChanged += OnDeviceConnectionChanged;
                            _dataForwardHandler ??= (_, d) => DataUpdated?.Invoke(this, d);
                            dev.DataUpdated += _dataForwardHandler;
                            ConnectionChanged?.Invoke(this, true);
                            return;
                        }
                        else
                        {
                            dev.Dispose();
                        }
                    }
                    catch
                    {
                        try
                        {
                            dev.Dispose();
                        }
                        catch
                        {
                        }
                    }

                }
            }
        }

        private void OnDeviceConnectionChanged(object? sender, bool connected)
        {
            if (!connected)
            {
                // drop and let loop reconnect
                DisconnectInternal();
            }
            ConnectionChanged?.Invoke(this, connected);
        }

        private void DisconnectInternal()
        {
            try
            {
                if (_device != null)
                {
                    _device.ConnectionChanged -= OnDeviceConnectionChanged;

                    if (_dataForwardHandler != null)
                        _device.DataUpdated -= _dataForwardHandler;

                    _device.Disconnect();
                    _device.Dispose();
                }
            }
            catch { }
            finally
            {
                _device = null;
                ConnectionChanged?.Invoke(this, false);
            }
        }

        public void Dispose() => Stop();
    }
}