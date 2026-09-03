namespace DataRecoveryStudio.Core;

public sealed record DeepScanRecoveryPolicy(
    bool AllowMediumTruncated = false,
    bool AllowOverlappingCandidates = false,
    bool CreateDestinationIfMissing = false,
    int MaximumCandidatesPerBatch = 1_000,
    long MaximumBytesPerCandidate = 4L * 1024 * 1024 * 1024,
    long MaximumTotalBytes = 16L * 1024 * 1024 * 1024,
    long MaximumSourceReads = 2_000_000,
    TimeSpan? MaximumRecoveryDuration = null,
    int BufferSize = 128 * 1024,
    int MaximumCollisionAttempts = 1_000,
    int MaximumDiagnostics = 1_000,
    long MaximumPostWriteVerificationBytes = 16L * 1024 * 1024 * 1024,
    int MaximumPartialCleanupAttempts = 3)
{
    public TimeSpan EffectiveMaximumRecoveryDuration => MaximumRecoveryDuration ?? TimeSpan.FromHours(4);

    public void Validate()
    {
        if (MaximumCandidatesPerBatch is <= 0 or > 1_000 || MaximumBytesPerCandidate is <= 0 or > 4L * 1024 * 1024 * 1024 ||
            MaximumTotalBytes is <= 0 or > 16L * 1024 * 1024 * 1024 || MaximumSourceReads is <= 0 or > 2_000_000 ||
            EffectiveMaximumRecoveryDuration <= TimeSpan.Zero || EffectiveMaximumRecoveryDuration > TimeSpan.FromHours(4) ||
            BufferSize is < 4_096 or > 4 * 1024 * 1024 || MaximumCollisionAttempts is <= 0 or > 10_000 ||
            MaximumDiagnostics is <= 0 or > 10_000 || MaximumPostWriteVerificationBytes is <= 0 or > 16L * 1024 * 1024 * 1024 ||
            MaximumPartialCleanupAttempts is <= 0 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(DeepScanRecoveryPolicy), "Deep Scan recovery limits exceed the fixed Phase 6B safety envelope.");
        }
    }
}

public sealed record DeepScanRecoveryRequest(
    Guid ScanSessionId,
    IReadOnlyList<string> CandidateIds,
    string DestinationRoot,
    DeepScanRecoveryPolicy? Policy = null)
{
    public DeepScanRecoveryPolicy EffectivePolicy => Policy ?? new DeepScanRecoveryPolicy();
}

public sealed record DeepScanRecoveryPlanItem(
    string CandidateId,
    string FormatId,
    string GeneratedFileName,
    long ExpectedBytes,
    bool IsEligible,
    bool IsPartial,
    string? Warning);

public sealed record DeepScanRecoveryPlan(
    Guid PlanId,
    Guid ScanSessionId,
    string DestinationRoot,
    IReadOnlyList<DeepScanRecoveryPlanItem> Items,
    long TotalKnownBytes);

public enum DeepScanRecoveryFileOutcome
{
    CarvedAndCopyVerified,
    CarvedPartialWithWarning,
    SkippedByPolicy,
    CandidateChanged,
    SourceChanged,
    UnsupportedCandidate,
    SourceReadFailed,
    DestinationFailed,
    VerificationFailed,
    Canceled,
    CleanupFailed,
}

public enum DeepScanRecoveryBatchOutcome
{
    Completed,
    CompletedWithWarnings,
    CompletedWithFailures,
    Canceled,
    SourceChanged,
    DestinationUnavailable,
}

public sealed record DeepScanRecoveryDiagnostic(
    string Code,
    string Reason,
    string? CandidateId = null,
    string? FormatId = null,
    string? SanitizedFileName = null,
    long? ImageOffset = null,
    long? ExpectedBytes = null,
    long? ActualBytes = null);

public sealed record DeepScanRecoveryFileResult(
    string CandidateId,
    string FormatId,
    DeepScanRecoveryFileOutcome Outcome,
    string? OutputPath,
    long BytesWritten,
    string? Sha256,
    RecoveryVerificationState Verification,
    string Message,
    IReadOnlyList<DeepScanRecoveryDiagnostic> Diagnostics);

public sealed record DeepScanRecoveryBatchResult(
    DeepScanRecoveryBatchOutcome Outcome,
    IReadOnlyList<DeepScanRecoveryFileResult> Files,
    int CompletedItems,
    int TotalItems,
    long VerifiedBytes,
    IReadOnlyList<DeepScanRecoveryDiagnostic> Diagnostics,
    TimeSpan Elapsed);

public sealed record DeepScanRecoveryProgress(
    string? CurrentCandidateId,
    string? CurrentFile,
    int CompletedItems,
    int TotalItems,
    long CurrentBytes,
    long TotalKnownBytes);

