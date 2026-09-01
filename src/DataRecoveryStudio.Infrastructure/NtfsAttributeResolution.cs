using System.Buffers.Binary;
using System.Text;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

internal sealed record NtfsAttributeListEntry(
    uint Type,
    string? Name,
    long LowestVcn,
    long RecordNumber,
    ushort SequenceNumber,
    ushort AttributeId);

internal static class NtfsAttributeListParser
{
    public static IReadOnlyList<NtfsAttributeListEntry> Parse(
        ReadOnlySpan<byte> bytes,
        StandardScanBudgets budgets,
        DiagnosticCollector diagnostics,
        long ownerRecordNumber)
    {
        var entries = new List<NtfsAttributeListEntry>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            if (bytes[offset..].IndexOfAnyExcept((byte)0) < 0)
            {
                break;
            }

            if (bytes.Length - offset < 26)
            {
                diagnostics.Add("NTFS_ATTRIBUTE_LIST_ENTRY_INVALID", ScanDiagnosticSeverity.Warning, "attribute-list", "An attribute-list entry is truncated.", null, ownerRecordNumber);
                break;
            }

            if (entries.Count >= budgets.MaximumAttributeListEntries)
            {
                diagnostics.Add("NTFS_ATTRIBUTE_LIST_LIMIT_REACHED", ScanDiagnosticSeverity.Warning, "attribute-list", "The attribute-list entry budget was reached.", null, ownerRecordNumber);
                break;
            }

            var entry = bytes[offset..];
            var type = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(entry[4..]);
            var nameLength = entry[6];
            var nameOffset = entry[7];
            if (length == 0)
            {
                diagnostics.Add("NTFS_ATTRIBUTE_LIST_ENTRY_ZERO", ScanDiagnosticSeverity.Warning, "attribute-list", "A zero-length attribute-list entry was rejected.", null, ownerRecordNumber);
                break;
            }

            if (length < 26 || length > bytes.Length - offset)
            {
                diagnostics.Add("NTFS_ATTRIBUTE_LIST_ENTRY_INVALID", ScanDiagnosticSeverity.Warning, "attribute-list", "An attribute-list entry exceeds its bounded value.", null, ownerRecordNumber);
                break;
            }

            var nameBytes = checked(nameLength * 2);
            if (nameLength > budgets.MaximumFilenameLength || (nameLength > 0 && (nameOffset < 26 || nameOffset > length - nameBytes)))
            {
                diagnostics.Add("NTFS_ATTRIBUTE_LIST_ENTRY_INVALID", ScanDiagnosticSeverity.Warning, "attribute-list", "An attribute-list entry name is invalid.", null, ownerRecordNumber);
                offset = checked(offset + length);
                continue;
            }

            var fileReference = BinaryPrimitives.ReadUInt64LittleEndian(entry[16..]);
            var recordNumber = checked((long)(fileReference & 0x0000FFFFFFFFFFFFUL));
            var lowestVcnValue = BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]);
            if (lowestVcnValue > long.MaxValue)
            {
                diagnostics.Add("NTFS_ATTRIBUTE_LIST_ENTRY_INVALID", ScanDiagnosticSeverity.Warning, "attribute-list", "An attribute-list VCN exceeds the normalized range.", null, ownerRecordNumber);
                offset = checked(offset + length);
                continue;
            }

            string? name = null;
            if (nameLength > 0)
            {
                name = Encoding.Unicode.GetString(entry.Slice(nameOffset, nameBytes));
                if (name.Contains('\uFFFD'))
                {
                    diagnostics.Add("NTFS_ATTRIBUTE_LIST_ENTRY_INVALID", ScanDiagnosticSeverity.Warning, "attribute-list", "An attribute-list name is not valid UTF-16.", null, ownerRecordNumber);
                    offset = checked(offset + length);
                    continue;
                }
            }

            entries.Add(new(type, name, (long)lowestVcnValue, recordNumber, (ushort)(fileReference >> 48), BinaryPrimitives.ReadUInt16LittleEndian(entry[24..])));
            offset = checked(offset + length);
        }

        return entries;
    }
}

