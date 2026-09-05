using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using DataRecoveryStudio.Application;
using DataRecoveryStudio.Core;
using DataRecoveryStudio.Infrastructure;
using Microsoft.Win32.SafeHandles;

namespace DataRecoveryStudio.Tests;

public sealed class Phase8CLiveExFatTests
{
    internal const LiveScanScannerKind Kind = LiveScanScannerKind.ExFatStandardMetadata;
    internal const string VolumePath = "\\\\?\\Volume{88888888-2222-3333-4444-555555555555}";

    [Theory]
    [InlineData(null, false)]
    [InlineData("1", true)]
    [InlineData("true", false)]
    [InlineData(" 1", false)]
    [InlineData("1 ", false)]
    [InlineData("0", false)]
    public void GateIsExactIndependentAndDisabledByDefault(string? value, bool expected)
    {
        Assert.Equal(expected, LiveExFatStandardScanFeatureGate.IsEnabled(key =>
            key == LiveExFatStandardScanFeatureGate.EnvironmentVariable ? value : "1"));
        Assert.Equal(ScanCapabilityKind.LiveExFatStandardScanFeatureDisabled,
            ScanCapabilityEvaluator.Evaluate(Device(), false, true, true).Kind);
        var capability = ScanCapabilityEvaluator.Evaluate(Device(), false, false, false, true);
        Assert.True(capability.CanStartStandard);
        Assert.False(capability.CanStartDeep);
        Assert.True(capability.RequiresAdministratorPermission);
    }

    [Fact]
    public void GrantsBindExactSnapshotScannerGenerationExpiryAndExtents()
    {
        var clock = new TestClock();
        var source = Device();
        var authority = new LiveScanTargetGrantAuthority(clock, ntfsEnabled: false, exFatEnabled: true);
        authority.UpdateDiscoverySnapshot([source]);
        Assert.Throws<LiveScanAuthorizationException>(() => authority.IssueGrant(source with { }));
        Assert.Throws<LiveScanAuthorizationException>(() => authority.IssueGrant(source, LiveScanScannerKind.Fat32StandardMetadata));
        var grant = authority.IssueGrant(source);
        Assert.Equal(source.Volumes[0].Extents, grant.Extents);
        Assert.Equal(Kind, authority.ConsumeGrant(grant.GrantId).ScannerKind);
        Assert.Equal(LiveScanAuthorizationError.GrantAlreadyConsumed,
            Assert.Throws<LiveScanAuthorizationException>(() => authority.ConsumeGrant(grant.GrantId)).Error);
        grant = authority.IssueGrant(source);
        clock.Now += TimeSpan.FromMinutes(3);
        Assert.Equal(LiveScanAuthorizationError.GrantExpired,
            Assert.Throws<LiveScanAuthorizationException>(() => authority.ConsumeGrant(grant.GrantId)).Error);
        grant = authority.IssueGrant(source);
        authority.UpdateDiscoverySnapshot([source]);
        Assert.Equal(LiveScanAuthorizationError.StaleDiscoveryGeneration,
            Assert.Throws<LiveScanAuthorizationException>(() => authority.ConsumeGrant(grant.GrantId)).Error);
    }

    [Theory]
    [InlineData("mapping")]
    [InlineData("extent")]
    [InlineData("capacity")]
    [InlineData("mount")]
    [InlineData("disconnected")]
    [InlineData("unsupported")]
    public void IneligibleSourcesCannotStartOrReceiveGrant(string fault)
    {
        var device = Device();
        var volume = device.Volumes[0];
        device = fault switch
        {
            "mapping" => device with { PhysicalDisks = [] },
            "extent" => device with { Volumes = [volume with { Extents = [new(7, long.MaxValue, 10)] }] },
            "capacity" => device with { Volumes = [volume with { CapacityBytes = 0 }] },
            "mount" => device with { Volumes = [volume with { MountPaths = [] }] },
            "disconnected" => device with { ConnectionStatus = DeviceConnectionStatus.Disconnected },
            _ => device with { Volumes = [volume with { IsSupported = false }] },
        };
        Assert.False(ScanCapabilityEvaluator.Evaluate(device, false, false, false, true).CanStartStandard);
        var authority = new LiveScanTargetGrantAuthority(exFatEnabled: true);
        authority.UpdateDiscoverySnapshot([device]);
        Assert.Throws<LiveScanAuthorizationException>(() => authority.IssueGrant(device));
    }

