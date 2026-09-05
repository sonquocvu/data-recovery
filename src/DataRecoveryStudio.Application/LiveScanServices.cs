using System.Security.Cryptography;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Application;

public interface ILiveScanTargetGrantAuthority
{
    long DiscoveryGeneration { get; }

    void UpdateDiscoverySnapshot(IReadOnlyList<StorageDevice> devices);

    LiveScanTargetGrant IssueGrant(StorageDevice selectedDevice);

    LiveScanTargetGrant ConsumeGrant(Guid grantId);
}

public sealed class LiveScanTargetGrantAuthority(
    TimeProvider? timeProvider = null,
    TimeSpan? grantLifetime = null,
    bool ntfsEnabled = true,
    bool fat32Enabled = false,
    bool exFatEnabled = false)
    : ILiveScanTargetGrantAuthority
{
    private const int MaximumOutstandingGrants = 1024;
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _grantLifetime = grantLifetime ?? TimeSpan.FromMinutes(2);
    private readonly Dictionary<Guid, LiveScanTargetGrant> _grants = [];
    private IReadOnlyList<StorageDevice> _snapshot = [];
    private long _generation;

    public long DiscoveryGeneration => Volatile.Read(ref _generation);

    public void UpdateDiscoverySnapshot(IReadOnlyList<StorageDevice> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        lock (_sync)
        {
            _snapshot = devices.ToArray();
            _generation = checked(_generation + 1);
        }
    }

    public LiveScanTargetGrant IssueGrant(StorageDevice selectedDevice)
    {
        ArgumentNullException.ThrowIfNull(selectedDevice);
        if (selectedDevice.Volumes.Count != 1)
            throw new LiveScanAuthorizationException(LiveScanAuthorizationError.UnsupportedTarget);
        return IssueGrant(selectedDevice, LiveScanScannerSelector.FromFileSystem(selectedDevice.Volumes[0].FileSystem));
    }

    public LiveScanTargetGrant IssueGrant(StorageDevice selectedDevice, LiveScanScannerKind scannerKind)
    {
        ArgumentNullException.ThrowIfNull(selectedDevice);
        if (!Enum.IsDefined(scannerKind)) throw new LiveScanAuthorizationException(LiveScanAuthorizationError.UnsupportedFileSystem);
        lock (_sync)
        {
            if (_generation == 0)
            {
                throw new LiveScanAuthorizationException(LiveScanAuthorizationError.NoCurrentDiscoverySnapshot);
            }

            var current = _snapshot.FirstOrDefault(device => ReferenceEquals(device, selectedDevice));
            if (current is null)
            {
                throw new LiveScanAuthorizationException(LiveScanAuthorizationError.TargetNotInCurrentSnapshot);
            }

            var volume = RequireEligibleVolume(current, scannerKind, ntfsEnabled, fat32Enabled, exFatEnabled);
            foreach (var expired in _grants.Where(item => item.Value.ExpiresAt <= _timeProvider.GetUtcNow()).Select(item => item.Key).ToArray())
            {
                _grants.Remove(expired);
            }

            if (_grants.Count >= MaximumOutstandingGrants)
            {
                var oldest = _grants.MinBy(item => item.Value.ExpiresAt).Key;
                _grants.Remove(oldest);
            }

            var diskNumbers = current.PhysicalDisks
                .Where(disk => disk.DiskNumber is not null)
                .Select(disk => disk.DiskNumber!.Value)
                .Distinct()
                .Order()
                .ToArray();
            if (diskNumbers.Length == 0)
            {
                throw new LiveScanAuthorizationException(LiveScanAuthorizationError.UnsupportedTarget);
            }

            var grant = new LiveScanTargetGrant(
                Guid.NewGuid(),
                _timeProvider.GetUtcNow().Add(_grantLifetime),
                CanonicalVolumeGuidPath.Parse(volume.VolumeGuidPath),
                SanitizeDisplayMountPath(volume.MountPath),
                volume.FileSystem,
                LiveScanIdentity.CreateVolumeIdentity(volume),
                volume.PhysicalDeviceIds.Select(identity => identity.Value).Order(StringComparer.Ordinal).ToArray(),
                diskNumbers,
                volume.CapacityBytes,
                _generation,
                IsMounted: true,
                IsLocal: true,
                IsSupported: true,
                IsConnected: true,
                Convert.ToHexString(RandomNumberGenerator.GetBytes(32)))
            {
                ScannerKind = scannerKind,
                CorrelationId = Guid.NewGuid(),
                Extents = volume.Extents.ToArray(),
            };
            _grants.Add(grant.GrantId, grant);
            return grant;
        }
    }

    public LiveScanTargetGrant ConsumeGrant(Guid grantId)
    {
        lock (_sync)
        {
            if (!_grants.Remove(grantId, out var grant))
            {
                throw new LiveScanAuthorizationException(LiveScanAuthorizationError.GrantAlreadyConsumed);
            }

            if (grant.DiscoveryGeneration != _generation)
            {
                throw new LiveScanAuthorizationException(LiveScanAuthorizationError.StaleDiscoveryGeneration);
            }

            if (grant.ExpiresAt <= _timeProvider.GetUtcNow())
            {
                throw new LiveScanAuthorizationException(LiveScanAuthorizationError.GrantExpired);
            }

            var current = _snapshot.FirstOrDefault(device =>
                device.Volumes.Any(volume => CanonicalVolumeGuidPath.TryParse(volume.VolumeGuidPath, out var canonical) &&
                    canonical.Equals(grant.CanonicalVolumeGuidPath, StringComparison.OrdinalIgnoreCase)));
            if (current is null)
            {
                throw new LiveScanAuthorizationException(LiveScanAuthorizationError.TargetNotInCurrentSnapshot);
            }

            var currentVolume = RequireEligibleVolume(current, grant.ScannerKind, ntfsEnabled, fat32Enabled, exFatEnabled);
            if (!LiveScanIdentity.CreateVolumeIdentity(currentVolume).Equals(grant.VolumeIdentity, StringComparison.Ordinal) ||
                !currentVolume.FileSystem.Equals(grant.FileSystem, StringComparison.OrdinalIgnoreCase))
            {
                throw new LiveScanAuthorizationException(LiveScanAuthorizationError.UnsupportedFileSystem);
            }
            if (grant.ScannerKind == LiveScanScannerKind.ExFatStandardMetadata &&
                (!currentVolume.Extents.SequenceEqual(grant.Extents) ||
                 !currentVolume.PhysicalDeviceIds.Select(d => d.Value).ToHashSet().SetEquals(grant.PhysicalDeviceIdentities)))
                throw new LiveScanAuthorizationException(LiveScanAuthorizationError.UnsupportedTarget);
            return grant;
        }
    }

    private static Volume RequireEligibleVolume(StorageDevice device, LiveScanScannerKind scannerKind, bool ntfsEnabled, bool fat32Enabled, bool exFatEnabled)
    {
        if (device.ConnectionStatus != DeviceConnectionStatus.Online)
        {
            throw new LiveScanAuthorizationException(LiveScanAuthorizationError.Disconnected);
        }

        if (device.Volumes.Count != 1)
        {
            throw new LiveScanAuthorizationException(LiveScanAuthorizationError.UnsupportedTarget);
        }

        var volume = device.Volumes[0];
        if (volume.Availability != VolumeAvailability.Available || volume.MountPaths.Count == 0 || string.IsNullOrWhiteSpace(volume.MountPath))
        {
            throw new LiveScanAuthorizationException(LiveScanAuthorizationError.NotMounted);
        }

        var expectedFileSystem = LiveScanScannerSelector.FileSystem(scannerKind);
        if (!volume.FileSystem.Equals(expectedFileSystem, StringComparison.OrdinalIgnoreCase))
        {
            throw new LiveScanAuthorizationException(LiveScanAuthorizationError.UnsupportedFileSystem);
        }

        if (scannerKind == LiveScanScannerKind.NtfsStandardMetadata && !ntfsEnabled ||
            scannerKind == LiveScanScannerKind.Fat32StandardMetadata && !fat32Enabled ||
            scannerKind == LiveScanScannerKind.ExFatStandardMetadata && !exFatEnabled)
        {
            throw new LiveScanAuthorizationException(LiveScanAuthorizationError.FeatureDisabled);
        }

        if (!volume.IsSupported || !device.IsSupported || volume.PhysicalDeviceIds.Count == 0 || volume.CapacityBytes <= 0)
        {
            throw new LiveScanAuthorizationException(LiveScanAuthorizationError.UnsupportedTarget);
        }

        if (!CanonicalVolumeGuidPath.TryParse(volume.VolumeGuidPath, out _))
        {
            throw new LiveScanAuthorizationException(LiveScanAuthorizationError.InvalidVolumePath);
        }

        if (scannerKind == LiveScanScannerKind.ExFatStandardMetadata &&
            (!LiveExFatValidation.ValidExtents(volume.Extents, volume.CapacityBytes,
                device.PhysicalDisks.Select(d => d.DiskNumber ?? -1)) ||
             !volume.PhysicalDeviceIds.SetEquals(device.PhysicalDisks.Select(d => d.Id))))
            throw new LiveScanAuthorizationException(LiveScanAuthorizationError.UnsupportedTarget);
        return volume;
    }

    private static string SanitizeDisplayMountPath(string path) =>
        path.Length <= 260 && !path.Any(character => character == '\0' || char.IsControl(character))
            ? path
            : string.Empty;
}

