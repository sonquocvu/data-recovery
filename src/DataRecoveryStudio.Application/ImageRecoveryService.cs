using System.Collections.Concurrent;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Application;

public sealed class ImageRecoveryService(
    StandardImageScanService scanService,
    IRecoverySourceMetadataProvider metadataProvider,
    ITrustedNtfsRecoveryEngine recoveryEngine) : IImageRecoveryService
{
    private readonly ConcurrentDictionary<Guid, SessionState> _sessions = [];
    private readonly ConcurrentDictionary<Guid, StoredPlan> _plans = [];

    public async Task<RecoveryScanSession> ScanAsync(
        string imagePath,
        StandardScanRequest request,
        IProgress<StandardScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(request);
        request.EffectiveBudgets.Validate();
        var before = await metadataProvider.CaptureAsync(imagePath, request.Volume.VolumeOffset, cancellationToken).ConfigureAwait(false);
        var result = await scanService.ScanAsync(imagePath, request, progress, cancellationToken).ConfigureAwait(false);
        var after = await metadataProvider.CaptureAsync(imagePath, request.Volume.VolumeOffset, cancellationToken).ConfigureAwait(false);
        if (!SourceMetadataMatches(before, after))
        {
            throw new IOException("The image source changed while the recovery scan session was being created.");
        }

        if (result.Outcome != StandardScanOutcome.Completed || result.Geometry is null || result.MftLayout is null)
        {
            throw new InvalidOperationException("Only a completed production NTFS image scan can create a trusted recovery session.");
        }

        var expectedGeometryFingerprint = RecoveryFingerprint.ComputeGeometry(result.Geometry);
        if (!string.Equals(expectedGeometryFingerprint, before.BootGeometryFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The boot geometry observed by the scanner does not match the source provenance.");
        }

        var sessionId = Guid.NewGuid();
        var references = result.Candidates
            .OrderBy(candidate => candidate.MftRecordNumber)
            .Select(candidate => new RecoveryCandidateReference(new RecoveryCandidateId(Guid.NewGuid()), candidate))
            .ToArray();
        var volumeLength = checked((long)result.Geometry.TotalSectors * result.Geometry.BytesPerSector);
        var provenance = new RecoverySourceProvenance(
            before.CanonicalPath,
            before.Length,
            before.LastWriteTimeUtcTicks,
            request.Volume.VolumeOffset,
            volumeLength,
            before.BootGeometryFingerprint);
        var state = new SessionState(sessionId, result, references, provenance, request.EffectiveBudgets);
        if (!_sessions.TryAdd(sessionId, state))
        {
            throw new InvalidOperationException("A unique recovery session could not be created.");
        }

        return new(sessionId, result, references);
    }

    public Task<RecoveryPlan> CreatePlanAsync(ImageRecoveryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationRoot);
        request.EffectivePolicy.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessions.TryGetValue(request.ScanSessionId, out var session) || session.IsDisposed)
        {
            throw new InvalidOperationException("The recovery scan session is unknown or disposed.");
        }

        var seen = new HashSet<RecoveryCandidateId>();
        var publicItems = new List<RecoveryPlanItem>();
        var trustedItems = new List<TrustedRecoveryItem>();
        var totalKnownBytes = 0L;
        foreach (var candidateId in request.CandidateIds ?? throw new ArgumentNullException(nameof(request.CandidateIds)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(candidateId))
            {
                continue;
            }

            if (!session.Candidates.TryGetValue(candidateId, out var reference))
            {
                throw new InvalidOperationException("A selected candidate does not belong to this scan session.");
            }

            var candidate = reference.Candidate;
            var unnamed = candidate.DataStreams.SingleOrDefault(stream => string.IsNullOrEmpty(stream.Name));
            var (allowed, warning) = EvaluatePolicy(candidate, unnamed, request.EffectivePolicy);
            var expected = Math.Max(0, unnamed?.LogicalSize ?? candidate.LogicalSize);
            totalKnownBytes = checked(totalKnownBytes + expected);
            publicItems.Add(new(candidateId, candidate.MftRecordNumber, candidate.Name, expected, candidate.Recoverability, allowed, warning));
            trustedItems.Add(new(
                candidateId,
                candidate.MftRecordNumber,
                candidate.SequenceNumber,
                unnamed?.Name,
                unnamed?.AttributeIdentity ?? string.Empty,
                RecoveryFingerprint.ComputeCandidate(candidate),
                candidate.Recoverability,
                expected,
                candidate.Name,
                candidate.OriginalPath,
                allowed,
                warning));
        }

        var planId = Guid.NewGuid();
        var publicPlan = new RecoveryPlan(
            planId,
            session.SessionId,
            request.DestinationRoot,
            request.EffectivePolicy.OutputLayout,
            publicItems.ToArray(),
            totalKnownBytes);
        var engineRequest = new TrustedRecoveryBatchRequest(
            session.SessionId,
            session.Source,
            session.Budgets,
            request.DestinationRoot,
            request.EffectivePolicy,
            trustedItems.ToArray(),
            totalKnownBytes);
        if (!_plans.TryAdd(planId, new StoredPlan(publicPlan, engineRequest, session)))
        {
            throw new InvalidOperationException("A unique recovery plan could not be created.");
        }

        return Task.FromResult(publicPlan);
    }

    public async Task<RecoveryBatchResult> RecoverAsync(
        Guid planId,
        IProgress<RecoveryProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!_plans.TryGetValue(planId, out var stored) || stored.Session.IsDisposed ||
            !_sessions.ContainsKey(stored.Session.SessionId))
        {
            throw new InvalidOperationException("The recovery plan is unknown or its scan session was disposed.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stored.Session.Disposal.Token);
        try
        {
            return await recoveryEngine.RecoverAsync(stored.EngineRequest, progress, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return new(
                RecoveryBatchOutcome.Canceled,
                [],
                0,
                stored.EngineRequest.Items.Count,
                0,
                [new("RECOVERY_CANCELED", "batch", "Recovery was canceled before final completion publication.")]);
        }
    }

    public bool DisposeSession(Guid sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
        {
            return false;
        }

        session.Dispose();
        foreach (var plan in _plans.Where(item => item.Value.Session.SessionId == sessionId).ToArray())
        {
            _plans.TryRemove(plan.Key, out _);
        }

        return true;
    }

    private static (bool Allowed, string? Warning) EvaluatePolicy(
        DeletedFileCandidate candidate,
        NtfsDataStream? unnamed,
        RecoveryPolicy policy)
    {
        if (candidate.IsDirectory || unnamed is null || candidate.Recoverability == CandidateRecoverability.MetadataOnly)
        {
            return (false, "No recoverable unnamed file-content stream is available.");
        }

        if (!unnamed.MetadataIsComplete || candidate.Recoverability == CandidateRecoverability.DamagedMetadata)
        {
            return (false, "Damaged metadata is blocked.");
        }

        if (unnamed.IsCompressed || unnamed.IsEncrypted || unnamed.IsSparse || candidate.Recoverability == CandidateRecoverability.UnsupportedLayout)
        {
            return (false, "Compressed, encrypted, sparse, or otherwise unsupported streams are blocked.");
        }

        return candidate.Recoverability switch
        {
            CandidateRecoverability.ResidentDataAvailable or CandidateRecoverability.ZeroLength or CandidateRecoverability.PossiblyRecoverable => (true, null),
            CandidateRecoverability.PartiallyOverwritten when policy.AllowPartiallyOverwritten => (true, "Allocation evidence indicates that some clusters may have been overwritten."),
            CandidateRecoverability.Overwritten when policy.AllowOverwritten => (true, "Allocation evidence indicates that all referenced clusters are currently allocated."),
            CandidateRecoverability.Unknown when policy.AllowUnknown => (true, "Allocation evidence is unavailable or inconclusive."),
            CandidateRecoverability.PartiallyOverwritten => (false, "Partial allocation evidence requires explicit opt-in."),
            CandidateRecoverability.Overwritten => (false, "Overwritten allocation evidence requires explicit damaged-content opt-in."),
            CandidateRecoverability.Unknown => (false, "Unknown allocation evidence requires explicit opt-in."),
            _ => (false, "The candidate is not recoverable under the selected policy."),
        };
    }

    private static bool SourceMetadataMatches(RecoverySourceMetadata left, RecoverySourceMetadata right) =>
        string.Equals(left.CanonicalPath, right.CanonicalPath, StringComparison.OrdinalIgnoreCase) &&
        left.Length == right.Length &&
        left.LastWriteTimeUtcTicks == right.LastWriteTimeUtcTicks &&
        string.Equals(left.BootGeometryFingerprint, right.BootGeometryFingerprint, StringComparison.Ordinal);

    private sealed class SessionState
    {
        private int _disposed;

        public SessionState(
            Guid sessionId,
            StandardScanResult result,
            IReadOnlyList<RecoveryCandidateReference> candidates,
            RecoverySourceProvenance source,
            StandardScanBudgets budgets)
        {
            SessionId = sessionId;
            Result = result;
            Source = source;
            Budgets = budgets;
            Candidates = candidates.ToDictionary(candidate => candidate.Id);
        }

        public Guid SessionId { get; }
        public StandardScanResult Result { get; }
        public IReadOnlyDictionary<RecoveryCandidateId, RecoveryCandidateReference> Candidates { get; }
        public RecoverySourceProvenance Source { get; }
        public StandardScanBudgets Budgets { get; }
        public CancellationTokenSource Disposal { get; } = new();
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Disposal.Cancel();
            }
        }
    }

    private sealed record StoredPlan(RecoveryPlan PublicPlan, TrustedRecoveryBatchRequest EngineRequest, SessionState Session);
}
