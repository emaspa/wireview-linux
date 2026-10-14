using System.Diagnostics;
using System.Globalization;
using System.Text;
using WireView2.Net;

namespace WireView2.Mini;

/// <summary>wireviewd's settings (<c>/etc/wireview/config</c>) as
/// <c>wireviewctl config show</c> prints them.</summary>
internal sealed class DaemonConfig
{
    /// <summary>remote_enabled: the LAN listener is on.</summary>
    public bool RemoteEnabled { get; set; }
    public int Port { get; set; } = DaemonConfigService.DefaultPort;
    /// <summary>A numeric address, empty for all addresses.</summary>
    public string Bind { get; set; } = "";
    /// <summary>The HMAC passphrase other hosts sign their writes to this PC with.</summary>
    public string Secret { get; set; } = "";
    public int LogDays { get; set; } = 14;
    /// <summary>Lines with keys wireviewctl does not know, as printed.</summary>
    public List<string> Extra { get; set; } = new();
}

/// <summary>An edit from the control panel: null fields are left as they are.</summary>
internal sealed class DaemonConfigEdit
{
    public bool? RemoteEnabled { get; set; }
    public int? Port { get; set; }
    public string? Bind { get; set; }
    public string? Secret { get; set; }
    public int? LogDays { get; set; }
}

/// <summary>A <c>config show</c>. <paramref name="Error"/> is wireviewctl's
/// stderr (trimmed) when it failed; <paramref name="Unsupported"/> means this
/// wireviewctl has no <c>config</c> command (or there is none); <paramref name="Note"/>
/// is its stderr when it worked (a missing file).</summary>
internal sealed record DaemonConfigRead(bool Ok, DaemonConfig? Config, string? Error, bool Unsupported, string? Note);

/// <summary>A <c>config set</c>. <paramref name="Reply"/> is the daemon's answer
/// as wireviewctl prints it ("config reloaded: listener on [::]:9876");
/// <paramref name="Changed"/> the keys written (never their values);
/// <paramref name="Config"/> the settings read back afterwards.</summary>
internal sealed record DaemonConfigWrite(bool Ok, string? Reply, string? Error, bool Unsupported,
                                         IReadOnlyList<string> Changed, DaemonConfig? Config);

/// <summary>What answers on wireviewd's port: <paramref name="Publishing"/> when
/// a WireView publisher that is not WireView Mini (wireviewd) serves /sensors there.</summary>
internal readonly record struct DaemonObservation(bool Publishing, int Port);

/// <summary>
/// wireviewd's settings through <c>wireviewctl config</c> (wireview-hwmon 1.8 or
/// newer, which makes <c>/etc/wireview/config</c> editable by the 'wireview'
/// group and has the daemon reload it on <c>config set</c>), Linux only.
///
/// wireviewctl is <c>$WIREVIEW_CTL</c> (a full path, for tests), else
/// <c>wireviewctl</c> on PATH, else <c>/usr/bin/wireviewctl</c>. The secret goes
/// to it on stdin (<c>secret=-</c>), never on a command line, and is never logged.
///
/// Also keeps what WireView Mini knows of the daemon for the status line and the
/// tray: its port (from the last read, 9876 until then), whether wireviewctl
/// supports <c>config</c> (probed once, again on a read after it did not), and
/// what answers on that port (<see cref="LocalMiniProbe.TryFetch(int, TimeSpan, string)"/>
/// every few seconds). <see cref="IsReserved"/> is the port(s) WireView Mini keeps
/// its own listener off. Runs wireviewctl on the caller's thread (the panel's
/// request thread, never the accept loop or the tick); the probe and the
/// observation run on the thread pool.
/// </summary>
internal sealed class DaemonConfigService
{
    /// <summary>wireviewd's port when its settings do not say.</summary>
    public const int DefaultPort = 9876;
    public const string ToolVariable = "WIREVIEW_CTL";
    private const string SystemTool = "/usr/bin/wireviewctl";

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ObserveEvery = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RereadAtMost = TimeSpan.FromSeconds(30);

    internal const string UnsupportedText =
        "Changing the daemon settings here needs wireview-hwmon 1.8 or newer: this wireviewctl has no config command.";

    private readonly Action _settle;
    private readonly object _writeGate = new();
    private readonly object _gate = new();
    private bool? _supported;           // wireviewctl has "config"; null until known
    private int _port = DefaultPort;    // wireviewd's port, from the last read
    private string _bind = "";
    private int? _pendingPort;          // a port a write is moving the daemon to
    private DaemonObservation? _observed;
    private int _probing, _observing;
    private bool _probed;
    private DateTime _nextObserve = DateTime.MinValue;
    private DateTime _lastReread = DateTime.MinValue;

    /// <param name="settle">Blocks until WireView Mini's tick has run once (its
    /// listener moves off a reserved port there).</param>
    public DaemonConfigService(Action settle) => _settle = settle;

    /// <summary>wireviewctl has a config command: true, false, or null while not known.</summary>
    public bool? Supported { get { lock (_gate) return _supported; } }

