using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

public sealed class Fat32ImageRecoveryEngine(
    IReadOnlyImageSourceFactory sourceFactory,
    IFat32SourceMetadataProvider metadataProvider,
    IFat32MetadataScanner scanner) : ITrustedFat32RecoveryEngine
{
    public Fat32ImageRecoveryEngine() : this(
        new RegularFileRandomAccessSourceFactory(),
        new Fat32SourceMetadataProvider(),
        new Fat32MetadataScanner())
    {
    }

    public async Task<Fat32RecoveryBatchResult> RecoverAsync(
        TrustedFat32RecoveryBatchRequest request,
        IProgress<Fat32RecoveryProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Policy.Validate();
        var stopwatch = Stopwatch.StartNew();
        var diagnostics = new Fat32RecoveryDiagnostics(request.Policy.MaximumDiagnostics);
        if (cancellationToken.IsCancellationRequested) return Result(Fat32RecoveryBatchOutcome.Canceled, [], 0);
        if (request.Items.Count > request.Policy.MaximumCandidates || request.TotalKnownBytes > request.Policy.MaximumTotalRecoveredBytes)
        {
            diagnostics.Add("FAT32_RECOVERY_BUDGET", "The trusted batch exceeds a hard Phase 7B recovery budget.");
            return Result(Fat32RecoveryBatchOutcome.BudgetExceeded, [], 0);
        }

        RecoveryDestination destination;
        try
        {
            destination = RecoveryDestination.ValidateAndOpen(request.DestinationRoot, request.Source.CanonicalPath,
                request.Policy.CreateDestinationIfMissing, request.TotalKnownBytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            diagnostics.Add("DESTINATION_UNAVAILABLE", SafeReason(exception));
            return Result(Fat32RecoveryBatchOutcome.DestinationUnavailable, [], 0);
        }

        Fat32SourceFingerprint before;
        try
        {
            before = await metadataProvider.CaptureAsync(request.Source.CanonicalPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return Result(Fat32RecoveryBatchOutcome.Canceled, [], 0); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            diagnostics.Add("SOURCE_REOPEN_FAILED", SafeReason(exception));
            return Result(Fat32RecoveryBatchOutcome.SourceChanged, [], 0);
        }
        if (!Matches(request.Source, before))
        {
            diagnostics.Add("SOURCE_CHANGED", "The regular image path, length, timestamp, or SHA-256 changed after scanning.");
            return Result(Fat32RecoveryBatchOutcome.SourceChanged, [], 0);
        }

        IReadOnlyRandomAccessSource opened;
        try { opened = await sourceFactory.OpenAsync(request.Source.CanonicalPath, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return Result(Fat32RecoveryBatchOutcome.Canceled, [], 0); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            diagnostics.Add("SOURCE_REOPEN_FAILED", SafeReason(exception));
            return Result(Fat32RecoveryBatchOutcome.SourceChanged, [], 0);
        }

        await using var source = opened;
        var deadline = DateTimeOffset.UtcNow + request.Policy.EffectiveMaximumDuration;
        var reader = new Fat32RecoveryReader(source, request.VolumeOffset, request.Geometry, request.Policy, deadline);
        if (source.Length != request.Source.Length)
        {
            diagnostics.Add("SOURCE_CHANGED", "The source length changed before FAT32 recovery.");
            return Result(Fat32RecoveryBatchOutcome.SourceChanged, [], 0);
        }

        Fat32ScanResult currentScan;
        try
        {
            currentScan = await scanner.ScanAsync(source,
                new(new(request.VolumeOffset), request.ScanBudget, request.ScanSessionId, request.Source.Sha256),
                null, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return Result(Fat32RecoveryBatchOutcome.Canceled, [], 0); }
        if (currentScan.Outcome != Fat32ScanOutcome.Completed || currentScan.Geometry is null ||
            !string.Equals(Fat32RecoveryFingerprint.Geometry(currentScan.Geometry), Fat32RecoveryFingerprint.Geometry(request.Geometry), StringComparison.Ordinal))
        {
            diagnostics.Add("SOURCE_CHANGED", "Fresh FAT32 geometry and metadata traversal no longer match the trusted completed scan.");
            return Result(Fat32RecoveryBatchOutcome.SourceChanged, [], 0);
        }

        Fat32OwnershipEvidence ownership;
        try { ownership = await Fat32ActiveOwnershipAnalyzer.BuildAsync(reader, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return Result(Fat32RecoveryBatchOutcome.Canceled, [], 0); }
        catch (Fat32RecoveryBudgetException exception)
        {
            diagnostics.Add("ACTIVE_OWNERSHIP_INCOMPLETE", exception.Message);
            ownership = new(false, new HashSet<uint>(), exception.Message);
        }
        catch (Fat32SourceReadException exception)
        {
            diagnostics.Add("ACTIVE_OWNERSHIP_INCOMPLETE", exception.Message);
            ownership = new(false, new HashSet<uint>(), exception.Message);
        }

        var currentCandidates = currentScan.Candidates.ToDictionary(item => item.CandidateId, StringComparer.Ordinal);
        var outcomes = new List<Fat32RecoveryFileResult>();
        var progressBytes = 0L;
        var verifiedBytes = 0L;
        var totalClusters = 0L;
        progress?.Report(Progress(null, null, 0, 0));
        foreach (var item in request.Items)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (DateTimeOffset.UtcNow > deadline)
            {
                diagnostics.Add("FAT32_RECOVERY_DURATION", "The recovery duration budget was reached between candidates.");
                break;
            }
            progress?.Report(Progress(item.Candidate.CandidateId, item.OutputName, outcomes.Count, progressBytes));
            Fat32RecoveryFileResult outcome;
            if (!item.IsEligible)
            {
                outcome = Failure(item, Fat32RecoveryOutcome.SkippedByPolicy, "POLICY_BLOCKED", item.Warning ?? "The candidate is blocked by policy.");
            }
            else if (!currentCandidates.TryGetValue(item.Candidate.CandidateId, out var current) || current.Provenance is null ||
                     current.Provenance.ParentDirectoryCluster != item.ParentDirectoryCluster ||
                     current.Provenance.DirectorySlotSourceOffset != item.DirectorySlotSourceOffset ||
                     !string.Equals(Fat32RecoveryFingerprint.Candidate(current), item.EvidenceFingerprint, StringComparison.Ordinal))
            {
                outcome = Failure(item, Fat32RecoveryOutcome.CandidateChanged, "CANDIDATE_CHANGED", "The exact deleted directory entry or its name evidence changed after scanning.");
            }
            else if (!ownership.IsComplete && current.LogicalFileSize > 0)
            {
                outcome = Failure(item, Fat32RecoveryOutcome.DamagedMetadata, "ACTIVE_OWNERSHIP_INCOMPLETE", ownership.Reason ?? "Active cluster ownership could not be proven complete.");
            }
            else
            {
                Fat32ResolvedPlan resolved;
                try
                {
                    resolved = await ResolvePlanAsync(reader, current, item, ownership, cancellationToken).ConfigureAwait(false);
                    totalClusters = checked(totalClusters + resolved.Clusters.Count);
                    if (totalClusters > request.Policy.MaximumTotalClusters) throw new Fat32RecoveryBudgetException("The maximum total recovery cluster budget was reached.");
                    outcome = resolved.Failure is not null
                        ? Failure(item, resolved.Failure.Value, resolved.Code!, resolved.Reason!)
                        : await RecoverItemAsync(reader, destination, request, item, resolved, progress, outcomes.Count, progressBytes, Count(outcomes), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    outcome = Failure(item, Fat32RecoveryOutcome.Canceled, "RECOVERY_CANCELED", "Recovery was canceled and in-flight output was cleaned.");
                }
                catch (Fat32RecoveryBudgetException exception)
                {
                    outcome = Failure(item, Fat32RecoveryOutcome.DamagedMetadata, "FAT32_RECOVERY_BUDGET", exception.Message);
                }
                catch (Fat32SourceReadException exception)
                {
                    outcome = Failure(item, Fat32RecoveryOutcome.SourceReadFailed, "SOURCE_READ_FAILED", exception.Message);
                }
            }
            outcomes.Add(outcome);
            progressBytes = checked(progressBytes + Math.Max(0, outcome.BytesWritten));
            if (IsVerified(outcome)) verifiedBytes = checked(verifiedBytes + outcome.BytesWritten);
            progress?.Report(Progress(item.Candidate.CandidateId, outcome.OutputPath is null ? item.OutputName : Path.GetFileName(outcome.OutputPath), outcomes.Count, progressBytes));
            if (outcome.Outcome is Fat32RecoveryOutcome.SourceChanged or Fat32RecoveryOutcome.Canceled) break;
        }

        if (cancellationToken.IsCancellationRequested || outcomes.LastOrDefault()?.Outcome == Fat32RecoveryOutcome.Canceled)
            return Result(Fat32RecoveryBatchOutcome.Canceled, outcomes, verifiedBytes);
        if (cancellationToken.IsCancellationRequested) return Result(Fat32RecoveryBatchOutcome.Canceled, outcomes, verifiedBytes);
        progress?.Report(Progress(null, null, outcomes.Count, progressBytes));
        if (cancellationToken.IsCancellationRequested) return Result(Fat32RecoveryBatchOutcome.Canceled, outcomes, verifiedBytes);
        var terminal = outcomes.Any(item => item.Outcome == Fat32RecoveryOutcome.SourceChanged) ? Fat32RecoveryBatchOutcome.SourceChanged
            : outcomes.Any(IsFailure) ? Fat32RecoveryBatchOutcome.CompletedWithFailures
            : outcomes.Any(item => item.Outcome is Fat32RecoveryOutcome.RecoveredContiguousHeuristic or Fat32RecoveryOutcome.RecoveredPreservedChainWithWarning or Fat32RecoveryOutcome.RecoveredDamagedWithWarning)
                ? Fat32RecoveryBatchOutcome.CompletedWithWarnings : Fat32RecoveryBatchOutcome.Completed;
        return Result(terminal, outcomes, verifiedBytes);

        Fat32RecoveryProgress Progress(string? id, string? name, int completed, long bytes)
        {
            var counts = Count(outcomes);
            return new(id, name, completed, request.Items.Count, bytes, request.TotalKnownBytes, counts.Success, counts.Warning, counts.Skipped, counts.Failed);
        }

        Fat32RecoveryBatchResult Result(Fat32RecoveryBatchOutcome outcome, IReadOnlyList<Fat32RecoveryFileResult> files, long bytes)
        {
            var counts = Count(files);
            return new(outcome, files.ToArray(), files.Count, request.Items.Count, bytes, counts.Success, counts.Warning, counts.Skipped, counts.Failed, diagnostics.Items, stopwatch.Elapsed);
        }
    }

    private static async Task<Fat32ResolvedPlan> ResolvePlanAsync(
        Fat32RecoveryReader reader, Fat32DeletedCandidate current, TrustedFat32RecoveryCandidate trusted,
        Fat32OwnershipEvidence ownership, CancellationToken cancellationToken)
    {
        if (current.Kind != Fat32CandidateKind.File) return Fat32ResolvedPlan.Fail(Fat32RecoveryOutcome.DamagedMetadata, "DELETED_DIRECTORY", "Deleted directories are never recovered or traversed.");
        if (current.LogicalFileSize != trusted.Candidate.LogicalFileSize || current.FirstCluster != trusted.Candidate.FirstCluster)
            return Fat32ResolvedPlan.Fail(Fat32RecoveryOutcome.CandidateChanged, "CANDIDATE_CHANGED", "The trusted first cluster or logical size changed.");
        if (current.LogicalFileSize == 0)
        {
            if (current.FirstCluster != 0 && !Fat32MetadataScanner.IsDataCluster(reader.Geometry, current.FirstCluster))
                return Fat32ResolvedPlan.Fail(Fat32RecoveryOutcome.DamagedMetadata, "INVALID_ZERO_LENGTH_CLUSTER", "The zero-length entry has an invalid first cluster.");
            return new(Fat32RecoveryMode.ZeroLength, [], null, null, null);
        }
        if (!Fat32MetadataScanner.IsDataCluster(reader.Geometry, current.FirstCluster))
            return Fat32ResolvedPlan.Fail(Fat32RecoveryOutcome.DamagedMetadata, "INVALID_FIRST_CLUSTER", "The first cluster is outside the data region.");
        var required = checked((int)(((ulong)current.LogicalFileSize + (uint)reader.Geometry.ClusterSize - 1) / (uint)reader.Geometry.ClusterSize));
        if (required <= 0 || required > reader.Policy.MaximumClustersPerCandidate)
            return Fat32ResolvedPlan.Fail(Fat32RecoveryOutcome.DamagedMetadata, "CLUSTER_BUDGET", "The candidate exceeds the per-file cluster budget.");
        if (current.Allocation != trusted.Candidate.Allocation)
            return Fat32ResolvedPlan.Fail(Fat32RecoveryOutcome.AllocationChanged, "ALLOCATION_CHANGED", "The FAT allocation classification changed after scanning.");

        if (current.Allocation == Fat32AllocationAssessment.PreservedAllocatedChain)
        {
            if (!reader.Policy.AllowPreservedFatChain) return Fat32ResolvedPlan.Fail(Fat32RecoveryOutcome.SkippedByPolicy, "PRESERVED_CHAIN_BLOCKED", "Preserved-chain recovery requires explicit opt-in.");
            var clusters = new List<uint>(required);
            var visited = new HashSet<uint>();
            var cluster = current.FirstCluster;
            for (var index = 0; index < required; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Fat32MetadataScanner.IsDataCluster(reader.Geometry, cluster) || !visited.Add(cluster))
                    return Fat32ResolvedPlan.Fail(Fat32RecoveryOutcome.DamagedMetadata, "INVALID_PRESERVED_CHAIN", "The preserved FAT chain is cyclic or out of range.");
                if (ownership.Clusters.Contains(cluster)) return Fat32ResolvedPlan.Fail(Fat32RecoveryOutcome.ActiveClusterConflict, "ACTIVE_CLUSTER_CONFLICT", "A preserved-chain cluster belongs to an active file or directory.");
                clusters.Add(cluster);
                var entry = await reader.ReadFatEntryAsync(cluster, cancellationToken).ConfigureAwait(false);
                if (entry.CopiesDisagree) return Fat32ResolvedPlan.Fail(Fat32RecoveryOutcome.AllocationChanged, "FAT_COPY_DISAGREEMENT", "Mirrored FAT copies disagree for the preserved chain.");
                if (index == required - 1)
                {
                    if (entry.Kind != Fat32FatEntryKind.EndOfChain) return Fat32ResolvedPlan.Fail(Fat32RecoveryOutcome.DamagedMetadata, "EXTRA_CHAIN_CLUSTERS", "The preserved chain has unexpected clusters beyond logical EOF.");
                }
                else
                {
                    if (entry.Kind != Fat32FatEntryKind.NextCluster) return Fat32ResolvedPlan.Fail(Fat32RecoveryOutcome.DamagedMetadata, "SHORT_PRESERVED_CHAIN", "The preserved chain is shorter than the logical file size.");
                    cluster = entry.Value;
                }
            }
            return new(Fat32RecoveryMode.PreservedChain, clusters, null, null, null);
        }

        var contiguous = new List<uint>(required);
        var sawAllocated = false;
        for (var index = 0; index < required; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cluster = checked(current.FirstCluster + (uint)index);
            if (!Fat32MetadataScanner.IsDataCluster(reader.Geometry, cluster)) return Fat32ResolvedPlan.Fail(Fat32RecoveryOutcome.DamagedMetadata, "FILE_EXCEEDS_VOLUME", "The contiguous span extends beyond the volume.");
            if (ownership.Clusters.Contains(cluster)) return Fat32ResolvedPlan.Fail(Fat32RecoveryOutcome.ActiveClusterConflict, "ACTIVE_CLUSTER_CONFLICT", "A candidate cluster belongs to an active file or directory.");
            var entry = await reader.ReadFatEntryAsync(cluster, cancellationToken).ConfigureAwait(false);
            if (entry.CopiesDisagree) return Fat32ResolvedPlan.Fail(Fat32RecoveryOutcome.AllocationChanged, "FAT_COPY_DISAGREEMENT", "Mirrored FAT copies disagree for the recovery span.");
            if (entry.Kind != Fat32FatEntryKind.Free)
            {
                if (entry.Kind is not (Fat32FatEntryKind.NextCluster or Fat32FatEntryKind.EndOfChain))
                    return Fat32ResolvedPlan.Fail(Fat32RecoveryOutcome.DamagedMetadata, "INVALID_FAT_ENTRY", "The recovery span contains a bad, reserved, or invalid FAT entry.");
                sawAllocated = true;
            }
            contiguous.Add(cluster);
        }
        if (!sawAllocated && current.Allocation == Fat32AllocationAssessment.PossiblyRecoverableContiguous)
            return new(Fat32RecoveryMode.ContiguousFree, contiguous, null, null, null);
        if (reader.Policy.AllowDeterministicDamagedContent && current.Allocation is Fat32AllocationAssessment.PartiallyOverwrittenOrReused or Fat32AllocationAssessment.OverwrittenOrReused)
            return new(Fat32RecoveryMode.DamagedContiguous, contiguous, null, null, null);
        return Fat32ResolvedPlan.Fail(Fat32RecoveryOutcome.AllocationChanged, "ALLOCATION_CHANGED", "The contiguous span is no longer wholly free under the selected policy.");
    }

    private async Task<Fat32RecoveryFileResult> RecoverItemAsync(
        Fat32RecoveryReader reader, RecoveryDestination destination, TrustedFat32RecoveryBatchRequest batch,
        TrustedFat32RecoveryCandidate item, Fat32ResolvedPlan plan, IProgress<Fat32RecoveryProgress>? progress,
        int completedBefore, long progressBefore, (int Success, int Warning, int Skipped, int Failed) priorCounts,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fallback = $"FAT32_Deleted_{item.ParentDirectoryCluster:X8}_{item.DirectorySlotSourceOffset:X16}.bin";
        var basePath = destination.PrepareFlatBasePath(item.OutputName, fallback);
        var partialPath = RecoveryFilePublication.CreatePartialPath(destination, basePath, Guid.NewGuid());
        string? publishedPath = null;
        var written = 0L;
        try
        {
            destination.RevalidateParent(partialPath);
            string sourceHash;
            string writerHash;
            await using (var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                batch.Policy.BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            using (var sourceHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            using (var writerHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                written = await StreamPlanAsync(reader, plan.Clusters, item.Candidate.LogicalFileSize, output, sourceHasher, writerHasher,
                    item, batch, progress, completedBefore, progressBefore, priorCounts, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                sourceHash = Convert.ToHexString(sourceHasher.GetHashAndReset());
                writerHash = Convert.ToHexString(writerHasher.GetHashAndReset());
            }
            if (written != item.Candidate.LogicalFileSize || !string.Equals(sourceHash, writerHash, StringComparison.Ordinal))
                return CleanupFailure(item, partialPath, null, written, Fat32RecoveryOutcome.VerificationFailed, "STREAM_VERIFICATION_FAILED", "The streamed byte count or write-path hash did not match.", batch.Policy.MaximumCleanupAttempts);

            using (var secondHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var second = await HashPlanAsync(reader, plan.Clusters, item.Candidate.LogicalFileSize, secondHasher, batch.Policy.BufferSize, cancellationToken).ConfigureAwait(false);
                var secondHash = Convert.ToHexString(secondHasher.GetHashAndReset());
                if (second != written || !string.Equals(secondHash, sourceHash, StringComparison.Ordinal))
                    return CleanupFailure(item, partialPath, null, written, Fat32RecoveryOutcome.SourceChanged, "SOURCE_CONTENT_CHANGED", "Selected source cluster content changed during extraction.", batch.Policy.MaximumCleanupAttempts);
            }
            var during = await metadataProvider.CaptureAsync(batch.Source.CanonicalPath, cancellationToken).ConfigureAwait(false);
            if (!Matches(batch.Source, during)) return CleanupFailure(item, partialPath, null, written, Fat32RecoveryOutcome.SourceChanged, "SOURCE_CHANGED", "The source fingerprint changed before publication.", batch.Policy.MaximumCleanupAttempts);
            cancellationToken.ThrowIfCancellationRequested();
            publishedPath = RecoveryFilePublication.PublishWithoutOverwrite(partialPath, basePath, destination, batch.Policy.MaximumCollisionAttempts);
            partialPath = string.Empty;
            var final = await RecoveryFilePublication.HashFileAsync(publishedPath, batch.Policy.BufferSize, item.Candidate.LogicalFileSize, cancellationToken).ConfigureAwait(false);
            var after = await metadataProvider.CaptureAsync(batch.Source.CanonicalPath, cancellationToken).ConfigureAwait(false);
            if (!Matches(batch.Source, after)) return CleanupFailure(item, null, publishedPath, written, Fat32RecoveryOutcome.SourceChanged, "SOURCE_CHANGED", "The source fingerprint changed before verification completed.", batch.Policy.MaximumCleanupAttempts);
            if (final.Bytes != written || !string.Equals(final.Sha256, sourceHash, StringComparison.Ordinal))
                return CleanupFailure(item, null, publishedPath, written, Fat32RecoveryOutcome.VerificationFailed, "DESTINATION_VERIFICATION_FAILED", "Published output length or SHA-256 did not match selected source bytes.", batch.Policy.MaximumCleanupAttempts);
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = plan.Mode switch
            {
                Fat32RecoveryMode.ZeroLength => Fat32RecoveryOutcome.RecoveredCopyVerified,
                Fat32RecoveryMode.ContiguousFree => Fat32RecoveryOutcome.RecoveredContiguousHeuristic,
                Fat32RecoveryMode.PreservedChain => Fat32RecoveryOutcome.RecoveredPreservedChainWithWarning,
                _ => Fat32RecoveryOutcome.RecoveredDamagedWithWarning,
            };
            var message = plan.Mode switch
            {
                Fat32RecoveryMode.ZeroLength => "The empty file was atomically published and its zero-byte SHA-256 was verified. Filename confidence is reported separately.",
                Fat32RecoveryMode.ContiguousFree => "Selected contiguous free clusters were copied and SHA-256 verified. FAT32 deletion often clears chain metadata; this heuristic does not prove the clusters are the original content.",
                Fat32RecoveryMode.PreservedChain => "The explicitly permitted preserved FAT chain was copied and SHA-256 verified; this does not prove the chain still belonged to the deleted entry.",
                _ => "The deterministic damaged-content span was copied and SHA-256 verified; content may be overwritten or unrelated and is not classified as intact.",
            };
            return new(item.Candidate.CandidateId, outcome, publishedPath, Path.GetFileName(publishedPath), item.Candidate.NameState,
                written, sourceHash, RecoveryVerificationState.Verified, message, []);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CleanupFailure(item, NullIfEmpty(partialPath), publishedPath, written, Fat32RecoveryOutcome.Canceled, "RECOVERY_CANCELED", "Recovery was canceled and unverified output was cleaned.", batch.Policy.MaximumCleanupAttempts);
        }
        catch (Fat32SourceReadException exception)
        {
            return CleanupFailure(item, NullIfEmpty(partialPath), publishedPath, written, Fat32RecoveryOutcome.SourceReadFailed, "SOURCE_READ_FAILED", exception.Message, batch.Policy.MaximumCleanupAttempts);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentOutOfRangeException or CryptographicException)
        {
            return CleanupFailure(item, NullIfEmpty(partialPath), publishedPath, written, Fat32RecoveryOutcome.DestinationFailed, "DESTINATION_FAILED", SafeReason(exception), batch.Policy.MaximumCleanupAttempts);
        }
    }

    private static async Task<long> StreamPlanAsync(Fat32RecoveryReader reader, IReadOnlyList<uint> clusters, long logicalSize,
        Stream output, IncrementalHash sourceHasher, IncrementalHash writerHasher, TrustedFat32RecoveryCandidate item,
        TrustedFat32RecoveryBatchRequest batch, IProgress<Fat32RecoveryProgress>? progress, int completedBefore,
        long progressBefore, (int Success, int Warning, int Skipped, int Failed) priorCounts, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(batch.Policy.BufferSize);
        var written = 0L;
        try
        {
            foreach (var cluster in clusters)
            {
                var within = 0;
                while (written < logicalSize && within < reader.Geometry.ClusterSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = (int)Math.Min(Math.Min(buffer.Length, reader.Geometry.ClusterSize - within), logicalSize - written);
                    await reader.ReadClusterBytesAsync(cluster, within, buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    sourceHasher.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    writerHasher.AppendData(buffer, 0, count);
                    within += count;
                    written = checked(written + count);
                    progress?.Report(new(item.Candidate.CandidateId, item.OutputName, completedBefore, batch.Items.Count,
                        checked(progressBefore + written), batch.TotalKnownBytes, priorCounts.Success, priorCounts.Warning, priorCounts.Skipped, priorCounts.Failed));
                }
            }
            return written;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static async Task<long> HashPlanAsync(Fat32RecoveryReader reader, IReadOnlyList<uint> clusters, long logicalSize,
        IncrementalHash hasher, int bufferSize, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        var read = 0L;
        try
        {
            foreach (var cluster in clusters)
            {
                var within = 0;
                while (read < logicalSize && within < reader.Geometry.ClusterSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = (int)Math.Min(Math.Min(buffer.Length, reader.Geometry.ClusterSize - within), logicalSize - read);
                    await reader.ReadClusterBytesAsync(cluster, within, buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    hasher.AppendData(buffer, 0, count);
                    within += count;
                    read = checked(read + count);
                }
            }
            return read;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static Fat32RecoveryFileResult CleanupFailure(TrustedFat32RecoveryCandidate item, string? partial, string? published,
        long bytes, Fat32RecoveryOutcome requested, string code, string reason, int attempts)
    {
        var cleaned = RecoveryFilePublication.CleanupExact(partial, published, attempts);
        var outcome = cleaned ? requested : Fat32RecoveryOutcome.CleanupFailed;
        var diagnostic = new Fat32RecoveryDiagnostic(cleaned ? code : "CLEANUP_FAILED", cleaned ? reason : "An exact session-owned partial or unverified output could not be removed.", item.Candidate.CandidateId);
        return new(item.Candidate.CandidateId, outcome, null, item.OutputName, item.Candidate.NameState, bytes, null,
            RecoveryVerificationState.NotPerformed, diagnostic.Reason, [diagnostic]);
    }

    private static Fat32RecoveryFileResult Failure(TrustedFat32RecoveryCandidate item, Fat32RecoveryOutcome outcome, string code, string reason) =>
        new(item.Candidate.CandidateId, outcome, null, item.OutputName, item.Candidate.NameState, 0, null,
            RecoveryVerificationState.NotPerformed, reason, [new(code, reason, item.Candidate.CandidateId)]);

    private static bool Matches(Fat32SourceFingerprint left, Fat32SourceFingerprint right) =>
        string.Equals(left.CanonicalPath, right.CanonicalPath, StringComparison.OrdinalIgnoreCase) && left.Length == right.Length &&
        left.LastWriteTimeUtcTicks == right.LastWriteTimeUtcTicks && string.Equals(left.Sha256, right.Sha256, StringComparison.Ordinal);
    private static bool IsVerified(Fat32RecoveryFileResult item) => item.Verification == RecoveryVerificationState.Verified;
    private static bool IsFailure(Fat32RecoveryFileResult item) => item.Outcome is not (
        Fat32RecoveryOutcome.RecoveredCopyVerified or Fat32RecoveryOutcome.RecoveredContiguousHeuristic or
        Fat32RecoveryOutcome.RecoveredPreservedChainWithWarning or Fat32RecoveryOutcome.RecoveredDamagedWithWarning or
        Fat32RecoveryOutcome.SkippedByPolicy);
    private static (int Success, int Warning, int Skipped, int Failed) Count(IReadOnlyList<Fat32RecoveryFileResult> files) => (
        files.Count(item => item.Outcome == Fat32RecoveryOutcome.RecoveredCopyVerified),
        files.Count(item => item.Outcome is Fat32RecoveryOutcome.RecoveredContiguousHeuristic or Fat32RecoveryOutcome.RecoveredPreservedChainWithWarning or Fat32RecoveryOutcome.RecoveredDamagedWithWarning),
        files.Count(item => item.Outcome == Fat32RecoveryOutcome.SkippedByPolicy),
        files.Count(IsFailure));
    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;
    private static string SafeReason(Exception exception) => $"{exception.GetType().Name}: {exception.Message}";
}

internal enum Fat32RecoveryMode { ZeroLength, ContiguousFree, PreservedChain, DamagedContiguous }
internal sealed record Fat32ResolvedPlan(Fat32RecoveryMode Mode, IReadOnlyList<uint> Clusters, Fat32RecoveryOutcome? Failure, string? Code, string? Reason)
{
    public static Fat32ResolvedPlan Fail(Fat32RecoveryOutcome outcome, string code, string reason) => new(default, [], outcome, code, reason);
}

internal sealed class Fat32RecoveryReader(IReadOnlyRandomAccessSource source, long volumeOffset, Fat32Geometry geometry, Fat32RecoveryPolicy policy, DateTimeOffset deadline)
{
    private long _reads;
    private int _fatEntries;
    public Fat32Geometry Geometry { get; } = geometry;
    public Fat32RecoveryPolicy Policy { get; } = policy;

    public async ValueTask<Fat32FatEntry> ReadFatEntryAsync(uint cluster, CancellationToken cancellationToken)
    {
        if (++_fatEntries > Policy.MaximumFatEntriesInspected) throw new Fat32RecoveryBudgetException("The FAT-entry inspection budget was reached.");
        uint selected = 0;
        var disagree = false;
        var copies = Geometry.FatMirroringEnabled ? Geometry.FatCount : (byte)1;
        for (byte index = 0; index < copies; index++)
        {
            var fat = Geometry.FatMirroringEnabled ? index : Geometry.ActiveFatIndex;
            var offset = checked(volumeOffset + checked((long)(Geometry.FirstFatSector + (ulong)fat * Geometry.FatSizeSectors) * Geometry.BytesPerSector) + checked((long)cluster * 4));
            var bytes = new byte[4];
            await ReadAsync(offset, bytes, cancellationToken).ConfigureAwait(false);
            var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes) & 0x0FFFFFFF;
            if (index == 0) selected = value; else disagree |= value != selected;
        }
        var kind = selected switch
        {
            0 => Fat32FatEntryKind.Free,
            0x0FFFFFF7 => Fat32FatEntryKind.Bad,
            >= 0x0FFFFFF8 and <= 0x0FFFFFFF => Fat32FatEntryKind.EndOfChain,
            >= 0x0FFFFFF0 and <= 0x0FFFFFF6 => Fat32FatEntryKind.Reserved,
            >= 2 and <= 0x0FFFFFEF when selected <= Geometry.MaximumDataCluster => Fat32FatEntryKind.NextCluster,
            _ => Fat32FatEntryKind.Invalid,
        };
        return new(selected, kind, disagree);
    }

    public ValueTask ReadClusterBytesAsync(uint cluster, int withinCluster, Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (withinCluster < 0 || destination.Length > Geometry.ClusterSize - withinCluster) throw new ArgumentOutOfRangeException(nameof(withinCluster));
        var offset = checked(Fat32ClusterMapper.GetSourceOffset(Geometry, volumeOffset, cluster, source.Length) + withinCluster);
        return ReadAsync(offset, destination, cancellationToken);
    }

    private async ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow > deadline) throw new Fat32RecoveryBudgetException("The FAT32 recovery duration budget was reached.");
        if (++_reads > Policy.MaximumSourceReads) throw new Fat32RecoveryBudgetException("The FAT32 source-read budget was reached.");
        try { await source.ReadExactlyAsync(offset, destination, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or EndOfStreamException or ArgumentOutOfRangeException)
        { throw new Fat32SourceReadException("A bounded FAT32 image read failed.", exception); }
    }
}

internal sealed record Fat32OwnershipEvidence(bool IsComplete, IReadOnlySet<uint> Clusters, string? Reason);

internal static class Fat32ActiveOwnershipAnalyzer
{
    public static async Task<Fat32OwnershipEvidence> BuildAsync(Fat32RecoveryReader reader, CancellationToken cancellationToken)
    {
        var owners = new Dictionary<uint, string>();
        var queue = new Queue<(uint Cluster, string Owner)>();
        var queued = new HashSet<uint>();
        queue.Enqueue((reader.Geometry.RootDirectoryCluster, "root"));
        queued.Add(reader.Geometry.RootDirectoryCluster);
        var activeFiles = 0;
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = queue.Dequeue();
            var chain = await FollowChainAsync(reader, directory.Cluster, directory.Owner, owners, cancellationToken).ConfigureAwait(false);
            foreach (var cluster in chain)
            {
                var bytes = new byte[reader.Geometry.ClusterSize];
                await reader.ReadClusterBytesAsync(cluster, 0, bytes, cancellationToken).ConfigureAwait(false);
                for (var offset = 0; offset < bytes.Length; offset += 32)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (bytes[offset] == 0) break;
                    if (bytes[offset] == 0xE5 || bytes[offset + 11] == 0x0F || !Fat32DirectoryEntry.TryParse(bytes.AsSpan(offset, 32), out var entry) || entry!.IsVolumeLabel) continue;
                    if (Fat32NameDecoder.IsDotEntry(entry.ShortNameBytes)) continue;
                    if (entry.IsDirectory)
                    {
                        if (!Fat32MetadataScanner.IsDataCluster(reader.Geometry, entry.FirstCluster)) return new(false, owners.Keys.ToHashSet(), "An active directory has an invalid first cluster.");
                        if (queued.Add(entry.FirstCluster)) queue.Enqueue((entry.FirstCluster, $"directory:{entry.FirstCluster:X8}"));
                    }
                    else if (entry.FileSize > 0)
                    {
                        if (++activeFiles > reader.Policy.MaximumActiveFilesAnalyzed) throw new Fat32RecoveryBudgetException("The active-file analysis budget was reached.");
                        if (!Fat32MetadataScanner.IsDataCluster(reader.Geometry, entry.FirstCluster)) return new(false, owners.Keys.ToHashSet(), "An active file has an invalid first cluster.");
                        var fileChain = await FollowChainAsync(reader, entry.FirstCluster, $"file:{entry.FirstCluster:X8}", owners, cancellationToken).ConfigureAwait(false);
                        var needed = ((ulong)entry.FileSize + (uint)reader.Geometry.ClusterSize - 1) / (uint)reader.Geometry.ClusterSize;
                        if ((ulong)fileChain.Count < needed) return new(false, owners.Keys.ToHashSet(), "An active file chain is shorter than its logical size.");
                    }
                }
            }
        }
        return new(true, owners.Keys.ToHashSet(), null);
    }

    private static async Task<IReadOnlyList<uint>> FollowChainAsync(Fat32RecoveryReader reader, uint first, string owner,
        Dictionary<uint, string> owners, CancellationToken cancellationToken)
    {
        var chain = new List<uint>();
        var visited = new HashSet<uint>();
        var current = first;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (chain.Count >= reader.Policy.MaximumClustersPerCandidate) throw new Fat32RecoveryBudgetException("An active FAT chain exceeded the bounded chain length.");
            if (!Fat32MetadataScanner.IsDataCluster(reader.Geometry, current) || !visited.Add(current)) throw new Fat32RecoveryBudgetException("An active FAT chain is cyclic or out of range.");
            if (owners.TryGetValue(current, out var existing) && !string.Equals(existing, owner, StringComparison.Ordinal))
                throw new Fat32RecoveryBudgetException("Active FAT chains are cross-linked; ownership is unsafe.");
            owners[current] = owner;
            if (owners.Count > reader.Policy.MaximumActiveOwnershipClusters) throw new Fat32RecoveryBudgetException("The active ownership cluster budget was reached.");
            chain.Add(current);
            var entry = await reader.ReadFatEntryAsync(current, cancellationToken).ConfigureAwait(false);
            if (entry.CopiesDisagree) throw new Fat32RecoveryBudgetException("Mirrored FAT copies disagree during active ownership analysis.");
            if (entry.Kind == Fat32FatEntryKind.EndOfChain) return chain;
            if (entry.Kind != Fat32FatEntryKind.NextCluster) throw new Fat32RecoveryBudgetException("An active FAT chain contains a free, bad, reserved, or invalid entry.");
            current = entry.Value;
        }
    }
}

internal sealed class Fat32RecoveryDiagnostics(int maximum)
{
    private readonly List<Fat32RecoveryDiagnostic> _items = [];
    public IReadOnlyList<Fat32RecoveryDiagnostic> Items => _items;
    public void Add(string code, string reason) { if (_items.Count < maximum) _items.Add(new(code, reason)); }
}

internal sealed class Fat32RecoveryBudgetException(string message) : Exception(message);
internal sealed class Fat32SourceReadException(string message, Exception inner) : IOException(message, inner);
