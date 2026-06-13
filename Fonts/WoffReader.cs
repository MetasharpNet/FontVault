using System.Buffers.Binary;
using System.IO.Compression;

namespace FontVault.Fonts;

/// <summary>
/// WOFF 1.0 container: per-table zlib compression.
/// Metadata path decompresses only the useful tables; full rebuild for preview.
/// </summary>
public static class WoffReader
{
    public const uint Signature = 0x774F4646; // 'wOFF'

    private readonly record struct WoffTable(uint Tag, int Offset, int CompLength, int OrigLength);

    private static (uint Flavor, List<WoffTable> Tables) ReadDirectory(ReadOnlySpan<byte> data)
    {
        if (data.Length < 44 || BinaryPrimitives.ReadUInt32BigEndian(data) != Signature)
            throw new InvalidDataException("Invalid WOFF signature.");
        uint flavor = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        int numTables = BinaryPrimitives.ReadUInt16BigEndian(data[12..]);
        if (data.Length < 44 + numTables * 20)
            throw new InvalidDataException("Truncated WOFF directory.");

        var tables = new List<WoffTable>(numTables);
        for (int i = 0; i < numTables; i++)
        {
            int rec = 44 + i * 20;
            uint tag = BinaryPrimitives.ReadUInt32BigEndian(data[rec..]);
            long offset = BinaryPrimitives.ReadUInt32BigEndian(data[(rec + 4)..]);
            long compLength = BinaryPrimitives.ReadUInt32BigEndian(data[(rec + 8)..]);
            long origLength = BinaryPrimitives.ReadUInt32BigEndian(data[(rec + 12)..]);
            if (offset + compLength > data.Length || origLength > 256L * 1024 * 1024)
                throw new InvalidDataException($"WOFF table out of bounds (tag 0x{tag:X8}).");
            tables.Add(new WoffTable(tag, (int)offset, (int)compLength, (int)origLength));
        }
        return (flavor, tables);
    }

    private static byte[] DecompressTable(ReadOnlySpan<byte> data, WoffTable table)
    {
        var src = data.Slice(table.Offset, table.CompLength);
        if (table.CompLength == table.OrigLength) return src.ToArray(); // stored, not compressed
        using var input = new MemoryStream(src.ToArray());
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        var result = new byte[table.OrigLength];
        zlib.ReadExactly(result);
        return result;
    }

    /// <summary>Metadata extraction: decompresses only the tables needed by the parser.</summary>
    public static ParsedFont ExtractMetadata(ReadOnlySpan<byte> data)
    {
        var (flavor, tables) = ReadDirectory(data);
        var needed = new List<(uint, byte[])>();
        foreach (var table in tables)
            if (FontFileReader.IsMetadataTable(table.Tag))
                needed.Add((table.Tag, DecompressTable(data, table)));
        return SfntParser.Parse(SfntBuilder.Build(flavor, needed));
    }

    /// <summary>Full standalone sfnt rebuild (preview).</summary>
    public static byte[] Reconstruct(ReadOnlySpan<byte> data)
    {
        var (flavor, tables) = ReadDirectory(data);
        var all = new List<(uint, byte[])>(tables.Count);
        foreach (var table in tables)
            all.Add((table.Tag, DecompressTable(data, table)));
        return SfntBuilder.Build(flavor, all);
    }
}
