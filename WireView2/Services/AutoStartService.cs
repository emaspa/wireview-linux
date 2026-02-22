using System;
using System.IO;

namespace WireView2.Services;

/// <summary>
/// Linux auto-start service using XDG autostart (~/.config/autostart/*.desktop).
/// </summary>
public static class AutoStartService
{
    private const string DesktopFileName = "wireview2.desktop";
    private const string AppName = "WireView Pro II";

    private static string GetDesktopFilePath()
    {
        string autostartDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "autostart");
        Directory.CreateDirectory(autostartDir);
        return Path.Combine(autostartDir, DesktopFileName);
    }

    public static void SetAutoStart(bool enabled)
    {
        string desktopFile = GetDesktopFilePath();

        if (enabled)
        {
            string? processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
            {
                throw new InvalidOperationException(
                    "Unable to resolve the executable path for auto-start.");
            }

            string contents =
                "[Desktop Entry]\n" +
                $"Name={AppName}\n" +
                $"Exec=\"{processPath}\"\n" +
                "Type=Application\n" +
                "X-GNOME-Autostart-enabled=true\n" +
                "Terminal=false\n" +
                "Comment=WireView Pro II power monitoring application\n";

            File.WriteAllText(desktopFile, contents);
        }
        else
        {
            if (File.Exists(desktopFile))
            {
                File.Delete(desktopFile);
            }
        }
    }

    public static bool GetAutoStart()
    {
        string desktopFile = GetDesktopFilePath();
        return File.Exists(desktopFile);
    }
}
