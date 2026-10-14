using System;
using System.IO;

namespace WireView2.Device;

/// <summary>The firmware image shipped next to the executable
/// (<see cref="FileName"/>, copied there by the GUI and WireView Mini builds) and
/// the labels both show for a firmware version and build, so the app and
/// WireView Mini describe the same image with the same words.</summary>
public static class BundledFirmware
{
    /// <summary>The bundled image's file name.</summary>
    public const string FileName = "TG-WV-PRO2-FW.hex";

    /// <summary>Path of the firmware image shipped alongside the executable, if any.</summary>
    public static string GetPath() => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>"v05" for 5, "-" when unknown.</summary>
    public static string FormatVersion(int? version) =>
        version.HasValue ? "v" + version.Value.ToString().PadLeft(2, '0') : "-";

    /// <summary>"(TG-WV-PRO2-FW_20260902_0741)", empty when there is no build string.</summary>
    public static string FormatBuild(string? build) =>
        string.IsNullOrWhiteSpace(build) ? string.Empty : "(" + build.Trim() + ")";

    /// <summary>Version and build together: "v05 (TG-WV-PRO2-FW_20260902_0741)".</summary>
    public static string Label(int? version, string? build) =>
        $"{FormatVersion(version)} {FormatBuild(build)}".Trim();
}
