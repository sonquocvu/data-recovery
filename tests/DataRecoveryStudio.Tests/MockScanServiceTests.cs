using DataRecoveryStudio.Core;
using DataRecoveryStudio.Infrastructure;

namespace DataRecoveryStudio.Tests;

public sealed class MockScanServiceTests
{
    [Fact]
    public async Task Scan_ReportsStartingScanningAndCompletedTransitions()
    {
        var service = new MockScanService(TimeSpan.Zero, 5);
        var progress = new RecordingProgress<ScanProgress>();
        var source = await GetSourceAsync();

        var session = await service.ScanAsync(source, ScanModeKind.Standard, progress, CancellationToken.None);

        Assert.Equal(ScanState.Completed, session.State);
        Assert.Equal(ScanState.Starting, progress.Values.First().State);
        Assert.Contains(progress.Values, item => item.State == ScanState.Scanning);
        Assert.Equal(ScanState.Completed, progress.Values.Last().State);
        Assert.Equal(100, progress.Values.Last().Percentage);
        Assert.True(progress.Values.Zip(progress.Values.Skip(1), (left, right) => right.BytesScanned >= left.BytesScanned).All(value => value));
    }

    [Fact]
    public async Task Scan_HonorsCancellation()
    {
        var service = new MockScanService(TimeSpan.FromMilliseconds(20), 100);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(55));
        var source = await GetSourceAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ScanAsync(source, ScanModeKind.Deep, new RecordingProgress<ScanProgress>(), cancellation.Token));
    }

    private static async Task<StorageDevice> GetSourceAsync()
    {
        var devices = await new MockDeviceDiscoveryService().GetDevicesAsync(CancellationToken.None);
        return devices[0];
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];
        public void Report(T value) => Values.Add(value);
    }
}