    [Fact]
    public async Task ProductionParserRunsThroughBoundedLiveSourceWithoutPayloadOrImageFingerprint()
    {
        var fixture = Fixture();
        var before = SHA256.HashData(fixture.Bytes);
        var factory = new InstrumentedFactory(fixture.Bytes);
        var progress = new CaptureProgress();
        var (session, execution) = await Execute(factory, progress: progress);
        Assert.Equal(LiveScanTerminalStatus.Completed, execution.Terminal.Status);
        Assert.Equal(2, execution.Candidates.Count);
        Assert.True(factory.Handle!.IsClosed);
        Assert.Equal(factory.BytesRead, execution.Terminal.BytesRead);
        Assert.True(factory.BytesRead < fixture.Bytes.Length / 10);
        Assert.Equal(before, SHA256.HashData(fixture.Bytes));
        Assert.All(factory.Reads, read => Assert.DoesNotContain(fixture.PayloadRanges,
            payload => read.Offset < payload.Offset + payload.Length && payload.Offset < read.Offset + read.Length));
        var evidence = Assert.IsType<LiveExFatEvidence>(execution.Terminal.ExFat);
        Assert.Equal(ExFatScannerVersion.Phase8A, evidence.ScannerVersion);
        Assert.Equal(evidence.SampleBytes, evidence.ConsistencyBytes);
        Assert.InRange(evidence.SampleCount, 1, 32768);
        Assert.True(evidence.BootBytes > 0 && evidence.FatBytes > 0 && evidence.BitmapBytes > 0 &&
            evidence.UpCaseBytes > 0 && evidence.DirectoryBytes > 0);
        Assert.Equal(execution.Terminal.ConsistencyEvidenceBefore, execution.Terminal.ConsistencyEvidenceAfter);
        Assert.All(execution.Candidates, c => Assert.True(LiveExFatValidation.ValidCandidate(c)));
        for (var i = 1; i < progress.Values.Count; i++)
        {
            Assert.True(progress.Values[i].BytesRead >= progress.Values[i - 1].BytesRead);
            Assert.True(progress.Values[i].DirectoryEntriesExamined >= progress.Values[i - 1].DirectoryEntriesExamined);
            Assert.Equal(0, progress.Values[i].TotalRecords);
        }
        var accumulator = new LiveScanResultAccumulator(session, null, Kind);
        accumulator.Accept(Envelope(session, LiveScanMessageKind.CandidateBatch,
            new LiveScanCandidateBatchDto(0, execution.Candidates) { ScannerKind = Kind }));
        if (execution.Diagnostics.Count > 0)
            accumulator.Accept(Envelope(session, LiveScanMessageKind.DiagnosticBatch,
                new LiveScanDiagnosticBatchDto(0, execution.Diagnostics) { ScannerKind = Kind }));
        var terminal = Envelope(session, LiveScanMessageKind.TerminalResult, execution.Terminal);
        Assert.Equal(2, accumulator.Complete(terminal).Candidates.Count);
        Assert.Throws<LiveScanProtocolException>(() => accumulator.Complete(terminal));
        Assert.Throws<LiveScanProtocolException>(() => accumulator.Accept(terminal));
    }

