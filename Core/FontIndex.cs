using System.IO.MemoryMappedFiles;
using System.Text;

namespace FontVault.Core;

/// <summary>
/// Binary index fontvault.idx.
/// Layout: fixed header | string section | heavy-block section | entry section | validation CRC32.
/// Header offsets make the physical order transparent to the reader.
/// The trailing CRC32 covers the light ranges only (header + strings + entries): startup never
/// reads the heavy section; heavy blocks are bounds-guarded at read time instead.
/// Format change => version bump => regeneration by rescan (no migration).
/// </summary>
public static class IndexFormat
{
    public const uint Magic = 0x58495646; // "FVIX" little-endian
    public const int Version = 3;         // v3: CRC restricted to light sections (large-scale startup)
    public const int HeaderSize = 56;
    public const int EntryRecordSize = 70;
    public const string FileName = "fontvault.idx";
}

public static class IndexWriter
{
    private static void WriteTag(BinaryWriter bw, string tag)
    {
        Span<byte> bytes = stackalloc byte[4];
        bytes.Fill((byte)' ');
        for (int i = 0; i < tag.Length && i < 4; i++) bytes[i] = (byte)tag[i];
        bw.Write(bytes);
    }

    /// <summary>Writes the full index to <paramref name="path"/> (temporary path; atomic replace by the caller).</summary>
    public static void Write(string path, IReadOnlyList<FontEntry> entries, Func<FontEntry, HeavyData> heavyProvider)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        using var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);

        // Placeholder header.
        bw.Write(new byte[IndexFormat.HeaderSize]);

        // String section: deduplicated pool.
        var stringIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var stringList = new List<string>();
        int Id(string s)
        {
            if (stringIds.TryGetValue(s, out int id)) return id;
            id = stringList.Count;
            stringList.Add(s);
            stringIds[s] = id;
            return id;
        }
        // Pre-collect so the string section can be written before the entries.
        foreach (var e in entries)
        {
            Id(e.WindowsDisplayName); Id(e.FamilyName); Id(e.TypographicFamilyName);
            Id(e.Style); Id(e.TypographicSubfamily); Id(e.FullName);
            Id(e.PostScriptName); Id(e.Version); Id(e.VaultRelPath);
        }
        long stringsOffset = fs.Position;
        bw.Write(stringList.Count);
        foreach (var s in stringList) bw.Write(s);

        // Heavy-block section: sequential blocks, absolute offset remembered per entry.
        long heavyOffset = fs.Position;
        foreach (var e in entries)
        {
            var heavy = e.Heavy ?? heavyProvider(e);
            e.HeavyOffset = fs.Position;
            bw.Write(heavy.Coverage.Count);
            foreach (var iv in heavy.Coverage) { bw.Write(iv.Start); bw.Write(iv.End); }
            bw.Write(heavy.Axes.Count);
            foreach (var axis in heavy.Axes)
            {
                WriteTag(bw, axis.Tag);
                bw.Write(axis.Min);
                bw.Write(axis.Default);
                bw.Write(axis.Max);
            }
            bw.Write(heavy.Features.Count);
            foreach (var feature in heavy.Features) WriteTag(bw, feature);
            bw.Write(heavy.OriginalPaths.Count);
            foreach (var p in heavy.OriginalPaths)
            {
                var bytes = Encoding.UTF8.GetBytes(p);
                bw.Write(bytes.Length);
                bw.Write(bytes);
            }
        }
        long heavyLength = fs.Position - heavyOffset;

        // Entry section: fixed-size records.
        long entriesOffset = fs.Position;
        long vaultTotalBytes = 0;
        foreach (var e in entries)
        {
            bw.Write(Id(e.WindowsDisplayName)); bw.Write(Id(e.FamilyName)); bw.Write(Id(e.TypographicFamilyName));
            bw.Write(Id(e.Style)); bw.Write(Id(e.TypographicSubfamily)); bw.Write(Id(e.FullName));
            bw.Write(Id(e.PostScriptName)); bw.Write(Id(e.Version)); bw.Write(Id(e.VaultRelPath));
            bw.Write(e.GlyphCount);
            bw.Write((byte)e.Format);
            bw.Write((byte)e.Extension);
            bw.Write((byte)(e.IsVariableFont ? 1 : 0));
            bw.Write(e.MetadataScore);
            bw.Write(e.FileSize);
            bw.Write(e.Crc32);
            bw.Write(e.ScanDateTicks);
            bw.Write(e.HeavyOffset);
            vaultTotalBytes += e.FileSize;
        }

        // Final header.
        fs.Position = 0;
        bw.Write(IndexFormat.Magic);
        bw.Write(IndexFormat.Version);
        bw.Write(entries.Count);
        bw.Write(entries.Count);      // vaultFileCount: one vault file per entry
        bw.Write(vaultTotalBytes);
        bw.Write(stringsOffset);
        bw.Write(entriesOffset);
        bw.Write(heavyOffset);
        bw.Write(heavyLength);
        bw.Flush();

        // Validation CRC32 over the light ranges: [0, heavyOffset) + [entriesOffset, end).
        uint crc = ComputeLightCrc(fs, heavyOffset, entriesOffset, fs.Length);
        fs.Position = fs.Length;
        bw.Write(crc);
        bw.Flush();
    }

    internal static uint ComputeLightCrc(FileStream fs, long heavyOffset, long entriesOffset, long end,
        Action<long>? bytesDone = null)
    {
        uint state = Crc32.Initial;
        var buffer = new byte[1 << 16];
        long total = 0;
        void AppendRange(long from, long to)
        {
            fs.Position = from;
            long remaining = to - from;
            while (remaining > 0)
            {
                int read = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read <= 0) throw new InvalidDataException("Truncated index.");
                state = Crc32.Append(state, buffer.AsSpan(0, read));
                remaining -= read;
                total += read;
                bytesDone?.Invoke(total);
            }
        }
        AppendRange(0, heavyOffset);
        AppendRange(entriesOffset, end);
        return Crc32.Finalize(state);
    }
}

