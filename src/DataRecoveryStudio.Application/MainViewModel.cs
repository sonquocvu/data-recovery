using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Application;

public enum PageKind
{
    Devices,
    ScanMode,
    ScanProgress,
    Results,
    Settings,
}

public sealed class MainViewModel : ObservableObject
{
    private PageKind _currentPage = PageKind.Devices;

    public MainViewModel(
        IDeviceDiscoveryService devices,
        IScanService scan,
        IRecoveryCatalogService catalog,
        ISettingsStore settings,
        ILocalizationService localization,
        bool isDevelopmentMode = false)
    {
        Localization = new LocalizedText(localization);
        ScanMode = new ScanModeViewModel(() => Navigate(PageKind.Devices), BeginScan);
        ScanProgress = new ScanProgressViewModel(scan, ScanFinished, localization);
        Results = new ResultsViewModel(catalog, isDevelopmentMode, localization);
        Settings = new SettingsViewModel(settings, localization, isDevelopmentMode);
        Devices = new DeviceSelectionViewModel(devices, SelectDevice, localization);
        NavigateCommand = new RelayCommand<PageKind>(Navigate);
        ShowLargeDatasetCommand = new RelayCommand(ShowLargeDataset, () => Results.IsDevelopmentMode);
    }

    public LocalizedText Localization { get; }
    public DeviceSelectionViewModel Devices { get; }
    public ScanModeViewModel ScanMode { get; }
    public ScanProgressViewModel ScanProgress { get; }
    public ResultsViewModel Results { get; }
    public SettingsViewModel Settings { get; }
    public RelayCommand<PageKind> NavigateCommand { get; }
    public RelayCommand ShowLargeDatasetCommand { get; }

    public PageKind CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (SetProperty(ref _currentPage, value))
            {
                OnPropertyChanged(nameof(CurrentPageViewModel));
                OnPropertyChanged(nameof(IsDevicesSelected));
                OnPropertyChanged(nameof(IsResultsSelected));
                OnPropertyChanged(nameof(IsSettingsSelected));
            }
        }
    }

    public object CurrentPageViewModel => CurrentPage switch
    {
        PageKind.ScanMode => ScanMode,
        PageKind.ScanProgress => ScanProgress,
        PageKind.Results => Results,
        PageKind.Settings => Settings,
        _ => Devices,
    };

    public bool IsDevicesSelected => CurrentPage is PageKind.Devices or PageKind.ScanMode or PageKind.ScanProgress;
    public bool IsResultsSelected => CurrentPage == PageKind.Results;
    public bool IsSettingsSelected => CurrentPage == PageKind.Settings;

    public async Task InitializeAsync()
    {
        await Settings.LoadAsync().ConfigureAwait(true);
        await Devices.LoadAsync().ConfigureAwait(true);
    }

    public void Navigate(PageKind page) => CurrentPage = page;

    private void SelectDevice(StorageDevice source)
    {
        ScanMode.Source = source;
        CurrentPage = PageKind.ScanMode;
    }

    private void BeginScan(StorageDevice source, ScanModeKind mode)
    {
        CurrentPage = PageKind.ScanProgress;
        _ = ScanProgress.StartAsync(source, mode);
    }

    private void ScanFinished(ScanSession session)
    {
        CurrentPage = PageKind.Results;
        _ = Results.LoadAsync(session);
    }

    private void ShowLargeDataset()
    {
        Results.GenerateLargeMockDataset();
        CurrentPage = PageKind.Results;
    }
}
