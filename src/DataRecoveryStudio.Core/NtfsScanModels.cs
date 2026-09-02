namespace DataRecoveryStudio.Core;

public interface IReadOnlyRandomAccessSource : IAsyncDisposable
{
    long Length { get; }

    ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken);
}

public interface IReadOnlyImageSourceFactory
{
    ValueTask<IReadOnlyRandomAccessSource> OpenAsync(string path, CancellationToken cancellationToken);
}

public interface INtfsMetadataScanner
{
    Task<StandardScanResult> ScanAsync(
        IReadOnlyRandomAccessSource source,
        StandardScanRequest request,
        IProgress<StandardScanProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed record NtfsVolumeContext(long VolumeOffset);

public sealed record StandardScanBudgets(
    long MaximumRecords = 1_000_000,
    int MaximumAttributesPerRecord = 128,
    long MaximumBytesRead = 1L * 1024 * 1024 * 1024,
    int MaximumDiagnostics = 1_000,
    int MaximumPathDepth = 256,
    int MaximumFilenameLength = 255,
    int MaximumDataRuns = 16_384,
    int MaximumMftExtents = 1_024,
    int MaximumAttributeListEntries = 4_096,
    int MaximumExtensionRecords = 1_024,
    int MaximumExtensionDepth = 32,
    int MaximumAttributeListBytes = 4 * 1024 * 1024,
    int MaximumBitmapCacheBytes = 256 * 1024,
    int MaximumAlternatePaths = 32,
    int MaximumPathCharacters = 32 * 1024,
    int MaximumCandidates = 100_000)
{
    public void Validate()
    {
        if (MaximumRecords <= 0 || MaximumBytesRead <= 0 || MaximumAttributesPerRecord <= 0 ||
            MaximumDiagnostics <= 0 || MaximumPathDepth <= 0 || MaximumFilenameLength <= 0 || MaximumDataRuns <= 0 ||
            MaximumMftExtents <= 0 || MaximumAttributeListEntries <= 0 || MaximumExtensionRecords <= 0 ||
            MaximumExtensionDepth <= 0 || MaximumAttributeListBytes <= 0 || MaximumBitmapCacheBytes <= 0 ||
            MaximumAlternatePaths <= 0 || MaximumPathCharacters <= 0 || MaximumCandidates <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(StandardScanBudgets), "All scan safety budgets must be positive.");
        }
    }
}

public sealed record StandardScanRequest(
    NtfsVolumeContext Volume,
    StandardScanBudgets? Budgets = null)
{
    public StandardScanBudgets EffectiveBudgets => Budgets ?? new StandardScanBudgets();
}

public enum StandardScanOutcome
{
    Completed,
    Partial,
    InvalidVolume,
}

public sealed record StandardScanProgress(
    long RecordsProcessed,
    long TotalRecords,
    long BytesRead,
    int CandidatesFound,
    string Phase)
{
    public double Percentage => TotalRecords <= 0
        ? 0
        : Math.Clamp(RecordsProcessed * 100d / TotalRecords, 0d, 100d);
}

public sealed record NtfsBootGeometry(
    ushort BytesPerSector,
    byte SectorsPerCluster,
    int ClusterSize,
    ulong TotalSectors,
    ulong MftLogicalClusterNumber,
    ulong MftMirrorLogicalClusterNumber,
    sbyte ClustersPerFileRecordEncoding,
    int FileRecordSize);

public enum NtfsBootstrapSource
{
    PrimaryMft,
    MftMirror,
}

public sealed record NtfsMftLayout(
    long LogicalSize,
    long AllocatedSize,
    long InitializedSize,
    long DerivedRecordCount,
    int ExtentCount,
    NtfsBootstrapSource BootstrapSource);

public enum ScanDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record ScanDiagnostic(
    string Code,
    ScanDiagnosticSeverity Severity,
    string Operation,
    string Reason,
    long? ImageOffset = null,
    long? MftRecordNumber = null);

public enum CandidatePathState
{
    Complete,
    Orphaned,
    StaleParent,
    CycleDetected,
    Invalid,
}

public enum CandidateRecoverability
{
    Unknown,
    MetadataOnly,
    ResidentDataAvailable,
    PossiblyRecoverable,
    PartiallyOverwritten,
    Overwritten,
    ZeroLength,
    UnsupportedLayout,
    DamagedMetadata,
}

public enum NtfsAllocationState
{
    EntirelyFree,
    EntirelyAllocated,
    Mixed,
    OutsideCoverage,
    Unknown,
}

public enum NtfsDataStorage
{
    None,
    Resident,
    NonResident,
    Unknown,
}

public sealed record NtfsDataRun(long VirtualCluster, long ClusterCount, long? LogicalCluster, bool IsSparse);

public sealed record NtfsDataStream(
    string? Name,
    NtfsDataStorage Storage,
    long LogicalSize,
    long AllocatedSize,
    long InitializedSize,
    IReadOnlyList<NtfsDataRun> Runs,
    bool MetadataIsComplete,
    bool IsSparse = false,
    bool IsCompressed = false,
    bool IsEncrypted = false,
    NtfsAllocationState AllocationState = NtfsAllocationState.Unknown,
    string AttributeIdentity = "");

public sealed record NtfsFileLink(
    string Name,
    string Path,
    CandidatePathState PathState,
    long ParentRecordNumber,
    ushort ParentSequenceNumber,
    byte Namespace);

public sealed record DeletedFileCandidate(
    long MftRecordNumber,
    ushort SequenceNumber,
    bool IsDirectory,
    string Name,
    string OriginalPath,
    CandidatePathState PathState,
    long? ParentRecordNumber,
    ushort? ParentSequenceNumber,
    long LogicalSize,
    long AllocatedSize,
    uint FileAttributes,
    DateTimeOffset? ModifiedAt,
    IReadOnlyList<NtfsDataStream> DataStreams,
    IReadOnlyList<NtfsFileLink> Links,
    CandidateRecoverability Recoverability,
    string RecoverabilityNote);

public sealed record StandardScanResult(
    StandardScanOutcome Outcome,
    NtfsBootGeometry? Geometry,
    NtfsMftLayout? MftLayout,
    IReadOnlyList<DeletedFileCandidate> Candidates,
    IReadOnlyList<ScanDiagnostic> Diagnostics,
    long RecordsProcessed,
    long BytesRead,
    bool DiagnosticsTruncated,
    string? PartialReason = null);