internal sealed record MergedAttributeStream(
    string? Name,
    NtfsDataStorage Storage,
    long LogicalSize,
    long AllocatedSize,
    long InitializedSize,
    IReadOnlyList<NtfsDataRun> Runs,
    bool MetadataIsComplete,
    bool IsSparse,
    bool IsCompressed,
    bool IsEncrypted,
    byte[]? ResidentValue,
    string AttributeIdentity)
{
    public NtfsDataStream ToDomain(NtfsAllocationState allocation = NtfsAllocationState.Unknown) =>
        new(Name, Storage, LogicalSize, AllocatedSize, InitializedSize, Runs, MetadataIsComplete, IsSparse, IsCompressed, IsEncrypted, allocation, AttributeIdentity);
}

internal static class NtfsStreamMerger
{
    public static MergedAttributeStream Merge(
        string? name,
        IReadOnlyList<ParsedStreamAttribute> attributes,
        DiagnosticCollector diagnostics,
        long ownerRecordNumber)
    {
        if (attributes.Count == 0)
        {
            return new(name, NtfsDataStorage.None, 0, 0, 0, [], true, false, false, false, null, string.Empty);
        }

        if (attributes.Any(attribute => !attribute.MetadataIsComplete))
        {
            var damagedAttribute = attributes[0];
            return new(name, NtfsDataStorage.Unknown, damagedAttribute.LogicalSize, damagedAttribute.AllocatedSize, damagedAttribute.InitializedSize, [], false, damagedAttribute.IsSparse, damagedAttribute.IsCompressed, damagedAttribute.IsEncrypted, null, Identity(attributes));
        }

        var resident = attributes.Where(attribute => !attribute.IsNonResident).ToArray();
        var nonResident = attributes.Where(attribute => attribute.IsNonResident).OrderBy(attribute => attribute.LowestVcn).ToArray();
        if (resident.Length > 0)
        {
            if (resident.Length != 1 || nonResident.Length != 0)
            {
                diagnostics.Add("NTFS_ATTRIBUTE_EXTENT_CONFLICT", ScanDiagnosticSeverity.Warning, "attribute-merge", "Resident and non-resident forms conflict for one logical stream.", null, ownerRecordNumber);
                return new(name, NtfsDataStorage.Unknown, resident[0].LogicalSize, resident[0].AllocatedSize, resident[0].InitializedSize, [], false, false, false, false, null, Identity(attributes));
            }

            var value = resident[0];
            return new(name, NtfsDataStorage.Resident, value.LogicalSize, value.AllocatedSize, value.InitializedSize, [], true, false, false, false, value.ResidentValue, Identity(attributes));
        }

        var first = nonResident[0];
        var expectedVcn = 0L;
        var runs = new List<NtfsDataRun>();
        var complete = true;
        foreach (var extent in nonResident)
        {
            if (extent.LowestVcn != expectedVcn)
            {
                diagnostics.Add(extent.LowestVcn < expectedVcn ? "NTFS_VCN_EXTENT_OVERLAP" : "NTFS_VCN_EXTENT_GAP", ScanDiagnosticSeverity.Warning, "attribute-merge", extent.LowestVcn < expectedVcn ? "Attribute VCN extents overlap." : "Attribute VCN extents contain an unexplained gap.", null, ownerRecordNumber);
                complete = false;
                break;
            }

            if (extent.CompressionUnit != first.CompressionUnit || extent.Flags != first.Flags ||
                (extent.LowestVcn > 0 && extent.LogicalSize != 0 && extent.LogicalSize != first.LogicalSize))
            {
                diagnostics.Add("NTFS_ATTRIBUTE_EXTENT_CONFLICT", ScanDiagnosticSeverity.Warning, "attribute-merge", "Attribute extents contain conflicting flags, compression units, or sizes.", null, ownerRecordNumber);
                complete = false;
                break;
            }

            runs.AddRange(extent.Runs);
            try
            {
                expectedVcn = checked(extent.HighestVcn + 1);
            }
            catch (OverflowException)
            {
                complete = false;
                break;
            }
        }

        return new(
            name,
            complete ? NtfsDataStorage.NonResident : NtfsDataStorage.Unknown,
            first.LogicalSize,
            first.AllocatedSize,
            first.InitializedSize,
            complete ? runs : [],
            complete,
            nonResident.Any(attribute => attribute.IsSparse),
            nonResident.Any(attribute => attribute.IsCompressed),
            nonResident.Any(attribute => attribute.IsEncrypted),
            null,
            Identity(attributes));
    }

