namespace WireView2.Services;

/// <summary>Drops every notification (platforms without a notifier).</summary>
public sealed class NullToastNotifier : IToastNotifier
{
    public void Show(string title, string message) { }
}
