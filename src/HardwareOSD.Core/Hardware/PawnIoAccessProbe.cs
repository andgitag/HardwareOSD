using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HardwareOSD.Core.Hardware;

public enum PawnIoState
{
    Unknown = 0,
    NotInstalled = 1,
    AccessDenied = 2,
    Available = 3,
}

/// <summary>Result of probing the external PawnIO driver used by LibreHardwareMonitor for MSR access.</summary>
public readonly record struct PawnIoStatus(PawnIoState State, string Detail);

/// <summary>
/// Read-only probe of the PawnIO kernel driver. It never installs, starts or reconfigures anything:
/// it checks the service registration and tries to open the device read/write. On a default Windows
/// installation the device ACL allows administrators only, which is why CPU package temperature and
/// package power are unavailable in a non-elevated process even when the driver is installed.
/// </summary>
public static class PawnIoAccessProbe
{
    private const string DevicePath = @"\\?\GLOBALROOT\Device\PawnIO";
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;
    private static readonly IntPtr InvalidHandle = new(-1);

    public static PawnIoStatus Query()
    {
        var installed = IsInstalled();
        try
        {
            var handle = CreateFileW(DevicePath, GenericRead | GenericWrite, ShareReadWrite,
                IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            var error = Marshal.GetLastWin32Error();
            if (handle != IntPtr.Zero && handle != InvalidHandle)
            {
                CloseHandle(handle);
                return new PawnIoStatus(PawnIoState.Available, "PawnIO device opened for MSR access");
            }

            if (!installed)
            {
                return new PawnIoStatus(PawnIoState.NotInstalled,
                    "PawnIO is not installed; LibreHardwareMonitor cannot read CPU MSRs");
            }

            return error switch
            {
                5 => new PawnIoStatus(PawnIoState.AccessDenied,
                    "PawnIO is installed but the device is accessible to administrators only (Win32 5: access denied)"),
                2 or 3 => new PawnIoStatus(PawnIoState.NotInstalled,
                    "PawnIO service is registered but its device is not present (Win32 2/3)"),
                _ => new PawnIoStatus(PawnIoState.Unknown,
                    $"PawnIO device could not be opened (Win32 {error})"),
            };
        }
        catch (Exception ex)
        {
            return new PawnIoStatus(installed ? PawnIoState.Unknown : PawnIoState.NotInstalled,
                "PawnIO probe failed: " + ex.Message);
        }
    }

    private static bool IsInstalled()
    {
        try
        {
            using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            if (service is not null)
            {
                return true;
            }

            using var uninstall = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
            return uninstall is not null;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
