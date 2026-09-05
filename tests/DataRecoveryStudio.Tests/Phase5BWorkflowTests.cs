using System.Diagnostics;
using DataRecoveryStudio.Application;
using DataRecoveryStudio.Core;
using DataRecoveryStudio.Infrastructure;
using Xunit.Abstractions;

namespace DataRecoveryStudio.Tests;

public sealed class Phase5BWorkflowTests
{
    private readonly ITestOutputHelper _output;

    public Phase5BWorkflowTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void FeatureGate_RequiresExactOptInValue()
    {
        Assert.True(LiveStandardScanFeatureGate.IsEnabled(_ => "1"));
        Assert.False(LiveStandardScanFeatureGate.IsEnabled(_ => "true"));
        Assert.False(LiveStandardScanFeatureGate.IsEnabled(_ => " 1 "));
        Assert.False(LiveStandardScanFeatureGate.IsEnabled(_ => null));
    }

    [Theory]
    [InlineData("NTFS", true, ScanCapabilityKind.LiveNtfsStandardScanAvailable, true)]
    [InlineData("NTFS", false, ScanCapabilityKind.LiveStandardScanFeatureDisabled, false)]
    [InlineData("FAT32", true, ScanCapabilityKind.LiveFat32StandardScanFeatureDisabled, false)]
    [InlineData("exFAT", true, ScanCapabilityKind.LiveExFatStandardScanFeatureDisabled, false)]
    [InlineData("ReFS", true, ScanCapabilityKind.UnsupportedFilesystem, false)]
    public void CapabilityMapping_IsExplicit(string filesystem, bool flag, ScanCapabilityKind expected, bool canStart)
    {
        var capability = ScanCapabilityEvaluator.Evaluate(CreateDevice(filesystem), false, flag);
        Assert.Equal(expected, capability.Kind);
        Assert.Equal(canStart, capability.CanStartStandard);
        Assert.False(capability.CanStartDeep);
    }

    [Fact]
    public void CapabilityMapping_DoesNotCallUnsupportedDeviceDisconnected()
    {
        var source = CreateDevice() with
        {
            Volumes = [CreateDevice().Volumes[0] with { IsSupported = false, UnsupportedReasonKey = "Device.Unsupported.Inaccessible" }],
        };
        var capability = ScanCapabilityEvaluator.Evaluate(source, false, true);
        Assert.Equal(ScanCapabilityKind.UnsupportedDeviceType, capability.Kind);
        Assert.NotEqual(ScanCapabilityKind.Disconnected, capability.Kind);
    }

    [Fact]
    public void CapabilityMapping_RequiresMappedPhysicalDisk()
    {
        var capability = ScanCapabilityEvaluator.Evaluate(CreateDevice() with { PhysicalDisks = [] }, false, true);
        Assert.Equal(ScanCapabilityKind.UnmappedPhysicalIdentity, capability.Kind);
        Assert.False(capability.CanStartStandard);
    }

    [Fact]
    public void StateMachine_EnforcesTransitionTableAndTerminality()
    {
        var machine = new LiveScanStateMachine();
        Assert.False(machine.TryTransition(LiveScanUiState.Scanning));
        Assert.True(machine.TryTransition(LiveScanUiState.ValidatingSelection));
        Assert.True(machine.TryTransition(LiveScanUiState.RequestingPermission));
        Assert.True(machine.TryTransition(LiveScanUiState.LaunchingWorker));
        Assert.True(machine.TryTransition(LiveScanUiState.ConnectingSecureChannel));
        Assert.True(machine.TryTransition(LiveScanUiState.Scanning));
        Assert.True(machine.TryTransition(LiveScanUiState.ReceivingResults));
        Assert.True(machine.TryTransition(LiveScanUiState.CompletedPartial));
        Assert.True(machine.IsTerminal);
        Assert.False(machine.TryTransition(LiveScanUiState.Scanning));
    }

    [Fact]
    public async Task ProductionStandard_RoutesOnceToLiveBoundary()
    {
        var source = CreateDevice();
        var live = new ControllableLiveOrchestrator();
        using var main = CreateMain(source, live, enabled: true);
        await main.InitializeAsync();
        main.Devices.SelectDeviceCommand.Execute(main.Devices.Devices.Single());

        main.ScanMode.StartScanCommand.Execute(null);
        main.ScanMode.StartScanCommand.Execute(null);

        await WaitUntilAsync(() => live.CallCount == 1);
        Assert.Equal(PageKind.ScanProgress, main.CurrentPage);
        live.Complete(CreateResult(live.SessionId, LiveScanTerminalStatus.Completed));
        await WaitUntilAsync(() => main.CurrentPage == PageKind.Results);
        Assert.True(main.Results.IsLiveSession);
    }

