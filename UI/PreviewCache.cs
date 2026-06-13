using FontVault.Core;
using FontVault.Fonts;

namespace FontVault.UI;

/// <summary>
/// Preview file cache. Two reasons to exist:
/// 1. WOFF/WOFF2/EOT are not loadable by GlyphTypeface: a standalone sfnt is rebuilt once per entry.
/// 2. WPF snapshots a folder's font list on first access; files added to that folder later in the
///    same process break subsequent GlyphTypeface loads. Every previewed font therefore gets its own
///    immutable subfolder (one file per folder, created before first access), keyed by CRC32 + size.
/// Vault paths never reach GlyphTypeface directly: cache paths are short, ASCII and URI-safe.
/// Read-only attributes (inherited from source fonts) are stripped so overwrites never fail.
/// </summary>
public static class PreviewCache
{
    private static readonly string CacheDir = Path.Combine(Path.GetTempPath(), "FontVault.PreviewCache");

    public static string GetPreviewPath(string vaultAbsPath, FontEntry entry)
    {
        string ext = entry.Format == FontFormat.Cff ? ".otf" : ".ttf";
        string dir = Path.Combine(CacheDir, $"{entry.Crc32:X8}_{entry.FileSize:X}");
        string target = Path.Combine(dir, "font" + ext);
        if (File.Exists(target)) return target;

        if (!File.Exists(vaultAbsPath))
            throw new FileNotFoundException($"Font file missing from the vault ({entry.VaultRelPath}) — run Process to repair.", vaultAbsPath);

        Directory.CreateDirectory(dir);
        string tmp = target + ".tmp";
        ClearReadOnly(tmp);
        if (entry.Extension is FontExt.Otf or FontExt.Ttf)
        {
            File.Copy(vaultAbsPath, tmp, overwrite: true);
            ClearReadOnly(tmp);
        }
        else
        {
            byte[] data = File.ReadAllBytes(vaultAbsPath);
            byte[] sfnt = FontFileReader.ReconstructSfnt(data, entry.Extension)
                ?? throw new InvalidDataException("Reconstruction returned no data.");
            File.WriteAllBytes(tmp, sfnt);
        }
        ClearReadOnly(target);
        File.Move(tmp, target, overwrite: true);
        return target;
    }

    private static void ClearReadOnly(string path)
    {
        try
        {
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
        }
        catch
        {
            // best effort; the subsequent operation reports the real error
        }
    }
}
