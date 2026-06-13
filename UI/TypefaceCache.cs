using System.Windows.Media;

namespace FontVault.UI;

/// <summary>Bounded cache of opened GlyphTypeface instances (one per displayed font file). UI thread only.</summary>
internal static class TypefaceCache
{
    private static readonly Dictionary<string, GlyphTypeface> Cache = new(StringComparer.OrdinalIgnoreCase);
    private const int Limit = 32;

    public static GlyphTypeface Get(string path)
    {
        if (Cache.TryGetValue(path, out var cached)) return cached;
        var typeface = new GlyphTypeface(new Uri(path));
        if (Cache.Count >= Limit) Cache.Clear();
        Cache[path] = typeface;
        return typeface;
    }
}
