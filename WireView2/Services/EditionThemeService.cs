using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using WireView2.Device;

namespace WireView2.Services;

/// <summary>Per-edition colour palettes (upstream 1.0.8). The Noctua Edition
/// (product id 6, hardware revision "EF06") gets Noctua brown/beige accents, its
/// own background photos and chart colours, either because the connected (or, in
/// multi-device mode, the selected) device is a Noctua Edition while the theme is
/// Auto, or because the user picked Noctua Light / Noctua Dark.
///
/// Port difference: upstream also writes a copy of its default palette into the
/// application resources. Here the default (Thermal Grizzly) edition removes the
/// overrides instead, so the theme dictionaries and chart defaults in App.axaml
/// (the port's own accent, nav colours, CPU_WELLE backgrounds and per-series
/// Overview colours) apply unchanged. Top-level <c>Application.Resources</c> keys
/// take precedence over both the theme dictionaries and the merged chart defaults.</summary>
internal static class EditionThemeService
{
    public enum WireViewDeviceTheme
    {
        WireViewPro2,
        WireViewPro2Noctua,
    }

    private readonly record struct ChartPalette(Color BarLow, Color BarHigh, Color GaugeTrack, Color GaugeAccent);

    /// <summary>A null <see cref="Overrides"/> means "use App.axaml as is".</summary>
    private readonly record struct ThemePalette(
        double BackgroundOpacity, Color BackgroundColor, ResourceOverrides? Overrides);

    private sealed record ResourceOverrides(
        Color Accent, Color NavPaneBackground, Color NavPaneBorder, Color SideNavHover,
        Color SideNavPressed, Color OverlayPanel, string BackgroundUri, ChartPalette Charts,
        Color? CheckGlyph = null);

    private readonly record struct EditionPaletteSet(ThemePalette Light, ThemePalette Dark);

    private const string AppBackgroundBrushKey = "AppBackgroundBrush";

    private static readonly string[] AccentColorKeys =
    {
        "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2",
        "SystemAccentColorLight3", "SystemAccentColorDark1", "SystemAccentColorDark2",
        "SystemAccentColorDark3",
    };

    private static readonly string[] BrushKeys =
    {
        "NavPaneBackgroundBrush", "NavPaneBorderBrush", "SideNavHoverBrush",
        "SideNavPressedBrush", "OverlayPanelBrush",
    };

    // Fluent draws the check mark of a checked CheckBox in white on the accent
    // colour. Noctua Dark's accent is near-white (#E9E9EE, upstream's value), which
    // leaves the mark invisible; that palette sets a dark glyph instead.
    private static readonly string[] CheckGlyphKeys =
    {
        "CheckBoxCheckGlyphForegroundChecked", "CheckBoxCheckGlyphForegroundCheckedPointerOver",
        "CheckBoxCheckGlyphForegroundCheckedPressed", "CheckBoxCheckGlyphForegroundIndeterminate",
        "CheckBoxCheckGlyphForegroundIndeterminatePointerOver", "CheckBoxCheckGlyphForegroundIndeterminatePressed",
    };

    private const string OverviewBarLowColorKey = "OverviewBarLowColor";
    private const string OverviewBarHighColorKey = "OverviewBarHighColor";
    private const string OverviewGaugeTrackBrushKey = "OverviewGaugeTrackBrush";

    private static readonly string[] OverviewGaugeAccentBrushKeys =
    {
        "OverviewGaugeTotalCurrentBrush", "OverviewGaugeTotalPowerBrush",
        "OverviewGaugeAvgVoltageBrush", "OverviewGaugeTempInBrush",
        "OverviewGaugeTempOutBrush", "OverviewGaugeExternal1Brush",
        "OverviewGaugeExternal2Brush",
    };

    // Opacity and window colour match what the port applied before editions existed.
    private static readonly ThemePalette DefaultLight = new(1.0, Colors.White, null);
    private static readonly ThemePalette DefaultDark = new(0.5, Colors.Black, null);

    // Upstream 1.0.8 values.
    private static readonly ThemePalette NoctuaLight = new(0.75, Colors.White, new ResourceOverrides(
        Color.Parse("#FF653025"), Color.Parse("#FF653025"), Color.Parse("#33653025"),
        Color.Parse("#22653025"), Color.Parse("#33653025"), Color.Parse("#F2E9E9EE"),
        "avares://WireView2/Assets/Backgrounds/thermal_grizzly_wireview_pro_II_noctua_edition_software_background_light_v2.jpg",
        new ChartPalette(Color.Parse("#FF653025"), Color.Parse("#FFE9E9EE"),
            Color.Parse("#FFE9E9EE"), Color.Parse("#FF653025"))));

