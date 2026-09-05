using System.Text;
using DataRecoveryStudio.Core;
using static DataRecoveryStudio.Infrastructure.ExFatStructures;

namespace DataRecoveryStudio.Infrastructure;

public sealed class ExFatMetadataScanner : IExFatMetadataScanner
{
    public async Task<ExFatScanResult> ScanAsync(IReadOnlyRandomAccessSource source, ExFatScanRequest request,
        IProgress<ExFatScanProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        request.EffectiveBudget.Validate();
        if (request.VolumeOffset < 0) throw new ArgumentOutOfRangeException(nameof(request));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(request.EffectiveBudget.EffectiveDuration);
        return await new Scan(source, request, progress, deadline.Token, cancellationToken).RunAsync().ConfigureAwait(false);
    }

    private sealed class Scan(IReadOnlyRandomAccessSource source, ExFatScanRequest request,
        IProgress<ExFatScanProgress>? progress, CancellationToken token, CancellationToken callerToken)
    {
        private readonly ExFatScanBudget _budget = request.EffectiveBudget;
        private readonly ExFatWork _work = request.Work ?? new(request.EffectiveBudget);
        private readonly List<ExFatScanDiagnostic> _diagnostics = [];
        private readonly List<ExFatScanCandidate> _candidates = [];
        private readonly List<Pending> _pending = [];
        private readonly Dictionary<string, IReadOnlyList<uint>> _recoveryLayouts = new(StringComparer.Ordinal);
        private uint[]? _assessedChain;
        private readonly List<ExFatEntrySet> _rootActiveNames = [];
        private readonly Queue<Directory> _directories = new();
        private readonly HashSet<uint> _owners = [];
        private readonly HashSet<uint> _directoryStarts = [];
        // Fixed direct-mapped caches evict on collision; keys AND values count against the byte limit.
        private readonly uint[] _fatKeys = new uint[request.EffectiveBudget.MaximumFatCacheBytes / 8];
        private readonly uint[] _fatValues = new uint[request.EffectiveBudget.MaximumFatCacheBytes / 8];
        private readonly long[] _bitmapKeys = new long[request.EffectiveBudget.MaximumBitmapCacheBytes / 9];
        private readonly byte[] _bitmapValues = new byte[request.EffectiveBudget.MaximumBitmapCacheBytes / 9];
        private ExFatGeometry? _geometry;
        private ExFatBootEvidence? _boot;
        private Metadata? _bitmapEntry, _upcaseEntry;
        private uint[]? _bitmap;
        private ushort[]? _upcase;
        private bool _bitmapDuplicate, _upcaseDuplicate, _ownershipComplete = true, _partial;
        private bool _guidSeen;
        private bool _bootstrap = true;
        private int _ownershipRecords;
        private int _pathCharacters;
        private string? _label;
        private ExFatScanPhase _phase = ExFatScanPhase.Boot;
        private ExFatGeometry Geometry => _geometry ?? throw new InvalidOperationException();

        internal async Task<ExFatScanResult> RunAsync()
        {
            var outcome = ExFatScanOutcome.Completed;
            try
            {
                await BootAsync().ConfigureAwait(false);
                _phase = ExFatScanPhase.Directories;
                var root = await ChainAsync(Geometry.RootCluster, null, false).ConfigureAwait(false);
                Own(root);
                _directories.Enqueue(new(Geometry.RootCluster, root, (long)root.Length * Geometry.ClusterSize, "\\", 0, false, null));
                await DirectoryAsync(_directories.Dequeue(), true).ConfigureAwait(false);
                await MetadataAsync().ConfigureAwait(false);
                _bootstrap = false;
                var rootNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var active in _rootActiveNames) ValidateActiveName(active, rootNames);
                while (_directories.TryDequeue(out var directory))
                {
                    _work.Check(token);
                    try { await DirectoryAsync(directory, false).ConfigureAwait(false); }
                    catch (Exception exception) when (LocalError(exception)) { Damage("DIRECTORY_" + exception.Message); }
                }
                _phase = ExFatScanPhase.Ownership;
                Report();
                if (_bitmap is not null)
                {
                    foreach (var cluster in _owners)
                    {
                        _work.Check(token);
                        if (await AllocatedAsync(cluster).ConfigureAwait(false) != true)
                        {
                            Damage("ACTIVE_BITMAP_DISAGREEMENT");
                            break;
                        }
                    }
                }
                _phase = ExFatScanPhase.Allocation;
                foreach (var pending in _pending.OrderBy(p => p.Offset))
                {
                    _work.Check(token);
                    await CandidateAsync(pending).ConfigureAwait(false);
                    Report();
                }
                _work.Check(token);
                if (_partial) outcome = ExFatScanOutcome.Partial;
            }
            catch (OperationCanceledException)
            {
                outcome = callerToken.IsCancellationRequested ? ExFatScanOutcome.Canceled : ExFatScanOutcome.Partial;
                if (outcome == ExFatScanOutcome.Partial) { _work.Limited = true; TerminalDiagnostic("BUDGET_Duration"); }
            }
            catch (ExFatBudgetException exception)
            {
                outcome = ExFatScanOutcome.Partial;
                TerminalDiagnostic("BUDGET_" + exception.Message);
            }
            catch (Exception exception) when (LocalError(exception))
            {
                outcome = _geometry is null ? ExFatScanOutcome.InvalidVolume : ExFatScanOutcome.Partial;
                TerminalDiagnostic(exception.Message);
            }
            if (outcome != ExFatScanOutcome.Completed)
            {
                // No partial traversal may leave an optimistic allocation assessment behind.
                for (var i = 0; i < _candidates.Count; i++)
                {
                    _candidates[i] = _candidates[i] with { IsPartial = true };
                    if (_candidates[i].Recoverability == ExFatRecoverabilityState.AllocationSuggestsPossibleContent)
                        _candidates[i] = _candidates[i] with { AllocationEvidence = ExFatAllocationEvidence.Unknown, Recoverability = ExFatRecoverabilityState.Unknown };
                }
            }
            var metrics = _work.Snapshot(ExFatScanPhase.Terminal);
            if (!request.SuppressTerminalProgress) progress?.Report(metrics);
            var seal = new ExFatResultSeal { RecoveryLayouts = new System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<uint>>(_recoveryLayouts) };
            var result = new ExFatScanResult(outcome, _geometry, _boot, _label, _candidates.AsReadOnly(), _diagnostics.AsReadOnly(), metrics) { ProductionSeal = seal };
            seal.Result = result;
            return result;
        }

