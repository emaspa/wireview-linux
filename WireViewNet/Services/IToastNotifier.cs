namespace WireView2.Services;

/// <summary>A desktop notification sink (fault alerts, failed daemon commands).
/// Shared by the GUI and the headless agent.</summary>
public interface IToastNotifier
{
    void Show(string title, string message);
}