public sealed record TrustedDeepScanCandidate
{
    internal TrustedDeepScanCandidate(
        Guid scanSessionId,
        string candidateId,
        DeepScanSourceFingerprint source,
        string sourceRangeIdentity,
        long sourceRangeOffset,
        long sourceRangeLength,
        long candidateOffset,
        long candidateLength,
        DeepScanConfidence lengthConfidence,
        string formatId,
        string suggestedExtension,
        string validatorVersion,
        DeepScanConfidence validationConfidence,
        DeepScanCompleteness completeness,
        DeepScanValidationState validationState,
        string detectionEvidenceFingerprint,
        DeepScanRangeKind rangeKind,
        bool hasOverlapWarning,
        bool isEligible,
        bool isPartial,
        string generatedFileName,
        string? policyWarning)
    {
        ScanSessionId = scanSessionId;
        CandidateId = candidateId;
        Source = source;
        SourceRangeIdentity = sourceRangeIdentity;
        SourceRangeOffset = sourceRangeOffset;
        SourceRangeLength = sourceRangeLength;
        CandidateOffset = candidateOffset;
        CandidateLength = candidateLength;
        LengthConfidence = lengthConfidence;
        FormatId = formatId;
        SuggestedExtension = suggestedExtension;
        ValidatorVersion = validatorVersion;
        ValidationConfidence = validationConfidence;
        Completeness = completeness;
        ValidationState = validationState;
        DetectionEvidenceFingerprint = detectionEvidenceFingerprint;
        RangeKind = rangeKind;
        HasOverlapWarning = hasOverlapWarning;
        IsEligible = isEligible;
        IsPartial = isPartial;
        GeneratedFileName = generatedFileName;
        PolicyWarning = policyWarning;
    }

    public Guid ScanSessionId { get; }
    public string CandidateId { get; }
    public DeepScanSourceFingerprint Source { get; }
    public string SourceRangeIdentity { get; }
    public long SourceRangeOffset { get; }
    public long SourceRangeLength { get; }
    public long CandidateOffset { get; }
    public long CandidateLength { get; }
    public DeepScanConfidence LengthConfidence { get; }
    public string FormatId { get; }
    public string SuggestedExtension { get; }
    public string ValidatorVersion { get; }
    public DeepScanConfidence ValidationConfidence { get; }
    public DeepScanCompleteness Completeness { get; }
    public DeepScanValidationState ValidationState { get; }
    public string DetectionEvidenceFingerprint { get; }
    public DeepScanRangeKind RangeKind { get; }
    public bool HasOverlapWarning { get; }
    public bool IsEligible { get; }
    public bool IsPartial { get; }
    public string GeneratedFileName { get; }
    public string? PolicyWarning { get; }
}

public sealed record TrustedDeepScanRecoveryBatchRequest
{
    internal TrustedDeepScanRecoveryBatchRequest(
        Guid planId,
        Guid scanSessionId,
        DeepScanSourceFingerprint source,
        string destinationRoot,
        DeepScanRecoveryPolicy policy,
        IReadOnlyList<TrustedDeepScanCandidate> items,
        long totalKnownBytes)
    {
        PlanId = planId;
        ScanSessionId = scanSessionId;
        Source = source;
        DestinationRoot = destinationRoot;
        Policy = policy;
        Items = items;
        TotalKnownBytes = totalKnownBytes;
    }

    public Guid PlanId { get; }
    public Guid ScanSessionId { get; }
    public DeepScanSourceFingerprint Source { get; }
    public string DestinationRoot { get; }
    public DeepScanRecoveryPolicy Policy { get; }
    public IReadOnlyList<TrustedDeepScanCandidate> Items { get; }
    public long TotalKnownBytes { get; }
}

public sealed record DeepScanCandidateRevalidationResult(
    bool MatchesProvenance,
    CandidateValidationResult Validation,
    string? DiagnosticCode,
    string? Reason);

public interface IDeepScanCandidateRevalidator
{
    ValueTask<DeepScanCandidateRevalidationResult> RevalidateAsync(
        IReadOnlyRandomAccessSource source,
        TrustedDeepScanCandidate candidate,
        DeepScanRecoveryPolicy policy,
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken);
}

public interface ITrustedDeepScanRecoveryEngine
{
    Task<DeepScanRecoveryBatchResult> RecoverAsync(
        TrustedDeepScanRecoveryBatchRequest request,
        IProgress<DeepScanRecoveryProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IDeepScanRecoveryService
{
    Task<DeepScanRecoveryPlan> CreateRecoveryPlanAsync(
        DeepScanRecoveryRequest request,
        CancellationToken cancellationToken);

    Task<DeepScanRecoveryBatchResult> RecoverAsync(
        Guid planId,
        IProgress<DeepScanRecoveryProgress>? progress,
        CancellationToken cancellationToken);
}
