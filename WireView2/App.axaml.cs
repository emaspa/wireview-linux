using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using WireView2.Services;
using WireView2.ViewModels;
using WireView2.Views;

namespace WireView2;

public class App : Application
{
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _autoStartMenuItem;
    private readonly object _mainWindowGate = new object();
    private bool _isShowingMainWindow;

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
        });
    }

    private void InitializeTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var stream = AssetLoader.Open(new Uri("avares://WireView2/Assets/Icons/bear.ico"));
        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(stream),
            ToolTipText = "WireView2"
        };
        var menu = new NativeMenu();
        var showItem = new NativeMenuItem("Show");
        showItem.Click += (_, _) => ShowMainWindow(desktop);
        _autoStartMenuItem = new NativeMenuItem("Auto-start")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = AppSettings.Current.AutoStart
        };
        _autoStartMenuItem.Click += (_, _) =>
        {
            bool isChecked = _autoStartMenuItem.IsChecked;
            AutoStartService.SetAutoStart(isChecked);
            AppSettings.Current.AutoStart = isChecked;
            AppSettings.SaveCurrent();
        };
        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) => desktop.Shutdown();
        menu.Items.Add(showItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_autoStartMenuItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);
        _trayIcon.Menu = menu;
        _trayIcon.Clicked += (_, _) => ShowMainWindow(desktop);
        _trayIcon.IsVisible = true;
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
                window = new MainWindow { DataContext = new MainWindowViewModel() };
                if (startMinimized)
                    window.WindowState = WindowState.Minimized;
                window.Closed += (_, _) =>
                {
                    if (desktop.MainWindow == window)
                        desktop.MainWindow = null;
                };
                desktop.MainWindow = window;
                window.Show();
                if (!startMinimized) window.Activate();
            }
            else
            {
                if (window.WindowState == WindowState.Minimized)
                    window.WindowState = WindowState.Normal;
                window.Activate();
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