    /// <summary>The last look at wireviewd's port, null before the first.</summary>
    public DaemonObservation? Observed { get { lock (_gate) return _observed; } }

    /// <summary>A port wireviewd uses or a write is moving it to.</summary>
    public bool IsReserved(int port)
    {
        lock (_gate) return port == _port || port == _pendingPort;
    }

    /// <summary>The wireviewctl to run, or null when there is none.</summary>
    public static string? Locate()
    {
        string? env = Environment.GetEnvironmentVariable(ToolVariable);
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        if (GuiLauncher.FindOnPath("wireviewctl") is { } onPath) return onPath;
        return File.Exists(SystemTool) ? SystemTool : null;
    }

    /// <summary>Called on every tick while wireviewd is the publisher: the
    /// first probe, then a look at its port every few seconds, both in the background.</summary>
    public void Tick()
    {
        bool probe;
        lock (_gate)
        {
            probe = !_probed;
            _probed = true;
        }
        if (probe && Interlocked.Exchange(ref _probing, 1) == 0)
            _ = Task.Run(() =>
            {
                try { Read(showSecret: false); }
                finally { Volatile.Write(ref _probing, 0); }
            });

        if (DateTime.UtcNow >= _nextObserve && Interlocked.Exchange(ref _observing, 1) == 0)
        {
            _nextObserve = DateTime.UtcNow + ObserveEvery;
            _ = Task.Run(() =>
            {
                try { Observe(); }
                finally { Volatile.Write(ref _observing, 0); }
            });
        }
    }

    /// <summary>Asks wireviewd's port for /sensors now. A snapshot without an
    /// mini field is wireviewd's.</summary>
    public DaemonObservation Observe()
    {
        int port;
        string bind;
        lock (_gate) { port = _port; bind = _bind; }
        // A daemon bound to one address answers there only.
        string host = bind.Length == 0 || bind == "0.0.0.0" || bind == "::" ? "127.0.0.1" : bind;
        var snap = LocalMiniProbe.TryFetch(port, TimeSpan.FromSeconds(1), host);
        var now = new DaemonObservation(snap != null && snap.Mini == null, port);
        DaemonObservation? was;
        bool reread;
        lock (_gate)
        {
            was = _observed;
            _observed = now;
            // Gone from its port: maybe it was moved outside the panel (wireviewctl
            // in a terminal); read the settings again, at most every 30 s.
            reread = was is { Publishing: true } && !now.Publishing && _supported == true &&
                     DateTime.UtcNow - _lastReread >= RereadAtMost;
            if (reread) _lastReread = DateTime.UtcNow;
        }
        if (was?.Publishing != now.Publishing || was?.Port != now.Port)
            FileLog.Info(now.Publishing
                ? $"wireviewd answers on port {now.Port}"
                : $"wireviewd does not answer on port {now.Port} (its LAN listener is off)");
        if (reread && Read(showSecret: false).Config is { } c && c.Port != port)
            return Observe();
        return now;
    }

    /// <summary><c>wireviewctl config show</c>, with the secret when asked.</summary>
    public DaemonConfigRead Read(bool showSecret = true)
    {
        if (Locate() is not { } tool)
        {
            SetSupported(false);
            return new DaemonConfigRead(false, null, NotFoundText, true, null);
        }
        var args = new List<string> { "config", "show" };
        if (showSecret) args.Add("--show-secret");
        var run = Exec(tool, args, stdin: null, ReadTimeout);
        if (run.Failure != null) return new DaemonConfigRead(false, null, run.Failure, false, null);

        if (IsUnsupported(run))
        {
            SetSupported(false);
            return new DaemonConfigRead(false, null, UnsupportedText, true, null);
        }
        SetSupported(true);
        if (run.ExitCode != 0)
            return new DaemonConfigRead(false, null, ErrorText(run), false, null);

        var config = Parse(run.Stdout);
        lock (_gate)
        {
            _port = config.Port;
            _bind = config.Bind;
        }
        string note = run.Stderr.Trim();
        return new DaemonConfigRead(true, config, null, false, note.Length > 0 ? note : null);
    }

