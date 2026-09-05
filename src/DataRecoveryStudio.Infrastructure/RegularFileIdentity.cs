using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DataRecoveryStudio.Infrastructure;

/// <summary>Read-only file metadata, never raw-device access. Uses 128-bit IDs for ReFS as well as NTFS.</summary>
internal sealed record RegularFileIdentity(string Key)
{
    internal static RegularFileIdentity Capture(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Stable image identity currently requires Windows file IDs.");
        if (!GetFileInformationByHandleEx(handle, 18, out FileIdInfo id, (uint)Marshal.SizeOf<FileIdInfo>()))
            throw new IOException("Stable regular-file identity is unavailable.", new Win32Exception(Marshal.GetLastWin32Error()));
        return new($"{id.Volume:X16}:{id.Low:X16}{id.High:X16}");
    }
    internal static RegularFileIdentity CapturePath(string path)
    {
        var full = ExFatImageSourceFactory.ValidatePath(path);
        using var handle = File.OpenHandle(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Capture(handle);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo { public ulong Volume, Low, High; }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out FileIdInfo information, uint bufferSize);
}

internal interface IRegularFileIdentitySource
{
    string FileIdentity { get; }
    void ValidateIdentity(string path);
}
