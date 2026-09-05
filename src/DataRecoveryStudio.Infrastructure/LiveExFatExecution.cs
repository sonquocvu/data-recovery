using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

public sealed partial class LiveScanExecutor
{
    private async Task<LiveScanExecutionResult> ExecuteExFatAsync(Guid sessionId,
        IReadOnlyRandomAccessSource source, LiveScanBudgets limits, IProgress<LiveScanProgressDto>? progress,
        CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(limits.EffectiveMaximumDuration);
        token = deadline.Token;
        var budget = new ExFatScanBudget(MaximumSourceBytes: limits.MaximumBytesRead,
            MaximumDirectories: Math.Min(100_000, limits.MaximumFatDirectories),
            MaximumEntries: Math.Min(2_000_000, limits.MaximumFatDirectoryEntries),
            MaximumFatEntries: Math.Min(4_000_000, limits.MaximumFatEntriesInspected),
            MaximumVisitedClusters: Math.Min(1_000_000, limits.MaximumFatDirectoryClusters),
            MaximumCandidates: limits.MaximumCandidates, MaximumDiagnostics: limits.MaximumDiagnostics,
            MaximumDuration: limits.EffectiveMaximumDuration);
        var work = new ExFatWork(budget);
        var adapter = new LiveExFatProgress(progress);
        var evidence = new LiveExFatReadEvidence(work, source is LiveVolumeRandomAccessSource live ? () => live.TotalBytesRead : null, adapter.Report);
        var result = await new ExFatMetadataScanner().ScanAsync(source,
            new ExFatScanRequest(Budget: budget) { Work = work, ReadObserver = evidence, SuppressTerminalProgress = true },
            adapter, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var before = evidence.Fingerprint();
        var after = result.Geometry is null ? null : await evidence.VerifyAsync(source, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var changed = result.Geometry is not null && (after is null || before != after || evidence.Changed);
        var partial = result.Outcome != ExFatScanOutcome.Completed || work.Limited ||
            result.BootEvidence is not { MainValid: true, BackupValid: true } ||
            result.Diagnostics.Any(d => d.Code == "BOOT_SERIAL_DISAGREEMENT") ||
            result.Candidates.Any(c => c.MetadataDamaged || c.IsPartial);
        var status = changed ? LiveScanTerminalStatus.ChangedDuringScan : result.Geometry is null
            ? LiveScanTerminalStatus.Failed : partial ? LiveScanTerminalStatus.Partial : LiveScanTerminalStatus.Completed;
        var candidates = status is LiveScanTerminalStatus.Completed or LiveScanTerminalStatus.Partial
            ? result.Candidates.Select(c => NormalizeExFat(sessionId, c, partial)).ToArray() : [];
        var diagnostics = result.Diagnostics.Select(d => new LiveScanDiagnosticDto(SanitizeCode(d.Code),
            ScanDiagnosticSeverity.Warning, "ExFatMetadata")).ToArray();
        adapter.ReportFinal(work.Snapshot(ExFatScanPhase.Finalizing));
        return new(new(status, changed ? LiveScanConsistency.ChangedDuringScan : partial
            ? LiveScanConsistency.Partial : LiveScanConsistency.LiveBestEffort,
            candidates.Length, diagnostics.Length, work.Entries, work.Bytes, partial || changed,
            changed ? "ExFatCriticalEvidenceChanged" : status == LiveScanTerminalStatus.Failed ? "ExFatInvalidVolume" :
                work.Limited ? "ExFatBudgetReached" : partial ? "ExFatMetadataPartial" : null)
        {
            ScannerKind = LiveScanScannerKind.ExFatStandardMetadata,
            FileSystem = "exFAT",
            DirectoriesExamined = work.Directories,
            DirectoryEntriesExamined = work.Entries,
            FatEntriesInspected = work.FatEntries,
            ConsistencyEvidenceBefore = before,
            ConsistencyEvidenceAfter = after,
            ExFat = new(ExFatScannerVersion.Phase8A, result.Geometry is not null,
                result.BootEvidence?.MainValid ?? false, result.BootEvidence?.BackupValid ?? false,
                result.BootEvidence?.UsedBackup ?? false, evidence.Count, evidence.SampleBytes,
                evidence.Bytes[0], evidence.Bytes[1], evidence.Bytes[2], evidence.Bytes[3], evidence.Bytes[4],
                evidence.ConsistencyBytes, work.Limited),
        }, candidates, diagnostics);
    }

    private static LiveScanCandidateDto NormalizeExFat(Guid session, ExFatScanCandidate c, bool partial)
    {
        var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{session:D}|{c.CandidateId}"))[..16]);
        return new(id, session, 0, 0, SanitizeText(c.DisplayName, 255), SanitizeText(c.ParentPath, 32768),
            c.LogicalSize, Categorize(c.DisplayName), true, false, CandidatePathState.Invalid,
            CandidateRecoverability.Unknown, [], c.DiagnosticCodes.Select(SanitizeCode).Take(128).ToArray())
        {
            ScannerKind = LiveScanScannerKind.ExFatStandardMetadata,
            FileSystem = "exFAT",
            ExFat = new(c.ValidDataLength, c.NameEvidence, c.PathState, c.Layout,
                partial && c.AllocationEvidence != ExFatAllocationEvidence.ActiveOwnershipConflict ? ExFatAllocationEvidence.Unknown : c.AllocationEvidence,
                partial ? ExFatRecoverabilityState.Unknown : c.Recoverability, c.Attributes,
                c.Created, c.Modified, c.Accessed, c.MetadataDamaged, partial || c.IsPartial),
        };
    }

    private sealed class LiveExFatProgress(IProgress<LiveScanProgressDto>? target) : IProgress<ExFatScanProgress>
    {
        private TimeSpan _last = TimeSpan.MinValue;
        private int _reports;
        public void Report(ExFatScanProgress p)
        {
            if (_reports >= 18000 || (_last != TimeSpan.MinValue && p.Elapsed - _last < TimeSpan.FromMilliseconds(100))) return;
            ReportFinal(p);
        }
        internal void ReportFinal(ExFatScanProgress p)
        {
            _last = p.Elapsed;
            _reports++;
            target?.Report(new(p.EntriesExamined, 0, p.SourceBytesRead, p.DeletedCandidatesFound, $"LiveScan.ExFat.{p.Phase}")
            {
                ScannerKind = LiveScanScannerKind.ExFatStandardMetadata,
                DirectoriesExamined = p.DirectoriesVisited,
                DirectoryEntriesExamined = p.EntriesExamined,
                FatEntriesInspected = p.FatEntriesInspected,
                IsBudgetLimited = p.BudgetLimited,
            });
        }
    }
}

// Hashes only parser-requested bootstrap metadata. Reserve each required reread before traversal spends its budget.
internal sealed class LiveExFatReadEvidence(ExFatWork work, Func<long>? actualBytes, Action<ExFatScanProgress>? onRead = null) : IExFatMetadataReadObserver
{
    private readonly Dictionary<(long Offset, int Length), byte[]?> _samples = [];
    private long _readStart;
    internal long[] Bytes { get; } = new long[5];
    internal long SampleBytes { get; private set; }
    internal long ConsistencyBytes { get; private set; }
    internal int Count => _samples.Count;
    internal bool Changed { get; private set; }

    public void BeforeRead(long offset, int length, ExFatMetadataReadKind kind, bool bootstrap)
    {
        _readStart = actualBytes?.Invoke() ?? 0;
        if (!bootstrap || _samples.ContainsKey((offset, length))) return;
        work.Require(_samples.Count < 32768 && length <= 8 * 1024 * 1024 - SampleBytes, "LiveEvidence");
        work.ReservedForFutureBytes += length;
        SampleBytes += length;
        _samples.Add((offset, length), null);
    }

    public void AfterRead(long offset, ReadOnlySpan<byte> bytes, ExFatMetadataReadKind kind, bool bootstrap)
    {
        Bytes[(int)kind] += bytes.Length;
        onRead?.Invoke(work.Snapshot(kind switch
        {
            ExFatMetadataReadKind.Boot => ExFatScanPhase.Boot,
            ExFatMetadataReadKind.UpCase => ExFatScanPhase.UpCase,
            ExFatMetadataReadKind.Bitmap => ExFatScanPhase.Allocation,
            _ => ExFatScanPhase.Directories,
        }));
        if (!_samples.TryGetValue((offset, bytes.Length), out var previous)) return;
        var hash = SHA256.HashData(bytes);
        if (previous is not null && !previous.AsSpan().SequenceEqual(hash)) Changed = true;
        else _samples[(offset, bytes.Length)] = hash;
    }

    public void ReadFailed(ExFatMetadataReadKind kind)
    {
        var partial = checked((int)((actualBytes?.Invoke() ?? _readStart) - _readStart));
        Bytes[(int)kind] += partial;
        work.AfterRead(partial, false);
    }

    internal string Fingerprint()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> range = stackalloc byte[12];
        foreach (var item in _samples.OrderBy(s => s.Key.Offset).ThenBy(s => s.Key.Length))
        {
            BinaryPrimitives.WriteInt64LittleEndian(range, item.Key.Offset);
            BinaryPrimitives.WriteInt32LittleEndian(range[8..], item.Key.Length);
            hash.AppendData(range);
            hash.AppendData(item.Value ?? new byte[32]);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal async Task<string?> VerifyAsync(IReadOnlyRandomAccessSource source, CancellationToken token)
    {
        try
        {
            foreach (var key in _samples.Keys.ToArray())
            {
                // A failed initial read has no before evidence and can never establish a matching sample.
                if (_samples[key] is null) return null;
                work.ReservedForFutureBytes -= key.Length;
                work.BeforeRead(key.Length, token);
                var bytes = new byte[key.Length];
                var start = actualBytes?.Invoke() ?? 0;
                try { await source.ReadExactlyAsync(key.Offset, bytes, token).ConfigureAwait(false); }
                catch
                {
                    var partial = checked((int)((actualBytes?.Invoke() ?? start) - start));
                    work.AfterRead(partial, false);
                    ConsistencyBytes += partial;
                    throw;
                }
                work.AfterRead(bytes.Length, false);
                ConsistencyBytes += bytes.Length;
                onRead?.Invoke(work.Snapshot(ExFatScanPhase.Finalizing));
                _samples[key] = SHA256.HashData(bytes);
            }
            return Fingerprint();
        }
        catch (Exception exception) when (exception is IOException or ExFatBudgetException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
