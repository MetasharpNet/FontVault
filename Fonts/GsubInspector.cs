using System.Buffers.Binary;

namespace FontVault.Fonts;

public sealed class GsubFeatureInfo
{
    public string Tag = "";
    public int LookupCount;
    public string LookupTypes = "";
    public int LigatureCount;
    public string Display => LigatureCount > 0
        ? $"{Tag} — {LookupCount} lookup(s) — {LookupTypes} — {LigatureCount} ligature(s)"
        : $"{Tag} — {LookupCount} lookup(s) — {LookupTypes}";
}

public sealed class LigatureInfo
{
    public string Components = "";
    public ushort LigatureGlyph;
}

/// <summary>
/// GSUB feature inspection: feature list with resolved lookup types, and extraction of
/// concrete ligature substitutions (LookupType 4, Extension type 7 resolved).
/// Component glyphs are mapped back to characters through a reverse cmap.
/// </summary>
public static class GsubInspector
{
    private const uint TagGsub = 0x47535542;
    private const uint TagCmap = 0x636D6170;

    private static readonly string[] LookupTypeNames =
    {
        "?", "Single", "Multiple", "Alternate", "Ligature", "Context", "ChainContext", "Extension", "ReverseChain",
    };

    public static (List<GsubFeatureInfo> Features, List<LigatureInfo> Ligatures) Inspect(byte[] sfnt, int maxLigatures = 1500)
    {
        var features = new List<GsubFeatureInfo>();
        var ligatures = new List<LigatureInfo>();
        var data = sfnt.AsSpan();
        if (!TryFindTable(data, TagGsub, out int gsub, out int gsubLen))
            return (features, ligatures);

        var reverseCmap = TryFindTable(data, TagCmap, out int cmap, out _)
            ? BuildReverseCmap(data, cmap)
            : new Dictionary<ushort, int>();

        int featureListOffset = ReadU16(data, gsub + 6);
        int lookupListOffset = ReadU16(data, gsub + 8);
        if (featureListOffset == 0 || lookupListOffset == 0) return (features, ligatures);
        int featureList = gsub + featureListOffset;
        int lookupList = gsub + lookupListOffset;

        // Lookup list: type per lookup (Extension resolved to the wrapped type) + subtable offsets.
        int lookupCount = ReadU16(data, lookupList);
        var lookups = new List<(int Type, List<int> Subtables)>(lookupCount);
        for (int i = 0; i < lookupCount; i++)
        {
            int lookup = lookupList + ReadU16(data, lookupList + 2 + i * 2);
            if (lookup + 6 > gsub + gsubLen) { lookups.Add((0, new List<int>())); continue; }
            int type = ReadU16(data, lookup);
            int subCount = ReadU16(data, lookup + 4);
            var subtables = new List<int>(subCount);
            int resolvedType = type;
            for (int s = 0; s < subCount; s++)
            {
                int sub = lookup + ReadU16(data, lookup + 6 + s * 2);
                if (type == 7 && sub + 8 <= data.Length) // ExtensionSubstFormat1
                {
                    resolvedType = ReadU16(data, sub + 2);
                    sub += (int)ReadU32(data, sub + 4);
                }
                if (sub < data.Length) subtables.Add(sub);
            }
            lookups.Add((resolvedType, subtables));
        }

        // Feature list: tag → lookup indices.
        var seenLigatures = new HashSet<(ushort, string)>();
        int featureCount = ReadU16(data, featureList);
        for (int i = 0; i < featureCount; i++)
        {
            int rec = featureList + 2 + i * 6;
            if (rec + 6 > data.Length) break;
            string tag = ReadTag(data, rec);
            int feature = featureList + ReadU16(data, rec + 4);
            if (feature + 4 > data.Length) continue;
            int lookupIndexCount = ReadU16(data, feature + 2);

            var types = new SortedSet<int>();
            int ligCountForFeature = 0;
            for (int li = 0; li < lookupIndexCount; li++)
            {
                int idx = ReadU16(data, feature + 4 + li * 2);
                if (idx >= lookups.Count) continue;
                var (type, subtables) = lookups[idx];
                types.Add(type);
                if (type == 4)
                {
                    foreach (int sub in subtables)
                        ligCountForFeature += CollectLigatures(data, sub, reverseCmap, ligatures, seenLigatures, maxLigatures);
                }
            }
            features.Add(new GsubFeatureInfo
            {
                Tag = tag,
                LookupCount = lookupIndexCount,
                LookupTypes = string.Join(", ", types.Select(t => t is > 0 and <= 8 ? LookupTypeNames[t] : $"type {t}")),
                LigatureCount = ligCountForFeature,
            });
        }
        return (features, ligatures);
    }