    [Theory]
    [InlineData("FAT32")]
    [InlineData("exFAT")]
    public async Task NonNtfs_NeverLaunchesWorker(string filesystem)
    {
        var live = new ControllableLiveOrchestrator();
        using var main = CreateMain(CreateDevice(filesystem), live, enabled: true);
        await main.InitializeAsync();
        main.Devices.SelectDeviceCommand.Execute(main.Devices.Devices.Single());
        Assert.False(main.ScanMode.StartScanCommand.CanExecute(null));
        main.ScanMode.StartScanCommand.Execute(null);
        Assert.Equal(0, live.CallCount);
    }

    [Fact]
    public async Task FeatureDisabled_HasNoLiveOrMockFallback()
    {
        var live = new ControllableLiveOrchestrator();
        using var main = CreateMain(CreateDevice(), live, enabled: false);
        await main.InitializeAsync();
        main.Devices.SelectDeviceCommand.Execute(main.Devices.Devices.Single());
        Assert.Equal(ScanCapabilityKind.LiveStandardScanFeatureDisabled, main.ScanMode.Capability!.Kind);
        Assert.False(main.ScanMode.StartScanCommand.CanExecute(null));
        main.ScanMode.StartScanCommand.Execute(null);
        Assert.Equal(0, live.CallCount);
        Assert.Equal(PageKind.ScanMode, main.CurrentPage);
    }

    [Fact]
    public async Task RealDeepScan_IsVisibleButCannotLaunch()
    {
        var live = new ControllableLiveOrchestrator();
        using var main = CreateMain(CreateDevice(), live, enabled: true);
        await main.InitializeAsync();
        main.Devices.SelectDeviceCommand.Execute(main.Devices.Devices.Single());
        main.ScanMode.SelectModeCommand.Execute(ScanModeKind.Deep);
        Assert.False(main.ScanMode.IsDeepSelected);
        Assert.False(main.ScanMode.IsDeepEnabled);
        Assert.Equal(0, live.CallCount);
    }

    [Theory]
    [InlineData(LiveScanClientPhase.RequestingPermission)]
    [InlineData(LiveScanClientPhase.LaunchingWorker)]
    [InlineData(LiveScanClientPhase.ConnectingSecureChannel)]
    [InlineData("Progress.Phase.Metadata")]
    public async Task Cancellation_IsIdempotentAcrossActivePhases(string phase)
    {
        var live = new ControllableLiveOrchestrator(phase);
        LiveScanUiSession? completed = null;
        var viewModel = new ScanProgressViewModel(new UnavailableScanService(), _ => { },
            new DictionaryLocalizationService(), live, session => completed = session);
        var task = viewModel.StartLiveAsync(CreateDevice());
        await WaitUntilAsync(() => live.CallCount == 1);
        viewModel.RequestCancellation();
        viewModel.RequestCancellation();
        await task;
        Assert.Equal(1, live.CancellationCount);
        Assert.Equal(LiveScanUiState.Canceled, viewModel.LiveState);
        Assert.NotNull(completed);
        Assert.Empty(completed!.Result.Candidates);
    }

    [Fact]
    public async Task ActiveNavigation_WaitsForConfirmedCancellation()
    {
        var live = new ControllableLiveOrchestrator();
        using var main = CreateMain(CreateDevice(), live, enabled: true);
        await main.InitializeAsync();
        main.Devices.SelectDeviceCommand.Execute(main.Devices.Devices.Single());
        main.ScanMode.StartScanCommand.Execute(null);
        await WaitUntilAsync(() => live.CallCount == 1);
        var requests = 0;
        main.NavigationCancellationRequested += target => { requests++; Assert.Equal(PageKind.Settings, target); };
        main.Navigate(PageKind.Settings);
        Assert.Equal(1, requests);
        Assert.Equal(PageKind.ScanProgress, main.CurrentPage);
        main.ConfirmNavigationAfterCancellation(PageKind.Settings);
        await WaitUntilAsync(() => main.CurrentPage == PageKind.Settings);
    }

