using System.Collections.Concurrent;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Application;

public sealed class ImageDeepScanService(
    IReadOnlyImageSourceFactory sourceFactory,
    IDeepScanEngine engine,
    IDeepScanSourceMetadataProvider metadataProvider,
    ITrustedDeepScanRecoveryEngine? recoveryEngine = null) : IImageDeepScanService, IDeepScanRecoveryService
{
    private readonly ConcurrentDictionary<Guid, StoredSession> _sessions = [];
    private readonly ConcurrentDictionary<Guid, StoredPlan> _plans = [];

    public async Task<DeepScanSession> ScanAsync(
        string imagePath,
        DeepScanRequest request,
        IProgress<DeepScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(request);
        request.EffectiveBudget.Validate();
        var before = await metadataProvider.CaptureAsync(imagePath, cancellationToken).ConfigureAwait(false);
        await using var source = await sourceFactory.OpenAsync(imagePath, cancellationToken).ConfigureAwait(false);
        var result = await engine.ScanAsync(source, request, progress, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var after = await metadataProvider.CaptureAsync(imagePath, cancellationToken).ConfigureAwait(false);
        if (!Matches(before, after))
        {
            throw new IOException("SOURCE_CHANGED: The image length, timestamp, or SHA-256 fingerprint changed during Deep Scan.");
        }

        var session = new DeepScanSession(Guid.NewGuid(), before, result);
        if (!_sessions.TryAdd(session.SessionId, new(session, CaptureCandidates(session))))
        {
            throw new InvalidOperationException("A unique Deep Scan session could not be created.");
        }

        return session;
    }

    public async Task<DeepScanSession> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessions.TryGetValue(sessionId, out var stored) || stored.IsDisposed)
        {
            throw new InvalidOperationException("The Deep Scan session is unknown or disposed.");
        }

        var current = await metadataProvider.CaptureAsync(stored.Session.Source.CanonicalPath, cancellationToken).ConfigureAwait(false);
        if (!Matches(stored.Session.Source, current))
        {
            DisposeSession(sessionId);
            throw new IOException("SOURCE_CHANGED: The Deep Scan session is stale because its source changed.");
        }

        return stored.Session;
    }

    public async Task<DeepScanRecoveryPlan> CreateRecoveryPlanAsync(
        DeepScanRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationRoot);
        var policy = request.EffectivePolicy;
        policy.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessions.TryGetValue(request.ScanSessionId, out var session) || session.IsDisposed)
        {
            throw new InvalidOperationException("The trusted Deep Scan session is unknown or disposed.");
        }

        if (session.Session.Result.Outcome != DeepScanOutcome.Completed)
        {
            throw new InvalidOperationException("Only a completed production Deep Scan can authorize carving recovery.");
        }

        var current = await metadataProvider.CaptureAsync(session.Session.Source.CanonicalPath, cancellationToken).ConfigureAwait(false);
        if (!Matches(session.Session.Source, current))
        {
            DisposeSession(request.ScanSessionId);
            throw new IOException("SOURCE_CHANGED: The Deep Scan session became stale before recovery planning.");
        }

        var requestedIds = request.CandidateIds ?? throw new ArgumentNullException(nameof(request.CandidateIds));
        var uniqueIds = new HashSet<string>(StringComparer.Ordinal);
        var selected = new List<CandidateState>();
        foreach (var candidateId in requestedIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(candidateId)) throw new InvalidOperationException("Candidate IDs must be opaque non-empty scan results.");
            if (!uniqueIds.Add(candidateId)) continue;
            if (!session.Candidates.TryGetValue(candidateId, out var candidate))
            {
                throw new InvalidOperationException("A selected candidate does not belong to this trusted Deep Scan session.");
            }

            selected.Add(candidate);
        }

        if (selected.Count > policy.MaximumCandidatesPerBatch)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The selected candidate count exceeds the recovery budget.");
        }

        var publicItems = new List<DeepScanRecoveryPlanItem>(selected.Count);
        var trustedItems = new List<TrustedDeepScanCandidate>(selected.Count);
        var eligibleBytes = 0L;
        foreach (var state in selected.OrderBy(item => item.CandidateOffset).ThenBy(item => item.CandidateId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var evaluation = Evaluate(state, policy);
            var candidateLength = state.CandidateLength ?? 0;
            var totalByteLimit = Math.Min(policy.MaximumTotalBytes, policy.MaximumPostWriteVerificationBytes);
            if (evaluation.Eligible && (candidateLength > policy.MaximumBytesPerCandidate || candidateLength > totalByteLimit || eligibleBytes > totalByteLimit - candidateLength))
            {
                evaluation = (false, false, "The candidate exceeds the configured recovery byte budget.");
            }

            if (evaluation.Eligible) eligibleBytes = checked(eligibleBytes + candidateLength);
            var partialMarker = evaluation.Partial ? ".partial" : string.Empty;
            var generatedName = $"Carved_{state.FormatId}_{state.CandidateOffset:X16}{partialMarker}{state.SuggestedExtension}";
            publicItems.Add(new(state.CandidateId, state.FormatId, generatedName, candidateLength, evaluation.Eligible, evaluation.Partial, evaluation.Warning));
            trustedItems.Add(new(
                session.Session.SessionId,
                state.CandidateId,
                session.Session.Source,
                state.SourceRangeIdentity,
                state.SourceRangeOffset,
                state.SourceRangeLength,
                state.CandidateOffset,
                candidateLength,
                state.LengthConfidence,
                state.FormatId,
                state.SuggestedExtension,
                state.ValidatorVersion,
                state.ValidationConfidence,
                state.Completeness,
                state.ValidationState,
                state.DetectionEvidenceFingerprint,
                state.RangeKind,
                state.HasOverlapWarning,
                evaluation.Eligible,
                evaluation.Partial,
                generatedName,
                evaluation.Warning));
        }

        var planId = Guid.NewGuid();
        var plan = new DeepScanRecoveryPlan(planId, request.ScanSessionId, request.DestinationRoot, publicItems.ToArray(), eligibleBytes);
        var trusted = new TrustedDeepScanRecoveryBatchRequest(
            planId,
            request.ScanSessionId,
            session.Session.Source,
            request.DestinationRoot,
            policy,
            trustedItems.ToArray(),
            eligibleBytes);
        if (!_plans.TryAdd(planId, new(plan, trusted, session)))
        {
            throw new InvalidOperationException("A unique Deep Scan recovery plan could not be created.");
        }

        return plan;
    }

    public async Task<DeepScanRecoveryBatchResult> RecoverAsync(
        Guid planId,
        IProgress<DeepScanRecoveryProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (recoveryEngine is null) throw new InvalidOperationException("No trusted Deep Scan recovery engine is configured.");
        if (!_plans.TryGetValue(planId, out var plan) || plan.Session.IsDisposed || !_sessions.ContainsKey(plan.Session.Session.SessionId))
        {
            throw new InvalidOperationException("The recovery plan is unknown or its trusted Deep Scan session was disposed.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, plan.Session.Disposal.Token);
        return await recoveryEngine.RecoverAsync(plan.TrustedRequest, progress, linked.Token).ConfigureAwait(false);
    }

    public bool DisposeSession(Guid sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var stored)) return false;
        stored.Dispose();
        foreach (var plan in _plans.Where(item => item.Value.Session.Session.SessionId == sessionId).ToArray())
        {
            _plans.TryRemove(plan.Key, out _);
        }

        return true;
    }

    private static IReadOnlyDictionary<string, CandidateState> CaptureCandidates(DeepScanSession session)
    {
        var ranges = (session.Result.ScannedRanges ?? []).ToDictionary(item => item.Identity, StringComparer.Ordinal);
        var candidates = new Dictionary<string, CandidateState>(StringComparer.Ordinal);
        foreach (var candidate in session.Result.Candidates)
        {
            if (!ranges.TryGetValue(candidate.SourceRangeIdentity, out var range)) continue;
            var length = candidate.LogicalLength;
            if (length < 0) continue;
            if (length is not null)
            {
                long end;
                try
                {
                    end = checked(candidate.StartOffset + length.Value);
                }
                catch (OverflowException)
                {
                    continue;
                }

                if (candidate.StartOffset < range.Offset || end > range.End) continue;
            }
            candidates[candidate.CandidateId] = new(
                candidate.CandidateId,
                candidate.FormatId,
                candidate.SuggestedExtension,
                candidate.StartOffset,
                length,
                candidate.LengthConfidence,
                candidate.ValidationConfidence,
                candidate.Completeness,
                candidate.ValidationState,
                candidate.ValidatorVersion,
                candidate.StructuralEvidenceFingerprint,
                candidate.SourceRangeIdentity,
                range.Offset,
                range.Length,
                candidate.RangeKind,
                candidate.HasOverlapWarning,
                candidate.IsRecoverySupported);
        }

        return candidates;
    }

    private static (bool Eligible, bool Partial, string? Warning) Evaluate(CandidateState candidate, DeepScanRecoveryPolicy policy)
    {
        if (candidate.HasOverlapWarning && !policy.AllowOverlappingCandidates)
        {
            return (false, false, "The candidate has an unresolved overlap and requires explicit overlap opt-in.");
        }

        if (candidate.CandidateLength is not null && candidate.IsRecoverySupported && candidate.ValidationConfidence == DeepScanConfidence.High &&
            candidate.Completeness == DeepScanCompleteness.Complete && candidate.ValidationState == DeepScanValidationState.StructurallyValidated &&
            candidate.LengthConfidence == DeepScanConfidence.High)
        {
            return (true, false, candidate.HasOverlapWarning ? "The candidate overlaps another result; it is being carved independently by explicit policy." : null);
        }

        if (candidate.CandidateLength is not null && policy.AllowMediumTruncated && candidate.ValidationConfidence == DeepScanConfidence.Medium &&
            candidate.Completeness == DeepScanCompleteness.Truncated && candidate.ValidationState == DeepScanValidationState.StructurallyPlausible &&
            candidate.LengthConfidence == DeepScanConfidence.Medium)
        {
            return (true, true, "The candidate is truncated; copied bytes cannot be described as verified-complete.");
        }

        return (false, false, "Default carving requires a High-confidence, Complete, structurally validated candidate with an exact trusted length.");
    }

    private static bool Matches(DeepScanSourceFingerprint left, DeepScanSourceFingerprint right) =>
        string.Equals(left.CanonicalPath, right.CanonicalPath, StringComparison.OrdinalIgnoreCase) &&
        left.Length == right.Length &&
        left.LastWriteTimeUtcTicks == right.LastWriteTimeUtcTicks &&
        string.Equals(left.Sha256, right.Sha256, StringComparison.Ordinal);

    private sealed class StoredSession(DeepScanSession session, IReadOnlyDictionary<string, CandidateState> candidates)
    {
        private int _disposed;
        public DeepScanSession Session { get; } = session;
        public IReadOnlyDictionary<string, CandidateState> Candidates { get; } = candidates;
        public CancellationTokenSource Disposal { get; } = new();
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Disposal.Cancel();
        }
    }

    private sealed record StoredPlan(DeepScanRecoveryPlan PublicPlan, TrustedDeepScanRecoveryBatchRequest TrustedRequest, StoredSession Session);

    private sealed record CandidateState(
        string CandidateId,
        string FormatId,
        string SuggestedExtension,
        long CandidateOffset,
        long? CandidateLength,
        DeepScanConfidence LengthConfidence,
        DeepScanConfidence ValidationConfidence,
        DeepScanCompleteness Completeness,
        DeepScanValidationState ValidationState,
        string ValidatorVersion,
        string DetectionEvidenceFingerprint,
        string SourceRangeIdentity,
        long SourceRangeOffset,
        long SourceRangeLength,
        DeepScanRangeKind RangeKind,
        bool HasOverlapWarning,
        bool IsRecoverySupported);
}
