using System.Text.Json;
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

public sealed class Phase7CLiveFat32Tests
{
    private const string VolumePath = "\\\\?\\Volume{77777777-2222-3333-4444-555555555555}";

    [Fact]
    public void Fat32FeatureGateRequiresSeparateExactOptIn()
    {
        Assert.True(LiveFat32StandardScanFeatureGate.IsEnabled(_ => "1"));
        Assert.False(LiveFat32StandardScanFeatureGate.IsEnabled(_ => "true"));
        Assert.False(LiveFat32StandardScanFeatureGate.IsEnabled(_ => " 1 "));
        Assert.False(LiveFat32StandardScanFeatureGate.IsEnabled(_ => null));
        Assert.NotEqual(LiveStandardScanFeatureGate.EnvironmentVariable, LiveFat32StandardScanFeatureGate.EnvironmentVariable);

        Assert.Equal(ScanCapabilityKind.LiveFat32StandardScanFeatureDisabled,
            ScanCapabilityEvaluator.Evaluate(CreateDevice(), false, true, false).Kind);
        Assert.Equal(ScanCapabilityKind.LiveFat32StandardScanAvailable,
            ScanCapabilityEvaluator.Evaluate(CreateDevice(), false, false, true).Kind);
    }

    [Fact]
    public void Fat32GrantIsScannerBoundOneTimeAndIndependentlyGated()
    {
        var source = CreateDevice();
        var disabled = new LiveScanTargetGrantAuthority(ntfsEnabled: true, fat32Enabled: false);
        disabled.UpdateDiscoverySnapshot([source]);
        Assert.Equal(LiveScanAuthorizationError.FeatureDisabled,
            Assert.Throws<LiveScanAuthorizationException>(() => disabled.IssueGrant(source)).Error);

        var authority = new LiveScanTargetGrantAuthority(ntfsEnabled: false, fat32Enabled: true);
        authority.UpdateDiscoverySnapshot([source]);
        var issued = authority.IssueGrant(source);
        Assert.Equal(LiveScanScannerKind.Fat32StandardMetadata, issued.ScannerKind);
        Assert.Equal("FAT32", issued.FileSystem);
        Assert.Equal(issued, authority.ConsumeGrant(issued.GrantId));
        Assert.Equal(LiveScanAuthorizationError.GrantAlreadyConsumed,
            Assert.Throws<LiveScanAuthorizationException>(() => authority.ConsumeGrant(issued.GrantId)).Error);
    }

    [Fact]
    public void Fat32DisabledCapabilityCannotStartOrReachWorkerBoundary()
    {
        var starts = 0;
        var viewModel = new ScanModeViewModel(() => { }, (_, _) => starts++, isDevelopmentMode: false,
            liveStandardScanEnabled: true, liveFat32StandardScanEnabled: false)
        {
            Source = CreateDevice(),
        };

        Assert.False(viewModel.IsStandardEnabled);
        Assert.False(viewModel.StartScanCommand.CanExecute(null));
        viewModel.StartScanCommand.Execute(null);
        Assert.Equal(0, starts);
    }

    [Fact]
    public void Fat32DiscoveryRefreshInvalidatesOutstandingGrant()
    {
        var source = CreateDevice();
        var authority = new LiveScanTargetGrantAuthority(ntfsEnabled: false, fat32Enabled: true);
        authority.UpdateDiscoverySnapshot([source]);
        var grant = authority.IssueGrant(source);
        authority.UpdateDiscoverySnapshot([source]);

        Assert.Equal(LiveScanAuthorizationError.StaleDiscoveryGeneration,
            Assert.Throws<LiveScanAuthorizationException>(() => authority.ConsumeGrant(grant.GrantId)).Error);
    }

