using System.Buffers.Binary;
using System.Text;

namespace DataRecoveryStudio.Tests;

internal sealed class NtfsTestFixtureBuilder
{
    private readonly Dictionary<int, byte[]> _records = [];
    private readonly Dictionary<long, byte[]> _clusterPayloads = [];
    private List<(long Lcn, long Count)> _mftRuns;
    private bool _corruptPrimaryBootstrap;
    private bool _corruptMirrorBootstrap;
    private bool _mirrorDisagrees;
    private bool _sparseMftExtent;

    public NtfsTestFixtureBuilder(int recordCount = 12, ushort bytesPerSector = 512, byte sectorsPerCluster = 2, sbyte recordSizeEncoding = -10)
    {
        RecordCount = recordCount;
        BytesPerSector = bytesPerSector;
        SectorsPerCluster = sectorsPerCluster;
        RecordSizeEncoding = recordSizeEncoding;
        ClusterSize = checked(bytesPerSector * sectorsPerCluster);
        RecordSize = recordSizeEncoding > 0 ? checked(recordSizeEncoding * ClusterSize) : checked(1 << -recordSizeEncoding);
        MftLcn = 2;
        var clusters = DivideRoundUp(checked((long)recordCount * RecordSize), ClusterSize);
        _mftRuns = [(MftLcn, clusters)];
        MftMirrorLcn = checked(MftLcn + clusters + 8);
        TotalSectors = checked((ulong)((MftMirrorLcn + 8) * SectorsPerCluster));
    }

    public int RecordCount { get; }
    public ushort BytesPerSector { get; }
    public byte SectorsPerCluster { get; }
    public sbyte RecordSizeEncoding { get; }
    public int ClusterSize { get; }
    public int RecordSize { get; }
    public int MftLcn { get; }
    public long MftMirrorLcn { get; private set; }
    public ulong TotalSectors { get; private set; }
    public int MftOffset => checked(MftLcn * ClusterSize);

    public NtfsTestFixtureBuilder SetMftRuns(params (long Lcn, long Count)[] runs)
    {
        _mftRuns = runs.ToList();
        MftMirrorLcn = checked(runs.Max(run => run.Lcn + run.Count) + 8);
        return this;
    }

    public NtfsTestFixtureBuilder AddRecord(int number, ushort sequence, bool inUse, bool directory, params byte[][] attributes)
    {
        _records[number] = BuildFileRecord(number, sequence, inUse, directory, null, 0, attributes);
        return this;
    }

    public NtfsTestFixtureBuilder AddExtensionRecord(int number, ushort sequence, long baseRecordNumber, ushort baseSequence, params byte[][] attributes)
    {
        _records[number] = BuildFileRecord(number, sequence, true, false, baseRecordNumber, baseSequence, attributes);
        return this;
    }

    public NtfsTestFixtureBuilder AddRoot(ushort sequence = 1) =>
        AddRecord(5, sequence, true, true, FileName(5, sequence, ".", 1, directory: true));

    public NtfsTestFixtureBuilder CorruptUsa(int number)
    {
        _records[number][BytesPerSector - 2] ^= 0xFF;
        return this;
    }

    public NtfsTestFixtureBuilder CorruptPrimaryBootstrap() { _corruptPrimaryBootstrap = true; return this; }
    public NtfsTestFixtureBuilder CorruptMirrorBootstrap() { _corruptMirrorBootstrap = true; return this; }
    public NtfsTestFixtureBuilder MakeMirrorDisagree() { _mirrorDisagrees = true; return this; }
    public NtfsTestFixtureBuilder MakeMftSparse() { _sparseMftExtent = true; return this; }

    public NtfsTestFixtureBuilder MutateRecord(int number, Action<byte[]> mutation) { mutation(_records[number]); return this; }

    public NtfsTestFixtureBuilder WriteClusterPayload(long lcn, byte[] bytes)
    {
        _clusterPayloads[lcn] = bytes.ToArray();
        return this;
    }

