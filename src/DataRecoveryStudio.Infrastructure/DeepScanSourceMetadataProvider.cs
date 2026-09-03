using System.Buffers;
using System.Security.Cryptography;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

public sealed class DeepScanSourceMetadataProvider(IReadOnlyImageSourceFactory sourceFactory) : IDeepScanSourceMetadataProvider
{
    public DeepScanSourceMetadataProvider() : this(new RegularFileRandomAccessSourceFactory())
    {
    }

    public async ValueTask<DeepScanSourceFingerprint> CaptureAsync(string imagePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var canonicalPath = SafeImagePath.GetValidatedFullPath(imagePath);
        var attributes = File.GetAttributes(canonicalPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
        {
            throw new NotSupportedException("The Deep Scan source must be an ordinary, non-reparse-point image file.");
        }

        var before = new FileInfo(canonicalPath);
        before.Refresh();
        await using var source = await sourceFactory.OpenAsync(canonicalPath, cancellationToken).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            for (var offset = 0L; offset < source.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = checked((int)Math.Min(buffer.Length, source.Length - offset));
                await source.ReadExactlyAsync(offset, buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, count);
                offset = checked(offset + count);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var after = new FileInfo(canonicalPath);
        after.Refresh();
        if (before.Length != after.Length || before.LastWriteTimeUtc.Ticks != after.LastWriteTimeUtc.Ticks || source.Length != after.Length)
        {
            throw new IOException("SOURCE_CHANGED: The image changed while its Deep Scan fingerprint was captured.");
        }

        return new(canonicalPath, after.Length, after.LastWriteTimeUtc.Ticks, Convert.ToHexString(hash.GetHashAndReset()));
    }
}
