using WireView2.Device;
using WireView2.Net;
using WireView2.Services;

namespace WireView2.Mini;

/// <summary>
/// Glue between <see cref="MiniService"/> and the tray icon: builds the
/// <see cref="TrayState"/> from WireView Mini's status, the settings and the login
/// entry (at most once per second, plus right after a menu action), and turns
/// menu clicks into actions. Settings toggles go through
/// <see cref="MiniService.RequestSettingsChange"/>, so they are saved and applied
/// on WireView Mini's tick like a GUI edit.
/// </summary>
internal sealed class TrayController : IDisposable
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    private readonly MiniService _service;
    private readonly IToastNotifier _toast;
    private readonly Action _requestStop;
    private readonly object _lock = new();
    private ITrayIcon? _tray;
    private TrayState _shown = TrayState.Initial;
    private DateTime _lastPush = DateTime.MinValue;
    private bool _forceNext;

    private TrayController(MiniService service, IToastNotifier toast, Action requestStop)
    {
        _service = service;
        _toast = toast;
        _requestStop = requestStop;
    }

    /// <summary>Creates the tray icon for this platform; null when there is none
    /// (unsupported platform or it failed to come up, which is logged).</summary>
    public static TrayController? Start(MiniService service, IToastNotifier toast, Action requestStop)
    {
        var controller = new TrayController(service, toast, requestStop);
        var initial = controller.BuildState();
        controller._shown = initial;
        var tray = TrayIconFactory.Create(initial, controller.OnCommand);
        if (tray == null) return null;
        controller._tray = tray;
        service.StatusChanged += controller.Refresh;
        return controller;
    }

    private void Refresh()
    {
        lock (_lock)
        {
            if (_tray == null) return;
            var now = DateTime.UtcNow;
            if (!_forceNext && now - _lastPush < MinInterval) return;
            _forceNext = false;
            _lastPush = now;
            _shown = BuildState();
            _tray.SetState(_shown);
        }
    }

    private TrayState BuildState()
    {
        var status = _service.GetStatus();
        var settings = AppSettings.Current;
        return new TrayState(
            Header: TrayState.HeaderFor(status),
            Connected: status.DeviceName != null,
            Fault: status.FaultText != null,
            PublishEnabled: settings.PublishEnabled,
            ShutdownOnFault: settings.SoftwareShutdownOnFault,
            StartAtLogin: MiniInstaller.IsInstalled(),
            DeviceCommands: status.DeviceName != null && !status.Handover,
            RemoteLines: TrayState.RemoteLinesFor(_service.GetRemoteHosts()),
            DaemonLine: _service.DaemonMode ? TrayState.DaemonLineFor(_service.Daemon.Observed) : null);
    }

    /// <summary>A menu click, on the tray's thread.</summary>
    private void OnCommand(TrayCommand command)
    {
        TrayState shown;
        lock (_lock) shown = _shown;
        try
        {
            switch (command)
            {
                case TrayCommand.OpenApp:
                    GuiLauncher.Open(_toast);
                    break;

                case TrayCommand.OpenPanel:
                    OpenPanel();
                    break;

                case TrayCommand.ScreenMain:
                case TrayCommand.ScreenSimple:
                case TrayCommand.ScreenCurrent:
                case TrayCommand.ScreenTemp:
                case TrayCommand.ScreenStatus:
                case TrayCommand.PauseUpdates:
                case TrayCommand.ResumeUpdates:
                {
                    int cmd = (int)(command switch
                    {
                        TrayCommand.ScreenSimple => WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_SIMPLE,
                        TrayCommand.ScreenCurrent => WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_CURRENT,
                        TrayCommand.ScreenTemp => WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_TEMP,
                        TrayCommand.ScreenStatus => WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_STATUS,
                        TrayCommand.PauseUpdates => WireViewPro2Device.SCREEN_CMD.SCREEN_PAUSE_UPDATES,
                        TrayCommand.ResumeUpdates => WireViewPro2Device.SCREEN_CMD.SCREEN_RESUME_UPDATES,
                        _ => WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_MAIN,
                    });
                    var r = _service.RunLocalCommand(WireViewCommand.Screen("", cmd));
                    string text = ControlPanel.ScreenText(cmd);
                    FileLog.Write(r == DaemonResult.Ok ? "INFO" : "WARN", $"tray: {text.TrimEnd('.')} -> {r}");
                    if (r != DaemonResult.Ok) _toast.Show("WireView Mini", CommandFailure("The screen change", r));
                    break;
                }

                case TrayCommand.ClearFaults:
                {
                    var r = _service.ClearLocalFaults();
                    if (r != DaemonResult.Ok)
                    {
                        FileLog.Warn($"tray: Clear faults -> {r}");
                        _toast.Show("WireView Mini", CommandFailure("Clearing the faults", r));
                    }
                    break;
                }

                case TrayCommand.TogglePublish:
                {
                    // Set to the opposite of what the menu showed, so a double
                    // click before the tick lands sets the same value twice.
                    bool on = !shown.PublishEnabled;
                    _service.RequestSettingsChange($"LAN publishing turned {(on ? "on" : "off")}",
                        s => s.PublishEnabled = on);
                    break;
                }

                case TrayCommand.ToggleShutdownOnFault:
                {
                    bool on = !shown.ShutdownOnFault;
                    _service.RequestSettingsChange($"shutdown on fault turned {(on ? "on" : "off")}",
                        s => s.SoftwareShutdownOnFault = on);
                    break;
                }

                case TrayCommand.ToggleStartAtLogin:
                {
                    bool on = !shown.StartAtLogin;
                    string where = MiniInstaller.SetInstalled(on);
                    FileLog.Info(on
                        ? $"tray: start at login turned on ({where})"
                        : $"tray: start at login turned off (removed {where})");
                    break;
                }

                case TrayCommand.Quit:
                    FileLog.Info("tray: Quit chosen");
                    _requestStop();
                    return;
            }
        }
        catch (Exception ex)
        {
            FileLog.Warn($"tray: {command} failed: {ex.Message}");
            _toast.Show("WireView Mini", $"That did not work: {ex.Message}");
        }

        lock (_lock) _forceNext = true;
        if (command == TrayCommand.ToggleStartAtLogin) Refresh(); // not tick-driven
    }

    private void OpenPanel()
    {
        if (_service.PanelUrl is not { } url)
        {
            string why = _service.PublishError ?? "WireView Mini is not listening on its port";
            FileLog.Warn($"tray: Settings and controls: no listener ({why})");
            _toast.Show("WireView Mini", $"The control panel is not available: {why}.");
            return;
        }
        if (GuiLauncher.OpenUrl(url) is { } error)
        {
            FileLog.Warn($"tray: Settings and controls: cannot open the browser: {error}");
            _toast.Show("WireView Mini", $"Cannot open the browser ({error}). Run wireview-mini --open for the address.");
            return;
        }
        FileLog.Info("tray: Settings and controls: opened the control panel in the browser");
    }

    private static string CommandFailure(string action, DaemonResult r) => r switch
    {
        DaemonResult.NotConnected => $"{action} did not work: no device connected.",
        DaemonResult.Denied => $"{action} was refused by wireviewd. Add your user to the 'wireview' group.",
        _ => $"{action} did not work: the device did not take the command.",
    };

    public void Dispose()
    {
        _service.StatusChanged -= Refresh;
        ITrayIcon? tray;
        lock (_lock)
        {
            tray = _tray;
            _tray = null;
        }
        tray?.Dispose();
    }
}
