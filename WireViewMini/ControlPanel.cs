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
/// POST /api/settings, POST /api/open-app, GET /api/firmware and POST
/// /api/firmware/flash (the bundled firmware, see <see cref="FirmwareService"/>),
/// GET and POST /api/theme, POST /api/theme/restore-defaults, GET and POST
/// /api/theme/file and POST /api/theme/file/export (the display theme, see
/// <see cref="ThemeService"/>), GET /api/history and /api/history.csv (the chart
/// history, see <see cref="HistoryBuffer"/>), POST /api/devicelog/read, GET
/// /api/devicelog, /api/devicelog/cycle/N and /api/devicelog/cycle/N.csv (the
/// on-device log, see <see cref="DeviceLogService"/>), and on Linux GET and POST /api/daemon-config (wireviewd's settings through
/// wireviewctl, see <see cref="DaemonConfigService"/>). Device writes go through the same
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
        bool image = req.Path is "/panel/fan1.png" or "/panel/fan2.png";
        bool api = req.Path.StartsWith("/api/", StringComparison.Ordinal);
        if (!page && !image && !api) return null;

        if (!req.IsLoopback)
        {
            FileLog.Warn($"control panel: {req.Method} {req.Path} from {req.RemoteIp} refused (local only)");
            return Error(403, "local only");
        }

        if (page)
            return req.Method is "GET" or "HEAD" ? Page() : Error(405, "method not allowed");
        if (image)
            return req.Method is "GET" or "HEAD" ? Png(req.Path["/panel/".Length..]) : Error(405, "method not allowed");

        if (!TokenMatches(req.Header(TokenHeader)))
            return Error(401, "missing or wrong control panel token; open the panel from the tray or with wireview-mini --open");

        try
        {
            return (req.Method, req.Path) switch
            {
                ("GET", "/api/status") => Ok(Status(req.Query), MiniJsonContext.Default.PanelStatus),
                ("GET", "/api/config") => GetConfig(),
                ("POST", "/api/config") => PostConfig(req.Body),
                ("POST", "/api/command") => PostCommand(req.Body),
                ("POST", "/api/settings") => PostSettings(req.Body),
                ("POST", "/api/open-app") => OpenApp(),
                ("GET", "/api/firmware") => GetFirmware(),
                ("POST", "/api/firmware/flash") => PostFlash(req.Body),
                ("GET", "/api/theme") => GetTheme(req.Query),
                ("POST", "/api/theme") => PostTheme(req.Body),
                ("POST", "/api/theme/restore-defaults") => PostThemeRestore(req.Body),
                ("GET", "/api/theme/file") => GetThemeFile(),
                ("POST", "/api/theme/file") => PostThemeFile(req.Body),
                ("POST", "/api/theme/file/export") => PostThemeExport(req.Body),
                ("GET", "/api/daemon-config") => GetDaemonConfig(),
                ("POST", "/api/daemon-config") => PostDaemonConfig(req.Body),
                ("GET", "/api/history") => Ok(History(ParseQuery(req.Query)), MiniJsonContext.Default.PanelHistory),
                ("GET", "/api/history.csv") => HistoryCsv(ParseQuery(req.Query)),
                ("POST", "/api/devicelog/read") => PostDeviceLogRead(),
                ("GET", "/api/devicelog") => Ok(DeviceLogView(), MiniJsonContext.Default.PanelDeviceLog),
                ("GET", var cycle) when cycle.StartsWith(CyclePrefix, StringComparison.Ordinal) => GetCycle(cycle[CyclePrefix.Length..], req.Query),
                (_, "/api/status" or "/api/config" or "/api/command" or "/api/settings" or "/api/open-app" or "/api/daemon-config"
                    or "/api/firmware" or "/api/firmware/flash" or "/api/theme" or "/api/theme/restore-defaults"
                    or "/api/theme/file" or "/api/theme/file/export" or "/api/history" or "/api/history.csv"
                    or "/api/devicelog" or "/api/devicelog/read")
                    => Error(405, "method not allowed"),
                _ => Error(404, "no such API"),
            };
        }
        catch (JsonException ex)
        {
            return Error(400, $"bad JSON: {ex.Message}");
        }
    }

    /// <summary>The publisher's body-limit hook: the theme routes take an image
    /// (a 320x170 RGB565 background is 145 KB of base64), from this PC with the
    /// token only; every other request keeps the publisher's size cap.</summary>
    public int BodyLimit(HttpRequestInfo head) =>
        head.IsLoopback && head.Method == "POST"
        && head.Path is "/api/theme" or "/api/theme/file" or "/api/theme/file/export"
        && TokenMatches(head.Header(TokenHeader))
            ? ThemeFileFormat.MaxFileBytes
            : 0;

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

    /// <summary>The app's fan templates (white on transparent), for the theme
    /// editor's fan frames: static artwork, no token needed.</summary>
    private static HttpResponseInfo Png(string name)
    {
        using var stream = typeof(ControlPanel).Assembly.GetManifestResourceStream("wireview-mini." + name);
        if (stream == null) return Error(404, "no such image");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return new HttpResponseInfo(200, "image/png", ms.ToArray())
        {
            Headers = [new("Cache-Control", "max-age=86400"), new("X-Content-Type-Options", "nosniff")],
        };
    }

    // ---- Status ----

    private PanelStatus Status(string query)
    {
        // ?history=<unix seconds>[&step=n]: the chart's new samples ride along
        // with the status poll, so an open chart costs no extra request.
        var q = ParseQuery(query);
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
            Gui: new PanelGui(Location: gui, Available: gui != null),
            Firmware: FlashView(_mini.Firmware.State, _mini.Firmware.UpdateAvailable()),
            // Only while an upload runs: the progress needs no extra requests.
            Theme: _mini.Theme.State is { Running: true } t ? ThemeUploadView(t) : null,
            DeviceLog: _mini.DeviceLog.State is { Running: true } l ? DeviceLogStateView(l) : null,
            History: q.TryGetValue("history", out var since) ? History(q, since) : null);
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
        // Lent to the app, or closed for a firmware flash: the last readings.
        if (_mini.PortSuspended)
        {
            var frozen = _mini.Publisher.BuildSnapshot().Devices.FirstOrDefault();
            return new PanelDevice(
                Name: frozen?.Name,
                Firmware: frozen?.FwVer,
                BuildString: frozen?.BuildString,
                UniqueId: frozen?.Id,
                Source: null,
                Connected: false,
                SerialSuspended: _mini.HandoverActive,
                CanWrite: false,
                Readings: frozen,
                FaultStatus: frozen?.FaultStatus ?? 0,
                FaultLog: frozen?.FaultLog ?? 0,
                FaultText: null,
                FaultLogText: null);
        }

        var md = _mini.PreferredLocal();
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
            CanWrite: MiniService.CanWrite(dev) && !_mini.Firmware.Running && !_mini.Theme.UploadRunning && !_mini.DeviceLog.Busy,
            Readings: latest != null ? WireViewSensorDto.FromDevice(dev, latest) : null,
            FaultStatus: status,
            FaultLog: log,
            // The debounced state the alerts use, else the raw bits.
            FaultText: _mini.LocalFaultText(md.Id) ?? (status != 0 ? FaultMonitor.FormatFaults((ushort)status) : null),
            FaultLogText: log != 0 ? FaultNames((ushort)log) : null);
    }

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
        if (_mini.Firmware.Running)
            return Error(409, "a firmware update is running; try again when it has finished");
        if (_mini.Theme.Busy)
            return Error(409, "a theme transfer is using the serial port; try again when it has finished");
        if (_mini.DeviceLog.Busy)
            return Error(409, "the device log is being read; try again when it has finished");
        var md = _mini.PreferredLocal();
        if (md == null || !md.Device.Connected) return Error(503, "no device connected");
        if (!MiniService.CanWrite(md.Device))
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
        DaemonResult.Busy => Error(409, $"{action}: a firmware update is running, try again when it is done"),
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

    // ---- Firmware ----

    private static PanelFlash FlashView(FlashState s, bool updateAvailable) => new(
        State: s.Phase switch
        {
            FlashPhase.EnteringBootloader => "EnteringBootloader",
            FlashPhase.WaitingForDfu => "WaitingForDfu",
            FlashPhase.Flashing => "Flashing",
            FlashPhase.Done => "Done",
            FlashPhase.Failed => "Failed",
            _ => "Idle",
        },
        Progress: Math.Round(s.Progress, 3),
        Message: s.Message,
        Running: s.Running,
        UpdateAvailable: updateAvailable);

    private static string? VerdictName(FlashVerdict? v) => v switch
    {
        FlashVerdict.Newer => "Newer",
        FlashVerdict.Same => "Same",
        FlashVerdict.Unknown => "Unknown",
        FlashVerdict.Downgrade => "Downgrade",
        FlashVerdict.ProductMismatch => "ProductMismatch",
        FlashVerdict.TooOldForNoctua => "TooOldForNoctua",
        _ => null,
    };

    private HttpResponseInfo GetFirmware()
    {
        var r = _mini.Firmware.Report();
        var d = r.Device;
        var b = r.Bundled;
        return Ok(new PanelFirmware(
            Device: d == null ? null : new PanelFirmwareDevice(
                Name: d.Name,
                Firmware: string.IsNullOrEmpty(d.FirmwareVersion) ? null : d.FirmwareVersion,
                BuildString: string.IsNullOrEmpty(d.BuildString) ? null : d.BuildString,
                Label: d.Label,
                Route: d.Route switch { FlashRoute.Serial => "serial", FlashRoute.Daemon => "daemon", _ => "readOnly" },
                Connected: d.Connected),
            Bundled: new PanelFirmwareImage(
                Path: r.BundledPath,
                Version: b?.Version,
                BuildString: b?.BuildString,
                Label: b == null ? null : BundledFirmware.Label(b.Version, b.BuildString),
                Error: r.BundledError),
            Verdict: VerdictName(r.Verdict),
            VerdictText: FirmwareService.VerdictText(r.Verdict),
            CanFlash: r.CanFlash,
            Reason: r.Reason,
            NeedsOverride: r.OverrideWarning != null,
            OverrideWarning: r.OverrideWarning,
            OverrideLabel: r.OverrideLabel,
            ConfirmText: r.ConfirmText,
            DfuUtilRequired: r.DfuUtilRequired,
            DfuUtil: r.DfuUtilVersion,
            Hint: r.Hint,
            Flash: FlashView(r.State, r.Verdict == FlashVerdict.Newer && !r.State.Running)), MiniJsonContext.Default.PanelFirmware);
    }

    /// <summary>Body: {"allowOlder": bool} ("force" is taken as the same). 202 once
    /// started (the progress is in /api/status), 409 while one runs or the app
    /// holds the port, 400 with the gate's message when it may not flash.</summary>
    private HttpResponseInfo PostFlash(string body)
    {
        using var doc = JsonDocument.Parse(body.Length == 0 ? "{}" : body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return Error(400, "expected a JSON object");
        bool Flag(string name) => root.TryGetProperty(name, out var v) && v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False or JsonValueKind.Null => false,
            _ => throw new JsonException($"{name} must be true or false"),
        };
        bool allowOlder = Flag("allowOlder") || Flag("force");

        FileLog.Info($"control panel: firmware update requested{(allowOlder ? " (older or same build allowed)" : "")}");
        var start = _mini.Firmware.Start(allowOlder);
        if (start.Status != 202) return Error(start.Status, start.Error ?? "the firmware update did not start");
        return HttpResponseInfo.Json(202, JsonSerializer.Serialize(
            new PanelFlashStarted(true, start.State.Message ?? "Firmware update started.", FlashView(start.State, false)),
            MiniJsonContext.Default.PanelFlashStarted));
    }

    // ---- Theme ----

    private static PanelThemeUpload ThemeUploadView(ThemeUploadState s) => new(
        State: s.Phase switch
        {
            ThemePhase.Uploading => "Uploading",
            ThemePhase.WritingConfig => "WritingConfig",
            ThemePhase.Redrawing => "Redrawing",
            ThemePhase.Done => "Done",
            ThemePhase.Failed => "Failed",
            _ => "Idle",
        },
        Progress: Math.Round(s.Progress, 3),
        Message: s.Message,
        Running: s.Running);

    private static PanelThemeValues ValuesView(DeviceThemeValues v) => new(
        PrimaryColor: DeviceThemePresets.FormatColor(v.PrimaryColor),
        SecondaryColor: DeviceThemePresets.FormatColor(v.SecondaryColor),
        HighlightColor: DeviceThemePresets.FormatColor(v.HighlightColor),
        BackgroundColor: DeviceThemePresets.FormatColor(v.BackgroundColor),
        DisplayInversion: v.DisplayInversion,
        BackgroundBitmapId: v.BackgroundBitmapId,
        FanBitmapId: v.FanBitmapId);

    private static readonly List<PanelThemePreset> PresetViews = DeviceThemePresets.All
        .Select(p => new PanelThemePreset(p.Name, p.Label, ValuesView(new DeviceThemeValues(
            p.PrimaryColor, p.SecondaryColor, p.HighlightColor, p.BackgroundColor, p.DisplayInversion,
            (byte)p.BackgroundBitmap, (byte)p.FanBitmap))))
        .ToList();

    private static readonly List<PanelOption> BackgroundViews =
        DeviceThemePresets.Backgrounds.Select(b => new PanelOption(b.Value, b.Label)).ToList();

    private static readonly List<PanelOption> FanViews =
        DeviceThemePresets.Fans.Select(f => new PanelOption(f.Value, f.Label)).ToList();

    private static string? B64(byte[]? b) => b == null ? null : Convert.ToBase64String(b);

    /// <summary>?artwork=0 skips the SPI read (colours only); bg= reads another
    /// background slot's artwork than the device's own (the editor's slot choice),
    /// with the fan frames of the slot's fan (the fan follows the background).</summary>
    private HttpResponseInfo GetTheme(string query)
    {
        var q = ParseQuery(query);
        bool artwork = !q.TryGetValue("artwork", out var a) || a is not ("0" or "false");
        byte? bgSlot = q.TryGetValue("bg", out var b) && byte.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out byte bv) ? bv : null;
        var r = _mini.Theme.Read(artwork, bgSlot);
        // Busy states are transient: 409, so the page keeps what it shows.
        if (r.Status == 409) return Error(409, r.Problem!);
        return Ok(ThemeView(r, null), MiniJsonContext.Default.PanelTheme);
    }

    private PanelTheme ThemeView(ThemeRead r, string? message)
    {
        string? unavailable = r.Problem ?? (r.ConfigVersion is < 2 ? ThemeService.LayoutMessage : null);
        var art = r.Artwork;
        return new PanelTheme(
            UiV2Supported: r.ConfigVersion >= 2,
            Unavailable: unavailable,
            ConfigVersion: r.ConfigVersion,
            Values: r.Values is { } v ? ValuesView(v) : null,
            Preset: r.Values is { } pv ? DeviceThemePresets.Detect(pv) : null,
            Artwork: art == null ? null : new PanelThemeArtwork(
                BackgroundSlot: art.BackgroundSlot,
                FanSlot: art.FanSlot,
                Width: WireViewPro2Device.ThemeBackgroundWidth,
                Height: WireViewPro2Device.ThemeBackgroundHeight,
                Background: B64(art.Background),
                FanWidth: WireViewPro2Device.ThemeFanWidth,
                FanHeight: WireViewPro2Device.ThemeFanHeight,
                FanFrame1: B64(art.FanFrame1),
                FanFrame2: B64(art.FanFrame2)),
            ArtworkError: r.ArtworkError,
            Presets: PresetViews,
            Backgrounds: BackgroundViews,
            Fans: FanViews,
            FactoryArtwork: _mini.Theme.FactoryArtworkAvailable,
            Upload: ThemeUploadView(_mini.Theme.State),
            Message: message);
    }

    /// <summary>Body: <see cref="ThemeEdit"/>. 200 with the theme read back when
    /// only the colours, inversion or slots change; 202 once a custom background's
    /// upload started (the progress is in /api/status); 409 busy, 503 no device,
    /// 400 invalid.</summary>
    private HttpResponseInfo PostTheme(string body)
    {
        var edit = JsonSerializer.Deserialize(body.Length == 0 ? "{}" : body, MiniJsonContext.Default.ThemeEdit);
        if (edit == null) return Error(400, "no theme in the request");
        FileLog.Info("control panel: theme apply requested" + (string.IsNullOrEmpty(edit.Background) ? "" : " (with a custom background)"));
        var start = _mini.Theme.Apply(edit);
        if (start.Status == 202)
            return HttpResponseInfo.Json(202, JsonSerializer.Serialize(
                new PanelThemeStarted(true, start.Message ?? "Theme upload started.", ThemeUploadView(start.State)),
                MiniJsonContext.Default.PanelThemeStarted));
        if (start.Status != 200) return Error(start.Status, start.Error ?? "the theme was not applied");
        var after = _mini.Theme.Read(artwork: false);
        return Ok(ThemeView(after, start.Message), MiniJsonContext.Default.PanelTheme);
    }

    /// <summary>Body: {"backgroundBitmapId": 1 or 2}, the slot to restore (the
    /// editor's choice, as in the app). 202 once started.</summary>
    private HttpResponseInfo PostThemeRestore(string body)
    {
        using var doc = JsonDocument.Parse(body.Length == 0 ? "{}" : body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("backgroundBitmapId", out var idEl)
            || idEl.ValueKind != JsonValueKind.Number || !idEl.TryGetInt32(out int slot))
            return Error(400, "backgroundBitmapId (the background slot to restore) is required");
        FileLog.Info($"control panel: factory theme artwork restore requested for background slot {slot}");
        var start = _mini.Theme.RestoreDefaults(slot);
        if (start.Status != 202) return Error(start.Status, start.Error ?? "the restore did not start");
        return HttpResponseInfo.Json(202, JsonSerializer.Serialize(
            new PanelThemeStarted(true, start.Message ?? "Restore started.", ThemeUploadView(start.State)),
            MiniJsonContext.Default.PanelThemeStarted));
    }

    /// <summary>The device's theme as a .wv2t file (the app's Save theme file for
    /// what the device shows now): the background image read from its slot.</summary>
    private HttpResponseInfo GetThemeFile()
    {
        var r = _mini.Theme.Read(artwork: true, fans: false);
        if (r.Status != 200) return Error(r.Status, r.Problem!);
        if (r.Values is not { } v) return Error(400, "Theme files are only supported on config v2+ devices.");
        // As the app: without the artwork (denied, failed) a colours-only file is still useful.
        return ThemeFileAnswer(ThemeService.BuildFile(v, r.Artwork?.Background));
    }

    /// <summary>Body: <see cref="ThemeEdit"/>, the editor's state; answers it as a
    /// .wv2t file written by the shared code (the app's Save theme file). Fan
    /// frames are not written, as in the app.</summary>
    private static HttpResponseInfo PostThemeExport(string body)
    {
        var edit = JsonSerializer.Deserialize(body.Length == 0 ? "{}" : body, MiniJsonContext.Default.ThemeEdit);
        if (edit == null) return Error(400, "no theme in the request");
        if (!ThemeService.TryParse(edit, out var values, out byte[]? bg, out _, out _, out string? error, requireFans: false))
            return Error(400, error!);
        return ThemeFileAnswer(ThemeService.BuildFile(values, bg));
    }

    private static HttpResponseInfo ThemeFileAnswer(byte[] file) =>
        new(200, "application/octet-stream", file)
        {
            Headers =
            [
                new("Cache-Control", "no-store"),
                new("Content-Disposition", "attachment; filename=\"wireview-theme.wv2t\""),
                new("X-Content-Type-Options", "nosniff"),
            ],
        };

    /// <summary>Body: a .wv2t file. Parsed with the shared code and answered as
    /// the editor's state (the app's Load theme file); nothing is written until
    /// the page applies it.</summary>
    private static HttpResponseInfo PostThemeFile(string body)
    {
        ThemeDocument doc;
        byte[]? bg;
        (byte[] Frame1, byte[] Frame2)? fans;
        try
        {
            doc = ThemeFileFormat.Parse(body);
            bg = ThemeFileFormat.TryDecodeBackgroundRgb565(doc, WireViewPro2Device.ThemeBackgroundSizeBytes);
            fans = ThemeFileFormat.TryDecodeFanFrames(doc, WireViewPro2Device.ThemeFanFrameSizeBytes);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return Error(400, "Theme load failed: " + ex.Message);
        }
        // The app's reading: an unknown background is Disabled. The fan follows the
        // background, as in the app's editor, whatever fan the file names.
        var background = DeviceThemePresets.KnownBackground(doc.BackgroundBitmapId);
        var fan = DeviceConfigEditor.FanBitmapFor(background);
        var values = ThemeFileFormat.Values(doc) with { BackgroundBitmapId = (byte)background, FanBitmapId = (byte)fan };
        string? fanNote = doc.FanBitmapId == (byte)fan ? null
            : $" The file's fan animation ({FanLabel(doc.FanBitmapId)}) does not match its background: " +
              $"the fan follows the background ({FanLabel((byte)fan)}).";
        FileLog.Info($"control panel: theme file read ({(bg != null ? "with" : "without")} a background image" +
                     (fanNote != null ? $", fan slot {doc.FanBitmapId} replaced by the background's {(byte)fan}" : "") + ")");
        return Ok(new PanelThemeFile(
            Values: ValuesView(values),
            Preset: DeviceThemePresets.Detect(values),
            Background: B64(bg),
            FanFrame1: B64(fans?.Frame1),
            FanFrame2: B64(fans?.Frame2),
            Message: (bg != null
                ? "Theme loaded and background staged (press Apply to upload)."
                : "Theme loaded (press Apply to upload colors).") + fanNote), MiniJsonContext.Default.PanelThemeFile);
    }

    private static string FanLabel(byte id) =>
        DeviceThemePresets.Fans.FirstOrDefault(f => f.Value == id)?.Label ?? $"slot {id}";

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            d[Uri.UnescapeDataString(eq < 0 ? part : part[..eq])] = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..]);
        }
        return d;
    }

    // ---- History (the Charts card) ----

    /// <summary>The most samples one answer carries (an hour at one a second).</summary>
    private const int MaxChartPoints = 3600;

    /// <summary>?since=&lt;unix seconds&gt; (only newer samples; 0 or none: all)
    /// and ?step=n (every n-th sample, raised to stay within <see cref="MaxChartPoints"/>).</summary>
    private PanelHistory History(Dictionary<string, string> q) =>
        History(q, q.TryGetValue("since", out var since) ? since : null);

    private PanelHistory History(Dictionary<string, string> q, string? since)
    {
        int step = q.TryGetValue("step", out var st) && int.TryParse(st, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : 1;
        var h = _mini.History;
        var slice = h.Read(SinceMs(since), step, MaxChartPoints);
        int count = slice.Count;
        var v = Columns(6, count, (k, p) => slice.Volts[k * 6 + p]);
        var i = Columns(6, count, (k, p) => slice.Amps[k * 6 + p]);
        var temp = Columns(4, count, (k, p) => TempOrNull(slice.Temps[k * 4 + p]));
        var t = new long[count];
        var fan = new int[count];
        var fault = new int[count];
        bool energy = false;
        for (int k = 0; k < count; k++)
        {
            t[k] = slice.UnixMs(k);
            fan[k] = slice.Fan[k];
            fault[k] = slice.Fault[k];
            energy |= !double.IsNaN(slice.EnergyJ[k]);
        }
        double?[]? e = null;
        if (energy)
        {
            e = new double?[count];
            for (int k = 0; k < count; k++) e[k] = double.IsNaN(slice.EnergyJ[k]) ? null : slice.EnergyJ[k];
        }
        return new PanelHistory(h.Capacity > 0, AppSettings.Current.EffectiveHistoryMinutes, h.Capacity, h.Count,
            slice.Step, MaxChartPoints, count, t, v, i, temp, fan, fault, e);
    }

    /// <summary>"1760450000.123" (Unix seconds) to milliseconds, 0 for none.</summary>
    private static long SinceMs(string? since) =>
        double.TryParse(since, NumberStyles.Float, CultureInfo.InvariantCulture, out double s) && s > 0 && double.IsFinite(s)
            ? (long)Math.Round(s * 1000)
            : 0;

    /// <summary>Column arrays (one per pin or sensor), NaN as null (JSON has no NaN).</summary>
    private static float?[][] Columns(int columns, int count, Func<int, int, float> value)
    {
        var cols = new float?[columns][];
        for (int p = 0; p < columns; p++)
        {
            var col = cols[p] = new float?[count];
            for (int k = 0; k < count; k++)
            {
                float x = value(k, p);
                col[k] = float.IsFinite(x) ? x : null;
            }
        }
        return cols;
    }

    /// <summary>The app's test for a connected sensor (TemperatureSensorPresence):
    /// an absent one reads about -3276.8 °C over serial, NaN through hwmon.</summary>
    private static float TempOrNull(float c) => c > -100f && c < 200f ? c : float.NaN;

    /// <summary>The history as the app's live CSV log writes it (CsvLogger's
    /// columns, formats and UTF-8 with a byte order mark), every sample, ?since= as above.</summary>
    private HttpResponseInfo HistoryCsv(Dictionary<string, string> q)
    {
        var slice = _mini.History.Read(SinceMs(q.TryGetValue("since", out var since) ? since : null), 1, 0);
        FileLog.Info($"control panel: history CSV export ({slice.Count} rows)");
        return Csv($"wireview-history-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.csv", bom: true, w =>
        {
            DeviceDataCsv.WriteHeader(w, DeviceDataCsv.LiveHeaders);
            for (int k = 0; k < slice.Count; k++) DeviceDataCsv.WriteLiveRow(w, slice.ToDeviceData(k));
        });
    }

    /// <summary>A CSV download, written while it is sent.</summary>
    private static HttpResponseInfo Csv(string fileName, bool bom, Action<TextWriter> write) =>
        new(200, "text/csv; charset=utf-8", [])
        {
            Headers =
            [
                new("Cache-Control", "no-store"),
                new("Content-Disposition", $"attachment; filename=\"{fileName}\""),
                new("X-Content-Type-Options", "nosniff"),
            ],
            WriteBody = stream =>
            {
                using var w = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: bom), 64 * 1024, leaveOpen: true);
                write(w);
            },
        };

    // ---- Device log ----

    private const string CyclePrefix = "/api/devicelog/cycle/";

    private static PanelDeviceLogState DeviceLogStateView(DeviceLogState s) => new(
        State: s.Phase switch
        {
            DeviceLogPhase.Reading => "Reading",
            DeviceLogPhase.Parsing => "Parsing",
            DeviceLogPhase.Done => "Done",
            DeviceLogPhase.Failed => "Failed",
            _ => "Idle",
        },
        Progress: Math.Round(s.Progress, 3),
        Message: s.Message,
        Running: s.Running);

    /// <summary>202 once started (the progress is in /api/status), 409 busy, 503 no device.</summary>
    private HttpResponseInfo PostDeviceLogRead()
    {
        FileLog.Info("control panel: device log read requested");
        var start = _mini.DeviceLog.Start();
        if (start.Status != 202) return Error(start.Status, start.Error ?? "the device log read did not start");
        return HttpResponseInfo.Json(202, JsonSerializer.Serialize(
            new PanelDeviceLogStarted(true, start.State.Message ?? "Reading the device log...", DeviceLogStateView(start.State)),
            MiniJsonContext.Default.PanelDeviceLogStarted));
    }

    private PanelDeviceLog DeviceLogView()
    {
        var log = _mini.DeviceLog.Log;
        return new PanelDeviceLog(
            Loaded: log != null,
            ReadAt: log?.ReadAt.ToString("o", CultureInfo.InvariantCulture),
            Samples: log?.Samples ?? 0,
            MemoryBytes: log?.ResidentBytes ?? 0,
            Cycles: log?.Cycles.Select(c => new PanelDeviceLogCycle(c.Index, c.Count,
                c.Start.ToString("o", CultureInfo.InvariantCulture), c.Duration.TotalSeconds, c.Label)).ToList() ?? [],
            State: DeviceLogStateView(_mini.DeviceLog.State));
    }

    /// <summary>"N" (?step=, as for the history) or "N.csv" (every sample, the
    /// app's Logging export: its columns and formats, UTF-8 without a byte order mark).</summary>
    private HttpResponseInfo GetCycle(string rest, string query)
    {
        bool csv = rest.EndsWith(".csv", StringComparison.Ordinal);
        if (csv) rest = rest[..^4];
        var log = _mini.DeviceLog.Log;
        if (log == null) return Error(404, "the device log has not been read yet");
        if (!int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index >= log.Cycles.Count)
            return Error(404, "no such power cycle");
        var c = log.Cycles[index];

        if (csv)
        {
            FileLog.Info($"control panel: device log CSV export (cycle {index + 1}, {c.Count} rows)");
            return Csv($"wireview-devicelog-cycle{index + 1}-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.csv", bom: false, w =>
            {
                DeviceDataCsv.WriteHeader(w, DeviceDataCsv.LogHeaders);
                for (int k = 0; k < c.Count; k++) DeviceDataCsv.WriteLogRow(w, log.ToDeviceData(c.First + k));
            });
        }

        var q = ParseQuery(query);
        int step = q.TryGetValue("step", out var st) && int.TryParse(st, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : 1;
        if (c.Count / step > MaxChartPoints) step = (c.Count + MaxChartPoints - 1) / MaxChartPoints;
        int count = (c.Count + step - 1) / step;
        var t = new double[count];
        for (int k = 0; k < count; k++) t[k] = (log.Time(c.First + k * step) - c.Start).TotalSeconds;
        // The app's values: 100 mV and 100 mA steps, whole degrees.
        var v = Columns(6, count, (k, p) => log.Raw(c.First + k * step)[4 + p] / 10f);
        var i = Columns(6, count, (k, p) => log.Raw(c.First + k * step)[10 + p] / 10f);
        var temp = Columns(4, count, (k, p) => TempOrNull(log.Raw(c.First + k * step)[p]));
        return Ok(new PanelDeviceLogSamples(index, c.Label, c.Count, step, count, t, v, i, temp),
            MiniJsonContext.Default.PanelDeviceLogSamples);
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
