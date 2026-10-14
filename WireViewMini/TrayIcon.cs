using System.Globalization;

namespace WireView2.Mini;

/// <summary>The menu entries a tray click acts on. The values are the menu item
/// ids on both platforms (Win32 command ids, dbusmenu item ids).</summary>
internal enum TrayCommand
{
    OpenApp = 2,
    TogglePublish = 4,
    ToggleShutdownOnFault = 5,
    ToggleStartAtLogin = 6,
    Quit = 8,
    OpenPanel = 9,
    ClearFaults = 11,
    ScreenMain = 20,
    ScreenSimple = 21,
    ScreenCurrent = 22,
    ScreenTemp = 23,
    ScreenStatus = 24,
    PauseUpdates = 26,
    ResumeUpdates = 27,
}

internal enum TrayItemKind { Header, Action, Check, Separator, Submenu }

/// <summary>One menu line. <see cref="Id"/> is stable across updates. A
/// <see cref="TrayItemKind.Submenu"/> carries its lines in <see cref="Children"/>;
/// a header line is shown disabled.</summary>
internal sealed record TrayItem(int Id, TrayItemKind Kind, string Label = "", bool Checked = false,
                                bool Enabled = true, IReadOnlyList<TrayItem>? Children = null)
{
    /// <summary>The line itself, children left out: equal lines draw the same.</summary>
    public TrayItem Line => Children == null ? this : this with { Children = null };

    public bool IsEnabled => Kind != TrayItemKind.Header && Enabled;
}

