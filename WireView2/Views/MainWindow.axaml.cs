using System;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Styling;

namespace WireView2.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = GetTitleWithVersion();
        var navPane = this.FindControl<Border>("NavPane");
        if (navPane != null)
        {
            navPane.PointerEntered += (_, _) => ExpandNav(navPane, true);
            navPane.PointerExited += (_, _) => ExpandNav(navPane, false);
        }
    }

    private static string GetTitleWithVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        string title = asm.GetCustomAttribute<AssemblyTitleAttribute>()?.Title ?? "WireView2";
        var infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVer))
        {
            var parts = infoVer.Split('+');
            if (parts.Length > 0) title += " v" + parts[0];
        }
        else
        {
            var ver = asm.GetName().Version;
            if (ver != null) title += $" v{ver}";
        }
        return title + " - Linux Unofficial Client";
    }

    private static void ExpandNav(Border navPane, bool expand)
    {
        navPane.Width = expand ? 180.0 : 48.0;
    }
}
