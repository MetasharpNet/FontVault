using System.Text;

namespace FontVault.Scan;

/// <summary>Scan error/event log (scan.log, append, thread-safe). Buffered: flushed in batches and on dispose.</summary>
public sealed class ScanLog : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();
    private int _sinceFlush;

    public ScanLog(string path)
    {
        // AutoFlush off + batch flush: a Process may log millions of "rejected" lines; a flush per line would crawl.
        _writer = new StreamWriter(
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 1 << 16), Encoding.UTF8)
        { AutoFlush = false };
    }

    public void Write(string stage, string path, string message)
    {
        lock (_lock)
        {
            _writer.WriteLine($"{DateTime.UtcNow:O}\t{stage}\t{path}\t{message.Replace('\n', ' ').Replace('\r', ' ')}");
            if (++_sinceFlush >= 4096) { _writer.Flush(); _sinceFlush = 0; }
        }
    }

    /// <summary>Free-form line (used for the run header and the end-of-run summary).</summary>
    public void Note(string message)
    {
        lock (_lock)
        {
            _writer.WriteLine(message);
            if (++_sinceFlush >= 4096) { _writer.Flush(); _sinceFlush = 0; }
        }
    }

    public void Flush()
    {
        lock (_lock) { _writer.Flush(); _sinceFlush = 0; }
    }

    public void Dispose()
    {
        lock (_lock) { _writer.Flush(); _writer.Dispose(); }
    }
}
