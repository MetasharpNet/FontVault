using System.Buffers.Binary;
using System.Text;
using FontVault.Core;

namespace FontVault.Fonts;

/// <summary>Metadata extracted from an sfnt (OTF/TTF) buffer.</summary>
public sealed class ParsedFont
{
    public string WindowsDisplayName = "";
    public string FamilyName = "";
    public string TypographicFamilyName = "";
    public string Style = "";
    public string TypographicSubfamily = "";
    public string FullName = "";
    public string PostScriptName = "";
    public string Version = "";
    public ushort GlyphCount;
    public FontFormat Format;
    public bool IsVariableFont;
    public byte MetadataScore;
    public LicenseClass License;
    public List<UnicodeInterval> Coverage = new();
    public List<VariableAxis> Axes = new();
    public List<string> Features = new();

    // Transient license sources (name table); used only to classify License, not persisted.
    public string Copyright = "";
    public string LicenseDescription = "";
    public string LicenseUrl = "";
}

/// <summary>
/// In-house SFNT reader: parses only the useful tables
/// (name, head, OS/2, maxp, cmap, fvar, GSUB, GPOS) from an already-read buffer
/// (the full read is required anyway for the global CRC32).
/// </summary>
public static class SfntParser
{
    private const uint TagHead = 0x68656164; // 'head'
    private const uint TagName = 0x6E616D65; // 'name'
    private const uint TagOs2 = 0x4F532F32;  // 'OS/2'
    private const uint TagMaxp = 0x6D617870; // 'maxp'
    private const uint TagCmap = 0x636D6170; // 'cmap'
    private const uint TagFvar = 0x66766172; // 'fvar'
    private const uint TagGsub = 0x47535542; // 'GSUB'
    private const uint TagGpos = 0x47504F53; // 'GPOS'

