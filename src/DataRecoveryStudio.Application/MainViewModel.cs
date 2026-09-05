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
    private readonly bool _isDevelopmentMode;
    private readonly bool _liveStandardScanEnabled;
    private readonly bool _liveFat32StandardScanEnabled;
    private readonly bool _liveExFatStandardScanEnabled;
    private readonly ILiveScanUiOrchestrator? _liveOrchestrator;
    private PageKind? _pendingNavigation;

    public MainViewModel(
        IDeviceDiscoveryService devices,
        IScanService scan,
        IRecoveryCatalogService catalog,
        ISettingsStore settings,
        ILocalizationService localization,
        bool isDevelopmentMode = false,
        bool liveStandardScanEnabled = false,
        ILiveScanUiOrchestrator? liveOrchestrator = null,
        bool liveFat32StandardScanEnabled = false,
        bool liveExFatStandardScanEnabled = false)
    {
        _isDevelopmentMode = isDevelopmentMode;
        _liveStandardScanEnabled = liveStandardScanEnabled;
        _liveFat32StandardScanEnabled = liveFat32StandardScanEnabled;
        _liveExFatStandardScanEnabled = liveExFatStandardScanEnabled;
        _liveOrchestrator = liveOrchestrator;
        Localization = new LocalizedText(localization);
        ScanMode = new ScanModeViewModel(() => Navigate(PageKind.Devices), BeginScan, localization, isDevelopmentMode,
            liveStandardScanEnabled, liveFat32StandardScanEnabled, liveExFatStandardScanEnabled);
        ScanProgress = new ScanProgressViewModel(scan, ScanFinished, localization, liveOrchestrator, LiveScanFinished);
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
    public event Action<PageKind>? NavigationCancellationRequested;
    public event EventHandler? ScanTerminal;
    public bool IsDevelopmentMode => _isDevelopmentMode;
    public bool IsLiveStandardScanEnabled => _liveStandardScanEnabled;
    public bool IsLiveExFatStandardScanEnabled => _liveExFatStandardScanEnabled;
    public bool IsAnyLiveStandardScanEnabled => _liveStandardScanEnabled || _liveFat32StandardScanEnabled || _liveExFatStandardScanEnabled;
    public bool IsLiveFat32StandardScanEnabled => _liveFat32StandardScanEnabled;
    public bool IsProductionFeatureDisabled => !_isDevelopmentMode && !_liveStandardScanEnabled && !_liveFat32StandardScanEnabled && !_liveExFatStandardScanEnabled;

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
        if (ScanProgress.IsActive && page != PageKind.ScanProgress)
        {
            NavigationCancellationRequested?.Invoke(page);
            return;
        }

        NavigateImmediately(page);
    }

    public void ConfirmNavigationAfterCancellation(PageKind page)
    {
        if (!ScanProgress.IsActive)
        {
            NavigateImmediately(page);
            return;
        }

        _pendingNavigation = page;
        ScanProgress.RequestCancellation();
    }

    private void NavigateImmediately(PageKind page)
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
        if (ScanProgress.IsActive)
        {
            return;
        }

        CurrentPage = PageKind.ScanProgress;
        if (!_isDevelopmentMode && mode == ScanModeKind.Standard && ScanMode.Capability?.IsLive == true && _liveOrchestrator is not null)
        {
            _ = ScanProgress.StartLiveAsync(source);
            return;
        }

        if (_isDevelopmentMode)
        {
            _ = ScanProgress.StartAsync(source, mode);
        }
        else
        {
            CurrentPage = PageKind.ScanMode;
        }
    }

    private void ScanFinished(ScanSession session)
    {
        ScanTerminal?.Invoke(this, EventArgs.Empty);
        if (_pendingNavigation is PageKind pending)
        {
            _pendingNavigation = null;
            NavigateImmediately(pending);
            return;
        }

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

    private void LiveScanFinished(LiveScanUiSession session)
    {
        ScanTerminal?.Invoke(this, EventArgs.Empty);
        if (_pendingNavigation is PageKind pending)
        {
            _pendingNavigation = null;
            NavigateImmediately(pending);
            return;
        }

        var status = session.Result.Terminal.Status;
        if (_sourceRemovedDuringScan || status is LiveScanTerminalStatus.SourceRemoved or LiveScanTerminalStatus.TargetChanged)
        {
            _sourceRemovedDuringScan = false;
            Results.Reset();
            ScanMode.Source = null;
            Devices.ShowNotice("Device.LiveSourceRemoved");
            Devices.PrepareForDisplay();
            CurrentPage = PageKind.Devices;
            return;
        }

        if (status is LiveScanTerminalStatus.Completed or LiveScanTerminalStatus.Partial or LiveScanTerminalStatus.ChangedDuringScan)
        {
            CurrentPage = PageKind.Results;
            _ = Results.LoadLiveAsync(session);
            return;
        }

        ScanMode.ShowOutcome(LiveScanOutcomeLocalization.GetLocalizationKey(status));
        CurrentPage = PageKind.ScanMode;
    }

    public void Dispose()
    {
        Devices.DevicesRefreshed -= HandleDevicesRefreshed;
        Devices.Dispose();
    }

    private void HandleDevicesRefreshed(IReadOnlyList<StorageDevice> devices)
    {
        _liveOrchestrator?.UpdateDiscoverySnapshot(devices);
        var source = CurrentPage == PageKind.ScanProgress ? ScanProgress.Source : ScanMode.Source;
        if (source is null)
        {
            return;
        }

        var replacement = devices.FirstOrDefault(device => IsSameSource(source, device));
        if (replacement is not null && replacement.IsSupported)
        {
            if (CurrentPage != PageKind.ScanProgress)
            {
                ScanMode.Source = replacement;
            }

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

    private static bool IsSameSource(StorageDevice expected, StorageDevice current)
    {
        var expectedVolume = expected.Volumes.SingleOrDefault();
        var currentVolume = current.Volumes.SingleOrDefault();
        if (expectedVolume is null || currentVolume is null) return false;
        return expected.ConnectionStatus == current.ConnectionStatus &&
            (!expectedVolume.FileSystem.Equals("exFAT", StringComparison.OrdinalIgnoreCase) || expectedVolume.Extents.SequenceEqual(currentVolume.Extents)) &&
            expectedVolume.VolumeGuidPath.Equals(currentVolume.VolumeGuidPath, StringComparison.OrdinalIgnoreCase) &&
            expectedVolume.MountPath.Equals(currentVolume.MountPath, StringComparison.OrdinalIgnoreCase) &&
            expectedVolume.FileSystem.Equals(currentVolume.FileSystem, StringComparison.OrdinalIgnoreCase) &&
            expectedVolume.CapacityBytes == currentVolume.CapacityBytes &&
            expectedVolume.PhysicalDeviceIds.Select(id => id.Value).ToHashSet(StringComparer.Ordinal)
                .SetEquals(currentVolume.PhysicalDeviceIds.Select(id => id.Value)) &&
            expected.PhysicalDisks.Select(disk => disk.DiskNumber).ToHashSet()
                .SetEquals(current.PhysicalDisks.Select(disk => disk.DiskNumber));
    }

    private void ShowLargeDataset()
    {
        Results.GenerateLargeMockDataset();
        CurrentPage = PageKind.Results;
    }
}