        private static bool LocalError(Exception exception) => exception is IOException or InvalidDataException or OverflowException or ArgumentOutOfRangeException or DecoderFallbackException;
        private void Diagnostic(string code, long? offset = null, bool partial = true)
        {
            _work.Require(_diagnostics.Count < _budget.MaximumDiagnostics, "Diagnostics");
            _diagnostics.Add(new(code, code.Replace('_', ' '), offset));
            _work.Diagnostics = _diagnostics.Count;
            _partial |= partial;
        }
        private void Damage(string code, long? offset = null)
        {
            _ownershipComplete = false;
            Diagnostic(code, offset);
        }
        private void TerminalDiagnostic(string code)
        {
            if (_budget.MaximumDiagnostics == 0) return;
            if (_diagnostics.Count == _budget.MaximumDiagnostics) _diagnostics.RemoveAt(_diagnostics.Count - 1);
            _diagnostics.Add(new(code, code.Replace('_', ' ')));
            _work.Diagnostics = _diagnostics.Count;
        }
        private void Report()
        {
            _work.Check(token);
            // One callback slot is reserved for the sole terminal result. Exhaustion throttles, not aborts.
            if (progress is null || _work.Callbacks >= _budget.MaximumProgressCallbacks - 1) return;
            _work.Callbacks++;
            progress.Report(_work.Snapshot(_phase));
            _work.Check(token);
        }
        private async ValueTask<byte[]> ReadAsync(long offset, int length, ExFatMetadataReadKind kind = ExFatMetadataReadKind.Boot)
        {
            RandomAccessValidation.ValidateRange(source.Length, offset, length);
            request.ReadObserver?.BeforeRead(offset, length, kind, _bootstrap);
            _work.BeforeRead(length, token);
            var bytes = new byte[length];
            try { await source.ReadExactlyAsync(offset, bytes, token).ConfigureAwait(false); }
            catch
            {
                request.ReadObserver?.ReadFailed(kind);
                throw;
            }
            _work.AfterRead(length, false);
            request.ReadObserver?.AfterRead(offset, bytes, kind, _bootstrap);
            _work.Check(token);
            return bytes;
        }
        private long ClusterOffset(uint cluster)
        {
            if (cluster < 2 || cluster > (ulong)Geometry.ClusterCount + 1) throw new InvalidDataException("CLUSTER_DOMAIN");
            return checked(request.VolumeOffset + (long)Geometry.ClusterHeapOffset * Geometry.BytesPerSector + (long)(cluster - 2) * Geometry.ClusterSize);
        }

