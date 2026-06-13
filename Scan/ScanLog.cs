using System.Text;

namespace FontVault.Scan;

/// <summary>Scan error/event log (scan.log, append, thread-safe).</summary>
public sealed class ScanLog : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();

    public ScanLog(string path)
    {
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8);
    }

    public void Write(string stage, string path, string message)
    {
        lock (_lock)
        {
            _writer.WriteLine($"{DateTime.UtcNow:O}\t{stage}\t{path}\t{message.Replace('\n', ' ').Replace('\r', ' ')}");
            _writer.Flush();
        }
    }

    public void Dispose()
    {
        lock (_lock) _writer.Dispose();
    }
}
