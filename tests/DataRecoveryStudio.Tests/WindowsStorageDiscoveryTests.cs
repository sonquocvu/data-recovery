using System.Buffers.Binary;
using System.ComponentModel;
using System.Reflection;
using System.Text;
using DataRecoveryStudio.Core;
using DataRecoveryStudio.Infrastructure;

namespace DataRecoveryStudio.Tests;

public sealed class WindowsStorageDiscoveryTests
{
    [Theory]
    [InlineData(" ntfs ", true)]
    [InlineData("FAT32", true)]
    [InlineData("ExFaT", true)]
    [InlineData("RAW", false)]
    [InlineData("ReFS", false)]
    public void FileSystemSupport_IsCaseInsensitiveAndAllowListed(string fileSystem, bool expected) =>
        Assert.Equal(expected, StorageMetadataNormalizer.IsSupportedFileSystem(fileSystem));

    [Fact]
    public void PreferredMountPath_PrefersDriveLetterThenShortestFolder()
    {
        var result = StorageMetadataNormalizer.SelectPreferredMountPath(["C:\\Mounts\\Long\\", "D:\\", "C:\\Mount\\"]);

        Assert.Equal("D:\\", result);
    }

    [Fact]
    public void DescriptorNormalization_RemovesNullsPaddingWhitespaceAndCase()
    {
        Assert.Equal("ACME FAST DISK", StorageMetadataNormalizer.NormalizeText("  Acme\0  fast\t disk  "));
    }

    [Fact]
    public void Identity_IsStableAndDoesNotExposeSerial()
    {
        var descriptor = new StorageDescriptor("Acme", "Fast Disk", "SERIAL-SECRET-123", StorageBusType.Nvme, false);

        var first = StorageMetadataNormalizer.CreateIdentity(1, descriptor, "volume-a");
        var second = StorageMetadataNormalizer.CreateIdentity(9, descriptor with { SerialNumber = " serial-secret-123 " }, "volume-b");

        Assert.Equal(PhysicalIdentityConfidence.High, first.Confidence);
        Assert.Equal(first.Id, second.Id);
        Assert.DoesNotContain("SECRET", first.Id.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Identity_FallsBackToSessionDiskThenVolume()
    {
        var session = StorageMetadataNormalizer.CreateIdentity(4, new StorageDescriptor("", "", "", StorageBusType.Unknown, false), "volume-a");
        var unknown = StorageMetadataNormalizer.CreateIdentity(null, null, "volume-a");

        Assert.Equal(PhysicalIdentityConfidence.SessionOnly, session.Confidence);
        Assert.Equal(PhysicalIdentityConfidence.Unknown, unknown.Confidence);
        Assert.NotEqual(session.Id, unknown.Id);
    }

    [Theory]
    [InlineData(StorageBusType.Usb, true, null, StorageDeviceType.UsbDevice)]
    [InlineData(StorageBusType.Usb, false, true, StorageDeviceType.ExternalDrive)]
    [InlineData(StorageBusType.Nvme, false, false, StorageDeviceType.InternalSsd)]
    [InlineData(StorageBusType.Sata, false, true, StorageDeviceType.InternalHdd)]
    public void DeviceClassification_UsesBusRemovableAndSeekPenalty(
        StorageBusType bus,
        bool removable,
        bool? seekPenalty,
        StorageDeviceType expected)
    {
        var descriptor = new StorageDescriptor("", "", "", bus, removable);

        Assert.Equal(expected, StorageMetadataNormalizer.ClassifyDevice(descriptor, seekPenalty));
    }

    [Fact]
    public void MultiDiskDestinationIntersection_IsRejected()
    {
        var diskA = new PhysicalDeviceId("disk-a");
        var diskB = new PhysicalDeviceId("disk-b");
        var diskC = new PhysicalDeviceId("disk-c");

        var result = RecoveryDestinationPolicy.Validate(
            new HashSet<PhysicalDeviceId> { diskA, diskB },
            new HashSet<PhysicalDeviceId> { diskB, diskC },
            10,
            100);

        Assert.False(result.IsValid);
        Assert.Equal(DestinationValidationError.SamePhysicalDevice, result.Error);
    }

    [Fact]
    public async Task Discovery_UsesZeroAccessApprovedSharingDisposesHandlesAndMapsMultipleDisks()
    {
        var native = new FakeWindowsStorageNative();
        var service = new WindowsStorageDiscoveryService(native);

        var devices = await service.GetDevicesAsync(CancellationToken.None);

        var device = Assert.Single(devices);
        Assert.True(device.IsSupported);
        Assert.Equal(2, device.PhysicalDeviceIds.Count);
        Assert.Equal("NTFS", device.Volumes[0].FileSystem);
        Assert.Equal(3, native.OpenCalls.Count);
        Assert.All(native.OpenCalls, call =>
        {
            Assert.Equal(0u, call.Options.DesiredAccess);
            Assert.Equal(StorageNativeConstants.FileShareRead | StorageNativeConstants.FileShareWrite | StorageNativeConstants.FileShareDelete, call.Options.ShareMode);
            Assert.Equal(StorageNativeConstants.OpenExisting, call.Options.CreationDisposition);
            Assert.True(call.Handle.IsDisposed);
        });
        Assert.DoesNotContain(native.QueriedControlCodes, code => code is not (
            StorageNativeConstants.IoctlVolumeGetVolumeDiskExtents or
            StorageNativeConstants.IoctlStorageQueryProperty or
            StorageNativeConstants.IoctlDiskGetDriveGeometryEx));
    }

    [Fact]
    public async Task Discovery_DoesNotFallBackToMocksWhenNativeEnumerationFails()
    {
        var native = new FakeWindowsStorageNative { EnumerationException = new Win32Exception(5) };
        var service = new WindowsStorageDiscoveryService(native);

        var exception = await Assert.ThrowsAsync<Win32Exception>(() => service.GetDevicesAsync(CancellationToken.None));

        Assert.Equal(5, exception.NativeErrorCode);
    }

    [Fact]
    public async Task Discovery_ObservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WindowsStorageDiscoveryService(new FakeWindowsStorageNative()).GetDevicesAsync(cancellation.Token));
    }