    private static string Identity(IEnumerable<ParsedStreamAttribute> attributes) => string.Join(
        ";",
        attributes
            .OrderBy(attribute => attribute.LowestVcn)
            .ThenBy(attribute => attribute.AttributeId)
            .Select(attribute => $"{attribute.AttributeId}:{attribute.LowestVcn}:{attribute.HighestVcn}"));
}

internal sealed class NtfsAttributeListLoader(
    IReadOnlyRandomAccessSource source,
    ScanReadBudget readBudget,
    long volumeOffset,
    long volumeEnd,
    int clusterSize,
    StandardScanBudgets budgets,
    DiagnosticCollector diagnostics)
{
    public async Task<IReadOnlyList<NtfsAttributeListEntry>> LoadAsync(ParsedFileRecord record, CancellationToken cancellationToken)
    {
        var entries = new List<NtfsAttributeListEntry>();
        foreach (var attribute in record.AttributeLists)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!attribute.MetadataIsComplete)
            {
                diagnostics.Add("NTFS_ATTRIBUTE_LIST_INVALID", ScanDiagnosticSeverity.Warning, "attribute-list", "Attribute-list metadata is damaged.", null, record.RecordNumber);
                continue;
            }

            byte[] bytes;
            if (!attribute.IsNonResident)
            {
                bytes = attribute.ResidentValue ?? [];
            }
            else
            {
                if (attribute.LogicalSize < 0 || attribute.LogicalSize > budgets.MaximumAttributeListBytes)
                {
                    diagnostics.Add("NTFS_ATTRIBUTE_LIST_LIMIT_REACHED", ScanDiagnosticSeverity.Warning, "attribute-list", "The non-resident attribute list exceeds its byte budget.", null, record.RecordNumber);
                    continue;
                }

                if (!NtfsVirtualStream.TryCreate(source, readBudget, attribute.Runs, attribute.LogicalSize, volumeOffset, volumeEnd, clusterSize, budgets.MaximumDataRuns, false, out var stream, out var code, out var reason))
                {
                    diagnostics.Add(code, ScanDiagnosticSeverity.Warning, "attribute-list", reason, null, record.RecordNumber);
                    continue;
                }

                bytes = new byte[checked((int)attribute.LogicalSize)];
                try
                {
                    await stream!.ReadExactlyAsync(0, bytes, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentOutOfRangeException)
                {
                    diagnostics.Add("NTFS_ATTRIBUTE_LIST_READ_FAILED", ScanDiagnosticSeverity.Warning, "attribute-list", "The non-resident attribute list could not be read exactly.", null, record.RecordNumber);
                    continue;
                }
            }

            entries.AddRange(NtfsAttributeListParser.Parse(bytes, budgets, diagnostics, record.RecordNumber));
            if (entries.Count >= budgets.MaximumAttributeListEntries)
            {
                if (entries.Count > budgets.MaximumAttributeListEntries) entries.RemoveRange(budgets.MaximumAttributeListEntries, entries.Count - budgets.MaximumAttributeListEntries);
                diagnostics.Add("NTFS_ATTRIBUTE_LIST_LIMIT_REACHED", ScanDiagnosticSeverity.Warning, "attribute-list", "The per-record attribute-list entry budget was reached.", null, record.RecordNumber);
                break;
            }
        }

        return entries;
    }
}

internal sealed class ResolvedFileRecord
{
    public required ParsedFileRecord Base { get; init; }
    public required IReadOnlyList<ParsedFileName> FileNames { get; init; }
    public required IReadOnlyList<MergedAttributeStream> DataStreams { get; init; }
    public required bool HasDamagedMetadata { get; init; }

    public ParsedFileName? PrimaryFileName => FileNames
        .OrderBy(name => NtfsFileRecordParser.NamespaceScore(name.Namespace))
        .ThenBy(name => name.Name, StringComparer.Ordinal)
        .ThenBy(name => name.ParentRecordNumber)
        .FirstOrDefault();
}

