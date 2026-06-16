using System.Buffers.Binary;
using System.Text;

namespace FontVault.Fonts;

/// <summary>
/// Builds a minimal CFF table from glyph outlines: Type2 charstrings (unhinted), Name/Top-DICT/
/// String/GlobalSubr INDEXes, format-0 charset, and a Private DICT. Offsets in the Top DICT use the
/// fixed 5-byte (29) encoding so the layout resolves in a single pass.
/// </summary>
internal static class CffWriter
{
    public static byte[] Build(Type1Converter.FontInfo info, List<Type1Converter.BuiltGlyph> glyphs)
    {
        int n = glyphs.Count;

        var charStrings = new List<byte[]>(n);
        foreach (var g in glyphs) charStrings.Add(EmitCharString(g.Outline));

        // Custom strings (SID >= 391).
        var strings = new List<string>();
        var sidMap = new Dictionary<string, int>(StringComparer.Ordinal);
        int Sid(string s)
        {
            if (string.IsNullOrEmpty(s)) s = " ";
            if (sidMap.TryGetValue(s, out int v)) return v;
            v = 391 + strings.Count;
            strings.Add(s); sidMap[s] = v;
            return v;
        }

        string psName = (info.FontName.Length > 0 ? info.FontName : "Untitled").Replace(" ", "");
        int versionSid = Sid("Version " + info.Version);
        int noticeSid = info.Notice.Length > 0 ? Sid(info.Notice) : -1;
        int fullSid = Sid(info.FullName.Length > 0 ? info.FullName : psName);
        int familySid = Sid(info.FamilyName.Length > 0 ? info.FamilyName : psName);
        int weightSid = info.Weight.Length > 0 ? Sid(info.Weight) : -1;
        var charsetSids = new int[n];
        for (int gid = 1; gid < n; gid++) charsetSids[gid] = Sid(glyphs[gid].Name);

        byte[] nameIndex = BuildIndex(new List<byte[]> { Encoding.ASCII.GetBytes(psName) });
        var stringItems = new List<byte[]>(strings.Count);
        foreach (var s in strings) stringItems.Add(Encoding.UTF8.GetBytes(s));
        byte[] stringIndex = BuildIndex(stringItems);
        byte[] globalSubrIndex = BuildIndex(new List<byte[]>());
        byte[] charStringsIndex = BuildIndex(charStrings);
        byte[] charset = BuildCharset(charsetSids);
        byte[] privateDict = BuildPrivate();

        byte[] BuildTop(int charsetOff, int charStringsOff, int privOff, int privSize)
        {
            var td = new List<byte>();
            void Op(int sid, int op) { if (sid >= 0) { EncodeDictInt(td, sid, false); td.Add((byte)op); } }
            Op(versionSid, 0);
            Op(noticeSid, 1);
            Op(fullSid, 2);
            Op(familySid, 3);
            Op(weightSid, 4);
            foreach (int v in info.FontBBox) EncodeDictInt(td, v, false);
            td.Add(5); // FontBBox
            EncodeDictInt(td, charsetOff, true); td.Add(15);
            EncodeDictInt(td, charStringsOff, true); td.Add(17);
            EncodeDictInt(td, privSize, false); EncodeDictInt(td, privOff, true); td.Add(18);
            return td.ToArray();
        }

        byte[] topIndexPlaceholder = BuildIndex(new List<byte[]> { BuildTop(0, 0, 0, privateDict.Length) });

        const int header = 4;
        int baseAfterTop = header + nameIndex.Length + topIndexPlaceholder.Length + stringIndex.Length + globalSubrIndex.Length;
        int charsetOff = baseAfterTop;
        int charStringsOff = charsetOff + charset.Length;
        int privOff = charStringsOff + charStringsIndex.Length;

        byte[] topIndex = BuildIndex(new List<byte[]> { BuildTop(charsetOff, charStringsOff, privOff, privateDict.Length) });
        if (topIndex.Length != topIndexPlaceholder.Length)
            throw new InvalidDataException("CFF layout: Top DICT size changed unexpectedly.");

        var outp = new List<byte>(privOff + privateDict.Length)
        {
            1, 0, 4, 4, // header: major, minor, hdrSize, offSize
        };
        outp.AddRange(nameIndex);
        outp.AddRange(topIndex);
        outp.AddRange(stringIndex);
        outp.AddRange(globalSubrIndex);
        outp.AddRange(charset);
        outp.AddRange(charStringsIndex);
        outp.AddRange(privateDict);
        return outp.ToArray();
    }

