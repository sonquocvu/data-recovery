using System.Buffers.Binary;
using System.Text;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

public sealed record StorageDescriptor(
    string Vendor,
    string Model,
    string SerialNumber,
    StorageBusType BusType,
    bool IsRemovable);

public static class NativeStorageParser
{
    private const int DiskExtentOffset = 8;
    private const int DiskExtentSize = 24;
    private const int StorageDescriptorMinimumSize = 36;

    public static IReadOnlyList<string> ParseMultiString(ReadOnlySpan<char> buffer, uint returnedCharacters)
    {
        if (returnedCharacters > buffer.Length)
        {
            throw new InvalidDataException("The multi-string character count exceeds its buffer.");
        }

        var length = checked((int)returnedCharacters);
        var result = new List<string>();
        var start = 0;
        for (var index = 0; index < length; index++)
        {
            if (buffer[index] != '\0')
            {
                continue;
            }

            if (index == start)
            {
                break;
            }

            result.Add(new string(buffer[start..index]));
            start = index + 1;
        }

        if (start < length && buffer[start..length].IndexOf('\0') < 0)
        {
            throw new InvalidDataException("The multi-string buffer is missing a null terminator.");
        }

        return result;
    }

    public static IReadOnlyList<int> ParseDiskExtents(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < sizeof(uint))
        {
            throw new InvalidDataException("The disk-extent buffer is truncated.");
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        if (count == 0)
        {
            return [];
        }

        var required = checked(DiskExtentOffset + checked((int)count * DiskExtentSize));
        if (required > buffer.Length)
        {
            throw new InvalidDataException("The disk-extent buffer does not contain all declared extents.");
        }

        var result = new HashSet<int>();
        for (var index = 0; index < count; index++)
        {
            var offset = checked(DiskExtentOffset + index * DiskExtentSize);
            var diskNumber = BinaryPrimitives.ReadInt32LittleEndian(buffer[offset..]);
            if (diskNumber < 0)
            {
                throw new InvalidDataException("A disk extent contains an invalid disk number.");
            }

            result.Add(diskNumber);
        }

        return result.Order().ToArray();
    }

    public static StorageDescriptor ParseStorageDescriptor(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < StorageDescriptorMinimumSize)
        {
            throw new InvalidDataException("The storage descriptor is truncated.");
        }

        var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]);
        if (declaredSize < StorageDescriptorMinimumSize || declaredSize > buffer.Length)
        {
            throw new InvalidDataException("The storage descriptor declares an invalid size.");
        }

        var descriptor = buffer[..checked((int)declaredSize)];
        var vendor = ReadOffsetString(descriptor, BinaryPrimitives.ReadUInt32LittleEndian(descriptor[12..]));
        var model = ReadOffsetString(descriptor, BinaryPrimitives.ReadUInt32LittleEndian(descriptor[16..]));
        var serial = ReadOffsetString(descriptor, BinaryPrimitives.ReadUInt32LittleEndian(descriptor[24..]));
        var bus = MapBusType(BinaryPrimitives.ReadUInt32LittleEndian(descriptor[28..]));
        return new(vendor, model, serial, bus, descriptor[10] != 0);
    }

    public static bool ParseSeekPenalty(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 9)
        {
            throw new InvalidDataException("The seek-penalty descriptor is truncated.");
        }

        var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]);
        if (declaredSize < 9 || declaredSize > buffer.Length)
        {
            throw new InvalidDataException("The seek-penalty descriptor declares an invalid size.");
        }

        return buffer[8] != 0;
    }

    public static long ParseDiskCapacity(ReadOnlySpan<byte> buffer)
    {
        const int diskSizeOffset = 24;
        if (buffer.Length < diskSizeOffset + sizeof(long))
        {
            throw new InvalidDataException("The drive-geometry buffer is truncated.");
        }

        var size = BinaryPrimitives.ReadInt64LittleEndian(buffer[diskSizeOffset..]);
        return size < 0 ? throw new InvalidDataException("The drive geometry contains a negative size.") : size;
    }

    private static string ReadOffsetString(ReadOnlySpan<byte> descriptor, uint offsetValue)
    {
        if (offsetValue == 0)
        {
            return string.Empty;
        }

        if (offsetValue >= descriptor.Length)
        {
            throw new InvalidDataException("A storage descriptor string offset is outside the buffer.");
        }

        var offset = checked((int)offsetValue);
        var remainder = descriptor[offset..];
        var terminator = remainder.IndexOf((byte)0);
        if (terminator < 0)
        {
            throw new InvalidDataException("A storage descriptor string is missing its null terminator.");
        }

        return StorageMetadataNormalizer.NormalizeText(Encoding.ASCII.GetString(remainder[..terminator]));
    }

    private static StorageBusType MapBusType(uint value) => value switch
    {
        1 => StorageBusType.Scsi,
        3 => StorageBusType.Ata,
        7 => StorageBusType.Usb,
        11 => StorageBusType.Sata,
        12 => StorageBusType.Sd,
        13 => StorageBusType.Mmc,
        14 => StorageBusType.Virtual,
        17 => StorageBusType.Nvme,
        _ => StorageBusType.Unknown,
    };
}
