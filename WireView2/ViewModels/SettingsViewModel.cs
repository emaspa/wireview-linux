using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    private bool _showTrayPower;
    private bool _showTrayPowerUnit;

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

    public bool ShowTrayPower
    {
        get => _showTrayPower;
        set
        {
            if (Set(ref _showTrayPower, value))
            {
                AppSettings.Current.ShowTrayPower = value;
                AppSettings.SaveCurrent();
            }
        }
    }

    public bool ShowTrayPowerUnit
    {
        get => _showTrayPowerUnit;
        set
        {
            if (Set(ref _showTrayPowerUnit, value))
            {
                AppSettings.Current.ShowTrayPowerUnit = value;
                AppSettings.SaveCurrent();
            }
        }
    }

    /// <summary>The in-app publisher only runs on Windows/macOS; on Linux the wireviewd
    /// daemon owns publishing, so the toggle is hidden there.</summary>
    public bool ShowPublishToggle => !OperatingSystem.IsLinux();

    /// <summary>Whether this host opens the LAN listener — publishing GET /sensors and,
    /// with a secret, accepting authenticated POST /command writes. OFF by default;
    /// opening a port is opt-in and independent of reading remote hosts below. Toggling
    /// starts/stops the listener live (no-op on Linux, where the daemon publishes).</summary>
    public bool PublishEnabled
    {
        get => AppSettings.Current.PublishEnabled;
        set
        {
            if (AppSettings.Current.PublishEnabled == value) return;
            AppSettings.Current.PublishEnabled = value;
            AppSettings.SaveCurrent();
            if (value) WireViewPublishService.Shared.Start();
            else WireViewPublishService.Shared.Stop();
            OnPropertyChanged();
        }
    }

    /// <summary>Comma-separated list of remote WireView hosts to read over the LAN
    /// (host or host:port). Backed by <see cref="AppSettings.RemoteHosts"/>; the
    /// discovery probe re-reads it each tick, so edits apply without a restart.</summary>
    public string RemoteHostsText
    {
        get => AppSettings.Current.RemoteHosts == null
            ? string.Empty
            : string.Join(", ", AppSettings.Current.RemoteHosts);
        set
        {
            var list = (value ?? string.Empty)
                .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            AppSettings.Current.RemoteHosts = list.Count > 0 ? list : null;
            AppSettings.SaveCurrent();
            OnPropertyChanged();
        }
    }

    /// <summary>Shared HMAC passphrase that authenticates remote writes (POST /command).
    /// Backed by <see cref="AppSettings.NetworkSecret"/>; the same value must be set on
    /// every host. Empty disables remote writes. Stored plaintext in settings.json.</summary>
    public string NetworkSecretText
    {
        get => AppSettings.Current.NetworkSecret ?? string.Empty;
        set
        {
            AppSettings.Current.NetworkSecret = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            AppSettings.SaveCurrent();
            OnPropertyChanged();
        }
    }

    /// <summary>Days of daily-rotating audit logs to keep (0 = forever). Applied live.</summary>
    public string LogRetentionDaysText
    {
        get => AppSettings.Current.LogRetentionDays.ToString();
        set
        {
            if (int.TryParse(value, out int days) && days >= 0)
            {
                AppSettings.Current.LogRetentionDays = days;
                AppSettings.SaveCurrent();
                WireView2.Net.FileLog.SetRetentionDays(days);
            }
            OnPropertyChanged();
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

    /// <summary>OS-aware app name shown in the About box, e.g.
    /// "WireView Pro II - Unofficial Windows Plus Client v1.1.0.0".</summary>
    public string AppTitle => AppInfo.TitleWithVersion;

    /// <summary>"Windows port by" / "Linux port by" depending on the running OS.</summary>
    public string PortByText => $"{AppInfo.Os} port by";

    /// <summary>Disclaimer paragraph, with the platform substituted for the running OS.</summary>
    public string Disclaimer =>
        $"This software is an unofficial, community-made {AppInfo.Os} port of the WireView Pro II " +
        "application. It is not affiliated with, endorsed by, or supported by Thermal Grizzly or " +
        "ElmorLabs. All trademarks belong to their respective owners.";

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
        _showTrayPower = AppSettings.Current.ShowTrayPower;
        _showTrayPowerUnit = AppSettings.Current.ShowTrayPowerUnit;
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

        if (Set(ref _showTrayPower, AppSettings.Current.ShowTrayPower, nameof(ShowTrayPower)))
            OnPropertyChanged(nameof(ShowTrayPower));

        if (Set(ref _showTrayPowerUnit, AppSettings.Current.ShowTrayPowerUnit, nameof(ShowTrayPowerUnit)))
            OnPropertyChanged(nameof(ShowTrayPowerUnit));

        OnPropertyChanged(nameof(RemoteHostsText));
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
