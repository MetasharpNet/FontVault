namespace FontVault.Core;

/// <summary>String pool: deduplicates instances (repeated family/style names).</summary>
public sealed class StringPool
{
    private readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);

    public string Intern(string value)
    {
        if (value.Length == 0) return string.Empty;
        if (_map.TryGetValue(value, out var existing)) return existing;
        _map[value] = value;
        return value;
    }
}
