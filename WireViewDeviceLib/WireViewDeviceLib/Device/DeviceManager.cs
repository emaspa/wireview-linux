using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WireView2.Device
{
    /// <summary>A connected local WireView plus its most recent reading.</summary>
    public sealed class ManagedDevice
    {
        public ManagedDevice(IWireViewDevice device, string source)
        {
            Device = device;
            Source = source;
        }

        public IWireViewDevice Device { get; }
        /// <summary>Where it came from, e.g. "hwmon" or "serial:/dev/ttyACM0". Used to avoid re-opening the same source.</summary>
        public string Source { get; }
        public string Id => Device.UniqueId;
        public DeviceData? Latest { get; internal set; }
    }

    /// <summary>
    /// Owns and maintains connections to <b>all</b> local WireView devices
    /// (hwmon chip + every matching serial port), keyed by the stable chip
    /// <c>UniqueId</c>. A single discovery worker connects new devices, prunes
    /// disconnected ones, and caches each device's latest <see cref="DeviceData"/>.
    /// This is the single owner of local hardware — nothing else should open the
    /// serial ports (see <see cref="DeviceAutoConnector"/>, which is now a facade
    /// exposing the <see cref="SelectedDevice"/>).
    /// </summary>
    public sealed class DeviceManager : IDisposable
    {
        public static DeviceManager Shared { get; } = new DeviceManager();

        private readonly object _gate = new();
        private readonly Dictionary<string, ManagedDevice> _byId = new();   // UniqueId -> device
        private readonly HashSet<string> _openSources = new();              // sources we already hold
        private readonly Dictionary<IWireViewDevice, EventHandler<DeviceData>> _dataHandlers = new();

        private CancellationTokenSource? _cts;
        private Task? _worker;
        private int _pollMs = 1000;
        private string? _selectedId;

        // Each probe yields candidate (unconnected) devices to attempt, skipping
        // sources already held. The default probes real hwmon + serial; the app
        // registers a remote probe; tests inject fakes.
        private readonly List<Func<ISet<string>, IEnumerable<(IWireViewDevice device, string source)>>> _probes = new();

        public DeviceManager() : this(null) { }

        /// <summary>Test/extension hook: supply a custom device probe (replaces the default).</summary>
        public DeviceManager(Func<ISet<string>, IEnumerable<(IWireViewDevice device, string source)>>? probe)
        {
            _probes.Add(probe ?? DefaultProbe);
        }

        /// <summary>Add another device source (e.g. the remote/network probe).</summary>
        public void RegisterProbe(Func<ISet<string>, IEnumerable<(IWireViewDevice device, string source)>> probe)
        {
            lock (_gate) { _probes.Add(probe); }
        }

        private static IEnumerable<(IWireViewDevice, string)> DefaultProbe(ISet<string> held)
        {
            if (OperatingSystem.IsLinux())
            {
                string? hwmonPath = HwmonDevice.FindHwmonPath();
                if (hwmonPath != null && !held.Contains("hwmon"))
                    yield return (new HwmonDevice(hwmonPath), "hwmon");
            }
            foreach (var port in Stm32PortFinder.FindMatchingComPorts())
            {
                string source = "serial:" + port;
                if (!held.Contains(source))
                    yield return (new WireViewPro2Device(port), source);
            }
        }

        /// <summary>Drive one discovery + prune pass synchronously (used by tests).</summary>
        public void Tick() { Discover(); Prune(); }

        /// <summary>Raised (on a background thread) when a device is added or removed.</summary>
        public event EventHandler? DevicesChanged;
        /// <summary>Raised when any device produces new data. Args: (deviceId, data).</summary>
        public event EventHandler<(string Id, DeviceData Data)>? DataUpdated;
        /// <summary>Raised when the selected device changes (or its connection flips).</summary>
        public event EventHandler? SelectedChanged;

        public IReadOnlyList<ManagedDevice> Devices
        {
            get { lock (_gate) { return _byId.Values.ToList(); } }
        }

        public string? SelectedId
        {
            get { lock (_gate) { return _selectedId; } }
            set
            {
                lock (_gate)
                {
                    if (_selectedId == value) return;
                    _selectedId = value;
                }
                SelectedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public ManagedDevice? Selected
        {
            get
            {
                lock (_gate)
                {
                    if (_selectedId != null && _byId.TryGetValue(_selectedId, out var d)) return d;
                    return _byId.Values.FirstOrDefault();
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
            try { _worker?.Wait(500); } catch { /* ignore */ }
            _worker = null;
            _cts = null;
            lock (_gate)
            {
                foreach (var md in _byId.Values.ToList()) Drop(md);
                _byId.Clear();
                _openSources.Clear();
                _selectedId = null;
            }
        }

        public void SetPollInterval(int ms)
        {
            _pollMs = Math.Clamp(ms, 50, 5000);
            lock (_gate)
            {
                foreach (var md in _byId.Values)
                {
                    if (md.Device is WireViewPro2Device s) s.PollIntervalMs = _pollMs;
                    else if (md.Device is HwmonDevice h) h.PollIntervalMs = _pollMs;
                }
            }
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { Discover(); Prune(); } catch { /* ignore and retry */ }
                await Task.Delay(_pollMs, ct).ConfigureAwait(false);
            }
        }

        private void Discover()
        {
            ISet<string> held;
            List<Func<ISet<string>, IEnumerable<(IWireViewDevice device, string source)>>> probes;
            lock (_gate)
            {
                held = new HashSet<string>(_openSources);
                probes = _probes.ToList();
            }

            foreach (var probe in probes)
            foreach (var (device, source) in probe(held))
            {
                if (HoldsSource(source)) { Dispose(device); continue; }
                ApplyPoll(device);
                TryAdd(device, source);
            }
        }

        private bool HoldsSource(string source)
        {
            lock (_gate) { return _openSources.Contains(source); }
        }

        private void ApplyPoll(IWireViewDevice device)
        {
            if (device is WireViewPro2Device s) s.PollIntervalMs = _pollMs;
            else if (device is HwmonDevice h) h.PollIntervalMs = _pollMs;
        }

        private void TryAdd(IWireViewDevice device, string source)
        {
            bool added = false;
            try
            {
                device.Connect();
                if (!device.Connected) { Dispose(device); return; }

                lock (_gate)
                {
                    string id = device.UniqueId;
                    // Same physical chip already managed (e.g. via the other source): keep the existing one.
                    if (string.IsNullOrEmpty(id) || _byId.ContainsKey(id))
                    {
                        Dispose(device);
                        return;
                    }

                    var md = new ManagedDevice(device, source);
                    _byId[id] = md;
                    _openSources.Add(source);

                    EventHandler<DeviceData> h = (_, d) => OnData(id, d);
                    _dataHandlers[device] = h;
                    device.DataUpdated += h;
                    device.ConnectionChanged += OnDeviceConnectionChanged;

                    _selectedId ??= id;
                    added = true;
                }
            }
            catch
            {
                Dispose(device);
                return;
            }

            if (added)
            {
                DevicesChanged?.Invoke(this, EventArgs.Empty);
                SelectedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void OnData(string id, DeviceData data)
        {
            lock (_gate)
            {
                if (_byId.TryGetValue(id, out var md)) md.Latest = data;
            }
            DataUpdated?.Invoke(this, (id, data));
        }

        private void OnDeviceConnectionChanged(object? sender, bool connected)
        {
            if (connected) return;
            if (sender is IWireViewDevice dev)
            {
                lock (_gate)
                {
                    var entry = _byId.FirstOrDefault(kv => ReferenceEquals(kv.Value.Device, dev));
                    if (entry.Value != null) Drop(entry.Value);
                }
                DevicesChanged?.Invoke(this, EventArgs.Empty);
                SelectedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>Remove a device from the collection (called under <see cref="_gate"/>).</summary>
        private void Drop(ManagedDevice md)
        {
            _byId.Remove(md.Id);
            _openSources.Remove(md.Source);
            if (_selectedId == md.Id) _selectedId = _byId.Keys.FirstOrDefault();

            var dev = md.Device;
            try
            {
                dev.ConnectionChanged -= OnDeviceConnectionChanged;
                if (_dataHandlers.TryGetValue(dev, out var h)) { dev.DataUpdated -= h; _dataHandlers.Remove(dev); }
            }
            catch { /* ignore */ }
            Dispose(dev);
        }

        private void Prune()
        {
            List<ManagedDevice> dead;
            lock (_gate) { dead = _byId.Values.Where(m => !m.Device.Connected).ToList(); }
            if (dead.Count == 0) return;
            lock (_gate) { foreach (var md in dead) Drop(md); }
            DevicesChanged?.Invoke(this, EventArgs.Empty);
            SelectedChanged?.Invoke(this, EventArgs.Empty);
        }

        private static void Dispose(IWireViewDevice dev)
        {
            try { dev.Disconnect(); } catch { /* ignore */ }
            try { (dev as IDisposable)?.Dispose(); } catch { /* ignore */ }
        }

        public void Dispose() => Stop();
    }
}
