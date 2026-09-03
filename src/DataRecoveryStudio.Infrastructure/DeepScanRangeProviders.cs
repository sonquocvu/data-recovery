using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

public sealed class WholeImageDeepScanRangeProvider(long offset = 0, long? length = null) : IDeepScanRangeProvider
{
    public ValueTask<DeepScanRangeResult> GetRangesAsync(
        IReadOnlyRandomAccessSource source,
        DeepScanBudget budget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(budget);
        cancellationToken.ThrowIfCancellationRequested();
        var selectedLength = length ?? checked(source.Length - offset);
        DeepScanRangeValidation.Validate(source.Length, offset, selectedLength);
        return ValueTask.FromResult(new DeepScanRangeResult(
            [new(offset, selectedLength, $"whole:{offset:X16}:{selectedLength:X16}", DeepScanRangeKind.WholeImage)],
            true,
            []));
    }
}

public sealed class ExplicitDeepScanRangeProvider : IDeepScanRangeProvider
{
    private readonly DeepScanRange[] _ranges;

    public ExplicitDeepScanRangeProvider(IEnumerable<DeepScanRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        _ranges = ranges.ToArray();
    }

    public ValueTask<DeepScanRangeResult> GetRangesAsync(
        IReadOnlyRandomAccessSource source,
        DeepScanBudget budget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(budget);
        cancellationToken.ThrowIfCancellationRequested();
        if (_ranges.Length > budget.MaximumRanges)
        {
            throw new ArgumentOutOfRangeException(nameof(_ranges), "The explicit range count exceeds the Deep Scan budget.");
        }

        foreach (var range in _ranges)
        {
            DeepScanRangeValidation.Validate(source.Length, range.Offset, range.Length);
        }

        var ordered = _ranges.Where(range => range.Length > 0).OrderBy(range => range.Offset).ThenBy(range => range.Length).ToArray();
        var merged = new List<DeepScanRange>(ordered.Length);
        foreach (var range in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (merged.Count == 0 || range.Offset > merged[^1].End)
            {
                merged.Add(new(range.Offset, range.Length, $"explicit:{merged.Count:D5}", DeepScanRangeKind.Explicit));
                continue;
            }

            var previous = merged[^1];
            var end = Math.Max(previous.End, range.End);
            merged[^1] = previous with { Length = checked(end - previous.Offset) };
        }

        return ValueTask.FromResult(new DeepScanRangeResult(merged.ToArray(), true, []));
    }
}

