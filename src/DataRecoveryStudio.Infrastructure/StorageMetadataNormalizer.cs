using System.Security.Cryptography;
using System.Text;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

public static class StorageMetadataNormalizer
{
    public static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(' ', value.Replace("\0", string.Empty, StringComparison.Ordinal).Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToUpperInvariant();
    }

    public static string SelectPreferredMountPath(IEnumerable<string> mountPaths)
    {
        return mountPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.Length == 3 && path[1] == ':' ? 0 : 1)
            .ThenBy(path => path.Length)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? string.Empty;
    }

    public static bool IsSupportedFileSystem(string? fileSystem)
    {
        var normalized = fileSystem?.Trim();
        return normalized is not null &&
               (normalized.Equals("NTFS", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("FAT32", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("exFAT", StringComparison.OrdinalIgnoreCase));
    }

    public static (PhysicalDeviceId Id, PhysicalIdentityConfidence Confidence) CreateIdentity(
        int? diskNumber,
        StorageDescriptor? descriptor,
        string volumeGuid)
    {
        var vendor = NormalizeText(descriptor?.Vendor);
        var model = NormalizeText(descriptor?.Model);
        var serial = NormalizeText(descriptor?.SerialNumber);
        if (serial.Length > 0 && (vendor.Length > 0 || model.Length > 0))
        {
            return (HashIdentity($"SERIAL|{serial}|{vendor}|{model}|{descriptor!.BusType}"), PhysicalIdentityConfidence.High);
        }

        if (diskNumber is not null && (vendor.Length > 0 || model.Length > 0))
        {
            return (HashIdentity($"SESSION|{diskNumber.Value}|{vendor}|{model}|{descriptor!.BusType}"), PhysicalIdentityConfidence.SessionOnly);
        }

        if (diskNumber is not null)
        {
            return (HashIdentity($"SESSION|{diskNumber.Value}"), PhysicalIdentityConfidence.SessionOnly);
        }

        return (HashIdentity($"VOLUME|{NormalizeText(volumeGuid)}"), PhysicalIdentityConfidence.Unknown);
    }

    public static StorageDeviceType ClassifyDevice(StorageDescriptor? descriptor, bool? incursSeekPenalty)
    {
        if (descriptor?.BusType == StorageBusType.Usb)
        {
            return descriptor.IsRemovable ? StorageDeviceType.UsbDevice : StorageDeviceType.ExternalDrive;
        }

        if (descriptor?.BusType is StorageBusType.Sd or StorageBusType.Mmc || descriptor?.IsRemovable == true)
        {
            return StorageDeviceType.UsbDevice;
        }

        if (descriptor?.BusType is StorageBusType.Ata or StorageBusType.Sata or StorageBusType.Nvme or StorageBusType.Scsi)
        {
            return incursSeekPenalty == false ? StorageDeviceType.InternalSsd :
                incursSeekPenalty == true ? StorageDeviceType.InternalHdd : StorageDeviceType.Unknown;
        }

        return StorageDeviceType.Unknown;
    }

    public static IReadOnlyList<StorageDevice> OrderAndDeduplicate(IEnumerable<StorageDevice> devices, string systemRoot)
    {
        var system = systemRoot.TrimEnd('\\') + "\\";
        return devices
            .GroupBy(device => device.Volumes.FirstOrDefault()?.VolumeGuidPath ?? device.Id.Value, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(device => device.Volumes.Any(volume => volume.MountPaths.Contains(system, StringComparer.OrdinalIgnoreCase)) ? 0 : SortGroup(device))
            .ThenBy(device => device.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(device => device.Volumes.FirstOrDefault()?.VolumeGuidPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int SortGroup(StorageDevice device) => device.IsSupported
        ? device.Type switch
        {
            StorageDeviceType.InternalSsd or StorageDeviceType.InternalHdd => 1,
            StorageDeviceType.ExternalDrive => 2,
            StorageDeviceType.UsbDevice => 3,
            _ => 4,
        }
        : 5;

    private static PhysicalDeviceId HashIdentity(string material)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return new PhysicalDeviceId($"windows:physical:{Convert.ToHexString(hash).ToLowerInvariant()}");
    }
}
