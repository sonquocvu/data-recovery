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

        progress.Report(new ScanProgress(sessionId, ScanState.Starting, "Preparing read-only mock scan", 0, total, TimeSpan.Zero, null, 0));

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

        return new ScanSession(sessionId, source.Id, mode, startedAt, ScanState.Completed);
    }

    private static string GetPhase(ScanModeKind mode, double ratio)
    {
        if (mode == ScanModeKind.Standard)
        {
            return ratio switch
            {
                < 0.18 => "Reading mock volume metadata",
                < 0.76 => "Reviewing deleted file records",
                < 0.94 => "Reconstructing mock folder paths",
                _ => "Finalizing the result catalog",
            };
        }

        return ratio switch
        {
            < 0.12 => "Preparing mock signature catalog",
            < 0.84 => "Matching simulated file signatures",
            < 0.96 => "Validating mock file boundaries",
            _ => "Finalizing the result catalog",
        };
    }
}