public sealed class NtfsUnallocatedDeepScanRangeProvider(
    long volumeOffset = 0,
    StandardScanBudgets? ntfsBudgets = null) : IDeepScanRangeProvider
{
    public async ValueTask<DeepScanRangeResult> GetRangesAsync(
        IReadOnlyRandomAccessSource source,
        DeepScanBudget budget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(budget);
        budget.Validate();
        var scanBudgets = ntfsBudgets ?? new StandardScanBudgets();
        scanBudgets.Validate();
        var result = await new NtfsMetadataScanner().ScanAsync(
            source,
            new(new(volumeOffset), scanBudgets),
            null,
            cancellationToken).ConfigureAwait(false);
        var diagnostics = new List<DeepScanDiagnostic>();
        if (result.Geometry is null || result.AllocationBitmap is null)
        {
            diagnostics.Add(new("NTFS_BITMAP_INCOMPLETE", ScanDiagnosticSeverity.Warning, "Validated NTFS allocation bitmap metadata is unavailable."));
            return new([], false, diagnostics);
        }

        var geometry = result.Geometry;
        var metadata = result.AllocationBitmap;
        long volumeLength;
        long volumeEnd;
        long totalClusters;
        try
        {
            volumeLength = checked((long)geometry.TotalSectors * geometry.BytesPerSector);
            volumeEnd = checked(volumeOffset + volumeLength);
            totalClusters = volumeLength / geometry.ClusterSize;
        }
        catch (OverflowException)
        {
            diagnostics.Add(new("INVALID_RANGE", ScanDiagnosticSeverity.Error, "NTFS geometry overflowed image-relative range arithmetic."));
            return new([], false, diagnostics);
        }

        var bitmapReadBudget = new ScanReadBudget(Math.Min(scanBudgets.MaximumBytesRead, Math.Max(4096, budget.MaximumTotalValidationBytes)));
        if (!metadata.MetadataIsComplete || !NtfsVirtualStream.TryCreate(
                source,
                bitmapReadBudget,
                metadata.Runs,
                metadata.LogicalSize,
                volumeOffset,
                volumeEnd,
                geometry.ClusterSize,
                scanBudgets.MaximumDataRuns,
                false,
                out var stream,
                out _,
                out _) ||
            stream!.CoveredLength < stream.Length)
        {
            diagnostics.Add(new("NTFS_BITMAP_INCOMPLETE", ScanDiagnosticSeverity.Warning, "The NTFS bitmap stream is damaged, incomplete, or unsupported."));
            return new([], false, diagnostics);
        }

        var collector = new DiagnosticCollector(Math.Max(1, Math.Min(scanBudgets.MaximumDiagnostics, budget.MaximumDiagnostics)));
        var bitmap = new NtfsAllocationBitmap(stream, scanBudgets.MaximumBitmapCacheBytes, collector);
        long bitmapCoverage;
        try
        {
            bitmapCoverage = checked(Math.Min(metadata.LogicalSize, metadata.InitializedSize) * 8);
        }
        catch (OverflowException)
        {
            diagnostics.Add(new("NTFS_BITMAP_INCOMPLETE", ScanDiagnosticSeverity.Warning, "The NTFS bitmap coverage overflowed bounded arithmetic."));
            return new([], false, diagnostics);
        }

        var coveredBits = Math.Min(totalClusters, bitmapCoverage);
        var maximumClustersByByteBudget = Math.Max(1, budget.MaximumSourceBytesScanned / geometry.ClusterSize);
        var clustersToInspect = Math.Min(coveredBits, maximumClustersByByteBudget);
        var ranges = new List<DeepScanRange>();
        long? freeStart = null;
        long selectedBytes = 0;
        var complete = clustersToInspect == totalClusters && coveredBits >= totalClusters && result.Outcome == StandardScanOutcome.Completed;

        for (var cluster = 0L; cluster < clustersToInspect; cluster++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NtfsAllocationState state;
            try
            {
                state = IsKnownMetadataCluster(cluster)
                    ? NtfsAllocationState.EntirelyAllocated
                    : await bitmap.QueryRangeAsync(cluster, 1, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentOutOfRangeException or ScanBudgetExceededException)
            {
                diagnostics.Add(new("NTFS_BITMAP_INCOMPLETE", ScanDiagnosticSeverity.Warning, "The bounded NTFS bitmap read could not be completed."));
                complete = false;
                break;
            }
            if (state == NtfsAllocationState.EntirelyFree)
            {
                freeStart ??= cluster;
                continue;
            }

            if (freeStart is not null && !AddRange(freeStart.Value, cluster))
            {
                complete = false;
                break;
            }

            freeStart = null;
        }

        if (freeStart is not null && ranges.Count < budget.MaximumRanges)
        {
            if (!AddRange(freeStart.Value, clustersToInspect)) complete = false;
        }

        if (!complete)
        {
            diagnostics.Add(new("NTFS_BITMAP_INCOMPLETE", ScanDiagnosticSeverity.Warning, "Only the validated, budget-bounded prefix of NTFS unallocated space was exposed."));
        }

        return new(ranges.ToArray(), complete, diagnostics.Take(budget.MaximumDiagnostics).ToArray());

        bool AddRange(long firstCluster, long exclusiveEndCluster)
        {
            if (ranges.Count >= budget.MaximumRanges)
            {
                return false;
            }

            var rangeBytes = checked((exclusiveEndCluster - firstCluster) * geometry.ClusterSize);
            var remaining = budget.MaximumSourceBytesScanned - selectedBytes;
            var clusterAlignedRemaining = remaining / geometry.ClusterSize * geometry.ClusterSize;
            if (clusterAlignedRemaining <= 0)
            {
                return false;
            }

            rangeBytes = Math.Min(rangeBytes, clusterAlignedRemaining);
            if (rangeBytes <= 0)
            {
                return false;
            }

            var imageOffset = checked(volumeOffset + checked(firstCluster * geometry.ClusterSize));
            ranges.Add(new(imageOffset, rangeBytes, $"ntfs-free:{firstCluster:X16}", DeepScanRangeKind.NtfsUnallocated));
            selectedBytes = checked(selectedBytes + rangeBytes);
            return rangeBytes == checked((exclusiveEndCluster - firstCluster) * geometry.ClusterSize);
        }

        bool IsKnownMetadataCluster(long cluster)
        {
            if (cluster == 0 || (ulong)cluster == geometry.MftLogicalClusterNumber || (ulong)cluster == geometry.MftMirrorLogicalClusterNumber)
            {
                return true;
            }

            return metadata.Runs.Any(run => !run.IsSparse && run.LogicalCluster is not null && run.ClusterCount > 0 &&
                cluster >= run.LogicalCluster.Value && cluster - run.LogicalCluster.Value < run.ClusterCount);
        }
    }
}

internal static class DeepScanRangeValidation
{
    public static void Validate(long sourceLength, long offset, long length)
    {
        if (offset < 0 || length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Deep Scan ranges must be non-negative.");
        }

        long end;
        try
        {
            end = checked(offset + length);
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The Deep Scan range overflows.");
        }

        if (end > sourceLength)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The Deep Scan range exceeds the source.");
        }
    }
}
