using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using WireView2.Device;
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
    private DateTime _lastToastUtc = DateTime.MinValue;

    private static readonly TimeSpan ToastCooldown = TimeSpan.FromSeconds(10.0);

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

    public string ConnectionText => IsConnected ? "Connected" : "Disconnected";
    public string StatusText => IsConnected ? "Connected" : "Disconnected";

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
        SetFault(0, 0);
    }

    // --------------- Commands ---------------

    [RelayCommand]
    public void DismissFault()
    {
        if (_connector.Device is WireViewPro2Device device)
        {
            device.ClearFaults(0);
            device.ScreenCmd(WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_SAME);
        }
        else if (_connector.Device is HwmonDevice { DaemonAvailable: true } hwmon)
        {
            hwmon.ClearFaults(0);
            hwmon.ScreenCmd(WireViewPro2Device.SCREEN_CMD.SCREEN_GOTO_SAME);
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

    // --------------- Event handlers ---------------

    private void OnConnectionChanged(object? sender, bool connected)
    {
        if (!connected)
            SetFault(0, 0);
        SetConnected(connected);
    }

    private void OnDataUpdated(object? sender, DeviceData data)
    {
        ushort prevStatus = FaultStatusMask;
        SetFault(data.FaultStatus, data.FaultLog);

        if (prevStatus == 0 && data.FaultStatus != 0
            && DateTime.UtcNow - _lastToastUtc > ToastCooldown)
        {
            _lastToastUtc = DateTime.UtcNow;
            string message = FaultText ?? "Fault detected.";
            _toast.Show("WireView Fault", message);

            if (AppSettings.Current.SoftwareShutdownOnFault)
            {
                try
                {
                    Process.Start("systemctl", "poweroff");
                }
                catch
                {
                    // Fallback if systemctl is not available
                    try { Process.Start("shutdown", "-h now"); } catch { }
                }
            }
        }
    }

    // --------------- Helpers ---------------

    private void SetConnected(bool connected)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            IsConnected = connected;
            OnPropertyChanged(nameof(StatusText));
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                IsConnected = connected;
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

    private static string FormatFaults(ushort faultStatus)
    {
        if (faultStatus == 0xFFFF)
            return "FAULT (details unavailable via hwmon)";

        var parts = new List<string>();
        foreach (var fault in Enum.GetValues<WireViewPro2Device.FAULT>())
        {
            int bit = 1 << (int)fault;
            if ((faultStatus & bit) != 0)
                parts.Add(FriendlyFaultName(fault));
        }
        return parts.Count > 0
            ? "FAULT: " + string.Join(", ", parts)
            : "FAULT";
    }

    private static string FriendlyFaultName(WireViewPro2Device.FAULT f) => f switch
    {
        WireViewPro2Device.FAULT.FAULT_OTP_TCHIP      => "Chip Temperature",
        WireViewPro2Device.FAULT.FAULT_OTP_TS          => "Connector Temperature",
        WireViewPro2Device.FAULT.FAULT_OCP             => "Over-Current",
        WireViewPro2Device.FAULT.FAULT_WIRE_OCP        => "Wire Over-Current",
        WireViewPro2Device.FAULT.FAULT_OPP             => "Over-Power",
        WireViewPro2Device.FAULT.FAULT_CURRENT_IMBALANCE => "Current Imbalance",
        _ => f.ToString(),
    };

    public void Dispose()
    {
        _connector.ConnectionChanged -= OnConnectionChanged;
        _connector.DataUpdated -= OnDataUpdated;
    }
}
