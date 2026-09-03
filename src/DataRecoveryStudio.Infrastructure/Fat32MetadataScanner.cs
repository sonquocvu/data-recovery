using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

public sealed class Fat32MetadataScanner : IFat32MetadataScanner
{
    internal const string ParserVersion = Fat32ScannerVersions.MetadataPhase7A;

    public async Task<Fat32ScanResult> ScanAsync(
        IReadOnlyRandomAccessSource source,
        Fat32ScanRequest request,
        IProgress<Fat32ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        var budget = request.EffectiveBudget;
        budget.Validate();
        if (request.Volume.VolumeOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The FAT32 volume offset must be non-negative.");
        }

        var state = new Fat32ScanState(source, request, progress);
        try
        {
            state.Checkpoint(Fat32ScanPhase.ReadingBootSectors, cancellationToken, forceProgress: true);
            var bootstrap = await ReadBootSectorsAsync(state, cancellationToken).ConfigureAwait(false);
            if (bootstrap.Geometry is null)
            {
                return state.Result(Fat32ScanOutcome.InvalidVolume, null, null);
            }

            var geometry = bootstrap.Geometry;
            state.Geometry = geometry;
            state.Checkpoint(Fat32ScanPhase.ReadingFsInfo, cancellationToken, forceProgress: true);
            var fsInfo = await ReadFsInfoAsync(state, geometry, cancellationToken).ConfigureAwait(false);
            var fat = new Fat32AllocationTable(state, geometry);
            await fat.ValidateReservedEntriesAsync(cancellationToken).ConfigureAwait(false);
            state.Checkpoint(Fat32ScanPhase.TraversingDirectories, cancellationToken, forceProgress: true);
            await TraverseDirectoriesAsync(state, fat, cancellationToken).ConfigureAwait(false);
            state.Checkpoint(Fat32ScanPhase.Finalizing, cancellationToken, forceProgress: true);
            state.Candidates.Sort(Fat32CandidateComparer.Instance);
            state.Checkpoint(Fat32ScanPhase.Completed, cancellationToken, forceProgress: true);
            return state.Result(state.IsPartial ? Fat32ScanOutcome.Partial : Fat32ScanOutcome.Completed, geometry, fsInfo);
        }
        catch (OperationCanceledException)
        {
            state.Diagnostics.Add("FAT32_CANCELED", ScanDiagnosticSeverity.Information, "scan", "The FAT32 metadata scan was canceled; validated partial candidates were retained.");
            state.Report(Fat32ScanPhase.Canceled, true);
            state.Candidates.Sort(Fat32CandidateComparer.Instance);
            return state.Result(Fat32ScanOutcome.Canceled, state.Geometry, state.FsInfo, "Cancellation");
        }
        catch (Fat32LimitException exception)
        {
            state.ReachBudget(exception.BudgetName, exception.Operation);
            state.Report(Fat32ScanPhase.Partial, true);
            state.Candidates.Sort(Fat32CandidateComparer.Instance);
            return state.Result(Fat32ScanOutcome.Partial, state.Geometry, state.FsInfo, exception.BudgetName);
        }
        catch (Exception exception) when (exception is IOException or EndOfStreamException or ArgumentOutOfRangeException or OverflowException)
        {
            state.Diagnostics.Add("FAT32_INVALID_GEOMETRY", ScanDiagnosticSeverity.Error, "scan", "A bounded FAT32 metadata read or checked geometry calculation failed.");
            return state.Result(Fat32ScanOutcome.InvalidVolume, state.Geometry, state.FsInfo);
        }
    }

    private static async Task<BootSelection> ReadBootSectorsAsync(Fat32ScanState state, CancellationToken cancellationToken)
    {
        var primaryBytes = new byte[512];
        if (!await state.TryReadAsync(state.VolumeOffset, primaryBytes, "primary-boot", cancellationToken).ConfigureAwait(false))
        {
            state.Diagnostics.Add("FAT32_PRIMARY_BOOT_INVALID", ScanDiagnosticSeverity.Error, "boot-sector", "The primary boot sector could not be read.", state.VolumeOffset);
            return default;
        }

        var primary = Fat32BootSectorParser.Parse(primaryBytes, state.Source.Length, state.VolumeOffset, false);
        if (!primary.IsValid)
        {
            state.Diagnostics.Add("FAT32_PRIMARY_BOOT_INVALID", ScanDiagnosticSeverity.Warning, "boot-sector", primary.Reason, state.VolumeOffset);
        }

        var raw = Fat32BootSectorParser.ReadBackupLocator(primaryBytes);
        Fat32BootParseResult? backup = null;
        if (raw is { } locator && locator.BackupSector is not 0 and not ushort.MaxValue)
        {
            try
            {
                var relative = checked((long)locator.BackupSector * locator.BytesPerSector);
                var offset = checked(state.VolumeOffset + relative);
                var declaredBytes = checked((long)locator.TotalSectors * locator.BytesPerSector);
                if (locator.BackupSector >= locator.ReservedSectors || relative + 512 > declaredBytes || offset + 512 > state.Source.Length)
                {
                    state.Diagnostics.Add("FAT32_BACKUP_BOOT_INVALID", ScanDiagnosticSeverity.Warning, "backup-boot", "The declared backup boot sector is outside the bounded volume.", offset);
                }
                else
                {
                    var backupBytes = new byte[512];
                    if (await state.TryReadAsync(offset, backupBytes, "backup-boot", cancellationToken).ConfigureAwait(false))
                    {
                        backup = Fat32BootSectorParser.Parse(backupBytes, state.Source.Length, state.VolumeOffset, true);
                        if (!backup.Value.IsValid)
                        {
                            state.Diagnostics.Add("FAT32_BACKUP_BOOT_INVALID", ScanDiagnosticSeverity.Warning, "backup-boot", backup.Value.Reason, offset);
                        }
                    }
                }
            }
            catch (OverflowException)
            {
                state.Diagnostics.Add("FAT32_BACKUP_BOOT_INVALID", ScanDiagnosticSeverity.Warning, "backup-boot", "The backup boot-sector address overflowed.");
            }
        }

        if (primary.IsValid)
        {
            if (backup is { IsValid: true } && !Fat32BootSectorParser.CriticalGeometryMatches(primary.Geometry!, backup.Value.Geometry!))
            {
                state.Diagnostics.Add("FAT32_BOOT_SECTOR_DISAGREEMENT", ScanDiagnosticSeverity.Warning, "boot-sector", "Primary and backup boot sectors disagree on critical FAT32 geometry.");
            }

            return new(primary.Geometry);
        }

        if (backup is { IsValid: true })
        {
            return new(backup.Value.Geometry);
        }

        state.Diagnostics.Add(primary.Code, ScanDiagnosticSeverity.Error, "boot-sector", primary.Reason, state.VolumeOffset);
        return default;
    }