        private async Task BootAsync()
        {
            _work.Check(token);
            ExFatGeometry? main = null, backup = null;
            string? mainHash = null, backupHash = null;
            var header = await ReadAsync(request.VolumeOffset, 512).ConfigureAwait(false);
            if (header[108] is >= 9 and <= 12)
            {
                var sector = 1 << header[108];
                try
                {
                    var bytes = await ReadAsync(request.VolumeOffset, sector * 12).ConfigureAwait(false);
                    main = ParseBoot(bytes, sector, request.VolumeOffset, source.Length);
                    mainHash = Hash(bytes);
                }
                catch (Exception exception) when (LocalError(exception)) { Diagnostic("MAIN_" + exception.Message, partial: false); }
            }
            var sectors = main is not null ? new[] { main.BytesPerSector } : new[] { 512, 1024, 2048, 4096 };
            var ambiguousBackup = false;
            foreach (var sector in sectors)
            {
                try
                {
                    var offset = checked(request.VolumeOffset + 12L * sector);
                    var probe = await ReadAsync(offset, 512).ConfigureAwait(false);
                    if (probe[108] is < 9 or > 12 || (1 << probe[108]) != sector || !probe.AsSpan(3, 8).SequenceEqual("EXFAT   "u8)) continue;
                    var bytes = await ReadAsync(offset, sector * 12).ConfigureAwait(false);
                    var parsed = ParseBoot(bytes, sector, request.VolumeOffset, source.Length);
                    if (backup is not null) { ambiguousBackup = true; continue; }
                    backup = parsed;
                    backupHash = Hash(bytes);
                }
                catch (Exception exception) when (LocalError(exception)) { Diagnostic("BACKUP_" + exception.Message, partial: false); }
            }
            if (ambiguousBackup) throw new InvalidDataException("AMBIGUOUS_BACKUP");
            _boot = new(main is not null, backup is not null, main is null && backup is not null, mainHash, backupHash);
            if (main is not null && backup is not null && main.Fingerprint != backup.Fingerprint)
                throw new InvalidDataException("BOOT_GEOMETRY_DISAGREEMENT");
            _geometry = main ?? backup ?? throw new InvalidDataException("NO_VALID_BOOT_REGION");
            if (main is not null && backup is not null && main.SerialNumber != backup.SerialNumber) Diagnostic("BOOT_SERIAL_DISAGREEMENT", partial: false);
            if (main is null) Diagnostic("BACKUP_FALLBACK", partial: false);
            if (backup is null) Diagnostic("BACKUP_INVALID", partial: false);
            if (Geometry.PercentInUse > 100 && Geometry.PercentInUse != 255) Diagnostic("PERCENT_IN_USE_ADVISORY", partial: false);
            if ((Geometry.VolumeFlags & 6) != 0 || main is null)
            {
                // Backup mutable fields are stale; safe discovery is possible but allocation is unknown.
                _ownershipComplete = false;
                Diagnostic(main is null ? "BACKUP_MUTABLE_FLAGS_STALE" : "VOLUME_DIRTY_OR_MEDIA_FAILURE");
            }
            var fatHeader = await ReadAsync(checked(request.VolumeOffset + Geometry.FatOffset * (long)Geometry.BytesPerSector), 8, ExFatMetadataReadKind.Fat).ConfigureAwait(false);
            if ((U32(fatHeader, 0) & 0xFFFFFF00) != 0xFFFFFF00 || U32(fatHeader, 4) != 0xFFFFFFFF)
                throw new InvalidDataException("FAT_HEADER");
            Report();
        }