    public byte[] Build()
    {
        var mftLogicalSize = checked((long)RecordCount * RecordSize);
        var mftClusters = DivideRoundUp(mftLogicalSize, ClusterSize);
        var describedClusters = _mftRuns.Sum(run => run.Count);
        var describedRuns = _mftRuns.Select(run => ((long?)run.Lcn, run.Count)).ToArray();
        if (_sparseMftExtent) describedRuns[^1] = (null, describedRuns[^1].Count);
        var mftRunList = EncodeRunList(describedRuns);
        var mftData = NonResidentData(mftRunList, mftLogicalSize, checked(mftClusters * ClusterSize), mftLogicalSize, highestVcn: describedClusters - 1);
        var primaryRecord = BuildFileRecord(0, 1, true, false, null, 0, [mftData, FileName(5, 1, "$MFT", 1)]);
        if (_records.TryGetValue(0, out var customRecord)) primaryRecord = customRecord;
        var mirrorRecord = primaryRecord.ToArray();
        if (_corruptPrimaryBootstrap) primaryRecord[BytesPerSector - 2] ^= 0xFF;
        if (_corruptMirrorBootstrap) mirrorRecord[BytesPerSector - 2] ^= 0xFF;
        if (_mirrorDisagrees) MutateFirstNonResidentLogicalSize(mirrorRecord, mftLogicalSize + RecordSize);

        var highestCluster = Math.Max(MftMirrorLcn + DivideRoundUp(RecordSize, ClusterSize), _mftRuns.Max(run => run.Lcn + run.Count));
        if (_clusterPayloads.Count > 0) highestCluster = Math.Max(highestCluster, _clusterPayloads.Max(item => item.Key + DivideRoundUp(item.Value.LongLength, ClusterSize)));
        TotalSectors = checked((ulong)((highestCluster + 4) * SectorsPerCluster));
        var image = new byte[checked((int)(TotalSectors * BytesPerSector))];
        WriteBootSector(image);

        var mftBytes = new byte[checked((int)mftLogicalSize)];
        primaryRecord.CopyTo(mftBytes, 0);
        foreach (var (number, record) in _records.Where(item => item.Key != 0))
        {
            record.CopyTo(mftBytes, checked(number * RecordSize));
        }

        var virtualCluster = 0L;
        foreach (var run in _mftRuns)
        {
            var bytes = checked((int)(run.Count * ClusterSize));
            var available = Math.Min(bytes, mftBytes.Length - checked((int)(virtualCluster * ClusterSize)));
            if (available > 0) Array.Copy(mftBytes, checked((int)(virtualCluster * ClusterSize)), image, checked((int)(run.Lcn * ClusterSize)), available);
            virtualCluster = checked(virtualCluster + run.Count);
        }

        mirrorRecord.CopyTo(image, checked((int)(MftMirrorLcn * ClusterSize)));
        foreach (var (lcn, payload) in _clusterPayloads) payload.CopyTo(image, checked((int)(lcn * ClusterSize)));
        return image;
    }

