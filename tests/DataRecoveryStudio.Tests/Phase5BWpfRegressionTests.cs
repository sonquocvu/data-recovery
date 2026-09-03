using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DataRecoveryStudio.Application;
using DataRecoveryStudio.App;
using DataRecoveryStudio.Core;
using DataRecoveryStudio.Infrastructure;

namespace DataRecoveryStudio.Tests;

public sealed class Phase5BWpfRegressionTests
{
    [Fact]
    public Task ProductionViews_RenderLiveProgressAndTenThousandMetadataResultsWithoutBindingErrors() =>
        WpfTestHost.Instance.RunAsync(async () =>
        {
            var source = CreateDevice();
            var orchestration = new RenderingLiveOrchestrator();
            var localization = new DictionaryLocalizationService();
            using var main = new MainViewModel(
                new SingleDeviceDiscovery(source),
                new UnavailableScanService(),
                new UnavailableRecoveryCatalogService(),
                new MemorySettingsStore(),
                localization,
                false,
                true,
                orchestration);
            using var bindings = new WpfBindingErrorScope();
            var window = new MainWindow(main)
            {
                Width = 1080,
                Height = 680,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20_000,
                Top = -20_000,
                ShowInTaskbar = false,
                ShowActivated = false,
            };
            try
            {
                window.Show();
                await main.InitializeAsync();
                main.Devices.SelectDeviceCommand.Execute(main.Devices.Devices.Single());
                await RenderAsync(window);
                Assert.True(main.ScanMode.IsStandardEnabled);
                Assert.False(main.ScanMode.IsDeepEnabled);

                main.ScanMode.StartScanCommand.Execute(null);
                await WaitUntilAsync(() => orchestration.Progress is not null);
                await WaitUntilAsync(() => main.ScanProgress.LiveState == LiveScanUiState.RequestingPermission);
                orchestration.Progress!.Report(NtfsProgress(LiveScanClientPhase.LaunchingWorker));
                await WaitUntilAsync(() => main.ScanProgress.LiveState == LiveScanUiState.LaunchingWorker);
                orchestration.Progress!.Report(NtfsProgress(LiveScanClientPhase.ConnectingSecureChannel));
                await WaitUntilAsync(() => main.ScanProgress.LiveState == LiveScanUiState.ConnectingSecureChannel);
                await RenderAsync(window);
                Assert.Contains(FindVisualChildren<ProgressBar>(window), bar => bar.IsIndeterminate);

                orchestration.Progress.Report(new LiveScanProgressDto(5_000, 10_000, 2_048, 120, "Progress.Phase.Records")
                { ScannerKind = LiveScanScannerKind.NtfsStandardMetadata });
                await WaitUntilAsync(() => main.ScanProgress.LiveState == LiveScanUiState.Scanning);
                await RenderAsync(window);
                Assert.Contains(FindVisualChildren<ProgressBar>(window), bar => !bar.IsIndeterminate && bar.Value == 50);

                var candidates = Enumerable.Range(1, 10_000).Select(index => CreateCandidate(orchestration.SessionId, index)).ToArray();
                orchestration.Complete(new LiveScanResult(
                    orchestration.SessionId,
                    new LiveScanTerminalResultDto(LiveScanTerminalStatus.Partial, LiveScanConsistency.Partial,
                        candidates.Length, 0, 10_000, 2_048, true, "CandidateBudget")
                    { ScannerKind = LiveScanScannerKind.NtfsStandardMetadata, FileSystem = "NTFS" },
                    candidates,
                    []));
                await WaitUntilAsync(() => main.CurrentPage == PageKind.Results && main.Results.TotalCount == 10_000);
                await RenderAsync(window);
                var dataGrid = Assert.Single(FindVisualChildren<DataGrid>(window));
                Assert.True(dataGrid.EnableRowVirtualization);
                main.Results.VisibleResults[0].IsSelected = true;
                main.Results.SelectedItem = main.Results.VisibleResults[0];
                await RenderAsync(window);
                Assert.False(main.Results.CanRecover);
                Assert.True(main.Results.SelectedItem.HasUnsupportedPreview);

                ThemeManager.Apply(ThemePreference.Light);
                localization.SetLanguage("vi-VN");
                await RenderAsync(window);
                ThemeManager.Apply(ThemePreference.Dark);
                localization.SetLanguage("en-US");
                await RenderAsync(window);

                Assert.Empty(bindings.Errors);
            }
            finally
            {
                if (main.ScanProgress.IsActive)
                {
                    main.ScanProgress.RequestCancellation();
                    orchestration.Complete(new LiveScanResult(
                        orchestration.SessionId,
                        new LiveScanTerminalResultDto(LiveScanTerminalStatus.Canceled, LiveScanConsistency.Partial, 0, 0, 0, 0, true, "TestCleanup")
                        { ScannerKind = LiveScanScannerKind.NtfsStandardMetadata, FileSystem = "NTFS" },
                        [],
                        []));
                    await WaitUntilAsync(() => !main.ScanProgress.IsActive);
                }
                window.Close();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            }
        }, TimeSpan.FromSeconds(60));