        private async ValueTask<uint> FatAsync(uint cluster)
        {
            _work.Check(token);
            _ = ClusterOffset(cluster);
            _work.Require(_work.FatEntries < _budget.MaximumFatEntries, "FatEntries");
            _work.FatEntries++;
            var slot = _fatKeys.Length == 0 ? -1 : (int)(cluster % _fatKeys.Length);
            if (slot >= 0 && _fatKeys[slot] == cluster) return _fatValues[slot];
            var bytes = await ReadAsync(checked(request.VolumeOffset + (long)Geometry.FatOffset * Geometry.BytesPerSector + cluster * 4L), 4, ExFatMetadataReadKind.Fat).ConfigureAwait(false);
            var value = U32(bytes, 0);
            if (slot >= 0)
            {
                _fatKeys[slot] = cluster;
                _fatValues[slot] = value;
            }
            return value;
        }
        private async Task<uint[]> ChainAsync(uint first, long? length, bool contiguous)
        {
            _work.Check(token);
            _work.Require(_work.Chains < _budget.MaximumChains, "Chains");
            _work.Chains++;
            var needed = length.HasValue ? length.Value / Geometry.ClusterSize + (length.Value % Geometry.ClusterSize == 0 ? 0 : 1) : (long?)null;
            if (needed == 0) return first == 0 ? [] : throw new InvalidDataException("ZERO_LENGTH_CLUSTER");
            if (needed > Geometry.ClusterCount) throw new InvalidDataException("STREAM_OUTSIDE_HEAP");
            var chain = new List<uint>();
            var seen = new HashSet<uint>();
            var current = first;
            while (true)
            {
                _work.Check(token);
                _work.Require(chain.Count < _budget.MaximumChainLength, "ChainLength");
                _work.Require(_work.VisitedClusters < _budget.MaximumVisitedClusters, "VisitedClusters");
                _ = ClusterOffset(current);
                if (!seen.Add(current)) throw new InvalidDataException("FAT_CHAIN_CYCLE");
                chain.Add(current);
                _work.VisitedClusters++;
                if (contiguous)
                {
                    if (chain.Count == needed) return chain.ToArray();
                    current = checked(current + 1);
                    continue;
                }
                var next = await FatAsync(current).ConfigureAwait(false);
                if (next == 0xFFFFFFFF)
                {
                    if (needed.HasValue && needed != chain.Count) throw new InvalidDataException("FAT_CHAIN_SHORT");
                    return chain.ToArray();
                }
                if (next == 0) throw new InvalidDataException("FAT_CHAIN_MISSING");
                if (next == 0xFFFFFFF7) throw new InvalidDataException("FAT_BAD_CLUSTER");
                if (next > 0xFFFFFFF7 || next == 1) throw new InvalidDataException("FAT_RESERVED");
                if (seen.Contains(next)) throw new InvalidDataException("FAT_CHAIN_CYCLE");
                if (needed.HasValue && chain.Count >= needed) throw new InvalidDataException("FAT_CHAIN_EXCESS");
                current = next;
            }
        }
        private bool Own(IEnumerable<uint> clusters)
        {
            var good = true;
            foreach (var cluster in clusters)
            {
                _work.Check(token);
                if (!_owners.Contains(cluster)) ReserveOwnershipRecord();
                if (!_owners.Add(cluster)) { good = false; Damage("ACTIVE_CROSS_LINK"); }
            }
            return good;
        }

        private async IAsyncEnumerable<Slot> SlotsAsync(Directory directory)
        {
            long remaining = directory.Length;
            foreach (var cluster in directory.Clusters)
            {
                for (var position = 0; position < Geometry.ClusterSize && remaining > 0;)
                {
                    _work.Check(token);
                    var count = (int)Math.Min(512, Math.Min(Geometry.ClusterSize - position, remaining));
                    _work.Require(count <= _budget.MaximumDirectoryBytes - _work.DirectoryBytes, "DirectoryBytes");
                    var offset = checked(ClusterOffset(cluster) + position);
                    var bytes = await ReadAsync(offset, count, ExFatMetadataReadKind.Directory).ConfigureAwait(false);
                    _work.DirectoryBytes += count;
                    for (var i = 0; i + 32 <= count; i += 32)
                    {
                        _work.Check(token);
                        _work.Require(_work.Entries < _budget.MaximumEntries, "Entries");
                        _work.Entries++;
                        yield return new(offset + i, bytes.AsSpan(i, 32).ToArray());
                    }
                    position += count;
                    remaining -= count;
                }
            }
        }

