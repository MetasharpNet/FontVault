using System.Buffers.Binary;
using System.IO.Compression;

namespace FontVault.Fonts;

/// <summary>
/// WOFF 2.0 container: single Brotli stream, glyf/loca (and optionally hmtx) stored transformed.
/// Metadata path uses the raw (never transformed) tables; preview path performs the full
/// glyf/loca/hmtx reconstruction per the W3C WOFF2 specification.
/// </summary>
public static class Woff2Reader
{
    public const uint Signature = 0x774F4632; // 'wOF2'

    private const uint TagGlyf = 0x676C7966;
    private const uint TagLoca = 0x6C6F6361;
    private const uint TagHmtx = 0x686D7478;
    private const uint TagHhea = 0x68686561;
    private const uint TagHead = 0x68656164;
    private const uint TagMaxp = 0x6D617870;

    // Known table tags, indexed by the 6-bit value in the directory flags byte (63 = arbitrary tag follows).
    private static readonly uint[] KnownTags = BuildKnownTags();

    private static uint[] BuildKnownTags()
    {
        string[] tags =
        {
            "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post",
            "cvt ", "fpgm", "glyf", "loca", "prep", "CFF ", "VORG", "EBDT",
            "EBLC", "gasp", "hdmx", "kern", "LTSH", "PCLT", "VDMX", "vhea",
            "vmtx", "BASE", "GDEF", "GPOS", "GSUB", "EBSC", "JSTF", "MATH",
            "CBDT", "CBLC", "COLR", "CPAL", "SVG ", "sbix", "acnt", "avar",
            "bdat", "bloc", "bsln", "cvar", "fdsc", "feat", "fmtx", "fvar",
            "gvar", "hsty", "just", "lcar", "mort", "morx", "opbd", "prop",
            "trak", "Zapf", "Silf", "Glat", "Gloc", "Feat", "Sill",
        };
        var result = new uint[63];
        for (int i = 0; i < tags.Length; i++)
            result[i] = ((uint)tags[i][0] << 24) | ((uint)tags[i][1] << 16) | ((uint)tags[i][2] << 8) | tags[i][3];
        return result;
    }

    private sealed class Woff2Table
    {
        public uint Tag;
        public int TransformVersion;
        public int OrigLength;
        public int StoredLength;
        public byte[] Data = Array.Empty<byte>();
        public bool IsTransformed;
    }

