namespace DataRecoveryStudio.Core;

public interface IFat32MetadataScanner
{
    Task<Fat32ScanResult> ScanAsync(
        IReadOnlyRandomAccessSource source,
        Fat32ScanRequest request,
        IProgress<Fat32ScanProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed record Fat32VolumeContext(long VolumeOffset);

public sealed record Fat32ScanBudget(
    long MaximumBytesRead = 1L * 1024 * 1024 * 1024,
    int MaximumDirectoryDepth = 128,
    int MaximumDirectories = 100_000,
    int MaximumDirectoryClusters = 1_000_000,
    int MaximumDirectoryEntries = 5_000_000,
    int MaximumEntriesPerDirectory = 1_000_000,
    int MaximumFatEntriesInspected = 10_000_000,
    int MaximumFatChainLength = 1_000_000,
    int MaximumVisitedClusters = 1_000_000,
    int MaximumLfnSlotsPerEntry = 20,
    int MaximumFilenameCharacters = 255,
    int MaximumPathCharacters = 32 * 1024,
    int MaximumCandidates = 100_000,
    int MaximumDiagnostics = 1_000,
    int MaximumFatCacheBytes = 256 * 1024,
    TimeSpan? MaximumScanDuration = null,
    TimeSpan? MinimumProgressInterval = null)
{
    public static Fat32ScanBudget HardLimits { get; } = new();

    public TimeSpan EffectiveMaximumScanDuration => MaximumScanDuration ?? TimeSpan.FromHours(4);
    public TimeSpan EffectiveMinimumProgressInterval => MinimumProgressInterval ?? TimeSpan.FromMilliseconds(100);

    public void Validate()
    {
        var hard = HardLimits;
        if (MaximumBytesRead <= 0 || MaximumBytesRead > hard.MaximumBytesRead ||
            MaximumDirectoryDepth <= 0 || MaximumDirectoryDepth > hard.MaximumDirectoryDepth ||
            MaximumDirectories <= 0 || MaximumDirectories > hard.MaximumDirectories ||
            MaximumDirectoryClusters <= 0 || MaximumDirectoryClusters > hard.MaximumDirectoryClusters ||
            MaximumDirectoryEntries <= 0 || MaximumDirectoryEntries > hard.MaximumDirectoryEntries ||
            MaximumEntriesPerDirectory <= 0 || MaximumEntriesPerDirectory > hard.MaximumEntriesPerDirectory ||
            MaximumFatEntriesInspected <= 0 || MaximumFatEntriesInspected > hard.MaximumFatEntriesInspected ||
            MaximumFatChainLength <= 0 || MaximumFatChainLength > hard.MaximumFatChainLength ||
            MaximumVisitedClusters <= 0 || MaximumVisitedClusters > hard.MaximumVisitedClusters ||
            MaximumLfnSlotsPerEntry <= 0 || MaximumLfnSlotsPerEntry > hard.MaximumLfnSlotsPerEntry ||
            MaximumFilenameCharacters <= 0 || MaximumFilenameCharacters > hard.MaximumFilenameCharacters ||
            MaximumPathCharacters <= 0 || MaximumPathCharacters > hard.MaximumPathCharacters ||
            MaximumCandidates <= 0 || MaximumCandidates > hard.MaximumCandidates ||
            MaximumDiagnostics <= 0 || MaximumDiagnostics > hard.MaximumDiagnostics ||
            MaximumFatCacheBytes < 512 || MaximumFatCacheBytes > hard.MaximumFatCacheBytes ||
            EffectiveMaximumScanDuration <= TimeSpan.Zero || EffectiveMaximumScanDuration > hard.EffectiveMaximumScanDuration ||
            EffectiveMinimumProgressInterval < TimeSpan.Zero || EffectiveMinimumProgressInterval > hard.EffectiveMinimumProgressInterval)
        {
            throw new ArgumentOutOfRangeException(nameof(Fat32ScanBudget), "FAT32 scan budgets must be positive and may lower, but not raise, hard implementation limits.");
        }
    }
}

public sealed record Fat32ScanRequest(
    Fat32VolumeContext Volume,
    Fat32ScanBudget? Budget = null,
    Guid ScanSessionId = default,
    string SourceIdentity = "memory")
{
    public Fat32ScanBudget EffectiveBudget => Budget ?? new Fat32ScanBudget();
}

public enum Fat32ScanOutcome
{
    Completed,
    Partial,
    Canceled,
    InvalidVolume,
    SourceChanged,
}

public enum Fat32ScanPhase
{
    ReadingBootSectors,
    ReadingFsInfo,
    TraversingDirectories,
    AnalyzingAllocation,
    Finalizing,
    Completed,
    Partial,
    Canceled,
}

public sealed record Fat32ScanProgress(
    Fat32ScanPhase Phase,
    int DirectoryClustersExamined,
    int DirectoryEntriesExamined,
    int FatEntriesInspected,
    int DeletedCandidatesFound,
    long BytesRead,
    TimeSpan Elapsed,
    bool IsBudgetLimited)
{
    public int DirectoriesTraversed { get; init; }
}

public sealed record Fat32Geometry(
    ushort BytesPerSector,
    byte SectorsPerCluster,
    int ClusterSize,
    ushort ReservedSectorCount,
    byte FatCount,
    uint TotalSectors,
    uint FatSizeSectors,
    byte MediaDescriptor,
    ushort ExtendedFlags,
    ushort FileSystemVersion,
    uint RootDirectoryCluster,
    ushort FsInfoSector,
    ushort BackupBootSector,
    uint FirstFatSector,
    uint FirstDataSector,
    uint ClusterCount,
    uint MaximumDataCluster,
    bool FatMirroringEnabled,
    byte ActiveFatIndex,
    string OemName,
    string? VolumeLabel,
    uint? VolumeId,
    bool UsedBackupBootSector);

public sealed record Fat32FsInfo(uint? FreeClusterCount, uint? NextFreeCluster, bool IsValid);

public enum Fat32CandidateKind { File, Directory }

public enum Fat32NameState
{
    VerifiedLongName,
    ProbableDeletedLongName,
    AmbiguousDeletedLongName,
    ShortNameWithMissingFirstCharacter,
    GeneratedFallback,
    InvalidName,
}

public enum Fat32PathState
{
    CompleteActiveParent,
    NameUncertain,
    ParentDamaged,
    PathTruncated,
    Invalid,
}

public enum Fat32AllocationAssessment
{
    ZeroLength,
    MetadataOnly,
    PossiblyRecoverableContiguous,
    PartiallyOverwrittenOrReused,
    OverwrittenOrReused,
    PreservedAllocatedChain,
    DamagedMetadata,
    AllocationUnknown,
    Unknown,
}

public sealed record Fat32LongNameEvidence(string? Name, bool ChecksumValidated, int SlotCount, int MissingFirstByteMatches);

public sealed record Fat32DeletedCandidate(
    string CandidateId,
    string FileSystemType,
    Fat32CandidateKind Kind,
    string DisplayName,
    string ShortName,
    Fat32LongNameEvidence? LongNameEvidence,
    string ParentDirectoryIdentity,
    string OriginalPath,
    uint FirstCluster,
    uint LogicalFileSize,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? LastAccessedAt,
    DateTimeOffset? ModifiedAt,
    byte Attributes,
    Fat32AllocationAssessment Allocation,
    Fat32NameState NameState,
    Fat32PathState PathState,
    IReadOnlyList<string> DiagnosticCodes,
    Guid ScanSessionId)
{
    internal Fat32CandidateProvenance? Provenance { get; init; }
}

internal sealed record Fat32CandidateProvenance(
    uint ParentDirectoryCluster,
    long DirectorySlotSourceOffset,
    string EvidenceFingerprint);

public sealed record Fat32Diagnostic(
    string Code,
    ScanDiagnosticSeverity Severity,
    string Operation,
    string Reason,
    long? ImageOffset = null,
    uint? Cluster = null);

public sealed record Fat32ScanMetrics(
    int DirectoriesTraversed,
    int DirectoryClustersExamined,
    int DirectoryEntriesExamined,
    int FatEntriesInspected,
    long BytesRead,
    TimeSpan Elapsed);

public sealed record Fat32ScanResult(
    Fat32ScanOutcome Outcome,
    Fat32Geometry? Geometry,
    Fat32FsInfo? FsInfo,
    IReadOnlyList<Fat32DeletedCandidate> Candidates,
    IReadOnlyList<Fat32Diagnostic> Diagnostics,
    Fat32ScanMetrics Metrics,
    bool IsBudgetLimited,
    bool DiagnosticsTruncated,
    string? PartialReason = null);

public sealed record Fat32SourceFingerprint(string CanonicalPath, long Length, long LastWriteTimeUtcTicks, string Sha256);

public static class Fat32ScannerVersions
{
    public const string MetadataPhase7A = "FAT32-METADATA-7A-1";
    public const string RecoveryPhase7B = "FAT32-RECOVERY-7B-1";
}

public sealed record Fat32ScanSession(
    Guid SessionId,
    Fat32SourceFingerprint Source,
    long VolumeOffset,
    Fat32Geometry Geometry,
    string ScannerVersion,
    Fat32ScanResult Result);

public interface IFat32SourceMetadataProvider
{
    ValueTask<Fat32SourceFingerprint> CaptureAsync(string imagePath, CancellationToken cancellationToken);
}

public interface IFat32ImageScanService
{
    Task<Fat32ScanSession> ScanAsync(string imagePath, Fat32ScanRequest request, IProgress<Fat32ScanProgress>? progress, CancellationToken cancellationToken);
    Task<Fat32ScanSession> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken);
    bool DisposeSession(Guid sessionId);
}

internal interface IFat32AllocationTable
{
    ValueTask<Fat32FatEntry> ReadEntryAsync(uint cluster, CancellationToken cancellationToken);
}

internal interface IFat32DirectoryReader
{
    Task<Fat32ScanResult> ScanAsync(IReadOnlyRandomAccessSource source, Fat32ScanRequest request, IProgress<Fat32ScanProgress>? progress, CancellationToken cancellationToken);
}

internal enum Fat32FatEntryKind { Free, NextCluster, EndOfChain, Bad, Reserved, Invalid }

internal sealed record Fat32FatEntry(uint Value, Fat32FatEntryKind Kind, bool CopiesDisagree);