        private async Task DirectoryAsync(Directory directory, bool root)
        {
            _phase = ExFatScanPhase.Directories;
            _work.Require(directory.Depth <= _budget.MaximumDirectoryDepth, "DirectoryDepth");
            _work.Require(_work.Directories < _budget.MaximumDirectories, "Directories");
            if (!_directoryStarts.Add(directory.First)) { Damage("DIRECTORY_CYCLE_CROSS_LINK"); return; }
            _work.Directories++;
            if (directory.Entry is not null)
                directory = directory with { Uncertain = directory.Uncertain || _upcase is null || NameHash(directory.Entry.Name, _upcase, token) != directory.Entry.NameHash };
            Report();
            var activeNames = new HashSet<string>(StringComparer.Ordinal);
            await using var slots = SlotsAsync(directory).GetAsyncEnumerator(token);
            while (await slots.MoveNextAsync().ConfigureAwait(false))
            {
                _work.Check(token);
                var slot = slots.Current;
                var type = slot.Bytes[0];
                if (type == 0) break;
                if (type is 0x81 or 0x82)
                {
                    if (!root) { Damage("METADATA_OUTSIDE_ROOT", slot.Offset); continue; }
                    RegisterMetadata(slot);
                    continue;
                }
                if (type == 0x83)
                {
                    try
                    {
                        if (!root || _label is not null || slot.Bytes[1] > 11) throw new InvalidDataException("VOLUME_LABEL");
                        _label = Display(DecodeName(slot.Bytes.AsSpan(2, slot.Bytes[1] * 2)));
                    }
                    catch (Exception exception) when (LocalError(exception)) { Diagnostic(exception.Message, slot.Offset); }
                    continue;
                }
                if (type == 0xA0)
                {
                    if (!root || _guidSeen || slot.Bytes[1] != 0 || U16(slot.Bytes, 4) != 0 ||
                        slot.Bytes.AsSpan(6, 16).IndexOfAnyExcept((byte)0) < 0 || SetChecksum(slot.Bytes, false) != U16(slot.Bytes, 2)) Damage("VOLUME_GUID", slot.Offset);
                    _guidSeen = true;
                    continue;
                }
                if (type is not (0x85 or 0x05))
                {
                    if ((type & 0x80) == 0 && (type & 0x40) == 0) continue; // inactive primary metadata
                    if ((type & 0xE0) == 0xA0) { Damage("UNSUPPORTED_BENIGN_METADATA", slot.Offset); continue; }
                    Damage((type & 0x40) != 0 ? "ORPHAN_SECONDARY" : "UNKNOWN_CRITICAL_ENTRY", slot.Offset);
                    continue;
                }
                var secondaryCount = slot.Bytes[1];
                if (secondaryCount < 2) { Damage("SECONDARY_COUNT", slot.Offset); continue; }
                _work.Require(secondaryCount <= _budget.MaximumSecondaryCount, "SecondaryCount");
                var bytes = new byte[(secondaryCount + 1) * 32];
                slot.Bytes.CopyTo(bytes, 0);
                var truncated = false;
                for (var i = 1; i <= secondaryCount; i++)
                {
                    _work.Check(token);
                    if (!await slots.MoveNextAsync().ConfigureAwait(false) || slots.Current.Bytes[0] == 0) { truncated = true; break; }
                    slots.Current.Bytes.CopyTo(bytes, i * 32);
                }
                if (truncated) { Damage("TRUNCATED_ENTRY_SET", slot.Offset); break; }
                try
                {
                    _work.Require(bytes[35] <= _budget.MaximumFilenameLength, "FilenameLength");
                    var set = ParseSet(bytes, _budget, token);
                    _work.Require(set.Name.Length <= _budget.MaximumTotalFilenameCharacters - _work.NameCharacters, "FilenameCharacters");
                    _work.NameCharacters += set.Name.Length;
                    _work.Sets++;
                    if (set.Deleted)
                    {
                        if (set.Directory) continue;
                        _work.Require(_pending.Count < _budget.MaximumCandidates, "Candidates");
                        _pending.Add(new(set, directory.First, slot.Offset, directory.Path, directory.Uncertain));
                    }
                    else
                    {
                        ReserveOwnershipRecord();
                        if (root) _rootActiveNames.Add(set); else ValidateActiveName(set, activeNames);
                        var chain = await ChainAsync(set.FirstCluster, set.Size, set.Contiguous).ConfigureAwait(false);
                        var unique = Own(chain);
                        if (set.Directory && unique && set.Size > 0)
                        {
                            if (set.Size > 256L * 1024 * 1024 || set.Size % Geometry.ClusterSize != 0 || set.ValidLength != set.Size)
                                throw new InvalidDataException("DIRECTORY_LENGTH");
                            _work.Require(_directories.Count + _work.Directories < _budget.MaximumDirectories, "Directories");
                            var pathLength = directory.Path.Length + set.Name.Length + 1;
                            _work.Require(pathLength <= _budget.MaximumPathCharacters, "PathCharacters");
                            _work.Require(pathLength <= _budget.MaximumTotalPathCharacters - _pathCharacters, "TotalPathCharacters");
                            _pathCharacters += pathLength;
                            _directories.Enqueue(new(set.FirstCluster, chain, set.ValidLength,
                                directory.Path + Display(set.Name) + "\\", directory.Depth + 1, directory.Uncertain, set));
                        }
                    }
                }
                catch (Exception exception) when (LocalError(exception)) { Damage(exception.Message, slot.Offset); }
                Report();
            }
        }

