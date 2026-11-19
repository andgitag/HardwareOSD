using Microsoft.Win32;
using HardwareOSD.Core.Diagnostics;
using System.IO;

namespace HardwareOSD.App.Services;

/// <summary>
/// HKCU Run-key registration. Per-user, no elevation and no scheduled task required.
/// The key is only written from an explicit user toggle; normal launches never create it.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "HardwareOSD";

    /// <summary>True when the Run value exists, regardless of Task Manager's approval flag.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when Windows will actually start the app. Task Manager can leave the Run value in place
    /// but mark it disabled in StartupApproved (first byte 0x03); that state must be respected.
    /// </summary>
    public static bool IsActive()
    {
        if (!IsEnabled())
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedKey, writable: false);
            return key?.GetValue(ValueName) is not byte[] { Length: > 0 } data || data[0] != 0x03;
        }
        catch
        {
            return true;
        }
    }

    public static bool Set(bool enabled, ILog log)
    {
        try
        {
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe) ||
                    !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileName(exe), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
                {
                    // Running through `dotnet HardwareOSD.dll` - registering the host would be wrong.
                    log.Warn("Startup", "Cannot register startup: executable path is unavailable or is the .NET host");
                    return false;
                }

                using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
                if (key is null)
                {
                    return false;
                }

                key.SetValue(ValueName, $"\"{exe}\" --startup", RegistryValueKind.String);
                ClearApproval();
                log.Info("Startup", $"Start with Windows enabled ({exe})");
            }
            else
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
                {
                    key?.DeleteValue(ValueName, throwOnMissingValue: false);
                }

                ClearApproval();
                log.Info("Startup", "Start with Windows disabled");
            }

            return true;
        }
        catch (Exception ex)
        {
            log.Error("Startup", "Failed to update the Run key", ex);
            return false;
        }
    }

    /// <summary>
    /// Removes a stale Task Manager "disabled" flag. Without this, enabling the option in HardwareOSD
    /// would write the Run value but Windows would keep ignoring it.
    /// </summary>
    private static void ClearApproval()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // The approval entry is advisory; failing to clear it must not break the Run update.
        }
    }
}
