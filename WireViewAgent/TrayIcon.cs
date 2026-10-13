using System.Globalization;

namespace WireView2.Agent;

/// <summary>The menu entries a tray click acts on. The values are the menu item
/// ids on both platforms (Win32 command ids, dbusmenu item ids).</summary>
internal enum TrayCommand
{
    OpenApp = 2,
    TogglePublish = 4,
    ToggleShutdownOnFault = 5,
    ToggleStartAtLogin = 6,
    Quit = 8,
}

internal enum TrayItemKind { Header, Action, Check, Separator }

/// <summary>One menu line. <see cref="Id"/> is stable across updates.</summary>
internal sealed record TrayItem(int Id, TrayItemKind Kind, string Label = "", bool Checked = false);

/// <summary>Everything a tray icon shows: the menu header line, the checkbox
/// states and whether a fault is active. Equal states render identically, so
/// implementations compare with the last one and send only what changed.</summary>
internal sealed record TrayState(
    string Header,
    bool Connected,
    bool Fault,
    bool PublishEnabled,
    bool ShutdownOnFault,
    bool StartAtLogin)
{
    public const string AppTitle = "WireView agent";

    public static TrayState Initial { get; } = new("No device", false, false, false, false, false);

    /// <summary>Second tooltip line: the live header while a device is connected.</summary>
    public string? DetailLine => Connected ? Header : null;

    /// <summary>Windows tooltip: the name, then the live line.</summary>
    public string Tooltip => DetailLine is { } d ? $"{AppTitle}\n{d}" : AppTitle;

    /// <summary>SNI Title: one line.</summary>
    public string Title => DetailLine is { } d ? $"{AppTitle} - {d}" : AppTitle;

    /// <summary>The menu, top to bottom; same on both platforms.</summary>
    public IReadOnlyList<TrayItem> Items =>
    [
        new(1, TrayItemKind.Header, Header),
        new((int)TrayCommand.OpenApp, TrayItemKind.Action, "Open WireView"),
        new(3, TrayItemKind.Separator),
        new((int)TrayCommand.TogglePublish, TrayItemKind.Check, "Publish on the LAN", PublishEnabled),
        new((int)TrayCommand.ToggleShutdownOnFault, TrayItemKind.Check, "Shutdown on fault", ShutdownOnFault),
        new((int)TrayCommand.ToggleStartAtLogin, TrayItemKind.Check, "Start at login", StartAtLogin),
        new(7, TrayItemKind.Separator),
        new((int)TrayCommand.Quit, TrayItemKind.Action, "Quit"),
    ];

    /// <summary>The header line for an agent status.</summary>
    public static string HeaderFor(AgentStatus status)
    {
        if (status.Handover) return "Serial port lent to the app";
        if (status.DeviceName is not { } name) return "No device";
        if (status.FaultText is { } fault) return $"{name}: {fault}";
        return string.Create(CultureInfo.InvariantCulture, $"{name}: {status.PowerW ?? 0:0.0} W");
    }
}

/// <summary>A notification-area icon with the agent's menu. Created by
/// <see cref="TrayIconFactory"/>; menu clicks arrive through the callback given
/// there, on the implementation's own thread.</summary>
internal interface ITrayIcon : IDisposable
{
    /// <summary>Shows a new state. Cheap and non-blocking; callable from any thread.</summary>
    void SetState(TrayState state);
}

internal static class TrayIconFactory
{
    /// <summary>Win32 notification icon on Windows, StatusNotifierItem on Linux,
    /// none elsewhere. Never throws: a tray that cannot come up logs and the
    /// agent runs without one.</summary>
    public static ITrayIcon? Create(TrayState initial, Action<TrayCommand> onCommand)
    {
        try
        {
#if WINDOWS
            return Win32TrayIcon.TryCreate(initial, onCommand);
#else
            return OperatingSystem.IsLinux() ? new SniTrayIcon(initial, onCommand) : null;
#endif
        }
        catch (Exception ex)
        {
            WireView2.Net.FileLog.Warn($"tray: cannot create the tray icon: {ex.Message}. Running without one.");
            return null;
        }
    }
}