    /// <summary>LigatureSubstFormat1: coverage glyphs (first components) parallel to ligature sets.</summary>
    private static int CollectLigatures(ReadOnlySpan<byte> data, int sub, Dictionary<ushort, int> reverseCmap,
        List<LigatureInfo> output, HashSet<(ushort, string)> seen, int maxLigatures)
    {
        int collected = 0;
        if (sub + 6 > data.Length || ReadU16(data, sub) != 1) return 0;
        var coverage = ReadCoverage(data, sub + ReadU16(data, sub + 2));
        int ligSetCount = ReadU16(data, sub + 4);
        for (int i = 0; i < ligSetCount && i < coverage.Count; i++)
        {
            int ligSet = sub + ReadU16(data, sub + 6 + i * 2);
            if (ligSet + 2 > data.Length) continue;
            int ligCount = ReadU16(data, ligSet);
            for (int j = 0; j < ligCount; j++)
            {
                if (ligSet + 2 + j * 2 + 2 > data.Length) break;
                int lig = ligSet + ReadU16(data, ligSet + 2 + j * 2);
                if (lig + 4 > data.Length) continue;
                ushort ligGlyph = ReadU16(data, lig);
                int compCount = ReadU16(data, lig + 2);
                if (compCount < 1 || compCount > 16 || lig + 4 + (compCount - 1) * 2 > data.Length) continue;

                var parts = new List<string>(compCount) { GlyphLabel(coverage[i], reverseCmap) };
                for (int c = 0; c < compCount - 1; c++)
                    parts.Add(GlyphLabel(ReadU16(data, lig + 4 + c * 2), reverseCmap));
                string components = string.Join(" + ", parts);

                if (output.Count >= maxLigatures) return collected;
                if (seen.Add((ligGlyph, components)))
                {
                    output.Add(new LigatureInfo { Components = components, LigatureGlyph = ligGlyph });
                    collected++;
                }
            }
        }
        return collected;
    }

    private static string GlyphLabel(ushort glyph, Dictionary<ushort, int> reverseCmap)
    {
        if (reverseCmap.TryGetValue(glyph, out int cp))
        {
            if (cp > 0x20 && cp != 0x7F && !char.IsControl((char)Math.Min(cp, 0xFFFF)))
                return char.ConvertFromUtf32(cp);
            return $"U+{cp:X4}";
        }
        return $"#{glyph}";
    }

    private static List<ushort> ReadCoverage(ReadOnlySpan<byte> data, int coverage)
    {
        var glyphs = new List<ushort>();
        if (coverage + 4 > data.Length) return glyphs;
        int format = ReadU16(data, coverage);
        int count = ReadU16(data, coverage + 2);
        if (format == 1)
        {
            for (int i = 0; i < count && coverage + 4 + i * 2 + 2 <= data.Length; i++)
                glyphs.Add(ReadU16(data, coverage + 4 + i * 2));
        }
        else if (format == 2)
        {
            for (int i = 0; i < count && coverage + 4 + i * 6 + 6 <= data.Length; i++)
            {
                int rec = coverage + 4 + i * 6;
                ushort start = ReadU16(data, rec);
                ushort end = ReadU16(data, rec + 2);
                for (int g = start; g <= end && g <= 0xFFFF; g++) glyphs.Add((ushort)g);
            }
        }
        return glyphs;
    }

