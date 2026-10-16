using System.ComponentModel;
using System.Diagnostics;
using WireView2.Net;

namespace WireView2.Services;

/// <summary>Desktop notifications on Linux through <c>notify-send</c>
/// (libnotify). Best-effort: a failure is logged, never thrown.</summary>
public sealed class LinuxToastNotifier : IToastNotifier
{
    /// <summary>How long <see cref="Show"/> waits for notify-send's exit status.
    /// It normally exits in a few milliseconds; one still running after this
    /// (a notification server slow to start) counts as handed over.</summary>
    private const int ExitWaitMs = 2000;

    /// <summary>Whether notify-send is on the PATH (to tell "not installed" from
    /// "refused" after a failed <see cref="Show"/>).</summary>
    public static bool Found() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, "notify-send")));

    public bool Show(string title, string message)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "notify-send",
                ArgumentList = { title, message },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            });
            if (p == null)
            {
                FileLog.Warn($"notification not shown (notify-send did not start): {title}: {message}");
                return false;
            }
            if (!p.WaitForExit(ExitWaitMs)) return true;
            if (p.ExitCode == 0) return true;
            string err = p.StandardError.ReadToEnd().Trim();
            FileLog.Warn($"notification refused (notify-send exited with {p.ExitCode}{(err.Length > 0 ? $": {err}" : "")}): {title}: {message}");
            return false;
        }
        catch (Win32Exception)
        {
            FileLog.Warn($"notification not shown (notify-send not found): {title}: {message}");
            return false;
        }
        catch (Exception ex)
        {
            FileLog.Warn($"notification failed: {ex.Message}");
            return false;
        }
    }
}
