using System.Security.Cryptography;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

public sealed record ValidatedLiveScanTarget(LiveScanTargetGrant Grant);

public interface ILiveScanTargetValidator
{
    Task<ValidatedLiveScanTarget> ValidateAsync(LiveScanTargetGrant grant, CancellationToken cancellationToken);
}

public sealed class LiveScanTargetValidationException(LiveScanTerminalStatus status, string reasonCode)
    : InvalidOperationException(reasonCode)
{
    public LiveScanTerminalStatus Status { get; } = status;
    public string ReasonCode { get; } = reasonCode;
}

public sealed class WindowsVolumeOnlyLiveScanTargetValidator(IWindowsStorageNative? native = null) : ILiveScanTargetValidator
{
    private readonly IWindowsStorageNative _native = native ?? new WindowsStorageNative();

    public Task<ValidatedLiveScanTarget> ValidateAsync(LiveScanTargetGrant grant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var expectedFileSystem = grant.ScannerKind switch
        {
            LiveScanScannerKind.NtfsStandardMetadata => "NTFS",
            LiveScanScannerKind.Fat32StandardMetadata => "FAT32",
            _ => throw new LiveScanTargetValidationException(LiveScanTerminalStatus.UnsupportedFileSystem, "UnknownScannerKind"),
        };
        if (!grant.FileSystem.Equals(expectedFileSystem, StringComparison.OrdinalIgnoreCase))
            throw new LiveScanTargetValidationException(LiveScanTerminalStatus.UnsupportedFileSystem, "ScannerFileSystemMismatch");
        var canonical = CanonicalVolumeGuidPath.Parse(grant.CanonicalVolumeGuidPath);
        if (!grant.IsMounted || !grant.IsLocal || !grant.IsSupported || !grant.IsConnected)
        {
            throw new LiveScanTargetValidationException(LiveScanTerminalStatus.TargetChanged, "GrantEligibilityChanged");
        }

        if (grant.PhysicalDeviceIdentities.Count == 0 || grant.PhysicalDiskNumbers.Count == 0)
        {
            throw new LiveScanTargetValidationException(LiveScanTerminalStatus.TargetChanged, "PhysicalIdentityChanged");
        }

        var volumeName = _native.EnumerateVolumeNames(cancellationToken)
            .FirstOrDefault(name => CanonicalVolumeGuidPath.TryParse(name, out var current) &&
                current.Equals(canonical, StringComparison.OrdinalIgnoreCase));
        if (volumeName is null)
        {
            throw new LiveScanTargetValidationException(LiveScanTerminalStatus.SourceRemoved, "VolumeNotFound");
        }

        var mountPaths = _native.GetVolumePathNames(volumeName)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (mountPaths.Length == 0)
        {
            throw new LiveScanTargetValidationException(LiveScanTerminalStatus.SourceRemoved, "VolumeDisconnected");
        }

        var preferredPath = StorageMetadataNormalizer.SelectPreferredMountPath(mountPaths);
        var driveType = _native.GetDriveType(preferredPath);
        if (driveType is not WindowsDriveType.Fixed and not WindowsDriveType.Removable)
        {
            throw new LiveScanTargetValidationException(LiveScanTerminalStatus.TargetChanged, "VolumeIsNotLocal");
        }

        var information = _native.GetVolumeInformation(volumeName);
        if (!information.FileSystem.Equals(expectedFileSystem, StringComparison.OrdinalIgnoreCase))
        {
            throw new LiveScanTargetValidationException(LiveScanTerminalStatus.UnsupportedFileSystem, "FileSystemChanged");
        }

        var space = _native.GetDiskSpace(preferredPath);
        var capacity = checked((long)Math.Min(space.TotalBytes, long.MaxValue));
        var free = checked((long)Math.Min(space.FreeBytes, long.MaxValue));
        var physical = new PhysicalDeviceId(grant.PhysicalDeviceIdentities[0]);
        var currentVolume = new Volume(volumeName, preferredPath, string.Empty, expectedFileSystem, capacity, Math.Max(0, capacity - Math.Min(capacity, free)), physical)
        {
            VolumeGuidPath = volumeName,
            MountPaths = mountPaths,
            PhysicalDeviceIds = grant.PhysicalDeviceIdentities.Select(value => new PhysicalDeviceId(value)).ToHashSet(),
        };
        if (capacity != grant.CapacityBytes ||
            !LiveScanIdentity.CreateVolumeIdentity(currentVolume).Equals(grant.VolumeIdentity, StringComparison.Ordinal))
        {
            throw new LiveScanTargetValidationException(LiveScanTerminalStatus.TargetChanged, "VolumeIdentityChanged");
        }

        IReadOnlyList<int> currentDiskNumbers;
        using (var handle = _native.OpenMetadataDevice(canonical, MetadataOpenOptions.ReadOnlyMetadata))
        {
            currentDiskNumbers = NativeStorageParser.ParseDiskExtents(
                _native.QueryDevice(handle, StorageNativeConstants.IoctlVolumeGetVolumeDiskExtents, []));
        }

        if (!grant.PhysicalDiskNumbers.ToHashSet().SetEquals(currentDiskNumbers))
        {
            throw new LiveScanTargetValidationException(LiveScanTerminalStatus.TargetChanged, "PhysicalIdentityChanged");
        }

        return Task.FromResult(new ValidatedLiveScanTarget(grant));
    }
}

