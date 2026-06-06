using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
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
            DisableAvaloniaDataAnnotationValidation();
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            AppSettings.Reload();
            bool osAutoStart = AutoStartService.GetAutoStart();
            if (AppSettings.Current.AutoStart != osAutoStart)
            {
                AppSettings.Current.AutoStart = osAutoStart;
                AppSettings.SaveCurrent();
            }
            ApplyTheme(AppSettings.Current.ThemePreference);
            InitializeTray(desktop);
            AppSettings.Saved += OnSettingsSaved;
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
        _statusMenuItem = new NativeMenuItem { IsEnabled = false };
        _statusSeparator = new NativeMenuItemSeparator();

        var showItem = new NativeMenuItem("Show");
        showItem.Click += (_, _) => ShowMainWindow(desktop);
        _autoStartMenuItem = new NativeMenuItem("Auto-start")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
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
            ToggleType = NativeMenuItemToggleType.CheckBox,
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
        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) => desktop.Shutdown();
        menu.Items.Add(showItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_showPowerMenuItem);
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
        UpdateValuesMenu();
    }

    private void OnTrayConnectionChanged(object? sender, bool connected)
    {
        if (_trayConnected == connected) return;
        _trayConnected = connected;
        if (!connected) _lastData = null;
        Dispatcher.UIThread.Post(UpdateTrayVisuals, DispatcherPriority.Background);
    }

    private void OnTrayDataUpdated(object? sender, DeviceData data)
    {
        // Keep the latest reading; the values dropdown reads it on NeedsUpdate.
        _lastData = data;
        int watts = (int)Math.Round(data.SumPowerW);
        bool connected = data.Connected;
        // Only re-render the icon when the integer watts or connection changes.
        if (watts == _trayWatts && connected == _trayConnected) return;
        _trayWatts = watts;
        _trayConnected = connected;
        Dispatcher.UIThread.Post(UpdateTrayVisuals, DispatcherPriority.Background);
    }

    private void UpdateTrayVisuals()
    {
        bool showPower = AppSettings.Current.ShowTrayPower;
        string statusText = _trayConnected ? $"{_trayWatts} W" : "Disconnected";

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
                _powerTrayIcon.ToolTipText = $"WireView Pro II — {statusText}";
            }
        }
        else if (_powerTrayIcon != null)
        {
            _powerTrayIcon.Dispose();
            _powerTrayIcon = null;
        }

        // Menu header (on the bear icon) mirrors the power/connection status.
        if (_trayMenu != null && _statusMenuItem != null && _statusSeparator != null)
        {
            _statusMenuItem.Header = statusText;
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
            _vTotalPower.Header = "Disconnected";
            _vTotalCurrent.Header = "Total current: N/A";
            _vAvgVoltage.Header = "Average voltage: N/A";
            for (int i = 0; i < _vPins.Length; i++)
                _vPins[i].Header = $"Pin {i + 1}: N/A";
            _vTempIn.Header = "Temp onboard in: N/A";
            _vTempOut.Header = "Temp onboard out: N/A";
            _vTempExt1.Header = "Temp external 1: N/A";
            _vTempExt2.Header = "Temp external 2: N/A";
            _vCableRating.Header = "Cable rating: N/A";
            return;
        }

        var ci = CultureInfo.InvariantCulture;
        _vTotalPower.Header = $"Total power: {d.SumPowerW.ToString("0.0", ci)} W";
        _vTotalCurrent.Header = $"Total current: {d.SumCurrentA.ToString("0.00", ci)} A";
        _vAvgVoltage.Header = $"Average voltage: {d.PinVoltage.Average().ToString("0.00", ci)} V";
        for (int i = 0; i < _vPins.Length; i++)
        {
            double v = d.PinVoltage[i], a = d.PinCurrent[i];
            _vPins[i].Header =
                $"Pin {i + 1}: {v.ToString("0.00", ci)} V  {a.ToString("0.00", ci)} A  {(v * a).ToString("0.0", ci)} W";
        }
        _vTempIn.Header = $"Temp onboard in: {FormatTemp(d.OnboardTempInC)}";
        _vTempOut.Header = $"Temp onboard out: {FormatTemp(d.OnboardTempOutC)}";
        _vTempExt1.Header = $"Temp external 1: {FormatTemp(d.ExternalTemp1C)}";
        _vTempExt2.Header = $"Temp external 2: {FormatTemp(d.ExternalTemp2C)}";
        _vCableRating.Header = d.PsuCapabilityW > 0
            ? $"Cable rating: {d.PsuCapabilityW} W"
            : "Cable rating: N/A";
    }

    private static string FormatTemp(double t) =>
        t > -100.0 && t < 200.0 ? $"{t.ToString("0.#", CultureInfo.InvariantCulture)} °C" : "N/A";

    // Plain monochrome wattage number filling a square icon (no background colour);
    // white glyphs with a dark outline stay legible on both light and dark panels.
    private WindowIcon RenderWattsIcon(int watts)
    {
        const int size = 64;
        var rtb = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        try
        {
            using (var ctx = rtb.CreateDrawingContext())
            {
                // Single uniform label, e.g. "180W", sized to fill the square.
                string label = watts >= 1000 ? $"{watts / 1000}kW" : $"{watts}W";
                var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);
                double avail = size - 8;
                double em = avail;
                var ft = MakeText(label, typeface, em);
                if (ft.Width > avail) ft = MakeText(label, typeface, em * avail / ft.Width);
                if (ft.Height > avail) ft = MakeText(label, typeface, em * avail / ft.Height);
                double tx = (size - ft.Width) / 2.0, ty = (size - ft.Height) / 2.0;
                var geo = ft.BuildGeometry(new Point(tx, ty));
                if (geo != null)
                {
                    // Dark halo underneath, solid white core on top → legible on any panel.
                    var halo = new Pen(new SolidColorBrush(Color.Parse("#E6000000")), 6)
                    {
                        LineJoin = PenLineJoin.Round
                    };
                    ctx.DrawGeometry(null, halo, geo);
                    ctx.DrawGeometry(Brushes.White, null, geo);
                }
            }
            using var ms = new MemoryStream();
            rtb.Save(ms);
            ms.Position = 0;
            return new WindowIcon(ms);
        }
        finally
        {
            rtb.Dispose();
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
            rtb.Save(ms);
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

    private void DisableAvaloniaDataAnnotationValidation()
    {
        var plugins = BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();
        foreach (var p in plugins)
            BindingPlugins.DataValidators.Remove(p);
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
