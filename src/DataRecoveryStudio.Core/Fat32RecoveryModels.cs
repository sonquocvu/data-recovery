using System.Security.Cryptography;
using System.Text;

namespace DataRecoveryStudio.Core;

public sealed record Fat32RecoveryPolicy(
    bool AllowPreservedFatChain = false,
    bool AllowDeterministicDamagedContent = false,
    bool CreateDestinationIfMissing = false,
    int MaximumCandidates = 1_000,
    long MaximumLogicalBytesPerCandidate = 4L * 1024 * 1024 * 1024,
    long MaximumTotalRecoveredBytes = 16L * 1024 * 1024 * 1024,
    long MaximumSourceReads = 2_000_000,
    int MaximumClustersPerCandidate = 1_000_000,
    int MaximumTotalClusters = 4_000_000,
    int MaximumFatEntriesInspected = 10_000_000,
    int MaximumActiveFilesAnalyzed = 1_000_000,
    int MaximumActiveOwnershipClusters = 4_000_000,
    int MaximumCollisionAttempts = 1_000,
    int MaximumDiagnostics = 1_000,
    TimeSpan? MaximumDuration = null,
    int BufferSize = 128 * 1024,
    int MaximumCleanupAttempts = 3)
{
    public static Fat32RecoveryPolicy HardLimits { get; } = new();
    public TimeSpan EffectiveMaximumDuration => MaximumDuration ?? TimeSpan.FromHours(4);

    public void Validate()
    {
        var hard = HardLimits;
        if (MaximumCandidates is <= 0 || MaximumCandidates > hard.MaximumCandidates ||
            MaximumLogicalBytesPerCandidate is <= 0 || MaximumLogicalBytesPerCandidate > hard.MaximumLogicalBytesPerCandidate ||
            MaximumTotalRecoveredBytes is <= 0 || MaximumTotalRecoveredBytes > hard.MaximumTotalRecoveredBytes ||
            MaximumSourceReads is <= 0 || MaximumSourceReads > hard.MaximumSourceReads ||
            MaximumClustersPerCandidate is <= 0 || MaximumClustersPerCandidate > hard.MaximumClustersPerCandidate ||
            MaximumTotalClusters is <= 0 || MaximumTotalClusters > hard.MaximumTotalClusters ||
            MaximumFatEntriesInspected is <= 0 || MaximumFatEntriesInspected > hard.MaximumFatEntriesInspected ||
            MaximumActiveFilesAnalyzed is <= 0 || MaximumActiveFilesAnalyzed > hard.MaximumActiveFilesAnalyzed ||
            MaximumActiveOwnershipClusters is <= 0 || MaximumActiveOwnershipClusters > hard.MaximumActiveOwnershipClusters ||
            MaximumCollisionAttempts is <= 0 || MaximumCollisionAttempts > hard.MaximumCollisionAttempts ||
            MaximumDiagnostics is <= 0 || MaximumDiagnostics > hard.MaximumDiagnostics ||
            BufferSize is < 4_096 or > 4 * 1024 * 1024 ||
            MaximumCleanupAttempts is <= 0 or > 10 ||
            EffectiveMaximumDuration <= TimeSpan.Zero || EffectiveMaximumDuration > hard.EffectiveMaximumDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(Fat32RecoveryPolicy), "FAT32 recovery limits must lower, and cannot raise, the fixed Phase 7B safety envelope.");
        }
    }
}

public sealed record Fat32RecoveryRequest(
    Guid ScanSessionId,
    IReadOnlyList<string> CandidateIds,
    string DestinationRoot,
    Fat32RecoveryPolicy? Policy = null)
{
    public Fat32RecoveryPolicy EffectivePolicy => Policy ?? new();
}

public sealed record Fat32RecoveryPlanItem(
    string CandidateId,
    string OutputName,
    Fat32NameState OriginalNameConfidence,
    long ExpectedBytes,
    Fat32AllocationAssessment AllocationAtScan,
    bool IsEligible,
    string? Warning);

public sealed record Fat32RecoveryPlan(
    Guid PlanId,
    Guid ScanSessionId,
    string DestinationRoot,
    IReadOnlyList<Fat32RecoveryPlanItem> Items,
    long TotalKnownBytes);

public enum Fat32RecoveryOutcome
{
    RecoveredCopyVerified,
    RecoveredContiguousHeuristic,
    RecoveredPreservedChainWithWarning,
    RecoveredDamagedWithWarning,
    SkippedByPolicy,
    ActiveClusterConflict,
    AllocationChanged,
    CandidateChanged,
    SourceChanged,
    DamagedMetadata,
    SourceReadFailed,
    DestinationFailed,
    VerificationFailed,
    Canceled,
    CleanupFailed,
}

public enum Fat32RecoveryBatchOutcome
{
    Completed,
    CompletedWithWarnings,
    CompletedWithFailures,
    Canceled,
    SourceChanged,
    DestinationUnavailable,
    BudgetExceeded,
}

public sealed record Fat32RecoveryDiagnostic(string Code, string Reason, string? CandidateId = null, uint? Cluster = null);

public sealed record Fat32RecoveryFileResult(
    string CandidateId,
    Fat32RecoveryOutcome Outcome,
    string? OutputPath,
    string OutputName,
    Fat32NameState OriginalNameConfidence,
    long BytesWritten,
    string? Sha256,
    RecoveryVerificationState Verification,
    string Message,
    IReadOnlyList<Fat32RecoveryDiagnostic> Diagnostics);

