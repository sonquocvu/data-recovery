using System.Collections.Concurrent;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Application;

public sealed partial class ExFatImageScanService
{
    private readonly ConcurrentDictionary<Guid, StoredExFatPlan> _plans = new();

    public Task<ExFatRecoveryPlan> CreateRecoveryPlanAsync(ExFatRecoveryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Policy);
        ArgumentNullException.ThrowIfNull(request.CandidateIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Destination);
        request.Policy.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (recoveryEngine is null) throw new InvalidOperationException("No trusted exFAT recovery engine is configured.");
        if (!_sessions.TryGetValue(request.SessionId, out var state) || state.Disposal.IsCancellationRequested ||
            state.Session.Result.Outcome != ExFatScanOutcome.Completed || state.Session.ScannerVersion != ExFatScannerVersion.Phase8A)
            throw new InvalidOperationException("A completed, owned, undisposed Phase 8A session is required.");
        var catalog = state.Session.Result.Candidates.ToDictionary(c => c.CandidateId, StringComparer.Ordinal);
        var selected = new Dictionary<string, ExFatScanCandidate>(StringComparer.Ordinal);
        foreach (var id in request.CandidateIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(id) || !catalog.TryGetValue(id, out var candidate) || candidate.Provenance is null)
                throw new InvalidOperationException("The candidate ID is not owned by this trusted session.");
            selected.TryAdd(id, candidate);
            if (selected.Count > request.Policy.MaximumCandidates) throw new ArgumentOutOfRangeException(nameof(request), "Candidate budget exceeded; select fewer files.");
        }
        var ordered = selected.Values.OrderBy(c => c.Provenance!.PrimaryOffset).ThenBy(c => c.CandidateId, StringComparer.Ordinal).ToArray();
        var items = ordered.Select(c =>
        {
            var eligibility = ExFatRecoveryEligibility.Evaluate(c, request.Policy);
            return new ExFatRecoveryPlanItem(c.CandidateId, c.DisplayName, c.LogicalSize, c.ValidDataLength, c.NameEvidence, eligibility.Allowed, eligibility.Reason);
        }).ToArray();
        var output = items.Where(i => i.Eligible).Sum(i => i.LogicalBytes);
        var metadataBytes = state.Session.Result.Metrics.SourceBytesRead - state.Session.Result.Metrics.FingerprintBytesRead;
        var minimumReads = checked(state.Session.Source.Length * 2 + metadataBytes + items.Where(i => i.Eligible).Sum(i => i.InitializedBytes));
        var idPlan = Guid.NewGuid();
        var plan = new ExFatRecoveryPlan(idPlan, request.SessionId, request.Destination, Array.AsReadOnly(items), output, minimumReads);
        var trusted = new TrustedExFatRecoveryRequest(idPlan, state.Session, state.Budget, request.Destination, request.Policy, Array.AsReadOnly(ordered));
        cancellationToken.ThrowIfCancellationRequested();
        state.Disposal.Token.ThrowIfCancellationRequested();
        if (!_plans.TryAdd(idPlan, new(plan, trusted, state))) throw new InvalidOperationException("Plan identity collision.");
        if (state.Disposal.IsCancellationRequested) { _plans.TryRemove(idPlan, out _); throw new InvalidOperationException("Session disposed during planning."); }
        return Task.FromResult(plan);
    }

    public async Task<ExFatRecoveryBatchResult> RecoverAsync(Guid planId, IProgress<ExFatRecoveryProgress>? progress, CancellationToken cancellationToken)
    {
        if (recoveryEngine is null || !_plans.TryGetValue(planId, out var plan) || plan.State.Disposal.IsCancellationRequested ||
            !_sessions.ContainsKey(plan.Public.SessionId)) throw new InvalidOperationException("Unknown plan or disposed exFAT session.");
        if (Interlocked.Exchange(ref plan.Started, 1) != 0) throw new InvalidOperationException("Recovery plans are single-use; create a new plan to repeat a batch.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, plan.State.Disposal.Token);
        try
        {
            var result = await recoveryEngine.RecoverAsync(plan.Trusted, progress, linked.Token).ConfigureAwait(false);
            if (result.Outcome is ExFatRecoveryBatchOutcome.SourceChanged or ExFatRecoveryBatchOutcome.MetadataChanged) DisposeSession(plan.Public.SessionId);
            return result;
        }
        finally { _plans.TryRemove(planId, out _); }
    }

    private sealed class StoredExFatPlan(ExFatRecoveryPlan plan, TrustedExFatRecoveryRequest trusted, SessionState state)
    {
        internal ExFatRecoveryPlan Public { get; } = plan;
        internal TrustedExFatRecoveryRequest Trusted { get; } = trusted;
        internal SessionState State { get; } = state;
        internal int Started;
    }
}
