using System.Security.Cryptography;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

public sealed record ValidatedLiveScanTarget(LiveScanTargetGrant Grant);

public interface ILiveScanTargetValidator
{
    Task<ValidatedLiveScanTarget> ValidateAsync(LiveScanTargetGrant grant, CancellationToken cancellationToken);
}

public sealed class LiveScanTargetValidationException(LiveScanTerminalStatus status, string reasonCode)
    : InvalidOperationException(reasonCode)
{
    public LiveScanTerminalStatus Status { get; } = status;
    public string ReasonCode { get; } = reasonCode;
}

public sealed class WindowsVolumeOnlyLiveScanTargetValidator(IWindowsStorageNative? native = null) : ILiveScanTargetValidator
{
    private readonly IWindowsStorageNative _native = native ?? new WindowsStorageNative();

    public Task<ValidatedLiveScanTarget> ValidateAsync(LiveScanTargetGrant grant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var canonical = CanonicalVolumeGuidPath.Parse(grant.CanonicalVolumeGuidPath);
        if (!grant.IsMounted || !grant.IsLocal || !grant.IsSupported || !grant.IsConnected)
        {
            throw new LiveScanTargetValidationException(LiveScanTerminalStatus.TargetChanged, "GrantEligibilityChanged");
        }

        if (grant.PhysicalDeviceIdentities.Count == 0 || grant.PhysicalDiskNumbers.Count == 0)
        {
            throw new LiveScanTargetValidationException(LiveScanTerminalStatus.TargetChanged, "PhysicalIdentityChanged");
        }

        var volumeName = _native.EnumerateVolumeNames(cancellationToken)
            .FirstOrDefault(name => CanonicalVolumeGuidPath.TryParse(name, out var current) &&
                current.Equals(canonical, StringComparison.OrdinalIgnoreCase));
        if (volumeName is null)
        {
            throw new LiveScanTargetValidationException(LiveScanTerminalStatus.SourceRemoved, "VolumeNotFound");
        }

        var mountPaths = _native.GetVolumePathNames(volumeName)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (mountPaths.Length == 0)
        {
            throw new LiveScanTargetValidationException(LiveScanTerminalStatus.SourceRemoved, "VolumeDisconnected");
        }

        var preferredPath = StorageMetadataNormalizer.SelectPreferredMountPath(mountPaths);
        var driveType = _native.GetDriveType(preferredPath);
        if (driveType is not WindowsDriveType.Fixed and not WindowsDriveType.Removable)
        {
            throw new LiveScanTargetValidationException(LiveScanTerminalStatus.TargetChanged, "VolumeIsNotLocal");
        }

        var information = _native.GetVolumeInformation(volumeName);
        if (!information.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
        {
            throw new LiveScanTargetValidationException(LiveScanTerminalStatus.UnsupportedFileSystem, "FileSystemChanged");
        }

        var space = _native.GetDiskSpace(preferredPath);
        var capacity = checked((long)Math.Min(space.TotalBytes, long.MaxValue));
        var free = checked((long)Math.Min(space.FreeBytes, long.MaxValue));
        var physical = new PhysicalDeviceId(grant.PhysicalDeviceIdentities[0]);
        var currentVolume = new Volume(volumeName, preferredPath, string.Empty, "NTFS", capacity, Math.Max(0, capacity - Math.Min(capacity, free)), physical)
        {
            VolumeGuidPath = volumeName,
            MountPaths = mountPaths,
            PhysicalDeviceIds = grant.PhysicalDeviceIdentities.Select(value => new PhysicalDeviceId(value)).ToHashSet(),
        };
        if (capacity != grant.CapacityBytes ||
            !LiveScanIdentity.CreateVolumeIdentity(currentVolume).Equals(grant.VolumeIdentity, StringComparison.Ordinal))
        {
            throw new LiveScanTargetValidationException(LiveScanTerminalStatus.TargetChanged, "VolumeIdentityChanged");
        }

        IReadOnlyList<int> currentDiskNumbers;
        using (var handle = _native.OpenMetadataDevice(canonical, MetadataOpenOptions.ReadOnlyMetadata))
        {
            currentDiskNumbers = NativeStorageParser.ParseDiskExtents(
                _native.QueryDevice(handle, StorageNativeConstants.IoctlVolumeGetVolumeDiskExtents, []));
        }

        if (!grant.PhysicalDiskNumbers.ToHashSet().SetEquals(currentDiskNumbers))
        {
            throw new LiveScanTargetValidationException(LiveScanTerminalStatus.TargetChanged, "PhysicalIdentityChanged");
        }

        return Task.FromResult(new ValidatedLiveScanTarget(grant));
    }
}

public static class LiveScanHardLimits
{
    public static LiveScanBudgets Clamp(LiveScanBudgets requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        requested.Validate();
        return new(
            Math.Min(requested.MaximumBytesRead, 1024L * 1024 * 1024),
            Math.Min(requested.MaximumMftRecords, 1_000_000),
            Math.Min(requested.MaximumCandidates, LiveScanProtocol.MaximumCandidates),
            Math.Min(requested.MaximumDiagnostics, LiveScanProtocol.MaximumDiagnostics),
            Math.Min(requested.MaximumAttributesPerRecord, 256),
            Math.Min(requested.MaximumDataRuns, 32_768),
            Math.Min(requested.CandidateBatchSize, LiveScanProtocol.MaximumCandidateBatchSize),
            requested.EffectiveMaximumDuration < TimeSpan.FromMinutes(30) ? requested.EffectiveMaximumDuration : TimeSpan.FromMinutes(30),
            requested.EffectiveMaximumIdleDuration < TimeSpan.FromMinutes(2) ? requested.EffectiveMaximumIdleDuration : TimeSpan.FromMinutes(2));
    }

