using System.Security.Cryptography;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

/// <summary>Headless reconstruction from a session authorization and fresh production metadata.</summary>
public sealed class ExFatImageRecoveryEngine : ITrustedExFatRecoveryEngine
{
    private readonly IReadOnlyImageSourceFactory _sources;
    private readonly IExFatMetadataScanner _scanner;
    private readonly IExFatSourceMetadataProvider _fingerprints;
    private readonly RecoveryFileOperations _files;

    public ExFatImageRecoveryEngine() : this(new ExFatImageSourceFactory(), new ExFatMetadataScanner(), new ExFatSourceMetadataProvider(), new()) { }
    internal ExFatImageRecoveryEngine(IReadOnlyImageSourceFactory sources, IExFatMetadataScanner scanner,
        IExFatSourceMetadataProvider fingerprints, RecoveryFileOperations files)
    { _sources = sources; _scanner = scanner; _fingerprints = fingerprints; _files = files; }

    public Task<ExFatRecoveryBatchResult> RecoverAsync(TrustedExFatRecoveryRequest request,
        IProgress<ExFatRecoveryProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Policy.Validate();
        return new Batch(this, request, progress, cancellationToken).RunAsync();
    }

    private sealed class Batch(ExFatImageRecoveryEngine engine, TrustedExFatRecoveryRequest request,
        IProgress<ExFatRecoveryProgress>? progress, CancellationToken callerToken)
    {
        private readonly ExFatRecoveryPolicy _policy = request.Policy;
        private readonly ExFatScanBudget _budget = request.ScanBudget with
        {
            MaximumSourceBytes = Math.Min(request.ScanBudget.MaximumSourceBytes, request.Policy.MaximumSourceBytes),
            MaximumChainLength = Math.Min(request.ScanBudget.MaximumChainLength, request.Policy.MaximumChainLength),
            MaximumVisitedClusters = Math.Min(request.ScanBudget.MaximumVisitedClusters, request.Policy.MaximumTotalClusters),
            MaximumDuration = request.ScanBudget.EffectiveDuration < request.Policy.EffectiveDuration ? request.ScanBudget.EffectiveDuration : request.Policy.EffectiveDuration,
        };
        private readonly Dictionary<string, ExFatRecoveryFileResult> _results = new(StringComparer.Ordinal);
        private readonly List<ExFatRecoveryDiagnostic> _diagnostics = [];
        private readonly List<StagedFile> _staged = [];
        private ExFatWork _work = null!;
        private RecoveryDestination? _destination;
        private RecoveryDirectoryLease? _destinationLease;
        private ExFatRecoveryPhase _phase;
        private string? _candidate;
        private long _metadataBytes, _payloadBytes, _copiedBytes, _zeroBytes, _verificationBytes;
        private int _callbacks;

        internal async Task<ExFatRecoveryBatchResult> RunAsync()
        {
            _work = new(_budget);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
            deadline.CancelAfter(_budget.EffectiveDuration);
            var token = deadline.Token;
            var outcome = ExFatRecoveryBatchOutcome.Completed;
            try
            {
                _work.Check(token);
                var eligible = request.Candidates.Where(c => ExFatRecoveryEligibility.Evaluate(c, _policy).Allowed).ToArray();
                var outputBytes = eligible.Sum(c => c.LogicalSize);
                var payloadBytes = eligible.Sum(c => c.ValidDataLength);
                var metadataEstimate = request.Session.Result.Metrics.SourceBytesRead - request.Session.Result.Metrics.FingerprintBytesRead;
                var source = request.Session.Source;
                _work.Require(request.Candidates.Count <= _policy.MaximumCandidates, "Candidates: select fewer files.");
                _work.Require(outputBytes <= _policy.MaximumOutputBytes && eligible.All(c => c.LogicalSize <= _policy.MaximumFileBytes), "OutputBytes: select fewer or smaller files.");
                _work.Require(source.Length <= (_budget.MaximumSourceBytes - payloadBytes - metadataEstimate) / 2, "SourceBytes: two full fingerprints, known metadata refresh and initialized payload exceed the read ceiling; use a smaller image or selection.");
                var clusters = eligible.Sum(c => ClusterCount(c.LogicalSize));
                _work.Require(clusters <= _policy.MaximumTotalClusters && eligible.All(c => ClusterCount(c.LogicalSize) <= _policy.MaximumChainLength), "Clusters: select fewer or smaller files.");
                _work.Require(request.Session.ScannerVersion == ExFatScannerVersion.Phase8A, "ScannerVersion");
                foreach (var c in request.Candidates.Except(eligible))
                    _results[c.CandidateId] = Result(c, ExFatRecoveryOutcome.Blocked, "BLOCKED", ExFatRecoveryEligibility.Evaluate(c, _policy).Reason);

                Report(ExFatRecoveryPhase.ValidatingSource, token);
                await using var image = await OpenImageAsync(source.CanonicalPath, token).ConfigureAwait(false);
                ValidateIdentity(image);
                var scanRequest = new ExFatScanRequest(request.Session.VolumeOffset, _budget)
                {
                    SourceIdentity = source.Sha256,
                    Work = _work,
                    SuppressTerminalProgress = true,
                    RecoverySelection = eligible.Select(c => c.CandidateId).ToHashSet(StringComparer.Ordinal),
                };
                _work.ReservedForFutureBytes = checked(source.Length + payloadBytes);
                await CheckFingerprintAsync(image, scanRequest, token).ConfigureAwait(false);
                Report(ExFatRecoveryPhase.RevalidatingMetadata, token);
                var beforeMetadata = _work.Bytes;
                ExFatScanResult fresh;
                try { fresh = await engine._scanner.ScanAsync(image, scanRequest, null, token).ConfigureAwait(false); }
                finally { _metadataBytes += _work.Bytes - beforeMetadata; }
                _work.Check(token);
                if (_work.Limited) throw new ExFatBudgetException("Metadata: lower the selection or use a smaller image; shared metadata must fit alongside both fingerprints and payload.");
                if (fresh.Outcome != ExFatScanOutcome.Completed || !ReferenceEquals(fresh.ProductionSeal?.Result, fresh) ||
                    fresh.Geometry != request.Session.Geometry || fresh.BootEvidence != request.Session.BootEvidence)
                    throw new MetadataChangedException();
                var catalog = fresh.Candidates.ToDictionary(c => c.CandidateId, StringComparer.Ordinal);
                foreach (var original in request.Candidates)
                {
                    _work.Check(token);
                    if (!catalog.TryGetValue(original.CandidateId, out var current) || !Matches(original, current))
                        throw new MetadataChangedException();
                }
                foreach (var c in eligible)
                    if (!fresh.ProductionSeal!.RecoveryLayouts.TryGetValue(c.CandidateId, out var chain) || chain.Count != ClusterCount(c.LogicalSize))
                        throw new MetadataChangedException();

                if (eligible.Length != 0)
                {
                    _destination = engine._files.OpenDestination(request.Destination, source.CanonicalPath, _policy.CreateDestinationIfMissing, outputBytes);
                    _destinationLease = new(_destination.Root, allowChildRenames: true);
                    var buffer = new byte[_policy.BufferSize];
                    foreach (var original in eligible)
                    {
                        _candidate = original.CandidateId;
                        Report(ExFatRecoveryPhase.Recovering, token);
                        ValidateIdentity(image);
                        ValidateDestination(_destination.Root + Path.DirectorySeparatorChar + ".validation");
                        await StageAsync(catalog[original.CandidateId], fresh.ProductionSeal!.RecoveryLayouts[original.CandidateId], image, buffer, token).ConfigureAwait(false);
                    }
                }
                _candidate = null;
                _work.ReservedForFutureBytes = 0;
                Report(ExFatRecoveryPhase.ValidatingSourceAfterCopy, token);
                await CheckFingerprintAsync(image, scanRequest, token).ConfigureAwait(false);
                foreach (var staged in _staged.Where(s => s.Ready))
                {
                    _candidate = staged.Candidate.CandidateId;
                    Report(ExFatRecoveryPhase.VerifyingOutput, token);
                    ValidateIdentity(image);
                    await PublishAsync(staged, token).ConfigureAwait(false);
                    ValidateIdentity(image);
                }
            }
            catch (OperationCanceledException) { outcome = callerToken.IsCancellationRequested ? ExFatRecoveryBatchOutcome.Canceled : ExFatRecoveryBatchOutcome.BudgetExceeded; Diagnostic("STOPPED", outcome.ToString()); }
            catch (ExFatBudgetException exception) { outcome = ExFatRecoveryBatchOutcome.BudgetExceeded; Diagnostic("BUDGET", exception.Message); }
            catch (MetadataChangedException) { outcome = ExFatRecoveryBatchOutcome.MetadataChanged; Diagnostic("METADATA_CHANGED", "Fresh production metadata no longer matches the trusted scan; scan again."); }
            catch (DestinationChangedException exception) { outcome = ExFatRecoveryBatchOutcome.DestinationUnavailable; Diagnostic("DESTINATION_CHANGED", exception.Message); }
            catch (IOException exception) when (exception.Message.StartsWith("SOURCE_CHANGED", StringComparison.Ordinal))
            { outcome = ExFatRecoveryBatchOutcome.SourceChanged; Diagnostic("SOURCE_CHANGED", "Source identity or fingerprint changed; scan again."); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                outcome = _phase is ExFatRecoveryPhase.RevalidatingMetadata or ExFatRecoveryPhase.Recovering ? ExFatRecoveryBatchOutcome.DestinationUnavailable : ExFatRecoveryBatchOutcome.SourceReadFailed;
                Diagnostic("BATCH_FAILED", exception.Message);
            }
            finally
            {
                _candidate = null;
                ReportCleanup();
                foreach (var staged in _staged.Where(s => !s.Verified && s.OwnedPath is not null)) Cleanup(staged);
                _destinationLease?.Dispose();
            }
            foreach (var c in request.Candidates)
                if (!_results.ContainsKey(c.CandidateId))
                    _results[c.CandidateId] = Result(c, outcome == ExFatRecoveryBatchOutcome.Canceled ? ExFatRecoveryOutcome.Canceled :
                        outcome is ExFatRecoveryBatchOutcome.SourceChanged or ExFatRecoveryBatchOutcome.MetadataChanged ? ExFatRecoveryOutcome.SourceChanged : ExFatRecoveryOutcome.NotAttempted,
                        "BATCH_STOPPED", outcome.ToString(), _staged.FirstOrDefault(s => s.Candidate.CandidateId == c.CandidateId));
            if (outcome == ExFatRecoveryBatchOutcome.Completed && _results.Values.Any(r => r.Outcome != ExFatRecoveryOutcome.ReconstructedCopyVerified))
                outcome = ExFatRecoveryBatchOutcome.CompletedWithFailures;
            var terminal = Snapshot(ExFatRecoveryPhase.Completed, true);
            progress?.Report(terminal);
            return new(outcome, Array.AsReadOnly(request.Candidates.Select(c => _results[c.CandidateId]).ToArray()), _diagnostics.AsReadOnly(), terminal);
        }

        private long ClusterCount(long size) => size == 0 ? 0 : checked((size - 1) / request.Session.Geometry.ClusterSize + 1);
        private async ValueTask<IReadOnlyRandomAccessSource> OpenImageAsync(string path, CancellationToken token)
        {
            try { return await engine._sources.OpenAsync(path, token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or NotSupportedException)
            { throw new IOException("SOURCE_CHANGED: source is missing or no longer an ordinary image.", exception); }
        }
        private static bool Matches(ExFatScanCandidate original, ExFatScanCandidate fresh) =>
            original.Provenance == fresh.Provenance && original.NameEvidence == fresh.NameEvidence && original.AllocationEvidence == fresh.AllocationEvidence &&
            original.Layout == fresh.Layout && original.Attributes == fresh.Attributes && original.LogicalSize == fresh.LogicalSize &&
            original.ValidDataLength == fresh.ValidDataLength && original.IsPartial == fresh.IsPartial && original.PathState == fresh.PathState && original.ParentPath == fresh.ParentPath;

        private void ValidateIdentity(IReadOnlyRandomAccessSource image)
        {
            try
            {
                var expected = request.Session.Source;
                if (image is not IRegularFileIdentitySource bound || bound.FileIdentity != expected.FileIdentity)
                    throw new IOException("SOURCE_CHANGED: stable regular-file identity is required.");
                bound.ValidateIdentity(expected.CanonicalPath);
                var info = new FileInfo(expected.CanonicalPath);
                if (!info.Exists || info.Length != expected.Length || info.LastWriteTimeUtc.Ticks != expected.LastWriteTimeUtcTicks)
                    throw new IOException("SOURCE_CHANGED: file length or timestamp differs.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            { throw new IOException("SOURCE_CHANGED: source identity cannot be confirmed.", exception); }
        }
        private void ValidateDestination(string path)
        {
            try { _destination!.RevalidateParent(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            { throw new DestinationChangedException(exception.Message); }
        }
        private async Task CheckFingerprintAsync(IReadOnlyRandomAccessSource image, ExFatScanRequest scanRequest, CancellationToken token)
        {
            ValidateIdentity(image);
            var current = await engine._fingerprints.CaptureAsync(request.Session.Source.CanonicalPath, image, scanRequest, token).ConfigureAwait(false);
            if (current != request.Session.Source) throw new IOException("SOURCE_CHANGED: full fingerprint differs.");
            _work.Check(token);
        }

        private async Task StageAsync(ExFatScanCandidate candidate, IReadOnlyList<uint> chain, IReadOnlyRandomAccessSource image, byte[] buffer, CancellationToken token)
        {
            var staged = new StagedFile(candidate);
            _staged.Add(staged);
            try
            {
                var fallback = "EXFAT_Deleted_" + candidate.CandidateId + ".bin";
                staged.BasePath = _destination!.PrepareFlatBasePath(staged.Fallback ? fallback : candidate.Provenance!.OriginalName, fallback);
                var partial = RecoveryFilePublication.CreatePartialPath(_destination, staged.BasePath, Guid.NewGuid());
                ValidateDestination(partial);
                await using (var output = engine._files.CreatePartial(partial, _policy.BufferSize))
                {
                    // Never claim a path before exclusive creation has succeeded.
                    staged.OwnedPath = partial;
                    staged.Identity = RegularFileIdentity.Capture(output.SafeFileHandle);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var geometry = request.Session.Geometry;
                    for (long position = 0; position < candidate.LogicalSize;)
                    {
                        _work.Check(token);
                        var initialized = position < candidate.ValidDataLength;
                        var count = (int)Math.Min(buffer.Length, (initialized ? candidate.ValidDataLength : candidate.LogicalSize) - position);
                        if (initialized)
                        {
                            var clusterOffset = position % geometry.ClusterSize;
                            count = (int)Math.Min(count, geometry.ClusterSize - clusterOffset);
                            var cluster = chain[checked((int)(position / geometry.ClusterSize))];
                            var offset = checked(request.Session.VolumeOffset + (long)geometry.ClusterHeapOffset * geometry.BytesPerSector + (cluster - 2L) * geometry.ClusterSize + clusterOffset);
                            _work.Require(offset >= 0 && offset <= image.Length - count, "PayloadRange");
                            _work.ReservedForFutureBytes -= count;
                            _work.BeforeRead(count, token);
                            try { await image.ReadExactlyAsync(offset, buffer.AsMemory(0, count), token).ConfigureAwait(false); }
                            catch (IOException exception) when (!exception.Message.StartsWith("SOURCE_CHANGED", StringComparison.Ordinal)) { throw new PayloadReadException(exception); }
                            _work.AfterRead(count, false);
                            _payloadBytes += count;
                        }
                        else Array.Clear(buffer, 0, count);
                        _work.Check(token);
                        await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                        hash.AppendData(buffer, 0, count);
                        if (initialized) { staged.Copied += count; _copiedBytes += count; }
                        else { staged.Zeros += count; _zeroBytes += count; }
                        position += count;
                        Report(ExFatRecoveryPhase.Recovering, token);
                    }
                    await output.FlushAsync(token).ConfigureAwait(false);
                    _work.Check(token);
                    output.Flush(true);
                    staged.Hash = Convert.ToHexString(hash.GetHashAndReset());
                }
                _work.Check(token);
                staged.Ready = true;
            }
            catch (PayloadReadException exception) { _results[candidate.CandidateId] = Result(candidate, ExFatRecoveryOutcome.SourceReadFailed, "PAYLOAD_READ_FAILED", exception.InnerException!.Message, staged); Cleanup(staged); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                if (exception.Message.StartsWith("SOURCE_CHANGED", StringComparison.Ordinal)) throw;
                _results[candidate.CandidateId] = Result(candidate, ExFatRecoveryOutcome.DestinationFailed, "DESTINATION_FAILED", exception.Message, staged);
                Cleanup(staged);
            }
        }

        private async Task PublishAsync(StagedFile staged, CancellationToken token)
        {
            var verifying = false;
            try
            {
                _work.Check(token);
                ValidateDestination(staged.OwnedPath!);
                if (RegularFileIdentity.CapturePath(staged.OwnedPath!) != staged.Identity) throw new IOException("Owned partial identity changed.");
                staged.OwnedPath = engine._files.Publish(staged.OwnedPath!, staged.BasePath!, _destination!, _policy.MaximumCollisionAttempts);
                verifying = true;
                _work.Check(token);
                ValidateDestination(staged.OwnedPath);
                if (RegularFileIdentity.CapturePath(staged.OwnedPath) != staged.Identity) throw new IOException("Published output identity changed.");
                staged.VerificationAttempted = true;
                var verified = await engine._files.VerifyAsync(staged.OwnedPath, _policy.BufferSize, staged.Candidate.LogicalSize, token,
                    bytes => { _verificationBytes += bytes; _work.Check(token); }).ConfigureAwait(false);
                _work.Check(token);
                ValidateDestination(staged.OwnedPath);
                if (verified.Bytes != staged.Candidate.LogicalSize || verified.Sha256 != staged.Hash || RegularFileIdentity.CapturePath(staged.OwnedPath) != staged.Identity)
                    throw new IOException("Reconstructed logical stream length or SHA-256 verification failed.");
                staged.Verified = true;
                _results[staged.Candidate.CandidateId] = new(staged.Candidate.CandidateId, ExFatRecoveryOutcome.ReconstructedCopyVerified,
                    staged.OwnedPath, staged.Candidate.NameEvidence, staged.Fallback, staged.Candidate.LogicalSize, staged.Copied, staged.Zeros, staged.Hash,
                    RecoveryVerificationState.Verified, Array.AsReadOnly(Array.Empty<ExFatRecoveryDiagnostic>()));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                _results[staged.Candidate.CandidateId] = Result(staged.Candidate, verifying ? ExFatRecoveryOutcome.VerificationFailed : ExFatRecoveryOutcome.DestinationFailed,
                    verifying ? "VERIFICATION_FAILED" : "PUBLICATION_FAILED", exception.Message, staged);
                Cleanup(staged);
            }
        }

        private void Cleanup(StagedFile staged)
        {
            if (staged.Verified || staged.OwnedPath is null || staged.CleanupAttempted) return;
            staged.CleanupAttempted = true;
            ReportCleanup();
            if (staged.Identity is not null && engine._files.Cleanup(staged.OwnedPath, staged.Identity, _destination!, _policy.MaximumCleanupAttempts))
                staged.OwnedPath = null;
            else
                _results[staged.Candidate.CandidateId] = Result(staged.Candidate, ExFatRecoveryOutcome.CleanupFailed, "CLEANUP_FAILED", "Could not safely remove the owned artifact; inspect the reported path.", staged, staged.OwnedPath);
        }
        private static ExFatRecoveryFileResult Result(ExFatScanCandidate c, ExFatRecoveryOutcome outcome, string code, string reason, StagedFile? staged = null, string? ownedPath = null) =>
            new(c.CandidateId, outcome, null, c.NameEvidence, staged?.Fallback ?? NeedsFallback(c), c.LogicalSize, staged?.Copied ?? 0, staged?.Zeros ?? 0, null,
                outcome == ExFatRecoveryOutcome.VerificationFailed || staged?.VerificationAttempted == true ? RecoveryVerificationState.Failed : RecoveryVerificationState.NotPerformed,
                Array.AsReadOnly(new[] { new ExFatRecoveryDiagnostic(code, reason, c.CandidateId, ownedPath) }));
        private static bool NeedsFallback(ExFatScanCandidate c) => c.NameEvidence is not (ExFatNameEvidence.VerifiedDeletedName or ExFatNameEvidence.ChecksumRecoveredName);
        private void Diagnostic(string code, string reason) { if (_diagnostics.Count < _policy.MaximumDiagnostics) _diagnostics.Add(new(code, reason, _candidate)); }
        private ExFatRecoveryProgress Snapshot(ExFatRecoveryPhase phase, bool terminal = false) =>
            new(phase, _candidate, _results.Count, request.Candidates.Count, _work.Bytes, _work.FingerprintBytes, _metadataBytes, _payloadBytes,
                _copiedBytes, _zeroBytes, _verificationBytes, _work.ReservedReadBytes, _work.Clock.Elapsed, terminal);
        private void Report(ExFatRecoveryPhase phase, CancellationToken token)
        {
            _phase = phase;
            if (_callbacks < _policy.MaximumProgressCallbacks - 1) { _callbacks++; progress?.Report(Snapshot(phase)); }
            _work.Check(token);
        }
        private void ReportCleanup()
        {
            if (_callbacks < _policy.MaximumProgressCallbacks - 1) { _callbacks++; progress?.Report(Snapshot(ExFatRecoveryPhase.Cleanup)); }
        }
        private sealed class StagedFile(ExFatScanCandidate candidate)
        {
            internal ExFatScanCandidate Candidate { get; } = candidate;
            internal bool Fallback { get; } = NeedsFallback(candidate);
            internal string? BasePath, OwnedPath, Hash;
            internal RegularFileIdentity? Identity;
            internal long Copied, Zeros;
            internal bool Ready, Verified, CleanupAttempted, VerificationAttempted;
        }
        private sealed class MetadataChangedException : Exception;
        private sealed class DestinationChangedException(string message) : Exception(message);
        private sealed class PayloadReadException(IOException inner) : Exception("Payload read failed", inner);
    }
}
