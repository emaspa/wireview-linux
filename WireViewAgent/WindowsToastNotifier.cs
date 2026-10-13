#if WINDOWS
using Microsoft.Toolkit.Uwp.Notifications;
using WireView2.Net;
using WireView2.Services;

namespace WireView2.Agent;

/// <summary>Windows toast notifications for an unpackaged program, through
/// Microsoft.Toolkit.Uwp.Notifications (it registers the app's identity under
/// HKCU on first use). Best-effort: a failure is logged, never thrown.</summary>
internal sealed class WindowsToastNotifier : IToastNotifier
{
    public void Show(string title, string message)
    {
        try
        {
            new ToastContentBuilder()
                .AddText(title)
                .AddText(message)
                .Show();
        }
        catch (Exception ex)
        {
            FileLog.Warn($"toast failed: {ex.Message}");
        }
    }
}
#endif
