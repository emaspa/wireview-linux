using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using WireView2.Device;
using WireView2.Net;
using WireView2.Services;

namespace WireView2.Mini;

/// <summary>What the tray shows about this PC's device.</summary>
/// <param name="DeviceName">The connected device, null when none.</param>
/// <param name="PowerW">Its latest total power.</param>
/// <param name="FaultText">Its active fault (<see cref="FaultMonitor.FormatFaults"/>), null when clear.</param>
/// <param name="Handover">The serial port is lent to a local client (the GUI).</param>
/// <param name="Flash">A firmware update in progress, null otherwise.</param>
/// <param name="FirmwareUpdate">The bundled firmware is newer than the device's.</param>
internal readonly record struct MiniStatus(string? DeviceName, double? PowerW, string? FaultText, bool Handover,
    FlashState? Flash = null, bool FirmwareUpdate = false);

/// <summary>One remote device (or an unreachable host) as the tray and the
/// control panel show it.</summary>
/// <param name="Host">The entry from the settings ("host" or "host:port").</param>
/// <param name="Endpoint">Its normalised URL.</param>
/// <param name="Online">A device answers there.</param>
/// <param name="HostName">The machine name it publishes.</param>
/// <param name="Readings">Its latest readings, null while offline.</param>
/// <param name="FaultText">Its active fault, null when clear.</param>
internal readonly record struct RemoteHostStatus(
    string Host, string Endpoint, bool Online, string? HostName, WireViewSensorDto? Readings, string? FaultText);

/// <summary>
/// WireView Mini's always-on work: owns this machine's WireView through its own
/// <see cref="DeviceManager"/> (hwmon when wireviewd runs, otherwise the serial
/// port, as in the GUI), watches the remote hosts from the settings with a
/// second one, publishes the local device through <see cref="LocalPublishHost"/>
/// (always: on all interfaces when LAN publishing is on, loopback only when it is
/// off, because the GUI reaches the device through it), raises fault alerts with
/// <see cref="FaultMonitor"/>, shuts the PC down on a local fault when the
/// setting asks for it, lends the serial port to the GUI on request, flashes
/// the bundled firmware on request (<see cref="Firmware"/>), reads and writes
/// the display theme on request (<see cref="Theme"/>), keeps the last readings in
/// memory for the panel's charts (<see cref="History"/>, one sample a tick) and
/// reads the on-device log on request (<see cref="DeviceLog"/>).
/// Settings changes made in the GUI are picked up every tick, and so are the
/// tray's (<see cref="RequestSettingsChange"/>).
/// </summary>
internal sealed class MiniService : IDisposable, IFlashHost, IThemeHost, IDeviceLogHost
{
    /// <summary>Handover window when the client does not ask for one, and the
    /// longest one granted: WireView Mini takes the port back after this even if
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
    private readonly ControlPanel _panel;
    private readonly DaemonConfigService _daemon;
    private readonly Stopwatch _uptime = Stopwatch.StartNew();

    private readonly object _gate = new();
    private readonly Dictionary<string, FaultMonitor> _monitors = new();
    private DateTime? _handoverUntil;     // set while the port is lent out
    private DateTime? _unfreezeBy;        // after a handover: stop showing the frozen readings by then
    private volatile bool _handoverActive; // _handoverUntil != null, readable without _gate
    private DateTime? _flashUntil;        // set while a firmware flash keeps the port closed
    private volatile bool _flashSuspended; // _flashUntil != null, readable without _gate

    // Last shown fault status per local device id (guarded by _monitors).
    private readonly Dictionary<string, ushort> _localFault = new();
    // Settings edits from the tray and the control panel, applied on the next
    // tick (guarded by itself); Done is set once they are applied.
    private readonly List<(string Source, string What, Action<AppSettings> Change, ManualResetEventSlim? Done)> _settingsChanges = new();
    private readonly List<ManualResetEventSlim> _settingsApplied = new();
    // Work for the tick from other threads (the listener moving off wireviewd's
    // port); Done is set once the tick has run it (guarded by _settingsChanges).
    private readonly List<(Action Work, ManualResetEventSlim Done)> _tickWork = new();
    private volatile bool _daemonMode;     // wireviewd serves this PC's device (Linux)
    // The last screen sent through RunLocalCommand: the theme's redraw returns there.
    private volatile int _lastScreen = (int)WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_MAIN;

