using DataRecoveryStudio.Application;
using DataRecoveryStudio.Infrastructure;

namespace DataRecoveryStudio.Tests;

public sealed class NavigationTests
{
    [Fact]
    public async Task SelectingDevice_NavigatesToScanModeWithSource()
    {
        var localization = new DictionaryLocalizationService();
        var settingsPath = Path.Combine(Path.GetTempPath(), "DataRecoveryStudio.Tests", Guid.NewGuid().ToString("N"), "settings.json");
        var viewModel = new MainViewModel(
            new MockDeviceDiscoveryService(),
            new MockScanService(TimeSpan.Zero, 2),
            new MockRecoveryCatalogService(),
            new JsonSettingsStore(settingsPath),
            localization);
        await viewModel.Devices.LoadAsync();

        viewModel.Devices.SelectDeviceCommand.Execute(viewModel.Devices.Devices[1]);

        Assert.Equal(PageKind.Devices, viewModel.CurrentPage);
        Assert.True(viewModel.Devices.Devices[1].IsSelected);
        viewModel.Devices.ContinueCommand.Execute(null);

        Assert.Equal(PageKind.ScanMode, viewModel.CurrentPage);
        Assert.Equal(viewModel.Devices.Devices[1].Device, viewModel.ScanMode.Source);
        Assert.Same(viewModel.ScanMode, viewModel.CurrentPageViewModel);
    }

    [Fact]
    public void NavigationCommand_ChangesCurrentPageAndViewModel()
    {
        var localization = new DictionaryLocalizationService();
        var viewModel = new MainViewModel(
            new MockDeviceDiscoveryService(),
            new MockScanService(TimeSpan.Zero, 2),
            new MockRecoveryCatalogService(),
            new JsonSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json")),
            localization);

        viewModel.NavigateCommand.Execute(PageKind.Settings);

        Assert.Equal(PageKind.Settings, viewModel.CurrentPage);
        Assert.Same(viewModel.Settings, viewModel.CurrentPageViewModel);
    }
}
