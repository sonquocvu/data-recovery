using DataRecoveryStudio.Core;
using DataRecoveryStudio.Infrastructure;

namespace DataRecoveryStudio.Tests;

public sealed class Phase4BNtfsTraversalTests
{
    [Fact]
    public async Task MftSizeAndRecordCount_AreDerivedWithoutCallerCount()
    {
        var fixture = new NtfsTestFixtureBuilder(17).AddRoot();
        fixture.AddRecord(16, 3, false, false, fixture.FileName(5, 1, "last.txt", 1));

        var result = await ScanAsync(fixture);

        Assert.Equal(StandardScanOutcome.Completed, result.Outcome);
        Assert.Equal(17, result.MftLayout!.DerivedRecordCount);
        Assert.Equal(17L * fixture.RecordSize, result.MftLayout.LogicalSize);
        Assert.Equal(NtfsBootstrapSource.PrimaryMft, result.MftLayout.BootstrapSource);
        Assert.Equal(16, Assert.Single(result.Candidates).MftRecordNumber);
    }

    [Fact]
    public async Task FragmentedMft_WithMoreThanTwoSignedDeltaExtents_IsTraversed()
    {
        var fixture = new NtfsTestFixtureBuilder(12)
            .SetMftRuns((2, 2), (30, 4), (10, 6))
            .AddRoot();
        fixture.AddRecord(11, 4, false, false, fixture.FileName(5, 1, "fragmented.txt", 1));

        var result = await ScanAsync(fixture);

        Assert.Equal(StandardScanOutcome.Completed, result.Outcome);
        Assert.Equal(3, result.MftLayout!.ExtentCount);
        Assert.Equal("fragmented.txt", Assert.Single(result.Candidates).Name);
    }

    [Fact]
    public async Task MftRecord_CanCrossExtentBoundaryExactly()
    {
        var fixture = new NtfsTestFixtureBuilder(8, recordSizeEncoding: -11)
            .SetMftRuns((2, 3), (30, 5), (12, 8))
            .AddRoot();
        fixture.AddRecord(1, 2, false, false, fixture.FileName(5, 1, "cross-boundary.txt", 1));

        var result = await ScanAsync(fixture);

        Assert.Equal(2048, result.Geometry!.FileRecordSize);
        Assert.Equal("cross-boundary.txt", Assert.Single(result.Candidates).Name);
        Assert.Equal(StandardScanOutcome.Completed, result.Outcome);
    }

