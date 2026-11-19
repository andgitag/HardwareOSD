using System.Runtime.InteropServices;

namespace HardwareOSD.App.Native;

/// <summary>
/// Small, documented Win32 interop surface. Only window styles, topmost re-assertion, dark mode
/// and the global hotkey are used; the overlay never touches another process.
/// </summary>
internal static class NativeMethods
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExLayered = 0x00080000L;
    private const long WsExNoActivate = 0x08000000L;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);

    public const int WmHotkey = 0x0312;

    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;

    public const int DwmwaUseImmersiveDarkMode = 20;
    public const int DwmwaWindowCornerPreference = 33;

    public static void ApplyOverlayStyles(IntPtr hwnd, bool clickThrough)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        exStyle |= WsExLayered | WsExToolWindow;
        if (clickThrough)
        {
            exStyle |= WsExTransparent | WsExNoActivate;
        }
        else
        {
            exStyle &= ~WsExTransparent;
            exStyle &= ~WsExNoActivate;
        }

        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(exStyle));
    }

    /// <summary>
    /// Toggles only WS_EX_TRANSPARENT. Modifier-based dragging needs hit-testing but must not make
    /// the overlay activatable; WS_EX_NOACTIVATE therefore stays in place.
    /// </summary>
    public static void SetOverlayClickThrough(IntPtr hwnd, bool clickThrough)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        exStyle |= WsExLayered | WsExToolWindow | WsExNoActivate;
        if (clickThrough)
        {
            exStyle |= WsExTransparent;
        }
        else
        {
            exStyle &= ~WsExTransparent;
        }

        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(exStyle));
    }

    public static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public static void ReassertTopmost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    /// <summary>Moves the overlay to physical screen pixels without resizing or changing Z-order.</summary>
    public static void MoveOverlay(IntPtr hwnd, int x, int y)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    public static bool GetCursorPosition(out int x, out int y)
    {
        if (GetCursorPos(out var point))
        {
            x = point.X;
            y = point.Y;
            return true;
        }

        x = 0;
        y = 0;
        return false;
    }

    public static void DropTopmost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(hwnd, HwndNotTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    public static void EnableDarkTitleBar(IntPtr hwnd)
    {
        SetTitleBarTheme(hwnd, dark: true);
    }

    /// <summary>Switches the native title bar between the dark and light DWM variants.</summary>
    public static void SetTitleBarTheme(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var enabled = dark ? 1 : 0;
        try
        {
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
            var rounded = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref rounded, sizeof(int));
        }
        catch
        {
            // Older builds do not support these attributes.
        }
    }

    public static bool RegisterHotkey(IntPtr hwnd, int id, uint modifiers, uint virtualKey)
        => RegisterHotKey(hwnd, id, modifiers, virtualKey);

    public static void UnregisterHotkey(IntPtr hwnd, int id)
    {
        if (hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(hwnd, id);
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out Point32 point);
}