    [Fact]
    public void WorkerArgumentsAndMessagesBindTheScannerKind()
    {
        var session = Guid.NewGuid();
        var nonce = new string('A', 64);
        var parsed = ScanWorkerArguments.Parse([
            "--pipe", $"DataRecoveryStudio.LiveScan.{session:N}.ABC",
            "--session", session.ToString("D"),
            "--nonce", nonce,
            "--protocol", LiveScanProtocol.Version.ToString(),
            "--scanner", "Fat32StandardMetadata"]);
        Assert.Equal(LiveScanScannerKind.Fat32StandardMetadata, parsed.ScannerKind);
        Assert.Throws<ArgumentException>(() => ScanWorkerArguments.Parse([
            "--pipe", $"DataRecoveryStudio.LiveScan.{session:N}.ABC",
            "--session", session.ToString("D"),
            "--nonce", nonce,
            "--protocol", LiveScanProtocol.Version.ToString(),
            "--scanner", "UnknownScanner"]));
    }

    [Fact]
    public async Task AccumulatorRejectsCrossFilesystemAndHostileFat32Candidates()
    {
        var session = Guid.NewGuid();
        var accumulator = new LiveScanResultAccumulator(session, null, LiveScanScannerKind.Fat32StandardMetadata);
        var ntfsCandidate = CreateFatCandidate(session, 1) with { ScannerKind = LiveScanScannerKind.NtfsStandardMetadata, FileSystem = "NTFS" };
        await Assert.ThrowsAsync<LiveScanProtocolException>(async () => accumulator.Accept(await Envelope(
            session, LiveScanMessageKind.CandidateBatch, new LiveScanCandidateBatchDto(0, [ntfsCandidate]))));

        accumulator = new LiveScanResultAccumulator(session, null, LiveScanScannerKind.Fat32StandardMetadata);
        var hostile = CreateFatCandidate(session, 2) with { MftRecordNumber = 9 };
        await Assert.ThrowsAsync<LiveScanProtocolException>(async () => accumulator.Accept(await Envelope(
            session, LiveScanMessageKind.CandidateBatch, new LiveScanCandidateBatchDto(0, [hostile]))));

        accumulator = new LiveScanResultAccumulator(session, null, LiveScanScannerKind.Fat32StandardMetadata);
        var stale = CreateFatCandidate(Guid.NewGuid(), 3);
        await Assert.ThrowsAsync<LiveScanProtocolException>(async () => accumulator.Accept(await Envelope(
            session, LiveScanMessageKind.CandidateBatch, new LiveScanCandidateBatchDto(0, [stale]))));
    }

    [Fact]
    public async Task ExecutorUsesExistingFat32ScannerAndNormalizesMetadataOnly()
    {
        var bytes = new Fat32TestFixtureBuilder().Build();
        var source = new MutableSource(bytes);
        var scanner = new RecordingFat32Scanner();
        var executor = new LiveScanExecutor(new AcceptingValidator(), new SingleSourceFactory(source), new UnusedNtfsScanner(), scanner);
        var progress = new ProgressCollector<LiveScanProgressDto>();
        var session = Guid.NewGuid();

        var result = await executor.ExecuteAsync(session, CreateGrant(bytes.Length), new LiveScanBudgets(MaximumCandidates: 8), progress, CancellationToken.None);

        Assert.Equal(1, scanner.Calls);
        Assert.Equal(Fat32ScannerVersions.MetadataPhase7A, Fat32MetadataScanner.ParserVersion);
        Assert.Equal(LiveScanTerminalStatus.Completed, result.Terminal.Status);
        Assert.Equal(LiveScanConsistency.LiveBestEffort, result.Terminal.Consistency);
        Assert.Equal(LiveScanScannerKind.Fat32StandardMetadata, result.Terminal.ScannerKind);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("FAT32", candidate.FileSystem);
        Assert.Empty(candidate.Streams);
        Assert.Equal(0, candidate.MftRecordNumber);
        Assert.Equal(Fat32NameState.ProbableDeletedLongName, candidate.Fat32NameState);
        Assert.DoesNotContain("FirstCluster", JsonSerializer.Serialize(candidate), StringComparison.Ordinal);
        Assert.DoesNotContain("Provenance", JsonSerializer.Serialize(candidate), StringComparison.Ordinal);
        Assert.Contains(progress.Values, item => item.ScannerKind == LiveScanScannerKind.Fat32StandardMetadata && item.DirectoryEntriesExamined == 4);
    }