    [Fact]
    public async Task LiveSelection_NeverEnablesRecoveryAndPreviewIsMetadataOnly()
    {
        var sessionId = Guid.NewGuid();
        var candidate = CreateCandidate(sessionId, 1);
        var results = new ResultsViewModel(new UnavailableRecoveryCatalogService(), false, new DictionaryLocalizationService());
        await results.LoadLiveAsync(new LiveScanUiSession(CreateDevice(),
            CreateResult(sessionId, LiveScanTerminalStatus.Completed, [candidate]), DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2)));
        results.VisibleResults[0].IsSelected = true;
        results.SelectedItem = results.VisibleResults[0];
        Assert.False(results.CanRecover);
        Assert.Empty(results.SelectedFiles);
        Assert.True(results.SelectedItem.HasUnsupportedPreview);
        Assert.Contains("not enabled", results.SelectedItem.PreviewDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CandidateCatalog_MapsTenThousandWithOneBulkResetAndDeterministicOrder()
    {
        var sessionId = Guid.NewGuid();
        var candidates = Enumerable.Range(0, 10_000).Select(index => CreateCandidate(sessionId, 10_000 - index)).ToArray();
        var results = new ResultsViewModel(new UnavailableRecoveryCatalogService(), false, new DictionaryLocalizationService());
        var resets = 0;
        results.VisibleResults.CollectionChanged += (_, args) =>
        {
            if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++;
        };
        var stopwatch = Stopwatch.StartNew();
        await results.LoadLiveAsync(new LiveScanUiSession(CreateDevice(),
            CreateResult(sessionId, LiveScanTerminalStatus.Partial, candidates), DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5)));
        stopwatch.Stop();
        _output.WriteLine("10,000-candidate application ingestion: {0:F2} ms; end-to-end test operation: {1:F2} ms.",
            results.LastIngestionDurationMilliseconds, stopwatch.Elapsed.TotalMilliseconds);
        Assert.Equal(10_000, results.TotalCount);
        Assert.Equal(1, resets);
        Assert.True(results.LastIngestionDurationMilliseconds >= 0);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal(results.VisibleResults.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).Select(item => item.Name),
            results.VisibleResults.Select(item => item.Name));
    }

    [Fact]
    public async Task MalformedOrWrongSessionCandidate_IsRejected()
    {
        var sessionId = Guid.NewGuid();
        var results = new ResultsViewModel(new UnavailableRecoveryCatalogService());
        await results.LoadLiveAsync(new LiveScanUiSession(CreateDevice(),
            CreateResult(sessionId, LiveScanTerminalStatus.Completed, [CreateCandidate(Guid.NewGuid(), 1)]),
            DateTimeOffset.UtcNow, TimeSpan.Zero));
        Assert.True(results.IsFailed);
        Assert.Empty(results.VisibleResults);
    }

    [Fact]
    public async Task ChangedDuringScan_IsPreservedOnlyAsExplicitPartial()
    {
        var sessionId = Guid.NewGuid();
        var results = new ResultsViewModel(new UnavailableRecoveryCatalogService(), false, new DictionaryLocalizationService());
        await results.LoadLiveAsync(new LiveScanUiSession(CreateDevice(),
            CreateResult(sessionId, LiveScanTerminalStatus.ChangedDuringScan, [CreateCandidate(sessionId, 1)]),
            DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1)));
        Assert.True(results.FileSystemChanged);
        Assert.True(results.IsPartialLiveResult);
        Assert.Contains("Changed", results.ScanSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(LiveScanTerminalStatus.PermissionDeclined)]
    [InlineData(LiveScanTerminalStatus.AccessDenied)]
    [InlineData(LiveScanTerminalStatus.WorkerMissing)]
    [InlineData(LiveScanTerminalStatus.WorkerVersionMismatch)]
    [InlineData(LiveScanTerminalStatus.SecureConnectionFailed)]
    [InlineData(LiveScanTerminalStatus.WorkerCrashed)]
    [InlineData(LiveScanTerminalStatus.TimedOut)]
    public async Task SanitizedTerminalFailures_ReturnToScanOptions(LiveScanTerminalStatus status)
    {
        var live = new ControllableLiveOrchestrator();
        live.Complete(CreateResult(live.SessionId, status));
        using var main = CreateMain(CreateDevice(), live, enabled: true);
        await main.InitializeAsync();
        main.Devices.SelectDeviceCommand.Execute(main.Devices.Devices.Single());
        main.ScanMode.StartScanCommand.Execute(null);
        await WaitUntilAsync(() => main.CurrentPage == PageKind.ScanMode && main.ScanMode.HasOutcomeMessage);
        Assert.False(main.Results.IsLiveSession);
        Assert.DoesNotContain("\\\\?\\", main.ScanMode.OutcomeMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcceptedCancellationWinsCompletionRace()
    {
        var live = new ControllableLiveOrchestrator();
        LiveScanUiSession? completed = null;
        var progress = new ScanProgressViewModel(new UnavailableScanService(), _ => { },
            new DictionaryLocalizationService(), live, session => completed = session);
        var running = progress.StartLiveAsync(CreateDevice());
        await WaitUntilAsync(() => live.CallCount == 1);
        progress.RequestCancellation();
        live.Complete(CreateResult(live.SessionId, LiveScanTerminalStatus.Completed,
            [CreateCandidate(live.SessionId, 1)]));
        await running;
        Assert.Equal(LiveScanUiState.Canceled, progress.LiveState);
        Assert.NotNull(completed);
        Assert.Empty(completed!.Result.Candidates);
    }

    [Fact]
    public async Task UnrelatedHotPlug_DoesNotCancelActiveSource()
    {
        var source = CreateDevice();
        var other = CreateOtherDevice();
        var provider = new MutableDiscovery([source, other]);
        var live = new ControllableLiveOrchestrator();
        using var main = CreateMain(provider, live, true);
        await main.InitializeAsync();
        main.Devices.SelectDeviceCommand.Execute(main.Devices.Devices.Single(card => card.Device.Id == source.Id));
        main.ScanMode.StartScanCommand.Execute(null);
        await WaitUntilAsync(() => live.CallCount == 1);
        provider.Devices = [source, other with { DisplayName = "Unrelated device refreshed" }];
        await main.RefreshDevicesAsync();
        Assert.Equal(PageKind.ScanProgress, main.CurrentPage);
        Assert.Equal(0, live.CancellationCount);
        main.ScanProgress.RequestCancellation();
        await WaitUntilAsync(() => main.CurrentPage == PageKind.ScanMode);
    }

    [Fact]
    public async Task SourceRemoval_CancelsOnceAndDiscardsResults()
    {
        var source = CreateDevice();
        var other = CreateOtherDevice();
        var provider = new MutableDiscovery([source, other]);
        var live = new ControllableLiveOrchestrator();
        using var main = CreateMain(provider, live, true);
        await main.InitializeAsync();
        main.Devices.SelectDeviceCommand.Execute(main.Devices.Devices.Single(card => card.Device.Id == source.Id));
        main.ScanMode.StartScanCommand.Execute(null);
        await WaitUntilAsync(() => live.CallCount == 1);
        provider.Devices = [other];
        await main.RefreshDevicesAsync();
        await WaitUntilAsync(() => main.CurrentPage == PageKind.Devices);
        Assert.Equal(1, live.CancellationCount);
        Assert.Null(main.ScanMode.Source);
        Assert.False(main.Results.IsLiveSession);
    }

    private static MainViewModel CreateMain(StorageDevice source, ILiveScanUiOrchestrator live, bool enabled) => new(
        new MutableDiscovery([source]),
        new UnavailableScanService(),
        new UnavailableRecoveryCatalogService(),
        new JsonSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json")),
        new DictionaryLocalizationService(),
        false,
        enabled,
        live);

    private static MainViewModel CreateMain(MutableDiscovery discovery, ILiveScanUiOrchestrator live, bool enabled) => new(
        discovery,
        new UnavailableScanService(),
        new UnavailableRecoveryCatalogService(),
        new JsonSettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json")),
        new DictionaryLocalizationService(),
        false,
        enabled,
        live);

    private static StorageDevice CreateDevice(string filesystem = "NTFS")
    {
        var physicalId = new PhysicalDeviceId("test:physical:phase5b");
        var volumeGuid = $"\\\\?\\Volume{{{Guid.Parse("11111111-2222-3333-4444-555555555555"):D}}}";
        var volume = new Volume(volumeGuid, "T:\\", "Controlled", filesystem, 1_000_000_000, 100_000_000, physicalId)
        {
            VolumeGuidPath = volumeGuid,
            MountPaths = ["T:\\"],
            PhysicalDeviceIds = new HashSet<PhysicalDeviceId> { physicalId },
        };
        return new StorageDevice(physicalId, "Controlled NTFS volume", "Test model", StorageDeviceType.ExternalDrive,
            DeviceConnectionStatus.Online, [volume])
        {
            PhysicalDisks = [new PhysicalDisk(7, physicalId, PhysicalIdentityConfidence.High, "Test model", "Test", StorageBusType.Usb, true, 1_000_000_000, StorageDeviceType.ExternalDrive)],
        };
    }

    private static StorageDevice CreateOtherDevice()
    {
        var physicalId = new PhysicalDeviceId("test:physical:unrelated");
        const string volumeGuid = "\\\\?\\Volume{99999999-2222-3333-4444-555555555555}";
        var volume = new Volume(volumeGuid, "U:\\", "Unrelated", "NTFS", 2_000_000_000, 200_000_000, physicalId)
        {
            VolumeGuidPath = volumeGuid,
            MountPaths = ["U:\\"],
            PhysicalDeviceIds = new HashSet<PhysicalDeviceId> { physicalId },
        };
        return new StorageDevice(physicalId, "Unrelated device", "Other model", StorageDeviceType.ExternalDrive,
            DeviceConnectionStatus.Online, [volume])
        {
            PhysicalDisks = [new PhysicalDisk(8, physicalId, PhysicalIdentityConfidence.High, "Other model", "Test", StorageBusType.Usb, true, 2_000_000_000, StorageDeviceType.ExternalDrive)],
        };
    }

    private static LiveScanCandidateDto CreateCandidate(Guid sessionId, int index) => new(
        GuidUtility(index), sessionId, index, 1, $"deleted-{index:00000}.dat", $"Folder\\deleted-{index:00000}.dat",
        index * 100L, FileCategory.Unknown, true, false, CandidatePathState.Complete,
        CandidateRecoverability.MetadataOnly, [], [])
    { ScannerKind = LiveScanScannerKind.NtfsStandardMetadata, FileSystem = "NTFS" };

    private static Guid GuidUtility(int value)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(value).CopyTo(bytes, 0);
        return new Guid(bytes);
    }

    private static LiveScanResult CreateResult(Guid sessionId, LiveScanTerminalStatus status,
        IReadOnlyList<LiveScanCandidateDto>? candidates = null)
    {
        candidates ??= [];
        var partial = status is LiveScanTerminalStatus.Partial or LiveScanTerminalStatus.ChangedDuringScan;
        var consistency = status == LiveScanTerminalStatus.ChangedDuringScan
            ? LiveScanConsistency.ChangedDuringScan : partial ? LiveScanConsistency.Partial : LiveScanConsistency.LiveBestEffort;
        return new(sessionId, new LiveScanTerminalResultDto(status, consistency, candidates.Count, 0, 12_345, 4_096, partial, partial ? "Budget" : null)
        { ScannerKind = LiveScanScannerKind.NtfsStandardMetadata, FileSystem = "NTFS" }, candidates, []);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Test condition was not reached.");
            await Task.Delay(10);
        }
    }

    private sealed class MutableDiscovery(IReadOnlyList<StorageDevice> devices) : IDeviceDiscoveryService
    {
        public IReadOnlyList<StorageDevice> Devices { get; set; } = devices;
        public Task<IReadOnlyList<StorageDevice>> GetDevicesAsync(CancellationToken cancellationToken) => Task.FromResult(Devices);
    }

    private sealed class ControllableLiveOrchestrator(string? phase = null) : ILiveScanUiOrchestrator
    {
        private readonly TaskCompletionSource<LiveScanResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }
        public int CancellationCount { get; private set; }
        public Guid SessionId { get; } = Guid.NewGuid();
        public void UpdateDiscoverySnapshot(IReadOnlyList<StorageDevice> devices) { }
        public async Task<LiveScanResult> ScanAsync(StorageDevice source, LiveScanBudgets budgets,
            IProgress<LiveScanProgressDto>? progress, CancellationToken cancellationToken)
        {
            CallCount++;
            progress?.Report(new LiveScanProgressDto(0, 0, 0, 0, phase ?? LiveScanClientPhase.RequestingPermission)
            { ScannerKind = LiveScanScannerKind.NtfsStandardMetadata });
            try
            {
                return await _completion.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationCount++;
                return CreateResult(SessionId, LiveScanTerminalStatus.Canceled);
            }
        }

        public void Complete(LiveScanResult result) => _completion.TrySetResult(result);
    }
}
