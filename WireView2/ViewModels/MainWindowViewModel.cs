using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using WireView2.Device;

namespace WireView2.ViewModels;

/// <summary>An entry in the device picker: a local or (later) remote WireView.</summary>
public sealed class DeviceItem
{
    public DeviceItem(string id, string display) { Id = id; Display = display; }
    public string Id { get; }
    public string Display { get; }
    public override string ToString() => Display;
}

public partial class MainWindowViewModel : ViewModelBase
{
    private ViewModelBase? _currentPageViewModel;
    private DeviceItem? _selectedDevice;
    private bool _syncingSelection;

    public ConnectionStatusViewModel ConnectionStatus { get; } = new ConnectionStatusViewModel();
    public OverviewViewModel Overview { get; }
    public MonitoringViewModel Monitoring { get; } = new MonitoringViewModel();
    public LoggingViewModel Logging { get; } = new LoggingViewModel();
    public SettingsViewModel Settings { get; } = new SettingsViewModel();
    public DeviceViewModel Device { get; } = new DeviceViewModel();

    /// <summary>All known WireView devices (local now; remote in a later phase).</summary>
    public ObservableCollection<DeviceItem> Devices { get; } = new();

    /// <summary>Show the picker only when there's a choice to make.</summary>
    public bool HasMultipleDevices => Devices.Count > 1;

    /// <summary>The device the whole UI is currently bound to. Setting it switches the active device.</summary>
    public DeviceItem? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (Set(ref _selectedDevice, value) && !_syncingSelection && value != null)
                DeviceManager.Shared.SelectedId = value.Id;
        }
    }

    public ViewModelBase? CurrentPageViewModel
    {
        get => _currentPageViewModel;
        set => Set(ref _currentPageViewModel, value);
    }

    public string Greeting { get; } = "Welcome to WireView II!";

    public MainWindowViewModel()
    {
        Overview = new OverviewViewModel(ConnectionStatus);
        CurrentPageViewModel = Overview;

        DeviceManager.Shared.DevicesChanged += OnDevicesChanged;
        DeviceManager.Shared.SelectedChanged += OnDevicesChanged;
        DeviceManager.Shared.Start();
        ReconcileDevices();
    }

    private void OnDevicesChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(ReconcileDevices);

    /// <summary>Sync the picker list + selection to <see cref="DeviceManager"/> without churning selection.</summary>
    private void ReconcileDevices()
    {
        var managed = DeviceManager.Shared.Devices;

        // add new / update display
        foreach (var md in managed)
        {
            string display = DisplayFor(md);
            var existing = Devices.FirstOrDefault(d => d.Id == md.Id);
            if (existing == null)
                Devices.Add(new DeviceItem(md.Id, display));
            else if (existing.Display != display)
            {
                int i = Devices.IndexOf(existing);
                Devices[i] = new DeviceItem(md.Id, display);
            }
        }
        // remove gone
        foreach (var item in Devices.Where(d => managed.All(m => m.Id != d.Id)).ToList())
            Devices.Remove(item);

        OnPropertyChanged(nameof(HasMultipleDevices));

        // reflect manager's selection into the picker (guarded against feedback)
        string? selId = DeviceManager.Shared.SelectedId;
        var selItem = Devices.FirstOrDefault(d => d.Id == selId);
        if (!ReferenceEquals(selItem, _selectedDevice))
        {
            _syncingSelection = true;
            SelectedDevice = selItem;
            _syncingSelection = false;
        }
    }

    private static string DisplayFor(ManagedDevice md)
    {
        string name = md.Device.DeviceName;
        if (string.IsNullOrWhiteSpace(name)) name = "WireView";
        string shortId = md.Id.Length > 6 ? md.Id[^6..] : md.Id;
        return $"{name} · {shortId}";
    }

    [RelayCommand]
    private void ShowOverview() => CurrentPageViewModel = Overview;

    [RelayCommand]
    private void ShowMonitoring() => CurrentPageViewModel = Monitoring;

    [RelayCommand]
    private void ShowLogging() => CurrentPageViewModel = Logging;

    [RelayCommand]
    private void ShowSettings() => CurrentPageViewModel = Settings;

    [RelayCommand]
    private void ShowDevice() => CurrentPageViewModel = Device;
}
