using System.Buffers.Binary;
using System.Text.Json;
using System.Windows.Threading;
using DataRecoveryStudio.Application;
using DataRecoveryStudio.App;
using DataRecoveryStudio.Core;
using DataRecoveryStudio.Infrastructure;

namespace DataRecoveryStudio.Tests;

public sealed class Phase7DHardwareValidationTests
{
    private const string VolumePath = "\\\\?\\Volume{7D7D7D7D-2222-3333-4444-555555555555}";
    private const string RawIdentity = "RAW-SERIAL-MUST-NOT-BE-REPORTED";

    [Fact]
    public void HardwareGateRequiresBothExactVariables()
    {
        var values = new Dictionary<string, string?>();
        LiveFat32HardwareValidationOptions Read() => LiveFat32HardwareValidationOptions.FromEnvironment(name => values.GetValueOrDefault(name));

        Assert.False(Read().IsEnabled);
        values[LiveFat32HardwareValidationOptions.RunVariable] = "1";
        Assert.False(Read().IsEnabled);
        values.Clear();
        values[LiveFat32HardwareValidationOptions.TargetVariable] = "R:\\";
        Assert.False(Read().IsEnabled);
        values[LiveFat32HardwareValidationOptions.RunVariable] = "true";
        Assert.False(Read().IsEnabled);
        values[LiveFat32HardwareValidationOptions.RunVariable] = "1";
        Assert.True(Read().IsEnabled);
        Assert.Equal(4, LiveScanProtocol.Version);
        Assert.Equal("8C.1", LiveScanProtocol.WorkerVersion);
    }

    [Fact]
    public async Task WorkerRejectsPreviousProtocolBeforeOpeningAnySource()
    {
        var session = Guid.NewGuid();
        var exit = await new NamedPipeScanWorkerHost(new NeverExecutor()).RunAsync(
            new ScanWorkerArguments(
                $"DataRecoveryStudio.LiveScan.{session:N}.ABC",
                session,
                new string('A', 64),
                LiveScanProtocol.Version - 1,
                LiveScanScannerKind.Fat32StandardMetadata),
            CancellationToken.None);
        Assert.Equal(20, exit);
    }

