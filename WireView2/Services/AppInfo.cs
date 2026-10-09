using System;
using System.Reflection;

namespace WireView2.Services;

/// <summary>
/// Canonical, OS-aware app branding shown in the title bar and the About box.
/// The platform token ("Windows" / "macOS" / "Linux") is resolved at runtime, so
/// the same build labels itself correctly wherever it runs.
/// </summary>
public static class AppInfo
{
    /// <summary>"Windows", "macOS", or "Linux" depending on the running OS.</summary>
    public static string Os =>
        OperatingSystem.IsWindows() ? "Windows" :
        OperatingSystem.IsMacOS()   ? "macOS"   :
                                      "Linux";

    /// <summary>The build's displayed version: the informational version without its
    /// "+build..." suffix. A release build says "1.3.0.0"; a build from any other
    /// git state carries its `git describe`, e.g. "1.3.0.0-2-g1a2b3c4-dirty"; the
    /// Windows build appends " beta" (set in WireView2.csproj). Falls back to the
    /// numeric assembly version.</summary>
    public static string Version
    {
        get
        {
            var asm = Assembly.GetExecutingAssembly();
            var infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(infoVer))
                return infoVer.Split('+')[0];
            return asm.GetName().Version?.ToString() ?? "0.0.0";
        }
    }

    /// <summary>App title with platform and version, e.g.
    /// "WireView Pro II - Unofficial Windows Plus Client v1.1.0.0".</summary>
    public static string TitleWithVersion
    {
        get
        {
            var asm = Assembly.GetExecutingAssembly();
            string title = asm.GetCustomAttribute<AssemblyTitleAttribute>()?.Title ?? "WireView Pro II";
            return $"{title} - Unofficial {Os} Plus Client v{Version}";
        }
    }
}
