using System.Buffers.Binary;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

public sealed class NtfsMetadataScanner : INtfsMetadataScanner
{
    private const int BootSectorReadSize = 512;

    public async Task<StandardScanResult> ScanAsync(
        IReadOnlyRandomAccessSource source,
        StandardScanRequest request,
        IProgress<StandardScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        var budgets = request.EffectiveBudgets;
        budgets.Validate();
        var diagnostics = new DiagnosticCollector(budgets.MaximumDiagnostics);
        var readBudget = new ScanReadBudget(budgets.MaximumBytesRead);
        var candidates = new List<DeletedFileCandidate>();
        var recordsProcessed = 0L;

        if (request.Volume.VolumeOffset < 0)
        {
            diagnostics.Add("NTFS_CONTEXT_INVALID", ScanDiagnosticSeverity.Error, "volume-context", "The volume offset must be non-negative.");
            return Result(StandardScanOutcome.InvalidVolume, null, null, candidates, diagnostics, recordsProcessed, readBudget.BytesRead);
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new(0, 0, 0, 0, "Reading NTFS boot sector"));
        var bootSector = new byte[BootSectorReadSize];
        try
        {
            await readBudget.ReadExactlyAsync(source, request.Volume.VolumeOffset, bootSector, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ScanBudgetExceededException)
        {
            diagnostics.Add("SCAN_BYTE_BUDGET_REACHED", ScanDiagnosticSeverity.Warning, "boot-sector", "The byte-read budget is too small for NTFS bootstrap.", request.Volume.VolumeOffset);
            return Result(StandardScanOutcome.Partial, null, null, candidates, diagnostics, recordsProcessed, readBudget.BytesRead, "MaximumBytesRead");
        }
        catch (Exception exception) when (exception is IOException or ArgumentOutOfRangeException or EndOfStreamException)
        {
            diagnostics.Add("NTFS_BOOT_READ_FAILED", ScanDiagnosticSeverity.Error, "boot-sector", "The bounded boot-sector range could not be read.", request.Volume.VolumeOffset);
            return Result(StandardScanOutcome.InvalidVolume, null, null, candidates, diagnostics, recordsProcessed, readBudget.BytesRead);
        }

        if (!NtfsBootSectorParser.TryParse(bootSector, out var geometry, out var code, out var reason))
        {
            diagnostics.Add(code, ScanDiagnosticSeverity.Error, "boot-sector", reason, request.Volume.VolumeOffset);
            return Result(StandardScanOutcome.InvalidVolume, null, null, candidates, diagnostics, recordsProcessed, readBudget.BytesRead);
        }

        var validGeometry = geometry!;
        if (!TryCalculateVolume(source.Length, request.Volume.VolumeOffset, validGeometry, out var volumeEnd, out code, out reason))
        {
            diagnostics.Add(code, ScanDiagnosticSeverity.Error, "volume-geometry", reason, request.Volume.VolumeOffset);
            return Result(StandardScanOutcome.InvalidVolume, validGeometry, null, candidates, diagnostics, recordsProcessed, readBudget.BytesRead);
        }

        MftBootstrap bootstrap;
        try
        {
            bootstrap = await BootstrapMftAsync(source, readBudget, request.Volume.VolumeOffset, volumeEnd, validGeometry, budgets, diagnostics, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ScanBudgetExceededException exception)
        {
            diagnostics.Add("SCAN_BYTE_BUDGET_REACHED", ScanDiagnosticSeverity.Warning, "mft-bootstrap", "The byte-read budget was reached during MFT bootstrap.");
            return Result(StandardScanOutcome.Partial, validGeometry, null, candidates, diagnostics, recordsProcessed, readBudget.BytesRead, exception.BudgetName);
        }
        catch (NtfsBootstrapException exception)
        {
            diagnostics.Add(exception.Code, ScanDiagnosticSeverity.Error, "mft-bootstrap", exception.Message);
            return Result(StandardScanOutcome.InvalidVolume, validGeometry, null, candidates, diagnostics, recordsProcessed, readBudget.BytesRead);
        }

        var derivedCount = bootstrap.Layout.DerivedRecordCount;
        var recordsToScan = Math.Min(derivedCount, budgets.MaximumRecords);
        var outcome = recordsToScan < derivedCount ? StandardScanOutcome.Partial : StandardScanOutcome.Completed;
        var partialReason = recordsToScan < derivedCount ? "MaximumRecords" : null;
        if (recordsToScan < derivedCount)
        {
            diagnostics.Add("SCAN_RECORD_BUDGET_REACHED", ScanDiagnosticSeverity.Warning, "mft-scan", "The record budget was reached; results are partial.");
        }

        var parsedRecords = new Dictionary<long, ParsedFileRecord>();
        var recordBuffer = new byte[validGeometry.FileRecordSize];
        for (var recordNumber = 0L; recordNumber < recordsToScan; recordNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var virtualOffset = checked(recordNumber * validGeometry.FileRecordSize);
            try
            {
                await bootstrap.Stream.ReadExactlyAsync(virtualOffset, recordBuffer, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ScanBudgetExceededException)
            {
                outcome = StandardScanOutcome.Partial;
                partialReason = "MaximumBytesRead";
                diagnostics.Add("SCAN_BYTE_BUDGET_REACHED", ScanDiagnosticSeverity.Warning, "mft-scan", "The byte-read budget was reached; results are partial.", null, recordNumber);
                break;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException or ArgumentOutOfRangeException)
            {
                diagnostics.Add("NTFS_RECORD_READ_FAILED", ScanDiagnosticSeverity.Warning, "mft-record", "A virtual MFT record could not be read exactly.", bootstrap.Stream.TryGetImageOffset(virtualOffset), recordNumber);
                outcome = StandardScanOutcome.Partial;
                partialReason = "MftReadFailure";
                break;
            }

            var parsed = NtfsFileRecordParser.Parse(recordBuffer, recordNumber, bootstrap.Stream.TryGetImageOffset(virtualOffset), validGeometry.BytesPerSector, budgets);
            foreach (var diagnostic in parsed.Diagnostics) diagnostics.Add(diagnostic);
            if (parsed.Record is not null) parsedRecords[recordNumber] = parsed.Record;
            recordsProcessed++;
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(recordsProcessed, recordsToScan, readBudget.BytesRead, 0, "Traversing NTFS MFT extents"));
            cancellationToken.ThrowIfCancellationRequested();
        }

        var listLoader = new NtfsAttributeListLoader(source, readBudget, request.Volume.VolumeOffset, volumeEnd, validGeometry.ClusterSize, budgets, diagnostics);
        IReadOnlyDictionary<long, ResolvedFileRecord> resolved;
        try
        {
            resolved = await new NtfsRecordResolver(listLoader, budgets, diagnostics, derivedCount).ResolveAsync(parsedRecords, cancellationToken).ConfigureAwait(false);
        }
        catch (ScanBudgetExceededException)
        {
            outcome = StandardScanOutcome.Partial;
            partialReason = "MaximumBytesRead";
            diagnostics.Add("SCAN_BYTE_BUDGET_REACHED", ScanDiagnosticSeverity.Warning, "extension-resolution", "The byte-read budget was reached during extension resolution.");
            resolved = parsedRecords.Values.Where(record => record.BaseRecordNumber is null).ToDictionary(
                record => record.RecordNumber,
                record => new ResolvedFileRecord
                {
                    Base = record,
                    FileNames = record.FileNames,
                    DataStreams = record.DataAttributes.GroupBy(attribute => attribute.Name, StringComparer.Ordinal).Select(group => NtfsStreamMerger.Merge(group.Key, group.ToArray(), diagnostics, record.RecordNumber)).ToArray(),
                    HasDamagedMetadata = true,
                });
        }

        var bitmap = CreateBitmap(source, readBudget, request.Volume.VolumeOffset, volumeEnd, validGeometry, resolved, budgets, diagnostics);
        foreach (var logicalRecord in resolved.Values.Where(record => !record.Base.IsInUse).OrderBy(record => record.Base.RecordNumber))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var validatedStreams = ValidateCandidateStreams(source, readBudget, request.Volume.VolumeOffset, volumeEnd, validGeometry, logicalRecord, budgets, diagnostics);
            var primaryName = logicalRecord.PrimaryFileName;
            var links = new List<NtfsFileLink>();
            var remainingPathCharacters = budgets.MaximumPathCharacters;
            foreach (var fileName in logicalRecord.FileNames.Take(budgets.MaximumAlternatePaths))
            {
                var path = NtfsPathBuilder.Build(logicalRecord.Base.RecordNumber, fileName, resolved, budgets.MaximumPathDepth, budgets.MaximumPathCharacters, cancellationToken);
                if (path.Path.Length > remainingPathCharacters)
                {
                    diagnostics.Add("NTFS_PATH_CHARACTER_LIMIT_REACHED", ScanDiagnosticSeverity.Warning, "path-reconstruction", "The per-candidate total path-character budget was reached.", null, logicalRecord.Base.RecordNumber);
                    break;
                }

                remainingPathCharacters -= path.Path.Length;
                links.Add(new(fileName.Name, path.Path, path.State, fileName.ParentRecordNumber, fileName.ParentSequenceNumber, fileName.Namespace));
            }

            var primaryLink = primaryName is null
                ? null
                : links.FirstOrDefault(link => link.Name == primaryName.Name && link.ParentRecordNumber == primaryName.ParentRecordNumber && link.Namespace == primaryName.Namespace);
            var streams = new List<NtfsDataStream>();
            CandidateRecoverability recoverability;
            string note;
            var unnamed = validatedStreams.FirstOrDefault(stream => string.IsNullOrEmpty(stream.Name));
            (recoverability, note) = await AssessRecoverabilityAsync(logicalRecord, unnamed, bitmap, diagnostics, cancellationToken).ConfigureAwait(false);
            foreach (var stream in validatedStreams)
            {
                var allocation = NtfsAllocationState.Unknown;
                if (ReferenceEquals(stream, unnamed) && bitmap is not null && stream.Storage == NtfsDataStorage.NonResident && stream.MetadataIsComplete &&
                    !stream.IsSparse && !stream.IsCompressed && !stream.IsEncrypted && stream.LogicalSize > 0)
                {
                    allocation = await bitmap.QueryRunsAsync(stream.Runs, logicalRecord.Base.RecordNumber, cancellationToken).ConfigureAwait(false);
                }

                streams.Add(stream.ToDomain(allocation));
            }

            var logicalSize = unnamed?.LogicalSize ?? primaryName?.LogicalSize ?? 0;
            var allocatedSize = unnamed?.AllocatedSize ?? primaryName?.AllocatedSize ?? 0;
            candidates.Add(new(
                logicalRecord.Base.RecordNumber,
                logicalRecord.Base.SequenceNumber,
                logicalRecord.Base.IsDirectory,
                primaryName?.Name ?? $"$MFT-{logicalRecord.Base.RecordNumber}",
                primaryLink?.Path ?? $"[unresolved]\\$MFT-{logicalRecord.Base.RecordNumber}",
                primaryLink?.PathState ?? CandidatePathState.Invalid,
                primaryName?.ParentRecordNumber,
                primaryName?.ParentSequenceNumber,
                Math.Max(0, logicalSize),
                Math.Max(0, allocatedSize),
                logicalRecord.Base.StandardFileAttributes != 0 ? logicalRecord.Base.StandardFileAttributes : primaryName?.FileAttributes ?? 0,
                logicalRecord.Base.ModifiedAt ?? primaryName?.ModifiedAt,
                streams,
                links,
                recoverability,
                note));
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new(recordsProcessed, recordsToScan, readBudget.BytesRead, candidates.Count, outcome == StandardScanOutcome.Completed ? "NTFS metadata scan complete" : "NTFS metadata scan partial"));
        cancellationToken.ThrowIfCancellationRequested();
        return Result(outcome, validGeometry, bootstrap.Layout, candidates, diagnostics, recordsProcessed, readBudget.BytesRead, partialReason);
    }

    internal async Task<NtfsRecoveryRecord> ResolveRecoveryRecordAsync(
        IReadOnlyRandomAccessSource source,
        StandardScanRequest request,
        long targetRecordNumber,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        var budgets = request.EffectiveBudgets;
        budgets.Validate();
        var diagnostics = new DiagnosticCollector(budgets.MaximumDiagnostics);
        var readBudget = new ScanReadBudget(budgets.MaximumBytesRead);
        var bootSector = new byte[BootSectorReadSize];
        await readBudget.ReadExactlyAsync(source, request.Volume.VolumeOffset, bootSector, cancellationToken).ConfigureAwait(false);
        if (!NtfsBootSectorParser.TryParse(bootSector, out var geometry, out var code, out var reason))
        {
            throw new NtfsBootstrapException(code, reason);
        }

        var validGeometry = geometry!;
        if (!TryCalculateVolume(source.Length, request.Volume.VolumeOffset, validGeometry, out var volumeEnd, out code, out reason))
        {
            throw new NtfsBootstrapException(code, reason);
        }

        var bootstrap = await BootstrapMftAsync(
            source,
            readBudget,
            request.Volume.VolumeOffset,
            volumeEnd,
            validGeometry,
            budgets,
            diagnostics,
            cancellationToken).ConfigureAwait(false);
        if (bootstrap.Layout.DerivedRecordCount > budgets.MaximumRecords)
        {
            throw new InvalidDataException("The trusted scan record budget no longer covers the complete MFT.");
        }

        if (targetRecordNumber < 0 || targetRecordNumber >= bootstrap.Layout.DerivedRecordCount)
        {
            throw new InvalidDataException("The selected record is outside the freshly derived MFT range.");
        }

        var records = new Dictionary<long, ParsedFileRecord>();
        var recordBuffer = new byte[validGeometry.FileRecordSize];
        for (var recordNumber = 0L; recordNumber < bootstrap.Layout.DerivedRecordCount; recordNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var virtualOffset = checked(recordNumber * validGeometry.FileRecordSize);
            await bootstrap.Stream.ReadExactlyAsync(virtualOffset, recordBuffer, cancellationToken).ConfigureAwait(false);
            var parsed = NtfsFileRecordParser.Parse(
                recordBuffer,
                recordNumber,
                bootstrap.Stream.TryGetImageOffset(virtualOffset),
                validGeometry.BytesPerSector,
                budgets);
            foreach (var diagnostic in parsed.Diagnostics)
            {
                diagnostics.Add(diagnostic);
            }

            if (parsed.Record is not null)
            {
                records[recordNumber] = parsed.Record;
            }
        }

        var loader = new NtfsAttributeListLoader(
            source,
            readBudget,
            request.Volume.VolumeOffset,
            volumeEnd,
            validGeometry.ClusterSize,
            budgets,
            diagnostics);
        var resolved = await new NtfsRecordResolver(loader, budgets, diagnostics, bootstrap.Layout.DerivedRecordCount)
            .ResolveAsync(records, cancellationToken)
            .ConfigureAwait(false);
        if (!resolved.TryGetValue(targetRecordNumber, out var target))
        {
            throw new InvalidDataException("The selected MFT record could not be resolved from current source metadata.");
        }

        var streams = ValidateCandidateStreams(
            source,
            readBudget,
            request.Volume.VolumeOffset,
            volumeEnd,
            validGeometry,
            target,
            budgets,
            diagnostics);
        return new(target, streams, validGeometry, volumeEnd, diagnostics.Items);
    }

    private static IReadOnlyList<MergedAttributeStream> ValidateCandidateStreams(
        IReadOnlyRandomAccessSource source,
        ScanReadBudget readBudget,
        long volumeOffset,
        long volumeEnd,
        NtfsBootGeometry geometry,
        ResolvedFileRecord record,
        StandardScanBudgets budgets,
        DiagnosticCollector diagnostics)
    {
        var validated = new List<MergedAttributeStream>(record.DataStreams.Count);
        foreach (var stream in record.DataStreams)
        {
            if (stream.Storage != NtfsDataStorage.NonResident || !stream.MetadataIsComplete)
            {
                validated.Add(stream);
                continue;
            }

            if (!NtfsVirtualStream.TryCreate(source, readBudget, stream.Runs, stream.LogicalSize, volumeOffset, volumeEnd, geometry.ClusterSize, budgets.MaximumDataRuns, true, out _, out var code, out var reason))
            {
                diagnostics.Add(code, ScanDiagnosticSeverity.Warning, "candidate-stream", reason, null, record.Base.RecordNumber);
                validated.Add(stream with { Storage = NtfsDataStorage.Unknown, MetadataIsComplete = false, Runs = [] });
            }
            else
            {
                validated.Add(stream);
            }
        }

        return validated;
    }

    private static async Task<(CandidateRecoverability State, string Note)> AssessRecoverabilityAsync(
        ResolvedFileRecord record,
        MergedAttributeStream? unnamed,
        NtfsAllocationBitmap? bitmap,
        DiagnosticCollector diagnostics,
        CancellationToken cancellationToken)
    {
        const string estimate = "Allocation status is an estimate and cannot prove content integrity or recovery success.";
        if (record.Base.IsDirectory) return (CandidateRecoverability.MetadataOnly, "Deleted directory metadata; no file-content recoverability rating is assigned.");
        if (record.HasDamagedMetadata) return (CandidateRecoverability.DamagedMetadata, "The logical file record is incomplete or damaged.");
        if (unnamed is null) return (record.HasDamagedMetadata ? CandidateRecoverability.DamagedMetadata : CandidateRecoverability.MetadataOnly, "No valid unnamed $DATA stream is available.");
        if (!unnamed.MetadataIsComplete || unnamed.Storage == NtfsDataStorage.Unknown) return (CandidateRecoverability.DamagedMetadata, "Unnamed $DATA metadata is incomplete or damaged.");
        if (unnamed.LogicalSize == 0) return (CandidateRecoverability.ZeroLength, "A structurally valid zero-length unnamed $DATA stream was found.");
        if (unnamed.Storage == NtfsDataStorage.Resident) return (CandidateRecoverability.ResidentDataAvailable, "Unnamed data remains resident in the valid MFT record; this is not a recovery guarantee.");
        if (unnamed.IsCompressed || unnamed.IsEncrypted || unnamed.IsSparse)
        {
            if (unnamed.IsCompressed) diagnostics.Add("NTFS_COMPRESSED_STREAM_UNSUPPORTED", ScanDiagnosticSeverity.Warning, "recoverability", "Compressed data is not allocation-rated.", null, record.Base.RecordNumber);
            if (unnamed.IsEncrypted) diagnostics.Add("NTFS_ENCRYPTED_STREAM_UNSUPPORTED", ScanDiagnosticSeverity.Warning, "recoverability", "Encrypted data is not allocation-rated.", null, record.Base.RecordNumber);
            if (unnamed.IsSparse) diagnostics.Add("NTFS_SPARSE_STREAM_UNSUPPORTED", ScanDiagnosticSeverity.Warning, "recoverability", "Sparse deleted-file semantics are not allocation-rated.", null, record.Base.RecordNumber);
            return (CandidateRecoverability.UnsupportedLayout, estimate);
        }

        if (bitmap is null) return (CandidateRecoverability.Unknown, $"$Bitmap metadata is unavailable. {estimate}");
        var allocation = await bitmap.QueryRunsAsync(unnamed.Runs, record.Base.RecordNumber, cancellationToken).ConfigureAwait(false);
        return allocation switch
        {
            NtfsAllocationState.EntirelyFree => (CandidateRecoverability.PossiblyRecoverable, estimate),
            NtfsAllocationState.Mixed => (CandidateRecoverability.PartiallyOverwritten, estimate),
            NtfsAllocationState.EntirelyAllocated => (CandidateRecoverability.Overwritten, estimate),
            _ => (CandidateRecoverability.Unknown, estimate),
        };
    }

    private static NtfsAllocationBitmap? CreateBitmap(
        IReadOnlyRandomAccessSource source,
        ScanReadBudget readBudget,
        long volumeOffset,
        long volumeEnd,
        NtfsBootGeometry geometry,
        IReadOnlyDictionary<long, ResolvedFileRecord> records,
        StandardScanBudgets budgets,
        DiagnosticCollector diagnostics)
    {
        var bitmapRecord = records.Values
            .Where(record => record.Base.IsInUse && record.FileNames.Any(name => string.Equals(name.Name, "$Bitmap", StringComparison.Ordinal)))
            .OrderBy(record => record.Base.RecordNumber)
            .FirstOrDefault();
        var streamMetadata = bitmapRecord?.DataStreams.FirstOrDefault(stream => string.IsNullOrEmpty(stream.Name));
        if (streamMetadata is null || streamMetadata.Storage != NtfsDataStorage.NonResident || !streamMetadata.MetadataIsComplete ||
            streamMetadata.IsSparse || streamMetadata.IsCompressed || streamMetadata.IsEncrypted)
        {
            diagnostics.Add("NTFS_BITMAP_UNAVAILABLE", ScanDiagnosticSeverity.Warning, "bitmap-bootstrap", "$Bitmap metadata is missing, damaged, or unsupported.");
            return null;
        }

        if (!NtfsVirtualStream.TryCreate(source, readBudget, streamMetadata.Runs, streamMetadata.LogicalSize, volumeOffset, volumeEnd, geometry.ClusterSize, budgets.MaximumDataRuns, false, out var stream, out var code, out var reason) || stream!.CoveredLength < stream.Length)
        {
            diagnostics.Add(code, ScanDiagnosticSeverity.Warning, "bitmap-bootstrap", reason, null, bitmapRecord!.Base.RecordNumber);
            diagnostics.Add("NTFS_BITMAP_UNAVAILABLE", ScanDiagnosticSeverity.Warning, "bitmap-bootstrap", "$Bitmap cannot be read through validated extents.", null, bitmapRecord.Base.RecordNumber);
            return null;
        }

        return new(stream, budgets.MaximumBitmapCacheBytes, diagnostics);
    }

    private static async Task<MftBootstrap> BootstrapMftAsync(
        IReadOnlyRandomAccessSource source,
        ScanReadBudget readBudget,
        long volumeOffset,
        long volumeEnd,
        NtfsBootGeometry geometry,
        StandardScanBudgets budgets,
        DiagnosticCollector diagnostics,
        CancellationToken cancellationToken)
    {
        var primary = await ReadBootstrapRecordAsync(source, readBudget, volumeOffset, volumeEnd, geometry, geometry.MftLogicalClusterNumber, budgets, cancellationToken).ConfigureAwait(false);
        var mirror = await ReadBootstrapRecordAsync(source, readBudget, volumeOffset, volumeEnd, geometry, geometry.MftMirrorLogicalClusterNumber, budgets, cancellationToken).ConfigureAwait(false);
        var primaryValid = IsBootstrapRecord(primary.Record);
        var mirrorValid = IsBootstrapRecord(mirror.Record);
        if (!primaryValid) diagnostics.Add("NTFS_MFT_BOOTSTRAP_INVALID", ScanDiagnosticSeverity.Warning, "mft-bootstrap", "The primary MFT bootstrap record is structurally unusable.", primary.ImageOffset, 0);
        if (!mirrorValid) diagnostics.Add("NTFS_MFT_MIRROR_INVALID", ScanDiagnosticSeverity.Warning, "mft-mirror", "The mirrored MFT bootstrap record is structurally unusable.", mirror.ImageOffset, 0);
        if (!primaryValid && !mirrorValid) throw new NtfsBootstrapException("NTFS_MFT_BOOTSTRAP_UNAVAILABLE", "Both primary and mirrored MFT bootstrap records are invalid.");

        if (primaryValid && mirrorValid && !BootstrapRecordsAgree(primary.Record!, mirror.Record!))
        {
            diagnostics.Add("NTFS_MFT_MIRROR_CONFLICT", ScanDiagnosticSeverity.Warning, "mft-mirror", "Primary and mirrored MFT bootstrap metadata disagree; the valid primary is preferred.", mirror.ImageOffset, 0);
        }

        var sourceKind = primaryValid ? NtfsBootstrapSource.PrimaryMft : NtfsBootstrapSource.MftMirror;
        var bootstrapRecord = primaryValid ? primary.Record! : mirror.Record!;
        var listLoader = new NtfsAttributeListLoader(source, readBudget, volumeOffset, volumeEnd, geometry.ClusterSize, budgets, diagnostics);
        var records = new List<ParsedFileRecord> { bootstrapRecord };
        var pending = new Queue<ParsedFileRecord>();
        pending.Enqueue(bootstrapRecord);
        var visited = new HashSet<long> { 0 };
        while (pending.TryDequeue(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = await listLoader.LoadAsync(current, cancellationToken).ConfigureAwait(false);
            foreach (var entry in entries.Where(entry => entry.RecordNumber != 0).OrderBy(entry => entry.LowestVcn).ThenBy(entry => entry.RecordNumber))
            {
                if (!visited.Add(entry.RecordNumber)) continue;
                if (visited.Count - 1 > budgets.MaximumExtensionRecords) throw new NtfsBootstrapException("NTFS_EXTENSION_LIMIT_REACHED", "The MFT bootstrap extension-record budget was reached.");
                var currentStream = MergeMftData(records, diagnostics);
                var virtualStream = CreateMftVirtualStream(source, readBudget, volumeOffset, volumeEnd, geometry, budgets, currentStream);
                var recordOffset = checked(entry.RecordNumber * geometry.FileRecordSize);
                if (recordOffset < 0 || recordOffset > virtualStream.CoveredLength - geometry.FileRecordSize)
                {
                    throw new NtfsBootstrapException("NTFS_EXTENSION_RECORD_MISSING", "An MFT attribute-list extension record is outside currently resolved extents.");
                }

                var buffer = new byte[geometry.FileRecordSize];
                await virtualStream.ReadExactlyAsync(recordOffset, buffer, cancellationToken).ConfigureAwait(false);
                var parsed = NtfsFileRecordParser.Parse(buffer, entry.RecordNumber, virtualStream.TryGetImageOffset(recordOffset), geometry.BytesPerSector, budgets);
                foreach (var diagnostic in parsed.Diagnostics) diagnostics.Add(diagnostic);
                if (parsed.Record is null) throw new NtfsBootstrapException("NTFS_EXTENSION_RECORD_MISSING", "An MFT extension record is structurally invalid.");
                if (entry.SequenceNumber > 0 && entry.SequenceNumber != parsed.Record.SequenceNumber) throw new NtfsBootstrapException("NTFS_EXTENSION_SEQUENCE_STALE", "An MFT extension-record sequence reference is stale.");
                if (parsed.Record.BaseRecordNumber != 0 || (parsed.Record.BaseRecordSequence > 0 && parsed.Record.BaseRecordSequence != bootstrapRecord.SequenceNumber)) throw new NtfsBootstrapException("NTFS_EXTENSION_BASE_CONFLICT", "An MFT extension record does not reference record zero.");
                records.Add(parsed.Record);
                pending.Enqueue(parsed.Record);
            }
        }

        var merged = MergeMftData(records, diagnostics);
        var stream = CreateMftVirtualStream(source, readBudget, volumeOffset, volumeEnd, geometry, budgets, merged);
        if (stream.CoveredLength < merged.LogicalSize) throw new NtfsBootstrapException("NTFS_MFT_EXTENTS_INCOMPLETE", "Resolved MFT extents do not cover the declared logical size.");
        if (merged.LogicalSize <= 0) throw new NtfsBootstrapException("NTFS_MFT_SIZE_INVALID", "The MFT logical size is invalid.");
        var count = merged.LogicalSize / geometry.FileRecordSize;
        if (count <= 0) throw new NtfsBootstrapException("NTFS_MFT_SIZE_INVALID", "The MFT contains no complete file records.");
        if (merged.LogicalSize % geometry.FileRecordSize != 0) diagnostics.Add("NTFS_MFT_SIZE_NOT_ALIGNED", ScanDiagnosticSeverity.Warning, "mft-bootstrap", "The MFT logical size is not a whole number of file records; the trailing partial record is ignored.");
        var layout = new NtfsMftLayout(merged.LogicalSize, merged.AllocatedSize, merged.InitializedSize, count, stream.ExtentCount, sourceKind);
        return new(stream, layout);
    }

    private static MergedAttributeStream MergeMftData(IReadOnlyList<ParsedFileRecord> records, DiagnosticCollector diagnostics)
    {
        var attributes = records.SelectMany(record => record.DataAttributes).Where(attribute => string.IsNullOrEmpty(attribute.Name)).ToArray();
        var merged = NtfsStreamMerger.Merge(null, attributes, diagnostics, 0);
        if (merged.Storage != NtfsDataStorage.NonResident || !merged.MetadataIsComplete || merged.IsSparse || merged.IsCompressed || merged.IsEncrypted)
        {
            throw new NtfsBootstrapException(merged.IsSparse ? "NTFS_MFT_SPARSE_EXTENT" : "NTFS_MFT_DATA_INVALID", "The unnamed non-resident MFT data stream is missing, damaged, sparse, compressed, or encrypted.");
        }

        return merged;
    }

    private static NtfsVirtualStream CreateMftVirtualStream(IReadOnlyRandomAccessSource source, ScanReadBudget readBudget, long volumeOffset, long volumeEnd, NtfsBootGeometry geometry, StandardScanBudgets budgets, MergedAttributeStream merged)
    {
        if (!NtfsVirtualStream.TryCreate(source, readBudget, merged.Runs, merged.LogicalSize, volumeOffset, volumeEnd, geometry.ClusterSize, budgets.MaximumMftExtents, false, out var stream, out var code, out var reason, "NTFS_MFT_SPARSE_EXTENT"))
        {
            throw new NtfsBootstrapException(code, reason);
        }

        return stream!;
    }

    private static async Task<BootstrapRecordRead> ReadBootstrapRecordAsync(IReadOnlyRandomAccessSource source, ScanReadBudget readBudget, long volumeOffset, long volumeEnd, NtfsBootGeometry geometry, ulong lcn, StandardScanBudgets budgets, CancellationToken cancellationToken)
    {
        long offset;
        try
        {
            offset = checked(volumeOffset + checked((long)lcn * geometry.ClusterSize));
        }
        catch (OverflowException)
        {
            return new(null, null);
        }

        if (offset < volumeOffset || offset > volumeEnd - geometry.FileRecordSize || offset > source.Length - geometry.FileRecordSize) return new(null, offset);
        var bytes = new byte[geometry.FileRecordSize];
        try
        {
            await readBudget.ReadExactlyAsync(source, offset, bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException && exception is not ScanBudgetExceededException)
        {
            return new(null, offset);
        }

        return new(NtfsFileRecordParser.Parse(bytes, 0, offset, geometry.BytesPerSector, budgets).Record, offset);
    }

    private static bool IsBootstrapRecord(ParsedFileRecord? record) =>
        record is { RecordNumber: 0, BaseRecordNumber: null } && record.DataAttributes.Any(attribute => string.IsNullOrEmpty(attribute.Name) && attribute.IsNonResident && attribute.MetadataIsComplete);

    private static bool BootstrapRecordsAgree(ParsedFileRecord primary, ParsedFileRecord mirror)
    {
        var left = primary.DataAttributes.First(attribute => string.IsNullOrEmpty(attribute.Name));
        var right = mirror.DataAttributes.First(attribute => string.IsNullOrEmpty(attribute.Name));
        return left.LogicalSize == right.LogicalSize && left.AllocatedSize == right.AllocatedSize && left.InitializedSize == right.InitializedSize && left.Flags == right.Flags && left.Runs.SequenceEqual(right.Runs);
    }

    private static bool TryCalculateVolume(long sourceLength, long volumeOffset, NtfsBootGeometry geometry, out long volumeEnd, out string code, out string reason)
    {
        volumeEnd = 0;
        code = "NTFS_GEOMETRY_OVERFLOW";
        reason = "NTFS volume geometry overflowed bounded arithmetic.";
        try
        {
            volumeEnd = checked(volumeOffset + checked((long)geometry.TotalSectors * geometry.BytesPerSector));
            if (volumeEnd > sourceLength)
            {
                code = "NTFS_VOLUME_TRUNCATED";
                reason = "The image is shorter than the volume size declared by the NTFS boot sector.";
                return false;
            }

            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static StandardScanResult Result(StandardScanOutcome outcome, NtfsBootGeometry? geometry, NtfsMftLayout? layout, IReadOnlyList<DeletedFileCandidate> candidates, DiagnosticCollector diagnostics, long recordsProcessed, long bytesRead, string? partialReason = null) =>
        new(outcome, geometry, layout, candidates, diagnostics.Items, recordsProcessed, bytesRead, diagnostics.WasTruncated, partialReason);

    private sealed record BootstrapRecordRead(ParsedFileRecord? Record, long? ImageOffset);
    private sealed record MftBootstrap(NtfsVirtualStream Stream, NtfsMftLayout Layout);
}

internal sealed record NtfsRecoveryRecord(
    ResolvedFileRecord Record,
    IReadOnlyList<MergedAttributeStream> Streams,
    NtfsBootGeometry Geometry,
    long VolumeEnd,
    IReadOnlyList<ScanDiagnostic> Diagnostics);

internal sealed class NtfsBootstrapException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

internal static class NtfsBootSectorParser
{
    public static bool TryParse(ReadOnlySpan<byte> sector, out NtfsBootGeometry? geometry, out string code, out string reason)
    {
        geometry = null;
        code = "NTFS_BOOT_INVALID";
        reason = "The NTFS boot sector is invalid.";
        if (sector.Length < 512) { code = "NTFS_BOOT_TRUNCATED"; reason = "The NTFS boot sector is truncated."; return false; }
        if (!sector.Slice(3, 8).SequenceEqual("NTFS    "u8)) { code = "NTFS_OEM_ID_INVALID"; reason = "The boot sector does not contain the NTFS OEM identifier."; return false; }
        if (sector[510] != 0x55 || sector[511] != 0xAA) { code = "NTFS_BOOT_SIGNATURE_INVALID"; reason = "The boot-sector signature is invalid."; return false; }
        var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(sector[11..]);
        var sectorsPerCluster = sector[13];
        if (bytesPerSector is < 512 or > 4096 || !IsPowerOfTwo(bytesPerSector) || sectorsPerCluster == 0 || !IsPowerOfTwo(sectorsPerCluster))
        { code = "NTFS_GEOMETRY_INVALID"; reason = "The sector or cluster geometry is invalid or implausible."; return false; }
        int clusterSize;
        try { clusterSize = checked(bytesPerSector * sectorsPerCluster); }
        catch (OverflowException) { code = "NTFS_GEOMETRY_OVERFLOW"; reason = "The cluster-size calculation overflowed."; return false; }
        if (clusterSize > 2 * 1024 * 1024) { code = "NTFS_GEOMETRY_INVALID"; reason = "The cluster size exceeds the scanner safety limit."; return false; }
        var totalSectors = BinaryPrimitives.ReadUInt64LittleEndian(sector[40..]);
        var mftLcn = BinaryPrimitives.ReadUInt64LittleEndian(sector[48..]);
        var mirrorLcn = BinaryPrimitives.ReadUInt64LittleEndian(sector[56..]);
        var clusterCount = totalSectors / sectorsPerCluster;
        if (totalSectors == 0 || clusterCount == 0 || mftLcn >= clusterCount || mirrorLcn >= clusterCount)
        { code = "NTFS_GEOMETRY_INVALID"; reason = "The declared sector count or MFT locations are outside plausible bounds."; return false; }
        var encoding = unchecked((sbyte)sector[64]);
        if (encoding == 0) { code = "NTFS_RECORD_SIZE_INVALID"; reason = "The file-record-size encoding is zero."; return false; }
        int recordSize;
        try
        {
            if (encoding > 0) recordSize = checked(encoding * clusterSize);
            else { var exponent = -encoding; if (exponent > 30) throw new OverflowException(); recordSize = checked(1 << exponent); }
        }
        catch (OverflowException) { code = "NTFS_RECORD_SIZE_OVERFLOW"; reason = "The file-record-size encoding overflowed bounded arithmetic."; return false; }
        if (recordSize < bytesPerSector || recordSize > 1024 * 1024 || recordSize % bytesPerSector != 0)
        { code = "NTFS_RECORD_SIZE_INVALID"; reason = "The decoded file-record size is invalid or exceeds the safety limit."; return false; }
        geometry = new(bytesPerSector, sectorsPerCluster, clusterSize, totalSectors, mftLcn, mirrorLcn, encoding, recordSize);
        return true;
    }

    private static bool IsPowerOfTwo(int value) => (value & (value - 1)) == 0;
}

internal sealed class DiagnosticCollector(int maximum)
{
    private readonly List<ScanDiagnostic> _items = [];
    public IReadOnlyList<ScanDiagnostic> Items => _items;
    public bool WasTruncated { get; private set; }
    public void Add(ScanDiagnostic diagnostic) { if (_items.Count < maximum) _items.Add(diagnostic); else WasTruncated = true; }
    public void Add(string code, ScanDiagnosticSeverity severity, string operation, string reason, long? offset = null, long? record = null) => Add(new(code, severity, operation, reason, offset, record));
}
