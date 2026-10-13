using System.Net;
using WireView2.Device;
using WireView2.Net;
using WireView2.Services;

namespace WireView2.Agent;

/// <summary>What the tray shows about this PC's device.</summary>
/// <param name="DeviceName">The connected device, null when none.</param>
/// <param name="PowerW">Its latest total power.</param>
/// <param name="FaultText">Its active fault (<see cref="FaultMonitor.FormatFaults"/>), null when clear.</param>
/// <param name="Handover">The serial port is lent to a local client (the GUI).</param>
internal readonly record struct AgentStatus(string? DeviceName, double? PowerW, string? FaultText, bool Handover);

/// <summary>
/// The agent's always-on work: owns this machine's WireView through its own
/// <see cref="DeviceManager"/> (hwmon when wireviewd runs, otherwise the serial
/// port, as in the GUI), watches the remote hosts from the settings with a
/// second one, publishes the local device through <see cref="LocalPublishHost"/>
/// (always: on all interfaces when LAN publishing is on, loopback only when it is
/// off, because the GUI reaches the device through it), raises fault alerts with
/// <see cref="FaultMonitor"/>, shuts the PC down on a local fault when the
/// setting asks for it, and lends the serial port to the GUI on request.
/// Settings changes made in the GUI are picked up every tick, and so are the
/// tray's (<see cref="RequestSettingsChange"/>).
/// </summary>
internal sealed class AgentService : IDisposable
{
    /// <summary>Handover window when the client does not ask for one, and the
    /// longest one granted: the agent takes the port back after this even if
    /// the client never hands it back (crashed GUI).</summary>
    internal const int DefaultHandoverSeconds = 600;
    internal const int MaxHandoverSeconds = 600;

    // After the handover, the last readings stay published (not connected)
    // until the device is back, so the GUI keeps the device listed.
    private static readonly TimeSpan UnfreezeGrace = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan BindRetry = TimeSpan.FromSeconds(30);

    private readonly string _version;
    private readonly IToastNotifier _toast;
    private readonly bool _noShutdown;
    private readonly string _localKey;

    private readonly DeviceManager _local = new();
    private readonly DeviceManager _remote;
    private readonly LocalPublishHost _publish;

    private readonly object _gate = new();
    private readonly Dictionary<string, FaultMonitor> _monitors = new();
    private DateTime? _handoverUntil;     // set while the port is lent out
    private DateTime? _unfreezeBy;        // after a handover: stop showing the frozen readings by then
    private volatile bool _handoverActive; // _handoverUntil != null, readable without _gate

    // Last shown fault status per local device id (guarded by _monitors).
    private readonly Dictionary<string, ushort> _localFault = new();
    // Settings edits from the tray, applied on the next tick (guarded by itself).
    private readonly List<(string What, Action<AppSettings> Change)> _settingsChanges = new();

    private (int Port, bool Lan)? _publishWanted;
    private (int Port, bool Lan)? _publishFailed;
    private DateTime _nextBindAttempt = DateTime.MinValue;
    private int _pollMs = -1;

    /// <summary>Raised after every tick and on every reading of this PC's
    /// device, on the tick or the device thread. The tray rate-limits.</summary>
    public event Action? StatusChanged;

    public AgentService(string version, IToastNotifier toast, bool noShutdown)
    {
        _version = version;
        _toast = toast;
        _noShutdown = noShutdown;
        _localKey = AgentKey.LoadOrCreate();

        _remote = new DeviceManager(
            new RemoteDeviceProbe(RemoteHostList.Endpoints, () => AppSettings.Current.NetworkSecret).Probe);
        _publish = new LocalPublishHost(_local, version)
        {
            AgentVersion = version,
            CommandHook = HandleAgentCommand,
            LocalSecretProvider = () => _localKey,
        };

        _local.DataUpdated += (_, e) => OnData(_local, e.Id, e.Data, local: true);
        _remote.DataUpdated += (_, e) => OnData(_remote, e.Id, e.Data, local: false);
        _local.DevicesChanged += (_, _) => LogDevices(_local, "local");
        _remote.DevicesChanged += (_, _) => LogDevices(_remote, "remote");
    }

    /// <summary>Runs until <paramref name="stop"/> is cancelled, then closes
    /// the port and the listener.</summary>
    public void Run(CancellationToken stop)
    {
        ApplySettings();
        _local.Start();
        _remote.Start();
        FileLog.Info($"wireview-agent {_version} started on {Environment.MachineName} ({Os})");

        while (!stop.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex) { FileLog.Warn($"tick failed: {ex.Message}"); }
            stop.WaitHandle.WaitOne(1000);
        }

