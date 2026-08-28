using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Application;

public static class DeviceDisplayFormatter
{
    public static string FormatContext(StorageDevice device)
    {
        var volume = device.Volumes.FirstOrDefault();
        if (volume is null)
        {
            return $"{device.DisplayName} · {ByteFormatter.Format(device.CapacityBytes)}";
        }

        return $"{device.DisplayName} ({FormatMountPath(volume.MountPath)}) · {volume.FileSystem} · {ByteFormatter.Format(device.CapacityBytes)}";
    }

    public static string FormatMountPath(string mountPath) => mountPath.TrimEnd('\\');
}
