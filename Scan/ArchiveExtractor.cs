using System.IO.Compression;
using FontVault.Core;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace FontVault.Scan;

/// <summary>
/// Extracts font files from archives found while scanning the sources.
/// .zip: native (System.IO.Compression, no dependency).
/// .rar / .7z: SharpCompress (managed, in-process), via the solid-safe reader.
/// Extracted files land under a per-archive folder in <c>extractRoot</c> and feed
/// the normal scan pipeline; the caller deletes <c>extractRoot</c> when the scan ends.
/// </summary>
public static class ArchiveExtractor
{
    /// <summary>
    /// Returns the absolute paths of font files extracted from <paramref name="archivePath"/>.
    /// Idempotent: an entry already present on disk (prior run) is reused, not re-extracted.
    /// </summary>
    public static List<string> ExtractFonts(string archivePath, string extractRoot,
        HashSet<string> fontExtensions, ScanLog log, CancellationToken ct)
    {
        string ext = Path.GetExtension(archivePath).ToLowerInvariant();
        string outDir = Path.Combine(extractRoot, Crc32.ComputeString(
            Path.GetFullPath(archivePath).ToLowerInvariant()).ToString("X8"));
        var result = new List<string>();
        try
        {
            if (ext == ".zip")
                ExtractZipNative(archivePath, outDir, fontExtensions, result, log, ct);
            else
                ExtractWithSharpCompress(archivePath, outDir, fontExtensions, result, log, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { log.Write("archive", archivePath, ex.Message); }
        return result;
    }

    private static void ExtractZipNative(string archivePath, string outDir, HashSet<string> fontExtensions,
        List<string> result, ScanLog log, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Name.Length == 0) continue; // directory entry
            if (entry.Name.StartsWith("._", StringComparison.Ordinal)) continue; // macOS AppleDouble / __MACOSX
            if (!fontExtensions.Contains(Path.GetExtension(entry.Name))) continue;
            string? target = SafeTarget(outDir, entry.FullName);
            if (target == null)
            {
                log.Write("archive", $"{archivePath} :: {entry.FullName}", "Unsafe entry path, skipped.");
                continue;
            }
            try
            {
                if (!File.Exists(target))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }
                result.Add(target);
            }
            catch (Exception ex)
            {
                log.Write("archive", $"{archivePath} :: {entry.FullName}", ex.Message);
            }
        }
    }

    private static void ExtractWithSharpCompress(string archivePath, string outDir,
        HashSet<string> fontExtensions, List<string> result, ScanLog log, CancellationToken ct)
    {
        using IArchive archive = ArchiveFactory.OpenArchive(archivePath, new ReaderOptions());

        // Extracts one entry (font extensions only); idempotent (existing target reused, resume-safe).
        void Take(string? key, bool isDirectory, Func<Stream> open)
        {
            if (isDirectory || string.IsNullOrEmpty(key)) return;
            if (Path.GetFileName(key).StartsWith("._", StringComparison.Ordinal)) return; // macOS AppleDouble / __MACOSX
            if (!fontExtensions.Contains(Path.GetExtension(key))) return;
            string? target = SafeTarget(outDir, key);
            if (target == null)
            {
                log.Write("archive", $"{archivePath} :: {key}", "Unsafe entry path, skipped.");
                return;
            }
            try
            {
                if (!File.Exists(target))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var es = open();
                    using var fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
                    es.CopyTo(fs);
                }
                result.Add(target);
            }
            catch (Exception ex)
            {
                log.Write("archive", $"{archivePath} :: {key}", ex.Message);
            }
        }

        if (archive.IsSolid)
        {
            // Solid RAR / 7z: forward-only reader, decompresses in stored order
            // (random per-entry access is unsupported / very slow on solid archives).
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                ct.ThrowIfCancellationRequested();
                Take(reader.Entry.Key, reader.Entry.IsDirectory, () => reader.OpenEntryStream());
            }
        }
        else
        {
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                Take(entry.Key, entry.IsDirectory, () => entry.OpenEntryStream());
            }
        }
    }

    /// <summary>Resolves an entry path inside <paramref name="outDir"/>, rejecting traversal (Zip Slip).</summary>
    private static string? SafeTarget(string outDir, string entryFullName)
    {
        string root = Path.GetFullPath(outDir);
        string combined;
        try { combined = Path.GetFullPath(Path.Combine(root, entryFullName.Replace('\\', '/'))); }
        catch { return null; }
        if (combined.Equals(root, StringComparison.OrdinalIgnoreCase)) return null;
        return combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? combined : null;
    }
}
