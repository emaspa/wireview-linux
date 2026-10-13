using System;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using WireView2.Device;
using WireView2.Net;
using WireView2.Services;

namespace WireView2.ViewModels;

public sealed partial class ConnectionStatusViewModel : ViewModelBase, IDisposable
{
    private readonly DeviceAutoConnector _connector;
    private readonly IToastNotifier _toast;

    private bool _isConnected;
    private bool _hasFault;
    private string? _faultText;
    private string _faultIcon = "?";
    private ushort _lastFaultStatus;
    private ushort _lastFaultLog;
    // Debounce, edge detection and toast cooldown (shared with wireview-agent).
    private readonly FaultMonitor _faultMonitor = new();

    // --------------- Connection state ---------------

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (Set(ref _isConnected, value))
                OnPropertyChanged(nameof(ConnectionText));
        }
    }

    public string ConnectionText => IsConnected ? ConnectedText : "Disconnected";
    public string StatusText => IsConnected ? ConnectedText : "Disconnected";

    /// <summary>"Connected", or "Connected via agent" when the selected device is
    /// reached through wireview-agent on this machine.</summary>
    private string ConnectedText =>
        _connector.Device is NetworkDevice { IsLocalAgent: true } ? "Connected via agent" : "Connected";

    // --------------- Fault state ---------------

    public bool HasFault
    {
        get => _hasFault;
        private set => Set(ref _hasFault, value);
    }

    public string? FaultText
    {
        get => _faultText;
        private set => Set(ref _faultText, value);
    }

    public string FaultIcon
    {
        get => _faultIcon;
        private set => Set(ref _faultIcon, value);
    }

    public ushort FaultStatusMask
    {
        get => _lastFaultStatus;
        private set
        {
            if (Set(ref _lastFaultStatus, value))
            {
                OnPropertyChanged(nameof(HasFault));
                RaiseFaultBitPropertiesChanged();
            }
        }
    }

    public ushort FaultLogMask
    {
        get => _lastFaultLog;
        private set
        {
            if (Set(ref _lastFaultLog, value))
                RaiseFaultBitPropertiesChanged();
        }
    }

    // --------------- Fault-status bit properties ---------------

    public bool StatusOtpTchip       => GetFaultBit(FaultStatusMask, WireViewPro2Device.FAULT.FAULT_OTP_TCHIP);
    public bool StatusOtpTs          => GetFaultBit(FaultStatusMask, WireViewPro2Device.FAULT.FAULT_OTP_TS);
    public bool StatusOcp            => GetFaultBit(FaultStatusMask, WireViewPro2Device.FAULT.FAULT_OCP);
    public bool StatusWireOcp        => GetFaultBit(FaultStatusMask, WireViewPro2Device.FAULT.FAULT_WIRE_OCP);
    public bool StatusOpp            => GetFaultBit(FaultStatusMask, WireViewPro2Device.FAULT.FAULT_OPP);
    public bool StatusCurrentImbalance => GetFaultBit(FaultStatusMask, WireViewPro2Device.FAULT.FAULT_CURRENT_IMBALANCE);

    // --------------- Fault-log bit properties ---------------

    public bool LogOtpTchip       => GetFaultBit(FaultLogMask, WireViewPro2Device.FAULT.FAULT_OTP_TCHIP);
    public bool LogOtpTs          => GetFaultBit(FaultLogMask, WireViewPro2Device.FAULT.FAULT_OTP_TS);
    public bool LogOcp            => GetFaultBit(FaultLogMask, WireViewPro2Device.FAULT.FAULT_OCP);
    public bool LogWireOcp        => GetFaultBit(FaultLogMask, WireViewPro2Device.FAULT.FAULT_WIRE_OCP);
    public bool LogOpp            => GetFaultBit(FaultLogMask, WireViewPro2Device.FAULT.FAULT_OPP);
    public bool LogCurrentImbalance => GetFaultBit(FaultLogMask, WireViewPro2Device.FAULT.FAULT_CURRENT_IMBALANCE);

    // --------------- Constructor ---------------

    public ConnectionStatusViewModel(DeviceAutoConnector? connector = null, IToastNotifier? toast = null)
    {
        _connector = connector ?? DeviceAutoConnector.Shared;

        if (toast != null)
        {
            _toast = toast;
        }
        else if (OperatingSystem.IsLinux())
        {
            _toast = new LinuxToastNotifier();
        }
        else
        {
            _toast = new NullToastNotifier();
        }

        _connector.ConnectionChanged += OnConnectionChanged;
        _connector.DataUpdated += OnDataUpdated;
        _connector.Start();
        SetConnected(_connector.Device?.Connected ?? false);
        ApplyEditionFromCurrentDevice();
        SetFault(0, 0);
    }

    // --------------- Commands ---------------

    [RelayCommand]
    public void DismissFault()
    {
        // Keep-masks: clear every active fault, keep the whole fault log.
        const int keepNoActive = 0, keepWholeLog = 0xFFFF;
        if (_connector.Device is WireViewPro2Device device)
        {
            device.ClearFaults(keepStatusMask: keepNoActive, keepLogMask: keepWholeLog);
            device.ScreenCmd(WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_SAME);
        }
        else if (_connector.Device is HwmonDevice { DaemonAvailable: true } hwmon)
        {
            var r = hwmon.ClearFaults(keepStatusMask: keepNoActive, keepLogMask: keepWholeLog);
            if (r != DaemonResult.Ok)
            {
                // Keep the banner: the fault is still latched on the device.
                ReportDaemonFailure("Clearing the fault", r);
                return;
            }
            hwmon.ScreenCmd(WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_SAME);
        }
        else if (_connector.Device is NetworkDevice { IsLocalAgent: true } agent)
        {
            // The agent owns the port: relay the clear through it.
            _ = ClearFaultsViaAgentAsync(agent, keepNoActive, keepWholeLog);
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            HasFault = false;
        }
        else
        {
            Dispatcher.UIThread.Post(() => HasFault = false, DispatcherPriority.Background);
        }
    }

    /// <summary>Clears faults on a device the local agent owns (keep-masks as for
    /// the direct paths), then refreshes its screen. A failure is reported with
    /// the same notification as a wireviewd failure.</summary>
    public async System.Threading.Tasks.Task ClearFaultsViaAgentAsync(NetworkDevice agent, int keepStatusMask, int keepLogMask)
    {
        var r = await agent.SendCommandAsync(WireViewCommand.Faults(agent.UniqueId, keepStatusMask, keepLogMask));
        if (!r.Ok)
        {
            _toast.Show("WireView", r.FailureText("Clearing the fault"));
            return;
        }
        await agent.SendCommandAsync(WireViewCommand.Screen(agent.UniqueId, (int)WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_SAME));
    }

    // --------------- Event handlers ---------------

    private void OnConnectionChanged(object? sender, bool connected)
    {
        if (!connected)
            SetFault(0, 0);
        // Also raised when the multi-device picker selects another device, so the
        // palette follows the selected device.
        ApplyEditionFromCurrentDevice();
        SetConnected(connected);
    }

    /// <summary>Noctua Edition device: Noctua palette while the theme is Auto
    /// (upstream 1.0.8). No device or any other device: the default palette.</summary>
    private void ApplyEditionFromCurrentDevice()
    {
        EditionThemeService.SetEdition(EditionThemeService.EditionOf(_connector.Device));
    }

    private void OnDataUpdated(object? sender, DeviceData data)
    {
        // A LAN device learns its hardware revision from its first /sensors
        // reply, after the connect event; SetEdition ignores an unchanged edition.
        ApplyEditionFromCurrentDevice();

        // Debounce (a fault must be seen on two consecutive polls), edge
        // detection and cooldown: see FaultMonitor.
        var fault = _faultMonitor.Observe(data);
        SetFault(fault.Status, fault.Log);

        if (fault.Raised)
        {
            // wireview-agent raises its own alert for every device it watches
            // (this machine's and the remote hosts') and shuts down for this
            // machine's device: one notification, one shutdown. A remote device
            // selected here keeps the GUI's own shutdown, as before.
            bool agentRunning = _connector.Device is NetworkDevice { IsLocalAgent: true }
                || WireViewDiscoveryService.Shared.AgentPresent
                || AgentInstance.IsRunning();
            bool localDevice = _connector.Device is not NetworkDevice { IsLocalAgent: false };

            // Format from the data directly: the FaultText property is set
            // via a UI-thread post and may not be updated yet on this thread.
            if (!agentRunning)
                _toast.Show("WireView Fault", fault.Text);

            if (AppSettings.Current.SoftwareShutdownOnFault && !(agentRunning && localDevice))
                SystemPower.Shutdown();
        }
    }

    // --------------- Helpers ---------------

    /// <summary>Tells the user a wireviewd command failed, via the same desktop
    /// notification used for faults. A permission denial gets the how-to-fix
    /// message.</summary>
    public void ReportDaemonFailure(string action, DaemonResult result)
    {
        string message = result == DaemonResult.Denied
            ? DaemonResults.DeniedMessage
            : $"{action} failed: {result.Describe()}.";
        _toast.Show("WireView", message);
    }

    private void SetConnected(bool connected)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            IsConnected = connected;
            OnPropertyChanged(nameof(ConnectionText));
            OnPropertyChanged(nameof(StatusText));
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                IsConnected = connected;
                OnPropertyChanged(nameof(ConnectionText));
                OnPropertyChanged(nameof(StatusText));
            }, DispatcherPriority.Background);
        }
    }

    private void SetFault(ushort faultStatus, ushort faultLog)
    {
        bool hasFault = faultStatus != 0;
        string? text = hasFault ? FormatFaults(faultStatus) : null;

        if (Dispatcher.UIThread.CheckAccess())
        {
            HasFault = hasFault;
            FaultText = text;
            FaultStatusMask = faultStatus;
            FaultLogMask = faultLog;
            OnPropertyChanged(nameof(StatusText));
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                HasFault = hasFault;
                FaultText = text;
                FaultStatusMask = faultStatus;
                FaultLogMask = faultLog;
                OnPropertyChanged(nameof(StatusText));
            }, DispatcherPriority.Background);
        }
    }

    private static bool GetFaultBit(ushort mask, WireViewPro2Device.FAULT f)
    {
        return (mask & (1 << (int)f)) != 0;
    }

    private void RaiseFaultBitPropertiesChanged()
    {
        OnPropertyChanged(nameof(StatusOtpTchip));
        OnPropertyChanged(nameof(StatusOtpTs));
        OnPropertyChanged(nameof(StatusOcp));
        OnPropertyChanged(nameof(StatusWireOcp));
        OnPropertyChanged(nameof(StatusOpp));
        OnPropertyChanged(nameof(StatusCurrentImbalance));
        OnPropertyChanged(nameof(LogOtpTchip));
        OnPropertyChanged(nameof(LogOtpTs));
        OnPropertyChanged(nameof(LogOcp));
        OnPropertyChanged(nameof(LogWireOcp));
        OnPropertyChanged(nameof(LogOpp));
        OnPropertyChanged(nameof(LogCurrentImbalance));
    }

    private static string FormatFaults(ushort faultStatus) => FaultMonitor.FormatFaults(faultStatus);

    public void Dispose()
    {
        _connector.ConnectionChanged -= OnConnectionChanged;
        _connector.DataUpdated -= OnDataUpdated;
    }
}
