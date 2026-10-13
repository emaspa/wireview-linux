using System;
using System.Collections.Generic;
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
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new FontManagerOptions { DefaultFamilyName = "avares://WireView2/Assets/Fonts/Inter-Regular.ttf#Inter" })
            .LogToTrace(LogEventLevel.Warning);

        // WIREVIEW_RENDER_MODE selects the Avalonia rendering backend, as a
        // comma-separated preference list: software, egl, glx, vulkan (X11) and
        // software, angle, wgl, vulkan (Windows). Unset keeps Avalonia's default.
        // Software rendering keeps Mesa (and its LLVM shader compiler) out of the
        // process, which on this UI is tens of MB for no visible difference.
        string? modes = Environment.GetEnvironmentVariable("WIREVIEW_RENDER_MODE");
        if (string.IsNullOrWhiteSpace(modes) && OperatingSystem.IsLinux())
        {
            // Default on Linux. Measured on X11 (docs/footprint-plan.md): GL via
            // Mesa cost 57 MB RSS and six threads for a UI of gauges and a few
            // line charts, with no CPU difference; GL under XWayland is also where
            // the flashing (#3) and connection-loss reports came from.
            modes = "software";
        }
        if (!string.IsNullOrWhiteSpace(modes))
        {
            var x11 = new List<X11RenderingMode>();
            var win = new List<Win32RenderingMode>();
            foreach (string raw in modes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (raw.ToLowerInvariant())
                {
                    case "software": x11.Add(X11RenderingMode.Software); win.Add(Win32RenderingMode.Software); break;
                    case "egl": x11.Add(X11RenderingMode.Egl); break;
                    case "glx": x11.Add(X11RenderingMode.Glx); break;
                    case "angle": win.Add(Win32RenderingMode.AngleEgl); break;
                    case "wgl": win.Add(Win32RenderingMode.Wgl); break;
                    case "vulkan": x11.Add(X11RenderingMode.Vulkan); win.Add(Win32RenderingMode.Vulkan); break;
                }
            }
            if (x11.Count > 0) builder = builder.With(new X11PlatformOptions { RenderingMode = x11 });
            if (win.Count > 0) builder = builder.With(new Win32PlatformOptions { RenderingMode = win });
        }

        return builder;
    }
}