public static class LiveScanScannerSelector
{
    public static LiveScanScannerKind FromFileSystem(string? fileSystem) => fileSystem?.Trim().ToUpperInvariant() switch
    {
        "NTFS" => LiveScanScannerKind.NtfsStandardMetadata,
        "FAT32" => LiveScanScannerKind.Fat32StandardMetadata,
        "EXFAT" => LiveScanScannerKind.ExFatStandardMetadata,
        _ => throw new LiveScanAuthorizationException(LiveScanAuthorizationError.UnsupportedFileSystem),
    };

    public static string FileSystem(LiveScanScannerKind scannerKind) => scannerKind switch
    {
        LiveScanScannerKind.NtfsStandardMetadata => "NTFS",
        LiveScanScannerKind.Fat32StandardMetadata => "FAT32",
        LiveScanScannerKind.ExFatStandardMetadata => "exFAT",
        _ => throw new LiveScanAuthorizationException(LiveScanAuthorizationError.UnsupportedFileSystem),
    };
}

public sealed class HeadlessLiveStandardScanService(
    ILiveScanTargetGrantAuthority grantAuthority,
    ILiveScanWorkerClient workerClient)
{
    public Task<LiveScanResult> ScanAsync(
        StorageDevice selectedDevice,
        LiveScanBudgets budgets,
        IProgress<LiveScanProgressDto>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selectedDevice);
        ArgumentNullException.ThrowIfNull(budgets);
        budgets.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var issued = grantAuthority.IssueGrant(selectedDevice);
        cancellationToken.ThrowIfCancellationRequested();
        var consumed = grantAuthority.ConsumeGrant(issued.GrantId);
        return workerClient.ScanAsync(consumed, budgets, progress, cancellationToken);
    }
}

