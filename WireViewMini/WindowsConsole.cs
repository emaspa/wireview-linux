#if WINDOWS
using System.Runtime.InteropServices;

namespace WireView2.Mini;

/// <summary>
/// The Windows build is a GUI-subsystem program (OutputType WinExe), so the
/// logon start opens no console window. Run from cmd or PowerShell, it attaches
/// to that console so --status, --help and a foreground run can print. The shell
/// does not wait for a GUI-subsystem program: use <c>start /wait wireview-mini --status</c>
/// in cmd, or pipe it (<c>wireview-mini --status | Out-Host</c>) in PowerShell, to
/// see the output before the next prompt and get the exit code.
/// </summary>
internal static class WindowsConsole
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    /// <summary>Attaches to the parent's console when there is one and the
    /// output is not redirected already. Call before the first Console use.</summary>
    public static void AttachToParent()
    {
        try { AttachConsole(AttachParentProcess); } catch { }
    }
}
#endif
