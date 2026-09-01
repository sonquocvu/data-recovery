using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Application;

public sealed class StandardImageScanService(
    IReadOnlyImageSourceFactory sourceFactory,
    INtfsMetadataScanner scanner)
{
    public async Task<StandardScanResult> ScanAsync(
        string imagePath,
        StandardScanRequest request,
        IProgress<StandardScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(request);
        request.EffectiveBudgets.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        await using var source = await sourceFactory.OpenAsync(imagePath, cancellationToken).ConfigureAwait(false);
        var result = await scanner.ScanAsync(source, request, progress, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}
