using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DataRecoveryStudio.Core;

namespace DataRecoveryStudio.Infrastructure;

internal static class ExFatStructures
{
    internal static ushort U16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    internal static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    internal static ulong U64(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]);
    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static uint Rotate32(uint sum, byte value) => unchecked((sum >> 1 | sum << 31) + value);
    internal static ushort Rotate16(ushort sum, byte value) => unchecked((ushort)((sum >> 1 | sum << 15) + value));
    internal static uint Checksum32(ReadOnlySpan<byte> bytes, bool boot = false)
    {
        uint sum = 0;
        for (var i = 0; i < bytes.Length; i++)
            if (!boot || i is not (106 or 107 or 112)) sum = Rotate32(sum, bytes[i]);
        return sum;
    }
    internal static ushort SetChecksum(ReadOnlySpan<byte> bytes, bool restoreInUse)
    {
        ushort sum = 0;
        for (var i = 0; i < bytes.Length; i++)
            if (i is not (2 or 3)) sum = Rotate16(sum, restoreInUse && i % 32 == 0 ? (byte)(bytes[i] | 0x80) : bytes[i]);
        return sum;
    }
    internal static ushort NameHash(string name, ushort[] upcase, CancellationToken token)
    {
        ushort hash = 0;
        foreach (var ch in name)
        {
            token.ThrowIfCancellationRequested();
            var upper = upcase[ch];
            hash = Rotate16(Rotate16(hash, (byte)upper), (byte)(upper >> 8));
        }
        return hash;
    }
    internal static string DecodeName(ReadOnlySpan<byte> bytes)
    {
        var name = new UnicodeEncoding(false, false, true).GetString(bytes);
        if (name.Any(ch => ch < 32 || "\"*/:<>?\\|".Contains(ch)) || name is "." or "..")
            throw new InvalidDataException("NAME_INVALID");
        return name;
    }
    internal static string Display(string name) => string.Concat(name.Select(ch =>
        char.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.Format ? '\uFFFD' : ch));

    internal static ExFatGeometry ParseBoot(byte[] region, int sector, long volumeOffset, long sourceLength)
    {
        var b = region.AsSpan();
        if (!b[..3].SequenceEqual(new byte[] { 0xEB, 0x76, 0x90 }) || !b.Slice(3, 8).SequenceEqual("EXFAT   "u8))
            throw new InvalidDataException("BOOT_IDENTITY");
        if (b.Slice(11, 53).IndexOfAnyExcept((byte)0) >= 0 || b.Slice(113, 7).IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("BOOT_REQUIRED_ZERO");
        if (b[108] is < 9 or > 12 || (1 << b[108]) != sector || b[109] > 25 - b[108])
            throw new InvalidDataException("BOOT_SHIFTS");
        if (U16(b, 104) != 0x100) throw new InvalidDataException("BOOT_REVISION");
        if (b[110] != 1) throw new InvalidDataException("UNSUPPORTED_FAT_COUNT_TEXFAT");
        if ((U16(b, 106) & 0xFFF1) != 0) throw new InvalidDataException("BOOT_FLAGS");
        if (U16(b, 510) != 0xAA55) throw new InvalidDataException("BOOT_SIGNATURE");
        for (var i = 1; i <= 8; i++)
            if (U32(b, (i + 1) * sector - 4) != 0xAA550000) throw new InvalidDataException("EXTENDED_BOOT_SIGNATURE");
        // OEM parameter records are opaque identifiers/parameters. Their reserved tail and sector 10 are zero.
        if (b.Slice(9 * sector + 480, sector - 480).IndexOfAnyExcept((byte)0) >= 0 ||
            b.Slice(10 * sector, sector).IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("BOOT_RESERVED");
        var sum = Checksum32(b[..(11 * sector)], true);
        for (var i = 11 * sector; i < 12 * sector; i += 4)
            if (U32(b, i) != sum) throw new InvalidDataException("BOOT_CHECKSUM");
        var volume = U64(b, 72);
        var fat = U32(b, 80);
        var fatLength = U32(b, 84);
        var heap = U32(b, 88);
        var clusters = U32(b, 92);
        var root = U32(b, 96);
        var spc = 1 << b[109];
        checked
        {
            if (volume < (ulong)(1024 * 1024 / sector) || clusters == 0 || clusters > 0xFFFFFFF5 ||
                fat < 24 || (ulong)fatLength * (uint)sector < ((ulong)clusters + 2) * 4 ||
                (ulong)fat + fatLength > heap || (ulong)heap + (ulong)clusters * (uint)spc > volume ||
                clusters != Math.Min((volume - heap) / (uint)spc, 0xFFFFFFF5UL) ||
                root < 2 || root > (ulong)clusters + 1 ||
                (long)(volume * (uint)sector) > sourceLength - volumeOffset)
                throw new InvalidDataException("BOOT_GEOMETRY");
            // PartitionOffset is media-relative metadata, not a second offset to add to an extracted image.
            var critical = b.Slice(64, 36).ToArray().Concat(new[] { b[104], b[105], b[108], b[109], b[110] }).ToArray();
            return new(sector, spc, sector * spc, U64(b, 64), volume, fat, fatLength, heap, clusters,
                root, U32(b, 100), U16(b, 104), U16(b, 106), b[110], b[111], b[112], Hash(critical));
        }
    }

    internal static ushort[] ExpandUpCase(byte[] bytes, uint expectedChecksum, CancellationToken token)
    {
        if (bytes.Length == 0 || bytes.Length % 2 != 0 || Checksum32(bytes) != expectedChecksum)
            throw new InvalidDataException("UPCASE_CHECKSUM_LENGTH");
        var table = new ushort[65536];
        var index = 0;
        for (var offset = 0; offset < bytes.Length; offset += 2)
        {
            token.ThrowIfCancellationRequested();
            if (index == table.Length) throw new InvalidDataException("UPCASE_EXPANSION");
            var value = U16(bytes, offset);
            if (value == 0xFFFF && index != 65535 && bytes.Length != 131072)
            {
                offset += 2;
                if (offset >= bytes.Length) throw new InvalidDataException("UPCASE_EXPANSION");
                var count = U16(bytes, offset);
                if (count == 0 || count > table.Length - index) throw new InvalidDataException("UPCASE_EXPANSION");
                for (var n = 0; n < count; n++) table[index] = (ushort)index++;
            }
            else table[index++] = value;
        }
        if (index != table.Length) throw new InvalidDataException("UPCASE_COVERAGE");
        for (var i = 0; i < 128; i++)
            if (table[i] != (i is >= 97 and <= 122 ? i - 32 : i)) throw new InvalidDataException("UPCASE_ASCII");
        return table;
    }

    internal static ExFatTimestamp Timestamp(uint raw, byte increment, byte utc)
    {
        try
        {
            if (increment > 199 || (raw & 31) > 29 || (utc < 128 && utc != 0)) return new(null, null, ExFatTimestampState.Invalid);
            var date = new DateTime(1980 + (int)(raw >> 25), (int)(raw >> 21 & 15), (int)(raw >> 16 & 31),
                (int)(raw >> 11 & 31), (int)(raw >> 5 & 63), (int)(raw & 31) * 2, DateTimeKind.Unspecified).AddMilliseconds(increment * 10);
            int? minutes = utc >= 128 ? ((utc & 64) != 0 ? (utc & 127) - 128 : utc & 127) * 15 : null;
            return new(date, minutes, minutes.HasValue ? ExFatTimestampState.Valid : ExFatTimestampState.OffsetUnknown);
        }
        catch (ArgumentOutOfRangeException) { return new(null, null, ExFatTimestampState.Invalid); }
    }

    internal static ExFatEntrySet ParseSet(byte[] bytes, ExFatScanBudget budget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var deleted = bytes[0] == 0x05;
        var mask = deleted ? 0 : 0x80;
        if (bytes[0] != (0x05 | mask) || bytes[1] < 2 || bytes[1] > budget.MaximumSecondaryCount || bytes.Length != (bytes[1] + 1) * 32)
            throw new InvalidDataException("SECONDARY_COUNT");
        var s = bytes.AsSpan(32, 32);
        if (s[0] != (0x40 | mask) || (s[1] & 0xFD) != 1) throw new InvalidDataException("STREAM_EXTENSION");
        var nameLength = s[3];
        if (nameLength == 0 || nameLength > budget.MaximumFilenameLength) throw new InvalidDataException("NAME_LENGTH");
        var slots = (nameLength + 14) / 15;
        if (bytes[1] < slots + 1) throw new InvalidDataException("FILENAME_ENTRIES");
        var nameBytes = new byte[slots * 30];
        for (var i = 0; i < slots; i++)
        {
            token.ThrowIfCancellationRequested();
            var entry = bytes.AsSpan((i + 2) * 32, 32);
            if (entry[0] != (0x41 | mask) || entry[1] != 0) throw new InvalidDataException("FILENAME_ENTRIES");
            entry[2..].CopyTo(nameBytes.AsSpan(i * 30));
        }
        if (nameBytes.AsSpan(nameLength * 2).IndexOfAnyExcept((byte)0) >= 0) throw new InvalidDataException("EXTRA_FILENAME_CONTENT");
        for (var i = slots + 2; i < bytes.Length / 32; i++)
        {
            var entry = bytes.AsSpan(i * 32, 32);
            if ((entry[0] & 0xE0) != (0x60 | mask)) throw new InvalidDataException("UNKNOWN_CRITICAL_SECONDARY");
            // Unknown benign secondaries may describe additional allocations. They cannot authorize a stream.
            if ((entry[1] & 3) != 0) throw new InvalidDataException("UNSUPPORTED_BENIGN_STREAM");
        }
        var name = DecodeName(nameBytes.AsSpan(0, nameLength * 2));
        var ordinary = SetChecksum(bytes, false) == U16(bytes, 2);
        var recovered = deleted && SetChecksum(bytes, true) == U16(bytes, 2);
        if (!deleted && !ordinary) throw new InvalidDataException("ACTIVE_SET_CHECKSUM");
        var size = checked((long)U64(s, 24));
        var valid = checked((long)U64(s, 8));
        var first = U32(s, 20);
        var attr = U16(bytes, 4);
        if ((attr & ~0x37) != 0 || valid > size || (size == 0 && (first != 0 || (s[1] & 2) != 0)) || (size > 0 && first < 2))
            throw new InvalidDataException("STREAM_LENGTH_ATTRIBUTES");
        return new(deleted, (attr & 16) != 0, name, U16(s, 4), s[1], first, size, valid, attr, ordinary, recovered,
            Timestamp(U32(bytes, 8), bytes[20], bytes[22]), Timestamp(U32(bytes, 12), bytes[21], bytes[23]),
            Timestamp(U32(bytes, 16), 0, bytes[24]), Hash(bytes));
    }
}

internal sealed record ExFatEntrySet(bool Deleted, bool Directory, string Name, ushort NameHash, byte Flags,
    uint FirstCluster, long Size, long ValidLength, ushort Attributes, bool OrdinaryChecksum, bool RecoveredChecksum,
    ExFatTimestamp Created, ExFatTimestamp Modified, ExFatTimestamp Accessed, string Fingerprint)
{
    internal bool Contiguous => (Flags & 2) != 0;
}
