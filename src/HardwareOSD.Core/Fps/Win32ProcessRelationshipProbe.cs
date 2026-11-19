using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HardwareOSD.Core.Fps;

/// <summary>
/// Read-only Win32 implementation of <see cref="IProcessRelationshipProbe"/>.
///
/// <para>Process facts come from <c>QueryFullProcessImageNameW</c> and a Toolhelp process snapshot.
/// Window facts come from <c>EnumWindows</c>/<c>EnumChildWindows</c>, <c>GetClassNameW</c>,
/// <c>IsWindowVisible</c> and <c>GetAncestor</c>. Nothing is injected, hooked, read from process
/// memory or written to the target.</para>
///
/// <para>The top-level window list is cached for 500 ms because the resolver may ask for it on
/// several consecutive publishes while the foreground is a multi-process application.</para>
/// </summary>
public sealed class Win32ProcessRelationshipProbe : IProcessRelationshipProbe
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Th32CsSnapProcess = 0x00000002;
    private const uint GwOwner = 4;
    private const uint GaRoot = 2;
    private const uint GaRootOwner = 3;
    private const int MaxPath = 1024;
    private const long WindowCacheMs = 500;

    private readonly object _gate = new();
    private IReadOnlyList<WindowDescriptor> _windows = Array.Empty<WindowDescriptor>();
    private long _windowsExpiresMs;

    public ProcessIdentity? GetIdentity(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        string? name = null;
        var parent = 0;
        try
        {
            var snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
            if (snapshot != IntPtr.Zero && snapshot != new IntPtr(-1))
            {
                try
                {
                    var entry = new ProcessEntry32W { Size = (uint)Marshal.SizeOf<ProcessEntry32W>() };
                    if (Process32FirstW(snapshot, ref entry))
                    {
                        do
                        {
                            if (entry.ProcessId == (uint)processId)
                            {
                                name = Path.GetFileNameWithoutExtension(entry.ExeFile);
                                parent = (int)entry.ParentProcessId;
                                break;
                            }
                        }
                        while (Process32NextW(snapshot, ref entry));
                    }
                }
                finally
                {
                    CloseHandle(snapshot);
                }
            }
        }
        catch
        {
            // Enumeration is best-effort; the pid still identifies the process.
        }

        var path = GetImagePath(processId);
        if (name is null && !string.IsNullOrEmpty(path))
        {
            try
            {
                name = Path.GetFileNameWithoutExtension(path);
            }
            catch
            {
                // ignored
            }
        }

        long startTicks = 0;
        try
        {
            using var process = Process.GetProcessById(processId);
            startTicks = process.StartTime.ToUniversalTime().Ticks;
            name ??= process.ProcessName;
        }
        catch
        {
            // Exited or protected process: the pid is still valid for this measurement.
        }

        if (name is null && path is null && parent == 0 && startTicks == 0)
        {
            return null;
        }

        return new ProcessIdentity(processId, path, name, parent, startTicks);
    }

    public IReadOnlyList<WindowDescriptor> GetTopLevelWindows()
    {
        var now = Environment.TickCount64;
        lock (_gate)
        {
            if (_windowsExpiresMs > now)
            {
                return _windows;
            }
        }

        var list = new List<WindowDescriptor>(128);
        try
        {
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == 0 || pid > int.MaxValue)
                {
                    return true;
                }

                var className = GetWindowClassName(hwnd);
                var owner = GetWindow(hwnd, GwOwner);
                var rootOwner = GetAncestor(hwnd, GaRootOwner);
                var root = GetAncestor(hwnd, GaRoot);
                list.Add(new WindowDescriptor(
                    hwnd,
                    (int)pid,
                    className,
                    IsWindowVisible(hwnd),
                    owner,
                    rootOwner,
                    root));
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // An enumeration failure yields an empty snapshot; the resolver degrades to pid rules.
        }

        lock (_gate)
        {
            _windows = list;
            _windowsExpiresMs = now + WindowCacheMs;
            return _windows;
        }
    }

    public int ResolveUwpChild(IntPtr frameHostWindow, int frameHostProcessId)
    {
        if (frameHostWindow == IntPtr.Zero)
        {
            return 0;
        }

        var resolved = 0;
        try
        {
            EnumChildWindows(frameHostWindow, (child, _) =>
            {
                GetWindowThreadProcessId(child, out var pid);
                if (pid == 0 || pid == (uint)frameHostProcessId || pid > int.MaxValue)
                {
                    return true;
                }

                var className = GetWindowClassName(child);
                if (className is null ||
                    (!className.Contains("CoreWindow", StringComparison.OrdinalIgnoreCase) &&
                     !className.Contains("Windows.UI.Core", StringComparison.OrdinalIgnoreCase)) ||
                    !IsWindowVisible(child))
                {
                    return true;
                }

                resolved = (int)pid;
                return false;
            }, IntPtr.Zero);
        }
        catch
        {
            return 0;
        }

        return resolved;
    }

    private static string? GetImagePath(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var builder = new StringBuilder(MaxPath);
            var size = builder.Capacity;
            if (QueryFullProcessImageNameW(handle, 0, builder, ref size))
            {
                return builder.ToString(0, size);
            }
        }
        catch
        {
            // ignored
        }
        finally
        {
            CloseHandle(handle);
        }

        return null;
    }

    private static string? GetWindowClassName(IntPtr hwnd)
    {
        try
        {
            var builder = new StringBuilder(256);
            var length = GetClassNameW(hwnd, builder, builder.Capacity);
            return length > 0 ? builder.ToString(0, length) : null;
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32W
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(
        IntPtr process, uint flags, StringBuilder exeName, ref int size);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
}