    public static ParsedFont Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12) throw new InvalidDataException("File too short for an sfnt header.");

        uint sfntVersion = ReadU32(data, 0);
        FontFormat format = sfntVersion switch
        {
            0x00010000 or 0x74727565 => FontFormat.TrueType, // 1.0 / 'true'
            0x4F54544F => FontFormat.Cff,                    // 'OTTO'
            0x74746366 => throw new InvalidDataException("TTC collection not supported."),
            _ => throw new InvalidDataException($"Unknown sfnt signature: 0x{sfntVersion:X8}."),
        };

        int numTables = ReadU16(data, 4);
        if (data.Length < 12 + numTables * 16) throw new InvalidDataException("Truncated table directory.");

        var tables = new Dictionary<uint, (int Offset, int Length)>(numTables);
        for (int i = 0; i < numTables; i++)
        {
            int rec = 12 + i * 16;
            uint tag = ReadU32(data, rec);
            long offset = ReadU32(data, rec + 8);
            long length = ReadU32(data, rec + 12);
            if (offset + length > data.Length) continue; // out-of-bounds table: skipped, the rest may suffice
            tables[tag] = ((int)offset, (int)length);
        }

        var result = new ParsedFont { Format = format, IsVariableFont = tables.ContainsKey(TagFvar) };

        if (!tables.TryGetValue(TagMaxp, out var maxp) || maxp.Length < 6)
            throw new InvalidDataException("maxp table missing or truncated.");
        result.GlyphCount = ReadU16(data, maxp.Offset + 4);

        if (tables.TryGetValue(TagName, out var name))
            ParseName(data, name.Offset, name.Length, result);

        if (string.IsNullOrEmpty(result.Version) && tables.TryGetValue(TagHead, out var head) && head.Length >= 8)
        {
            uint revision = ReadU32(data, head.Offset + 4); // Fixed 16.16
            result.Version = (revision / 65536.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        if (tables.TryGetValue(TagCmap, out var cmap))
            result.Coverage = ParseCmap(data, cmap.Offset, cmap.Length);

        if (result.IsVariableFont && tables.TryGetValue(TagFvar, out var fvar))
            result.Axes = ParseFvar(data, fvar.Offset, fvar.Length);

        if (tables.TryGetValue(TagGsub, out var gsub))
            ParseFeatureTags(data, gsub.Offset, gsub.Length, result.Features);
        if (tables.TryGetValue(TagGpos, out var gpos))
            ParseFeatureTags(data, gpos.Offset, gpos.Length, result.Features);

        // WindowsDisplayName: typographic family (ID 16) takes precedence over family (ID 1).
        result.WindowsDisplayName = result.TypographicFamilyName.Length > 0 ? result.TypographicFamilyName : result.FamilyName;
        if (result.WindowsDisplayName.Length == 0) result.WindowsDisplayName = result.PostScriptName;
        if (result.WindowsDisplayName.Length == 0)
            throw new InvalidDataException("Missing name metadata: no display name found.");
        if (result.Style.Length == 0) result.Style = "Regular";

        int score = 0;
        foreach (var s in new[] { result.FamilyName, result.TypographicFamilyName, result.Style,
                 result.TypographicSubfamily, result.FullName, result.PostScriptName, result.Version })
            if (s.Length > 0) score++;
        if (tables.ContainsKey(TagOs2)) score++;
        result.MetadataScore = (byte)score;

        // License classification: OS/2 fsType (embedding rights) + name-table license fields.
        ushort fsType = 0;
        if (tables.TryGetValue(TagOs2, out var os2) && os2.Length >= 10)
            fsType = ReadU16(data, os2.Offset + 8);
        result.License = ClassifyLicense(result.Copyright, result.LicenseDescription, result.LicenseUrl, fsType);

        return result;
    }

    // Open-font-license signatures (checked first): a match marks the font Free.
    private static readonly string[] FreeMarkers =
    {
        "open font license", "openfontlicense", "ofl.txt", "scripts.sil.org/ofl",
        "apache license", "gnu general public", "gnu lesser general public", "gpl",
        "ubuntu font licence", "ubuntu font license", "creative commons", "cc0",
        "public domain", "mit license", "the unlicense", "wtfpl",
    };

    // Proprietary / commercial wording (checked after Free and after the fsType bit).
    private static readonly string[] PaidMarkers =
    {
        "all rights reserved", "may not be", "is not permitted", "without permission",
        "commercial", "purchase", "eula", "end user license", "end-user license",
        "license agreement", "licensed to", "proprietary", "unauthorized", "unauthorised",
        "is prohibited",
    };

    /// <summary>
    /// Heuristic license classification (no canonical free/paid flag exists in a font):
    /// an open-license signature in the license fields → Free; otherwise a restrictive
    /// OS/2 fsType (Restricted License embedding, bit 1) or proprietary wording → Paid; else Unknown.
    /// </summary>
    private static LicenseClass ClassifyLicense(string copyright, string licenseDesc, string licenseUrl, ushort fsType)
    {
        string text = (copyright + " " + licenseDesc + " " + licenseUrl).ToLowerInvariant();
        foreach (var m in FreeMarkers)
            if (text.Contains(m, StringComparison.Ordinal)) return LicenseClass.Free;
        if ((fsType & 0x0002) != 0) return LicenseClass.Paid; // Restricted License embedding
        foreach (var m in PaidMarkers)
            if (text.Contains(m, StringComparison.Ordinal)) return LicenseClass.Paid;
        return LicenseClass.Unknown;
    }

    private static void ParseName(ReadOnlySpan<byte> data, int off, int len, ParsedFont result)
    {
        if (len < 6) return;
        int count = ReadU16(data, off + 2);
        int stringOffset = off + ReadU16(data, off + 4);

        // Per wanted name ID: best record by platform/language preference.
        var best = new Dictionary<int, (int Score, string Value)>();
        for (int i = 0; i < count; i++)
        {
            int rec = off + 6 + i * 12;
            if (rec + 12 > off + len) break;
            int platformId = ReadU16(data, rec);
            int encodingId = ReadU16(data, rec + 2);
            int languageId = ReadU16(data, rec + 4);
            int nameId = ReadU16(data, rec + 6);
            int strLen = ReadU16(data, rec + 8);
            int strOff = stringOffset + ReadU16(data, rec + 10);

            if (nameId is not (0 or 1 or 2 or 4 or 5 or 6 or 13 or 14 or 16 or 17)) continue;
            if (strOff + strLen > data.Length) continue;

            int score = platformId switch
            {
                3 when languageId == 0x0409 => 4, // Windows en-US
                3 => 3,                           // Windows, other language
                0 => 2,                           // Unicode
                1 when languageId == 0 => 1,      // Macintosh English
                _ => 0,
            };
            if (score == 0) continue;
            if (best.TryGetValue(nameId, out var existing) && existing.Score >= score) continue;

            var span = data.Slice(strOff, strLen);
            string value = platformId is 3 or 0
                ? Encoding.BigEndianUnicode.GetString(span)
                : Encoding.Latin1.GetString(span); // MacRoman approximation, fine for ASCII
            value = value.Trim();
            if (value.Length == 0) continue;
            best[nameId] = (score, value);
        }

        if (best.TryGetValue(1, out var v1)) result.FamilyName = v1.Value;
        if (best.TryGetValue(2, out var v2)) result.Style = v2.Value;
        if (best.TryGetValue(4, out var v4)) result.FullName = v4.Value;
        if (best.TryGetValue(6, out var v6)) result.PostScriptName = v6.Value;
        if (best.TryGetValue(16, out var v16)) result.TypographicFamilyName = v16.Value;
        if (best.TryGetValue(17, out var v17)) result.TypographicSubfamily = v17.Value;
        if (best.TryGetValue(0, out var v0)) result.Copyright = v0.Value;
        if (best.TryGetValue(13, out var v13)) result.LicenseDescription = v13.Value;
        if (best.TryGetValue(14, out var v14)) result.LicenseUrl = v14.Value;
        if (best.TryGetValue(5, out var v5))
        {
            var m = System.Text.RegularExpressions.Regex.Match(v5.Value, @"\d+(\.\d+)?");
            result.Version = m.Success ? m.Value : "";
        }
    }

    private static List<UnicodeInterval> ParseCmap(ReadOnlySpan<byte> data, int off, int len)
    {
        var result = new List<UnicodeInterval>();
        if (len < 4) return result;
        int numTables = ReadU16(data, off + 2);

        // Candidates by preference: (3,10) and (0,4+) = full Unicode, then (3,1) and (0,3) = BMP.
        var candidates = new List<(int Score, int SubOffset)>();
        for (int i = 0; i < numTables; i++)
        {
            int rec = off + 4 + i * 8;
            if (rec + 8 > off + len) break;
            int platformId = ReadU16(data, rec);
            int encodingId = ReadU16(data, rec + 2);
            long subOffset = ReadU32(data, rec + 4);
            if (off + subOffset + 4 > data.Length) continue;
            int score = (platformId, encodingId) switch
            {
                (3, 10) => 5,
                (0, 4) or (0, 5) or (0, 6) => 4,
                (3, 1) => 3,
                (0, 3) => 2,
                (0, _) => 1,
                _ => 0,
            };
            if (score > 0) candidates.Add((score, off + (int)subOffset));
        }
        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

        foreach (var (_, sub) in candidates)
        {
            int format = ReadU16(data, sub);
            if (format == 12 && TryParseFormat12(data, sub, result)) return Normalize(result);
            if (format == 4 && TryParseFormat4(data, sub, result)) return Normalize(result);
            result.Clear();
        }
        return result;
    }

    private static bool TryParseFormat12(ReadOnlySpan<byte> data, int sub, List<UnicodeInterval> result)
    {
        if (sub + 16 > data.Length) return false;
        long nGroups = ReadU32(data, sub + 12);
        if (sub + 16 + nGroups * 12 > data.Length) return false;
        for (long i = 0; i < nGroups; i++)
        {
            int rec = sub + 16 + (int)(i * 12);
            int start = (int)ReadU32(data, rec);
            int end = (int)ReadU32(data, rec + 4);
            if (start > end || start > 0x10FFFF) continue;
            result.Add(new UnicodeInterval(start, Math.Min(end, 0x10FFFF)));
        }
        return true;
    }

    private static bool TryParseFormat4(ReadOnlySpan<byte> data, int sub, List<UnicodeInterval> result)
    {
        if (sub + 14 > data.Length) return false;
        int segCount = ReadU16(data, sub + 6) / 2;
        int endCodes = sub + 14;
        int startCodes = endCodes + segCount * 2 + 2; // +2: reservedPad
        if (startCodes + segCount * 2 > data.Length) return false;
        for (int i = 0; i < segCount; i++)
        {
            int end = ReadU16(data, endCodes + i * 2);
            int start = ReadU16(data, startCodes + i * 2);
            if (start == 0xFFFF && end == 0xFFFF) continue; // terminal segment
            if (start > end) continue;
            result.Add(new UnicodeInterval(start, end));
        }
        return true;
    }

    private static List<VariableAxis> ParseFvar(ReadOnlySpan<byte> data, int off, int len)
    {
        var axes = new List<VariableAxis>();
        if (len < 16) return axes;
        int axesArrayOffset = ReadU16(data, off + 4);
        int axisCount = ReadU16(data, off + 8);
        int axisSize = ReadU16(data, off + 10);
        if (axisSize < 20) return axes;
        for (int i = 0; i < axisCount; i++)
        {
            int rec = off + axesArrayOffset + i * axisSize;
            if (rec + 20 > off + len || rec + 20 > data.Length) break;
            string tag = ReadTag(data, rec);
            float min = (int)ReadU32(data, rec + 4) / 65536f;
            float def = (int)ReadU32(data, rec + 8) / 65536f;
            float max = (int)ReadU32(data, rec + 12) / 65536f;
            axes.Add(new VariableAxis(tag, min, def, max));
        }
        return axes;
    }

    /// <summary>Collects distinct feature tags from a GSUB or GPOS feature list.</summary>
    private static void ParseFeatureTags(ReadOnlySpan<byte> data, int off, int len, List<string> features)
    {
        if (len < 8) return;
        int featureListOffset = ReadU16(data, off + 6);
        int list = off + featureListOffset;
        if (list + 2 > data.Length || featureListOffset == 0) return;
        int count = ReadU16(data, list);
        for (int i = 0; i < count; i++)
        {
            int rec = list + 2 + i * 6;
            if (rec + 6 > data.Length || rec + 6 > off + len) break;
            string tag = ReadTag(data, rec);
            if (tag.Length > 0 && !features.Contains(tag)) features.Add(tag);
        }
    }

    /// <summary>Sorts and merges adjacent or overlapping intervals.</summary>
    private static List<UnicodeInterval> Normalize(List<UnicodeInterval> intervals)
    {
        if (intervals.Count == 0) return intervals;
        intervals.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<UnicodeInterval>(intervals.Count);
        var current = intervals[0];
        for (int i = 1; i < intervals.Count; i++)
        {
            var next = intervals[i];
            if (next.Start <= current.End + 1)
                current = new UnicodeInterval(current.Start, Math.Max(current.End, next.End));
            else
            {
                merged.Add(current);
                current = next;
            }
        }
        merged.Add(current);
        return merged;
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
