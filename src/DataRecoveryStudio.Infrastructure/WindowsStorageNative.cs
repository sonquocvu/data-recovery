using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DataRecoveryStudio.Infrastructure;

public static class StorageNativeConstants
{
    public const uint MetadataDesiredAccess = 0;
    public const uint FileShareRead = 0x00000001;
    public const uint FileShareWrite = 0x00000002;
    public const uint FileShareDelete = 0x00000004;
    public const uint MetadataShareMode = FileShareRead | FileShareWrite | FileShareDelete;
    public const uint OpenExisting = 3;
    public const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;
    public const uint IoctlStorageQueryProperty = 0x002D1400;
    public const uint IoctlDiskGetDriveGeometryEx = 0x000700A0;
}

public readonly record struct MetadataOpenOptions(uint DesiredAccess, uint ShareMode, uint CreationDisposition)
{
    public static MetadataOpenOptions ReadOnlyMetadata { get; } = new(
        StorageNativeConstants.MetadataDesiredAccess,
        StorageNativeConstants.MetadataShareMode,
        StorageNativeConstants.OpenExisting);
}

public enum WindowsDriveType : uint
{
    Unknown,
    NoRootDirectory,
    Removable,
    Fixed,
    Remote,
    CdRom,
    RamDisk,
}

public readonly record struct NativeVolumeInformation(string Label, string FileSystem);

public readonly record struct NativeDiskSpace(ulong TotalBytes, ulong FreeBytes);

public interface IMetadataDeviceHandle : IDisposable
{
    bool IsInvalid { get; }
}

public interface IWindowsStorageNative
{
    IReadOnlyList<string> EnumerateVolumeNames(CancellationToken cancellationToken);

    IReadOnlyList<string> GetVolumePathNames(string volumeName);

    NativeVolumeInformation GetVolumeInformation(string volumeName);

    NativeDiskSpace GetDiskSpace(string path);

    WindowsDriveType GetDriveType(string rootPath);

    IMetadataDeviceHandle OpenMetadataDevice(string path, MetadataOpenOptions options);

    byte[] QueryDevice(IMetadataDeviceHandle handle, uint controlCode, ReadOnlySpan<byte> input, int initialCapacity = 4096);
}

internal sealed class MetadataDeviceHandle(SafeFileHandle handle) : IMetadataDeviceHandle
{
    public SafeFileHandle Handle { get; } = handle;

    public bool IsInvalid => Handle.IsInvalid;

    public void Dispose() => Handle.Dispose();
}

public sealed class WindowsStorageNative : IWindowsStorageNative
{
    private const int ErrorNoMoreFiles = 18;
    private const int ErrorMoreData = 234;
    private const int ErrorInsufficientBuffer = 122;
    private const int MaxVolumeNameCharacters = 32_768;
    private const int MaxNativeBufferBytes = 1024 * 1024;