    /// <summary><c>wireviewctl config set</c> with the keys <paramref name="edit"/>
    /// changes against a fresh read (the secret on stdin), then a read back. A
    /// new port is reserved first and WireView Mini's listener moved off it, so the
    /// daemon can bind it on its reload.</summary>
    public DaemonConfigWrite Write(DaemonConfigEdit edit)
    {
        lock (_writeGate)
        {
            if (edit.Secret is { } sec && (sec.Contains('\n') || sec.Contains('\r')))
                return Failed("the secret must be one line of text");

            var current = Read(showSecret: true);
            if (!current.Ok || current.Config is not { } cur)
                return new DaemonConfigWrite(false, null, current.Error, current.Unsupported, [], null);

            var args = new List<string> { "config", "set" };
            var changed = new List<string>();
            string? stdin = null;
            void Set(string key, string value)
            {
                args.Add($"{key}={value}");
                changed.Add(key);
            }
            if (edit.RemoteEnabled is { } on && on != cur.RemoteEnabled) Set("remote_enabled", on ? "1" : "0");
            if (edit.Port is { } port && port != cur.Port) Set("port", port.ToString(CultureInfo.InvariantCulture));
            if (edit.Bind is { } bind && bind.Trim() != cur.Bind) Set("bind", bind.Trim());
            if (edit.Secret is { } secret && secret != cur.Secret)
            {
                Set("secret", "-");
                stdin = secret + "\n";
            }
            if (edit.LogDays is { } days && days != cur.LogDays) Set("log_days", days.ToString(CultureInfo.InvariantCulture));

            if (changed.Count == 0)
                return new DaemonConfigWrite(true, "Nothing changed.", null, false, changed, cur);

            bool moving = edit.Port is { } p && p != cur.Port && p is >= 1 and <= 65535;
            if (moving)
            {
                lock (_gate) _pendingPort = edit.Port;
                _settle(); // WireView Mini's listener leaves the new port
            }

            string tool = Locate() ?? "wireviewctl"; // found by the read above
            var run = Exec(tool, args, stdin, WriteTimeout);
            stdin = null;

            DaemonConfigWrite result;
            if (run.Failure != null) result = Failed(run.Failure);
            else if (IsUnsupported(run))
            {
                SetSupported(false);
                result = new DaemonConfigWrite(false, null, UnsupportedText, true, changed, null);
            }
            else if (run.ExitCode != 0) result = new DaemonConfigWrite(false, null, ErrorText(run), false, changed, null);
            else
            {
                string reply = run.Stdout.Trim();
                string note = run.Stderr.Trim();
                if (note.Length > 0) reply = reply.Length > 0 ? $"{reply}\n{note}" : note;
                result = new DaemonConfigWrite(true, reply.Length > 0 ? reply : "Saved.", null, false, changed, null);
            }

            // Read back (also the port the reservation follows from now on).
            var after = Read(showSecret: true);
            lock (_gate)
            {
                _pendingPort = null;
                _lastReread = DateTime.UtcNow; // just read: the look below need not read again
            }
            if (result.Ok) result = result with { Config = after.Config };
            if (result.Ok)
                FileLog.Info($"control panel: wireviewd settings changed ({string.Join(", ", changed)}): {result.Reply!.Replace('\n', ' ')}");
            else
                FileLog.Warn($"control panel: wireviewd settings ({string.Join(", ", changed)}) not changed: {result.Error?.Replace('\n', ' ')}");

            _settle(); // WireView Mini may take the port the daemon left
            Observe();
            return result;
        }
    }

    private static DaemonConfigWrite Failed(string error) => new(false, null, error, false, [], null);

    private const string NotFoundText =
        "wireviewctl was not found. It comes with wireviewd (wireview-hwmon); changing the daemon " +
        "settings here needs version 1.8 or newer.";

    private void SetSupported(bool supported)
    {
        lock (_gate) _supported = supported;
    }

    /// <summary>An older wireviewctl (before 1.8) answers "config" with
    /// "unknown command" and its usage, and a non-zero exit.</summary>
    private static bool IsUnsupported(RunResult run) =>
        run.ExitCode != 0 && !run.Stdout.Contains("remote_enabled=", StringComparison.Ordinal) &&
        (run.Stderr.Contains("unknown command", StringComparison.Ordinal) ||
         run.Stderr.Contains("Usage: wireviewctl", StringComparison.Ordinal));

    private static string ErrorText(RunResult run)
    {
        string err = run.Stderr.Trim();
        if (err.Length == 0) err = run.Stdout.Trim();
        return err.Length > 0 ? err : $"wireviewctl exited with code {run.ExitCode}";
    }

    internal static DaemonConfig Parse(string text)
    {
        var c = new DaemonConfig();
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim(), value = line[(eq + 1)..];
            switch (key)
            {
                case "remote_enabled":
                    c.RemoteEnabled = value.Trim() is "1" or "on" or "true";
                    break;
                case "port":
                    if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)) c.Port = port;
                    break;
                case "bind":
                    c.Bind = value.Trim();
                    break;
                case "secret":
                    c.Secret = value;
                    break;
                case "log_days":
                    if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int days)) c.LogDays = days;
                    break;
                default:
                    c.Extra.Add(line);
                    break;
            }
        }
        return c;
    }

    private readonly record struct RunResult(int ExitCode, string Stdout, string Stderr, string? Failure);

    private static RunResult Exec(string tool, IReadOnlyList<string> args, string? stdin, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo(tool)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return new RunResult(-1, "", "", $"cannot start {tool}");
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            try
            {
                if (stdin != null) p.StandardInput.Write(stdin);
                p.StandardInput.Close();
            }
            catch (IOException) { } // it exited without reading
            if (!p.WaitForExit(timeout))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return new RunResult(-1, "", "", $"wireviewctl did not finish within {timeout.TotalSeconds:0} s");
            }
            p.WaitForExit();
            return new RunResult(p.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult(), null);
        }
        catch (Exception ex)
        {
            return new RunResult(-1, "", "", $"cannot run {tool}: {ex.Message}");
        }
    }
}