    [Fact]
    public void MalformedNativeBuffers_AreRejected()
    {
        Assert.Throws<InvalidDataException>(() => NativeStorageParser.ParseDiskExtents([2, 0, 0, 0]));
        Assert.Throws<InvalidDataException>(() => NativeStorageParser.ParseStorageDescriptor(new byte[35]));
        Assert.Throws<InvalidDataException>(() => NativeStorageParser.ParseSeekPenalty(new byte[8]));
        Assert.Throws<InvalidDataException>(() => NativeStorageParser.ParseMultiString("X:\\".ToCharArray(), 3));
    }

    [Fact]
    public void Ordering_DeduplicatesVolumesAndPlacesSystemVolumeFirst()
    {
        var disk = new PhysicalDeviceId("disk");
        var external = CreateDevice("v2", "Z:\\", "External", StorageDeviceType.ExternalDrive, disk);
        var system = CreateDevice("v1", "C:\\", "System", StorageDeviceType.InternalSsd, disk);

        var result = StorageMetadataNormalizer.OrderAndDeduplicate([external, system, system], "C:\\");

        Assert.Equal(2, result.Count);
        Assert.Equal("v1", result[0].Volumes[0].VolumeGuidPath);
    }

    [Fact]
    public void NativeWrapper_ExposesOnlyMetadataOpenFlagsAndNoReadWriteImports()
    {
        var options = MetadataOpenOptions.ReadOnlyMetadata;
        Assert.Equal(0u, options.DesiredAccess);
        Assert.Equal(7u, options.ShareMode);
        Assert.Equal(3u, options.CreationDisposition);

        var nativeMethodNames = typeof(WindowsStorageNative)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Select(method => method.Name)
            .ToArray();
        Assert.DoesNotContain("ReadFile", nativeMethodNames);
        Assert.DoesNotContain("WriteFile", nativeMethodNames);
        Assert.Contains("CreateFileW", nativeMethodNames);
        Assert.Contains("DeviceIoControl", nativeMethodNames);
    }

