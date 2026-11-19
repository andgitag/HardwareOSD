using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HardwareOSD.Core.Fps;

public interface IForegroundAppProbe
{
    int OwnProcessId { get; }

    /// <summary>Process id owning the foreground window, or 0 when there is none.</summary>
    int GetForegroundProcessId();

    /// <summary>Executable name of a process, or null when it cannot be queried.</summary>
    string? GetProcessName(int processId);
}

/// <summary>
/// Foreground window process plus whether it fills its monitor. <see cref="WindowHandle"/> and
/// <see cref="ClassName"/> are used by the All Applications resolver for UWP frame hosts and the
/// desktop-shell check; they are optional so existing fakes stay valid.
/// </summary>
public readonly record struct ForegroundWindowInfo(
    int ProcessId,
    bool IsFullscreen,
    IntPtr WindowHandle = default,
    string? ClassName = null);

/// <summary>
/// Optional probe extension. The service uses the fullscreen flag to avoid reporting desktop/UI
/// frame rates (Steam, browsers, Electron shells) as a game's FPS.
/// </summary>
public interface IForegroundWindowProbe
{
    ForegroundWindowInfo GetForegroundWindowInfo();
}

/// <summary>
/// Foreground window and process name lookups. Only the window handle and the process id are read;
/// the target process is never opened for memory access, injected or otherwise inspected.
/// </summary>
public sealed class Win32ForegroundAppProbe : IForegroundAppProbe, IForegroundWindowProbe
{
    private readonly object _gate = new();
    private readonly Dictionary<int, (string? Name, long ExpiresMs)> _nameCache = new();
    private readonly long _cacheMs;

    public Win32ForegroundAppProbe(long cacheMs = 5000)
    {
        _cacheMs = Math.Max(250, cacheMs);
    }

    public int OwnProcessId => Environment.ProcessId;

    public int GetForegroundProcessId() => GetForegroundWindowInfo().ProcessId;

    public ForegroundWindowInfo GetForegroundWindowInfo()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return new ForegroundWindowInfo(0, false);
            }

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || pid > int.MaxValue)
            {
                return new ForegroundWindowInfo(0, false);
            }

            return new ForegroundWindowInfo((int)pid, IsFullscreen(hwnd), hwnd, GetWindowClassName(hwnd));
        }
        catch
        {
            return new ForegroundWindowInfo(0, false);
        }
    }

    private static bool IsFullscreen(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var window))
        {
            return false;
        }

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info))
        {
            return false;
        }

        // A normal maximized window stops at the work area (taskbar excluded); a game in borderless
        // or exclusive fullscreen covers the whole monitor.
        return window.Left <= info.Monitor.Left &&
               window.Top <= info.Monitor.Top &&
               window.Right >= info.Monitor.Right &&
               window.Bottom >= info.Monitor.Bottom;
    }

    private static string? GetWindowClassName(IntPtr hwnd)
    {
        try
        {
            var builder = new System.Text.StringBuilder(256);
            var length = GetClassNameW(hwnd, builder, builder.Capacity);
            return length > 0 ? builder.ToString(0, length) : null;
        }
        catch
        {
            return null;
        }
    }

    public string? GetProcessName(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        var now = Environment.TickCount64;
        lock (_gate)
        {
            if (_nameCache.TryGetValue(processId, out var cached) && cached.ExpiresMs > now)
            {
                return cached.Name;
            }
        }

        string? name = null;
        try
        {
            using var process = Process.GetProcessById(processId);
            name = process.ProcessName;
        }
        catch
        {
            // Exited or protected process; the id is still valid for this measurement.
        }

        lock (_gate)
        {
            if (_nameCache.Count > 64)
            {
                // Keep the cache bounded; stale ids are re-queried on demand.
                var expired = _nameCache.Where(p => p.Value.ExpiresMs <= now).Select(p => p.Key).ToList();
                foreach (var key in expired)
                {
                    _nameCache.Remove(key);
                }

                if (_nameCache.Count > 96)
                {
                    _nameCache.Clear();
                }
            }

            _nameCache[processId] = (name, now + _cacheMs);
        }

        return name;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect32 rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, System.Text.StringBuilder className, int maxCount);

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect32 Monitor;
        public Rect32 Work;
        public uint Flags;
    }
}
