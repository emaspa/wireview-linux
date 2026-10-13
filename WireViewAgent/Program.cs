using System.Reflection;
using System.Runtime.InteropServices;
using WireView2.Net;
using WireView2.Services;

namespace WireView2.Agent;

/// <summary>
/// wireview-agent: the headless WireView process. With no arguments it runs in
/// the foreground until SIGINT/SIGTERM (Ctrl+C), logging to stderr and to
/// <c>logs/wireview-agent-YYYY-MM-DD.log</c> next to settings.json.
/// </summary>
internal static class Program
{
    // Exit codes (also listed by --help).
    internal const int ExitOk = 0;
    internal const int ExitError = 1;        // already running, install failure, crash
    internal const int ExitUsage = 2;
    internal const int ExitNotRunning = 3;   // --status: no agent
    internal const int ExitNoDevice = 4;     // --status: agent up, no live device (or not publishing)
    internal const int ExitFault = 5;        // --status: a device reports an active fault

    private const string Usage =
        "Usage: wireview-agent [option]\n" +
        "\n" +
        "Headless WireView Pro II agent: owns the device, publishes it on the LAN,\n" +
        "raises fault alerts and, when set in the app, shuts the PC down on a fault.\n" +
        "The WireView app connects through it while it runs.\n" +
        "\n" +
        "  (no option)    run in the foreground until Ctrl+C or SIGTERM\n" +
        "  --install      start the agent at login (Linux: XDG autostart entry,\n" +
        "                 Windows: HKCU Run key)\n" +
        "  --uninstall    remove that login entry\n" +
        "  --status       ask the running agent and print device and fault state\n" +
        "  --no-shutdown  run, but never power off (for testing shutdown on fault)\n" +
        "  --version      print the version\n" +
        "  --help         this text\n" +
        "\n" +
        "Settings are shared with the app (settings.json in the PowerMonitor folder).\n" +
        "Port: the app's publish port (9876 by default). LAN publishing on: all\n" +
        "interfaces. Off: 127.0.0.1 only.\n" +
        "\n" +
        "Exit codes: 0 ok, 1 error or already running, 2 bad usage,\n" +
        "--status only: 3 not running, 4 no device connected, 5 active fault.";

    public static int Main(string[] args)
    {
#if WINDOWS
        // A windowless program: a shell's console (when there is one) carries
        // the output of the CLI flags and of a foreground run.
        WindowsConsole.AttachToParent();
#endif
        if (args.Length > 1)
            return UsageError($"one option at a time, got: {string.Join(' ', args)}");

        switch (args.Length == 0 ? "" : args[0])
        {
            case "":
                return Run(noShutdown: false);
            case "--no-shutdown":
                return Run(noShutdown: true);
            case "--status":
                return StatusCommand.Run();
            case "--install":
                return AgentInstaller.Install();
            case "--uninstall":
                return AgentInstaller.Uninstall();
            case "--version":
                Console.WriteLine($"wireview-agent {Version}");
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

    private static int UsageError(string message)
    {
        Console.Error.WriteLine($"wireview-agent: {message}");
        Console.Error.WriteLine("Try wireview-agent --help.");
        return ExitUsage;
    }

    private static int Run(bool noShutdown)
    {
        using var instance = AgentInstance.TryAcquire();
        if (instance == null)
        {
            Console.Error.WriteLine("wireview-agent: already running");
            return ExitError;
        }

        AppSettings.Reload();
        FileLog.Echo = Console.Error;
        FileLog.Init(
            Path.Combine(Path.GetDirectoryName(AppSettings.GetSettingsPath())!, "logs"),
            AppSettings.Current.LogRetentionDays,
            "wireview-agent");

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
            using var service = new AgentService(Version, CreateNotifier(), noShutdown);
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