public static class LiveScanHardLimits
{
    public static LiveScanBudgets Clamp(LiveScanBudgets requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        requested.Validate();
        return new(
            Math.Min(requested.MaximumBytesRead, 1024L * 1024 * 1024),
            Math.Min(requested.MaximumMftRecords, 1_000_000),
            Math.Min(requested.MaximumCandidates, LiveScanProtocol.MaximumCandidates),
            Math.Min(requested.MaximumDiagnostics, LiveScanProtocol.MaximumDiagnostics),
            Math.Min(requested.MaximumAttributesPerRecord, 256),
            Math.Min(requested.MaximumDataRuns, 32_768),
            Math.Min(requested.CandidateBatchSize, LiveScanProtocol.MaximumCandidateBatchSize),
            requested.EffectiveMaximumDuration < TimeSpan.FromMinutes(30) ? requested.EffectiveMaximumDuration : TimeSpan.FromMinutes(30),
            requested.EffectiveMaximumIdleDuration < TimeSpan.FromMinutes(2) ? requested.EffectiveMaximumIdleDuration : TimeSpan.FromMinutes(2),
            Math.Min(requested.MaximumFatDirectories, Fat32ScanBudget.HardLimits.MaximumDirectories),
            Math.Min(requested.MaximumFatDirectoryClusters, Fat32ScanBudget.HardLimits.MaximumDirectoryClusters),
            Math.Min(requested.MaximumFatDirectoryEntries, Fat32ScanBudget.HardLimits.MaximumDirectoryEntries),
            Math.Min(requested.MaximumFatEntriesInspected, Fat32ScanBudget.HardLimits.MaximumFatEntriesInspected));
    }

    public static StandardScanBudgets ToScannerBudgets(LiveScanBudgets budgets) => new(
        MaximumRecords: budgets.MaximumMftRecords,
        MaximumAttributesPerRecord: budgets.MaximumAttributesPerRecord,
        MaximumBytesRead: budgets.MaximumBytesRead,
        MaximumDiagnostics: budgets.MaximumDiagnostics,
        MaximumDataRuns: budgets.MaximumDataRuns,
        MaximumCandidates: budgets.MaximumCandidates);

    public static Fat32ScanBudget ToFat32ScannerBudget(LiveScanBudgets budgets) => new(
        MaximumBytesRead: budgets.MaximumBytesRead,
        MaximumDirectories: budgets.MaximumFatDirectories,
        MaximumDirectoryClusters: budgets.MaximumFatDirectoryClusters,
        MaximumDirectoryEntries: budgets.MaximumFatDirectoryEntries,
        MaximumEntriesPerDirectory: Math.Min(budgets.MaximumFatDirectoryEntries, Fat32ScanBudget.HardLimits.MaximumEntriesPerDirectory),
        MaximumFatEntriesInspected: budgets.MaximumFatEntriesInspected,
        MaximumCandidates: budgets.MaximumCandidates,
        MaximumDiagnostics: budgets.MaximumDiagnostics,
        MaximumScanDuration: budgets.EffectiveMaximumDuration,
        MinimumProgressInterval: TimeSpan.FromMilliseconds(100));
}