    private static (uint Flavor, List<Woff2Table> Tables) Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 48 || BinaryPrimitives.ReadUInt32BigEndian(data) != Signature)
            throw new InvalidDataException("Invalid WOFF2 signature.");
        uint flavor = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        if (flavor == 0x74746366)
            throw new InvalidDataException("WOFF2 TTC collection not supported.");
        int numTables = BinaryPrimitives.ReadUInt16BigEndian(data[12..]);
        long totalCompressedSize = BinaryPrimitives.ReadUInt32BigEndian(data[20..]);

        int pos = 48;
        var tables = new List<Woff2Table>(numTables);
        long totalStored = 0;
        for (int i = 0; i < numTables; i++)
        {
            byte flags = data[pos++];
            int tagIndex = flags & 0x3F;
            uint tag;
            if (tagIndex == 0x3F)
            {
                tag = BinaryPrimitives.ReadUInt32BigEndian(data[pos..]);
                pos += 4;
            }
            else
            {
                tag = KnownTags[tagIndex];
            }
            int transformVersion = (flags >> 6) & 0x03;
            int origLength = (int)ReadBase128(data, ref pos);

            // glyf/loca: version 0 = transformed, 3 = null. Other tables: version 0 = null, non-zero = transformed.
            bool isTransformed = tag is TagGlyf or TagLoca ? transformVersion == 0 : transformVersion != 0;
            int storedLength = origLength;
            if (isTransformed)
                storedLength = (int)ReadBase128(data, ref pos);

            if (origLength > 512 * 1024 * 1024 || storedLength > 512 * 1024 * 1024)
                throw new InvalidDataException("WOFF2 table too large.");
            totalStored += storedLength;
            tables.Add(new Woff2Table
            {
                Tag = tag,
                TransformVersion = transformVersion,
                OrigLength = origLength,
                StoredLength = storedLength,
                IsTransformed = isTransformed,
            });
        }

        if (pos + totalCompressedSize > data.Length)
            throw new InvalidDataException("Truncated WOFF2 compressed stream.");

        // Single Brotli stream holding all stored tables back to back.
        var decompressed = new byte[totalStored];
        using (var input = new MemoryStream(data.Slice(pos, (int)totalCompressedSize).ToArray()))
        using (var brotli = new BrotliStream(input, CompressionMode.Decompress))
        {
            brotli.ReadExactly(decompressed);
        }

        int streamOffset = 0;
        foreach (var table in tables)
        {
            table.Data = decompressed.AsSpan(streamOffset, table.StoredLength).ToArray();
            streamOffset += table.StoredLength;
        }
        return (flavor, tables);
    }

    /// <summary>Metadata extraction: the tables needed by the parser are never transformed.</summary>
    public static ParsedFont ExtractMetadata(ReadOnlySpan<byte> data)
    {
        var (flavor, tables) = Decode(data);
        var needed = new List<(uint, byte[])>();
        foreach (var table in tables)
        {
            if (!FontFileReader.IsMetadataTable(table.Tag)) continue;
            if (table.IsTransformed)
                throw new InvalidDataException($"Unexpected transformed metadata table 0x{table.Tag:X8}.");
            needed.Add((table.Tag, table.Data));
        }
        return SfntParser.Parse(SfntBuilder.Build(flavor, needed));
    }

    /// <summary>Full standalone sfnt rebuild, including glyf/loca/hmtx reconstruction (preview).</summary>
    public static byte[] Reconstruct(ReadOnlySpan<byte> data)
    {
        var (flavor, tables) = Decode(data);
        var byTag = new Dictionary<uint, Woff2Table>();
        foreach (var t in tables) byTag.TryAdd(t.Tag, t);

        byte[]? glyf = null, loca = null;
        short[]? xMins = null;
        int indexFormat = -1;

        if (byTag.TryGetValue(TagGlyf, out var glyfTable) && glyfTable.IsTransformed)
        {
            (glyf, loca, xMins, indexFormat) = ReconstructGlyf(glyfTable.Data);
        }

        var output = new List<(uint, byte[])>(tables.Count);
        foreach (var table in tables)
        {
            byte[] content;
            if (table.Tag == TagGlyf && glyf != null) content = glyf;
            else if (table.Tag == TagLoca && loca != null) content = loca;
            else if (table.Tag == TagHmtx && table.IsTransformed)
                content = ReconstructHmtx(table.Data, byTag, glyf, loca, ref xMins);
            else if (table.IsTransformed)
                throw new InvalidDataException($"Unsupported WOFF2 transform on table 0x{table.Tag:X8}.");
            else content = table.Data;

            // head: keep indexToLocFormat consistent with the reconstructed loca.
            if (table.Tag == TagHead && indexFormat >= 0 && content.Length >= 52)
            {
                content = (byte[])content.Clone();
                BinaryPrimitives.WriteInt16BigEndian(content.AsSpan(50), (short)indexFormat);
            }
            output.Add((table.Tag, content));
        }
        return SfntBuilder.Build(flavor, output);
    }

    // ---- Transformed glyf reconstruction (W3C WOFF2 §5.1) ----

    private static (byte[] Glyf, byte[] Loca, short[] XMins, int IndexFormat) ReconstructGlyf(ReadOnlySpan<byte> t)
    {
        // Header: reserved (2) + optionFlags (2) + numGlyphs (2) + indexFormat (2) + 7 stream sizes = 36 bytes.
        if (t.Length < 36) throw new InvalidDataException("Transformed glyf header truncated.");
        int numGlyphs = BinaryPrimitives.ReadUInt16BigEndian(t[4..]);
        int indexFormat = BinaryPrimitives.ReadUInt16BigEndian(t[6..]);
        int nContourSize = (int)BinaryPrimitives.ReadUInt32BigEndian(t[8..]);
        int nPointsSize = (int)BinaryPrimitives.ReadUInt32BigEndian(t[12..]);
        int flagSize = (int)BinaryPrimitives.ReadUInt32BigEndian(t[16..]);
        int glyphSize = (int)BinaryPrimitives.ReadUInt32BigEndian(t[20..]);
        int compositeSize = (int)BinaryPrimitives.ReadUInt32BigEndian(t[24..]);
        int bboxSize = (int)BinaryPrimitives.ReadUInt32BigEndian(t[28..]);
        int instructionSize = (int)BinaryPrimitives.ReadUInt32BigEndian(t[32..]);

        int cursor = 36;
        var nContourStream = Slice(t, ref cursor, nContourSize);
        var nPointsStream = Slice(t, ref cursor, nPointsSize);
        var flagStream = Slice(t, ref cursor, flagSize);
        var glyphStream = Slice(t, ref cursor, glyphSize);
        var compositeStream = Slice(t, ref cursor, compositeSize);
        var bboxStream = Slice(t, ref cursor, bboxSize);
        var instructionStream = Slice(t, ref cursor, instructionSize);

        int bitmapLength = ((numGlyphs + 31) >> 5) << 2;
        if (bboxStream.Length < bitmapLength) throw new InvalidDataException("bbox bitmap truncated.");
        var bboxBitmap = bboxStream[..bitmapLength];
        int bboxPos = bitmapLength;

        int nPointsPos = 0, flagPos = 0, glyphPos = 0, compositePos = 0, instructionPos = 0;
        var glyfOut = new MemoryStream();
        var locaOffsets = new uint[numGlyphs + 1];
        var xMins = new short[numGlyphs];

        for (int gid = 0; gid < numGlyphs; gid++)
        {
            locaOffsets[gid] = (uint)glyfOut.Length;
            short nContours = BinaryPrimitives.ReadInt16BigEndian(nContourStream[(gid * 2)..]);
            bool hasBbox = (bboxBitmap[gid >> 3] & (0x80 >> (gid & 7))) != 0;

            if (nContours == 0)
            {
                if (hasBbox) throw new InvalidDataException("Empty glyph with explicit bbox.");
                continue;
            }

            short xMin, yMin, xMax, yMax;
            if (nContours > 0)
            {
                // Simple glyph: contours, flags, coordinate triplets.
                var endPts = new ushort[nContours];
                int totalPoints = 0;
                for (int c = 0; c < nContours; c++)
                {
                    totalPoints += Read255(nPointsStream, ref nPointsPos);
                    if (totalPoints > 0xFFFF) throw new InvalidDataException("Too many points in glyph.");
                    endPts[c] = (ushort)(totalPoints - 1);
                }
                var dx = new int[totalPoints];
                var dy = new int[totalPoints];
                var onCurve = new bool[totalPoints];
                int x = 0, y = 0;
                int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
                for (int p = 0; p < totalPoints; p++)
                {
                    byte flag = flagStream[flagPos++];
                    onCurve[p] = (flag >> 7) == 0;
                    DecodeTriplet(flag & 0x7F, glyphStream, ref glyphPos, out dx[p], out dy[p]);
                    x += dx[p];
                    y += dy[p];
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
                if (totalPoints == 0) { minX = minY = maxX = maxY = 0; }

                int instructionLength = Read255(glyphStream, ref glyphPos);
                if (instructionPos + instructionLength > instructionStream.Length)
                    throw new InvalidDataException("Instruction stream overrun.");

                if (hasBbox)
                {
                    xMin = BinaryPrimitives.ReadInt16BigEndian(bboxStream[bboxPos..]);
                    yMin = BinaryPrimitives.ReadInt16BigEndian(bboxStream[(bboxPos + 2)..]);
                    xMax = BinaryPrimitives.ReadInt16BigEndian(bboxStream[(bboxPos + 4)..]);
                    yMax = BinaryPrimitives.ReadInt16BigEndian(bboxStream[(bboxPos + 6)..]);
                    bboxPos += 8;
                }
                else
                {
                    xMin = (short)minX; yMin = (short)minY; xMax = (short)maxX; yMax = (short)maxY;
                }
                xMins[gid] = xMin;

                WriteI16(glyfOut, nContours);
                WriteI16(glyfOut, xMin); WriteI16(glyfOut, yMin); WriteI16(glyfOut, xMax); WriteI16(glyfOut, yMax);
                foreach (var ep in endPts) WriteU16(glyfOut, ep);
                WriteU16(glyfOut, (ushort)instructionLength);
                glyfOut.Write(instructionStream.Slice(instructionPos, instructionLength));
                instructionPos += instructionLength;

                // Flags then x/y deltas, standard glyf encoding (no repeat compression).
                var xBytes = new MemoryStream();
                var yBytes = new MemoryStream();
                for (int p = 0; p < totalPoints; p++)
                {
                    byte flag = (byte)(onCurve[p] ? 0x01 : 0x00);
                    int vx = dx[p], vy = dy[p];
                    if (vx == 0) flag |= 0x10;
                    else if (vx is >= -255 and <= 255)
                    {
                        flag |= 0x02;
                        if (vx > 0) flag |= 0x10;
                        xBytes.WriteByte((byte)Math.Abs(vx));
                    }
                    else { WriteI16(xBytes, (short)vx); }
                    if (vy == 0) flag |= 0x20;
                    else if (vy is >= -255 and <= 255)
                    {
                        flag |= 0x04;
                        if (vy > 0) flag |= 0x20;
                        yBytes.WriteByte((byte)Math.Abs(vy));
                    }
                    else { WriteI16(yBytes, (short)vy); }
                    glyfOut.WriteByte(flag);
                }
                xBytes.Position = 0; xBytes.CopyTo(glyfOut);
                yBytes.Position = 0; yBytes.CopyTo(glyfOut);
            }
            else
            {
                // Composite glyph: bbox is mandatory, component records copied verbatim.
                if (!hasBbox) throw new InvalidDataException("Composite glyph without explicit bbox.");
                xMin = BinaryPrimitives.ReadInt16BigEndian(bboxStream[bboxPos..]);
                yMin = BinaryPrimitives.ReadInt16BigEndian(bboxStream[(bboxPos + 2)..]);
                xMax = BinaryPrimitives.ReadInt16BigEndian(bboxStream[(bboxPos + 4)..]);
                yMax = BinaryPrimitives.ReadInt16BigEndian(bboxStream[(bboxPos + 6)..]);
                bboxPos += 8;
                xMins[gid] = xMin;

                int compStart = compositePos;
                bool haveInstructions = false;
                while (true)
                {
                    ushort flags = BinaryPrimitives.ReadUInt16BigEndian(compositeStream[compositePos..]);
                    if ((flags & 0x0100) != 0) haveInstructions = true;
                    int size = 4 + ((flags & 0x0001) != 0 ? 4 : 2);
                    if ((flags & 0x0008) != 0) size += 2;
                    else if ((flags & 0x0040) != 0) size += 4;
                    else if ((flags & 0x0080) != 0) size += 8;
                    compositePos += size;
                    if (compositePos > compositeStream.Length) throw new InvalidDataException("Composite stream overrun.");
                    if ((flags & 0x0020) == 0) break;
                }

                WriteI16(glyfOut, -1);
                WriteI16(glyfOut, xMin); WriteI16(glyfOut, yMin); WriteI16(glyfOut, xMax); WriteI16(glyfOut, yMax);
                glyfOut.Write(compositeStream[compStart..compositePos]);
                if (haveInstructions)
                {
                    int instructionLength = Read255(glyphStream, ref glyphPos);
                    if (instructionPos + instructionLength > instructionStream.Length)
                        throw new InvalidDataException("Instruction stream overrun.");
                    WriteU16(glyfOut, (ushort)instructionLength);
                    glyfOut.Write(instructionStream.Slice(instructionPos, instructionLength));
                    instructionPos += instructionLength;
                }
            }

            if ((glyfOut.Length & 1) != 0) glyfOut.WriteByte(0); // 2-byte alignment, required by short loca
        }
        locaOffsets[numGlyphs] = (uint)glyfOut.Length;

        byte[] loca;
        if (indexFormat == 0)
        {
            if (glyfOut.Length > 0x1FFFE) throw new InvalidDataException("glyf too large for short loca.");
            loca = new byte[(numGlyphs + 1) * 2];
            for (int i = 0; i <= numGlyphs; i++)
                BinaryPrimitives.WriteUInt16BigEndian(loca.AsSpan(i * 2), (ushort)(locaOffsets[i] / 2));
        }
        else
        {
            loca = new byte[(numGlyphs + 1) * 4];
            for (int i = 0; i <= numGlyphs; i++)
                BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(i * 4), locaOffsets[i]);
        }
        return (glyfOut.ToArray(), loca, xMins, indexFormat);
    }

    /// <summary>WOFF2 coordinate triplet decoding (flag = lower 7 bits of the point flag).</summary>
    private static void DecodeTriplet(int flag, ReadOnlySpan<byte> data, ref int pos, out int dx, out int dy)
    {
        static int WithSign(int flag, int baseVal) => (flag & 1) != 0 ? baseVal : -baseVal;

        if (flag < 10)
        {
            dx = 0;
            dy = WithSign(flag, ((flag & 14) << 7) + data[pos]);
            pos += 1;
        }
        else if (flag < 20)
        {
            dx = WithSign(flag, (((flag - 10) & 14) << 7) + data[pos]);
            dy = 0;
            pos += 1;
        }
        else if (flag < 84)
        {
            int b0 = flag - 20;
            int b1 = data[pos];
            dx = WithSign(flag, 1 + (b0 & 0x30) + (b1 >> 4));
            dy = WithSign(flag >> 1, 1 + ((b0 & 0x0C) << 2) + (b1 & 0x0F));
            pos += 1;
        }
        else if (flag < 120)
        {
            int b0 = flag - 84;
            dx = WithSign(flag, 1 + ((b0 / 12) << 8) + data[pos]);
            dy = WithSign(flag >> 1, 1 + (((b0 % 12) >> 2) << 8) + data[pos + 1]);
            pos += 2;
        }
        else if (flag < 124)
        {
            int b2 = data[pos + 1];
            dx = WithSign(flag, (data[pos] << 4) + (b2 >> 4));
            dy = WithSign(flag >> 1, ((b2 & 0x0F) << 8) + data[pos + 2]);
            pos += 3;
        }
        else
        {
            dx = WithSign(flag, (data[pos] << 8) + data[pos + 1]);
            dy = WithSign(flag >> 1, (data[pos + 2] << 8) + data[pos + 3]);
            pos += 4;
        }
    }

    // ---- Transformed hmtx reconstruction (W3C WOFF2 §5.4) ----

    private static byte[] ReconstructHmtx(ReadOnlySpan<byte> t, Dictionary<uint, Woff2Table> byTag,
        byte[]? glyf, byte[]? loca, ref short[]? xMins)
    {
        if (!byTag.TryGetValue(TagHhea, out var hhea) || hhea.Data.Length < 36)
            throw new InvalidDataException("hhea required to rebuild hmtx.");
        if (!byTag.TryGetValue(TagMaxp, out var maxp) || maxp.Data.Length < 6)
            throw new InvalidDataException("maxp required to rebuild hmtx.");
        int numberOfHMetrics = BinaryPrimitives.ReadUInt16BigEndian(hhea.Data.AsSpan(34));
        int numGlyphs = BinaryPrimitives.ReadUInt16BigEndian(maxp.Data.AsSpan(4));

        // Omitted lsbs are recovered from glyph xMin values.
        xMins ??= ExtractXMinsFromRawGlyf(glyf ?? byTag.GetValueOrDefault(TagGlyf)?.Data,
            loca ?? byTag.GetValueOrDefault(TagLoca)?.Data, numGlyphs);

        int pos = 0;
        byte flags = t[pos++];
        bool proportionalOmitted = (flags & 1) != 0;
        bool monospaceOmitted = (flags & 2) != 0;

        var advances = new ushort[numberOfHMetrics];
        for (int i = 0; i < numberOfHMetrics; i++)
        {
            advances[i] = BinaryPrimitives.ReadUInt16BigEndian(t[pos..]);
            pos += 2;
        }
        var lsbs = new short[numGlyphs];
        for (int i = 0; i < numberOfHMetrics; i++)
        {
            if (proportionalOmitted) lsbs[i] = i < xMins.Length ? xMins[i] : (short)0;
            else { lsbs[i] = BinaryPrimitives.ReadInt16BigEndian(t[pos..]); pos += 2; }
        }
        for (int i = numberOfHMetrics; i < numGlyphs; i++)
        {
            if (monospaceOmitted) lsbs[i] = i < xMins.Length ? xMins[i] : (short)0;
            else { lsbs[i] = BinaryPrimitives.ReadInt16BigEndian(t[pos..]); pos += 2; }
        }

        var result = new byte[numberOfHMetrics * 4 + (numGlyphs - numberOfHMetrics) * 2];
        var span = result.AsSpan();
        int outPos = 0;
        for (int i = 0; i < numberOfHMetrics; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(span[outPos..], advances[i]); outPos += 2;
            BinaryPrimitives.WriteInt16BigEndian(span[outPos..], lsbs[i]); outPos += 2;
        }
        for (int i = numberOfHMetrics; i < numGlyphs; i++)
        {
            BinaryPrimitives.WriteInt16BigEndian(span[outPos..], lsbs[i]); outPos += 2;
        }
        return result;
    }

    private static short[] ExtractXMinsFromRawGlyf(byte[]? glyf, byte[]? loca, int numGlyphs)
    {
        var xMins = new short[numGlyphs];
        if (glyf == null || loca == null) return xMins;
        bool shortFormat = loca.Length == (numGlyphs + 1) * 2;
        for (int i = 0; i < numGlyphs; i++)
        {
            long offset = shortFormat
                ? BinaryPrimitives.ReadUInt16BigEndian(loca.AsSpan(i * 2)) * 2L
                : BinaryPrimitives.ReadUInt32BigEndian(loca.AsSpan(i * 4));
            long next = shortFormat
                ? BinaryPrimitives.ReadUInt16BigEndian(loca.AsSpan((i + 1) * 2)) * 2L
                : BinaryPrimitives.ReadUInt32BigEndian(loca.AsSpan((i + 1) * 4));
            if (next > offset && offset + 4 <= glyf.Length)
                xMins[i] = BinaryPrimitives.ReadInt16BigEndian(glyf.AsSpan((int)offset + 2));
        }
        return xMins;
    }

    // ---- Stream helpers ----

    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> data, ref int cursor, int length)
    {
        if (cursor + length > data.Length) throw new InvalidDataException("Transformed glyf stream truncated.");
        var result = data.Slice(cursor, length);
        cursor += length;
        return result;
    }

    private static int Read255(ReadOnlySpan<byte> data, ref int pos)
    {
        byte code = data[pos++];
        if (code == 253) { int v = (data[pos] << 8) | data[pos + 1]; pos += 2; return v; }
        if (code == 255) return 253 + data[pos++];
        if (code == 254) return 506 + data[pos++];
        return code;
    }

    private static uint ReadBase128(ReadOnlySpan<byte> data, ref int pos)
    {
        uint result = 0;
        for (int i = 0; i < 5; i++)
        {
            byte b = data[pos++];
            if (i == 0 && b == 0x80) throw new InvalidDataException("Invalid UIntBase128 leading zero.");
            if ((result & 0xFE000000) != 0) throw new InvalidDataException("UIntBase128 overflow.");
            result = (result << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0) return result;
        }
        throw new InvalidDataException("UIntBase128 exceeds 5 bytes.");
    }

    private static void WriteU16(Stream s, ushort value)
    {
        s.WriteByte((byte)(value >> 8));
        s.WriteByte((byte)value);
    }

    private static void WriteI16(Stream s, short value) => WriteU16(s, (ushort)value);
}
