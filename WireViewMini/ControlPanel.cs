using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using WireView2.Device;
using WireView2.Net;
using WireView2.Services;

namespace WireView2.Mini;

/// <summary>
/// The control panel: a single page (the embedded <c>ControlPanel.html</c>) and
/// its JSON API, served on WireView Mini's publish port through
/// <see cref="LocalPublishHost.ExtraRoutes"/>. Everything here answers loopback
/// peers only (403 "local only" from any other address, even while the port is
/// open on the LAN), and every <c>/api/</c> request must carry the run's token
/// (<see cref="MiniService.PanelToken"/>) in <c>X-Mini-Token</c> (401 without
/// it). No CORS headers, so other web pages cannot read the answers.
///
/// API: GET /api/status, GET /api/config, POST /api/config, POST /api/command,
/// POST /api/settings, POST /api/open-app, and on Linux GET and POST
/// /api/daemon-config (wireviewd's settings through wireviewctl, see
/// <see cref="DaemonConfigService"/>). Device writes go through the same
/// relay as a LAN command (<see cref="LocalPublishHost.ExecuteCommand"/>), so
/// they work over direct serial and over hwmon with wireviewd alike.
/// </summary>
internal sealed class ControlPanel
{
    public const string TokenHeader = "X-Mini-Token";
    private const string PageResource = "wireview-mini.ControlPanel.html";

    // Screen commands the panel and the tray send (no SCREEN_GOTO_SAME on its own,
    // it only follows a write).
    private static readonly HashSet<int> ScreenCommands =
    [
        (int)WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_MAIN,
        (int)WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_SIMPLE,
        (int)WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_CURRENT,
        (int)WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_TEMP,
        (int)WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_STATUS,
        (int)WireViewPro2Device.SCREEN_CMD.SCREEN_PAUSE_UPDATES,
        (int)WireViewPro2Device.SCREEN_CMD.SCREEN_RESUME_UPDATES,
    ];

    // Config memory only; the calibration commands are not offered.
    private static readonly Dictionary<int, string> NvmCommands = new()
    {
        [(int)WireViewPro2Device.NVM_CMD.NVM_CMD_LOAD] = "reloaded from device memory",
        [(int)WireViewPro2Device.NVM_CMD.NVM_CMD_STORE] = "stored to device memory",
        [(int)WireViewPro2Device.NVM_CMD.NVM_CMD_RESET] = "reset to defaults",
    };

    private readonly MiniService _mini;
    private readonly object _writeGate = new(); // one device write at a time

    public ControlPanel(MiniService mini) => _mini = mini;

    /// <summary>The publisher's extra-routes hook; null for paths that are not ours.</summary>
    public HttpResponseInfo? Handle(HttpRequestInfo req)
    {
        bool page = req.Path is "/" or "/panel" or "/index.html";
        bool api = req.Path.StartsWith("/api/", StringComparison.Ordinal);
        if (!page && !api) return null;

        if (!req.IsLoopback)
        {
            FileLog.Warn($"control panel: {req.Method} {req.Path} from {req.RemoteIp} refused (local only)");
            return Error(403, "local only");
        }

        if (page)
            return req.Method is "GET" or "HEAD" ? Page() : Error(405, "method not allowed");

        if (!TokenMatches(req.Header(TokenHeader)))
            return Error(401, "missing or wrong control panel token; open the panel from the tray or with wireview-mini --open");

        try
        {
            return (req.Method, req.Path) switch
            {
                ("GET", "/api/status") => Ok(Status(), MiniJsonContext.Default.PanelStatus),
                ("GET", "/api/config") => GetConfig(),
                ("POST", "/api/config") => PostConfig(req.Body),
                ("POST", "/api/command") => PostCommand(req.Body),
                ("POST", "/api/settings") => PostSettings(req.Body),
                ("POST", "/api/open-app") => OpenApp(),
                ("GET", "/api/daemon-config") => GetDaemonConfig(),
                ("POST", "/api/daemon-config") => PostDaemonConfig(req.Body),
                (_, "/api/status" or "/api/config" or "/api/command" or "/api/settings" or "/api/open-app" or "/api/daemon-config")
                    => Error(405, "method not allowed"),
                _ => Error(404, "no such API"),
            };
        }
        catch (JsonException ex)
        {
            return Error(400, $"bad JSON: {ex.Message}");
        }
    }

