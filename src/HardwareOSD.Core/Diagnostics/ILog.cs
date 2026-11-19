namespace HardwareOSD.Core.Diagnostics;

/// <summary>
/// Minimal logging abstraction. Implementations must be thread safe and must never throw.
/// </summary>
public interface ILog
{
    void Write(LogLevel level, string category, string message, Exception? exception = null);
}

public static class LogExtensions
{
    public static void Debug(this ILog log, string category, string message) => log.Write(LogLevel.Debug, category, message);
    public static void Info(this ILog log, string category, string message) => log.Write(LogLevel.Info, category, message);
    public static void Warn(this ILog log, string category, string message, Exception? ex = null) => log.Write(LogLevel.Warning, category, message, ex);
    public static void Error(this ILog log, string category, string message, Exception? ex = null) => log.Write(LogLevel.Error, category, message, ex);
}

/// <summary>A logger that discards everything. Used by tests and headless helpers.</summary>
public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();
    public void Write(LogLevel level, string category, string message, Exception? exception = null) { }
}