internal sealed class NtfsRecordResolver(
    NtfsAttributeListLoader attributeListLoader,
    StandardScanBudgets budgets,
    DiagnosticCollector diagnostics,
    long derivedRecordCount)
{
    public async Task<IReadOnlyDictionary<long, ResolvedFileRecord>> ResolveAsync(
        IReadOnlyDictionary<long, ParsedFileRecord> parsedRecords,
        CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<long, ResolvedFileRecord>();
        foreach (var baseRecord in parsedRecords.Values.Where(record => record.BaseRecordNumber is null).OrderBy(record => record.RecordNumber))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var records = new List<ParsedFileRecord> { baseRecord };
            var visited = new HashSet<long> { baseRecord.RecordNumber };
            var active = new HashSet<long> { baseRecord.RecordNumber };
            var damaged = baseRecord.HasDamagedMetadata;
            await VisitAsync(baseRecord, baseRecord, records, visited, active, 0, cancellationToken).ConfigureAwait(false);

            var names = NormalizeFileNames(records.SelectMany(record => record.FileNames), budgets.MaximumAlternatePaths);
            var streams = records
                .SelectMany(record => record.DataAttributes)
                .GroupBy(attribute => attribute.Name, StringComparer.Ordinal)
                .OrderBy(group => group.Key is null ? 0 : 1)
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => NtfsStreamMerger.Merge(group.Key, group.ToArray(), diagnostics, baseRecord.RecordNumber))
                .ToArray();
            damaged |= records.Any(record => record.HasDamagedMetadata) || streams.Any(stream => !stream.MetadataIsComplete);
            resolved[baseRecord.RecordNumber] = new() { Base = baseRecord, FileNames = names, DataStreams = streams, HasDamagedMetadata = damaged };

            async Task VisitAsync(
                ParsedFileRecord current,
                ParsedFileRecord owner,
                List<ParsedFileRecord> collected,
                HashSet<long> seen,
                HashSet<long> stack,
                int depth,
                CancellationToken token)
            {
                if (depth >= budgets.MaximumExtensionDepth)
                {
                    damaged = true;
                    diagnostics.Add("NTFS_ATTRIBUTE_LIST_LIMIT_REACHED", ScanDiagnosticSeverity.Warning, "extension-resolution", "The extension-resolution depth budget was reached.", null, owner.RecordNumber);
                    return;
                }

                var entries = await attributeListLoader.LoadAsync(current, token).ConfigureAwait(false);
                foreach (var entry in entries.OrderBy(entry => entry.RecordNumber).ThenBy(entry => entry.Type).ThenBy(entry => entry.LowestVcn))
                {
                    token.ThrowIfCancellationRequested();
                    if (entry.RecordNumber == owner.RecordNumber || entry.RecordNumber == current.RecordNumber)
                    {
                        continue;
                    }

                    if (entry.RecordNumber < 0 || entry.RecordNumber >= derivedRecordCount)
                    {
                        damaged = true;
                        diagnostics.Add("NTFS_ATTRIBUTE_LIST_ENTRY_INVALID", ScanDiagnosticSeverity.Warning, "extension-resolution", "An attribute-list reference is outside the derived MFT record range.", null, owner.RecordNumber);
                        continue;
                    }

                    if (stack.Contains(entry.RecordNumber))
                    {
                        damaged = true;
                        diagnostics.Add("NTFS_ATTRIBUTE_LIST_CYCLE", ScanDiagnosticSeverity.Warning, "extension-resolution", "A cyclic extension-record reference was detected.", null, owner.RecordNumber);
                        continue;
                    }

                    if (!seen.Add(entry.RecordNumber))
                    {
                        continue;
                    }

                    if (collected.Count - 1 >= budgets.MaximumExtensionRecords)
                    {
                        damaged = true;
                        diagnostics.Add("NTFS_EXTENSION_LIMIT_REACHED", ScanDiagnosticSeverity.Warning, "extension-resolution", "The extension-record budget was reached.", null, owner.RecordNumber);
                        return;
                    }

                    if (!parsedRecords.TryGetValue(entry.RecordNumber, out var extension))
                    {
                        damaged = true;
                        diagnostics.Add("NTFS_EXTENSION_RECORD_MISSING", ScanDiagnosticSeverity.Warning, "extension-resolution", "A referenced extension record is missing or damaged.", null, owner.RecordNumber);
                        continue;
                    }

                    if (entry.SequenceNumber > 0 && entry.SequenceNumber != extension.SequenceNumber)
                    {
                        damaged = true;
                        diagnostics.Add("NTFS_EXTENSION_SEQUENCE_STALE", ScanDiagnosticSeverity.Warning, "extension-resolution", "An extension-record sequence reference is stale.", null, owner.RecordNumber);
                        continue;
                    }

                    if (extension.BaseRecordNumber != owner.RecordNumber || (extension.BaseRecordSequence > 0 && extension.BaseRecordSequence != owner.SequenceNumber))
                    {
                        damaged = true;
                        diagnostics.Add("NTFS_EXTENSION_BASE_CONFLICT", ScanDiagnosticSeverity.Warning, "extension-resolution", "A referenced extension record does not belong to the base record.", null, owner.RecordNumber);
                        continue;
                    }

                    collected.Add(extension);
                    stack.Add(extension.RecordNumber);
                    await VisitAsync(extension, owner, collected, seen, stack, depth + 1, token).ConfigureAwait(false);
                    stack.Remove(extension.RecordNumber);
                }
            }
        }

        return resolved;
    }

    private static IReadOnlyList<ParsedFileName> NormalizeFileNames(IEnumerable<ParsedFileName> names, int maximum)
    {
        var distinct = names
            .DistinctBy(name => (name.Name, name.ParentRecordNumber, name.ParentSequenceNumber, name.Namespace))
            .ToArray();
        var filtered = distinct.Where(name => name.Namespace != 2 || !distinct.Any(other =>
            other.ParentRecordNumber == name.ParentRecordNumber && other.ParentSequenceNumber == name.ParentSequenceNumber && other.Namespace is 1 or 3));
        return filtered
            .OrderBy(name => NtfsFileRecordParser.NamespaceScore(name.Namespace))
            .ThenBy(name => name.Name, StringComparer.Ordinal)
            .ThenBy(name => name.ParentRecordNumber)
            .Take(maximum)
            .ToArray();
    }
}

