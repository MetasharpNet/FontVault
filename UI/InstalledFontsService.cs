using System.Runtime.InteropServices;
using FontVault.Core;
using FontVault.Scan;
using Microsoft.Win32;

namespace FontVault.UI;

/// <summary>
/// Windows install state and per-user install/uninstall (no admin, no external dependency).
/// Detection is content-based: FontVault installs carry a ".fv&lt;CRC32&gt;" marker in the file name;
/// external installs (system fonts, fonts installed by other means) are matched by size + CRC32.
/// Uninstall removes per-user installs (FontVault markers, or per-user fonts matched by size + CRC32);
/// system-wide fonts (C:\Windows\Fonts) require admin rights and are refused.
/// </summary>
public static class InstalledFontsService
{
    private const string FontsRegistryKey = @"Software\Microsoft\Windows NT\CurrentVersion\Fonts";
    private const int WmFontChange = 0x001D;
    private static readonly IntPtr HwndBroadcast = new(0xFFFF);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AddFontResourceW(string path);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool RemoveFontResourceW(string path);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool SendNotifyMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public static string UserFontDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Fonts");

    private static string MarkerFileName(FontEntry entry)
    {
        string ext = entry.Format == FontFormat.Cff ? ".otf" : ".ttf";
        string name = VaultNamer.SanitizeNamePart($"{entry.WindowsDisplayName} {entry.EffectiveStyle}");
        return $"{name}.fv{entry.Crc32:X8}{ext}";
    }

    private static string? FindMarkerFile(uint crc)
    {
        if (!Directory.Exists(UserFontDir)) return null;
        foreach (var file in Directory.EnumerateFiles(UserFontDir, $"*.fv{crc:X8}.*"))
            return file;
        return null;
    }

    /// <summary>Finds a per-user font file (no FontVault marker) matching size + CRC32; null if none.</summary>
    private static string? FindUserFontByCrc(long size, uint crc)
    {
        if (!Directory.Exists(UserFontDir)) return null;
        foreach (var file in Directory.EnumerateFiles(UserFontDir))
        {
            if (file.Contains(".fv", StringComparison.OrdinalIgnoreCase)) continue; // markers handled separately
            try
            {
                if (new FileInfo(file).Length != size) continue;
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16,
                    FileOptions.SequentialScan);
                if (Crc32.Compute(fs) == crc) return file;
            }
            catch
            {
                // unreadable file: skip
            }
        }
        return null;
    }

    /// <summary>Computes <see cref="FontEntry.InstalledState"/> for all entries (background, blocking I/O).</summary>
    public static void ComputeStates(IReadOnlyList<FontEntry> entries)
    {
        // FontVault installs: CRC parsed from the ".fvXXXXXXXX" marker.
        var ourInstalls = new HashSet<uint>();
        if (Directory.Exists(UserFontDir))
        {
            foreach (var file in Directory.EnumerateFiles(UserFontDir))
            {
                string stem = Path.GetFileNameWithoutExtension(file);
                int marker = stem.LastIndexOf(".fv", StringComparison.OrdinalIgnoreCase);
                if (marker >= 0 && stem.Length - marker == 11 &&
                    uint.TryParse(stem.AsSpan(marker + 3), System.Globalization.NumberStyles.HexNumber, null, out uint crc))
                    ourInstalls.Add(crc);
            }
        }

        // External installs: system fonts + non-marker per-user fonts, matched by size then CRC32.
        var bySize = new Dictionary<long, List<string>>();
        void AddDir(string dir, bool skipMarkers)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (skipMarkers && file.Contains(".fv", StringComparison.OrdinalIgnoreCase)) continue;
                string ext = Path.GetExtension(file);
                if (FontEntry.ExtFromString(ext) == null) continue;
                long size;
                try { size = new FileInfo(file).Length; } catch { continue; }
                if (!bySize.TryGetValue(size, out var list)) bySize[size] = list = new List<string>();
                list.Add(file);
            }
        }
        AddDir(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), skipMarkers: false);
        AddDir(UserFontDir, skipMarkers: true);

        var crcCache = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        uint FileCrc(string path)
        {
            if (crcCache.TryGetValue(path, out uint cached)) return cached;
            uint crc = 0;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16,
                    FileOptions.SequentialScan);
                crc = Crc32.Compute(fs);
            }
            catch
            {
                // unreadable installed file: no match
            }
            crcCache[path] = crc;
            return crc;
        }

        foreach (var entry in entries)
        {
            if (ourInstalls.Contains(entry.Crc32))
            {
                entry.InstalledState = 1;
                continue;
            }
            byte state = 0;
            if (bySize.TryGetValue(entry.FileSize, out var candidates))
            {
                foreach (var candidate in candidates)
                {
                    if (FileCrc(candidate) == entry.Crc32)
                    {
                        state = 2;
                        break;
                    }
                }
            }
            entry.InstalledState = state;
        }
    }

    /// <summary>Per-user install: marker copy + HKCU Fonts registry value + GDI notification.</summary>
    public static void Install(FontEntry entry, string sfntPath)
    {
        Directory.CreateDirectory(UserFontDir);
        string target = Path.Combine(UserFontDir, MarkerFileName(entry));
        File.Copy(sfntPath, target, overwrite: true);
        File.SetAttributes(target, FileAttributes.Normal);

        using (var key = Registry.CurrentUser.CreateSubKey(FontsRegistryKey))
        {
            string suffix = entry.Format == FontFormat.Cff ? "(OpenType)" : "(TrueType)";
            key.SetValue($"{entry.WindowsDisplayName} {entry.EffectiveStyle} (FontVault) {suffix}", target);
        }
        AddFontResourceW(target);
        SendNotifyMessageW(HwndBroadcast, WmFontChange, IntPtr.Zero, IntPtr.Zero);
        entry.InstalledState = 1;
    }

    /// <summary>
    /// Removes a per-user install (FontVault marker, or a per-user font matched by size + CRC32):
    /// font resource + HKCU registry value + file. System-wide fonts cannot be removed without admin.
    /// </summary>
    public static void Uninstall(FontEntry entry)
    {
        string? target = FindMarkerFile(entry.Crc32) ?? FindUserFontByCrc(entry.FileSize, entry.Crc32);
        if (target == null)
        {
            // No per-user copy: the font lives in C:\Windows\Fonts (system) or was installed by another tool.
            throw new InvalidOperationException(
                "This font is installed system-wide or by another tool; uninstalling it requires administrator rights and is not done from here.");
        }

        for (int i = 0; i < 8 && RemoveFontResourceW(target); i++)
        {
        }
        string targetFile = Path.GetFileName(target);
        using (var key = Registry.CurrentUser.OpenSubKey(FontsRegistryKey, writable: true))
        {
            if (key != null)
            {
                foreach (var name in key.GetValueNames())
                {
                    // Per-user registry data may be a full path or a bare file name; match both.
                    if (key.GetValue(name) is string value &&
                        (string.Equals(value, target, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(Path.GetFileName(value), targetFile, StringComparison.OrdinalIgnoreCase)))
                        key.DeleteValue(name, throwOnMissingValue: false);
                }
            }
        }
        File.Delete(target);
        SendNotifyMessageW(HwndBroadcast, WmFontChange, IntPtr.Zero, IntPtr.Zero);
        entry.InstalledState = 0;
    }
}