    [Fact]
    public async Task SparseMftExtent_IsRejectedAsStructuralDamage()
    {
        var result = await ScanAsync(new NtfsTestFixtureBuilder(8).MakeMftSparse().AddRoot());

        Assert.Equal(StandardScanOutcome.InvalidVolume, result.Outcome);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "NTFS_MFT_SPARSE_EXTENT");
    }

    [Fact]
    public async Task PhysicalMftExtentOutsideDeclaredVolume_IsRejected()
    {
        var fixture = new NtfsTestFixtureBuilder(8).AddRoot();
        var logical = 8L * fixture.RecordSize;
        fixture.AddRecord(0, 1, true, false,
            fixture.NonResidentData(NtfsTestFixtureBuilder.EncodeRunList((1000, 8)), logical, logical, logical, highestVcn: 7),
            fixture.FileName(5, 1, "$MFT", 1));

        var result = await ScanAsync(fixture);

        Assert.Equal(StandardScanOutcome.InvalidVolume, result.Outcome);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "NTFS_EXTENT_OUTSIDE_VOLUME");
    }

    [Fact]
    public async Task MftExtentBudget_IsEnforcedDuringBootstrap()
    {
        var fixture = new NtfsTestFixtureBuilder(12).SetMftRuns((2, 3), (20, 3), (30, 6)).AddRoot();
        var budgets = new StandardScanBudgets(MaximumMftExtents: 2);

        var result = await ScanAsync(fixture, budgets);

        Assert.Equal(StandardScanOutcome.InvalidVolume, result.Outcome);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "NTFS_EXTENT_LIMIT_REACHED");
    }

    [Theory]
    [InlineData(false, false, false, "PrimaryMft")]
    [InlineData(true, false, false, "MftMirror")]
    [InlineData(false, true, false, "PrimaryMft")]
    [InlineData(false, false, true, "PrimaryMft")]
    public async Task MftMirror_PrefersPrimaryFallsBackAndDiagnoses(bool corruptPrimary, bool corruptMirror, bool disagree, string expectedSource)
    {
        var fixture = new NtfsTestFixtureBuilder(8).AddRoot();
        if (corruptPrimary) fixture.CorruptPrimaryBootstrap();
        if (corruptMirror) fixture.CorruptMirrorBootstrap();
        if (disagree) fixture.MakeMirrorDisagree();

        var result = await ScanAsync(fixture);

        Assert.Equal(Enum.Parse<NtfsBootstrapSource>(expectedSource), result.MftLayout!.BootstrapSource);
        if (corruptPrimary) Assert.Contains(result.Diagnostics, item => item.Code == "NTFS_MFT_BOOTSTRAP_INVALID");
        if (corruptMirror) Assert.Contains(result.Diagnostics, item => item.Code == "NTFS_MFT_MIRROR_INVALID");
        if (disagree) Assert.Contains(result.Diagnostics, item => item.Code == "NTFS_MFT_MIRROR_CONFLICT");
    }

    [Fact]
    public async Task BothMftBootstrapCopiesInvalid_FailsSafely()
    {
        var fixture = new NtfsTestFixtureBuilder(8).AddRoot().CorruptPrimaryBootstrap().CorruptMirrorBootstrap();

        var result = await ScanAsync(fixture);

        Assert.Equal(StandardScanOutcome.InvalidVolume, result.Outcome);
        Assert.Contains(result.Diagnostics, item => item.Code == "NTFS_MFT_BOOTSTRAP_UNAVAILABLE");
    }

    [Fact]
    public async Task MftAttributeList_ExtensionCompletesFragmentedDataStream()
    {
        var fixture = new NtfsTestFixtureBuilder(12).SetMftRuns((2, 2), (20, 10)).AddRoot();
        var logical = 12L * fixture.RecordSize;
        var firstData = fixture.NonResidentData(NtfsTestFixtureBuilder.EncodeRunList((2, 2)), logical, logical, logical, lowestVcn: 0, highestVcn: 1);
        var list = fixture.AttributeList(
            fixture.AttributeListEntry(0x80, 0, 1, lowestVcn: 0),
            fixture.AttributeListEntry(0x80, 1, 2, lowestVcn: 2));
        fixture.AddRecord(0, 1, true, false, firstData, list, fixture.FileName(5, 1, "$MFT", 1));
        var secondData = fixture.NonResidentData(NtfsTestFixtureBuilder.EncodeRunList((20, 10)), logical, logical, logical, lowestVcn: 2, highestVcn: 11);
        fixture.AddExtensionRecord(1, 2, 0, 1, secondData);
        fixture.AddRecord(11, 3, false, false, fixture.FileName(5, 1, "resolved.txt", 1));

        var result = await ScanAsync(fixture);

        Assert.Equal(StandardScanOutcome.Completed, result.Outcome);
        Assert.Equal(2, result.MftLayout!.ExtentCount);
        Assert.Equal("resolved.txt", Assert.Single(result.Candidates).Name);
    }

    [Fact]
    public async Task ResidentAttributeList_MergesExtensionAndDoesNotDuplicateCandidate()
    {
        var fixture = new NtfsTestFixtureBuilder(10).AddRoot();
        fixture.AddRecord(7, 3, false, false,
            fixture.FileName(5, 1, "base.txt", 1),
            fixture.AttributeList(fixture.AttributeListEntry(0x80, 8, 8)));
        fixture.AddExtensionRecord(8, 8, 7, 3, fixture.ResidentData(9));

        var result = await ScanAsync(fixture);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(7, candidate.MftRecordNumber);
        Assert.Equal(9, candidate.LogicalSize);
        Assert.Equal(CandidateRecoverability.ResidentDataAvailable, candidate.Recoverability);
    }

    [Fact]
    public async Task NonResidentAttributeList_IsReadThroughMetadataRuns()
    {
        var fixture = new NtfsTestFixtureBuilder(10).AddRoot();
        var entry = fixture.AttributeListEntry(0x80, 8, 8);
        fixture.WriteClusterPayload(40, entry);
        fixture.AddRecord(7, 3, false, false,
            fixture.FileName(5, 1, "listed.txt", 1),
            fixture.NonResidentAttributeList(NtfsTestFixtureBuilder.EncodeRunList((40, 1)), entry.Length, fixture.ClusterSize, entry.Length, 0));
        fixture.AddExtensionRecord(8, 8, 7, 3, fixture.ResidentData(5));

        var candidate = Assert.Single((await ScanAsync(fixture)).Candidates);

        Assert.Equal(5, candidate.LogicalSize);
        Assert.Equal(CandidateRecoverability.ResidentDataAvailable, candidate.Recoverability);
    }

    [Theory]
    [InlineData(9, 8, "NTFS_EXTENSION_RECORD_MISSING")]
    [InlineData(8, 99, "NTFS_EXTENSION_SEQUENCE_STALE")]
    public async Task MissingOrStaleExtension_PreservesBaseAndMarksDamage(int referenceRecord, int referenceSequence, string code)
    {
        var fixture = new NtfsTestFixtureBuilder(10).AddRoot();
        fixture.AddRecord(7, 3, false, false,
            fixture.FileName(5, 1, "incomplete.txt", 1),
            fixture.AttributeList(fixture.AttributeListEntry(0x80, referenceRecord, (ushort)referenceSequence)));
        fixture.AddExtensionRecord(8, 8, 7, 3, fixture.ResidentData(4));

        var result = await ScanAsync(fixture);

        Assert.Equal(CandidateRecoverability.DamagedMetadata, Assert.Single(result.Candidates).Recoverability);
        Assert.Contains(result.Diagnostics, item => item.Code == code);
    }

    [Fact]
    public async Task CyclicExtensionReferences_AreBoundedAndDiagnosed()
    {
        var fixture = new NtfsTestFixtureBuilder(11).AddRoot();
        fixture.AddRecord(7, 3, false, false,
            fixture.FileName(5, 1, "cycle.txt", 1),
            fixture.AttributeList(fixture.AttributeListEntry(0x80, 8, 8)));
        fixture.AddExtensionRecord(8, 8, 7, 3,
            fixture.ResidentData(4),
            fixture.AttributeList(fixture.AttributeListEntry(0x20, 9, 9)));
        fixture.AddExtensionRecord(9, 9, 7, 3,
            fixture.AttributeList(fixture.AttributeListEntry(0x20, 8, 8)));

        var result = await ScanAsync(fixture);

        Assert.Contains(result.Diagnostics, item => item.Code == "NTFS_ATTRIBUTE_LIST_CYCLE");
        Assert.Equal(CandidateRecoverability.DamagedMetadata, Assert.Single(result.Candidates).Recoverability);
    }

    [Fact]
    public async Task MultipleUnnamedExtentsMergeWhileNamedStreamRemainsSeparate()
    {
        var fixture = new NtfsTestFixtureBuilder(10).AddRoot().WriteClusterPayload(70, [0]);
        var logical = 2L * fixture.ClusterSize;
        fixture.AddRecord(7, 3, false, false,
            fixture.FileName(5, 1, "extents.bin", 1),
            fixture.NonResidentData(NtfsTestFixtureBuilder.EncodeRunList((50, 1)), logical, logical, logical, lowestVcn: 0, highestVcn: 0),
            fixture.ResidentData(3, "ads"),
            fixture.AttributeList(fixture.AttributeListEntry(0x80, 8, 8, lowestVcn: 1)));
        fixture.AddExtensionRecord(8, 8, 7, 3,
            fixture.NonResidentData(NtfsTestFixtureBuilder.EncodeRunList((51, 1)), logical, logical, logical, lowestVcn: 1, highestVcn: 1));

        var candidate = Assert.Single((await ScanAsync(fixture)).Candidates);

        Assert.Equal(2, candidate.DataStreams.Count);
        Assert.Equal(2, candidate.DataStreams.Single(stream => stream.Name is null).Runs.Count);
        Assert.Equal(NtfsDataStorage.Resident, candidate.DataStreams.Single(stream => stream.Name == "ads").Storage);
    }

    [Theory]
    [InlineData(1, "NTFS_VCN_EXTENT_OVERLAP")]
    [InlineData(3, "NTFS_VCN_EXTENT_GAP")]
    public async Task ConflictingAttributeExtents_AreNotSilentlyCombined(int extensionLowestVcn, string diagnosticCode)
    {
        var fixture = new NtfsTestFixtureBuilder(10).AddRoot().WriteClusterPayload(70, [0]);
        var logical = 3L * fixture.ClusterSize;
        fixture.AddRecord(7, 3, false, false,
            fixture.FileName(5, 1, "conflict.bin", 1),
            fixture.NonResidentData(NtfsTestFixtureBuilder.EncodeRunList((50, 2)), logical, logical, logical, lowestVcn: 0, highestVcn: 1),
            fixture.AttributeList(fixture.AttributeListEntry(0x80, 8, 8, lowestVcn: extensionLowestVcn)));
        fixture.AddExtensionRecord(8, 8, 7, 3,
            fixture.NonResidentData(NtfsTestFixtureBuilder.EncodeRunList((51, 1)), logical, logical, logical, lowestVcn: extensionLowestVcn, highestVcn: extensionLowestVcn));

        var result = await ScanAsync(fixture);

        Assert.Equal(CandidateRecoverability.DamagedMetadata, Assert.Single(result.Candidates).Recoverability);
        Assert.Contains(result.Diagnostics, item => item.Code == diagnosticCode);
    }

    [Fact]
    public async Task ExtensionRecordBudget_ReturnsDamagedCandidateWithoutUnboundedResolution()
    {
        var fixture = new NtfsTestFixtureBuilder(11).AddRoot();
        fixture.AddRecord(7, 3, false, false,
            fixture.FileName(5, 1, "bounded-extensions.txt", 1),
            fixture.AttributeList(
                fixture.AttributeListEntry(0x80, 8, 8),
                fixture.AttributeListEntry(0x80, 9, 9)));
        fixture.AddExtensionRecord(8, 8, 7, 3, fixture.ResidentData(2));
        fixture.AddExtensionRecord(9, 9, 7, 3, fixture.ResidentData(2, "extra"));

        var result = await ScanAsync(fixture, new StandardScanBudgets(MaximumExtensionRecords: 1));

        Assert.Equal(CandidateRecoverability.DamagedMetadata, Assert.Single(result.Candidates).Recoverability);
        Assert.Contains(result.Diagnostics, item => item.Code == "NTFS_EXTENSION_LIMIT_REACHED");
    }

    [Fact]
    public async Task HardLinksArePreservedWithoutDosAliasOrDuplicateCandidates()
    {
        var fixture = new NtfsTestFixtureBuilder(11).AddRoot();
        fixture.AddRecord(6, 2, true, true, fixture.FileName(5, 1, "One", 1, directory: true));
        fixture.AddRecord(7, 3, true, true, fixture.FileName(5, 1, "Two", 1, directory: true));
        fixture.AddRecord(8, 4, false, false,
            fixture.FileName(6, 2, "Long Name.txt", 1),
            fixture.FileName(6, 2, "LONGNA~1.TXT", 2),
            fixture.FileName(7, 3, "Other Link.txt", 1),
            fixture.ResidentData(2));

        var candidate = Assert.Single((await ScanAsync(fixture)).Candidates);

        Assert.Equal("Long Name.txt", candidate.Name);
        Assert.Equal(2, candidate.Links.Count);
        Assert.DoesNotContain(candidate.Links, link => link.Namespace == 2);
        Assert.Contains(candidate.Links, link => link.Path == "\\One\\Long Name.txt");
        Assert.Contains(candidate.Links, link => link.Path == "\\Two\\Other Link.txt");
    }

    [Fact]
    public async Task BitmapMapsFreeAllocatedAndMixedClustersConservatively()
    {
        var fixture = CreateBitmapFixture();

        var candidates = (await ScanAsync(fixture)).Candidates.ToDictionary(candidate => candidate.Name);

        Assert.Equal(CandidateRecoverability.PossiblyRecoverable, candidates["free.bin"].Recoverability);
        Assert.Equal(NtfsAllocationState.EntirelyFree, candidates["free.bin"].DataStreams.Single().AllocationState);
        Assert.Equal(CandidateRecoverability.Overwritten, candidates["allocated.bin"].Recoverability);
        Assert.Equal(NtfsAllocationState.EntirelyAllocated, candidates["allocated.bin"].DataStreams.Single().AllocationState);
        Assert.Equal(CandidateRecoverability.PartiallyOverwritten, candidates["mixed.bin"].Recoverability);
        Assert.Equal(NtfsAllocationState.Mixed, candidates["mixed.bin"].DataStreams.Single().AllocationState);
    }

    [Fact]
    public async Task FragmentedBitmap_ReadsAcrossExtentAndByteBoundaryWithBoundedCache()
    {
        var image = new byte[80 * 1024];
        image[60 * 1024] = 0x01;
        await using var source = new MemoryRandomAccessSource(image);
        var budget = new ScanReadBudget(10_000);
        Assert.True(NtfsVirtualStream.TryCreate(source, budget,
            [new(0, 1, 40, false), new(1, 1, 60, false)], 1025, 0, image.Length, 1024, 4, false,
            out var stream, out _, out _));
        var diagnostics = new DiagnosticCollector(10);
        var bitmap = new NtfsAllocationBitmap(stream!, 1024, diagnostics);

        var state = await bitmap.QueryRangeAsync(8192, 1, CancellationToken.None);

        Assert.Equal(NtfsAllocationState.EntirelyAllocated, state);
        Assert.InRange(budget.BytesRead, 1, 1025);
    }

    [Fact]
    public async Task BitmapTooShort_ReturnsUnknownAndDiagnostic()
    {
        var fixture = CreateBitmapFixture(bitmapBytes: 6);

        var candidate = (await ScanAsync(fixture)).Candidates.Single(item => item.Name == "free.bin");

        Assert.Equal(CandidateRecoverability.Unknown, candidate.Recoverability);
        Assert.Equal(NtfsAllocationState.OutsideCoverage, candidate.DataStreams.Single().AllocationState);
    }

    [Fact]
    public async Task ResidentZeroLengthDirectoryCompressedAndEncryptedSemanticsAreConservative()
    {
        var fixture = new NtfsTestFixtureBuilder(13).AddRoot().WriteClusterPayload(60, [0]);
        fixture.AddRecord(7, 2, false, false, fixture.FileName(5, 1, "resident.txt", 1), fixture.ResidentData(4));
        fixture.AddRecord(8, 3, false, false, fixture.FileName(5, 1, "empty.txt", 1), fixture.ResidentData(0));
        fixture.AddRecord(9, 4, false, true, fixture.FileName(5, 1, "folder", 1, directory: true));
        fixture.AddRecord(10, 5, false, false, fixture.FileName(5, 1, "compressed.bin", 1), fixture.NonResidentData(NtfsTestFixtureBuilder.EncodeRunList((50, 1)), 10, 1024, 10, highestVcn: 0, flags: 0x0001));
        fixture.AddRecord(11, 6, false, false, fixture.FileName(5, 1, "encrypted.bin", 1), fixture.NonResidentData(NtfsTestFixtureBuilder.EncodeRunList((51, 1)), 10, 1024, 10, highestVcn: 0, flags: 0x4000));

        var result = await ScanAsync(fixture);
        var candidates = result.Candidates.ToDictionary(item => item.Name);

        Assert.Equal(CandidateRecoverability.ResidentDataAvailable, candidates["resident.txt"].Recoverability);
        Assert.Equal(CandidateRecoverability.ZeroLength, candidates["empty.txt"].Recoverability);
        Assert.Equal(CandidateRecoverability.MetadataOnly, candidates["folder"].Recoverability);
        Assert.Equal(CandidateRecoverability.UnsupportedLayout, candidates["compressed.bin"].Recoverability);
        Assert.Equal(CandidateRecoverability.UnsupportedLayout, candidates["encrypted.bin"].Recoverability);
        Assert.Contains(result.Diagnostics, item => item.Code == "NTFS_COMPRESSED_STREAM_UNSUPPORTED");
        Assert.Contains(result.Diagnostics, item => item.Code == "NTFS_ENCRYPTED_STREAM_UNSUPPORTED");
    }

    [Fact]
    public async Task CancellationDuringBootstrapAndBitmapAnalysisWins()
    {
        var bootstrapFixture = new NtfsTestFixtureBuilder(12).AddRoot();
        using var bootstrapCancellation = new CancellationTokenSource();
        await using var bootstrapInner = new MemoryRandomAccessSource(bootstrapFixture.Build());
        await using var bootstrapSource = new CancelAfterReadsSource(bootstrapInner, bootstrapCancellation, 2);
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new NtfsMetadataScanner().ScanAsync(bootstrapSource, new(new(0)), null, bootstrapCancellation.Token));

        var bitmapFixture = CreateBitmapFixture();
        using var bitmapCancellation = new CancellationTokenSource();
        await using var bitmapInner = new MemoryRandomAccessSource(bitmapFixture.Build());
        await using var bitmapSource = new CancelAfterReadsSource(bitmapInner, bitmapCancellation, 3 + bitmapFixture.RecordCount + 1);
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new NtfsMetadataScanner().ScanAsync(bitmapSource, new(new(0)), null, bitmapCancellation.Token));
    }

    [Fact]
    public async Task LargeFragmentedMft_IsBoundedByRecordBudgetAndDeterministic()
    {
        var fixture = new NtfsTestFixtureBuilder(2_000).SetMftRuns((2, 700), (2500, 600), (1000, 700)).AddRoot();
        fixture.AddRecord(499, 3, false, false, fixture.FileName(5, 1, "bounded.txt", 1));
        var budgets = new StandardScanBudgets(MaximumRecords: 500, MaximumDiagnostics: 20);

        var first = await ScanAsync(fixture, budgets);
        var second = await ScanAsync(fixture, budgets);

        Assert.Equal(StandardScanOutcome.Partial, first.Outcome);
        Assert.Equal(500, first.RecordsProcessed);
        Assert.Equal("MaximumRecords", first.PartialReason);
        Assert.Equal(first.Candidates.Select(item => (item.MftRecordNumber, item.Name)), second.Candidates.Select(item => (item.MftRecordNumber, item.Name)));
        Assert.True(first.BytesRead < 4 * 1024 * 1024);
    }

    private static NtfsTestFixtureBuilder CreateBitmapFixture(int bitmapBytes = 16)
    {
        var fixture = new NtfsTestFixtureBuilder(13).AddRoot().WriteClusterPayload(70, [0]);
        var bitmap = new byte[bitmapBytes];
        if (bitmapBytes > 6)
        {
            SetBit(bitmap, 52); SetBit(bitmap, 53); SetBit(bitmap, 55);
        }

        fixture.WriteClusterPayload(40, bitmap);
        fixture.AddRecord(6, 2, true, false,
            fixture.FileName(5, 1, "$Bitmap", 1),
            fixture.NonResidentData(NtfsTestFixtureBuilder.EncodeRunList((40, 1)), bitmap.Length, fixture.ClusterSize, bitmap.Length, highestVcn: 0));
        AddCandidate(7, "free.bin", 50);
        AddCandidate(8, "allocated.bin", 52);
        AddCandidate(9, "mixed.bin", 54);
        return fixture;

        void AddCandidate(int number, string name, long lcn) => fixture.AddRecord(number, (ushort)(number + 1), false, false,
            fixture.FileName(5, 1, name, 1),
            fixture.NonResidentData(NtfsTestFixtureBuilder.EncodeRunList((lcn, 2)), 2L * fixture.ClusterSize, 2L * fixture.ClusterSize, 2L * fixture.ClusterSize, highestVcn: 1));
    }

    private static void SetBit(byte[] bitmap, int cluster) => bitmap[cluster / 8] |= (byte)(1 << (cluster & 7));

    private static async Task<StandardScanResult> ScanAsync(NtfsTestFixtureBuilder fixture, StandardScanBudgets? budgets = null, CancellationToken cancellationToken = default)
    {
        await using var source = new MemoryRandomAccessSource(fixture.Build());
        return await new NtfsMetadataScanner().ScanAsync(source, new(new(0), budgets), null, cancellationToken);
    }

    private sealed class CancelAfterReadsSource(IReadOnlyRandomAccessSource inner, CancellationTokenSource cancellation, int cancelAtRead) : IReadOnlyRandomAccessSource
    {
        public int ReadCount { get; private set; }
        public long Length => inner.Length;
        public async ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
        {
            ReadCount++;
            await inner.ReadExactlyAsync(offset, destination, cancellationToken);
            if (ReadCount == cancelAtRead) cancellation.Cancel();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
