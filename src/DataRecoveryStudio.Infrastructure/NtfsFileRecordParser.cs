using System.Buffers.Binary;
using System.Text;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

internal static class NtfsFileRecordParser
{
    private const uint AttributeEnd = 0xFFFFFFFF;
    private const uint StandardInformationType = 0x10;
    private const uint AttributeListType = 0x20;
    private const uint FileNameType = 0x30;
    private const uint DataType = 0x80;

    public static FileRecordParseResult Parse(ReadOnlySpan<byte> rawRecord, long recordNumber, long? imageOffset, int bytesPerSector, StandardScanBudgets budgets)
    {
        var diagnostics = new List<ScanDiagnostic>();
        if (rawRecord.Length < 48 || !rawRecord[..4].SequenceEqual("FILE"u8))
        {
            diagnostics.Add(Diagnostic("NTFS_FILE_SIGNATURE_INVALID", "file-record", "The MFT record signature is invalid.", imageOffset, recordNumber));
            return new(null, diagnostics);
        }

        if (!TryApplyUsaFixups(rawRecord, bytesPerSector, out var fixedRecord, out var fixupReason))
        {
            diagnostics.Add(Diagnostic("NTFS_USA_FIXUP_INVALID", "usa-fixup", fixupReason, imageOffset, recordNumber));
            return new(null, diagnostics);
        }

        var record = fixedRecord.AsSpan();
        var sequence = BinaryPrimitives.ReadUInt16LittleEndian(record[16..]);
        var attributeOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[20..]);
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(record[22..]);
        var usedSizeValue = BinaryPrimitives.ReadUInt32LittleEndian(record[24..]);
        var allocatedSizeValue = BinaryPrimitives.ReadUInt32LittleEndian(record[28..]);
        var baseReference = BinaryPrimitives.ReadUInt64LittleEndian(record[32..]);
        if (usedSizeValue > (uint)record.Length || allocatedSizeValue > (uint)record.Length || usedSizeValue < 48 ||
            allocatedSizeValue < usedSizeValue || attributeOffset < 48 || attributeOffset >= usedSizeValue)
        {
            diagnostics.Add(Diagnostic("NTFS_FILE_HEADER_INVALID", "file-record", "The MFT record declares invalid used, allocated, or attribute bounds.", imageOffset, recordNumber));
            return new(null, diagnostics);
        }

        var parsed = new ParsedFileRecord(recordNumber, sequence, (flags & 0x01) != 0, (flags & 0x02) != 0)
        {
            BaseRecordNumber = baseReference == 0 ? null : checked((long)(baseReference & 0x0000FFFFFFFFFFFFUL)),
            BaseRecordSequence = (ushort)(baseReference >> 48),
        };
        var usedSize = checked((int)usedSizeValue);
        var offset = (int)attributeOffset;
        var attributeCount = 0;
        var foundTerminator = false;
        while (offset < usedSize)
        {
            if (usedSize - offset < 4)
            {
                AddDamage("NTFS_ATTRIBUTE_HEADER_TRUNCATED", "An attribute header is truncated.");
                break;
            }

            var type = BinaryPrimitives.ReadUInt32LittleEndian(record[offset..]);
            if (type == AttributeEnd)
            {
                foundTerminator = true;
                break;
            }

            if (++attributeCount > budgets.MaximumAttributesPerRecord)
            {
                AddDamage("NTFS_ATTRIBUTE_LIMIT_REACHED", "The per-record attribute safety limit was reached.");
                break;
            }

            if (usedSize - offset < 16)
            {
                AddDamage("NTFS_ATTRIBUTE_HEADER_TRUNCATED", "An attribute header is truncated.");
                break;
            }

            var lengthValue = BinaryPrimitives.ReadUInt32LittleEndian(record[(offset + 4)..]);
            if (lengthValue == 0)
            {
                AddDamage("NTFS_ATTRIBUTE_LENGTH_ZERO", "A zero-length attribute was rejected to prevent a parser loop.");
                break;
            }

            if (lengthValue < 16 || lengthValue > int.MaxValue)
            {
                AddDamage("NTFS_ATTRIBUTE_LENGTH_INVALID", "An attribute declares an invalid length.");
                break;
            }

            var length = (int)lengthValue;
            int end;
            try
            {
                end = checked(offset + length);
            }
            catch (OverflowException)
            {
                AddDamage("NTFS_ATTRIBUTE_RANGE_OVERFLOW", "An attribute range overflowed bounded arithmetic.");
                break;
            }

            if (end > usedSize)
            {
                AddDamage("NTFS_ATTRIBUTE_OUT_OF_RANGE", "An attribute extends beyond the used MFT record range.");
                break;
            }

            var attribute = record.Slice(offset, length);
            var nonResident = attribute[8];
            var nameLength = attribute[9];
            var nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[10..]);
            var attributeFlags = BinaryPrimitives.ReadUInt16LittleEndian(attribute[12..]);
            var attributeId = BinaryPrimitives.ReadUInt16LittleEndian(attribute[14..]);
            if (nonResident is not (0 or 1))
            {
                AddDamage("NTFS_ATTRIBUTE_FORM_INVALID", "An attribute has an invalid resident flag.");
                break;
            }

