using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

public sealed class DeepScanCandidateRevalidator : IDeepScanCandidateRevalidator
{
    private readonly IReadOnlyDictionary<string, FileSignatureDescriptor> _descriptors;

    public DeepScanCandidateRevalidator(IFileSignatureRegistry? registry = null)
    {
        var trustedRegistry = registry ?? FileSignatureRegistry.CreateDefault();
        _descriptors = trustedRegistry.Descriptors.ToDictionary(item => item.FormatId, StringComparer.Ordinal);
    }

    public async ValueTask<DeepScanCandidateRevalidationResult> RevalidateAsync(
        IReadOnlyRandomAccessSource source,
        TrustedDeepScanCandidate candidate,
        DeepScanRecoveryPolicy policy,
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(policy);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_descriptors.TryGetValue(candidate.FormatId, out var descriptor) ||
            !string.Equals(descriptor.ValidatorVersion, candidate.ValidatorVersion, StringComparison.Ordinal))
        {
            return Failure("UNSUPPORTED_CANDIDATE", "The trusted format or validator version is not supported by the active registry.");
        }

        long rangeEnd;
        long candidateEnd;
        try
        {
            rangeEnd = checked(candidate.SourceRangeOffset + candidate.SourceRangeLength);
            candidateEnd = checked(candidate.CandidateOffset + candidate.CandidateLength);
        }
        catch (OverflowException)
        {
            return Failure("CANDIDATE_RANGE_INVALID", "Trusted candidate range arithmetic overflowed.");
        }

        if (candidate.SourceRangeOffset < 0 || candidate.SourceRangeLength < 0 || candidate.CandidateOffset < candidate.SourceRangeOffset ||
            candidate.CandidateLength < 0 || candidateEnd > rangeEnd || rangeEnd > source.Length)
        {
            return Failure("CANDIDATE_RANGE_INVALID", "The trusted candidate extent is outside its original scan range or source.");
        }

        var maximumAvailable = checked(rangeEnd - candidate.CandidateOffset);
        var maximumInspect = Math.Min(policy.MaximumBytesPerCandidate, descriptor.MaximumValidationSize);
        var validation = await descriptor.Validator.ValidateAsync(
            source,
            new(
                candidate.CandidateOffset,
                maximumAvailable,
                maximumInspect,
                checked((int)Math.Min(int.MaxValue, policy.MaximumSourceReads)),
                100_000,
                64,
                Math.Min(maximumAvailable, maximumInspect),
                4_096,
                deadlineUtc),
            cancellationToken).ConfigureAwait(false);
        var matches = validation.LogicalLength == candidate.CandidateLength &&
            validation.LengthConfidence == candidate.LengthConfidence &&
            validation.Confidence == candidate.ValidationConfidence &&
            validation.Completeness == candidate.Completeness &&
            validation.ValidationState == candidate.ValidationState &&
            string.Equals(DeepScanFingerprint.ComputeEvidence(validation.Evidence), candidate.DetectionEvidenceFingerprint, StringComparison.Ordinal);
        return matches
            ? new(true, validation, null, null)
            : new(false, validation, "CANDIDATE_CHANGED", "Current bounded structural validation no longer matches scan provenance.");

        DeepScanCandidateRevalidationResult Failure(string code, string reason) => new(
            false,
            new(DeepScanValidationState.StructurallyInvalid, DeepScanCompleteness.Corrupt, DeepScanConfidence.Low, null, DeepScanConfidence.Low, [], [code], 0),
            code,
            reason);
    }
}
