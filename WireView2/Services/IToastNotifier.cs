namespace WireView2.Services;

public interface IToastNotifier
{
    void Show(string title, string message);
}
