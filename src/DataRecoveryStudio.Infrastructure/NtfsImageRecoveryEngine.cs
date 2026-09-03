using System.Security.Cryptography;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

public sealed class NtfsImageRecoveryEngine(
    IReadOnlyImageSourceFactory sourceFactory,
    IRecoverySourceMetadataProvider metadataProvider,
    NtfsMetadataScanner scanner) : ITrustedNtfsRecoveryEngine
{
    public NtfsImageRecoveryEngine() : this(
        new RegularFileRandomAccessSourceFactory(),
        new RecoverySourceMetadataProvider(),
        new NtfsMetadataScanner())
    {
    }

    public async Task<RecoveryBatchResult> RecoverAsync(
        TrustedRecoveryBatchRequest request,
        IProgress<RecoveryProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Policy.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var batchDiagnostics = new BoundedRecoveryDiagnostics(request.Policy.MaximumDiagnostics);
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
            batchDiagnostics.Add("DESTINATION_UNAVAILABLE", "destination-validation", SafeReason(exception));
            return new(RecoveryBatchOutcome.DestinationUnavailable, [], 0, request.Items.Count, 0, batchDiagnostics.Items);
        }

        RecoverySourceMetadata before;
        try
        {
            before = await metadataProvider.CaptureAsync(
                request.Source.CanonicalPath,
                request.Source.VolumeOffset,
                cancellationToken).ConfigureAwait(false);
            if (!Matches(request.Source, before))
            {
                batchDiagnostics.Add("SOURCE_CHANGED", "source-validation", "The source path, length, timestamp, or boot geometry changed after scanning.");
                return new(RecoveryBatchOutcome.FatalSourceFailure, [], 0, request.Items.Count, 0, batchDiagnostics.Items);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            batchDiagnostics.Add("SOURCE_REOPEN_FAILED", "source-validation", SafeReason(exception));
            return new(RecoveryBatchOutcome.FatalSourceFailure, [], 0, request.Items.Count, 0, batchDiagnostics.Items);
        }

        IReadOnlyRandomAccessSource openedSource;
        try
        {
            openedSource = await sourceFactory.OpenAsync(request.Source.CanonicalPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            batchDiagnostics.Add("SOURCE_REOPEN_FAILED", "source-reopen", SafeReason(exception));
            return new(RecoveryBatchOutcome.FatalSourceFailure, [], 0, request.Items.Count, 0, batchDiagnostics.Items);
        }

        await using var source = openedSource;
        if (source.Length != request.Source.SourceLength)
        {
            batchDiagnostics.Add("SOURCE_CHANGED", "source-validation", "The source length changed after scanning.");
            return new(RecoveryBatchOutcome.FatalSourceFailure, [], 0, request.Items.Count, 0, batchDiagnostics.Items);
        }

        StandardScanResult currentScan;
        try
        {
            currentScan = await scanner.ScanAsync(
                source,
                new StandardScanRequest(new(request.Source.VolumeOffset), request.ScanBudgets),
                null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentOutOfRangeException)
        {
            batchDiagnostics.Add("SOURCE_REVALIDATION_FAILED", "source-revalidation", SafeReason(exception));
            return new(RecoveryBatchOutcome.FatalSourceFailure, [], 0, request.Items.Count, 0, batchDiagnostics.Items);
        }

        if (currentScan.Outcome != StandardScanOutcome.Completed || currentScan.Geometry is null ||
            checked((long)currentScan.Geometry.TotalSectors * currentScan.Geometry.BytesPerSector) != request.Source.ValidatedVolumeLength ||
            !string.Equals(RecoveryFingerprint.ComputeGeometry(currentScan.Geometry), request.Source.BootGeometryFingerprint, StringComparison.Ordinal))
        {
            batchDiagnostics.Add("SOURCE_CHANGED", "source-revalidation", "The current NTFS scan no longer matches the trusted completed scan.");
            return new(RecoveryBatchOutcome.FatalSourceFailure, [], 0, request.Items.Count, 0, batchDiagnostics.Items);
        }

        var currentCandidates = currentScan.Candidates.ToDictionary(candidate => candidate.MftRecordNumber);
        var outcomes = new List<RecoveryFileOutcome>();
        var completed = 0;
        var successfulBytes = 0L;
        var progressBytes = 0L;
        var fatalSourceFailure = false;
        progress?.Report(new(null, null, 0, request.Items.Count, 0, request.TotalKnownBytes));

        foreach (var item in request.Items)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            progress?.Report(new(item.CandidateId, RecoveryDestination.SanitizeComponent(item.OriginalName, $"MFT-{item.MftRecordNumber}"), completed, request.Items.Count, progressBytes, request.TotalKnownBytes));
            RecoveryFileOutcome outcome;
            if (!item.IsAllowed)
            {
                outcome = Blocked(item);
            }
            else if (!currentCandidates.TryGetValue(item.MftRecordNumber, out var current) ||
                     current.SequenceNumber != item.SequenceNumber ||
                     !string.Equals(RecoveryFingerprint.ComputeCandidate(current), item.CandidateMetadataFingerprint, StringComparison.Ordinal))
            {
                outcome = Failure(item, RecoveryFileOutcomeCode.SourceChanged, "SOURCE_METADATA_CHANGED", "The candidate record, sequence, or stream metadata changed after scanning.");
                fatalSourceFailure = true;
            }
            else
            {
                try
                {
                    outcome = await RecoverItemAsync(
                        source,
                        destination,
                        request,
                        item,
                        progress,
                        completed,
                        progressBytes,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    outcome = Failure(item, RecoveryFileOutcomeCode.Canceled, "RECOVERY_CANCELED", "Recovery was canceled and incomplete output was removed.");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentOutOfRangeException or CryptographicException)
                {
                    outcome = Failure(item, RecoveryFileOutcomeCode.SourceReadFailed, "RECOVERY_ITEM_FAILED", SafeReason(exception));
                }
            }

            outcomes.Add(outcome);
            completed++;
            progressBytes = checked(progressBytes + Math.Max(0, outcome.BytesWritten));
            if (outcome.Outcome is RecoveryFileOutcomeCode.RecoveredAndVerified or RecoveryFileOutcomeCode.RecoveredWithAllocationWarning)
            {
                successfulBytes = checked(successfulBytes + outcome.BytesWritten);
            }

            progress?.Report(new(item.CandidateId, outcome.OutputPath is null ? null : Path.GetFileName(outcome.OutputPath), completed, request.Items.Count, progressBytes, request.TotalKnownBytes));
            if (fatalSourceFailure || outcome.Outcome == RecoveryFileOutcomeCode.Canceled)
            {
                break;
            }
        }

        if (cancellationToken.IsCancellationRequested || outcomes.LastOrDefault()?.Outcome == RecoveryFileOutcomeCode.Canceled)
        {
            return new(RecoveryBatchOutcome.Canceled, outcomes.ToArray(), completed, request.Items.Count, successfulBytes, batchDiagnostics.Items);
        }

        if (fatalSourceFailure)
        {
            return new(RecoveryBatchOutcome.FatalSourceFailure, outcomes.ToArray(), completed, request.Items.Count, successfulBytes, batchDiagnostics.Items);
        }

        var batchOutcome = outcomes.Any(outcome => outcome.Outcome is not (RecoveryFileOutcomeCode.RecoveredAndVerified or RecoveryFileOutcomeCode.RecoveredWithAllocationWarning))
            ? RecoveryBatchOutcome.CompletedWithFailures
            : RecoveryBatchOutcome.Completed;
        if (cancellationToken.IsCancellationRequested)
        {
            return new(RecoveryBatchOutcome.Canceled, outcomes.ToArray(), completed, request.Items.Count, successfulBytes, batchDiagnostics.Items);
        }

        progress?.Report(new(null, null, completed, request.Items.Count, progressBytes, request.TotalKnownBytes));
        return new(batchOutcome, outcomes.ToArray(), completed, request.Items.Count, successfulBytes, batchDiagnostics.Items);
    }

    private async Task<RecoveryFileOutcome> RecoverItemAsync(
        IReadOnlyRandomAccessSource source,
        RecoveryDestination destination,
        TrustedRecoveryBatchRequest batch,
        TrustedRecoveryItem item,
        IProgress<RecoveryProgress>? progress,
        int completedBefore,
        long progressBefore,
        CancellationToken cancellationToken)
    {
        var resolved = await scanner.ResolveRecoveryRecordAsync(
            source,
            new StandardScanRequest(new(batch.Source.VolumeOffset), batch.ScanBudgets),
            item.MftRecordNumber,
            cancellationToken).ConfigureAwait(false);
        if (resolved.Record.Base.SequenceNumber != item.SequenceNumber || resolved.Record.Base.IsInUse)
        {
            return Failure(item, RecoveryFileOutcomeCode.SourceChanged, "SOURCE_RECORD_CHANGED", "The selected record sequence or deleted state changed.");
        }

        var stream = resolved.Streams.SingleOrDefault(candidate => string.IsNullOrEmpty(candidate.Name));
        if (stream is null || !string.Equals(stream.AttributeIdentity, item.AttributeIdentity, StringComparison.Ordinal) || stream.LogicalSize != item.LogicalSize)
        {
            return Failure(item, RecoveryFileOutcomeCode.SourceChanged, "SOURCE_STREAM_CHANGED", "The selected unnamed data attribute no longer matches scan provenance.");
        }

        if (!stream.MetadataIsComplete || stream.Storage == NtfsDataStorage.Unknown)
        {
            return Failure(item, RecoveryFileOutcomeCode.DamagedMetadata, "DAMAGED_STREAM_METADATA", "The current stream metadata is damaged or conflicting.");
        }

        if (stream.IsCompressed || stream.IsEncrypted || stream.IsSparse)
        {
            return Failure(item, RecoveryFileOutcomeCode.UnsupportedLayout, "UNSUPPORTED_STREAM_LAYOUT", "Compressed, encrypted, and sparse streams are not supported in Phase 4C.");
        }

        if (stream.LogicalSize < 0 || stream.InitializedSize < 0 || stream.InitializedSize > stream.LogicalSize)
        {
            return Failure(item, RecoveryFileOutcomeCode.DamagedMetadata, "INVALID_STREAM_SIZE", "Logical or initialized stream sizes are inconsistent.");
        }

        var basePath = destination.PrepareDirectoryAndBasePath(item, batch.Policy.OutputLayout);
        var sanitizedName = Path.GetFileName(basePath);
        var partialPath = RecoveryFilePublication.CreatePartialPath(destination, basePath, Guid.NewGuid());
        string? publishedPath = null;
        var written = 0L;
        try
        {
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
                written = await StreamContentAsync(
                    source,
                    resolved,
                    stream,
                    output,
                    sourceHasher,
                    writerHasher,
                    batch,
                    item,
                    progress,
                    completedBefore,
                    progressBefore,
                    cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                sourceHash = Convert.ToHexString(sourceHasher.GetHashAndReset());
                writerHash = Convert.ToHexString(writerHasher.GetHashAndReset());
            }

            if (written != stream.LogicalSize || !string.Equals(sourceHash, writerHash, StringComparison.Ordinal))
            {
                return await CleanupFailureAsync(item, partialPath, null, written, RecoveryFileOutcomeCode.VerificationFailed, "STREAM_VERIFICATION_FAILED", "The streamed byte count or write-path hash did not match.").ConfigureAwait(false);
            }

            var during = await metadataProvider.CaptureAsync(batch.Source.CanonicalPath, batch.Source.VolumeOffset, cancellationToken).ConfigureAwait(false);
            if (!Matches(batch.Source, during))
            {
                return await CleanupFailureAsync(item, partialPath, null, written, RecoveryFileOutcomeCode.SourceChanged, "SOURCE_CHANGED_DURING_RECOVERY", "The source changed before atomic publication.").ConfigureAwait(false);
            }

            destination.RevalidateParent(basePath);
            publishedPath = RecoveryFilePublication.PublishWithoutOverwrite(partialPath, basePath, destination, batch.Policy.MaximumCollisionAttempts);
            partialPath = string.Empty;
            var destinationHash = (await RecoveryFilePublication.HashFileAsync(publishedPath, batch.Policy.BufferSize, stream.LogicalSize, cancellationToken).ConfigureAwait(false)).Sha256;
            var after = await metadataProvider.CaptureAsync(batch.Source.CanonicalPath, batch.Source.VolumeOffset, cancellationToken).ConfigureAwait(false);
            if (!Matches(batch.Source, after))
            {
                return await CleanupFailureAsync(item, null, publishedPath, written, RecoveryFileOutcomeCode.SourceChanged, "SOURCE_CHANGED_DURING_RECOVERY", "The source changed before verification completed.").ConfigureAwait(false);
            }

            if (!string.Equals(sourceHash, destinationHash, StringComparison.Ordinal) || new FileInfo(publishedPath).Length != stream.LogicalSize)
            {
                return await CleanupFailureAsync(item, null, publishedPath, written, RecoveryFileOutcomeCode.VerificationFailed, "DESTINATION_VERIFICATION_FAILED", "Published output did not match the source-stream byte count and SHA-256.").ConfigureAwait(false);
            }

            return new(
                item.CandidateId,
                item.MftRecordNumber,
                item.PolicyWarning is null ? RecoveryFileOutcomeCode.RecoveredAndVerified : RecoveryFileOutcomeCode.RecoveredWithAllocationWarning,
                publishedPath,
                written,
                sourceHash,
                RecoveryVerificationState.Verified,
                item.PolicyWarning is null
                    ? "Recovered bytes were copied and SHA-256 verified; this does not prove original file integrity."
                    : $"{item.PolicyWarning} Copied bytes were SHA-256 verified, which does not prove original file integrity.",
                []);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await CleanupFailureAsync(item, NullIfEmpty(partialPath), publishedPath, written, RecoveryFileOutcomeCode.Canceled, "RECOVERY_CANCELED", "Recovery was canceled and incomplete output was removed.").ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentOutOfRangeException or CryptographicException)
        {
            return await CleanupFailureAsync(item, NullIfEmpty(partialPath), publishedPath, written, RecoveryFileOutcomeCode.DestinationFailed, "RECOVERY_IO_FAILED", SafeReason(exception)).ConfigureAwait(false);
        }
    }

    private static async Task<long> StreamContentAsync(
        IReadOnlyRandomAccessSource source,
        NtfsRecoveryRecord resolved,
        MergedAttributeStream stream,
        Stream output,
        IncrementalHash sourceHasher,
        IncrementalHash writerHasher,
        TrustedRecoveryBatchRequest batch,
        TrustedRecoveryItem item,
        IProgress<RecoveryProgress>? progress,
        int completedBefore,
        long progressBefore,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[batch.Policy.BufferSize];
        NtfsVirtualStream? virtualStream = null;
        if (stream.Storage == NtfsDataStorage.NonResident)
        {
            if (!NtfsVirtualStream.TryCreate(
                    source,
                    new ScanReadBudget(long.MaxValue),
                    stream.Runs,
                    stream.LogicalSize,
                    batch.Source.VolumeOffset,
                    resolved.VolumeEnd,
                    resolved.Geometry.ClusterSize,
                    batch.ScanBudgets.MaximumDataRuns,
                    false,
                    out virtualStream,
                    out _,
                    out var reason,
                    "NTFS_RECOVERY_SPARSE_EXTENT") || virtualStream!.CoveredLength < stream.LogicalSize)
            {
                throw new InvalidDataException(reason);
            }
        }
        else if (stream.Storage != NtfsDataStorage.Resident)
        {
            throw new InvalidDataException("The stream storage form is unsupported.");
        }

        var resident = stream.ResidentValue;
        if (stream.Storage == NtfsDataStorage.Resident && (resident is null || resident.LongLength != stream.LogicalSize))
        {
            throw new InvalidDataException("Resident data boundaries no longer match the logical size.");
        }

        var position = 0L;
        while (position < stream.LogicalSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = checked((int)Math.Min(buffer.Length, stream.LogicalSize - position));
            var memory = buffer.AsMemory(0, count);
            if (stream.Storage == NtfsDataStorage.Resident)
            {
                resident!.AsMemory(checked((int)position), count).CopyTo(memory);
            }
            else if (position < stream.InitializedSize)
            {
                count = checked((int)Math.Min(count, stream.InitializedSize - position));
                memory = buffer.AsMemory(0, count);
                await virtualStream!.ReadExactlyAsync(position, memory, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                memory.Span.Clear();
            }

            sourceHasher.AppendData(memory.Span);
            await output.WriteAsync(memory, cancellationToken).ConfigureAwait(false);
            writerHasher.AppendData(memory.Span);
            position = checked(position + count);
            progress?.Report(new(item.CandidateId, RecoveryDestination.SanitizeComponent(item.OriginalName, $"MFT-{item.MftRecordNumber}"), completedBefore, batch.Items.Count, checked(progressBefore + position), batch.TotalKnownBytes));
        }

        return position;
    }

    private static Task<RecoveryFileOutcome> CleanupFailureAsync(
        TrustedRecoveryItem item,
        string? partialPath,
        string? publishedPath,
        long bytes,
        RecoveryFileOutcomeCode requestedOutcome,
        string code,
        string reason)
    {
        try
        {
            if (!RecoveryFilePublication.CleanupExact(partialPath, publishedPath, 3))
            {
                throw new IOException("Exact recovery cleanup did not complete.");
            }

            return Task.FromResult(Failure(item, requestedOutcome, code, reason, bytes));
        }
        catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(Failure(
                item,
                RecoveryFileOutcomeCode.CleanupFailed,
                "RECOVERY_CLEANUP_FAILED",
                $"Cleanup failed after {code}: {SafeReason(cleanupException)}",
                bytes));
        }
    }

    private static RecoveryFileOutcome Blocked(TrustedRecoveryItem item)
    {
        var code = item.Recoverability switch
        {
            CandidateRecoverability.UnsupportedLayout => RecoveryFileOutcomeCode.UnsupportedLayout,
            CandidateRecoverability.DamagedMetadata => RecoveryFileOutcomeCode.DamagedMetadata,
            _ => RecoveryFileOutcomeCode.SkippedByPolicy,
        };
        return Failure(item, code, "RECOVERY_POLICY_BLOCKED", item.PolicyWarning ?? "The candidate was blocked by recovery policy.");
    }

    private static RecoveryFileOutcome Failure(
        TrustedRecoveryItem item,
        RecoveryFileOutcomeCode outcome,
        string code,
        string reason,
        long bytes = 0) =>
        new(
            item.CandidateId,
            item.MftRecordNumber,
            outcome,
            null,
            bytes,
            null,
            outcome == RecoveryFileOutcomeCode.VerificationFailed ? RecoveryVerificationState.Failed : RecoveryVerificationState.NotPerformed,
            reason,
            [new(code, "recover-file", reason, item.MftRecordNumber, RecoveryDestination.SanitizeComponent(item.OriginalName, $"MFT-{item.MftRecordNumber}"))]);

    private static bool Matches(RecoverySourceProvenance expected, RecoverySourceMetadata actual) =>
        string.Equals(expected.CanonicalPath, actual.CanonicalPath, StringComparison.OrdinalIgnoreCase) &&
        expected.SourceLength == actual.Length &&
        expected.LastWriteTimeUtcTicks == actual.LastWriteTimeUtcTicks &&
        string.Equals(expected.BootGeometryFingerprint, actual.BootGeometryFingerprint, StringComparison.Ordinal);

    private static string SafeReason(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "Access to the bounded source or destination was denied.",
        DirectoryNotFoundException => "A required ordinary directory was not found.",
        EndOfStreamException => "The source returned an unexpected short read.",
        _ => exception.Message,
    };

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    private sealed class BoundedRecoveryDiagnostics(int maximum)
    {
        private readonly List<RecoveryDiagnostic> _items = [];

        public IReadOnlyList<RecoveryDiagnostic> Items => _items.ToArray();

        public void Add(string code, string operation, string reason)
        {
            if (_items.Count < maximum)
            {
                _items.Add(new(code, operation, reason));
            }
        }
    }
}
