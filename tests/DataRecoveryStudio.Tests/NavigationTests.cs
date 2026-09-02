using DataRecoveryStudio.Application;
using DataRecoveryStudio.Core;
using DataRecoveryStudio.Infrastructure;

namespace DataRecoveryStudio.Tests;

public sealed class NavigationTests
{
    [Fact]
    public async Task SelectingConnectedDevice_NavigatesDirectlyToScanModeWithStandardSelected()
    {
        var viewModel = CreateMainViewModel();
        await viewModel.Devices.LoadAsync();
        var selected = viewModel.Devices.Devices[1];

        viewModel.Devices.SelectDeviceCommand.Execute(selected);

        Assert.Equal(PageKind.ScanMode, viewModel.CurrentPage);
        Assert.Equal(selected.Device, viewModel.ScanMode.Source);
        Assert.True(viewModel.ScanMode.IsStandardSelected);
        Assert.True(viewModel.ScanMode.StartScanCommand.CanExecute(null));
        Assert.Same(viewModel.ScanMode, viewModel.CurrentPageViewModel);
        Assert.Null(typeof(DeviceSelectionViewModel).GetProperty("ContinueCommand"));
    }

    [Fact]
    public async Task RapidRepeatedSelect_RaisesOnlyOneNavigationRequest()
    {
        var requests = 0;
        var selection = new DeviceSelectionViewModel(
            new MockDeviceDiscoveryService(),
            _ => requests++,
            new DictionaryLocalizationService());
        await selection.LoadAsync();
        var card = selection.Devices.First(device => device.IsAvailable);

        selection.SelectDeviceCommand.Execute(card);
        selection.SelectDeviceCommand.Execute(card);

        Assert.Equal(1, requests);
        Assert.True(selection.IsNavigating);
        Assert.False(selection.SelectDeviceCommand.CanExecute(card));
    }

    [Fact]
    public async Task DisconnectedDevice_CannotNavigate()
    {
        var requests = 0;
        var selection = new DeviceSelectionViewModel(
            new MockDeviceDiscoveryService(),
            _ => requests++,
            new DictionaryLocalizationService());
        await selection.LoadAsync();
        var disconnected = selection.Devices.Single(device => !device.IsAvailable);

        Assert.False(selection.SelectDeviceCommand.CanExecute(disconnected));
        selection.SelectDeviceCommand.Execute(disconnected);

        Assert.Equal(0, requests);
        Assert.False(selection.IsNavigating);
    }

    [Fact]
    public async Task Refresh_DoesNotNavigate()
    {
        var viewModel = CreateMainViewModel();
        await viewModel.InitializeAsync();

        await viewModel.Devices.LoadAsync();

        Assert.Equal(PageKind.Devices, viewModel.CurrentPage);
        Assert.Equal(4, viewModel.Devices.Devices.Count);
    }

    [Fact]
    public async Task Back_ReturnsToDevicesWithoutAutomaticRenavigation()
    {
        var viewModel = CreateMainViewModel();
        await viewModel.Devices.LoadAsync();
        var device = viewModel.Devices.Devices.First(candidate => candidate.IsAvailable);
        viewModel.Devices.SelectDeviceCommand.Execute(device);
        viewModel.ScanMode.SelectModeCommand.Execute(ScanModeKind.Deep);

        viewModel.ScanMode.BackCommand.Execute(null);

        Assert.Equal(PageKind.Devices, viewModel.CurrentPage);
        Assert.False(viewModel.Devices.IsNavigating);
        Assert.True(viewModel.Devices.SelectDeviceCommand.CanExecute(device));

        viewModel.Devices.SelectDeviceCommand.Execute(device);
        Assert.Equal(PageKind.ScanMode, viewModel.CurrentPage);
        Assert.True(viewModel.ScanMode.IsStandardSelected);
    }

    [Fact]
    public async Task DeepScanSelection_IsUsedWhenStartingScan()
    {
        var scan = new RecordingScanService();
        var viewModel = CreateMainViewModel(scan);
        await viewModel.Devices.LoadAsync();
        var selected = viewModel.Devices.Devices.First(device => device.IsAvailable);
        viewModel.Devices.SelectDeviceCommand.Execute(selected);

        viewModel.ScanMode.SelectModeCommand.Execute(ScanModeKind.Deep);
        viewModel.ScanMode.StartScanCommand.Execute(null);

        Assert.True(viewModel.ScanMode.IsDeepSelected);
        Assert.Equal(PageKind.ScanProgress, viewModel.CurrentPage);
        Assert.Equal(selected.Device, scan.Source);
        Assert.Equal(ScanModeKind.Deep, scan.Mode);
    }

    [Fact]
    public void NavigationCommand_ChangesCurrentPageAndViewModel()
    {
        var viewModel = CreateMainViewModel();

        viewModel.NavigateCommand.Execute(PageKind.Settings);

        Assert.Equal(PageKind.Settings, viewModel.CurrentPage);
        Assert.Same(viewModel.Settings, viewModel.CurrentPageViewModel);
    }

    private static MainViewModel CreateMainViewModel(IScanService? scan = null)
    {
        var localization = new DictionaryLocalizationService();
        return new MainViewModel(
            new MockDeviceDiscoveryService(),
            scan ?? new MockScanService(TimeSpan.Zero, 2),
            new MockRecoveryCatalogService(),
            new JsonSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json")),
            localization,
            true);
    }

    private sealed class RecordingScanService : IScanService
    {
        private readonly TaskCompletionSource<ScanSession> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StorageDevice? Source { get; private set; }
        public ScanModeKind? Mode { get; private set; }

        public Task<ScanSession> ScanAsync(
            StorageDevice source,
            ScanModeKind mode,
            IProgress<ScanProgress> progress,
            CancellationToken cancellationToken)
        {
            Source = source;
            Mode = mode;
            return _completion.Task;
        }
    }
}
