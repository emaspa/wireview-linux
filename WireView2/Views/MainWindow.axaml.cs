using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using WireView2.Services;

namespace WireView2.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = AppInfo.TitleWithVersion;
        var navPane = this.FindControl<Border>("NavPane");
        if (navPane != null)
        {
            UpdateNavWidth(navPane);
            navPane.PointerEntered += (_, _) => UpdateNavWidth(navPane);
            navPane.PointerExited += (_, _) => UpdateNavWidth(navPane);
            AppSettings.Saved += (_, _) => Dispatcher.UIThread.Post(() => UpdateNavWidth(navPane));
        }
    }

    private static void UpdateNavWidth(Border navPane)
    {
        navPane.Width = AppSettings.Current.NavPane switch
        {
            AppSettings.NavPaneMode.Expanded => 180.0,
            AppSettings.NavPaneMode.Minimal => 48.0,
            _ => navPane.IsPointerOver ? 180.0 : 48.0,
        };
    }
}