            if (!TryReadAttributeName(attribute, nameLength, nameOffset, budgets.MaximumFilenameLength, out var name))
            {
                AddDamage("NTFS_ATTRIBUTE_NAME_INVALID", "An attribute name is outside its bounded attribute range.");
                break;
            }

            if (type == StandardInformationType)
            {
                ParseStandardInformation(attribute, nonResident, parsed, diagnostics, imageOffset, recordNumber);
            }
            else if (type == FileNameType)
            {
                if (nonResident != 0 || !TryGetResidentValue(attribute, out var value) || !TryParseFileName(value, budgets.MaximumFilenameLength, out var fileName))
                {
                    AddDamage("NTFS_FILE_NAME_INVALID", "$FILE_NAME is malformed or exceeds the filename safety limit.");
                }
                else
                {
                    parsed.FileNames.Add(fileName!);
                }
            }
            else if (type is DataType or AttributeListType)
            {
                var stream = ParseStreamAttribute(type, attribute, nonResident != 0, name, attributeFlags, attributeId, budgets.MaximumDataRuns, imageOffset, recordNumber, diagnostics);
                (type == DataType ? parsed.DataAttributes : parsed.AttributeLists).Add(stream);
                parsed.HasDamagedMetadata |= !stream.MetadataIsComplete;
            }

