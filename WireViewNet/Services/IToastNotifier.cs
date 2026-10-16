namespace WireView2.Services;

/// <summary>A desktop notification sink (fault alerts, failed daemon commands).
/// Shared by the GUI and the WireView Mini.</summary>
public interface IToastNotifier
{
    /// <summary>Shows a notification. Returns true when it was handed to the
    /// desktop, false when it could not be (the reason is logged). Never throws;
    /// callers that only want best-effort delivery ignore the result.</summary>
    bool Show(string title, string message);
}
