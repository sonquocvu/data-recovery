using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

public sealed class DeepScanEngine : IDeepScanEngine, IFileCarver
{
    private readonly IFileSignatureRegistry _registry;

    public DeepScanEngine(IFileSignatureRegistry? registry = null) =>
        _registry = registry ?? FileSignatureRegistry.CreateDefault();

    public async Task<DeepScanResult> ScanAsync(
        IReadOnlyRandomAccessSource source,
        DeepScanRequest request,
        IProgress<DeepScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.RangeProvider);
        var budget = request.EffectiveBudget;
        budget.Validate();
        var overlap = Math.Max(0, _registry.LongestSignatureLength - 1);
        var bufferLength = checked(budget.ChunkSize + overlap);
        var stopwatch = Stopwatch.StartNew();
        var deadline = DateTimeOffset.UtcNow + budget.EffectiveMaximumScanDuration;
        var diagnostics = new BoundedDeepScanDiagnostics(budget.MaximumDiagnostics);
        var candidates = new List<DeepScanCandidate>();
        var hitKeys = new HashSet<string>(StringComparer.Ordinal);
        var signatureHits = 0;
        var candidatesValidated = 0;
        var validationBytes = 0L;
        var bytesExamined = 0L;
        var totalEligible = 0L;
        IReadOnlyList<DeepScanRange> scannedRanges = [];
        var isLimited = false;
        string? partialReason = null;
        var reporter = new ProgressReporter(progress, budget.EffectiveMinimumProgressInterval, stopwatch);
        DeepScanRangeResult rangeResult;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            reporter.Report(DeepScanPhase.PreparingRanges, 0, 0, 0, 0, 0, null, false, true);
            rangeResult = await request.RangeProvider.GetRangesAsync(source, budget, cancellationToken).ConfigureAwait(false);
            scannedRanges = rangeResult.Ranges
                .OrderBy(item => item.Offset)
                .ThenBy(item => item.Identity, StringComparer.Ordinal)
                .ToArray();
            if (rangeResult.Ranges.Count > budget.MaximumRanges)
            {
                throw new ArgumentOutOfRangeException(nameof(request), "The range provider exceeded the configured range count.");
            }