    [Theory]
    [InlineData(4_000u, 20u)]
    [InlineData(60_000u, 600u)]
    public void ProductionGeometryParserRejectsFat12AndFat16ClusterCounts(uint totalSectors, uint fatSectors)
    {
        var sector = new byte[512];
        sector[0] = 0xEB;
        sector[1] = 0x3C;
        sector[2] = 0x90;
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(11, 2), 512);
        sector[13] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(14, 2), 32);
        sector[16] = 2;
        sector[21] = 0xF8;
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(32, 4), totalSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(36, 4), fatSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(44, 4), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(48, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(50, 2), 6);
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(510, 2), 0xAA55);

        var parsed = Fat32BootSectorParser.Parse(sector, checked((long)totalSectors * 512), 0, false);
        Assert.False(parsed.IsValid);
        Assert.Equal("FAT32_NOT_FAT32", parsed.Code);
    }

    [Theory]
    [InlineData("PhysicalDrive7")]
    [InlineData("\\\\.\\PhysicalDrive7")]
    [InlineData("R:\\folder")]
    [InlineData("not-a-volume")]
    public void ResolverRejectsMalformedAndPhysicalDriveTargets(string target)
    {
        var resolver = new LiveFat32ControlledTargetResolver();
        Assert.Equal(LiveFat32TargetRejection.MalformedTarget,
            Assert.Throws<LiveFat32TargetValidationException>(() => resolver.Resolve(target, [CreateDevice()])).Rejection);
    }

    [Fact]
    public void DriveLetterResolvesToExactlyOneCanonicalDiscoveredFat32Volume()
    {
        var resolved = new LiveFat32ControlledTargetResolver().Resolve("r:", [CreateDevice()], "C:\\Windows", "D:\\Application");

        Assert.Equal(VolumePath, resolved.Volume.VolumeGuidPath);
        Assert.Equal("R:\\", resolved.Sanitized.MountPath);
        Assert.DoesNotContain(RawIdentity, resolved.Sanitized.OpaqueVolumeIdentity, StringComparison.Ordinal);
        Assert.DoesNotContain(RawIdentity, resolved.Sanitized.HashedPhysicalIdentities);
    }

    [Fact]
    public void ResolverRejectsAmbiguousNonFat32SystemAndVirtualTargets()
    {
        var resolver = new LiveFat32ControlledTargetResolver();
        Assert.Equal(LiveFat32TargetRejection.AmbiguousTarget,
            Assert.Throws<LiveFat32TargetValidationException>(() => resolver.Resolve("R:\\", [CreateDevice(), CreateDevice("other")], "C:\\Windows", "D:\\App")).Rejection);

        var ntfs = ReplaceVolume(CreateDevice(), volume => volume with { FileSystem = "NTFS" });
        Assert.Equal(LiveFat32TargetRejection.NotLocalFat32,
            Assert.Throws<LiveFat32TargetValidationException>(() => resolver.Resolve(VolumePath, [ntfs], "C:\\Windows", "D:\\App")).Rejection);

        var system = ReplaceVolume(CreateDevice(), volume => volume with { MountPath = "C:\\", MountPaths = ["C:\\"] });
        Assert.Equal(LiveFat32TargetRejection.SystemVolume,
            Assert.Throws<LiveFat32TargetValidationException>(() => resolver.Resolve(VolumePath, [system], "C:\\Windows", "D:\\App")).Rejection);

        var virtualDevice = CreateDevice() with
        {
            PhysicalDisks = [CreateDevice().PhysicalDisks[0] with { BusType = StorageBusType.Virtual }],
        };
        Assert.Equal(LiveFat32TargetRejection.UnsupportedTarget,
            Assert.Throws<LiveFat32TargetValidationException>(() => resolver.Resolve(VolumePath, [virtualDevice], "C:\\Windows", "D:\\App")).Rejection);
    }

    [Fact]
    public void ProgressMustBeMonotonicAndCancellationIsRequestedOnce()
    {
        using var cancellation = new CancellationTokenSource();
        var progress = new CancelOnFat32ScanStartProgress(cancellation);
        progress.Report(FatProgress(1, 10, 100, 1));
        progress.Report(FatProgress(2, 10, 200, 2));
        Assert.True(progress.IsMonotonic);
        Assert.True(progress.CancellationRequested);
        Assert.True(cancellation.IsCancellationRequested);

        using var later = new CancellationTokenSource();
        var invalid = new CancelOnFat32ScanStartProgress(later);
        invalid.Report(FatProgress(2, 10, 200, 2));
        invalid.Report(FatProgress(1, 10, 100, 1));
        Assert.False(invalid.IsMonotonic);
    }

    [Fact]
    public void AbnormalTerminalSuppressesIncompleteCandidateCatalog()
    {
        var session = Guid.NewGuid();
        var terminal = ValidTerminal(LiveScanTerminalStatus.ChangedDuringScan) with { CandidateCount = 1 };
        var candidate = new LiveScanCandidateDto(
            Guid.NewGuid(), session, 0, 0, "deleted.txt", "\\deleted.txt", 12, FileCategory.Document,
            true, false, CandidatePathState.Complete, CandidateRecoverability.Unknown, [], [])
        {
            ScannerKind = LiveScanScannerKind.Fat32StandardMetadata,
            FileSystem = "FAT32",
            Fat32Kind = Fat32CandidateKind.File,
            Fat32NameState = Fat32NameState.ShortNameWithMissingFirstCharacter,
            Fat32PathState = Fat32PathState.NameUncertain,
            Fat32Allocation = Fat32AllocationAssessment.AllocationUnknown,
        };

        var suppressed = NamedPipeLiveScanWorkerClient.SuppressIncompleteCandidates(new(session, terminal, [candidate], []));
        Assert.Empty(suppressed.Candidates);
        Assert.Equal(0, suppressed.Terminal.CandidateCount);
    }

    [Fact]
    public void ReleaseReadinessNeverTreatsSkippedChangedOrEmptyEvidenceAsPassed()
    {
        Assert.Equal(LiveFat32ValidationOutcome.Skipped, LiveFat32ReleaseReadiness.Evaluate([], [], true));
        Assert.Equal(LiveFat32ValidationOutcome.ChangedDuringScan,
            LiveFat32ReleaseReadiness.Evaluate([Scenario(LiveFat32ValidationOutcome.ChangedDuringScan)], [new("Evidence", true, "Satisfied")], true));
        Assert.Equal(LiveFat32ValidationOutcome.Failed,
            LiveFat32ReleaseReadiness.Evaluate([Scenario(LiveFat32ValidationOutcome.Passed)], [], true));
    }

    [Fact]
    public void ManualRemovalBoundaryOnlyMatchesTheAuthorizedCanonicalTarget()
    {
        var target = CreateDevice();
        var unrelated = CreateDevice("unrelated", "S:\\", "\\\\?\\Volume{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}");
        Assert.False(LiveFat32ManualRemovalBoundary.IsMatchingTargetRemoved(VolumePath, [target, unrelated]));
        Assert.True(LiveFat32ManualRemovalBoundary.IsMatchingTargetRemoved(VolumePath, [unrelated]));
        Assert.Contains("physically remove only", LiveFat32ManualRemovalBoundary.CreatePrompt(
            new("Controlled USB", "R:\\", "FAT32", 1024, "HASH", ["HASH"], [7])), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ManualRemovalMonitorIgnoresUnrelatedHotPlugThenCancelsMatchingScan()
    {
        var target = CreateDevice();
        var unrelated = CreateDevice("unrelated", "S:\\", "\\\\?\\Volume{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}");
        using var scanCancellation = new CancellationTokenSource();
        var prompt = new ValueCollector<string>();
        var removed = await LiveFat32ManualRemovalBoundary.MonitorAndCancelMatchingScanAsync(
            new SnapshotSequenceDiscovery([target, unrelated], [unrelated]),
            VolumePath,
            new("Controlled USB", "R:\\", "FAT32", 1024, "HASH", ["HASH"], [7]),
            scanCancellation,
            prompt,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.True(removed);
        Assert.True(scanCancellation.IsCancellationRequested);
        Assert.Single(prompt.Values);
    }

    [Fact]
    public async Task SyntheticHarnessCanCreateEvidenceButCannotPassProductionHardwareGate()
    {
        var device = CreateDevice();
        var lifecycle = new LiveScanLifecycleCollector();
        var observed = new ValueCollector<LiveFat32SanitizedTarget>();
        var worker = new FakeWorker(lifecycle, grant => new(grant.CorrelationId, ValidTerminal(), [], []));
        var harness = CreateHarness(new SequenceDiscovery(device, device), lifecycle, worker, observed);

        var report = await harness.RunControlledScanAsync(EnabledOptions(), new LiveScanBudgets(), true, CancellationToken.None);

        Assert.Equal(LiveFat32ValidationOutcome.Failed, report.OverallOutcome);
        Assert.Equal(LiveScanProtocol.Version, report.ProtocolVersion);
        Assert.Equal(Fat32ScannerVersions.MetadataPhase7A, report.ScannerVersion);
        Assert.NotNull(Assert.Single(report.Scenarios).Observations);
        Assert.Equal(4321, report.Scenarios[0].Observations!.WorkerProcessId);
        Assert.Contains(report.MandatoryInvariants, invariant => invariant.Code == "ProductionHardwarePath" && !invariant.Passed);
        Assert.Single(observed.Values);
    }

    [Theory]
    [InlineData("capacity")]
    [InlineData("physical")]
    [InlineData("extent")]
    public async Task ChangedDiscoveryIdentityCannotPassReleaseGate(string change)
    {
        var before = CreateDevice();
        var after = change switch
        {
            "capacity" => ReplaceVolume(before, volume => volume with { CapacityBytes = volume.CapacityBytes + 1 }),
            "physical" => ReplaceVolume(before, volume => volume with
            {
                PhysicalDeviceId = new PhysicalDeviceId("OTHER"),
                PhysicalDeviceIds = new HashSet<PhysicalDeviceId> { new("OTHER") },
            }),
            _ => before with { PhysicalDisks = [before.PhysicalDisks[0] with { DiskNumber = 8 }] },
        };
        var lifecycle = new LiveScanLifecycleCollector();
        var harness = CreateHarness(new SequenceDiscovery(before, after), lifecycle,
            new FakeWorker(lifecycle, grant => new(grant.CorrelationId, ValidTerminal(), [], [])));

        var report = await harness.RunControlledScanAsync(EnabledOptions(), new LiveScanBudgets(), true, CancellationToken.None);

        Assert.Equal(LiveFat32ValidationOutcome.Failed, report.OverallOutcome);
        Assert.Contains(report.MandatoryInvariants, invariant => !invariant.Passed);
    }

    [Theory]
    [InlineData(LiveScanTerminalStatus.PermissionDeclined, LiveFat32ValidationOutcome.UacDenied)]
    [InlineData(LiveScanTerminalStatus.WorkerStartFailed, LiveFat32ValidationOutcome.WorkerFailed)]
    [InlineData(LiveScanTerminalStatus.ProtocolFailure, LiveFat32ValidationOutcome.ProtocolRejected)]
    [InlineData(LiveScanTerminalStatus.TimedOut, LiveFat32ValidationOutcome.TimedOut)]
    [InlineData(LiveScanTerminalStatus.SourceRemoved, LiveFat32ValidationOutcome.SourceRemoved)]
    public async Task HarnessReportsDistinctFailureOutcomes(LiveScanTerminalStatus terminalStatus, LiveFat32ValidationOutcome expected)
    {
        var device = CreateDevice();
        var lifecycle = new LiveScanLifecycleCollector();
        var worker = new FakeWorker(lifecycle, grant => new(grant.CorrelationId, ValidTerminal(terminalStatus), [], []));
        var report = await CreateHarness(new SequenceDiscovery(device, device), lifecycle, worker)
            .RunControlledScanAsync(EnabledOptions(), new LiveScanBudgets(), true, CancellationToken.None);
        Assert.Equal(expected, report.OverallOutcome);
    }

    [Fact]
    public async Task CancellationAndNaturalCompletionRaceAreReportedWithoutFabrication()
    {
        var device = CreateDevice();
        var canceledLifecycle = new LiveScanLifecycleCollector();
        var canceledWorker = new FakeWorker(canceledLifecycle, grant => new(grant.CorrelationId, ValidTerminal(LiveScanTerminalStatus.Canceled), [], []), reportScanStart: true);
        var canceled = await CreateHarness(new SequenceDiscovery(device), canceledLifecycle, canceledWorker)
            .RunControlledCancellationAsync(EnabledOptions(runCancellation: true), new LiveScanBudgets(), TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Equal(LiveFat32ValidationOutcome.Canceled, canceled.Outcome);
        Assert.All(canceled.Invariants, invariant => Assert.True(invariant.Passed));

        var completedLifecycle = new LiveScanLifecycleCollector();
        var completedWorker = new FakeWorker(completedLifecycle, grant => new(grant.CorrelationId, ValidTerminal(), [], []), reportScanStart: false);
        var completed = await CreateHarness(new SequenceDiscovery(device), completedLifecycle, completedWorker)
            .RunControlledCancellationAsync(EnabledOptions(runCancellation: true), new LiveScanBudgets(), TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Equal(LiveFat32ValidationOutcome.Inconclusive, completed.Outcome);
    }

    [Fact]
    public async Task ReportWriterUsesOnlyDiagnosticsDirectoryAndSanitizedModel()
    {
        var root = Path.Combine(Path.GetTempPath(), "DataRecoveryStudio-Phase7D", Guid.NewGuid().ToString("N"));
        try
        {
            var report = new LiveFat32ValidationReport(
                "1.0", LiveScanProtocol.WorkerVersion, LiveScanProtocol.Version, Fat32ScannerVersions.MetadataPhase7A,
                new("Controlled USB", "R:\\", "FAT32", 1024, "OPAQUE-HASH", ["PHYSICAL-HASH"], [7]),
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(1),
                [Scenario(LiveFat32ValidationOutcome.Passed)], [new("Safe", true, "Satisfied")], true, LiveFat32ValidationOutcome.Passed);
            var path = await new LiveFat32ValidationReportWriter().WriteAsync(report, root, CancellationToken.None);
            var json = await File.ReadAllTextAsync(path);

            Assert.Equal("Passed", JsonDocument.Parse(json).RootElement.GetProperty("overallOutcome").GetString());
            Assert.DoesNotContain(RawIdentity, json, StringComparison.Ordinal);
            Assert.DoesNotContain("nonce", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("grant", json, StringComparison.OrdinalIgnoreCase);

            var sourceRoot = Path.GetPathRoot(root)!;
            var unsafeReport = report with { Target = report.Target! with { MountPath = sourceRoot } };
            await Assert.ThrowsAsync<InvalidOperationException>(() => new LiveFat32ValidationReportWriter().WriteAsync(unsafeReport, root, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ValidationStatesAreLocalizedInEnglishAndVietnamese()
    {
        var localization = new DictionaryLocalizationService();
        foreach (var outcome in Enum.GetValues<LiveFat32ValidationOutcome>())
        {
            var key = LiveFat32ValidationOutcomeLocalization.GetLocalizationKey(outcome);
            Assert.NotEqual(key, localization[key]);
            localization.SetLanguage("vi-VN");
            Assert.NotEqual(key, localization[key]);
            localization.SetLanguage("en-US");
        }
    }

    [Fact]
    public void ValidationHarnessHasNoTargetWriteOrPhase7BRecoveryComposition()
    {
        var root = FindRepositoryRoot();
        var harness = File.ReadAllText(Path.Combine(root, "src", "DataRecoveryStudio.Application", "LiveFat32Validation.cs"));
        var worker = File.ReadAllText(Path.Combine(root, "src", "DataRecoveryStudio.ScanWorker", "Program.cs"));
        Assert.DoesNotContain("FileStream", harness, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Write", harness, StringComparison.Ordinal);
        Assert.DoesNotContain("Fat32ImageRecoveryEngine", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("Fat32Recovery", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("PhysicalDrive", worker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorkerConnectionTimeoutTerminatesAndDisposesOnlyLaunchedProcess()
    {
        var device = CreateDevice();
        var authority = new LiveScanTargetGrantAuthority(ntfsEnabled: false, fat32Enabled: true);
        authority.UpdateDiscoverySnapshot([device]);
        var grant = authority.ConsumeGrant(authority.IssueGrant(device).GrantId);
        var process = new HangingProcess();
        var lifecycle = new LiveScanLifecycleCollector();
        var client = new NamedPipeLiveScanWorkerClient(
            "D:\\InstalledApp",
            new TrustedScanWorkerPathResolver(new OrdinaryWorkerFileSystem()),
            new SingleProcessLauncher(process),
            connectionTimeout: TimeSpan.FromMilliseconds(20),
            cancelGracePeriod: TimeSpan.FromMilliseconds(20),
            lifecycle: lifecycle);

        var result = await client.ScanAsync(grant, new LiveScanBudgets(), null, CancellationToken.None);

        Assert.Equal(LiveScanTerminalStatus.SecureConnectionFailed, result.Terminal.Status);
        Assert.Equal(1, process.KillCount);
        Assert.True(process.Disposed);
        Assert.Contains(lifecycle.Snapshot(), item => item.Kind == LiveScanWorkerLifecycleEventKind.WorkerTerminatedAfterGracePeriod && item.ProcessId == process.Id);
        Assert.Contains(lifecycle.Snapshot(), item => item.Kind == LiveScanWorkerLifecycleEventKind.WorkerDisposed && item.ProcessId == process.Id);
    }

    [Phase7DHardwareIntegrationFact]
    public async Task LiveFat32HardwareIntegration_UsesProductionDiscoveryWorkerAndScanner()
    {
        var options = LiveFat32HardwareValidationOptions.FromEnvironment();
        var lifecycle = new LiveScanLifecycleCollector();
        var authority = new LiveScanTargetGrantAuthority(ntfsEnabled: false, fat32Enabled: true);
        var worker = new NamedPipeLiveScanWorkerClient(AppContext.BaseDirectory, lifecycle: lifecycle);
        var report = await new LiveFat32HardwareValidationHarness(new WindowsStorageDiscoveryService(), authority, worker, lifecycle)
            .RunControlledScanAsync(options, new LiveScanBudgets(), safetyAuditPassed: true, CancellationToken.None);
        var output = Path.Combine(FindRepositoryRoot(), "artifacts", "test-output", "phase7d");
        _ = await new LiveFat32ValidationReportWriter().WriteAsync(report, output, CancellationToken.None);
        Assert.Equal(LiveFat32ValidationOutcome.Passed, report.OverallOutcome);
    }

    [Phase7DCancellationHardwareIntegrationFact]
    public async Task LiveFat32HardwareCancellation_ObservesTerminalAndCleanup()
    {
        var options = LiveFat32HardwareValidationOptions.FromEnvironment();
        var lifecycle = new LiveScanLifecycleCollector();
        var authority = new LiveScanTargetGrantAuthority(ntfsEnabled: false, fat32Enabled: true);
        var worker = new NamedPipeLiveScanWorkerClient(AppContext.BaseDirectory, lifecycle: lifecycle);
        var scenario = await new LiveFat32HardwareValidationHarness(new WindowsStorageDiscoveryService(), authority, worker, lifecycle)
            .RunControlledCancellationAsync(options, new LiveScanBudgets(), TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(LiveFat32ValidationOutcome.Canceled, scenario.Outcome);
        Assert.All(scenario.Invariants, invariant => Assert.True(invariant.Passed, invariant.Code));
    }

    [Phase7DManualRemovalHardwareIntegrationFact]
    public async Task LiveFat32ManualRemoval_StopsOnlyExplicitTargetScan()
    {
        var options = LiveFat32HardwareValidationOptions.FromEnvironment();
        var discovery = new WindowsStorageDiscoveryService();
        var snapshot = await discovery.GetDevicesAsync(CancellationToken.None);
        var target = new LiveFat32ControlledTargetResolver().Resolve(options.ExplicitTarget!, snapshot);
        var authority = new LiveScanTargetGrantAuthority(ntfsEnabled: false, fat32Enabled: true);
        authority.UpdateDiscoverySnapshot(snapshot);
        var grant = authority.ConsumeGrant(authority.IssueGrant(target.Device).GrantId);
        var lifecycle = new LiveScanLifecycleCollector();
        var worker = new NamedPipeLiveScanWorkerClient(AppContext.BaseDirectory, lifecycle: lifecycle);
        using var scanCancellation = new CancellationTokenSource();
        using var monitorCancellation = new CancellationTokenSource();
        var scanTask = worker.ScanAsync(grant, new LiveScanBudgets(), null, scanCancellation.Token);
        var monitorTask = LiveFat32ManualRemovalBoundary.MonitorAndCancelMatchingScanAsync(
            discovery, target.Volume.VolumeGuidPath, target.Sanitized, scanCancellation,
            new ConsoleProgress(), TimeSpan.FromMilliseconds(500), TimeSpan.FromMinutes(2), monitorCancellation.Token);

        if (await Task.WhenAny(scanTask, monitorTask) == scanTask)
        {
            monitorCancellation.Cancel();
            try { _ = await monitorTask; } catch (OperationCanceledException) { }
            var early = await scanTask;
            Assert.Fail($"Manual removal was inconclusive because the scan completed first ({early.Terminal.Status}).");
        }

        Assert.True(await monitorTask, "The explicit target was not physically removed before the manual timeout.");
        var result = await scanTask;
        Assert.Contains(result.Terminal.Status, [LiveScanTerminalStatus.Canceled, LiveScanTerminalStatus.SourceRemoved]);
        Assert.Empty(result.Candidates);
        Assert.Contains(lifecycle.Snapshot(), item => item.Kind == LiveScanWorkerLifecycleEventKind.WorkerDisposed);
    }

    [Phase7DWpfHardwareIntegrationFact]
    public Task LiveFat32WpfHardwareSmoke_UsesRealWorkerAndKeepsRecoveryDisabled() =>
        WpfTestHost.Instance.RunAsync(async () =>
        {
            var options = LiveFat32HardwareValidationOptions.FromEnvironment();
            var localization = new DictionaryLocalizationService();
            var lifecycle = new LiveScanLifecycleCollector();
            var authority = new LiveScanTargetGrantAuthority(ntfsEnabled: false, fat32Enabled: true);
            var worker = new NamedPipeLiveScanWorkerClient(AppContext.BaseDirectory, lifecycle: lifecycle);
            var orchestrator = new LiveScanUiOrchestrator(authority, new HeadlessLiveStandardScanService(authority, worker));
            using var main = new MainViewModel(
                new WindowsStorageDiscoveryService(), new UnavailableScanService(), new UnavailableRecoveryCatalogService(),
                new MemorySettingsStore(), localization, isDevelopmentMode: false, liveStandardScanEnabled: false,
                liveOrchestrator: orchestrator, liveFat32StandardScanEnabled: true);
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
                var resolved = new LiveFat32ControlledTargetResolver().Resolve(
                    options.ExplicitTarget!, main.Devices.Devices.Select(card => card.Device).ToArray());
                main.Devices.SelectDeviceCommand.Execute(main.Devices.Devices.Single(card => ReferenceEquals(card.Device, resolved.Device)));
                Assert.True(main.ScanMode.IsStandardEnabled);
                Assert.False(main.ScanMode.IsDeepEnabled);
                main.ScanMode.StartScanCommand.Execute(null);
                await WaitUntilAsync(() => !main.ScanProgress.IsActive, TimeSpan.FromMinutes(2));
                Assert.Equal(PageKind.Results, main.CurrentPage);
                Assert.False(main.Results.CanRecover);
                Assert.Contains(lifecycle.Snapshot(), item => item.Kind == LiveScanWorkerLifecycleEventKind.HandshakeCompleted);
                Assert.Contains(lifecycle.Snapshot(), item => item.Kind == LiveScanWorkerLifecycleEventKind.WorkerDisposed);
                localization.SetLanguage("vi-VN");
                ThemeManager.Apply(ThemePreference.Light);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                ThemeManager.Apply(ThemePreference.Dark);
            }
            finally
            {
                if (main.ScanProgress.IsActive)
                {
                    main.ScanProgress.RequestCancellation();
                    await WaitUntilAsync(() => !main.ScanProgress.IsActive, TimeSpan.FromSeconds(10));
                }
                window.Close();
            }
        }, TimeSpan.FromMinutes(3));

    private static LiveFat32HardwareValidationHarness CreateHarness(
        IDeviceDiscoveryService discovery,
        LiveScanLifecycleCollector lifecycle,
        ILiveScanWorkerClient worker,
        IProgress<LiveFat32SanitizedTarget>? observed = null) => new(
            discovery,
            new LiveScanTargetGrantAuthority(ntfsEnabled: false, fat32Enabled: true),
            worker,
            lifecycle,
            timeProvider: TimeProvider.System,
            preElevationTarget: observed);

    private static LiveFat32HardwareValidationOptions EnabledOptions(bool runCancellation = false) =>
        new(true, VolumePath, runCancellation, false, false);

    private static LiveScanTerminalResultDto ValidTerminal(LiveScanTerminalStatus status = LiveScanTerminalStatus.Completed)
    {
        var evidence = new string('A', 64);
        return new(status, status == LiveScanTerminalStatus.ChangedDuringScan ? LiveScanConsistency.ChangedDuringScan : LiveScanConsistency.LiveBestEffort,
            0, 0, 10, 4096, status != LiveScanTerminalStatus.Completed, status == LiveScanTerminalStatus.Completed ? null : status.ToString())
        {
            ScannerKind = LiveScanScannerKind.Fat32StandardMetadata,
            FileSystem = "FAT32",
            DirectoriesExamined = 2,
            DirectoryEntriesExamined = 10,
            FatEntriesInspected = 4,
            Fat32GeometryValidated = true,
            Fat32BootRelationship = Fat32BootRelationship.PrimaryAndBackupMatch,
            Fat32BootRelationshipAfter = Fat32BootRelationship.PrimaryAndBackupMatch,
            Fat32MirroringEnabled = true,
            Fat32FatCount = 2,
            Fat32ActiveFatIndex = 0,
            Fat32RootDirectoryCluster = 2,
            Fat32ScannerVersion = Fat32ScannerVersions.MetadataPhase7A,
            ConsistencyEvidenceBefore = evidence,
            ConsistencyEvidenceAfter = evidence,
            Fat32GeometryEvidenceBefore = evidence,
            Fat32GeometryEvidenceAfter = evidence,
            Fat32BootEvidenceBefore = evidence,
            Fat32BootEvidenceAfter = evidence,
            Fat32SelectedFatEvidenceBefore = evidence,
            Fat32SelectedFatEvidenceAfter = evidence,
            Fat32RootChainEvidenceBefore = evidence,
            Fat32RootChainEvidenceAfter = evidence,
            SourceHandleDisposed = true,
        };
    }

    private static LiveScanProgressDto FatProgress(long records, long total, long bytes, int candidates) =>
        new(records, total, bytes, candidates, "Progress.Phase.Fat32.TraversingDirectories")
        {
            ScannerKind = LiveScanScannerKind.Fat32StandardMetadata,
            DirectoriesExamined = checked((int)records),
            DirectoryEntriesExamined = checked((int)records),
            FatEntriesInspected = checked((int)records),
        };

    private static LiveFat32ValidationScenarioResult Scenario(LiveFat32ValidationOutcome outcome) => new(
        LiveFat32ValidationScenarioKind.ControlledScan, outcome, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
        null, [], [], outcome.ToString());

    private static StorageDevice CreateDevice(string suffix = "main", string mountPath = "R:\\", string volumePath = VolumePath)
    {
        var physicalId = new PhysicalDeviceId($"{RawIdentity}-{suffix}");
        var volume = new Volume(volumePath, mountPath, "Controlled USB", "FAT32", 64 * 1024 * 1024, 1024, physicalId)
        {
            VolumeGuidPath = volumePath,
            MountPaths = [mountPath],
            PhysicalDeviceIds = new HashSet<PhysicalDeviceId> { physicalId },
        };
        return new(physicalId, "Controlled USB", "Test Model", StorageDeviceType.UsbDevice, DeviceConnectionStatus.Online, [volume])
        {
            PhysicalDisks = [new(7, physicalId, PhysicalIdentityConfidence.High, "Test Model", "Vendor", StorageBusType.Usb, true, volume.CapacityBytes, StorageDeviceType.UsbDevice)],
        };
    }

    private static StorageDevice ReplaceVolume(StorageDevice device, Func<Volume, Volume> change)
    {
        var volume = change(device.Volumes[0]);
        return device with { Volumes = [volume] };
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "DataRecoveryStudio.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("The Phase 7D WPF smoke timed out.");
            await Task.Delay(20);
            await Dispatcher.Yield(DispatcherPriority.Background);
        }
    }

    private sealed class SequenceDiscovery(params StorageDevice[] snapshots) : IDeviceDiscoveryService
    {
        private int _index;
        public Task<IReadOnlyList<StorageDevice>> GetDevicesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Math.Min(Interlocked.Increment(ref _index) - 1, snapshots.Length - 1);
            return Task.FromResult<IReadOnlyList<StorageDevice>>([snapshots[index]]);
        }
    }

    private sealed class SnapshotSequenceDiscovery(params IReadOnlyList<StorageDevice>[] snapshots) : IDeviceDiscoveryService
    {
        private int _index;
        public Task<IReadOnlyList<StorageDevice>> GetDevicesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Math.Min(Interlocked.Increment(ref _index) - 1, snapshots.Length - 1);
            return Task.FromResult(snapshots[index]);
        }
    }

    private sealed class FakeWorker(
        LiveScanLifecycleCollector lifecycle,
        Func<LiveScanTargetGrant, LiveScanResult> result,
        bool reportScanStart = false) : ILiveScanWorkerClient
    {
        public Task<LiveScanResult> ScanAsync(LiveScanTargetGrant grant, LiveScanBudgets budgets, IProgress<LiveScanProgressDto>? progress, CancellationToken cancellationToken)
        {
            lifecycle.Report(new(LiveScanWorkerLifecycleEventKind.LaunchRequested, grant.CorrelationId, DateTimeOffset.UtcNow));
            lifecycle.Report(new(LiveScanWorkerLifecycleEventKind.WorkerStarted, grant.CorrelationId, DateTimeOffset.UtcNow, 4321));
            lifecycle.Report(new(LiveScanWorkerLifecycleEventKind.HandshakeCompleted, grant.CorrelationId, DateTimeOffset.UtcNow, 4321));
            if (reportScanStart) progress?.Report(FatProgress(1, 10, 512, 0));
            lifecycle.Report(new(LiveScanWorkerLifecycleEventKind.TerminalAccepted, grant.CorrelationId, DateTimeOffset.UtcNow, 4321));
            lifecycle.Report(new(LiveScanWorkerLifecycleEventKind.WorkerExited, grant.CorrelationId, DateTimeOffset.UtcNow, 4321, 0));
            lifecycle.Report(new(LiveScanWorkerLifecycleEventKind.WorkerDisposed, grant.CorrelationId, DateTimeOffset.UtcNow, 4321));
            return Task.FromResult(result(grant));
        }
    }

    private sealed class ValueCollector<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];
        public void Report(T value) => Values.Add(value);
    }

    private sealed class OrdinaryWorkerFileSystem : ITrustedWorkerFileSystem
    {
        public string GetFullPath(string path) => path;
        public bool FileExists(string path) => true;
        public FileAttributes GetAttributes(string path) => FileAttributes.Normal;
    }

    private sealed class SingleProcessLauncher(HangingProcess process) : IScanWorkerLauncher
    {
        public IScanWorkerProcess Launch(string workerPath, string pipeName, Guid sessionId, string nonce) => process;
        public IScanWorkerProcess Launch(string workerPath, string pipeName, Guid sessionId, string nonce, LiveScanScannerKind scannerKind) => process;
    }

    private sealed class HangingProcess : IScanWorkerProcess
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Id => 7654;
        public bool HasExited { get; private set; }
        public int ExitCode => HasExited ? 1 : throw new InvalidOperationException();
        public int KillCount { get; private set; }
        public bool Disposed { get; private set; }
        public Task WaitForExitAsync(CancellationToken cancellationToken) => _exit.Task.WaitAsync(cancellationToken);
        public void Kill()
        {
            KillCount++;
            HasExited = true;
            _exit.TrySetResult();
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class MemorySettingsStore : ISettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(AppSettings.Default);
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NeverExecutor : ILiveScanExecutor
    {
        public Task<LiveScanExecutionResult> ExecuteAsync(
            Guid sessionId,
            LiveScanTargetGrant grant,
            LiveScanBudgets requestedBudgets,
            IProgress<LiveScanProgressDto>? progress,
            CancellationToken cancellationToken) => throw new InvalidOperationException("A protocol mismatch must not reach execution.");
    }

    private sealed class ConsoleProgress : IProgress<string>
    {
        public void Report(string value) => Console.WriteLine(value);
    }
}

public sealed class Phase7DHardwareIntegrationFactAttribute : FactAttribute
{
    public Phase7DHardwareIntegrationFactAttribute()
    {
        if (!LiveFat32HardwareValidationOptions.FromEnvironment().IsEnabled)
            Skip = LiveFat32HardwareValidationOptions.MissingOptInReason;
    }
}

public sealed class Phase7DWpfHardwareIntegrationFactAttribute : FactAttribute
{
    public Phase7DWpfHardwareIntegrationFactAttribute()
    {
        var options = LiveFat32HardwareValidationOptions.FromEnvironment();
        if (!options.IsEnabled || !options.RunWpfSmoke ||
            !string.Equals(Environment.GetEnvironmentVariable("DATA_RECOVERY_STUDIO_ENABLE_LIVE_FAT32_STANDARD_SCAN"), "1", StringComparison.Ordinal) ||
            !Environment.UserInteractive)
        {
            Skip = "Set both live FAT32 gates, an explicit controlled target, and DATA_RECOVERY_STUDIO_RUN_LIVE_FAT32_WPF_SMOKE=1 in an interactive UAC-capable session.";
        }
    }
}

public sealed class Phase7DCancellationHardwareIntegrationFactAttribute : FactAttribute
{
    public Phase7DCancellationHardwareIntegrationFactAttribute()
    {
        var options = LiveFat32HardwareValidationOptions.FromEnvironment();
        if (!options.IsEnabled || !options.RunCancellation)
            Skip = "Set both live FAT32 hardware variables and DATA_RECOVERY_STUDIO_RUN_LIVE_FAT32_CANCELLATION=1 to exercise real worker cancellation.";
    }
}

public sealed class Phase7DManualRemovalHardwareIntegrationFactAttribute : FactAttribute
{
    public Phase7DManualRemovalHardwareIntegrationFactAttribute()
    {
        var options = LiveFat32HardwareValidationOptions.FromEnvironment();
        if (!options.IsEnabled || !options.RunManualRemoval || !Environment.UserInteractive)
            Skip = LiveFat32ManualRemovalBoundary.NoInteractiveSessionReason;
    }
}
