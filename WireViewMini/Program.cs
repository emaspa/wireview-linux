using System.Reflection;
using System.Runtime.InteropServices;
using WireView2.Net;
using WireView2.Services;

namespace WireView2.Mini;

/// <summary>
/// wireview-mini: the headless WireView process. With no arguments it runs in
/// the foreground until SIGINT/SIGTERM (Ctrl+C), logging to stderr and to
/// <c>logs/wireview-mini-YYYY-MM-DD.log</c> next to settings.json.
/// </summary>
internal static class Program
{
    // Exit codes (also listed by --help).
    internal const int ExitOk = 0;
    internal const int ExitError = 1;        // already running, install failure, crash
    internal const int ExitUsage = 2;
    internal const int ExitNotRunning = 3;   // --status: not running
    internal const int ExitNoDevice = 4;     // --status: running, no live device (or not publishing)
    internal const int ExitFault = 5;        // --status: a device reports an active fault

    private const string Usage =
        "Usage: wireview-mini [option]\n" +
        "\n" +
        "WireView Mini, the headless WireView Pro II client: owns the device, publishes it on the LAN,\n" +
        "raises fault alerts and, when set in the app, shuts the PC down on a fault.\n" +
        "The WireView app connects through it while it runs. Its tray icon shows the\n" +
        "live power and has the main switches (Linux: needs a StatusNotifierItem tray).\n" +
        "Its control panel, a local web page, edits the device configuration, alarms\n" +
        "and LAN monitoring, and flashes the bundled firmware: open it from the tray\n" +
        "or with --open.\n" +
        "\n" +
        "  (no option)    run in the foreground until Ctrl+C or SIGTERM\n" +
        "  --install      start WireView Mini at login (Linux: XDG autostart entry,\n" +
        "                 Windows: HKCU Run key)\n" +
        "  --uninstall    remove that login entry\n" +
        "  --status       ask the running WireView Mini and print device and fault state\n" +
        "  --open         print the running WireView Mini's control panel address and open it\n" +
        "                 in the browser\n" +
        "  --no-shutdown  run, but never power off (for testing shutdown on fault)\n" +
        "  --no-tray      run without the tray icon (combines with --no-shutdown)\n" +
        "  --version      print the version\n" +
        "  --help         this text\n" +
        "\n" +
        "Settings are shared with the app (settings.json in the PowerMonitor folder).\n" +
        "Port: the app's publish port (9876 by default). LAN publishing on: all\n" +
        "interfaces. Off: 127.0.0.1 only.\n" +
        "\n" +
        "Exit codes: 0 ok, 1 error or already running, 2 bad usage,\n" +
        "--status only: 3 not running, 4 no device connected, 5 active fault.\n" +
        "--open: 3 when WireView Mini is not running.\n" +
        "\n" +
        "Unofficial, community-made software by emaspa, https://github.com/emaspa/wireview-linux.\n" +
        "Not affiliated with, endorsed by, or supported by Thermal Grizzly or ElmorLabs.\n" +
        "Use at your own risk: it interacts with hardware, and the authors are not\n" +
        "responsible for any damage to your device.\n" +
        "Thanks to Thermal Grizzly for providing a WireView Pro II Noctua Edition unit for development.";

    public static int Main(string[] args)
    {
#if WINDOWS
        // A windowless program: a shell's console (when there is one) carries
        // the output of the CLI flags and of a foreground run.
        WindowsConsole.AttachToParent();
#endif
        // A foreground run takes any mix of its two flags; the other options
        // come alone.
        if (args.All(a => a is "--no-shutdown" or "--no-tray"))
            return Run(noShutdown: args.Contains("--no-shutdown"), noTray: args.Contains("--no-tray"));
        if (args.Length > 1)
            return UsageError($"one option at a time, got: {string.Join(' ', args)}");

        switch (args[0])
        {
            case "--status":
                return StatusCommand.Run();
            case "--open":
                return OpenPanel();
            case "--install":
                return MiniInstaller.Install();
            case "--uninstall":
                return MiniInstaller.Uninstall();
            case "--version":
                Console.WriteLine($"wireview-mini {Version}");
                return ExitOk;
            case "--help":
            case "-h":
            case "/?":
                Console.WriteLine(Usage);
                return ExitOk;
            default:
                return UsageError($"unknown option {args[0]}");
        }
    }

    /// <summary><c>--open</c>: the running WireView Mini's control panel. The token and the
    /// port it listens on (the publish port, or its loopback fallback while that is
    /// taken) are in the owner-only file WireView Mini writes.</summary>
    private static int OpenPanel()
    {
        AppSettings.Reload();
        var (port, snap) = StatusCommand.FindMini();
        string? token = MiniPanelToken.Read();
        if (snap?.Mini == null || token == null)
        {
            Console.Error.WriteLine(MiniInstance.IsRunning()
                ? $"wireview-mini is running but its control panel does not answer on 127.0.0.1:{port}; see its log."
                : "wireview-mini is not running. Start it first (wireview-mini, or --install to start it at login).");
            return ExitNotRunning;
        }
        string url = $"http://127.0.0.1:{port}/#t={token}";
        Console.WriteLine(url);
        if (GuiLauncher.OpenUrl(url) is { } error)
            Console.Error.WriteLine($"wireview-mini: cannot open the browser ({error}); open the address above.");
        return ExitOk;
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine($"wireview-mini: {message}");
        Console.Error.WriteLine("Try wireview-mini --help.");
        return ExitUsage;
    }

    private static int Run(bool noShutdown, bool noTray)
    {
        using var instance = MiniInstance.TryAcquire();
        if (instance == null)
        {
            Console.Error.WriteLine("wireview-mini: already running");
            return ExitError;
        }

        AppSettings.Reload();
        FileLog.Echo = Console.Error;
        FileLog.Init(
            Path.Combine(Path.GetDirectoryName(AppSettings.GetSettingsPath())!, "logs"),
            AppSettings.Current.LogRetentionDays,
            "wireview-mini");

        using var stop = new CancellationTokenSource();
        void RequestStop(PosixSignalContext ctx)
        {
            ctx.Cancel = true; // exit through the normal path, after cleanup
            FileLog.Info($"{ctx.Signal} received");
            try { stop.Cancel(); } catch (ObjectDisposedException) { }
        }
        // SIGINT is Ctrl+C on Windows too; SIGTERM covers systemd, kill and logout.
        using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, RequestStop);
        using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, RequestStop);
        using var sigQuit = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, RequestStop);

        try
        {
            var notifier = CreateNotifier();
            using var service = new MiniService(Version, notifier, noShutdown);
            // The tray's Quit takes the same way out as a signal.
            using var tray = noTray ? null : TrayController.Start(service, notifier, () =>
            {
                try { stop.Cancel(); } catch (ObjectDisposedException) { }
            });
            service.Run(stop.Token);
            FileLog.Info("stopped");
            return ExitOk;
        }
        catch (Exception ex)
        {
            FileLog.Warn($"fatal: {ex}");
            return ExitError;
        }
        finally
        {
            try { instance.ReleaseMutex(); } catch { }
        }
    }

    private static IToastNotifier CreateNotifier()
    {
#if WINDOWS
        return new WindowsToastNotifier();
#else
        return OperatingSystem.IsLinux() ? new LinuxToastNotifier() : new NullToastNotifier();
#endif
    }

    /// <summary>The informational version without its "+commit" suffix.</summary>
    internal static string Version
    {
        get
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info)) return info.Split('+')[0];
            return asm.GetName().Version?.ToString() ?? "0.0.0";
        }
    }
}
