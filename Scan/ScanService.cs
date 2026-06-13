using System.Buffers;
using System.Threading.Channels;
using FontVault.Core;
using FontVault.Fonts;

namespace FontVault.Scan;

public sealed class ScanProgress
{
    public string Phase = "";
    public int Discovered;
    public int Processed;
    public int Errors;
    public int Copied;
    /// <summary>Item counter for the current phase (Selection/Copying); drives the bar when PhaseTotal &gt; 0.</summary>
    public int PhaseProcessed;
    /// <summary>Total items for the current phase; 0 = fall back to Processed/Discovered.</summary>
    public int PhaseTotal;
}

public sealed class ScanResult
{
    public int Discovered;
    public int Processed;
    public int Errors;
    public int Copied;
    public int TotalEntries;
}

/// <summary>
/// Scan pipeline (§6): discovery → minimal read → extraction → signatures →
/// selection → vault copy → index generation.
/// Producer/consumer over a Channel, worker threads at BelowNormal priority,
/// resume via scan.partial + scan.checkpoint, per-file errors logged.
/// </summary>
public static class ScanService
{
    private const long MaxFontFileSize = 512L * 1024 * 1024;
    private static readonly HashSet<string> FontExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".otf", ".ttf", ".woff", ".woff2", ".eot" };
    private static readonly HashSet<string> ArchiveExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".zip", ".rar", ".7z" };

    /// <summary>
    /// Runs a full scan. Blocking: call off the UI thread.
    /// <paramref name="workDir"/>: folder for the index and scan artifacts (idx, checkpoint, partial, log) —
    /// the exe folder in the app; the vault holds font files only.
    /// <paramref name="existingReader"/>: current index; ownership is transferred (dispose guaranteed).
    /// </summary>
    public static ScanResult Run(IReadOnlyList<string> sources, string vaultRoot, string workDir,
        IndexReader? existingReader, IProgress<ScanProgress>? progress, CancellationToken ct,
        string? errorDir = null)
    {
        Directory.CreateDirectory(vaultRoot);
        Directory.CreateDirectory(workDir);
        string indexPath = Path.Combine(workDir, IndexFormat.FileName);
        string checkpointPath = Path.Combine(workDir, "scan.checkpoint");
        string partialPath = Path.Combine(workDir, "scan.partial");

        var result = new ScanResult();
        int discovered = 0, processed = 0, errors = 0;
        string phase = "Discovery";
        int phaseProcessed = 0, phaseTotal = 0;
        void Report()
        {
            progress?.Report(new ScanProgress
            {
                Phase = phase,
                Discovered = Volatile.Read(ref discovered),
                Processed = Volatile.Read(ref processed),
                Errors = Volatile.Read(ref errors),
                Copied = result.Copied,
                PhaseProcessed = phaseProcessed,
                PhaseTotal = phaseTotal,
            });
        }

        try
        {
            using var log = new ScanLog(Path.Combine(workDir, "scan.log"));

            // Session key: same sources + same vault = resumable.
            string sessionKey = Crc32.ComputeString(
                string.Join("\n", sources.Select(Path.GetFullPath).OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
                + "\n" + Path.GetFullPath(vaultRoot)).ToString("X8");

            // Resume: reload the partial journal when the session matches.
            var records = new List<FontEntry>();
            var processedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool resuming = File.Exists(checkpointPath) && File.Exists(partialPath) &&
                File.ReadAllText(checkpointPath).Trim() == sessionKey;
            if (resuming)
            {
                records = PartialJournal.LoadAll(partialPath);
                foreach (var r in records)
                    processedPaths.Add(r.Heavy!.OriginalPaths[0]);
                log.Write("resume", partialPath, $"{records.Count} entries reloaded from the partial journal.");
            }
            if (File.Exists(partialPath)) File.Delete(partialPath);
            File.WriteAllText(checkpointPath, sessionKey);

            // Fonts pulled out of archives are staged here and fed to the pipeline as regular files;
            // a resume reuses the prior run's extraction, a fresh scan discards it.
            string extractRoot = Path.Combine(workDir, "scan.extract");
            if (!resuming) SafeDeleteDir(extractRoot);

            using (var journal = new PartialJournal(partialPath))
            {
                // Clean rewrite of the reloaded journal (healthy baseline after a possible truncation).
                foreach (var r in records) journal.Append(r);

                // Phases 1-4: discovery + read + extraction + signatures, bounded parallelism.
                phase = "Reading";
                var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
                {
                    SingleWriter = true,
                    SingleReader = false,
                });

                void CopyToErrors(string src)
                {
                    if (errorDir == null) return;
                    try
                    {
                        Directory.CreateDirectory(errorDir);
                        string dest = Path.Combine(errorDir,
                            $"{Path.GetFileNameWithoutExtension(src)}.{Crc32.ComputeString(src.ToLowerInvariant()):X8}{Path.GetExtension(src)}");
                        File.Copy(src, dest, overwrite: true);
                        File.SetAttributes(dest, FileAttributes.Normal);
                    }
                    catch (Exception ex)
                    {
                        log.Write("errorcopy", src, ex.Message);
                    }
                }

                void ProcessFile(string path)
                {
                    var fontExt = FontEntry.ExtFromString(Path.GetExtension(path));
                    if (fontExt == null)
                    {
                        Interlocked.Increment(ref processed);
                        return;
                    }
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
                        var entry = new FontEntry
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
                            FileSize = length,
                            Crc32 = crc,
                            ScanDateTicks = DateTime.UtcNow.Ticks,
                            Heavy = new HeavyData
                            {
                                Coverage = parsed.Coverage,
                                Axes = parsed.Axes,
                                Features = parsed.Features,
                                OriginalPaths = { path },
                            },
                        };
                        journal.Append(entry);
                        lock (records) records.Add(entry);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref errors);
                        log.Write("read", path, ex.Message);
                        CopyToErrors(path);
                    }
                    finally
                    {
                        if (buffer != null) ArrayPool<byte>.Shared.Return(buffer);
                        int done = Interlocked.Increment(ref processed);
                        if (done % 64 == 0) Report();
                    }
                }

                void WorkerLoop()
                {
                    var reader = channel.Reader;
                    while (true)
                    {
                        if (reader.TryRead(out var path))
                        {
                            ProcessFile(path);
                            continue;
                        }
                        bool more;
                        try { more = reader.WaitToReadAsync(ct).AsTask().GetAwaiter().GetResult(); }
                        catch (OperationCanceledException) { break; }
                        catch (AggregateException) { break; }
                        if (!more) break;
                    }
                }

                int workerCount = Math.Max(1, Environment.ProcessorCount - 1);
                var workers = new Thread[workerCount];
                for (int i = 0; i < workerCount; i++)
                {
                    workers[i] = new Thread(WorkerLoop)
                    {
                        IsBackground = true,
                        Priority = ThreadPriority.BelowNormal,
                        Name = $"FontVault.Scan.{i}",
                    };
                    workers[i].Start();
                }

                void Enqueue(string file)
                {
                    if (processedPaths.Contains(file)) return;
                    int n = Interlocked.Increment(ref discovered);
                    if (n % 256 == 0) Report();
                    while (!channel.Writer.TryWrite(file))
                    {
                        if (!channel.Writer.WaitToWriteAsync(ct).AsTask().GetAwaiter().GetResult())
                            break;
                    }
                }

                try
                {
                    foreach (var source in sources)
                    {
                        foreach (var file in SafeEnumerateFiles(source, log))
                        {
                            ct.ThrowIfCancellationRequested();
                            string ext = Path.GetExtension(file);
                            if (FontExtensions.Contains(ext))
                                Enqueue(file);
                            else if (ArchiveExtensions.Contains(ext))
                                foreach (var fontFile in ArchiveExtractor.ExtractFonts(
                                    file, extractRoot, FontExtensions, log, ct))
                                    Enqueue(fontFile);
                        }
                    }
                }
                finally
                {
                    channel.Writer.TryComplete();
                    foreach (var w in workers) w.Join();
                }
                ct.ThrowIfCancellationRequested();
                Report();

                // Phase 5: deduplication + best-version selection.
                phase = "Selection";
                Report();
                var existing = existingReader?.Entries ?? Array.Empty<FontEntry>();
                var toCopy = BestVersionSelector.Select(records, existing, existingReader, vaultRoot, log,
                    (label, done, total) =>
                    {
                        phase = "Selection — " + label;
                        phaseProcessed = done;
                        phaseTotal = total;
                        Report();
                    });

                // Phase 6: vault copy (idempotent: safe resume).
                phase = "Copying";
                phaseProcessed = 0;
                phaseTotal = toCopy.Count;
                Report();
                var usedRelPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in existing) usedRelPaths.Add(e.VaultRelPath);
                var copied = new List<FontEntry>();
                foreach (var rec in toCopy)
                {
                    ct.ThrowIfCancellationRequested();
                    phaseProcessed++;
                    try
                    {
                        string rel = VaultNamer.BuildRelPath(rec, withCrcSuffix: false);
                        string abs = Path.Combine(vaultRoot, rel);
                        if (usedRelPaths.Contains(rel) || (File.Exists(abs) && !IsSameContent(abs, rec)))
                        {
                            rel = VaultNamer.BuildRelPath(rec, withCrcSuffix: true);
                            abs = Path.Combine(vaultRoot, rel);
                            if (usedRelPaths.Contains(rel) || (File.Exists(abs) && !IsSameContent(abs, rec)))
                                throw new IOException("Unresolvable vault name collision (same name and CRC, different content).");
                        }
                        if (!File.Exists(abs))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
                            string tmp = abs + ".fvtmp";
                            File.Copy(rec.Heavy!.OriginalPaths[0], tmp, overwrite: true);
                            // Sources may carry the read-only attribute; vault files must stay writable.
                            File.SetAttributes(tmp, FileAttributes.Normal);
                            File.Move(tmp, abs);
                        }
                        rec.VaultRelPath = rel;
                        usedRelPaths.Add(rel);
                        copied.Add(rec);
                        result.Copied++;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref errors);
                        log.Write("copy", rec.Heavy!.OriginalPaths[0], ex.Message);
                    }
                    if ((phaseProcessed & 63) == 0) Report();
                }

                // Phase 7: index generation (atomic write).
                phase = "Indexing";
                phaseTotal = 0;
                Report();
                var finalEntries = new List<FontEntry>(existing.Length + copied.Count);
                finalEntries.AddRange(existing);
                finalEntries.AddRange(copied);
                finalEntries.Sort((a, b) =>
                {
                    int c = string.Compare(a.WindowsDisplayName, b.WindowsDisplayName, StringComparison.OrdinalIgnoreCase);
                    if (c != 0) return c;
                    return string.Compare(a.EffectiveStyle, b.EffectiveStyle, StringComparison.OrdinalIgnoreCase);
                });
                string tmpIndex = indexPath + ".tmp";
                IndexWriter.Write(tmpIndex, finalEntries,
                    e => existingReader?.GetHeavy(e) ?? e.Heavy ?? new HeavyData());
                existingReader?.Dispose();
                existingReader = null;
                File.Move(tmpIndex, indexPath, overwrite: true);
                result.TotalEntries = finalEntries.Count;
            }

            // Complete scan: resume artifacts removed.
            File.Delete(partialPath);
            File.Delete(checkpointPath);
            SafeDeleteDir(extractRoot);

            phase = "Done";
            Report();
            result.Discovered = discovered;
            result.Processed = processed;
            result.Errors = errors;
            return result;
        }
        finally
        {
            existingReader?.Dispose();
        }
    }

    /// <summary>Recursive enumeration tolerant to access errors (logged, never blocking).</summary>
    private static IEnumerable<string> SafeEnumerateFiles(string root, ScanLog log)
    {
        var dirs = new Stack<string>();
        dirs.Push(root);
        while (dirs.Count > 0)
        {
            string dir = dirs.Pop();
            string[] files = Array.Empty<string>();
            string[] subDirs = Array.Empty<string>();
            try
            {
                files = Directory.GetFiles(dir);
                subDirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex)
            {
                log.Write("discovery", dir, ex.Message);
            }
            foreach (var f in files) yield return f;
            foreach (var d in subDirs) dirs.Push(d);
        }
    }

    private static void SafeDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort; leftover staging is reclaimed on the next fresh scan */ }
    }

    private static bool IsSameContent(string existingFile, FontEntry rec)
    {
        try
        {
            using var fs = new FileStream(existingFile, FileMode.Open, FileAccess.Read, FileShare.Read,
                1 << 16, FileOptions.SequentialScan);
            if (fs.Length != rec.FileSize) return false;
            return Crc32.Compute(fs) == rec.Crc32;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