    private static StorageDevice CreateDevice(string volumeId, string mount, string name, StorageDeviceType type, PhysicalDeviceId disk)
    {
        var volume = new Volume(volumeId, mount, name, "NTFS", 100, 10, disk)
        {
            VolumeGuidPath = volumeId,
            MountPaths = [mount],
        };
        return new(disk, name, name, type, DeviceConnectionStatus.Online, [volume]);
    }

    private sealed class FakeWindowsStorageNative : IWindowsStorageNative
    {
        public Exception? EnumerationException { get; init; }
        public List<OpenCall> OpenCalls { get; } = [];
        public List<uint> QueriedControlCodes { get; } = [];

        public IReadOnlyList<string> EnumerateVolumeNames(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (EnumerationException is not null) throw EnumerationException;
            return ["\\\\?\\Volume{test}\\"];
        }

        public IReadOnlyList<string> GetVolumePathNames(string volumeName) => ["C:\\"];

        public NativeVolumeInformation GetVolumeInformation(string volumeName) => new("Windows", "NTFS");

        public NativeDiskSpace GetDiskSpace(string path) => new(1000, 400);

        public WindowsDriveType GetDriveType(string rootPath) => WindowsDriveType.Fixed;

        public IMetadataDeviceHandle OpenMetadataDevice(string path, MetadataOpenOptions options)
        {
            var handle = new FakeHandle();
            OpenCalls.Add(new(path, options, handle));
            return handle;
        }

        public byte[] QueryDevice(IMetadataDeviceHandle handle, uint controlCode, ReadOnlySpan<byte> input, int initialCapacity = 4096)
        {
            QueriedControlCodes.Add(controlCode);
            if (controlCode == StorageNativeConstants.IoctlVolumeGetVolumeDiskExtents) return CreateExtents(2, 7);
            if (controlCode == StorageNativeConstants.IoctlDiskGetDriveGeometryEx) return CreateGeometry(2_000);
            if (input.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(input) == 7) return CreateSeekPenalty(false);
            var diskNumber = OpenCalls.Last().Path.EndsWith("7", StringComparison.Ordinal) ? 7 : 2;
            return CreateDescriptor($"SERIAL-{diskNumber}");
        }
    }

    private sealed record OpenCall(string Path, MetadataOpenOptions Options, FakeHandle Handle);

    private sealed class FakeHandle : IMetadataDeviceHandle
    {
        public bool IsInvalid => false;
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
    }

    private static byte[] CreateExtents(params int[] diskNumbers)
    {
        var buffer = new byte[8 + diskNumbers.Length * 24];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, diskNumbers.Length);
        for (var index = 0; index < diskNumbers.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8 + index * 24), diskNumbers[index]);
        }

        return buffer;
    }

    private static byte[] CreateDescriptor(string serial)
    {
        var vendor = Encoding.ASCII.GetBytes("ACME\0");
        var model = Encoding.ASCII.GetBytes("FAST DISK\0");
        var serialBytes = Encoding.ASCII.GetBytes(serial + "\0");
        var buffer = new byte[36 + vendor.Length + model.Length + serialBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), (uint)buffer.Length);
        buffer[10] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12), 36);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16), checked((uint)(36 + vendor.Length)));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(24), checked((uint)(36 + vendor.Length + model.Length)));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(28), 17);
        vendor.CopyTo(buffer, 36);
        model.CopyTo(buffer, 36 + vendor.Length);
        serialBytes.CopyTo(buffer, 36 + vendor.Length + model.Length);
        return buffer;
    }

    private static byte[] CreateSeekPenalty(bool value)
    {
        var buffer = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), 12);
        buffer[8] = value ? (byte)1 : (byte)0;
        return buffer;
    }

    private static byte[] CreateGeometry(long capacity)
    {
        var buffer = new byte[32];
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(24), capacity);
        return buffer;
    }
}