    private (int Port, bool Lan)? _publishWanted;
    private (int Port, bool Lan)? _publishFailed;
    private DateTime _nextBindAttempt = DateTime.MinValue;
    private volatile string? _publishError;
    private volatile Tuple<int, bool>? _wantedView; // _publishWanted, readable from any thread
    private volatile bool _onFallback;     // listening on the loopback fallback port
    private string? _portHolder;           // who serves the wanted port, when known
    private int _lastFallbackPort;
    private int _pollMs = -1;
    private string? _historyDevice;        // UniqueId of the device the history is of

    /// <summary>Raised after every tick and on every reading of this PC's
    /// device, on the tick or the device thread. The tray rate-limits.</summary>
    public event Action? StatusChanged;

    public MiniService(string version, IToastNotifier toast, bool noShutdown)
    {
        _version = version;
        _toast = toast;
        _noShutdown = noShutdown;
        _localKey = MiniKey.LoadOrCreate();
        PanelToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        Instance = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        Firmware = new FirmwareService(this);
        Theme = new ThemeService(this);
        DeviceLog = new DeviceLogService(this);
        _panel = new ControlPanel(this);
        _daemon = new DaemonConfigService(() =>
            RunOnTick(() => _nextBindAttempt = DateTime.MinValue, TimeSpan.FromSeconds(5)));

        _remote = new DeviceManager(
            new RemoteDeviceProbe(RemoteHostList.Endpoints, () => AppSettings.Current.NetworkSecret).Probe);
        _publish = new LocalPublishHost(_local, version)
        {
            MiniVersion = version,
            CommandHook = HandleMiniCommand,
            LocalSecretProvider = () => _localKey,
            ExtraRoutes = _panel.Handle,
            ExtraRouteBodyLimit = _panel.BodyLimit,
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
        FileLog.Info($"wireview-mini {_version} started on {Environment.MachineName} ({Os})");
        WritePanelFile(); // again with the port once the listener is up

        while (!stop.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex) { FileLog.Warn($"tick failed: {ex.Message}"); }
            stop.WaitHandle.WaitOne(1000);
        }

        if (Firmware.Running)
            FileLog.Warn("stopping during a firmware update: the device may stay in its bootloader until it is flashed again");
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
        UpdateDaemonMode();
        var work = TakeTickWork();
        foreach (var w in work)
            try { w.Work(); } catch (Exception ex) { FileLog.Warn($"tick work failed: {ex.Message}"); }
        EnsurePublisher();
        SignalSettingsApplied();
        foreach (var w in work)
            try { w.Done.Set(); } catch (ObjectDisposedException) { } // the waiter gave up
        if (_daemonMode) _daemon.Tick();
        RecordHistory();

        lock (_gate)
        {
            if (_handoverUntil is { } until && DateTime.UtcNow >= until)
            {
                FileLog.Warn("serial handover window ran out: taking the port back");
                ResumeLocked();
            }
            if (_flashUntil is { } flashUntil && DateTime.UtcNow >= flashUntil)
            {
                FileLog.Warn("firmware update window ran out: taking the port back");
                ResumeFlashLocked();
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

    // ---- Tray and control panel ----

    public string Version => _version;

    /// <summary>Started with --no-shutdown: shutdown on fault only logs.</summary>
    public bool NoShutdown => _noShutdown;

    /// <summary>The control panel's API token: random, minted once per run.</summary>
    public string PanelToken { get; }

    /// <summary>A random id for this run, so an open control panel page can tell
    /// this WireView Mini on another port from a restarted one (whose token it
    /// does not have). Not a secret: the probe answers it without the token.</summary>
    public string Instance { get; }

    public TimeSpan Uptime => _uptime.Elapsed;

    internal LocalPublishHost Publisher => _publish;
    internal DaemonConfigService Daemon => _daemon;

    /// <summary>Linux with wireviewd serving this PC's device (hwmon + daemon):
    /// the daemon publishes it on the LAN, WireView Mini listens on loopback only and
    /// keeps off the daemon's port. Stays set through a daemon restart; cleared
    /// when WireView Mini opens the device itself (serial).</summary>
    public bool DaemonMode => _daemonMode;
    internal DeviceManager LocalDevices => _local;

    /// <summary>The serial port is lent to a local client.</summary>
    public bool HandoverActive => _handoverActive;

    /// <summary>The bundled firmware flasher (the panel's Firmware section).</summary>
    public FirmwareService Firmware { get; }

    /// <summary>The display theme editor's device side (the panel's Theme section).</summary>
    public ThemeService Theme { get; }

    /// <summary>The on-device log read (the panel's Device log section).</summary>
    public DeviceLogService DeviceLog { get; }

    /// <summary>The last <see cref="AppSettings.HistoryMinutes"/> of readings of
    /// this PC's device (the panel's Charts section).</summary>
    public HistoryBuffer History { get; } = new();

    /// <summary>Something else uses the serial port now (a theme transfer or a
    /// device log read): the device's readings pause meanwhile.</summary>
    public bool SerialTransferBusy => Theme.Busy || DeviceLog.Busy;

    /// <summary>The local device is out of use: lent to the GUI, or closed for a
    /// firmware flash. The panel shows the last readings meanwhile.</summary>
    public bool PortSuspended => _handoverActive || _flashSuspended;

    /// <summary>Why the publisher is not on the wanted port (the bind error, and
    /// the fallback port while one is used), null while it is.</summary>
    public string? PublishError => _onFallback || _publish.Endpoint == null ? _publishError : null;

    /// <summary>The wanted port is taken and the loopback fallback serves instead.</summary>
    public bool OnFallback => _onFallback;

    /// <summary>The port and LAN mode the settings ask for.</summary>
    public (int Port, bool Lan)? PublishWanted => _wantedView is { } w ? (w.Item1, w.Item2) : null;

    /// <summary>The control panel's address, with the token in the fragment, or
    /// null while WireView Mini is not listening.</summary>
    public string? PanelUrl => _publish.Endpoint is { } ep ? $"http://127.0.0.1:{ep.Port}/#t={PanelToken}" : null;

    /// <summary>The debounced fault text for a local device id, null when clear.</summary>
    public string? LocalFaultText(string id)
    {
        ushort fault;
        lock (_monitors) _localFault.TryGetValue(id, out fault);
        return fault != 0 ? FaultMonitor.FormatFaults(fault) : null;
    }

    /// <summary>Every remote host in the settings with its devices: one entry per
    /// device that answers, one offline entry for a host with none.</summary>
    public IReadOnlyList<RemoteHostStatus> GetRemoteHosts()
    {
        var list = new List<RemoteHostStatus>();
        var devices = _remote.Devices;
        foreach (var host in RemoteHostList.Endpoints().Select(h => h.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string url = RemoteDeviceProbe.Normalize(host);
            bool any = false;
            foreach (var md in devices)
            {
                if (md.Device is not NetworkDevice nd || nd.Endpoint != url || !nd.Connected || md.Latest is not { } data)
                    continue;
                any = true;
                list.Add(new RemoteHostStatus(host, url, true,
                    string.IsNullOrWhiteSpace(nd.HostName) ? null : nd.HostName,
                    WireViewSensorDto.FromDevice(nd, data),
                    data.FaultStatus != 0 ? FaultMonitor.FormatFaults(data.FaultStatus) : null));
            }
            if (!any) list.Add(new RemoteHostStatus(host, url, false, null, null, null));
        }
        return list;
    }

    /// <summary>Runs a device command on this PC's device through the same relay
    /// as a LAN command (direct serial, or hwmon through wireviewd).</summary>
    public DaemonResult RunLocalCommand(WireViewCommand cmd)
    {
        if (Firmware.Running) return DaemonResult.Busy;
        if (_handoverActive) return DaemonResult.NotConnected;
        var r = _publish.ExecuteCommand(cmd);
        if (r == DaemonResult.Ok && cmd.Op == "screen"
            && cmd.Cmd is >= (int)WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_MAIN and <= (int)WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_STATUS)
            _lastScreen = cmd.Cmd;
        return r;
    }

    /// <summary>Clears the active faults like the app's fault banner: every active
    /// fault (keep-mask 0), the fault log kept (keep-mask 0xFFFF), then a redraw of
    /// the current screen.</summary>
    public DaemonResult ClearLocalFaults()
    {
        var r = RunLocalCommand(WireViewCommand.Faults("", keepStatusMask: 0, keepLogMask: 0xFFFF));
        if (r == DaemonResult.Ok)
        {
            RunLocalCommand(WireViewCommand.Screen("", (int)WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_SAME));
            lock (_monitors)
                foreach (var id in _localFault.Keys.ToList()) _localFault[id] = 0;
            FileLog.Info("faults cleared on this PC's device");
        }
        return r;
    }

    /// <summary>This PC's device as the tray shows it. Never blocks on the
    /// handover lock, so it is safe from the device thread.</summary>
    public MiniStatus GetStatus()
    {
        var flash = Firmware.State;
        if (flash.Running) return new MiniStatus(null, null, null, Handover: false, Flash: flash);
        if (_handoverActive) return new MiniStatus(null, null, null, Handover: true);
        foreach (var d in _local.Devices)
        {
            if (!d.Device.Connected || d.Latest is not { } data) continue;
            ushort fault;
            lock (_monitors) _localFault.TryGetValue(d.Id, out fault);
            return new MiniStatus(d.Device.DeviceName, data.SumPowerW,
                fault != 0 ? FaultMonitor.FormatFaults(fault) : null, Handover: false,
                FirmwareUpdate: Firmware.UpdateAvailable());
        }
        return default;
    }

    /// <summary>The local device the panel shows and drives. With the hwmon
    /// module loaded but wireviewd stopped the manager lists both a read-only
    /// hwmon entry and the serial port, so prefer a device that takes commands,
    /// then a connected one, then one with readings.</summary>
    internal ManagedDevice? PreferredLocal() =>
        _local.Devices
            .Where(d => d.Device is not NetworkDevice)
            .OrderByDescending(d => CanWrite(d.Device))
            .ThenByDescending(d => d.Device.Connected)
            .ThenByDescending(d => d.Latest != null)
            .FirstOrDefault();

    /// <summary>The device takes commands: serial, or hwmon with wireviewd.</summary>
    internal static bool CanWrite(IWireViewDevice dev) =>
        dev is WireViewPro2Device || dev is HwmonDevice { DaemonAvailable: true };

    // ---- Firmware flashing (IFlashHost) ----

    FlashTarget? IFlashHost.FlashTarget()
    {
        if (PortSuspended) return null;
        var md = PreferredLocal();
        if (md == null) return null;
        var dev = md.Device;
        return new FlashTarget(md.Id, dev.DeviceName, dev.VendorId, dev.ProductId,
            dev.FirmwareVersion ?? "", dev.BuildString ?? "", dev.Connected,
            dev switch
            {
                WireViewPro2Device => FlashRoute.Serial,
                HwmonDevice { DaemonAvailable: true } => FlashRoute.Daemon,
                _ => FlashRoute.ReadOnly,
            });
    }

    DaemonResult IFlashHost.EnterBootloaderThroughDaemon()
    {
        var hwmon = PreferredLocal()?.Device as HwmonDevice;
        return hwmon?.EnterBootloader() ?? DaemonResult.NotConnected;
    }

    /// <summary>Like <see cref="Suspend"/>, but for WireView Mini's own flash: its
    /// own window (a GUI resumeSerial does not end it) and refused while the GUI
    /// holds the port. Returns once the port is closed.</summary>
    bool IFlashHost.SuspendForFlash(int seconds)
    {
        int window = Math.Clamp(seconds, 1, MaxHandoverSeconds);
        lock (_gate)
        {
            if (_handoverUntil != null) return false;
            bool rearm = _flashUntil != null;
            _flashUntil = DateTime.UtcNow.AddSeconds(window);
            _flashSuspended = true;
            if (rearm) return true;

            _unfreezeBy = null;
            _publish.FreezeDevices();
            // Wait for a discovery pass in flight, so no port is opened after this.
            _local.Stop(TimeSpan.FromSeconds(5));
            FileLog.Info($"firmware update: device closed and discovery paused for up to {window} s");
        }
        return true;
    }

    string? IFlashHost.EnterBootloaderDirect()
    {
        // The app's EnterBootloaderViaMini, with WireView Mini's port already closed.
        string? port = Stm32PortFinder.FindMatchingComPorts().FirstOrDefault();
        if (port == null)
            return "No WireView serial port found.";
        var direct = new WireViewPro2Device(port);
        try
        {
            direct.Connect();
            if (!direct.Connected)
                return $"Could not open {port}.";
            direct.EnterBootloader();
            FileLog.Info($"firmware update: bootloader command sent on {port}");
            return null;
        }
        finally
        {
            try { direct.Disconnect(); } catch { }
        }
    }

    void IFlashHost.ResumeAfterFlash()
    {
        lock (_gate) ResumeFlashLocked();
    }

    bool IFlashHost.SerialTransferRunning => SerialTransferBusy;

    // ---- Theme transfers (IThemeHost) ----

    bool IThemeHost.FlashRunning => Firmware.Running;

    bool IThemeHost.DeviceLogBusy => DeviceLog.Busy;

    FlashTarget? IThemeHost.ThemeTarget() => ((IFlashHost)this).FlashTarget();

    ConfigSnapshot? IThemeHost.ReadConfig() => _publish.ReadLocalConfig(null);

    DaemonResult IThemeHost.RunCommand(WireViewCommand cmd) => RunLocalCommand(cmd);

    WireViewPro2Device.SCREEN_CMD IThemeHost.LastScreen => (WireViewPro2Device.SCREEN_CMD)_lastScreen;

    /// <summary>The app's RunOnSerialDeviceAsync: WireView Mini's own port when it
    /// has the device on serial (the poll loop waits on the port lock meanwhile),
    /// else the port borrowed from wireviewd for the transfer.</summary>
    Task<T> IThemeHost.RunOnSerialAsync<T>(Func<IThemeDevice, CancellationToken, Task<T>> op, CancellationToken ct) =>
        RunOnSerialAsync((d, t) => op(new SerialThemeDevice(d), t), ct, "Theme assets require a locally connected device.");

    private Task<T> RunOnSerialAsync<T>(Func<WireViewPro2Device, CancellationToken, Task<T>> op, CancellationToken ct, string notConnected)
    {
        var dev = PreferredLocal()?.Device;
        if (dev is WireViewPro2Device { Connected: true } pro2)
            return op(pro2, ct);
        if (dev is HwmonDevice { Connected: true, DaemonAvailable: true } hwmon)
            return DirectSerialSession.RunAsync(hwmon, op, ct);
        throw new InvalidOperationException(notConnected);
    }

    // ---- Device log (IDeviceLogHost) ----

    bool IDeviceLogHost.FlashRunning => Firmware.Running;

    bool IDeviceLogHost.ThemeBusy => Theme.Busy;

    FlashTarget? IDeviceLogHost.LogTarget() => ((IFlashHost)this).FlashTarget();

    /// <summary>The app's Logging read: WireView Mini's own port in direct serial
    /// mode (the poll loop waits on the port lock meanwhile), else the port
    /// borrowed from wireviewd for the read.</summary>
    Task<byte[]> IDeviceLogHost.ReadLogAsync(IProgress<double> progress, CancellationToken ct) =>
        RunOnSerialAsync((d, t) => d.ReadDeviceLogAsync(progress, t), ct, "Not connected.");

    // ---- History ----

    /// <summary>One sample of this PC's device per tick into <see cref="History"/>.
    /// Not while the port is lent out or used for a flash, a theme transfer or a
    /// log read (the readings are frozen or stale then), and not the same reading
    /// twice. Another device starts a new history.</summary>
    private void RecordHistory()
    {
        if (History.Capacity == 0) return;
        if (PortSuspended || Firmware.Running || SerialTransferBusy) return;
        var md = PreferredLocal();
        if (md == null || !md.Device.Connected || md.Latest is not { Connected: true } data) return;
        string id = md.Device.UniqueId;
        if (!string.IsNullOrEmpty(id))
        {
            if (_historyDevice != null && _historyDevice != id)
            {
                FileLog.Info($"history: another device ({md.Device.DeviceName}), the chart history starts again");
                History.Clear();
            }
            _historyDevice = id;
        }
        History.Add(data);
    }

    private void ResumeFlashLocked()
    {
        if (_flashUntil == null) return;
        _flashUntil = null;
        _flashSuspended = false;
        RestartLocalLocked();
    }

    /// <summary>Queues a settings edit (the tray's checkboxes, the control panel).
    /// The next tick applies it on top of a fresh read of settings.json, saves the
    /// file and applies the settings (and rebinds the publisher), the same path a
    /// GUI edit takes. The save records the file's new write time, so
    /// <see cref="AppSettings.ReloadIfChanged"/> does not see WireView Mini's own write
    /// as a change. Returns once applied when <paramref name="wait"/> is set
    /// (true), or false when the tick did not get to it in that time.</summary>
    public bool RequestSettingsChange(string what, Action<AppSettings> change, string source = "tray", TimeSpan? wait = null)
    {
        using var done = wait != null ? new ManualResetEventSlim() : null;
        lock (_settingsChanges) _settingsChanges.Add((source, what, change, done));
        return done?.Wait(wait!.Value) ?? true;
    }

    /// <summary>Runs <paramref name="work"/> on the next tick, before the publisher
    /// is checked, and returns once that tick is done (false after <paramref name="wait"/>).</summary>
    public bool RunOnTick(Action work, TimeSpan wait)
    {
        using var done = new ManualResetEventSlim();
        lock (_settingsChanges) _tickWork.Add((work, done));
        return done.Wait(wait);
    }

    private (Action Work, ManualResetEventSlim Done)[] TakeTickWork()
    {
        lock (_settingsChanges)
        {
            if (_tickWork.Count == 0) return [];
            var work = _tickWork.ToArray();
            _tickWork.Clear();
            return work;
        }
    }

    private void UpdateDaemonMode()
    {
        if (!OperatingSystem.IsLinux()) return;
        var devices = _local.Devices;
        if (!_daemonMode && devices.Any(d => d.Device is HwmonDevice { DaemonAvailable: true }))
        {
            _daemonMode = true;
            FileLog.Info("wireviewd serves this PC's device: it does the LAN publishing, WireView Mini listens on loopback only");
        }
        else if (_daemonMode && devices.Any(d => d.Device is not HwmonDevice && d.Device is not NetworkDevice && d.Device.Connected))
        {
            _daemonMode = false;
            FileLog.Info("WireView Mini opened the device itself: wireviewd no longer serves it, LAN publishing follows WireView Mini's setting");
        }
    }

    private void ApplyRequestedSettings()
    {
        (string Source, string What, Action<AppSettings> Change, ManualResetEventSlim? Done)[] pending;
        lock (_settingsChanges)
        {
            if (_settingsChanges.Count == 0) return;
            pending = _settingsChanges.ToArray();
            _settingsChanges.Clear();
        }
        foreach (var p in pending) p.Change(AppSettings.Current);
        foreach (var group in pending.GroupBy(p => p.Source))
        {
            string what = string.Join(", ", group.Select(p => p.What));
            try
            {
                AppSettings.SaveCurrent();
                FileLog.Info($"{group.Key}: {what}; saved to settings.json");
            }
            catch (Exception ex)
            {
                FileLog.Warn($"{group.Key}: {what}, but settings.json could not be saved: {ex.Message}");
            }
        }
        ApplySettings();
        lock (_settingsApplied)
            foreach (var p in pending)
                if (p.Done != null) _settingsApplied.Add(p.Done);
    }

    private void SignalSettingsApplied()
    {
        lock (_settingsApplied)
        {
            foreach (var done in _settingsApplied)
                try { done.Set(); } catch (ObjectDisposedException) { } // the waiter gave up
            _settingsApplied.Clear();
        }
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
        _wantedView = Tuple.Create(s.PublishPort, s.PublishEnabled);

        int samples = s.EffectiveHistoryMinutes * 60;
        if (samples != History.Capacity)
        {
            History.Resize(samples);
            FileLog.Info(samples == 0
                ? "chart history off (HistoryMinutes 0)"
                : $"chart history: {s.EffectiveHistoryMinutes} min, {samples} samples, {History.ResidentBytes / 1024} KB");
        }
    }

    // ---- Publishing ----

    /// <summary>Binds the publisher to the wanted port (all interfaces with LAN
    /// publishing on, loopback otherwise). When that port is taken (wireviewd holds
    /// 9876 on Linux), a loopback-only fallback listener serves the same routes on
    /// the next free port of PublishPort+1 to +10, else an ephemeral one, so the
    /// control panel, --open and --status keep working; the wanted port is retried
    /// every 30 s and the listener moves there once it is free. The port in use is
    /// written next to the panel token for --open and --status.</summary>
    private void EnsurePublisher()
    {
        if (_publishWanted is not { } setting) return;
        bool daemon = _daemonMode;
        // With wireviewd the daemon publishes on the LAN (as the GUI leaves it to
        // it on Linux): loopback only, and never on a port the daemon uses.
        var want = (Port: setting.Port, Lan: setting.Lan && !daemon);
        var current = _publish.Endpoint;
        if (daemon && current is { } held && _daemon.IsReserved(held.Port))
        {
            FileLog.Info($"port {held.Port} is wireviewd's: moving WireView Mini's listener off it");
            _publish.Stop();
            current = null;
            _onFallback = false;
        }
        if (daemon && _daemon.IsReserved(want.Port))
        {
            if (_onFallback && current != null) return;
            _publishFailed = want;
            _nextBindAttempt = DateTime.UtcNow + BindRetry;
            _portHolder = "wireviewd";
            _publishError = $"port {want.Port} is left to wireviewd, which publishes this PC's device on the LAN";
            StartFallback(want.Port, logIt: true);
            return;
        }
        bool lan = current is { } c && !IPAddress.IsLoopback(c.Address);
        if (!_onFallback && current is { } cur && cur.Port == want.Port && lan == want.Lan) return;

        if (_publishFailed == want && DateTime.UtcNow < _nextBindAttempt) return;
        var address = want.Lan ? IPAddress.Any : IPAddress.Loopback;

        // On the fallback, move only once the wanted port is free, so the panel
        // stays reachable meanwhile.
        if (_onFallback && current != null && _publishFailed == want && ControlPanel.PortProblem(want.Port, want.Lan) != null)
        {
            _nextBindAttempt = DateTime.UtcNow + BindRetry;
            return;
        }

        bool wasFallback = _onFallback;
        if (current != null) _publish.Stop();
        _onFallback = false;
        try
        {
            _publish.Start(want.Port, address);
            _publishFailed = null;
            _publishError = null;
            if (wasFallback) FileLog.Info($"port {want.Port} is free again: the fallback listener is closed");
            FileLog.Info(want.Lan
                ? $"publishing on port {want.Port}, all interfaces (LAN publishing on)"
                : daemon ? $"listening on 127.0.0.1:{want.Port} only (wireviewd does the LAN publishing)"
                : $"publishing on 127.0.0.1:{want.Port} only (LAN publishing off)");
            WritePanelFile();
        }
        catch (Exception ex)
        {
            bool firstFailure = _publishFailed != want;
            _publishFailed = want;
            _nextBindAttempt = DateTime.UtcNow + BindRetry;
            if (firstFailure)
            {
                // Name the holder when it is a WireView publisher (wireviewd on Linux).
                _portHolder = LocalMiniProbe.TryFetch(want.Port, TimeSpan.FromSeconds(1)) is { } snap
                    ? (string.IsNullOrEmpty(snap.AppVersion) ? "another WireView publisher" : snap.AppVersion)
                    : null;
                FileLog.Warn($"cannot listen on port {want.Port}" +
                             (_portHolder != null ? $" (it is served by {_portHolder})" : "") + $": {ex.Message}. " +
                             $"Not publishing on it; retrying every {BindRetry.TotalSeconds:0} s. Alerts and shutdown keep working.");
            }
            _publishError = $"cannot listen on port {want.Port}" +
                            (_portHolder != null ? $" (served by {_portHolder})" : "") + $": {ex.Message}";
            StartFallback(want.Port, logIt: firstFailure || wasFallback);
        }
    }

    private void StartFallback(int wanted, bool logIt)
    {
        var ports = Enumerable.Range(wanted + 1, 10)
            .Where(p => p <= 65535 && !(_daemonMode && _daemon.IsReserved(p))).Append(0);
        foreach (int port in ports)
        {
            try { _publish.Start(port, IPAddress.Loopback); }
            catch (Exception) { continue; }
            _onFallback = true;
            int bound = _publish.Endpoint?.Port ?? port;
            _publishError += $". The control panel and /sensors are on 127.0.0.1:{bound} (loopback only) " +
                             $"until port {wanted} is free; retrying every {BindRetry.TotalSeconds:0} s";
            if (logIt || bound != _lastFallbackPort)
                FileLog.Info($"control panel on http://127.0.0.1:{bound} (port {wanted} is " +
                             (_portHolder != null ? $"served by {_portHolder})" : "in use)"));
            _lastFallbackPort = bound;
            WritePanelFile();
            return;
        }
        FileLog.Warn("no fallback port could be bound either: the control panel is unavailable until the next retry");
    }

    /// <summary>Writes the panel token and the port in use for --open and --status.</summary>
    private void WritePanelFile()
    {
        try { MiniPanelToken.Write(PanelToken, _publish.Endpoint?.Port); }
        catch (Exception ex) { FileLog.Warn($"cannot write {MiniPanelToken.FilePath} (wireview-mini --open needs it): {ex.Message}"); }
    }

    // ---- Serial handover ----

    private DaemonResult? HandleMiniCommand(WireViewCommand cmd)
    {
        switch (cmd.Op)
        {
            case WireViewCommand.SuspendSerialOp:
                // WireView Mini is flashing the device or moving theme artwork
                // itself: the app must not open the port in the middle of it.
                if (!Suspend(cmd.Seconds))
                {
                    FileLog.Warn(Theme.Busy ? "serial handover refused: a theme transfer is running"
                        : DeviceLog.Busy ? "serial handover refused: the device log is being read"
                        : "serial handover refused: a firmware update is running");
                    return DaemonResult.Busy;
                }
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
                // No device commands from the LAN or the app while flashing.
                return Firmware.Running ? DaemonResult.Busy : null;
        }
    }

    /// <summary>Closes the port (stops the local manager) and keeps the last
    /// readings published, marked not connected. Re-sending extends the window.
    /// Returns once the port is closed, so the client can open it right away;
    /// false (nothing done) while WireView Mini flashes the firmware, moves theme
    /// artwork or reads the device log over the port.</summary>
    private bool Suspend(int seconds)
    {
        int window = seconds <= 0 ? DefaultHandoverSeconds : Math.Clamp(seconds, 1, MaxHandoverSeconds);
        lock (_gate)
        {
            if (_flashUntil != null || Firmware.Running || SerialTransferBusy) return false;
            bool rearm = _handoverUntil != null;
            _handoverUntil = DateTime.UtcNow.AddSeconds(window);
            _handoverActive = true;
            if (rearm) return true;

            _unfreezeBy = null;
            _publish.FreezeDevices();
            // Wait for a discovery pass in flight, so no port is opened after this.
            _local.Stop(TimeSpan.FromSeconds(5));
            FileLog.Info($"serial port handed over to a local client for up to {window} s");
        }
        return true;
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
        RestartLocalLocked();
    }

    private void RestartLocalLocked()
    {
        _unfreezeBy = DateTime.UtcNow + UnfreezeGrace;
        _local.SetPollInterval(_pollMs);
        _local.Start();
    }

    // ---- Fault alerts ----

    /// <summary>The control panel's Send a test notification: one notification
    /// through the notifier the fault alerts use. Returns whether the desktop took
    /// it and a sentence for the user (why not, as the notifier logs it).</summary>
    public (bool Ok, string Message) TestNotification()
    {
        FileLog.Info("control panel: test notification");
        if (_toast.Show("WireView Mini", "This is how a fault alert looks on this desktop."))
            return (true, "Notification sent.");
#if WINDOWS
        if (Win32TrayIcon.Current == null)
            return (false, "Not shown: WireView Mini has no tray icon (no notification area, or it runs with --no-tray).");
#endif
        if (_toast is NullToastNotifier)
            return (false, "Not shown: this system has no notifier.");
        if (_toast is LinuxToastNotifier && !LinuxToastNotifier.Found())
            return (false, "Not shown: notify-send was not found (install libnotify-bin, or libnotify).");
        return (false, "Not shown: the desktop refused it, see the log.");
    }

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
        // own WireView Mini or GUI handles that host.
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
        MiniPanelToken.Delete();
        _publish.Stop();
        _local.Stop(TimeSpan.FromSeconds(3));
        _remote.Stop();
    }
}
