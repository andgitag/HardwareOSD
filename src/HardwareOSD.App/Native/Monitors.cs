using System.Runtime.InteropServices;
using System.Text;

namespace HardwareOSD.App.Native;

public sealed record MonitorInfo(
    int Index,
    string DeviceName,
    string DisplayLabel,
    int Left,
    int Top,
    int Width,
    int Height,
    int WorkLeft,
    int WorkTop,
    int WorkWidth,
    int WorkHeight,
    bool IsPrimary,
    double Scale);

/// <summary>
/// Monitor enumeration through the documented Win32 display APIs. Positions are physical pixels;
/// the overlay uses them with SetWindowPos so mixed-DPI layouts are handled by the OS rather than
/// by WPF's DIP approximation.
/// </summary>
internal static class Monitors
{
    private const uint MonitorInfoPrimary = 0x00000001;
    private const int MdtEffectiveDpi = 0;

    public static IReadOnlyList<MonitorInfo> All()
    {
        var monitors = new List<MonitorInfo>(2);
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (handle, _, _, _) =>
            {
                var info = new MonitorInfoEx();
                info.Size = Marshal.SizeOf<MonitorInfoEx>();
                if (!GetMonitorInfoW(handle, ref info))
                {
                    return true;
                }

                var scale = 1.0;
                try
                {
                    if (GetDpiForMonitor(handle, MdtEffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0)
                    {
                        scale = dpiX / 96.0;
                    }
                }
                catch
                {
                    // Shcore is unavailable on very old builds; assume 100 %.
                }

                var device = info.DeviceName ?? string.Empty;
                var index = monitors.Count + 1;
                monitors.Add(new MonitorInfo(
                    index,
                    device,
                    $"Display {index} — {info.Monitor.Right - info.Monitor.Left}×{info.Monitor.Bottom - info.Monitor.Top} @ {scale * 100:0}%",
                    info.Monitor.Left,
                    info.Monitor.Top,
                    info.Monitor.Right - info.Monitor.Left,
                    info.Monitor.Bottom - info.Monitor.Top,
                    info.Work.Left,
                    info.Work.Top,
                    info.Work.Right - info.Work.Left,
                    info.Work.Bottom - info.Work.Top,
                    (info.Flags & MonitorInfoPrimary) != 0,
                    scale));

                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // fall through to the primary-only fallback
        }

        if (monitors.Count == 0)
        {
            var width = GetSystemMetrics(0);
            var height = GetSystemMetrics(1);
            monitors.Add(new MonitorInfo(1, string.Empty, $"Display 1 — {width}×{height}",
                0, 0, width, height, 0, 0, width, height, true, 1.0));
        }

        return monitors;
    }

    public static MonitorInfo Primary(IReadOnlyList<MonitorInfo>? monitors = null)
    {
        monitors ??= All();
        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }

    public static MonitorInfo? Find(string? deviceName, IReadOnlyList<MonitorInfo>? monitors = null)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return null;
        }

        monitors ??= All();
        return monitors.FirstOrDefault(m =>
            string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
    }

    public static MonitorInfo FromPoint(int x, int y, IReadOnlyList<MonitorInfo>? monitors = null)
    {
        monitors ??= All();
        foreach (var monitor in monitors)
        {
            if (x >= monitor.Left && x < monitor.Left + monitor.Width &&
                y >= monitor.Top && y < monitor.Top + monitor.Height)
            {
                return monitor;
            }
        }

        return Primary(monitors);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect32 Monitor;
        public Rect32 Work;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}