    public IReadOnlyList<string> EnumerateVolumeNames(CancellationToken cancellationToken)
    {
        EnsureWindows();
        var capacity = 1024;
        while (true)
        {
            var name = new StringBuilder(capacity);
            var findHandle = FindFirstVolumeW(name, (uint)name.Capacity);
            if (findHandle != InvalidFindHandle)
            {
                try
                {
                    var result = new List<string> { name.ToString() };
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        name.Clear();
                        if (FindNextVolumeW(findHandle, name, (uint)name.Capacity))
                        {
                            result.Add(name.ToString());
                            continue;
                        }

                        var error = Marshal.GetLastWin32Error();
                        if (error == ErrorNoMoreFiles)
                        {
                            return result;
                        }

                        throw new Win32Exception(error, "FindNextVolumeW failed.");
                    }
                }
                finally
                {
                    _ = FindVolumeClose(findHandle);
                }
            }

            var firstError = Marshal.GetLastWin32Error();
            if (firstError is ErrorMoreData or ErrorInsufficientBuffer && capacity < MaxVolumeNameCharacters)
            {
                capacity = checked(Math.Min(capacity * 2, MaxVolumeNameCharacters));
                continue;
            }

            throw new Win32Exception(firstError, "FindFirstVolumeW failed.");
        }
    }

    public IReadOnlyList<string> GetVolumePathNames(string volumeName)
    {
        EnsureWindows();
        uint required = 0;
        _ = GetVolumePathNamesForVolumeNameW(volumeName, null, 0, out required);
        var error = Marshal.GetLastWin32Error();
        if (required == 0)
        {
            if (error == 0)
            {
                return [];
            }

            throw new Win32Exception(error, "GetVolumePathNamesForVolumeNameW size query failed.");
        }

        if (required > MaxVolumeNameCharacters)
        {
            throw new InvalidDataException("The volume mount-path buffer exceeds the safety limit.");
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = new char[checked((int)required)];
            if (GetVolumePathNamesForVolumeNameW(volumeName, buffer, (uint)buffer.Length, out var returned))
            {
                return NativeStorageParser.ParseMultiString(buffer, returned);
            }

            error = Marshal.GetLastWin32Error();
            if (error is ErrorMoreData or ErrorInsufficientBuffer && returned > required && returned <= MaxVolumeNameCharacters)
            {
                required = returned;
                continue;
            }

            throw new Win32Exception(error, "GetVolumePathNamesForVolumeNameW failed.");
        }

        throw new InvalidDataException("The volume mount-path buffer changed too many times.");
    }

    public NativeVolumeInformation GetVolumeInformation(string volumeName)
    {
        EnsureWindows();
        var label = new StringBuilder(261);
        var fileSystem = new StringBuilder(261);
        if (!GetVolumeInformationW(volumeName, label, (uint)label.Capacity, out _, out _, out _, fileSystem, (uint)fileSystem.Capacity))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetVolumeInformationW failed.");
        }

        return new(label.ToString(), fileSystem.ToString());
    }

    public NativeDiskSpace GetDiskSpace(string path)
    {
        EnsureWindows();
        if (!GetDiskFreeSpaceExW(path, out _, out var total, out var free))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetDiskFreeSpaceExW failed.");
        }

        return new(total, free);
    }

    public WindowsDriveType GetDriveType(string rootPath)
    {
        EnsureWindows();
        return (WindowsDriveType)GetDriveTypeW(rootPath);
    }

    public IMetadataDeviceHandle OpenMetadataDevice(string path, MetadataOpenOptions options)
    {
        EnsureWindows();
        if (options != MetadataOpenOptions.ReadOnlyMetadata)
        {
            throw new InvalidOperationException("Storage metadata handles must use the approved access, sharing, and disposition flags.");
        }

        var handle = CreateFileW(path, options.DesiredAccess, options.ShareMode, IntPtr.Zero, options.CreationDisposition, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"CreateFileW metadata open failed for '{SanitizeDevicePath(path)}'.");
        }

        return new MetadataDeviceHandle(handle);
    }

    public byte[] QueryDevice(IMetadataDeviceHandle handle, uint controlCode, ReadOnlySpan<byte> input, int initialCapacity = 4096)
    {
        EnsureWindows();
        if (handle is not MetadataDeviceHandle nativeHandle || nativeHandle.IsInvalid)
        {
            throw new ArgumentException("A valid Windows metadata handle is required.", nameof(handle));
        }

        if (initialCapacity <= 0 || initialCapacity > MaxNativeBufferBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(initialCapacity));
        }

        var inputBuffer = input.ToArray();
        var capacity = initialCapacity;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var output = new byte[capacity];
            if (DeviceIoControl(
                    nativeHandle.Handle,
                    controlCode,
                    inputBuffer.Length == 0 ? null : inputBuffer,
                    (uint)inputBuffer.Length,
                    output,
                    (uint)output.Length,
                    out var returned,
                    IntPtr.Zero))
            {
                if (returned > output.Length)
                {
                    throw new InvalidDataException("DeviceIoControl returned an invalid byte count.");
                }

                return output.AsSpan(0, checked((int)returned)).ToArray();
            }

            var error = Marshal.GetLastWin32Error();
            if (error is ErrorMoreData or ErrorInsufficientBuffer && capacity < MaxNativeBufferBytes)
            {
                capacity = checked(Math.Min(capacity * 2, MaxNativeBufferBytes));
                continue;
            }

            throw new Win32Exception(error, $"DeviceIoControl 0x{controlCode:X8} failed.");
        }

        throw new InvalidDataException("DeviceIoControl exceeded the bounded allocation retry limit.");
    }

    private static string SanitizeDevicePath(string path) =>
        path.StartsWith("\\\\.\\PhysicalDrive", StringComparison.OrdinalIgnoreCase) ? "physical disk" : "volume";

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows storage discovery requires Windows.");
        }
    }

    private static readonly IntPtr InvalidFindHandle = new(-1);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstVolumeW(StringBuilder volumeName, uint bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextVolumeW(IntPtr findVolume, StringBuilder volumeName, uint bufferLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindVolumeClose(IntPtr findVolume);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNamesForVolumeNameW(
        string volumeName,
        [Out] char[]? volumePathNames,
        uint bufferLength,
        out uint returnLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(
        string rootPathName,
        StringBuilder volumeNameBuffer,
        uint volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder fileSystemNameBuffer,
        uint fileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(
        string directoryName,
        out ulong freeBytesAvailable,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveTypeW(string rootPathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        byte[]? inputBuffer,
        uint inputBufferSize,
        [Out] byte[] outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);
}
