using System;
using Avalonia;
using Avalonia.Logging;
using Avalonia.Media;
using WireView2.Services;

namespace WireView2;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (SingleInstanceService.IsFirstInstance())
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new FontManagerOptions { DefaultFamilyName = "avares://WireView2/Assets/Fonts/Inter-Regular.ttf#Inter" })
            .LogToTrace(LogEventLevel.Warning);
    }
}