        FileLog.Info("stopping: closing the device and the listener");
        Dispose();
    }

    private void Tick()
    {
        if (AppSettings.ReloadIfChanged())
        {
            FileLog.Info("settings changed on disk: reloaded");
            FileLog.SetRetentionDays(AppSettings.Current.LogRetentionDays);
            ApplySettings();
        }

        ApplyRequestedSettings();
        EnsurePublisher();

        lock (_gate)
        {
            if (_handoverUntil is { } until && DateTime.UtcNow >= until)
            {
                FileLog.Warn("serial handover window ran out: taking the port back");
                ResumeLocked();
            }
            if (_unfreezeBy is { } by &&
                (DateTime.UtcNow >= by || _local.Devices.Any(d => d.Latest != null)))
            {
                _unfreezeBy = null;
                _publish.Unfreeze();
            }
        }
        RaiseStatusChanged();
    }

    // ---- Tray ----

    /// <summary>This PC's device as the tray shows it. Never blocks on the
    /// handover lock, so it is safe from the device thread.</summary>
    public AgentStatus GetStatus()
    {
        if (_handoverActive) return new AgentStatus(null, null, null, Handover: true);
        foreach (var d in _local.Devices)
        {
            if (!d.Device.Connected || d.Latest is not { } data) continue;
            ushort fault;
            lock (_monitors) _localFault.TryGetValue(d.Id, out fault);
            return new AgentStatus(d.Device.DeviceName, data.SumPowerW,
                fault != 0 ? FaultMonitor.FormatFaults(fault) : null, Handover: false);
        }
        return default;
    }

    /// <summary>Queues a settings edit (the tray's checkboxes). The next tick
    /// applies it on top of a fresh read of settings.json, saves the file and
    /// applies the settings, the same path a GUI edit takes. The save records the
    /// file's new write time, so <see cref="AppSettings.ReloadIfChanged"/> does not
    /// see the agent's own write as a change.</summary>
    public void RequestSettingsChange(string what, Action<AppSettings> change)
    {
        lock (_settingsChanges) _settingsChanges.Add((what, change));
    }

    private void ApplyRequestedSettings()
    {
        (string What, Action<AppSettings> Change)[] pending;
        lock (_settingsChanges)
        {
            if (_settingsChanges.Count == 0) return;
            pending = _settingsChanges.ToArray();
            _settingsChanges.Clear();
        }
        foreach (var (_, change) in pending) change(AppSettings.Current);
        string what = string.Join(", ", pending.Select(p => p.What));
        try
        {
            AppSettings.SaveCurrent();
            FileLog.Info($"tray: {what}; saved to settings.json");
        }
        catch (Exception ex)
        {
            FileLog.Warn($"tray: {what}, but settings.json could not be saved: {ex.Message}");
        }
        ApplySettings();
    }

    private void RaiseStatusChanged()
    {
        try { StatusChanged?.Invoke(); }
        catch (Exception ex) { FileLog.Warn($"tray update failed: {ex.Message}"); }
    }

    private void ApplySettings()
    {
        var s = AppSettings.Current;
        int poll = Math.Clamp(s.MonitoringUpdateIntervalMs, 50, 5000);
        if (poll != _pollMs)
        {
            _pollMs = poll;
            _local.SetPollInterval(poll);
        }
        _publishWanted = (s.PublishPort, s.PublishEnabled);
    }

    // ---- Publishing ----

    private void EnsurePublisher()
    {
        if (_publishWanted is not { } want) return;
        var current = _publish.Endpoint;
        bool lan = current is { } c && !IPAddress.IsLoopback(c.Address);
        if (current is { } cur && cur.Port == want.Port && lan == want.Lan) return;

        if (_publishFailed == want && DateTime.UtcNow < _nextBindAttempt) return;

        if (current != null) _publish.Stop();
        var address = want.Lan ? IPAddress.Any : IPAddress.Loopback;
        try
        {
            _publish.Start(want.Port, address);
            _publishFailed = null;
            FileLog.Info(want.Lan
                ? $"publishing on port {want.Port}, all interfaces (LAN publishing on)"
                : $"publishing on 127.0.0.1:{want.Port} only (LAN publishing off)");
        }
        catch (Exception ex)
        {
            bool firstFailure = _publishFailed != want;
            _publishFailed = want;
            _nextBindAttempt = DateTime.UtcNow + BindRetry;
            if (firstFailure)
            {
                // Name the holder when it is a WireView publisher (wireviewd on Linux).
                string holder = LocalAgentProbe.TryFetch(want.Port, TimeSpan.FromSeconds(1)) is { } snap
                    ? $" (it is served by {(string.IsNullOrEmpty(snap.AppVersion) ? "another WireView publisher" : snap.AppVersion)})"
                    : "";
                FileLog.Warn($"cannot listen on port {want.Port}{holder}: {ex.Message}. " +
                             $"Not publishing; retrying every {BindRetry.TotalSeconds:0} s. Alerts and shutdown keep working.");
            }
        }
    }

    // ---- Serial handover ----

    private DaemonResult? HandleAgentCommand(WireViewCommand cmd)
    {
        switch (cmd.Op)
        {
            case WireViewCommand.SuspendSerialOp:
                Suspend(cmd.Seconds);
                return DaemonResult.Ok;
            case WireViewCommand.ResumeSerialOp:
                bool resumed = false;
                lock (_gate)
                {
                    if (_handoverUntil != null)
                    {
                        FileLog.Info("serial port handed back by the client");
                        ResumeLocked();
                        resumed = true;
                    }
                }
                // Answer once the device is back (briefly), so the client's next
                // command finds it instead of "no device".
                if (resumed) WaitForLocalDevice(ResumeWait);
                return DaemonResult.Ok;
            default:
                return null;
        }
    }

    /// <summary>Closes the port (stops the local manager) and keeps the last
    /// readings published, marked not connected. Re-sending extends the window.
    /// Returns once the port is closed, so the client can open it right away.</summary>
    private void Suspend(int seconds)
    {
        int window = seconds <= 0 ? DefaultHandoverSeconds : Math.Clamp(seconds, 1, MaxHandoverSeconds);
        lock (_gate)
        {
            bool rearm = _handoverUntil != null;
            _handoverUntil = DateTime.UtcNow.AddSeconds(window);
            _handoverActive = true;
            if (rearm) return;

            _unfreezeBy = null;
            _publish.FreezeDevices();
            // Wait for a discovery pass in flight, so no port is opened after this.
            _local.Stop(TimeSpan.FromSeconds(5));
            FileLog.Info($"serial port handed over to a local client for up to {window} s");
        }
    }

    private static readonly TimeSpan ResumeWait = TimeSpan.FromSeconds(3);

    private void WaitForLocalDevice(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until && !_local.Devices.Any(d => d.Device.Connected))
            Thread.Sleep(100);
    }

    private void ResumeLocked()
    {
        _handoverUntil = null;
        _handoverActive = false;
        _unfreezeBy = DateTime.UtcNow + UnfreezeGrace;
        _local.SetPollInterval(_pollMs);
        _local.Start();
    }

    // ---- Fault alerts ----

    private void OnData(DeviceManager mgr, string id, DeviceData data, bool local)
    {
        FaultMonitor monitor;
        lock (_monitors)
        {
            if (!_monitors.TryGetValue((local ? "local|" : "remote|") + id, out monitor!))
                _monitors[(local ? "local|" : "remote|") + id] = monitor = new FaultMonitor();
        }

        FaultEvent fault;
        lock (monitor) fault = monitor.Observe(data);
        if (local)
        {
            lock (_monitors) _localFault[id] = fault.Status;
            RaiseStatusChanged();
        }
        if (!fault.Raised) return;

        var device = mgr.Devices.FirstOrDefault(d => d.Id == id)?.Device;
        string where = device is NetworkDevice nd
            ? (string.IsNullOrWhiteSpace(nd.HostName) ? nd.Endpoint.Replace("http://", "") : nd.HostName)
            : Environment.MachineName;
        // Same title and text as the GUI; a remote host's alert names the host.
        string body = local ? fault.Text : $"{fault.Text} ({where})";
        FileLog.Warn($"fault on {device?.DeviceName ?? id} at {where}: {fault.Text}");
        _toast.Show("WireView Fault", body);

        // Only this machine's device powers this machine off; a remote host's
        // own agent or GUI handles that host.
        if (local && AppSettings.Current.SoftwareShutdownOnFault)
        {
            if (_noShutdown)
            {
                FileLog.Warn("shutdown on fault is on, but --no-shutdown was given: not shutting down");
                return;
            }
            FileLog.Warn("shutdown on fault: powering off");
            SystemPower.Shutdown();
        }
    }

    private static void LogDevices(DeviceManager mgr, string kind)
    {
        var list = mgr.Devices;
        FileLog.Info(list.Count == 0
            ? $"{kind} devices: none"
            : $"{kind} devices: " + string.Join(", ", list.Select(d => $"{d.Device.DeviceName} [{d.Source}]")));
    }

    private static string Os =>
        OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";

    public void Dispose()
    {
        _publish.Stop();
        _local.Stop(TimeSpan.FromSeconds(3));
        _remote.Stop();
    }
}
