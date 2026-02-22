using System;
using Avalonia;
using Avalonia.Logging;
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
            .LogToTrace(LogEventLevel.Warning);
    }
}
