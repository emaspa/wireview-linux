using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using WireView2.Device;
using WireView2.Services;
using WireView2.ViewModels;
using WireView2.Views;

namespace WireView2;

public class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private TrayIcon? _bearTrayIcon;
    private TrayIcon? _powerTrayIcon;
    private NativeMenuItem? _autoStartMenuItem;
    private NativeMenuItem? _showPowerMenuItem;
    private NativeMenuItem? _showUnitMenuItem;
    private NativeMenu? _trayMenu;
    private NativeMenuItem? _statusMenuItem;
    private NativeMenuItemSeparator? _statusSeparator;
    private Bitmap? _baseTrayBitmap;
    private readonly object _mainWindowGate = new object();
    private bool _isShowingMainWindow;

    // Live tray state, updated from the shared device connector
    private int _trayWatts;
    private bool _trayConnected;
    private DeviceData? _lastData;

    // Right-click "values" dropdown on the power icon (live hwmon readings)
    private NativeMenu? _valuesMenu;
    private NativeMenuItem? _vTotalPower;
    private NativeMenuItem? _vTotalCurrent;
    private NativeMenuItem? _vAvgVoltage;
    private NativeMenuItem? _vCableRating;
    private NativeMenuItem[]? _vPins;
    private NativeMenuItem? _vTempIn;
    private NativeMenuItem? _vTempOut;
    private NativeMenuItem? _vTempExt1;
    private NativeMenuItem? _vTempExt2;

    public static event EventHandler<bool>? MainWindowVisibilityChanged;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            AppSettings.Reload();
            WireView2.Net.FileLog.Init(
                System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(AppSettings.GetSettingsPath())!, "logs"),
                AppSettings.Current.LogRetentionDays);
            WireView2.Net.FileLog.Info($"WireView2 started on {AppInfo.Os} v{AppInfo.Version}");
            bool osAutoStart = AutoStartService.GetAutoStart();
            if (AppSettings.Current.AutoStart != osAutoStart)
            {
                AppSettings.Current.AutoStart = osAutoStart;
                AppSettings.SaveCurrent();
            }
            ApplyTheme(AppSettings.Current.ThemePreference);
            InitializeTray(desktop);
            AppSettings.Saved += OnSettingsSaved;
            WireViewPublishService.Shared.Start();
            WireViewDiscoveryService.Shared.Start();
            StartActivationListener(desktop);
            if (!AppSettings.Current.StartMinimized)
            {
                ShowMainWindow(desktop);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void OnSettingsSaved(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_autoStartMenuItem != null)
                _autoStartMenuItem.IsChecked = AppSettings.Current.AutoStart;
            if (_showPowerMenuItem != null)
                _showPowerMenuItem.IsChecked = AppSettings.Current.ShowTrayPower;
            if (_showUnitMenuItem != null)
                _showUnitMenuItem.IsChecked = AppSettings.Current.ShowTrayPowerUnit;
            UpdateTrayVisuals();
        });
    }

    private void InitializeTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _desktop = desktop;
        using (var stream = AssetLoader.Open(new Uri("avares://WireView2/Assets/Icons/bear.png")))
            _baseTrayBitmap = new Bitmap(stream);

        // Icon 1: the bear — app identity, click to show the window, holds the menu.
        _bearTrayIcon = new TrayIcon
        {
            Icon = new WindowIcon(_baseTrayBitmap),
            ToolTipText = "WireView Pro II",
            IsVisible = true
        };
        var menu = new NativeMenu();
        _trayMenu = menu;

        // Non-clickable status header (live power / connection), inserted at the
        // top of the menu by UpdateTrayVisuals when the power icon is enabled.
        // Its text is refreshed only when the menu is about to show; see
        // UpdateTrayVisuals for why per-tick updates are avoided.
        _statusMenuItem = new NativeMenuItem { IsEnabled = false, Header = "..." };
        _statusSeparator = new NativeMenuItemSeparator();
        // Hosts that deliver about-to-show get a fresh header; the GNOME
        // appindicator stack does not (verified 2026-07-10), so the throttled
        // refresh in OnTrayDataUpdated keeps it near-live as the fallback.
        menu.NeedsUpdate += (_, _) => UpdateStatusHeader();
        menu.Opening += (_, _) => UpdateStatusHeader();

        var showItem = new NativeMenuItem("Show");
        showItem.Click += (_, _) => ShowMainWindow(desktop);
        _autoStartMenuItem = new NativeMenuItem("Auto-start")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = AppSettings.Current.AutoStart
        };
        _autoStartMenuItem.Click += (_, _) =>
        {
            try
            {
                bool newState = !AppSettings.Current.AutoStart;
                AutoStartService.SetAutoStart(newState);
                AppSettings.Current.AutoStart = newState;
                AppSettings.SaveCurrent();
                _autoStartMenuItem.IsChecked = newState;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Auto-start toggle failed: {ex.Message}");
            }
        };
        _showPowerMenuItem = new NativeMenuItem("Show power tray icon")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = AppSettings.Current.ShowTrayPower
        };
        _showPowerMenuItem.Click += (_, _) =>
        {
            bool newState = !AppSettings.Current.ShowTrayPower;
            AppSettings.Current.ShowTrayPower = newState;
            AppSettings.SaveCurrent();
            _showPowerMenuItem.IsChecked = newState;
            UpdateTrayVisuals();
        };
        _showUnitMenuItem = new NativeMenuItem("Show unit name")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = AppSettings.Current.ShowTrayPowerUnit,
            IsEnabled = AppSettings.Current.ShowTrayPower
        };
        _showUnitMenuItem.Click += (_, _) =>
        {
            bool newState = !AppSettings.Current.ShowTrayPowerUnit;
            AppSettings.Current.ShowTrayPowerUnit = newState;
            AppSettings.SaveCurrent();
            _showUnitMenuItem.IsChecked = newState;
            UpdateTrayVisuals();
        };
        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) => desktop.Shutdown();
        menu.Items.Add(showItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_showPowerMenuItem);
        menu.Items.Add(_showUnitMenuItem);
        menu.Items.Add(_autoStartMenuItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);
        _bearTrayIcon.Menu = menu;
        _bearTrayIcon.Clicked += (_, _) => ShowMainWindow(desktop);

        // Right-click dropdown on the power icon listing every live hwmon value.
        NativeMenuItem ValueRow() => new NativeMenuItem { IsEnabled = false };
        _valuesMenu = new NativeMenu();
        _vTotalPower = ValueRow();
        _vTotalCurrent = ValueRow();
        _vAvgVoltage = ValueRow();
        _valuesMenu.Items.Add(_vTotalPower);
        _valuesMenu.Items.Add(_vTotalCurrent);
        _valuesMenu.Items.Add(_vAvgVoltage);
        _valuesMenu.Items.Add(new NativeMenuItemSeparator());
        _vPins = new NativeMenuItem[6];
        for (int i = 0; i < 6; i++)
        {
            _vPins[i] = ValueRow();
            _valuesMenu.Items.Add(_vPins[i]);
        }
        _valuesMenu.Items.Add(new NativeMenuItemSeparator());
        _vTempIn = ValueRow();
        _vTempOut = ValueRow();
        _vTempExt1 = ValueRow();
        _vTempExt2 = ValueRow();
        _valuesMenu.Items.Add(_vTempIn);
        _valuesMenu.Items.Add(_vTempOut);
        _valuesMenu.Items.Add(_vTempExt1);
        _valuesMenu.Items.Add(_vTempExt2);
        _valuesMenu.Items.Add(new NativeMenuItemSeparator());
        _vCableRating = ValueRow();
        _valuesMenu.Items.Add(_vCableRating);
        _valuesMenu.Items.Add(new NativeMenuItemSeparator());
        var valuesShow = new NativeMenuItem("Show window");
        valuesShow.Click += (_, _) => ShowMainWindow(desktop);
        _valuesMenu.Items.Add(valuesShow);
        // Populate the values only just before the menu is shown. Refreshing the
        // items on every poll instead makes an open menu flicker, so we don't.
        _valuesMenu.NeedsUpdate += (_, _) => UpdateValuesMenu();
        _valuesMenu.Opening += (_, _) => UpdateValuesMenu();

        // Drive the tray icon/tooltip from live device data, independent of
        // whether the main window has been created yet.
        var connector = DeviceAutoConnector.Shared;
        _trayConnected = connector.Device?.Connected ?? false;
        connector.ConnectionChanged += OnTrayConnectionChanged;
        connector.DataUpdated += OnTrayDataUpdated;
        // Begin polling immediately so the tray reflects live state even while the
        // main window has never been opened (Start is idempotent).
        connector.Start();
        UpdateTrayVisuals();
        UpdateStatusHeader();
        UpdateValuesMenu();
    }

    private void OnTrayConnectionChanged(object? sender, bool connected)
    {
        if (_trayConnected == connected) return;
        _trayConnected = connected;
        if (!connected) _lastData = null;
        Dispatcher.UIThread.Post(() =>
        {
            UpdateTrayVisuals();
            UpdateStatusHeader();
        }, DispatcherPriority.Background);
    }

    /// <summary>Writes the live power/connection text into the tray menu's status
    /// header. Called sparingly (menu about to show, connection changes, startup):
    /// mutating the item on every watt tick makes Avalonia 12's DBus menu exporter
    /// re-publish the layout, visibly flashing an open menu.</summary>
    private void UpdateStatusHeader()
    {
        if (_statusMenuItem != null)
            SetHeaderIfChanged(_statusMenuItem, _trayConnected ? $"{_trayWatts} W" : "Disconnected");
    }

    private void OnTrayDataUpdated(object? sender, DeviceData data)
    {
        _lastData = data;
        int watts = (int)Math.Round(data.SumPowerW);
        bool connected = data.Connected;

        // Menus refresh from data ticks, but throttled and change-only: our
        // GNOME appindicator host never signals menu-open (no NeedsUpdate or
        // Opening arrives), so this is the only way to keep an open menu's
        // values from going stale, while updating rarely enough that the
        // exporter's layout re-publish (a visible flash on an open menu)
        // stays occasional.
        if ((DateTime.UtcNow - _lastMenuRefreshUtc).TotalSeconds >= MenuRefreshSeconds)
        {
            _lastMenuRefreshUtc = DateTime.UtcNow;
            Dispatcher.UIThread.Post(() =>
            {
                UpdateStatusHeader();
                UpdateValuesMenu();
            }, DispatcherPriority.Background);
        }

        // Only re-render the icon when the integer watts or connection changes.
        if (watts == _trayWatts && connected == _trayConnected) return;
        _trayWatts = watts;
        _trayConnected = connected;
        Dispatcher.UIThread.Post(UpdateTrayVisuals, DispatcherPriority.Background);
    }

    private const int MenuRefreshSeconds = 3;
    private DateTime _lastMenuRefreshUtc;

    /// <summary>Sets a menu item's text only when it changed: every write makes the
    /// DBus menu exporter re-publish the layout, which flashes an open menu.</summary>
    private static void SetHeaderIfChanged(NativeMenuItem item, string text)
    {
        if (!string.Equals(item.Header as string, text, StringComparison.Ordinal))
            item.Header = text;
    }

    private void UpdateTrayVisuals()
    {
        bool showPower = AppSettings.Current.ShowTrayPower;
        string statusText = _trayConnected ? $"{_trayWatts} W" : "Disconnected";

        // The unit toggle only applies while the power icon is shown.
        if (_showUnitMenuItem != null)
            _showUnitMenuItem.IsEnabled = showPower;

        // Icon 2: create/dispose rather than toggling IsVisible. Avalonia's
        // FreeDesktop StatusNotifierItem backend throws when an icon is hidden and
        // later re-shown with a new bitmap, so we make a fresh icon each time.
        if (showPower)
        {
            if (_powerTrayIcon == null)
                CreatePowerTrayIcon();
            if (_powerTrayIcon != null)
            {
                try
                {
                    _powerTrayIcon.Icon = _trayConnected
                        ? RenderWattsIcon(_trayWatts)
                        : RenderDisconnectedIcon();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Tray power icon render failed: {ex.Message}");
                }
                _powerTrayIcon.ToolTipText = $"WireView Pro II - {statusText}";
            }
        }
        else if (_powerTrayIcon != null)
        {
            _powerTrayIcon.Dispose();
            _powerTrayIcon = null;
        }

        // Menu header (on the bear icon) mirrors the power/connection status.
        // The text itself is written in the menu's NeedsUpdate (just before it
        // shows): mutating an item on every watt tick makes Avalonia 12's DBus
        // menu exporter re-publish the layout, visibly flashing an open menu.
        if (_trayMenu != null && _statusMenuItem != null && _statusSeparator != null)
        {
            bool present = _trayMenu.Items.Contains(_statusMenuItem);
            if (showPower && !present)
            {
                _trayMenu.Items.Insert(0, _statusSeparator);
                _trayMenu.Items.Insert(0, _statusMenuItem);
            }
            else if (!showPower && present)
            {
                _trayMenu.Items.Remove(_statusMenuItem);
                _trayMenu.Items.Remove(_statusSeparator);
            }
        }
    }

    // Create a fresh power tray icon (with the values dropdown attached). Used on
    // first show and whenever the icon is re-enabled, to avoid Avalonia's
    // hide-then-reshow StatusNotifierItem crash.
    private void CreatePowerTrayIcon()
    {
        var desktop = _desktop;
        if (_powerTrayIcon != null || desktop == null) return;

        var icon = new TrayIcon { ToolTipText = "WireView Pro II", IsVisible = true };
        icon.Clicked += (_, _) => ShowMainWindow(desktop);
        icon.Menu = _valuesMenu;
        _powerTrayIcon = icon;
    }

    // Refresh the right-click "values" dropdown with the latest hwmon readings.
    private void UpdateValuesMenu()
    {
        if (_vTotalPower == null || _vTotalCurrent == null || _vAvgVoltage == null
            || _vPins == null || _vTempIn == null || _vTempOut == null
            || _vTempExt1 == null || _vTempExt2 == null || _vCableRating == null)
            return;

        var d = _lastData;
        if (!_trayConnected || d == null)
        {
            SetHeaderIfChanged(_vTotalPower, "Disconnected");
            SetHeaderIfChanged(_vTotalCurrent, "Total current: N/A");
            SetHeaderIfChanged(_vAvgVoltage, "Average voltage: N/A");
            for (int i = 0; i < _vPins.Length; i++)
                SetHeaderIfChanged(_vPins[i], $"Pin {i + 1}: N/A");
            SetHeaderIfChanged(_vTempIn, "Temp onboard in: N/A");
            SetHeaderIfChanged(_vTempOut, "Temp onboard out: N/A");
            SetHeaderIfChanged(_vTempExt1, "Temp external 1: N/A");
            SetHeaderIfChanged(_vTempExt2, "Temp external 2: N/A");
            SetHeaderIfChanged(_vCableRating, "Cable rating: N/A");
            return;
        }

        var ci = CultureInfo.InvariantCulture;
        SetHeaderIfChanged(_vTotalPower, $"Total power: {d.SumPowerW.ToString("0.0", ci)} W");
        SetHeaderIfChanged(_vTotalCurrent, $"Total current: {d.SumCurrentA.ToString("0.00", ci)} A");
        SetHeaderIfChanged(_vAvgVoltage, $"Average voltage: {d.PinVoltage.Average().ToString("0.00", ci)} V");
        for (int i = 0; i < _vPins.Length; i++)
        {
            double v = d.PinVoltage[i], a = d.PinCurrent[i];
            SetHeaderIfChanged(_vPins[i],
                $"Pin {i + 1}: {v.ToString("0.00", ci)} V  {a.ToString("0.00", ci)} A  {(v * a).ToString("0.0", ci)} W");
        }
        SetHeaderIfChanged(_vTempIn, $"Temp onboard in: {FormatTemp(d.OnboardTempInC)}");
        SetHeaderIfChanged(_vTempOut, $"Temp onboard out: {FormatTemp(d.OnboardTempOutC)}");
        SetHeaderIfChanged(_vTempExt1, $"Temp external 1: {FormatTemp(d.ExternalTemp1C)}");
        SetHeaderIfChanged(_vTempExt2, $"Temp external 2: {FormatTemp(d.ExternalTemp2C)}");
        SetHeaderIfChanged(_vCableRating, d.PsuCapabilityW > 0
            ? $"Cable rating: {d.PsuCapabilityW} W"
            : "Cable rating: N/A");
    }

    private static string FormatTemp(double t) =>
        t > -100.0 && t < 200.0 ? $"{t.ToString("0.#", CultureInfo.InvariantCulture)} °C" : "N/A";

    // Monochrome wattage on a square icon: the number large on top with the unit
    // word ("watt") smaller below, so the digits stay big for 2- and 3-digit
    // values. White glyphs with a dark halo stay legible on light and dark panels.
    private const string TrayPowerUnit = "watt";

    private WindowIcon RenderWattsIcon(int watts)
    {
        const int size = 64;
        var rtb = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        try
        {
            using (var ctx = rtb.CreateDrawingContext())
            {
                string num = watts >= 1000 ? $"{watts / 1000}k" : watts.ToString(CultureInfo.InvariantCulture);
                var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);
                var halo = new Pen(new SolidColorBrush(Color.Parse("#E6000000")), 5)
                {
                    LineJoin = PenLineJoin.Round
                };

                if (AppSettings.Current.ShowTrayPowerUnit)
                {
                    // Number row (~56% of the height) over the unit word (~30%).
                    var numGeo = SizeGlyphs(num, typeface, size - 6, size * 0.56, out var numBounds);
                    var unitGeo = SizeGlyphs(TrayPowerUnit, typeface, size - 3, size * 0.30, out var unitBounds);

                    double gap = size * 0.03;
                    double totalH = numBounds.Height + gap + unitBounds.Height;
                    double top = (size - totalH) / 2.0;
                    DrawGlyphs(ctx, numGeo, numBounds, (size - numBounds.Width) / 2.0, top, halo);
                    DrawGlyphs(ctx, unitGeo, unitBounds, (size - unitBounds.Width) / 2.0,
                        top + numBounds.Height + gap, halo);
                }
                else
                {
                    // Number only, filling the square.
                    var numGeo = SizeGlyphs(num, typeface, size - 6, size - 8, out var numBounds);
                    DrawGlyphs(ctx, numGeo, numBounds, (size - numBounds.Width) / 2.0,
                        (size - numBounds.Height) / 2.0, halo);
                }
            }
            using var ms = new MemoryStream();
            // Save(Stream, int?) is obsolete in Avalonia 12, but its replacement
            // (BitmapEncoderOptions) only exists in later 12.x releases; we track
            // 12.0.2 to stay aligned with the upstream Windows client.
#pragma warning disable CS0618
            rtb.Save(ms);
#pragma warning restore CS0618
            ms.Position = 0;
            return new WindowIcon(ms);
        }
        finally
        {
            rtb.Dispose();
        }
    }

    // Build glyph geometry for text scaled to fit maxW × maxH, returning its tight
    // ink bounds for precise placement.
    private static Geometry? SizeGlyphs(string text, Typeface tf, double maxW, double maxH, out Rect bounds)
    {
        double em = maxH * 1.4;
        var geo = MakeText(text, tf, em).BuildGeometry(new Point(0, 0));
        if (geo == null) { bounds = default; return null; }
        var b = geo.Bounds;
        double scale = Math.Min(maxW / b.Width, maxH / b.Height);
        geo = MakeText(text, tf, em * scale).BuildGeometry(new Point(0, 0));
        bounds = geo?.Bounds ?? default;
        return geo;
    }

    // Draw glyph geometry so its ink top-left lands at (tx, ty): dark halo under,
    // solid white core on top.
    private static void DrawGlyphs(DrawingContext ctx, Geometry? geo, Rect bounds, double tx, double ty, Pen halo)
    {
        if (geo == null) return;
        using (ctx.PushTransform(Matrix.CreateTranslation(tx - bounds.X, ty - bounds.Y)))
        {
            ctx.DrawGeometry(null, halo, geo);
            ctx.DrawGeometry(Brushes.White, null, geo);
        }
    }

    // Red disconnected indicator shown in place of the wattage.
    private WindowIcon RenderDisconnectedIcon()
    {
        const int size = 64;
        var rtb = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        try
        {
            using (var ctx = rtb.CreateDrawingContext())
            {
                ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#FFD32F2F")),
                    new Pen(Brushes.White, 3),
                    new Point(size / 2.0, size / 2.0), size / 2.0 - 6, size / 2.0 - 6);
            }
            using var ms = new MemoryStream();
            // Save(Stream, int?) is obsolete in Avalonia 12, but its replacement
            // (BitmapEncoderOptions) only exists in later 12.x releases; we track
            // 12.0.2 to stay aligned with the upstream Windows client.
#pragma warning disable CS0618
            rtb.Save(ms);
#pragma warning restore CS0618
            ms.Position = 0;
            return new WindowIcon(ms);
        }
        finally
        {
            rtb.Dispose();
        }
    }

    private static FormattedText MakeText(string text, Typeface typeface, double emSize) =>
        new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, emSize, Brushes.White);

    private void ShowMainWindow(IClassicDesktopStyleApplicationLifetime desktop, bool startMinimized = false)
    {
        lock (_mainWindowGate)
        {
            if (_isShowingMainWindow) return;
            _isShowingMainWindow = true;
        }
        try
        {
            var window = desktop.MainWindow as MainWindow;
            if (window == null || !window.IsVisible)
            {
                window = new MainWindow { DataContext = new MainWindowViewModel() };
                window.PropertyChanged += (_, args) =>
                {
                    if (args.Property == Window.WindowStateProperty || args.Property == Visual.IsVisibleProperty)
                    {
                        bool visible = window.IsVisible && window.WindowState != WindowState.Minimized;
                        MainWindowVisibilityChanged?.Invoke(this, visible);
                    }
                };
                window.Closing += (_, args) =>
                {
                    if (args.CloseReason == WindowCloseReason.WindowClosing)
                    {
                        args.Cancel = true;
                        window.Hide();
                        MainWindowVisibilityChanged?.Invoke(this, false);
                    }
                };
                if (startMinimized)
                    window.WindowState = WindowState.Minimized;
                window.Closed += (_, _) =>
                {
                    if (desktop.MainWindow == window)
                        desktop.MainWindow = null;
                };
                desktop.MainWindow = window;
                window.Show();
                MainWindowVisibilityChanged?.Invoke(this, !startMinimized);
                if (!startMinimized) window.Activate();
            }
            else
            {
                if (window.WindowState == WindowState.Minimized)
                    window.WindowState = WindowState.Normal;
                window.Activate();
                MainWindowVisibilityChanged?.Invoke(this, true);
            }
        }
        finally
        {
            lock (_mainWindowGate)
            {
                _isShowingMainWindow = false;
            }
        }
    }

    private static void ApplyTheme(AppSettings.ThemeMode mode)
    {
        var current = Application.Current;
        if (current == null) return;
        current.RequestedThemeVariant = mode switch
        {
            AppSettings.ThemeMode.Light => ThemeVariant.Light,
            AppSettings.ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    private static void StartActivationListener(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var activationEvent = SingleInstanceService.CreateOrOpenActivationEvent();
        Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    activationEvent.WaitOne();
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (desktop.MainWindow is MainWindow { IsVisible: true, WindowState: not WindowState.Minimized } mw)
                            mw.Activate();
                        else if (Application.Current is App app)
                            app.ShowMainWindow(desktop);
                    });
                }
                catch { break; }
            }
        });
    }
}
