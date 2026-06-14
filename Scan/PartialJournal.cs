using System.Text;
using FontVault.Core;

namespace FontVault.Scan;

/// <summary>
/// Append-only journal of entries extracted during the read phase (scan.partial).
/// File starts with a magic + format version; each record is length-prefixed and
/// followed by a CRC32: on reload, parsing stops at the first corrupted record
/// (interrupted write). Enables resume without reprocessing already-read files.
/// </summary>
public sealed class PartialJournal : IDisposable
{
    private static readonly byte[] Header = { (byte)'F', (byte)'V', (byte)'P', (byte)'J', 3, 0, 0, 0 };

    private readonly FileStream _stream;
    private readonly object _lock = new();

    public PartialJournal(string path)
    {
        _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        if (_stream.Length == 0)
        {
            _stream.Write(Header);
            _stream.Flush();
        }
    }

    public void Append(FontEntry entry)
    {
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            WriteEntry(bw, entry);
        var payload = ms.ToArray();
        uint crc = Crc32.Compute(payload);

        lock (_lock)
        {
            Span<byte> header = stackalloc byte[4];
            BitConverter.TryWriteBytes(header, payload.Length);
            _stream.Write(header);
            _stream.Write(payload);
            Span<byte> crcBytes = stackalloc byte[4];
            BitConverter.TryWriteBytes(crcBytes, crc);
            _stream.Write(crcBytes);
            _stream.Flush();
        }
    }

    public void Dispose() => _stream.Dispose();

    /// <summary>Reloads all valid entries; silently truncates at the first corruption or on a version mismatch.</summary>
    public static List<FontEntry> LoadAll(string path)
    {
        var entries = new List<FontEntry>();
        if (!File.Exists(path)) return entries;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var headerBytes = new byte[Header.Length];
        if (fs.Read(headerBytes, 0, headerBytes.Length) != headerBytes.Length ||
            !headerBytes.AsSpan().SequenceEqual(Header))
            return entries; // unknown or older journal format: start over

        var lenBytes = new byte[4];
        while (true)
        {
            if (fs.Read(lenBytes, 0, 4) != 4) break;
            int len = BitConverter.ToInt32(lenBytes);
            if (len <= 0 || len > 64 * 1024 * 1024 || fs.Position + len + 4 > fs.Length) break;
            var payload = new byte[len];
            fs.ReadExactly(payload);
            var crcBytes = new byte[4];
            if (fs.Read(crcBytes, 0, 4) != 4) break;
            if (Crc32.Compute(payload) != BitConverter.ToUInt32(crcBytes)) break;

            using var ms = new MemoryStream(payload);
            using var br = new BinaryReader(ms, Encoding.UTF8);
            entries.Add(ReadEntry(br));
        }
        return entries;
    }

    private static void WriteEntry(BinaryWriter bw, FontEntry e)
    {
        bw.Write(e.WindowsDisplayName); bw.Write(e.FamilyName); bw.Write(e.TypographicFamilyName);
        bw.Write(e.Style); bw.Write(e.TypographicSubfamily); bw.Write(e.FullName);
        bw.Write(e.PostScriptName); bw.Write(e.Version);
        bw.Write(e.GlyphCount);
        bw.Write((byte)e.Format);
        bw.Write((byte)e.Extension);
        bw.Write(e.IsVariableFont);
        bw.Write(e.MetadataScore);
        bw.Write((byte)e.License);
        bw.Write(e.FileSize);
        bw.Write(e.Crc32);
        bw.Write(e.ScanDateTicks);
        var heavy = e.Heavy ?? new HeavyData();
        bw.Write(heavy.Coverage.Count);
        foreach (var iv in heavy.Coverage) { bw.Write(iv.Start); bw.Write(iv.End); }
        bw.Write(heavy.Axes.Count);
        foreach (var axis in heavy.Axes)
        {
            bw.Write(axis.Tag);
            bw.Write(axis.Min); bw.Write(axis.Default); bw.Write(axis.Max);
        }
        bw.Write(heavy.Features.Count);
        foreach (var feature in heavy.Features) bw.Write(feature);
        bw.Write(heavy.OriginalPaths.Count);
        foreach (var p in heavy.OriginalPaths) bw.Write(p);
    }

    private static FontEntry ReadEntry(BinaryReader br)
    {
        var e = new FontEntry
        {
            WindowsDisplayName = br.ReadString(),
            FamilyName = br.ReadString(),
            TypographicFamilyName = br.ReadString(),
            Style = br.ReadString(),
            TypographicSubfamily = br.ReadString(),
            FullName = br.ReadString(),
            PostScriptName = br.ReadString(),
            Version = br.ReadString(),
            GlyphCount = br.ReadUInt16(),
            Format = (FontFormat)br.ReadByte(),
            Extension = (FontExt)br.ReadByte(),
            IsVariableFont = br.ReadBoolean(),
            MetadataScore = br.ReadByte(),
            License = (LicenseClass)br.ReadByte(),
            FileSize = br.ReadInt64(),
            Crc32 = br.ReadUInt32(),
            ScanDateTicks = br.ReadInt64(),
            Heavy = new HeavyData(),
        };
        int coverageCount = br.ReadInt32();
        for (int i = 0; i < coverageCount; i++)
            e.Heavy.Coverage.Add(new UnicodeInterval(br.ReadInt32(), br.ReadInt32()));
        int axesCount = br.ReadInt32();
        for (int i = 0; i < axesCount; i++)
            e.Heavy.Axes.Add(new VariableAxis(br.ReadString(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle()));
        int featureCount = br.ReadInt32();
        for (int i = 0; i < featureCount; i++)
            e.Heavy.Features.Add(br.ReadString());
        int pathCount = br.ReadInt32();
        for (int i = 0; i < pathCount; i++)
            e.Heavy.OriginalPaths.Add(br.ReadString());
        return e;
    }
}
