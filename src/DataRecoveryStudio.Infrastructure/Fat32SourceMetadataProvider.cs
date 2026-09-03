using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

public sealed class Fat32SourceMetadataProvider(IReadOnlyImageSourceFactory sourceFactory) : IFat32SourceMetadataProvider
{
    private readonly DeepScanSourceMetadataProvider _inner = new(sourceFactory);

    public Fat32SourceMetadataProvider() : this(new RegularFileRandomAccessSourceFactory())
    {
    }

    public async ValueTask<Fat32SourceFingerprint> CaptureAsync(string imagePath, CancellationToken cancellationToken)
    {
        var fingerprint = await _inner.CaptureAsync(imagePath, cancellationToken).ConfigureAwait(false);
        return new(fingerprint.CanonicalPath, fingerprint.Length, fingerprint.LastWriteTimeUtcTicks, fingerprint.Sha256);
    }
}