    [Theory]
    [InlineData("flags")]
    [InlineData("backup")]
    [InlineData("descriptor")]
    [InlineData("root-chain")]
    [InlineData("upcase")]
    [InlineData("unreadable")]
    public async Task RequiredEvidenceChangesOrBecomesUnreadableDiscardCandidates(string change)
    {
        var fixture = Fixture();
        var factory = new InstrumentedFactory(fixture.Bytes);
        factory.BeforePost = () =>
        {
            if (change == "unreadable") throw new IOException("Simulated post-read failure");
            var offset = change switch
            {
                "flags" => 106,
                "backup" => fixture.SectorSize * 12 + 106,
                "descriptor" => fixture.RootSlot(0) + 20,
                "root-chain" => (int)ExFatTestFixtureBuilder.FatSector * fixture.SectorSize + 8,
                _ => fixture.Offset(4),
            };
            fixture.Bytes[offset] ^= 2;
        };
        var (_, result) = await Execute(factory);
        Assert.Equal(LiveScanTerminalStatus.ChangedDuringScan, result.Terminal.Status);
        Assert.Empty(result.Candidates);
        Assert.True(factory.Handle!.IsClosed);
    }

    [Theory]
    [InlineData(512)]
    [InlineData(16000)]
    [InlineData(30000)]
    public async Task ActualTraversalAndConsistencyReadsShareOneBudget(long limit)
    {
        var factory = new InstrumentedFactory(Fixture().Bytes);
        var (_, result) = await Execute(factory, new LiveScanBudgets(MaximumBytesRead: limit));
        Assert.Equal(limit, factory.Budget);
        Assert.InRange(factory.BytesRead, 0, limit);
        Assert.Equal(factory.BytesRead, result.Terminal.BytesRead);
        if (result.Terminal.Status == LiveScanTerminalStatus.Completed)
            Assert.Equal(result.Terminal.ExFat!.SampleBytes, result.Terminal.ExFat.ConsistencyBytes);
    }