        private void ValidateActiveName(ExFatEntrySet set, HashSet<string> names)
        {
            _work.Check(token);
            if (_upcase is not null && NameHash(set.Name, _upcase, token) != set.NameHash) Damage("ACTIVE_NAME_HASH");
            if (_upcase is not null && !names.Add(new string(set.Name.Select(ch => (char)_upcase[ch]).ToArray()))) Damage("DUPLICATE_ACTIVE_NAME");
        }

        private void ReserveOwnershipRecord()
        {
            _work.Require(_ownershipRecords < _budget.MaximumOwnershipRecords, "OwnershipRecords");
            _ownershipRecords++;
        }

        private void RegisterMetadata(Slot slot)
        {
            try
            {
                var length = checked((long)U64(slot.Bytes, 24));
                var first = U32(slot.Bytes, 20);
                _ = ClusterOffset(first);
                if (length <= 0) throw new InvalidDataException("METADATA_LENGTH");
                var metadata = new Metadata(first, length, U32(slot.Bytes, 4));
                if (slot.Bytes[0] == 0x81)
                {
                    if (_bitmapEntry is not null) { _bitmapDuplicate = true; Damage("DUPLICATE_BITMAP", slot.Offset); }
                    _bitmapEntry = metadata;
                    if (slot.Bytes[1] != 0 || slot.Bytes.AsSpan(2, 18).IndexOfAnyExcept((byte)0) >= 0 || length != ((long)Geometry.ClusterCount + 7) / 8)
                    { _bitmapDuplicate = true; Damage("BITMAP_FLAGS_LENGTH", slot.Offset); }
                }
                else
                {
                    if (_upcaseEntry is not null) { _upcaseDuplicate = true; Damage("DUPLICATE_UPCASE", slot.Offset); }
                    _upcaseEntry = metadata;
                    if (slot.Bytes.AsSpan(1, 3).IndexOfAnyExcept((byte)0) >= 0 || slot.Bytes.AsSpan(8, 12).IndexOfAnyExcept((byte)0) >= 0)
                    { _upcaseDuplicate = true; Damage("UPCASE_RESERVED", slot.Offset); }
                }
            }
            catch (Exception exception) when (LocalError(exception))
            {
                if (slot.Bytes[0] == 0x81) _bitmapDuplicate = true; else _upcaseDuplicate = true;
                Damage(exception.Message, slot.Offset);
            }
        }
        private async Task MetadataAsync()
        {
            if (_bitmapEntry is not null && !_bitmapDuplicate)
            {
                try
                {
                    var chain = await ChainAsync(_bitmapEntry.First, _bitmapEntry.Length, false).ConfigureAwait(false);
                    if (Own(chain)) _bitmap = chain;
                }
                catch (Exception exception) when (LocalError(exception)) { Damage("BITMAP_" + exception.Message); }
            }
            if (_bitmap is null) Damage("BITMAP_UNAVAILABLE");
            _phase = ExFatScanPhase.UpCase;
            Report();
            if (_upcaseEntry is not null && !_upcaseDuplicate)
            {
                try
                {
                    _work.Require(_upcaseEntry.Length <= _budget.MaximumUpCaseBytes, "UpCaseBytes");
                    _work.Require(_budget.MaximumUpCaseCacheBytes >= 131072, "UpCaseCache");
                    var chain = await ChainAsync(_upcaseEntry.First, _upcaseEntry.Length, false).ConfigureAwait(false);
                    if (Own(chain))
                    {
                        var bytes = new byte[(int)_upcaseEntry.Length];
                        var position = 0;
                        foreach (var cluster in chain)
                        {
                            var count = Math.Min(Geometry.ClusterSize, bytes.Length - position);
                            var chunk = await ReadAsync(ClusterOffset(cluster), count, ExFatMetadataReadKind.UpCase).ConfigureAwait(false);
                            chunk.CopyTo(bytes, position);
                            position += count;
                        }
                        _upcase = ExpandUpCase(bytes, _upcaseEntry.Checksum, token);
                        _work.Check(token);
                    }
                }
                catch (Exception exception) when (LocalError(exception)) { Damage("UPCASE_" + exception.Message); }
            }
            if (_upcase is null) Diagnostic("UPCASE_UNAVAILABLE");
        }
        private async ValueTask<bool?> AllocatedAsync(uint cluster)
        {
            _work.Check(token);
            _work.Require(_work.BitmapQueries < _budget.MaximumBitmapQueries, "BitmapQueries");
            _work.BitmapQueries++;
            if (_bitmap is null || _bitmapEntry is null || cluster < 2 || cluster > (ulong)Geometry.ClusterCount + 1) return null;
            var index = (cluster - 2L) / 8;
            if (index >= _bitmapEntry.Length) return null;
            var slot = _bitmapKeys.Length == 0 ? -1 : (int)(index % _bitmapKeys.Length);
            byte value;
            if (slot >= 0 && _bitmapKeys[slot] == index + 1) value = _bitmapValues[slot];
            else
            {
                var chainIndex = index / Geometry.ClusterSize;
                if (chainIndex >= _bitmap.Length) return null;
                try
                {
                    var bytes = await ReadAsync(checked(ClusterOffset(_bitmap[chainIndex]) + index % Geometry.ClusterSize), 1, ExFatMetadataReadKind.Bitmap).ConfigureAwait(false);
                    value = bytes[0];
                }
                catch (Exception exception) when (LocalError(exception)) { Damage("BITMAP_READ_FAILURE"); _bitmap = null; return null; }
                if (slot >= 0)
                {
                    _bitmapKeys[slot] = index + 1;
                    _bitmapValues[slot] = value;
                }
            }
            return (value & 1 << (int)((cluster - 2) % 8)) != 0;
        }

