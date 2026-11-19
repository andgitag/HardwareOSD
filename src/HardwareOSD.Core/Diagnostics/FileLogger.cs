using System.Collections.Concurrent;
using System.Text;

namespace HardwareOSD.Core.Diagnostics;

/// <summary>
/// Append-only, size-capped, thread-safe text logger. Keeps a small in-memory ring buffer
/// so the UI can show recent diagnostics without re-reading the file.
/// </summary>
public sealed class FileLogger : ILog, IDisposable
{
    private const int MaxRingEntries = 400;
    private const long MaxFileBytes = 2 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly ConcurrentQueue<string> _ring = new();
    private readonly string _directory;
    private readonly string _filePrefix;
    private StreamWriter? _writer;
    private long _bytesWritten;

    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public string? CurrentFilePath { get; private set; }

    public FileLogger(string directory, string filePrefix = "hardwareosd")
    {
        _directory = directory;
        _filePrefix = filePrefix;
    }

    public static FileLogger CreateDefault(LogLevel level)
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HardwareOSD",
            "logs");
        var logger = new FileLogger(dir) { MinimumLevel = level };
        return logger;
    }

    public void Write(LogLevel level, string category, string message, Exception? exception = null)
    {
        if (level < MinimumLevel)
        {
            return;
        }

        var line = Format(level, category, message, exception);
        _ring.Enqueue(line);
        while (_ring.Count > MaxRingEntries && _ring.TryDequeue(out _)) { }

        if (level >= LogLevel.Error)
        {
            // Also surface fatal-ish diagnostics on stderr when a console is attached.
            try { Console.Error.WriteLine(line); } catch { /* ignored */ }
        }

        lock (_gate)
        {
            try
            {
                EnsureWriter();
                _writer!.WriteLine(line);
                _writer.Flush();
                _bytesWritten += line.Length + Environment.NewLine.Length;
                if (_bytesWritten > MaxFileBytes)
                {
                    RollFiles();
                }
            }
            catch
            {
                // Logging must never take the application down.
            }
        }
    }

    private static string Format(LogLevel level, string category, string message, Exception? ex)
    {
        var sb = new StringBuilder(160);
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        sb.Append(" [");
        sb.Append(level.ToString().ToUpperInvariant());
        sb.Append("] ");
        sb.Append(category);
        sb.Append(": ");
        sb.Append(message);
        if (ex is not null)
        {
            var current = ex;
            var depth = 0;
            while (current is not null && depth < 4)
            {
                sb.Append(depth == 0 ? " | " : " -> ");
                sb.Append(current.GetType().Name).Append(": ").Append(current.Message);
                current = current.InnerException;
                depth++;
            }

            if (ex.StackTrace is { Length: > 0 } st)
            {
                sb.Append(" @ ").Append(st.ReplaceLineEndings(" "));
            }
        }

        return sb.ToString();
    }

    private void EnsureWriter()
    {
        if (_writer is not null)
        {
            return;
        }

        Directory.CreateDirectory(_directory);
        var file = Path.Combine(_directory, $"{_filePrefix}-{DateTime.Now:yyyyMMdd}.log");
        var info = new FileInfo(file);
        _bytesWritten = info.Exists ? info.Length : 0;
        CurrentFilePath = file;
        _writer = new StreamWriter(new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8)
        {
            AutoFlush = true,
        };
    }

    private void RollFiles()
    {
        try
        {
            _writer?.Dispose();
            _writer = null;
            var current = CurrentFilePath;
            if (current is not null && File.Exists(current))
            {
                var rolled = Path.Combine(
                    _directory,
                    $"{_filePrefix}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                File.Move(current, rolled, overwrite: true);
                PruneOldFiles();
            }
        }
        catch
        {
            // ignored
        }
    }

    private void PruneOldFiles()
    {
        try
        {
            var files = new DirectoryInfo(_directory)
                .GetFiles($"{_filePrefix}-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(4);
            foreach (var f in files)
            {
                f.Delete();
            }
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>Recent log lines (oldest first), for the diagnostics panel.</summary>
    public IReadOnlyList<string> RecentLines(int max = 120) => _ring.Reverse().Take(max).Reverse().ToList();

    public void Dispose()
    {
        lock (_gate)
        {
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
            }
            catch
            {
                // ignored
            }
            finally
            {
                _writer = null;
            }
        }
    }
}
