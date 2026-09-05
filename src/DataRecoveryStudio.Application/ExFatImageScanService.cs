using System.Collections.Concurrent;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Application;

/// <summary>Headless image orchestration; recovery authorization stays in this session owner.</summary>
public sealed partial class ExFatImageScanService(IReadOnlyImageSourceFactory sourceFactory,
    IExFatMetadataScanner scanner, IExFatSourceMetadataProvider metadataProvider,
    ITrustedExFatRecoveryEngine? recoveryEngine = null) : IExFatImageScanService, IExFatImageRecoveryService
{
    private readonly ConcurrentDictionary<Guid, SessionState> _sessions = new();

    public async Task<ExFatImageScanResult> ScanAsync(string imagePath, ExFatScanRequest request,
        IProgress<ExFatScanProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.EffectiveBudget.Validate();
        if (request.VolumeOffset < 0) throw new ArgumentOutOfRangeException(nameof(request));
        var work = new ExFatWork(request.EffectiveBudget);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(request.EffectiveBudget.EffectiveDuration);
        var token = deadline.Token;
        var bound = request with { Work = work, SuppressTerminalProgress = true };
        ExFatScanResult result = new(ExFatScanOutcome.Partial, null, null, null,
            Array.AsReadOnly(Array.Empty<ExFatScanCandidate>()), Array.AsReadOnly(Array.Empty<ExFatScanDiagnostic>()), work.Snapshot(ExFatScanPhase.Fingerprinting));
        ExFatScanSession? session = null;
        try
        {
            work.Check(token);
            Report(work, request.EffectiveBudget, progress, ExFatScanPhase.Fingerprinting, token);
            await using var source = await sourceFactory.OpenAsync(imagePath, token).ConfigureAwait(false);
            var before = await metadataProvider.CaptureAsync(imagePath, source, bound, token).ConfigureAwait(false);
            bound = bound with { SourceIdentity = before.Sha256 };
            result = await scanner.ScanAsync(source, bound, progress, token).ConfigureAwait(false);
            work.Check(token);
            Report(work, request.EffectiveBudget, progress, ExFatScanPhase.Finalizing, token);
            var after = await metadataProvider.CaptureAsync(before.CanonicalPath, source, bound, token).ConfigureAwait(false);
            if (before != after) throw new IOException("SOURCE_CHANGED: fingerprint, length, path or timestamp changed.");
            work.Check(token);
            if (result.Outcome == ExFatScanOutcome.Completed && ReferenceEquals(result.ProductionSeal?.Result, result) && result.Geometry is not null && result.BootEvidence is not null)
            {
                var id = Guid.NewGuid();
                result = result with { Metrics = work.Snapshot(ExFatScanPhase.Terminal) };
                var completedSession = new ExFatScanSession(id, before, request.VolumeOffset, result.Geometry, result.BootEvidence, ExFatScannerVersion.Phase8A, result);
                work.Check(token);
                if (!_sessions.TryAdd(id, new(completedSession, request.EffectiveBudget))) throw new InvalidOperationException("Session identity collision.");
                session = completedSession;
                if (token.IsCancellationRequested)
                {
                    DisposeSession(id);
                    session = null;
                    token.ThrowIfCancellationRequested();
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                work.Limited = true;
                result = Failure(result, ExFatScanOutcome.Partial, "BUDGET_Duration", request.EffectiveBudget);
            }
            else result = result with { Outcome = ExFatScanOutcome.Canceled };
        }
        catch (ExFatBudgetException exception)
        {
            result = Failure(result, ExFatScanOutcome.Partial, "BUDGET_" + exception.Message, request.EffectiveBudget);
        }
        catch (IOException exception) when (exception.Message.StartsWith("SOURCE_CHANGED", StringComparison.Ordinal))
        {
            result = Failure(result, ExFatScanOutcome.SourceChanged, "SOURCE_CHANGED", request.EffectiveBudget);
        }
        catch (IOException)
        {
            result = Failure(result, ExFatScanOutcome.Partial, "SOURCE_READ_FAILURE", request.EffectiveBudget);
        }
        if (result.Outcome != ExFatScanOutcome.Completed)
        {
            if (session is not null) { DisposeSession(session.SessionId); session = null; }
            result = result with
            {
                Candidates = Array.AsReadOnly(result.Candidates.Select(candidate =>
                candidate.Recoverability == ExFatRecoverabilityState.AllocationSuggestsPossibleContent
                    ? candidate with { AllocationEvidence = ExFatAllocationEvidence.Unknown, Recoverability = ExFatRecoverabilityState.Unknown, IsPartial = true }
                    : candidate with { IsPartial = true }).ToArray())
            };
        }
        work.Diagnostics = result.Diagnostics.Count;
        result = session?.Result ?? result with { Metrics = work.Snapshot(ExFatScanPhase.Terminal) };
        progress?.Report(result.Metrics);
        return new(result, session);
    }

    public async Task<ExFatScanSession> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(sessionId, out var state)) throw new InvalidOperationException("Unknown or disposed exFAT session.");
        var session = state.Session;
        try
        {
            await using var source = await sourceFactory.OpenAsync(session.Source.CanonicalPath, cancellationToken).ConfigureAwait(false);
            var current = await metadataProvider.CaptureAsync(session.Source.CanonicalPath, source, new(), cancellationToken).ConfigureAwait(false);
            if (current != session.Source) throw new IOException("SOURCE_CHANGED: exFAT session is stale.");
            cancellationToken.ThrowIfCancellationRequested();
            if (!_sessions.ContainsKey(sessionId)) throw new InvalidOperationException("The exFAT session was disposed.");
            return session;
        }
        catch (IOException)
        {
            DisposeSession(sessionId);
            throw;
        }
    }

    public bool DisposeSession(Guid sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var state)) return false;
        state.Disposal.Cancel();
        foreach (var plan in _plans.Where(p => p.Value.State == state)) _plans.TryRemove(plan.Key, out _);
        return true;
    }

    private sealed class SessionState(ExFatScanSession session, ExFatScanBudget budget)
    {
        internal ExFatScanSession Session { get; } = session;
        internal ExFatScanBudget Budget { get; } = budget;
        internal CancellationTokenSource Disposal { get; } = new();
    }

    private static ExFatScanResult Failure(ExFatScanResult result, ExFatScanOutcome outcome, string code, ExFatScanBudget budget) =>
        result with { Outcome = outcome, Diagnostics = Array.AsReadOnly(result.Diagnostics.Take(budget.MaximumDiagnostics - 1).Append(new ExFatScanDiagnostic(code, code)).ToArray()) };

    private static void Report(ExFatWork work, ExFatScanBudget budget, IProgress<ExFatScanProgress>? progress, ExFatScanPhase phase, CancellationToken token)
    {
        if (progress is not null && work.Callbacks < budget.MaximumProgressCallbacks - 1)
        {
            work.Callbacks++;
            progress.Report(work.Snapshot(phase));
        }
        work.Check(token);
    }
}
