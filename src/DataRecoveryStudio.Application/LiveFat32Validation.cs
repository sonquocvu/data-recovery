using System.Security.Cryptography;
using System.Text;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Application;

public enum LiveFat32TargetRejection
{
    MissingOptIn,
    MissingTarget,
    MalformedTarget,
    AmbiguousTarget,
    TargetNotFound,
    Disconnected,
    NotMounted,
    NotLocalFat32,
    UnsupportedTarget,
    MissingPhysicalMapping,
    SystemVolume,
    InstallationVolume,
}

public sealed class LiveFat32TargetValidationException(LiveFat32TargetRejection rejection)
    : InvalidOperationException(rejection.ToString())
{
    public LiveFat32TargetRejection Rejection { get; } = rejection;
}

public sealed record LiveFat32HardwareValidationOptions(
    bool IsEnabled,
    string? ExplicitTarget,
    bool RunCancellation,
    bool RunManualRemoval,
    bool RunWpfSmoke)
{
    public const string RunVariable = "DATA_RECOVERY_STUDIO_RUN_LIVE_FAT32_SCAN_INTEGRATION";
    public const string TargetVariable = "DATA_RECOVERY_STUDIO_LIVE_FAT32_SCAN_VOLUME";
    public const string CancellationVariable = "DATA_RECOVERY_STUDIO_RUN_LIVE_FAT32_CANCELLATION";
    public const string ManualRemovalVariable = "DATA_RECOVERY_STUDIO_RUN_LIVE_FAT32_MANUAL_REMOVAL";
    public const string WpfSmokeVariable = "DATA_RECOVERY_STUDIO_RUN_LIVE_FAT32_WPF_SMOKE";
    public const string MissingOptInReason = "Set DATA_RECOVERY_STUDIO_RUN_LIVE_FAT32_SCAN_INTEGRATION=1 and DATA_RECOVERY_STUDIO_LIVE_FAT32_SCAN_VOLUME to run the elevated read-only live FAT32 metadata validation.";

    public static LiveFat32HardwareValidationOptions FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        var target = read(TargetVariable);
        var enabled = string.Equals(read(RunVariable), "1", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(target);
        return new(
            enabled,
            target,
            string.Equals(read(CancellationVariable), "1", StringComparison.Ordinal),
            string.Equals(read(ManualRemovalVariable), "1", StringComparison.Ordinal),
            string.Equals(read(WpfSmokeVariable), "1", StringComparison.Ordinal));
    }
}

public sealed record ResolvedLiveFat32ValidationTarget(
    StorageDevice Device,
    Volume Volume,
    LiveFat32SanitizedTarget Sanitized);

public sealed class LiveFat32ControlledTargetResolver
{
    public ResolvedLiveFat32ValidationTarget Resolve(
        string suppliedTarget,
        IReadOnlyList<StorageDevice> snapshot,
        string? systemDirectory = null,
        string? installationDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suppliedTarget);
        ArgumentNullException.ThrowIfNull(snapshot);
        var supplied = suppliedTarget.Trim();
        var isCanonical = CanonicalVolumeGuidPath.TryParse(supplied, out var canonical);
        var isDriveRoot = TryNormalizeDriveRoot(supplied, out var driveRoot);
        if (!isCanonical && !isDriveRoot)
            throw new LiveFat32TargetValidationException(LiveFat32TargetRejection.MalformedTarget);

