using Avalonia.Controls;
using Avalonia.Styling;
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
            navPane.PointerEntered += (_, _) => ExpandNav(navPane, true);
            navPane.PointerExited += (_, _) => ExpandNav(navPane, false);
        }
    }

    private static void ExpandNav(Border navPane, bool expand)
    {
        navPane.Width = expand ? 180.0 : 48.0;
    }
}
