using System.Security.Cryptography;
using System.Text;

namespace DataRecoveryStudio.Core;

public static class LiveScanProtocol
{
    public const int Version = 4;
    public const int MaximumMessageBytes = 1024 * 1024;
    public const int MaximumStringCharacters = 32 * 1024;
    public const int MaximumDiagnostics = 1_000;
    public const int MaximumCandidates = 100_000;
    public const int MaximumCandidateBatchSize = 256;
    public const int MaximumDiagnosticBatchSize = 128;
    public const int MaximumSerializerDepth = 16;
    public const int MaximumIndividualReadBytes = 1024 * 1024;
    public const string WorkerVersion = "8C.1";
}

public enum LiveScanScannerKind
{
    Unknown = 0,
    NtfsStandardMetadata = 1,
    Fat32StandardMetadata = 2,
    ExFatStandardMetadata = 3,
}

public static class CanonicalVolumeGuidPath
{
    private const string Prefix = "\\\\?\\Volume{";

    public static string Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 64 || value.Any(character => character == '\0' || char.IsControl(character)))
        {
            throw new FormatException("The volume target contains prohibited characters.");
        }

        var candidate = value;
        if (candidate.EndsWith('\\'))
        {
            candidate = candidate[..^1];
        }

        if (!candidate.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || !candidate.EndsWith('}'))
        {
            throw new FormatException("A canonical volume GUID path is required.");
        }

        var guidText = candidate.Substring(Prefix.Length, candidate.Length - Prefix.Length - 1);
        if (!Guid.TryParseExact(guidText, "D", out var guid) || guid == Guid.Empty)
        {
            throw new FormatException("The volume GUID is malformed.");
        }

        var canonical = $"\\\\?\\Volume{{{guid:D}}}";
        if (!candidate.Equals(canonical, StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("The volume target contains an alternate namespace or path component.");
        }

        return canonical;
    }

    public static bool TryParse(string? value, out string canonical)
    {
        try
        {
            canonical = Parse(value!);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            canonical = string.Empty;
            return false;
        }
    }

    public static string ForMetadataApi(string canonicalPath) => Parse(canonicalPath) + "\\";
}

public static class LiveScanIdentity
{
    public static string CreateVolumeIdentity(Volume volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        var canonical = CanonicalVolumeGuidPath.Parse(volume.VolumeGuidPath);
        var material = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{canonical.ToUpperInvariant()}|{volume.FileSystem.Trim().ToUpperInvariant()}|{volume.CapacityBytes}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}

public sealed record LiveScanTargetGrant(
    Guid GrantId,
    DateTimeOffset ExpiresAt,
    string CanonicalVolumeGuidPath,
    string DisplayMountPath,
    string FileSystem,
    string VolumeIdentity,
    IReadOnlyList<string> PhysicalDeviceIdentities,
    IReadOnlyList<int> PhysicalDiskNumbers,
    long CapacityBytes,
    long DiscoveryGeneration,
    bool IsMounted,
    bool IsLocal,
    bool IsSupported,
    bool IsConnected,
    string Nonce)
{
    public LiveScanScannerKind ScannerKind { get; init; } = LiveScanScannerKind.Unknown;
    public Guid CorrelationId { get; init; }
    public IReadOnlyList<VolumeDiskExtent> Extents { get; init; } = [];
}

public sealed record LiveScanBudgets(
    long MaximumBytesRead = 256L * 1024 * 1024,
    long MaximumMftRecords = 250_000,
    int MaximumCandidates = 50_000,
    int MaximumDiagnostics = 500,
    int MaximumAttributesPerRecord = 128,
    int MaximumDataRuns = 16_384,
    int CandidateBatchSize = 128,
    TimeSpan? MaximumDuration = null,
    TimeSpan? MaximumIdleDuration = null,
    int MaximumFatDirectories = 100_000,
    int MaximumFatDirectoryClusters = 250_000,
    int MaximumFatDirectoryEntries = 1_000_000,
    int MaximumFatEntriesInspected = 2_000_000)
{
    public TimeSpan EffectiveMaximumDuration => MaximumDuration ?? TimeSpan.FromMinutes(10);
    public TimeSpan EffectiveMaximumIdleDuration => MaximumIdleDuration ?? TimeSpan.FromSeconds(30);

    public void Validate()
    {
        if (MaximumBytesRead <= 0 || MaximumMftRecords <= 0 || MaximumCandidates <= 0 ||
            MaximumDiagnostics <= 0 || MaximumAttributesPerRecord <= 0 || MaximumDataRuns <= 0 ||
            CandidateBatchSize <= 0 || MaximumFatDirectories <= 0 || MaximumFatDirectoryClusters <= 0 ||
            MaximumFatDirectoryEntries <= 0 || MaximumFatEntriesInspected <= 0 || EffectiveMaximumDuration <= TimeSpan.Zero ||
            EffectiveMaximumIdleDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(LiveScanBudgets), "All live scan budgets must be positive.");
        }
    }
}

