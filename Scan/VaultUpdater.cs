using System.Buffers;
using FontVault.Core;
using FontVault.Fonts;

namespace FontVault.Scan;

public sealed class UpdateResult
{
    public int Scanned;
    public int TotalEntries;
    public int DuplicatesRemoved;
    public int Renamed;
    public int Errors;
}

/// <summary>
/// Vault repair ("Update"): the vault on disk is authoritative. Re-parses every font file,
/// removes byte-identical duplicates, renames files to the §3 naming convention, prunes empty
/// folders, and rebuilds the index from the reconciled set. Distinct fonts are never deleted
/// (only exact duplicates are); unreadable files are logged and left in place.
/// Idempotent and self-healing: an interrupted run leaves valid font files and is fixed by re-running.
/// </summary>
public static class VaultUpdater
{
    private const long MaxFontFileSize = 512L * 1024 * 1024;
    private const string StagingRoot = ".fvupdate";
    private static readonly HashSet<string> FontExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".otf", ".ttf", ".woff", ".woff2", ".eot" };

    /// <summary>Blocking: call off the UI thread. <paramref name="workDir"/> holds the index (the exe folder).</summary>
    public static UpdateResult Run(string vaultRoot, string workDir,
        IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        vaultRoot = Path.GetFullPath(vaultRoot);
        var result = new UpdateResult();
        string phase = "Reading fonts";
        double percent = 0;
        void Report() => progress?.Report(new ScanProgress
        {
            Phase = phase,
            Percent = percent,
            Copied = result.Renamed,
            Errors = result.Errors,
        });

        using var log = new ScanLog(Path.Combine(workDir, "scan.log"));
        log.Note($"==== Update (vault repair) started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====");
        log.Note($"  vault : {vaultRoot}");
        log.Note("Per-file lines below: stage 'update' = unreadable file kept / duplicate removed / rename issue (with reason). Summary at the end.");

        // 1. Discover + parse every vault font file (parallel). Leftover staged files from an
        //    interrupted run live under .fvupdate and are enumerated too, so they are recovered.
        phase = "Scanning the vault folder";
        Report();
        var files = SafeEnumerateFonts(vaultRoot, log);
        phase = "Reading fonts";
        Report();

        var records = new List<FontEntry>(files.Count);
        int processed = 0, errors = 0;
        Parallel.ForEach(files,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1), CancellationToken = ct },
            path =>
            {
                var entry = TryParse(path, vaultRoot, log);
                if (entry != null) { lock (records) records.Add(entry); }
                else Interlocked.Increment(ref errors);
                int done = Interlocked.Increment(ref processed);
                if (done % 32 == 0)
                {
                    phase = $"Reading fonts ({done:N0}/{files.Count:N0})";
                    percent = files.Count == 0 ? 70 : 70.0 * done / files.Count;
                    Report();
                }
            });
        ct.ThrowIfCancellationRequested();
        result.Scanned = files.Count;
        result.Errors = errors;

        // 2. Remove byte-identical duplicates: (size, CRC32) key + binary comparison on collision.
        phase = "Removing exact duplicates";
        percent = 70; Report();
        var byKey = new Dictionary<(long, uint), List<FontEntry>>(records.Count);
        var kept = new List<FontEntry>(records.Count);
        for (int i = 0; i < records.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var rec = records[i];
            string recPath = rec.Heavy!.OriginalPaths[0];
            var key = (rec.FileSize, rec.Crc32);
            if (!byKey.TryGetValue(key, out var group)) byKey[key] = group = new List<FontEntry>();
            FontEntry? dup = null;
            foreach (var c in group)
                if (BestVersionSelector.FilesEqual(c.Heavy!.OriginalPaths[0], recPath)) { dup = c; break; }
            if (dup != null)
            {
                if (TryDelete(recPath))
                {
                    result.DuplicatesRemoved++;
                    log.Write("update", recPath, $"Duplicate removed (identical to {dup.Heavy!.OriginalPaths[0]}).");
                }
            }
            else
            {
                group.Add(rec);
                kept.Add(rec);
            }
            if ((i & 1023) == 0) { percent = records.Count == 0 ? 80 : 70 + 10.0 * i / records.Count; Report(); }
        }

        // 3. Canonical naming: assign each kept entry its §3 path, collisions get the _cCRC32 suffix
        //    (and a counter on the rare same-name + same-CRC, different-content case).
        kept.Sort((a, b) => string.Compare(a.Heavy!.OriginalPaths[0], b.Heavy!.OriginalPaths[0],
            StringComparison.OrdinalIgnoreCase));
        var usedRel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in kept)
        {
            string rel = VaultNamer.BuildRelPath(e, withCrcSuffix: false);
            if (usedRel.Contains(rel)) rel = VaultNamer.BuildRelPath(e, withCrcSuffix: true);
            if (usedRel.Contains(rel))
            {
                string baseRel = VaultNamer.BuildRelPath(e, withCrcSuffix: true);
                string ext = FontEntry.ExtensionString(e.Extension);
                string stem = baseRel[..^ext.Length];
                int n = 2;
                do { rel = $"{stem}_{n++}{ext}"; } while (usedRel.Contains(rel));
            }
            usedRel.Add(rel);
            e.VaultRelPath = rel;
        }

        // 4. Apply moves (two-phase via a staging folder, so target-occupied / swap cases cannot clash).
        phase = "Fixing file names";
        percent = 82; Report();
        string stagingDir = Path.Combine(vaultRoot, StagingRoot, Guid.NewGuid().ToString("N"));
        var staged = new List<(FontEntry Entry, string Temp, string Dest)>();
        int stageIndex = 0;
        for (int i = 0; i < kept.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var e = kept[i];
            string cur = e.Heavy!.OriginalPaths[0];
            string dest = Path.Combine(vaultRoot, e.VaultRelPath);
            if (!string.Equals(Path.GetFullPath(cur), dest, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    Directory.CreateDirectory(stagingDir);
                    string temp = Path.Combine(stagingDir, $"{stageIndex++}{FontEntry.ExtensionString(e.Extension)}");
                    File.Move(cur, temp);
                    staged.Add((e, temp, dest));
                }
                catch (Exception ex)
                {
                    log.Write("update", cur, "Could not stage for rename: " + ex.Message);
                    Interlocked.Increment(ref result.Errors);
                }
            }
            if ((i & 255) == 0) { percent = kept.Count == 0 ? 90 : 82 + 8.0 * i / kept.Count; Report(); }
        }
        int placed = 0;
        foreach (var (e, temp, dest) in staged)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (File.Exists(dest)) { try { File.SetAttributes(dest, FileAttributes.Normal); } catch { } }
                File.Move(temp, dest, overwrite: true);
                e.Heavy!.OriginalPaths[0] = dest;
                result.Renamed++;
                log.Write("update", dest, "Renamed to the naming convention.");
            }
            catch (Exception ex)
            {
                log.Write("update", dest, "Could not place file: " + ex.Message);
                Interlocked.Increment(ref result.Errors);
            }
            if ((++placed & 255) == 0) { percent = staged.Count == 0 ? 95 : 90 + 5.0 * placed / staged.Count; Report(); }
        }

        // 5. Prune empty folders (removes the staging tree and any folder emptied by the moves).
        phase = "Pruning empty folders";
        percent = 96; Report();
        foreach (var sub in SafeDirectories(vaultRoot)) PruneEmptyDirs(sub);

        // 6. Rebuild the index (atomic write) from the reconciled set.
        phase = "Rebuilding index";
        percent = 97; Report();
        kept.Sort((a, b) =>
        {
            int c = string.Compare(a.WindowsDisplayName, b.WindowsDisplayName, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare(a.EffectiveStyle, b.EffectiveStyle, StringComparison.OrdinalIgnoreCase);
        });
        string indexPath = Path.Combine(workDir, IndexFormat.FileName);
        string tmpIndex = indexPath + ".tmp";
        IndexWriter.Write(tmpIndex, kept, e => e.Heavy ?? new HeavyData());
        File.Move(tmpIndex, indexPath, overwrite: true);
        result.TotalEntries = kept.Count;

        phase = "Done";
        percent = 100;
        Report();

        log.Note("");
        log.Note($"==== Update summary {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====");
        log.Note($"  font files scanned         : {result.Scanned:N0}");
        log.Note($"  unreadable / errors        : {result.Errors:N0}   [stage 'update'; left in place]");
        log.Note($"  exact duplicates removed   : {result.DuplicatesRemoved:N0}");
        log.Note($"  files renamed / relocated  : {result.Renamed:N0}");
        log.Note($"  total fonts indexed (vault): {result.TotalEntries:N0}");
        log.Flush();
        return result;
    }

    private static FontEntry? TryParse(string path, string vaultRoot, ScanLog log)
    {
        var fontExt = FontEntry.ExtFromString(Path.GetExtension(path));
        if (fontExt == null) return null;
        byte[]? buffer = null;
        try
        {
            long length = new FileInfo(path).Length;
            if (length < 12 || length > MaxFontFileSize)
                throw new InvalidDataException($"File size out of bounds ({length} bytes).");
            buffer = ArrayPool<byte>.Shared.Rent((int)length);
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                1 << 16, FileOptions.SequentialScan))
            {
                fs.ReadExactly(buffer, 0, (int)length);
            }
            var span = buffer.AsSpan(0, (int)length);
            uint crc = Crc32.Compute(span);
            var parsed = FontFileReader.ExtractMetadata(span, fontExt.Value);
            return new FontEntry
            {
                WindowsDisplayName = parsed.WindowsDisplayName,
                FamilyName = parsed.FamilyName,
                TypographicFamilyName = parsed.TypographicFamilyName,
                Style = parsed.Style,
                TypographicSubfamily = parsed.TypographicSubfamily,
                FullName = parsed.FullName,
                PostScriptName = parsed.PostScriptName,
                Version = parsed.Version,
                GlyphCount = parsed.GlyphCount,
                Format = parsed.Format,
                Extension = fontExt.Value,
                IsVariableFont = parsed.IsVariableFont,
                MetadataScore = parsed.MetadataScore,
                License = parsed.License,
                FileSize = length,
                Crc32 = crc,
                ScanDateTicks = DateTime.UtcNow.Ticks,
                VaultRelPath = Path.GetRelativePath(vaultRoot, path),
                Heavy = new HeavyData
                {
                    Coverage = parsed.Coverage,
                    Axes = parsed.Axes,
                    Features = parsed.Features,
                    OriginalPaths = { path },
                },
            };
        }
        catch (Exception ex)
        {
            log.Write("update", path, ex.Message);
            return null;
        }
        finally
        {
            if (buffer != null) ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static List<string> SafeEnumerateFonts(string root, ScanLog log)
    {
        var result = new List<string>();
        var dirs = new Stack<string>();
        dirs.Push(root);
        while (dirs.Count > 0)
        {
            string dir = dirs.Pop();
            try
            {
                foreach (var f in Directory.GetFiles(dir))
                    if (!Path.GetFileName(f).StartsWith("._", StringComparison.Ordinal) && // macOS AppleDouble sidecar
                        FontExtensions.Contains(Path.GetExtension(f)))
                        result.Add(f);
                foreach (var d in Directory.GetDirectories(dir)) dirs.Push(d);
            }
            catch (Exception ex)
            {
                log.Write("update", dir, ex.Message);
            }
        }
        return result;
    }

    private static IEnumerable<string> SafeDirectories(string root)
    {
        try { return Directory.GetDirectories(root); }
        catch { return Array.Empty<string>(); }
    }

    private static void PruneEmptyDirs(string dir)
    {
        foreach (var sub in SafeDirectories(dir)) PruneEmptyDirs(sub);
        try
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch { /* best-effort */ }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