public sealed record LiveScanExecutionResult(
    LiveScanTerminalResultDto Terminal,
    IReadOnlyList<LiveScanCandidateDto> Candidates,
    IReadOnlyList<LiveScanDiagnosticDto> Diagnostics);

public interface ILiveScanExecutor
{
    Task<LiveScanExecutionResult> ExecuteAsync(
        Guid sessionId,
        LiveScanTargetGrant grant,
        LiveScanBudgets requestedBudgets,
        IProgress<LiveScanProgressDto>? progress,
        CancellationToken cancellationToken);
}

public sealed class LiveScanExecutor : ILiveScanExecutor
{
    private readonly ILiveScanTargetValidator _targetValidator;
    private readonly ILiveVolumeSourceFactory _sourceFactory;
    private readonly INtfsMetadataScanner _ntfsScanner;
    private readonly IFat32MetadataScanner _fat32Scanner;

    public LiveScanExecutor(
        ILiveScanTargetValidator targetValidator,
        ILiveVolumeSourceFactory sourceFactory,
        INtfsMetadataScanner scanner)
        : this(targetValidator, sourceFactory, scanner, new Fat32MetadataScanner())
    {
    }

    public LiveScanExecutor(
        ILiveScanTargetValidator targetValidator,
        ILiveVolumeSourceFactory sourceFactory,
        INtfsMetadataScanner ntfsScanner,
        IFat32MetadataScanner fat32Scanner)
    {
        _targetValidator = targetValidator ?? throw new ArgumentNullException(nameof(targetValidator));
        _sourceFactory = sourceFactory ?? throw new ArgumentNullException(nameof(sourceFactory));
        _ntfsScanner = ntfsScanner ?? throw new ArgumentNullException(nameof(ntfsScanner));
        _fat32Scanner = fat32Scanner ?? throw new ArgumentNullException(nameof(fat32Scanner));
    }

    public async Task<LiveScanExecutionResult> ExecuteAsync(
        Guid sessionId,
        LiveScanTargetGrant grant,
        LiveScanBudgets requestedBudgets,
        IProgress<LiveScanProgressDto>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var validated = await _targetValidator.ValidateAsync(grant, cancellationToken).ConfigureAwait(false);
        var budgets = LiveScanHardLimits.Clamp(requestedBudgets);
        var consistencyOverhead = checked(2L * (LiveScanProtocol.MaximumIndividualReadBytes + 512));
        await using var source = _sourceFactory.Open(validated.Grant, checked(budgets.MaximumBytesRead + consistencyOverhead));
        return grant.ScannerKind switch
        {
            LiveScanScannerKind.NtfsStandardMetadata => await ExecuteNtfsAsync(sessionId, source, budgets, progress, cancellationToken).ConfigureAwait(false),
            LiveScanScannerKind.Fat32StandardMetadata => await ExecuteFat32Async(sessionId, grant, source, budgets, progress, cancellationToken).ConfigureAwait(false),
            _ => throw new LiveScanTargetValidationException(LiveScanTerminalStatus.UnsupportedFileSystem, "UnknownScannerKind"),
        };
    }

