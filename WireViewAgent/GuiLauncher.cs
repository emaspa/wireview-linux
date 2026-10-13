using System.Diagnostics;
using System.Runtime.InteropServices;
using WireView2.Net;
using WireView2.Services;

namespace WireView2.Agent;

/// <summary>
/// The tray's "Open WireView": starts the GUI, or brings a running one forward
/// where the GUI supports that.
///
/// Where the GUI is, in order: next to the agent (the Windows zip ships
/// <c>wireview2-plus.exe</c>, a dev build is <c>WireView2.exe</c>; the Linux
/// tarball and packages ship <c>WireView2</c>), then on Linux the package path
/// <c>/usr/lib/wireview-linux/WireView2</c>, <c>wireview-linux</c> on PATH (the
/// package's and the Flatpak's launcher) and the installed Flatpak
/// (<c>flatpak run io.github.emaspa.WireViewLinux</c>, through
/// <c>flatpak-spawn --host</c> when the agent itself runs inside a sandbox).
///
/// A GUI that is already running: on Windows it waits on a named event
/// (SingleInstanceService.CreateOrOpenActivationEvent) and shows its window when
/// it is set, so the agent sets it. A second GUI launch does not set it (the new
/// copy just exits), so launching again would do nothing. On Linux the GUI has
/// no such channel (named events do not exist there), so the agent says where
/// to find it instead.
/// </summary>
internal static class GuiLauncher
{
    internal const string FlatpakAppId = "io.github.emaspa.WireViewLinux";
    private const string GuiMutexName = "WireView2_SingleInstance";
    private const string GuiActivationEventName = "WireView2_SingleInstance_ActivationEvent";

    public static void Open(IToastNotifier toast)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (TryActivateRunningWindowsGui()) return;
            }
            else if (IsLinuxGuiRunning())
            {
                FileLog.Info("tray: Open WireView: the app is already running");
                toast.Show("WireView", "WireView is already running. Open its window from its own tray icon.");
                return;
            }

            if (Resolve() is not { } start)
            {
                FileLog.Warn("tray: Open WireView: the app was not found");
                toast.Show("WireView", "The WireView app was not found. Install it, or put it next to wireview-agent.");
                return;
            }

            var psi = new ProcessStartInfo(start.File) { UseShellExecute = false };
            foreach (var a in start.Args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            FileLog.Info($"tray: Open WireView: started {start.File} {string.Join(' ', start.Args)}".TrimEnd());
        }
        catch (Exception ex)
        {
            FileLog.Warn($"tray: Open WireView failed: {ex.Message}");
            toast.Show("WireView", $"Cannot start the WireView app: {ex.Message}");
        }
    }

    private readonly record struct Launch(string File, string[] Args);

    private static Launch? Resolve()
    {
        string? here = Path.GetDirectoryName(Environment.ProcessPath);
        if (OperatingSystem.IsWindows())
        {
            foreach (var name in new[] { "wireview2-plus.exe", "WireView2.exe" })
                if (here != null && File.Exists(Path.Combine(here, name)))
                    return new Launch(Path.Combine(here, name), []);
            return null;
        }

        var candidates = new List<string>();
        if (here != null) candidates.Add(Path.Combine(here, "WireView2"));
        candidates.Add("/usr/lib/wireview-linux/WireView2");
        foreach (var c in candidates)
            if (IsExecutable(c)) return new Launch(c, []);
        if (FindOnPath("wireview-linux") is { } launcher) return new Launch(launcher, []);

        bool inSandbox = File.Exists("/.flatpak-info");
        if (inSandbox && FindOnPath("flatpak-spawn") is { } spawn)
            return new Launch(spawn, ["--host", "flatpak", "run", FlatpakAppId]);
        if (!inSandbox && FindOnPath("flatpak") is { } flatpak && FlatpakInstalled())
            return new Launch(flatpak, ["run", FlatpakAppId]);
        return null;
    }

    private static bool FlatpakInstalled()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Directory.Exists(Path.Combine("/var/lib/flatpak/app", FlatpakAppId)) ||
               Directory.Exists(Path.Combine(home, ".local/share/flatpak/app", FlatpakAppId));
    }

    private static string? FindOnPath(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            string path = Path.Combine(dir, name);
            if (IsExecutable(path)) return path;
        }
        return null;
    }

    private static bool IsExecutable(string path)
    {
        try
        {
            return File.Exists(path) &&
                   (OperatingSystem.IsWindows() ||
                    (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0);
        }
        catch { return false; }
    }

    /// <summary>A running GUI: a process named WireView2 (the Flatpak's sandboxed
    /// process shows under the same name) or wireview-linux.</summary>
    private static bool IsLinuxGuiRunning()
    {
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            // Started through the package's /usr/bin/wireview-linux symlink, the
            // process is named after the link.
            foreach (var name in new[] { "WireView2", "wireview-linux" })
            {
                var found = Process.GetProcessesByName(name);
                foreach (var p in found) p.Dispose();
                if (found.Length > 0) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>Windows: sets the running GUI's activation event, so it shows its
    /// window. True when a GUI is running (whether or not the signal got through).</summary>
    private static bool TryActivateRunningWindowsGui()
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (!Mutex.TryOpenExisting(GuiMutexName, out var mutex)) return false;
        mutex.Dispose();
        if (EventWaitHandle.TryOpenExisting(GuiActivationEventName, out var activate))
        {
            using (activate)
            {
                // The tray click made the agent the foreground process; pass that
                // on, or Windows only flashes the GUI's taskbar button.
                try { AllowSetForegroundWindow(AsfwAny); } catch { }
                activate.Set();
            }
            FileLog.Info("tray: Open WireView: asked the running app to show its window");
        }
        else
        {
            FileLog.Info("tray: Open WireView: the app is running but did not answer");
        }
        return true;
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
}