    private bool TokenMatches(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        byte[] want = Encoding.ASCII.GetBytes(_mini.PanelToken);
        byte[] got = Encoding.ASCII.GetBytes(token.Trim());
        return want.Length == got.Length && CryptographicOperations.FixedTimeEquals(want, got);
    }

    // ---- Page ----

    private static HttpResponseInfo Page()
    {
        using var stream = typeof(ControlPanel).Assembly.GetManifestResourceStream(PageResource);
        if (stream == null) return Error(500, "the control panel page is missing from this build");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return new HttpResponseInfo(200, "text/html; charset=utf-8", ms.ToArray())
        {
            Headers =
            [
                new("Cache-Control", "no-store"),
                new("X-Frame-Options", "DENY"),
                new("X-Content-Type-Options", "nosniff"),
                new("Referrer-Policy", "no-referrer"),
                new("Content-Security-Policy",
                    "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; " +
                    "connect-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'none'"),
            ],
        };
    }

    // ---- Status ----

    private PanelStatus Status()
    {
        var s = AppSettings.Current;
        var ep = _mini.Publisher.Endpoint;
        string? gui = GuiLauncher.Locate();
        return new PanelStatus(
            Mini: new PanelMiniInfo(
                Version: _mini.Version,
                UptimeSeconds: (long)_mini.Uptime.TotalSeconds,
                Os: OsName,
                Host: Environment.MachineName,
                LogPath: FileLog.CurrentPath,
                SettingsPath: AppSettings.GetSettingsPath(),
                NoShutdown: _mini.NoShutdown),
            Publish: new PanelPublishInfo(
                Listening: ep != null,
                Address: ep?.Address.ToString(),
                Port: ep?.Port,
                Lan: ep is { } e && !IPAddress.IsLoopback(e.Address),
                Error: _mini.PublishError,
                Fallback: _mini.OnFallback,
                WantedPort: _mini.PublishWanted?.Port,
                WantedLan: _mini.PublishWanted?.Lan),
            Device: LocalDevice(),
            Daemon: DaemonView(),
            RemoteHosts: _mini.GetRemoteHosts()
                .Select(h => new PanelRemoteHost(h.Host, h.Endpoint, h.Online, h.HostName, h.Readings, h.FaultText))
                .ToList(),
            Settings: SettingsView(s),
            Gui: new PanelGui(Location: gui, Available: gui != null));
    }

    private static PanelSettings SettingsView(AppSettings s) => new(
        ShutdownOnFault: s.SoftwareShutdownOnFault,
        StartAtLogin: MiniInstaller.IsInstalled(),
        PublishEnabled: s.PublishEnabled,
        PublishPort: s.PublishPort,
        RemoteHosts: s.RemoteHosts ?? new List<string>(),
        NetworkSecretSet: !string.IsNullOrEmpty(s.NetworkSecret),
        NetworkSecret: s.NetworkSecret ?? "");

    private PanelDevice? LocalDevice()
    {
        if (_mini.HandoverActive)
        {
            var frozen = _mini.Publisher.BuildSnapshot().Devices.FirstOrDefault();
            return new PanelDevice(
                Name: frozen?.Name,
                Firmware: frozen?.FwVer,
                BuildString: frozen?.BuildString,
                UniqueId: frozen?.Id,
                Source: null,
                Connected: false,
                SerialSuspended: true,
                CanWrite: false,
                Readings: frozen,
                FaultStatus: frozen?.FaultStatus ?? 0,
                FaultLog: frozen?.FaultLog ?? 0,
                FaultText: null,
                FaultLogText: null);
        }

        var md = PreferredLocal();
        if (md == null) return null;
        var dev = md.Device;
        var latest = md.Latest;
        int status = latest?.FaultStatus ?? 0, log = latest?.FaultLog ?? 0;
        return new PanelDevice(
            Name: dev.DeviceName,
            Firmware: dev.FirmwareVersion,
            BuildString: dev.BuildString,
            UniqueId: dev.UniqueId,
            Source: md.Source,
            Connected: dev.Connected,
            SerialSuspended: false,
            CanWrite: CanWrite(dev),
            Readings: latest != null ? WireViewSensorDto.FromDevice(dev, latest) : null,
            FaultStatus: status,
            FaultLog: log,
            // The debounced state the alerts use, else the raw bits.
            FaultText: _mini.LocalFaultText(md.Id) ?? (status != 0 ? FaultMonitor.FormatFaults((ushort)status) : null),
            FaultLogText: log != 0 ? FaultNames((ushort)log) : null);
    }

