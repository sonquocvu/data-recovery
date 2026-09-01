using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

internal sealed class ScanReadBudget(long maximumBytes)
{
    public long BytesRead { get; private set; }

    public async ValueTask ReadExactlyAsync(
        IReadOnlyRandomAccessSource source,
        long offset,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (BytesRead > maximumBytes - destination.Length)
        {
            throw new ScanBudgetExceededException("MaximumBytesRead");
        }

        await source.ReadExactlyAsync(offset, destination, cancellationToken).ConfigureAwait(false);
        BytesRead = checked(BytesRead + destination.Length);
    }
}

internal sealed class ScanBudgetExceededException(string budgetName) : Exception
{
    public string BudgetName { get; } = budgetName;
}

internal sealed class NtfsVirtualStream
{
    private readonly IReadOnlyRandomAccessSource _source;
    private readonly ScanReadBudget _budget;
    private readonly IReadOnlyList<NtfsDataRun> _runs;
    private readonly long _volumeOffset;
    private readonly int _clusterSize;

    private NtfsVirtualStream(
        IReadOnlyRandomAccessSource source,
        ScanReadBudget budget,
        IReadOnlyList<NtfsDataRun> runs,
        long logicalSize,
        long coveredSize,
        long volumeOffset,
        int clusterSize)
    {
        _source = source;
        _budget = budget;
        _runs = runs;
        Length = logicalSize;
        CoveredLength = coveredSize;
        _volumeOffset = volumeOffset;
        _clusterSize = clusterSize;
    }

    public long Length { get; }

    public long CoveredLength { get; }

    public int ExtentCount => _runs.Count;

    public static bool TryCreate(
        IReadOnlyRandomAccessSource source,
        ScanReadBudget budget,
        IEnumerable<NtfsDataRun> runs,
        long logicalSize,
        long volumeOffset,
        long volumeEnd,
        int clusterSize,
        int maximumExtents,
        bool allowSparse,
        out NtfsVirtualStream? stream,
        out string code,
        out string reason,
        string sparseDiagnosticCode = "NTFS_METADATA_SPARSE_EXTENT")
    {
        stream = null;
        code = "NTFS_VIRTUAL_STREAM_INVALID";
        reason = "The virtual stream layout is invalid.";
        if (logicalSize < 0 || clusterSize <= 0 || volumeOffset < 0 || volumeEnd < volumeOffset)
        {
            return false;
        }

        var ordered = runs.OrderBy(run => run.VirtualCluster).ToArray();
        if (ordered.Length == 0 || ordered.Length > maximumExtents)
        {
            code = ordered.Length > maximumExtents ? "NTFS_EXTENT_LIMIT_REACHED" : code;
            reason = ordered.Length > maximumExtents ? "The virtual stream extent limit was reached." : reason;
            return false;
        }

        var expectedVcn = 0L;
        var physicalRanges = new List<(long Start, long End)>();
        foreach (var run in ordered)
        {
            if (run.ClusterCount <= 0 || run.VirtualCluster != expectedVcn)
            {
                code = run.VirtualCluster < expectedVcn ? "NTFS_VCN_EXTENT_OVERLAP" : "NTFS_VCN_EXTENT_GAP";
                reason = run.VirtualCluster < expectedVcn ? "Virtual-cluster extents overlap." : "The virtual stream contains an unexplained VCN gap.";
                return false;
            }

            if (run.IsSparse || run.LogicalCluster is null)
            {
                if (!allowSparse)
                {
                    code = sparseDiagnosticCode;
                    reason = "A sparse extent is not valid for this metadata stream.";
                    return false;
                }
            }
            else
            {
                try
                {
                    var physicalStart = checked(volumeOffset + checked(run.LogicalCluster.Value * clusterSize));
                    var physicalLength = checked(run.ClusterCount * clusterSize);
                    var physicalEnd = checked(physicalStart + physicalLength);
                    if (physicalStart < volumeOffset || physicalEnd > volumeEnd || physicalEnd > source.Length)
                    {
                        code = "NTFS_EXTENT_OUTSIDE_VOLUME";
                        reason = "A physical extent lies outside the declared NTFS volume or backing image.";
                        return false;
                    }

                    if (physicalRanges.Any(range => physicalStart < range.End && physicalEnd > range.Start))
                    {
                        code = "NTFS_PHYSICAL_EXTENT_OVERLAP";
                        reason = "Physical stream extents overlap or contradict one another.";
                        return false;
                    }

                    physicalRanges.Add((physicalStart, physicalEnd));
                }
                catch (OverflowException)
                {
                    code = "NTFS_EXTENT_OVERFLOW";
                    reason = "A physical extent overflowed bounded arithmetic.";
                    return false;
                }
            }

            try
            {
                expectedVcn = checked(expectedVcn + run.ClusterCount);
            }
            catch (OverflowException)
            {
                code = "NTFS_EXTENT_OVERFLOW";
                reason = "A virtual extent overflowed bounded arithmetic.";
                return false;
            }
        }

        long coveredBytes;
        try
        {
            coveredBytes = checked(expectedVcn * clusterSize);
        }
        catch (OverflowException)
        {
            code = "NTFS_EXTENT_OVERFLOW";
            reason = "The covered virtual length overflowed bounded arithmetic.";
            return false;
        }

        stream = new(source, budget, ordered, logicalSize, Math.Min(logicalSize, coveredBytes), volumeOffset, clusterSize);
        return true;
    }

