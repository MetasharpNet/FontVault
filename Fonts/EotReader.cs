using System.Buffers.Binary;

namespace FontVault.Fonts;

/// <summary>
/// EOT container: little-endian header followed by an embedded sfnt.
/// XOR obfuscation (TTEMBED_XORENCRYPTDATA) is handled directly;
/// MicroType Express compression (TTEMBED_TTCOMPRESSED) is decompressed
/// through the in-box t2embed.dll (see T2EmbedDecompressor).
/// </summary>
public static class EotReader
{
    private const ushort MagicNumber = 0x504C;
    private const uint FlagTtCompressed = 0x00000004;
    private const uint FlagXorEncrypted = 0x10000000;

    /// <summary>Extracts the embedded sfnt bytes (shared by metadata and preview paths).</summary>
    public static byte[] ExtractSfnt(ReadOnlySpan<byte> data)
    {
        if (data.Length < 40)
            throw new InvalidDataException("EOT header truncated.");
        uint fontDataSize = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(data[34..]);
        if (magic != MagicNumber)
            throw new InvalidDataException("Invalid EOT magic number.");
        if (fontDataSize == 0 || fontDataSize > (uint)data.Length)
            throw new InvalidDataException("Invalid EOT font data size.");

        // Font data sits at the end of the file (header + name strings are variable length).
        var font = data.Slice(data.Length - (int)fontDataSize, (int)fontDataSize).ToArray();
        if ((flags & FlagXorEncrypted) != 0)
        {
            for (int i = 0; i < font.Length; i++) font[i] ^= 0x50;
        }
        if ((flags & FlagTtCompressed) != 0)
        {
            string? headerFamily = ParseHeaderFamilyName(data);
            font = T2EmbedDecompressor.Decompress(font, headerFamily, out string? placeholder);
            // Placeholder path (name collision with an installed font): restore the original
            // family name inside the extracted name table.
            if (placeholder != null && headerFamily != null)
                font = ReplaceNamePlaceholder(font, placeholder, headerFamily);
        }
        return font;
    }

    /// <summary>Rewrites name-table strings containing <paramref name="placeholder"/> and reassembles the sfnt.</summary>
    private static byte[] ReplaceNamePlaceholder(byte[] sfnt, string placeholder, string family)
    {
        try
        {
            if (sfnt.Length < 12) return sfnt;
            var span = sfnt.AsSpan();
            uint flavor = BinaryPrimitives.ReadUInt32BigEndian(span);
            int numTables = BinaryPrimitives.ReadUInt16BigEndian(span[4..]);
            var tables = new List<(uint Tag, byte[] Data)>(numTables);
            int nameIndex = -1;
            for (int i = 0; i < numTables; i++)
            {
                int rec = 12 + i * 16;
                uint tag = BinaryPrimitives.ReadUInt32BigEndian(span[rec..]);
                int off = (int)BinaryPrimitives.ReadUInt32BigEndian(span[(rec + 8)..]);
                int len = (int)BinaryPrimitives.ReadUInt32BigEndian(span[(rec + 12)..]);
                if (off + len > sfnt.Length) return sfnt;
                tables.Add((tag, span.Slice(off, len).ToArray()));
                if (tag == 0x6E616D65) nameIndex = tables.Count - 1;
            }
            if (nameIndex < 0) return sfnt;

            var name = tables[nameIndex].Data;
            int count = BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(2));
            int stringOffset = BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(4));
            var records = new List<(ushort Platform, ushort Encoding, ushort Language, ushort Id, byte[] Value)>(count);
            for (int i = 0; i < count; i++)
            {
                int rec = 6 + i * 12;
                if (rec + 12 > name.Length) return sfnt;
                ushort platform = BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(rec));
                ushort encoding = BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(rec + 2));
                ushort language = BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(rec + 4));
                ushort nameId = BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(rec + 6));
                int length = BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(rec + 8));
                int offset = stringOffset + BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(rec + 10));
                if (offset + length > name.Length) return sfnt;
                var raw = name.AsSpan(offset, length).ToArray();
                bool wide = platform is 3 or 0;
                string text = wide
                    ? System.Text.Encoding.BigEndianUnicode.GetString(raw)
                    : System.Text.Encoding.Latin1.GetString(raw);
                if (text.Contains(placeholder, StringComparison.Ordinal))
                {
                    text = text.Replace(placeholder, family, StringComparison.Ordinal);
                    raw = wide
                        ? System.Text.Encoding.BigEndianUnicode.GetBytes(text)
                        : System.Text.Encoding.Latin1.GetBytes(text);
                }
                records.Add((platform, encoding, language, nameId, raw));
            }

            using var rebuilt = new MemoryStream();
            void WriteU16(int v) { rebuilt.WriteByte((byte)(v >> 8)); rebuilt.WriteByte((byte)v); }
            WriteU16(0);
            WriteU16(records.Count);
            WriteU16(6 + records.Count * 12);
            int running = 0;
            foreach (var r in records)
            {
                WriteU16(r.Platform); WriteU16(r.Encoding); WriteU16(r.Language); WriteU16(r.Id);
                WriteU16(r.Value.Length); WriteU16(running);
                running += r.Value.Length;
            }
            foreach (var r in records) rebuilt.Write(r.Value);

            tables[nameIndex] = (0x6E616D65, rebuilt.ToArray());
            return SfntBuilder.Build(flavor, tables);
        }
        catch
        {
            return sfnt; // cosmetic repair only: never fail the extraction over it
        }
    }

    /// <summary>FamilyName string from the EOT header (same fixed prefix in all EOT versions).</summary>
    private static string? ParseHeaderFamilyName(ReadOnlySpan<byte> data)
    {
        const int familyNameSizeOffset = 82;
        if (data.Length < familyNameSizeOffset + 2) return null;
        int size = BinaryPrimitives.ReadUInt16LittleEndian(data[familyNameSizeOffset..]);
        if (size <= 0 || (size & 1) != 0 || familyNameSizeOffset + 2 + size > data.Length) return null;
        var name = new char[size / 2];
        for (int i = 0; i < name.Length; i++)
            name[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(data[(familyNameSizeOffset + 2 + i * 2)..]);
        string result = new string(name).Trim('\0', ' ');
        return result.Length > 0 && result.Length <= 64 ? result : null;
    }

    public static ParsedFont ExtractMetadata(ReadOnlySpan<byte> data) =>
        SfntParser.Parse(ExtractSfnt(data));

    public static byte[] Reconstruct(ReadOnlySpan<byte> data) => ExtractSfnt(data);
}
