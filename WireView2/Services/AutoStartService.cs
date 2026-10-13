namespace WireView2.Services;

/// <summary>
/// Linux auto-start service using XDG autostart (~/.config/autostart/*.desktop).
/// </summary>
public static class AutoStartService
{
    private static readonly XdgAutostart Entry = new(
        "wireview2.desktop",
        "WireView Pro II",
        "WireView Pro II power monitoring application");

    public static void SetAutoStart(bool enabled) => Entry.Set(enabled);

    public static bool GetAutoStart() => Entry.IsEnabled;
}
