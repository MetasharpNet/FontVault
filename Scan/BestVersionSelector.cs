using FontVault.Core;

namespace FontVault.Scan;

/// <summary>
/// Exact deduplication and best-version selection (§7, §8).
/// The vault is additive: entries already indexed are always kept;
/// a new file is copied only if it beats the kept entries or matches the §8 exception.
/// Logical grouping key: typographic family (display name) + effective style,
/// so concurrent variants of one family member compete inside the same group.
/// </summary>
public static class BestVersionSelector
{
    /// <summary>
    /// Returns the new entries to copy into the vault. Side effects:
    /// source paths of duplicates/losers merged into the winning entry (resident Heavy).
    /// </summary>
    /// <param name="progress">Sub-phase reporter: (label, done, total). Throttled by the caller's UI marshaling.</param>
    public static List<FontEntry> Select(List<FontEntry> newRecords, IReadOnlyList<FontEntry> existing,
        IndexReader? reader, string vaultRoot, ScanLog log, RejectionStats stats,
        Action<string, int, int>? progress = null)
    {
        // 1. Exact duplicates among new files: (size, CRC32) key + binary comparison against collisions.
        var byExactKey = new Dictionary<(long, uint), List<FontEntry>>();
        var deduped = new List<FontEntry>();
        for (int i = 0; i < newRecords.Count; i++)
        {
            if ((i & 4095) == 0) progress?.Invoke("Deduplication", i, newRecords.Count);
            var rec = newRecords[i];
            var key = (rec.FileSize, rec.Crc32);
            if (!byExactKey.TryGetValue(key, out var group))
            {
                byExactKey[key] = group = new List<FontEntry>();
            }
            FontEntry? duplicateOf = null;
            foreach (var candidate in group)
            {
                if (FilesEqual(SourcePath(candidate), SourcePath(rec)))
                {
                    duplicateOf = candidate;
                    break;
                }
            }
            if (duplicateOf != null)
            {
                stats.ExactDuplicateNew++;
                log.Write("rejected", SourcePath(rec),
                    $"Exact duplicate of {SourcePath(duplicateOf)} — not added.");
                duplicateOf.Heavy!.OriginalPaths.AddRange(rec.Heavy!.OriginalPaths);
            }
            else
            {
                group.Add(rec);
                deduped.Add(rec);
            }
        }

        // 2. Exact duplicates against existing entries: source path merged, no copy.
        var existingByKey = new Dictionary<(long, uint), FontEntry>();
        foreach (var e in existing)
            existingByKey.TryAdd((e.FileSize, e.Crc32), e);

        var remaining = new List<FontEntry>();
        for (int i = 0; i < deduped.Count; i++)
        {
            if ((i & 4095) == 0) progress?.Invoke("Matching existing fonts", i, deduped.Count);
            var rec = deduped[i];
            if (existingByKey.TryGetValue((rec.FileSize, rec.Crc32), out var match) &&
                FilesEqual(Path.Combine(vaultRoot, match.VaultRelPath), SourcePath(rec)))
            {
                stats.ExactDuplicateExisting++;
                log.Write("rejected", SourcePath(rec),
                    $"Already present in the vault as {match.VaultRelPath} — not added.");
                EnsureHeavy(match, reader);
                foreach (var p in rec.Heavy!.OriginalPaths)
                    if (!match.Heavy!.OriginalPaths.Contains(p, StringComparer.OrdinalIgnoreCase))
                        match.Heavy!.OriginalPaths.Add(p);
            }
            else
            {
                remaining.Add(rec);
            }
        }

        // 3. Logical groups (typographic family, effective style): existing + new candidates.
        var coverageCache = new Dictionary<FontEntry, int>();
        int CoverageCount(FontEntry e)
        {
            if (coverageCache.TryGetValue(e, out int n)) return n;
            var heavy = e.Heavy ?? reader?.GetHeavy(e) ?? new HeavyData();
            n = 0;
            foreach (var iv in heavy.Coverage) n += iv.Count;
            coverageCache[e] = n;
            return n;
        }

        // §8: deterministic comparator, criteria in order.
        int Compare(FontEntry a, FontEntry b)
        {
            int c = FontEntry.FormatPriority(a.Extension).CompareTo(FontEntry.FormatPriority(b.Extension));
            if (c != 0) return c;
            c = a.VersionNumber().CompareTo(b.VersionNumber());
            if (c != 0) return c;
            c = a.GlyphCount.CompareTo(b.GlyphCount);
            if (c != 0) return c;
            c = CoverageCount(a).CompareTo(CoverageCount(b));
            if (c != 0) return c;
            c = a.MetadataScore.CompareTo(b.MetadataScore);
            if (c != 0) return c;
            return b.Crc32.CompareTo(a.Crc32); // lowest CRC32 wins
        }

        static string GroupKey(FontEntry e) =>
            $"{Norm(e.WindowsDisplayName)} {Norm(e.EffectiveStyle)}";

        var groups = new Dictionary<string, (List<FontEntry> Kept, List<FontEntry> Candidates)>();
        foreach (var e in existing)
        {
            var key = GroupKey(e);
            if (!groups.TryGetValue(key, out var g)) groups[key] = g = (new List<FontEntry>(), new List<FontEntry>());
            g.Kept.Add(e);
        }
        foreach (var rec in remaining)
        {
            var key = GroupKey(rec);
            if (!groups.TryGetValue(key, out var g)) groups[key] = g = (new List<FontEntry>(), new List<FontEntry>());
            g.Candidates.Add(rec);
        }

        // 4. Per-group selection.
        var toCopy = new List<FontEntry>();
        int gi = 0;
        foreach (var (_, group) in groups)
        {
            if ((gi++ & 1023) == 0) progress?.Invoke("Selecting best versions", gi, groups.Count);
            if (group.Candidates.Count == 0) continue;
            group.Candidates.Sort((a, b) => Compare(b, a)); // best first

            var kept = new List<FontEntry>(group.Kept);
            foreach (var cand in group.Candidates)
            {
                bool keep;
                if (kept.Count == 0)
                {
                    keep = true;
                }
                else
                {
                    var best = kept[0];
                    foreach (var k in kept) if (Compare(k, best) > 0) best = k;
                    keep = Compare(cand, best) > 0 || IsSection8Exception(cand, best, kept, CoverageCount);
                }

                if (keep)
                {
                    kept.Add(cand);
                    toCopy.Add(cand);
                }
                else
                {
                    // Loser: source paths recorded under the winning entry (§7).
                    var winner = kept[0];
                    foreach (var k in kept) if (Compare(k, winner) > 0) winner = k;
                    EnsureHeavy(winner, reader);
                    foreach (var p in cand.Heavy!.OriginalPaths)
                        if (!winner.Heavy!.OriginalPaths.Contains(p, StringComparer.OrdinalIgnoreCase))
                            winner.Heavy!.OriginalPaths.Add(p);
                    stats.NotBestVersion++;
                    log.Write("rejected", SourcePath(cand),
                        $"Not the best version — retained version = {winner.VaultRelPath} " +
                        $"(this candidate: {SourcePath(cand)}, format {cand.Extension}, v{cand.Version}, {cand.GlyphCount} glyphs).");
                }
            }
        }
        return toCopy;
    }