        var matches = snapshot
            .SelectMany(device => device.Volumes.Select(volume => (Device: device, Volume: volume)))
            .Where(item => isCanonical
                ? CanonicalVolumeGuidPath.TryParse(item.Volume.VolumeGuidPath, out var current) && current.Equals(canonical, StringComparison.OrdinalIgnoreCase)
                : item.Volume.MountPaths.Any(path => NormalizeMount(path).Equals(driveRoot, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (matches.Length == 0) throw new LiveFat32TargetValidationException(LiveFat32TargetRejection.TargetNotFound);
        if (matches.Length != 1) throw new LiveFat32TargetValidationException(LiveFat32TargetRejection.AmbiguousTarget);

        var match = matches[0];
        var device = match.Device;
        var volume = match.Volume;
        if (device.ConnectionStatus != DeviceConnectionStatus.Online || volume.Availability == VolumeAvailability.Disconnected)
            throw new LiveFat32TargetValidationException(LiveFat32TargetRejection.Disconnected);
        if (volume.Availability != VolumeAvailability.Available || volume.MountPaths.Count == 0 || string.IsNullOrWhiteSpace(volume.MountPath))
            throw new LiveFat32TargetValidationException(LiveFat32TargetRejection.NotMounted);
        if (!volume.FileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase))
            throw new LiveFat32TargetValidationException(LiveFat32TargetRejection.NotLocalFat32);
        if (!device.IsSupported || !volume.IsSupported || device.Type == StorageDeviceType.Unknown ||
            device.PhysicalDisks.Any(disk => disk.BusType == StorageBusType.Virtual) ||
            !CanonicalVolumeGuidPath.TryParse(volume.VolumeGuidPath, out _))
            throw new LiveFat32TargetValidationException(LiveFat32TargetRejection.UnsupportedTarget);
        var diskNumbers = device.PhysicalDisks.Where(disk => disk.DiskNumber is not null).Select(disk => disk.DiskNumber!.Value).Distinct().Order().ToArray();
        if (volume.PhysicalDeviceIds.Count == 0 || diskNumbers.Length == 0)
            throw new LiveFat32TargetValidationException(LiveFat32TargetRejection.MissingPhysicalMapping);

        var systemRoot = NormalizeRoot(systemDirectory ?? Environment.SystemDirectory);
        var installationRoot = NormalizeRoot(installationDirectory ?? AppContext.BaseDirectory);
        var targetRoots = volume.MountPaths.Select(NormalizeMount).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (systemRoot.Length > 0 && targetRoots.Contains(systemRoot))
            throw new LiveFat32TargetValidationException(LiveFat32TargetRejection.SystemVolume);
        if (installationRoot.Length > 0 && targetRoots.Contains(installationRoot))
            throw new LiveFat32TargetValidationException(LiveFat32TargetRejection.InstallationVolume);

        var physicalHashes = volume.PhysicalDeviceIds.Select(identity => Hash(identity.Value)).Order(StringComparer.Ordinal).ToArray();
        var sanitized = new LiveFat32SanitizedTarget(
            Sanitize(string.IsNullOrWhiteSpace(volume.Label) ? device.DisplayName : volume.Label, 128),
            Sanitize(NormalizeMount(volume.MountPath), 260),
            "FAT32",
            volume.CapacityBytes,
            Hash(LiveScanIdentity.CreateVolumeIdentity(volume)),
            physicalHashes,
            diskNumbers);
        return new(device, volume, sanitized);
    }

    private static bool TryNormalizeDriveRoot(string value, out string root)
    {
        root = string.Empty;
        if (value.Length is not (2 or 3) || !char.IsAsciiLetter(value[0]) || value[1] != ':' ||
            value.Length == 3 && value[2] is not ('\\' or '/'))
            return false;
        root = $"{char.ToUpperInvariant(value[0])}:\\";
        return true;
    }

    private static string NormalizeRoot(string path)
    {
        try
        {
            return NormalizeMount(Path.GetPathRoot(Path.GetFullPath(path)) ?? string.Empty);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeMount(string path) => path.Trim().Replace('/', '\\').TrimEnd('\\') + "\\";
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Sanitize(string value, int maximum) =>
        new(value.Where(character => character != '\0' && !char.IsControl(character)).Take(maximum).ToArray());
}

public sealed class LiveScanLifecycleCollector : IProgress<LiveScanWorkerLifecycleEvent>
{
    private readonly object _sync = new();
    private readonly List<LiveScanWorkerLifecycleEvent> _events = [];

    public IReadOnlyList<LiveScanWorkerLifecycleEvent> Snapshot()
    {
        lock (_sync) return _events.ToArray();
    }

    public void Report(LiveScanWorkerLifecycleEvent value)
    {
        lock (_sync) _events.Add(value);
    }
}

public sealed class MonotonicLiveFat32Progress : IProgress<LiveScanProgressDto>
{
    private readonly object _sync = new();
    private long _records;
    private long _bytes;
    private int _candidates;
    private int _directories;
    private int _directoryEntries;
    private int _fatEntries;

    public bool IsValid { get; private set; } = true;
    public bool ScanStarted { get; private set; }

    public void Report(LiveScanProgressDto value)
    {
        lock (_sync)
        {
            IsValid &= value.ScannerKind == LiveScanScannerKind.Fat32StandardMetadata &&
                value.RecordsProcessed >= _records && value.BytesRead >= _bytes && value.CandidatesFound >= _candidates &&
                value.DirectoriesExamined >= _directories && value.DirectoryEntriesExamined >= _directoryEntries &&
                value.FatEntriesInspected >= _fatEntries;
            ScanStarted |= value.Phase.StartsWith("Progress.Phase.Fat32.", StringComparison.Ordinal);
            _records = Math.Max(_records, value.RecordsProcessed);
            _bytes = Math.Max(_bytes, value.BytesRead);
            _candidates = Math.Max(_candidates, value.CandidatesFound);
            _directories = Math.Max(_directories, value.DirectoriesExamined);
            _directoryEntries = Math.Max(_directoryEntries, value.DirectoryEntriesExamined);
            _fatEntries = Math.Max(_fatEntries, value.FatEntriesInspected);
        }
    }
}

public sealed class CancelOnFat32ScanStartProgress(CancellationTokenSource cancellation) : IProgress<LiveScanProgressDto>
{
    private readonly MonotonicLiveFat32Progress _inner = new();
    private int _requested;

    public bool CancellationRequested => Volatile.Read(ref _requested) != 0;
    public bool IsMonotonic => _inner.IsValid;

    public void Report(LiveScanProgressDto value)
    {
        _inner.Report(value);
        if (value.ScannerKind == LiveScanScannerKind.Fat32StandardMetadata &&
            value.Phase.StartsWith("Progress.Phase.Fat32.", StringComparison.Ordinal) &&
            Interlocked.Exchange(ref _requested, 1) == 0)
        {
            cancellation.Cancel();
        }
    }
}

public sealed class LiveFat32ManualRemovalBoundary
{
    public const string NoInteractiveSessionReason = "Manual physical-removal validation requires an interactive operator session and DATA_RECOVERY_STUDIO_RUN_LIVE_FAT32_MANUAL_REMOVAL=1.";

    public static string CreatePrompt(LiveFat32SanitizedTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return $"Physically remove only the controlled target '{target.DisplayName}' at '{target.MountPath}'. Do not use software eject.";
    }

    public static bool IsMatchingTargetRemoved(string canonicalVolumeGuidPath, IReadOnlyList<StorageDevice> currentSnapshot)
    {
        var canonical = CanonicalVolumeGuidPath.Parse(canonicalVolumeGuidPath);
        ArgumentNullException.ThrowIfNull(currentSnapshot);
        return !currentSnapshot.Any(device => device.ConnectionStatus == DeviceConnectionStatus.Online &&
            device.Volumes.Any(volume =>
                CanonicalVolumeGuidPath.TryParse(volume.VolumeGuidPath, out var current) &&
                current.Equals(canonical, StringComparison.OrdinalIgnoreCase) &&
                volume.Availability != VolumeAvailability.Disconnected));
    }

    public static async Task<bool> MonitorAndCancelMatchingScanAsync(
        IDeviceDiscoveryService discovery,
        string canonicalVolumeGuidPath,
        LiveFat32SanitizedTarget target,
        CancellationTokenSource matchingScanCancellation,
        IProgress<string>? prompt,
        TimeSpan pollInterval,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(matchingScanCancellation);
        if (pollInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        prompt?.Report(CreatePrompt(target));
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await discovery.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
            if (IsMatchingTargetRemoved(canonicalVolumeGuidPath, snapshot))
            {
                matchingScanCancellation.Cancel();
                return true;
            }

            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }
}

public sealed class LiveFat32HardwareValidationHarness(
    IDeviceDiscoveryService discovery,
    ILiveScanTargetGrantAuthority grantAuthority,
    ILiveScanWorkerClient workerClient,
    LiveScanLifecycleCollector lifecycle,
    LiveFat32ControlledTargetResolver? resolver = null,
    TimeProvider? timeProvider = null,
    IProgress<LiveFat32SanitizedTarget>? preElevationTarget = null)
{
    private readonly LiveFat32ControlledTargetResolver _resolver = resolver ?? new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<LiveFat32ValidationReport> RunControlledScanAsync(
        LiveFat32HardwareValidationOptions options,
        LiveScanBudgets budgets,
        bool safetyAuditPassed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(budgets);
        var started = _timeProvider.GetUtcNow();
        if (!options.IsEnabled || string.IsNullOrWhiteSpace(options.ExplicitTarget))
        {
            var skipped = new LiveFat32ValidationScenarioResult(
                LiveFat32ValidationScenarioKind.ControlledScan,
                LiveFat32ValidationOutcome.Skipped,
                started,
                _timeProvider.GetUtcNow(),
                null,
                [],
                [],
                LiveFat32HardwareValidationOptions.MissingOptInReason);
            return CreateReport(null, started, [skipped], [], safetyAuditPassed);
        }

        ResolvedLiveFat32ValidationTarget target;
        try
        {
            var beforeSnapshot = await discovery.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
            target = _resolver.Resolve(options.ExplicitTarget, beforeSnapshot);
            preElevationTarget?.Report(target.Sanitized);
            grantAuthority.UpdateDiscoverySnapshot(beforeSnapshot);
        }
        catch (Exception exception) when (exception is LiveFat32TargetValidationException or LiveScanAuthorizationException)
        {
            var failed = Scenario(LiveFat32ValidationOutcome.Failed, started, null, [], $"TargetRejected.{exception.Message}");
            return CreateReport(null, started, [failed], [], safetyAuditPassed);
        }

        var generation = grantAuthority.DiscoveryGeneration;
        var invariants = new List<LiveFat32ValidationInvariant>
        {
            Invariant("ExplicitTargetResolved", true),
        };
        LiveScanTargetGrant grant;
        try
        {
            var issued = grantAuthority.IssueGrant(target.Device);
            grant = grantAuthority.ConsumeGrant(issued.GrantId);
            invariants.Add(Invariant("GrantCurrentGeneration", grant.DiscoveryGeneration == generation));
            invariants.Add(Invariant("GrantScannerBound", grant.ScannerKind == LiveScanScannerKind.Fat32StandardMetadata && grant.FileSystem == "FAT32"));
            invariants.Add(Invariant("GrantExpiring", grant.ExpiresAt > _timeProvider.GetUtcNow()));
            var replayRejected = false;
            try { _ = grantAuthority.ConsumeGrant(grant.GrantId); }
            catch (LiveScanAuthorizationException exception) when (exception.Error == LiveScanAuthorizationError.GrantAlreadyConsumed) { replayRejected = true; }
            invariants.Add(Invariant("GrantConsumedOnce", replayRejected));
        }
        catch (LiveScanAuthorizationException exception)
        {
            var failed = Scenario(LiveFat32ValidationOutcome.Failed, started, null, invariants, $"GrantRejected.{exception.Error}");
            return CreateReport(target.Sanitized, started, [failed], invariants, safetyAuditPassed);
        }

        var progress = new MonotonicLiveFat32Progress();
        LiveScanResult result;
        try
        {
            result = await workerClient.ScanAsync(grant, budgets, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var canceled = Scenario(LiveFat32ValidationOutcome.Canceled, started, LiveScanTerminalStatus.Canceled, invariants, "Canceled");
            return CreateReport(target.Sanitized, started, [canceled], invariants, safetyAuditPassed);
        }

        var afterMatches = await CaptureAfterSnapshotAsync(target, cancellationToken).ConfigureAwait(false);
        var terminal = result.Terminal;
        var lifecycleEvents = lifecycle.Snapshot().Where(item => item.CorrelationId == grant.CorrelationId).ToArray();
        invariants.Add(Invariant("CanonicalTargetMatch", grant.CanonicalVolumeGuidPath.Equals(CanonicalVolumeGuidPath.Parse(target.Volume.VolumeGuidPath), StringComparison.OrdinalIgnoreCase)));
        invariants.Add(Invariant("FilesystemMatch", grant.FileSystem == "FAT32" && afterMatches.FileSystem));
        invariants.Add(Invariant("CapacityMatch", afterMatches.Capacity));
        invariants.Add(Invariant("PhysicalIdentitySetMatch", afterMatches.PhysicalIdentities));
        invariants.Add(Invariant("ExtentSetMatch", afterMatches.Extents));
        invariants.Add(Invariant("Fat32GeometryValidated", terminal.Fat32GeometryValidated));
        invariants.Add(Invariant("ProductionScannerVersion", terminal.Fat32ScannerVersion == Fat32ScannerVersions.MetadataPhase7A));
        invariants.Add(Invariant("MonotonicProgress", progress.IsValid));
        invariants.Add(Invariant("ProtocolAggregateBounds", result.Candidates.Count <= LiveScanProtocol.MaximumCandidates && result.Diagnostics.Count <= LiveScanProtocol.MaximumDiagnostics));
        invariants.Add(Invariant("ExactlyOneTerminal", lifecycleEvents.Count(item => item.Kind == LiveScanWorkerLifecycleEventKind.TerminalAccepted) == 1));
        invariants.Add(Invariant("WorkerExited", lifecycleEvents.Any(item => item.Kind == LiveScanWorkerLifecycleEventKind.WorkerExited && item.ExitCode == 0)));
        invariants.Add(Invariant("WorkerDisposed", lifecycleEvents.Any(item => item.Kind == LiveScanWorkerLifecycleEventKind.WorkerDisposed)));
        invariants.Add(Invariant("SourceHandleDisposed", terminal.SourceHandleDisposed));
        invariants.Add(Invariant("NoIncompleteCandidateCatalog", terminal.Status is LiveScanTerminalStatus.Completed or LiveScanTerminalStatus.Partial || result.Candidates.Count == 0));
        invariants.Add(Invariant("RecoveryLockedOut", result.Candidates.All(candidate => candidate.ScannerKind == LiveScanScannerKind.Fat32StandardMetadata)));
        var consistencyStable = terminal.Consistency != LiveScanConsistency.ChangedDuringScan &&
            terminal.ConsistencyEvidenceBefore is not null && terminal.ConsistencyEvidenceBefore == terminal.ConsistencyEvidenceAfter;
        invariants.Add(Invariant("BoundedConsistencyEvidenceStable", consistencyStable));
        invariants.Add(Invariant("Fat32GeometryEvidenceStable", EvidenceMatches(terminal.Fat32GeometryEvidenceBefore, terminal.Fat32GeometryEvidenceAfter)));
        invariants.Add(Invariant("Fat32BootEvidenceStable", EvidenceMatches(terminal.Fat32BootEvidenceBefore, terminal.Fat32BootEvidenceAfter)));
        invariants.Add(Invariant("Fat32BootRelationshipStable", terminal.Fat32BootRelationshipAfter == terminal.Fat32BootRelationship));
        invariants.Add(Invariant("Fat32SelectedFatEvidenceStable", EvidenceMatches(terminal.Fat32SelectedFatEvidenceBefore, terminal.Fat32SelectedFatEvidenceAfter)));
        invariants.Add(Invariant("Fat32RootChainEvidenceStable", EvidenceMatches(terminal.Fat32RootChainEvidenceBefore, terminal.Fat32RootChainEvidenceAfter)));
        invariants.Add(Invariant("SafetyAudit", safetyAuditPassed));
        invariants.Add(Invariant("ProductionHardwarePath", discovery is IProductionDeviceDiscoveryService && workerClient is IProductionLiveScanWorkerClient));

        var outcome = MapOutcome(terminal.Status);
        if (outcome == LiveFat32ValidationOutcome.Passed && invariants.Any(item => !item.Passed))
            outcome = LiveFat32ValidationOutcome.Failed;
        var scenario = new LiveFat32ValidationScenarioResult(
            LiveFat32ValidationScenarioKind.ControlledScan,
            outcome,
            started,
            _timeProvider.GetUtcNow(),
            terminal.Status,
            lifecycleEvents,
            invariants.ToArray(),
            terminal.ReasonCode ?? terminal.Status.ToString())
        {
            Observations = CreateObservations(grant, terminal, lifecycleEvents, started),
        };
        return CreateReport(target.Sanitized, started, [scenario], invariants, safetyAuditPassed);
    }

    public async Task<LiveFat32ValidationScenarioResult> RunControlledCancellationAsync(
        LiveFat32HardwareValidationOptions options,
        LiveScanBudgets budgets,
        TimeSpan startTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(budgets);
        if (startTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(startTimeout));
        var started = _timeProvider.GetUtcNow();
        if (!options.IsEnabled || !options.RunCancellation || string.IsNullOrWhiteSpace(options.ExplicitTarget))
        {
            return new(
                LiveFat32ValidationScenarioKind.ControlledCancellation,
                LiveFat32ValidationOutcome.Skipped,
                started,
                _timeProvider.GetUtcNow(),
                null,
                [],
                [],
                "CancellationOptInMissing");
        }

        ResolvedLiveFat32ValidationTarget target;
        LiveScanTargetGrant grant;
        try
        {
            var snapshot = await discovery.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
            target = _resolver.Resolve(options.ExplicitTarget, snapshot);
            preElevationTarget?.Report(target.Sanitized);
            grantAuthority.UpdateDiscoverySnapshot(snapshot);
            grant = grantAuthority.ConsumeGrant(grantAuthority.IssueGrant(target.Device).GrantId);
        }
        catch (Exception exception) when (exception is LiveFat32TargetValidationException or LiveScanAuthorizationException)
        {
            return new(
                LiveFat32ValidationScenarioKind.ControlledCancellation,
                LiveFat32ValidationOutcome.Failed,
                started,
                _timeProvider.GetUtcNow(),
                null,
                [],
                [],
                $"TargetRejected.{exception.Message}");
        }

        using var cancelScan = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var progress = new CancelOnFat32ScanStartProgress(cancelScan);
        var scanTask = workerClient.ScanAsync(grant, budgets, progress, cancelScan.Token);
        var timeoutTask = Task.Delay(startTimeout, cancellationToken);
        var completed = await Task.WhenAny(scanTask, timeoutTask).ConfigureAwait(false);
        if (completed == timeoutTask)
        {
            cancellationToken.ThrowIfCancellationRequested();
            cancelScan.Cancel();
            try { _ = await scanTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
            return CancellationScenario(grant, started, LiveFat32ValidationOutcome.TimedOut, null, progress, [], "ScanStartTimedOut");
        }

        var result = await scanTask.ConfigureAwait(false);
        var events = lifecycle.Snapshot().Where(item => item.CorrelationId == grant.CorrelationId).ToArray();
        var invariants = new[]
        {
            Invariant("CancellationRequestedOnce", progress.CancellationRequested),
            Invariant("MonotonicProgress", progress.IsMonotonic),
            Invariant("ExactlyOneTerminal", events.Count(item => item.Kind == LiveScanWorkerLifecycleEventKind.TerminalAccepted) == 1),
            Invariant("NoIncompleteCandidateCatalog", result.Candidates.Count == 0),
            Invariant("WorkerDisposed", events.Any(item => item.Kind == LiveScanWorkerLifecycleEventKind.WorkerDisposed)),
        };
        if (!progress.CancellationRequested || result.Terminal.Status is LiveScanTerminalStatus.Completed or LiveScanTerminalStatus.Partial)
            return CancellationScenario(grant, started, LiveFat32ValidationOutcome.Inconclusive, result.Terminal.Status, progress, invariants, "CompletedBeforeCancellationObserved");
        var outcome = result.Terminal.Status == LiveScanTerminalStatus.Canceled && invariants.All(item => item.Passed)
            ? LiveFat32ValidationOutcome.Canceled
            : MapOutcome(result.Terminal.Status);
        return CancellationScenario(grant, started, outcome, result.Terminal.Status, progress, invariants, result.Terminal.ReasonCode ?? result.Terminal.Status.ToString());
    }

    private async Task<(bool FileSystem, bool Capacity, bool PhysicalIdentities, bool Extents)> CaptureAfterSnapshotAsync(
        ResolvedLiveFat32ValidationTarget target,
        CancellationToken cancellationToken)
    {
        var after = await discovery.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _resolver.Resolve(target.Volume.VolumeGuidPath, after);
            var beforePhysical = target.Volume.PhysicalDeviceIds.Select(item => item.Value).ToHashSet(StringComparer.Ordinal);
            var afterPhysical = current.Volume.PhysicalDeviceIds.Select(item => item.Value).ToHashSet(StringComparer.Ordinal);
            var beforeExtents = target.Device.PhysicalDisks.Where(item => item.DiskNumber is not null).Select(item => item.DiskNumber!.Value).ToHashSet();
            var afterExtents = current.Device.PhysicalDisks.Where(item => item.DiskNumber is not null).Select(item => item.DiskNumber!.Value).ToHashSet();
            return (
                current.Volume.FileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase),
                current.Volume.CapacityBytes == target.Volume.CapacityBytes,
                beforePhysical.SetEquals(afterPhysical),
                beforeExtents.SetEquals(afterExtents));
        }
        catch (LiveFat32TargetValidationException)
        {
            return (false, false, false, false);
        }
    }

    private LiveFat32ValidationReport CreateReport(
        LiveFat32SanitizedTarget? target,
        DateTimeOffset started,
        IReadOnlyList<LiveFat32ValidationScenarioResult> scenarios,
        IReadOnlyList<LiveFat32ValidationInvariant> invariants,
        bool safetyAuditPassed) => new(
            typeof(LiveFat32HardwareValidationHarness).Assembly.GetName().Version?.ToString() ?? "unknown",
            LiveScanProtocol.WorkerVersion,
            LiveScanProtocol.Version,
            Fat32ScannerVersions.MetadataPhase7A,
            target,
            started,
            _timeProvider.GetUtcNow(),
            scenarios,
            invariants,
            safetyAuditPassed,
            LiveFat32ReleaseReadiness.Evaluate(scenarios, invariants, safetyAuditPassed));

    private LiveFat32ValidationScenarioResult Scenario(
        LiveFat32ValidationOutcome outcome,
        DateTimeOffset started,
        LiveScanTerminalStatus? terminal,
        IReadOnlyList<LiveFat32ValidationInvariant> invariants,
        string detail) => new(
            LiveFat32ValidationScenarioKind.ControlledScan,
            outcome,
            started,
            _timeProvider.GetUtcNow(),
            terminal,
            lifecycle.Snapshot(),
            invariants.ToArray(),
            detail);

    private LiveFat32ValidationScenarioResult CancellationScenario(
        LiveScanTargetGrant grant,
        DateTimeOffset started,
        LiveFat32ValidationOutcome outcome,
        LiveScanTerminalStatus? terminal,
        CancelOnFat32ScanStartProgress progress,
        IReadOnlyList<LiveFat32ValidationInvariant> invariants,
        string detail) => new(
            LiveFat32ValidationScenarioKind.ControlledCancellation,
            outcome,
            started,
            _timeProvider.GetUtcNow(),
            terminal,
            lifecycle.Snapshot().Where(item => item.CorrelationId == grant.CorrelationId).ToArray(),
            invariants,
            detail);

    private static LiveFat32ValidationInvariant Invariant(string code, bool passed) =>
        new(code, passed, passed ? "Satisfied" : "Failed");

    private LiveFat32ValidationObservations CreateObservations(
        LiveScanTargetGrant grant,
        LiveScanTerminalResultDto terminal,
        IReadOnlyList<LiveScanWorkerLifecycleEvent> events,
        DateTimeOffset started)
    {
        var startedEvent = events.SingleOrDefault(item => item.Kind == LiveScanWorkerLifecycleEventKind.WorkerStarted);
        var exitEvent = events.LastOrDefault(item => item.Kind == LiveScanWorkerLifecycleEventKind.WorkerExited);
        return new(
            grant.ScannerKind,
            grant.DiscoveryGeneration,
            true,
            true,
            startedEvent?.ProcessId,
            events.Any(item => item.Kind == LiveScanWorkerLifecycleEventKind.HandshakeCompleted),
            terminal.Fat32GeometryValidated,
            terminal.Fat32BootRelationship,
            terminal.Fat32MirroringEnabled,
            terminal.Fat32FatCount,
            terminal.Fat32ActiveFatIndex,
            terminal.Fat32RootDirectoryCluster,
            terminal.DirectoriesExamined,
            terminal.DirectoryEntriesExamined,
            terminal.FatEntriesInspected,
            terminal.CandidateCount,
            terminal.DiagnosticCount,
            terminal.BytesRead,
            _timeProvider.GetUtcNow() - started,
            exitEvent?.ExitCode,
            terminal.SourceHandleDisposed);
    }

    private static bool EvidenceMatches(string? before, string? after) =>
        before is not null && before.Equals(after, StringComparison.Ordinal);

    private static LiveFat32ValidationOutcome MapOutcome(LiveScanTerminalStatus status) => status switch
    {
        LiveScanTerminalStatus.Completed => LiveFat32ValidationOutcome.Passed,
        LiveScanTerminalStatus.Partial => LiveFat32ValidationOutcome.Inconclusive,
        LiveScanTerminalStatus.ChangedDuringScan => LiveFat32ValidationOutcome.ChangedDuringScan,
        LiveScanTerminalStatus.Canceled => LiveFat32ValidationOutcome.Canceled,
        LiveScanTerminalStatus.SourceRemoved or LiveScanTerminalStatus.TargetChanged => LiveFat32ValidationOutcome.SourceRemoved,
        LiveScanTerminalStatus.PermissionDeclined => LiveFat32ValidationOutcome.UacDenied,
        LiveScanTerminalStatus.TimedOut => LiveFat32ValidationOutcome.TimedOut,
        LiveScanTerminalStatus.ProtocolFailure or LiveScanTerminalStatus.WorkerVersionMismatch => LiveFat32ValidationOutcome.ProtocolRejected,
        LiveScanTerminalStatus.WorkerCrashed or LiveScanTerminalStatus.WorkerMissing or LiveScanTerminalStatus.WorkerStartFailed => LiveFat32ValidationOutcome.WorkerFailed,
        _ => LiveFat32ValidationOutcome.Failed,
    };
}
