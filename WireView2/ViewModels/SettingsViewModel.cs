using System;
using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using WireView2.Services;

namespace WireView2.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private bool _exportToHwinfo;
    private bool _autoStart;
    private bool _startMinimized;
    private AppSettings.ThemeMode _themePreference;
    private AppSettings.BackgroundColorMode _backgroundColorPreference;
    private double _backgroundOpacity;
    private AppSettings.StartupScreen _screenAfterConnection;
    private bool _softwareShutdownOnFault;

    // ======================== Properties ========================

    public bool ExportToHwinfo
    {
        get => _exportToHwinfo;
        set
        {
            if (Set(ref _exportToHwinfo, value))
                AppSettings.SaveCurrent();
        }
    }

    public bool AutoStart
    {
        get => _autoStart;
        set
        {
            if (Set(ref _autoStart, value))
            {
                AutoStartService.SetAutoStart(value);
                AppSettings.Current.AutoStart = value;
                AppSettings.SaveCurrent();
            }
        }
    }

    public bool StartMinimized
    {
        get => _startMinimized;
        set
        {
            if (Set(ref _startMinimized, value))
            {
                AppSettings.Current.StartMinimized = value;
                AppSettings.SaveCurrent();
            }
        }
    }

    public AppSettings.ThemeMode ThemePreference
    {
        get => _themePreference;
        set
        {
            if (Set(ref _themePreference, value))
            {
                AppSettings.Current.ThemePreference = value;
                AppSettings.SaveCurrent();
                ApplyTheme(value);
            }
        }
    }

    public AppSettings.BackgroundColorMode BackgroundColorPreference
    {
        get => _backgroundColorPreference;
        set
        {
            if (Set(ref _backgroundColorPreference, value))
            {
                AppSettings.Current.BackgroundColorPreference = value;
                AppSettings.SaveCurrent();
                ApplyBackgroundColor(value);
            }
        }
    }

    public double BackgroundOpacity
    {
        get => _backgroundOpacity;
        set
        {
            double clamped = ClampOpacity(value);
            if (Set(ref _backgroundOpacity, clamped))
            {
                AppSettings.Current.BackgroundOpacity = clamped;
                AppSettings.SaveCurrent();
                ApplyBackgroundOpacity(clamped);
            }
        }
    }

    public AppSettings.StartupScreen ScreenAfterConnection
    {
        get => _screenAfterConnection;
        set
        {
            if (Set(ref _screenAfterConnection, value))
            {
                AppSettings.Current.ScreenAfterConnection = value;
                AppSettings.SaveCurrent();
            }
        }
    }

    public bool SoftwareShutdownOnFault
    {
        get => _softwareShutdownOnFault;
        set
        {
            if (Set(ref _softwareShutdownOnFault, value))
            {
                AppSettings.Current.SoftwareShutdownOnFault = value;
                AppSettings.SaveCurrent();
            }
        }
    }

    public string BuildDateText
    {
        get
        {
            var infoVer = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (infoVer != null)
            {
                // Format: 1.0.2.0+build20260222143012
                var plusIdx = infoVer.IndexOf("+build", StringComparison.Ordinal);
                if (plusIdx >= 0)
                {
                    var stamp = infoVer.Substring(plusIdx + 6);
                    if (DateTime.TryParseExact(stamp, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal, out var dt))
                    {
                        return "Built on " + dt.ToString("yyyy-MM-dd HH:mm") + " UTC";
                    }
                }
            }
            return "Build date unknown";
        }
    }

    public Array ThemeModes => Enum.GetValues(typeof(AppSettings.ThemeMode));
    public Array BackgroundColorModes => Enum.GetValues(typeof(AppSettings.BackgroundColorMode));
    public Array DeviceScreens => Enum.GetValues(typeof(AppSettings.StartupScreen));

    // ======================== Constructor ========================

    public SettingsViewModel()
    {
        _autoStart = AppSettings.Current.AutoStart;
        _startMinimized = AppSettings.Current.StartMinimized;
        _themePreference = AppSettings.Current.ThemePreference;
        _backgroundColorPreference = AppSettings.Current.BackgroundColorPreference;
        ApplyBackgroundColor(_backgroundColorPreference);
        _backgroundOpacity = ClampOpacity(AppSettings.Current.BackgroundOpacity);
        ApplyBackgroundOpacity(_backgroundOpacity);
        _screenAfterConnection = AppSettings.Current.ScreenAfterConnection;
        _softwareShutdownOnFault = AppSettings.Current.SoftwareShutdownOnFault;
        AppSettings.Saved += OnSettingsSaved;
    }

    // ======================== Settings reload handler ========================

    private void OnSettingsSaved(object? sender, EventArgs e)
    {
        if (Set(ref _autoStart, AppSettings.Current.AutoStart, nameof(AutoStart)))
            OnPropertyChanged(nameof(AutoStart));

        if (Set(ref _startMinimized, AppSettings.Current.StartMinimized, nameof(StartMinimized)))
            OnPropertyChanged(nameof(StartMinimized));

        if (Set(ref _themePreference, AppSettings.Current.ThemePreference, nameof(ThemePreference)))
        {
            OnPropertyChanged(nameof(ThemePreference));
            ApplyTheme(_themePreference);
        }

        if (Set(ref _backgroundColorPreference, AppSettings.Current.BackgroundColorPreference,
                nameof(BackgroundColorPreference)))
        {
            OnPropertyChanged(nameof(BackgroundColorPreference));
            ApplyBackgroundColor(_backgroundColorPreference);
        }

        double opacity = ClampOpacity(AppSettings.Current.BackgroundOpacity);
        if (Set(ref _backgroundOpacity, opacity, nameof(BackgroundOpacity)))
        {
            OnPropertyChanged(nameof(BackgroundOpacity));
            ApplyBackgroundOpacity(opacity);
        }

        if (Set(ref _screenAfterConnection, AppSettings.Current.ScreenAfterConnection,
                nameof(ScreenAfterConnection)))
            OnPropertyChanged(nameof(ScreenAfterConnection));

        if (Set(ref _softwareShutdownOnFault, AppSettings.Current.SoftwareShutdownOnFault,
                nameof(SoftwareShutdownOnFault)))
            OnPropertyChanged(nameof(SoftwareShutdownOnFault));
    }

    // ======================== Theme / appearance ========================

    private static void ApplyTheme(AppSettings.ThemeMode mode)
    {
        var app = Application.Current;
        if (app == null) return;

        app.RequestedThemeVariant = mode switch
        {
            AppSettings.ThemeMode.Auto  => ThemeVariant.Default,
            AppSettings.ThemeMode.Light => ThemeVariant.Light,
            AppSettings.ThemeMode.Dark  => ThemeVariant.Dark,
            _                           => ThemeVariant.Default,
        };

        AppSettings.Current.BackgroundOpacity =
            app.ActualThemeVariant == ThemeVariant.Light ? 1.0 : 0.5;
        ApplyBackgroundOpacity(AppSettings.Current.BackgroundOpacity);
        ApplyBackgroundColor(AppSettings.Current.BackgroundColorPreference);
    }

    private static double ClampOpacity(double value)
    {
        if (!double.IsFinite(value)) return 0.5;
        return Math.Clamp(value, 0.0, 1.0);
    }

    private static void ApplyBackgroundOpacity(double opacity)
    {
        var app = Application.Current;
        if (app != null
            && app.TryFindResource("AppBackgroundBrush", app.ActualThemeVariant, out object? resource)
            && resource is ImageBrush imageBrush)
        {
            imageBrush.Opacity = opacity;
        }
    }

    private static void ApplyBackgroundColor(AppSettings.BackgroundColorMode mode)
    {
        var app = Application.Current;
        if (app?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        var mainWindow = desktop.MainWindow;
        if (mainWindow == null) return;

        mainWindow.Background = mode switch
        {
            AppSettings.BackgroundColorMode.Black => Brushes.Black,
            AppSettings.BackgroundColorMode.White => Brushes.White,
            AppSettings.BackgroundColorMode.Auto  => app.ActualThemeVariant == ThemeVariant.Light
                                                         ? Brushes.White
                                                         : Brushes.Black,
            _ => Brushes.Black,
        };
    }
}
