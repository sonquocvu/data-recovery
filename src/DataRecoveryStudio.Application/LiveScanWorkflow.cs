using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Application;

public enum ScanCapabilityKind
{
    LiveNtfsStandardScanAvailable,
    LiveFat32StandardScanAvailable,
    LiveFat32StandardScanFeatureDisabled,
    LiveStandardScanFeatureDisabled,
    AdministratorPermissionRequired,
    UnsupportedFilesystem,
    UnsupportedDeviceType,
    Disconnected,
    UnmappedPhysicalIdentity,
    DevelopmentMock,
    DeepScanNotImplemented,
}

public sealed record ScanCapability(
    ScanCapabilityKind Kind,
    string ReasonKey,
    bool CanStartStandard,
    bool CanStartDeep,
    bool IsLive,
    bool RequiresAdministratorPermission);

public static class LiveStandardScanFeatureGate
{
    public const string EnvironmentVariable = "DATA_RECOVERY_STUDIO_ENABLE_LIVE_STANDARD_SCAN";

    public static bool IsEnabled(Func<string, string?>? readEnvironmentVariable = null)
    {
        readEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        return string.Equals(readEnvironmentVariable(EnvironmentVariable), "1", StringComparison.Ordinal);
    }
}

public static class LiveFat32StandardScanFeatureGate
{
    public const string EnvironmentVariable = "DATA_RECOVERY_STUDIO_ENABLE_LIVE_FAT32_STANDARD_SCAN";

    public static bool IsEnabled(Func<string, string?>? readEnvironmentVariable = null)
    {
        readEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        return string.Equals(readEnvironmentVariable(EnvironmentVariable), "1", StringComparison.Ordinal);
    }
}

public static class ScanCapabilityEvaluator
{
    public static ScanCapability Evaluate(StorageDevice device, bool isDevelopmentMode, bool liveStandardScanEnabled)
        => Evaluate(device, isDevelopmentMode, liveStandardScanEnabled, false);

    public static ScanCapability Evaluate(
        StorageDevice device,
        bool isDevelopmentMode,
        bool liveNtfsStandardScanEnabled,
        bool liveFat32StandardScanEnabled)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (isDevelopmentMode)
        {
            return new(ScanCapabilityKind.DevelopmentMock, "Capability.DevelopmentMock", true, true, false, false);
        }

        if (device.ConnectionStatus != DeviceConnectionStatus.Online ||
            device.Volumes.Any(volume => volume.Availability == VolumeAvailability.Disconnected))
        {
            return Unavailable(ScanCapabilityKind.Disconnected, "Capability.Disconnected");
        }

        if (device.Volumes.Count != 1)
        {
            return Unavailable(ScanCapabilityKind.UnsupportedDeviceType, "Capability.UnsupportedDeviceType");
        }

        var volume = device.Volumes[0];
        if (volume.Availability != VolumeAvailability.Available ||
            volume.MountPaths.Count == 0 ||
            string.IsNullOrWhiteSpace(volume.MountPath))
        {
            return Unavailable(ScanCapabilityKind.UnsupportedDeviceType, "Capability.NotMounted");
        }

