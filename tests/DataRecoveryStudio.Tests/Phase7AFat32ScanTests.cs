using System.Security.Cryptography;
using DataRecoveryStudio.Application;
using DataRecoveryStudio.Core;
using DataRecoveryStudio.Infrastructure;

namespace DataRecoveryStudio.Tests;

public sealed class Phase7AFat32ScanTests
{
    [Fact]
    public async Task ProductionScanner_TraversesActiveTreeAndReturnsOnlyDeletedEntries()
    {
        await using var source = new MemoryRandomAccessSource(new Fat32TestFixtureBuilder().Build());
        var result = await ScanAsync(source);

        Assert.Equal(Fat32ScanOutcome.Completed, result.Outcome);
        Assert.Equal((ushort)512, result.Geometry!.BytesPerSector);
        Assert.True(result.FsInfo!.IsValid);
        Assert.Equal(9, result.Candidates.Count);
        Assert.DoesNotContain(result.Candidates, item => item.DisplayName.Contains("ACTIVE", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Candidates, item => item.OriginalPath.StartsWith("\\Long Folder\\", StringComparison.Ordinal));
        Assert.Equal(3, result.Metrics.DirectoryClustersExamined);
        Assert.DoesNotContain(result.Candidates, item => item.DisplayName.Contains("LABEL", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("FAT32_ORPHAN_LFN", result.Diagnostics.Select(item => item.Code));
    }

    [Fact]
    public async Task AllocationAssessment_IsConservativeAcrossDeletionCases()
    {
        await using var source = new MemoryRandomAccessSource(new Fat32TestFixtureBuilder().Build());
        var result = await ScanAsync(source);

        Assert.Equal(Fat32AllocationAssessment.PossiblyRecoverableContiguous, Find(result, "?REE.TXT").Allocation);
        Assert.Equal(Fat32AllocationAssessment.ZeroLength, Find(result, "?ERO.TXT").Allocation);
        Assert.Equal(Fat32AllocationAssessment.PartiallyOverwrittenOrReused, Find(result, "?IXED.BIN").Allocation);
        Assert.Equal(Fat32AllocationAssessment.OverwrittenOrReused, Find(result, "?EUSED.BIN").Allocation);
        Assert.Equal(Fat32AllocationAssessment.PreservedAllocatedChain, Find(result, "?HAIN.DAT").Allocation);
        Assert.Equal(Fat32AllocationAssessment.MetadataOnly, Find(result, "?LDDIR").Allocation);
    }

    [Fact]
    public async Task DeletedNames_PreserveMissingShortByteAndLfnUncertainty()
    {
        await using var source = new MemoryRandomAccessSource(new Fat32TestFixtureBuilder().Build());
        var result = await ScanAsync(source);

        var probable = Assert.Single(result.Candidates.Where(item => item.DisplayName == "Đã xóa.txt"));
        Assert.Equal(Fat32NameState.ProbableDeletedLongName, probable.NameState);
        Assert.True(probable.LongNameEvidence!.ChecksumValidated);
        Assert.StartsWith("?", probable.ShortName);

        var ambiguous = Assert.Single(result.Candidates.Where(item => item.DisplayName == "Maybe.txt"));
        Assert.Equal(Fat32NameState.AmbiguousDeletedLongName, ambiguous.NameState);
        Assert.False(ambiguous.LongNameEvidence!.ChecksumValidated);
        Assert.Contains("FAT32_AMBIGUOUS_DELETED_LFN", result.Diagnostics.Select(item => item.Code));
    }

    [Fact]
    public async Task InvalidTimestamp_IsIsolatedFromOtherwiseUsableCandidate()
    {
        await using var source = new MemoryRandomAccessSource(new Fat32TestFixtureBuilder().Build());
        var candidate = Find(await ScanAsync(source), "?REE.TXT");

        Assert.Null(candidate.CreatedAt);
        Assert.Contains("FAT32_INVALID_TIMESTAMP", candidate.DiagnosticCodes);
    }

    [Fact]
    public async Task PrimaryBackupPolicy_PrefersPrimaryAndFallsBackOnlyToValidBackup()
    {
        await using var fallbackSource = new MemoryRandomAccessSource(new Fat32TestFixtureBuilder().InvalidatePrimarySignature().Build());
        var fallback = await ScanAsync(fallbackSource);
        Assert.True(fallback.Geometry!.UsedBackupBootSector);
        Assert.Contains("FAT32_PRIMARY_BOOT_INVALID", fallback.Diagnostics.Select(item => item.Code));

        await using var primarySource = new MemoryRandomAccessSource(new Fat32TestFixtureBuilder().InvalidateBackupSignature().Build());
        var primary = await ScanAsync(primarySource);
        Assert.False(primary.Geometry!.UsedBackupBootSector);
        Assert.Contains("FAT32_BACKUP_BOOT_INVALID", primary.Diagnostics.Select(item => item.Code));

        await using var bothSource = new MemoryRandomAccessSource(new Fat32TestFixtureBuilder().InvalidatePrimarySignature().InvalidateBackupSignature().Build());
        Assert.Equal(Fat32ScanOutcome.InvalidVolume, (await ScanAsync(bothSource)).Outcome);
    }

    [Fact]
    public async Task ConflictingBackup_IsReportedWithoutMergingGeometry()
    {
        await using var source = new MemoryRandomAccessSource(new Fat32TestFixtureBuilder().DisagreeingBackupRoot(3).Build());
        var result = await ScanAsync(source);

        Assert.Equal((uint)2, result.Geometry!.RootDirectoryCluster);
        Assert.Contains("FAT32_BOOT_SECTOR_DISAGREEMENT", result.Diagnostics.Select(item => item.Code));
    }

    [Fact]
    public async Task BackupLocationOutsideReservedVolumeRegionIsIgnored()
    {
        await using var source = new MemoryRandomAccessSource(new Fat32TestFixtureBuilder().BackupSector(40).Build());
        var result = await ScanAsync(source);

        Assert.Equal(Fat32ScanOutcome.Completed, result.Outcome);
        Assert.False(result.Geometry!.UsedBackupBootSector);
        Assert.Contains("FAT32_BACKUP_BOOT_INVALID", result.Diagnostics.Select(item => item.Code));
    }

    [Fact]
    public async Task InvalidFsInfo_IsAdvisoryAndDoesNotStopTraversal()
    {
        await using var source = new MemoryRandomAccessSource(new Fat32TestFixtureBuilder().InvalidFsInfo().Build());
        var result = await ScanAsync(source);

        Assert.Equal(Fat32ScanOutcome.Completed, result.Outcome);
        Assert.False(result.FsInfo!.IsValid);
        Assert.NotEmpty(result.Candidates);
        Assert.Contains("FAT32_FSINFO_INVALID", result.Diagnostics.Select(item => item.Code));
    }

    [Fact]
    public async Task MirroredFatDisagreement_MakesAffectedAllocationUnknown()
    {
        await using var source = new MemoryRandomAccessSource(new Fat32TestFixtureBuilder().MirroredDisagreement(10, 0x0FFFFFFF).Build());
        var result = await ScanAsync(source);

        Assert.Equal(Fat32AllocationAssessment.AllocationUnknown, Find(result, "?REE.TXT").Allocation);
        Assert.Contains("FAT32_FAT_COPY_DISAGREEMENT", Find(result, "?REE.TXT").DiagnosticCodes);
    }

    [Fact]
    public async Task DisabledMirroring_UsesOnlyValidatedActiveFat()
    {
        var builder = new Fat32TestFixtureBuilder().UseSecondFatOnly().MirroredDisagreement(10, 0x0FFFFFFF);
        await using var source = new MemoryRandomAccessSource(builder.Build());
        var result = await ScanAsync(source);

        Assert.False(result.Geometry!.FatMirroringEnabled);
        Assert.Equal((byte)1, result.Geometry.ActiveFatIndex);
        Assert.DoesNotContain("FAT32_FAT_COPY_DISAGREEMENT", Find(result, "?REE.TXT").DiagnosticCodes);

        await using var invalid = new MemoryRandomAccessSource(new Fat32TestFixtureBuilder().InvalidActiveFatIndex().Build());
        var invalidResult = await ScanAsync(invalid);
        Assert.Equal(Fat32ScanOutcome.InvalidVolume, invalidResult.Outcome);
        Assert.Contains("FAT32_INVALID_ACTIVE_FAT_INDEX", invalidResult.Diagnostics.Select(item => item.Code));
    }

    [Fact]
    public async Task SingleFatVolumeUsesItsOnlyCopy()
    {
        await using var source = new MemoryRandomAccessSource(new Fat32TestFixtureBuilder().UseOneFatCopy().Build());
        var result = await ScanAsync(source);

        Assert.Equal((byte)1, result.Geometry!.FatCount);
        Assert.Equal(9, result.Candidates.Count);
        Assert.Equal(Fat32AllocationAssessment.PossiblyRecoverableContiguous, Find(result, "?REE.TXT").Allocation);
    }

    [Fact]
    public async Task DirectoryCycle_IsDiagnosedAndBounded()
    {
        await using var source = new MemoryRandomAccessSource(new Fat32TestFixtureBuilder().DirectoryCycle().Build());
        var result = await ScanAsync(source);

        Assert.Equal(Fat32ScanOutcome.Partial, result.Outcome);
        Assert.Contains("FAT32_FAT_CHAIN_CYCLE", result.Diagnostics.Select(item => item.Code));
        Assert.InRange(result.Metrics.DirectoryClustersExamined, 1, 4);
    }

    [Fact]
    public async Task CrossLinkedActiveDirectoriesAreDiagnosedWithoutDuplicateCandidates()
    {
        await using var source = new MemoryRandomAccessSource(new Fat32TestFixtureBuilder().CrossLinkedChildDirectory().Build());
        var result = await ScanAsync(source);

        Assert.Contains("FAT32_CROSS_LINKED_DIRECTORY", result.Diagnostics.Select(item => item.Code));
        Assert.Equal(result.Candidates.Count, result.Candidates.Select(item => item.CandidateId).Distinct(StringComparer.Ordinal).Count());
        Assert.Single(result.Candidates.Where(item => item.ShortName == "?NNER.TXT"));
    }

    [Fact]
    public async Task CrossLinkedPreservedFileChainsLowerBothCandidatesToUnknown()
    {
        await using var source = new MemoryRandomAccessSource(new Fat32TestFixtureBuilder().CrossLinkedPreservedFile().Build());
        var result = await ScanAsync(source);

        Assert.Equal(Fat32AllocationAssessment.AllocationUnknown, Find(result, "?ROSSLNK.DAT").Allocation);
        Assert.Equal(Fat32AllocationAssessment.AllocationUnknown, Find(result, "?HAIN.DAT").Allocation);
        Assert.Contains("FAT32_CROSS_LINKED_CLUSTER", result.Diagnostics.Select(item => item.Code));
    }

    [Theory]
    [InlineData(0x0FFFFFF7u, "FAT32_BAD_CLUSTER")]
    [InlineData(0x0FFFFFF2u, "FAT32_INVALID_FAT_ENTRY")]
    public async Task BadAndReservedFatValuesRemainUnknown(uint fatValue, string diagnostic)
    {
        await using var source = new MemoryRandomAccessSource(new Fat32TestFixtureBuilder().FatValue(10, fatValue).Build());
        var candidate = Find(await ScanAsync(source), "?REE.TXT");

        Assert.Equal(Fat32AllocationAssessment.Unknown, candidate.Allocation);
        Assert.Contains(diagnostic, candidate.DiagnosticCodes);
    }

    [Fact]
    public async Task FatEntriesAreMaskedToLow28Bits()
    {
        await using var source = new MemoryRandomAccessSource(new Fat32TestFixtureBuilder().FatValue(10, 0xF0000000).Build());
        Assert.Equal(Fat32AllocationAssessment.PossiblyRecoverableContiguous, Find(await ScanAsync(source), "?REE.TXT").Allocation);
    }

    [Fact]
    public async Task FileBeyondRemainingVolumeIsDamagedMetadata()
    {
        await using var source = new MemoryRandomAccessSource(new Fat32TestFixtureBuilder().DeletedFileStartsAt(65_526, 700).Build());
        var candidate = Find(await ScanAsync(source), "?REE.TXT");

        Assert.Equal(Fat32AllocationAssessment.DamagedMetadata, candidate.Allocation);
        Assert.Contains("FAT32_FILE_EXCEEDS_VOLUME", candidate.DiagnosticCodes);
    }

    [Fact]
    public async Task UnsafeLongNameCharactersAreDisplaySanitizedWithoutClaimingExactness()
    {
        await using var source = new MemoryRandomAccessSource(new Fat32TestFixtureBuilder().MaliciousDeletedLongName().Build());
        var candidate = Assert.Single((await ScanAsync(source)).Candidates.Where(item => item.DisplayName == "Bad_name.txt"));

        Assert.Equal(Fat32NameState.InvalidName, candidate.NameState);
        Assert.DoesNotContain('/', candidate.OriginalPath);
    }

    [Fact]
    public async Task InvalidLfnUtf16IsIsolatedAndFallsBackToUncertainShortName()
    {
        await using var source = new MemoryRandomAccessSource(new Fat32TestFixtureBuilder().InvalidDeletedLfnUtf16().Build());
        var result = await ScanAsync(source);
        var candidate = Find(result, "?NICOD~1.TXT");

        Assert.Equal(Fat32NameState.ShortNameWithMissingFirstCharacter, candidate.NameState);
        Assert.Null(candidate.LongNameEvidence);
        Assert.Contains("FAT32_INVALID_LFN_STRUCTURE", result.Diagnostics.Select(item => item.Code));
    }

    [Fact]
    public async Task ExplicitValidatedVolumeOffsetUsesCanonicalCheckedMapping()
    {
        var fixture = new Fat32TestFixtureBuilder().Build();
        var bytes = new byte[4096 + fixture.Length];
        fixture.CopyTo(bytes, 4096);
        await using var source = new MemoryRandomAccessSource(bytes);

        var result = await new Fat32MetadataScanner().ScanAsync(source, new(new(4096), SourceIdentity: "offset-fixture"), null, CancellationToken.None);

        Assert.Equal(Fat32ScanOutcome.Completed, result.Outcome);
        Assert.Equal(9, result.Candidates.Count);
    }

    [Fact]
    public async Task HardBudgetsProduceExplicitPartialResultsAndBoundFatCache()
    {
        await using var byteLimited = new MemoryRandomAccessSource(new Fat32TestFixtureBuilder().Build());
        var partial = await ScanAsync(byteLimited, new(MaximumBytesRead: 1024));
        Assert.Equal(Fat32ScanOutcome.Partial, partial.Outcome);
        Assert.True(partial.IsBudgetLimited);
        Assert.Contains("FAT32_BUDGET_REACHED", partial.Diagnostics.Select(item => item.Code));

        Assert.Throws<ArgumentOutOfRangeException>(() => new Fat32ScanBudget(MaximumFatCacheBytes: Fat32ScanBudget.HardLimits.MaximumFatCacheBytes + 1).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new Fat32ScanBudget(MaximumCandidates: Fat32ScanBudget.HardLimits.MaximumCandidates + 1).Validate());
    }

    [Fact]
    public async Task PerDirectoryAndDiagnosticBudgetsAreBounded()
    {
        await using var source = new MemoryRandomAccessSource(new Fat32TestFixtureBuilder().Build());
        var result = await ScanAsync(source, new(MaximumEntriesPerDirectory: 14, MaximumDiagnostics: 1));

        Assert.Equal(Fat32ScanOutcome.Partial, result.Outcome);
        Assert.True(result.IsBudgetLimited);
        Assert.Single(result.Diagnostics);
        Assert.True(result.DiagnosticsTruncated);
    }

    [Fact]
    public async Task CancellationReturnsOneCanceledTerminalResultWithRetainedValidatedCandidates()
    {
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<Fat32ScanProgress>(value =>
        {
            if (value.DeletedCandidatesFound >= 2) cancellation.Cancel();
        });
        await using var source = new MemoryRandomAccessSource(new Fat32TestFixtureBuilder().Build());

        var result = await new Fat32MetadataScanner().ScanAsync(source, new(new(0), MinimumProgressBudget()), progress, cancellation.Token);

        Assert.Equal(Fat32ScanOutcome.Canceled, result.Outcome);
        Assert.InRange(result.Candidates.Count, 2, 8);
        Assert.Contains("FAT32_CANCELED", result.Diagnostics.Select(item => item.Code));
    }

    [Fact]
    public async Task ProgressIsMonotonicAndUsesMeasuredWork()
    {
        var reports = new List<Fat32ScanProgress>();
        await using var source = new MemoryRandomAccessSource(new Fat32TestFixtureBuilder().Build());
        var result = await new Fat32MetadataScanner().ScanAsync(source, new(new(0), MinimumProgressBudget()), new InlineProgress<Fat32ScanProgress>(reports.Add), CancellationToken.None);

        Assert.Equal(Fat32ScanOutcome.Completed, result.Outcome);
        Assert.NotEmpty(reports);
        Assert.Equal(Fat32ScanPhase.Completed, reports[^1].Phase);
        Assert.True(reports.Zip(reports.Skip(1)).All(pair => pair.First.BytesRead <= pair.Second.BytesRead && pair.First.DirectoryEntriesExamined <= pair.Second.DirectoryEntriesExamined));
    }

    [Fact]
    public void GeometryParser_UsesCalculatedClassificationAndSupports4096ByteSectors()
    {
        var sector = Fat32TestFixtureBuilder.CreateBootSector4096();
        var parsed = Fat32BootSectorParser.Parse(sector, 65_622L * 4096, 0, false);
        Assert.True(parsed.IsValid, parsed.Reason);
        Assert.Equal(4096, parsed.Geometry!.ClusterSize);

        sector[82] = (byte)'X';
        Assert.True(Fat32BootSectorParser.Parse(sector, 65_622L * 4096, 0, false).IsValid);

        var small = Fat32TestFixtureBuilder.CreateBootSector4096();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(small.AsSpan(32), 60_000);
        var rejected = Fat32BootSectorParser.Parse(small, 65_622L * 4096, 0, false);
        Assert.False(rejected.IsValid);
        Assert.Equal("FAT32_NOT_FAT32", rejected.Code);
    }

    [Fact]
    public void GeometryParserRejectsInvalidGlobalBpbFields()
    {
        var valid = new Fat32TestFixtureBuilder().Build()[..512];
        var mutations = new Action<byte[]>[]
        {
            bytes => System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(11), 768),
            bytes => bytes[13] = 3,
            bytes => System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), 0),
            bytes => bytes[16] = 0,
            bytes => System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(17), 1),
            bytes => System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), 1),
            bytes => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(36), 0),
            bytes => System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(42), 1),
            bytes => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(44), 1),
            bytes => bytes[21] = 0x01,
            bytes => bytes[0] = 0,
            bytes => bytes[510] = 0,
        };

        foreach (var mutate in mutations)
        {
            var sector = valid.ToArray();
            mutate(sector);
            Assert.False(Fat32BootSectorParser.Parse(sector, Fat32TestFixtureBuilder.ImageLength, 0, false).IsValid);
        }
    }

    [Fact]
    public void ClusterMappingRejectsReservedOutOfRangeAndOverflowingClusters()
    {
        var parse = Fat32BootSectorParser.Parse(new Fat32TestFixtureBuilder().Build().AsSpan(0, 512), Fat32TestFixtureBuilder.ImageLength, 0, false);
        var geometry = parse.Geometry!;
        Assert.Throws<ArgumentOutOfRangeException>(() => Fat32ClusterMapper.GetSourceOffset(geometry, 0, 0, Fat32TestFixtureBuilder.ImageLength));
        Assert.Throws<ArgumentOutOfRangeException>(() => Fat32ClusterMapper.GetSourceOffset(geometry, 0, geometry.MaximumDataCluster + 1, Fat32TestFixtureBuilder.ImageLength));
        Assert.Equal((long)Fat32TestFixtureBuilder.FirstDataSector * 512, Fat32ClusterMapper.GetSourceOffset(geometry, 0, 2, Fat32TestFixtureBuilder.ImageLength));
    }

    [Fact]
    public async Task CandidateIdsAndOrderingAreStableForUnchangedInput()
    {
        var bytes = new Fat32TestFixtureBuilder().Build();
        await using var firstSource = new MemoryRandomAccessSource(bytes);
        await using var secondSource = new MemoryRandomAccessSource(bytes);
        var first = await ScanAsync(firstSource);
        var second = await ScanAsync(secondSource);

        Assert.Equal(first.Candidates.Select(item => item.CandidateId), second.Candidates.Select(item => item.CandidateId));
        Assert.Equal(first.Candidates.Count, first.Candidates.Select(item => item.CandidateId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task ProductionApplicationServiceCreatesTrustedSessionAndProvesSourceUnchanged()
    {
        var path = Path.Combine(Path.GetTempPath(), $"drs-fat32-{Guid.NewGuid():N}.img");
        try
        {
            await File.WriteAllBytesAsync(path, new Fat32TestFixtureBuilder().Build());
            var before = await FingerprintAsync(path);
            var factory = new RegularFileRandomAccessSourceFactory();
            var service = new Fat32ImageScanService(factory, new Fat32MetadataScanner(), new Fat32SourceMetadataProvider(factory));

            var session = await service.ScanAsync(path, new(new(0)), null, CancellationToken.None);
            var after = await FingerprintAsync(path);

            Assert.Equal(Fat32ScanOutcome.Completed, session.Result.Outcome);
            Assert.Equal(Fat32ScannerVersions.MetadataPhase7A, session.ScannerVersion);
            Assert.All(session.Result.Candidates, item => Assert.Equal(session.SessionId, item.ScanSessionId));
            Assert.Equal(before, after);
            Assert.Same(session, await service.GetSessionAsync(session.SessionId, CancellationToken.None));
            Assert.False(File.Exists(path + ".recovered"));
            Assert.True(service.DisposeSession(session.SessionId));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ApplicationServiceRefusesSessionPublicationWhenSourceFingerprintChanges()
    {
        var bytes = new Fat32TestFixtureBuilder().Build();
        var factory = new MemorySourceFactory(bytes);
        var before = new Fat32SourceFingerprint("fixture.img", bytes.Length, 1, "A");
        var after = before with { LastWriteTimeUtcTicks = 2, Sha256 = "B" };
        var service = new Fat32ImageScanService(factory, new Fat32MetadataScanner(), new SequencedMetadataProvider(before, after));

        var exception = await Assert.ThrowsAsync<IOException>(() => service.ScanAsync("fixture.img", new(new(0)), null, CancellationToken.None));
        Assert.Contains("SOURCE_CHANGED", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Phase7AProductionBoundaryHasNoWpfWorkerNativeWriteOrRecoveryRoute()
    {
        var root = FindRepositoryRoot();
        var appSource = File.ReadAllText(Path.Combine(root, "src", "DataRecoveryStudio.App", "App.xaml.cs"));
        var scannerSource = File.ReadAllText(Path.Combine(root, "src", "DataRecoveryStudio.Infrastructure", "Fat32MetadataScanner.cs"));
        var serviceSource = File.ReadAllText(Path.Combine(root, "src", "DataRecoveryStudio.Application", "Fat32ImageScanService.cs"));
        var manifest = File.ReadAllText(Path.Combine(root, "src", "DataRecoveryStudio.App", "app.manifest"));

        Assert.DoesNotContain("Fat32MetadataScanner", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Fat32ImageScanService", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("DllImport", scannerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("LibraryImport", scannerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ScanWorker", scannerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("LiveVolume", scannerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Destination", scannerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Write", scannerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ScanWorker", serviceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("LiveVolume", serviceSource, StringComparison.Ordinal);
        Assert.Contains("level=\"asInvoker\"", manifest, StringComparison.Ordinal);

        var fat32Device = new StorageDevice(new("physical:test"), "FAT", "Fixture", StorageDeviceType.UsbDevice, DeviceConnectionStatus.Online,
        [
            new Volume("volume", "E:\\", "FAT", "FAT32", 1_000_000, 500_000, new("physical:test"))
            {
                MountPaths = ["E:\\"],
                IsSupported = true,
                Availability = VolumeAvailability.Available,
            },
        ]);
        Assert.False(ScanCapabilityEvaluator.Evaluate(fat32Device, false, true).CanStartStandard);
    }

    [Theory]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\?\Volume{00000000-0000-0000-0000-000000000000}")]
    public async Task RegularImageBoundaryRejectsDevicePaths(string path)
    {
        var factory = new RegularFileRandomAccessSourceFactory();
        await Assert.ThrowsAsync<NotSupportedException>(async () => await factory.OpenAsync(path, CancellationToken.None));
    }

    private static Fat32DeletedCandidate Find(Fat32ScanResult result, string shortName) => Assert.Single(result.Candidates.Where(item => item.ShortName == shortName));

    private static async Task<Fat32ScanResult> ScanAsync(MemoryRandomAccessSource source, Fat32ScanBudget? budget = null) =>
        await new Fat32MetadataScanner().ScanAsync(source, new(new(0), budget, SourceIdentity: "fixture-sha"), null, CancellationToken.None);

    private static Fat32ScanBudget MinimumProgressBudget() => new(MinimumProgressInterval: TimeSpan.Zero);

    private static async Task<string> FingerprintAsync(string path)
    {
        var info = new FileInfo(path);
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
        return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}:{hash}";
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "DataRecoveryStudio.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }

    private sealed class MemorySourceFactory(byte[] bytes) : IReadOnlyImageSourceFactory
    {
        public ValueTask<IReadOnlyRandomAccessSource> OpenAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyRandomAccessSource>(new MemoryRandomAccessSource(bytes));
        }
    }

    private sealed class SequencedMetadataProvider(Fat32SourceFingerprint first, Fat32SourceFingerprint second) : IFat32SourceMetadataProvider
    {
        private int _calls;
        public ValueTask<Fat32SourceFingerprint> CaptureAsync(string imagePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Interlocked.Increment(ref _calls) == 1 ? first : second);
        }
    }
}
