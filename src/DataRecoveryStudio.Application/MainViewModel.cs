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

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private PageKind _currentPage = PageKind.Devices;
    private bool _sourceRemovedDuringScan;

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
        Devices.DevicesRefreshed += HandleDevicesRefreshed;
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

    public Task RefreshDevicesAsync() => Devices.LoadAsync();

    public void Navigate(PageKind page)
    {
        if (page == PageKind.Devices)
        {
            Devices.PrepareForDisplay();
        }

        CurrentPage = page;
    }

    private void SelectDevice(StorageDevice source)
    {
        _sourceRemovedDuringScan = false;
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
        if (_sourceRemovedDuringScan)
        {
            _sourceRemovedDuringScan = false;
            Results.Reset();
            ScanMode.Source = null;
            Devices.ShowNotice("Device.RemovedDuringScan");
            Devices.PrepareForDisplay();
            CurrentPage = PageKind.Devices;
            return;
        }

        CurrentPage = PageKind.Results;
        _ = Results.LoadAsync(session);
    }

    public void Dispose()
    {
        Devices.DevicesRefreshed -= HandleDevicesRefreshed;
        Devices.Dispose();
    }

    private void HandleDevicesRefreshed(IReadOnlyList<StorageDevice> devices)
    {
        var source = CurrentPage == PageKind.ScanProgress ? ScanProgress.Source : ScanMode.Source;
        if (source is null)
        {
            return;
        }

        var sourceVolumeId = source.Volumes.FirstOrDefault()?.VolumeGuidPath;
        var stillPresent = sourceVolumeId is not null && devices.Any(device =>
            device.Volumes.Any(volume => volume.VolumeGuidPath.Equals(sourceVolumeId, StringComparison.OrdinalIgnoreCase)) &&
            device.IsSupported);
        if (stillPresent)
        {
            return;
        }

        if (CurrentPage == PageKind.ScanProgress)
        {
            _sourceRemovedDuringScan = true;
            ScanProgress.CancelForDeviceRemoval();
            return;
        }

        ScanMode.Source = null;
        Devices.ShowNotice("Device.Removed");
        Devices.PrepareForDisplay();
        CurrentPage = PageKind.Devices;
    }

    private void ShowLargeDataset()
    {
        Results.GenerateLargeMockDataset();
        CurrentPage = PageKind.Results;
    }
}