    public byte[] StandardInformation(uint attributes = 0x20)
    {
        var value = new byte[48];
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(8), DateTimeOffset.Parse("2024-01-02T03:04:05Z").ToFileTime());
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(32), attributes);
        return ResidentAttribute(0x10, null, value);
    }

    public byte[] FileName(long parentNumber, ushort parentSequence, string name, byte nameSpace, long logicalSize = 0, long allocatedSize = 0, bool directory = false)
    {
        var encoded = Encoding.Unicode.GetBytes(name);
        var value = new byte[66 + encoded.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(value, FileReference(parentNumber, parentSequence));
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(16), DateTimeOffset.Parse("2024-02-03T04:05:06Z").ToFileTime());
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(40), allocatedSize);
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(48), logicalSize);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(56), directory ? 0x10000000u : 0x20u);
        value[64] = checked((byte)name.Length);
        value[65] = nameSpace;
        encoded.CopyTo(value, 66);
        return ResidentAttribute(0x30, null, value);
    }

    public byte[] ResidentData(int length, string? name = null) => ResidentAttribute(0x80, name, Enumerable.Repeat((byte)0xCC, length).ToArray());
    public byte[] ResidentData(byte[] content, string? name = null) => ResidentAttribute(0x80, name, content.ToArray());
    public byte[] AttributeList(params byte[][] entries) => ResidentAttribute(0x20, null, entries.SelectMany(entry => entry).ToArray());
    public byte[] NonResidentAttributeList(byte[] runList, long logicalSize, long allocatedSize, long initializedSize, long highestVcn) =>
        NonResidentAttribute(0x20, runList, logicalSize, allocatedSize, initializedSize, null, 0, highestVcn, 0);

    public byte[] AttributeListEntry(uint type, long recordNumber, ushort sequence, ushort attributeId = 0, long lowestVcn = 0, string? name = null)
    {
        var encodedName = name is null ? [] : Encoding.Unicode.GetBytes(name);
        var length = Align(26 + encodedName.Length, 8);
        var entry = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(entry, type);
        BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(4), (ushort)length);
        entry[6] = checked((byte)(name?.Length ?? 0));
        entry[7] = name is null ? (byte)0 : (byte)26;
        BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(8), lowestVcn);
        BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(16), FileReference(recordNumber, sequence));
        BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(24), attributeId);
        encodedName.CopyTo(entry, 26);
        return entry;
    }

    public byte[] NonResidentData(byte[] runList, long logicalSize, long allocatedSize, long initializedSize, string? name = null, long lowestVcn = 0, long? highestVcn = null, ushort flags = 0) =>
        NonResidentAttribute(0x80, runList, logicalSize, allocatedSize, initializedSize, name, lowestVcn, highestVcn ?? lowestVcn + 5, flags);

    public static byte[] EncodeRunList(params (long? Lcn, long Count)[] runs)
    {
        var bytes = new List<byte>();
        var currentLcn = 0L;
        foreach (var run in runs)
        {
            var lengthBytes = EncodeUnsigned((ulong)run.Count);
            byte[] offsetBytes = [];
            if (run.Lcn is not null)
            {
                offsetBytes = EncodeSigned(checked(run.Lcn.Value - currentLcn));
                currentLcn = run.Lcn.Value;
            }

            bytes.Add((byte)((offsetBytes.Length << 4) | lengthBytes.Length));
            bytes.AddRange(lengthBytes);
            bytes.AddRange(offsetBytes);
        }

        bytes.Add(0);
        return bytes.ToArray();
    }

    public static byte[] ZeroLengthAttribute() { var value = new byte[16]; BinaryPrimitives.WriteUInt32LittleEndian(value, 0x90); return value; }
    public static byte[] InvalidLengthAttribute() { var value = new byte[16]; BinaryPrimitives.WriteUInt32LittleEndian(value, 0x90); BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(4), uint.MaxValue); return value; }

    private byte[] NonResidentAttribute(uint type, byte[] runList, long logicalSize, long allocatedSize, long initializedSize, string? name, long lowestVcn, long highestVcn, ushort flags)
    {
        var encodedName = name is null ? [] : Encoding.Unicode.GetBytes(name);
        var runOffset = Align(64 + encodedName.Length, 8);
        var length = Align(runOffset + runList.Length, 8);
        var attribute = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(attribute, type);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(4), (uint)length);
        attribute[8] = 1;
        attribute[9] = checked((byte)(name?.Length ?? 0));
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(10), name is null ? (ushort)0 : (ushort)64);
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(12), flags);
        BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(16), lowestVcn);
        BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(24), highestVcn);
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(32), (ushort)runOffset);
        BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(40), allocatedSize);
        BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(48), logicalSize);
        BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(56), initializedSize);
        encodedName.CopyTo(attribute, 64);
        runList.CopyTo(attribute, runOffset);
        return attribute;
    }

    private byte[] BuildFileRecord(int number, ushort sequence, bool inUse, bool directory, long? baseRecord, ushort baseSequence, IReadOnlyList<byte[]> attributes)
    {
        var record = new byte[RecordSize];
        "FILE"u8.CopyTo(record);
        const ushort usaOffset = 48;
        var usaCount = checked((ushort)((RecordSize / BytesPerSector) + 1));
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), usaOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), usaCount);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(16), sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(18), 1);
        var attributeOffset = checked((ushort)Align(usaOffset + (usaCount * 2), 8));
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(20), attributeOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(22), (ushort)((inUse ? 1 : 0) | (directory ? 2 : 0)));
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(28), (uint)RecordSize);
        if (baseRecord is not null) BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(32), FileReference(baseRecord.Value, baseSequence));
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(44), (uint)number);
        var offset = (int)attributeOffset;
        foreach (var attribute in attributes) { attribute.CopyTo(record, offset); offset = checked(offset + attribute.Length); }
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset), uint.MaxValue);
        offset = Align(offset + 4, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(24), (uint)offset);
        const ushort usn = 0xA55A;
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(usaOffset), usn);
        for (var sector = 0; sector < usaCount - 1; sector++)
        {
            var trailer = ((sector + 1) * BytesPerSector) - 2;
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(usaOffset + ((sector + 1) * 2)), BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(trailer)));
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(trailer), usn);
        }

        return record;
    }

    private static byte[] ResidentAttribute(uint type, string? name, byte[] value)
    {
        var encodedName = name is null ? [] : Encoding.Unicode.GetBytes(name);
        var valueOffset = Align(24 + encodedName.Length, 8);
        var length = Align(valueOffset + value.Length, 8);
        var attribute = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(attribute, type);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(4), (uint)length);
        attribute[9] = checked((byte)(name?.Length ?? 0));
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(10), name is null ? (ushort)0 : (ushort)24);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(16), (uint)value.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(20), (ushort)valueOffset);
        encodedName.CopyTo(attribute, 24);
        value.CopyTo(attribute, valueOffset);
        return attribute;
    }

    private void WriteBootSector(byte[] image)
    {
        image[0] = 0xEB; image[1] = 0x52; image[2] = 0x90; "NTFS    "u8.CopyTo(image.AsSpan(3));
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(11), BytesPerSector); image[13] = SectorsPerCluster;
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(40), TotalSectors);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(48), (ulong)MftLcn);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(56), (ulong)MftMirrorLcn);
        image[64] = unchecked((byte)RecordSizeEncoding); image[68] = 1; image[510] = 0x55; image[511] = 0xAA;
    }

    private static void MutateFirstNonResidentLogicalSize(byte[] record, long value)
    {
        var offset = (int)BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(20));
        while (BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(offset)) != uint.MaxValue)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(offset)) == 0x80 && record[offset + 8] == 1)
            { BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(offset + 48), value); return; }
            offset += checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(offset + 4)));
        }
    }

    private static byte[] EncodeUnsigned(ulong value)
    {
        var bytes = new List<byte>();
        do { bytes.Add((byte)value); value >>= 8; } while (value != 0);
        return bytes.ToArray();
    }

    private static byte[] EncodeSigned(long value)
    {
        var bytes = new List<byte>();
        var remaining = value;
        while (true)
        {
            var current = (byte)remaining;
            remaining >>= 8;
            var sign = (current & 0x80) != 0;
            bytes.Add(current);
            if ((remaining == 0 && !sign) || (remaining == -1 && sign)) return bytes.ToArray();
        }
    }

    private static ulong FileReference(long number, ushort sequence) => ((ulong)sequence << 48) | ((ulong)number & 0x0000FFFFFFFFFFFFUL);
    private static int Align(int value, int alignment) => checked((value + alignment - 1) / alignment * alignment);
    private static long DivideRoundUp(long value, long divisor) => checked((value + divisor - 1) / divisor);
}
