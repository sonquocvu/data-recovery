using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

public sealed class MemoryRandomAccessSource : IReadOnlyRandomAccessSource
{
    private readonly byte[] _bytes;
    private bool _disposed;

    public MemoryRandomAccessSource(ReadOnlySpan<byte> bytes) => _bytes = bytes.ToArray();

    public long Length => _bytes.LongLength;

    public ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        RandomAccessValidation.ValidateRange(Length, offset, destination.Length);
        _bytes.AsMemory(checked((int)offset), destination.Length).CopyTo(destination);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}

public sealed class RegularFileRandomAccessSourceFactory : IReadOnlyImageSourceFactory
{
    private readonly IRegularFileOpener _opener;

    public RegularFileRandomAccessSourceFactory() : this(new RegularFileOpener())
    {
    }

    internal RegularFileRandomAccessSourceFactory(IRegularFileOpener opener) =>
        _opener = opener ?? throw new ArgumentNullException(nameof(opener));

    public ValueTask<IReadOnlyRandomAccessSource> OpenAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = SafeImagePath.GetValidatedFullPath(path);
        var attributes = File.GetAttributes(fullPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
        {
            throw new NotSupportedException("The scan source must be an ordinary, non-reparse-point file.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyRandomAccessSource>(new FileRandomAccessSource(_opener.OpenRead(fullPath)));
    }
}

internal static class SafeImagePath
{
    public static string GetValidatedFullPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        RejectDeviceNamespace(path);
        var fullPath = Path.GetFullPath(path);
        RejectDeviceNamespace(fullPath);
        if (fullPath.AsSpan(Path.GetPathRoot(fullPath)!.Length).Contains(':'))
            throw new NotSupportedException("Alternate data streams are not ordinary image or destination paths.");
        return fullPath;
    }

    private static void RejectDeviceNamespace(string path)
    {
        var normalized = path.Trim().Replace('/', '\\');
        if (normalized.StartsWith("\\\\.\\", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("\\??\\", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("GLOBALROOT\\", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("\\GLOBALROOT\\", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("PhysicalDrive", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Device namespace paths cannot be used as scan sources.");
        }
    }
}

internal interface IRegularFileOpener
{
    FileStream OpenRead(string fullPath);
}

internal sealed class RegularFileOpener : IRegularFileOpener
{
    public FileStream OpenRead(string fullPath) => new(
        fullPath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        // Exact metadata/payload ranges must not trigger FileStream read-ahead into adjacent bytes.
        bufferSize: 1,
        FileOptions.Asynchronous | FileOptions.RandomAccess);
}

internal sealed class FileRandomAccessSource(FileStream stream) : IReadOnlyRandomAccessSource
{
    private readonly FileStream _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposeState;

    public long Length => _stream.Length;
    internal Microsoft.Win32.SafeHandles.SafeFileHandle Handle => _stream.SafeFileHandle;

    public async ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        RandomAccessValidation.ValidateRange(Length, offset, destination.Length);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
            _stream.Position = offset;
            var read = 0;
            while (read < destination.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = await _stream.ReadAsync(destination[read..], cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    throw new EndOfStreamException("The image source returned a deterministic short read.");
                }

                read = checked(read + count);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}

internal static class RandomAccessValidation
{
    public static void ValidateRange(long sourceLength, long offset, int length)
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        long end;
        try
        {
            end = checked(offset + length);
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "The requested range overflows.");
        }

        if (end > sourceLength)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The requested range exceeds the source length.");
        }
    }
}