    public static StandardScanBudgets ToScannerBudgets(LiveScanBudgets budgets) => new(
        MaximumRecords: budgets.MaximumMftRecords,
        MaximumAttributesPerRecord: budgets.MaximumAttributesPerRecord,
        MaximumBytesRead: budgets.MaximumBytesRead,
        MaximumDiagnostics: budgets.MaximumDiagnostics,
        MaximumDataRuns: budgets.MaximumDataRuns,
        MaximumCandidates: budgets.MaximumCandidates);
}

public sealed record LiveScanExecutionResult(
    LiveScanTerminalResultDto Terminal,
    IReadOnlyList<LiveScanCandidateDto> Candidates,
    IReadOnlyList<LiveScanDiagnosticDto> Diagnostics);

public interface ILiveScanExecutor
{
    Task<LiveScanExecutionResult> ExecuteAsync(
        Guid sessionId,
        LiveScanTargetGrant grant,
        LiveScanBudgets requestedBudgets,
        IProgress<LiveScanProgressDto>? progress,
        CancellationToken cancellationToken);
}

public sealed class LiveScanExecutor(
    ILiveScanTargetValidator targetValidator,
    ILiveVolumeSourceFactory sourceFactory,
    INtfsMetadataScanner scanner) : ILiveScanExecutor
{
    public async Task<LiveScanExecutionResult> ExecuteAsync(
        Guid sessionId,
        LiveScanTargetGrant grant,
        LiveScanBudgets requestedBudgets,
        IProgress<LiveScanProgressDto>? progress,
        CancellationToken cancellationToken)
    {
        var validated = await targetValidator.ValidateAsync(grant, cancellationToken).ConfigureAwait(false);
        var budgets = LiveScanHardLimits.Clamp(requestedBudgets);
        var consistencyOverhead = checked(2L * (LiveScanProtocol.MaximumIndividualReadBytes + 512));
        await using var source = sourceFactory.Open(validated.Grant, checked(budgets.MaximumBytesRead + consistencyOverhead));
        var before = await ReadFingerprintAsync(source, cancellationToken).ConfigureAwait(false);
        var scannerProgress = progress is null ? null : new ThrottledMonotonicProgress(progress, TimeProvider.System);
        var result = await scanner.ScanAsync(
            source,
            new StandardScanRequest(new NtfsVolumeContext(0), LiveScanHardLimits.ToScannerBudgets(budgets)),
            scannerProgress,
            cancellationToken).ConfigureAwait(false);
        BootstrapFingerprint? after = null;
        try
        {
            after = await ReadFingerprintAsync(source, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or ArgumentOutOfRangeException)
        {
        }

        var geometryChanged = after is null || before.Geometry != after.Geometry;
        var mftChanged = after is not null && !before.MftFingerprint.AsSpan().SequenceEqual(after.MftFingerprint);
        var changed = geometryChanged || mftChanged;

        var candidates = result.Candidates.Take(budgets.MaximumCandidates)
            .Select(candidate => NormalizeCandidate(sessionId, candidate))
            .ToArray();
        var diagnostics = result.Diagnostics.Take(budgets.MaximumDiagnostics)
            .Select(item => new LiveScanDiagnosticDto(SanitizeCode(item.Code), item.Severity, SanitizeCode(item.Operation)))
            .ToArray();
        var isPartial = changed || result.Outcome != StandardScanOutcome.Completed || candidates.Length < result.Candidates.Count;
        var status = changed
            ? LiveScanTerminalStatus.ChangedDuringScan
            : isPartial ? LiveScanTerminalStatus.Partial : LiveScanTerminalStatus.Completed;
        var consistency = changed
            ? LiveScanConsistency.ChangedDuringScan
            : isPartial ? LiveScanConsistency.Partial : LiveScanConsistency.LiveBestEffort;
        var reason = geometryChanged ? "CriticalGeometryChanged" : mftChanged ? "MftLayoutChanged" : result.PartialReason;
        var terminal = new LiveScanTerminalResultDto(
            status,
            consistency,
            candidates.Length,
            diagnostics.Length,
            result.RecordsProcessed,
            result.BytesRead,
            isPartial,
            reason is null ? null : SanitizeCode(reason));
        return new(terminal, candidates, diagnostics);
    }

    private static async Task<BootstrapFingerprint> ReadFingerprintAsync(IReadOnlyRandomAccessSource source, CancellationToken cancellationToken)
    {
        var boot = new byte[512];
        await source.ReadExactlyAsync(0, boot, cancellationToken).ConfigureAwait(false);
        if (!NtfsBootSectorParser.TryParse(boot, out var geometry, out _, out _))
        {
            throw new InvalidDataException("The live NTFS bootstrap metadata is invalid.");
        }

        var valid = geometry!;
        var recordSize = Math.Min(valid.FileRecordSize, LiveScanProtocol.MaximumIndividualReadBytes);
        var offset = checked((long)valid.MftLogicalClusterNumber * valid.ClusterSize);
        var mft = new byte[recordSize];
        await source.ReadExactlyAsync(offset, mft, cancellationToken).ConfigureAwait(false);
        return new(valid, SHA256.HashData(mft));
    }

    private static LiveScanCandidateDto NormalizeCandidate(Guid sessionId, DeletedFileCandidate candidate)
    {
        var identityMaterial = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{sessionId:N}|{candidate.MftRecordNumber}|{candidate.SequenceNumber}"));
        var guidBytes = identityMaterial.AsSpan(0, 16).ToArray();
        var streams = candidate.DataStreams.Take(128).Select(stream => new LiveScanStreamSummaryDto(
            SanitizeText(stream.Name ?? string.Empty, 255),
            Math.Max(0, stream.LogicalSize),
            stream.Storage,
            stream.AllocationState,
            stream.IsSparse,
            stream.IsCompressed,
            stream.IsEncrypted)).ToArray();
        var name = SanitizeText(candidate.Name, 255);
        return new(
            new Guid(guidBytes),
            sessionId,
            candidate.MftRecordNumber,
            candidate.SequenceNumber,
            name,
            SanitizeText(candidate.OriginalPath, LiveScanProtocol.MaximumStringCharacters),
            Math.Max(0, candidate.LogicalSize),
            Categorize(name),
            IsDeleted: true,
            candidate.IsDirectory,
            candidate.PathState,
            candidate.Recoverability,
            streams,
            []);
    }

    private static FileCategory Categorize(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".tif" or ".tiff" => FileCategory.Image,
        ".doc" or ".docx" or ".pdf" or ".txt" or ".xls" or ".xlsx" => FileCategory.Document,
        ".mp4" or ".mov" or ".avi" or ".mkv" => FileCategory.Video,
        ".mp3" or ".wav" or ".flac" or ".aac" => FileCategory.Audio,
        ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => FileCategory.Archive,
        _ => FileCategory.Unknown,
    };

    private static string SanitizeText(string value, int maximum)
    {
        var cleaned = new string(value.Where(character => character != '\0' && !char.IsControl(character)).Take(maximum).ToArray());
        return cleaned;
    }

    private static string SanitizeCode(string value) =>
        new(value.Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.').Take(128).ToArray());

    private sealed record BootstrapFingerprint(NtfsBootGeometry Geometry, byte[] MftFingerprint);
}

internal sealed class ThrottledMonotonicProgress(IProgress<LiveScanProgressDto> inner, TimeProvider clock)
    : IProgress<StandardScanProgress>
{
    private readonly object _sync = new();
    private long _records;
    private long _bytes;
    private int _candidates;
    private DateTimeOffset _last;

    public void Report(StandardScanProgress value)
    {
        lock (_sync)
        {
            var now = clock.GetUtcNow();
            _records = Math.Max(_records, value.RecordsProcessed);
            _bytes = Math.Max(_bytes, value.BytesRead);
            _candidates = Math.Max(_candidates, value.CandidatesFound);
            if (_last != default && now - _last < TimeSpan.FromMilliseconds(250) && value.RecordsProcessed < value.TotalRecords)
            {
                return;
            }

            _last = now;
            inner.Report(new(_records, Math.Max(_records, value.TotalRecords), _bytes, _candidates, value.Phase[..Math.Min(value.Phase.Length, 256)]));
        }
    }
}
