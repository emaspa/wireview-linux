using System.Runtime.Versioning;
using WireView2.Services;

namespace WireView2.Agent;

/// <summary>
/// <c>--install</c> / <c>--uninstall</c>: starts the agent at login.
/// Linux (and other Unix desktops): an XDG autostart entry,
/// <c>~/.config/autostart/wireview-agent.desktop</c>.
/// Windows: the per-user Run key (<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>,
/// value "WireView Agent"). The Run key needs no administrator rights, unlike a
/// Task Scheduler logon trigger, and since the Windows build is a windowless
/// program it starts with no console window.
/// </summary>
internal static class AgentInstaller
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "WireView Agent";

    private static readonly XdgAutostart Xdg = new(
        "wireview-agent.desktop",
        "WireView Agent",
        "WireView Pro II headless agent: fault alerts, shutdown on fault, LAN publishing");

    public static int Install()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                string where = InstallRunKey();
                Console.WriteLine($"Installed: wireview-agent starts at login ({where}).");
            }
            else
            {
                Xdg.Set(true);
                Console.WriteLine($"Installed: wireview-agent starts at login ({Xdg.DesktopFilePath}).");
            }
            Console.WriteLine("Run wireview-agent now to start it without logging out.");
            return Program.ExitOk;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"wireview-agent: install failed: {ex.Message}");
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
                    ? "Uninstalled: wireview-agent no longer starts at login."
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
            Console.WriteLine("A running agent keeps running; stop it with Ctrl+C, kill or Task Manager.");
            return Program.ExitOk;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"wireview-agent: uninstall failed: {ex.Message}");
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
