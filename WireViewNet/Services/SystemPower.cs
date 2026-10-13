using System.Diagnostics;

namespace WireView2.Services;

/// <summary>Powers the machine off for the shutdown-on-fault option. Shared by
/// the GUI and the headless agent.</summary>
public static class SystemPower
{
    /// <summary>Starts an immediate shutdown: <c>shutdown /s /t 0</c> on Windows,
    /// <c>systemctl poweroff</c> elsewhere with <c>shutdown -h now</c> as the
    /// fallback when systemctl cannot be started. Best-effort, never throws.</summary>
    public static void Shutdown()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                Process.Start(new ProcessStartInfo("shutdown", "/s /t 0")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            }
            catch { }
            return;
        }

        try
        {
            Process.Start("systemctl", "poweroff");
        }
        catch
        {
            // Fallback if systemctl is not available
            try { Process.Start("shutdown", "-h now"); } catch { }
        }
    }
}