public static class LiveScanOutcomeLocalization
{
    public static string GetLocalizationKey(LiveScanTerminalStatus status) => status switch
    {
        LiveScanTerminalStatus.PermissionRequired => "LiveScan.PermissionRequired",
        LiveScanTerminalStatus.PermissionDeclined => "LiveScan.PermissionDeclined",
        LiveScanTerminalStatus.AccessDenied => "LiveScan.AccessDenied",
        LiveScanTerminalStatus.SourceRemoved => "LiveScan.VolumeUnavailable",
        LiveScanTerminalStatus.TargetChanged => "LiveScan.VolumeChanged",
        LiveScanTerminalStatus.UnsupportedFileSystem => "LiveScan.UnsupportedFileSystem",
        LiveScanTerminalStatus.WorkerMissing => "LiveScan.WorkerMissing",
        LiveScanTerminalStatus.WorkerVersionMismatch => "LiveScan.WorkerVersionMismatch",
        LiveScanTerminalStatus.WorkerStartFailed => "LiveScan.WorkerStartFailed",
        LiveScanTerminalStatus.SecureConnectionFailed => "LiveScan.SecureConnectionFailed",
        LiveScanTerminalStatus.WorkerCrashed => "LiveScan.WorkerCrashed",
        LiveScanTerminalStatus.TimedOut => "LiveScan.TimedOut",
        LiveScanTerminalStatus.Canceled => "LiveScan.Canceled",
        LiveScanTerminalStatus.Partial => "LiveScan.Partial",
        LiveScanTerminalStatus.ChangedDuringScan => "LiveScan.ChangedDuringScan",
        _ => "LiveScan.UnexpectedFailure",
    };
}

public static class LiveFat32ValidationOutcomeLocalization
{
    public static string GetLocalizationKey(LiveFat32ValidationOutcome outcome) => outcome switch
    {
        LiveFat32ValidationOutcome.Passed => "Validation.LiveFat32.Passed",
        LiveFat32ValidationOutcome.Failed => "Validation.LiveFat32.Failed",
        LiveFat32ValidationOutcome.Skipped => "Validation.LiveFat32.Skipped",
        LiveFat32ValidationOutcome.Inconclusive => "Validation.LiveFat32.Inconclusive",
        LiveFat32ValidationOutcome.ChangedDuringScan => "Validation.LiveFat32.ChangedDuringScan",
        LiveFat32ValidationOutcome.Canceled => "Validation.LiveFat32.Canceled",
        LiveFat32ValidationOutcome.SourceRemoved => "Validation.LiveFat32.SourceRemoved",
        LiveFat32ValidationOutcome.UacDenied => "Validation.LiveFat32.UacDenied",
        LiveFat32ValidationOutcome.TimedOut => "Validation.LiveFat32.TimedOut",
        LiveFat32ValidationOutcome.WorkerFailed => "Validation.LiveFat32.WorkerFailed",
        LiveFat32ValidationOutcome.ProtocolRejected => "Validation.LiveFat32.ProtocolRejected",
        _ => "Validation.LiveFat32.Failed",
    };
}
