using System.Buffers.Binary;
using System.Text;
using DataRecoveryStudio.Infrastructure;

namespace DataRecoveryStudio.Tests;

internal sealed class Fat32TestFixtureBuilder
{
    public const ushort BytesPerSector = 512;
    public const byte SectorsPerCluster = 1;
    public const ushort ReservedSectors = 32;
    public const byte FatCount = 2;
    public const uint FatSectors = 512;
    public const uint ClusterCount = 65_525;
    public const uint FirstDataSector = ReservedSectors + FatCount * FatSectors;
    public const uint TotalSectors = FirstDataSector + ClusterCount;
    public const int ImageLength = checked((int)(TotalSectors * BytesPerSector));

    private readonly byte[] _image = new byte[ImageLength];

    public Fat32TestFixtureBuilder()
    {
        WriteBootSector(0);
        WriteBootSector(6);
        WriteFsInfo(valid: true);
        for (byte fat = 0; fat < FatCount; fat++)
        {
            SetFat(fat, 0, 0x0FFFFFF8);
            SetFat(fat, 1, 0x0FFFFFFF);
            SetFat(fat, 2, 4);
            SetFat(fat, 3, 0x0FFFFFFF);
            SetFat(fat, 4, 0x0FFFFFFF);
            SetFat(fat, 20, 0);
            SetFat(fat, 21, 0x0FFFFFFF);
            SetFat(fat, 30, 0x0FFFFFFF);
            SetFat(fat, 31, 0x0FFFFFFF);
            SetFat(fat, 40, 41);
            SetFat(fat, 41, 0x0FFFFFFF);
            SetFat(fat, 60, 0x0FFFFFFF);
        }

        FillCluster(10, 0x10);
        FillCluster(11, 0x11);
        FillCluster(40, 0x40);
        FillCluster(41, 0x41);
        FillCluster(60, 0x60);

        var root = Cluster(2);
        WriteShort(root, 0, "ACTIVE", "TXT", 0x20, 60, 12, deleted: false);
        WriteShort(root, 1, "FREE", "TXT", 0x20, 10, 700, deleted: true, invalidDate: true);
        WriteShort(root, 2, "ZERO", "TXT", 0x20, 0, 0, deleted: true);
        WriteShort(root, 3, "MIXED", "BIN", 0x20, 20, 700, deleted: true);
        WriteShort(root, 4, "REUSED", "BIN", 0x20, 30, 700, deleted: true);

        var probableShort = ShortBytes("UNICOD~1", "TXT");
        WriteLfn(root, 5, "Đã xóa.txt", Fat32NameDecoder.ComputeChecksum(probableShort), deleted: true);
        WriteShort(root, 6, probableShort, 0x20, 12, 100, deleted: true);

        var ambiguousShort = ShortBytes("AMBIGU~1", "TXT");
        var checksumCandidate = ambiguousShort.ToArray();
        checksumCandidate[0] = 0;
        WriteLfn(root, 7, "Maybe.txt", Fat32NameDecoder.ComputeChecksum(checksumCandidate), deleted: true);
        WriteShort(root, 8, ambiguousShort, 0x20, 13, 100, deleted: true);
        WriteShort(root, 9, "OLDDIR", string.Empty, 0x10, 50, 0, deleted: true);
        var activeDirectoryShort = ShortBytes("SUBDIR", string.Empty);
        WriteLfn(root, 10, "Long Folder", Fat32NameDecoder.ComputeChecksum(activeDirectoryShort), deleted: false);
        WriteShort(root, 11, activeDirectoryShort, 0x10, 3, 0, deleted: false);
        WriteLfn(root, 12, "Orphan.txt", 0x42, deleted: false);
        WriteShort(root, 13, "LABEL", string.Empty, 0x08, 0, 0, deleted: true);
        for (var slot = 14; slot < 16; slot++)
        {
            WriteShort(root, slot, $"FILL{slot}", "TMP", 0x20, 0, 0, deleted: false);
        }

        var secondRoot = Cluster(4);
        WriteShort(secondRoot, 0, "CHAIN", "DAT", 0x20, 40, 700, deleted: true);
        secondRoot[32] = 0;

        var nested = Cluster(3);
        WriteShort(nested, 0, "INNER", "TXT", 0x20, 14, 1, deleted: true);
        WriteShort(nested, 1, ".", string.Empty, 0x10, 2, 0, deleted: false);
        nested[64] = 0;
    }