    private async Task<LiveScanExecutionResult> ExecuteNtfsAsync(
        Guid sessionId,
        IReadOnlyRandomAccessSource source,
        LiveScanBudgets budgets,
        IProgress<LiveScanProgressDto>? progress,
        CancellationToken cancellationToken)
    {
        var before = await ReadFingerprintAsync(source, cancellationToken).ConfigureAwait(false);
        var scannerProgress = progress is null ? null : new ThrottledMonotonicProgress(progress, TimeProvider.System);
        var result = await _ntfsScanner.ScanAsync(
            source,
            new StandardScanRequest(new NtfsVolumeContext(0), LiveScanHardLimits.ToScannerBudgets(budgets)),
            scannerProgress,
            cancellationToken).ConfigureAwait(false);
        BootstrapFingerprint? after = null;
        try
        {
            after = await ReadFingerprintAsync(source, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or ArgumentOutOfRangeException)
        {
        }

        var geometryChanged = after is null || before.Geometry != after.Geometry;
        var mftChanged = after is not null && !before.MftFingerprint.AsSpan().SequenceEqual(after.MftFingerprint);
        var changed = geometryChanged || mftChanged;

        var candidates = result.Candidates.Take(budgets.MaximumCandidates)
            .Select(candidate => NormalizeCandidate(sessionId, candidate))
            .ToArray();
        var diagnostics = result.Diagnostics.Take(budgets.MaximumDiagnostics)
            .Select(item => new LiveScanDiagnosticDto(SanitizeCode(item.Code), item.Severity, SanitizeCode(item.Operation)))
            .ToArray();
        var isPartial = changed || result.Outcome != StandardScanOutcome.Completed || candidates.Length < result.Candidates.Count;
        var status = changed
            ? LiveScanTerminalStatus.ChangedDuringScan
            : isPartial ? LiveScanTerminalStatus.Partial : LiveScanTerminalStatus.Completed;
        var consistency = changed
            ? LiveScanConsistency.ChangedDuringScan
            : isPartial ? LiveScanConsistency.Partial : LiveScanConsistency.LiveBestEffort;
        var reason = geometryChanged ? "CriticalGeometryChanged" : mftChanged ? "MftLayoutChanged" : result.PartialReason;
        var terminal = new LiveScanTerminalResultDto(
            status,
            consistency,
            candidates.Length,
            diagnostics.Length,
            result.RecordsProcessed,
            result.BytesRead,
            isPartial,
            reason is null ? null : SanitizeCode(reason))
        {
            ScannerKind = LiveScanScannerKind.NtfsStandardMetadata,
            FileSystem = "NTFS",
        };
        return new(terminal, candidates, diagnostics);
    }

    private async Task<LiveScanExecutionResult> ExecuteFat32Async(
        Guid sessionId,
        LiveScanTargetGrant grant,
        IReadOnlyRandomAccessSource source,
        LiveScanBudgets budgets,
        IProgress<LiveScanProgressDto>? progress,
        CancellationToken cancellationToken)
    {
        var before = await ReadFat32FingerprintAsync(source, cancellationToken).ConfigureAwait(false);
        var scannerProgress = progress is null ? null : new Fat32ThrottledMonotonicProgress(progress, TimeProvider.System);
        var result = await _fat32Scanner.ScanAsync(
            source,
            new Fat32ScanRequest(new Fat32VolumeContext(0), LiveScanHardLimits.ToFat32ScannerBudget(budgets), sessionId, grant.VolumeIdentity),
            scannerProgress,
            cancellationToken).ConfigureAwait(false);

        Fat32BootstrapFingerprint? after = null;
        try
        {
            after = await ReadFat32FingerprintAsync(source, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or ArgumentOutOfRangeException or OverflowException)
        {
        }

        var geometryChanged = after is null || !before.GeometryFingerprint.Equals(after.GeometryFingerprint, StringComparison.Ordinal);
        var bootstrapChanged = after is not null && !before.BootstrapHash.AsSpan().SequenceEqual(after.BootstrapHash);
        var rootChanged = after is not null && !before.RootFingerprint.AsSpan().SequenceEqual(after.RootFingerprint);
        var changed = geometryChanged || bootstrapChanged || rootChanged;
        var candidates = result.Candidates.Take(budgets.MaximumCandidates)
            .Select(candidate => NormalizeFat32Candidate(sessionId, candidate, changed))
            .ToArray();
        var diagnostics = result.Diagnostics.Take(budgets.MaximumDiagnostics)
            .Select(item => new LiveScanDiagnosticDto(SanitizeCode(item.Code), item.Severity, SanitizeCode(item.Operation)))
            .ToArray();
        var scannerPartial = result.Outcome != Fat32ScanOutcome.Completed;
        var isPartial = changed || scannerPartial || result.IsBudgetLimited || candidates.Length < result.Candidates.Count;
        var status = changed
            ? LiveScanTerminalStatus.ChangedDuringScan
            : result.Outcome == Fat32ScanOutcome.Canceled ? LiveScanTerminalStatus.Canceled
            : result.Outcome == Fat32ScanOutcome.InvalidVolume ? LiveScanTerminalStatus.Failed
            : isPartial ? LiveScanTerminalStatus.Partial : LiveScanTerminalStatus.Completed;
        var consistency = changed
            ? LiveScanConsistency.ChangedDuringScan
            : isPartial ? LiveScanConsistency.Partial : LiveScanConsistency.LiveBestEffort;
        var reason = geometryChanged ? "Fat32CriticalGeometryChanged"
            : bootstrapChanged ? "Fat32BootstrapChanged"
            : rootChanged ? "Fat32RootMetadataChanged"
            : result.PartialReason;
        var terminal = new LiveScanTerminalResultDto(
            status,
            consistency,
            candidates.Length,
            diagnostics.Length,
            result.Metrics.DirectoryEntriesExamined,
            result.Metrics.BytesRead,
            isPartial,
            reason is null ? null : SanitizeCode(reason))
        {
            ScannerKind = LiveScanScannerKind.Fat32StandardMetadata,
            FileSystem = "FAT32",
            DirectoriesExamined = result.Metrics.DirectoriesTraversed,
            DirectoryEntriesExamined = result.Metrics.DirectoryEntriesExamined,
            FatEntriesInspected = result.Metrics.FatEntriesInspected,
            Fat32GeometryValidated = result.Geometry is not null && result.Outcome != Fat32ScanOutcome.InvalidVolume,
            Fat32BootRelationship = before.BootRelationship,
            Fat32MirroringEnabled = result.Geometry?.FatMirroringEnabled,
            Fat32FatCount = result.Geometry?.FatCount,
            Fat32ActiveFatIndex = result.Geometry?.ActiveFatIndex,
            Fat32RootDirectoryCluster = result.Geometry?.RootDirectoryCluster,
            ConsistencyEvidenceBefore = before.EvidenceFingerprint,
            ConsistencyEvidenceAfter = after?.EvidenceFingerprint,
            Fat32ScannerVersion = Fat32ScannerVersions.MetadataPhase7A,
            Fat32BootRelationshipAfter = after?.BootRelationship,
            Fat32GeometryEvidenceBefore = before.GeometryEvidence,
            Fat32GeometryEvidenceAfter = after?.GeometryEvidence,
            Fat32BootEvidenceBefore = before.BootEvidence,
            Fat32BootEvidenceAfter = after?.BootEvidence,
            Fat32SelectedFatEvidenceBefore = before.SelectedFatEvidence,
            Fat32SelectedFatEvidenceAfter = after?.SelectedFatEvidence,
            Fat32RootChainEvidenceBefore = before.RootChainEvidence,
            Fat32RootChainEvidenceAfter = after?.RootChainEvidence,
        };
        return new(terminal, candidates, diagnostics);
    }

    private static async Task<BootstrapFingerprint> ReadFingerprintAsync(IReadOnlyRandomAccessSource source, CancellationToken cancellationToken)
    {
        var boot = new byte[512];
        await source.ReadExactlyAsync(0, boot, cancellationToken).ConfigureAwait(false);
        if (!NtfsBootSectorParser.TryParse(boot, out var geometry, out _, out _))
        {
            throw new InvalidDataException("The live NTFS bootstrap metadata is invalid.");
        }

        var valid = geometry!;
        var recordSize = Math.Min(valid.FileRecordSize, LiveScanProtocol.MaximumIndividualReadBytes);
        var offset = checked((long)valid.MftLogicalClusterNumber * valid.ClusterSize);
        var mft = new byte[recordSize];
        await source.ReadExactlyAsync(offset, mft, cancellationToken).ConfigureAwait(false);
        return new(valid, SHA256.HashData(mft));
    }

    private static LiveScanCandidateDto NormalizeCandidate(Guid sessionId, DeletedFileCandidate candidate)
    {
        var identityMaterial = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{sessionId:N}|{candidate.MftRecordNumber}|{candidate.SequenceNumber}"));
        var guidBytes = identityMaterial.AsSpan(0, 16).ToArray();
        var streams = candidate.DataStreams.Take(128).Select(stream => new LiveScanStreamSummaryDto(
            SanitizeText(stream.Name ?? string.Empty, 255),
            Math.Max(0, stream.LogicalSize),
            stream.Storage,
            stream.AllocationState,
            stream.IsSparse,
            stream.IsCompressed,
            stream.IsEncrypted)).ToArray();
        var name = SanitizeText(candidate.Name, 255);
        return new(
            new Guid(guidBytes),
            sessionId,
            candidate.MftRecordNumber,
            candidate.SequenceNumber,
            name,
            SanitizeText(candidate.OriginalPath, LiveScanProtocol.MaximumStringCharacters),
            Math.Max(0, candidate.LogicalSize),
            Categorize(name),
            IsDeleted: true,
            candidate.IsDirectory,
            candidate.PathState,
            candidate.Recoverability,
            streams,
            [])
        {
            ScannerKind = LiveScanScannerKind.NtfsStandardMetadata,
            FileSystem = "NTFS",
        };
    }

    private static LiveScanCandidateDto NormalizeFat32Candidate(Guid sessionId, Fat32DeletedCandidate candidate, bool changedDuringScan)
    {
        var identityMaterial = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{sessionId:N}|{candidate.CandidateId}"));
        var name = SanitizeText(candidate.DisplayName, 255);
        var allocation = changedDuringScan ? Fat32AllocationAssessment.AllocationUnknown : candidate.Allocation;
        return new LiveScanCandidateDto(
            new Guid(identityMaterial.AsSpan(0, 16)),
            sessionId,
            0,
            0,
            name,
            SanitizeText(candidate.OriginalPath, LiveScanProtocol.MaximumStringCharacters),
            candidate.LogicalFileSize,
            Categorize(name),
            IsDeleted: true,
            candidate.Kind == Fat32CandidateKind.Directory,
            MapPathState(candidate.PathState),
            changedDuringScan ? CandidateRecoverability.Unknown : MapRecoverability(candidate.Allocation),
            [],
            candidate.DiagnosticCodes.Take(64).Select(SanitizeCode).ToArray())
        {
            ScannerKind = LiveScanScannerKind.Fat32StandardMetadata,
            FileSystem = "FAT32",
            Fat32Kind = candidate.Kind,
            Fat32NameState = candidate.NameState,
            Fat32PathState = candidate.PathState,
            Fat32Allocation = allocation,
            CreatedAt = candidate.CreatedAt,
            LastAccessedAt = candidate.LastAccessedAt,
            ModifiedAt = candidate.ModifiedAt,
            AttributeFlags = candidate.Attributes,
        };
    }

    private static CandidatePathState MapPathState(Fat32PathState state) => state switch
    {
        Fat32PathState.CompleteActiveParent or Fat32PathState.NameUncertain => CandidatePathState.Complete,
        Fat32PathState.ParentDamaged => CandidatePathState.Orphaned,
        Fat32PathState.PathTruncated => CandidatePathState.StaleParent,
        _ => CandidatePathState.Invalid,
    };

    private static CandidateRecoverability MapRecoverability(Fat32AllocationAssessment allocation) => allocation switch
    {
        Fat32AllocationAssessment.ZeroLength => CandidateRecoverability.ZeroLength,
        Fat32AllocationAssessment.MetadataOnly => CandidateRecoverability.MetadataOnly,
        Fat32AllocationAssessment.PossiblyRecoverableContiguous or Fat32AllocationAssessment.PreservedAllocatedChain => CandidateRecoverability.PossiblyRecoverable,
        Fat32AllocationAssessment.PartiallyOverwrittenOrReused => CandidateRecoverability.PartiallyOverwritten,
        Fat32AllocationAssessment.OverwrittenOrReused => CandidateRecoverability.Overwritten,
        Fat32AllocationAssessment.DamagedMetadata => CandidateRecoverability.DamagedMetadata,
        _ => CandidateRecoverability.Unknown,
    };

    private static async Task<Fat32BootstrapFingerprint> ReadFat32FingerprintAsync(
        IReadOnlyRandomAccessSource source,
        CancellationToken cancellationToken)
    {
        var primary = new byte[512];
        await source.ReadExactlyAsync(0, primary, cancellationToken).ConfigureAwait(false);
        var primaryParsed = Fat32BootSectorParser.Parse(primary, source.Length, 0, false);
        var parsed = primaryParsed;
        var selectedBytes = primary;
        byte[]? backup = null;
        Fat32BootParseResult? backupParsed = null;
        var locator = Fat32BootSectorParser.ReadBackupLocator(primary);
        if (locator is { BackupSector: not 0 and not ushort.MaxValue } && locator.Value.BackupSector < locator.Value.ReservedSectors)
        {
            var backupOffset = checked((long)locator.Value.BackupSector * locator.Value.BytesPerSector);
            if (backupOffset + 512 <= source.Length)
            {
                backup = new byte[512];
                await source.ReadExactlyAsync(backupOffset, backup, cancellationToken).ConfigureAwait(false);
                backupParsed = Fat32BootSectorParser.Parse(backup, source.Length, 0, true);
            }
        }

        if (!parsed.IsValid)
        {
            if (backup is null || backupParsed is not { IsValid: true })
                throw new InvalidDataException("The live FAT32 backup boot sector is invalid.");
            selectedBytes = backup;
            parsed = backupParsed.Value;
        }

        if (!parsed.IsValid || parsed.Geometry is null)
            throw new InvalidDataException("The live FAT32 bootstrap metadata is invalid.");
        var geometry = parsed.Geometry;
        var root = new byte[geometry.ClusterSize];
        var rootOffset = Fat32ClusterMapper.GetSourceOffset(geometry, 0, geometry.RootDirectoryCluster, source.Length);
        await source.ReadExactlyAsync(rootOffset, root, cancellationToken).ConfigureAwait(false);
        var fatEntryOffset = checked((long)geometry.FirstFatSector * geometry.BytesPerSector + checked((long)geometry.RootDirectoryCluster * 4));
        var fatEntry = new byte[4];
        await source.ReadExactlyAsync(fatEntryOffset, fatEntry, cancellationToken).ConfigureAwait(false);
        var geometryMatches = primaryParsed.IsValid && backupParsed is { IsValid: true } &&
            Fat32BootSectorParser.CriticalGeometryMatches(primaryParsed.Geometry!, backupParsed.Value.Geometry!);
        var relationship = !primaryParsed.IsValid
            ? Fat32BootRelationship.BackupSelected
            : backupParsed is not { IsValid: true }
                ? Fat32BootRelationship.PrimaryOnly
                : geometryMatches ? Fat32BootRelationship.PrimaryAndBackupMatch : Fat32BootRelationship.PrimaryAndBackupDiffer;
        var geometryFingerprint = $"{Fat32RecoveryFingerprint.Geometry(geometry)}|{relationship}";
        var bootstrapHash = SHA256.HashData(primary.Concat(backup ?? []).Concat(selectedBytes).ToArray());
        var rootHash = SHA256.HashData(root.Concat(fatEntry).ToArray());
        var geometryEvidence = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(geometryFingerprint)));
        var bootEvidence = Convert.ToHexString(bootstrapHash);
        var selectedFatEvidence = Convert.ToHexString(SHA256.HashData(fatEntry));
        var rootChainEvidence = Convert.ToHexString(SHA256.HashData(root));
        var evidenceFingerprint = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{geometryEvidence}|{bootEvidence}|{selectedFatEvidence}|{rootChainEvidence}|{Convert.ToHexString(rootHash)}")));
        return new(geometry, geometryFingerprint, relationship, bootstrapHash, rootHash, evidenceFingerprint,
            geometryEvidence, bootEvidence, selectedFatEvidence, rootChainEvidence);
    }

