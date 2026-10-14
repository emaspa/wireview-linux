using System.Text.Json.Serialization;
using WireView2.Device;
using WireView2.Net;

namespace WireView2.Mini;

// The control panel's JSON answers (ControlPanel.cs), one type per shape, written
// camelCase by MiniJsonContext: "ShutdownOnFault" goes out as "shutdownOnFault".
// ControlPanel.html reads these names, so renaming a property changes the API.

/// <summary>GET /api/status.</summary>
internal sealed record PanelStatus(
    PanelMiniInfo Mini,
    PanelPublishInfo Publish,
    PanelDevice? Device,
    PanelDaemon Daemon,
    List<PanelRemoteHost> RemoteHosts,
    PanelSettings Settings,
    PanelGui Gui,
    PanelFlash Firmware,
    PanelThemeUpload? Theme);

internal sealed record PanelMiniInfo(
    string Version, long UptimeSeconds, string Os, string Host, string? LogPath, string SettingsPath, bool NoShutdown);

/// <param name="Fallback">The wanted port is taken: the panel and /sensors are on a loopback fallback.</param>
internal sealed record PanelPublishInfo(
    bool Listening, string? Address, int? Port, bool Lan, string? Error, bool Fallback, int? WantedPort, bool? WantedLan);

/// <summary>This PC's device. <paramref name="FaultText"/>: the debounced state the
/// alerts use, else the raw bits.</summary>
internal sealed record PanelDevice(
    string? Name, string? Firmware, string? BuildString, string? UniqueId, string? Source,
    bool Connected, bool SerialSuspended, bool CanWrite, WireViewSensorDto? Readings,
    int FaultStatus, int FaultLog, string? FaultText, string? FaultLogText);

/// <summary>present: wireviewd serves this PC's device (it does the LAN
/// publishing); publishing and port: what answers on its port (null before the
/// first look); editable: wireviewctl has a config command (null while not known).</summary>
internal sealed record PanelDaemon(bool Present, bool? Publishing, int? Port, bool? Editable);

internal sealed record PanelRemoteHost(
    string Host, string Endpoint, bool Online, string? HostName, WireViewSensorDto? Readings, string? FaultText);

internal sealed record PanelSettings(
    bool ShutdownOnFault, bool StartAtLogin, bool PublishEnabled, int PublishPort,
    List<string> RemoteHosts, bool NetworkSecretSet, string NetworkSecret);

internal sealed record PanelGui(string? Location, bool Available);

/// <summary>GET and POST /api/config. <paramref name="Result"/>: set after a write.</summary>
internal sealed record PanelConfig(
    string DeviceId, int Version, int? Firmware, DeviceConfigModel Config, DeviceConfigOptions Options,
    PanelConfigWrite? Result);

internal sealed record PanelConfigWrite(bool Changed, bool ReadBackMatches);

/// <summary>A command that worked (POST /api/command) and POST /api/open-app.</summary>
internal sealed record PanelMessage(bool Ok, string Message);

/// <summary>POST /api/settings. <paramref name="PanelUrl"/>: set when the port moved.</summary>
internal sealed record PanelSettingsSaved(
    bool Ok, string Message, PanelSettings Settings, string? PanelUrl, string? PublishError);

/// <summary>wireviewd's settings as the panel edits them.</summary>
internal sealed record PanelDaemonConfig(
    bool RemoteEnabled, int Port, string Bind, string Secret, int LogDays, List<string> Extra);

/// <summary>GET /api/daemon-config.</summary>
internal sealed record PanelDaemonConfigRead(
    bool Ok, PanelDaemonConfig? Config, string? Error, bool Unsupported, string? Note, string? Tool, PanelDaemon Daemon);

/// <summary>POST /api/daemon-config. <paramref name="PanelUrl"/>: set when WireView
/// Mini's listener moved off the daemon's new port, so the page follows it.</summary>
internal sealed record PanelDaemonConfigWritten(
    bool Ok, string? Reply, string? Error, bool Unsupported, IReadOnlyList<string> Changed,
    PanelDaemonConfig? Config, PanelSettings Settings, string? PanelUrl, PanelDaemon Daemon);

/// <summary>The firmware flash state, in /api/status (polled every second, so the
/// progress needs no extra requests) and in the firmware answers.</summary>
/// <param name="State">Idle, EnteringBootloader, WaitingForDfu, Flashing, Done or Failed.</param>
/// <param name="Progress">0 to 1 while flashing.</param>
/// <param name="Running">Between the start and the end of a flash.</param>
/// <param name="UpdateAvailable">The bundled firmware is newer than the device's (cached verdict).</param>
internal sealed record PanelFlash(string State, double Progress, string? Message, bool Running, bool UpdateAvailable);

/// <param name="Firmware">The version number as the device reports it ("5"), null while unknown.</param>
/// <param name="Label">"v05 (TG-WV-PRO2-FW_...)", the app's words.</param>
/// <param name="Route">serial, daemon (wireviewd) or readOnly (hwmon without wireviewd).</param>
internal sealed record PanelFirmwareDevice(
    string Name, string? Firmware, string? BuildString, string Label, string Route, bool Connected);

/// <param name="Error">Why the image cannot be used (missing, unparsable), null when it can.</param>
internal sealed record PanelFirmwareImage(string Path, int? Version, string? BuildString, string? Label, string? Error);