/// <summary>
/// Index reading: sequential load of light fields at startup,
/// on-demand access to heavy blocks through a memory-mapped file.
/// </summary>
public sealed class IndexReader : IDisposable
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;

    public FontEntry[] Entries { get; }
    public int VaultFileCount { get; }
    public long VaultTotalBytes { get; }
    public string IndexPath { get; }

    private IndexReader(string path, FontEntry[] entries, int vaultFileCount, long vaultTotalBytes,
        MemoryMappedFile mmf, MemoryMappedViewAccessor accessor)
    {
        IndexPath = path;
        Entries = entries;
        VaultFileCount = vaultFileCount;
        VaultTotalBytes = vaultTotalBytes;
        _mmf = mmf;
        _accessor = accessor;
    }

    /// <summary>Loads the index. <paramref name="progress"/> reports 0..1 (CRC pass, then entry records).</summary>
    public static IndexReader Load(string path, IProgress<double>? progress = null)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

        long fileLength = fs.Length;
        if (fileLength < IndexFormat.HeaderSize + 4)
            throw new InvalidDataException("Truncated index.");

        if (br.ReadUInt32() != IndexFormat.Magic)
            throw new InvalidDataException("Invalid index magic.");
        int version = br.ReadInt32();
        if (version != IndexFormat.Version)
            throw new InvalidDataException($"Index version {version} not supported: regeneration by rescan required.");
        int entryCount = br.ReadInt32();
        int vaultFileCount = br.ReadInt32();
        long vaultTotalBytes = br.ReadInt64();
        long stringsOffset = br.ReadInt64();
        long entriesOffset = br.ReadInt64();
        long heavyOffset = br.ReadInt64();
        long heavyLength = br.ReadInt64();
        if (heavyOffset < IndexFormat.HeaderSize || entriesOffset != heavyOffset + heavyLength ||
            entriesOffset > fileLength - 4)
            throw new InvalidDataException("Inconsistent index offsets.");

        // Validation CRC over the light ranges only: the heavy section is never read at startup.
        long lightBytes = heavyOffset + (fileLength - 4 - entriesOffset);
        uint computed = IndexWriter.ComputeLightCrc(fs, heavyOffset, entriesOffset, fileLength - 4,
            progress == null ? null : done => progress.Report(0.6 * done / lightBytes));
        fs.Position = fileLength - 4;
        uint stored = br.ReadUInt32();
        if (computed != stored)
            throw new InvalidDataException("Invalid index CRC: index is corrupted.");

        fs.Position = stringsOffset;
        int stringCount = br.ReadInt32();
        var strings = new string[stringCount];
        for (int i = 0; i < stringCount; i++) strings[i] = br.ReadString();

        fs.Position = entriesOffset;
        var entries = new FontEntry[entryCount];
        for (int i = 0; i < entryCount; i++)
        {
            if (progress != null && (i & 0xFFFF) == 0)
                progress.Report(0.65 + 0.35 * i / entryCount);
            var e = new FontEntry
            {
                WindowsDisplayName = strings[br.ReadInt32()],
                FamilyName = strings[br.ReadInt32()],
                TypographicFamilyName = strings[br.ReadInt32()],
                Style = strings[br.ReadInt32()],
                TypographicSubfamily = strings[br.ReadInt32()],
                FullName = strings[br.ReadInt32()],
                PostScriptName = strings[br.ReadInt32()],
                Version = strings[br.ReadInt32()],
                VaultRelPath = strings[br.ReadInt32()],
                GlyphCount = br.ReadUInt16(),
                Format = (FontFormat)br.ReadByte(),
                Extension = (FontExt)br.ReadByte(),
            };
            e.IsVariableFont = br.ReadByte() != 0;
            e.MetadataScore = br.ReadByte();
            e.FileSize = br.ReadInt64();
            e.Crc32 = br.ReadUInt32();
            e.ScanDateTicks = br.ReadInt64();
            e.HeavyOffset = br.ReadInt64();
            entries[i] = e;
        }

        var mapStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var mmf = MemoryMappedFile.CreateFromFile(mapStream, null, 0, MemoryMappedFileAccess.Read,
            HandleInheritability.None, leaveOpen: false);
        var accessor = mmf.CreateViewAccessor(0, fileLength, MemoryMappedFileAccess.Read);

        return new IndexReader(path, entries, vaultFileCount, vaultTotalBytes, mmf, accessor);
    }

    private string ReadTag(ref long pos)
    {
        var bytes = new byte[4];
        _accessor.ReadArray(pos, bytes, 0, 4); pos += 4;
        return Encoding.ASCII.GetString(bytes).TrimEnd(' ');
    }

    /// <summary>
    /// Reads an entry's heavy block on demand (never loaded globally).
    /// The heavy section is outside the validation CRC, so all counts are sanity-capped.
    /// </summary>
    public HeavyData GetHeavy(FontEntry entry)
    {
        if (entry.Heavy != null) return entry.Heavy;
        var heavy = new HeavyData();
        if (entry.HeavyOffset < 0) return heavy;

        static void Guard(bool ok)
        {
            if (!ok) throw new InvalidDataException("Corrupted heavy block.");
        }

        long pos = entry.HeavyOffset;
        int coverageCount = _accessor.ReadInt32(pos); pos += 4;
        Guard(coverageCount is >= 0 and <= 600_000);
        heavy.Coverage.Capacity = coverageCount;
        for (int i = 0; i < coverageCount; i++)
        {
            int start = _accessor.ReadInt32(pos); pos += 4;
            int end = _accessor.ReadInt32(pos); pos += 4;
            heavy.Coverage.Add(new UnicodeInterval(start, end));
        }
        int axesCount = _accessor.ReadInt32(pos); pos += 4;
        Guard(axesCount is >= 0 and <= 512);
        for (int i = 0; i < axesCount; i++)
        {
            string tag = ReadTag(ref pos);
            float min = _accessor.ReadSingle(pos); pos += 4;
            float def = _accessor.ReadSingle(pos); pos += 4;
            float max = _accessor.ReadSingle(pos); pos += 4;
            heavy.Axes.Add(new VariableAxis(tag, min, def, max));
        }
        int featureCount = _accessor.ReadInt32(pos); pos += 4;
        Guard(featureCount is >= 0 and <= 4096);
        for (int i = 0; i < featureCount; i++)
            heavy.Features.Add(ReadTag(ref pos));
        int pathCount = _accessor.ReadInt32(pos); pos += 4;
        Guard(pathCount is >= 0 and <= 100_000);
        for (int i = 0; i < pathCount; i++)
        {
            int len = _accessor.ReadInt32(pos); pos += 4;
            Guard(len is >= 0 and <= 32_768);
            var bytes = new byte[len];
            _accessor.ReadArray(pos, bytes, 0, len); pos += len;
            heavy.OriginalPaths.Add(Encoding.UTF8.GetString(bytes));
        }
        return heavy;
    }

    public void Dispose()
    {
        _accessor.Dispose();
        _mmf.Dispose();
    }
}
