using System.Runtime.Versioning;
using WireView2.Services;

namespace WireView2.Mini;

/// <summary>
/// <c>--install</c> / <c>--uninstall</c>: starts WireView Mini at login.
/// Linux (and other Unix desktops): an XDG autostart entry,
/// <c>~/.config/autostart/wireview-mini.desktop</c>.
/// Windows: the per-user Run key (<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>,
/// value "WireView Mini"). The Run key needs no administrator rights, unlike a
/// Task Scheduler logon trigger, and since the Windows build is a windowless
/// program it starts with no console window.
/// </summary>
internal static class MiniInstaller
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "WireView Mini";

    private static readonly XdgAutostart Xdg = new(
        "wireview-mini.desktop",
        "WireView Mini",
        "WireView Pro II WireView Mini: fault alerts, shutdown on fault, LAN publishing");

    /// <summary>Whether the login entry exists (the tray's "Start at login").</summary>
    public static bool IsInstalled()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(RunValueName) != null;
            }
            return Xdg.IsEnabled;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Writes or removes the login entry; returns where it lives.
    /// Throws on failure.</summary>
    public static string SetInstalled(bool installed)
    {
        if (OperatingSystem.IsWindows())
        {
            if (installed) return InstallRunKey();
            UninstallRunKey();
            return $@"HKCU\{RunKeyPath}";
        }
        Xdg.Set(installed);
        return Xdg.DesktopFilePath;
    }

    public static int Install()
    {
        try
        {
            string where = SetInstalled(true);
            Console.WriteLine($"Installed: wireview-mini starts at login ({where}).");
            Console.WriteLine("Run wireview-mini now to start it without logging out.");
            return Program.ExitOk;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"wireview-mini: install failed: {ex.Message}");
            return Program.ExitError;
        }
    }

    public static int Uninstall()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                bool removed = UninstallRunKey();
                Console.WriteLine(removed
                    ? "Uninstalled: wireview-mini no longer starts at login."
                    : "Nothing to uninstall: no login entry found.");
            }
            else
            {
                bool had = Xdg.IsEnabled;
                Xdg.Set(false);
                Console.WriteLine(had
                    ? $"Uninstalled: removed {Xdg.DesktopFilePath}."
                    : "Nothing to uninstall: no login entry found.");
            }
            Console.WriteLine("A running WireView Mini keeps running; stop it with Ctrl+C, kill or Task Manager.");
            return Program.ExitOk;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"wireview-mini: uninstall failed: {ex.Message}");
            return Program.ExitError;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string InstallRunKey()
    {
        string exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Unable to resolve the executable path.");
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key.SetValue(RunValueName, $"\"{exe}\"");
        return $@"HKCU\{RunKeyPath}, value ""{RunValueName}""";
    }

    [SupportedOSPlatform("windows")]
    private static bool UninstallRunKey()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key?.GetValue(RunValueName) == null) return false;
        key.DeleteValue(RunValueName, throwOnMissingValue: false);
        return true;
    }
}
