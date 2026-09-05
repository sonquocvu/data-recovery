using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DataRecoveryStudio.Application;
using DataRecoveryStudio.App;
using DataRecoveryStudio.Core;
using DataRecoveryStudio.Infrastructure;
using Xunit.Abstractions;
using static DataRecoveryStudio.Tests.Phase8CLiveExFatTests;

namespace DataRecoveryStudio.Tests;

public sealed class Phase8CWpfTests(ITestOutputHelper output)
{
    [Fact]
    public async Task TenThousandCandidatesRenderScrollLocalizeAndCannotRecover()
    {
        var (session, execution) = await Execute(new InstrumentedFactory(Fixture().Bytes));
        // Synthetic volume of DTOs from production metadata; this is UI performance coverage, not device discovery.
        var candidates = Enumerable.Range(1, 10_000).Select(index => execution.Candidates[0] with
        { CandidateId = Guid.NewGuid(), Name = $"deleted-{index:00000}.txt" }).ToArray();
        var result = new LiveScanResult(session, execution.Terminal with { CandidateCount = candidates.Length }, candidates, []);
        await WpfTestHost.Instance.RunAsync(async () =>
        {
            var source = Device();
            var live = new ControlledOrchestrator(session);
            var localization = new DictionaryLocalizationService();
            using var main = CreateMain(new MutableDiscovery([source]), live, localization);
            using var bindings = new WpfBindingErrorScope();
            var window = new MainWindow(main)
            { Width = 1280, Height = 800, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
            try
            {
                window.Show();
                await main.InitializeAsync();
                Assert.Equal(0, live.Calls); // Enabled gate never launches at startup or discovery.
                main.Devices.SelectDeviceCommand.Execute(main.Devices.Devices.Single());
                Assert.Equal(PageKind.ScanMode, main.CurrentPage);
                Assert.True(main.ScanMode.IsExFatLiveScan);
                Assert.True(main.ScanMode.IsStandardSelected);
                Assert.False(main.ScanMode.IsDeepEnabled);
                Assert.Equal(0, live.Calls);
                main.ScanMode.StartScanCommand.Execute(null);
                main.ScanMode.StartScanCommand.Execute(null);
                await Wait(() => live.Progress is not null);
                Assert.Equal(1, live.Calls);
                Assert.False(live.RanOnDispatcher);
                live.Progress!.Report(new(350, 0, 16000, 2, "LiveScan.ExFat.Allocation")
                { ScannerKind = Kind, DirectoriesExamined = 12, DirectoryEntriesExamined = 350, FatEntriesInspected = 700 });
                await Wait(() => main.ScanProgress.FatEntriesInspected == 700);
                Assert.True(main.ScanProgress.IsIndeterminate);
                Assert.True(main.ScanProgress.IsLiveExFatScan);
                Assert.False(main.ScanProgress.IsLiveNtfsScan);
                Assert.Contains("exFAT", main.ScanProgress.LiveScanKind);
                Assert.Equal(localization["Progress.DirectoryEntriesExamined"], main.ScanProgress.PrimaryLiveMetricLabel);
                var resets = 0;
                main.Results.VisibleResults.CollectionChanged += (_, e) => { if (e.Action == NotifyCollectionChangedAction.Reset) resets++; };
                live.Complete(result);
                await Wait(() => main.CurrentPage == PageKind.Results && main.Results.TotalCount == 10000);
                Assert.InRange(resets, 1, 2);
                output.WriteLine($"10,000 DTO ingestion/mapping: {main.Results.LastIngestionDurationMilliseconds:F1} ms (separate from rendering).");
                var row = main.Results.VisibleResults[0];
                row.IsSelected = true;
                main.Results.SelectedItem = row;
                Assert.False(main.Results.CanRecover);
                Assert.Empty(main.Results.SelectedFiles);
                Assert.Null(main.Results.Session);
                Assert.True(row.IsExFatLiveResult);
                Assert.Equal("501 B", row.ValidDataLength);
                Assert.Contains("UTC", row.ExFatTimestamps);
                Assert.False(row.HasSupportedPreview);
                var windows = System.Windows.Application.Current.Windows.Count;
                window.ShowRecoveryDestinationDialog(); // Direct command path must also be inert.
                Assert.Equal(windows, System.Windows.Application.Current.Windows.Count);
                foreach (var language in new[] { "en-US", "vi-VN" })
                    foreach (var theme in new[] { ThemePreference.Dark, ThemePreference.Light })
                    {
                        localization.SetLanguage(language);
                        ThemeManager.Apply(theme);
                        var rendering = Stopwatch.StartNew();
                        window.UpdateLayout();
                        await Dispatcher.Yield(DispatcherPriority.Render);
                        var grid = Visuals<DataGrid>(window).Single();
                        Assert.True(grid.EnableRowVirtualization && grid.EnableColumnVirtualization);
                        Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(grid));
                        foreach (var index in new[] { 0, 2000, 4000, 6000, 8000, 9999, 0 })
                        {
                            grid.ScrollIntoView(main.Results.VisibleResults[index]);
                            grid.UpdateLayout();
                            await Dispatcher.Yield(DispatcherPriority.Render);
                            Assert.NotNull(grid.ItemContainerGenerator.ContainerFromIndex(index));
                        }
                        Assert.InRange(Visuals<DataGridRow>(grid).Count(), 1, 150);
                        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(window);
                        rendering.Stop();
                        output.WriteLine($"{language}/{theme}: layout, seven realized scroll positions and raster render: {rendering.Elapsed.TotalMilliseconds:F1} ms.");
                        var folder = Path.Combine(AppContext.BaseDirectory, "TestResults", "phase8c-ui");
                        Directory.CreateDirectory(folder);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using (var file = File.Create(Path.Combine(folder, $"{language}-{theme}.png"))) encoder.Save(file);
                        var preview = Visuals<ScrollViewer>(window).Single(view => view.Content is StackPanel panel && panel.DataContext == row);
                        preview.ScrollToEnd();
                        preview.UpdateLayout();
                        bitmap.Render(window);
                        encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using (var file = File.Create(Path.Combine(folder, $"{language}-{theme}-metadata.png"))) encoder.Save(file);
                        preview.ScrollToTop();
                        Assert.DoesNotContain("ExFatName.", row.NameConfidence);
                        Assert.DoesNotContain("ExFatTimestamp.", row.ExFatTimestamps);
                        Assert.DoesNotContain("ExFatLayout.", row.ExFatLayout);
                        Assert.Empty(bindings.Errors);
                    }
                var search = Visuals<TextBox>(window).Single(box => box.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "SearchText");
                search.Text = "deleted-10000";
                await Wait(() => main.Results.VisibleCount == 1);
                search.Text = string.Empty;
                await Wait(() => main.Results.VisibleCount == 10000);
                Visuals<ComboBox>(window).Single().SelectedValue = ResultSort.Size;
                Assert.Equal(ResultSort.Size, main.Results.SortBy);
            }
            finally { window.Close(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
        }, TimeSpan.FromSeconds(60));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("remove")]
    [InlineData("extents")]
    [InlineData("navigate")]
    public Task SharedWorkflowIgnoresUnrelatedHotPlugAndDiscardsCanceledOrRemovedResults(string mode) =>
        WpfTestHost.Instance.RunAsync(async () =>
        {
            var source = Device();
            var provider = new MutableDiscovery([source]);
            var live = new ControlledOrchestrator(Guid.NewGuid());
            using var main = CreateMain(provider, live, new DictionaryLocalizationService());
            await main.InitializeAsync();
            main.Devices.SelectDeviceCommand.Execute(main.Devices.Devices.Single());
            main.ScanMode.StartScanCommand.Execute(null);
            await Wait(() => live.Progress is not null);
            provider.Devices = [source with { DisplayName = "Refreshed label" }];
            await main.RefreshDevicesAsync();
            Assert.Equal(0, live.Cancellations);
            Assert.Equal(PageKind.ScanProgress, main.CurrentPage);
            if (mode == "remove") provider.Devices = [];
            if (mode == "extents") provider.Devices = [source with
                { Volumes = [source.Volumes[0] with { Extents = [new(7, 2097152, 1179648)] }] }];
            if (mode is "remove" or "extents") await main.RefreshDevicesAsync();
            else if (mode == "navigate") { main.NavigateCommand.Execute(PageKind.Settings); main.ScanProgress.RequestCancellation(); }
            else main.ScanProgress.RequestCancellation();
            // A completion arriving after accepted cancellation cannot revive the catalog.
            live.Complete(new(live.Session, new(LiveScanTerminalStatus.Completed, LiveScanConsistency.LiveBestEffort,
                0, 0, 0, 0, false, null)
            { ScannerKind = Kind, FileSystem = "exFAT" }, [], []));
            await Wait(() => main.CurrentPage != PageKind.ScanProgress);
            var bytes = main.ScanProgress.BytesScanned;
            live.Progress!.Report(new(9999, 0, 999999, 10, "LiveScan.ExFat.Allocation") { ScannerKind = Kind });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(bytes, main.ScanProgress.BytesScanned);
            Assert.Empty(main.Results.VisibleResults);
            Assert.False(main.Results.CanRecover);
            Assert.InRange(live.Cancellations, 1, 1);
        });

    [Fact]
    public void AllExFatEvidenceEnumsHaveEnglishAndVietnameseText()
    {
        var localization = new DictionaryLocalizationService();
        foreach (var language in new[] { "en-US", "vi-VN" })
        {
            localization.SetLanguage(language);
            var keys = Enum.GetNames<ExFatNameEvidence>().Select(n => "ExFatName." + n)
                .Concat(Enum.GetNames<ExFatAllocationEvidence>().Select(n => "ExFatAllocation." + n))
                .Concat(Enum.GetNames<ExFatTimestampState>().Select(n => "ExFatTimestamp." + n))
                .Concat(Enum.GetNames<ExFatPathState>().Select(n => "ExFatPath." + n))
                .Concat(Enum.GetNames<ExFatLayout>().Select(n => "ExFatLayout." + n));
            foreach (var key in keys) Assert.NotEqual(key, localization[key]);
        }
    }

    private static MainViewModel CreateMain(MutableDiscovery discovery, ControlledOrchestrator live, ILocalizationService localization) =>
        new(discovery, new UnavailableScanService(), new UnavailableRecoveryCatalogService(), new MemorySettings(), localization,
            isDevelopmentMode: false, liveStandardScanEnabled: false, liveOrchestrator: live, liveExFatStandardScanEnabled: true);

    private static async Task Wait(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private static IEnumerable<T> Visuals<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Visuals<T>(child)) yield return descendant;
        }
    }
    private sealed class MutableDiscovery(IReadOnlyList<StorageDevice> devices) : IDeviceDiscoveryService
    {
        public IReadOnlyList<StorageDevice> Devices { get; set; } = devices;
        public Task<IReadOnlyList<StorageDevice>> GetDevicesAsync(CancellationToken token) => Task.FromResult(Devices);
    }
    private sealed class ControlledOrchestrator(Guid session) : ILiveScanUiOrchestrator
    {
        private readonly TaskCompletionSource<LiveScanResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid Session => session;
        public int Calls { get; private set; }
        public int Cancellations { get; private set; }
        public bool RanOnDispatcher { get; private set; }
        public IProgress<LiveScanProgressDto>? Progress { get; private set; }
        public void UpdateDiscoverySnapshot(IReadOnlyList<StorageDevice> devices) { }
        public async Task<LiveScanResult> ScanAsync(StorageDevice source, LiveScanBudgets budgets,
            IProgress<LiveScanProgressDto>? progress, CancellationToken token)
        {
            Calls++;
            RanOnDispatcher = System.Windows.Application.Current.Dispatcher.CheckAccess();
            Progress = progress;
            using var registration = token.Register(() => Cancellations++);
            return await _completion.Task.WaitAsync(token);
        }
        public void Complete(LiveScanResult result) => _completion.TrySetResult(result);
    }
    private sealed class MemorySettings : ISettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken token) => Task.FromResult(new AppSettings(ThemePreference.Dark, "en-US"));
        public Task SaveAsync(AppSettings settings, CancellationToken token) => Task.CompletedTask;
    }
}
