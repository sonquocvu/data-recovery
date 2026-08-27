using System.Windows;
using DataRecoveryStudio.Application;
using DataRecoveryStudio.Infrastructure;

namespace DataRecoveryStudio.App;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var localization = new DictionaryLocalizationService();
        var logger = new JsonLineStructuredLogger();
        var viewModel = new MainViewModel(
            new MockDeviceDiscoveryService(),
            new MockScanService(),
            new MockRecoveryCatalogService(),
            new JsonSettingsStore(),
            localization);
        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Show();
        await logger.LogAsync(
            "Information",
            "ApplicationStarted",
            new Dictionary<string, object?> { ["phase"] = 1, ["dataMode"] = "mock" });
        await viewModel.InitializeAsync();
    }
}