    /// <summary>The local device the panel shows and drives. With the hwmon
    /// module loaded but wireviewd stopped the manager lists both a read-only
    /// hwmon entry and the serial port, so prefer a device that takes commands,
    /// then a connected one, then one with readings.</summary>
    private ManagedDevice? PreferredLocal() =>
        _mini.LocalDevices.Devices
            .Where(d => d.Device is not NetworkDevice)
            .OrderByDescending(d => CanWrite(d.Device))
            .ThenByDescending(d => d.Device.Connected)
            .ThenByDescending(d => d.Latest != null)
            .FirstOrDefault();

    private static bool CanWrite(IWireViewDevice dev) =>
        dev is WireViewPro2Device || dev is HwmonDevice { DaemonAvailable: true };

    private static string FaultNames(ushort mask)
    {
        var names = Enum.GetValues<WireViewPro2Device.FAULT>()
            .Where(f => (mask & (1 << (int)f)) != 0)
            .Select(FaultMonitor.FriendlyFaultName)
            .ToList();
        return names.Count > 0 ? string.Join(", ", names) : $"0x{mask:X4}";
    }

    // ---- Config ----

    private HttpResponseInfo GetConfig()
    {
        if (DeviceProblem() is { } problem) return problem;
        var snap = _mini.Publisher.ReadLocalConfig(null);
        if (snap == null) return Error(503, "could not read the device configuration");
        return ConfigAnswer(snap, extra: null);
    }

    private HttpResponseInfo ConfigAnswer(ConfigSnapshot snap, PanelConfigWrite? extra)
    {
        DeviceConfigModel model;
        try { model = DeviceConfigModel.Decode(snap.Version, snap.Data); }
        catch (DeviceConfigException ex) { return Error(500, ex.Message); }
        var md = _mini.LocalDevices.Devices.FirstOrDefault(d => d.Device.UniqueId == snap.DeviceId);
        int? fw = int.TryParse(md?.Device.FirmwareVersion, NumberStyles.Integer, CultureInfo.InvariantCulture, out int f) ? f : null;
        return Ok(new PanelConfig(
            DeviceId: snap.DeviceId,
            Version: snap.Version,
            Firmware: fw,
            Config: model,
            Options: DeviceConfigOptions.For(fw, model),
            Result: extra), MiniJsonContext.Default.PanelConfig);
    }

    private HttpResponseInfo PostConfig(string body)
    {
        using var doc = JsonDocument.Parse(body.Length == 0 ? "{}" : body);
        var root = doc.RootElement;
        var element = root.TryGetProperty("config", out var c) ? c : root;
        var model = element.Deserialize(MiniJsonContext.Default.DeviceConfigModel);
        if (model == null) return Error(400, "no config in the request");

        if (DeviceProblem() is { } problem) return problem;
        lock (_writeGate)
        {
            var current = _mini.Publisher.ReadLocalConfig(null);
            if (current == null) return Error(503, "could not read the device configuration to apply the changes to");
            if (current.Version != model.Version)
                return Error(409, $"the device's config layout is {current.Version}, the edit was made for {model.Version}; reload");

            byte[] bytes;
            try { bytes = model.EncodeOnto(current.Data); }
            catch (DeviceConfigException ex) { return Error(400, ex.Message); }
            catch (FormatException) { return Error(400, "raw is not valid base64"); }

            bool changed = !bytes.AsSpan().SequenceEqual(current.Data);
            // As the app's Apply: write the merged bytes, then redraw the screen.
            var r = _mini.RunLocalCommand(WireViewCommand.WriteConfig(current.DeviceId, current.Version, bytes));
            if (r != DaemonResult.Ok) return CommandError("Apply", r);
            _mini.RunLocalCommand(WireViewCommand.Screen(current.DeviceId, (int)WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_SAME));
            FileLog.Info($"control panel: config applied ({(changed ? "changed" : "no field changed")}, {bytes.Length} bytes)");

            Thread.Sleep(150); // let the device take the frames before reading back
            var after = _mini.Publisher.ReadLocalConfig(null);
            if (after == null) return Error(500, "the config was written but could not be read back");
            return ConfigAnswer(after, new PanelConfigWrite(
                Changed: changed,
                ReadBackMatches: after.Data.AsSpan().SequenceEqual(bytes)));
        }
    }

