#if WINDOWS
using WireView2.Net;
using WireView2.Services;

namespace WireView2.Mini;

/// <summary>Windows notifications through the tray icon (Shell_NotifyIcon with
/// NIF_INFO): a toast on Windows 10 and 11, no library, no app registration, and
/// nothing NativeAOT cannot compile. Best-effort: a failure is logged, never
/// thrown. Without a tray icon (no Explorer, or --no-tray) there is no
/// notification, as there is no notification area to show it in.</summary>
internal sealed class WindowsToastNotifier : IToastNotifier
{
    public void Show(string title, string message)
    {
        try
        {
            var tray = Win32TrayIcon.Current;
            if (tray == null) FileLog.Warn($"notification not shown (no tray icon): {title}: {message}");
            else if (!tray.ShowNotification(title, message)) FileLog.Warn($"notification refused by the shell: {title}: {message}");
        }
        catch (Exception ex)
        {
            FileLog.Warn($"notification failed: {ex.Message}");
        }
    }
}
#endif
