namespace FontVault.Core;

public enum FontFormat : byte
{
    TrueType = 0,
    Cff = 1,
}

public enum FontExt : byte
{
    Otf = 0,
    Ttf = 1,
    Woff = 2,
    Woff2 = 3,
    Eot = 4,
}

/// <summary>Covered Unicode codepoint interval, inclusive bounds.</summary>
public readonly record struct UnicodeInterval(int Start, int End)
{
    public int Count => End - Start + 1;
}

/// <summary>Variable font axis (fvar).</summary>
public readonly record struct VariableAxis(string Tag, float Min, float Default, float Max);

/// <summary>Heavy fields, loaded on demand from the index (or resident during a scan).</summary>
public sealed class HeavyData
{
    public List<UnicodeInterval> Coverage = new();
    public List<VariableAxis> Axes = new();
    public List<string> Features = new();
    public List<string> OriginalPaths = new();

    private static readonly string[] LigatureFeatures = { "liga", "dlig", "rlig", "clig", "hlig" };

    public bool HasLigatures => Features.Any(f => LigatureFeatures.Contains(f));
}

/// <summary>
/// Index entry: light fields resident in memory; heavy fields via HeavyOffset.
/// Members are properties (not fields) so WPF data binding can read them directly.
/// </summary>
public sealed class FontEntry
{
    public string WindowsDisplayName { get; set; } = "";
    public string FamilyName { get; set; } = "";
    public string TypographicFamilyName { get; set; } = "";
    public string Style { get; set; } = "";
    public string TypographicSubfamily { get; set; } = "";
    public string FullName { get; set; } = "";
    public string PostScriptName { get; set; } = "";
    public string Version { get; set; } = "";
    public string VaultRelPath { get; set; } = "";

    public ushort GlyphCount { get; set; }
    public FontFormat Format { get; set; }
    public FontExt Extension { get; set; }
    public bool IsVariableFont { get; set; }
    public long FileSize { get; set; }
    public uint Crc32 { get; set; }
    public long ScanDateTicks { get; set; }

    /// <summary>Number of populated metadata fields (quality, selection criterion §8.5).</summary>
    public byte MetadataScore { get; set; }

    /// <summary>Absolute offset of the heavy block inside fontvault.idx; -1 if not indexed.</summary>
    public long HeavyOffset { get; set; } = -1;

    /// <summary>Resident heavy block (during a scan or after on-demand load).</summary>
    public HeavyData? Heavy { get; set; }

    /// <summary>Style used for grouping and naming: typographic subfamily (name ID 17) preferred over subfamily (ID 2).</summary>
    public string EffectiveStyle => TypographicSubfamily.Length > 0 ? TypographicSubfamily : Style;

    /// <summary>
    /// Runtime-only Windows install state (not persisted):
    /// 0 = not installed, 1 = installed by FontVault (per-user), 2 = installed externally (system or other).
    /// </summary>
    public byte InstalledState { get; set; }

    public bool IsInstalled => InstalledState != 0;

    /// <summary>Extension as displayed in the UI (".ttf", ".woff2", …).</summary>
    public string ExtensionDisplay => ExtensionString(Extension);

    public static string ExtensionString(FontExt ext) => ext switch
    {
        FontExt.Otf => ".otf",
        FontExt.Ttf => ".ttf",
        FontExt.Woff => ".woff",
        FontExt.Woff2 => ".woff2",
        FontExt.Eot => ".eot",
        _ => ".bin",
    };

    public static FontExt? ExtFromString(string extension) => extension.ToLowerInvariant() switch
    {
        ".otf" => FontExt.Otf,
        ".ttf" => FontExt.Ttf,
        ".woff" => FontExt.Woff,
        ".woff2" => FontExt.Woff2,
        ".eot" => FontExt.Eot,
        _ => null,
    };

    /// <summary>Format priority for selection (§8.1): OTF > WOFF2 > TTF > WOFF > EOT.</summary>
    public static int FormatPriority(FontExt ext) => ext switch
    {
        FontExt.Otf => 5,
        FontExt.Woff2 => 4,
        FontExt.Ttf => 3,
        FontExt.Woff => 2,
        FontExt.Eot => 1,
        _ => 0,
    };

    /// <summary>Numeric version for comparison (first number in the version string).</summary>
    public double VersionNumber()
    {
        var m = System.Text.RegularExpressions.Regex.Match(Version, @"\d+(\.\d+)?");
        return m.Success && double.TryParse(m.Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 0.0;
    }
}
