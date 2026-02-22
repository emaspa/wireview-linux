using System.Diagnostics;

namespace WireView2.Services;

public sealed class LinuxToastNotifier : IToastNotifier
{
    public void Show(string title, string message)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "notify-send",
                ArgumentList = { title, message },
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch { }
    }
}
