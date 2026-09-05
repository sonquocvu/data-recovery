using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using DataRecoveryStudio.Application;
using DataRecoveryStudio.Core;
using DataRecoveryStudio.Infrastructure;
using Microsoft.Win32.SafeHandles;

namespace DataRecoveryStudio.Tests;

public sealed class Phase5ALiveScanTests
{
    private const string VolumePath = "\\\\?\\Volume{11111111-2222-3333-4444-555555555555}";

    [Theory]
    [InlineData("\\\\.\\PhysicalDrive0")]
    [InlineData("C:\\")]
    [InlineData("\\\\server\\share")]
    [InlineData("\\\\?\\GLOBALROOT\\Device\\HarddiskVolume1")]
    [InlineData("\\\\?\\Volume{11111111-2222-3333-4444-555555555555}\\folder")]
    [InlineData("\\\\?\\Volume{not-a-guid}")]
    [InlineData("\\??\\Volume{11111111-2222-3333-4444-555555555555}")]
    [InlineData("\\\\?\\Volume{00000000-0000-0000-0000-000000000000}")]
    [InlineData("\\\\?\\Volume{11111111-2222-3333-4444-555555555555}\0")]
    public void CanonicalVolumePath_RejectsRawAliasesAndMalformedTargets(string path) =>
        Assert.ThrowsAny<Exception>(() => CanonicalVolumeGuidPath.Parse(path));

    [Fact]
    public void CanonicalVolumePath_NormalizesOneTrailingSeparator()
    {
        Assert.Equal(VolumePath, CanonicalVolumeGuidPath.Parse(VolumePath + "\\"));
        Assert.Equal(VolumePath, CanonicalVolumeGuidPath.Parse(VolumePath.ToUpperInvariant()));
    }

    [Fact]
    public void Grants_AreCurrentMemoryOnlyAndConsumedOnce()
    {
        var device = CreateDevice();
        var authority = new LiveScanTargetGrantAuthority();
        authority.UpdateDiscoverySnapshot([device]);

        var grant = authority.IssueGrant(device);
        var consumed = authority.ConsumeGrant(grant.GrantId);

        Assert.Equal(1, grant.DiscoveryGeneration);
        Assert.Equal(VolumePath, consumed.CanonicalVolumeGuidPath);
        Assert.Equal(64, consumed.Nonce.Length);
        Assert.Throws<LiveScanAuthorizationException>(() => authority.ConsumeGrant(grant.GrantId));
    }

    [Fact]
    public void Grants_ExpireAndRejectOldDiscoveryGeneration()
    {
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var device = CreateDevice();
        var authority = new LiveScanTargetGrantAuthority(clock, TimeSpan.FromSeconds(1));
        authority.UpdateDiscoverySnapshot([device]);
        var expired = authority.IssueGrant(device);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(LiveScanAuthorizationError.GrantExpired,
            Assert.Throws<LiveScanAuthorizationException>(() => authority.ConsumeGrant(expired.GrantId)).Error);

        var current = authority.IssueGrant(device);
        authority.UpdateDiscoverySnapshot([CreateDevice()]);
        Assert.Equal(LiveScanAuthorizationError.StaleDiscoveryGeneration,
            Assert.Throws<LiveScanAuthorizationException>(() => authority.ConsumeGrant(current.GrantId)).Error);
    }

    [Theory]
    [InlineData("FAT32", DeviceConnectionStatus.Online, VolumeAvailability.Available, LiveScanAuthorizationError.FeatureDisabled)]
    [InlineData("NTFS", DeviceConnectionStatus.Disconnected, VolumeAvailability.Available, LiveScanAuthorizationError.Disconnected)]
    [InlineData("NTFS", DeviceConnectionStatus.Online, VolumeAvailability.NoMountPoint, LiveScanAuthorizationError.NotMounted)]
    public void Grants_RejectIneligibleDiscoveryTargets(
        string fileSystem,
        DeviceConnectionStatus connection,
        VolumeAvailability availability,
        LiveScanAuthorizationError expected)
    {
        var device = CreateDevice(fileSystem, connection, availability);
        var authority = new LiveScanTargetGrantAuthority();
        authority.UpdateDiscoverySnapshot([device]);

        Assert.Equal(expected, Assert.Throws<LiveScanAuthorizationException>(() => authority.IssueGrant(device)).Error);
    }

    [Fact]
    public void Grants_RejectObjectsNotProducedByCurrentSnapshot()
    {
        var authority = new LiveScanTargetGrantAuthority();
        authority.UpdateDiscoverySnapshot([CreateDevice()]);

        Assert.Equal(LiveScanAuthorizationError.TargetNotInCurrentSnapshot,
            Assert.Throws<LiveScanAuthorizationException>(() => authority.IssueGrant(CreateDevice())).Error);
    }