internal static class NtfsPathBuilder
{
    public static (string Path, CandidatePathState State) Build(
        long recordNumber,
        ParsedFileName link,
        IReadOnlyDictionary<long, ResolvedFileRecord> records,
        int maximumDepth,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var components = new List<string> { link.Name };
        var characters = link.Name.Length;
        var visited = new HashSet<long> { recordNumber };
        var parentNumber = link.ParentRecordNumber;
        var parentSequence = link.ParentSequenceNumber;
        for (var depth = 0; depth < maximumDepth; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(parentNumber)) return (Incomplete(components), CandidatePathState.CycleDetected);
            if (!records.TryGetValue(parentNumber, out var parent)) return (Incomplete(components), CandidatePathState.Orphaned);
            if (parentSequence > 0 && parent.Base.SequenceNumber != parentSequence) return (Incomplete(components), CandidatePathState.StaleParent);
            if (parent.Base.RecordNumber == 5) return ($"\\{string.Join('\\', components.AsEnumerable().Reverse())}", CandidatePathState.Complete);

            var parentName = parent.PrimaryFileName;
            if (parentName is null) return (Incomplete(components), CandidatePathState.Invalid);
            if (characters > maximumCharacters - parentName.Name.Length - 1) return (Incomplete(components), CandidatePathState.Invalid);
            characters += parentName.Name.Length + 1;
            components.Add(parentName.Name);
            parentNumber = parentName.ParentRecordNumber;
            parentSequence = parentName.ParentSequenceNumber;
        }

        return (Incomplete(components), CandidatePathState.Invalid);
    }

    private static string Incomplete(IEnumerable<string> leafFirst) => $"[unresolved]\\{string.Join('\\', leafFirst.Reverse())}";
}
