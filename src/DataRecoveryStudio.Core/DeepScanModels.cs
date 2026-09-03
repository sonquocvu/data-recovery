namespace DataRecoveryStudio.Core;

public enum DeepScanOutcome
{
    Completed,
    Partial,
    Canceled,
    InvalidRequest,
    SourceChanged,
}

public enum DeepScanValidationState
{
    SignatureOnly,
    StructurallyPlausible,
    StructurallyValidated,
    StructurallyInvalid,
    UnsupportedVariant,
}

public enum DeepScanCompleteness
{
    Complete,
    Truncated,
    LengthUnknown,
    Corrupt,
    BudgetLimited,
}

public enum DeepScanConfidence
{
    Low,
    Medium,
    High,
}

public enum DeepScanRangeKind
{
    WholeImage,
    Explicit,
    NtfsUnallocated,
}

public enum DeepScanPhase
{
    PreparingRanges,
    Scanning,
    Validating,
    Finalizing,
    Completed,
    Partial,
    Canceled,
}

public sealed record DeepScanBudget(
    long MaximumSourceBytesScanned = 1L * 1024 * 1024 * 1024,
    int MaximumRanges = 16_384,
    int ChunkSize = 256 * 1024,
    int MaximumSignatureHits = 100_000,
    int MaximumCandidatesValidated = 25_000,
    int MaximumCandidatesReturned = 10_000,
    long MaximumBytesInspectedPerCandidate = 128L * 1024 * 1024,
    long MaximumTotalValidationBytes = 2L * 1024 * 1024 * 1024,
    int MaximumValidationReadsPerCandidate = 65_536,
    int MaximumStructuralElementsPerCandidate = 100_000,
    int MaximumNestedElementsPerCandidate = 64,
    long MaximumTerminatorSearchDistance = 128L * 1024 * 1024,
    int MaximumMetadataStringLength = 4_096,
    int MaximumDiagnostics = 1_000,
    TimeSpan? MaximumScanDuration = null,
    int MaximumOverlapRecords = 10_000,
    TimeSpan? MinimumProgressInterval = null)
{
    public TimeSpan EffectiveMaximumScanDuration => MaximumScanDuration ?? TimeSpan.FromHours(4);
    public TimeSpan EffectiveMinimumProgressInterval => MinimumProgressInterval ?? TimeSpan.FromMilliseconds(100);

    public void Validate()
    {
        if (MaximumSourceBytesScanned <= 0 || MaximumRanges <= 0 || ChunkSize is < 4_096 or > 4 * 1024 * 1024 ||
            MaximumSignatureHits <= 0 || MaximumCandidatesValidated <= 0 || MaximumCandidatesReturned <= 0 ||
            MaximumBytesInspectedPerCandidate <= 0 || MaximumTotalValidationBytes <= 0 || MaximumValidationReadsPerCandidate <= 0 ||
            MaximumStructuralElementsPerCandidate <= 0 || MaximumNestedElementsPerCandidate <= 0 ||
            MaximumTerminatorSearchDistance <= 0 || MaximumMetadataStringLength <= 0 || MaximumDiagnostics <= 0 ||
            EffectiveMaximumScanDuration <= TimeSpan.Zero || MaximumOverlapRecords <= 0 ||
            EffectiveMinimumProgressInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(DeepScanBudget), "Deep Scan safety budgets are outside their supported bounds.");
        }
    }
}

public sealed record DeepScanRange(long Offset, long Length, string Identity, DeepScanRangeKind Kind)
{
    public long End => checked(Offset + Length);
}

public sealed record DeepScanRangeResult(
    IReadOnlyList<DeepScanRange> Ranges,
    bool IsComplete,
    IReadOnlyList<DeepScanDiagnostic> Diagnostics);

public sealed record DeepScanRequest(
    IDeepScanRangeProvider RangeProvider,
    DeepScanBudget? Budget = null,
    bool RetainInvalidCandidates = false)
{
    public DeepScanBudget EffectiveBudget => Budget ?? new DeepScanBudget();
}

public sealed record DeepScanProgress(
    DeepScanPhase Phase,
    long TotalEligibleRangeBytes,
    long BytesExamined,
    int SignatureHits,
    int CandidatesValidated,
    int CandidatesAccepted,
    string? CurrentFormatId,
    TimeSpan Elapsed,
    bool IsBudgetLimited)
{
    public double? Percentage => TotalEligibleRangeBytes <= 0
        ? null
        : Math.Clamp(BytesExamined * 100d / TotalEligibleRangeBytes, 0d, 100d);
}

public sealed record DeepScanDiagnostic(
    string Code,
    ScanDiagnosticSeverity Severity,
    string Reason,
    long? ImageOffset = null,
    string? FormatId = null);

