using System.Diagnostics;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

public sealed class MockScanService(TimeSpan? stepDelay = null, int stepCount = 80) : IScanService
{
    private readonly TimeSpan _stepDelay = stepDelay ?? TimeSpan.FromMilliseconds(70);
    private readonly int _stepCount = Math.Max(2, stepCount);

    public async Task<ScanSession> ScanAsync(
        StorageDevice source,
        ScanModeKind mode,
        IProgress<ScanProgress> progress,
        CancellationToken cancellationToken)
    {
        var sessionId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var total = Math.Max(1, source.CapacityBytes);

        progress.Report(new ScanProgress(sessionId, ScanState.Starting, "Progress.Phase.Preparing", 0, total, TimeSpan.Zero, null, 0));

        for (var step = 1; step <= _stepCount; step++)
        {
            await Task.Delay(_stepDelay, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var ratio = (double)step / _stepCount;
            var bytes = (long)(total * ratio);
            var phase = GetPhase(mode, ratio);
            TimeSpan? remaining = step == 0
                ? null
                : TimeSpan.FromTicks((long)(stopwatch.Elapsed.Ticks / ratio * (1 - ratio)));
            var expectedFiles = mode == ScanModeKind.Standard ? 1_284 : 4_716;

            progress.Report(new ScanProgress(
                sessionId,
                step == _stepCount ? ScanState.Completed : ScanState.Scanning,
                phase,
                bytes,
                total,
                stopwatch.Elapsed,
                step == _stepCount ? TimeSpan.Zero : remaining,
                (int)(expectedFiles * ratio)));
        }

        return new ScanSession(
            sessionId,
            source.Id,
            mode,
            startedAt,
            ScanState.Completed,
            stopwatch.Elapsed,
            mode == ScanModeKind.Standard ? 1_284 : 4_716,
            source);
    }

    private static string GetPhase(ScanModeKind mode, double ratio)
    {
        if (mode == ScanModeKind.Standard)
        {
            return ratio switch
            {
                < 0.18 => "Progress.Phase.Metadata",
                < 0.76 => "Progress.Phase.Records",
                < 0.94 => "Progress.Phase.Paths",
                _ => "Progress.Phase.Finalizing",
            };
        }

        return ratio switch
        {
            < 0.12 => "Progress.Phase.Signatures",
            < 0.84 => "Progress.Phase.Matching",
            < 0.96 => "Progress.Phase.Boundaries",
            _ => "Progress.Phase.Finalizing",
        };
    }
}