        var isNtfs = volume.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
        var isFat32 = volume.FileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase);
        if (!isNtfs && !isFat32)
        {
            return Unavailable(ScanCapabilityKind.UnsupportedFilesystem,
                volume.FileSystem.Equals("exFAT", StringComparison.OrdinalIgnoreCase)
                    ? "Capability.FilesystemComingLater"
                    : "Capability.UnsupportedFilesystem");
        }

        if (!volume.IsSupported)
        {
            var reason = volume.UnsupportedReasonKey switch
            {
                "Device.Unsupported.PhysicalMapping" => "Capability.UnmappedPhysicalIdentity",
                "Device.Unsupported.Network" or "Device.Unsupported.Optical" or "Device.Unsupported.RamDisk" or
                    "Device.Unsupported.DriveType" => "Capability.UnsupportedDeviceType",
                _ => "Capability.UnsupportedVolume",
            };
            var kind = reason == "Capability.UnmappedPhysicalIdentity"
                ? ScanCapabilityKind.UnmappedPhysicalIdentity
                : ScanCapabilityKind.UnsupportedDeviceType;
            return Unavailable(kind, reason);
        }

        if (volume.PhysicalDeviceIds.Count == 0 ||
            device.PhysicalDisks.Count == 0 ||
            device.PhysicalDisks.Any(disk => disk.DiskNumber is null) ||
            !CanonicalVolumeGuidPath.TryParse(volume.VolumeGuidPath, out _))
        {
            return Unavailable(ScanCapabilityKind.UnmappedPhysicalIdentity, "Capability.UnmappedPhysicalIdentity");
        }

        if (isNtfs && !liveNtfsStandardScanEnabled)
        {
            return Unavailable(ScanCapabilityKind.LiveStandardScanFeatureDisabled, "Capability.LivePreviewDisabled");
        }

        if (isFat32 && !liveFat32StandardScanEnabled)
        {
            return Unavailable(ScanCapabilityKind.LiveFat32StandardScanFeatureDisabled, "Capability.LiveFat32PreviewDisabled");
        }

        return new(
            isFat32 ? ScanCapabilityKind.LiveFat32StandardScanAvailable : ScanCapabilityKind.LiveNtfsStandardScanAvailable,
            isFat32 ? "Capability.LiveFat32Available" : "Capability.LiveNtfsAvailable",
            true,
            false,
            true,
            true);
    }

    private static ScanCapability Unavailable(ScanCapabilityKind kind, string reasonKey) =>
        new(kind, reasonKey, false, false, false, false);
}

public enum LiveScanUiState
{
    Idle,
    ValidatingSelection,
    RequestingPermission,
    LaunchingWorker,
    ConnectingSecureChannel,
    Scanning,
    ReceivingResults,
    CancelRequested,
    Canceling,
    Completed,
    CompletedPartial,
    PermissionDeclined,
    SourceRemoved,
    TimedOut,
    WorkerFailed,
    Failed,
    Canceled,
}

public sealed class LiveScanStateMachine
{
    private static readonly IReadOnlyDictionary<LiveScanUiState, IReadOnlySet<LiveScanUiState>> AllowedTransitions =
        new Dictionary<LiveScanUiState, IReadOnlySet<LiveScanUiState>>
        {
            [LiveScanUiState.Idle] = Set(LiveScanUiState.ValidatingSelection),
            [LiveScanUiState.ValidatingSelection] = Set(LiveScanUiState.RequestingPermission, LiveScanUiState.ReceivingResults, LiveScanUiState.Completed, LiveScanUiState.CompletedPartial, LiveScanUiState.PermissionDeclined, LiveScanUiState.WorkerFailed, LiveScanUiState.Failed, LiveScanUiState.SourceRemoved, LiveScanUiState.TimedOut, LiveScanUiState.Canceled, LiveScanUiState.CancelRequested),
            [LiveScanUiState.RequestingPermission] = Set(LiveScanUiState.LaunchingWorker, LiveScanUiState.ReceivingResults, LiveScanUiState.Completed, LiveScanUiState.CompletedPartial, LiveScanUiState.PermissionDeclined, LiveScanUiState.WorkerFailed, LiveScanUiState.Failed, LiveScanUiState.SourceRemoved, LiveScanUiState.TimedOut, LiveScanUiState.Canceled, LiveScanUiState.CancelRequested),
            [LiveScanUiState.LaunchingWorker] = Set(LiveScanUiState.ConnectingSecureChannel, LiveScanUiState.ReceivingResults, LiveScanUiState.Completed, LiveScanUiState.CompletedPartial, LiveScanUiState.PermissionDeclined, LiveScanUiState.WorkerFailed, LiveScanUiState.Failed, LiveScanUiState.SourceRemoved, LiveScanUiState.TimedOut, LiveScanUiState.Canceled, LiveScanUiState.CancelRequested),
            [LiveScanUiState.ConnectingSecureChannel] = Set(LiveScanUiState.Scanning, LiveScanUiState.ReceivingResults, LiveScanUiState.Completed, LiveScanUiState.CompletedPartial, LiveScanUiState.WorkerFailed, LiveScanUiState.Failed, LiveScanUiState.SourceRemoved, LiveScanUiState.TimedOut, LiveScanUiState.Canceled, LiveScanUiState.CancelRequested),
            [LiveScanUiState.Scanning] = Set(LiveScanUiState.ReceivingResults, LiveScanUiState.Completed, LiveScanUiState.CompletedPartial, LiveScanUiState.SourceRemoved, LiveScanUiState.TimedOut, LiveScanUiState.WorkerFailed, LiveScanUiState.Failed, LiveScanUiState.Canceled, LiveScanUiState.CancelRequested),
            [LiveScanUiState.ReceivingResults] = Set(LiveScanUiState.Completed, LiveScanUiState.CompletedPartial, LiveScanUiState.SourceRemoved, LiveScanUiState.WorkerFailed, LiveScanUiState.Failed, LiveScanUiState.Canceled, LiveScanUiState.CancelRequested),
            [LiveScanUiState.CancelRequested] = Set(LiveScanUiState.Canceling, LiveScanUiState.Canceled, LiveScanUiState.SourceRemoved),
            [LiveScanUiState.Canceling] = Set(LiveScanUiState.Canceled, LiveScanUiState.SourceRemoved),
        };

