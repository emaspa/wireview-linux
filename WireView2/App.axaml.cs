using System;
using System.Collections.Generic;
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
    private NativeMenuItem? _showAllPowerMenuItem;
    private NativeMenu? _trayMenu;
    private NativeMenuItem? _statusMenuItem;
    // One row per WireView on this PC when there are several; clicking selects it.
    private NativeMenuItem? _trayShowItem;
    private readonly List<(string Id, NativeMenuItem Item)> _deviceMenuItems = new();
    private NativeMenuItemSeparator? _deviceMenuSeparator;
    private string _deviceMenuKey = "";
    private NativeMenuItemSeparator? _statusSeparator;
    private Bitmap? _baseTrayBitmap;
    private readonly object _mainWindowGate = new object();
    private bool _isShowingMainWindow;

    // Live tray state, updated from the shared device connector
    private int _trayWatts;
    private bool _trayConnected;
    private DeviceData? _lastData;

    // Right-click "values" dropdown on the power icon (the selected device's readings)
    private TrayValuesMenu? _selectedValues;

    // "Power icon for each device": one power icon per WireView on this PC, keyed
    // by device id, used instead of the single icon while there are two or more.
    private readonly Dictionary<string, DevicePowerIcon> _devicePowerIcons = new();

    private sealed class DevicePowerIcon
    {
        public DevicePowerIcon(TrayIcon icon, TrayValuesMenu values)
        {
            Icon = icon;
            Values = values;
        }
        public TrayIcon Icon { get; }
        public TrayValuesMenu Values { get; }
        public DeviceData? Last { get; set; }
        public string Label { get; set; } = "";
        // What the icon was last drawn with, to redraw only on a change.
        public string Drawn { get; set; } = "";
    }

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
            _startPage = StartPageFromArgs(desktop.Args);
            AppSettings.Reload();
            // The device picked last comes back first; --device overrides it for this run.
            DeviceManager.Shared.PreferredUid = AppSettings.Current.SelectedDeviceUid;
            _startDevice = OptionFromArgs(desktop.Args, "--device");
            if (_startDevice != null)
                DeviceManager.Shared.DevicesChanged += OnStartDeviceCandidates;
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
            // Before the tray starts the device manager: the probe for
            // wireview-mini must be in place before the first serial probe.
            WireViewDiscoveryService.Shared.Start();
            Dispatcher.UIThread.UnhandledException += OnUiThreadUnhandledException;
            InitializeTray(desktop);
            AppSettings.Saved += OnSettingsSaved;
            WireViewPublishService.Shared.Start();
            StartActivationListener(desktop);
            if (!AppSettings.Current.StartMinimized)
            {
                ShowMainWindow(desktop);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    // Avalonia registers each tray icon on D-Bus from an async void method, so a
    // refused registration (a sandbox that may not own the StatusNotifierItem
    // name, #6) would otherwise take the whole app down. Run without that tray
    // icon instead, and show the window once, since with no tray icon it is the
    // only way into the app.
    private bool _trayFailed;

    private void OnUiThreadUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (e.Exception.StackTrace?.Contains("Avalonia.FreeDesktop.DBusTrayIconImpl", StringComparison.Ordinal) != true)
            return;
        e.Handled = true;
        string message = $"Tray icon registration failed, running without it: {e.Exception.GetType().Name}: {e.Exception.Message}";
        Console.Error.WriteLine(message);
        WireView2.Net.FileLog.Warn(message);
        if (_trayFailed || _desktop == null) return;
        _trayFailed = true;
        ShowMainWindow(_desktop);
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
            if (_showAllPowerMenuItem != null)
                _showAllPowerMenuItem.IsChecked = AppSettings.Current.ShowTrayPowerAllDevices;
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
        _trayShowItem = showItem;
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
        _showAllPowerMenuItem = new NativeMenuItem("Power icon for each device")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = AppSettings.Current.ShowTrayPowerAllDevices,
            IsEnabled = AppSettings.Current.ShowTrayPower
        };
        _showAllPowerMenuItem.Click += (_, _) =>
        {
            bool newState = !AppSettings.Current.ShowTrayPowerAllDevices;
            AppSettings.Current.ShowTrayPowerAllDevices = newState;
            AppSettings.SaveCurrent();
            _showAllPowerMenuItem.IsChecked = newState;
            UpdateTrayVisuals();
        };
        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) => desktop.Shutdown();
        menu.Items.Add(showItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_showPowerMenuItem);
        menu.Items.Add(_showUnitMenuItem);
        menu.Items.Add(_showAllPowerMenuItem);
        menu.Items.Add(_autoStartMenuItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);
        _bearTrayIcon.Menu = menu;
        _bearTrayIcon.Clicked += (_, _) => ShowMainWindow(desktop);

        // Right-click dropdown on the power icon listing every live hwmon value.
        // Populated only just before the menu is shown: refreshing the items on
        // every poll instead makes an open menu flicker.
        _selectedValues = new TrayValuesMenu(() => ShowMainWindow(desktop), UpdateValuesMenu);

        // Drive the tray icon/tooltip from live device data, independent of
        // whether the main window has been created yet.
        var connector = DeviceAutoConnector.Shared;
        _trayConnected = connector.Device?.Connected ?? false;
        connector.ConnectionChanged += OnTrayConnectionChanged;
        connector.DataUpdated += OnTrayDataUpdated;
        // Begin polling immediately so the tray reflects live state even while the
        // main window has never been opened (Start is idempotent).
        connector.Start();
        DeviceManager.Shared.DevicesChanged += OnTrayDevicesChanged;
        DeviceManager.Shared.SelectedChanged += OnTrayDevicesChanged;
        DeviceManager.Shared.DataUpdated += OnAnyTrayData;
        AppSettings.Saved += OnTrayDevicesChanged;   // a nickname changed
        UpdateTrayVisuals();
        UpdateStatusHeader();
        UpdateValuesMenu();
        UpdateDeviceMenu();
    }

    private void OnTrayDevicesChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            UpdateDeviceMenu();
            // Two devices or more switch "Power icon for each device" on, fewer off.
            if (AppSettings.Current.ShowTrayPower && AppSettings.Current.ShowTrayPowerAllDevices)
                UpdateTrayVisuals();
        }, DispatcherPriority.Background);

    /// <summary>The WireViews on this PC, in a stable order (the tray menu rows and
    /// the per-device power icons).</summary>
    private static List<ManagedDevice> LocalTrayDevices() =>
        DeviceManager.Shared.Devices.Where(d => !d.IsRemote).OrderBy(d => d.Id, StringComparer.Ordinal).ToList();

    private static string TrayLabel(ManagedDevice md) =>
        DeviceLabels.Label(md.Device, AppSettings.Current.NicknameFor(md.Device.UniqueId));

    /// <summary>Every device's readings, for its own power icon when there is one.</summary>
    private void OnAnyTrayData(object? sender, (string Id, DeviceData Data) e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_devicePowerIcons.TryGetValue(e.Id, out var entry)) return;
            entry.Last = e.Data;
            entry.Values.TempPresence.Update(e.Data);
            DrawDeviceIcon(entry);
        }, DispatcherPriority.Background);
    }

    /// <summary>Creates, keeps or removes the per-device power icons: one per
    /// WireView on this PC while <paramref name="on"/>, none otherwise.</summary>
    private void SyncDevicePowerIcons(bool on)
    {
        var local = on ? LocalTrayDevices() : new List<ManagedDevice>();
        foreach (var id in _devicePowerIcons.Keys.Where(id => local.All(d => d.Id != id)).ToList())
        {
            _devicePowerIcons[id].Icon.Dispose();
            _devicePowerIcons.Remove(id);
        }
        foreach (var md in local)
        {
            if (!_devicePowerIcons.TryGetValue(md.Id, out var entry))
            {
                string id = md.Id;
                var values = new TrayValuesMenu(() => ShowTrayDevice(id), () => UpdateDeviceValuesMenu(id), withTitle: true);
                // A fresh icon each time, never hidden and re-shown (see UpdateTrayVisuals).
                var icon = new TrayIcon { ToolTipText = "WireView Pro II", IsVisible = true, Menu = values.Menu };
                icon.Clicked += (_, _) => ShowTrayDevice(id);
                entry = new DevicePowerIcon(icon, values);
                _devicePowerIcons[id] = entry;
            }
            entry.Last = md.Latest ?? entry.Last;
            entry.Label = TrayLabel(md);
            entry.Values.SetTitle(entry.Label);
            DrawDeviceIcon(entry);
        }
    }

    private void DrawDeviceIcon(DevicePowerIcon entry)
    {
        bool connected = entry.Last is { Connected: true };
        int watts = connected ? (int)Math.Round(entry.Last!.SumPowerW) : 0;
        string drawn = $"{connected}|{watts}|{AppSettings.Current.ShowTrayPowerUnit}";
        if (drawn != entry.Drawn)
        {
            entry.Drawn = drawn;
            try
            {
                entry.Icon.Icon = connected ? RenderWattsIcon(watts) : RenderDisconnectedIcon();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Tray power icon render failed: {ex.Message}");
            }
        }
        string tip = connected ? $"{entry.Label}: {watts} W" : $"{entry.Label}: disconnected";
        if (entry.Icon.ToolTipText != tip) entry.Icon.ToolTipText = tip;
    }

    private void UpdateDeviceValuesMenu(string id)
    {
        if (_devicePowerIcons.TryGetValue(id, out var entry))
            entry.Values.Update(entry.Last, entry.Last is { Connected: true });
    }

    /// <summary>A per-device power icon was clicked: show that device.</summary>
    private void ShowTrayDevice(string id)
    {
        DeviceManager.Shared.SelectedId = id;
        if (_desktop != null) ShowMainWindow(_desktop);
    }

    /// <summary>The tray menu's device rows: with two or more WireViews on this PC,
    /// one row each ("name · 4E46: 312 W"), the selected one checked; clicking a
    /// row shows that device. The rows are rebuilt only when the devices, the
    /// selection or a nickname change, and otherwise only their text is updated
    /// when it changed, for the same flicker reason as the status header.</summary>
    private void UpdateDeviceMenu()
    {
        if (_trayMenu == null || _trayShowItem == null) return;
        var mgr = DeviceManager.Shared;
        var local = LocalTrayDevices();
        if (local.Count < 2) local.Clear();
        string selected = mgr.SelectedId ?? "";
        string key = selected + "|" + string.Join(",", local.Select(d => d.Id + "=" + RowLabel(d)));
        if (key != _deviceMenuKey)
        {
            _deviceMenuKey = key;
            foreach (var (_, item) in _deviceMenuItems) _trayMenu.Items.Remove(item);
            _deviceMenuItems.Clear();
            if (_deviceMenuSeparator != null) _trayMenu.Items.Remove(_deviceMenuSeparator);
            _deviceMenuSeparator = null;
            if (local.Count > 0)
            {
                int at = _trayMenu.Items.IndexOf(_trayShowItem) + 1;
                _deviceMenuSeparator = new NativeMenuItemSeparator();
                _trayMenu.Items.Insert(at++, _deviceMenuSeparator);
                foreach (var md in local)
                {
                    string id = md.Id;
                    var item = new NativeMenuItem(RowText(md))
                    {
                        ToggleType = MenuItemToggleType.Radio,
                        IsChecked = id == selected,
                    };
                    item.Click += (_, _) => mgr.SelectedId = id;
                    _trayMenu.Items.Insert(at++, item);
                    _deviceMenuItems.Add((id, item));
                }
            }
            return;
        }
        foreach (var (id, item) in _deviceMenuItems)
            if (local.FirstOrDefault(d => d.Id == id) is { } md)
                SetHeaderIfChanged(item, RowText(md));

        static string RowLabel(ManagedDevice md) => TrayLabel(md);
        static string RowText(ManagedDevice md) =>
            md.Latest is { Connected: true } d ? $"{RowLabel(md)}: {(int)Math.Round(d.SumPowerW)} W" : RowLabel(md);
    }

    private void OnTrayConnectionChanged(object? sender, bool connected)
    {
        _selectedValues?.TempPresence.Reset();
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
        _selectedValues?.TempPresence.Update(data);
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
                UpdateDeviceMenu();
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
    private static void SetHeaderIfChanged(NativeMenuItem item, string text) =>
        TrayValuesMenu.SetHeaderIfChanged(item, text);

    private void UpdateTrayVisuals()
    {
        bool showPower = AppSettings.Current.ShowTrayPower;
        string statusText = _trayConnected ? $"{_trayWatts} W" : "Disconnected";
        // One power icon per device instead of the single one, with two or more.
        bool perDevice = showPower && AppSettings.Current.ShowTrayPowerAllDevices
            && LocalTrayDevices().Count >= 2;

        // The unit and per-device toggles only apply while the power icon is shown.
        if (_showUnitMenuItem != null)
            _showUnitMenuItem.IsEnabled = showPower;
        if (_showAllPowerMenuItem != null)
            _showAllPowerMenuItem.IsEnabled = showPower;
        SyncDevicePowerIcons(perDevice);

        // Icon 2: create/dispose rather than toggling IsVisible. Avalonia's
        // FreeDesktop StatusNotifierItem backend throws when an icon is hidden and
        // later re-shown with a new bitmap, so we make a fresh icon each time.
        if (showPower && !perDevice)
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
        icon.Menu = _selectedValues?.Menu;
        _powerTrayIcon = icon;
    }

    // Refresh the right-click "values" dropdowns with the latest hwmon readings:
    // the single power icon's, and each per-device icon's.
    private void UpdateValuesMenu()
    {
        _selectedValues?.Update(_lastData, _trayConnected);
        foreach (var id in _devicePowerIcons.Keys)
            UpdateDeviceValuesMenu(id);
    }

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

    /// <summary>Page the first window opens on, from "--page name" or
    /// "--page=name" on the command line (overview, monitoring, logging, device,
    /// settings). Handy for launchers and for measuring a page; consumed once.</summary>
    private string? _startPage;

    private static string? StartPageFromArgs(string[]? args) => OptionFromArgs(args, "--page");

    private static string? OptionFromArgs(string[]? args, string name)
    {
        if (args == null) return null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == name && i + 1 < args.Length) return args[i + 1];
            if (args[i].StartsWith(name + "=", StringComparison.Ordinal)) return args[i][(name.Length + 1)..];
        }
        return null;
    }

    /// <summary>The device to show first, from "--device X" on the command line:
    /// a nickname, a chip UID, or a USB serial number or its end (as the picker and
    /// wireviewctl show it). Selected once it shows up, for this run only.</summary>
    private string? _startDevice;

    private void SelectStartDevice()
    {
        if (_startDevice == null) return;
        var match = DeviceManager.Shared.Devices
            .Where(d => !d.IsRemote)
            .FirstOrDefault(d => DeviceMatches(d.Device.UniqueId, _startDevice));
        if (match == null) return;
        _startDevice = null;
        DeviceManager.Shared.DevicesChanged -= OnStartDeviceCandidates;
        DeviceManager.Shared.PreferredUid = match.Device.UniqueId;
        DeviceManager.Shared.SelectedId = match.Id;
    }

    private void OnStartDeviceCandidates(object? sender, EventArgs e) => SelectStartDevice();

    internal static bool DeviceMatches(string uid, string query)
    {
        query = query.Trim();
        if (query.Length == 0 || string.IsNullOrEmpty(uid)) return false;
        if (string.Equals(AppSettings.Current.NicknameFor(uid), query, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(uid, query, StringComparison.OrdinalIgnoreCase)) return true;
        string? serial = Stm32PortFinder.UsbSerialFromUid(uid);
        return serial != null && query.Length >= 4 && serial.EndsWith(query, StringComparison.OrdinalIgnoreCase);
    }

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
                var viewModel = new MainWindowViewModel();
                if (_startPage != null) { viewModel.ShowPage(_startPage); _startPage = null; }
                window = new MainWindow { DataContext = viewModel };
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
                        // Hidden in the tray the app may sit for days: give back
                        // what the views just released (chart snapshots, render
                        // caches) instead of waiting for the next natural GC.
                        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: true);
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
                EditionThemeService.ApplyBackgroundColor(AppSettings.Current.BackgroundColorPreference);
                // The background photo is decoded at the widest screen's width.
                try { EditionThemeService.SetBackgroundTargetWidth(window.Screens.All.Max(s => s.Bounds.Width)); }
                catch (InvalidOperationException) { EditionThemeService.SetBackgroundTargetWidth(1920); }
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
        current.RequestedThemeVariant = EditionThemeService.GetThemeVariant(mode);
        EditionThemeService.ApplyCurrentThemePalette();
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
