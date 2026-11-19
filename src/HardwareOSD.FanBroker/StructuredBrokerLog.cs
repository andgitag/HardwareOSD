using System.Text;
using System.Text.Json;
using HardwareOSD.Core.Diagnostics;

namespace HardwareOSD.FanBroker;

/// <summary>Small JSON-lines logger for the LocalSystem service. It never throws into the caller.</summary>
internal sealed class StructuredBrokerLog : ILog, IDisposable
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _prefix;
    private StreamWriter? _writer;
    private long _bytes;
    private bool _disposed;

    public StructuredBrokerLog(string directory, string prefix = "fan-broker")
    {
        _directory = directory;
        _prefix = prefix;
    }

    public void Write(LogLevel level, string category, string message, Exception? exception = null)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.UtcNow,
                level = level.ToString(),
                category,
                message,
                exception = exception is null ? null : new
                {
                    type = exception.GetType().FullName,
                    message = exception.Message,
                    stack = exception.StackTrace,
                },
            });

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                EnsureWriter();
                _writer!.WriteLine(payload);
                _writer.Flush();
                _bytes += Encoding.UTF8.GetByteCount(payload) + Environment.NewLine.Length;
                if (_bytes > MaxBytes)
                {
                    Roll();
                }
            }
        }
        catch
        {
            // Logging must not stop the broker.
        }
    }

    private void EnsureWriter()
    {
        if (_writer is not null)
        {
            return;
        }

        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"{_prefix}-{DateTime.UtcNow:yyyyMMdd}.log");
        _bytes = File.Exists(path) ? new FileInfo(path).Length : 0;
        _writer = new StreamWriter(
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(false))
        {
            AutoFlush = true,
        };
    }

    private void Roll()
    {
        try
        {
            _writer?.Dispose();
            _writer = null;
            var current = Path.Combine(_directory, $"{_prefix}-{DateTime.UtcNow:yyyyMMdd}.log");
            if (File.Exists(current))
            {
                File.Move(current,
                    Path.Combine(_directory, $"{_prefix}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log"),
                    overwrite: true);
            }

            foreach (var old in new DirectoryInfo(_directory)
                         .GetFiles($"{_prefix}-*.log")
                         .OrderByDescending(file => file.LastWriteTimeUtc)
                         .Skip(5))
            {
                old.Delete();
            }
        }
        catch
        {
            // ignored
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            try
            {
                _writer?.Dispose();
            }
            catch
            {
                // ignored
            }

            _writer = null;
        }
    }
}