    public LiveScanUiState State { get; private set; } = LiveScanUiState.Idle;

    public bool IsActive => State is LiveScanUiState.ValidatingSelection or LiveScanUiState.RequestingPermission or
        LiveScanUiState.LaunchingWorker or LiveScanUiState.ConnectingSecureChannel or LiveScanUiState.Scanning or
        LiveScanUiState.ReceivingResults or LiveScanUiState.CancelRequested or LiveScanUiState.Canceling;

    public bool IsTerminal => State is LiveScanUiState.Completed or LiveScanUiState.CompletedPartial or
        LiveScanUiState.PermissionDeclined or LiveScanUiState.SourceRemoved or LiveScanUiState.TimedOut or
        LiveScanUiState.WorkerFailed or LiveScanUiState.Failed or LiveScanUiState.Canceled;

    public bool TryTransition(LiveScanUiState next)
    {
        if (!AllowedTransitions.TryGetValue(State, out var allowed) || !allowed.Contains(next))
        {
            return false;
        }

        State = next;
        return true;
    }

    private static IReadOnlySet<LiveScanUiState> Set(params LiveScanUiState[] states) => states.ToHashSet();
}

public static class LiveScanClientPhase
{
    public const string RequestingPermission = "LiveClient.RequestingPermission";
    public const string LaunchingWorker = "LiveClient.LaunchingWorker";
    public const string ConnectingSecureChannel = "LiveClient.ConnectingSecureChannel";
    public const string ReceivingResults = "LiveClient.ReceivingResults";
}

public interface ILiveScanUiOrchestrator
{
    void UpdateDiscoverySnapshot(IReadOnlyList<StorageDevice> devices);

    Task<LiveScanResult> ScanAsync(
        StorageDevice source,
        LiveScanBudgets budgets,
        IProgress<LiveScanProgressDto>? progress,
        CancellationToken cancellationToken);
}

public sealed class LiveScanUiOrchestrator(
    ILiveScanTargetGrantAuthority grantAuthority,
    HeadlessLiveStandardScanService scanService) : ILiveScanUiOrchestrator
{
    public void UpdateDiscoverySnapshot(IReadOnlyList<StorageDevice> devices) =>
        grantAuthority.UpdateDiscoverySnapshot(devices);

    public Task<LiveScanResult> ScanAsync(
        StorageDevice source,
        LiveScanBudgets budgets,
        IProgress<LiveScanProgressDto>? progress,
        CancellationToken cancellationToken) =>
        scanService.ScanAsync(source, budgets, progress, cancellationToken);
}

public sealed class UnavailableScanService : IScanService
{
    public Task<ScanSession> ScanAsync(StorageDevice source, ScanModeKind mode, IProgress<ScanProgress> progress, CancellationToken cancellationToken) =>
        Task.FromException<ScanSession>(new InvalidOperationException("A simulated scan service is not available in production mode."));
}

public sealed class UnavailableRecoveryCatalogService : IRecoveryCatalogService
{
    public Task<IReadOnlyList<RecoverableFile>> GetResultsAsync(ScanSession session, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RecoverableFile>>([]);
}

public sealed record LiveScanUiSession(
    StorageDevice Source,
    LiveScanResult Result,
    DateTimeOffset StartedAt,
    TimeSpan Duration);