/// <summary>GET /api/firmware. <paramref name="Verdict"/>: a FlashVerdict name
/// (Newer, Same, Unknown, Downgrade, ProductMismatch, TooOldForNoctua), null with
/// no device or no image. <paramref name="Reason"/>: why it cannot flash, when
/// <paramref name="CanFlash"/> is false. <paramref name="NeedsOverride"/>: a
/// downgrade or the same build, which POST /api/firmware/flash takes only with
/// allowOlder. <paramref name="DfuUtil"/>: dfu-util's version line, null when
/// missing (and on Windows, where the native flasher needs none).</summary>
internal sealed record PanelFirmware(
    PanelFirmwareDevice? Device, PanelFirmwareImage Bundled, string? Verdict, string? VerdictText,
    bool CanFlash, string? Reason, bool NeedsOverride, string? OverrideWarning, string? OverrideLabel,
    string? ConfirmText, bool DfuUtilRequired, string? DfuUtil, string Hint, PanelFlash Flash);

/// <summary>POST /api/firmware/flash when it started (202).</summary>
internal sealed record PanelFlashStarted(bool Ok, string Message, PanelFlash Flash);

/// <summary>A theme upload's state: in /api/status while one runs (null
/// otherwise), and in the theme answers.</summary>
/// <param name="State">Idle, Uploading, WritingConfig, Redrawing, Done or Failed.</param>
internal sealed record PanelThemeUpload(string State, double Progress, string? Message, bool Running);

/// <summary>A device theme. Colours are "#rrggbb" ("#aarrggbb" when the alpha is
/// not 255); the slot ids are THEME_BACKGROUND and THEME_FAN values.</summary>
internal sealed record PanelThemeValues(
    string PrimaryColor, string SecondaryColor, string HighlightColor, string BackgroundColor,
    bool DisplayInversion, int BackgroundBitmapId, int FanBitmapId);

/// <summary>The device's artwork read for the preview: base64 RGB565 in the
/// device layout (column-major, mirrored, little-endian). Background null for
/// slot none; fan frames null when the slot has none.</summary>
internal sealed record PanelThemeArtwork(
    int BackgroundSlot, int FanSlot, int Width, int Height, string? Background,
    int FanWidth, int FanHeight, string? FanFrame1, string? FanFrame2);

/// <summary>One of the app's presets. <paramref name="Name"/>: the app's (ThemeTg1 ... ThemeNoctua).</summary>
internal sealed record PanelThemePreset(string Name, string Label, PanelThemeValues Values);

internal sealed record PanelOption(int Value, string Label);

/// <summary>GET /api/theme and POST /api/theme (200). <paramref name="Unavailable"/>:
/// why the editor cannot be used (not connected, older firmware), null when it can.
/// <paramref name="Preset"/>: the app's preset for the device's values, or Custom.
/// <paramref name="Artwork"/>: null when not read (artwork=0, or the read failed:
/// <paramref name="ArtworkError"/>). <paramref name="FactoryArtwork"/>: ext_flash.bin is
/// next to WireView Mini (Restore factory artwork).</summary>
internal sealed record PanelTheme(
    bool UiV2Supported, string? Unavailable, int? ConfigVersion, PanelThemeValues? Values, string? Preset,
    PanelThemeArtwork? Artwork, string? ArtworkError, List<PanelThemePreset> Presets,
    List<PanelOption> Backgrounds, List<PanelOption> Fans, bool FactoryArtwork,
    PanelThemeUpload Upload, string? Message);

/// <summary>POST /api/theme and POST /api/theme/restore-defaults when an upload started (202).</summary>
internal sealed record PanelThemeStarted(bool Ok, string Message, PanelThemeUpload Upload);

/// <summary>POST /api/theme/file: an uploaded .wv2t read as the editor's state
/// (the images base64 RGB565, null when the file has none).</summary>
internal sealed record PanelThemeFile(
    PanelThemeValues Values, string Preset, string? Background, string? FanFrame1, string? FanFrame2, string Message);

/// <summary>Every failure: {"error": "..."}.</summary>
internal sealed record PanelError(string Error);

/// <summary>Source-generated serializer for the control panel: no reflection, so it
/// works in WireView Mini's NativeAOT build. The options are the LAN contract's
/// (<see cref="WireViewJsonContext"/>): camelCase, case-insensitive reading, nulls
/// written. Request bodies are read into <see cref="DeviceConfigModel"/>,
/// <see cref="DaemonConfigEdit"/> and <see cref="ThemeEdit"/>.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(PanelStatus))]
[JsonSerializable(typeof(PanelConfig))]
[JsonSerializable(typeof(PanelMessage))]
[JsonSerializable(typeof(PanelSettingsSaved))]
[JsonSerializable(typeof(PanelDaemonConfigRead))]
[JsonSerializable(typeof(PanelDaemonConfigWritten))]
[JsonSerializable(typeof(PanelError))]
[JsonSerializable(typeof(PanelFirmware))]
[JsonSerializable(typeof(PanelFlashStarted))]
[JsonSerializable(typeof(PanelTheme))]
[JsonSerializable(typeof(PanelThemeStarted))]
[JsonSerializable(typeof(PanelThemeFile))]
[JsonSerializable(typeof(ThemeEdit))]
[JsonSerializable(typeof(DeviceConfigModel))]
[JsonSerializable(typeof(DaemonConfigEdit))]
internal sealed partial class MiniJsonContext : JsonSerializerContext
{
}
