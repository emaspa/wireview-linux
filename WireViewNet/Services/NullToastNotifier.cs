namespace WireView2.Services;

/// <summary>Drops every notification (platforms without a notifier).</summary>
public sealed class NullToastNotifier : IToastNotifier
{
    public bool Show(string title, string message) => false;
}