    public long? TryGetImageOffset(long virtualOffset)
    {
        if (virtualOffset < 0 || virtualOffset >= CoveredLength)
        {
            return null;
        }

        var vcn = virtualOffset / _clusterSize;
        var run = FindRun(vcn);
        if (run?.LogicalCluster is null)
        {
            return null;
        }

        try
        {
            return checked(_volumeOffset + checked((run.LogicalCluster.Value + (vcn - run.VirtualCluster)) * _clusterSize) + (virtualOffset % _clusterSize));
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    public async ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        long end;
        try
        {
            end = checked(offset + destination.Length);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("The virtual read range overflowed.", exception);
        }

        if (end > Length || end > CoveredLength)
        {
            throw new EndOfStreamException("The virtual stream cannot satisfy the exact range from validated extents.");
        }

        var completed = 0;
        while (completed < destination.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = checked(offset + completed);
            var vcn = current / _clusterSize;
            var intraCluster = (int)(current % _clusterSize);
            var run = FindRun(vcn) ?? throw new EndOfStreamException("The virtual stream contains an unmapped range.");
            if (run.IsSparse || run.LogicalCluster is null)
            {
                throw new InvalidDataException("Sparse metadata stream ranges are not silently materialized.");
            }

            var clustersRemaining = checked(run.VirtualCluster + run.ClusterCount - vcn);
            var runBytesRemaining = checked(clustersRemaining * _clusterSize - intraCluster);
            var count = (int)Math.Min(destination.Length - completed, runBytesRemaining);
            var imageOffset = checked(_volumeOffset + checked((run.LogicalCluster.Value + (vcn - run.VirtualCluster)) * _clusterSize) + intraCluster);
            await _budget.ReadExactlyAsync(_source, imageOffset, destination.Slice(completed, count), cancellationToken).ConfigureAwait(false);
            completed = checked(completed + count);
        }
    }

    private NtfsDataRun? FindRun(long vcn)
    {
        var low = 0;
        var high = _runs.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var run = _runs[middle];
            if (vcn < run.VirtualCluster)
            {
                high = middle - 1;
                continue;
            }

            if (vcn >= run.VirtualCluster && vcn < run.VirtualCluster + run.ClusterCount)
            {
                return run;
            }

            low = middle + 1;
        }

        return null;
    }
}
