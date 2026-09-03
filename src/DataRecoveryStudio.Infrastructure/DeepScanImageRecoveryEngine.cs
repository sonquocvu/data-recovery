using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

public sealed class DeepScanImageRecoveryEngine(
    IReadOnlyImageSourceFactory sourceFactory,
    IDeepScanSourceMetadataProvider metadataProvider,
    IDeepScanCandidateRevalidator revalidator) : ITrustedDeepScanRecoveryEngine
{
    public DeepScanImageRecoveryEngine() : this(
        new RegularFileRandomAccessSourceFactory(),
        new DeepScanSourceMetadataProvider(),
        new DeepScanCandidateRevalidator())
    {
    }

    public async Task<DeepScanRecoveryBatchResult> RecoverAsync(
        TrustedDeepScanRecoveryBatchRequest request,
        IProgress<DeepScanRecoveryProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Policy.Validate();
        var stopwatch = Stopwatch.StartNew();
        var diagnostics = new BoundedCarvingDiagnostics(request.Policy.MaximumDiagnostics);
        if (cancellationToken.IsCancellationRequested)
        {
            return Result(DeepScanRecoveryBatchOutcome.Canceled, [], 0, 0);
        }

        if (request.Items.Count > request.Policy.MaximumCandidatesPerBatch || request.TotalKnownBytes > request.Policy.MaximumTotalBytes ||
            request.TotalKnownBytes > request.Policy.MaximumPostWriteVerificationBytes)
        {
            diagnostics.Add(new("RECOVERY_BUDGET_REACHED", "The trusted recovery plan exceeds a hard Phase 6B batch budget."));
            return Result(DeepScanRecoveryBatchOutcome.CompletedWithFailures, [], 0, 0);
        }

        RecoveryDestination destination;
        try
        {
            destination = RecoveryDestination.ValidateAndOpen(
                request.DestinationRoot,
                request.Source.CanonicalPath,
                request.Policy.CreateDestinationIfMissing,
                request.TotalKnownBytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            diagnostics.Add(new("DESTINATION_UNAVAILABLE", SafeReason(exception)));
            return Result(DeepScanRecoveryBatchOutcome.DestinationUnavailable, [], 0, 0);
        }

        DeepScanSourceFingerprint before;
        try
        {
            before = await metadataProvider.CaptureAsync(request.Source.CanonicalPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result(DeepScanRecoveryBatchOutcome.Canceled, [], 0, 0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            diagnostics.Add(new("SOURCE_REOPEN_FAILED", SafeReason(exception)));
            return Result(DeepScanRecoveryBatchOutcome.SourceChanged, [], 0, 0);
        }

        if (!Matches(request.Source, before))
        {
            diagnostics.Add(new("SOURCE_CHANGED", "The regular image path, length, timestamp, or SHA-256 changed after scanning."));
            return Result(DeepScanRecoveryBatchOutcome.SourceChanged, [], 0, 0);
        }

        IReadOnlyRandomAccessSource opened;
        try
        {
            opened = await sourceFactory.OpenAsync(request.Source.CanonicalPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result(DeepScanRecoveryBatchOutcome.Canceled, [], 0, 0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            diagnostics.Add(new("SOURCE_REOPEN_FAILED", SafeReason(exception)));
            return Result(DeepScanRecoveryBatchOutcome.SourceChanged, [], 0, 0);
        }

        await using var sourceLifetime = opened;
        var deadline = DateTimeOffset.UtcNow + request.Policy.EffectiveMaximumRecoveryDuration;
        await using var source = new BoundedRecoverySource(opened, request.Policy.MaximumSourceReads, deadline);
        if (source.Length != request.Source.Length)
        {
            diagnostics.Add(new("SOURCE_CHANGED", "The image length changed before carving."));
            return Result(DeepScanRecoveryBatchOutcome.SourceChanged, [], 0, 0);
        }

        var outcomes = new List<DeepScanRecoveryFileResult>();
        var completed = 0;
        var verifiedBytes = 0L;
        var progressBytes = 0L;
        progress?.Report(new(null, null, 0, request.Items.Count, 0, request.TotalKnownBytes));
        foreach (var item in request.Items)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (DateTimeOffset.UtcNow > deadline)
            {
                diagnostics.Add(new("RECOVERY_DURATION_REACHED", "The recovery duration budget was reached before all items were processed."));
                break;
            }
            progress?.Report(new(item.CandidateId, item.GeneratedFileName, completed, request.Items.Count, progressBytes, request.TotalKnownBytes));
            DeepScanRecoveryFileResult outcome;
            if (!item.IsEligible)
            {
                outcome = Failure(item, DeepScanRecoveryFileOutcome.SkippedByPolicy, "RECOVERY_POLICY_BLOCKED", item.PolicyWarning ?? "The candidate is not eligible under the selected recovery policy.");
            }
            else
            {
                try
                {
                    var current = await revalidator.RevalidateAsync(source, item, request.Policy, deadline, cancellationToken).ConfigureAwait(false);
                    outcome = current.MatchesProvenance
                        ? await RecoverItemAsync(source, destination, request, item, progress, completed, progressBytes, deadline, cancellationToken).ConfigureAwait(false)
                        : Failure(item,
                            current.DiagnosticCode == "UNSUPPORTED_CANDIDATE" ? DeepScanRecoveryFileOutcome.UnsupportedCandidate : DeepScanRecoveryFileOutcome.CandidateChanged,
                            current.DiagnosticCode ?? "CANDIDATE_CHANGED",
                            current.Reason ?? "Candidate revalidation did not match scan provenance.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    outcome = Failure(item, DeepScanRecoveryFileOutcome.Canceled, "RECOVERY_CANCELED", "Recovery was canceled before final publication.");
                }
                catch (RecoveryBudgetExceededException exception)
                {
                    outcome = Failure(item, DeepScanRecoveryFileOutcome.SourceReadFailed, "RECOVERY_BUDGET_REACHED", exception.Message);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentOutOfRangeException or CryptographicException)
                {
                    outcome = Failure(item, DeepScanRecoveryFileOutcome.SourceReadFailed, "CANDIDATE_REVALIDATION_FAILED", SafeReason(exception));
                }
            }

            outcomes.Add(outcome);
            completed++;
            progressBytes = checked(progressBytes + Math.Max(0, outcome.BytesWritten));
            if (outcome.Outcome is DeepScanRecoveryFileOutcome.CarvedAndCopyVerified or DeepScanRecoveryFileOutcome.CarvedPartialWithWarning)
            {
                verifiedBytes = checked(verifiedBytes + outcome.BytesWritten);
            }

            progress?.Report(new(item.CandidateId, outcome.OutputPath is null ? item.GeneratedFileName : Path.GetFileName(outcome.OutputPath), completed, request.Items.Count, progressBytes, request.TotalKnownBytes));
            if (outcome.Outcome is DeepScanRecoveryFileOutcome.SourceChanged or DeepScanRecoveryFileOutcome.Canceled) break;
        }

        if (cancellationToken.IsCancellationRequested || outcomes.LastOrDefault()?.Outcome == DeepScanRecoveryFileOutcome.Canceled)
        {
            return Result(DeepScanRecoveryBatchOutcome.Canceled, outcomes, completed, verifiedBytes);
        }

        if (outcomes.LastOrDefault()?.Outcome == DeepScanRecoveryFileOutcome.SourceChanged)
        {
            return Result(DeepScanRecoveryBatchOutcome.SourceChanged, outcomes, completed, verifiedBytes);
        }

        var terminal = completed < request.Items.Count || outcomes.Any(item => item.Outcome is not (DeepScanRecoveryFileOutcome.CarvedAndCopyVerified or DeepScanRecoveryFileOutcome.CarvedPartialWithWarning))
            ? DeepScanRecoveryBatchOutcome.CompletedWithFailures
            : outcomes.Any(item => item.Outcome == DeepScanRecoveryFileOutcome.CarvedPartialWithWarning)
                ? DeepScanRecoveryBatchOutcome.CompletedWithWarnings
                : DeepScanRecoveryBatchOutcome.Completed;
        if (cancellationToken.IsCancellationRequested) terminal = DeepScanRecoveryBatchOutcome.Canceled;
        progress?.Report(new(null, null, completed, request.Items.Count, progressBytes, request.TotalKnownBytes));
        if (cancellationToken.IsCancellationRequested) terminal = DeepScanRecoveryBatchOutcome.Canceled;
        return Result(terminal, outcomes, completed, verifiedBytes);

        DeepScanRecoveryBatchResult Result(
            DeepScanRecoveryBatchOutcome outcome,
            IReadOnlyList<DeepScanRecoveryFileResult> files,
            int completedItems,
            long bytes)
        {
            stopwatch.Stop();
            return new(outcome, files.ToArray(), completedItems, request.Items.Count, bytes, diagnostics.Items, stopwatch.Elapsed);
        }
    }

    private async Task<DeepScanRecoveryFileResult> RecoverItemAsync(
        IReadOnlyRandomAccessSource source,
        RecoveryDestination destination,
        TrustedDeepScanRecoveryBatchRequest batch,
        TrustedDeepScanCandidate item,
        IProgress<DeepScanRecoveryProgress>? progress,
        int completedBefore,
        long progressBefore,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        var fallback = $"Carved_{item.FormatId}_{item.CandidateOffset:X16}{item.SuggestedExtension}";
        var basePath = destination.PrepareFlatBasePath(item.GeneratedFileName, fallback);
        var partialPath = RecoveryFilePublication.CreatePartialPath(destination, basePath, Guid.NewGuid());
        string? publishedPath = null;
        var written = 0L;
        var stage = "destination";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            destination.RevalidateParent(partialPath);
            string sourceHash;
            string writerHash;
            await using (var output = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                batch.Policy.BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            using (var sourceHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            using (var writerHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(batch.Policy.BufferSize);
                try
                {
                    while (written < item.CandidateLength)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (DateTimeOffset.UtcNow > deadline) throw new RecoveryBudgetExceededException("The recovery duration budget was reached.");
                        var count = checked((int)Math.Min(batch.Policy.BufferSize, item.CandidateLength - written));
                        var memory = buffer.AsMemory(0, count);
                        stage = "source";
                        await source.ReadExactlyAsync(checked(item.CandidateOffset + written), memory, cancellationToken).ConfigureAwait(false);
                        sourceHasher.AppendData(memory.Span);
                        stage = "destination";
                        await output.WriteAsync(memory, cancellationToken).ConfigureAwait(false);
                        writerHasher.AppendData(memory.Span);
                        written = checked(written + count);
                        progress?.Report(new(item.CandidateId, item.GeneratedFileName, completedBefore, batch.Items.Count, checked(progressBefore + written), batch.TotalKnownBytes));
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                cancellationToken.ThrowIfCancellationRequested();
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                sourceHash = Convert.ToHexString(sourceHasher.GetHashAndReset());
                writerHash = Convert.ToHexString(writerHasher.GetHashAndReset());
            }

            if (written != item.CandidateLength || !string.Equals(sourceHash, writerHash, StringComparison.Ordinal))
            {
                return CleanupFailure(item, partialPath, null, written, DeepScanRecoveryFileOutcome.VerificationFailed, "STREAM_VERIFICATION_FAILED", "The streamed byte count or write-path hash did not match.", batch.Policy.MaximumPartialCleanupAttempts);
            }

            var during = await metadataProvider.CaptureAsync(batch.Source.CanonicalPath, cancellationToken).ConfigureAwait(false);
            if (!Matches(batch.Source, during))
            {
                return CleanupFailure(item, partialPath, null, written, DeepScanRecoveryFileOutcome.SourceChanged, "SOURCE_CHANGED_DURING_RECOVERY", "The source changed before atomic publication.", batch.Policy.MaximumPartialCleanupAttempts);
            }

            cancellationToken.ThrowIfCancellationRequested();
            destination.RevalidateParent(basePath);
            publishedPath = RecoveryFilePublication.PublishWithoutOverwrite(partialPath, basePath, destination, batch.Policy.MaximumCollisionAttempts);
            partialPath = string.Empty;
            stage = "verification";
            var destinationHash = await RecoveryFilePublication.HashFileAsync(
                publishedPath,
                batch.Policy.BufferSize,
                Math.Min(batch.Policy.MaximumPostWriteVerificationBytes, item.CandidateLength),
                cancellationToken).ConfigureAwait(false);
            var after = await metadataProvider.CaptureAsync(batch.Source.CanonicalPath, cancellationToken).ConfigureAwait(false);
            if (!Matches(batch.Source, after))
            {
                return CleanupFailure(item, null, publishedPath, written, DeepScanRecoveryFileOutcome.SourceChanged, "SOURCE_CHANGED_DURING_RECOVERY", "The source changed before verification completed.", batch.Policy.MaximumPartialCleanupAttempts);
            }

            if (destinationHash.Bytes != item.CandidateLength || !string.Equals(sourceHash, destinationHash.Sha256, StringComparison.Ordinal))
            {
                return CleanupFailure(item, null, publishedPath, written, DeepScanRecoveryFileOutcome.VerificationFailed, "DESTINATION_VERIFICATION_FAILED", "Published output did not match the trusted source extent byte count and SHA-256.", batch.Policy.MaximumPartialCleanupAttempts);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new(
                item.CandidateId,
                item.FormatId,
                item.IsPartial ? DeepScanRecoveryFileOutcome.CarvedPartialWithWarning : DeepScanRecoveryFileOutcome.CarvedAndCopyVerified,
                publishedPath,
                written,
                sourceHash,
                RecoveryVerificationState.Verified,
                item.IsPartial
                    ? "Partial candidate bytes were copied and SHA-256 verified; completeness and original semantic integrity are unknown."
                    : "Structurally validated candidate bytes were copied and SHA-256 verified; original semantic integrity remains unknown.",
                []);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || DateTimeOffset.UtcNow > deadline)
        {
            return CleanupFailure(item, NullIfEmpty(partialPath), publishedPath, written, DeepScanRecoveryFileOutcome.Canceled, "RECOVERY_CANCELED", "Recovery was canceled and in-flight output was removed.", batch.Policy.MaximumPartialCleanupAttempts);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentOutOfRangeException or CryptographicException or RecoveryBudgetExceededException)
        {
            var outcome = stage == "source" ? DeepScanRecoveryFileOutcome.SourceReadFailed : stage == "verification" ? DeepScanRecoveryFileOutcome.VerificationFailed : DeepScanRecoveryFileOutcome.DestinationFailed;
            return CleanupFailure(item, NullIfEmpty(partialPath), publishedPath, written, outcome, "RECOVERY_ITEM_FAILED", SafeReason(exception), batch.Policy.MaximumPartialCleanupAttempts);
        }
    }

    private static DeepScanRecoveryFileResult CleanupFailure(
        TrustedDeepScanCandidate item,
        string? partialPath,
        string? publishedPath,
        long bytes,
        DeepScanRecoveryFileOutcome requestedOutcome,
        string code,
        string reason,
        int maximumAttempts)
    {
        if (!RecoveryFilePublication.CleanupExact(partialPath, publishedPath, maximumAttempts))
        {
            return Failure(item, DeepScanRecoveryFileOutcome.CleanupFailed, "RECOVERY_CLEANUP_FAILED", $"Exact cleanup failed after {code}.", bytes);
        }

        return Failure(item, requestedOutcome, code, reason, bytes);
    }

    private static DeepScanRecoveryFileResult Failure(
        TrustedDeepScanCandidate item,
        DeepScanRecoveryFileOutcome outcome,
        string code,
        string reason,
        long bytes = 0) =>
        new(
            item.CandidateId,
            item.FormatId,
            outcome,
            null,
            bytes,
            null,
            outcome == DeepScanRecoveryFileOutcome.VerificationFailed ? RecoveryVerificationState.Failed : RecoveryVerificationState.NotPerformed,
            reason,
            [new(code, reason, item.CandidateId, item.FormatId, item.GeneratedFileName, item.CandidateOffset, item.CandidateLength, bytes)]);

    private static bool Matches(DeepScanSourceFingerprint expected, DeepScanSourceFingerprint actual) =>
        string.Equals(expected.CanonicalPath, actual.CanonicalPath, StringComparison.OrdinalIgnoreCase) &&
        expected.Length == actual.Length &&
        expected.LastWriteTimeUtcTicks == actual.LastWriteTimeUtcTicks &&
        string.Equals(expected.Sha256, actual.Sha256, StringComparison.Ordinal);

    private static string SafeReason(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "Access to the bounded source or validated destination was denied.",
        DirectoryNotFoundException => "A required ordinary directory was not found.",
        EndOfStreamException => "The source returned an unexpected short read.",
        RecoveryBudgetExceededException => exception.Message,
        _ => exception.Message,
    };

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    private sealed class BoundedCarvingDiagnostics(int maximum)
    {
        private readonly List<DeepScanRecoveryDiagnostic> _items = [];
        public IReadOnlyList<DeepScanRecoveryDiagnostic> Items => _items.ToArray();
        public void Add(DeepScanRecoveryDiagnostic item)
        {
            if (_items.Count < maximum) _items.Add(item);
        }
    }
}

internal sealed class BoundedRecoverySource(
    IReadOnlyRandomAccessSource inner,
    long maximumReads,
    DateTimeOffset deadline) : IReadOnlyRandomAccessSource
{
    private long _reads;
    public long Length => inner.Length;

    public async ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow > deadline) throw new RecoveryBudgetExceededException("The recovery duration budget was reached.");
        if (Interlocked.Increment(ref _reads) > maximumReads) throw new RecoveryBudgetExceededException("The recovery source-read budget was reached.");
        await inner.ReadExactlyAsync(offset, destination, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class RecoveryBudgetExceededException(string message) : IOException(message);