public sealed record DeepScanCandidate(
    string CandidateId,
    string FormatId,
    string SuggestedExtension,
    long StartOffset,
    long? LogicalLength,
    DeepScanConfidence LengthConfidence,
    DeepScanConfidence ValidationConfidence,
    DeepScanCompleteness Completeness,
    DeepScanValidationState ValidationState,
    IReadOnlyList<string> DetectionEvidence,
    string SourceRangeIdentity,
    DeepScanRangeKind RangeKind,
    IReadOnlyList<string> DiagnosticCodes,
    bool IsRecoverySupported,
    bool HasOverlapWarning = false,
    string ValidatorVersion = "",
    string StructuralEvidenceFingerprint = "");

public sealed record DeepScanMetrics(
    long BytesExamined,
    long ValidationBytesRead,
    int SignatureHits,
    int CandidatesValidated,
    int CandidatesAccepted,
    int PeakScanBufferBytes,
    TimeSpan Elapsed);

public sealed record DeepScanResult(
    DeepScanOutcome Outcome,
    IReadOnlyList<DeepScanCandidate> Candidates,
    IReadOnlyList<DeepScanDiagnostic> Diagnostics,
    DeepScanMetrics Metrics,
    bool IsBudgetLimited,
    string? PartialReason = null,
    IReadOnlyList<DeepScanRange>? ScannedRanges = null);

public sealed class FileSignature
{
    private readonly byte[] _bytes;

    public FileSignature(IEnumerable<byte> bytes, int candidateRelativeOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        _bytes = bytes.ToArray();
        CandidateRelativeOffset = candidateRelativeOffset;
    }

    public ReadOnlyMemory<byte> Bytes => _bytes;
    public int CandidateRelativeOffset { get; }
}

public sealed record FileSignatureDescriptor(
    string FormatId,
    string SuggestedExtension,
    string Category,
    int Priority,
    long MinimumCandidateSize,
    long MaximumValidationSize,
    IReadOnlyList<FileSignature> Signatures,
    IReadOnlyList<string> SupportedVariants,
    IReadOnlyList<string> UnsupportedVariants,
    ICarvedCandidateValidator Validator,
    string ValidatorVersion);

public sealed record CandidateValidationContext(
    long CandidateOffset,
    long MaximumAvailableBytes,
    long MaximumBytesInspected,
    int MaximumRandomReads,
    int MaximumStructuralElements,
    int MaximumNestedElements,
    long MaximumTerminatorSearchDistance,
    int MaximumMetadataStringLength,
    DateTimeOffset DeadlineUtc);

public sealed record CandidateValidationResult(
    DeepScanValidationState ValidationState,
    DeepScanCompleteness Completeness,
    DeepScanConfidence Confidence,
    long? LogicalLength,
    DeepScanConfidence LengthConfidence,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> DiagnosticCodes,
    long BytesInspected);

public interface IDeepScanRangeProvider
{
    ValueTask<DeepScanRangeResult> GetRangesAsync(
        IReadOnlyRandomAccessSource source,
        DeepScanBudget budget,
        CancellationToken cancellationToken);
}

public interface IFileSignatureRegistry
{
    IReadOnlyList<FileSignatureDescriptor> Descriptors { get; }
    int LongestSignatureLength { get; }
}

public interface ICarvedCandidateValidator
{
    ValueTask<CandidateValidationResult> ValidateAsync(
        IReadOnlyRandomAccessSource source,
        CandidateValidationContext context,
        CancellationToken cancellationToken);
}

public interface IDeepScanEngine
{
    Task<DeepScanResult> ScanAsync(
        IReadOnlyRandomAccessSource source,
        DeepScanRequest request,
        IProgress<DeepScanProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IFileCarver : IDeepScanEngine
{
}

public sealed record DeepScanSourceFingerprint(
    string CanonicalPath,
    long Length,
    long LastWriteTimeUtcTicks,
    string Sha256);

public sealed record DeepScanSession(
    Guid SessionId,
    DeepScanSourceFingerprint Source,
    DeepScanResult Result);

public interface IDeepScanSourceMetadataProvider
{
    ValueTask<DeepScanSourceFingerprint> CaptureAsync(string imagePath, CancellationToken cancellationToken);
}

public interface IImageDeepScanService
{
    Task<DeepScanSession> ScanAsync(
        string imagePath,
        DeepScanRequest request,
        IProgress<DeepScanProgress>? progress,
        CancellationToken cancellationToken);

    Task<DeepScanSession> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken);

    bool DisposeSession(Guid sessionId);
}

public sealed record NtfsBitmapMetadata(
    long LogicalSize,
    long InitializedSize,
    IReadOnlyList<NtfsDataRun> Runs,
    bool MetadataIsComplete);