public sealed record Fat32RecoveryBatchResult(
    Fat32RecoveryBatchOutcome Outcome,
    IReadOnlyList<Fat32RecoveryFileResult> Files,
    int CompletedItems,
    int TotalItems,
    long VerifiedBytes,
    int SuccessCount,
    int WarningCount,
    int SkippedCount,
    int FailedCount,
    IReadOnlyList<Fat32RecoveryDiagnostic> Diagnostics,
    TimeSpan Elapsed);

public sealed record Fat32RecoveryProgress(
    string? CurrentCandidateId,
    string? CurrentFile,
    int CompletedItems,
    int TotalItems,
    long CurrentBytes,
    long TotalKnownBytes,
    int SuccessCount,
    int WarningCount,
    int SkippedCount,
    int FailedCount);

public interface IFat32ImageRecoveryService
{
    Task<Fat32RecoveryPlan> CreateRecoveryPlanAsync(Fat32RecoveryRequest request, CancellationToken cancellationToken);
    Task<Fat32RecoveryBatchResult> RecoverAsync(Guid planId, IProgress<Fat32RecoveryProgress>? progress, CancellationToken cancellationToken);
}

public sealed record TrustedFat32RecoveryCandidate
{
    internal TrustedFat32RecoveryCandidate(
        Guid scanSessionId,
        Fat32SourceFingerprint source,
        long volumeOffset,
        Fat32Geometry geometry,
        string scannerVersion,
        Fat32DeletedCandidate candidate,
        uint parentDirectoryCluster,
        long directorySlotSourceOffset,
        string evidenceFingerprint,
        string outputName,
        bool isEligible,
        string? warning)
    {
        ScanSessionId = scanSessionId;
        Source = source;
        VolumeOffset = volumeOffset;
        Geometry = geometry;
        ScannerVersion = scannerVersion;
        Candidate = candidate;
        ParentDirectoryCluster = parentDirectoryCluster;
        DirectorySlotSourceOffset = directorySlotSourceOffset;
        EvidenceFingerprint = evidenceFingerprint;
        OutputName = outputName;
        IsEligible = isEligible;
        Warning = warning;
    }

    public Guid ScanSessionId { get; }
    public Fat32SourceFingerprint Source { get; }
    public long VolumeOffset { get; }
    public Fat32Geometry Geometry { get; }
    public string ScannerVersion { get; }
    public Fat32DeletedCandidate Candidate { get; }
    public uint ParentDirectoryCluster { get; }
    public long DirectorySlotSourceOffset { get; }
    public string EvidenceFingerprint { get; }
    public string OutputName { get; }
    public bool IsEligible { get; }
    public string? Warning { get; }
}

public sealed record TrustedFat32RecoveryBatchRequest
{
    internal TrustedFat32RecoveryBatchRequest(
        Guid planId,
        Guid scanSessionId,
        Fat32SourceFingerprint source,
        long volumeOffset,
        Fat32Geometry geometry,
        Fat32ScanBudget scanBudget,
        string destinationRoot,
        Fat32RecoveryPolicy policy,
        IReadOnlyList<TrustedFat32RecoveryCandidate> items,
        long totalKnownBytes)
    {
        PlanId = planId;
        ScanSessionId = scanSessionId;
        Source = source;
        VolumeOffset = volumeOffset;
        Geometry = geometry;
        ScanBudget = scanBudget;
        DestinationRoot = destinationRoot;
        Policy = policy;
        Items = items;
        TotalKnownBytes = totalKnownBytes;
    }

    public Guid PlanId { get; }
    public Guid ScanSessionId { get; }
    public Fat32SourceFingerprint Source { get; }
    public long VolumeOffset { get; }
    public Fat32Geometry Geometry { get; }
    public Fat32ScanBudget ScanBudget { get; }
    public string DestinationRoot { get; }
    public Fat32RecoveryPolicy Policy { get; }
    public IReadOnlyList<TrustedFat32RecoveryCandidate> Items { get; }
    public long TotalKnownBytes { get; }
}

public interface ITrustedFat32RecoveryEngine
{
    Task<Fat32RecoveryBatchResult> RecoverAsync(TrustedFat32RecoveryBatchRequest request, IProgress<Fat32RecoveryProgress>? progress, CancellationToken cancellationToken);
}

public interface IFat32RecoveryPlanResolver
{
    string ResolveOutputName(Fat32DeletedCandidate candidate, uint parentDirectoryCluster, long directorySlotSourceOffset);
}

internal static class Fat32RecoveryFingerprint
{
    public static string Geometry(Fat32Geometry geometry) => Hash(string.Join('|',
        geometry.BytesPerSector, geometry.SectorsPerCluster, geometry.ClusterSize, geometry.ReservedSectorCount,
        geometry.FatCount, geometry.TotalSectors, geometry.FatSizeSectors, geometry.MediaDescriptor,
        geometry.ExtendedFlags, geometry.FileSystemVersion, geometry.RootDirectoryCluster, geometry.FirstFatSector,
        geometry.FirstDataSector, geometry.ClusterCount, geometry.MaximumDataCluster, geometry.FatMirroringEnabled,
        geometry.ActiveFatIndex, geometry.UsedBackupBootSector));

    public static string Candidate(Fat32DeletedCandidate candidate) => Hash(string.Join('|',
        candidate.CandidateId, candidate.FileSystemType, candidate.Kind, candidate.DisplayName, candidate.ShortName,
        candidate.LongNameEvidence?.Name, candidate.LongNameEvidence?.ChecksumValidated,
        candidate.LongNameEvidence?.SlotCount, candidate.LongNameEvidence?.MissingFirstByteMatches,
        candidate.ParentDirectoryIdentity, candidate.OriginalPath, candidate.FirstCluster, candidate.LogicalFileSize,
        candidate.Attributes, candidate.NameState, candidate.PathState, candidate.ScanSessionId));

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
