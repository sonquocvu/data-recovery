using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DataRecoveryStudio.Infrastructure;

/// <summary>Read-only directory handles prevent rename, deletion and reparse mutation while publishing.</summary>
internal sealed class RecoveryDirectoryLease : IDisposable
{
    private readonly List<SafeFileHandle> _handles = [];
    private FileStream? _guard;
    internal RecoveryDirectoryLease(string destination, bool existingAncestorsOnly = false, bool allowChildRenames = false)
    {
        try
        {
            var chain = new Stack<string>();
            for (var directory = new DirectoryInfo(destination); directory is not null; directory = directory.Parent)
                if (!existingAncestorsOnly || directory.Exists) chain.Push(directory.FullName);
            foreach (var path in chain)
            {
                // FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES: attribute-only handles do not enforce sharing.
                // OPEN_EXISTING, FILE_SHARE_READ, BACKUP_SEMANTICS | OPEN_REPARSE_POINT.
                var handle = OpenDirectory(path, 0x81, 1, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new IOException($"Cannot hold destination directory '{path}' stable: {new Win32Exception(error).Message}", new Win32Exception(error)); }
                _handles.Add(handle);
                if (!GetAttributes(handle, 9, out var info, 8)) throw new IOException("Cannot inspect the held destination directory.", new Win32Exception(Marshal.GetLastWin32Error()));
                if ((info.Attributes & 0x400) != 0 || (info.Attributes & 0x10) == 0) throw new IOException("Destination ancestor is a reparse point or not a directory.");
            }
            if (allowChildRenames)
            {
                // Renaming child files needs write sharing on their directory. A non-deletable owned
                // guard keeps the destination nonempty, which prevents setting a reparse point.
                // Create it while the strict handles still prohibit directory mutation.
                _guard = new FileStream(Path.Combine(destination, $".drs-recovery-guard-{Guid.NewGuid():N}.tmp"),
                    FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
                var strictCount = _handles.Count;
                foreach (var path in chain)
                {
                    var handle = OpenDirectory(path, 0x81, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                    if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new IOException("Cannot retain the publication directory lease.", new Win32Exception(error)); }
                    _handles.Add(handle);
                }
                for (var i = 0; i < strictCount; i++) _handles[i].Dispose();
                _handles.RemoveRange(0, strictCount);
            }
        }
        catch { Dispose(); throw; }
    }
    public void Dispose()
    {
        try { _guard?.Dispose(); }
        finally { _guard = null; for (var i = _handles.Count - 1; i >= 0; i--) _handles[i].Dispose(); _handles.Clear(); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag { internal uint Attributes, Tag; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenDirectory(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetAttributes(SafeFileHandle handle, int informationClass, out AttributeTag info, uint size);
}
