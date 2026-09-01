using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

public sealed class RecoverySourceMetadataProvider(IReadOnlyImageSourceFactory sourceFactory) : IRecoverySourceMetadataProvider
{
    public RecoverySourceMetadataProvider() : this(new RegularFileRandomAccessSourceFactory())
    {
    }

    public async ValueTask<RecoverySourceMetadata> CaptureAsync(
        string imagePath,
        long volumeOffset,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (volumeOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(volumeOffset));
        }

        var canonicalPath = SafeImagePath.GetValidatedFullPath(imagePath);
        var attributes = File.GetAttributes(canonicalPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
        {
            throw new NotSupportedException("The recovery source must be an ordinary, non-reparse-point image file.");
        }

        var before = new FileInfo(canonicalPath);
        before.Refresh();
        await using var source = await sourceFactory.OpenAsync(canonicalPath, cancellationToken).ConfigureAwait(false);
        var boot = new byte[512];
        await source.ReadExactlyAsync(volumeOffset, boot, cancellationToken).ConfigureAwait(false);
        if (!NtfsBootSectorParser.TryParse(boot, out var geometry, out _, out var reason))
        {
            throw new InvalidDataException(reason);
        }

        var after = new FileInfo(canonicalPath);
        after.Refresh();
        if (before.Length != after.Length || before.LastWriteTimeUtc.Ticks != after.LastWriteTimeUtc.Ticks || source.Length != after.Length)
        {
            throw new IOException("The image source changed while its recovery provenance was captured.");
        }

        return new(
            canonicalPath,
            after.Length,
            after.LastWriteTimeUtc.Ticks,
            RecoveryFingerprint.ComputeGeometry(geometry!));
    }
}