            foreach (var diagnostic in rangeResult.Diagnostics) diagnostics.Add(diagnostic);
            foreach (var range in rangeResult.Ranges)
            {
                DeepScanRangeValidation.Validate(source.Length, range.Offset, range.Length);
                totalEligible = checked(totalEligible + range.Length);
            }
        }
        catch (OperationCanceledException)
        {
            diagnostics.Add(new("CANCELLATION", ScanDiagnosticSeverity.Information, "Deep Scan was canceled before range preparation completed."));
            return Finish(DeepScanOutcome.Canceled, "Cancellation", DeepScanPhase.Canceled);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException or InvalidDataException)
        {
            diagnostics.Add(new("INVALID_RANGE", ScanDiagnosticSeverity.Error, exception.Message));
            return Finish(DeepScanOutcome.InvalidRequest, "InvalidRange", DeepScanPhase.Partial);
        }

        if (!rangeResult.IsComplete)
        {
            isLimited = true;
            partialReason = "RangeProviderPartial";
        }

        var buffer = ArrayPool<byte>.Shared.Rent(bufferLength);
        try
        {
            foreach (var range in rangeResult.Ranges.OrderBy(item => item.Offset).ThenBy(item => item.Identity, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var position = range.Offset;
                var carry = 0;
                while (position < range.End)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (DateTimeOffset.UtcNow > deadline)
                    {
                        Limit("SCAN_DURATION_REACHED", "MaximumScanDuration", "The scan duration budget was reached.");
                        goto ScanningFinished;
                    }

                    var remainingGlobal = budget.MaximumSourceBytesScanned - bytesExamined;
                    if (remainingGlobal <= 0)
                    {
                        Limit("SCAN_BYTE_BUDGET_REACHED", "MaximumSourceBytesScanned", "The source-byte scan budget was reached.");
                        goto ScanningFinished;
                    }

                    var count = checked((int)Math.Min(budget.ChunkSize, Math.Min(range.End - position, remainingGlobal)));
                    try
                    {
                        await source.ReadExactlyAsync(position, buffer.AsMemory(carry, count), cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception) when (exception is EndOfStreamException or IOException or ArgumentOutOfRangeException)
                    {
                        diagnostics.Add(new("SOURCE_SHORT_READ", ScanDiagnosticSeverity.Error, "The source did not satisfy an exact bounded scan read.", position));
                        isLimited = true;
                        partialReason = "SourceShortRead";
                        goto ScanningFinished;
                    }

                    bytesExamined = checked(bytesExamined + count);

                    var combined = carry + count;
                    var baseOffset = checked(position - carry);
                    for (var index = 0; index < combined; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        foreach (var descriptor in _registry.Descriptors)
                        {
                            foreach (var signature in descriptor.Signatures)
                            {
                                var pattern = signature.Bytes;
                                if (index > combined - pattern.Length || buffer[index] != pattern.Span[0] ||
                                    !buffer.AsMemory(index, pattern.Length).Span.SequenceEqual(pattern.Span))
                                {
                                    continue;
                                }

                                var signatureOffset = checked(baseOffset + index);
                                var candidateOffset = checked(signatureOffset - signature.CandidateRelativeOffset);
                                if (candidateOffset < range.Offset || signatureOffset > range.End - pattern.Length) continue;
                                var hitKey = string.Create(CultureInfo.InvariantCulture, $"{descriptor.FormatId}:{candidateOffset}");
                                if (!hitKeys.Add(hitKey)) continue;
                                if (signatureHits >= budget.MaximumSignatureHits)
                                {
                                    Limit("SIGNATURE_BUDGET_REACHED", "MaximumSignatureHits", "The signature-hit budget was reached.");
                                    goto ScanningFinished;
                                }

                                signatureHits++;
                                if (candidatesValidated >= budget.MaximumCandidatesValidated)
                                {
                                    Limit("CANDIDATE_BUDGET_REACHED", "MaximumCandidatesValidated", "The candidate-validation budget was reached.");
                                    goto ScanningFinished;
                                }

                                var globalValidationRemaining = budget.MaximumTotalValidationBytes - validationBytes;
                                if (globalValidationRemaining <= 0)
                                {
                                    Limit("VALIDATOR_BUDGET_REACHED", "MaximumTotalValidationBytes", "The total candidate-validation byte budget was reached.");
                                    goto ScanningFinished;
                                }

                                candidatesValidated++;
                                reporter.Report(DeepScanPhase.Validating, totalEligible, bytesExamined, signatureHits, candidatesValidated, candidates.Count, descriptor.FormatId, isLimited);
                                var maximumAvailable = checked(range.End - candidateOffset);
                                var maximumInspect = Math.Min(
                                    Math.Min(budget.MaximumBytesInspectedPerCandidate, descriptor.MaximumValidationSize),
                                    globalValidationRemaining);
                                CandidateValidationResult validation;
                                try
                                {
                                    validation = await descriptor.Validator.ValidateAsync(
                                        source,
                                        new(
                                            candidateOffset,
                                            maximumAvailable,
                                            maximumInspect,
                                            budget.MaximumValidationReadsPerCandidate,
                                            budget.MaximumStructuralElementsPerCandidate,
                                            budget.MaximumNestedElementsPerCandidate,
                                            Math.Min(budget.MaximumTerminatorSearchDistance, maximumInspect),
                                            budget.MaximumMetadataStringLength,
                                            deadline),
                                        cancellationToken).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException)
                                {
                                    throw;
                                }
                                catch (Exception exception) when (exception is IOException or EndOfStreamException or ArgumentOutOfRangeException or OverflowException)
                                {
                                    validation = ValidationResults.Invalid("SOURCE_SHORT_READ", 0, "Candidate validation read failed");
                                }

                                validationBytes = checked(validationBytes + validation.BytesInspected);
                                foreach (var code in validation.DiagnosticCodes)
                                {
                                    diagnostics.Add(new(code, ScanDiagnosticSeverity.Warning, "Candidate validation reported a bounded structural issue.", candidateOffset, descriptor.FormatId));
                                }

                                var accepted = validation.ValidationState != DeepScanValidationState.StructurallyInvalid || request.RetainInvalidCandidates;
                                if (!accepted) continue;
                                if (candidates.Count >= budget.MaximumCandidatesReturned)
                                {
                                    Limit("CANDIDATE_BUDGET_REACHED", "MaximumCandidatesReturned", "The returned-candidate budget was reached.");
                                    goto ScanningFinished;
                                }

                                candidates.Add(CreateCandidate(descriptor, validation, candidateOffset, range));
                            }
                        }
                    }

                    position = checked(position + count);
                    reporter.Report(DeepScanPhase.Scanning, totalEligible, bytesExamined, signatureHits, candidatesValidated, candidates.Count, null, isLimited);
                    carry = Math.Min(overlap, combined);
                    buffer.AsSpan(combined - carry, carry).CopyTo(buffer);
                }
            }

        ScanningFinished:
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            diagnostics.Add(new("CANCELLATION", ScanDiagnosticSeverity.Information, "Deep Scan was canceled; already accepted candidates are returned as partial results."));
            return Finish(DeepScanOutcome.Canceled, "Cancellation", DeepScanPhase.Canceled);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = NormalizeOverlaps(candidates, budget, diagnostics, out var overlapLimited);
            if (overlapLimited)
            {
                isLimited = true;
                partialReason ??= "MaximumOverlapRecords";
            }

            candidates.Clear();
            candidates.AddRange(normalized);
            cancellationToken.ThrowIfCancellationRequested();
            return Finish(isLimited ? DeepScanOutcome.Partial : DeepScanOutcome.Completed, partialReason, isLimited ? DeepScanPhase.Partial : DeepScanPhase.Completed);
        }
        catch (OperationCanceledException)
        {
            diagnostics.Add(new("CANCELLATION", ScanDiagnosticSeverity.Information, "Deep Scan cancellation won before final completion publication."));
            return Finish(DeepScanOutcome.Canceled, "Cancellation", DeepScanPhase.Canceled);
        }

        void Limit(string code, string reason, string message)
        {
            isLimited = true;
            partialReason ??= reason;
            diagnostics.Add(new(code, ScanDiagnosticSeverity.Warning, message));
        }

        DeepScanResult Finish(DeepScanOutcome outcome, string? reason, DeepScanPhase phase)
        {
            if (outcome is not DeepScanOutcome.Canceled && cancellationToken.IsCancellationRequested)
            {
                outcome = DeepScanOutcome.Canceled;
                reason = "Cancellation";
                phase = DeepScanPhase.Canceled;
            }

            stopwatch.Stop();
            var ordered = candidates
                .OrderBy(item => item.StartOffset)
                .ThenByDescending(item => _registry.Descriptors.First(descriptor => descriptor.FormatId == item.FormatId).Priority)
                .ThenBy(item => item.CandidateId, StringComparer.Ordinal)
                .ToArray();
            reporter.Report(phase, totalEligible, bytesExamined, signatureHits, candidatesValidated, ordered.Length, null, isLimited, true);
            return new(
                outcome,
                ordered,
                diagnostics.Items,
                new(bytesExamined, validationBytes, signatureHits, candidatesValidated, ordered.Length, bufferLength, stopwatch.Elapsed),
                isLimited,
                reason,
                scannedRanges);
        }
    }

    private static DeepScanCandidate CreateCandidate(
        FileSignatureDescriptor descriptor,
        CandidateValidationResult validation,
        long candidateOffset,
        DeepScanRange range)
    {
        var fingerprint = string.Join('|',
            descriptor.FormatId,
            candidateOffset.ToString(CultureInfo.InvariantCulture),
            validation.LogicalLength?.ToString(CultureInfo.InvariantCulture) ?? "?",
            descriptor.ValidatorVersion,
            string.Join(';', validation.Evidence));
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)))[..32];
        return new(
            id,
            descriptor.FormatId,
            descriptor.SuggestedExtension,
            candidateOffset,
            validation.LogicalLength,
            validation.LengthConfidence,
            validation.Confidence,
            validation.Completeness,
            validation.ValidationState,
            validation.Evidence.ToArray(),
            range.Identity,
            range.Kind,
            validation.DiagnosticCodes.ToArray(),
            validation.ValidationState == DeepScanValidationState.StructurallyValidated &&
            validation.Completeness == DeepScanCompleteness.Complete &&
            validation.Confidence == DeepScanConfidence.High &&
            validation.LogicalLength is not null,
            false,
            descriptor.ValidatorVersion,
            DeepScanFingerprint.ComputeEvidence(validation.Evidence));
    }

    private IReadOnlyList<DeepScanCandidate> NormalizeOverlaps(
        IReadOnlyList<DeepScanCandidate> source,
        DeepScanBudget budget,
        BoundedDeepScanDiagnostics diagnostics,
        out bool limited)
    {
        limited = false;
        var priority = _registry.Descriptors.ToDictionary(item => item.FormatId, item => item.Priority, StringComparer.Ordinal);
        var exact = source
            .GroupBy(item => (item.StartOffset, item.LogicalLength))
            .Select(group => group.Key.LogicalLength is null || group.Count() == 1
                ? group.ToArray()
                : new[] { group.OrderByDescending(item => item.ValidationConfidence).ThenByDescending(item => priority[item.FormatId]).ThenBy(item => item.CandidateId, StringComparer.Ordinal).First() })
            .SelectMany(group => group)
            .OrderBy(item => item.StartOffset)
            .ThenByDescending(item => priority[item.FormatId])
            .ThenBy(item => item.CandidateId, StringComparer.Ordinal)
            .ToList();
        var suppressed = new HashSet<string>(StringComparer.Ordinal);
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        var comparisons = 0;
        for (var index = 0; index < exact.Count; index++)
        {
            var current = exact[index];
            if (current.LogicalLength is null)
            {
                for (var otherIndex = index - 1; otherIndex >= 0; otherIndex--)
                {
                    var other = exact[otherIndex];
                    if (other.StartOffset > current.StartOffset || other.LogicalLength is null) continue;
                    var otherEnd = checked(other.StartOffset + other.LogicalLength.Value);
                    if (otherEnd <= current.StartOffset) continue;
                    if (++comparisons > budget.MaximumOverlapRecords)
                    {
                        limited = true;
                        goto Done;
                    }

                    if (other.FormatId == current.FormatId && other.ValidationState == DeepScanValidationState.StructurallyValidated)
                    {
                        suppressed.Add(current.CandidateId);
                        break;
                    }
                }

                continue;
            }
            var currentEnd = checked(current.StartOffset + current.LogicalLength.Value);
            for (var otherIndex = index - 1; otherIndex >= 0; otherIndex--)
            {
                var other = exact[otherIndex];
                if (other.LogicalLength is null) continue;
                var otherEnd = checked(other.StartOffset + other.LogicalLength.Value);
                if (otherEnd <= current.StartOffset) break;
                if (++comparisons > budget.MaximumOverlapRecords)
                {
                    limited = true;
                    diagnostics.Add(new("CANDIDATE_OVERLAP", ScanDiagnosticSeverity.Warning, "Overlap comparison budget reached; remaining candidates retain deterministic order without further suppression."));
                    goto Done;
                }

                if (other.FormatId == current.FormatId && currentEnd <= otherEnd && other.ValidationConfidence >= current.ValidationConfidence)
                {
                    suppressed.Add(current.CandidateId);
                    continue;
                }

                warnings.Add(other.CandidateId);
                warnings.Add(current.CandidateId);
            }
        }

    Done:
        if (warnings.Count > 0)
        {
            diagnostics.Add(new("CANDIDATE_OVERLAP", ScanDiagnosticSeverity.Warning, "One or more validated candidates overlap; distinct or embedded formats remain visible."));
        }

        return exact.Where(item => !suppressed.Contains(item.CandidateId))
            .Select(item => warnings.Contains(item.CandidateId)
                ? item with { HasOverlapWarning = true, DiagnosticCodes = item.DiagnosticCodes.Append("CANDIDATE_OVERLAP").Distinct(StringComparer.Ordinal).ToArray() }
                : item)
            .ToArray();
    }

    private sealed class ProgressReporter(
        IProgress<DeepScanProgress>? progress,
        TimeSpan minimumInterval,
        Stopwatch stopwatch)
    {
        private TimeSpan _last;

        public void Report(
            DeepScanPhase phase,
            long total,
            long examined,
            int hits,
            int validated,
            int accepted,
            string? format,
            bool limited,
            bool force = false)
        {
            if (progress is null || (!force && stopwatch.Elapsed - _last < minimumInterval)) return;
            _last = stopwatch.Elapsed;
            progress.Report(new(phase, total, examined, hits, validated, accepted, format, stopwatch.Elapsed, limited));
        }
    }
}

internal sealed class BoundedDeepScanDiagnostics(int maximum)
{
    private readonly List<DeepScanDiagnostic> _items = [];
    public IReadOnlyList<DeepScanDiagnostic> Items => _items.ToArray();

    public void Add(DeepScanDiagnostic diagnostic)
    {
        if (_items.Count < maximum) _items.Add(diagnostic);
    }
}