/// <summary>Everything a tray icon shows: the menu header line, the checkbox
/// states, whether a fault is active, whether the device takes commands and the
/// remote host lines. <see cref="DaemonLine"/>, set when wireviewd does the LAN
/// publishing (Linux), replaces the Publish on the LAN checkbox with a disabled
/// line. Equal states render identically, so implementations compare with the
/// last one and send only what changed.</summary>
internal sealed record TrayState(
    string Header,
    bool Connected,
    bool Fault,
    bool PublishEnabled,
    bool ShutdownOnFault,
    bool StartAtLogin,
    bool DeviceCommands = false,
    string RemoteLines = "",
    string? DaemonLine = null)
{
    public const string AppTitle = "WireView Mini";

    /// <summary>First id of the remote host lines (one per host or device).</summary>
    public const int RemoteLineIdBase = 100;

    /// <summary>The "LAN publishing: wireviewd, ..." line in place of the publish checkbox.</summary>
    public const int DaemonLineId = 14;

    public static TrayState Initial { get; } = new("No device", false, false, false, false, false);

    /// <summary>Second tooltip line: the live header while a device is connected.</summary>
    public string? DetailLine => Connected ? Header : null;

    /// <summary>Windows tooltip: the name, then the live line.</summary>
    public string Tooltip => DetailLine is { } d ? $"{AppTitle}\n{d}" : AppTitle;

    /// <summary>SNI Title: one line.</summary>
    public string Title => DetailLine is { } d ? $"{AppTitle} - {d}" : AppTitle;

    /// <summary>The menu, top to bottom; same on both platforms.</summary>
    public IReadOnlyList<TrayItem> Items
    {
        get
        {
            var items = new List<TrayItem>
            {
                new(1, TrayItemKind.Header, Header),
                new((int)TrayCommand.OpenApp, TrayItemKind.Action, "Open WireView"),
                new((int)TrayCommand.OpenPanel, TrayItemKind.Action, "Settings and controls..."),
                new(3, TrayItemKind.Separator),
                new(10, TrayItemKind.Submenu, "Screen", Enabled: DeviceCommands, Children:
                [
                    new((int)TrayCommand.ScreenMain, TrayItemKind.Action, "Main", Enabled: DeviceCommands),
                    new((int)TrayCommand.ScreenSimple, TrayItemKind.Action, "Simple", Enabled: DeviceCommands),
                    new((int)TrayCommand.ScreenCurrent, TrayItemKind.Action, "Current", Enabled: DeviceCommands),
                    new((int)TrayCommand.ScreenTemp, TrayItemKind.Action, "Temperature", Enabled: DeviceCommands),
                    new((int)TrayCommand.ScreenStatus, TrayItemKind.Action, "Status", Enabled: DeviceCommands),
                    new(25, TrayItemKind.Separator),
                    new((int)TrayCommand.PauseUpdates, TrayItemKind.Action, "Pause updates", Enabled: DeviceCommands),
                    new((int)TrayCommand.ResumeUpdates, TrayItemKind.Action, "Resume updates", Enabled: DeviceCommands),
                ]),
                new((int)TrayCommand.ClearFaults, TrayItemKind.Action, "Clear faults", Enabled: Fault && DeviceCommands),
            };
            if (RemoteLines.Length > 0)
            {
                var lines = RemoteLines.Split('\n')
                    .Select((line, i) => new TrayItem(RemoteLineIdBase + i, TrayItemKind.Header, line))
                    .ToList();
                items.Add(new TrayItem(12, TrayItemKind.Submenu, "Remote hosts", Children: lines));
            }
            items.AddRange(
            [
                new(7, TrayItemKind.Separator),
                DaemonLine != null
                    ? new(DaemonLineId, TrayItemKind.Header, DaemonLine)
                    : new((int)TrayCommand.TogglePublish, TrayItemKind.Check, "Publish on the LAN", PublishEnabled),
                new((int)TrayCommand.ToggleShutdownOnFault, TrayItemKind.Check, "Shutdown on fault", ShutdownOnFault),
                new((int)TrayCommand.ToggleStartAtLogin, TrayItemKind.Check, "Start at login", StartAtLogin),
                new(13, TrayItemKind.Separator),
                new((int)TrayCommand.Quit, TrayItemKind.Action, "Quit"),
            ]);
            return items;
        }
    }

    /// <summary>Every line, submenu lines after their submenu, depth first.</summary>
    public static IEnumerable<TrayItem> Flatten(IEnumerable<TrayItem> items)
    {
        foreach (var item in items)
        {
            yield return item;
            if (item.Children != null)
                foreach (var child in Flatten(item.Children)) yield return child;
        }
    }

    /// <summary>The header line for the WireView Mini status.</summary>
    public static string HeaderFor(MiniStatus status)
    {
        if (status.Handover) return "Serial port lent to the app";
        if (status.DeviceName is not { } name) return "No device";
        if (status.FaultText is { } fault) return $"{name}: {fault}";
        return string.Create(CultureInfo.InvariantCulture, $"{name}: {status.PowerW ?? 0:0.0} W");
    }

    /// <summary>The tray line while wireviewd publishes: "LAN publishing: wireviewd,
    /// port 9876" while it answers there, "..., off" when it does not, no state
    /// before the first look.</summary>
    public static string DaemonLineFor(DaemonObservation? seen) => seen switch
    {
        { Publishing: true } s => $"LAN publishing: wireviewd, port {s.Port}",
        { } => "LAN publishing: wireviewd, off",
        null => "LAN publishing: wireviewd",
    };

    /// <summary>The Remote hosts submenu's lines: "host: 123.4 W", "host: FAULT: ...",
    /// "host: offline"; a host with several devices gets one line per device.</summary>
    public static string RemoteLinesFor(IReadOnlyList<RemoteHostStatus> hosts)
    {
        var lines = new List<string>();
        foreach (var group in hosts.GroupBy(h => h.Host))
        {
            int n = 0, count = group.Count();
            foreach (var h in group)
            {
                string who = count > 1 ? $"{h.Host} ({++n})" : h.Host;
                lines.Add(!h.Online ? $"{who}: offline"
                    : h.FaultText is { } f ? $"{who}: {f}"
                    : string.Create(CultureInfo.InvariantCulture, $"{who}: {h.Readings?.SumPowerW ?? 0:0.0} W"));
            }
        }
        return string.Join('\n', lines);
    }
}

/// <summary>A notification-area icon with WireView Mini's menu. Created by
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
    /// it runs without one.</summary>
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