    /// <summary>§8 exception: a lower-priority format is kept as well when strictly better on one criterion.</summary>
    private static bool IsSection8Exception(FontEntry cand, FontEntry best, List<FontEntry> kept,
        Func<FontEntry, int> coverageCount)
    {
        if (FontEntry.FormatPriority(cand.Extension) >= FontEntry.FormatPriority(best.Extension)) return false;
        if (cand.VersionNumber() > best.VersionNumber()) return true;
        if (cand.GlyphCount > best.GlyphCount) return true;
        if (coverageCount(cand) > coverageCount(best)) return true;
        if (cand.MetadataScore > best.MetadataScore) return true;
        if (cand.IsVariableFont && !kept.Any(k => k.IsVariableFont)) return true;
        return false;
    }

    private static void EnsureHeavy(FontEntry entry, IndexReader? reader)
    {
        entry.Heavy ??= reader?.GetHeavy(entry) ?? new HeavyData();
    }

    private static string SourcePath(FontEntry rec) => rec.Heavy!.OriginalPaths[0];

    private static string Norm(string value) =>
        string.Join(' ', value.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>Binary comparison of two files (guards against CRC32 collisions).</summary>
    internal static bool FilesEqual(string pathA, string pathB)
    {
        try
        {
            using var a = new FileStream(pathA, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using var b = new FileStream(pathB, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            if (a.Length != b.Length) return false;
            var bufA = new byte[1 << 16];
            var bufB = new byte[1 << 16];
            while (true)
            {
                int readA = a.Read(bufA, 0, bufA.Length);
                if (readA == 0) return true;
                int readB = 0;
                while (readB < readA)
                {
                    int r = b.Read(bufB, readB, readA - readB);
                    if (r == 0) return false;
                    readB += r;
                }
                if (!bufA.AsSpan(0, readA).SequenceEqual(bufB.AsSpan(0, readA))) return false;
            }
        }
        catch (IOException)
        {
            return false; // unreadable file: treated as distinct
        }
    }
}