    /// <summary>Glyph id → first mapping codepoint, from cmap format 12 or full format 4 decoding.</summary>
    private static Dictionary<ushort, int> BuildReverseCmap(ReadOnlySpan<byte> data, int cmap)
    {
        var map = new Dictionary<ushort, int>();
        int numTables = ReadU16(data, cmap + 2);
        int best = -1, bestScore = 0;
        for (int i = 0; i < numTables; i++)
        {
            int rec = cmap + 4 + i * 8;
            if (rec + 8 > data.Length) break;
            int platform = ReadU16(data, rec);
            int encoding = ReadU16(data, rec + 2);
            int sub = cmap + (int)ReadU32(data, rec + 4);
            if (sub + 4 > data.Length) continue;
            int score = (platform, encoding) switch
            {
                (3, 10) => 5,
                (0, 4) or (0, 5) or (0, 6) => 4,
                (3, 1) => 3,
                (0, _) => 2,
                _ => 1,
            };
            if (score > bestScore) { bestScore = score; best = sub; }
        }
        if (best < 0) return map;

        int format = ReadU16(data, best);
        if (format == 12 && best + 16 <= data.Length)
        {
            long groups = ReadU32(data, best + 12);
            for (long g = 0; g < groups; g++)
            {
                int rec = best + 16 + (int)(g * 12);
                if (rec + 12 > data.Length) break;
                long start = ReadU32(data, rec);
                long end = ReadU32(data, rec + 4);
                long startGlyph = ReadU32(data, rec + 8);
                for (long cp = start; cp <= end && cp <= 0x10FFFF; cp++)
                    map.TryAdd((ushort)(startGlyph + (cp - start)), (int)cp);
            }
        }
        else if (format == 4 && best + 14 <= data.Length)
        {
            int segCount = ReadU16(data, best + 6) / 2;
            int endCodes = best + 14;
            int startCodes = endCodes + segCount * 2 + 2;
            int idDeltas = startCodes + segCount * 2;
            int idRangeOffsets = idDeltas + segCount * 2;
            if (idRangeOffsets + segCount * 2 > data.Length) return map;
            for (int s = 0; s < segCount; s++)
            {
                int start = ReadU16(data, startCodes + s * 2);
                int end = ReadU16(data, endCodes + s * 2);
                if (start == 0xFFFF) continue;
                int idDelta = (short)ReadU16(data, idDeltas + s * 2);
                int idRangeOffset = ReadU16(data, idRangeOffsets + s * 2);
                for (int cp = start; cp <= end; cp++)
                {
                    int glyph;
                    if (idRangeOffset == 0)
                    {
                        glyph = (cp + idDelta) & 0xFFFF;
                    }
                    else
                    {
                        int pos = idRangeOffsets + s * 2 + idRangeOffset + (cp - start) * 2;
                        if (pos + 2 > data.Length) continue;
                        glyph = ReadU16(data, pos);
                        if (glyph != 0) glyph = (glyph + idDelta) & 0xFFFF;
                    }
                    if (glyph != 0) map.TryAdd((ushort)glyph, cp);
                }
            }
        }
        return map;
    }

    private static bool TryFindTable(ReadOnlySpan<byte> data, uint tag, out int offset, out int length)
    {
        offset = 0;
        length = 0;
        if (data.Length < 12) return false;
        int numTables = ReadU16(data, 4);
        for (int i = 0; i < numTables; i++)
        {
            int rec = 12 + i * 16;
            if (rec + 16 > data.Length) return false;
            if (ReadU32(data, rec) != tag) continue;
            long off = ReadU32(data, rec + 8);
            long len = ReadU32(data, rec + 12);
            if (off + len > data.Length) return false;
            offset = (int)off;
            length = (int)len;
            return true;
        }
        return false;
    }

    private static string ReadTag(ReadOnlySpan<byte> data, int offset)
    {
        Span<char> chars = stackalloc char[4];
        int n = 0;
        for (int i = 0; i < 4; i++)
        {
            byte b = data[offset + i];
            if (b is >= 0x20 and < 0x7F) chars[n++] = (char)b;
        }
        return new string(chars[..n]).TrimEnd(' ');
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
}
