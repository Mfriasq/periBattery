using System.Text;

namespace PeriBattery.Core.Infra;

public interface ILog
{
    bool DebugEnabled { get; }
    void Debug(string message);
    void Info(string message);
    void Warn(string message);
}

public static class LogExtensions
{
    public static string Hex(this byte[] data, int count = -1)
    {
        if (count < 0 || count > data.Length) count = data.Length;
        // Trim trailing zero padding to keep raw dumps readable.
        int end = count;
        while (end > 8 && data[end - 1] == 0) end--;
        var sb = new StringBuilder(end * 3);
        for (int i = 0; i < end; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(data[i].ToString("X2"));
        }
        if (end < count) sb.Append(" ..");
        return sb.ToString();
    }
}

public sealed class ConsoleLog(bool debug) : ILog
{
    public bool DebugEnabled => debug;
    public void Debug(string message) { if (debug) Console.WriteLine("    " + message); }
    public void Info(string message) => Console.WriteLine(message);
    public void Warn(string message) => Console.WriteLine("! " + message);
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();
    public bool DebugEnabled => false;
    public void Debug(string message) { }
    public void Info(string message) { }
    public void Warn(string message) { }
}

/// <summary>Size-capped rolling text log. Keeps the current file plus <c>maxFiles - 1</c> older ones.</summary>
public sealed class RollingFileLog : ILog, IDisposable
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly int _maxFiles;
    private StreamWriter? _writer;

    public RollingFileLog(string path, long maxBytes = 1_000_000, int maxFiles = 3)
    {
        _path = path;
        _maxBytes = maxBytes;
        _maxFiles = maxFiles;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
    }

    public bool DebugEnabled { get; set; }
    public string Path => _path;

    public void Debug(string message) { if (DebugEnabled) Write("DBG", message); }
    public void Info(string message) => Write("INF", message);
    public void Warn(string message) => Write("WRN", message);

    private void Write(string level, string message)
    {
        lock (_gate)
        {
            try
            {
                _writer ??= new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
                _writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}");
                if (_writer.BaseStream.Length > _maxBytes) Roll();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void Roll()
    {
        _writer?.Dispose();
        _writer = null;
        for (int i = _maxFiles - 1; i >= 1; i--)
        {
            string src = i == 1 ? _path : $"{_path}.{i - 1}";
            string dst = $"{_path}.{i}";
            if (!File.Exists(src)) continue;
            File.Delete(dst);
            File.Move(src, dst);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