    [Fact]
    public async Task LiveFat32RejectsNonFat32GeometryBeforeScannerSelection()
    {
        var scanner = new RecordingFat32Scanner();
        var bytes = new byte[2 * 1024 * 1024];
        var executor = new LiveScanExecutor(new AcceptingValidator(), new SingleSourceFactory(new MutableSource(bytes)), new UnusedNtfsScanner(), scanner);

        await Assert.ThrowsAsync<InvalidDataException>(() => executor.ExecuteAsync(
            Guid.NewGuid(), CreateGrant(bytes.Length), new LiveScanBudgets(), null, CancellationToken.None));

        Assert.Equal(0, scanner.Calls);
    }

    [Fact]
    public async Task ProductionFat32ScannerDoesNotReadDeletedCandidatePayloadClusters()
    {
        var source = new TrackingSource(new Fat32TestFixtureBuilder().Build());
        var executor = new LiveScanExecutor(
            new AcceptingValidator(), new SingleSourceFactory(source), new UnusedNtfsScanner(), new Fat32MetadataScanner());

        var result = await executor.ExecuteAsync(Guid.NewGuid(), CreateGrant(source.Length),
            new LiveScanBudgets(MaximumCandidates: 32, MaximumFatDirectories: 32, MaximumFatDirectoryClusters: 128,
                MaximumFatDirectoryEntries: 2_048, MaximumFatEntriesInspected: 4_096), null, CancellationToken.None);

        var deletedPayloadClusterOffset = checked((long)(Fat32TestFixtureBuilder.FirstDataSector + (10u - 2) * Fat32TestFixtureBuilder.SectorsPerCluster) * Fat32TestFixtureBuilder.BytesPerSector);
        Assert.NotEmpty(result.Candidates);
        Assert.DoesNotContain(deletedPayloadClusterOffset, source.Offsets);
    }

    [Fact]
    public async Task ChangedFat32BootstrapDowngradesConsistencyAndAllocation()
    {
        var bytes = new Fat32TestFixtureBuilder().Build();
        var source = new MutableSource(bytes);
        var scanner = new RecordingFat32Scanner(() => source.Bytes[0] ^= 0x01);
        var executor = new LiveScanExecutor(new AcceptingValidator(), new SingleSourceFactory(source), new UnusedNtfsScanner(), scanner);

        var result = await executor.ExecuteAsync(Guid.NewGuid(), CreateGrant(bytes.Length), new LiveScanBudgets(MaximumCandidates: 8), null, CancellationToken.None);

        Assert.Equal(LiveScanTerminalStatus.ChangedDuringScan, result.Terminal.Status);
        Assert.Equal(LiveScanConsistency.ChangedDuringScan, result.Terminal.Consistency);
        Assert.Equal(Fat32AllocationAssessment.AllocationUnknown, Assert.Single(result.Candidates).Fat32Allocation);
        Assert.Equal(CandidateRecoverability.Unknown, Assert.Single(result.Candidates).Recoverability);
    }

    [Theory]
    [InlineData(Fat32ScanOutcome.Partial, true, LiveScanTerminalStatus.Partial)]
    [InlineData(Fat32ScanOutcome.Canceled, false, LiveScanTerminalStatus.Canceled)]
    public async Task ExecutorMapsFat32LimitsAndCancellation(Fat32ScanOutcome outcome, bool budgetLimited, LiveScanTerminalStatus expected)
    {
        var bytes = new Fat32TestFixtureBuilder().Build();
        var scanner = new RecordingFat32Scanner(outcome: outcome, budgetLimited: budgetLimited);
        var executor = new LiveScanExecutor(new AcceptingValidator(), new SingleSourceFactory(new MutableSource(bytes)), new UnusedNtfsScanner(), scanner);

        var result = await executor.ExecuteAsync(Guid.NewGuid(), CreateGrant(bytes.Length), new LiveScanBudgets(MaximumCandidates: 8), null, CancellationToken.None);

        Assert.Equal(expected, result.Terminal.Status);
        Assert.True(result.Terminal.IsPartial);
    }

