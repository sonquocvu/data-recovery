using System.Diagnostics;

namespace DataRecoveryStudio.Core;

public sealed record ExFatScanBudget(
    long MaximumSourceBytes = 8L * 1024 * 1024 * 1024,
    int MaximumDirectories = 100_000, int MaximumDirectoryDepth = 128,
    long MaximumDirectoryBytes = 256L * 1024 * 1024, int MaximumEntries = 2_000_000,
    int MaximumFatEntries = 4_000_000, int MaximumFatCacheBytes = 256 * 1024,
    int MaximumChains = 200_000, int MaximumChainLength = 262_144,
    int MaximumVisitedClusters = 1_000_000, int MaximumBitmapCacheBytes = 64 * 1024,
    int MaximumBitmapQueries = 4_000_000, int MaximumUpCaseBytes = 128 * 1024,
    int MaximumUpCaseCacheBytes = 128 * 1024, int MaximumSecondaryCount = 255,
    int MaximumFilenameLength = 255, int MaximumTotalFilenameCharacters = 4_000_000,
    int MaximumPathCharacters = 32768, int MaximumTotalPathCharacters = 4_000_000,
    int MaximumOwnershipRecords = 1_000_000, int MaximumCandidates = 100_000,
    int MaximumDiagnostics = 1000, int MaximumProgressCallbacks = 1000,
    TimeSpan? MaximumDuration = null)
{
    public TimeSpan EffectiveDuration => MaximumDuration ?? TimeSpan.FromHours(4);
    public void Validate()
    {
        var hard = new ExFatScanBudget();
        // Every numeric limit may be lowered, never raised. Zero permits deliberate budget tests.
        foreach (var property in typeof(ExFatScanBudget).GetProperties())
        {
            if (property.PropertyType != typeof(int) && property.PropertyType != typeof(long)) continue;
            var value = Convert.ToInt64(property.GetValue(this), System.Globalization.CultureInfo.InvariantCulture);
            var ceiling = Convert.ToInt64(property.GetValue(hard), System.Globalization.CultureInfo.InvariantCulture);
            if (value < 0 || value > ceiling) throw new ArgumentOutOfRangeException(property.Name);
        }
        if (EffectiveDuration <= TimeSpan.Zero || EffectiveDuration > hard.EffectiveDuration)
            throw new ArgumentOutOfRangeException(nameof(MaximumDuration));
        if (MaximumDiagnostics < 1 || MaximumProgressCallbacks < 1)
            throw new ArgumentOutOfRangeException(nameof(MaximumDiagnostics), "Reserve at least one diagnostic and one terminal progress slot.");
    }
}

public sealed record ExFatScanRequest(long VolumeOffset = 0, ExFatScanBudget? Budget = null)
{
    public ExFatScanBudget EffectiveBudget => Budget ?? new();
    internal string SourceIdentity { get; init; } = "unbound";
    internal ExFatWork? Work { get; init; }
    internal IExFatMetadataReadObserver? ReadObserver { get; init; }
    internal bool SuppressTerminalProgress { get; init; }
    internal IReadOnlySet<string>? RecoverySelection { get; init; }
}

public enum ExFatScanOutcome { Completed, Partial, Canceled, InvalidVolume, SourceChanged }
public enum ExFatScanPhase { Fingerprinting, Boot, Directories, UpCase, Ownership, Allocation, Finalizing, Terminal }
public enum ExFatNameEvidence { VerifiedDeletedName, ChecksumRecoveredName, HashVerifiedName, ProbableName, AmbiguousName, DamagedName, GeneratedFallbackRequired }
public enum ExFatAllocationEvidence
{
    ZeroLength, ContiguousAllFree, ContiguousPartiallyAllocated, ContiguousFullyAllocated,
    PreservedChainAllFree, PreservedChainPartiallyAllocated, PreservedChainFullyAllocated,
    ActiveOwnershipConflict, BitmapUnavailable, FatChainMissing, FatChainCycle, FatChainDamaged,
    FatBitmapDisagreement, UnsupportedLayout, DamagedMetadata, Unknown,
}
public enum ExFatPathState { CompleteActiveParent, NameUncertain, ParentDamaged }
public enum ExFatRecoverabilityState { EmptyFileMetadata, AllocationSuggestsPossibleContent, Blocked, Unknown }
public enum ExFatLayout { Contiguous, FatChain }
public enum ExFatTimestampState { Valid, OffsetUnknown, Invalid }
public sealed record ExFatTimestamp(DateTime? LocalTime, int? UtcOffsetMinutes, ExFatTimestampState State);
public sealed record ExFatScanDiagnostic(string Code, string Reason, long? MetadataOffset = null);
public sealed record ExFatGeometry(int BytesPerSector, int SectorsPerCluster, int ClusterSize,
    ulong PartitionOffset, ulong VolumeLength, uint FatOffset, uint FatLength, uint ClusterHeapOffset,
    uint ClusterCount, uint RootCluster, uint SerialNumber, ushort Revision, ushort VolumeFlags,
    byte NumberOfFats, byte DriveSelect, byte PercentInUse, string Fingerprint);
public sealed record ExFatBootEvidence(bool MainValid, bool BackupValid, bool UsedBackup,
    string? MainFingerprint, string? BackupFingerprint);
public sealed record ExFatScanProgress(ExFatScanPhase Phase, long SourceBytesRead, long FingerprintBytesRead,
    int DirectoriesVisited, int EntriesExamined, int FatEntriesInspected, int BitmapQueries,
    int EntrySetsValidated, int DeletedCandidatesFound, int DiagnosticsEmitted, bool BudgetLimited,
    TimeSpan Elapsed);
