using System.Globalization;
using System.Text;
using FontVault.Core;

namespace FontVault.Scan;

/// <summary>
/// Vault naming convention:
/// Letter/Family/WindowsDisplayName_Style_vVersion_gGlyphCount.ext
/// Collision: _cCRC32 suffix before the extension.
/// The family folder is the Windows display name (typographic family, name ID 16, when present),
/// so all members of one family land in the same folder.
/// </summary>
public static class VaultNamer
{
    public static string BuildRelPath(FontEntry e, bool withCrcSuffix)
    {
        string family = SanitizePathComponent(e.WindowsDisplayName);
        string display = SanitizeNamePart(e.WindowsDisplayName);
        string style = SanitizeNamePart(e.EffectiveStyle);
        string version = e.Version.Length > 0 ? SanitizeNamePart(e.Version) : "0";
        string ext = FontEntry.ExtensionString(e.Extension);
        string suffix = withCrcSuffix ? $"_c{e.Crc32:X8}" : "";
        string fileName = $"{display}_{style}_v{version}_g{e.GlyphCount}{suffix}{ext}";
        return Path.Combine(FirstLetterDir(family), family, fileName);
    }

    /// <summary>Sorting letter: first A–Z letter after diacritics removal, otherwise '#'.</summary>
    public static string FirstLetterDir(string family)
    {
        foreach (char c in family.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            char upper = char.ToUpperInvariant(c);
            if (upper is >= 'A' and <= 'Z') return upper.ToString();
            break;
        }
        return "#";
    }

    /// <summary>Valid NTFS path component: forbidden characters replaced by '-', trailing dots/spaces removed.</summary>
    public static string SanitizePathComponent(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
            sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '-' : c);
        string result = sb.ToString().TrimEnd('.', ' ').Trim();
        return result.Length > 0 ? result : "-";
    }

    /// <summary>File-name part: like SanitizePathComponent, plus '_' replaced by '-' (field separator).</summary>
    public static string SanitizeNamePart(string value) =>
        SanitizePathComponent(value).Replace('_', '-');
}