    private static FileCategory Categorize(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".tif" or ".tiff" => FileCategory.Image,
        ".doc" or ".docx" or ".pdf" or ".txt" or ".xls" or ".xlsx" => FileCategory.Document,
        ".mp4" or ".mov" or ".avi" or ".mkv" => FileCategory.Video,
        ".mp3" or ".wav" or ".flac" or ".aac" => FileCategory.Audio,
        ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => FileCategory.Archive,
        _ => FileCategory.Unknown,
    };

    private static string SanitizeText(string value, int maximum)
    {
        var cleaned = new string(value.Where(character => character != '\0' && !char.IsControl(character)).Take(maximum).ToArray());
        return cleaned;
    }

    private static string SanitizeCode(string value) =>
        new(value.Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.').Take(128).ToArray());

    private sealed record BootstrapFingerprint(NtfsBootGeometry Geometry, byte[] MftFingerprint);
    private sealed record Fat32BootstrapFingerprint(
        Fat32Geometry Geometry,
        string GeometryFingerprint,
        Fat32BootRelationship BootRelationship,
        byte[] BootstrapHash,
        byte[] RootFingerprint,
        string EvidenceFingerprint,
        string GeometryEvidence,
        string BootEvidence,
        string SelectedFatEvidence,
        string RootChainEvidence);
}

internal sealed class ThrottledMonotonicProgress(IProgress<LiveScanProgressDto> inner, TimeProvider clock)
    : IProgress<StandardScanProgress>
{
    private readonly object _sync = new();
    private long _records;
    private long _bytes;
    private int _candidates;
    private DateTimeOffset _last;

    public void Report(StandardScanProgress value)
    {
        lock (_sync)
        {
            var now = clock.GetUtcNow();
            _records = Math.Max(_records, value.RecordsProcessed);
            _bytes = Math.Max(_bytes, value.BytesRead);
            _candidates = Math.Max(_candidates, value.CandidatesFound);
            if (_last != default && now - _last < TimeSpan.FromMilliseconds(250) && value.RecordsProcessed < value.TotalRecords)
            {
                return;
            }

            _last = now;
            inner.Report(new(_records, Math.Max(_records, value.TotalRecords), _bytes, _candidates, value.Phase[..Math.Min(value.Phase.Length, 256)])
            {
                ScannerKind = LiveScanScannerKind.NtfsStandardMetadata,
            });
        }
    }
}

internal sealed class Fat32ThrottledMonotonicProgress(IProgress<LiveScanProgressDto> inner, TimeProvider clock)
    : IProgress<Fat32ScanProgress>
{
    private readonly object _sync = new();
    private int _clusters;
    private int _entries;
    private int _fatEntries;
    private int _candidates;
    private long _bytes;
    private DateTimeOffset _last;

    public void Report(Fat32ScanProgress value)
    {
        lock (_sync)
        {
            var now = clock.GetUtcNow();
            _clusters = Math.Max(_clusters, value.DirectoriesTraversed);
            _entries = Math.Max(_entries, value.DirectoryEntriesExamined);
            _fatEntries = Math.Max(_fatEntries, value.FatEntriesInspected);
            _candidates = Math.Max(_candidates, value.DeletedCandidatesFound);
            _bytes = Math.Max(_bytes, value.BytesRead);
            if (_last != default && now - _last < TimeSpan.FromMilliseconds(250) && value.Phase is not (Fat32ScanPhase.Completed or Fat32ScanPhase.Partial or Fat32ScanPhase.Canceled))
                return;
            _last = now;
            inner.Report(new(_entries, 0, _bytes, _candidates, $"Progress.Phase.Fat32.{value.Phase}")
            {
                ScannerKind = LiveScanScannerKind.Fat32StandardMetadata,
                DirectoriesExamined = _clusters,
                DirectoryEntriesExamined = _entries,
                FatEntriesInspected = _fatEntries,
                IsBudgetLimited = value.IsBudgetLimited,
            });
        }
    }
}