public sealed record ExFatScanCandidate(string CandidateId, string DisplayName, string ParentPath,
    ExFatPathState PathState, long LogicalSize, long ValidDataLength, ExFatLayout Layout, ushort Attributes,
    ExFatTimestamp Created, ExFatTimestamp Modified, ExFatTimestamp Accessed,
    ExFatNameEvidence NameEvidence, ExFatAllocationEvidence AllocationEvidence,
    ExFatRecoverabilityState Recoverability, bool MetadataDamaged, IReadOnlyList<string> DiagnosticCodes)
{
    internal ExFatCandidateProvenance? Provenance { get; init; }
    public bool IsPartial { get; init; }
}
internal sealed record ExFatCandidateProvenance(uint ParentCluster, long PrimaryOffset, string EntrySetFingerprint,
    uint FirstCluster, long DataLength, long ValidDataLength, byte Flags, string OriginalName,
    bool OrdinaryChecksumValid, bool RecoveredChecksumValid, bool NameHashValid);
public sealed record ExFatScanResult(ExFatScanOutcome Outcome, ExFatGeometry? Geometry,
    ExFatBootEvidence? BootEvidence, string? VolumeLabel, IReadOnlyList<ExFatScanCandidate> Candidates,
    IReadOnlyList<ExFatScanDiagnostic> Diagnostics, ExFatScanProgress Metrics)
{
    // A public record clone cannot carry the identity of the original production result.
    internal ExFatResultSeal? ProductionSeal { get; init; }
}
// Reference equality avoids recursive record equality while binding exactly one parser result.
internal sealed class ExFatResultSeal
{
    internal ExFatScanResult? Result { get; set; }
    internal IReadOnlyDictionary<string, IReadOnlyList<uint>> RecoveryLayouts { get; init; } = new Dictionary<string, IReadOnlyList<uint>>();
}
public sealed record ExFatSourceFingerprint(string CanonicalPath, long Length, long LastWriteTimeUtcTicks, string Sha256)
{
    public string? FileIdentity { get; init; }
}
public sealed record ExFatScanSession(Guid SessionId, ExFatSourceFingerprint Source, long VolumeOffset,
    ExFatGeometry Geometry, ExFatBootEvidence BootEvidence, string ScannerVersion, ExFatScanResult Result);
public sealed record ExFatImageScanResult(ExFatScanResult Result, ExFatScanSession? Session);
public interface IExFatMetadataScanner
{
    Task<ExFatScanResult> ScanAsync(IReadOnlyRandomAccessSource source, ExFatScanRequest request,
        IProgress<ExFatScanProgress>? progress, CancellationToken cancellationToken);
}
public interface IExFatImageScanService
{
    Task<ExFatImageScanResult> ScanAsync(string imagePath, ExFatScanRequest request,
        IProgress<ExFatScanProgress>? progress, CancellationToken cancellationToken);
    Task<ExFatScanSession> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken);
    bool DisposeSession(Guid sessionId);
}
public interface IExFatSourceMetadataProvider
{
    ValueTask<ExFatSourceFingerprint> CaptureAsync(string path, IReadOnlyRandomAccessSource source,
        ExFatScanRequest request, CancellationToken cancellationToken);
}
public static class ExFatScannerVersion { public const string Phase8A = "EXFAT-METADATA-8A-1"; }

internal sealed class ExFatBudgetException(string limit) : Exception(limit);
internal sealed class ExFatWork(ExFatScanBudget budget)
{
    internal readonly Stopwatch Clock = Stopwatch.StartNew();
    internal long Bytes, FingerprintBytes, DirectoryBytes, ReservedReadBytes;
    internal long ReservedForFutureBytes;
    internal int Directories, Entries, FatEntries, BitmapQueries, Sets, Candidates, Diagnostics, Callbacks;
    internal int Chains, VisitedClusters, NameCharacters;
    internal bool Limited;
    internal void Check(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Require(Clock.Elapsed <= budget.EffectiveDuration, "Duration");
    }
    internal void Require(bool condition, string limit)
    {
        if (condition) return;
        Limited = true;
        throw new ExFatBudgetException(limit);
    }
    internal void BeforeRead(int bytes, CancellationToken token)
    {
        Check(token);
        Require(bytes <= budget.MaximumSourceBytes - ReservedReadBytes - ReservedForFutureBytes, "SourceBytes");
        ReservedReadBytes += bytes;
    }
    internal void AfterRead(int bytes, bool fingerprint)
    {
        Bytes += bytes;
        if (fingerprint) FingerprintBytes += bytes;
    }
    internal ExFatScanProgress Snapshot(ExFatScanPhase phase) => new(phase, Bytes, FingerprintBytes,
        Directories, Entries, FatEntries, BitmapQueries, Sets, Candidates, Diagnostics, Limited, Clock.Elapsed);
}

internal enum ExFatMetadataReadKind { Boot, Fat, Bitmap, UpCase, Directory }
internal interface IExFatMetadataReadObserver
{
    void BeforeRead(long offset, int length, ExFatMetadataReadKind kind, bool bootstrap);
    void AfterRead(long offset, ReadOnlySpan<byte> bytes, ExFatMetadataReadKind kind, bool bootstrap);
    void ReadFailed(ExFatMetadataReadKind kind);
}
