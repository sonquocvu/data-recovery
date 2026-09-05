using System.ComponentModel;
using System.Runtime.InteropServices;
using DataRecoveryStudio.Core;
using Microsoft.Win32.SafeHandles;

namespace DataRecoveryStudio.Infrastructure;

public static class LiveVolumeNativeConstants
{
    public const uint GenericRead = 0x80000000;
    public const uint ShareMode = StorageNativeConstants.FileShareRead |
        StorageNativeConstants.FileShareWrite |
        StorageNativeConstants.FileShareDelete;
    public const uint OpenExisting = StorageNativeConstants.OpenExisting;
    public const uint FileFlagOverlapped = 0x40000000;
}

public readonly record struct LiveVolumeOpenOptions(
    uint DesiredAccess,
    uint ShareMode,
    uint CreationDisposition,
    uint FlagsAndAttributes)
{
    public static LiveVolumeOpenOptions ReadOnlyScan { get; } = new(
        LiveVolumeNativeConstants.GenericRead,
        LiveVolumeNativeConstants.ShareMode,
        LiveVolumeNativeConstants.OpenExisting,
        LiveVolumeNativeConstants.FileFlagOverlapped);
}

public interface ILiveVolumeNative
{
    SafeFileHandle OpenReadOnlyVolume(string canonicalTarget, LiveVolumeOpenOptions options);
}

public sealed class WindowsLiveVolumeNative : ILiveVolumeNative
{
    public SafeFileHandle OpenReadOnlyVolume(string canonicalTarget, LiveVolumeOpenOptions options)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Live volume scanning requires Windows.");
        }

        var target = CanonicalVolumeGuidPath.Parse(canonicalTarget);
        if (options != LiveVolumeOpenOptions.ReadOnlyScan)
        {
            throw new InvalidOperationException("Live scan handles must use the approved read-only access flags.");
        }

        var handle = CreateFileW(
            target,
            options.DesiredAccess,
            options.ShareMode,
            IntPtr.Zero,
            options.CreationDisposition,
            options.FlagsAndAttributes,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "The authorized live volume could not be opened read-only.");
        }

        return handle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
}

internal interface ILiveVolumeReader
{
    ValueTask<int> ReadAsync(SafeFileHandle handle, Memory<byte> buffer, long fileOffset, CancellationToken cancellationToken);
}

internal sealed class WindowsLiveVolumeReader : ILiveVolumeReader
{
    public ValueTask<int> ReadAsync(SafeFileHandle handle, Memory<byte> buffer, long fileOffset, CancellationToken cancellationToken) =>
        RandomAccess.ReadAsync(handle, buffer, fileOffset, cancellationToken);
}

public sealed class LiveSourceRemovedException : Exception
{
    public LiveSourceRemovedException() : base("The live source is no longer available.")
    {
    }
}

public sealed class LiveVolumeRandomAccessSource : IReadOnlyRandomAccessSource
{
    private readonly SafeFileHandle _handle;
    private readonly ILiveVolumeReader _reader;
    private readonly int _maximumReadSize;
    private readonly long _maximumTotalBytes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _totalBytes;
    private int _disposeState;

    public LiveVolumeRandomAccessSource(SafeFileHandle handle, long length, long maximumTotalBytes)
        : this(handle, length, maximumTotalBytes, LiveScanProtocol.MaximumIndividualReadBytes, new WindowsLiveVolumeReader())
    {
    }

    internal LiveVolumeRandomAccessSource(
        SafeFileHandle handle,
        long length,
        long maximumTotalBytes,
        int maximumReadSize,
        ILiveVolumeReader reader)
    {
        _handle = handle ?? throw new ArgumentNullException(nameof(handle));
        if (_handle.IsInvalid || _handle.IsClosed) throw new ArgumentException("A valid read-only volume handle is required.", nameof(handle));
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        if (maximumTotalBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumTotalBytes));
        if (maximumReadSize <= 0 || maximumReadSize > LiveScanProtocol.MaximumIndividualReadBytes) throw new ArgumentOutOfRangeException(nameof(maximumReadSize));
        Length = length;
        _maximumTotalBytes = maximumTotalBytes;
        _maximumReadSize = maximumReadSize;
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public long Length { get; }
    internal long TotalBytesRead => Interlocked.Read(ref _totalBytes);

    public async ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        RandomAccessValidation.ValidateRange(Length, offset, destination.Length);
        if (destination.Length > _maximumReadSize)
        {
            throw new ArgumentOutOfRangeException(nameof(destination), "The live read exceeds the per-operation safety limit.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
            if (checked(_totalBytes + destination.Length) > _maximumTotalBytes)
            {
                throw new IOException("The live source byte budget was reached.");
            }

            var read = 0;
            while (read < destination.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count;
                try
                {
                    count = await _reader.ReadAsync(_handle, destination[read..], checked(offset + read), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsRemoval(exception))
                {
                    throw new LiveSourceRemovedException();
                }

                if (count <= 0 || count > destination.Length - read)
                {
                    throw new EndOfStreamException("The live volume returned a deterministic short read.");
                }

                read = checked(read + count);
                _totalBytes = checked(_totalBytes + count);
            }


        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _handle.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsRemoval(Exception exception)
    {
        var code = exception switch
        {
            Win32Exception win32 => win32.NativeErrorCode,
            IOException { InnerException: Win32Exception win32 } => win32.NativeErrorCode,
            _ => 0,
        };
        return code is 21 or 1112 or 1167;
    }
}

public interface ILiveVolumeSourceFactory
{
    IReadOnlyRandomAccessSource Open(LiveScanTargetGrant grant, long maximumTotalBytes);
}

public sealed class LiveVolumeSourceFactory(ILiveVolumeNative? native = null) : ILiveVolumeSourceFactory
{
    private readonly ILiveVolumeNative _native = native ?? new WindowsLiveVolumeNative();

    public IReadOnlyRandomAccessSource Open(LiveScanTargetGrant grant, long maximumTotalBytes)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var target = CanonicalVolumeGuidPath.Parse(grant.CanonicalVolumeGuidPath);
        if (grant.CapacityBytes <= 0) throw new InvalidDataException("The authorized volume capacity is invalid.");
        var handle = _native.OpenReadOnlyVolume(target, LiveVolumeOpenOptions.ReadOnlyScan);
        try
        {
            return new LiveVolumeRandomAccessSource(handle, grant.CapacityBytes, maximumTotalBytes);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }
}
