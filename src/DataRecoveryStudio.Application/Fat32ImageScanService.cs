using System.Collections.Concurrent;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Application;

public sealed class Fat32ImageScanService(
    IReadOnlyImageSourceFactory sourceFactory,
    IFat32MetadataScanner scanner,
    IFat32SourceMetadataProvider metadataProvider,
    ITrustedFat32RecoveryEngine? recoveryEngine = null,
    IFat32RecoveryPlanResolver? planResolver = null) : IFat32ImageScanService, IFat32ImageRecoveryService
{
    private readonly ConcurrentDictionary<Guid, SessionState> _sessions = [];
    private readonly ConcurrentDictionary<Guid, StoredPlan> _plans = [];

    public async Task<Fat32ScanSession> ScanAsync(
        string imagePath,
        Fat32ScanRequest request,
        IProgress<Fat32ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(request);
        request.EffectiveBudget.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var before = await metadataProvider.CaptureAsync(imagePath, cancellationToken).ConfigureAwait(false);
        var sessionId = Guid.NewGuid();
        var boundRequest = request with { ScanSessionId = sessionId, SourceIdentity = before.Sha256 };
        await using var source = await sourceFactory.OpenAsync(before.CanonicalPath, cancellationToken).ConfigureAwait(false);
        var result = await scanner.ScanAsync(source, boundRequest, progress, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var after = await metadataProvider.CaptureAsync(before.CanonicalPath, cancellationToken).ConfigureAwait(false);
        if (!Matches(before, after))
        {
            throw new IOException("SOURCE_CHANGED: The image length, timestamp, or SHA-256 fingerprint changed during the FAT32 metadata scan.");
        }

        if (result.Outcome == Fat32ScanOutcome.Canceled)
        {
            throw new OperationCanceledException("Cancellation won before FAT32 scan-session publication.", cancellationToken);
        }

        if (result.Geometry is null)
        {
            throw new InvalidDataException("A trusted FAT32 scan session requires validated FAT32 geometry.");
        }

        var session = new Fat32ScanSession(sessionId, before, request.Volume.VolumeOffset, result.Geometry, Fat32ScannerVersions.MetadataPhase7A, result);
        if (!_sessions.TryAdd(sessionId, new(session, request.EffectiveBudget))) throw new InvalidOperationException("A unique FAT32 scan session could not be created.");
        return session;
    }

    public async Task<Fat32ScanSession> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessions.TryGetValue(sessionId, out var state) || state.IsDisposed) throw new InvalidOperationException("The FAT32 scan session is unknown or disposed.");
        var current = await metadataProvider.CaptureAsync(state.Session.Source.CanonicalPath, cancellationToken).ConfigureAwait(false);
        if (!Matches(state.Session.Source, current))
        {
            DisposeSession(sessionId);
            throw new IOException("SOURCE_CHANGED: The FAT32 scan session is stale because its source changed.");
        }

        return state.Session;
    }

    public async Task<Fat32RecoveryPlan> CreateRecoveryPlanAsync(Fat32RecoveryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationRoot);
        if (recoveryEngine is null || planResolver is null) throw new InvalidOperationException("No trusted FAT32 recovery components are configured.");
        var policy = request.EffectivePolicy;
        policy.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessions.TryGetValue(request.ScanSessionId, out var state) || state.IsDisposed)
            throw new InvalidOperationException("The trusted FAT32 scan session is unknown or disposed.");
        if (state.Session.Result.Outcome != Fat32ScanOutcome.Completed)
            throw new InvalidOperationException("Only a completed production FAT32 scan can authorize recovery.");
        var current = await metadataProvider.CaptureAsync(state.Session.Source.CanonicalPath, cancellationToken).ConfigureAwait(false);
        if (!Matches(state.Session.Source, current))
        {
            DisposeSession(request.ScanSessionId);
            throw new IOException("SOURCE_CHANGED: The FAT32 scan session became stale before recovery planning.");
        }

        var requested = request.CandidateIds ?? throw new ArgumentNullException(nameof(request.CandidateIds));
        var unique = new HashSet<string>(StringComparer.Ordinal);
        var selected = new List<Fat32DeletedCandidate>();
        foreach (var id in requested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("Candidate IDs must be opaque non-empty scan results.");
            if (!unique.Add(id)) continue;
            if (!state.Candidates.TryGetValue(id, out var candidate) || candidate.ScanSessionId != request.ScanSessionId)
                throw new InvalidOperationException("A selected candidate does not belong to this trusted FAT32 scan session.");
            selected.Add(candidate);
        }
        if (selected.Count > policy.MaximumCandidates) throw new ArgumentOutOfRangeException(nameof(request), "The selected candidate count exceeds the FAT32 recovery budget.");

        var publicItems = new List<Fat32RecoveryPlanItem>(selected.Count);
        var trustedItems = new List<TrustedFat32RecoveryCandidate>(selected.Count);
        var total = 0L;
        foreach (var candidate in selected.OrderBy(item => item.Provenance!.DirectorySlotSourceOffset).ThenBy(item => item.CandidateId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provenance = candidate.Provenance ?? throw new InvalidOperationException("The candidate lacks trusted Phase 7A slot provenance.");
            var evaluation = Evaluate(candidate, policy);
            if (evaluation.Allowed && (candidate.LogicalFileSize > policy.MaximumLogicalBytesPerCandidate || total > policy.MaximumTotalRecoveredBytes - candidate.LogicalFileSize))
                evaluation = (false, "The candidate exceeds the configured FAT32 recovery byte budget.");
            if (evaluation.Allowed) total = checked(total + candidate.LogicalFileSize);
            var outputName = planResolver.ResolveOutputName(candidate, provenance.ParentDirectoryCluster, provenance.DirectorySlotSourceOffset);
            publicItems.Add(new(candidate.CandidateId, outputName, candidate.NameState, candidate.LogicalFileSize, candidate.Allocation, evaluation.Allowed, evaluation.Warning));
            trustedItems.Add(new(
                state.Session.SessionId, state.Session.Source, state.Session.VolumeOffset, state.Session.Geometry,
                state.Session.ScannerVersion, candidate, provenance.ParentDirectoryCluster, provenance.DirectorySlotSourceOffset,
                provenance.EvidenceFingerprint, outputName, evaluation.Allowed, evaluation.Warning));
        }

        var planId = Guid.NewGuid();
        var plan = new Fat32RecoveryPlan(planId, request.ScanSessionId, request.DestinationRoot, publicItems.ToArray(), total);
        var trusted = new TrustedFat32RecoveryBatchRequest(planId, request.ScanSessionId, state.Session.Source,
            state.Session.VolumeOffset, state.Session.Geometry, state.ScanBudget, request.DestinationRoot, policy, trustedItems.ToArray(), total);
        if (!_plans.TryAdd(planId, new(plan, trusted, state))) throw new InvalidOperationException("A unique FAT32 recovery plan could not be created.");
        return plan;
    }

    public async Task<Fat32RecoveryBatchResult> RecoverAsync(Guid planId, IProgress<Fat32RecoveryProgress>? progress, CancellationToken cancellationToken)
    {
        if (recoveryEngine is null) throw new InvalidOperationException("No trusted FAT32 recovery engine is configured.");
        if (!_plans.TryGetValue(planId, out var plan) || plan.Session.IsDisposed || !_sessions.ContainsKey(plan.Session.Session.SessionId))
            throw new InvalidOperationException("The recovery plan is unknown or its trusted FAT32 session was disposed.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, plan.Session.Disposal.Token);
        return await recoveryEngine.RecoverAsync(plan.Trusted, progress, linked.Token).ConfigureAwait(false);
    }

    public bool DisposeSession(Guid sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var state)) return false;
        state.Dispose();
        foreach (var plan in _plans.Where(item => item.Value.Session.Session.SessionId == sessionId).ToArray()) _plans.TryRemove(plan.Key, out _);
        return true;
    }

    private static (bool Allowed, string? Warning) Evaluate(Fat32DeletedCandidate candidate, Fat32RecoveryPolicy policy)
    {
        if (candidate.Kind != Fat32CandidateKind.File) return (false, "Deleted directories are metadata-only and are not traversed or recovered in Phase 7B.");
        return candidate.Allocation switch
        {
            Fat32AllocationAssessment.ZeroLength => (true, candidate.NameState == Fat32NameState.VerifiedLongName ? null : "The original filename is uncertain."),
            Fat32AllocationAssessment.PossiblyRecoverableContiguous => (true, "FAT32 deletion often clears chain metadata; contiguous recovery is a heuristic and copy verification does not prove original content integrity."),
            Fat32AllocationAssessment.PreservedAllocatedChain when policy.AllowPreservedFatChain => (true, "Preserved FAT-chain extraction was explicitly enabled; allocation and copy verification do not prove the chain still belongs to this entry."),
            Fat32AllocationAssessment.PartiallyOverwrittenOrReused or Fat32AllocationAssessment.OverwrittenOrReused when policy.AllowDeterministicDamagedContent => (true, "Damaged-content extraction was explicitly enabled; content may be overwritten or unrelated."),
            Fat32AllocationAssessment.PreservedAllocatedChain => (false, "Preserved FAT chains require explicit policy opt-in."),
            Fat32AllocationAssessment.PartiallyOverwrittenOrReused or Fat32AllocationAssessment.OverwrittenOrReused => (false, "Mixed or reallocated spans require explicit damaged-content policy."),
            _ => (false, "The candidate is blocked by conservative FAT32 recovery policy."),
        };
    }

    private static bool Matches(Fat32SourceFingerprint left, Fat32SourceFingerprint right) =>
        string.Equals(left.CanonicalPath, right.CanonicalPath, StringComparison.OrdinalIgnoreCase) &&
        left.Length == right.Length && left.LastWriteTimeUtcTicks == right.LastWriteTimeUtcTicks &&
        string.Equals(left.Sha256, right.Sha256, StringComparison.Ordinal);

    private sealed class SessionState
    {
        private int _disposed;
        public SessionState(Fat32ScanSession session, Fat32ScanBudget scanBudget)
        {
            Session = session;
            ScanBudget = scanBudget;
            Candidates = session.Result.Candidates.Where(item => item.Provenance is not null).ToDictionary(item => item.CandidateId, StringComparer.Ordinal);
        }
        public Fat32ScanSession Session { get; }
        public Fat32ScanBudget ScanBudget { get; }
        public IReadOnlyDictionary<string, Fat32DeletedCandidate> Candidates { get; }
        public CancellationTokenSource Disposal { get; } = new();
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) Disposal.Cancel(); }
    }

    private sealed record StoredPlan(Fat32RecoveryPlan Public, TrustedFat32RecoveryBatchRequest Trusted, SessionState Session);
}
