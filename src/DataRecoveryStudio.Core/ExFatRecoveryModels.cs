namespace DataRecoveryStudio.Core;

public sealed record ExFatRecoveryPolicy(
    bool AllowPreservedFatChain = false, bool CreateDestinationIfMissing = false,
    int MaximumCandidates = 1000, long MaximumOutputBytes = 4L * 1024 * 1024 * 1024,
    long MaximumFileBytes = 2L * 1024 * 1024 * 1024,
    long MaximumSourceBytes = 8L * 1024 * 1024 * 1024,
    int MaximumTotalClusters = 1_000_000, int MaximumChainLength = 262_144,
    int BufferSize = 128 * 1024, int MaximumCollisionAttempts = 1000,
    int MaximumCleanupAttempts = 3, int MaximumDiagnostics = 1000,
    int MaximumProgressCallbacks = 1000, TimeSpan? MaximumDuration = null)
{
    public TimeSpan EffectiveDuration => MaximumDuration ?? TimeSpan.FromHours(4);
    public void Validate()
    {
        var hard = new ExFatRecoveryPolicy();
        foreach (var property in typeof(ExFatRecoveryPolicy).GetProperties())
        {
            if (property.PropertyType != typeof(int) && property.PropertyType != typeof(long)) continue;
            var value = Convert.ToInt64(property.GetValue(this), System.Globalization.CultureInfo.InvariantCulture);
            if (value < 0 || value > Convert.ToInt64(property.GetValue(hard), System.Globalization.CultureInfo.InvariantCulture))
                throw new ArgumentOutOfRangeException(property.Name);
        }
        if (BufferSize < 4096 || MaximumCleanupAttempts < 1 || MaximumCollisionAttempts < 1 ||
            MaximumDiagnostics < 1 || MaximumProgressCallbacks < 1 || EffectiveDuration <= TimeSpan.Zero || EffectiveDuration > hard.EffectiveDuration)
            throw new ArgumentOutOfRangeException(nameof(ExFatRecoveryPolicy));
    }
}

public sealed record ExFatRecoveryRequest(Guid SessionId, IReadOnlyList<string> CandidateIds,
    string Destination, ExFatRecoveryPolicy Policy);
public sealed record ExFatRecoveryPlanItem(string CandidateId, string DisplayName, long LogicalBytes,
    long InitializedBytes, ExFatNameEvidence NameEvidence, bool Eligible, string Reason);
public sealed record ExFatRecoveryPlan(Guid PlanId, Guid SessionId, string Destination,
    IReadOnlyList<ExFatRecoveryPlanItem> Items, long OutputBytes, long MinimumSourceBytes);
public enum ExFatRecoveryPhase { ValidatingSource, RevalidatingMetadata, Recovering, ValidatingSourceAfterCopy, VerifyingOutput, Cleanup, Completed }
public enum ExFatRecoveryOutcome { ReconstructedCopyVerified, Blocked, SourceReadFailed, DestinationFailed, VerificationFailed, Canceled, SourceChanged, NotAttempted, CleanupFailed }
public enum ExFatRecoveryBatchOutcome { Completed, CompletedWithFailures, Canceled, SourceChanged, MetadataChanged, DestinationUnavailable, BudgetExceeded, SourceReadFailed }
public sealed record ExFatRecoveryDiagnostic(string Code, string Reason, string? CandidateId = null, string? OwnedPath = null);
public sealed record ExFatRecoveryFileResult(string CandidateId, ExFatRecoveryOutcome Outcome,
    string? OutputPath, ExFatNameEvidence NameEvidence, bool UsedFallbackName, long LogicalBytes,
    long CopiedBytes, long SynthesizedZeroBytes, string? Sha256, RecoveryVerificationState Verification,
    IReadOnlyList<ExFatRecoveryDiagnostic> Diagnostics);
public sealed record ExFatRecoveryProgress(ExFatRecoveryPhase Phase, string? CandidateId, int CompletedItems,
    int TotalItems, long SourceBytes, long FingerprintBytes, long MetadataBytes, long PayloadBytes,
    long CopiedBytes, long SynthesizedZeroBytes, long OutputVerificationBytes, long ChargedSourceBytes, TimeSpan Elapsed, bool IsTerminal);
public sealed record ExFatRecoveryBatchResult(ExFatRecoveryBatchOutcome Outcome,
    IReadOnlyList<ExFatRecoveryFileResult> Files, IReadOnlyList<ExFatRecoveryDiagnostic> Diagnostics,
    ExFatRecoveryProgress Metrics);

public interface IExFatImageRecoveryService
{
    Task<ExFatRecoveryPlan> CreateRecoveryPlanAsync(ExFatRecoveryRequest request, CancellationToken cancellationToken);
    Task<ExFatRecoveryBatchResult> RecoverAsync(Guid planId, IProgress<ExFatRecoveryProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>Only Application can construct an authorization from its private Phase 8A registry.</summary>
public sealed class TrustedExFatRecoveryRequest
{
    internal TrustedExFatRecoveryRequest(Guid planId, ExFatScanSession session, ExFatScanBudget scanBudget,
        string destination, ExFatRecoveryPolicy policy, IReadOnlyList<ExFatScanCandidate> candidates)
    { PlanId = planId; Session = session; ScanBudget = scanBudget; Destination = destination; Policy = policy; Candidates = candidates; }
    public Guid PlanId { get; }
    public ExFatScanSession Session { get; }
    public ExFatScanBudget ScanBudget { get; }
    public string Destination { get; }
    public ExFatRecoveryPolicy Policy { get; }
    public IReadOnlyList<ExFatScanCandidate> Candidates { get; }
}
public interface ITrustedExFatRecoveryEngine
{
    Task<ExFatRecoveryBatchResult> RecoverAsync(TrustedExFatRecoveryRequest request,
        IProgress<ExFatRecoveryProgress>? progress, CancellationToken cancellationToken);
}

internal static class ExFatRecoveryEligibility
{
    internal static (bool Allowed, string Reason) Evaluate(ExFatScanCandidate candidate, ExFatRecoveryPolicy policy)
    {
        var p = candidate.Provenance;
        if (p is null || candidate.IsPartial || (candidate.Attributes & 16) != 0 ||
            !(p.OrdinaryChecksumValid || p.RecoveredChecksumValid) ||
            p.DataLength < 0 || p.ValidDataLength < 0 || p.ValidDataLength > p.DataLength ||
            p.DataLength != candidate.LogicalSize || p.ValidDataLength != candidate.ValidDataLength)
            return (false, "Extraction metadata is not trustworthy.");
        return candidate.AllocationEvidence switch
        {
            ExFatAllocationEvidence.ZeroLength when p.DataLength == 0 && p.FirstCluster == 0 => (true, "Structurally valid empty logical stream."),
            ExFatAllocationEvidence.ContiguousAllFree when (p.Flags & 2) != 0 => (true, "Exact contiguous layout; all required clusters free and ownership complete."),
            ExFatAllocationEvidence.PreservedChainAllFree when policy.AllowPreservedFatChain && (p.Flags & 2) == 0 => (true, "Preserved FAT chain explicitly enabled; copy fidelity cannot prove original content integrity."),
            ExFatAllocationEvidence.PreservedChainAllFree => (false, "Preserved FAT chains require a separate explicit opt-in."),
            _ => (false, "Allocation or ownership evidence blocks extraction: " + candidate.AllocationEvidence),
        };
    }
}
