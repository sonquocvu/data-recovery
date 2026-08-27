using DataRecoveryStudio.Application;
using DataRecoveryStudio.Core;
using DataRecoveryStudio.Infrastructure;

namespace DataRecoveryStudio.Tests;

public sealed class RuntimeRegressionTests
{
    [Fact]
    public async Task DefaultMockCatalog_ContainsConnectedInternalNtfsAndRemovableExFatOrFat32()
    {
        var devices = await new MockDeviceDiscoveryService().GetDevicesAsync(CancellationToken.None);

        Assert.Contains(devices, device =>
            device.Type == StorageDeviceType.Internal &&
            device.ConnectionStatus == DeviceConnectionStatus.Online &&
            device.Volumes.Any(volume => volume.FileSystem == "NTFS"));
        Assert.Contains(devices, device =>
            device.Type == StorageDeviceType.RemovableUsb &&
            device.ConnectionStatus == DeviceConnectionStatus.Online &&
            device.Volumes.Any(volume => volume.FileSystem is "FAT32" or "exFAT"));
    }

    [Fact]
    public async Task Refresh_ReplacesCatalogWithoutDuplicates()
    {
        var viewModel = new DeviceSelectionViewModel(
            new MockDeviceDiscoveryService(),
            _ => { },
            new DictionaryLocalizationService());

        await viewModel.LoadAsync();
        await viewModel.LoadAsync();

        Assert.Equal(4, viewModel.Devices.Count);
        Assert.Equal(4, viewModel.Devices.Select(device => device.Device.Id).Distinct().Count());
        Assert.Contains(viewModel.Devices, device => device.IsInternal && device.IsAvailable);
        Assert.Contains(viewModel.Devices, device => device.IsRemovable && device.IsAvailable);
    }

    [Fact]
    public async Task StartupInitialization_CompletesAfterDelayedServicesWithoutShortLivedToken()
    {
        var settings = new DeferredSettingsStore();
        var devices = new DeferredDeviceDiscoveryService();
        var localization = new DictionaryLocalizationService();
        var viewModel = new MainViewModel(
            devices,
            new MockScanService(TimeSpan.Zero, 2),
            new MockRecoveryCatalogService(),
            settings,
            localization);

        var initialization = viewModel.InitializeAsync();
        Assert.False(initialization.IsCompleted);
        settings.Completion.SetResult(new AppSettings(ThemePreference.Dark, "vi-VN"));
        devices.Completion.SetResult(await new MockDeviceDiscoveryService().GetDevicesAsync(CancellationToken.None));
        await initialization;

        Assert.Equal(4, viewModel.Devices.Devices.Count);
        Assert.Equal(ThemePreference.Dark, viewModel.Settings.Theme);
        Assert.Equal("vi-VN", viewModel.Settings.LanguageCode);
    }

    [Fact]
    public async Task CorruptSettings_FallBackAndWriteDiagnosticEvent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DataRecoveryStudio.Tests", Guid.NewGuid().ToString("N"));
        var settingsPath = Path.Combine(directory, "settings.json");
        var logPath = Path.Combine(directory, "diagnostics.jsonl");
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(settingsPath, "{ definitely-not-valid-json");
            var logger = new JsonLineStructuredLogger(logPath);
            var store = new JsonSettingsStore(settingsPath, logger);

            var loaded = await store.LoadAsync(CancellationToken.None);

            Assert.Equal(AppSettings.Default, loaded);
            var diagnostic = await File.ReadAllTextAsync(logPath);
            Assert.Contains("SettingsLoadFailed", diagnostic, StringComparison.Ordinal);
            Assert.Contains("InvalidJson", diagnostic, StringComparison.Ordinal);
            Assert.Contains("JsonException", diagnostic, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private sealed class DeferredSettingsStore : ISettingsStore
    {
        public TaskCompletionSource<AppSettings> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken) => Completion.Task.WaitAsync(cancellationToken);
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class DeferredDeviceDiscoveryService : IDeviceDiscoveryService
    {
        public TaskCompletionSource<IReadOnlyList<StorageDevice>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<StorageDevice>> GetDevicesAsync(CancellationToken cancellationToken) => Completion.Task.WaitAsync(cancellationToken);
    }
}