    [Fact]
    public async Task HeadlessService_CancellationBeforeGrantNeverLaunchesWorker()
    {
        var device = CreateDevice();
        var authority = new LiveScanTargetGrantAuthority();
        authority.UpdateDiscoverySnapshot([device]);
        var worker = new RecordingWorkerClient();
        var service = new HeadlessLiveStandardScanService(authority, worker);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ScanAsync(device, new LiveScanBudgets(), null, cancellation.Token));
        Assert.Equal(0, worker.Calls);
    }

    [Fact]
    public async Task WorkerRevalidation_RequiresCurrentIdentityAndPhysicalIntersection()
    {
        var device = CreateDevice();
        var grant = IssueGrant(device);
        var validator = new WindowsVolumeOnlyLiveScanTargetValidator(new RevalidationNative());

        var validated = await validator.ValidateAsync(grant, CancellationToken.None);

        Assert.Equal(grant, validated.Grant);
        var changed = grant with { PhysicalDiskNumbers = [9] };
        Assert.Equal(LiveScanTerminalStatus.TargetChanged,
            (await Assert.ThrowsAsync<LiveScanTargetValidationException>(() => validator.ValidateAsync(changed, CancellationToken.None))).Status);
    }

    [Fact]
    public async Task WorkerRevalidation_RejectsRemovedAndChangedFilesystem()
    {
        var grant = IssueGrant(CreateDevice());
        var removed = new WindowsVolumeOnlyLiveScanTargetValidator(new RevalidationNative { IsRemoved = true });
        Assert.Equal(LiveScanTerminalStatus.SourceRemoved,
            (await Assert.ThrowsAsync<LiveScanTargetValidationException>(() => removed.ValidateAsync(grant, CancellationToken.None))).Status);

        var changed = new WindowsVolumeOnlyLiveScanTargetValidator(new RevalidationNative { FileSystem = "FAT32" });
        Assert.Equal(LiveScanTerminalStatus.UnsupportedFileSystem,
            (await Assert.ThrowsAsync<LiveScanTargetValidationException>(() => changed.ValidateAsync(grant, CancellationToken.None))).Status);
    }

    [Fact]
    public async Task LiveSource_UsesExactReadOnlyOpenFlagsAndDisposesHandle()
    {
        var native = new RecordingLiveNative();
        var source = new LiveVolumeSourceFactory(native).Open(IssueGrant(CreateDevice()), 4096);

        await source.DisposeAsync();

        Assert.Equal(VolumePath, native.Target);
        Assert.Equal(LiveVolumeNativeConstants.GenericRead, native.Options.DesiredAccess);
        Assert.Equal(7u, native.Options.ShareMode);
        Assert.Equal(3u, native.Options.CreationDisposition);
        Assert.True(native.Handle!.IsClosed);
    }

    [Fact]
    public void NativeBoundary_ContainsNoWriteImportOrWriteCapableFlag()
    {
        Assert.Equal(0x80000000u, LiveVolumeOpenOptions.ReadOnlyScan.DesiredAccess);
        Assert.Equal(7u, LiveVolumeOpenOptions.ReadOnlyScan.ShareMode);
        Assert.Equal(3u, LiveVolumeOpenOptions.ReadOnlyScan.CreationDisposition);
        var imports = typeof(WindowsLiveVolumeNative).GetMethods(BindingFlags.NonPublic | BindingFlags.Static).Select(method => method.Name);
        Assert.Contains("CreateFileW", imports);
        Assert.DoesNotContain("WriteFile", imports);
        Assert.DoesNotContain("DeviceIoControl", imports);
    }

    [Fact]
    public async Task LiveSource_HandlesPartialReadsWithExactOffsetsAndBounds()
    {
        var reader = new PartialReader();
        var handle = ValidHandle();
        await using var source = new LiveVolumeRandomAccessSource(handle, 100, 20, 8, reader);
        var bytes = new byte[5];

        await source.ReadExactlyAsync(10, bytes, CancellationToken.None);

        Assert.Equal([10L, 12L, 14L], reader.Offsets);
        Assert.All(bytes, value => Assert.Equal(0x5A, value));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await source.ReadExactlyAsync(0, new byte[9], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await source.ReadExactlyAsync(99, new byte[2], CancellationToken.None));
    }

    [Fact]
    public async Task LiveSource_MapsRemovalAndCancellationWithoutRetryingBroaderAccess()
    {
        await using var removed = new LiveVolumeRandomAccessSource(ValidHandle(), 100, 100, 8, new RemovalReader());
        await Assert.ThrowsAsync<LiveSourceRemovedException>(async () => await removed.ReadExactlyAsync(0, new byte[1], CancellationToken.None));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var canceled = new LiveVolumeRandomAccessSource(ValidHandle(), 100, 100, 8, new PartialReader());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await canceled.ReadExactlyAsync(0, new byte[1], cancellation.Token));
    }

    [Fact]
    public async Task Protocol_RoundTripsLengthPrefixedNarrowDtos()
    {
        var session = Guid.NewGuid();
        await using var stream = new MemoryStream();
        await LiveScanProtocolCodec.WriteAsync(stream, session, LiveScanMessageKind.HandshakeAccepted,
            new LiveScanHandshakeAccepted(LiveScanProtocol.Version) { ScannerKind = LiveScanScannerKind.NtfsStandardMetadata }, CancellationToken.None);
        stream.Position = 0;

        var envelope = await LiveScanProtocolCodec.ReadAsync(stream, CancellationToken.None);

        Assert.Equal(session, envelope.SessionId);
        Assert.Equal(LiveScanProtocol.Version, LiveScanProtocolCodec.ReadPayload<LiveScanHandshakeAccepted>(envelope).ProtocolVersion);
    }

    [Fact]
    public async Task Protocol_RejectsOversizeUnknownFieldsAndInvalidEnums()
    {
        var oversizeBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(oversizeBytes, LiveScanProtocol.MaximumMessageBytes + 1);
        await using var oversize = new MemoryStream(oversizeBytes);
        await Assert.ThrowsAsync<LiveScanProtocolException>(async () => await LiveScanProtocolCodec.ReadAsync(oversize, CancellationToken.None));

        var session = Guid.NewGuid();
        var unknown = Frame($"{{\"ProtocolVersion\":1,\"SessionId\":\"{session}\",\"Kind\":2,\"Payload\":{{\"ProtocolVersion\":1}},\"Unknown\":1}}");
        await Assert.ThrowsAsync<LiveScanProtocolException>(async () => await LiveScanProtocolCodec.ReadAsync(unknown, CancellationToken.None));
        var invalidEnum = Frame($"{{\"ProtocolVersion\":1,\"SessionId\":\"{session}\",\"Kind\":999,\"Payload\":{{}}}}");
        await Assert.ThrowsAsync<LiveScanProtocolException>(async () => await LiveScanProtocolCodec.ReadAsync(invalidEnum, CancellationToken.None));
    }

    [Fact]
    public async Task ResultAccumulator_EnforcesMonotonicProgressAndOneTerminalResult()
    {
        var session = Guid.NewGuid();
        var accumulator = new LiveScanResultAccumulator(session, null);
        accumulator.Accept(await Envelope(session, LiveScanMessageKind.Progress, new LiveScanProgressDto(2, 10, 100, 0, "scan") { ScannerKind = LiveScanScannerKind.NtfsStandardMetadata }));
        var regressing = await Envelope(session, LiveScanMessageKind.Progress, new LiveScanProgressDto(1, 10, 99, 0, "scan") { ScannerKind = LiveScanScannerKind.NtfsStandardMetadata });
        Assert.Throws<LiveScanProtocolException>(() => accumulator.Accept(regressing));

        var terminal = await Envelope(session, LiveScanMessageKind.TerminalResult,
            new LiveScanTerminalResultDto(LiveScanTerminalStatus.Completed, LiveScanConsistency.LiveBestEffort, 0, 0, 2, 100, false, null)
            { ScannerKind = LiveScanScannerKind.NtfsStandardMetadata, FileSystem = "NTFS" });
        Assert.Equal(LiveScanTerminalStatus.Completed, accumulator.Complete(terminal).Terminal.Status);
        Assert.Throws<LiveScanProtocolException>(() => accumulator.Complete(terminal));
    }

    [Fact]
    public async Task ResultAccumulator_RejectsOutOfSequenceBoundedBatches()
    {
        var session = Guid.NewGuid();
        var accumulator = new LiveScanResultAccumulator(session, null);
        var envelope = await Envelope(session, LiveScanMessageKind.CandidateBatch, new LiveScanCandidateBatchDto(1, []));

        Assert.Throws<LiveScanProtocolException>(() => accumulator.Accept(envelope));
    }

    [Fact]
    public void ProtocolSurface_HasNoRawReadRecoveryWriteOrPayloadBytes()
    {
        Assert.Equal(
            ["HandshakeHello", "HandshakeAccepted", "StartScan", "Progress", "CandidateBatch", "DiagnosticBatch", "Cancel", "TerminalResult"],
            Enum.GetNames<LiveScanMessageKind>());
        var dtoTypes = new[]
        {
            typeof(LiveScanStartRequest), typeof(LiveScanCandidateDto), typeof(LiveScanCandidateBatchDto),
            typeof(LiveScanDiagnosticDto), typeof(LiveScanTerminalResultDto),
        };
        Assert.All(dtoTypes, type => Assert.DoesNotContain(type.GetProperties(), property => property.PropertyType == typeof(byte[])));
    }

    [Fact]
    public void WorkerHardLimits_CannotBeRaisedByParent()
    {
        var clamped = LiveScanHardLimits.Clamp(new(
            long.MaxValue, long.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue,
            int.MaxValue, TimeSpan.MaxValue, TimeSpan.MaxValue));

        Assert.Equal(1024L * 1024 * 1024, clamped.MaximumBytesRead);
        Assert.Equal(1_000_000, clamped.MaximumMftRecords);
        Assert.Equal(LiveScanProtocol.MaximumCandidates, clamped.MaximumCandidates);
        Assert.Equal(LiveScanProtocol.MaximumMessageBytes, 1024 * 1024);
        Assert.Equal(LiveScanProtocol.MaximumCandidateBatchSize, clamped.CandidateBatchSize);
    }

    [Fact]
    public async Task ProductionScanner_EnforcesCandidateBudgetHonestly()
    {
        var fixture = new NtfsTestFixtureBuilder(9).AddRoot();
        fixture.AddRecord(6, 2, false, false, fixture.FileName(5, 1, "one.txt", 1));
        fixture.AddRecord(7, 2, false, false, fixture.FileName(5, 1, "two.txt", 1));
        await using var source = new MemoryRandomAccessSource(fixture.Build());

        var result = await new NtfsMetadataScanner().ScanAsync(
            source,
            new StandardScanRequest(new NtfsVolumeContext(0), new StandardScanBudgets(MaximumCandidates: 1)),
            null,
            CancellationToken.None);

        Assert.Equal(StandardScanOutcome.Partial, result.Outcome);
        Assert.Equal("MaximumCandidates", result.PartialReason);
        Assert.Single(result.Candidates);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SCAN_CANDIDATE_BUDGET_REACHED");
    }

    [Fact]
    public async Task ConsistencyRunner_MarksMftBootstrapChangesAsChangedDuringScan()
    {
        var bytes = CreateLiveBootstrap();
        var source = new MutableSource(bytes);
        var scanner = new CallbackScanner(() => source.Bytes[4 * 4096 + 100]++);
        var executor = new LiveScanExecutor(new AcceptingValidator(), new SingleSourceFactory(source), scanner);

        var result = await executor.ExecuteAsync(Guid.NewGuid(), CreateGrant(bytes.Length), new LiveScanBudgets(MaximumMftRecords: 10), null, CancellationToken.None);

        Assert.Equal(LiveScanTerminalStatus.ChangedDuringScan, result.Terminal.Status);
        Assert.Equal(LiveScanConsistency.ChangedDuringScan, result.Terminal.Consistency);
        Assert.True(result.Terminal.IsPartial);
        Assert.Equal("MftLayoutChanged", result.Terminal.ReasonCode);
    }

    [Fact]
    public async Task HeadlessExecutor_ProductionScannerConsumesAuthorizedReadOnlySource()
    {
        var fixture = new NtfsTestFixtureBuilder(8).AddRoot();
        fixture.AddRecord(6, 2, false, false, fixture.FileName(5, 1, "deleted.txt", 1));
        var bytes = fixture.Build();
        var executor = new LiveScanExecutor(
            new AcceptingValidator(),
            new SingleSourceFactory(new MutableSource(bytes)),
            new NtfsMetadataScanner());

        var result = await executor.ExecuteAsync(
            Guid.NewGuid(),
            CreateGrant(bytes.Length),
            new LiveScanBudgets(MaximumMftRecords: 8, MaximumCandidates: 8),
            null,
            CancellationToken.None);

        Assert.Equal(LiveScanTerminalStatus.Completed, result.Terminal.Status);
        Assert.Equal(LiveScanConsistency.LiveBestEffort, result.Terminal.Consistency);
        Assert.Equal("deleted.txt", Assert.Single(result.Candidates).Name);
    }

    [Fact]
    public async Task WorkerHost_AuthenticatesThenEmitsBoundedDataAndOneTerminalResult()
    {
        var session = Guid.NewGuid();
        var nonce = new string('A', 64);
        var pipeName = $"DataRecoveryStudio.LiveScan.{session:N}.ABC";
        var executor = new SuccessfulExecutor();
        var host = new NamedPipeScanWorkerHost(executor);
        await using var server = CreateServer(pipeName);
        var hostTask = host.RunAsync(new ScanWorkerArguments(pipeName, session, nonce, LiveScanProtocol.Version), CancellationToken.None);
        await server.WaitForConnectionAsync();

        var helloEnvelope = await LiveScanProtocolCodec.ReadAsync(server, CancellationToken.None);
        var hello = LiveScanProtocolCodec.ReadPayload<LiveScanHandshakeHello>(helloEnvelope);
        Assert.Equal(nonce, hello.Nonce);
        await LiveScanProtocolCodec.WriteAsync(server, session, LiveScanMessageKind.HandshakeAccepted, new LiveScanHandshakeAccepted(LiveScanProtocol.Version) { ScannerKind = LiveScanScannerKind.NtfsStandardMetadata }, CancellationToken.None);
        await LiveScanProtocolCodec.WriteAsync(server, session, LiveScanMessageKind.StartScan,
            new LiveScanStartRequest(CreateGrant(2 * 1024 * 1024) with { CorrelationId = session }, new LiveScanBudgets()) { ScannerKind = LiveScanScannerKind.NtfsStandardMetadata }, CancellationToken.None);

        var accumulator = new LiveScanResultAccumulator(session, null);
        LiveScanResult? result = null;
        while (result is null)
        {
            var envelope = await LiveScanProtocolCodec.ReadAsync(server, CancellationToken.None);
            if (envelope.Kind == LiveScanMessageKind.TerminalResult) result = accumulator.Complete(envelope);
            else accumulator.Accept(envelope);
        }

        Assert.Equal(0, await hostTask);
        Assert.Equal(LiveScanTerminalStatus.Completed, result.Terminal.Status);
        Assert.Single(result.Candidates);
        Assert.Single(result.Diagnostics);
        Assert.Equal(1, executor.Calls);
    }

    [Fact]
    public async Task WorkerHost_CancelIsIdempotentAndPipeLossCancelsExecution()
    {
        var canceledRun = await RunCancelableHostAsync(disconnect: false);
        Assert.Equal(LiveScanTerminalStatus.Canceled, canceledRun.TerminalStatus);
        Assert.True(canceledRun.Executor.WasCanceled);
        Assert.Equal(0, canceledRun.ExitCode);

        var disconnectedRun = await RunCancelableHostAsync(disconnect: true);
        Assert.True(disconnectedRun.Executor.WasCanceled);
        Assert.NotEqual(0, disconnectedRun.ExitCode);
    }

    [Fact]
    public async Task WorkerHost_RejectsProtocolAndGrantNonceMismatchBeforeOpeningSource()
    {
        var executor = new SuccessfulExecutor();
        var mismatch = await new NamedPipeScanWorkerHost(executor).RunAsync(
            new ScanWorkerArguments("DataRecoveryStudio.LiveScan.test.ABC", Guid.NewGuid(), new string('A', 64), 99),
            CancellationToken.None);
        Assert.Equal(20, mismatch);
        Assert.Equal(0, executor.Calls);

        var session = Guid.NewGuid();
        var pipeName = $"DataRecoveryStudio.LiveScan.{session:N}.ABC";
        await using var server = CreateServer(pipeName);
        var hostTask = new NamedPipeScanWorkerHost(executor).RunAsync(new ScanWorkerArguments(pipeName, session, new string('A', 64), LiveScanProtocol.Version), CancellationToken.None);
        await server.WaitForConnectionAsync();
        _ = await LiveScanProtocolCodec.ReadAsync(server, CancellationToken.None);
        await LiveScanProtocolCodec.WriteAsync(server, session, LiveScanMessageKind.HandshakeAccepted, new LiveScanHandshakeAccepted(LiveScanProtocol.Version) { ScannerKind = LiveScanScannerKind.NtfsStandardMetadata }, CancellationToken.None);
        await LiveScanProtocolCodec.WriteAsync(server, session, LiveScanMessageKind.StartScan,
            new LiveScanStartRequest(CreateGrant(2 * 1024 * 1024) with { Nonce = new string('B', 64), CorrelationId = session }, new LiveScanBudgets()) { ScannerKind = LiveScanScannerKind.NtfsStandardMetadata }, CancellationToken.None);

        Assert.Equal(22, await hostTask);
        Assert.Equal(0, executor.Calls);
    }

    [Fact]
    public void WorkerArguments_AreNarrowAndRejectInjectedOrMissingValues()
    {
        var session = Guid.NewGuid();
        var nonce = new string('A', 64);
        var parsed = ScanWorkerArguments.Parse(["--pipe", $"DataRecoveryStudio.LiveScan.{session:N}.ABC", "--session", session.ToString("D"), "--nonce", nonce, "--protocol", LiveScanProtocol.Version.ToString(), "--scanner", "NtfsStandardMetadata"]);
        Assert.Equal(session, parsed.SessionId);
        Assert.Throws<ArgumentException>(() => ScanWorkerArguments.Parse(["--pipe", "bad\\pipe", "--session", session.ToString(), "--nonce", nonce, "--protocol", "1"]));
        Assert.Throws<ArgumentException>(() => ScanWorkerArguments.Parse(["--pipe", "x", "--session", session.ToString()]));
    }

    [Fact]
    public void TrustedWorkerResolver_UsesOnlyExpectedOrdinaryNonReparseFile()
    {
        var fileSystem = new FakeTrustedFileSystem();
        var resolver = new TrustedScanWorkerPathResolver(fileSystem);
        var path = resolver.Resolve("E:\\InstalledApp");
        Assert.Equal("E:\\InstalledApp\\DataRecoveryStudio.ScanWorker.exe", path);

        fileSystem.WorkerAttributes = FileAttributes.ReparsePoint;
        Assert.Throws<InvalidOperationException>(() => resolver.Resolve("E:\\InstalledApp"));
        fileSystem.WorkerAttributes = FileAttributes.Normal;
        fileSystem.ReparseDirectory = "E:\\InstalledApp";
        Assert.Throws<InvalidOperationException>(() => resolver.Resolve("E:\\InstalledApp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParentMapsUacDenialMissingWorkerCrashAndPreLaunchCancellationSeparately(bool exFat)
    {
        var grant = exFat ? Phase8CLiveExFatTests.Grant() : IssueGrant(CreateDevice());
        var resolver = new TrustedScanWorkerPathResolver(new FakeTrustedFileSystem());
        var declined = await new NamedPipeLiveScanWorkerClient("E:\\InstalledApp", resolver, new ThrowingLauncher(new Win32Exception(1223)))
            .ScanAsync(grant, new LiveScanBudgets(), null, CancellationToken.None);
        Assert.Equal(LiveScanTerminalStatus.PermissionDeclined, declined.Terminal.Status);

        var crashed = await new NamedPipeLiveScanWorkerClient("E:\\InstalledApp", resolver, new ImmediateExitLauncher())
            .ScanAsync(grant, new LiveScanBudgets(), null, CancellationToken.None);
        Assert.Equal(LiveScanTerminalStatus.WorkerCrashed, crashed.Terminal.Status);

        var missingFs = new FakeTrustedFileSystem { Exists = false };
        var missing = await new NamedPipeLiveScanWorkerClient("E:\\InstalledApp", new TrustedScanWorkerPathResolver(missingFs), new ImmediateExitLauncher())
            .ScanAsync(grant, new LiveScanBudgets(), null, CancellationToken.None);
        Assert.Equal(LiveScanTerminalStatus.WorkerMissing, missing.Terminal.Status);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var before = await new NamedPipeLiveScanWorkerClient("E:\\InstalledApp", resolver, new ThrowingLauncher(new Exception()))
            .ScanAsync(grant, new LiveScanBudgets(), null, canceled.Token);
        Assert.Equal(LiveScanTerminalStatus.Canceled, before.Terminal.Status);
    }

    [Fact]
    public void MainApplicationRemainsAsInvokerAndWorkerElevationIsIsolated()
    {
        var root = FindRepositoryRoot();
        var appManifest = File.ReadAllText(Path.Combine(root, "src", "DataRecoveryStudio.App", "app.manifest"));
        var workerManifest = File.ReadAllText(Path.Combine(root, "src", "DataRecoveryStudio.ScanWorker", "app.manifest"));
        Assert.Contains("level=\"asInvoker\"", appManifest);
        Assert.DoesNotContain("requireAdministrator", appManifest);
        Assert.Contains("level=\"requireAdministrator\"", workerManifest);
        Assert.DoesNotContain("UseWPF", File.ReadAllText(Path.Combine(root, "src", "DataRecoveryStudio.ScanWorker", "DataRecoveryStudio.ScanWorker.csproj")));
        var workerProgram = File.ReadAllText(Path.Combine(root, "src", "DataRecoveryStudio.ScanWorker", "Program.cs"));
        Assert.Contains("WindowsVolumeOnlyLiveScanTargetValidator", workerProgram);
        Assert.DoesNotContain("WindowsStorageDiscoveryService", workerProgram);
        Assert.DoesNotContain("PhysicalDrive", workerProgram, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(typeof(ILiveScanWorkerClient), typeof(MainViewModel).GetConstructors().SelectMany(constructor => constructor.GetParameters()).Select(parameter => parameter.ParameterType));
    }

    [LiveScanIntegrationFact]
    public async Task LiveVolumeIntegration_OptInOnly()
    {
        var requested = Environment.GetEnvironmentVariable("DATA_RECOVERY_STUDIO_LIVE_SCAN_VOLUME");
        Assert.False(string.IsNullOrWhiteSpace(requested), "DATA_RECOVERY_STUDIO_LIVE_SCAN_VOLUME must be set for the opt-in integration test.");

        var devices = await new WindowsStorageDiscoveryService().GetDevicesAsync(CancellationToken.None);
        var selected = devices.FirstOrDefault(device => device.Volumes.Any(volume =>
            volume.MountPaths.Contains(requested, StringComparer.OrdinalIgnoreCase) ||
            (CanonicalVolumeGuidPath.TryParse(requested, out var requestedCanonical) &&
             CanonicalVolumeGuidPath.TryParse(volume.VolumeGuidPath, out var current) &&
             requestedCanonical.Equals(current, StringComparison.OrdinalIgnoreCase))));
        if (selected is null) throw new InvalidOperationException("The opt-in target was not found in the current Phase 3 discovery snapshot.");
        if (!selected.Volumes[0].FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The opt-in target is not NTFS.");

        var authority = new LiveScanTargetGrantAuthority();
        authority.UpdateDiscoverySnapshot(devices);
        var service = new HeadlessLiveStandardScanService(authority, new NamedPipeLiveScanWorkerClient(AppContext.BaseDirectory));
        var result = await service.ScanAsync(selected, new LiveScanBudgets(
            MaximumBytesRead: 8 * 1024 * 1024,
            MaximumMftRecords: 64,
            MaximumCandidates: 64,
            MaximumDiagnostics: 64,
            CandidateBatchSize: 16,
            MaximumDuration: TimeSpan.FromMinutes(1)), null, CancellationToken.None);
        if (result.Terminal.Status == LiveScanTerminalStatus.PermissionDeclined)
            Assert.Fail("Live scan integration unavailable: interactive UAC elevation was declined or could not be automated.");
        Assert.Contains(result.Terminal.Status, new[] { LiveScanTerminalStatus.Completed, LiveScanTerminalStatus.Partial, LiveScanTerminalStatus.ChangedDuringScan });
    }

    private static StorageDevice CreateDevice(
        string fileSystem = "NTFS",
        DeviceConnectionStatus connection = DeviceConnectionStatus.Online,
        VolumeAvailability availability = VolumeAvailability.Available)
    {
        var physical = new PhysicalDeviceId("physical:hash:abc");
        var volume = new Volume(VolumePath + "\\", "C:\\", "System", fileSystem, 2 * 1024 * 1024, 1024, physical)
        {
            VolumeGuidPath = VolumePath + "\\",
            MountPaths = availability == VolumeAvailability.NoMountPoint ? [] : ["C:\\"],
            PhysicalDeviceIds = new HashSet<PhysicalDeviceId> { physical },
            Availability = availability,
            IsSupported = availability == VolumeAvailability.Available,
        };
        return new(physical, "System", "Disk", StorageDeviceType.InternalSsd, connection, [volume])
        {
            PhysicalDisks =
            [
                new PhysicalDisk(2, physical, PhysicalIdentityConfidence.High, "Disk", "Vendor", StorageBusType.Nvme, false, 2 * 1024 * 1024, StorageDeviceType.InternalSsd),
            ],
            PhysicalDeviceIds = new HashSet<PhysicalDeviceId> { physical },
        };
    }

    private static LiveScanTargetGrant IssueGrant(StorageDevice device)
    {
        var authority = new LiveScanTargetGrantAuthority();
        authority.UpdateDiscoverySnapshot([device]);
        return authority.IssueGrant(device);
    }

    private static LiveScanTargetGrant CreateGrant(long capacity) => new(
        Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1), VolumePath, "C:\\", "NTFS", "identity",
        ["physical"], [2], capacity, 1, true, true, true, true, new string('A', 64))
    {
        ScannerKind = LiveScanScannerKind.NtfsStandardMetadata,
        CorrelationId = Guid.NewGuid(),
    };

    private static SafeFileHandle ValidHandle() => new(new IntPtr(1234), ownsHandle: false);

    private static MemoryStream Frame(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        return new MemoryStream(frame);
    }

    private static NamedPipeServerStream CreateServer(string pipeName) => new(
        pipeName,
        PipeDirection.InOut,
        1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static async Task<(LiveScanTerminalStatus? TerminalStatus, CancelableExecutor Executor, int ExitCode)> RunCancelableHostAsync(bool disconnect)
    {
        var session = Guid.NewGuid();
        var nonce = new string('A', 64);
        var pipeName = $"DataRecoveryStudio.LiveScan.{session:N}.ABC";
        var executor = new CancelableExecutor();
        var server = CreateServer(pipeName);
        var hostTask = new NamedPipeScanWorkerHost(executor).RunAsync(new ScanWorkerArguments(pipeName, session, nonce, LiveScanProtocol.Version), CancellationToken.None);
        await server.WaitForConnectionAsync();
        _ = await LiveScanProtocolCodec.ReadAsync(server, CancellationToken.None);
        await LiveScanProtocolCodec.WriteAsync(server, session, LiveScanMessageKind.HandshakeAccepted, new LiveScanHandshakeAccepted(LiveScanProtocol.Version) { ScannerKind = LiveScanScannerKind.NtfsStandardMetadata }, CancellationToken.None);
        await LiveScanProtocolCodec.WriteAsync(server, session, LiveScanMessageKind.StartScan,
            new LiveScanStartRequest(CreateGrant(2 * 1024 * 1024) with { CorrelationId = session }, new LiveScanBudgets()) { ScannerKind = LiveScanScannerKind.NtfsStandardMetadata }, CancellationToken.None);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (disconnect)
        {
            await server.DisposeAsync();
            return (null, executor, await hostTask.WaitAsync(TimeSpan.FromSeconds(2)));
        }

        await LiveScanProtocolCodec.WriteAsync(server, session, LiveScanMessageKind.Cancel, new LiveScanCancelRequest("TestCancel"), CancellationToken.None);
        LiveScanTerminalStatus? terminal = null;
        while (terminal is null)
        {
            var envelope = await LiveScanProtocolCodec.ReadAsync(server, CancellationToken.None);
            if (envelope.Kind == LiveScanMessageKind.TerminalResult)
                terminal = LiveScanProtocolCodec.ReadPayload<LiveScanTerminalResultDto>(envelope).Status;
        }

        await server.DisposeAsync();
        return (terminal, executor, await hostTask.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private static async Task<LiveScanMessageEnvelope> Envelope<T>(Guid session, LiveScanMessageKind kind, T payload)
    {
        await using var stream = new MemoryStream();
        await LiveScanProtocolCodec.WriteAsync(stream, session, kind, payload, CancellationToken.None);
        stream.Position = 0;
        return await LiveScanProtocolCodec.ReadAsync(stream, CancellationToken.None);
    }

    private static byte[] CreateLiveBootstrap()
    {
        var bytes = new byte[2 * 1024 * 1024];
        "NTFS    "u8.CopyTo(bytes.AsSpan(3));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(11), 512);
        bytes[13] = 8;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(40), 4096);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(48), 4);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(56), 8);
        bytes[64] = unchecked((byte)-10);
        bytes[510] = 0x55;
        bytes[511] = 0xAA;
        return bytes;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "DataRecoveryStudio.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now = now.Add(duration);
    }

    private sealed class RecordingWorkerClient : ILiveScanWorkerClient
    {
        public int Calls { get; private set; }
        public Task<LiveScanResult> ScanAsync(LiveScanTargetGrant grant, LiveScanBudgets budgets, IProgress<LiveScanProgressDto>? progress, CancellationToken cancellationToken)
        {
            Calls++;
            throw new NotImplementedException();
        }
    }

    private sealed class RecordingLiveNative : ILiveVolumeNative
    {
        public string? Target { get; private set; }
        public LiveVolumeOpenOptions Options { get; private set; }
        public SafeFileHandle? Handle { get; private set; }
        public SafeFileHandle OpenReadOnlyVolume(string canonicalTarget, LiveVolumeOpenOptions options)
        {
            Target = canonicalTarget;
            Options = options;
            return Handle = ValidHandle();
        }
    }

    private sealed class RevalidationNative : IWindowsStorageNative
    {
        public bool IsRemoved { get; init; }
        public string FileSystem { get; init; } = "NTFS";
        public IReadOnlyList<string> EnumerateVolumeNames(CancellationToken cancellationToken) => IsRemoved ? [] : [VolumePath + "\\"];
        public IReadOnlyList<string> GetVolumePathNames(string volumeName) => ["C:\\"];
        public NativeVolumeInformation GetVolumeInformation(string volumeName) => new("System", FileSystem);
        public NativeDiskSpace GetDiskSpace(string path) => new(2 * 1024 * 1024, 1024);
        public WindowsDriveType GetDriveType(string rootPath) => WindowsDriveType.Fixed;
        public IMetadataDeviceHandle OpenMetadataDevice(string path, MetadataOpenOptions options)
        {
            Assert.Equal(VolumePath, path);
            Assert.DoesNotContain("PhysicalDrive", path, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(MetadataOpenOptions.ReadOnlyMetadata, options);
            return new TestMetadataHandle();
        }

        public byte[] QueryDevice(IMetadataDeviceHandle handle, uint controlCode, ReadOnlySpan<byte> input, int initialCapacity = 4096)
        {
            Assert.Equal(StorageNativeConstants.IoctlVolumeGetVolumeDiskExtents, controlCode);
            var bytes = new byte[32];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, 1);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 2);
            return bytes;
        }
    }

    private sealed class TestMetadataHandle : IMetadataDeviceHandle
    {
        public bool IsInvalid => false;
        public void Dispose() { }
    }

    private sealed class PartialReader : ILiveVolumeReader
    {
        public List<long> Offsets { get; } = [];
        public ValueTask<int> ReadAsync(SafeFileHandle handle, Memory<byte> buffer, long fileOffset, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Offsets.Add(fileOffset);
            var count = Math.Min(2, buffer.Length);
            buffer.Span[..count].Fill(0x5A);
            return ValueTask.FromResult(count);
        }
    }

    private sealed class RemovalReader : ILiveVolumeReader
    {
        public ValueTask<int> ReadAsync(SafeFileHandle handle, Memory<byte> buffer, long fileOffset, CancellationToken cancellationToken) =>
            ValueTask.FromException<int>(new Win32Exception(1167));
    }

    private sealed class AcceptingValidator : ILiveScanTargetValidator
    {
        public Task<ValidatedLiveScanTarget> ValidateAsync(LiveScanTargetGrant grant, CancellationToken cancellationToken) => Task.FromResult(new ValidatedLiveScanTarget(grant));
    }

    private sealed class SingleSourceFactory(MutableSource source) : ILiveVolumeSourceFactory
    {
        public IReadOnlyRandomAccessSource Open(LiveScanTargetGrant grant, long maximumTotalBytes) => source;
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

    private sealed class CallbackScanner(Action callback) : INtfsMetadataScanner
    {
        public Task<StandardScanResult> ScanAsync(IReadOnlyRandomAccessSource source, StandardScanRequest request, IProgress<StandardScanProgress>? progress, CancellationToken cancellationToken)
        {
            callback();
            var geometry = new NtfsBootGeometry(512, 8, 4096, 4096, 4, 8, -10, 1024);
            return Task.FromResult(new StandardScanResult(StandardScanOutcome.Completed, geometry, new NtfsMftLayout(1024, 1024, 1024, 1, 1, NtfsBootstrapSource.PrimaryMft), [], [], 1, 1024, false));
        }
    }

    private sealed class SuccessfulExecutor : ILiveScanExecutor
    {
        public int Calls { get; private set; }
        public Task<LiveScanExecutionResult> ExecuteAsync(Guid sessionId, LiveScanTargetGrant grant, LiveScanBudgets requestedBudgets, IProgress<LiveScanProgressDto>? progress, CancellationToken cancellationToken)
        {
            Calls++;
            progress?.Report(new LiveScanProgressDto(1, 1, 512, 1, "complete") { ScannerKind = LiveScanScannerKind.NtfsStandardMetadata });
            var candidate = new LiveScanCandidateDto(
                Guid.NewGuid(), sessionId, 6, 2, "deleted.txt", "\\deleted.txt", 10, FileCategory.Document,
                true, false, CandidatePathState.Complete, CandidateRecoverability.MetadataOnly, [], [])
            { ScannerKind = LiveScanScannerKind.NtfsStandardMetadata, FileSystem = "NTFS" };
            var diagnostic = new LiveScanDiagnosticDto("TEST", ScanDiagnosticSeverity.Information, "test");
            return Task.FromResult(new LiveScanExecutionResult(
                new LiveScanTerminalResultDto(LiveScanTerminalStatus.Completed, LiveScanConsistency.LiveBestEffort, 1, 1, 1, 512, false, null)
                { ScannerKind = LiveScanScannerKind.NtfsStandardMetadata, FileSystem = "NTFS" },
                [candidate],
                [diagnostic]));
        }
    }

    private sealed class CancelableExecutor : ILiveScanExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WasCanceled { get; private set; }
        public async Task<LiveScanExecutionResult> ExecuteAsync(Guid sessionId, LiveScanTargetGrant grant, LiveScanBudgets requestedBudgets, IProgress<LiveScanProgressDto>? progress, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException();
            }
            catch (OperationCanceledException)
            {
                WasCanceled = true;
                throw;
            }
        }
    }

    private sealed class FakeTrustedFileSystem : ITrustedWorkerFileSystem
    {
        public bool Exists { get; set; } = true;
        public FileAttributes WorkerAttributes { get; set; } = FileAttributes.Normal;
        public string? ReparseDirectory { get; set; }
        public string GetFullPath(string path) => Path.GetFullPath(path);
        public bool FileExists(string path) => Exists;
        public FileAttributes GetAttributes(string path)
        {
            if (path.EndsWith(TrustedScanWorkerPathResolver.WorkerFileName, StringComparison.OrdinalIgnoreCase)) return WorkerAttributes;
            return path.Equals(ReparseDirectory, StringComparison.OrdinalIgnoreCase) ? FileAttributes.Directory | FileAttributes.ReparsePoint : FileAttributes.Directory;
        }
    }

    private sealed class ThrowingLauncher(Exception exception) : IScanWorkerLauncher
    {
        public IScanWorkerProcess Launch(string workerPath, string pipeName, Guid sessionId, string nonce) => throw exception;
    }

    private sealed class ImmediateExitLauncher : IScanWorkerLauncher
    {
        public IScanWorkerProcess Launch(string workerPath, string pipeName, Guid sessionId, string nonce) => new ImmediateExitProcess();
    }

    private sealed class ImmediateExitProcess : IScanWorkerProcess
    {
        public bool HasExited => true;
        public int ExitCode => 1;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Kill() { }
        public void Dispose() { }
    }
}

public sealed class LiveScanIntegrationFactAttribute : FactAttribute
{
    public LiveScanIntegrationFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("DATA_RECOVERY_STUDIO_RUN_LIVE_SCAN_INTEGRATION"), "1", StringComparison.Ordinal))
        {
            Skip = "Set DATA_RECOVERY_STUDIO_RUN_LIVE_SCAN_INTEGRATION=1 to run the elevated read-only live NTFS scan.";
        }
    }
}