    private static async Task<Fat32FsInfo?> ReadFsInfoAsync(Fat32ScanState state, Fat32Geometry geometry, CancellationToken cancellationToken)
    {
        if (geometry.FsInfoSector is 0 or ushort.MaxValue || geometry.FsInfoSector >= geometry.ReservedSectorCount)
        {
            state.Diagnostics.Add("FAT32_FSINFO_INVALID", ScanDiagnosticSeverity.Warning, "fsinfo", "The FSInfo sector is absent or outside the reserved region.");
            state.FsInfo = new(null, null, false);
            return state.FsInfo;
        }

        var buffer = new byte[512];
        var offset = checked(state.VolumeOffset + checked((long)geometry.FsInfoSector * geometry.BytesPerSector));
        if (!await state.TryReadAsync(offset, buffer, "fsinfo", cancellationToken).ConfigureAwait(false))
        {
            state.Diagnostics.Add("FAT32_FSINFO_INVALID", ScanDiagnosticSeverity.Warning, "fsinfo", "The FSInfo sector could not be read.", offset);
            state.FsInfo = new(null, null, false);
            return state.FsInfo;
        }

        var valid = BinaryPrimitives.ReadUInt32LittleEndian(buffer) == 0x41615252 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(484)) == 0x61417272 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(508)) == 0xAA550000;
        var freeRaw = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(488));
        var nextRaw = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(492));
        uint? free = freeRaw == uint.MaxValue ? null : freeRaw;
        uint? next = nextRaw == uint.MaxValue ? null : nextRaw;
        if (free > geometry.ClusterCount || (next is not null && (next < 2 || next > geometry.MaximumDataCluster)))
        {
            valid = false;
        }

        if (!valid)
        {
            state.Diagnostics.Add("FAT32_FSINFO_INVALID", ScanDiagnosticSeverity.Warning, "fsinfo", "FSInfo signatures or advisory values are invalid; allocation decisions will use only FAT entries.", offset);
            free = null;
            next = null;
        }

        state.FsInfo = new(free, next, valid);
        return state.FsInfo;
    }

    private static async Task TraverseDirectoriesAsync(Fat32ScanState state, Fat32AllocationTable fat, CancellationToken cancellationToken)
    {
        var geometry = state.Geometry!;
        var queue = new Queue<DirectoryWork>();
        queue.Enqueue(new(geometry.RootDirectoryCluster, "\\", 0, $"root:{geometry.RootDirectoryCluster:X8}"));
        var clusterOwners = new Dictionary<uint, string>();
        var candidateKeys = new HashSet<string>(StringComparer.Ordinal);

        while (queue.Count > 0)
        {
            state.Checkpoint(Fat32ScanPhase.TraversingDirectories, cancellationToken);
            if (state.DirectoriesTraversed >= state.Budget.MaximumDirectories)
            {
                throw new Fat32LimitException("MaximumDirectories", "directory-traversal");
            }

            var directory = queue.Dequeue();
            state.DirectoriesTraversed++;
            var current = directory.FirstCluster;
            var chain = new HashSet<uint>();
            var pendingLfn = new List<LfnSlot>();
            var stopDirectory = false;
            var directoryEntries = 0;
            for (var chainLength = 0; !stopDirectory; chainLength++)
            {
                state.Checkpoint(Fat32ScanPhase.TraversingDirectories, cancellationToken);
                if (chainLength >= state.Budget.MaximumFatChainLength)
                {
                    state.ReachBudget("MaximumFatChainLength", "directory-chain");
                    break;
                }

                if (!IsDataCluster(geometry, current))
                {
                    state.Diagnostics.Add("FAT32_INVALID_FIRST_CLUSTER", ScanDiagnosticSeverity.Warning, "directory-chain", "A directory chain references a cluster outside the data region.", cluster: current);
                    state.MarkIncomplete("InvalidDirectoryChain");
                    break;
                }

                if (!chain.Add(current))
                {
                    state.Diagnostics.Add("FAT32_FAT_CHAIN_CYCLE", ScanDiagnosticSeverity.Warning, "directory-chain", "A cycle was detected in an active directory chain.", cluster: current);
                    state.MarkIncomplete("DirectoryChainCycle");
                    break;
                }

                if (clusterOwners.TryGetValue(current, out var owner) && owner != directory.Identity)
                {
                    state.Diagnostics.Add("FAT32_CROSS_LINKED_DIRECTORY", ScanDiagnosticSeverity.Warning, "directory-chain", "An active directory cluster is cross-linked to another traversed directory.", cluster: current);
                    state.MarkIncomplete("CrossLinkedDirectory");
                    break;
                }

                if (clusterOwners.Count >= state.Budget.MaximumVisitedClusters && !clusterOwners.ContainsKey(current))
                {
                    throw new Fat32LimitException("MaximumVisitedClusters", "directory-traversal");
                }

                clusterOwners[current] = directory.Identity;
                if (state.DirectoryClustersExamined >= state.Budget.MaximumDirectoryClusters)
                {
                    throw new Fat32LimitException("MaximumDirectoryClusters", "directory-traversal");
                }

                var bytes = new byte[geometry.ClusterSize];
                var clusterOffset = Fat32ClusterMapper.GetSourceOffset(geometry, state.VolumeOffset, current, state.Source.Length);
                if (!await state.TryReadAsync(clusterOffset, bytes, "directory-cluster", cancellationToken).ConfigureAwait(false))
                {
                    state.Diagnostics.Add("FAT32_INVALID_DIRECTORY_ENTRY", ScanDiagnosticSeverity.Warning, "directory-cluster", "An active directory cluster could not be read.", clusterOffset, current);
                    state.MarkIncomplete("DirectoryReadFailure");
                    break;
                }

                state.DirectoryClustersExamined++;
                for (var slotOffset = 0; slotOffset < bytes.Length; slotOffset += 32)
                {
                    state.Checkpoint(Fat32ScanPhase.TraversingDirectories, cancellationToken);
                    if (state.DirectoryEntriesExamined >= state.Budget.MaximumDirectoryEntries)
                    {
                        throw new Fat32LimitException("MaximumDirectoryEntries", "directory-entry");
                    }
                    if (directoryEntries >= state.Budget.MaximumEntriesPerDirectory)
                    {
                        state.ReachBudget("MaximumEntriesPerDirectory", "directory-entry");
                        stopDirectory = true;
                        break;
                    }

                    state.DirectoryEntriesExamined++;
                    directoryEntries++;
                    var slot = bytes.AsMemory(slotOffset, 32).ToArray();
                    var first = slot[0];
                    if (first == 0)
                    {
                        if (pendingLfn.Count > 0)
                        {
                            state.Diagnostics.Add("FAT32_ORPHAN_LFN", ScanDiagnosticSeverity.Warning, "directory-entry", "An LFN sequence was not followed by a short entry.", clusterOffset + slotOffset, current);
                        }

                        stopDirectory = true;
                        break;
                    }

                    if (slot[11] == 0x0F)
                    {
                        if (pendingLfn.Count >= state.Budget.MaximumLfnSlotsPerEntry)
                        {
                            state.Diagnostics.Add("FAT32_INVALID_LFN_STRUCTURE", ScanDiagnosticSeverity.Warning, "lfn", "The LFN slot limit was reached; the sequence was discarded.", clusterOffset + slotOffset, current);
                            pendingLfn.Clear();
                        }
                        else
                        {
                            pendingLfn.Add(LfnSlot.Parse(slot, clusterOffset + slotOffset));
                        }

                        continue;
                    }

                    var absoluteSlotOffset = checked(clusterOffset + slotOffset);
                    if (!Fat32DirectoryEntry.TryParse(slot, out var entry))
                    {
                        state.Diagnostics.Add("FAT32_INVALID_DIRECTORY_ENTRY", ScanDiagnosticSeverity.Warning, "directory-entry", "The directory slot has an invalid attribute combination.", absoluteSlotOffset, current);
                        pendingLfn.Clear();
                        continue;
                    }

                    if (entry!.IsVolumeLabel)
                    {
                        if (pendingLfn.Count > 0)
                        {
                            state.Diagnostics.Add("FAT32_ORPHAN_LFN", ScanDiagnosticSeverity.Warning, "lfn", "An LFN group was not followed by a file or directory short entry.", pendingLfn[0].Offset, current);
                        }
                        pendingLfn.Clear();
                        continue;
                    }

                    var lfn = Fat32NameDecoder.DecodeLongName(pendingLfn, entry.ShortNameBytes, entry.IsDeleted, state.Budget.MaximumFilenameCharacters, state.Diagnostics, cancellationToken);
                    pendingLfn.Clear();
                    var shortName = Fat32NameDecoder.DecodeShortName(entry.ShortNameBytes, entry.NtReserved, entry.IsDeleted);
                    if (entry.IsDeleted)
                    {
                        if (state.Candidates.Count >= state.Budget.MaximumCandidates)
                        {
                            throw new Fat32LimitException("MaximumCandidates", "candidate-normalization");
                        }

                        var key = $"{state.Request.SourceIdentity}|{state.VolumeOffset}|{current}|{absoluteSlotOffset}|{entry.IsDirectory}";
                        if (!candidateKeys.Add(key))
                        {
                            continue;
                        }

                        var candidate = await CreateCandidateAsync(state, fat, directory, current, absoluteSlotOffset, entry, shortName, lfn, cancellationToken).ConfigureAwait(false);
                        state.Candidates.Add(candidate);
                        continue;
                    }

                    if (!entry.IsDirectory || Fat32NameDecoder.IsDotEntry(entry.ShortNameBytes))
                    {
                        continue;
                    }

                    if (!IsDataCluster(geometry, entry.FirstCluster))
                    {
                        state.Diagnostics.Add("FAT32_INVALID_FIRST_CLUSTER", ScanDiagnosticSeverity.Warning, "directory-entry", "An active child directory has an invalid first cluster.", absoluteSlotOffset, entry.FirstCluster);
                        state.MarkIncomplete("InvalidChildDirectory");
                        continue;
                    }

                    if (directory.Depth >= state.Budget.MaximumDirectoryDepth)
                    {
                        state.Diagnostics.Add("FAT32_DIRECTORY_DEPTH_EXCEEDED", ScanDiagnosticSeverity.Warning, "directory-traversal", "The directory depth budget prevented recursion.", absoluteSlotOffset, entry.FirstCluster);
                        state.MarkPartial("MaximumDirectoryDepth");
                        continue;
                    }

                    state.Checkpoint(Fat32ScanPhase.TraversingDirectories, cancellationToken);
                    var component = lfn.Name is not null && lfn.NameState == Fat32NameState.VerifiedLongName ? lfn.Name : shortName.Display;
                    component = Fat32NameDecoder.Sanitize(component, state.Budget.MaximumFilenameCharacters, out _);
                    var childPath = directory.Path == "\\" ? $"\\{component}" : $"{directory.Path}\\{component}";
                    if (childPath.Length > state.Budget.MaximumPathCharacters)
                    {
                        state.Diagnostics.Add("FAT32_PATH_TRUNCATED", ScanDiagnosticSeverity.Warning, "path", "An active parent path exceeded the path-character budget.", absoluteSlotOffset, entry.FirstCluster);
                        state.MarkPartial("MaximumPathCharacters");
                        continue;
                    }

                    queue.Enqueue(new(entry.FirstCluster, childPath, directory.Depth + 1, $"dir:{entry.FirstCluster:X8}:{absoluteSlotOffset:X16}"));
                }

                if (stopDirectory)
                {
                    break;
                }

                var next = await fat.ReadEntryAsync(current, cancellationToken).ConfigureAwait(false);
                if (next.CopiesDisagree)
                {
                    state.Diagnostics.Add("FAT32_FAT_COPY_DISAGREEMENT", ScanDiagnosticSeverity.Warning, "directory-chain", "Mirrored FAT copies disagree for a traversed directory cluster.", cluster: current);
                    state.MarkIncomplete("DirectoryFatCopyDisagreement");
                    break;
                }

                if (next.Kind == Fat32FatEntryKind.NextCluster)
                {
                    current = next.Value;
                    continue;
                }

                if (next.Kind != Fat32FatEntryKind.EndOfChain)
                {
                    state.Diagnostics.Add("FAT32_INVALID_FAT_ENTRY", ScanDiagnosticSeverity.Warning, "directory-chain", $"A directory chain ended with {next.Kind}.", cluster: current);
                    state.MarkIncomplete("InvalidDirectoryChain");
                }

                break;
            }
        }
    }

    private static async Task<Fat32DeletedCandidate> CreateCandidateAsync(
        Fat32ScanState state,
        Fat32AllocationTable fat,
        DirectoryWork parent,
        uint parentCluster,
        long slotOffset,
        Fat32DirectoryEntry entry,
        ShortNameResult shortName,
        LongNameResult longName,
        CancellationToken cancellationToken)
    {
        state.Checkpoint(Fat32ScanPhase.AnalyzingAllocation, cancellationToken);
        var codes = new List<string>();
        var (created, createdValid) = Fat32Timestamp.Decode(entry.CreationDate, entry.CreationTime, entry.CreationTenths, true);
        var (modified, modifiedValid) = Fat32Timestamp.Decode(entry.ModifiedDate, entry.ModifiedTime, 0, true);
        var (accessed, accessedValid) = Fat32Timestamp.Decode(entry.AccessDate, 0, 0, false);
        if (!createdValid || !modifiedValid || !accessedValid)
        {
            codes.Add("FAT32_INVALID_TIMESTAMP");
            state.Diagnostics.Add("FAT32_INVALID_TIMESTAMP", ScanDiagnosticSeverity.Warning, "directory-entry", "One or more FAT timestamps are invalid and were reported as unknown.", slotOffset, parentCluster);
        }

        var nameState = longName.NameState;
        var display = longName.Name ?? shortName.Display;
        if (longName.Name is null)
        {
            nameState = shortName.IsUsable ? Fat32NameState.ShortNameWithMissingFirstCharacter : Fat32NameState.GeneratedFallback;
        }

        state.Checkpoint(Fat32ScanPhase.TraversingDirectories, cancellationToken);
        var onlyMissingMarkerWasUnsafe = nameState == Fat32NameState.ShortNameWithMissingFirstCharacter &&
                                         display.Length > 0 && display[0] == '?' &&
                                         Fat32NameDecoder.Sanitize(display[1..], state.Budget.MaximumFilenameCharacters, out var remainderChanged) == display[1..] &&
                                         !remainderChanged;
        display = Fat32NameDecoder.Sanitize(display, state.Budget.MaximumFilenameCharacters, out var changed);
        if (string.IsNullOrWhiteSpace(display))
        {
            display = $"Deleted_{slotOffset:X}";
            nameState = Fat32NameState.GeneratedFallback;
        }
        else if (changed && !onlyMissingMarkerWasUnsafe && nameState != Fat32NameState.GeneratedFallback)
        {
            nameState = Fat32NameState.InvalidName;
        }

        var path = parent.Path == "\\" ? $"\\{display}" : $"{parent.Path}\\{display}";
        var pathState = nameState is Fat32NameState.VerifiedLongName ? Fat32PathState.CompleteActiveParent : Fat32PathState.NameUncertain;
        if (path.Length > state.Budget.MaximumPathCharacters)
        {
            path = path[..state.Budget.MaximumPathCharacters];
            pathState = Fat32PathState.PathTruncated;
            codes.Add("FAT32_PATH_TRUNCATED");
        }

        var idMaterial = Encoding.UTF8.GetBytes($"{ParserVersion}|{state.Request.ScanSessionId:D}|{state.Request.SourceIdentity}|{state.VolumeOffset}|{parentCluster}|{slotOffset}|{entry.IsDirectory}");
        var id = Convert.ToHexString(SHA256.HashData(idMaterial));
        var allocation = await AssessAllocationAsync(state, fat, entry, id, codes, cancellationToken).ConfigureAwait(false);
        var candidate = new Fat32DeletedCandidate(
            id,
            "FAT32",
            entry.IsDirectory ? Fat32CandidateKind.Directory : Fat32CandidateKind.File,
            display,
            shortName.Display,
            longName.Name is null ? null : new(longName.Name, longName.ChecksumValidated, longName.SlotCount, longName.MissingFirstByteMatches),
            parent.Identity,
            path,
            entry.FirstCluster,
            entry.FileSize,
            createdValid ? created : null,
            accessedValid ? accessed : null,
            modifiedValid ? modified : null,
            entry.Attributes,
            allocation,
            nameState,
            pathState,
            codes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            state.Request.ScanSessionId);
        return candidate with
        {
            Provenance = new(parentCluster, slotOffset, Fat32RecoveryFingerprint.Candidate(candidate)),
        };
    }

    private static async Task<Fat32AllocationAssessment> AssessAllocationAsync(
        Fat32ScanState state,
        Fat32AllocationTable fat,
        Fat32DirectoryEntry entry,
        string candidateId,
        List<string> codes,
        CancellationToken cancellationToken)
    {
        if (entry.IsDirectory)
        {
            return Fat32AllocationAssessment.MetadataOnly;
        }

        if (entry.FileSize == 0)
        {
            return Fat32AllocationAssessment.ZeroLength;
        }

        var geometry = state.Geometry!;
        if (!IsDataCluster(geometry, entry.FirstCluster))
        {
            codes.Add("FAT32_INVALID_FIRST_CLUSTER");
            state.Diagnostics.Add("FAT32_INVALID_FIRST_CLUSTER", ScanDiagnosticSeverity.Warning, "allocation", "A deleted entry has a first cluster outside the data region.", cluster: entry.FirstCluster);
            return Fat32AllocationAssessment.DamagedMetadata;
        }

        var required = ((ulong)entry.FileSize + (uint)geometry.ClusterSize - 1) / (uint)geometry.ClusterSize;
        if (required == 0 || required > geometry.ClusterCount || (ulong)entry.FirstCluster + required - 1 > geometry.MaximumDataCluster)
        {
            codes.Add("FAT32_FILE_EXCEEDS_VOLUME");
            state.Diagnostics.Add("FAT32_FILE_EXCEEDS_VOLUME", ScanDiagnosticSeverity.Warning, "allocation", "The deleted file's logical size exceeds the remaining contiguous volume span.", cluster: entry.FirstCluster);
            return Fat32AllocationAssessment.DamagedMetadata;
        }

        state.Checkpoint(Fat32ScanPhase.AnalyzingAllocation, cancellationToken);
        var first = await fat.ReadEntryAsync(entry.FirstCluster, cancellationToken).ConfigureAwait(false);
        if (first.CopiesDisagree)
        {
            codes.Add("FAT32_FAT_COPY_DISAGREEMENT");
            codes.Add("FAT32_ALLOCATION_UNKNOWN");
            state.Diagnostics.Add("FAT32_FAT_COPY_DISAGREEMENT", ScanDiagnosticSeverity.Warning, "allocation", "Mirrored FAT copies disagree for a deleted candidate.", cluster: entry.FirstCluster);
            return Fat32AllocationAssessment.AllocationUnknown;
        }

        if (first.Kind is Fat32FatEntryKind.NextCluster or Fat32FatEntryKind.EndOfChain)
        {
            var current = entry.FirstCluster;
            var visited = new HashSet<uint>();
            var chainTooShort = false;
            for (ulong count = 0; count < required; count++)
            {
                state.Checkpoint(Fat32ScanPhase.AnalyzingAllocation, cancellationToken);
                if (count >= (ulong)state.Budget.MaximumFatChainLength)
                {
                    codes.Add("FAT32_BUDGET_REACHED");
                    state.MarkPartial("MaximumFatChainLength");
                    return Fat32AllocationAssessment.Unknown;
                }

                if (!visited.Add(current))
                {
                    codes.Add("FAT32_FAT_CHAIN_CYCLE");
                    return Fat32AllocationAssessment.DamagedMetadata;
                }

                var item = await fat.ReadEntryAsync(current, cancellationToken).ConfigureAwait(false);
                if (item.CopiesDisagree)
                {
                    codes.Add("FAT32_FAT_COPY_DISAGREEMENT");
                    state.Diagnostics.Add("FAT32_FAT_COPY_DISAGREEMENT", ScanDiagnosticSeverity.Warning, "allocation", "Mirrored FAT copies disagree within a candidate chain.", cluster: current);
                    return Fat32AllocationAssessment.AllocationUnknown;
                }

                if (count + 1 == required)
                {
                    if (!state.RegisterPreservedChain(candidateId, visited))
                    {
                        codes.Add("FAT32_CROSS_LINKED_CLUSTER");
                        codes.Add("FAT32_ALLOCATION_UNKNOWN");
                        return Fat32AllocationAssessment.AllocationUnknown;
                    }
                    return Fat32AllocationAssessment.PreservedAllocatedChain;
                }

                if (item.Kind == Fat32FatEntryKind.EndOfChain)
                {
                    chainTooShort = true;
                    break;
                }

                if (item.Kind != Fat32FatEntryKind.NextCluster || !IsDataCluster(geometry, item.Value))
                {
                    codes.Add(item.Kind == Fat32FatEntryKind.Bad ? "FAT32_BAD_CLUSTER" : "FAT32_INVALID_FAT_ENTRY");
                    return Fat32AllocationAssessment.DamagedMetadata;
                }

                current = item.Value;
            }

            if (!chainTooShort)
            {
                return Fat32AllocationAssessment.DamagedMetadata;
            }
        }

        var free = 0UL;
        var allocated = 0UL;
        for (ulong index = 0; index < required; index++)
        {
            state.Checkpoint(Fat32ScanPhase.AnalyzingAllocation, cancellationToken);
            var cluster = checked(entry.FirstCluster + (uint)index);
            var item = await fat.ReadEntryAsync(cluster, cancellationToken).ConfigureAwait(false);
            if (item.CopiesDisagree)
            {
                codes.Add("FAT32_FAT_COPY_DISAGREEMENT");
                state.Diagnostics.Add("FAT32_FAT_COPY_DISAGREEMENT", ScanDiagnosticSeverity.Warning, "allocation", "Mirrored FAT copies disagree within a candidate's contiguous allocation span.", cluster: cluster);
                return Fat32AllocationAssessment.AllocationUnknown;
            }

            if (item.Kind == Fat32FatEntryKind.Free)
            {
                free++;
            }
            else if (item.Kind is Fat32FatEntryKind.NextCluster or Fat32FatEntryKind.EndOfChain)
            {
                allocated++;
            }
            else
            {
                codes.Add(item.Kind == Fat32FatEntryKind.Bad ? "FAT32_BAD_CLUSTER" : "FAT32_INVALID_FAT_ENTRY");
                return Fat32AllocationAssessment.Unknown;
            }
        }

        if (free == required) return Fat32AllocationAssessment.PossiblyRecoverableContiguous;
        if (allocated == required) return Fat32AllocationAssessment.OverwrittenOrReused;
        return Fat32AllocationAssessment.PartiallyOverwrittenOrReused;
    }

    internal static bool IsDataCluster(Fat32Geometry geometry, uint cluster) => cluster >= 2 && cluster <= geometry.MaximumDataCluster;

    private readonly record struct BootSelection(Fat32Geometry? Geometry);
    private sealed record DirectoryWork(uint FirstCluster, string Path, int Depth, string Identity);

    private sealed class Fat32CandidateComparer : IComparer<Fat32DeletedCandidate>
    {
        public static Fat32CandidateComparer Instance { get; } = new();
        public int Compare(Fat32DeletedCandidate? x, Fat32DeletedCandidate? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            var parent = StringComparer.Ordinal.Compare(x.ParentDirectoryIdentity, y.ParentDirectoryIdentity);
            return parent != 0 ? parent : StringComparer.Ordinal.Compare(x.CandidateId, y.CandidateId);
        }
    }
}

