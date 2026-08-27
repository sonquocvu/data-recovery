using System.Diagnostics;
using DataRecoveryStudio.Application;
using DataRecoveryStudio.Core;
using DataRecoveryStudio.Infrastructure;

namespace DataRecoveryStudio.Tests;

public sealed class Phase2BehaviorTests
{
    [Fact]
    public void LocalizationSwitch_UpdatesEnglishAndVietnameseText()
    {
        var service = new DictionaryLocalizationService();
        var text = new LocalizedText(service);
        var changed = 0;
        text.PropertyChanged += (_, _) => changed++;

        service.SetLanguage("vi-VN");

        Assert.Equal("Thiết bị", text["Nav.Devices"]);
        Assert.Equal("Quét tiêu chuẩn", text["ScanMode.Standard"]);
        Assert.Equal("Khả năng khôi phục", text["Results.Column.Recoverability"]);
        Assert.True(changed > 0);
    }

    [Fact]
    public void ThemeChange_RaisesImmediateApplicationEvent()
    {
        var localization = new DictionaryLocalizationService();
        var settings = new InMemorySettingsStore();
        var viewModel = new SettingsViewModel(settings, localization);
        ThemePreference? applied = null;
        viewModel.ThemeChanged += (_, theme) => applied = theme;

        viewModel.Theme = ThemePreference.Light;

        Assert.Equal(ThemePreference.Light, applied);
    }

    [Fact]
    public void LargeMockCatalog_FiltersTenThousandItemsWithBulkRefresh()
    {
        var localization = new DictionaryLocalizationService();
        var viewModel = new ResultsViewModel(new MockRecoveryCatalogService(), true, localization);
        var stopwatch = Stopwatch.StartNew();

        viewModel.GenerateLargeMockDataset();
        viewModel.SearchText = "09999";
        stopwatch.Stop();

        Assert.Equal(10_000, viewModel.TotalCount);
        var result = Assert.Single(viewModel.VisibleResults);
        Assert.Contains("09999", result.Name, StringComparison.Ordinal);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.True(viewModel.LastFilterDurationMilliseconds < 5_000);
    }

    [Fact]
    public async Task RecoveryAction_RequiresSessionAndSelection()
    {
        var localization = new DictionaryLocalizationService();
        var viewModel = new ResultsViewModel(new MockRecoveryCatalogService(), false, localization);
        var source = new PhysicalDeviceId("mock:source");
        var session = new ScanSession(Guid.NewGuid(), source, ScanModeKind.Standard, DateTimeOffset.UtcNow, ScanState.Completed);

        await viewModel.LoadAsync(session);
        Assert.False(viewModel.CanRecover);

        viewModel.VisibleResults[0].IsSelected = true;

        Assert.True(viewModel.CanRecover);
        Assert.Equal(viewModel.VisibleResults[0].File.SizeBytes, viewModel.SelectedBytes);
    }

    [Fact]
    public void DestinationError_ExplainsSamePhysicalDevice()
    {
        var localization = new DictionaryLocalizationService();
        var source = new PhysicalDeviceId("mock:physical:source");
        var option = new DestinationOption(source, "Source", "D:\\Recovered", 1_000_000);
        var viewModel = new DestinationViewModel(source, "Source", "D:\\", 10_000, [option], localization);

        viewModel.SelectedDestination = option;

        Assert.False(viewModel.IsValid);
        Assert.Equal(localization["Destination.Error.SameDevice"], viewModel.ValidationMessage);
    }

    [Fact]
    public async Task CancelCommand_ShowsCancelingStateUntilScanStops()
    {
        var service = new DeferredCancellationScanService();
        var source = (await new MockDeviceDiscoveryService().GetDevicesAsync(CancellationToken.None))[0];
        ScanSession? completed = null;
        var viewModel = new ScanProgressViewModel(service, session => completed = session, new DictionaryLocalizationService());
        var running = viewModel.StartAsync(source, ScanModeKind.Standard);
        await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        viewModel.CancelCommand.Execute(null);

        Assert.Equal(ScanState.Canceling, viewModel.State);
        Assert.True(viewModel.IsCanceling);
        service.AllowCancellation.SetResult();
        await running;
        Assert.Equal(ScanState.Canceled, completed?.State);
    }

    private sealed class InMemorySettingsStore : ISettingsStore
    {
        public AppSettings Settings { get; private set; } = AppSettings.Default;
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Settings);
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
        {
            Settings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class DeferredCancellationScanService : IScanService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowCancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ScanSession> ScanAsync(StorageDevice source, ScanModeKind mode, IProgress<ScanProgress> progress, CancellationToken cancellationToken)
        {
            var id = Guid.NewGuid();
            progress.Report(new ScanProgress(id, ScanState.Scanning, "Progress.Phase.Metadata", 1, 100, TimeSpan.Zero, null, 1));
            Started.SetResult();
            await AllowCancellation.Task;
            cancellationToken.ThrowIfCancellationRequested();
            return new ScanSession(id, source.Id, mode, DateTimeOffset.UtcNow, ScanState.Completed);
        }
    }
}
