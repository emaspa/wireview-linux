using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;

namespace WireView2.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void OnRepoLinkPressed(object? sender, PointerPressedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://github.com/emaspa/wireview-linux",
            UseShellExecute = true
        });
    }
}