public enum LiveScanMessageKind
{
    HandshakeHello = 1,
    HandshakeAccepted = 2,
    StartScan = 3,
    Progress = 4,
    CandidateBatch = 5,
    DiagnosticBatch = 6,
    Cancel = 7,
    TerminalResult = 8,
}

public enum LiveScanTerminalStatus
{
    Completed = 1,
    Partial = 2,
    ChangedDuringScan = 3,
    Canceled = 4,
    PermissionDeclined = 5,
    AccessDenied = 6,
    SourceRemoved = 7,
    TargetChanged = 8,
    UnsupportedFileSystem = 9,
    WorkerMissing = 10,
    WorkerVersionMismatch = 11,
    WorkerStartFailed = 12,
    SecureConnectionFailed = 13,
    WorkerCrashed = 14,
    TimedOut = 15,
    ProtocolFailure = 16,
    Failed = 17,
    PermissionRequired = 18,
}

public enum LiveScanConsistency
{
    LiveBestEffort = 1,
    ChangedDuringScan = 2,
    Partial = 3,
}

public sealed record LiveScanHandshakeHello(int ProtocolVersion, string WorkerVersion, string Nonce)
{
    public LiveScanScannerKind ScannerKind { get; init; } = LiveScanScannerKind.Unknown;
}

public sealed record LiveScanHandshakeAccepted(int ProtocolVersion)
{
    public LiveScanScannerKind ScannerKind { get; init; } = LiveScanScannerKind.Unknown;
}

public sealed record LiveScanStartRequest(LiveScanTargetGrant Grant, LiveScanBudgets Budgets)
{
    public LiveScanScannerKind ScannerKind { get; init; } = LiveScanScannerKind.Unknown;
}

public sealed record LiveScanCancelRequest(string ReasonCode);

public sealed record LiveScanProgressDto(
    long RecordsProcessed,
    long TotalRecords,
    long BytesRead,
    int CandidatesFound,
    string Phase)
{
    public LiveScanScannerKind ScannerKind { get; init; } = LiveScanScannerKind.NtfsStandardMetadata;
    public int DirectoriesExamined { get; init; }
    public int DirectoryEntriesExamined { get; init; }
    public int FatEntriesInspected { get; init; }
    public bool IsBudgetLimited { get; init; }
}

public sealed record LiveScanStreamSummaryDto(
    string Name,
    long LogicalSize,
    NtfsDataStorage Storage,
    NtfsAllocationState AllocationState,
    bool IsSparse,
    bool IsCompressed,
    bool IsEncrypted);

