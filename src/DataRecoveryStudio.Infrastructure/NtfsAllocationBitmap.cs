using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

internal sealed class NtfsAllocationBitmap(
    NtfsVirtualStream stream,
    int maximumCacheBytes,
    DiagnosticCollector diagnostics)
{
    private readonly int _blockSize = Math.Min(4096, maximumCacheBytes);
    private readonly int _maximumBlocks = Math.Max(1, maximumCacheBytes / Math.Min(4096, maximumCacheBytes));
    private readonly Dictionary<long, byte[]> _cache = [];
    private readonly Queue<long> _cacheOrder = [];

    public async Task<NtfsAllocationState> QueryRunsAsync(
        IReadOnlyList<NtfsDataRun> runs,
        long candidateRecordNumber,
        CancellationToken cancellationToken)
    {
        var sawFree = false;
        var sawAllocated = false;
        foreach (var run in runs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (run.IsSparse || run.LogicalCluster is null || run.LogicalCluster < 0 || run.ClusterCount <= 0)
            {
                return NtfsAllocationState.Unknown;
            }

            NtfsAllocationState state;
            try
            {
                state = await QueryRangeAsync(run.LogicalCluster.Value, run.ClusterCount, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentOutOfRangeException)
            {
                diagnostics.Add("NTFS_ALLOCATION_ANALYSIS_INCOMPLETE", ScanDiagnosticSeverity.Warning, "bitmap-query", "Allocation metadata could not be read for a candidate range.", null, candidateRecordNumber);
                return NtfsAllocationState.Unknown;
            }

            if (state is NtfsAllocationState.Unknown or NtfsAllocationState.OutsideCoverage)
            {
                diagnostics.Add(state == NtfsAllocationState.OutsideCoverage ? "NTFS_BITMAP_RANGE_OUTSIDE_COVERAGE" : "NTFS_ALLOCATION_ANALYSIS_INCOMPLETE", ScanDiagnosticSeverity.Warning, "bitmap-query", state == NtfsAllocationState.OutsideCoverage ? "A candidate cluster range lies outside bitmap coverage." : "Allocation analysis is incomplete.", null, candidateRecordNumber);
                return state;
            }

            sawFree |= state is NtfsAllocationState.EntirelyFree or NtfsAllocationState.Mixed;
            sawAllocated |= state is NtfsAllocationState.EntirelyAllocated or NtfsAllocationState.Mixed;
            if (sawFree && sawAllocated)
            {
                return NtfsAllocationState.Mixed;
            }
        }

        if (sawAllocated) return NtfsAllocationState.EntirelyAllocated;
        if (sawFree) return NtfsAllocationState.EntirelyFree;
        return NtfsAllocationState.Unknown;
    }

    internal async Task<NtfsAllocationState> QueryRangeAsync(long firstCluster, long clusterCount, CancellationToken cancellationToken)
    {
        if (firstCluster < 0 || clusterCount <= 0)
        {
            return NtfsAllocationState.Unknown;
        }

        long lastCluster;
        long bitmapBits;
        try
        {
            lastCluster = checked(firstCluster + clusterCount - 1);
            bitmapBits = checked(stream.Length * 8);
        }
        catch (OverflowException)
        {
            return NtfsAllocationState.Unknown;
        }

        if (lastCluster >= bitmapBits)
        {
            return NtfsAllocationState.OutsideCoverage;
        }

        var sawFree = false;
        var sawAllocated = false;
        var firstByte = firstCluster / 8;
        var lastByte = lastCluster / 8;
        for (var byteOffset = firstByte; byteOffset <= lastByte; byteOffset++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var blockIndex = byteOffset / _blockSize;
            var block = await GetBlockAsync(blockIndex, cancellationToken).ConfigureAwait(false);
            var withinBlockByte = checked((int)(byteOffset % _blockSize));
            var firstBit = byteOffset == firstByte ? (int)(firstCluster & 7) : 0;
            var lastBit = byteOffset == lastByte ? (int)(lastCluster & 7) : 7;
            var mask = (byte)(((1 << (lastBit - firstBit + 1)) - 1) << firstBit);
            var value = (byte)(block[withinBlockByte] & mask);
            sawFree |= value != mask;
            sawAllocated |= value != 0;
            if (sawAllocated && sawFree)
            {
                return NtfsAllocationState.Mixed;
            }
        }

        return sawAllocated ? NtfsAllocationState.EntirelyAllocated : NtfsAllocationState.EntirelyFree;
    }

    private async Task<byte[]> GetBlockAsync(long blockIndex, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(blockIndex, out var cached))
        {
            return cached;
        }

        var offset = checked(blockIndex * _blockSize);
        var count = checked((int)Math.Min(_blockSize, stream.Length - offset));
        if (count <= 0)
        {
            throw new EndOfStreamException("The bitmap cache block is outside the virtual stream.");
        }

        var block = new byte[count];
        await stream.ReadExactlyAsync(offset, block, cancellationToken).ConfigureAwait(false);
        while (_cache.Count >= _maximumBlocks && _cacheOrder.TryDequeue(out var expired))
        {
            _cache.Remove(expired);
        }

        _cache[blockIndex] = block;
        _cacheOrder.Enqueue(blockIndex);
        return block;
    }
}