            offset = end;
        }

        if (!foundTerminator && offset >= usedSize)
        {
            AddDamage("NTFS_ATTRIBUTE_TERMINATOR_MISSING", "The attribute sequence has no bounded end marker.");
        }

        if (parsed.BaseRecordNumber is not null)
        {
            diagnostics.Add(new("NTFS_EXTENSION_RECORD_SKIPPED", ScanDiagnosticSeverity.Information, "file-record", "An extension record was parsed but is not emitted as an independent deleted candidate.", imageOffset, recordNumber));
        }

        return new(parsed, diagnostics);

        void AddDamage(string code, string reason)
        {
            parsed.HasDamagedMetadata = true;
            diagnostics.Add(Diagnostic(code, "attribute-parse", reason, imageOffset, recordNumber));
        }
    }

    private static void ParseStandardInformation(ReadOnlySpan<byte> attribute, byte nonResident, ParsedFileRecord parsed, List<ScanDiagnostic> diagnostics, long? imageOffset, long recordNumber)
    {
        if (nonResident != 0 || !TryGetResidentValue(attribute, out var value))
        {
            parsed.HasDamagedMetadata = true;
            diagnostics.Add(Diagnostic("NTFS_STANDARD_INFORMATION_INVALID", "standard-information", "$STANDARD_INFORMATION is not a valid bounded resident value.", imageOffset, recordNumber));
        }
        else if (value.Length >= 36)
        {
            parsed.ModifiedAt = TryReadFileTime(BinaryPrimitives.ReadInt64LittleEndian(value[8..]));
            parsed.StandardFileAttributes = BinaryPrimitives.ReadUInt32LittleEndian(value[32..]);
        }
        else
        {
            parsed.HasDamagedMetadata = true;
            diagnostics.Add(Diagnostic("NTFS_STANDARD_INFORMATION_TRUNCATED", "standard-information", "$STANDARD_INFORMATION is truncated.", imageOffset, recordNumber));
        }
    }

    private static ParsedStreamAttribute ParseStreamAttribute(uint type, ReadOnlySpan<byte> attribute, bool nonResident, string? name, ushort flags, ushort id, int maximumRuns, long? imageOffset, long recordNumber, List<ScanDiagnostic> diagnostics)
    {
        var operation = type == AttributeListType ? "attribute-list" : "data-attribute";
        if (!nonResident)
        {
            if (!TryGetResidentValue(attribute, out var value))
            {
                diagnostics.Add(Diagnostic(type == AttributeListType ? "NTFS_ATTRIBUTE_LIST_INVALID" : "NTFS_DATA_RESIDENT_INVALID", operation, "A resident value is outside its bounded attribute range.", imageOffset, recordNumber));
                return ParsedStreamAttribute.Invalid(type, name, id, flags);
            }

            return new(type, name, id, false, 0, 0, value.Length, value.Length, value.Length, 0, flags, [], value.ToArray(), true);
        }

        if (attribute.Length < 64)
        {
            diagnostics.Add(Diagnostic(type == AttributeListType ? "NTFS_ATTRIBUTE_LIST_INVALID" : "NTFS_DATA_NONRESIDENT_TRUNCATED", operation, "A non-resident attribute header is truncated.", imageOffset, recordNumber));
            return ParsedStreamAttribute.Invalid(type, name, id, flags);
        }

        var lowestValue = BinaryPrimitives.ReadUInt64LittleEndian(attribute[16..]);
        var highestValue = BinaryPrimitives.ReadUInt64LittleEndian(attribute[24..]);
        var runOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[32..]);
        var compressionUnit = BinaryPrimitives.ReadUInt16LittleEndian(attribute[34..]);
        var allocatedValue = BinaryPrimitives.ReadUInt64LittleEndian(attribute[40..]);
        var logicalValue = BinaryPrimitives.ReadUInt64LittleEndian(attribute[48..]);
        var initializedValue = BinaryPrimitives.ReadUInt64LittleEndian(attribute[56..]);
        if (runOffset < 64 || runOffset >= attribute.Length || lowestValue > long.MaxValue || highestValue > long.MaxValue ||
            allocatedValue > long.MaxValue || logicalValue > long.MaxValue || initializedValue > long.MaxValue || lowestValue > highestValue)
        {
            diagnostics.Add(Diagnostic(type == AttributeListType ? "NTFS_ATTRIBUTE_LIST_INVALID" : "NTFS_DATA_NONRESIDENT_INVALID", operation, "A non-resident attribute header contains an out-of-range value.", imageOffset, recordNumber));
            return ParsedStreamAttribute.Invalid(type, name, id, flags);
        }

        var lowest = (long)lowestValue;
        var highest = (long)highestValue;
        if (!NtfsRunListParser.TryParse(attribute[runOffset..], lowest, maximumRuns, out var runs, out var code, out var reason))
        {
            diagnostics.Add(Diagnostic(code, "run-list", reason, imageOffset, recordNumber));
            return new(type, name, id, true, lowest, highest, (long)logicalValue, (long)allocatedValue, (long)initializedValue, compressionUnit, flags, [], null, false);
        }

        long expectedEnd;
        try
        {
            expectedEnd = runs.Count == 0 ? lowest : checked(runs[^1].VirtualCluster + runs[^1].ClusterCount - 1);
        }
        catch (OverflowException)
        {
            diagnostics.Add(Diagnostic("NTFS_ATTRIBUTE_VCN_RANGE_INVALID", operation, "The run-list VCN range overflowed.", imageOffset, recordNumber));
            return new(type, name, id, true, lowest, highest, (long)logicalValue, (long)allocatedValue, (long)initializedValue, compressionUnit, flags, runs, null, false);
        }

        var complete = runs.Count > 0 && runs[0].VirtualCluster == lowest && expectedEnd == highest;
        if (!complete)
        {
            diagnostics.Add(Diagnostic("NTFS_ATTRIBUTE_VCN_RANGE_INVALID", operation, "The run list does not match the attribute's declared VCN range.", imageOffset, recordNumber));
        }

        return new(type, name, id, true, lowest, highest, (long)logicalValue, (long)allocatedValue, (long)initializedValue, compressionUnit, flags, runs, null, complete);
    }

    private static bool TryApplyUsaFixups(ReadOnlySpan<byte> record, int bytesPerSector, out byte[] fixedRecord, out string reason)
    {
        fixedRecord = [];
        reason = "The Update Sequence Array is invalid.";
        if (bytesPerSector <= 0 || record.Length % bytesPerSector != 0)
        {
            reason = "The record size is not aligned to the declared sector size.";
            return false;
        }

        var usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        var usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
        var expectedCount = checked(record.Length / bytesPerSector + 1);
        var usaBytes = checked(usaCount * 2);
        if (usaCount != expectedCount || usaOffset < 8 || usaOffset > record.Length - usaBytes)
        {
            reason = "The Update Sequence Array count or range is invalid.";
            return false;
        }

        fixedRecord = record.ToArray();
        var usn = BinaryPrimitives.ReadUInt16LittleEndian(record[usaOffset..]);
        for (var sector = 0; sector < usaCount - 1; sector++)
        {
            var trailer = checked(((sector + 1) * bytesPerSector) - 2);
            if (BinaryPrimitives.ReadUInt16LittleEndian(record[trailer..]) != usn)
            {
                fixedRecord = [];
                reason = "A sector trailer does not match the Update Sequence Number.";
                return false;
            }

            var replacement = checked(usaOffset + ((sector + 1) * 2));
            fixedRecord[trailer] = record[replacement];
            fixedRecord[trailer + 1] = record[replacement + 1];
        }

        return true;
    }

    private static bool TryGetResidentValue(ReadOnlySpan<byte> attribute, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (attribute.Length < 24)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(attribute[16..]);
        var offset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[20..]);
        if (length > int.MaxValue || offset < 24)
        {
            return false;
        }

        int end;
        try
        {
            end = checked(offset + (int)length);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (end > attribute.Length)
        {
            return false;
        }

        value = attribute.Slice(offset, (int)length);
        return true;
    }

    private static bool TryReadAttributeName(ReadOnlySpan<byte> attribute, byte count, ushort offset, int maximum, out string? name)
    {
        name = null;
        if (count == 0)
        {
            return true;
        }

        var bytes = checked(count * 2);
        var minimum = attribute[8] == 0 ? 24 : 64;
        if (count > maximum || offset < minimum || offset > attribute.Length - bytes)
        {
            return false;
        }

        name = Encoding.Unicode.GetString(attribute.Slice(offset, bytes));
        return !name.Contains('\uFFFD');
    }

    private static bool TryParseFileName(ReadOnlySpan<byte> value, int maximum, out ParsedFileName? fileName)
    {
        fileName = null;
        if (value.Length < 66)
        {
            return false;
        }

        var count = value[64];
        var bytes = checked(count * 2);
        if (count == 0 || count > maximum || 66 + bytes > value.Length)
        {
            return false;
        }

        var name = Encoding.Unicode.GetString(value.Slice(66, bytes));
        if (name.Contains('\uFFFD'))
        {
            return false;
        }

        var parent = BinaryPrimitives.ReadUInt64LittleEndian(value);
        fileName = new(
            SanitizeName(name),
            checked((long)(parent & 0x0000FFFFFFFFFFFFUL)),
            (ushort)(parent >> 48),
            value[65],
            ToLong(BinaryPrimitives.ReadUInt64LittleEndian(value[48..])),
            ToLong(BinaryPrimitives.ReadUInt64LittleEndian(value[40..])),
            BinaryPrimitives.ReadUInt32LittleEndian(value[56..]),
            TryReadFileTime(BinaryPrimitives.ReadInt64LittleEndian(value[16..])));
        return true;
    }

    private static long ToLong(ulong value) => value > long.MaxValue ? 0 : (long)value;

    private static DateTimeOffset? TryReadFileTime(long value)
    {
        if (value <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromFileTime(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    internal static int NamespaceScore(byte value) => value switch { 1 => 0, 3 => 1, 0 => 2, 2 => 3, _ => 4 };

    private static string SanitizeName(string name) =>
        new(name.Select(character => character is '\\' or '/' || char.IsControl(character) ? '_' : character).ToArray());

    private static ScanDiagnostic Diagnostic(string code, string operation, string reason, long? offset, long record) =>
        new(code, ScanDiagnosticSeverity.Warning, operation, reason, offset, record);
}

internal static class NtfsRunListParser
{
    public static bool TryParse(ReadOnlySpan<byte> bytes, long startingVcn, int maximumRuns, out IReadOnlyList<NtfsDataRun> runs, out string code, out string reason)
    {
        var parsed = new List<NtfsDataRun>();
        runs = parsed;
        code = "NTFS_RUN_LIST_INVALID";
        reason = "The data run list is invalid.";
        var offset = 0;
        var vcn = startingVcn;
        var currentLcn = 0L;
        while (offset < bytes.Length)
        {
            var header = bytes[offset++];
            if (header == 0)
            {
                return true;
            }

            if (parsed.Count >= maximumRuns)
            {
                code = "NTFS_RUN_LIMIT_REACHED";
                reason = "The data-run safety limit was reached.";
                return false;
            }

            var lengthBytes = header & 0x0F;
            var offsetBytes = header >> 4;
            if (lengthBytes is 0 or > 8 || offsetBytes > 8 || offset > bytes.Length - lengthBytes - offsetBytes)
            {
                return false;
            }

            var lengthValue = ReadUnsigned(bytes.Slice(offset, lengthBytes));
            offset += lengthBytes;
            if (lengthValue == 0 || lengthValue > long.MaxValue)
            {
                return false;
            }

            var length = (long)lengthValue;
            long? lcn = null;
            var sparse = offsetBytes == 0;
            try
            {
                if (!sparse)
                {
                    currentLcn = checked(currentLcn + ReadSigned(bytes.Slice(offset, offsetBytes)));
                    if (currentLcn < 0)
                    {
                        return false;
                    }

                    lcn = currentLcn;
                }

                parsed.Add(new(vcn, length, lcn, sparse));
                vcn = checked(vcn + length);
            }
            catch (OverflowException)
            {
                code = "NTFS_RUN_LIST_OVERFLOW";
                reason = "A data run overflowed bounded cluster arithmetic.";
                return false;
            }

            offset += offsetBytes;
        }

        code = "NTFS_RUN_LIST_UNTERMINATED";
        reason = "The data run list has no bounded terminator.";
        return false;
    }

    private static ulong ReadUnsigned(ReadOnlySpan<byte> bytes)
    {
        ulong value = 0;
        for (var index = 0; index < bytes.Length; index++) value |= (ulong)bytes[index] << (index * 8);
        return value;
    }

    private static long ReadSigned(ReadOnlySpan<byte> bytes)
    {
        var value = ReadUnsigned(bytes);
        if (bytes.Length < 8 && (bytes[^1] & 0x80) != 0) value |= ulong.MaxValue << (bytes.Length * 8);
        return unchecked((long)value);
    }
}

internal sealed record ParsedStreamAttribute(
    uint Type,
    string? Name,
    ushort AttributeId,
    bool IsNonResident,
    long LowestVcn,
    long HighestVcn,
    long LogicalSize,
    long AllocatedSize,
    long InitializedSize,
    ushort CompressionUnit,
    ushort Flags,
    IReadOnlyList<NtfsDataRun> Runs,
    byte[]? ResidentValue,
    bool MetadataIsComplete)
{
    public bool IsCompressed => (Flags & 0x0001) != 0;
    public bool IsEncrypted => (Flags & 0x4000) != 0;
    public bool IsSparse => (Flags & 0x8000) != 0 || Runs.Any(run => run.IsSparse);
    public static ParsedStreamAttribute Invalid(uint type, string? name, ushort id, ushort flags) =>
        new(type, name, id, false, 0, 0, 0, 0, 0, 0, flags, [], null, false);
}

internal sealed record ParsedFileName(string Name, long ParentRecordNumber, ushort ParentSequenceNumber, byte Namespace, long LogicalSize, long AllocatedSize, uint FileAttributes, DateTimeOffset? ModifiedAt);

internal sealed class ParsedFileRecord(long recordNumber, ushort sequenceNumber, bool isInUse, bool isDirectory)
{
    public long RecordNumber { get; } = recordNumber;
    public ushort SequenceNumber { get; } = sequenceNumber;
    public bool IsInUse { get; } = isInUse;
    public bool IsDirectory { get; } = isDirectory;
    public long? BaseRecordNumber { get; init; }
    public ushort BaseRecordSequence { get; init; }
    public uint StandardFileAttributes { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public List<ParsedFileName> FileNames { get; } = [];
    public List<ParsedStreamAttribute> DataAttributes { get; } = [];
    public List<ParsedStreamAttribute> AttributeLists { get; } = [];
    public bool HasDamagedMetadata { get; set; }
}

internal sealed record FileRecordParseResult(ParsedFileRecord? Record, IReadOnlyList<ScanDiagnostic> Diagnostics);
