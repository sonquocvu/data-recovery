using System.Buffers.Binary;
using System.Text;

namespace DataRecoveryStudio.Tests;

/// <summary>Independent deterministic metadata fixture writer. Never targets devices or production sources.</summary>
internal sealed class ExFatTestFixtureBuilder
{
    internal readonly byte[] Bytes;
    internal int SectorSize { get; }
    internal int ClusterSize { get; }
    internal uint ClusterCount { get; }
    internal const uint FatSector = 24;
    internal uint HeapSector { get; }
    internal readonly List<(long Offset, long Length)> PayloadRanges = [];
    private readonly Dictionary<uint, int> _slots = [];
    private readonly Dictionary<uint, uint[]> _directoryChains = [];
    internal ExFatTestFixtureBuilder(int sectorSize = 512, int sectorsPerCluster = 1)
    {
        SectorSize = sectorSize;
        ClusterSize = sectorSize * sectorsPerCluster;
        HeapSector = (uint)(128 * 1024 / sectorSize);
        ClusterCount = (uint)(1024 * 1024 / ClusterSize);
        Bytes = new byte[HeapSector * sectorSize + ClusterCount * ClusterSize];
        var b = Bytes.AsSpan(0, sectorSize * 12);
        b[0] = 0xEB; b[1] = 0x76; b[2] = 0x90;
        Encoding.ASCII.GetBytes("EXFAT   ").CopyTo(b[3..]);
        W64(72, (ulong)(Bytes.Length / sectorSize));
        W32(80, FatSector);
        W32(84, (uint)(((ClusterCount + 2) * 4 + sectorSize - 1) / sectorSize));
        W32(88, HeapSector); W32(92, ClusterCount); W32(96, 2); W32(100, 0x8A112233);
        W16(104, 0x100);
        b[108] = (byte)System.Numerics.BitOperations.Log2((uint)sectorSize);
        b[109] = (byte)System.Numerics.BitOperations.Log2((uint)sectorsPerCluster);
        b[110] = 1; b[111] = 0x80; b[112] = 255;
        W16(510, 0xAA55);
        for (var i = 1; i <= 8; i++) W32((i + 1) * sectorSize - 4, 0xAA550000);
        RechecksumBoot(false);
        Bytes.AsSpan(0, sectorSize * 12).CopyTo(Bytes.AsSpan(sectorSize * 12));
        W32((int)FatSector * sectorSize, 0xFFFFFFF8);
        W32((int)FatSector * sectorSize + 4, 0xFFFFFFFF);
        DirectoryChain(2, 2, 5, 6, 7);
        SetFat(3, 0xFFFFFFFF); SetFat(4, 0xFFFFFFFF);
        SetAllocated(3); SetAllocated(4);
        var bitmap = new byte[32]; bitmap[0] = 0x81;
        Put32(bitmap, 20, 3); Put64(bitmap, 24, (ClusterCount + 7UL) / 8);
        AddRaw(2, bitmap);
        // Full Unicode coverage using ASCII folding and an identity run through U+FFFE.
        var upcase = new List<ushort>();
        for (var i = 0; i < 128; i++) upcase.Add((ushort)(i is >= 97 and <= 122 ? i - 32 : i));
        upcase.Add(0xFFFF); upcase.Add(65407); upcase.Add(0xFFFF);
        var table = new byte[upcase.Count * 2];
        for (var i = 0; i < upcase.Count; i++) Put16(table, i * 2, upcase[i]);
        table.CopyTo(Bytes, Offset(4));
        var entry = new byte[32]; entry[0] = 0x82;
        Put32(entry, 4, Sum32(table)); Put32(entry, 20, 4); Put64(entry, 24, (ulong)table.Length);
        AddRaw(2, entry);
        var label = new byte[32]; label[0] = 0x83; label[1] = 7;
        Encoding.Unicode.GetBytes("PHASE8A").CopyTo(label, 2);
        AddRaw(2, label);
    }
    internal int Offset(uint cluster) => checked((int)(HeapSector * SectorSize + (cluster - 2) * ClusterSize));
    internal void WritePayload(IReadOnlyList<uint> chain, ReadOnlySpan<byte> initialized, byte padding = 0xE7)
    {
        if (initialized.Length > (long)chain.Count * ClusterSize) throw new ArgumentOutOfRangeException(nameof(initialized));
        foreach (var cluster in chain)
        {
            var block = Bytes.AsSpan(Offset(cluster), ClusterSize);
            block.Fill(padding);
            var count = Math.Min(initialized.Length, ClusterSize);
            initialized[..count].CopyTo(block);
            initialized = initialized[count..];
            if (!PayloadRanges.Contains((Offset(cluster), ClusterSize))) PayloadRanges.Add((Offset(cluster), ClusterSize));
        }
    }
    internal int RootSlot(int slot) => SlotOffset(2, slot);
    internal int SlotOffset(uint directory, int slot)
    {
        var chain = _directoryChains.GetValueOrDefault(directory) ?? [directory];
        return Offset(chain[slot * 32 / ClusterSize]) + slot * 32 % ClusterSize;
    }
    internal void DirectoryChain(uint directory, params uint[] chain)
    {
        _directoryChains[directory] = chain;
        for (var i = 0; i < chain.Length; i++)
        {
            SetFat(chain[i], i == chain.Length - 1 ? 0xFFFFFFFF : chain[i + 1]);
            SetAllocated(chain[i]);
        }
    }
    internal int AddRaw(uint directory, byte[] bytes)
    {
        var slot = _slots.GetValueOrDefault(directory);
        var offset = SlotOffset(directory, slot);
        for (var i = 0; i < bytes.Length / 32; i++) bytes.AsSpan(i * 32, 32).CopyTo(Bytes.AsSpan(SlotOffset(directory, slot + i), 32));
        _slots[directory] = slot + bytes.Length / 32;
        return offset;
    }
    internal void PadToSlot(uint directory, int slot)
    {
        while (_slots.GetValueOrDefault(directory) < slot) { var b = new byte[32]; b[0] = 1; AddRaw(directory, b); }
    }
    internal int AddFile(string name, uint first, long size, bool deleted = true, bool contiguous = true,
        bool directory = false, uint parent = 2, long? valid = null, bool wrongHash = false, bool badChecksum = false)
    {
        var bytes = FileSet(name, first, size, deleted, contiguous, directory, valid ?? size, wrongHash, badChecksum);
        if (!directory && size > 0 && contiguous) PayloadRanges.Add((Offset(first), ((size + ClusterSize - 1) / ClusterSize) * ClusterSize));
        return AddRaw(parent, bytes);
    }
    internal static byte[] FileSet(string name, uint first, long size, bool deleted, bool contiguous, bool directory,
        long valid, bool wrongHash = false, bool badChecksum = false)
    {
        var slots = (name.Length + 14) / 15;
        var bytes = new byte[(slots + 2) * 32];
        bytes[0] = 0x85; bytes[1] = (byte)(slots + 1); Put16(bytes, 4, (ushort)(directory ? 16 : 32));
        uint timestamp = (uint)((2026 - 1980) << 25 | 9 << 21 | 5 << 16 | 10 << 11 | 20 << 5 | 15);
        Put32(bytes, 8, timestamp); Put32(bytes, 12, timestamp); Put32(bytes, 16, timestamp);
        bytes[20] = 123; bytes[21] = 99; bytes[22] = bytes[23] = bytes[24] = 0x9C;
        bytes[32] = 0xC0; bytes[33] = (byte)(size == 0 || !contiguous ? 1 : 3); bytes[35] = (byte)name.Length;
        Put16(bytes, 36, (ushort)(NameHash(name) ^ (wrongHash ? 1 : 0)));
        Put64(bytes, 40, (ulong)valid); Put32(bytes, 52, first); Put64(bytes, 56, (ulong)size);
        var text = Encoding.Unicode.GetBytes(name);
        for (var i = 0; i < slots; i++)
        {
            bytes[(i + 2) * 32] = 0xC1;
            text.AsSpan(i * 30, Math.Min(30, text.Length - i * 30)).CopyTo(bytes.AsSpan((i + 2) * 32 + 2));
        }
        Put16(bytes, 2, (ushort)(Sum16(bytes) ^ (badChecksum ? 1 : 0)));
        if (deleted) for (var i = 0; i < bytes.Length; i += 32) bytes[i] &= 0x7F;
        return bytes;
    }
    internal void SetFat(uint cluster, uint value) => W32((int)FatSector * SectorSize + (int)cluster * 4, value);
    internal void SetAllocated(uint cluster, bool allocated = true)
    {
        var offset = Offset(3) + (int)(cluster - 2) / 8;
        var mask = (byte)(1 << (int)((cluster - 2) % 8));
        if (allocated) Bytes[offset] |= mask; else Bytes[offset] &= (byte)~mask;
    }
    internal void RechecksumBoot(bool backup)
    {
        var offset = backup ? SectorSize * 12 : 0;
        uint sum = 0;
        for (var i = 0; i < SectorSize * 11; i++)
            if (i is not (106 or 107 or 112)) sum = unchecked((sum >> 1 | sum << 31) + Bytes[offset + i]);
        for (var i = SectorSize * 11; i < SectorSize * 12; i += 4) W32(offset + i, sum);
    }
    internal void W16(int offset, ushort value) => Put16(Bytes, offset, value);
    internal void W32(int offset, uint value) => Put32(Bytes, offset, value);
    internal void W64(int offset, ulong value) => Put64(Bytes, offset, value);
    internal static void Put16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    internal static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    internal static void Put64(byte[] bytes, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);
    internal static uint Sum32(byte[] bytes)
    {
        uint sum = 0;
        foreach (var value in bytes) sum = unchecked((sum >> 1 | sum << 31) + value);
        return sum;
    }
    internal static ushort Sum16(byte[] bytes)
    {
        ushort sum = 0;
        for (var i = 0; i < bytes.Length; i++) if (i is not (2 or 3)) sum = unchecked((ushort)((sum >> 1 | sum << 15) + bytes[i]));
        return sum;
    }
    internal static ushort NameHash(string name)
    {
        ushort sum = 0;
        foreach (var ch in name)
        {
            var value = ch is >= 'a' and <= 'z' ? ch - 32 : ch;
            sum = unchecked((ushort)((sum >> 1 | sum << 15) + (byte)value));
            sum = unchecked((ushort)((sum >> 1 | sum << 15) + (byte)(value >> 8)));
        }
        return sum;
    }
}
