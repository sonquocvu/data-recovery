using System.Security.Cryptography;
using DataRecoveryStudio.Application;
using DataRecoveryStudio.Core;
using DataRecoveryStudio.Infrastructure;

namespace DataRecoveryStudio.Tests;

public sealed class Phase8AExFatScanTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static async Task<ExFatScanResult> Scan(ExFatTestFixtureBuilder fixture, ExFatScanBudget? budget = null,
        IProgress<ExFatScanProgress>? progress = null, CancellationToken token = default)
    {
        await using var source = new AuditSource(new MemoryRandomAccessSource(fixture.Bytes), fixture.PayloadRanges);
        return await new ExFatMetadataScanner().ScanAsync(source, new(Budget: budget), progress, token);
    }
    private static ExFatScanCandidate Candidate(ExFatScanResult result, string name = "deleted.txt") => Assert.Single(result.Candidates.Where(c => c.DisplayName == name));

    [Theory]
    [InlineData(512, 1)]
    [InlineData(1024, 1)]
    [InlineData(2048, 1)]
    [InlineData(4096, 1)]
    [InlineData(512, 8)]
    [InlineData(4096, 4)]
    public async Task ValidGeometryMetadataAndDeletedName(int sector, int spc)
    {
        var f = new ExFatTestFixtureBuilder(sector, spc);
        f.AddFile("deleted.txt", 20, 513);
        var result = await Scan(f);
        Assert.Equal(ExFatScanOutcome.Completed, result.Outcome);
        Assert.Equal(sector, result.Geometry!.BytesPerSector);
        Assert.Equal(sector * spc, result.Geometry.ClusterSize);
        Assert.True(result.BootEvidence!.MainValid);
        Assert.True(result.BootEvidence.BackupValid);
        Assert.False(result.BootEvidence.UsedBackup);
        Assert.Equal("PHASE8A", result.VolumeLabel);
        var c = Candidate(result);
        Assert.Equal(ExFatNameEvidence.ChecksumRecoveredName, c.NameEvidence);
        Assert.Equal(ExFatAllocationEvidence.ContiguousAllFree, c.AllocationEvidence);
        Assert.Equal(513, c.LogicalSize);
        Assert.Equal("deleted.txt", c.Provenance!.OriginalName);
        Assert.True(c.Provenance.RecoveredChecksumValid);
        Assert.False(c.Provenance.OrdinaryChecksumValid);
        Assert.Equal(420, c.Created.UtcOffsetMinutes);
        Assert.Equal(ExFatTimestampState.Valid, c.Created.State);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("jump")]
    [InlineData("zero")]
    [InlineData("revision")]
    [InlineData("sector")]
    [InlineData("cluster")]
    [InlineData("fat-offset")]
    [InlineData("heap")]
    [InlineData("fat-small")]
    [InlineData("root")]
    [InlineData("overflow")]
    [InlineData("zero-clusters")]
    [InlineData("two-fats")]
    [InlineData("no-fats")]
    [InlineData("flags")]
    [InlineData("signature")]
    [InlineData("extended")]
    [InlineData("reserved")]
    [InlineData("oem-tail")]
    [InlineData("checksum")]
    public async Task InvalidBootRegionsFailClosed(string defect)
    {
        var f = new ExFatTestFixtureBuilder();
        for (var copy = 0; copy < 2; copy++)
        {
            var o = copy * f.SectorSize * 12;
            switch (defect)
            {
                case "identity": f.Bytes[o + 3] = (byte)'N'; break;
                case "jump": f.Bytes[o] = 0; break;
                case "zero": f.Bytes[o + 20] = 1; break;
                case "revision": f.W16(o + 104, 0x200); break;
                case "sector": f.Bytes[o + 108] = 8; break;
                case "cluster": f.Bytes[o + 109] = 30; break;
                case "fat-offset": f.W32(o + 80, uint.MaxValue); break;
                case "heap": f.W32(o + 88, uint.MaxValue); break;
                case "fat-small": f.W32(o + 84, 1); break;
                case "root": f.W32(o + 96, f.ClusterCount + 2); break;
                case "overflow": f.W64(o + 72, ulong.MaxValue); break;
                case "zero-clusters": f.W32(o + 92, 0); break;
                case "two-fats": f.Bytes[o + 110] = 2; break;
                case "no-fats": f.Bytes[o + 110] = 0; break;
                case "flags": f.W16(o + 106, 1); break;
                case "signature": f.W16(o + 510, 0); break;
                case "extended": f.W32(o + 1020, 0); break;
                case "reserved": f.Bytes[o + f.SectorSize * 10] = 1; break;
                case "oem-tail": f.Bytes[o + f.SectorSize * 9 + 480] = 1; break;
            }
            f.RechecksumBoot(copy == 1);
            if (defect == "checksum") f.Bytes[o + f.SectorSize * 11] ^= 1;
        }
        var result = await Scan(f);
        Assert.Equal(ExFatScanOutcome.InvalidVolume, result.Outcome);
        Assert.Null(result.Geometry);
        Assert.Empty(result.Candidates);
    }

    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(2048)]
    [InlineData(4096)]
    public async Task BackupFallbackIsIndependentEvenWithUnknownMainSectorShift(int sector)
    {
        var f = new ExFatTestFixtureBuilder(sector);
        f.Bytes[108] = 0;
        var result = await Scan(f);
        Assert.True(result.BootEvidence!.UsedBackup);
        Assert.Equal(sector, result.Geometry!.BytesPerSector);
        Assert.Contains(result.Diagnostics, d => d.Code == "BACKUP_MUTABLE_FLAGS_STALE");
    }

    [Fact]
    public async Task MainPreferredInvalidBackupAndDisagreementAreExplicit()
    {
        var f = new ExFatTestFixtureBuilder();
        f.Bytes[12 * 512 + 510] = 0;
        var result = await Scan(f);
        Assert.Equal(ExFatScanOutcome.Completed, result.Outcome);
        Assert.False(result.BootEvidence!.UsedBackup);
        f = new();
        f.W32(12 * 512 + 96, 5); f.RechecksumBoot(true);
        result = await Scan(f);
        Assert.Equal(ExFatScanOutcome.InvalidVolume, result.Outcome);
        Assert.Contains(result.Diagnostics, d => d.Code == "BOOT_GEOMETRY_DISAGREEMENT");
    }

    [Theory]
    [InlineData(106, 8)]
    [InlineData(112, 73)]
    [InlineData(112, 255)]
    [InlineData(112, 201)]
    public async Task MutableChecksumBytesAreExcludedAndPercentIsAdvisory(int offset, byte value)
    {
        var f = new ExFatTestFixtureBuilder(); f.Bytes[offset] = value;
        f.AddFile("deleted.txt", 20, 10);
        var result = await Scan(f);
        Assert.True(result.BootEvidence!.MainValid);
        Assert.Equal(ExFatAllocationEvidence.ContiguousAllFree, Candidate(result).AllocationEvidence);
    }

    [Fact]
    public async Task TruncatedImageAndOffsetOverflowFailSafely()
    {
        var f = new ExFatTestFixtureBuilder();
        await using var source = new MemoryRandomAccessSource(f.Bytes[..^1]);
        var scanner = new ExFatMetadataScanner();
        Assert.Equal(ExFatScanOutcome.InvalidVolume, (await scanner.ScanAsync(source, new(), null, default)).Outcome);
        Assert.Equal(ExFatScanOutcome.InvalidVolume, (await scanner.ScanAsync(source, new(long.MaxValue), null, default)).Outcome);
    }

    [Theory]
    [InlineData("missing-bitmap")]
    [InlineData("bitmap-length")]
    [InlineData("bitmap-duplicate")]
    [InlineData("bitmap-conflict")]
    [InlineData("upcase-missing")]
    [InlineData("upcase-duplicate")]
    [InlineData("upcase-checksum")]
    [InlineData("upcase-short")]
    public async Task RequiredMetadataDamageDegradesEvidence(string defect)
    {
        var f = new ExFatTestFixtureBuilder();
        f.AddFile("deleted.txt", 20, 10);
        switch (defect)
        {
            case "missing-bitmap": f.Bytes[f.RootSlot(0)] = 1; break;
            case "bitmap-length": f.W64(f.RootSlot(0) + 24, 1); break;
            case "bitmap-duplicate": f.AddRaw(2, f.Bytes.AsSpan(f.RootSlot(0), 32).ToArray()); break;
            case "bitmap-conflict": f.W32(f.RootSlot(0) + 20, 4); break;
            case "upcase-missing": f.Bytes[f.RootSlot(1)] = 2; break;
            case "upcase-duplicate": f.AddRaw(2, f.Bytes.AsSpan(f.RootSlot(1), 32).ToArray()); break;
            case "upcase-checksum": f.Bytes[f.Offset(4)] ^= 1; break;
            case "upcase-short": f.W64(f.RootSlot(1) + 24, 10); break;
        }
        var result = await Scan(f);
        Assert.Equal(ExFatScanOutcome.Partial, result.Outcome);
        Assert.NotEqual(ExFatRecoverabilityState.AllocationSuggestsPossibleContent, Candidate(result).Recoverability);
        if (defect.StartsWith("upcase", StringComparison.Ordinal)) Assert.Equal(ExFatNameEvidence.ProbableName, Candidate(result).NameEvidence);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NestedDirectoriesAndCrossClusterEntrySets(bool contiguous)
    {
        var f = new ExFatTestFixtureBuilder();
        f.PadToSlot(2, 15);
        f.AddFile("crossing-long-deleted-name.txt", 20, 12);
        f.DirectoryChain(10, 10, 11);
        f.AddFile("folder", 10, 1024, deleted: false, contiguous: contiguous, directory: true);
        f.PadToSlot(10, 15);
        f.AddFile("nested-deleted-name.txt", 24, 513, parent: 10);
        f.DirectoryChain(12, 12);
        f.AddFile("child", 12, 512, deleted: false, directory: true, parent: 10);
        f.AddFile("deep.txt", 26, 12, parent: 12);
        f.AddFile("ACTIVE.txt", 30, 10, deleted: false); f.SetAllocated(30);
        f.AddFile("DELETED-DIR", 31, 512, directory: true);
        var result = await Scan(f);
        Assert.Equal(ExFatScanOutcome.Completed, result.Outcome);
        Assert.Equal(3, result.Candidates.Count);
        Assert.Equal(3, result.Metrics.DirectoriesVisited);
        Assert.Equal("\\folder\\child\\", Candidate(result, "deep.txt").ParentPath);
        Assert.Equal(result.Candidates.Count, result.Candidates.Select(c => c.CandidateId).Distinct().Count());
    }

    [Theory]
    [InlineData("secondary")]
    [InlineData("stream")]
    [InlineData("filename")]
    [InlineData("orphan")]
    [InlineData("utf16")]
    [InlineData("null")]
    [InlineData("name-length")]
    [InlineData("extra")]
    [InlineData("duplicate-stream")]
    [InlineData("critical-secondary")]
    [InlineData("length-overflow")]
    [InlineData("valid-length")]
    [InlineData("zero-first")]
    [InlineData("flags")]
    [InlineData("mixed-in-use")]
    public async Task IncoherentEntrySetsDoNotBecomeCandidates(string defect)
    {
        var f = new ExFatTestFixtureBuilder();
        var o = f.AddFile("deleted.txt", 20, 513);
        switch (defect)
        {
            case "secondary": f.Bytes[o + 1] = 1; break;
            case "stream": f.Bytes[o + 32] = 0x41; break;
            case "filename": f.Bytes[o + 64] = 0x42; break;
            case "orphan": f.Bytes[o] = 1; break;
            case "utf16": f.W16(o + 66, 0xD800); break;
            case "null": f.W16(o + 66, 0); break;
            case "name-length": f.Bytes[o + 35] = 0; break;
            case "extra": f.W16(o + 94, 1); break;
            case "duplicate-stream": f.Bytes[o + 64] = 0x40; break;
            case "critical-secondary": f.Bytes[o + 1] = 3; var b = new byte[32]; b[0] = 0x42; f.AddRaw(2, b); break;
            case "length-overflow": f.W64(o + 56, ulong.MaxValue); break;
            case "valid-length": f.W64(o + 40, 514); break;
            case "zero-first": f.W32(o + 52, 0); break;
            case "flags": f.Bytes[o + 33] = 2; break;
            case "mixed-in-use": f.Bytes[o + 64] = 0xC1; break;
        }
        var result = await Scan(f);
        Assert.Empty(result.Candidates);
        Assert.Equal(ExFatScanOutcome.Partial, result.Outcome);
    }

    [Theory]
    [InlineData(false, false, ExFatNameEvidence.ChecksumRecoveredName)]
    [InlineData(true, false, ExFatNameEvidence.AmbiguousName)]
    [InlineData(false, true, ExFatNameEvidence.HashVerifiedName)]
    [InlineData(true, true, ExFatNameEvidence.AmbiguousName)]
    public async Task NameConfidenceSeparatesChecksumAndHash(bool wrongHash, bool badChecksum, ExFatNameEvidence expected)
    {
        var f = new ExFatTestFixtureBuilder(); f.AddFile("deleted.txt", 20, 10, wrongHash: wrongHash, badChecksum: badChecksum);
        var c = Candidate(await Scan(f));
        Assert.Equal(expected, c.NameEvidence);
        if (badChecksum) Assert.Equal(ExFatAllocationEvidence.DamagedMetadata, c.AllocationEvidence);
    }

    [Fact]
    public async Task OrdinaryDeletedChecksumIsDistinguishedAndTimestampDamageIsLocal()
    {
        var f = new ExFatTestFixtureBuilder(); var o = f.AddFile("deleted.txt", 20, 10);
        f.Bytes[o + 20] = 200;
        f.W16(o + 2, ExFatTestFixtureBuilder.Sum16(f.Bytes.AsSpan(o, 96).ToArray()));
        var c = Candidate(await Scan(f));
        Assert.Equal(ExFatNameEvidence.VerifiedDeletedName, c.NameEvidence);
        Assert.Equal(ExFatTimestampState.Invalid, c.Created.State);
        Assert.Equal(ExFatTimestampState.Valid, c.Modified.State);
        Assert.True(c.Provenance!.OrdinaryChecksumValid);
    }

    [Theory]
    [InlineData(true, 0, ExFatAllocationEvidence.ContiguousAllFree)]
    [InlineData(true, 1, ExFatAllocationEvidence.ContiguousPartiallyAllocated)]
    [InlineData(true, 2, ExFatAllocationEvidence.ContiguousFullyAllocated)]
    [InlineData(false, 0, ExFatAllocationEvidence.PreservedChainAllFree)]
    [InlineData(false, 1, ExFatAllocationEvidence.PreservedChainPartiallyAllocated)]
    [InlineData(false, 2, ExFatAllocationEvidence.PreservedChainFullyAllocated)]
    public async Task BitmapAuthorityForContiguousAndPreservedChains(bool contiguous, int allocated, ExFatAllocationEvidence expected)
    {
        var f = new ExFatTestFixtureBuilder();
        var last = contiguous ? 21u : 24u;
        f.AddFile("deleted.txt", 20, 513, contiguous: contiguous);
        f.SetFat(20, last); f.SetFat(last, 0xFFFFFFFF);
        if (!contiguous) { f.PayloadRanges.Add((f.Offset(20), 512)); f.PayloadRanges.Add((f.Offset(last), 512)); }
        if (allocated > 0) f.SetAllocated(20);
        if (allocated > 1) f.SetAllocated(last);
        var c = Candidate(await Scan(f));
        Assert.Equal(expected, c.AllocationEvidence);
        Assert.Equal(513, c.LogicalSize);
    }

    [Theory]
    [InlineData(0u, ExFatAllocationEvidence.FatChainMissing)]
    [InlineData(20u, ExFatAllocationEvidence.FatChainCycle)]
    [InlineData(0xFFFFFFF7u, ExFatAllocationEvidence.FatChainDamaged)]
    [InlineData(0xFFFFFFF8u, ExFatAllocationEvidence.FatChainDamaged)]
    [InlineData(1u, ExFatAllocationEvidence.FatChainDamaged)]
    [InlineData(99999u, ExFatAllocationEvidence.FatChainDamaged)]
    [InlineData(0xFFFFFFFFu, ExFatAllocationEvidence.FatChainDamaged)]
    public async Task BrokenChainsNeverGuessFragments(uint next, ExFatAllocationEvidence expected)
    {
        var f = new ExFatTestFixtureBuilder(); f.AddFile("deleted.txt", 20, 513, contiguous: false); f.SetFat(20, next);
        Assert.Equal(expected, Candidate(await Scan(f)).AllocationEvidence);
    }

    [Fact]
    public async Task ZeroLengthOwnershipConflictAndIncompleteOwnership()
    {
        var f = new ExFatTestFixtureBuilder();
        f.AddFile("zero.txt", 0, 0); f.AddFile("deleted.txt", 20, 10);
        f.AddFile("ACTIVE.txt", 20, 10, deleted: false); f.SetAllocated(20);
        var result = await Scan(f);
        Assert.Equal(ExFatAllocationEvidence.ZeroLength, Candidate(result, "zero.txt").AllocationEvidence);
        Assert.Equal(ExFatAllocationEvidence.ActiveOwnershipConflict, Candidate(result).AllocationEvidence);
        f = new(); f.AddFile("deleted.txt", 20, 10);
        f.AddFile("broken-active.txt", 30, 1000, deleted: false, contiguous: false);
        Assert.Equal(ExFatAllocationEvidence.Unknown, Candidate(await Scan(f)).AllocationEvidence);
    }

    [Fact]
    public async Task DirectoryCycleAndCrossLinkAreIsolated()
    {
        var f = new ExFatTestFixtureBuilder();
        f.DirectoryChain(10, 10); f.AddFile("folder", 10, 512, deleted: false, directory: true);
        f.AddFile("self", 10, 512, deleted: false, directory: true, parent: 10);
        f.AddFile("deleted.txt", 20, 10);
        var result = await Scan(f);
        Assert.Equal(ExFatScanOutcome.Partial, result.Outcome);
        Assert.Equal(2, result.Metrics.DirectoriesVisited);
        Assert.Contains(result.Diagnostics, d => d.Code == "ACTIVE_CROSS_LINK");
        Assert.Equal(ExFatAllocationEvidence.Unknown, Candidate(result).AllocationEvidence);
    }

    public static IEnumerable<object[]> Budgets()
    {
        yield return [new ExFatScanBudget(MaximumSourceBytes: 511), "SourceBytes"];
        yield return [new ExFatScanBudget(MaximumDuration: TimeSpan.FromTicks(1)), "Duration"];
        yield return [new ExFatScanBudget(MaximumCandidates: 0), "Candidates"];
        yield return [new ExFatScanBudget(MaximumChainLength: 1), "ChainLength"];
        yield return [new ExFatScanBudget(MaximumDirectories: 0), "Directories"];
        yield return [new ExFatScanBudget(MaximumDirectoryBytes: 0), "DirectoryBytes"];
        yield return [new ExFatScanBudget(MaximumEntries: 0), "Entries"];
        yield return [new ExFatScanBudget(MaximumFatEntries: 0), "FatEntries"];
        yield return [new ExFatScanBudget(MaximumChains: 0), "Chains"];
        yield return [new ExFatScanBudget(MaximumVisitedClusters: 0), "VisitedClusters"];
        yield return [new ExFatScanBudget(MaximumBitmapQueries: 0), "BitmapQueries"];
        yield return [new ExFatScanBudget(MaximumUpCaseBytes: 0), "UpCaseBytes"];
        yield return [new ExFatScanBudget(MaximumUpCaseCacheBytes: 0), "UpCaseCache"];
        yield return [new ExFatScanBudget(MaximumSecondaryCount: 1), "SecondaryCount"];
        yield return [new ExFatScanBudget(MaximumTotalFilenameCharacters: 0), "FilenameCharacters"];
        yield return [new ExFatScanBudget(MaximumFilenameLength: 0), "FilenameLength"];
        yield return [new ExFatScanBudget(MaximumOwnershipRecords: 0), "OwnershipRecords"];
    }
    [Theory]
    [MemberData(nameof(Budgets))]
    public async Task HardBudgetsProduceExplicitStablePartial(ExFatScanBudget budget, string limit)
    {
        var f = new ExFatTestFixtureBuilder(); f.AddFile("deleted.txt", 20, 10);
        var result = await Scan(f, budget);
        Assert.Equal(ExFatScanOutcome.Partial, result.Outcome);
        Assert.True(result.Metrics.BudgetLimited);
        Assert.Contains(result.Diagnostics, d => d.Code == "BUDGET_" + limit);
    }

    [Fact]
    public async Task DepthAndDiagnosticBudgetsAreEnforced()
    {
        var f = new ExFatTestFixtureBuilder(); f.DirectoryChain(10, 10);
        f.AddFile("folder", 10, 512, deleted: false, directory: true);
        var result = await Scan(f, new(MaximumDirectoryDepth: 0));
        Assert.Contains(result.Diagnostics, d => d.Code == "BUDGET_DirectoryDepth");
        f = new(); var b = new byte[32]; b[0] = 0x80;
        f.AddRaw(2, b); f.AddRaw(2, b);
        result = await Scan(f, new(MaximumDiagnostics: 1));
        Assert.Single(result.Diagnostics);
        Assert.Equal("BUDGET_Diagnostics", result.Diagnostics[0].Code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PathStorageHasExplicitHardBudgets(bool individual)
    {
        var f = new ExFatTestFixtureBuilder(); f.DirectoryChain(10, 10);
        f.AddFile("folder", 10, 512, deleted: false, directory: true);
        var result = await Scan(f, individual ? new(MaximumPathCharacters: 1) : new(MaximumTotalPathCharacters: 1));
        Assert.Equal(ExFatScanOutcome.Partial, result.Outcome);
        Assert.Contains(result.Diagnostics, d => d.Code == (individual ? "BUDGET_PathCharacters" : "BUDGET_TotalPathCharacters"));
    }

    [Fact]
    public async Task CaseFoldedActiveDuplicateNamesInvalidateOwnership()
    {
        var f = new ExFatTestFixtureBuilder();
        f.AddFile("active", 30, 10, deleted: false); f.SetAllocated(30);
        f.AddFile("ACTIVE", 31, 10, deleted: false); f.SetAllocated(31);
        f.AddFile("deleted.txt", 20, 10);
        var result = await Scan(f);
        Assert.Contains(result.Diagnostics, d => d.Code == "DUPLICATE_ACTIVE_NAME");
        Assert.True(Candidate(result).IsPartial);
        Assert.Equal(ExFatAllocationEvidence.Unknown, Candidate(result).AllocationEvidence);
    }

    [Fact]
    public void BudgetCeilingsCannotBeRaisedAndTerminalSlotsAreReserved()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExFatScanBudget(MaximumSourceBytes: long.MaxValue).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExFatScanBudget(MaximumDiagnostics: 0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExFatScanBudget(MaximumProgressCallbacks: 0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExFatScanBudget(MaximumCandidates: -1).Validate());
    }

    [Fact]
    public async Task CacheEvictionAndProgressLimitsPreserveResults()
    {
        var f = new ExFatTestFixtureBuilder();
        for (uint i = 20; i < 28; i++) f.AddFile("file" + i, i * 4, 10);
        var normal = await Scan(f);
        var progress = new CaptureProgress();
        var bounded = await Scan(f, new(MaximumFatCacheBytes: 4, MaximumBitmapCacheBytes: 1, MaximumProgressCallbacks: 3), progress);
        Assert.Equal(normal.Candidates.Select(c => c.CandidateId), bounded.Candidates.Select(c => c.CandidateId));
        Assert.True(progress.Values.Count <= 3);
        Assert.Single(progress.Values.Where(p => p.Phase == ExFatScanPhase.Terminal));
        AssertMonotonic(progress.Values);
    }

    [Theory]
    [InlineData(ExFatScanPhase.Boot)]
    [InlineData(ExFatScanPhase.Directories)]
    [InlineData(ExFatScanPhase.UpCase)]
    [InlineData(ExFatScanPhase.Allocation)]
    public async Task CancellationAtPhaseBoundariesHasOneTerminal(ExFatScanPhase phase)
    {
        var f = new ExFatTestFixtureBuilder(); f.AddFile("deleted.txt", 20, 10);
        using var cts = new CancellationTokenSource();
        var progress = new CaptureProgress(p => { if (p.Phase == phase) cts.Cancel(); });
        var result = await Scan(f, progress: progress, token: cts.Token);
        Assert.Equal(ExFatScanOutcome.Canceled, result.Outcome);
        Assert.Single(progress.Values.Where(p => p.Phase == ExFatScanPhase.Terminal));
        AssertMonotonic(progress.Values);
    }

    [Fact]
    public async Task LocalReadFailureNeverBecomesFreeBitmapEvidence()
    {
        var f = new ExFatTestFixtureBuilder(); f.AddFile("deleted.txt", 20, 10);
        await using var source = new AuditSource(new MemoryRandomAccessSource(f.Bytes), f.PayloadRanges,
            offset => { if (offset >= f.Offset(3) && offset < f.Offset(3) + 256) throw new IOException("injected bitmap read failure"); });
        var result = await new ExFatMetadataScanner().ScanAsync(source, new(), null, default);
        Assert.Equal(ExFatAllocationEvidence.Unknown, Candidate(result).AllocationEvidence);
        Assert.Equal(ExFatScanOutcome.Partial, result.Outcome);
    }

    [Fact]
    public void UpCaseExpansionIsBoundedAndUsesImageMappings()
    {
        var f = new ExFatTestFixtureBuilder();
        var bytes = f.Bytes.AsSpan(f.Offset(4), 262).ToArray();
        var table = ExFatStructures.ExpandUpCase(bytes, ExFatTestFixtureBuilder.Sum32(bytes), default);
        Assert.Equal(65536, table.Length);
        Assert.Equal((ushort)'A', table['a']);
        Assert.Equal((ushort)'é', table['é']); // Fixture's on-disk mapping deliberately differs from host Unicode.
        Assert.NotEqual(ExFatStructures.NameHash("é", table, default), ExFatStructures.NameHash("É", table, default));
        ExFatTestFixtureBuilder.Put16(bytes, 258, ushort.MaxValue);
        Assert.Throws<InvalidDataException>(() => ExFatStructures.ExpandUpCase(bytes, ExFatTestFixtureBuilder.Sum32(bytes), default));
    }

    [Theory]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\?\Volume{11111111-1111-1111-1111-111111111111}\")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume1")]
    [InlineData("PhysicalDrive0")]
    [InlineData("HarddiskVolume1")]
    [InlineData(@"\\.\pipe\test")]
    [InlineData(@"C:\")]
    [InlineData(@"C:\ordinary.img:stream")]
    [InlineData("GLOBALROOT")]
    public async Task UnsafeSourcesRejectedBeforeOpener(string path)
    {
        var opener = new NeverOpener(); var factory = new ExFatImageSourceFactory(opener);
        await Assert.ThrowsAnyAsync<Exception>(async () => await factory.OpenAsync(path, default));
        Assert.Equal(0, opener.Calls);
    }

    [Fact]
    public async Task MissingDirectoryAndReparseSourcesRejectedBeforeOpener()
    {
        var opener = new NeverOpener(); var factory = new ExFatImageSourceFactory(opener);
        await Assert.ThrowsAnyAsync<Exception>(async () => await factory.OpenAsync(Path.GetTempPath(), default));
        await Assert.ThrowsAnyAsync<Exception>(async () => await factory.OpenAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".img"), default));
        Assert.Equal(0, opener.Calls);
    }

    [Fact]
    public async Task EndToEndProductionServiceProvesMetadataOnlyReadsAndUnchangedSource()
    {
        var f = EndToEndFixture();
        using var file = new ImageFile(f.Bytes);
        var before = Fingerprint(file.Path);
        var scanner = new AuditedScanner(f.PayloadRanges);
        var service = new ExFatImageScanService(new ExFatImageSourceFactory(), scanner, new ExFatSourceMetadataProvider());
        var progress = new CaptureProgress();
        var first = await service.ScanAsync(file.Path, new(), progress, default);
        var second = await service.ScanAsync(file.Path, new(), null, default);
        Assert.Equal(ExFatScanOutcome.Completed, first.Result.Outcome);
        Assert.NotNull(first.Session);
        Assert.Equal(first.Result.Candidates.Select(c => c.CandidateId), second.Result.Candidates.Select(c => c.CandidateId));
        Assert.Equal(5, first.Result.Candidates.Count);
        Assert.Equal(ExFatAllocationEvidence.ZeroLength, Candidate(first.Result, "zero.txt").AllocationEvidence);
        Assert.Equal(ExFatAllocationEvidence.ContiguousAllFree, Candidate(first.Result, "free.txt").AllocationEvidence);
        Assert.Equal(ExFatAllocationEvidence.PreservedChainAllFree, Candidate(first.Result, "chain.txt").AllocationEvidence);
        Assert.Equal(ExFatNameEvidence.AmbiguousName, Candidate(first.Result, "ambiguous.txt").NameEvidence);
        Assert.Equal(ExFatAllocationEvidence.ActiveOwnershipConflict, Candidate(first.Result, "conflict.txt").AllocationEvidence);
        Assert.DoesNotContain(first.Result.Candidates, c => c.DisplayName.StartsWith("ACTIVE", StringComparison.Ordinal) || c.DisplayName == "DELETED-DIR");
        Assert.True(scanner.Reads > 0);
        Assert.Equal(before, Fingerprint(file.Path));
        Assert.Equal(before.Sha256, first.Session.Source.Sha256);
        Assert.Equal(before.Length, first.Session.Source.Length);
        Assert.Equal(before.Ticks, first.Session.Source.LastWriteTimeUtcTicks);
        Assert.Single(System.IO.Directory.GetFiles(file.Directory));
        Assert.Equal(first.Session, await service.GetSessionAsync(first.Session.SessionId, default));
        Assert.True(service.DisposeSession(first.Session.SessionId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetSessionAsync(first.Session.SessionId, default));
        Assert.Single(progress.Values.Where(p => p.Phase == ExFatScanPhase.Terminal));
        AssertMonotonic(progress.Values);
        Assert.Equal(2L * before.Length, first.Result.Metrics.FingerprintBytesRead);
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            Proof = "Phase8A production image metadata scan",
            before.Sha256,
            before.Length,
            before.Ticks,
            AfterSha256 = Fingerprint(file.Path).Sha256,
            AfterLength = Fingerprint(file.Path).Length,
            AfterTicks = Fingerprint(file.Path).Ticks,
            Candidates = first.Result.Candidates.Count,
            MetadataReadCalls = scanner.Reads,
            CandidatePayloadOverlaps = 0,
            SourceDirectoryFiles = System.IO.Directory.GetFiles(file.Directory).Length,
            first.Result.Metrics.SourceBytesRead,
            first.Result.Metrics.FingerprintBytesRead,
            TrustedSession = first.Session is not null,
        }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedOrCanceledScansNeverPublishTrustedSession(bool cancel)
    {
        var f = EndToEndFixture(); using var file = new ImageFile(f.Bytes);
        using var cts = new CancellationTokenSource();
        var provider = new ChangedMetadataProvider(cancel ? cts : null);
        var service = new ExFatImageScanService(new ExFatImageSourceFactory(), new ExFatMetadataScanner(), provider);
        var progress = new CaptureProgress();
        var result = await service.ScanAsync(file.Path, new(), progress, cts.Token);
        Assert.Null(result.Session);
        Assert.Equal(cancel ? ExFatScanOutcome.Canceled : ExFatScanOutcome.SourceChanged, result.Result.Outcome);
        Assert.Single(progress.Values.Where(p => p.Phase == ExFatScanPhase.Terminal));
    }

    [Fact]
    public async Task PartialAndUntrustedScannerResultsDoNotPublishSessions()
    {
        var f = EndToEndFixture(); using var file = new ImageFile(f.Bytes);
        var service = new ExFatImageScanService(new ExFatImageSourceFactory(), new ExFatMetadataScanner(), new ExFatSourceMetadataProvider());
        var result = await service.ScanAsync(file.Path, new(Budget: new(MaximumCandidates: 1)), null, default);
        Assert.Null(result.Session);
        Assert.Equal(ExFatScanOutcome.Partial, result.Result.Outcome);
        service = new(new ExFatImageSourceFactory(), new CloningScanner(), new ExFatSourceMetadataProvider());
        result = await service.ScanAsync(file.Path, new(), null, default);
        Assert.Equal(ExFatScanOutcome.Completed, result.Result.Outcome);
        Assert.Null(result.Session);
    }

    [Fact]
    public async Task FatBitmapDisagreementAndActiveBitmapDamageFailClosed()
    {
        var f = new ExFatTestFixtureBuilder(); f.AddFile("deleted.txt", 20, 513, contiguous: false); f.SetAllocated(20);
        Assert.Equal(ExFatAllocationEvidence.FatBitmapDisagreement, Candidate(await Scan(f)).AllocationEvidence);
        f = new(); f.AddFile("deleted.txt", 20, 10); f.SetAllocated(2, false);
        var result = await Scan(f);
        Assert.Contains(result.Diagnostics, d => d.Code == "ACTIVE_BITMAP_DISAGREEMENT");
        Assert.Equal(ExFatAllocationEvidence.Unknown, Candidate(result).AllocationEvidence);
    }

    [Fact]
    public async Task EmptyDirectoryAndBenignGuidAndSecondaryAreSupported()
    {
        var f = new ExFatTestFixtureBuilder(); f.AddFile("empty-folder", 0, 0, deleted: false, directory: true);
        var guid = new byte[32]; guid[0] = 0xA0; guid[6] = 1;
        ExFatTestFixtureBuilder.Put16(guid, 2, ExFatTestFixtureBuilder.Sum16(guid)); f.AddRaw(2, guid);
        var file = ExFatTestFixtureBuilder.FileSet("deleted.txt", 20, 10, false, true, false, 10);
        Array.Resize(ref file, file.Length + 32);
        file[1]++; file[^32] = 0xE0;
        ExFatTestFixtureBuilder.Put16(file, 2, ExFatTestFixtureBuilder.Sum16(file));
        for (var i = 0; i < file.Length; i += 32) file[i] &= 0x7F;
        f.AddRaw(2, file);
        var result = await Scan(f);
        Assert.Equal(ExFatScanOutcome.Completed, result.Outcome);
        Assert.Equal(ExFatNameEvidence.ChecksumRecoveredName, Candidate(result).NameEvidence);
    }

    [Fact]
    public async Task ActiveChecksumsHashesAndDirectoryLengthsAreValidated()
    {
        foreach (var defect in new[] { "checksum", "hash", "length", "valid-length" })
        {
            var f = new ExFatTestFixtureBuilder(); f.DirectoryChain(10, 10);
            f.AddFile("folder", 10, defect == "length" ? 511 : 512, deleted: false, directory: true,
                valid: defect == "valid-length" ? 500 : null, wrongHash: defect == "hash", badChecksum: defect == "checksum");
            f.AddFile("deleted.txt", 20, 10);
            var result = await Scan(f);
            Assert.Equal(ExFatScanOutcome.Partial, result.Outcome);
            Assert.Equal(ExFatAllocationEvidence.Unknown, Candidate(result).AllocationEvidence);
        }
    }

    [Fact]
    public async Task TruncatedEntrySetStopsAtEndMarkerAndUnusedSlotsAreNotTraversed()
    {
        var f = new ExFatTestFixtureBuilder(); var o = f.AddFile("deleted.txt", 20, 10); f.Bytes[o + 64] = 0;
        var result = await Scan(f);
        Assert.Empty(result.Candidates);
        Assert.Contains(result.Diagnostics, d => d.Code == "TRUNCATED_ENTRY_SET");
    }

    [Fact]
    public async Task VolumeOffsetAndSerialMetadataDoNotChangeMappings()
    {
        var f = new ExFatTestFixtureBuilder(); f.AddFile("deleted.txt", 20, 10);
        f.W64(64, ulong.MaxValue); f.RechecksumBoot(false);
        f.W64(512 * 12 + 64, ulong.MaxValue); f.W32(512 * 12 + 100, 7); f.RechecksumBoot(true);
        var bytes = new byte[f.Bytes.Length + 4096]; f.Bytes.CopyTo(bytes, 4096);
        await using var source = new MemoryRandomAccessSource(bytes);
        var result = await new ExFatMetadataScanner().ScanAsync(source, new(4096), null, default);
        Assert.Equal(ExFatScanOutcome.Completed, result.Outcome);
        Assert.Equal(ulong.MaxValue, result.Geometry!.PartitionOffset);
        Assert.Contains(result.Diagnostics, d => d.Code == "BOOT_SERIAL_DISAGREEMENT");
        Assert.Equal(ExFatAllocationEvidence.ContiguousAllFree, Candidate(result).AllocationEvidence);
    }

    [Theory]
    [InlineData(0x80, 0)]
    [InlineData(0xBF, 945)]
    [InlineData(0xC0, -960)]
    [InlineData(0xFF, -15)]
    public void TimestampOffsetsPreserveFullExFatRange(byte offset, int minutes)
    {
        var timestamp = ExFatStructures.Timestamp((uint)(46 << 25 | 9 << 21 | 5 << 16), 199, offset);
        Assert.Equal(ExFatTimestampState.Valid, timestamp.State);
        Assert.Equal(minutes, timestamp.UtcOffsetMinutes);
    }

    [Fact]
    public void UpCaseSupportsUncompressedAndRejectsIncompleteOrInvalidAscii()
    {
        var table = new byte[131072];
        for (var i = 0; i < 65536; i++) ExFatTestFixtureBuilder.Put16(table, i * 2, (ushort)(i is >= 97 and <= 122 ? i - 32 : i));
        Assert.Equal((ushort)0xFFFF, ExFatStructures.ExpandUpCase(table, ExFatTestFixtureBuilder.Sum32(table), default)[65535]);
        Assert.Throws<InvalidDataException>(() => ExFatStructures.ExpandUpCase(table[..^2], ExFatTestFixtureBuilder.Sum32(table[..^2]), default));
        table[0] = 1;
        Assert.Throws<InvalidDataException>(() => ExFatStructures.ExpandUpCase(table, ExFatTestFixtureBuilder.Sum32(table), default));
    }

    [Fact]
    public async Task ReadCancellationAndDeadlineInterruptBlockedReads()
    {
        await using var source = new BlockingSource();
        var result = await new ExFatMetadataScanner().ScanAsync(source, new(Budget: new(MaximumDuration: TimeSpan.FromMilliseconds(20))), null, default);
        Assert.Equal(ExFatScanOutcome.Partial, result.Outcome);
        Assert.Contains(result.Diagnostics, d => d.Code == "BUDGET_Duration");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        result = await new ExFatMetadataScanner().ScanAsync(source, new(), null, cts.Token);
        Assert.Equal(ExFatScanOutcome.Canceled, result.Outcome);
    }

    [Theory]
    [InlineData(ExFatScanPhase.Fingerprinting)]
    [InlineData(ExFatScanPhase.Finalizing)]
    [InlineData(ExFatScanPhase.Ownership)]
    public async Task ServiceCancellationAtPublicationBoundariesHasNoSession(ExFatScanPhase phase)
    {
        using var file = new ImageFile(EndToEndFixture().Bytes);
        using var cts = new CancellationTokenSource();
        var progress = new CaptureProgress(p => { if (p.Phase == phase) cts.Cancel(); });
        var service = new ExFatImageScanService(new ExFatImageSourceFactory(), new ExFatMetadataScanner(), new ExFatSourceMetadataProvider());
        var result = await service.ScanAsync(file.Path, new(), progress, cts.Token);
        Assert.Null(result.Session);
        Assert.Equal(ExFatScanOutcome.Canceled, result.Result.Outcome);
        Assert.Single(progress.Values.Where(p => p.Phase == ExFatScanPhase.Terminal));
    }

    [Fact]
    public async Task OpenImageDisallowsConcurrentWritesAndSessionsRejectLaterChanges()
    {
        using var file = new ImageFile(EndToEndFixture().Bytes);
        await using (var source = await new ExFatImageSourceFactory().OpenAsync(file.Path, default))
        {
            Assert.Throws<IOException>(() => { using var writer = new FileStream(file.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
        }
        var service = new ExFatImageScanService(new ExFatImageSourceFactory(), new ExFatMetadataScanner(), new ExFatSourceMetadataProvider());
        var result = await service.ScanAsync(file.Path, new(), null, default);
        Assert.NotNull(result.Session);
        File.SetLastWriteTimeUtc(file.Path, File.GetLastWriteTimeUtc(file.Path).AddSeconds(1));
        await Assert.ThrowsAsync<IOException>(() => service.GetSessionAsync(result.Session.SessionId, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetSessionAsync(result.Session.SessionId, default));
    }

    [Fact]
    public async Task JunctionAncestorIsRejectedBeforeOpener()
    {
        using var file = new ImageFile(EndToEndFixture().Bytes);
        var link = file.Directory + "-junction";
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link); start.ArgumentList.Add(file.Directory);
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        try
        {
            var opener = new NeverOpener();
            await Assert.ThrowsAsync<NotSupportedException>(async () => await new ExFatImageSourceFactory(opener).OpenAsync(Path.Combine(link, "fixture.img"), default));
            Assert.Equal(0, opener.Calls);
        }
        finally { System.IO.Directory.Delete(link); }
    }

    [Fact]
    public void ProjectAndNativeApiBoundariesAndManifestsRemainIntact()
    {
        var root = RepositoryRoot();
        foreach (var project in new[] { "Core", "Application", "Infrastructure" })
        {
            var text = File.ReadAllText(Path.Combine(root, "src", "DataRecoveryStudio." + project, "DataRecoveryStudio." + project + ".csproj"));
            Assert.DoesNotContain("DataRecoveryStudio.App\\", text, StringComparison.Ordinal);
            Assert.DoesNotContain("DataRecoveryStudio.ScanWorker", text, StringComparison.Ordinal);
            foreach (var file in System.IO.Directory.GetFiles(Path.Combine(root, "src", "DataRecoveryStudio." + project), "ExFat*.cs"))
            {
                text = File.ReadAllText(file);
                foreach (var forbidden in new[] { "DllImport", "LibraryImport", "DeviceIoControl", "File.Write", "FileAccess.Write", "FileAccess.ReadWrite", "FileMode.Create", "FileMode.Truncate", "FileMode.Append", "LiveVolume", "ScanWorker", "System.Windows" })
                    Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
            }
        }
        foreach (var file in new[] { "DataRecoveryStudio.App/App.xaml.cs", "DataRecoveryStudio.ScanWorker/Program.cs" })
            Assert.DoesNotContain("ExFatMetadataScanner", File.ReadAllText(Path.Combine(root, "src", file)), StringComparison.Ordinal);
        Assert.Contains("level=\"asInvoker\"", File.ReadAllText(Path.Combine(root, "src/DataRecoveryStudio.App/app.manifest")), StringComparison.Ordinal);
        Assert.Contains("level=\"requireAdministrator\"", File.ReadAllText(Path.Combine(root, "src/DataRecoveryStudio.ScanWorker/app.manifest")), StringComparison.Ordinal);
        var source = File.ReadAllText(Path.Combine(root, "src/DataRecoveryStudio.Infrastructure/ReadOnlyRandomAccessSources.cs"));
        Assert.Contains("FileMode.Open", source, StringComparison.Ordinal);
        Assert.Contains("FileAccess.Read,", source, StringComparison.Ordinal);
        Assert.Contains("FileShare.Read,", source, StringComparison.Ordinal);
        Assert.DoesNotContain(typeof(ExFatScanCandidate).GetProperties(), p => p.Name is "FirstCluster" or "PrimaryOffset" or "Payload" or "Provenance");
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "DataRecoveryStudio.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException();
    }

    private sealed class CloningScanner : IExFatMetadataScanner
    {
        public async Task<ExFatScanResult> ScanAsync(IReadOnlyRandomAccessSource source, ExFatScanRequest request, IProgress<ExFatScanProgress>? progress, CancellationToken token) =>
            (await new ExFatMetadataScanner().ScanAsync(source, request, progress, token)) with { Outcome = ExFatScanOutcome.Completed };
    }
    private sealed class BlockingSource : IReadOnlyRandomAccessSource
    {
        public long Length => 1024 * 1024;
        public async ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken) => await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static ExFatTestFixtureBuilder EndToEndFixture()
    {
        var f = new ExFatTestFixtureBuilder();
        f.AddFile("zero.txt", 0, 0); f.AddFile("free.txt", 20, 513);
        f.AddFile("chain.txt", 24, 513, contiguous: false); f.SetFat(24, 26); f.SetFat(26, 0xFFFFFFFF);
        f.PayloadRanges.Add((f.Offset(24), 512)); f.PayloadRanges.Add((f.Offset(26), 512));
        f.AddFile("ambiguous.txt", 30, 10, wrongHash: true);
        f.AddFile("conflict.txt", 32, 10);
        f.AddFile("ACTIVE.txt", 32, 10, deleted: false); f.SetAllocated(32);
        f.DirectoryChain(10, 10); f.AddFile("ACTIVE-FOLDER", 10, 512, deleted: false, directory: true);
        f.AddFile("ACTIVE-NESTED.txt", 34, 10, deleted: false, parent: 10); f.SetAllocated(34);
        f.AddFile("DELETED-DIR", 35, 512, directory: true);
        return f;
    }
    private static void AssertMonotonic(IReadOnlyList<ExFatScanProgress> values)
    {
        for (var i = 1; i < values.Count; i++)
        {
            Assert.True(values[i].SourceBytesRead >= values[i - 1].SourceBytesRead);
            Assert.True(values[i].DirectoriesVisited >= values[i - 1].DirectoriesVisited);
            Assert.True(values[i].EntriesExamined >= values[i - 1].EntriesExamined);
            Assert.True(values[i].FatEntriesInspected >= values[i - 1].FatEntriesInspected);
            Assert.True(values[i].BitmapQueries >= values[i - 1].BitmapQueries);
            Assert.True(values[i].DeletedCandidatesFound >= values[i - 1].DeletedCandidatesFound);
        }
    }
    private static (string Sha256, long Length, long Ticks) Fingerprint(string path) =>
        (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), new FileInfo(path).Length, File.GetLastWriteTimeUtc(path).Ticks);
    private sealed class CaptureProgress(Action<ExFatScanProgress>? action = null) : IProgress<ExFatScanProgress>
    {
        internal List<ExFatScanProgress> Values { get; } = [];
        public void Report(ExFatScanProgress value) { Values.Add(value); action?.Invoke(value); }
    }
    private sealed class AuditSource(IReadOnlyRandomAccessSource inner, List<(long Offset, long Length)> prohibited,
        Action<long>? beforeRead = null, bool ownsSource = true) : IReadOnlyRandomAccessSource
    {
        internal int Reads;
        public long Length => inner.Length;
        public async ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
        {
            Assert.DoesNotContain(prohibited, range => offset < range.Offset + range.Length && range.Offset < offset + destination.Length);
            beforeRead?.Invoke(offset); Reads++;
            await inner.ReadExactlyAsync(offset, destination, cancellationToken);
        }
        public ValueTask DisposeAsync() => ownsSource ? inner.DisposeAsync() : ValueTask.CompletedTask;
    }
    private sealed class AuditedScanner(List<(long Offset, long Length)> prohibited) : IExFatMetadataScanner
    {
        internal int Reads;
        public async Task<ExFatScanResult> ScanAsync(IReadOnlyRandomAccessSource source, ExFatScanRequest request, IProgress<ExFatScanProgress>? progress, CancellationToken token)
        {
            await using var audited = new AuditSource(source, prohibited, ownsSource: false);
            var result = await new ExFatMetadataScanner().ScanAsync(audited, request, progress, token);
            Reads += audited.Reads;
            return result;
        }
    }
    private sealed class ChangedMetadataProvider(CancellationTokenSource? cancel) : IExFatSourceMetadataProvider
    {
        private int _calls;
        public async ValueTask<ExFatSourceFingerprint> CaptureAsync(string path, IReadOnlyRandomAccessSource source, ExFatScanRequest request, CancellationToken token)
        {
            var result = await new ExFatSourceMetadataProvider().CaptureAsync(path, source, request, token);
            if (++_calls == 2)
            {
                cancel?.Cancel();
                if (cancel is null) result = result with { Sha256 = new string('0', 64) };
            }
            return result;
        }
    }
    private sealed class NeverOpener : IRegularFileOpener
    {
        internal int Calls;
        public FileStream OpenRead(string fullPath) { Calls++; throw new InvalidOperationException("Opener must not run."); }
    }
    private sealed class ImageFile : IDisposable
    {
        internal string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "exfat-8a-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(Directory, "fixture.img");
        internal ImageFile(byte[] bytes) { System.IO.Directory.CreateDirectory(Directory); File.WriteAllBytes(Path, bytes); }
        public void Dispose() { File.Delete(Path); System.IO.Directory.Delete(Directory); }
    }
}
