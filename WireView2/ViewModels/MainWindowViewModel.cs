using CommunityToolkit.Mvvm.Input;

namespace WireView2.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private ViewModelBase? _currentPageViewModel;

    public ConnectionStatusViewModel ConnectionStatus { get; } = new ConnectionStatusViewModel();
    public OverviewViewModel Overview { get; }
    public MonitoringViewModel Monitoring { get; } = new MonitoringViewModel();
    public LoggingViewModel Logging { get; } = new LoggingViewModel();
    public SettingsViewModel Settings { get; } = new SettingsViewModel();
    public DeviceViewModel Device { get; } = new DeviceViewModel();

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