    public Fat32TestFixtureBuilder InvalidatePrimarySignature()
    {
        _image[510] = 0;
        _image[511] = 0;
        return this;
    }

    public Fat32TestFixtureBuilder InvalidateBackupSignature()
    {
        var offset = 6 * BytesPerSector;
        _image[offset + 510] = 0;
        _image[offset + 511] = 0;
        return this;
    }

    public Fat32TestFixtureBuilder DisagreeingBackupRoot(uint rootCluster)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_image.AsSpan(6 * BytesPerSector + 44, 4), rootCluster);
        return this;
    }

    public Fat32TestFixtureBuilder InvalidFsInfo()
    {
        Array.Clear(_image, BytesPerSector, BytesPerSector);
        return this;
    }

    public Fat32TestFixtureBuilder MirroredDisagreement(uint cluster, uint secondFatValue)
    {
        SetFat(1, cluster, secondFatValue);
        return this;
    }

    public Fat32TestFixtureBuilder UseSecondFatOnly()
    {
        BinaryPrimitives.WriteUInt16LittleEndian(_image.AsSpan(40, 2), 0x0081);
        BinaryPrimitives.WriteUInt16LittleEndian(_image.AsSpan(6 * BytesPerSector + 40, 2), 0x0081);
        return this;
    }

    public Fat32TestFixtureBuilder UseOneFatCopy()
    {
        const uint oneFatFirstDataSector = ReservedSectors + FatSectors;
        var oneFatTotalSectors = oneFatFirstDataSector + ClusterCount;
        foreach (var cluster in new uint[] { 2, 3, 4 })
        {
            var oldOffset = checked((int)((FirstDataSector + cluster - 2) * BytesPerSector));
            var newOffset = checked((int)((oneFatFirstDataSector + cluster - 2) * BytesPerSector));
            _image.AsSpan(oldOffset, BytesPerSector).CopyTo(_image.AsSpan(newOffset, BytesPerSector));
        }
        _image[16] = 1;
        _image[6 * BytesPerSector + 16] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(_image.AsSpan(32, 4), oneFatTotalSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(_image.AsSpan(6 * BytesPerSector + 32, 4), oneFatTotalSectors);
        return this;
    }

    public Fat32TestFixtureBuilder InvalidActiveFatIndex()
    {
        BinaryPrimitives.WriteUInt16LittleEndian(_image.AsSpan(40, 2), 0x0082);
        BinaryPrimitives.WriteUInt16LittleEndian(_image.AsSpan(6 * BytesPerSector + 40, 2), 0x0082);
        return this;
    }

    public Fat32TestFixtureBuilder BackupSector(ushort sector)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(_image.AsSpan(50, 2), sector);
        return this;
    }

    public Fat32TestFixtureBuilder DirectoryCycle()
    {
        SetFat(0, 3, 3);
        SetFat(1, 3, 3);
        Cluster(3)[32] = 0x41;
        WriteShort(Cluster(3), 1, "FILL", "TMP", 0x20, 0, 0, false);
        for (var slot = 2; slot < 16; slot++) WriteShort(Cluster(3), slot, $"N{slot}", "TMP", 0x20, 0, 0, false);
        return this;
    }

    public Fat32TestFixtureBuilder FatValue(uint cluster, uint value)
    {
        SetFat(0, cluster, value);
        SetFat(1, cluster, value);
        return this;
    }

    public Fat32TestFixtureBuilder CrossLinkedChildDirectory()
    {
        WriteShort(Cluster(2), 14, "OTHERDIR", string.Empty, 0x10, 3, 0, deleted: false);
        return this;
    }

    public Fat32TestFixtureBuilder CrossLinkedPreservedFile()
    {
        WriteShort(Cluster(2), 14, "CROSSLNK", "DAT", 0x20, 41, 512, deleted: true);
        return this;
    }

    public Fat32TestFixtureBuilder MaliciousDeletedLongName()
    {
        var shortName = ShortBytes("UNICOD~1", "TXT");
        WriteLfn(Cluster(2), 5, "Bad/name.txt", Fat32NameDecoder.ComputeChecksum(shortName), deleted: true);
        return this;
    }

    public Fat32TestFixtureBuilder InvalidDeletedLfnUtf16()
    {
        BinaryPrimitives.WriteUInt16LittleEndian(Cluster(2).Slice(5 * 32 + 1, 2), 0xD800);
        BinaryPrimitives.WriteUInt16LittleEndian(Cluster(2).Slice(5 * 32 + 3, 2), (ushort)'A');
        return this;
    }

    public Fat32TestFixtureBuilder DeletedFileStartsAt(uint cluster, uint size)
    {
        var entry = Cluster(2).Slice(32, 32);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[20..], (ushort)(cluster >> 16));
        BinaryPrimitives.WriteUInt16LittleEndian(entry[26..], (ushort)cluster);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[28..], size);
        return this;
    }

    public Fat32TestFixtureBuilder Payload(uint cluster, byte value)
    {
        FillCluster(cluster, value);
        return this;
    }

    public Fat32TestFixtureBuilder SetFatEntry(uint cluster, uint value) => FatValue(cluster, value);

    public Fat32TestFixtureBuilder ActiveConflictCandidate()
    {
        WriteShort(Cluster(2), 14, "CONFLICT", "BIN", 0x20, 60, 12, deleted: true);
        return this;
    }

    public byte[] Build() => _image.ToArray();

    public static byte[] CreateBootSector4096()
    {
        var bytes = new byte[512];
        WriteBoot(bytes, 4096, 1, 32, 1, 65_622, 65, 2, 6);
        return bytes;
    }

    private void WriteBootSector(int sector) => WriteBoot(_image.AsSpan(sector * BytesPerSector, 512), BytesPerSector, SectorsPerCluster, ReservedSectors, FatCount, TotalSectors, FatSectors, 2, 6);

    private static void WriteBoot(Span<byte> boot, ushort bps, byte spc, ushort reserved, byte fats, uint total, uint fatSize, uint root, ushort backup)
    {
        boot.Clear();
        boot[0] = 0xEB; boot[1] = 0x58; boot[2] = 0x90;
        Encoding.ASCII.GetBytes("DRS7A   ").CopyTo(boot[3..]);
        BinaryPrimitives.WriteUInt16LittleEndian(boot[11..], bps);
        boot[13] = spc;
        BinaryPrimitives.WriteUInt16LittleEndian(boot[14..], reserved);
        boot[16] = fats;
        BinaryPrimitives.WriteUInt16LittleEndian(boot[17..], 0);
        boot[21] = 0xF8;
        BinaryPrimitives.WriteUInt16LittleEndian(boot[22..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(boot[24..], 63);
        BinaryPrimitives.WriteUInt16LittleEndian(boot[26..], 255);
        BinaryPrimitives.WriteUInt32LittleEndian(boot[32..], total);
        BinaryPrimitives.WriteUInt32LittleEndian(boot[36..], fatSize);
        BinaryPrimitives.WriteUInt16LittleEndian(boot[40..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(boot[42..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(boot[44..], root);
        BinaryPrimitives.WriteUInt16LittleEndian(boot[48..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(boot[50..], backup);
        boot[64] = 0x80; boot[66] = 0x29;
        BinaryPrimitives.WriteUInt32LittleEndian(boot[67..], 0x12345678);
        Encoding.ASCII.GetBytes("DRS FIXTURE").CopyTo(boot[71..]);
        Encoding.ASCII.GetBytes("FAT32   ").CopyTo(boot[82..]);
        BinaryPrimitives.WriteUInt16LittleEndian(boot[510..], 0xAA55);
    }

    private void WriteFsInfo(bool valid)
    {
        var fs = _image.AsSpan(BytesPerSector, BytesPerSector);
        if (!valid) return;
        BinaryPrimitives.WriteUInt32LittleEndian(fs, 0x41615252);
        BinaryPrimitives.WriteUInt32LittleEndian(fs[484..], 0x61417272);
        BinaryPrimitives.WriteUInt32LittleEndian(fs[488..], uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(fs[492..], uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(fs[508..], 0xAA550000);
    }

    private void SetFat(byte fat, uint cluster, uint value)
    {
        var offset = checked((int)((ReservedSectors + fat * FatSectors) * BytesPerSector + cluster * 4));
        BinaryPrimitives.WriteUInt32LittleEndian(_image.AsSpan(offset, 4), value);
    }

    private Span<byte> Cluster(uint cluster)
    {
        var sector = FirstDataSector + (cluster - 2) * SectorsPerCluster;
        return _image.AsSpan(checked((int)(sector * BytesPerSector)), BytesPerSector * SectorsPerCluster);
    }

    private void FillCluster(uint cluster, byte value) => Cluster(cluster).Fill(value);

    private static void WriteShort(Span<byte> directory, int slot, string basename, string extension, byte attributes, uint firstCluster, uint size, bool deleted, bool invalidDate = false) =>
        WriteShort(directory, slot, ShortBytes(basename, extension), attributes, firstCluster, size, deleted, invalidDate);

    private static void WriteShort(Span<byte> directory, int slot, byte[] name, byte attributes, uint firstCluster, uint size, bool deleted, bool invalidDate = false)
    {
        var entry = directory.Slice(slot * 32, 32);
        entry.Clear();
        name.CopyTo(entry);
        if (deleted) entry[0] = 0xE5;
        entry[11] = attributes;
        BinaryPrimitives.WriteUInt16LittleEndian(entry[16..], invalidDate ? (ushort)0x001F : EncodeDate(2025, 6, 15));
        BinaryPrimitives.WriteUInt16LittleEndian(entry[18..], EncodeDate(2025, 6, 15));
        BinaryPrimitives.WriteUInt16LittleEndian(entry[20..], (ushort)(firstCluster >> 16));
        BinaryPrimitives.WriteUInt16LittleEndian(entry[22..], EncodeTime(12, 30, 10));
        BinaryPrimitives.WriteUInt16LittleEndian(entry[24..], EncodeDate(2025, 6, 15));
        BinaryPrimitives.WriteUInt16LittleEndian(entry[26..], (ushort)firstCluster);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[28..], size);
    }

    private static void WriteLfn(Span<byte> directory, int slot, string name, byte checksum, bool deleted)
    {
        if (name.Length > 13) throw new ArgumentOutOfRangeException(nameof(name));
        var entry = directory.Slice(slot * 32, 32);
        entry.Fill(0xFF);
        entry[0] = deleted ? (byte)0xE5 : (byte)0x41;
        entry[11] = 0x0F; entry[12] = 0; entry[13] = checksum;
        BinaryPrimitives.WriteUInt16LittleEndian(entry[26..], 0);
        var positions = new[] { 1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30 };
        for (var i = 0; i < positions.Length; i++)
        {
            var value = i < name.Length ? name[i] : i == name.Length ? '\0' : '\uFFFF';
            BinaryPrimitives.WriteUInt16LittleEndian(entry[positions[i]..], value);
        }
    }

    private static byte[] ShortBytes(string basename, string extension)
    {
        var bytes = Enumerable.Repeat((byte)' ', 11).ToArray();
        Encoding.ASCII.GetBytes(basename.ToUpperInvariant()).AsSpan(0, Math.Min(8, basename.Length)).CopyTo(bytes);
        Encoding.ASCII.GetBytes(extension.ToUpperInvariant()).AsSpan(0, Math.Min(3, extension.Length)).CopyTo(bytes.AsSpan(8));
        return bytes;
    }

    private static ushort EncodeDate(int year, int month, int day) => (ushort)(((year - 1980) << 9) | (month << 5) | day);
    private static ushort EncodeTime(int hour, int minute, int second) => (ushort)((hour << 11) | (minute << 5) | (second / 2));
}