    private static readonly ThemePalette NoctuaDark = new(0.75, Colors.Black, new ResourceOverrides(
        Color.Parse("#FFE9E9EE"), Color.Parse("#FF1E0D09"), Color.Parse("#33E9E9EE"),
        Color.Parse("#22E9E9EE"), Color.Parse("#33E9E9EE"), Color.Parse("#22000000"),
        "avares://WireView2/Assets/Backgrounds/thermal_grizzly_wireview_pro_II_noctua_edition_software_background_dark_v2.jpg",
        new ChartPalette(Color.Parse("#FFE9E9EE"), Color.Parse("#FF1E0D09"),
            Color.Parse("#FFE9E9EE"), Color.Parse("#FF1E0D09")),
        CheckGlyph: Color.Parse("#FF1E0D09")));

    private static WireViewDeviceTheme _activeEdition = WireViewDeviceTheme.WireViewPro2;
    private static bool _followsThemeVariant;
    private static ThemePalette _activePalette = DefaultDark;
    private static bool _paletteApplied;
    private static ResourceOverrides? _appliedOverrides;

    // Only the background in use is kept decoded (a 3840 px photo is ~30 MB of
    // RGBA); switching drops the reference and the GC frees it.
    private static string? _backgroundUri;
    private static Bitmap? _background;

    public static WireViewDeviceTheme ActiveEdition => _activeEdition;

    /// <summary>Background opacity of the palette in effect (applied when the user
    /// changes the theme, like upstream).</summary>
    public static double ActiveBackgroundOpacity => _activePalette.BackgroundOpacity;

    /// <summary>Edition of a connected device. Uses the hardware revision
    /// ("EF06" = vendor 0xEF, product 6) that serial, LAN (hwRev) and a wireviewd
    /// reporting vendor/product all expose; the device name is the fallback for
    /// sources that report no revision.</summary>
    public static WireViewDeviceTheme EditionOf(IWireViewDevice? device)
    {
        if (device is not { Connected: true })
            return WireViewDeviceTheme.WireViewPro2;
        return device.Edition == WireViewEdition.Pro2Noctua
            ? WireViewDeviceTheme.WireViewPro2Noctua
            : WireViewDeviceTheme.WireViewPro2;
    }

