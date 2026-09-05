using System.Buffers;
using System.Security.Cryptography;

namespace DataRecoveryStudio.Infrastructure;

internal static class RecoveryFilePublication
{
    public static string CreatePartialPath(RecoveryDestination destination, string basePath, Guid correlationId)
    {
        var name = Path.GetFileName(basePath);
        return destination.EnsureContained(Path.Combine(
            Path.GetDirectoryName(basePath) ?? destination.Root,
            $".{name}.{correlationId:N}.partial"));
    }

    public static string PublishWithoutOverwrite(
        string partialPath,
        string basePath,
        RecoveryDestination destination,
        int maximumAttempts)
    {
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            var candidate = destination.EnsureContained(RecoveryDestination.WithCollisionSuffix(basePath, attempt));
            destination.RevalidateParent(candidate);
            try
            {
                File.Move(partialPath, candidate, overwrite: false);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate) || Directory.Exists(candidate))
            {
            }
        }

        throw new IOException("No safe unique output filename was available within the bounded collision limit.");
    }

    public static async Task<(string Sha256, long Bytes)> HashFileAsync(
        string path,
        int bufferSize,
        long maximumBytes,
        CancellationToken cancellationToken,
        Action<int>? bytesRead = null)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        var total = 0L;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(buffer.AsMemory(0, bufferSize), cancellationToken).ConfigureAwait(false);
                bytesRead?.Invoke(read);
                if (read == 0) break;
                if (total > maximumBytes - read) throw new IOException("The post-write verification byte budget was reached.");
                hasher.AppendData(buffer, 0, read);
                total = checked(total + read);
            }

            return (Convert.ToHexString(hasher.GetHashAndReset()), total);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static bool CleanupExact(string? partialPath, string? publishedPath, int maximumAttempts)
    {
        return Delete(partialPath) & Delete(publishedPath);

        bool Delete(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return true;
            for (var attempt = 0; attempt < maximumAttempts; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    return !File.Exists(path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    if (attempt == maximumAttempts - 1) return false;
                }
            }

            return false;
        }
    }

    internal static bool CleanupOwned(string path, RegularFileIdentity identity, int maximumAttempts)
    {
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            try { if (RecoveryOwnedFileDeletion.TryDelete(path, identity)) return true; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException) { }
        }
        return false;
    }
}
