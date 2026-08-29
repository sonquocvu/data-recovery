using System.Buffers.Binary;
using System.ComponentModel;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

public sealed class WindowsStorageDiscoveryService : IDeviceDiscoveryService
{
    private const uint StorageDeviceProperty = 0;
    private const uint StorageDeviceSeekPenaltyProperty = 7;
    private readonly IWindowsStorageNative _native;
    private readonly IStructuredLogger? _logger;

    public WindowsStorageDiscoveryService(IWindowsStorageNative? native = null, IStructuredLogger? logger = null)
    {
        _native = native ?? new WindowsStorageNative();
        _logger = logger;
    }

    public Task<IReadOnlyList<StorageDevice>> GetDevicesAsync(CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            try
            {
                return Discover(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogFailure("EnumerateVolumes", exception, isOptional: false);
                throw;
            }
        }, cancellationToken);

    private IReadOnlyList<StorageDevice> Discover(CancellationToken cancellationToken)
    {
        var diskCache = new Dictionary<int, PhysicalDisk>();
        var results = new List<StorageDevice>();
        foreach (var volumeName in _native.EnumerateVolumeNames(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                results.Add(DiscoverVolume(volumeName, diskCache, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogFailure("DiscoverVolume", exception, isOptional: false);
            }
        }

        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? string.Empty;
        return StorageMetadataNormalizer.OrderAndDeduplicate(results, systemRoot);
    }

    private StorageDevice DiscoverVolume(
        string volumeName,
        IDictionary<int, PhysicalDisk> diskCache,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> mountPaths = [];
        NativeVolumeInformation information = default;
        NativeDiskSpace space = default;
        var availability = VolumeAvailability.Available;
        var connection = DeviceConnectionStatus.Online;

        try
        {
            mountPaths = _native.GetVolumePathNames(volumeName)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception)
        {
            availability = VolumeAvailability.Inaccessible;
            connection = IsAccessDenied(exception) ? DeviceConnectionStatus.AccessDenied : DeviceConnectionStatus.Disconnected;
            LogFailure("GetVolumePathNamesForVolumeNameW", exception, isOptional: false);
        }

        var preferredPath = StorageMetadataNormalizer.SelectPreferredMountPath(mountPaths);
        if (availability == VolumeAvailability.Available && preferredPath.Length == 0)
        {
            availability = VolumeAvailability.NoMountPoint;
        }

        try
        {
            information = _native.GetVolumeInformation(volumeName);
        }
        catch (Exception exception)
        {
            availability = VolumeAvailability.Inaccessible;
            connection = IsAccessDenied(exception) ? DeviceConnectionStatus.AccessDenied : DeviceConnectionStatus.Disconnected;
            LogFailure("GetVolumeInformationW", exception, isOptional: false);
        }

        try
        {
            space = _native.GetDiskSpace(preferredPath.Length == 0 ? volumeName : preferredPath);
        }
        catch (Exception exception)
        {
            availability = VolumeAvailability.Inaccessible;
            connection = IsAccessDenied(exception) ? DeviceConnectionStatus.AccessDenied : DeviceConnectionStatus.Disconnected;
            LogFailure("GetDiskFreeSpaceExW", exception, isOptional: false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var driveType = preferredPath.Length == 0 ? WindowsDriveType.Unknown : _native.GetDriveType(preferredPath);
        var diskNumbers = TryGetDiskNumbers(volumeName);
        var disks = new List<PhysicalDisk>();
        foreach (var diskNumber in diskNumbers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!diskCache.TryGetValue(diskNumber, out var disk))
            {
                disk = DiscoverPhysicalDisk(diskNumber, volumeName);
                diskCache.Add(diskNumber, disk);
            }

            disks.Add(disk);
        }

        IReadOnlySet<PhysicalDeviceId> physicalIds = disks.Select(disk => disk.Id).ToHashSet();
        if (physicalIds.Count == 0)
        {
            physicalIds = new HashSet<PhysicalDeviceId>
            {
                StorageMetadataNormalizer.CreateIdentity(null, null, volumeName).Id,
            };
        }

        var identity = physicalIds.OrderBy(id => id.Value, StringComparer.Ordinal).First();
        var type = ClassifyVolumeDevice(disks);
        var fileSystem = StorageMetadataNormalizer.NormalizeText(information.FileSystem);
        var reason = DetermineUnsupportedReason(driveType, availability, fileSystem, diskNumbers.Count > 0);
        var supported = reason is null;
        var total = checked((long)Math.Min(space.TotalBytes, long.MaxValue));
        var free = checked((long)Math.Min(space.FreeBytes, long.MaxValue));
        var used = Math.Max(0, total - Math.Min(total, free));
        var fallbackName = preferredPath.Length > 0 ? preferredPath.TrimEnd('\\') : LocalVolumeFallback(volumeName);
        var displayName = string.IsNullOrWhiteSpace(information.Label) ? fallbackName : information.Label.Trim();
        var model = disks.Select(disk => disk.Model).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "Unknown storage device";
        var volume = new Volume(volumeName, preferredPath, displayName, fileSystem, total, used, identity)
        {
            VolumeGuidPath = volumeName,
            MountPaths = mountPaths,
            PhysicalDeviceIds = physicalIds,
            Availability = availability,
            IsSupported = supported,
            UnsupportedReasonKey = reason,
        };

        return new StorageDevice(identity, displayName, model, type, connection, [volume])
        {
            PhysicalDisks = disks,
            PhysicalDeviceIds = physicalIds,
        };
    }

    private IReadOnlyList<int> TryGetDiskNumbers(string volumeName)
    {
        try
        {
            using var handle = _native.OpenMetadataDevice(volumeName.TrimEnd('\\'), MetadataOpenOptions.ReadOnlyMetadata);
            var buffer = _native.QueryDevice(handle, StorageNativeConstants.IoctlVolumeGetVolumeDiskExtents, []);
            return NativeStorageParser.ParseDiskExtents(buffer);
        }
        catch (Exception exception)
        {
            LogFailure("IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS", exception, isOptional: true);
            return [];
        }
    }

    private PhysicalDisk DiscoverPhysicalDisk(int diskNumber, string volumeName)
    {
        StorageDescriptor? descriptor = null;
        bool? incursSeekPenalty = null;
        long capacity = 0;
        try
        {
            using var handle = _native.OpenMetadataDevice($"\\\\.\\PhysicalDrive{diskNumber}", MetadataOpenOptions.ReadOnlyMetadata);
            try
            {
                descriptor = NativeStorageParser.ParseStorageDescriptor(_native.QueryDevice(
                    handle,
                    StorageNativeConstants.IoctlStorageQueryProperty,
                    CreateStoragePropertyQuery(StorageDeviceProperty)));
            }
            catch (Exception exception)
            {
                LogFailure("IOCTL_STORAGE_QUERY_PROPERTY:StorageDeviceProperty", exception, isOptional: true);
            }

            try
            {
                incursSeekPenalty = NativeStorageParser.ParseSeekPenalty(_native.QueryDevice(
                    handle,
                    StorageNativeConstants.IoctlStorageQueryProperty,
                    CreateStoragePropertyQuery(StorageDeviceSeekPenaltyProperty),
                    64));
            }
            catch (Exception exception)
            {
                LogFailure("IOCTL_STORAGE_QUERY_PROPERTY:StorageDeviceSeekPenaltyProperty", exception, isOptional: true);
            }

            try
            {
                capacity = NativeStorageParser.ParseDiskCapacity(_native.QueryDevice(
                    handle,
                    StorageNativeConstants.IoctlDiskGetDriveGeometryEx,
                    [],
                    256));
            }
            catch (Exception exception)
            {
                LogFailure("IOCTL_DISK_GET_DRIVE_GEOMETRY_EX", exception, isOptional: true);
            }
        }
        catch (Exception exception)
        {
            LogFailure("CreateFileW:PhysicalDisk", exception, isOptional: true);
        }

        var identity = StorageMetadataNormalizer.CreateIdentity(diskNumber, descriptor, volumeName);
        var model = string.Join(' ', new[] { descriptor?.Vendor, descriptor?.Model }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
        return new(
            diskNumber,
            identity.Id,
            identity.Confidence,
            model.Length == 0 ? $"Physical disk {diskNumber}" : model,
            descriptor?.Vendor ?? string.Empty,
            descriptor?.BusType ?? StorageBusType.Unknown,
            descriptor?.IsRemovable ?? false,
            capacity,
            StorageMetadataNormalizer.ClassifyDevice(descriptor, incursSeekPenalty));
    }

    private static byte[] CreateStoragePropertyQuery(uint propertyId)
    {
        var query = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(query, propertyId);
        BinaryPrimitives.WriteUInt32LittleEndian(query.AsSpan(4), 0);
        return query;
    }

    private static StorageDeviceType ClassifyVolumeDevice(IReadOnlyList<PhysicalDisk> disks)
    {
        if (disks.Count == 0)
        {
            return StorageDeviceType.Unknown;
        }

        if (disks.Any(disk => disk.Type == StorageDeviceType.UsbDevice)) return StorageDeviceType.UsbDevice;
        if (disks.Any(disk => disk.Type == StorageDeviceType.ExternalDrive)) return StorageDeviceType.ExternalDrive;
        if (disks.All(disk => disk.Type == StorageDeviceType.InternalSsd)) return StorageDeviceType.InternalSsd;
        if (disks.All(disk => disk.Type == StorageDeviceType.InternalHdd)) return StorageDeviceType.InternalHdd;
        return StorageDeviceType.Unknown;
    }

    private static string? DetermineUnsupportedReason(
        WindowsDriveType driveType,
        VolumeAvailability availability,
        string fileSystem,
        bool hasPhysicalMapping)
    {
        if (availability == VolumeAvailability.NoMountPoint) return "Device.Unsupported.NoMountPoint";
        if (availability != VolumeAvailability.Available) return "Device.Unsupported.Inaccessible";
        if (driveType == WindowsDriveType.Remote) return "Device.Unsupported.Network";
        if (driveType == WindowsDriveType.CdRom) return "Device.Unsupported.Optical";
        if (driveType == WindowsDriveType.RamDisk) return "Device.Unsupported.RamDisk";
        if (driveType is not WindowsDriveType.Fixed and not WindowsDriveType.Removable) return "Device.Unsupported.DriveType";
        if (!StorageMetadataNormalizer.IsSupportedFileSystem(fileSystem)) return fileSystem.Length == 0
            ? "Device.Unsupported.RawOrLocked"
            : "Device.Unsupported.FileSystem";
        if (!hasPhysicalMapping) return "Device.Unsupported.PhysicalMapping";
        return null;
    }

    private static string LocalVolumeFallback(string volumeName)
    {
        var trimmed = volumeName.TrimEnd('\\');
        var separator = trimmed.LastIndexOf('{');
        return separator >= 0 ? $"Local volume {trimmed[separator..]}" : "Local volume";
    }

    private void LogFailure(string operation, Exception exception, bool isOptional)
    {
        if (_logger is null)
        {
            return;
        }

        var code = exception is Win32Exception win32 ? win32.NativeErrorCode : 0;
        _logger.LogAsync(
            isOptional ? "Warning" : "Error",
            "StorageDiscoveryOperationFailed",
            new Dictionary<string, object?>
            {
                ["operation"] = operation,
                ["win32Error"] = code,
                ["optionalMetadata"] = isOptional,
                ["exceptionType"] = exception.GetType().Name,
            }).AsTask().GetAwaiter().GetResult();
    }

    private static bool IsAccessDenied(Exception exception) =>
        exception is UnauthorizedAccessException || exception is Win32Exception { NativeErrorCode: 5 or 21 };
}