    private static async Task RenderAsync(Window window)
    {
        window.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.Render);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("WPF state was not reached.");
            await Task.Delay(10);
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private static StorageDevice CreateDevice()
    {
        var physicalId = new PhysicalDeviceId("test:wpf:physical");
        const string volumeGuid = "\\\\?\\Volume{77777777-2222-3333-4444-555555555555}";
        var volume = new Volume(volumeGuid, "T:\\", "Controlled live preview", "NTFS", 10_000_000_000, 1_000_000_000, physicalId)
        {
            VolumeGuidPath = volumeGuid,
            MountPaths = ["T:\\"],
            PhysicalDeviceIds = new HashSet<PhysicalDeviceId> { physicalId },
        };
        return new StorageDevice(physicalId, "Controlled production NTFS volume", "Regression model", StorageDeviceType.ExternalDrive,
            DeviceConnectionStatus.Online, [volume])
        {
            PhysicalDisks = [new PhysicalDisk(9, physicalId, PhysicalIdentityConfidence.High, "Regression model", "Test", StorageBusType.Usb, true, 10_000_000_000, StorageDeviceType.ExternalDrive)],
        };
    }

    private static LiveScanCandidateDto CreateCandidate(Guid sessionId, int index)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(index).CopyTo(bytes, 0);
        return new LiveScanCandidateDto(new Guid(bytes), sessionId, index, 1, $"candidate-{index:00000}.dat",
            $"Recovered\\candidate-{index:00000}.dat", index * 64L, FileCategory.Unknown, true, false,
            CandidatePathState.Complete, CandidateRecoverability.MetadataOnly, [], [])
        { ScannerKind = LiveScanScannerKind.NtfsStandardMetadata, FileSystem = "NTFS" };
    }

    private sealed class RenderingLiveOrchestrator : ILiveScanUiOrchestrator
    {
        private readonly TaskCompletionSource<LiveScanResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid SessionId { get; } = Guid.NewGuid();
        public IProgress<LiveScanProgressDto>? Progress { get; private set; }
        public void UpdateDiscoverySnapshot(IReadOnlyList<StorageDevice> devices) { }
        public Task<LiveScanResult> ScanAsync(StorageDevice source, LiveScanBudgets budgets,
            IProgress<LiveScanProgressDto>? progress, CancellationToken cancellationToken)
        {
            Progress = progress;
            progress?.Report(NtfsProgress(LiveScanClientPhase.RequestingPermission));
            return _completion.Task.WaitAsync(cancellationToken);
        }

        public void Complete(LiveScanResult result) => _completion.TrySetResult(result);
    }

    private static LiveScanProgressDto NtfsProgress(string phase) =>
        new(0, 0, 0, 0, phase) { ScannerKind = LiveScanScannerKind.NtfsStandardMetadata };

    private sealed class SingleDeviceDiscovery(StorageDevice source) : IDeviceDiscoveryService
    {
        public Task<IReadOnlyList<StorageDevice>> GetDevicesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StorageDevice>>([source]);
    }

    private sealed class MemorySettingsStore : ISettingsStore
    {
        private AppSettings _settings = new(ThemePreference.Dark, "en-US");
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(_settings);
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
        {
            _settings = settings;
            return Task.CompletedTask;
        }
    }
}
