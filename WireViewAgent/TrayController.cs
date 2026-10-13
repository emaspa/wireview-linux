using WireView2.Net;
using WireView2.Services;

namespace WireView2.Agent;

/// <summary>
/// Glue between <see cref="AgentService"/> and the tray icon: builds the
/// <see cref="TrayState"/> from the agent's status, the settings and the login
/// entry (at most once per second, plus right after a menu action), and turns
/// menu clicks into agent actions. Settings toggles go through
/// <see cref="AgentService.RequestSettingsChange"/>, so they are saved and applied
/// on the agent's tick like a GUI edit.
/// </summary>
internal sealed class TrayController : IDisposable
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    private readonly AgentService _service;
    private readonly IToastNotifier _toast;
    private readonly Action _requestStop;
    private readonly object _lock = new();
    private ITrayIcon? _tray;
    private TrayState _shown = TrayState.Initial;
    private DateTime _lastPush = DateTime.MinValue;
    private bool _forceNext;

    private TrayController(AgentService service, IToastNotifier toast, Action requestStop)
    {
        _service = service;
        _toast = toast;
        _requestStop = requestStop;
    }

    /// <summary>Creates the tray icon for this platform; null when there is none
    /// (unsupported platform or it failed to come up, which is logged).</summary>
    public static TrayController? Start(AgentService service, IToastNotifier toast, Action requestStop)
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
            StartAtLogin: AgentInstaller.IsInstalled());
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
                    string where = AgentInstaller.SetInstalled(on);
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
            _toast.Show("WireView agent", $"That did not work: {ex.Message}");
        }

        lock (_lock) _forceNext = true;
        if (command == TrayCommand.ToggleStartAtLogin) Refresh(); // not tick-driven
    }

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
