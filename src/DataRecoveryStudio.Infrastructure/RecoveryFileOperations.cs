using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

/// <summary>Shared-publication adapter; the engine tracks ownership only after CreateNew succeeds.</summary>
internal class RecoveryFileOperations
{
    internal virtual RecoveryDestination OpenDestination(string path, string source, bool create, long bytes) =>
        RecoveryDestination.ValidateAndOpen(path, source, create, bytes);

    internal virtual FileStream CreatePartial(string path, int bufferSize) =>
        new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

    internal virtual Task<(string Sha256, long Bytes)> VerifyAsync(string path, int bufferSize, long bytes, CancellationToken token, Action<int> bytesRead) =>
        RecoveryFilePublication.HashFileAsync(path, bufferSize, bytes, token, bytesRead);

    internal virtual string Publish(string partial, string path, RecoveryDestination destination, int attempts) =>
        RecoveryFilePublication.PublishWithoutOverwrite(partial, path, destination, attempts);

    internal virtual bool Cleanup(string path, RegularFileIdentity identity, RecoveryDestination destination, int attempts)
    {
        try
        {
            destination.RevalidateParent(path);
            if (!File.Exists(path)) return !Directory.Exists(path);
            if (RegularFileIdentity.CapturePath(path) != identity) return false;
            return RecoveryFilePublication.CleanupOwned(path, identity, attempts);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }
}
