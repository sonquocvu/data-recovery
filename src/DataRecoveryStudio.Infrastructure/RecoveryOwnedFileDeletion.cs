using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DataRecoveryStudio.Infrastructure;

/// <summary>Deletes a verified owned output by held handle, avoiding a path-check/delete race.</summary>
internal static class RecoveryOwnedFileDeletion
{
    internal static bool TryDelete(string path, RegularFileIdentity expected)
    {
        // DELETE | FILE_READ_ATTRIBUTES, SHARE_READ, OPEN_EXISTING, OPEN_REPARSE_POINT.
        // This handle denies concurrent replacement; the caller holds the validated directory ancestors.
        using var handle = OpenOwnedFile(path, 0x10080, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) return Marshal.GetLastWin32Error() is 2 or 3;
        if (RegularFileIdentity.Capture(handle) != expected) return false;
        var disposition = new Disposition { Delete = 1 };
        return SetDisposition(handle, 4, ref disposition, 1);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Disposition { internal byte Delete; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenOwnedFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDisposition(SafeFileHandle handle, int informationClass, ref Disposition disposition, uint size);
}
