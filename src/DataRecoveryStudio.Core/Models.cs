namespace DataRecoveryStudio.Core;

public readonly record struct PhysicalDeviceId
{
    public PhysicalDeviceId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A physical device identifier is required.", nameof(value));
        }

        Value = value.Trim();
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public enum StorageDeviceType
{
    InternalSsd,
    InternalHdd,
    ExternalDrive,
    UsbDevice,
    Unknown,
    Internal = InternalHdd,
    External = ExternalDrive,
    RemovableUsb = UsbDevice,
}

public enum PhysicalIdentityConfidence
{
    High,
    Medium,
    SessionOnly,
    Unknown,
}

public enum StorageBusType
{
    Unknown,
    Scsi,
    Ata,
    Sata,
    Usb,
    Nvme,
    Sd,
    Mmc,
    Virtual,
}

public enum DeviceConnectionStatus
{
    Online,
    Disconnected,
    AccessDenied,
}

public enum VolumeAvailability
{
    Available,
    NoMountPoint,
    Inaccessible,
    Disconnected,
}

public sealed record PhysicalDisk(
    int? DiskNumber,
    PhysicalDeviceId Id,
    PhysicalIdentityConfidence IdentityConfidence,
    string Model,
    string Vendor,
    StorageBusType BusType,
    bool IsRemovable,
    long CapacityBytes,
    StorageDeviceType Type);

public sealed record Volume(
    string Id,
    string MountPath,
    string Label,
    string FileSystem,
    long CapacityBytes,
    long UsedBytes,
    PhysicalDeviceId PhysicalDeviceId)
{
    public long FreeBytes => Math.Max(0, CapacityBytes - UsedBytes);

    public string VolumeGuidPath { get; init; } = Id;

    public IReadOnlyList<string> MountPaths { get; init; } = [MountPath];

    public IReadOnlySet<PhysicalDeviceId> PhysicalDeviceIds { get; init; } =
        new HashSet<PhysicalDeviceId> { PhysicalDeviceId };

    public VolumeAvailability Availability { get; init; } = VolumeAvailability.Available;

    public bool IsSupported { get; init; } = true;

    public string? UnsupportedReasonKey { get; init; }
}

public sealed record StorageDevice(
    PhysicalDeviceId Id,
    string DisplayName,
    string Model,
    StorageDeviceType Type,
    DeviceConnectionStatus ConnectionStatus,
    IReadOnlyList<Volume> Volumes)
{
    public long CapacityBytes => Volumes.Sum(volume => volume.CapacityBytes);

    public long UsedBytes => Volumes.Sum(volume => volume.UsedBytes);

    public long FreeBytes => Volumes.Sum(volume => volume.FreeBytes);

    public IReadOnlyList<PhysicalDisk> PhysicalDisks { get; init; } = [];

    public IReadOnlySet<PhysicalDeviceId> PhysicalDeviceIds { get; init; } =
        Volumes.SelectMany(volume => volume.PhysicalDeviceIds).ToHashSet();

    public bool IsSupported =>
        ConnectionStatus == DeviceConnectionStatus.Online &&
        Volumes.Count > 0 &&
        Volumes.All(volume => volume.IsSupported && volume.Availability == VolumeAvailability.Available);
}

public enum ScanModeKind
{
    Standard,
    Deep,
}

public enum ScanState
{
    Idle,
    Starting,
    Scanning,
    Canceling,
    Canceled,
    Completed,
    Failed,
}

public sealed record ScanSession(
    Guid Id,
    PhysicalDeviceId SourceDeviceId,
    ScanModeKind Mode,
    DateTimeOffset StartedAt,
    ScanState State,
    TimeSpan? Duration = null,
    int FilesFound = 0,
    StorageDevice? Source = null);

public sealed record ScanProgress(
    Guid SessionId,
    ScanState State,
    string Phase,
    long BytesScanned,
    long TotalBytes,
    TimeSpan Elapsed,
    TimeSpan? EstimatedRemaining,
    int FilesFound)
{
    public double Percentage => TotalBytes <= 0
        ? 0
        : Math.Clamp(BytesScanned * 100d / TotalBytes, 0d, 100d);
}

public enum FileCategory
{
    All,
    Image,
    Document,
    Video,
    Audio,
    Archive,
    Unknown,
}

public enum RecoverabilityStatus
{
    Excellent,
    Good,
    Poor,
    Unknown,
}

public enum PreviewState
{
    Supported,
    Unsupported,
    Missing,
    Damaged,
}

public sealed record RecoverableFile(
    Guid Id,
    string Name,
    string OriginalPath,
    long SizeBytes,
    DateTimeOffset? ModifiedAt,
    FileCategory Category,
    RecoverabilityStatus Recoverability,
    PhysicalDeviceId SourceDeviceId,
    string PreviewDescription,
    PreviewState PreviewState = PreviewState.Supported);

public sealed record RecoveryRequest(
    Guid ScanSessionId,
    PhysicalDeviceId SourceDeviceId,
    PhysicalDeviceId DestinationDeviceId,
    string DestinationPath,
    IReadOnlyList<RecoverableFile> Files)
{
    public long RequiredBytes => Files.Sum(file => file.SizeBytes);
}

public sealed record RecoveredFileResult(Guid FileId, string DestinationPath, bool Succeeded, string? Error);

public sealed record RecoveryResult(
    Guid RequestId,
    IReadOnlyList<RecoveredFileResult> Files,
    DateTimeOffset CompletedAt,
    bool WasCanceled)
{
    public int SuccessCount => Files.Count(file => file.Succeeded);
}

public enum ThemePreference
{
    FollowSystem,
    Light,
    Dark,
}

public sealed record AppSettings(ThemePreference Theme, string LanguageCode)
{
    public static AppSettings Default { get; } = new(ThemePreference.FollowSystem, "en-US");
}
