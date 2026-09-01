using System.Buffers.Binary;
using System.Security.Cryptography;
using DataRecoveryStudio.Application;
using DataRecoveryStudio.Core;
using DataRecoveryStudio.Infrastructure;

namespace DataRecoveryStudio.Tests;

public sealed class Phase4ANtfsScanTests
{
    [Fact]
    public async Task MemorySource_ProvidesExactBoundedCancelableReadsAndSafeDisposal()
    {
        var source = new MemoryRandomAccessSource([0, 1, 2, 3, 4]);
        var destination = new byte[3];

        await source.ReadExactlyAsync(1, destination, CancellationToken.None);

        Assert.Equal([1, 2, 3], destination);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => source.ReadExactlyAsync(-1, destination, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => source.ReadExactlyAsync(3, destination, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => source.ReadExactlyAsync(long.MaxValue, destination, CancellationToken.None).AsTask());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => source.ReadExactlyAsync(0, destination, canceled.Token).AsTask());
        await source.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => source.ReadExactlyAsync(0, destination, CancellationToken.None).AsTask());
    }

    [Theory]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\.\c:")]
    [InlineData(@"//./PhysicalDrive4")]
    [InlineData(@"\\?\GLOBALROOT\Device\Harddisk0\Partition1")]
    [InlineData(@"\\?\Volume{01234567-89ab-cdef-0123-456789abcdef}")]
    [InlineData(@"\??\PhysicalDrive2")]
    [InlineData("physicaldrive7")]
    [InlineData("Volume{01234567-89ab-cdef-0123-456789abcdef}")]
    public async Task DeviceNamespace_IsRejectedBeforeFileOpeningBoundary(string path)
    {
        var opener = new RecordingFileOpener();
        var factory = new RegularFileRandomAccessSourceFactory(opener);

        await Assert.ThrowsAsync<NotSupportedException>(() => factory.OpenAsync(path, CancellationToken.None).AsTask());

        Assert.Empty(opener.Paths);
    }

    [Fact]
    public async Task MissingImage_IsNeverCreated()
    {
        var path = Path.Combine(Path.GetTempPath(), $"drs-missing-{Guid.NewGuid():N}.img");
        var factory = new RegularFileRandomAccessSourceFactory();

        await Assert.ThrowsAsync<FileNotFoundException>(() => factory.OpenAsync(path, CancellationToken.None).AsTask());

        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(512, 2, -10, 1024)]
    [InlineData(1024, 1, -10, 1024)]
    [InlineData(1024, 1, 1, 1024)]
    public async Task BootGeometry_HandlesSectorAndBothRecordSizeEncodings(int bytesPerSector, int sectorsPerCluster, int encoding, int expectedRecordSize)
    {
        var fixture = new NtfsTestFixtureBuilder(8, (ushort)bytesPerSector, (byte)sectorsPerCluster, (sbyte)encoding)
            .AddRoot()
            .AddRecord(6, 2, false, false);

        var result = await ScanAsync(fixture);

        Assert.Equal(StandardScanOutcome.Completed, result.Outcome);
        Assert.Equal(bytesPerSector, result.Geometry!.BytesPerSector);
        Assert.Equal(expectedRecordSize, result.Geometry.FileRecordSize);
    }

    [Fact]
    public async Task InvalidGlobalGeometryAndCheckedOverflow_FailSafelyWithCodes()
    {
        var invalidSignature = new NtfsTestFixtureBuilder().Build();
        invalidSignature[510] = 0;
        var signatureResult = await ScanAsync(invalidSignature, 1);

        var overflow = new NtfsTestFixtureBuilder().Build();
        BinaryPrimitives.WriteUInt64LittleEndian(overflow.AsSpan(40), ulong.MaxValue);
        var overflowResult = await ScanAsync(overflow, 1);

        Assert.Equal(StandardScanOutcome.InvalidVolume, signatureResult.Outcome);
        Assert.Contains(signatureResult.Diagnostics, item => item.Code == "NTFS_BOOT_SIGNATURE_INVALID");
        Assert.Equal(StandardScanOutcome.InvalidVolume, overflowResult.Outcome);
        Assert.Contains(overflowResult.Diagnostics, item => item.Code == "NTFS_GEOMETRY_OVERFLOW");
    }

    [Fact]
    public async Task UsaFixupFailure_IsolatedWhileLaterDeletedRecordIsStillFound()
    {
        var fixture = new NtfsTestFixtureBuilder(9)
            .AddRoot()
            .AddRecord(6, 3, false, false, new byte[0])
            .AddRecord(7, 4, false, false);
        fixture.CorruptUsa(6);

        var result = await ScanAsync(fixture);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(7, candidate.MftRecordNumber);
        Assert.Contains(result.Diagnostics, item => item.Code == "NTFS_USA_FIXUP_INVALID" && item.MftRecordNumber == 6);
    }

    [Fact]
    public async Task DeletedAndActiveRecords_AreClassifiedWithoutClaimingRecoverability()
    {
        var fixture = new NtfsTestFixtureBuilder(9).AddRoot();
        fixture.AddRecord(6, 2, true, false,
            fixture.StandardInformation(), fixture.FileName(5, 1, "active.txt", 1), fixture.ResidentData(10));
        fixture.AddRecord(7, 3, false, false,
            fixture.StandardInformation(), fixture.FileName(5, 1, "deleted.txt", 1, 10, 16), fixture.ResidentData(10));

        var result = await ScanAsync(fixture);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("deleted.txt", candidate.Name);
        Assert.Equal(CandidateRecoverability.ResidentDataAvailable, candidate.Recoverability);
        Assert.Contains("not a recovery guarantee", candidate.RecoverabilityNote, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Candidates, item => item.Name == "active.txt");
    }

    [Fact]
    public async Task DataMetadata_ParsesResidentNamedNonResidentSparseAndSignedRuns()
    {
        var fixture = new NtfsTestFixtureBuilder(8).AddRoot();
        var runs = new byte[] { 0x11, 0x03, 0x0A, 0x01, 0x02, 0x11, 0x01, 0xFC, 0x00 };
        fixture.AddRecord(6, 2, false, false,
            fixture.FileName(5, 1, "streams.bin", 1, 4096, 8192),
            fixture.NonResidentData(runs, 4096, 8192, 4000),
            fixture.ResidentData(7, "preview"));

        var candidate = Assert.Single((await ScanAsync(fixture)).Candidates);

        Assert.Equal(2, candidate.DataStreams.Count);
        var unnamed = Assert.Single(candidate.DataStreams, stream => stream.Name is null);
        Assert.Equal(NtfsDataStorage.NonResident, unnamed.Storage);
        Assert.Equal(3, unnamed.Runs.Count);
        Assert.Equal(10, unnamed.Runs[0].LogicalCluster);
        Assert.True(unnamed.Runs[1].IsSparse);
        Assert.Null(unnamed.Runs[1].LogicalCluster);
        Assert.Equal(6, unnamed.Runs[2].LogicalCluster);
        var named = Assert.Single(candidate.DataStreams, stream => stream.Name == "preview");
        Assert.Equal(NtfsDataStorage.Resident, named.Storage);
        Assert.Equal(7, named.LogicalSize);
    }

    [Fact]
    public async Task CorruptRunList_ProducesUnknownDamagedAssessmentWithoutPayloadReads()
    {
        var fixture = new NtfsTestFixtureBuilder(8).AddRoot();
        fixture.AddRecord(6, 2, false, false,
            fixture.FileName(5, 1, "damaged.bin", 1),
            fixture.NonResidentData([0x99, 0x01, 0x00], 10, 16, 10));

        var result = await ScanAsync(fixture);
        var candidate = Assert.Single(result.Candidates);

        Assert.Equal(CandidateRecoverability.DamagedMetadata, candidate.Recoverability);
        Assert.Equal(NtfsDataStorage.Unknown, Assert.Single(candidate.DataStreams).Storage);
        Assert.Contains(result.Diagnostics, item => item.Code == "NTFS_RUN_LIST_INVALID");
    }

    [Fact]
    public async Task FilenameNamespaces_ChooseWin32DeterministicallyOverDosAndPosix()
    {
        var fixture = new NtfsTestFixtureBuilder(8).AddRoot();
        fixture.AddRecord(6, 2, false, false,
            fixture.FileName(5, 1, "FILE~1.TXT", 2),
            fixture.FileName(5, 1, "posix-name", 0),
            fixture.FileName(5, 1, "Long File Name.txt", 1));

        var result = await ScanAsync(fixture);

        Assert.Equal("Long File Name.txt", Assert.Single(result.Candidates).Name);
    }

    [Fact]
    public async Task Paths_ReportCompleteOrphanedStaleAndCycleStates()
    {
        var fixture = new NtfsTestFixtureBuilder(12).AddRoot();
        fixture.AddRecord(6, 2, true, true, fixture.FileName(5, 1, "Folder", 1, directory: true));
        fixture.AddRecord(7, 3, false, false, fixture.FileName(6, 2, "complete.txt", 1));
        fixture.AddRecord(8, 4, false, false, fixture.FileName(99, 1, "orphan.txt", 1));
        fixture.AddRecord(9, 5, false, false, fixture.FileName(6, 99, "stale.txt", 1));
        fixture.AddRecord(10, 6, false, true, fixture.FileName(11, 7, "A", 1, directory: true));
        fixture.AddRecord(11, 7, true, true, fixture.FileName(10, 6, "B", 1, directory: true));

        var candidates = (await ScanAsync(fixture)).Candidates.ToDictionary(item => item.Name);

        Assert.Equal(("\\Folder\\complete.txt", CandidatePathState.Complete), (candidates["complete.txt"].OriginalPath, candidates["complete.txt"].PathState));
        Assert.Equal(CandidatePathState.Orphaned, candidates["orphan.txt"].PathState);
        Assert.Equal(CandidatePathState.StaleParent, candidates["stale.txt"].PathState);
        Assert.Equal(CandidatePathState.CycleDetected, candidates["A"].PathState);
    }

    [Theory]
    [InlineData(true, "NTFS_ATTRIBUTE_LENGTH_ZERO")]
    [InlineData(false, "NTFS_ATTRIBUTE_LENGTH_INVALID")]
    public async Task MalformedAttribute_IsBoundedAndDoesNotStopLaterRecords(bool zeroLength, string expectedCode)
    {
        var fixture = new NtfsTestFixtureBuilder(9).AddRoot();
        fixture.AddRecord(6, 2, false, false,
            fixture.FileName(5, 1, "damaged.txt", 1),
            zeroLength ? NtfsTestFixtureBuilder.ZeroLengthAttribute() : NtfsTestFixtureBuilder.InvalidLengthAttribute());
        fixture.AddRecord(7, 3, false, false, fixture.FileName(5, 1, "later.txt", 1));

        var result = await ScanAsync(fixture);

        Assert.Equal(2, result.Candidates.Count);
        Assert.Contains(result.Diagnostics, item => item.Code == expectedCode && item.MftRecordNumber == 6);
        Assert.Equal(CandidateRecoverability.DamagedMetadata, result.Candidates.Single(item => item.Name == "damaged.txt").Recoverability);
    }

    [Fact]
    public async Task SafetyBudgets_ReturnExplicitPartialResultsAndBoundDiagnostics()
    {
        var fixture = new NtfsTestFixtureBuilder(10).AddRoot();
        fixture.AddRecord(6, 2, false, false,
            fixture.FileName(5, 1, "one.txt", 1),
            fixture.StandardInformation(),
            NtfsTestFixtureBuilder.ZeroLengthAttribute());
        fixture.AddRecord(7, 3, false, false, fixture.FileName(5, 1, "two.txt", 1));
        var budgets = new StandardScanBudgets(
            MaximumRecords: 7,
            MaximumAttributesPerRecord: 1,
            MaximumBytesRead: 20_000,
            MaximumDiagnostics: 2,
            MaximumPathDepth: 1,
            MaximumFilenameLength: 255,
            MaximumDataRuns: 1);

        var result = await ScanAsync(fixture.Build(), fixture.RecordCount, budgets);

        Assert.Equal(StandardScanOutcome.Partial, result.Outcome);
        Assert.Equal("MaximumRecords", result.PartialReason);
        Assert.True(result.Diagnostics.Count <= 2);
        Assert.True(result.DiagnosticsTruncated);
        Assert.Contains(result.Diagnostics, item => item.Code == "SCAN_RECORD_BUDGET_REACHED");
    }

    [Fact]
    public async Task FineGrainedAttributeRunFilenamePathAndByteBudgets_AreEnforced()
    {
        var fixture = new NtfsTestFixtureBuilder(10).AddRoot();
        fixture.AddRecord(6, 2, true, true, fixture.FileName(5, 1, "Folder", 1, directory: true));
        fixture.AddRecord(7, 3, false, false,
            fixture.FileName(6, 2, "long-name.bin", 1),
            fixture.NonResidentData([0x11, 0x01, 0x0A, 0x11, 0x01, 0x01, 0x00], 2, 2, 2));

        var attributeLimited = await ScanAsync(fixture.Build(), fixture.RecordCount, new(
            MaximumAttributesPerRecord: 1,
            MaximumDiagnostics: 100,
            MaximumPathDepth: 10,
            MaximumFilenameLength: 255,
            MaximumDataRuns: 10));
        Assert.Contains(attributeLimited.Diagnostics, item => item.Code == "NTFS_ATTRIBUTE_LIMIT_REACHED" && item.MftRecordNumber == 7);

        var runAndPathLimited = await ScanAsync(fixture.Build(), fixture.RecordCount, new(
            MaximumAttributesPerRecord: 10,
            MaximumDiagnostics: 100,
            MaximumPathDepth: 1,
            MaximumFilenameLength: 255,
            MaximumDataRuns: 1));
        var runCandidate = Assert.Single(runAndPathLimited.Candidates);
        Assert.Equal(CandidatePathState.Invalid, runCandidate.PathState);
        Assert.Equal(NtfsDataStorage.Unknown, Assert.Single(runCandidate.DataStreams).Storage);
        Assert.Contains(runAndPathLimited.Diagnostics, item => item.Code == "NTFS_RUN_LIMIT_REACHED");

        var filenameLimited = await ScanAsync(fixture.Build(), fixture.RecordCount, new(
            MaximumAttributesPerRecord: 10,
            MaximumDiagnostics: 100,
            MaximumPathDepth: 10,
            MaximumFilenameLength: 4,
            MaximumDataRuns: 10));
        Assert.Equal("$MFT-7", Assert.Single(filenameLimited.Candidates).Name);
        Assert.Contains(filenameLimited.Diagnostics, item => item.Code == "NTFS_FILE_NAME_INVALID" && item.MftRecordNumber == 7);

        var byteLimited = await ScanAsync(fixture.Build(), fixture.RecordCount, new(
            MaximumBytesRead: 512 + (6 * fixture.RecordSize),
            MaximumDiagnostics: 100));
        Assert.Equal(StandardScanOutcome.Partial, byteLimited.Outcome);
        Assert.Equal("MaximumBytesRead", byteLimited.PartialReason);
        Assert.Contains(byteLimited.Diagnostics, item => item.Code == "SCAN_BYTE_BUDGET_REACHED");
    }

    [Fact]
    public async Task CandidateOrdering_IsDeterministicAcrossRepeatedScans()
    {
        var fixture = new NtfsTestFixtureBuilder(10).AddRoot();
        fixture.AddRecord(8, 4, false, false, fixture.FileName(5, 1, "z.txt", 1));
        fixture.AddRecord(6, 2, false, false, fixture.FileName(5, 1, "a.txt", 1));
        fixture.AddRecord(7, 3, false, false, fixture.FileName(5, 1, "m.txt", 1));

        var first = await ScanAsync(fixture);
        var second = await ScanAsync(fixture);

        Assert.Equal([6L, 7L, 8L], first.Candidates.Select(item => item.MftRecordNumber));
        Assert.Equal(
            first.Candidates.Select(item => (item.MftRecordNumber, item.Name, item.OriginalPath, item.PathState, item.LogicalSize, item.Recoverability)),
            second.Candidates.Select(item => (item.MftRecordNumber, item.Name, item.OriginalPath, item.PathState, item.LogicalSize, item.Recoverability)));
        Assert.Equal(first.Diagnostics, second.Diagnostics);
    }

    [Fact]
    public async Task ProgressIsMonotonicAndCancellationWinsOverCompletionPublication()
    {
        var fixture = new NtfsTestFixtureBuilder(8).AddRoot();
        fixture.AddRecord(6, 2, false, false, fixture.FileName(5, 1, "deleted.txt", 1));
        var progressValues = new List<StandardScanProgress>();
        var progress = new InlineProgress<StandardScanProgress>(progressValues.Add);

        await ScanAsync(fixture.Build(), fixture.RecordCount, null, progress, CancellationToken.None);

        Assert.True(progressValues.Zip(progressValues.Skip(1)).All(pair => pair.First.RecordsProcessed <= pair.Second.RecordsProcessed));
        Assert.True(progressValues.Zip(progressValues.Skip(1)).All(pair => pair.First.BytesRead <= pair.Second.BytesRead));

        using var cancellation = new CancellationTokenSource();
        var cancelAtCompletion = new InlineProgress<StandardScanProgress>(value =>
        {
            if (value.Phase.Contains("complete", StringComparison.OrdinalIgnoreCase)) cancellation.Cancel();
        });
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ScanAsync(fixture.Build(), fixture.RecordCount, null, cancelAtCompletion, cancellation.Token));
    }

    [Fact]
    public async Task CancellationDuringMultiRecordRead_StopsPromptly()
    {
        var fixture = new NtfsTestFixtureBuilder(30).AddRoot();
        for (var number = 6; number < 30; number++)
        {
            fixture.AddRecord(number, (ushort)number, false, false, fixture.FileName(5, 1, $"file-{number}.txt", 1));
        }

        using var cancellation = new CancellationTokenSource();
        await using var inner = new MemoryRandomAccessSource(fixture.Build());
        await using var source = new CancelAfterReadsSource(inner, cancellation, 5);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new NtfsMetadataScanner().ScanAsync(source, new(new(0)), null, cancellation.Token));
        Assert.InRange(source.ReadCount, 5, 6);
    }

    [Fact]
    public async Task HeadlessFileScan_LeavesHashLengthAndTimestampUnchanged()
    {
        var fixture = new NtfsTestFixtureBuilder(8).AddRoot();
        fixture.AddRecord(6, 2, false, false, fixture.FileName(5, 1, "deleted.txt", 1), fixture.ResidentData(12));
        var path = Path.Combine(Path.GetTempPath(), $"drs-ntfs-{Guid.NewGuid():N}.img");
        await File.WriteAllBytesAsync(path, fixture.Build());
        try
        {
            var beforeHash = await HashAsync(path);
            var beforeLength = new FileInfo(path).Length;
            var beforeWrite = File.GetLastWriteTimeUtc(path);
            var service = new StandardImageScanService(new RegularFileRandomAccessSourceFactory(), new NtfsMetadataScanner());

            var result = await service.ScanAsync(path, new(new(0)), null, CancellationToken.None);

            Assert.Single(result.Candidates);
            Assert.Equal(beforeHash, await HashAsync(path));
            Assert.Equal(beforeLength, new FileInfo(path).Length);
            Assert.Equal(beforeWrite, File.GetLastWriteTimeUtc(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ProductionScannerAssemblies_AreIndependentFromWpf()
    {
        var applicationReferences = typeof(StandardImageScanService).Assembly.GetReferencedAssemblies();
        var infrastructureReferences = typeof(NtfsMetadataScanner).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(applicationReferences, assembly => assembly.Name is "PresentationFramework" or "WindowsBase");
        Assert.DoesNotContain(infrastructureReferences, assembly => assembly.Name is "PresentationFramework" or "WindowsBase");
    }

    private static Task<StandardScanResult> ScanAsync(NtfsTestFixtureBuilder fixture) =>
        ScanAsync(fixture.Build(), fixture.RecordCount);

    private static async Task<StandardScanResult> ScanAsync(
        byte[] image,
        long recordCount,
        StandardScanBudgets? budgets = null,
        IProgress<StandardScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await using var source = new MemoryRandomAccessSource(image);
        return await new NtfsMetadataScanner().ScanAsync(source, new(new(0), budgets), progress, cancellationToken);
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private sealed class RecordingFileOpener : IRegularFileOpener
    {
        public List<string> Paths { get; } = [];

        public FileStream OpenRead(string fullPath)
        {
            Paths.Add(fullPath);
            throw new InvalidOperationException("The test opener must never be reached for a rejected path.");
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class CancelAfterReadsSource(
        IReadOnlyRandomAccessSource inner,
        CancellationTokenSource cancellation,
        int cancelAtRead) : IReadOnlyRandomAccessSource
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