public sealed record LiveScanCandidateDto(
    Guid CandidateId,
    Guid SourceSessionId,
    long MftRecordNumber,
    ushort SequenceNumber,
    string Name,
    string OriginalPath,
    long LogicalSize,
    FileCategory Category,
    bool IsDeleted,
    bool IsDirectory,
    CandidatePathState PathState,
    CandidateRecoverability Recoverability,
    IReadOnlyList<LiveScanStreamSummaryDto> Streams,
    IReadOnlyList<string> DiagnosticCodes)
{
    public LiveScanScannerKind ScannerKind { get; init; } = LiveScanScannerKind.Unknown;
    public string FileSystem { get; init; } = string.Empty;
    public Fat32CandidateKind? Fat32Kind { get; init; }
    public Fat32NameState? Fat32NameState { get; init; }
    public Fat32PathState? Fat32PathState { get; init; }
    public Fat32AllocationAssessment? Fat32Allocation { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? LastAccessedAt { get; init; }
    public DateTimeOffset? ModifiedAt { get; init; }
    public byte AttributeFlags { get; init; }
    public LiveExFatMetadata? ExFat { get; init; }
}

public sealed record LiveScanCandidateBatchDto(int Sequence, IReadOnlyList<LiveScanCandidateDto> Candidates)
{
    public LiveScanScannerKind ScannerKind { get; init; } = LiveScanScannerKind.NtfsStandardMetadata;
}

public sealed record LiveScanDiagnosticDto(string Code, ScanDiagnosticSeverity Severity, string Operation);

public sealed record LiveScanDiagnosticBatchDto(int Sequence, IReadOnlyList<LiveScanDiagnosticDto> Diagnostics)
{
    public LiveScanScannerKind ScannerKind { get; init; } = LiveScanScannerKind.NtfsStandardMetadata;
}

public sealed record LiveScanTerminalResultDto(
    LiveScanTerminalStatus Status,
    LiveScanConsistency Consistency,
    int CandidateCount,
    int DiagnosticCount,
    long RecordsProcessed,
    long BytesRead,
    bool IsPartial,
    string? ReasonCode)
{
    public LiveScanScannerKind ScannerKind { get; init; } = LiveScanScannerKind.Unknown;
    public string FileSystem { get; init; } = string.Empty;
    public int DirectoriesExamined { get; init; }
    public int DirectoryEntriesExamined { get; init; }
    public int FatEntriesInspected { get; init; }
    public bool Fat32GeometryValidated { get; init; }
    public Fat32BootRelationship Fat32BootRelationship { get; init; }
    public bool? Fat32MirroringEnabled { get; init; }
    public int? Fat32FatCount { get; init; }
    public int? Fat32ActiveFatIndex { get; init; }
    public uint? Fat32RootDirectoryCluster { get; init; }
    public string? ConsistencyEvidenceBefore { get; init; }
    public string? ConsistencyEvidenceAfter { get; init; }
    public bool SourceHandleDisposed { get; init; }
    public LiveExFatEvidence? ExFat { get; init; }
    public string? Fat32ScannerVersion { get; init; }
    public Fat32BootRelationship? Fat32BootRelationshipAfter { get; init; }
    public string? Fat32GeometryEvidenceBefore { get; init; }
    public string? Fat32GeometryEvidenceAfter { get; init; }
    public string? Fat32BootEvidenceBefore { get; init; }
    public string? Fat32BootEvidenceAfter { get; init; }
    public string? Fat32SelectedFatEvidenceBefore { get; init; }
    public string? Fat32SelectedFatEvidenceAfter { get; init; }
    public string? Fat32RootChainEvidenceBefore { get; init; }
    public string? Fat32RootChainEvidenceAfter { get; init; }
}

public sealed record LiveScanResult(
    Guid SessionId,
    LiveScanTerminalResultDto Terminal,
    IReadOnlyList<LiveScanCandidateDto> Candidates,
    IReadOnlyList<LiveScanDiagnosticDto> Diagnostics);

public interface ILiveScanWorkerClient
{
    Task<LiveScanResult> ScanAsync(
        LiveScanTargetGrant grant,
        LiveScanBudgets budgets,
        IProgress<LiveScanProgressDto>? progress,
        CancellationToken cancellationToken);
}

public interface IProductionLiveScanWorkerClient : ILiveScanWorkerClient
{
}

public enum LiveScanAuthorizationError
{
    NoCurrentDiscoverySnapshot,
    TargetNotInCurrentSnapshot,
    StaleDiscoveryGeneration,
    GrantExpired,
    GrantAlreadyConsumed,
    Disconnected,
    NotMounted,
    NotLocal,
    UnsupportedFileSystem,
    FeatureDisabled,
    UnsupportedTarget,
    InvalidVolumePath,
}

public sealed class LiveScanAuthorizationException(LiveScanAuthorizationError error) : InvalidOperationException(error.ToString())
{
    public LiveScanAuthorizationError Error { get; } = error;
}