    /// <summary>An answer for a state where the device cannot be used, or null.</summary>
    private HttpResponseInfo? DeviceProblem()
    {
        if (_mini.HandoverActive)
            return Error(409, "the serial port is lent to the WireView app; try again when it hands it back");
        var md = PreferredLocal();
        if (md == null || !md.Device.Connected) return Error(503, "no device connected");
        if (!CanWrite(md.Device))
            return Error(503, "the device is reached through hwmon without wireviewd, which is read only");
        return null;
    }

    // ---- Commands ----

    private HttpResponseInfo PostCommand(string body)
    {
        using var doc = JsonDocument.Parse(body.Length == 0 ? "{}" : body);
        var root = doc.RootElement;
        string op = root.TryGetProperty("op", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString()! : "";
        int cmd = root.TryGetProperty("cmd", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out int n) ? n : -1;

        if (DeviceProblem() is { } problem) return problem;
        switch (op)
        {
            case "screen":
                if (!ScreenCommands.Contains(cmd)) return Error(400, $"unknown screen command {cmd}");
                return Done("Screen change", _mini.RunLocalCommand(WireViewCommand.Screen("", cmd)), ScreenText(cmd));
            case "nvm":
                if (!NvmCommands.TryGetValue(cmd, out var what))
                    return Error(400, $"NVM command {cmd} is not available here (config load, store and reset only)");
                lock (_writeGate)
                {
                    var r = _mini.RunLocalCommand(WireViewCommand.Nvm("", cmd));
                    if (r == DaemonResult.Ok) FileLog.Info($"control panel: config {what}");
                    return Done("Device memory", r, $"Config {what}.");
                }
            case "clearFaults":
                return Done("Clearing the faults", _mini.ClearLocalFaults(), "Faults cleared.");
            default:
                return Error(400, "op must be screen, nvm or clearFaults");
        }
    }

    internal static string ScreenText(int cmd) => (WireViewPro2Device.SCREEN_CMD)cmd switch
    {
        WireViewPro2Device.SCREEN_CMD.SCREEN_PAUSE_UPDATES => "Screen updates paused.",
        WireViewPro2Device.SCREEN_CMD.SCREEN_RESUME_UPDATES => "Screen updates resumed.",
        WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_SIMPLE => "Switched to Simple.",
        WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_CURRENT => "Switched to Current.",
        WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_TEMP => "Switched to Temperature.",
        WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_STATUS => "Switched to Status.",
        _ => "Switched to Main.",
    };

    private static HttpResponseInfo Done(string action, DaemonResult r, string message) =>
        r == DaemonResult.Ok ? Ok(new PanelMessage(true, message), MiniJsonContext.Default.PanelMessage) : CommandError(action, r);

    private static HttpResponseInfo CommandError(string action, DaemonResult r) => r switch
    {
        DaemonResult.Denied => Error(403,
            $"{action}: wireviewd refused it. Add your user to the 'wireview' group and log in again."),
        DaemonResult.NotConnected => Error(503, $"{action}: no device connected"),
        _ => Error(500, $"{action}: the device did not take the command"),
    };

    // ---- Settings ----

    private HttpResponseInfo PostSettings(string body)
    {
        using var doc = JsonDocument.Parse(body.Length == 0 ? "{}" : body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return Error(400, "expected a JSON object");
        var s = AppSettings.Current;
        var changes = new List<(string What, Action<AppSettings> Change)>();
        string? note = null;

        bool? Bool(string name)
        {
            if (!root.TryGetProperty(name, out var v)) return null;
            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new JsonException($"{name} must be true or false"),
            };
        }

        if (Bool("shutdownOnFault") is { } sof && sof != s.SoftwareShutdownOnFault)
            changes.Add(($"shutdown on fault turned {(sof ? "on" : "off")}", x => x.SoftwareShutdownOnFault = sof));
        if (Bool("publishEnabled") is { } pub && pub != s.PublishEnabled)
            changes.Add(($"LAN publishing turned {(pub ? "on" : "off")}", x => x.PublishEnabled = pub));

        if (root.TryGetProperty("publishPort", out var portEl))
        {
            if (portEl.ValueKind != JsonValueKind.Number || !portEl.TryGetInt32(out int port) || port is < 1024 or > 65535)
                return Error(400, "the port must be a whole number from 1024 to 65535");
            if (port != s.PublishPort)
            {
                bool lan = Bool("publishEnabled") ?? s.PublishEnabled;
                if (_mini.Publisher.Endpoint?.Port != port && PortProblem(port, lan) is { } why)
                    return Error(400, $"port {port} cannot be used: {why}");
                changes.Add(($"publish port set to {port}", x => x.PublishPort = port));
                note = "moved";
            }
        }

        if (root.TryGetProperty("remoteHosts", out var hostsEl))
        {
            IEnumerable<string> raw = hostsEl.ValueKind switch
            {
                JsonValueKind.Array => hostsEl.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String
                    ? e.GetString() ?? "" : throw new JsonException("remoteHosts must be a list of strings")),
                JsonValueKind.String => (hostsEl.GetString() ?? "").Split(',', '\n'),
                JsonValueKind.Null => Array.Empty<string>(),
                _ => throw new JsonException("remoteHosts must be a list of strings"),
            };
            var hosts = new List<string>();
            foreach (var entry in raw.Select(h => h.Trim()).Where(h => h.Length > 0))
            {
                if (NormalizeHost(entry) is not { } h) return Error(400, $"\"{entry}\" is not a host name, address or host:port");
                if (!hosts.Contains(h, StringComparer.OrdinalIgnoreCase)) hosts.Add(h);
            }
            if (hosts.Count > 32) return Error(400, "at most 32 remote hosts");
            var old = s.RemoteHosts ?? new List<string>();
            if (!old.SequenceEqual(hosts))
                changes.Add(($"remote hosts set to [{string.Join(", ", hosts)}]",
                    x => x.RemoteHosts = hosts.Count > 0 ? hosts : null));
        }

        if (root.TryGetProperty("networkSecret", out var secretEl))
        {
            if (secretEl.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                return Error(400, "networkSecret must be a string");
            string? secret = secretEl.ValueKind == JsonValueKind.Null ? null : secretEl.GetString()?.Trim();
            if (string.IsNullOrEmpty(secret)) secret = null;
            if (secret is { Length: > 256 }) return Error(400, "the network secret is longer than 256 characters");
            if (secret != (string.IsNullOrEmpty(s.NetworkSecret) ? null : s.NetworkSecret))
                changes.Add((secret == null ? "network secret cleared" : "network secret changed", x => x.NetworkSecret = secret));
        }

        // Start at login is the login entry, not a setting.
        if (Bool("startAtLogin") is { } sal && sal != MiniInstaller.IsInstalled())
        {
            try
            {
                string where = MiniInstaller.SetInstalled(sal);
                FileLog.Info(sal
                    ? $"control panel: start at login turned on ({where})"
                    : $"control panel: start at login turned off (removed {where})");
            }
            catch (Exception ex)
            {
                return Error(500, $"start at login: {ex.Message}");
            }
        }

        if (changes.Count > 0)
        {
            var list = changes.ToArray();
            bool applied = _mini.RequestSettingsChange(string.Join(", ", list.Select(c => c.What)),
                x => { foreach (var c in list) c.Change(x); },
                source: "control panel", wait: TimeSpan.FromSeconds(6));
            if (!applied) return Error(500, "WireView Mini did not apply the settings in time; reload the page");
        }

        return Ok(new PanelSettingsSaved(
            Ok: true,
            Message: changes.Count == 0 ? "Nothing changed." : "Saved.",
            Settings: SettingsView(AppSettings.Current),
            PanelUrl: note == "moved" ? _mini.PanelUrl : null,
            // With wireviewd the loopback fallback is the expected state, not an error.
            PublishError: _mini.DaemonMode ? null : _mini.PublishError), MiniJsonContext.Default.PanelSettingsSaved);
    }

    // ---- wireviewd settings (Linux) ----

    private PanelDaemon DaemonView()
    {
        var seen = _mini.Daemon.Observed;
        return new PanelDaemon(
            Present: _mini.DaemonMode,
            Publishing: seen?.Publishing,
            Port: seen?.Port,
            Editable: _mini.Daemon.Supported);
    }

    private static PanelDaemonConfig? ConfigView(DaemonConfig? c) => c == null ? null : new(
        RemoteEnabled: c.RemoteEnabled,
        Port: c.Port,
        Bind: c.Bind,
        Secret: c.Secret,
        LogDays: c.LogDays,
        Extra: c.Extra);

    private HttpResponseInfo GetDaemonConfig()
    {
        if (!OperatingSystem.IsLinux()) return Error(404, "wireviewd runs on Linux only");
        var r = _mini.Daemon.Read(showSecret: true);
        return Ok(new PanelDaemonConfigRead(
            Ok: r.Ok,
            Config: ConfigView(r.Config),
            Error: r.Error,
            Unsupported: r.Unsupported,
            Note: r.Note,
            Tool: DaemonConfigService.Locate(),
            Daemon: DaemonView()), MiniJsonContext.Default.PanelDaemonConfigRead);
    }

    /// <summary>Body: {"config": {remoteEnabled, port, bind, secret, logDays},
    /// "copySecret": bool}; fields left out stay as they are. copySecret also sets
    /// WireView Mini's network secret (for the remote hosts) to the daemon's after a
    /// successful save. wireviewctl checks the values: its message comes back as error.</summary>
    private HttpResponseInfo PostDaemonConfig(string body)
    {
        if (!OperatingSystem.IsLinux()) return Error(404, "wireviewd runs on Linux only");
        using var doc = JsonDocument.Parse(body.Length == 0 ? "{}" : body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return Error(400, "expected a JSON object");
        var element = root.TryGetProperty("config", out var c) ? c : root;
        if (element.ValueKind != JsonValueKind.Object) return Error(400, "config must be a JSON object");
        var edit = element.Deserialize(MiniJsonContext.Default.DaemonConfigEdit) ?? new DaemonConfigEdit();
        bool copySecret = root.TryGetProperty("copySecret", out var cs) && cs.ValueKind == JsonValueKind.True;

        string? panelBefore = _mini.PanelUrl;
        var w = _mini.Daemon.Write(edit);

        if (w.Ok && copySecret && w.Config is { } now)
        {
            string? secret = string.IsNullOrEmpty(now.Secret) ? null : now.Secret;
            string? mine = string.IsNullOrEmpty(AppSettings.Current.NetworkSecret) ? null : AppSettings.Current.NetworkSecret;
            if (secret != mine &&
                !_mini.RequestSettingsChange(secret == null ? "network secret cleared (as wireviewd's)" : "network secret set to wireviewd's",
                    x => x.NetworkSecret = secret, source: "control panel", wait: TimeSpan.FromSeconds(6)))
                return Error(500, "WireView Mini did not apply the settings in time; reload the page");
        }

        string? panelAfter = _mini.PanelUrl;
        return Ok(new PanelDaemonConfigWritten(
            Ok: w.Ok,
            Reply: w.Reply,
            Error: w.Error,
            Unsupported: w.Unsupported,
            Changed: w.Changed,
            Config: ConfigView(w.Config),
            Settings: SettingsView(AppSettings.Current),
            // WireView Mini's listener moved off the daemon's new port: the page follows it.
            PanelUrl: panelAfter != null && panelAfter != panelBefore ? panelAfter : null,
            Daemon: DaemonView()), MiniJsonContext.Default.PanelDaemonConfigWritten);
    }

    /// <summary>"host" or "host:port" for a remote host entry (port 9876 by default), or null
    /// when it is not one. Accepts "host", "host:port" and http URLs.</summary>
    internal static string? NormalizeHost(string entry)
    {
        if (entry.Any(char.IsWhiteSpace)) return null;
        string url = RemoteDeviceProbe.Normalize(entry);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp ||
            string.IsNullOrEmpty(uri.Host) || uri.AbsolutePath != "/" || uri.Query.Length > 0)
            return null;
        // The default port is left out, as the app's own settings write it.
        return uri.Port == RemoteDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
    }

    private const int RemoteDefaultPort = 9876; // RemoteDeviceProbe.Normalize's default

    /// <summary>Why the port cannot be bound now (taken), or null when it can.</summary>
    internal static string? PortProblem(int port, bool lan)
    {
        try
        {
            var l = new TcpListener(lan ? IPAddress.Any : IPAddress.Loopback, port);
            if (OperatingSystem.IsWindows()) l.ExclusiveAddressUse = true;
            l.Start();
            l.Stop();
            return null;
        }
        catch (SocketException ex)
        {
            return ex.SocketErrorCode == SocketError.AddressAlreadyInUse ? "another program uses it" : ex.Message;
        }
    }

    // ---- App ----

    private static HttpResponseInfo OpenApp()
    {
        var (started, message) = GuiLauncher.Launch();
        return Ok(new PanelMessage(started, message), MiniJsonContext.Default.PanelMessage);
    }

    // ---- Helpers ----

    private static string OsName =>
        OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";

    private static HttpResponseInfo Ok<T>(T value, JsonTypeInfo<T> type) =>
        HttpResponseInfo.Json(200, JsonSerializer.Serialize(value, type));

    private static HttpResponseInfo Error(int status, string message) =>
        HttpResponseInfo.Json(status, JsonSerializer.Serialize(new PanelError(message), MiniJsonContext.Default.PanelError));
}
