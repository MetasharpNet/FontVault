using System.Buffers.Binary;

namespace FontVault.Fonts;

/// <summary>
/// Assembles a standalone sfnt (OTF/TTF) from raw tables: directory, 4-byte alignment,
/// per-table checksums and head.checkSumAdjustment recomputation.
/// Used to rebuild previewable fonts from WOFF/WOFF2/EOT containers.
/// </summary>
public static class SfntBuilder
{
    private const uint TagHead = 0x68656164;

    public static byte[] Build(uint flavor, List<(uint Tag, byte[] Data)> tables)
    {
        tables.Sort((a, b) => a.Tag.CompareTo(b.Tag));
        int numTables = tables.Count;

        int entrySelector = 0;
        while ((2 << entrySelector) <= numTables) entrySelector++;
        entrySelector--;
        if (entrySelector < 0) entrySelector = 0;
        int searchRange = (1 << entrySelector) * 16;
        int rangeShift = numTables * 16 - searchRange;

        int directoryEnd = 12 + numTables * 16;
        long totalSize = directoryEnd;
        foreach (var (_, data) in tables)
            totalSize += (data.Length + 3) & ~3;
        var output = new byte[totalSize];
        var span = output.AsSpan();

        BinaryPrimitives.WriteUInt32BigEndian(span, flavor);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], (ushort)numTables);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..], (ushort)rangeShift);

        int offset = directoryEnd;
        int headOffset = -1;
        for (int i = 0; i < numTables; i++)
        {
            var (tag, data) = tables[i];
            data.CopyTo(output, offset);
            if (tag == TagHead && data.Length >= 12)
            {
                headOffset = offset;
                // checkSumAdjustment zeroed before any checksum computation.
                span.Slice(offset + 8, 4).Clear();
            }
            int rec = 12 + i * 16;
            BinaryPrimitives.WriteUInt32BigEndian(span[rec..], tag);
            BinaryPrimitives.WriteUInt32BigEndian(span[(rec + 4)..], TableChecksum(span, offset, data.Length));
            BinaryPrimitives.WriteUInt32BigEndian(span[(rec + 8)..], (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(span[(rec + 12)..], (uint)data.Length);
            offset += (data.Length + 3) & ~3;
        }

        if (headOffset >= 0)
        {
            uint fontChecksum = TableChecksum(span, 0, output.Length);
            BinaryPrimitives.WriteUInt32BigEndian(span[(headOffset + 8)..], 0xB1B0AFBAu - fontChecksum);
        }
        return output;
    }

    /// <summary>Sum of big-endian uint32 over the (zero-padded) range.</summary>
    private static uint TableChecksum(ReadOnlySpan<byte> data, int offset, int length)
    {
        uint sum = 0;
        int alignedEnd = offset + (length & ~3);
        for (int i = offset; i < alignedEnd; i += 4)
            sum += BinaryPrimitives.ReadUInt32BigEndian(data.Slice(i, 4));
        int rest = length & 3;
        if (rest > 0)
        {
            uint last = 0;
            for (int i = 0; i < 4; i++)
            {
                last <<= 8;
                if (i < rest) last |= data[alignedEnd + i];
            }
            sum += last;
        }
        return sum;
    }
}