    [Fact]
    public async Task ResultsIngestTenThousandFat32CandidatesAndKeepRecoveryDisabled()
    {
        var source = CreateDevice();
        var sessionId = Guid.NewGuid();
        var candidates = Enumerable.Range(1, 10_000).Select(index => CreateFatCandidate(sessionId, index)).ToArray();
        var terminal = new LiveScanTerminalResultDto(
            LiveScanTerminalStatus.Completed, LiveScanConsistency.LiveBestEffort, candidates.Length, 0,
            candidates.Length, 20_000_000, false, null)
        {
            ScannerKind = LiveScanScannerKind.Fat32StandardMetadata,
            FileSystem = "FAT32",
            DirectoriesExamined = 200,
            DirectoryEntriesExamined = 20_000,
            FatEntriesInspected = 30_000,
        };
        var viewModel = new ResultsViewModel(new UnavailableRecoveryCatalogService());

        await viewModel.LoadLiveAsync(new LiveScanUiSession(source, new LiveScanResult(sessionId, terminal, candidates, []), DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2)));

        Assert.Equal(10_000, viewModel.TotalCount);
        Assert.False(viewModel.CanRecover);
        Assert.Empty(viewModel.SelectedFiles);
        Assert.Contains("FAT32", viewModel.ScanSummary, StringComparison.Ordinal);
        Assert.All(viewModel.VisibleResults.Take(10), item => Assert.True(item.IsFat32LiveResult));
    }

    [Fact]
    public Task ProductionWpfUsesSharedFat32FlowWithoutBindingOrDispatcherErrors() =>
        WpfTestHost.Instance.RunAsync(async () =>
        {
            var source = CreateDevice();
            var orchestrator = new Fat32WpfOrchestrator();
            var localization = new DictionaryLocalizationService();
            using var main = new MainViewModel(new SingleDeviceDiscovery(source), new UnavailableScanService(),
                new UnavailableRecoveryCatalogService(), new MemorySettingsStore(), localization,
                isDevelopmentMode: false, liveStandardScanEnabled: false, liveOrchestrator: orchestrator,
                liveFat32StandardScanEnabled: true);
            using var bindings = new WpfBindingErrorScope();
            var window = new MainWindow(main)
            {
                Width = 1080,
                Height = 680,
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
                Assert.True(main.ScanMode.IsFat32LiveScan);
                Assert.True(main.ScanMode.IsStandardSelected);
                Assert.False(main.ScanMode.IsDeepEnabled);
                main.ScanMode.StartScanCommand.Execute(null);
                main.ScanMode.StartScanCommand.Execute(null);
                await WaitUntilAsync(() => orchestrator.Progress is not null);
                Assert.Equal(1, orchestrator.Calls);

                orchestrator.Progress!.Report(new LiveScanProgressDto(0, 0, 512, 0, "Progress.Phase.Fat32.ReadingBootSectors")
                { ScannerKind = LiveScanScannerKind.Fat32StandardMetadata });
                orchestrator.Progress.Report(new LiveScanProgressDto(400, 0, 4096, 20, "Progress.Phase.Fat32.AnalyzingAllocation")
                {
                    ScannerKind = LiveScanScannerKind.Fat32StandardMetadata,
                    DirectoriesExamined = 12,
                    DirectoryEntriesExamined = 400,
                    FatEntriesInspected = 700,
                });
                await WaitUntilAsync(() => main.ScanProgress.FatEntriesInspected == 700);
                Assert.True(main.ScanProgress.IsIndeterminate);

                var candidates = Enumerable.Range(1, 10_000).Select(index => CreateFatCandidate(orchestrator.SessionId, index)).ToArray();
                orchestrator.Complete(new LiveScanResult(orchestrator.SessionId,
                    new LiveScanTerminalResultDto(LiveScanTerminalStatus.Partial, LiveScanConsistency.Partial,
                        candidates.Length, 0, candidates.Length, 4096, true, "MaximumCandidates")
                    {
                        ScannerKind = LiveScanScannerKind.Fat32StandardMetadata,
                        FileSystem = "FAT32",
                        DirectoriesExamined = 12,
                        DirectoryEntriesExamined = 400,
                        FatEntriesInspected = 700,
                    }, candidates, []));
                await WaitUntilAsync(() => main.CurrentPage == PageKind.Results && main.Results.TotalCount == 10_000);
                window.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Contains(FindVisualChildren<DataGrid>(window), grid => grid.EnableRowVirtualization);
                main.Results.VisibleResults[0].IsSelected = true;
                Assert.False(main.Results.CanRecover);
                Assert.Contains("Probable", main.Results.VisibleResults[0].NameConfidence, StringComparison.OrdinalIgnoreCase);
                localization.SetLanguage("vi-VN");
                ThemeManager.Apply(ThemePreference.Light);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                ThemeManager.Apply(ThemePreference.Dark);
                Assert.Empty(bindings.Errors);
            }
            finally
            {
                window.Close();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            }
        }, TimeSpan.FromSeconds(60));

    [Fat32LiveScanIntegrationFact]
    public async Task LiveFat32VolumeIntegrationOptInOnly()
    {
        var requested = Environment.GetEnvironmentVariable("DATA_RECOVERY_STUDIO_LIVE_FAT32_SCAN_VOLUME");
        Assert.False(string.IsNullOrWhiteSpace(requested), "DATA_RECOVERY_STUDIO_LIVE_FAT32_SCAN_VOLUME must identify the controlled FAT32 target.");
        Assert.True(CanonicalVolumeGuidPath.TryParse(requested, out var requestedCanonical),
            "The controlled FAT32 integration target must be an exact canonical volume GUID path; drive letters and physical-drive paths are refused.");
        var devices = await new WindowsStorageDiscoveryService().GetDevicesAsync(CancellationToken.None);
        var selected = devices.FirstOrDefault(device => device.Volumes.Any(volume =>
            CanonicalVolumeGuidPath.TryParse(volume.VolumeGuidPath, out var current) &&
            requestedCanonical.Equals(current, StringComparison.OrdinalIgnoreCase)));
        Assert.NotNull(selected);
        Assert.Equal("FAT32", selected.Volumes.Single().FileSystem, ignoreCase: true);
        var authority = new LiveScanTargetGrantAuthority(ntfsEnabled: false, fat32Enabled: true);
        authority.UpdateDiscoverySnapshot(devices);
        var service = new HeadlessLiveStandardScanService(authority, new NamedPipeLiveScanWorkerClient(AppContext.BaseDirectory));
        var result = await service.ScanAsync(selected, new LiveScanBudgets(
            MaximumBytesRead: 8 * 1024 * 1024, MaximumCandidates: 64, MaximumDiagnostics: 64,
            CandidateBatchSize: 16, MaximumDuration: TimeSpan.FromMinutes(1),
            MaximumFatDirectories: 128, MaximumFatDirectoryClusters: 512,
            MaximumFatDirectoryEntries: 4_096, MaximumFatEntriesInspected: 8_192), null, CancellationToken.None);
        Assert.Contains(result.Terminal.Status, [LiveScanTerminalStatus.Completed, LiveScanTerminalStatus.Partial, LiveScanTerminalStatus.ChangedDuringScan]);
    }

    private static LiveScanCandidateDto CreateFatCandidate(Guid sessionId, int index) => new(
        GuidFrom(index), sessionId, 0, 0, $"deleted-{index:00000}.txt", $"\\Folder\\deleted-{index:00000}.txt",
        index, FileCategory.Document, true, false, CandidatePathState.Complete, CandidateRecoverability.PossiblyRecoverable, [], [])
    {
        ScannerKind = LiveScanScannerKind.Fat32StandardMetadata,
        FileSystem = "FAT32",
        Fat32Kind = Fat32CandidateKind.File,
        Fat32NameState = Fat32NameState.ProbableDeletedLongName,
        Fat32PathState = Fat32PathState.NameUncertain,
        Fat32Allocation = Fat32AllocationAssessment.PossiblyRecoverableContiguous,
        ModifiedAt = DateTimeOffset.UnixEpoch.AddSeconds(index),
        AttributeFlags = 0x20,
    };

    private static Guid GuidFrom(int value)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(value).CopyTo(bytes, 0);
        return new Guid(bytes);
    }

    private static StorageDevice CreateDevice()
    {
        var physical = new PhysicalDeviceId("windows:physical:fat32-test");
        var volume = new Volume(VolumePath, "F:\\", "Controlled FAT32", "FAT32", Fat32TestFixtureBuilder.ImageLength, 4096, physical)
        {
            VolumeGuidPath = VolumePath,
            MountPaths = ["F:\\"],
            PhysicalDeviceIds = new HashSet<PhysicalDeviceId> { physical },
        };
        return new StorageDevice(physical, "Controlled FAT32", "Test", StorageDeviceType.UsbDevice, DeviceConnectionStatus.Online, [volume])
        {
            PhysicalDisks = [new PhysicalDisk(7, physical, PhysicalIdentityConfidence.High, "Test", "Test", StorageBusType.Usb, true, volume.CapacityBytes, StorageDeviceType.UsbDevice)],
        };
    }

    private static LiveScanTargetGrant CreateGrant(long capacity) => new(
        Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1), VolumePath, "F:\\", "FAT32", "fat32-volume-identity",
        ["windows:physical:fat32-test"], [7], capacity, 1, true, true, true, true, new string('A', 64))
    {
        ScannerKind = LiveScanScannerKind.Fat32StandardMetadata,
        CorrelationId = Guid.NewGuid(),
    };

    private static async Task<LiveScanMessageEnvelope> Envelope<T>(Guid session, LiveScanMessageKind kind, T payload)
    {
        await using var stream = new MemoryStream();
        await LiveScanProtocolCodec.WriteAsync(stream, session, kind, payload, CancellationToken.None);
        stream.Position = 0;
        return await LiveScanProtocolCodec.ReadAsync(stream, CancellationToken.None);
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

    private sealed class AcceptingValidator : ILiveScanTargetValidator
    {
        public Task<ValidatedLiveScanTarget> ValidateAsync(LiveScanTargetGrant grant, CancellationToken cancellationToken) =>
            Task.FromResult(new ValidatedLiveScanTarget(grant));
    }

    private sealed class SingleSourceFactory(IReadOnlyRandomAccessSource source) : ILiveVolumeSourceFactory
    {
        public IReadOnlyRandomAccessSource Open(LiveScanTargetGrant grant, long maximumTotalBytes) => source;
    }

    private sealed class TrackingSource(byte[] bytes) : IReadOnlyRandomAccessSource
    {
        private readonly byte[] _bytes = bytes;
        public List<long> Offsets { get; } = [];
        public long Length => _bytes.LongLength;
        public ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Offsets.Add(offset);
            _bytes.AsMemory(checked((int)offset), destination.Length).CopyTo(destination);
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MutableSource(byte[] bytes) : IReadOnlyRandomAccessSource
    {
        public byte[] Bytes { get; } = bytes;
        public long Length => Bytes.LongLength;
        public ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Bytes.AsMemory(checked((int)offset), destination.Length).CopyTo(destination);
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UnusedNtfsScanner : INtfsMetadataScanner
    {
        public Task<StandardScanResult> ScanAsync(IReadOnlyRandomAccessSource source, StandardScanRequest request, IProgress<StandardScanProgress>? progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The NTFS scanner must not run for a FAT32 grant.");
    }

    private sealed class RecordingFat32Scanner(
        Action? callback = null,
        Fat32ScanOutcome outcome = Fat32ScanOutcome.Completed,
        bool budgetLimited = false) : IFat32MetadataScanner
    {
        public int Calls { get; private set; }
        public Task<Fat32ScanResult> ScanAsync(IReadOnlyRandomAccessSource source, Fat32ScanRequest request, IProgress<Fat32ScanProgress>? progress, CancellationToken cancellationToken)
        {
            Calls++;
            progress?.Report(new Fat32ScanProgress(Fat32ScanPhase.TraversingDirectories, 3, 4, 5, 1, 2048, TimeSpan.FromMilliseconds(1), budgetLimited)
            { DirectoriesTraversed = 2 });
            callback?.Invoke();
            var candidate = new Fat32DeletedCandidate(
                "opaque-candidate", "FAT32", Fat32CandidateKind.File, "deleted.txt", "_ELETED.TXT", null,
                "root", "\\deleted.txt", 20, 128, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch, 0x20, Fat32AllocationAssessment.PossiblyRecoverableContiguous,
                Fat32NameState.ProbableDeletedLongName, Fat32PathState.NameUncertain, [], request.ScanSessionId);
            var metrics = new Fat32ScanMetrics(2, 3, 4, 5, 2048, TimeSpan.FromMilliseconds(1));
            return Task.FromResult(new Fat32ScanResult(outcome, null, null, [candidate], [], metrics, budgetLimited, false,
                outcome == Fat32ScanOutcome.Partial ? "MaximumDirectoryEntries" : null));
        }
    }

    private sealed class ProgressCollector<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];
        public void Report(T value) => Values.Add(value);
    }

    private sealed class SingleDeviceDiscovery(StorageDevice source) : IDeviceDiscoveryService
    {
        public Task<IReadOnlyList<StorageDevice>> GetDevicesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StorageDevice>>([source]);
    }

    private sealed class Fat32WpfOrchestrator : ILiveScanUiOrchestrator
    {
        private readonly TaskCompletionSource<LiveScanResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid SessionId { get; } = Guid.NewGuid();
        public int Calls { get; private set; }
        public IProgress<LiveScanProgressDto>? Progress { get; private set; }
        public void UpdateDiscoverySnapshot(IReadOnlyList<StorageDevice> devices) { }
        public Task<LiveScanResult> ScanAsync(StorageDevice source, LiveScanBudgets budgets, IProgress<LiveScanProgressDto>? progress, CancellationToken cancellationToken)
        {
            Calls++;
            Progress = progress;
            progress?.Report(new LiveScanProgressDto(0, 0, 0, 0, LiveScanClientPhase.RequestingPermission)
            { ScannerKind = LiveScanScannerKind.Fat32StandardMetadata });
            return _completion.Task.WaitAsync(cancellationToken);
        }
        public void Complete(LiveScanResult result) => _completion.TrySetResult(result);
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

public sealed class Fat32LiveScanIntegrationFactAttribute : FactAttribute
{
    public const string DefaultSkipReason = "Set DATA_RECOVERY_STUDIO_RUN_LIVE_FAT32_SCAN_INTEGRATION=1 and DATA_RECOVERY_STUDIO_LIVE_FAT32_SCAN_VOLUME to run the elevated read-only live FAT32 metadata scan.";

    public Fat32LiveScanIntegrationFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("DATA_RECOVERY_STUDIO_RUN_LIVE_FAT32_SCAN_INTEGRATION"), "1", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DATA_RECOVERY_STUDIO_LIVE_FAT32_SCAN_VOLUME")))
        {
            Skip = DefaultSkipReason;
        }
    }
}
