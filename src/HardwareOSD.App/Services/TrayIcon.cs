using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using HardwareOSD.App.Localization;
using HardwareOSD.Core.Config;

namespace HardwareOSD.App.Services;

/// <summary>
/// System tray integration implemented directly on Shell_NotifyIcon, so no WinForms dependency is
/// needed. The overlay keeps running when the configuration window is hidden or closed.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WmApp = 0x8000;
    private const int WmTrayIcon = WmApp + 1;
    private const int WmCommand = 0x0111;
    private const int WmNull = 0x0000;
    private const int WmLeftButtonUp = 0x0202;
    private const int WmLeftButtonDoubleClick = 0x0203;
    private const int WmRightButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;

    private const int NimAdd = 0;
    private const int NimModify = 1;
    private const int NimDelete = 2;

    private const int NifMessage = 0x01;
    private const int NifIcon = 0x02;
    private const int NifTip = 0x04;
    private const int NifInfo = 0x10;

    private const int MfString = 0x0000;
    private const int MfSeparator = 0x0800;
    private const int MfChecked = 0x0008;
    private const int MfPopup = 0x0010;
    private const int TpmRightButton = 0x0002;
    private const int TpmReturnCmd = 0x0100;
    private const int TpmNoNotify = 0x0080;

    private const int IdiApplication = 32512;

    private const uint CmdOverlay = 1;
    private const uint CmdClickThrough = 2;
    private const uint CmdSettings = 3;
    private const uint CmdMonitoring = 4;
    private const uint CmdInterval500 = 5;
    private const uint CmdInterval1000 = 6;
    private const uint CmdInterval2000 = 7;
    private const uint CmdInterval3000 = 8;
    private const uint CmdInterval5000 = 9;
    private const uint CmdElevate = 10;
    private const uint CmdLogFolder = 11;
    private const uint CmdExit = 12;
    private const uint CmdMove = 13;

    private readonly AppRuntime _runtime;
    private readonly HwndSource _source;
    private readonly IntPtr _handle;
    private readonly IntPtr _icon;
    private bool _disposed;

    public TrayIcon(AppRuntime runtime)
    {
        _runtime = runtime;

        var parameters = new HwndSourceParameters("HardwareOSD.Tray")
        {
            Width = 1,
            Height = 1,
            PositionX = -32000,
            PositionY = -32000,
            WindowStyle = 0,
        };

        _source = new HwndSource(parameters);
        _handle = _source.Handle;
        _source.AddHook(WndProc);

        _icon = LoadApplicationIcon();
        AddOrUpdateIcon(NifMessage | NifIcon | NifTip);
        Sync();
    }

    public void Sync()
    {
        if (_disposed)
        {
            return;
        }

        var tooltip = _runtime.DemoMode
            ? Loc.T("tray_tooltip_demo")
            : Loc.T(_runtime.IsMonitoring ? "tray_tooltip_monitoring" : "tray_tooltip_paused");
        AddOrUpdateIcon(NifTip, tooltip);
    }

    public void ShowBalloon(string title, string text)
    {
        if (_disposed || !_runtime.Settings.Behavior.ShowTrayNotifications)
        {
            return;
        }

        var data = CreateData(NifInfo);
        data.szInfoTitle = title;
        data.szInfo = text;
        data.dwInfoFlags = 1; // NIIF_INFO
        Shell_NotifyIconW(NimModify, ref data);
    }

    private void AddOrUpdateIcon(int flags, string? tooltip = null)
    {
        var data = CreateData(flags);
        if (tooltip is not null)
        {
            data.szTip = tooltip.Length > 120 ? tooltip[..120] : tooltip;
        }

        var isNew = !_added;
        var ok = Shell_NotifyIconW(isNew ? NimAdd : NimModify, ref data);
        if (isNew)
        {
            _runtime.Logger.Write(Core.Diagnostics.LogLevel.Info, "Tray",
                ok ? "Notification area icon registered" : "Shell_NotifyIcon(NIM_ADD) failed");
        }

        _added = true;
    }

    private bool _added;

    private NotifyIconData CreateData(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = _handle,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WmTrayIcon,
        hIcon = _icon,
        szTip = "HardwareOSD",
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmTrayIcon)
        {
            var mouseMessage = lParam.ToInt32() & 0xFFFF;
            switch (mouseMessage)
            {
                case WmLeftButtonDoubleClick:
                case WmLeftButtonUp:
                    _runtime.ShowSettings();
                    handled = true;
                    break;
                case WmRightButtonUp:
                case WmContextMenu:
                    ShowMenu();
                    handled = true;
                    break;
            }
        }
        else if (msg == WmCommand)
        {
            HandleCommand((uint)(wParam.ToInt32() & 0xFFFF));
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var settings = _runtime.Settings;
            AppendMenuW(menu, MfString | (_runtime.IsOverlayVisible ? MfChecked : 0), (UIntPtr)CmdOverlay,
                Loc.T(_runtime.IsOverlayVisible ? "tray_hide" : "tray_show"));
            AppendMenuW(menu, MfString, (UIntPtr)CmdMove,
                Loc.T(_runtime.IsOverlayMoving ? "tray_cancel_move" : "tray_move"));
            AppendMenuW(menu, MfString | (settings.Overlay.ClickThrough ? MfChecked : 0), (UIntPtr)CmdClickThrough,
                Loc.T("tray_click_through"));
            AppendMenuW(menu, MfSeparator, UIntPtr.Zero, null);
            AppendMenuW(menu, MfString, (UIntPtr)CmdSettings, Loc.T("tray_settings"));
            AppendMenuW(menu, MfString | (_runtime.IsMonitoring ? MfChecked : 0), (UIntPtr)CmdMonitoring,
                Loc.T(_runtime.IsMonitoring ? "tray_monitoring_active" : "tray_monitoring_paused"));

            var intervals = CreatePopupMenu();
            foreach (var (id, ms) in new[]
                     {
                         (CmdInterval500, 500), (CmdInterval1000, 1000), (CmdInterval2000, 2000),
                         (CmdInterval3000, 3000), (CmdInterval5000, 5000),
                     })
            {
                AppendMenuW(intervals, MfString | (settings.Monitoring.PollIntervalMs == ms ? MfChecked : 0),
                    (UIntPtr)id, $"{ms} ms");
            }

            // MF_POPUP stores the submenu handle in the item id field, which is 64-bit on x64:
            // it must not be truncated to a 32-bit value.
            AppendMenuW(menu, MfPopup, (UIntPtr)(ulong)intervals.ToInt64(), Loc.T("tray_polling"));
            AppendMenuW(menu, MfSeparator, UIntPtr.Zero, null);
            AppendMenuW(menu, MfString, (UIntPtr)CmdElevate, Loc.T("tray_restart_admin"));
            AppendMenuW(menu, MfString, (UIntPtr)CmdLogFolder, Loc.T("tray_open_logs"));
            AppendMenuW(menu, MfSeparator, UIntPtr.Zero, null);
            AppendMenuW(menu, MfString, (UIntPtr)CmdExit, Loc.T("tray_exit"));

            GetCursorPos(out var point);
            SetForegroundWindow(_handle);
            var command = TrackPopupMenuEx(menu, TpmReturnCmd | TpmRightButton | TpmNoNotify,
                point.X, point.Y, _handle, IntPtr.Zero);
            PostMessageW(_handle, WmNull, IntPtr.Zero, IntPtr.Zero);

            if (command != 0)
            {
                HandleCommand((uint)command);
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void HandleCommand(uint command)
    {
        switch (command)
        {
            case CmdOverlay:
                _runtime.ToggleOverlay();
                break;
            case CmdMove:
                if (_runtime.IsOverlayMoving)
                {
                    _runtime.CancelOverlayMove();
                }
                else
                {
                    _runtime.BeginOverlayMove();
                }

                break;
            case CmdClickThrough:
                _runtime.Settings.Overlay.ClickThrough = !_runtime.Settings.Overlay.ClickThrough;
                _runtime.ApplySettings(_runtime.Settings.Clone(), persist: true);
                break;
            case CmdSettings:
                _runtime.ShowSettings();
                break;
            case CmdMonitoring:
                _runtime.ToggleMonitoring();
                break;
            case CmdInterval500:
            case CmdInterval1000:
            case CmdInterval2000:
            case CmdInterval3000:
            case CmdInterval5000:
                var ms = command switch
                {
                    CmdInterval500 => 500,
                    CmdInterval1000 => 1000,
                    CmdInterval2000 => 2000,
                    CmdInterval3000 => 3000,
                    _ => 5000,
                };
                _runtime.Settings.Monitoring.PollIntervalMs = ms;
                _runtime.ApplySettings(_runtime.Settings.Clone(), persist: true);
                break;
            case CmdElevate:
                _runtime.RestartElevated();
                break;
            case CmdLogFolder:
                _runtime.OpenLogFolder();
                break;
            case CmdExit:
                _runtime.Exit();
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            var data = CreateData(0);
            Shell_NotifyIconW(NimDelete, ref data);
        }
        catch
        {
            // ignored
        }

        try
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
        }
        catch
        {
            // ignored
        }

        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
        }
    }

    private static IntPtr LoadApplicationIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && ExtractIconExW(exe, 0, out var large, out var small, 1) > 0)
            {
                if (large != IntPtr.Zero)
                {
                    DestroyIcon(large);
                }

                if (small != IntPtr.Zero)
                {
                    return small;
                }
            }
        }
        catch
        {
            // ignored
        }

        return LoadIconW(IntPtr.Zero, (IntPtr)IdiApplication);
    }

    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch
        {
            // ignored
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public int dwState;
        public int dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public int uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(int message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr menu, int flags, UIntPtr itemId, string? text);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, int flags, int x, int y, IntPtr hwnd, IntPtr parameters);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point32 point);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIconW(IntPtr instance, IntPtr name);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconExW(string file, int index, out IntPtr large, out IntPtr small, int count);
}
