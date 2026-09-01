namespace DataRecoveryStudio.Core;

public readonly record struct RecoveryCandidateId(Guid Value)
{
    public override string ToString() => Value.ToString("N");
}

public enum RecoveryOutputLayout
{
    Flat,
    OriginalFoldersWhenSafe,
}

public sealed record RecoveryPolicy(
    bool AllowPartiallyOverwritten = false,
    bool AllowOverwritten = false,
    bool AllowUnknown = false,
    bool CreateDestinationIfMissing = false,
    RecoveryOutputLayout OutputLayout = RecoveryOutputLayout.Flat,
    int BufferSize = 128 * 1024,
    int MaximumCollisionAttempts = 1_000,
    int MaximumDiagnostics = 1_000)
{
    public void Validate()
    {
        if (BufferSize is < 4_096 or > 4 * 1024 * 1024 || MaximumCollisionAttempts <= 0 || MaximumDiagnostics <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(RecoveryPolicy), "Recovery safety limits are outside their supported bounds.");
        }
    }
}

public sealed record RecoveryCandidateReference(
    RecoveryCandidateId Id,
    DeletedFileCandidate Candidate);

public sealed record RecoveryScanSession(
    Guid SessionId,
    StandardScanResult ScanResult,
    IReadOnlyList<RecoveryCandidateReference> Candidates);

public sealed record ImageRecoveryRequest(
    Guid ScanSessionId,
    IReadOnlyList<RecoveryCandidateId> CandidateIds,
    string DestinationRoot,
    RecoveryPolicy? Policy = null)
{
    public RecoveryPolicy EffectivePolicy => Policy ?? new RecoveryPolicy();
}

public sealed record RecoveryPlanItem(
    RecoveryCandidateId CandidateId,
    long MftRecordNumber,
    string DisplayName,
    long ExpectedBytes,
    CandidateRecoverability Recoverability,
    bool IsAllowed,
    string? Warning);

public sealed record RecoveryPlan(
    Guid PlanId,
    Guid ScanSessionId,
    string DestinationRoot,
    RecoveryOutputLayout OutputLayout,
    IReadOnlyList<RecoveryPlanItem> Items,
    long TotalKnownBytes);

public enum RecoveryFileOutcomeCode
{
    RecoveredAndVerified,
    RecoveredWithAllocationWarning,
    SkippedByPolicy,
    UnsupportedLayout,
    DamagedMetadata,
    SourceChanged,
    SourceReadFailed,
    DestinationFailed,
    VerificationFailed,
    Canceled,
    CleanupFailed,
}

public enum RecoveryBatchOutcome
{
    Completed,
    CompletedWithFailures,
    Canceled,
    FatalSourceFailure,
    DestinationUnavailable,
}

public enum RecoveryVerificationState
{
    NotPerformed,
    Verified,
    Failed,
}

public sealed record RecoveryDiagnostic(
    string Code,
    string Operation,
    string Reason,
    long? MftRecordNumber = null,
    string? SanitizedOutputName = null);

public sealed record RecoveryFileOutcome(
    RecoveryCandidateId CandidateId,
    long MftRecordNumber,
    RecoveryFileOutcomeCode Outcome,
    string? OutputPath,
    long BytesWritten,
    string? Sha256,
    RecoveryVerificationState Verification,
    string Message,
    IReadOnlyList<RecoveryDiagnostic> Diagnostics);

public sealed record RecoveryBatchResult(
    RecoveryBatchOutcome Outcome,
    IReadOnlyList<RecoveryFileOutcome> Files,
    int CompletedCount,
    int TotalCount,
    long BytesWritten,
    IReadOnlyList<RecoveryDiagnostic> Diagnostics);

public sealed record RecoveryProgress(
    RecoveryCandidateId? CurrentCandidateId,
    string? CurrentFile,
    int CompletedCount,
    int TotalCount,
    long CurrentBytes,
    long TotalKnownBytes);

public sealed record RecoverySourceProvenance(
    string CanonicalPath,
    long SourceLength,
    long LastWriteTimeUtcTicks,
    long VolumeOffset,
    long ValidatedVolumeLength,
    string BootGeometryFingerprint);

public sealed record RecoverySourceMetadata(
    string CanonicalPath,
    long Length,
    long LastWriteTimeUtcTicks,
    string BootGeometryFingerprint);

public sealed record TrustedRecoveryItem(
    RecoveryCandidateId CandidateId,
    long MftRecordNumber,
    ushort SequenceNumber,
    string? StreamName,
    string AttributeIdentity,
    string CandidateMetadataFingerprint,
    CandidateRecoverability Recoverability,
    long LogicalSize,
    string OriginalName,
    string OriginalPath,
    bool IsAllowed,
    string? PolicyWarning);

public sealed record TrustedRecoveryBatchRequest(
    Guid ScanSessionId,
    RecoverySourceProvenance Source,
    StandardScanBudgets ScanBudgets,
    string DestinationRoot,
    RecoveryPolicy Policy,
    IReadOnlyList<TrustedRecoveryItem> Items,
    long TotalKnownBytes);

public interface IImageRecoveryService
{
    Task<RecoveryScanSession> ScanAsync(
        string imagePath,
        StandardScanRequest request,
        IProgress<StandardScanProgress>? progress,
        CancellationToken cancellationToken);

    Task<RecoveryPlan> CreatePlanAsync(ImageRecoveryRequest request, CancellationToken cancellationToken);

    Task<RecoveryBatchResult> RecoverAsync(
        Guid planId,
        IProgress<RecoveryProgress>? progress,
        CancellationToken cancellationToken);

    bool DisposeSession(Guid sessionId);
}

public interface IRecoverySourceMetadataProvider
{
    ValueTask<RecoverySourceMetadata> CaptureAsync(string imagePath, long volumeOffset, CancellationToken cancellationToken);
}

public interface ITrustedNtfsRecoveryEngine
{
    Task<RecoveryBatchResult> RecoverAsync(
        TrustedRecoveryBatchRequest request,
        IProgress<RecoveryProgress>? progress,
        CancellationToken cancellationToken);
}
