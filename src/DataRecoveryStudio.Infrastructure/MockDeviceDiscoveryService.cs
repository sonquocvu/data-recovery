using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

public sealed class MockDeviceDiscoveryService : IDeviceDiscoveryService
{
    public static readonly PhysicalDeviceId InternalDeviceId = new("mock:physical:nvme:system-001");
    public static readonly PhysicalDeviceId UsbDeviceId = new("mock:physical:usb:travel-042");
    public static readonly PhysicalDeviceId BackupDeviceId = new("mock:physical:usb:backup-903");

    public Task<IReadOnlyList<StorageDevice>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<StorageDevice> devices =
        [
            new(
                InternalDeviceId,
                "Windows system drive",
                "Aurora NVMe 1 TB",
                StorageDeviceType.Internal,
                DeviceConnectionStatus.Online,
                [new Volume("mock-volume-c", "C:\\", "Windows", "NTFS", 1_000_204_886_016, 612_318_420_992, InternalDeviceId)]),
            new(
                UsbDeviceId,
                "Travel USB",
                "Nimbus USB 3.2 128 GB",
                StorageDeviceType.RemovableUsb,
                DeviceConnectionStatus.Online,
                [new Volume("mock-volume-e", "E:\\", "TRAVEL", "exFAT", 128_043_712_512, 73_418_702_848, UsbDeviceId)]),
            new(
                BackupDeviceId,
                "Archive drive",
                "Harbor Portable 2 TB",
                StorageDeviceType.External,
                DeviceConnectionStatus.Online,
                [new Volume("mock-volume-f", "F:\\", "ARCHIVE", "NTFS", 2_000_398_934_016, 881_274_650_624, BackupDeviceId)]),
        ];

        return Task.FromResult(devices);
    }
}
