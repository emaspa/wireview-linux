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
    PanelGui Gui);

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

/// <summary>Every failure: {"error": "..."}.</summary>
internal sealed record PanelError(string Error);

/// <summary>Source-generated serializer for the control panel: no reflection, so it
/// works in WireView Mini's NativeAOT build. The options are the LAN contract's
/// (<see cref="WireViewJsonContext"/>): camelCase, case-insensitive reading, nulls
/// written. Request bodies are read into <see cref="DeviceConfigModel"/> and
/// <see cref="DaemonConfigEdit"/>.</summary>
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
[JsonSerializable(typeof(DeviceConfigModel))]
[JsonSerializable(typeof(DaemonConfigEdit))]
internal sealed partial class MiniJsonContext : JsonSerializerContext
{
}