    private static byte[] BuildCharset(int[] sids)
    {
        var b = new byte[1 + (sids.Length - 1) * 2];
        b[0] = 0; // format 0
        for (int gid = 1; gid < sids.Length; gid++)
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(1 + (gid - 1) * 2), (ushort)sids[gid]);
        return b;
    }

    private static byte[] BuildPrivate()
    {
        var p = new List<byte>();
        EncodeDictInt(p, 0, false); p.Add(20); // defaultWidthX
        EncodeDictInt(p, 0, false); p.Add(21); // nominalWidthX
        return p.ToArray();
    }

    private static byte[] BuildIndex(List<byte[]> items)
    {
        if (items.Count == 0) return new byte[] { 0, 0 };
        int count = items.Count;
        int dataLen = 0;
        foreach (var it in items) dataLen += it.Length;
        int last = dataLen + 1;
        int offSize = last <= 0xFF ? 1 : last <= 0xFFFF ? 2 : last <= 0xFFFFFF ? 3 : 4;

        var b = new List<byte>(3 + (count + 1) * offSize + dataLen)
        {
            (byte)(count >> 8), (byte)count, (byte)offSize,
        };
        int off = 1;
        void WriteOff(int o) { for (int k = offSize - 1; k >= 0; k--) b.Add((byte)(o >> (k * 8))); }
        WriteOff(off);
        foreach (var it in items) { off += it.Length; WriteOff(off); }
        foreach (var it in items) b.AddRange(it);
        return b.ToArray();
    }

    private static void EncodeDictInt(List<byte> dst, int v, bool fixed5)
    {
        if (fixed5)
        {
            dst.Add(29); dst.Add((byte)(v >> 24)); dst.Add((byte)(v >> 16)); dst.Add((byte)(v >> 8)); dst.Add((byte)v);
            return;
        }
        if (v is >= -107 and <= 107) { dst.Add((byte)(v + 139)); return; }
        if (v is >= 108 and <= 1131) { int b = v - 108; dst.Add((byte)(247 + (b >> 8))); dst.Add((byte)(b & 0xFF)); return; }
        if (v is >= -1131 and <= -108) { int b = -v - 108; dst.Add((byte)(251 + (b >> 8))); dst.Add((byte)(b & 0xFF)); return; }
        if (v is >= -32768 and <= 32767) { dst.Add(28); dst.Add((byte)(v >> 8)); dst.Add((byte)v); return; }
        dst.Add(29); dst.Add((byte)(v >> 24)); dst.Add((byte)(v >> 16)); dst.Add((byte)(v >> 8)); dst.Add((byte)v);
    }

    private static byte[] EmitCharString(Type1Converter.GlyphOutline outline)
    {
        var cs = new List<byte>();
        if (outline.Contours.Count == 0)
        {
            EmitNum(cs, outline.Width);
            cs.Add(14); // endchar
            return cs.ToArray();
        }
        int cx = 0, cy = 0;
        bool widthDone = false;
        foreach (var c in outline.Contours)
        {
            if (!widthDone) { EmitNum(cs, outline.Width); widthDone = true; }
            EmitNum(cs, c.StartX - cx); EmitNum(cs, c.StartY - cy); cs.Add(21); // rmoveto
            cx = c.StartX; cy = c.StartY;
            foreach (var seg in c.Segments)
            {
                if (!seg.IsCurve)
                {
                    EmitNum(cs, seg.X - cx); EmitNum(cs, seg.Y - cy); cs.Add(5); // rlineto
                    cx = seg.X; cy = seg.Y;
                }
                else
                {
                    EmitNum(cs, seg.X1 - cx); EmitNum(cs, seg.Y1 - cy);
                    EmitNum(cs, seg.X2 - seg.X1); EmitNum(cs, seg.Y2 - seg.Y1);
                    EmitNum(cs, seg.X - seg.X2); EmitNum(cs, seg.Y - seg.Y2);
                    cs.Add(8); // rrcurveto
                    cx = seg.X; cy = seg.Y;
                }
            }
        }
        cs.Add(14); // endchar
        return cs.ToArray();
    }

    private static void EmitNum(List<byte> cs, int v)
    {
        if (v is >= -107 and <= 107) { cs.Add((byte)(v + 139)); return; }
        if (v is >= 108 and <= 1131) { int b = v - 108; cs.Add((byte)(247 + (b >> 8))); cs.Add((byte)(b & 0xFF)); return; }
        if (v is >= -1131 and <= -108) { int b = -v - 108; cs.Add((byte)(251 + (b >> 8))); cs.Add((byte)(b & 0xFF)); return; }
        if (v is >= -32768 and <= 32767) { cs.Add(28); cs.Add((byte)(v >> 8)); cs.Add((byte)v); return; }
        int f = v << 16; // 255 = 16.16 fixed
        cs.Add(255); cs.Add((byte)(f >> 24)); cs.Add((byte)(f >> 16)); cs.Add((byte)(f >> 8)); cs.Add((byte)f);
    }
}