        private async Task CandidateAsync(Pending pending)
        {
            _assessedChain = null;
            var set = pending.Set;
            var hashValid = _upcase is not null && NameHash(set.Name, _upcase, token) == set.NameHash;
            var checksum = set.OrdinaryChecksum || set.RecoveredChecksum;
            var name = hashValid && set.OrdinaryChecksum ? ExFatNameEvidence.VerifiedDeletedName :
                hashValid && set.RecoveredChecksum ? ExFatNameEvidence.ChecksumRecoveredName :
                hashValid ? ExFatNameEvidence.HashVerifiedName : checksum && _upcase is null ? ExFatNameEvidence.ProbableName : ExFatNameEvidence.AmbiguousName;
            var allocation = await AssessAsync(set, checksum).ConfigureAwait(false);
            var recoverability = allocation switch
            {
                ExFatAllocationEvidence.ZeroLength => ExFatRecoverabilityState.EmptyFileMetadata,
                ExFatAllocationEvidence.ContiguousAllFree or ExFatAllocationEvidence.PreservedChainAllFree => ExFatRecoverabilityState.AllocationSuggestsPossibleContent,
                ExFatAllocationEvidence.Unknown or ExFatAllocationEvidence.BitmapUnavailable => ExFatRecoverabilityState.Unknown,
                _ => ExFatRecoverabilityState.Blocked,
            };
            var provenance = new ExFatCandidateProvenance(pending.Parent, pending.Offset, set.Fingerprint, set.FirstCluster,
                set.Size, set.ValidLength, set.Flags, set.Name, set.OrdinaryChecksum, set.RecoveredChecksum, hashValid);
            var identity = FormattableString.Invariant($"{ExFatScannerVersion.Phase8A}|{request.SourceIdentity}|{request.VolumeOffset}|{Geometry.Fingerprint}|{pending.Parent}|{pending.Offset}|{set.Fingerprint}|{set.FirstCluster}|{set.Size}|{set.ValidLength}|{set.Flags}|{name}");
            var candidateId = Hash(Encoding.UTF8.GetBytes(identity));
            if (request.RecoverySelection?.Contains(candidateId) == true &&
                allocation is ExFatAllocationEvidence.ZeroLength or ExFatAllocationEvidence.ContiguousAllFree or ExFatAllocationEvidence.PreservedChainAllFree)
                _recoveryLayouts.Add(candidateId, Array.AsReadOnly(_assessedChain ?? []));
            var codes = new List<string>();
            if (!hashValid) codes.Add("NAME_HASH_UNVERIFIED");
            if (!checksum) codes.Add("DELETED_CHECKSUM_UNVERIFIED");
            if (set.RecoveredChecksum) codes.Add("DELETED_IN_USE_BITS_RESTORED");
            if (set.Created.State == ExFatTimestampState.Invalid || set.Modified.State == ExFatTimestampState.Invalid || set.Accessed.State == ExFatTimestampState.Invalid) codes.Add("INVALID_TIMESTAMP");
            _work.Check(token);
            _candidates.Add(new(candidateId, Display(set.Name), pending.Path,
                pending.Uncertain ? ExFatPathState.NameUncertain : ExFatPathState.CompleteActiveParent,
                set.Size, set.ValidLength, set.Contiguous ? ExFatLayout.Contiguous : ExFatLayout.FatChain, set.Attributes,
                set.Created, set.Modified, set.Accessed, name, allocation, recoverability,
                !checksum || !hashValid || codes.Contains("INVALID_TIMESTAMP"), codes.AsReadOnly())
            { Provenance = provenance });
            _work.Candidates = _candidates.Count;
        }
        private async Task<ExFatAllocationEvidence> AssessAsync(ExFatEntrySet set, bool checksum)
        {
            if (!checksum) return ExFatAllocationEvidence.DamagedMetadata;
            if (set.Size == 0) return ExFatAllocationEvidence.ZeroLength;
            uint[] chain;
            try { chain = await ChainAsync(set.FirstCluster, set.Size, set.Contiguous).ConfigureAwait(false); }
            catch (Exception exception) when (LocalError(exception))
            {
                if (exception.Message == "FAT_CHAIN_MISSING" && await AllocatedAsync(set.FirstCluster).ConfigureAwait(false) == true)
                    return ExFatAllocationEvidence.FatBitmapDisagreement;
                return exception.Message switch
                {
                    "FAT_CHAIN_MISSING" => ExFatAllocationEvidence.FatChainMissing,
                    "FAT_CHAIN_CYCLE" => ExFatAllocationEvidence.FatChainCycle,
                    _ => ExFatAllocationEvidence.FatChainDamaged,
                };
            }
            if (chain.Any(_owners.Contains)) return ExFatAllocationEvidence.ActiveOwnershipConflict;
            if (!_ownershipComplete) return ExFatAllocationEvidence.Unknown;
            if (_bitmap is null) return ExFatAllocationEvidence.BitmapUnavailable;
            var allocated = 0;
            foreach (var cluster in chain)
            {
                _work.Check(token);
                var bit = await AllocatedAsync(cluster).ConfigureAwait(false);
                if (bit is null) return ExFatAllocationEvidence.Unknown;
                if (bit.Value) allocated++;
                // NoFatChain deliberately ignores stale FAT entries. FAT-chained entries were validated above.
            }
            if (!_ownershipComplete) return ExFatAllocationEvidence.Unknown;
            _assessedChain = chain;
            return set.Contiguous
                ? allocated == 0 ? ExFatAllocationEvidence.ContiguousAllFree : allocated == chain.Length ? ExFatAllocationEvidence.ContiguousFullyAllocated : ExFatAllocationEvidence.ContiguousPartiallyAllocated
                : allocated == 0 ? ExFatAllocationEvidence.PreservedChainAllFree : allocated == chain.Length ? ExFatAllocationEvidence.PreservedChainFullyAllocated : ExFatAllocationEvidence.PreservedChainPartiallyAllocated;
        }
        private sealed record Slot(long Offset, byte[] Bytes);
        private sealed record Metadata(uint First, long Length, uint Checksum);
        private sealed record Directory(uint First, uint[] Clusters, long Length, string Path, int Depth, bool Uncertain, ExFatEntrySet? Entry);
        private sealed record Pending(ExFatEntrySet Set, uint Parent, long Offset, string Path, bool Uncertain);
    }
}
