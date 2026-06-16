using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FontVault.Fonts;

/// <summary>
/// Simplified in-house converter from PostScript Type 1 fonts (PFB/PFA) to OpenType/CFF (.otf).
/// Type 1 and CFF share the cubic-outline model, so outlines convert near-losslessly. "Simplified":
/// hints are dropped (unhinted CFF), flex is expanded to plain curves, and seac accents are expanded
/// to composite outlines. Widths come from the charstrings (hsbw); AFM/PFM metrics are not used.
/// No dependency — emits a CFF table and assembles the sfnt via <see cref="SfntBuilder"/>.
/// </summary>
public static class Type1Converter
{
    public static bool IsType1(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) return false;
        if (data[0] == 0x80) return true;                                  // PFB
        if (data[0] == (byte)'%' && data[1] == (byte)'!') return true;     // PFA ("%!PS-AdobeFont" / "%!FontType1")
        return false;
    }

    /// <summary>Converts a PFB/PFA buffer to an OTF byte array. Throws on malformed input.</summary>
    public static byte[] Convert(ReadOnlySpan<byte> input)
    {
        var (header, eexec) = Split(input);
        var info = ParseHeader(header);
        byte[] privatePart = Decrypt(eexec, 55665, 4);
        int lenIV = ParseLenIV(privatePart);
        var subrs = ParseSubrs(privatePart, lenIV);
        var charstrings = ParseCharStrings(privatePart, lenIV); // name -> decrypted Type 1 charstring

        // Glyph order: .notdef first, then by the font's encoding order, then any remaining names.
        var order = BuildGlyphOrder(charstrings, info.Encoding);
        var interp = new T1Interpreter(charstrings, subrs);

        var glyphs = new List<BuiltGlyph>(order.Count);
        foreach (var name in order)
        {
            GlyphOutline outline;
            try { outline = interp.Run(name); }
            catch { outline = new GlyphOutline(); } // unconvertible glyph -> empty (keeps GID order stable)
            glyphs.Add(new BuiltGlyph(name, outline));
        }

        byte[] cff = CffWriter.Build(info, glyphs);
        return AssembleOtf(info, glyphs, cff);
    }

    // ---- PFB / PFA splitting ----

    private static (byte[] Header, byte[] Eexec) Split(ReadOnlySpan<byte> input)
    {
        if (input[0] == 0x80) // PFB: 6-byte segment headers (0x80, type, len32 LE)
        {
            var ascii = new List<byte>();
            var binary = new List<byte>();
            bool seenBinary = false;
            int p = 0;
            while (p + 6 <= input.Length)
            {
                if (input[p] != 0x80) break;
                int type = input[p + 1];
                if (type == 3) break; // EOF
                int len = input[p + 2] | (input[p + 3] << 8) | (input[p + 4] << 16) | (input[p + 5] << 24);
                p += 6;
                if (len < 0 || p + len > input.Length) len = input.Length - p;
                var seg = input.Slice(p, len);
                if (type == 1) { if (!seenBinary) ascii.AddRange(seg.ToArray()); }
                else { binary.AddRange(seg.ToArray()); seenBinary = true; }
                p += len;
            }
            return (ascii.ToArray(), binary.ToArray());
        }

        // PFA: find "eexec", the rest is hex (or binary) encrypted data up to the zero trailer.
        int idx = IndexOf(input, "eexec");
        if (idx < 0) throw new InvalidDataException("Type 1: no eexec section.");
        byte[] header = input[..idx].ToArray();
        int d = idx + 5;
        while (d < input.Length && (input[d] is (byte)'\r' or (byte)'\n' or (byte)' ' or (byte)'\t')) d++;
        // Determine if the eexec body is ASCII-hex (first bytes are hex digits/whitespace).
        var body = input[d..];
        byte[] eexec = LooksHex(body) ? HexToBytes(body) : body.ToArray();
        return (header, eexec);
    }

    private static bool LooksHex(ReadOnlySpan<byte> data)
    {
        int chec01 = 0;
        for (int i = 0; i < data.Length && i < 4; i++)
        {
            byte b = data[i];
            bool hex = b is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';
            if (hex) chec01++;
        }
        return chec01 >= 4;
    }

    private static byte[] HexToBytes(ReadOnlySpan<byte> data)
    {
        var outp = new List<byte>(data.Length / 2);
        int hi = -1;
        foreach (byte b in data)
        {
            int v = HexVal(b);
            if (v < 0) continue;
            if (hi < 0) hi = v;
            else { outp.Add((byte)((hi << 4) | v)); hi = -1; }
        }
        return outp.ToArray();
    }

    private static int HexVal(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };

    // ---- eexec / charstring decryption ----

    private static byte[] Decrypt(ReadOnlySpan<byte> cipher, ushort r, int skip)
    {
        const ushort c1 = 52845, c2 = 22719;
        var plain = new byte[cipher.Length];
        for (int i = 0; i < cipher.Length; i++)
        {
            byte c = cipher[i];
            plain[i] = (byte)(c ^ (r >> 8));
            r = (ushort)((c + r) * c1 + c2);
        }
        return skip >= plain.Length ? Array.Empty<byte>() : plain[skip..];
    }

    // ---- header parsing (FontInfo, FontMatrix, FontBBox, Encoding) ----

    public sealed class FontInfo
    {
        public string FontName = "Untitled";
        public string FamilyName = "";
        public string FullName = "";
        public string Weight = "";
        public string Notice = "";
        public string Version = "001.000";
        public double ItalicAngle;
        public bool IsFixedPitch;
        public int UnitsPerEm = 1000;
        public int[] FontBBox = { 0, -200, 1000, 800 };
        public string?[] Encoding = Type1Encoding.Standard; // code -> glyph name (or null)
    }

    private static FontInfo ParseHeader(byte[] header)
    {
        string text = Encoding.Latin1.GetString(header);
        var info = new FontInfo
        {
            FontName = ReadLiteral(text, "/FontName") ?? "Untitled",
            FullName = ReadString(text, "/FullName") ?? "",
            FamilyName = ReadString(text, "/FamilyName") ?? "",
            Weight = ReadString(text, "/Weight") ?? "",
            Notice = ReadString(text, "/Notice") ?? "",
            Version = ReadString(text, "/version") ?? "001.000",
        };
        if (TryReadNumber(text, "/ItalicAngle", out double ia)) info.ItalicAngle = ia;
        info.IsFixedPitch = text.Contains("/isFixedPitch true");
        if (info.FullName.Length == 0) info.FullName = info.FontName;
        if (info.FamilyName.Length == 0) info.FamilyName = info.FullName;

        var bbox = ReadArrayNumbers(text, "/FontBBox", 4);
        if (bbox != null) info.FontBBox = new[] { (int)bbox[0], (int)bbox[1], (int)bbox[2], (int)bbox[3] };

        var matrix = ReadArrayNumbers(text, "/FontMatrix", 6);
        if (matrix != null && matrix[0] > 0) info.UnitsPerEm = (int)Math.Round(1.0 / matrix[0]);
        if (info.UnitsPerEm <= 0) info.UnitsPerEm = 1000;

        info.Encoding = ParseEncoding(text);
        return info;
    }

    private static string?[] ParseEncoding(string text)
    {
        int idx = text.IndexOf("/Encoding", StringComparison.Ordinal);
        if (idx >= 0 && text.IndexOf("StandardEncoding", idx, Math.Min(40, text.Length - idx), StringComparison.Ordinal) >= 0)
            return Type1Encoding.Standard;

        var enc = new string?[256];
        // Custom encoding entries: "dup <code> /<name> put"
        int pos = idx < 0 ? 0 : idx;
        while (true)
        {
            int dup = text.IndexOf("dup ", pos, StringComparison.Ordinal);
            if (dup < 0) break;
            int put = text.IndexOf(" put", dup, StringComparison.Ordinal);
            if (put < 0) break;
            string seg = text.Substring(dup + 4, put - (dup + 4)).Trim();
            int slash = seg.IndexOf('/');
            if (slash > 0 && int.TryParse(seg[..slash].Trim(), out int code) && code is >= 0 and < 256)
            {
                string name = seg[(slash + 1)..].Trim();
                int sp = name.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
                if (sp >= 0) name = name[..sp];
                enc[code] = name;
            }
            pos = put + 4;
            if (text.IndexOf("readonly def", dup, StringComparison.Ordinal) is int rd && rd >= 0 && rd < pos) break;
        }
        return enc;
    }

    // ---- private-section parsing (lenIV, Subrs, CharStrings) ----

    private static int ParseLenIV(byte[] data)
    {
        int i = IndexOf(data, "/lenIV");
        if (i < 0) return 4;
        i += 6;
        while (i < data.Length && data[i] == ' ') i++;
        int v = 0; bool any = false;
        while (i < data.Length && data[i] is >= (byte)'0' and <= (byte)'9') { v = v * 10 + (data[i] - '0'); i++; any = true; }
        return any ? v : 4;
    }

    private static List<byte[]> ParseSubrs(byte[] data, int lenIV)
    {
        var subrs = new List<byte[]>();
        int i = IndexOf(data, "/Subrs");
        if (i < 0) return subrs;
        // entries: "dup <idx> <len> RD <bytes> NP"
        int pos = i;
        while (true)
        {
            int dup = IndexOf(data, "dup ", pos);
            if (dup < 0) break;
            // stop if we've reached CharStrings
            int cs = IndexOf(data, "/CharStrings", i);
            if (cs >= 0 && dup > cs) break;
            int p = dup + 4;
            int idx = ReadInt(data, ref p);
            int len = ReadInt(data, ref p);
            int bin = SkipToBinary(data, p);
            if (bin < 0 || bin + len > data.Length) break;
            byte[] cs1 = Decrypt(data.AsSpan(bin, len), 4330, lenIV);
            while (subrs.Count <= idx) subrs.Add(Array.Empty<byte>());
            subrs[idx] = cs1;
            pos = bin + len;
        }
        return subrs;
    }

    private static Dictionary<string, byte[]> ParseCharStrings(byte[] data, int lenIV)
    {
        var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        int i = IndexOf(data, "/CharStrings");
        if (i < 0) throw new InvalidDataException("Type 1: no CharStrings.");
        int pos = i + 12;
        while (true)
        {
            int slash = IndexOfByte(data, (byte)'/', pos);
            if (slash < 0) break;
            int p = slash + 1;
            var sb = new StringBuilder();
            while (p < data.Length && data[p] is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')) { sb.Append((char)data[p]); p++; }
            string name = sb.ToString();
            int len = ReadInt(data, ref p);
            if (len <= 0 || len > 65535) { pos = slash + 1; if (IndexOf(data, "end", slash) is int e && e == slash) break; continue; }
            int bin = SkipToBinary(data, p);
            if (bin < 0 || bin + len > data.Length) break;
            map[name] = Decrypt(data.AsSpan(bin, len), 4330, lenIV);
            pos = bin + len;
            // Stop at the closing "end" of the dict if it appears right after.
        }
        if (map.Count == 0) throw new InvalidDataException("Type 1: empty CharStrings.");
        return map;
    }

    private static List<string> BuildGlyphOrder(Dictionary<string, byte[]> charstrings, string?[] encoding)
    {
        var order = new List<string> { ".notdef" };
        var seen = new HashSet<string>(StringComparer.Ordinal) { ".notdef" };
        foreach (var name in encoding)
            if (name != null && charstrings.ContainsKey(name) && seen.Add(name)) order.Add(name);
        foreach (var name in charstrings.Keys)
            if (seen.Add(name)) order.Add(name);
        if (!charstrings.ContainsKey(".notdef")) charstrings[".notdef"] = Array.Empty<byte>();
        return order;
    }

    // ---- sfnt (OTF) assembly ----

    private static byte[] AssembleOtf(FontInfo info, List<BuiltGlyph> glyphs, byte[] cff)
    {
        int n = glyphs.Count;
        int upm = info.UnitsPerEm;
        int xMin = info.FontBBox[0], yMin = info.FontBBox[1], xMax = info.FontBBox[2], yMax = info.FontBBox[3];
        int ascent = yMax > 0 ? yMax : (int)(upm * 0.8);
        int descent = yMin < 0 ? yMin : -(int)(upm * 0.2);
        int advanceMax = 0;
        foreach (var g in glyphs) advanceMax = Math.Max(advanceMax, g.Outline.Width);

        var tables = new List<(uint, byte[])>
        {
            (0x43464620u, cff),                                  // 'CFF '
            (0x4F532F32u, BuildOs2(info, ascent, descent)),      // 'OS/2'
            (0x636D6170u, BuildCmap(glyphs)),                    // 'cmap'
            (0x68656164u, BuildHead(info, xMin, yMin, xMax, yMax)),
            (0x68686561u, BuildHhea(ascent, descent, advanceMax, n)),
            (0x686D7478u, BuildHmtx(glyphs)),                    // 'hmtx'
            (0x6D617870u, BuildMaxp(n)),                         // 'maxp'
            (0x6E616D65u, BuildName(info)),                      // 'name'
            (0x706F7374u, BuildPost(info)),                      // 'post'
        };
        return SfntBuilder.Build(0x4F54544Fu, tables); // 'OTTO'
    }

    private static byte[] BuildHead(FontInfo info, int xMin, int yMin, int xMax, int yMax)
    {
        var b = new byte[54];
        var s = b.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(s, 0x00010000);        // version
        BinaryPrimitives.WriteUInt32BigEndian(s[4..], 0x00010000);  // fontRevision
        // checkSumAdjustment (8) left 0 — SfntBuilder fills it.
        BinaryPrimitives.WriteUInt32BigEndian(s[12..], 0x5F0F3CF5); // magic
        ushort macStyle = 0;
        if (IsBold(info.Weight)) macStyle |= 0x01;
        if (info.ItalicAngle != 0) macStyle |= 0x02;
        BinaryPrimitives.WriteUInt16BigEndian(s[16..], 0x000B);     // flags
        BinaryPrimitives.WriteUInt16BigEndian(s[18..], (ushort)info.UnitsPerEm);
        // created/modified (8+8) left 0.
        BinaryPrimitives.WriteInt16BigEndian(s[36..], (short)xMin);
        BinaryPrimitives.WriteInt16BigEndian(s[38..], (short)yMin);
        BinaryPrimitives.WriteInt16BigEndian(s[40..], (short)xMax);
        BinaryPrimitives.WriteInt16BigEndian(s[42..], (short)yMax);
        BinaryPrimitives.WriteUInt16BigEndian(s[44..], macStyle);
        BinaryPrimitives.WriteUInt16BigEndian(s[46..], 8);          // lowestRecPPEM
        BinaryPrimitives.WriteInt16BigEndian(s[48..], 2);           // fontDirectionHint
        BinaryPrimitives.WriteInt16BigEndian(s[50..], 0);           // indexToLocFormat
        BinaryPrimitives.WriteInt16BigEndian(s[52..], 0);           // glyphDataFormat
        return b;
    }

    private static byte[] BuildHhea(int ascent, int descent, int advanceMax, int numGlyphs)
    {
        var b = new byte[36];
        var s = b.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(s, 0x00010000);
        BinaryPrimitives.WriteInt16BigEndian(s[4..], (short)ascent);
        BinaryPrimitives.WriteInt16BigEndian(s[6..], (short)descent);
        BinaryPrimitives.WriteInt16BigEndian(s[8..], 0);            // lineGap
        BinaryPrimitives.WriteUInt16BigEndian(s[10..], (ushort)advanceMax);
        // min side bearings / extents left 0.
        BinaryPrimitives.WriteInt16BigEndian(s[18..], 1);          // caretSlopeRise
        BinaryPrimitives.WriteUInt16BigEndian(s[34..], (ushort)numGlyphs); // numberOfHMetrics
        return b;
    }

    private static byte[] BuildHmtx(List<BuiltGlyph> glyphs)
    {
        var b = new byte[glyphs.Count * 4];
        var s = b.AsSpan();
        for (int i = 0; i < glyphs.Count; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(s[(i * 4)..], (ushort)Math.Clamp(glyphs[i].Outline.Width, 0, 65535));
            BinaryPrimitives.WriteInt16BigEndian(s[(i * 4 + 2)..], (short)glyphs[i].Outline.LeftSideBearing());
        }
        return b;
    }

    private static byte[] BuildMaxp(int numGlyphs)
    {
        var b = new byte[6];
        BinaryPrimitives.WriteUInt32BigEndian(b, 0x00005000); // 0.5 (CFF)
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), (ushort)numGlyphs);
        return b;
    }

    private static byte[] BuildOs2(FontInfo info, int ascent, int descent)
    {
        var b = new byte[96]; // version 4
        var s = b.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(s, 4);
        BinaryPrimitives.WriteInt16BigEndian(s[2..], 500);          // xAvgCharWidth (approx)
        BinaryPrimitives.WriteUInt16BigEndian(s[4..], (ushort)WeightClass(info.Weight));
        BinaryPrimitives.WriteUInt16BigEndian(s[6..], 5);           // usWidthClass = medium
        BinaryPrimitives.WriteUInt16BigEndian(s[8..], 0);           // fsType = installable
        // subscript/superscript/strikeout (10 shorts = 20 bytes) at 10..30 left 0.
        // panose (10 bytes) at 32..42 left 0.
        // ulUnicodeRange (16 bytes) at 42..58 left 0.
        WriteTag(s[58..], info.FontName);                          // achVendID (4) — reuse first chars
        ushort sel = 0;
        if (info.ItalicAngle != 0) sel |= 0x01;
        if (IsBold(info.Weight)) sel |= 0x20;
        if (sel == 0) sel = 0x40;                                  // REGULAR
        BinaryPrimitives.WriteUInt16BigEndian(s[62..], sel);       // fsSelection
        BinaryPrimitives.WriteUInt16BigEndian(s[64..], 0x20);      // usFirstCharIndex
        BinaryPrimitives.WriteUInt16BigEndian(s[66..], 0xFFFF);    // usLastCharIndex
        BinaryPrimitives.WriteInt16BigEndian(s[68..], (short)ascent);   // sTypoAscender
        BinaryPrimitives.WriteInt16BigEndian(s[70..], (short)descent);  // sTypoDescender
        BinaryPrimitives.WriteInt16BigEndian(s[72..], 0);          // sTypoLineGap
        BinaryPrimitives.WriteUInt16BigEndian(s[74..], (ushort)ascent);     // usWinAscent
        BinaryPrimitives.WriteUInt16BigEndian(s[76..], (ushort)(-descent)); // usWinDescent
        // ulCodePageRange (8 bytes) at 78..86 left 0.
        BinaryPrimitives.WriteInt16BigEndian(s[86..], (short)(ascent / 2)); // sxHeight
        BinaryPrimitives.WriteInt16BigEndian(s[88..], (short)ascent);       // sCapHeight
        return b;
    }

    private static byte[] BuildName(FontInfo info)
    {
        string family = Clean(info.FamilyName);
        string subfamily = Subfamily(info);
        string full = Clean(info.FullName);
        string ps = Clean(info.FontName).Replace(" ", "");
        string version = "Version " + info.Version;
        var records = new (int Id, string Val)[]
        {
            (0, info.Notice.Length > 0 ? Clean(info.Notice) : "Converted by FontVault"),
            (1, family), (2, subfamily), (3, ps), (4, full),
            (5, version), (6, ps),
        };

        var strings = new List<byte>();
        var entries = new List<(int Platform, int Enc, int Lang, int Id, int Off, int Len)>();
        foreach (var (id, val) in records)
        {
            byte[] utf16 = Encoding.BigEndianUnicode.GetBytes(val);
            entries.Add((3, 1, 0x409, id, strings.Count, utf16.Length)); // Windows BMP
            strings.AddRange(utf16);
            byte[] mac = Encoding.ASCII.GetBytes(val);
            entries.Add((1, 0, 0, id, strings.Count, mac.Length));       // Mac Roman
            strings.AddRange(mac);
        }

        int count = entries.Count;
        int storageOffset = 6 + count * 12;
        var b = new byte[storageOffset + strings.Count];
        var s = b.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(s, 0);                 // format
        BinaryPrimitives.WriteUInt16BigEndian(s[2..], (ushort)count);
        BinaryPrimitives.WriteUInt16BigEndian(s[4..], (ushort)storageOffset);
        int rec = 6;
        foreach (var e in entries)
        {
            BinaryPrimitives.WriteUInt16BigEndian(s[rec..], (ushort)e.Platform);
            BinaryPrimitives.WriteUInt16BigEndian(s[(rec + 2)..], (ushort)e.Enc);
            BinaryPrimitives.WriteUInt16BigEndian(s[(rec + 4)..], (ushort)e.Lang);
            BinaryPrimitives.WriteUInt16BigEndian(s[(rec + 6)..], (ushort)e.Id);
            BinaryPrimitives.WriteUInt16BigEndian(s[(rec + 8)..], (ushort)e.Len);
            BinaryPrimitives.WriteUInt16BigEndian(s[(rec + 10)..], (ushort)e.Off);
            rec += 12;
        }
        strings.CopyTo(b, storageOffset);
        return b;
    }

    private static byte[] BuildPost(FontInfo info)
    {
        var b = new byte[32];
        var s = b.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(s, 0x00030000); // version 3.0 (no names)
        BinaryPrimitives.WriteInt32BigEndian(s[4..], (int)Math.Round(info.ItalicAngle * 65536));
        BinaryPrimitives.WriteInt16BigEndian(s[8..], -100);   // underlinePosition
        BinaryPrimitives.WriteInt16BigEndian(s[10..], 50);    // underlineThickness
        BinaryPrimitives.WriteUInt32BigEndian(s[12..], info.IsFixedPitch ? 1u : 0u);
        return b;
    }

    // cmap: format 4 (platform 3, encoding 1), built from glyph-name -> Unicode (AGL subset + uniXXXX).
    private static byte[] BuildCmap(List<BuiltGlyph> glyphs)
    {
        var map = new SortedDictionary<int, int>(); // codepoint -> GID
        for (int gid = 1; gid < glyphs.Count; gid++)
        {
            int cp = GlyphList.ToUnicode(glyphs[gid].Name);
            if (cp >= 0 && cp <= 0xFFFF && !map.ContainsKey(cp)) map[cp] = gid;
        }

        // Segments of contiguous codepoints.
        var segs = new List<(int Start, int End, int StartGid)>();
        foreach (var kv in map)
        {
            if (segs.Count > 0 && kv.Key == segs[^1].End + 1 && kv.Value == segs[^1].StartGid + (segs[^1].End - segs[^1].Start) + 1)
            {
                var last = segs[^1]; segs[^1] = (last.Start, kv.Key, last.StartGid);
            }
            else segs.Add((kv.Key, kv.Key, kv.Value));
        }
        segs.Add((0xFFFF, 0xFFFF, 0)); // mandatory terminator maps to .notdef

        int segCount = segs.Count;
        int sub4Len = 16 + segCount * 8; // header + endCode/pad/startCode/idDelta/idRangeOffset arrays
        var sub = new byte[sub4Len];
        var ss = sub.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(ss, 4);
        BinaryPrimitives.WriteUInt16BigEndian(ss[2..], (ushort)sub4Len);
        BinaryPrimitives.WriteUInt16BigEndian(ss[4..], 0); // language
        int segX2 = segCount * 2;
        BinaryPrimitives.WriteUInt16BigEndian(ss[6..], (ushort)segX2);
        int sr = 2; while (sr * 2 <= segCount) sr *= 2; sr *= 2;
        int es = 0; { int t = sr / 2; while (t > 1) { t >>= 1; es++; } }
        BinaryPrimitives.WriteUInt16BigEndian(ss[8..], (ushort)sr);   // searchRange
        BinaryPrimitives.WriteUInt16BigEndian(ss[10..], (ushort)es);  // entrySelector
        BinaryPrimitives.WriteUInt16BigEndian(ss[12..], (ushort)(segX2 - sr)); // rangeShift
        int endBase = 14;
        int startBase = endBase + segX2 + 2;
        int deltaBase = startBase + segX2;
        int rangeBase = deltaBase + segX2;
        for (int i = 0; i < segCount; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(ss[(endBase + i * 2)..], (ushort)segs[i].End);
            BinaryPrimitives.WriteUInt16BigEndian(ss[(startBase + i * 2)..], (ushort)segs[i].Start);
            ushort delta = (ushort)((segs[i].StartGid - segs[i].Start) & 0xFFFF);
            BinaryPrimitives.WriteUInt16BigEndian(ss[(deltaBase + i * 2)..], delta);
            BinaryPrimitives.WriteUInt16BigEndian(ss[(rangeBase + i * 2)..], 0);
        }

        // cmap header: 1 table (3,1) -> subtable.
        int headerLen = 4 + 8;
        var b = new byte[headerLen + sub4Len];
        var s = b.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(s, 0);   // version
        BinaryPrimitives.WriteUInt16BigEndian(s[2..], 1); // numTables
        BinaryPrimitives.WriteUInt16BigEndian(s[4..], 3); // platform Windows
        BinaryPrimitives.WriteUInt16BigEndian(s[6..], 1); // encoding BMP
        BinaryPrimitives.WriteUInt32BigEndian(s[8..], (uint)headerLen);
        sub.CopyTo(b, headerLen);
        return b;
    }

    // ---- small text helpers ----

    private static string Subfamily(FontInfo info)
    {
        bool bold = IsBold(info.Weight), italic = info.ItalicAngle != 0;
        if (bold && italic) return "Bold Italic";
        if (bold) return "Bold";
        if (italic) return "Italic";
        return "Regular";
    }

    private static bool IsBold(string weight)
    {
        string w = weight.ToLowerInvariant();
        return w.Contains("bold") || w.Contains("black") || w.Contains("heavy");
    }

    private static int WeightClass(string weight) => weight.ToLowerInvariant() switch
    {
        var w when w.Contains("thin") => 100,
        var w when w.Contains("extralight") || w.Contains("ultralight") => 200,
        var w when w.Contains("light") => 300,
        var w when w.Contains("medium") => 500,
        var w when w.Contains("semibold") || w.Contains("demibold") || w.Contains("demi") => 600,
        var w when w.Contains("extrabold") || w.Contains("ultrabold") => 800,
        var w when w.Contains("black") || w.Contains("heavy") => 900,
        var w when w.Contains("bold") => 700,
        _ => 400,
    };

    private static string Clean(string s) => s.Replace("\0", "").Trim();

    private static void WriteTag(Span<byte> dst, string src)
    {
        for (int i = 0; i < 4; i++) dst[i] = (byte)(i < src.Length && src[i] < 128 ? src[i] : ' ');
    }

    private static string? ReadLiteral(string text, string key)
    {
        int i = text.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return null;
        i += key.Length;
        while (i < text.Length && (text[i] == ' ' || text[i] == '/')) i++;
        int j = i;
        while (j < text.Length && text[j] is not (' ' or '\t' or '\r' or '\n' or '/')) j++;
        return j > i ? text[i..j] : null;
    }

    private static string? ReadString(string text, string key)
    {
        int i = text.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return null;
        int open = text.IndexOf('(', i);
        int lineEnd = text.IndexOf('\n', i);
        if (open < 0 || (lineEnd >= 0 && open > lineEnd)) return ReadLiteral(text, key);
        int close = text.IndexOf(')', open + 1);
        return close > open ? text[(open + 1)..close] : null;
    }

    private static bool TryReadNumber(string text, string key, out double value)
    {
        value = 0;
        int i = text.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return false;
        i += key.Length;
        while (i < text.Length && text[i] == ' ') i++;
        int j = i;
        while (j < text.Length && (char.IsDigit(text[j]) || text[j] is '-' or '+' or '.' or 'e' or 'E')) j++;
        return double.TryParse(text[i..j], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static double[]? ReadArrayNumbers(string text, string key, int count)
    {
        int i = text.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return null;
        int open = text.IndexOf('{', i);
        int openB = text.IndexOf('[', i);
        if (openB >= 0 && (open < 0 || openB < open)) open = openB;
        if (open < 0) return null;
        int close = text.IndexOfAny(new[] { ']', '}' }, open + 1);
        if (close < 0) return null;
        var parts = text[(open + 1)..close].Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var result = new double[count];
        int k = 0;
        foreach (var p in parts)
        {
            if (k >= count) break;
            if (double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) result[k++] = v;
        }
        return k == count ? result : null;
    }

    private static int ReadInt(byte[] data, ref int p)
    {
        while (p < data.Length && data[p] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') p++;
        bool neg = false;
        if (p < data.Length && data[p] == '-') { neg = true; p++; }
        int v = 0; bool any = false;
        while (p < data.Length && data[p] is >= (byte)'0' and <= (byte)'9') { v = v * 10 + (data[p] - '0'); p++; any = true; }
        return any ? (neg ? -v : v) : 0;
    }

    // After the length token there's a token (RD / -|) then exactly one space, then binary.
    private static int SkipToBinary(byte[] data, int p)
    {
        while (p < data.Length && data[p] is (byte)' ' or (byte)'\t') p++;
        while (p < data.Length && data[p] is not ((byte)' ' or (byte)'\t')) p++; // skip RD / -|
        if (p < data.Length) p++; // single space before binary
        return p <= data.Length ? p : -1;
    }

    private static int IndexOf(ReadOnlySpan<byte> data, string token) => IndexOf(data, token, 0);

    private static int IndexOf(ReadOnlySpan<byte> data, string token, int start)
    {
        for (int i = Math.Max(0, start); i + token.Length <= data.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < token.Length; j++) if (data[i + j] != token[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    private static int IndexOf(byte[] data, string token, int start) => IndexOf((ReadOnlySpan<byte>)data, token, start);
    private static int IndexOf(byte[] data, string token) => IndexOf((ReadOnlySpan<byte>)data, token, 0);

    private static int IndexOfByte(byte[] data, byte b, int start)
    {
        for (int i = Math.Max(0, start); i < data.Length; i++) if (data[i] == b) return i;
        return -1;
    }

    // ---- glyph model ----

    public sealed class GlyphOutline
    {
        public int Width;
        public int Sbx;
        public List<Contour> Contours = new();
        public int LeftSideBearing()
        {
            int min = int.MaxValue;
            foreach (var c in Contours)
            {
                min = Math.Min(min, c.StartX);
                foreach (var seg in c.Segments)
                {
                    if (seg.IsCurve) { min = Math.Min(min, Math.Min(seg.X1, seg.X2)); }
                    min = Math.Min(min, seg.X);
                }
            }
            return min == int.MaxValue ? 0 : min;
        }
    }

    public sealed class Contour
    {
        public int StartX, StartY;
        public List<Seg> Segments = new();
    }

    public struct Seg
    {
        public bool IsCurve;
        public int X1, Y1, X2, Y2; // control points (curve only)
        public int X, Y;           // end point
    }

    public sealed record BuiltGlyph(string Name, GlyphOutline Outline);
}
