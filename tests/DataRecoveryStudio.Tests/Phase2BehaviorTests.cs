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
        Assert.Equal("Chọn", text["Action.Select"]);
        Assert.Equal("Quét tiêu chuẩn", text["ScanMode.Standard"]);
        Assert.Equal("Quét sâu", text["ScanMode.Deep"]);
        Assert.Equal("Khuyên dùng", text["ScanMode.Recommended"]);
        Assert.Equal("Bắt đầu quét", text["Action.StartScan"]);
        Assert.Equal("Đã dùng", text["Common.Used"]);
        Assert.Equal("Còn trống", text["Common.Free"]);
        Assert.Equal("Tiếp tục quét", text["Action.KeepScanning"]);
        Assert.Equal("Dừng quét an toàn", text["Action.CancelSafely"]);
        Assert.Equal("Khoảng 3 phút", string.Format(text["Progress.AboutMinutes"], 3));
        Assert.Equal("Đang hiển thị 3 trên 16 tệp", string.Format(text["Results.ShowingCount"], 3, 16));
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
    public void ResultsFilters_ReportCountsAndBulkSelectionOnlyAffectsVisibleFiles()
    {
        var localization = new DictionaryLocalizationService();
        var source = new PhysicalDeviceId("mock:physical:results-selection");
        var viewModel = new ResultsViewModel(new MockRecoveryCatalogService(), false, localization);
        viewModel.LoadFiles(
        [
            CreateRecoverableFile("photo-a.jpg", FileCategory.Image, source),
            CreateRecoverableFile("photo-b.jpg", FileCategory.Image, source),
            CreateRecoverableFile("notes.docx", FileCategory.Document, source),
        ]);

        var all = viewModel.CategoryOptions.Single(option => option.Value == FileCategory.All);
        Assert.True(all.IsSelected);
        Assert.Contains("(3)", all.DisplayName, StringComparison.Ordinal);

        viewModel.SelectedCategory = FileCategory.Image;

        Assert.Equal(2, viewModel.VisibleCount);
        Assert.True(viewModel.CategoryOptions.Single(option => option.Value == FileCategory.Image).IsSelected);
        Assert.Contains(
            "(2)",
            viewModel.CategoryOptions.Single(option => option.Value == FileCategory.Image).DisplayName,
            StringComparison.Ordinal);

        viewModel.ToggleVisibleSelectionCommand.Execute(true);

        Assert.True(viewModel.AreAllVisibleSelected);
        Assert.Equal(2, viewModel.SelectedCount);
        Assert.All(viewModel.VisibleResults, result => Assert.True(result.IsSelected));

        viewModel.SelectedCategory = FileCategory.Document;

        Assert.False(viewModel.AreAllVisibleSelected);
        Assert.Single(viewModel.VisibleResults);
        Assert.False(viewModel.VisibleResults[0].IsSelected);
        Assert.Equal(2, viewModel.SelectedCount);

        viewModel.ToggleVisibleSelectionCommand.Execute(true);
        Assert.Equal(3, viewModel.SelectedCount);
    }

    [Fact]
    public async Task ResultsSummaryAndVisibleCount_TrackCompletedScanContextAndFilters()
    {
        var localization = new DictionaryLocalizationService();
        var source = (await new MockDeviceDiscoveryService().GetDevicesAsync(CancellationToken.None))[0];
        var session = new ScanSession(
            Guid.NewGuid(),
            source.Id,
            ScanModeKind.Standard,
            DateTimeOffset.UtcNow,
            ScanState.Completed,
            TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(15),
            16,
            source);
        var viewModel = new ResultsViewModel(new MockRecoveryCatalogService(), false, localization);

        await viewModel.LoadAsync(session);

        Assert.True(viewModel.HasScanSummary);
        Assert.Contains(source.DisplayName, viewModel.ScanSummary, StringComparison.Ordinal);
        Assert.Contains("C:", viewModel.ScanSummary, StringComparison.Ordinal);
        Assert.Contains("NTFS", viewModel.ScanSummary, StringComparison.Ordinal);
        Assert.Contains(localization["ScanMode.Standard"], viewModel.ScanSummary, StringComparison.Ordinal);
        Assert.Contains(localization["Common.Completed"], viewModel.ScanSummary, StringComparison.Ordinal);
        Assert.Contains("00:02:15", viewModel.ScanSummary, StringComparison.Ordinal);
        Assert.Equal("Showing 16 of 16 files", viewModel.VisibleCountText);
        Assert.False(viewModel.CanRecover);

        viewModel.SelectedCategory = FileCategory.Image;
        Assert.Equal("Showing 3 of 16 files", viewModel.VisibleCountText);
        viewModel.SearchText = "mountain";
        Assert.Equal("Showing 1 of 16 files", viewModel.VisibleCountText);
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

    [Fact]
    public async Task CancelConfirmationEnterEscapeAndCloseDismissals_KeepScanRunning()
    {
        var service = new ControlledScanService();
        var source = (await new MockDeviceDiscoveryService().GetDevicesAsync(CancellationToken.None))[0];
        var completions = 0;
        var viewModel = new ScanProgressViewModel(service, _ => completions++, new DictionaryLocalizationService());
        var running = viewModel.StartAsync(source, ScanModeKind.Standard);
        await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => viewModel.Elapsed == TimeSpan.FromSeconds(2));

        Assert.Contains("C:", viewModel.SourceContext, StringComparison.Ordinal);
        Assert.Equal("00:00:02", viewModel.ElapsedText);
        Assert.Equal("About 3 minutes", viewModel.RemainingText);

        for (var dismissal = 0; dismissal < 3; dismissal++)
        {
            Assert.True(viewModel.TryBeginCancellationConfirmation());
            viewModel.EndCancellationConfirmation(cancelConfirmed: false);
            Assert.True(viewModel.CanCancel);
            Assert.Equal(0, service.CancellationRequests);
        }

        service.Complete(source, ScanModeKind.Standard);
        await running;
        Assert.Equal(1, completions);
        Assert.Equal(0, service.CancellationRequests);
    }

    [Fact]
    public async Task CancelSafely_IssuesExactlyOneCancellationRequest()
    {
        var service = new ControlledScanService();
        var source = (await new MockDeviceDiscoveryService().GetDevicesAsync(CancellationToken.None))[0];
        ScanSession? completed = null;
        var viewModel = new ScanProgressViewModel(service, session => completed = session, new DictionaryLocalizationService());
        var running = viewModel.StartAsync(source, ScanModeKind.Deep);
        await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(viewModel.TryBeginCancellationConfirmation());
        Assert.False(viewModel.TryBeginCancellationConfirmation());
        viewModel.EndCancellationConfirmation(cancelConfirmed: true);
        viewModel.EndCancellationConfirmation(cancelConfirmed: true);
        await running;

        Assert.Equal(1, service.CancellationRequests);
        Assert.Equal(ScanState.Canceled, completed?.State);
    }

    [Fact]
    public async Task NaturalCompletionWhileConfirmationOpen_DefersAndDeliversOnceWithoutCanceling()
    {
        var service = new ControlledScanService();
        var source = (await new MockDeviceDiscoveryService().GetDevicesAsync(CancellationToken.None))[0];
        var completions = new List<ScanSession>();
        var invalidations = 0;
        var viewModel = new ScanProgressViewModel(service, completions.Add, new DictionaryLocalizationService());
        viewModel.CancellationConfirmationInvalidated += (_, _) => invalidations++;
        var running = viewModel.StartAsync(source, ScanModeKind.Standard);
        await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(viewModel.TryBeginCancellationConfirmation());

        service.Complete(source, ScanModeKind.Standard);
        await running;

        Assert.Equal(ScanState.Completed, viewModel.State);
        Assert.Equal(1, invalidations);
        Assert.Empty(completions);
        viewModel.EndCancellationConfirmation(cancelConfirmed: true);
        viewModel.EndCancellationConfirmation(cancelConfirmed: true);

        Assert.Single(completions);
        Assert.Equal(ScanState.Completed, completions[0].State);
        Assert.Equal(0, service.CancellationRequests);
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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(1);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected view-model state was not reached.");
            }

            await Task.Delay(10);
        }
    }

    private static RecoverableFile CreateRecoverableFile(
        string name,
        FileCategory category,
        PhysicalDeviceId source) =>
        new(
            Guid.NewGuid(),
            name,
            @"Mock\Recovered\",
            1_024,
            DateTimeOffset.UtcNow,
            category,
            RecoverabilityStatus.Good,
            source,
            "Mock preview description.",
            PreviewState.Supported);

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

    private sealed class ControlledScanService : IScanService
    {
        private readonly TaskCompletionSource<ScanSession> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CancellationRequests;

        public async Task<ScanSession> ScanAsync(
            StorageDevice source,
            ScanModeKind mode,
            IProgress<ScanProgress> progress,
            CancellationToken cancellationToken)
        {
            progress.Report(new ScanProgress(Guid.NewGuid(), ScanState.Scanning, "Progress.Phase.Metadata", 1, 100, TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(3), 1));
            Started.TrySetResult();
            try
            {
                return await _completion.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref CancellationRequests);
                throw;
            }
        }

        public void Complete(StorageDevice source, ScanModeKind mode) =>
            _completion.TrySetResult(
                new ScanSession(
                    Guid.NewGuid(),
                    source.Id,
                    mode,
                    DateTimeOffset.UtcNow,
                    ScanState.Completed,
                    TimeSpan.FromSeconds(2),
                    1,
                    source));
    }
}
