namespace WireView2.Services;

public sealed class NullToastNotifier : IToastNotifier
{
    public void Show(string title, string message) { }
}
