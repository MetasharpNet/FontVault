using System.Windows.Media;
using FontVault.Core;

namespace FontVault.UI;

public sealed class RecognitionResult
{
    public FontEntry Entry = null!;
    public double Score;
    /// <summary>0..100 match percentage (for display).</summary>
    public int Percent => (int)Math.Round(Score * 100);
}

/// <summary>
/// Closed-set recognition orchestration. A persisted per-font descriptor cache (keyed by CRC32) lets a
/// query pre-filter the whole vault cheaply; the shortlist is then ranked by render-compare against the
/// query's text. All rendering (RenderTargetBitmap) must run on an STA thread — callers ensure that.
/// </summary>
public static class RecognizerService
{
    private const uint Magic = 0x43525646; // "FVRC"
    private const string ReferenceText = "Hamburgefonstiv 0123";

    public static string CachePath(string workDir) => Path.Combine(workDir, "recognizer.cache");

    // ---- descriptor cache ----

    public static Dictionary<uint, float[]> LoadCache(string path)
    {
        var d = new Dictionary<uint, float[]>();
        if (!File.Exists(path)) return d;
        try
        {
            using var br = new BinaryReader(File.OpenRead(path));
            if (br.ReadUInt32() != Magic || br.ReadInt32() != 1) return d;
            int count = br.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                uint crc = br.ReadUInt32();
                var v = new float[FontRecognizer.DescriptorSize];
                for (int k = 0; k < v.Length; k++) v[k] = br.ReadSingle();
                d[crc] = v;
            }
        }
        catch { /* corrupt cache: rebuilt on demand */ }
        return d;
    }

    public static void SaveCache(string path, Dictionary<uint, float[]> cache)
    {
        try
        {
            string tmp = path + ".tmp";
            using (var bw = new BinaryWriter(File.Create(tmp)))
            {
                bw.Write(Magic); bw.Write(1); bw.Write(cache.Count);
                foreach (var kv in cache)
                {
                    bw.Write(kv.Key);
                    foreach (float f in kv.Value) bw.Write(f);
                }
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* best effort */ }
    }

    /// <summary>How many entries still need a descriptor.</summary>
    public static int MissingCount(IReadOnlyList<FontEntry> entries, Dictionary<uint, float[]> cache)
    {
        int n = 0;
        foreach (var e in entries) if (!cache.ContainsKey(e.Crc32)) n++;
        return n;
    }

    /// <summary>Renders each not-yet-cached font and stores its descriptor. STA thread. Returns built count.</summary>
    public static int BuildMissing(string cachePath, IReadOnlyList<FontEntry> entries, string vaultRoot,
        Dictionary<uint, float[]> cache, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        int total = entries.Count, built = 0;
        var empty = new float[FontRecognizer.DescriptorSize];
        for (int i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            var e = entries[i];
            if (!cache.ContainsKey(e.Crc32))
            {
                try
                {
                    var ink = FontRecognizer.Render(LoadTypeface(e, vaultRoot), ReferenceText);
                    cache[e.Crc32] = ink is { W: > 0 } ? FontRecognizer.Describe(ink) : empty;
                    if (ink is { W: > 0 }) built++;
                }
                catch { cache[e.Crc32] = empty; } // mark attempted so it isn't retried every build
            }
            if ((i & 1023) == 0) progress?.Report((i + 1, total));
        }
        SaveCache(cachePath, cache);
        progress?.Report((total, total));
        return built;
    }

    // ---- recognition ----

    public static List<RecognitionResult> Recognize(FontRecognizer.Ink queryInk, string text,
        IReadOnlyList<FontEntry> entries, string vaultRoot, Dictionary<uint, float[]> cache,
        int prefilter, int top, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var qDesc = FontRecognizer.Describe(queryInk);

        // Pre-filter by descriptor distance (whole vault, cheap).
        var ranked = new List<(FontEntry E, double D)>(entries.Count);
        foreach (var e in entries)
            if (cache.TryGetValue(e.Crc32, out var d) && !IsEmpty(d))
                ranked.Add((e, FontRecognizer.Distance(qDesc, d)));
        ranked.Sort((a, b) => a.D.CompareTo(b.D));
        int n = Math.Min(prefilter, ranked.Count);

        // Render-compare the shortlist against the query's actual text.
        var results = new List<RecognitionResult>(n);
        for (int i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            var e = ranked[i].E;
            try
            {
                var ink = FontRecognizer.Render(LoadTypeface(e, vaultRoot), text);
                if (ink is { W: > 0 })
                    results.Add(new RecognitionResult { Entry = e, Score = FontRecognizer.Compare(queryInk, ink) });
            }
            catch { /* unrenderable candidate: skip */ }
            if ((i & 63) == 0) progress?.Report((i + 1, n));
        }
        progress?.Report((n, n));

        results.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (results.Count > top) results.RemoveRange(top, results.Count - top);
        return results;
    }

    /// <summary>
    /// Exhaustive recognition: render-compares the query text against EVERY font (no descriptor pre-filter),
    /// parallelized over STA worker threads. Much slower than <see cref="Recognize"/> but has full recall.
    /// Returns the top <paramref name="top"/> by similarity.
    /// </summary>
    public static List<RecognitionResult> RecognizeAll(FontRecognizer.Ink queryInk, string text,
        IReadOnlyList<FontEntry> entries, string vaultRoot, int top,
        IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        int total = entries.Count;
        if (total == 0) return new List<RecognitionResult>();

        // Shrink the query once (height-normalized): per-glyph comparison scans the query per candidate,
        // so a full-resolution query would be re-walked 255k times.
        queryInk = FontRecognizer.ResampleToHeight(queryInk, 64);

        int workerCount = Math.Min(Math.Max(1, Environment.ProcessorCount - 1), total);
        var partials = new List<RecognitionResult>[workerCount];
        var threads = new Thread[workerCount];
        int done = 0;
        Exception? failure = null;

        for (int wi = 0; wi < workerCount; wi++)
        {
            int idx = wi;
            var t = new Thread(() =>
            {
                var local = new List<RecognitionResult>();
                try
                {
                    // Striped partition (i += workerCount) balances the varying per-font render cost.
                    for (int i = idx; i < total; i += workerCount)
                    {
                        ct.ThrowIfCancellationRequested();
                        var e = entries[i];
                        try
                        {
                            var r = FontRecognizer.RenderGlyphs(LoadTypeface(e, vaultRoot), text);
                            if (r is { } res && res.Ink is { W: > 0 })
                                local.Add(new RecognitionResult { Entry = e, Score = FontRecognizer.Compare(queryInk, res.Ink, res.GlyphX) });
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { /* unrenderable candidate: skip */ }
                        int d = Interlocked.Increment(ref done);
                        if ((d & 511) == 0) progress?.Report((d, total));
                    }
                }
                catch (OperationCanceledException) { /* cancelled: stop, keep partial */ }
                catch (Exception ex) { failure ??= ex; }
                partials[idx] = local;
            })
            { IsBackground = true, Name = $"FontVault.Recognize.{idx}" };
            t.SetApartmentState(ApartmentState.STA);
            threads[idx] = t;
            t.Start();
        }
        foreach (var t in threads) t.Join();
        ct.ThrowIfCancellationRequested();
        if (failure != null) throw failure;

        progress?.Report((total, total));
        var results = new List<RecognitionResult>(total);
        foreach (var p in partials) if (p != null) results.AddRange(p);
        results.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (results.Count > top) results.RemoveRange(top, results.Count - top);
        return results;
    }

    /// <summary>Renders one font with the query text and returns its similarity (0..1) to the query. STA thread.</summary>
    public static double ScoreOne(FontRecognizer.Ink query, FontEntry entry, string text, string vaultRoot)
    {
        var r = FontRecognizer.RenderGlyphs(LoadTypeface(entry, vaultRoot), text);
        return r is { } res && res.Ink is { W: > 0 } ? FontRecognizer.Compare(query, res.Ink, res.GlyphX) : 0;
    }

    private static bool IsEmpty(float[] d)
    {
        foreach (float f in d) if (f != 0f) return false;
        return true;
    }

    private static GlyphTypeface LoadTypeface(FontEntry e, string vaultRoot)
    {
        string abs = Path.Combine(vaultRoot, e.VaultRelPath);
        // OTF/TTF load directly (avoids creating a temp folder per font during a full-vault build);
        // containers (WOFF/WOFF2/EOT) are rebuilt to sfnt through the preview cache.
        string path = e.Extension is FontExt.Otf or FontExt.Ttf ? abs : PreviewCache.GetPreviewPath(abs, e);
        return new GlyphTypeface(new Uri(path));
    }
}