    [Fact]
    public async Task PartialNativeReadsAreChargedEvenWhenTheExactReadFails()
    {
        var factory = new InstrumentedFactory(Fixture().Bytes) { FailAfterBytes = 128 };
        var (_, result) = await Execute(factory);
        Assert.Equal(128, factory.BytesRead);
        Assert.Equal(128, result.Terminal.BytesRead);
        Assert.Equal(128, result.Terminal.ExFat!.BootBytes);
        Assert.Equal(LiveScanTerminalStatus.Failed, result.Terminal.Status);
        factory = new InstrumentedFactory(Fixture().Bytes) { FailAfterBytes = 128 };
        await using var source = factory.Open(Grant(), 512);
        await Assert.ThrowsAsync<IOException>(async () => await source.ReadExactlyAsync(0, new byte[512], default));
        // Retrying a large read cannot reclaim the bytes returned before the failure.
        var error = await Assert.ThrowsAsync<IOException>(async () => await source.ReadExactlyAsync(0, new byte[512], default));
        Assert.Contains("budget", error.Message);
        Assert.Equal(128, factory.BytesRead);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("two-fats")]
    [InlineData("geometry")]
    public async Task UnsupportedPhase8ALayoutsAreNeverCompleted(string layout)
    {
        var fixture = Fixture();
        if (layout == "revision") fixture.W16(104, 0x101);
        else if (layout == "two-fats") fixture.Bytes[110] = 2;
        else fixture.W32(96, uint.MaxValue);
        fixture.RechecksumBoot(false);
        fixture.Bytes.AsSpan(0, fixture.SectorSize * 12).CopyTo(fixture.Bytes.AsSpan(fixture.SectorSize * 12));
        var (_, result) = await Execute(new InstrumentedFactory(fixture.Bytes));
        Assert.Equal(LiveScanTerminalStatus.Failed, result.Terminal.Status);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task DamagedMetadataRemainsPartialAndActiveOwnershipConflictIsPreserved()
    {
        var fixture = Fixture();
        fixture.AddFile("active.txt", 30, 512, deleted: false);
        fixture.AddFile("conflict.txt", 30, 512);
        fixture.SetAllocated(30);
        fixture.AddFile("damaged.txt", 40, 512, badChecksum: true, wrongHash: true);
        var (_, result) = await Execute(new InstrumentedFactory(fixture.Bytes));
        Assert.Equal(LiveScanTerminalStatus.Partial, result.Terminal.Status);
        Assert.All(result.Candidates, c => Assert.True(c.ExFat!.IsPartial));
        Assert.Contains(result.Candidates, c => c.ExFat!.Allocation == ExFatAllocationEvidence.ActiveOwnershipConflict);
        Assert.DoesNotContain(result.Candidates, c => c.ExFat!.Recoverability == ExFatRecoverabilityState.AllocationSuggestsPossibleContent);
    }

    [Fact]
    public async Task StaleDirectoryMetadataCanOverlapCandidateRangesAndIsFlaggedConservatively()
    {
        var fixture = Fixture();
        fixture.DirectoryChain(20, 20);
        fixture.AddFile("active-directory", 20, 512, deleted: false, directory: true);
        var source = new InstrumentedFactory(fixture.Bytes);
        var (_, result) = await Execute(source);
        Assert.Contains(source.Reads, r => r.Offset == fixture.Offset(20));
        Assert.Contains(result.Candidates, c => c.ExFat!.Allocation == ExFatAllocationEvidence.ActiveOwnershipConflict);
        Assert.True(result.Terminal.ExFat!.DirectoryBytes > 512);
    }

    [Fact]
    public async Task InvalidBackupIsPartialEvenWhenMainBootRemainsUsable()
    {
        var fixture = Fixture();
        fixture.Bytes[fixture.SectorSize * 12 + 3] = 0;
        var (_, result) = await Execute(new InstrumentedFactory(fixture.Bytes));
        Assert.Equal(LiveScanTerminalStatus.Partial, result.Terminal.Status);
        Assert.True(result.Terminal.ExFat!.MainValid);
        Assert.False(result.Terminal.ExFat.BackupValid);
        Assert.All(result.Candidates, c => Assert.True(c.ExFat!.IsPartial));
    }

    [Fact]
    public void EvidenceCacheHasIndependentByteAndRangeCaps()
    {
        var work = new ExFatWork(new());
        var evidence = new LiveExFatReadEvidence(work, null);
        evidence.BeforeRead(0, 8 * 1024 * 1024, ExFatMetadataReadKind.Directory, true);
        Assert.Throws<ExFatBudgetException>(() => evidence.BeforeRead(1, 1, ExFatMetadataReadKind.Directory, true));
        Assert.Equal(8 * 1024 * 1024, evidence.SampleBytes);
        evidence = new LiveExFatReadEvidence(new ExFatWork(new()), null);
        for (var i = 0; i < 32768; i++) evidence.BeforeRead(i, 1, ExFatMetadataReadKind.Fat, true);
        Assert.Throws<ExFatBudgetException>(() => evidence.BeforeRead(32768, 1, ExFatMetadataReadKind.Fat, true));
        Assert.Equal(32768, evidence.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndRemovalDisposeTheExactSource(bool removed)
    {
        using var cancel = new CancellationTokenSource();
        var factory = new InstrumentedFactory(Fixture().Bytes)
        {
            BeforePost = () => { if (removed) throw new LiveSourceRemovedException(); cancel.Cancel(); },
        };
        if (removed) await Assert.ThrowsAsync<LiveSourceRemovedException>(() => Execute(factory, token: cancel.Token));
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Execute(factory, token: cancel.Token));
        Assert.True(factory.Handle!.IsClosed);
    }

    [Theory]
    [InlineData("vdl")]
    [InlineData("enum")]
    [InlineData("timestamp")]
    [InlineData("missing")]
    [InlineData("fat32")]
    [InlineData("stream")]
    [InlineData("null-list")]
    [InlineData("null-name")]
    [InlineData("path")]
    [InlineData("scanner")]
    [InlineData("partial")]
    public async Task MalformedExFatCandidatesAreRejected(string fault)
    {
        var (session, result) = await Execute(new InstrumentedFactory(Fixture().Bytes));
        var c = result.Candidates[0];
        c = fault switch
        {
            "vdl" => c with { ExFat = c.ExFat! with { ValidDataLength = c.LogicalSize + 1 } },
            "enum" => c with { ExFat = c.ExFat! with { Allocation = (ExFatAllocationEvidence)999 } },
            "timestamp" => c with { ExFat = c.ExFat! with { Created = new(null, 77, ExFatTimestampState.Valid) } },
            "missing" => c with { ExFat = null },
            "fat32" => c with { Fat32Kind = Fat32CandidateKind.File },
            "stream" => c with { MftRecordNumber = 2 },
            "null-list" => c with { DiagnosticCodes = null! },
            "null-name" => c with { Name = null! },
            "path" => c with { OriginalPath = new string('p', 32769) },
            "scanner" => c with { ScannerKind = LiveScanScannerKind.NtfsStandardMetadata },
            _ => c with { ExFat = c.ExFat! with { IsPartial = true, Recoverability = ExFatRecoverabilityState.AllocationSuggestsPossibleContent } },
        };
        var accumulator = new LiveScanResultAccumulator(session, null, Kind);
        Assert.Throws<LiveScanProtocolException>(() => accumulator.Accept(Envelope(session,
            LiveScanMessageKind.CandidateBatch, new LiveScanCandidateBatchDto(0, [c]) { ScannerKind = Kind })));
    }

    [Fact]
    public async Task ProtocolRejectsVersionsScannerSubstitutionNullBatchesAndArithmeticOverflow()
    {
        var (session, result) = await Execute(new InstrumentedFactory(Fixture().Bytes));
        var a = new LiveScanResultAccumulator(session, null, Kind);
        var valid = Envelope(session, LiveScanMessageKind.CandidateBatch,
            new LiveScanCandidateBatchDto(0, result.Candidates) { ScannerKind = Kind });
        Assert.Throws<LiveScanProtocolException>(() => a.Accept(valid with { ProtocolVersion = 3 }));
        Assert.Throws<LiveScanProtocolException>(() => a.Accept(valid with { SessionId = Guid.NewGuid() }));
        Assert.Throws<LiveScanProtocolException>(() => a.Accept(Envelope(session, LiveScanMessageKind.DiagnosticBatch,
            new LiveScanDiagnosticBatchDto(0, []) { ScannerKind = LiveScanScannerKind.Fat32StandardMetadata })));
        Assert.Throws<LiveScanProtocolException>(() => a.Accept(Envelope(session, LiveScanMessageKind.CandidateBatch,
            new LiveScanCandidateBatchDto(0, null!) { ScannerKind = Kind })));
        var huge = result.Candidates[0] with { LogicalSize = long.MaxValue };
        Assert.Throws<LiveScanProtocolException>(() => new LiveScanResultAccumulator(session, null, Kind).Accept(
            Envelope(session, LiveScanMessageKind.CandidateBatch, new LiveScanCandidateBatchDto(0,
                [huge, huge with { CandidateId = Guid.NewGuid() }])
            { ScannerKind = Kind })));
        Assert.Equal(4, LiveScanProtocol.Version);
    }

    [Theory]
    [InlineData("NameEvidence")]
    [InlineData("IsPartial")]
    [InlineData("Created")]
    [InlineData("ValidDataLength")]
    public async Task RequiredExFatWireFieldsCannotDefaultToApparentlyValidEvidence(string field)
    {
        var (session, result) = await Execute(new InstrumentedFactory(Fixture().Bytes));
        var batch = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(
            new LiveScanCandidateBatchDto(0, result.Candidates) { ScannerKind = Kind }))!;
        Assert.True(batch["Candidates"]![0]!["ExFat"]!.AsObject().Remove(field));
        var envelope = new LiveScanMessageEnvelope(LiveScanProtocol.Version, session,
            LiveScanMessageKind.CandidateBatch, JsonSerializer.SerializeToElement(batch));
        Assert.Throws<LiveScanProtocolException>(() => new LiveScanResultAccumulator(session, null, Kind).Accept(envelope));
    }

    internal static StorageDevice Device()
    {
        var physical = new PhysicalDeviceId("windows:physical:exfat-fixture");
        var volume = new Volume(VolumePath, "F:\\", "Controlled exFAT", "exFAT", 1179648, 4096, physical)
        { Extents = [new(7, 1048576, 1179648)] };
        return new(physical, "Controlled exFAT", "Fixture", StorageDeviceType.UsbDevice, DeviceConnectionStatus.Online, [volume])
        { PhysicalDisks = [new(7, physical, PhysicalIdentityConfidence.High, "Fixture", "Fixture", StorageBusType.Usb, true, 4 * 1024 * 1024, StorageDeviceType.UsbDevice)] };
    }

    internal static LiveScanTargetGrant Grant()
    {
        var device = Device();
        var authority = new LiveScanTargetGrantAuthority(ntfsEnabled: false, exFatEnabled: true);
        authority.UpdateDiscoverySnapshot([device]);
        var grant = authority.IssueGrant(device);
        return authority.ConsumeGrant(grant.GrantId);
    }

    internal static ExFatTestFixtureBuilder Fixture()
    {
        var fixture = new ExFatTestFixtureBuilder();
        fixture.AddFile("deleted.txt", 20, 600, valid: 501);
        fixture.WritePayload([20, 21], new byte[501]);
        fixture.AddFile("empty.txt", 0, 0, contiguous: false);
        return fixture;
    }

    internal static async Task<(Guid Session, LiveScanExecutionResult Result)> Execute(InstrumentedFactory factory,
        LiveScanBudgets? budgets = null, CaptureProgress? progress = null, CancellationToken token = default)
    {
        var grant = Grant();
        // Authorization is simulated here; the parser and bounded live source are production implementations.
        var executor = new LiveScanExecutor(new AcceptingValidator(), factory, new NtfsMetadataScanner());
        return (grant.CorrelationId, await executor.ExecuteAsync(grant.CorrelationId, grant, budgets ?? new(), progress, token));
    }

    internal static LiveScanMessageEnvelope Envelope<T>(Guid session, LiveScanMessageKind kind, T payload) =>
        new(LiveScanProtocol.Version, session, kind, JsonSerializer.SerializeToElement(payload));

    internal sealed class CaptureProgress : IProgress<LiveScanProgressDto>
    {
        public List<LiveScanProgressDto> Values { get; } = [];
        public void Report(LiveScanProgressDto value) => Values.Add(value);
    }

    internal sealed class InstrumentedFactory(byte[] bytes) : ILiveVolumeSourceFactory, ILiveVolumeReader
    {
        public Action? BeforePost { get; set; }
        public int? FailAfterBytes { get; init; }
        public SafeFileHandle? Handle { get; private set; }
        public long BytesRead { get; private set; }
        public long Budget { get; private set; }
        public List<(long Offset, int Length)> Reads { get; } = [];
        private int _headers;
        public IReadOnlyRandomAccessSource Open(LiveScanTargetGrant grant, long maximumTotalBytes)
        {
            Budget = maximumTotalBytes;
            Handle = new SafeFileHandle(new IntPtr(12345), ownsHandle: false);
            return new LiveVolumeRandomAccessSource(Handle, bytes.Length, maximumTotalBytes,
                LiveScanProtocol.MaximumIndividualReadBytes, this);
        }
        public ValueTask<int> ReadAsync(SafeFileHandle handle, Memory<byte> destination, long offset, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (offset == 0 && destination.Length == 512 && ++_headers == 2) BeforePost?.Invoke();
            token.ThrowIfCancellationRequested();
            if (FailAfterBytes is { } limit && BytesRead >= limit) throw new IOException("Simulated partial native read failure");
            var count = FailAfterBytes is { } end ? (int)Math.Min(destination.Length, end - BytesRead) : destination.Length;
            bytes.AsMemory(checked((int)offset), count).CopyTo(destination);
            Reads.Add((offset, count));
            BytesRead += count;
            return ValueTask.FromResult(count);
        }
    }

    private sealed class AcceptingValidator : ILiveScanTargetValidator
    {
        public Task<ValidatedLiveScanTarget> ValidateAsync(LiveScanTargetGrant grant, CancellationToken cancellationToken) =>
            Task.FromResult(new ValidatedLiveScanTarget(grant));
    }
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