internal sealed class Fat32ScanState
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly IProgress<Fat32ScanProgress>? _progress;
    private readonly Dictionary<uint, string> _preservedChainOwners = [];
    private TimeSpan _lastProgress = TimeSpan.MinValue;
    private string? _partialReason;
    private Fat32ScanPhase _currentPhase = Fat32ScanPhase.ReadingBootSectors;

    public Fat32ScanState(IReadOnlyRandomAccessSource source, Fat32ScanRequest request, IProgress<Fat32ScanProgress>? progress)
    {
        Source = source;
        Request = request;
        Budget = request.EffectiveBudget;
        VolumeOffset = request.Volume.VolumeOffset;
        _progress = progress;
        Diagnostics = new Fat32DiagnosticCollector(Budget.MaximumDiagnostics);
    }

    public IReadOnlyRandomAccessSource Source { get; }
    public Fat32ScanRequest Request { get; }
    public Fat32ScanBudget Budget { get; }
    public long VolumeOffset { get; }
    public Fat32Geometry? Geometry { get; set; }
    public Fat32FsInfo? FsInfo { get; set; }
    public Fat32DiagnosticCollector Diagnostics { get; }
    public List<Fat32DeletedCandidate> Candidates { get; } = [];
    public long BytesRead { get; private set; }
    public int DirectoriesTraversed { get; set; }
    public int DirectoryClustersExamined { get; set; }
    public int DirectoryEntriesExamined { get; set; }
    public int FatEntriesInspected { get; set; }
    public bool IsBudgetLimited { get; private set; }
    public bool IsPartial => _partialReason is not null;

    public async ValueTask<bool> TryReadAsync(long offset, Memory<byte> destination, string operation, CancellationToken cancellationToken)
    {
        Checkpoint(_currentPhase, cancellationToken);
        if (destination.Length > Budget.MaximumBytesRead - BytesRead)
        {
            throw new Fat32LimitException("MaximumBytesRead", operation);
        }

        try
        {
            await Source.ReadExactlyAsync(offset, destination, cancellationToken).ConfigureAwait(false);
            BytesRead = checked(BytesRead + destination.Length);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or EndOfStreamException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    public void Checkpoint(Fat32ScanPhase phase, CancellationToken cancellationToken, bool forceProgress = false)
    {
        _currentPhase = phase;
        cancellationToken.ThrowIfCancellationRequested();
        if (_stopwatch.Elapsed > Budget.EffectiveMaximumScanDuration)
        {
            throw new Fat32LimitException("MaximumScanDuration", "scan");
        }

        Report(phase, forceProgress);
    }

    public void Report(Fat32ScanPhase phase, bool force)
    {
        if (!force && _stopwatch.Elapsed - _lastProgress < Budget.EffectiveMinimumProgressInterval) return;
        _lastProgress = _stopwatch.Elapsed;
        _progress?.Report(new(phase, DirectoryClustersExamined, DirectoryEntriesExamined, FatEntriesInspected, Candidates.Count, BytesRead, _stopwatch.Elapsed, IsBudgetLimited)
        {
            DirectoriesTraversed = DirectoriesTraversed,
        });
    }

    public void ReachBudget(string budgetName, string operation)
    {
        IsBudgetLimited = true;
        MarkIncomplete(budgetName);
        Diagnostics.Add("FAT32_BUDGET_REACHED", ScanDiagnosticSeverity.Warning, operation, $"The {budgetName} safety budget was reached; validated partial results were retained.");
    }

    public void MarkPartial(string reason)
    {
        IsBudgetLimited = true;
        MarkIncomplete(reason);
    }

    public void MarkIncomplete(string reason)
    {
        _partialReason ??= reason;
    }

    public bool RegisterPreservedChain(string candidateId, IEnumerable<uint> clusters)
    {
        var clusterArray = clusters.ToArray();
        var conflictingIds = clusterArray
            .Where(_preservedChainOwners.ContainsKey)
            .Select(cluster => _preservedChainOwners[cluster])
            .Where(owner => owner != candidateId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (conflictingIds.Length > 0)
        {
            Diagnostics.Add("FAT32_CROSS_LINKED_CLUSTER", ScanDiagnosticSeverity.Warning, "allocation", "Preserved deleted-file chains share one or more allocated clusters; allocation confidence was lowered.");
            foreach (var conflictingId in conflictingIds)
            {
                var index = Candidates.FindIndex(candidate => candidate.CandidateId == conflictingId);
                if (index < 0) continue;
                var prior = Candidates[index];
                Candidates[index] = prior with
                {
                    Allocation = Fat32AllocationAssessment.AllocationUnknown,
                    DiagnosticCodes = prior.DiagnosticCodes
                        .Append("FAT32_CROSS_LINKED_CLUSTER")
                        .Append("FAT32_ALLOCATION_UNKNOWN")
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .ToArray(),
                };
            }
            return false;
        }

        foreach (var cluster in clusterArray) _preservedChainOwners.TryAdd(cluster, candidateId);
        return true;
    }

    public Fat32ScanResult Result(Fat32ScanOutcome outcome, Fat32Geometry? geometry, Fat32FsInfo? fsInfo, string? reason = null) =>
        new(outcome, geometry, fsInfo, Candidates.ToArray(), Diagnostics.Items.ToArray(),
            new(DirectoriesTraversed, DirectoryClustersExamined, DirectoryEntriesExamined, FatEntriesInspected, BytesRead, _stopwatch.Elapsed),
            IsBudgetLimited, Diagnostics.IsTruncated, reason ?? _partialReason);
}

internal sealed class Fat32DiagnosticCollector(int maximum)
{
    private readonly List<Fat32Diagnostic> _items = [];
    public IReadOnlyList<Fat32Diagnostic> Items => _items;
    public bool IsTruncated { get; private set; }

    public void Add(string code, ScanDiagnosticSeverity severity, string operation, string reason, long? offset = null, uint? cluster = null)
    {
        if (_items.Count >= maximum) { IsTruncated = true; return; }
        _items.Add(new(code, severity, operation, reason, offset, cluster));
    }
}

internal sealed class Fat32LimitException(string budgetName, string operation) : Exception
{
    public string BudgetName { get; } = budgetName;
    public string Operation { get; } = operation;
}

internal static class Fat32ClusterMapper
{
    public static long GetSourceOffset(Fat32Geometry geometry, long volumeOffset, uint cluster, long sourceLength)
    {
        if (!Fat32MetadataScanner.IsDataCluster(geometry, cluster)) throw new ArgumentOutOfRangeException(nameof(cluster));
        var relativeSector = checked((ulong)geometry.FirstDataSector + checked((ulong)(cluster - 2) * geometry.SectorsPerCluster));
        var relativeByte = checked(relativeSector * geometry.BytesPerSector);
        var sourceOffset = checked(volumeOffset + checked((long)relativeByte));
        var end = checked(sourceOffset + geometry.ClusterSize);
        var volumeEnd = checked(volumeOffset + checked((long)geometry.TotalSectors * geometry.BytesPerSector));
        if (sourceOffset < volumeOffset || end > volumeEnd || end > sourceLength) throw new ArgumentOutOfRangeException(nameof(cluster));
        return sourceOffset;
    }
}

internal readonly record struct Fat32BootParseResult(bool IsValid, Fat32Geometry? Geometry, string Code, string Reason);
internal readonly record struct BackupLocator(ushort BytesPerSector, ushort ReservedSectors, uint TotalSectors, ushort BackupSector);

internal static class Fat32BootSectorParser
{
    public static BackupLocator? ReadBackupLocator(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < 512) return null;
        var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(sector[11..]);
        if (bytesPerSector is not (512 or 1024 or 2048 or 4096)) return null;
        var total16 = BinaryPrimitives.ReadUInt16LittleEndian(sector[19..]);
        var total = total16 != 0 ? total16 : BinaryPrimitives.ReadUInt32LittleEndian(sector[32..]);
        return total == 0 ? null : new(bytesPerSector, BinaryPrimitives.ReadUInt16LittleEndian(sector[14..]), total, BinaryPrimitives.ReadUInt16LittleEndian(sector[50..]));
    }

    public static Fat32BootParseResult Parse(ReadOnlySpan<byte> sector, long sourceLength, long volumeOffset, bool usedBackup)
    {
        try
        {
            if (sector.Length < 512) return Invalid("FAT32_INVALID_GEOMETRY", "The boot sector is shorter than 512 bytes.");
            if (!IsJumpValid(sector)) return Invalid("FAT32_INVALID_GEOMETRY", "The boot jump instruction is not a supported FAT shape.");
            if (BinaryPrimitives.ReadUInt16LittleEndian(sector[510..]) != 0xAA55) return Invalid("FAT32_INVALID_GEOMETRY", "The boot-sector signature is missing.");
            var bps = BinaryPrimitives.ReadUInt16LittleEndian(sector[11..]);
            if (bps is not (512 or 1024 or 2048 or 4096)) return Invalid("FAT32_INVALID_GEOMETRY", "Bytes per sector is unsupported.");
            var spc = sector[13];
            if (spc == 0 || (spc & (spc - 1)) != 0 || spc > 128) return Invalid("FAT32_INVALID_GEOMETRY", "Sectors per cluster is invalid.");
            var clusterSize = checked(bps * spc);
            if (clusterSize > 1024 * 1024) return Invalid("FAT32_INVALID_GEOMETRY", "The cluster size exceeds the implementation bound.");
            var reserved = BinaryPrimitives.ReadUInt16LittleEndian(sector[14..]);
            var fats = sector[16];
            var rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(sector[17..]);
            var total16 = BinaryPrimitives.ReadUInt16LittleEndian(sector[19..]);
            var fat16 = BinaryPrimitives.ReadUInt16LittleEndian(sector[22..]);
            var total32 = BinaryPrimitives.ReadUInt32LittleEndian(sector[32..]);
            var fat32 = BinaryPrimitives.ReadUInt32LittleEndian(sector[36..]);
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(sector[40..]);
            var version = BinaryPrimitives.ReadUInt16LittleEndian(sector[42..]);
            var root = BinaryPrimitives.ReadUInt32LittleEndian(sector[44..]);
            if (reserved == 0 || fats is 0 or > 4 || rootEntries != 0 || fat16 != 0 || fat32 == 0 || version != 0)
                return Invalid("FAT32_INVALID_GEOMETRY", "Required FAT32 BPB fields are invalid or unsupported.");
            if (sector[21] != 0xF0 && sector[21] < 0xF8) return Invalid("FAT32_INVALID_GEOMETRY", "The media descriptor is not plausible for FAT32.");
            var total = total16 != 0 ? total16 : total32;
            if (total == 0) return Invalid("FAT32_INVALID_GEOMETRY", "The declared volume has no sectors.");
            var fatRegion = checked((ulong)fats * fat32);
            var firstData = checked((ulong)reserved + fatRegion);
            if (firstData >= total) return Invalid("FAT32_INVALID_GEOMETRY", "The FAT and reserved regions consume the declared volume.");
            var dataSectors = checked((ulong)total - firstData);
            var clusters64 = dataSectors / spc;
            if (clusters64 < 65_525 || clusters64 > 0x0FFFFFEE) return Invalid("FAT32_NOT_FAT32", "Calculated cluster count does not classify the volume as FAT32.");
            var fatEntries = checked((ulong)fat32 * bps / 4);
            if (fatEntries < clusters64 + 2) return Invalid("FAT32_INVALID_GEOMETRY", "The FAT is too small for the calculated data-cluster count.");
            var volumeBytes = checked((ulong)total * bps);
            var volumeEnd = checked((ulong)volumeOffset + volumeBytes);
            if (volumeOffset < 0 || volumeEnd > (ulong)sourceLength) return Invalid("FAT32_INVALID_GEOMETRY", "The declared FAT32 volume lies outside the source.");
            var maximum = checked((uint)(clusters64 + 1));
            if (root < 2 || root > maximum) return Invalid("FAT32_INVALID_GEOMETRY", "The root-directory cluster is outside the data region.");
            var mirroring = (flags & 0x80) == 0;
            var active = mirroring ? (byte)0 : (byte)(flags & 0x0F);
            if (!mirroring && active >= fats) return Invalid("FAT32_INVALID_ACTIVE_FAT_INDEX", "The active FAT index is outside the declared FAT count.");
            var extSignature = sector[66];
            uint? volumeId = extSignature is 0x28 or 0x29 ? BinaryPrimitives.ReadUInt32LittleEndian(sector[67..]) : null;
            var label = extSignature is 0x28 or 0x29 ? DecodeAscii(sector.Slice(71, 11)).Trim() : null;
            if (string.IsNullOrWhiteSpace(label) || label == "NO NAME") label = null;
            var geometry = new Fat32Geometry(
                bps, spc, clusterSize, reserved, fats, total, fat32, sector[21], flags, version, root,
                BinaryPrimitives.ReadUInt16LittleEndian(sector[48..]), BinaryPrimitives.ReadUInt16LittleEndian(sector[50..]),
                reserved, checked((uint)firstData), checked((uint)clusters64), maximum, mirroring, active,
                DecodeAscii(sector.Slice(3, 8)).Trim(), label, volumeId, usedBackup);
            return new(true, geometry, string.Empty, string.Empty);
        }
        catch (OverflowException)
        {
            return Invalid("FAT32_INVALID_GEOMETRY", "A FAT32 geometry calculation overflowed.");
        }
    }

    public static bool CriticalGeometryMatches(Fat32Geometry left, Fat32Geometry right) =>
        left.BytesPerSector == right.BytesPerSector && left.SectorsPerCluster == right.SectorsPerCluster &&
        left.ReservedSectorCount == right.ReservedSectorCount && left.FatCount == right.FatCount &&
        left.TotalSectors == right.TotalSectors && left.FatSizeSectors == right.FatSizeSectors &&
        left.RootDirectoryCluster == right.RootDirectoryCluster && left.FileSystemVersion == right.FileSystemVersion &&
        left.ExtendedFlags == right.ExtendedFlags;

    private static bool IsJumpValid(ReadOnlySpan<byte> sector) => sector[0] == 0xEB && sector[2] == 0x90 || sector[0] == 0xE9;
    private static string DecodeAscii(ReadOnlySpan<byte> bytes) => Encoding.ASCII.GetString(bytes).Replace('\0', ' ');
    private static Fat32BootParseResult Invalid(string code, string reason) => new(false, null, code, reason);
}

internal sealed class Fat32AllocationTable : IFat32AllocationTable
{
    private readonly Fat32ScanState _state;
    private readonly Fat32Geometry _geometry;
    private readonly Dictionary<(byte Fat, long Page), byte[]> _cache = [];
    private readonly Queue<(byte Fat, long Page)> _fifo = [];
    private readonly int _pageSize;
    private readonly int _maximumPages;
    private readonly bool _useCache;

    public Fat32AllocationTable(Fat32ScanState state, Fat32Geometry geometry)
    {
        _state = state;
        _geometry = geometry;
        _pageSize = geometry.BytesPerSector;
        _maximumPages = state.Budget.MaximumFatCacheBytes / _pageSize;
        _useCache = _maximumPages > 0;
    }

    public async ValueTask ValidateReservedEntriesAsync(CancellationToken cancellationToken)
    {
        var zero = await ReadRawSelectedAsync(0, cancellationToken).ConfigureAwait(false);
        var one = await ReadRawSelectedAsync(1, cancellationToken).ConfigureAwait(false);
        if ((zero & 0xFF) != _geometry.MediaDescriptor || (one & 0x0FFFFFFF) < 0x0FFFFFF8)
        {
            _state.Diagnostics.Add("FAT32_INVALID_FAT_ENTRY", ScanDiagnosticSeverity.Warning, "fat-reserved", "FAT reserved entries are inconsistent with FAT32 conventions.");
        }
    }

    public async ValueTask<Fat32FatEntry> ReadEntryAsync(uint cluster, CancellationToken cancellationToken)
    {
        _state.Checkpoint(Fat32ScanPhase.AnalyzingAllocation, cancellationToken);
        if (_state.FatEntriesInspected >= _state.Budget.MaximumFatEntriesInspected)
            throw new Fat32LimitException("MaximumFatEntriesInspected", "fat-entry");
        _state.FatEntriesInspected++;
        if (cluster > _geometry.MaximumDataCluster) return new(cluster, Fat32FatEntryKind.Invalid, false);
        uint selected;
        try
        {
            selected = await ReadRawSelectedAsync(cluster, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            _state.Diagnostics.Add("FAT32_INVALID_FAT_ENTRY", ScanDiagnosticSeverity.Warning, "fat-entry", "A bounded FAT entry could not be read.", cluster: cluster);
            return new(cluster, Fat32FatEntryKind.Invalid, false);
        }
        var value = selected & 0x0FFFFFFF;
        var disagreement = false;
        if (_geometry.FatMirroringEnabled && _geometry.FatCount > 1)
        {
            for (byte fat = 1; fat < _geometry.FatCount; fat++)
            {
                try
                {
                    var copy = (await ReadRawAsync(fat, cluster, cancellationToken).ConfigureAwait(false)) & 0x0FFFFFFF;
                    disagreement |= copy != value;
                }
                catch (IOException)
                {
                    disagreement = true;
                    _state.Diagnostics.Add("FAT32_FAT_COPY_DISAGREEMENT", ScanDiagnosticSeverity.Warning, "fat-entry", "A mirrored FAT copy could not be read for comparison.", cluster: cluster);
                }
            }
        }

        var kind = Classify(value);
        if (kind == Fat32FatEntryKind.NextCluster && value > _geometry.MaximumDataCluster) kind = Fat32FatEntryKind.Invalid;
        return new(value, kind, disagreement);
    }

    private ValueTask<uint> ReadRawSelectedAsync(uint cluster, CancellationToken cancellationToken) =>
        ReadRawAsync(_geometry.ActiveFatIndex, cluster, cancellationToken);

    private async ValueTask<uint> ReadRawAsync(byte fat, uint cluster, CancellationToken cancellationToken)
    {
        var fatByteOffset = checked((long)cluster * 4);
        var fatStartSector = checked((ulong)_geometry.FirstFatSector + checked((ulong)fat * _geometry.FatSizeSectors));
        var fatStartOffset = checked(_state.VolumeOffset + checked((long)checked(fatStartSector * _geometry.BytesPerSector)));
        if (!_useCache)
        {
            var entry = new byte[4];
            if (!await _state.TryReadAsync(checked(fatStartOffset + fatByteOffset), entry, "fat-entry", cancellationToken).ConfigureAwait(false))
                throw new IOException("A bounded FAT entry could not be read.");
            return BinaryPrimitives.ReadUInt32LittleEndian(entry);
        }

        var page = fatByteOffset / _pageSize;
        var within = checked((int)(fatByteOffset % _pageSize));
        var key = (fat, page);
        if (!_cache.TryGetValue(key, out var bytes))
        {
            var offset = checked(fatStartOffset + checked(page * _pageSize));
            bytes = new byte[_pageSize];
            if (!await _state.TryReadAsync(offset, bytes, "fat-page", cancellationToken).ConfigureAwait(false))
                throw new IOException("A bounded FAT page could not be read.");
            while (_cache.Count >= _maximumPages)
            {
                _cache.Remove(_fifo.Dequeue());
            }

            _cache[key] = bytes;
            _fifo.Enqueue(key);
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(within, 4));
    }

    private static Fat32FatEntryKind Classify(uint value)
    {
        if (value == 0) return Fat32FatEntryKind.Free;
        if (value == 0x0FFFFFF7) return Fat32FatEntryKind.Bad;
        if (value >= 0x0FFFFFF8 && value <= 0x0FFFFFFF) return Fat32FatEntryKind.EndOfChain;
        if (value >= 0x0FFFFFF0 && value <= 0x0FFFFFF6) return Fat32FatEntryKind.Reserved;
        if (value >= 2 && value <= 0x0FFFFFEF) return Fat32FatEntryKind.NextCluster;
        return Fat32FatEntryKind.Invalid;
    }
}

internal sealed record Fat32DirectoryEntry(
    byte[] ShortNameBytes, byte Attributes, byte NtReserved, byte CreationTenths, ushort CreationTime,
    ushort CreationDate, ushort AccessDate, ushort ModifiedTime, ushort ModifiedDate, uint FirstCluster,
    uint FileSize, bool IsDeleted, bool IsDirectory, bool IsVolumeLabel)
{
    public static bool TryParse(ReadOnlySpan<byte> slot, out Fat32DirectoryEntry? entry)
    {
        entry = null;
        if (slot.Length != 32 || slot[11] == 0x0F) return false;
        var attributes = slot[11];
        if ((attributes & 0xC0) != 0 || (attributes & 0x18) == 0x18) return false;
        var high = BinaryPrimitives.ReadUInt16LittleEndian(slot[20..]);
        var low = BinaryPrimitives.ReadUInt16LittleEndian(slot[26..]);
        entry = new(slot[..11].ToArray(), attributes, slot[12], slot[13],
            BinaryPrimitives.ReadUInt16LittleEndian(slot[14..]), BinaryPrimitives.ReadUInt16LittleEndian(slot[16..]),
            BinaryPrimitives.ReadUInt16LittleEndian(slot[18..]), BinaryPrimitives.ReadUInt16LittleEndian(slot[22..]),
            BinaryPrimitives.ReadUInt16LittleEndian(slot[24..]), ((uint)high << 16) | low,
            BinaryPrimitives.ReadUInt32LittleEndian(slot[28..]), slot[0] == 0xE5, (attributes & 0x10) != 0, (attributes & 0x08) != 0);
        return true;
    }
}

internal readonly record struct LfnSlot(byte Ordinal, byte Checksum, byte Type, ushort FirstClusterLow, ushort[] Characters, long Offset)
{
    public static LfnSlot Parse(ReadOnlySpan<byte> slot, long offset)
    {
        var chars = new ushort[13];
        var positions = new[] { 1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30 };
        for (var i = 0; i < positions.Length; i++) chars[i] = BinaryPrimitives.ReadUInt16LittleEndian(slot[positions[i]..]);
        return new(slot[0], slot[13], slot[12], BinaryPrimitives.ReadUInt16LittleEndian(slot[26..]), chars, offset);
    }
}

internal readonly record struct ShortNameResult(string Display, bool IsUsable);
internal readonly record struct LongNameResult(string? Name, Fat32NameState NameState, bool ChecksumValidated, int SlotCount, int MissingFirstByteMatches);

internal static class Fat32NameDecoder
{
    public static ShortNameResult DecodeShortName(byte[] raw, byte ntReserved, bool deleted)
    {
        var copy = raw.ToArray();
        if (!deleted && copy[0] == 0x05) copy[0] = 0xE5;
        var basename = DecodeOemFallback(copy.AsSpan(0, 8), deleted).TrimEnd();
        var extension = DecodeOemFallback(copy.AsSpan(8, 3), false).TrimEnd();
        if ((ntReserved & 0x08) != 0) basename = basename.ToLowerInvariant();
        if ((ntReserved & 0x10) != 0) extension = extension.ToLowerInvariant();
        var result = string.IsNullOrEmpty(extension) ? basename : $"{basename}.{extension}";
        var usable = result.Any(ch => ch != '?' && ch != '.' && ch != ' ');
        return new(usable ? result : "Deleted_unknown", usable);
    }

    public static bool IsDotEntry(byte[] raw) => raw[0] == (byte)'.' && (raw[1] == (byte)' ' || raw[1] == (byte)'.');

    public static LongNameResult DecodeLongName(
        IReadOnlyList<LfnSlot> slots,
        byte[] shortName,
        bool deleted,
        int maximumCharacters,
        Fat32DiagnosticCollector diagnostics,
        CancellationToken cancellationToken)
    {
        if (slots.Count == 0) return default;
        cancellationToken.ThrowIfCancellationRequested();
        if (slots.Any(slot => slot.Type != 0 || slot.FirstClusterLow != 0) || slots.Select(slot => slot.Checksum).Distinct().Count() != 1)
        {
            diagnostics.Add("FAT32_INVALID_LFN_STRUCTURE", ScanDiagnosticSeverity.Warning, "lfn", "The contiguous LFN group has invalid structural fields.", slots[0].Offset);
            return default;
        }

        IReadOnlyList<LfnSlot> ordered;
        var checksum = slots[0].Checksum;
        var checksumValidated = false;
        var matches = 0;
        if (!deleted)
        {
            var firstOrdinal = slots[0].Ordinal;
            var expectedCount = firstOrdinal & 0x1F;
            if ((firstOrdinal & 0x40) == 0 || expectedCount != slots.Count || expectedCount == 0 ||
                slots.Where((slot, index) => (slot.Ordinal & 0x1F) != slots.Count - index).Any())
            {
                diagnostics.Add("FAT32_INVALID_LFN_STRUCTURE", ScanDiagnosticSeverity.Warning, "lfn", "Active LFN ordinal ordering is invalid.", slots[0].Offset);
                return default;
            }

            if (ComputeChecksum(shortName) != checksum)
            {
                diagnostics.Add("FAT32_LFN_CHECKSUM_MISMATCH", ScanDiagnosticSeverity.Warning, "lfn", "The active LFN checksum does not match its short entry.", slots[0].Offset);
                return default;
            }

            checksumValidated = true;
            matches = 1;
            ordered = slots.Reverse().ToArray();
        }
        else
        {
            if (slots.Any(slot => slot.Ordinal != 0xE5))
            {
                diagnostics.Add("FAT32_INVALID_LFN_STRUCTURE", ScanDiagnosticSeverity.Warning, "lfn", "A deleted LFN group contains an unexpected ordinal byte.", slots[0].Offset);
                return default;
            }

            var candidate = shortName.ToArray();
            for (var first = 1; first <= byte.MaxValue; first++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsPlausibleShortFirst((byte)first)) continue;
                candidate[0] = (byte)first;
                if (ComputeChecksum(candidate) == checksum) matches++;
            }

            checksumValidated = matches == 1;
            ordered = slots.Reverse().ToArray();
        }

        var units = ordered.SelectMany(slot => slot.Characters).ToArray();
        var chars = new List<char>(Math.Min(units.Length, maximumCharacters));
        var terminated = false;
        foreach (var unit in units)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (unit == 0) { terminated = true; continue; }
            if (unit == 0xFFFF)
            {
                if (!terminated)
                {
                    diagnostics.Add("FAT32_INVALID_LFN_STRUCTURE", ScanDiagnosticSeverity.Warning, "lfn", "LFN padding appeared before a terminator.", slots[0].Offset);
                    return default;
                }
                continue;
            }
            if (terminated || chars.Count >= maximumCharacters)
            {
                diagnostics.Add("FAT32_INVALID_LFN_STRUCTURE", ScanDiagnosticSeverity.Warning, "lfn", "The LFN exceeds its terminator or character bound.", slots[0].Offset);
                return default;
            }
            chars.Add((char)unit);
        }

        var name = new string(chars.ToArray());
        if (!IsWellFormedUtf16(name))
        {
            diagnostics.Add("FAT32_INVALID_LFN_STRUCTURE", ScanDiagnosticSeverity.Warning, "lfn", "The LFN contains invalid UTF-16 surrogate structure.", slots[0].Offset);
            return default;
        }
        if (string.IsNullOrWhiteSpace(name)) return default;
        var state = deleted
            ? checksumValidated ? Fat32NameState.ProbableDeletedLongName : Fat32NameState.AmbiguousDeletedLongName
            : Fat32NameState.VerifiedLongName;
        if (deleted && !checksumValidated)
            diagnostics.Add("FAT32_AMBIGUOUS_DELETED_LFN", ScanDiagnosticSeverity.Information, "lfn", "The deleted LFN is physically plausible but its missing short-name byte is not uniquely validated.", slots[0].Offset);
        return new(name, state, checksumValidated, slots.Count, matches);
    }

    public static string Sanitize(string value, int maximum, out bool changed)
    {
        changed = false;
        var builder = new StringBuilder(Math.Min(value.Length, maximum));
        foreach (var character in value)
        {
            if (builder.Length >= maximum) { changed = true; break; }
            if (char.IsControl(character) || character is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
            {
                builder.Append('_');
                changed = true;
            }
            else builder.Append(character);
        }
        return builder.ToString().TrimEnd(' ', '.');
    }

    public static byte ComputeChecksum(ReadOnlySpan<byte> shortName)
    {
        byte sum = 0;
        foreach (var value in shortName) sum = unchecked((byte)(((sum & 1) << 7) + (sum >> 1) + value));
        return sum;
    }

    private static bool IsPlausibleShortFirst(byte value) =>
        value >= 0x21 && value != 0x7F && value is not (byte)'"' and not (byte)'*' and not (byte)'+' and
        not (byte)',' and not (byte)'.' and not (byte)'/' and not (byte)':' and not (byte)';' and
        not (byte)'<' and not (byte)'=' and not (byte)'>' and not (byte)'?' and not (byte)'[' and
        not (byte)'\\' and not (byte)']' and not (byte)'|';

    private static bool IsWellFormedUtf16(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (++index >= value.Length || !char.IsLowSurrogate(value[index])) return false;
            }
            else if (char.IsLowSurrogate(value[index])) return false;
        }
        return true;
    }

    private static string DecodeOemFallback(ReadOnlySpan<byte> bytes, bool deleted)
    {
        var builder = new StringBuilder(bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (deleted && i == 0) { builder.Append('?'); continue; }
            var value = bytes[i];
            builder.Append(value is >= 0x20 and <= 0x7E && value is not (byte)'/' and not (byte)'\\' ? (char)value : '?');
        }
        return builder.ToString();
    }
}

internal static class Fat32Timestamp
{
    public static (DateTimeOffset? Value, bool IsValid) Decode(ushort date, ushort time, byte tenths, bool includeTime)
    {
        if (date == 0 && time == 0 && tenths == 0) return (null, true);
        if (tenths > 199) return (null, false);
        var year = 1980 + ((date >> 9) & 0x7F);
        var month = (date >> 5) & 0x0F;
        var day = date & 0x1F;
        var hour = includeTime ? (time >> 11) & 0x1F : 0;
        var minute = includeTime ? (time >> 5) & 0x3F : 0;
        var second = includeTime ? (time & 0x1F) * 2 + tenths / 100 : 0;
        var millisecond = includeTime ? tenths % 100 * 10 : 0;
        if (year is < 1980 or > 2107 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
            return (null, false);
        return (new DateTimeOffset(year, month, day, hour, minute, second, millisecond, TimeSpan.Zero), true);
    }
}