    public static void SetEdition(WireViewDeviceTheme edition)
    {
        // Called on every sample from the poll thread: bail out cheaply first.
        if (_activeEdition == edition && _paletteApplied) return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetEdition(edition), DispatcherPriority.Background);
            return;
        }
        _activeEdition = edition;
        ApplyCurrentThemePalette();
    }

    public static void ClearEdition() => SetEdition(WireViewDeviceTheme.WireViewPro2);

    public static ThemeVariant GetThemeVariant(AppSettings.ThemeMode mode) => mode switch
    {
        AppSettings.ThemeMode.Light or AppSettings.ThemeMode.NoctuaLight => ThemeVariant.Light,
        AppSettings.ThemeMode.Dark or AppSettings.ThemeMode.NoctuaDark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    /// <summary>User-facing name of a theme mode ("Noctua Light", not "NoctuaLight").</summary>
    public static string DisplayName(AppSettings.ThemeMode mode) => mode switch
    {
        AppSettings.ThemeMode.NoctuaLight => "Noctua Light",
        AppSettings.ThemeMode.NoctuaDark => "Noctua Dark",
        _ => mode.ToString(),
    };

    private static EditionPaletteSet GetPaletteSet(AppSettings.ThemeMode mode)
    {
        WireViewDeviceTheme edition = mode switch
        {
            AppSettings.ThemeMode.Auto => _activeEdition,
            AppSettings.ThemeMode.NoctuaLight or AppSettings.ThemeMode.NoctuaDark => WireViewDeviceTheme.WireViewPro2Noctua,
            _ => WireViewDeviceTheme.WireViewPro2,
        };
        return edition == WireViewDeviceTheme.WireViewPro2Noctua
            ? new EditionPaletteSet(NoctuaLight, NoctuaDark)
            : new EditionPaletteSet(DefaultLight, DefaultDark);
    }

    /// <summary>Applies the palette for the current theme setting, edition and
    /// actual light/dark variant. Safe to call from any thread.</summary>
    public static void ApplyCurrentThemePalette()
    {
        var app = Application.Current;
        if (app == null) return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(ApplyCurrentThemePalette, DispatcherPriority.Background);
            return;
        }
        if (!_followsThemeVariant)
        {
            // Auto follows the OS light/dark preference.
            app.ActualThemeVariantChanged += (_, _) => ApplyCurrentThemePalette();
            _followsThemeVariant = true;
        }

        bool light = app.ActualThemeVariant == ThemeVariant.Light;
        var set = GetPaletteSet(AppSettings.Current.ThemePreference);
        _activePalette = light ? set.Light : set.Dark;
        ApplyPalette(app, _activePalette);
        _paletteApplied = true;
    }

    private static void ApplyPalette(Application app, ThemePalette palette)
    {
        var o = palette.Overrides;
        if (!ReferenceEquals(o, _appliedOverrides))
        {
            if (o == null)
            {
                foreach (string key in AccentColorKeys) app.Resources.Remove(key);
                foreach (string key in BrushKeys) app.Resources.Remove(key);
                app.Resources.Remove(AppBackgroundBrushKey);
                app.Resources.Remove(OverviewBarLowColorKey);
                app.Resources.Remove(OverviewBarHighColorKey);
                app.Resources.Remove(OverviewGaugeTrackBrushKey);
                foreach (string key in OverviewGaugeAccentBrushKeys) app.Resources.Remove(key);
                foreach (string key in CheckGlyphKeys) app.Resources.Remove(key);
            }
            else
            {
                foreach (string key in AccentColorKeys) app.Resources[key] = o.Accent;
                app.Resources["NavPaneBackgroundBrush"] = new SolidColorBrush(o.NavPaneBackground);
                app.Resources["NavPaneBorderBrush"] = new SolidColorBrush(o.NavPaneBorder);
                app.Resources["SideNavHoverBrush"] = new SolidColorBrush(o.SideNavHover);
                app.Resources["SideNavPressedBrush"] = new SolidColorBrush(o.SideNavPressed);
                app.Resources["OverlayPanelBrush"] = new SolidColorBrush(o.OverlayPanel);
                app.Resources[AppBackgroundBrushKey] = new ImageBrush(LoadBackground(o.BackgroundUri))
                {
                    Stretch = Stretch.UniformToFill,
                    AlignmentX = AlignmentX.Center,
                    AlignmentY = AlignmentY.Center,
                };
                app.Resources[OverviewBarLowColorKey] = o.Charts.BarLow;
                app.Resources[OverviewBarHighColorKey] = o.Charts.BarHigh;
                app.Resources[OverviewGaugeTrackBrushKey] = new SolidColorBrush(o.Charts.GaugeTrack);
                var accent = new SolidColorBrush(o.Charts.GaugeAccent);
                foreach (string key in OverviewGaugeAccentBrushKeys) app.Resources[key] = accent;
                if (o.CheckGlyph is { } glyph)
                {
                    var glyphBrush = new SolidColorBrush(glyph);
                    foreach (string key in CheckGlyphKeys) app.Resources[key] = glyphBrush;
                }
                else
                {
                    foreach (string key in CheckGlyphKeys) app.Resources.Remove(key);
                }
            }
            _appliedOverrides = o;
        }
        ApplyBackgroundOpacity(AppSettings.Current.BackgroundOpacity);
        ApplyBackgroundColor(AppSettings.Current.BackgroundColorPreference);
    }

    private static Bitmap LoadBackground(string uri)
    {
        if (_background == null || _backgroundUri != uri)
        {
            using var stream = AssetLoader.Open(new Uri(uri));
            _background = new Bitmap(stream);
            _backgroundUri = uri;
        }
        return _background;
    }

    /// <summary>Sets the opacity of the background brush in effect: the edition
    /// override when there is one, else the theme dictionary's CPU_WELLE brush.</summary>
    public static void ApplyBackgroundOpacity(double opacity)
    {
        var app = Application.Current;
        if (!double.IsFinite(opacity)) opacity = 0.5;
        opacity = Math.Clamp(opacity, 0.0, 1.0);
        if (app != null
            && app.TryFindResource(AppBackgroundBrushKey, app.ActualThemeVariant, out object? resource)
            && resource is ImageBrush imageBrush)
        {
            imageBrush.Opacity = opacity;
        }
    }

    /// <summary>Main window colour behind the background image. Auto uses the
    /// active palette's colour (white for light, black for dark).</summary>
    public static void ApplyBackgroundColor(AppSettings.BackgroundColorMode mode)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;
        var mainWindow = desktop.MainWindow;
        if (mainWindow == null) return;
        mainWindow.Background = mode switch
        {
            AppSettings.BackgroundColorMode.Black => Brushes.Black,
            AppSettings.BackgroundColorMode.White => Brushes.White,
            _ => new SolidColorBrush(_activePalette.BackgroundColor),
        };
    }
}
