using System.Diagnostics;
using Microsoft.Win32;

namespace HardwareOSD.Core.SystemInfo;

/// <summary>Truthful state of an optional software component. Missing is not an error.</summary>
public enum SoftwareComponentState
{
    Unknown = 0,
    Available = 1,
    Running = 2,
    Installed = 3,
    Bundled = 4,
    AccessDenied = 5,
    Missing = 6,
}

/// <summary>One card in the Software Components section.</summary>
public sealed record SoftwareComponentInfo(
    string Id,
    string? Version,
    SoftwareComponentState State,
    string? Detail = null);

/// <summary>
/// Local, read-only version probes for the components HardwareOSD actually ships with or uses.
/// No network access and no invented components: absent software reports Missing.
/// </summary>
public static class SoftwareComponentProbe
{
    private const string PawnIoUninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";

    public static string? PawnIoVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PawnIoUninstallKey);
            return TrimVersion(key?.GetValue("DisplayVersion") as string);
        }
        catch
        {
            return null;
        }
    }

    public static string? LibreHardwareMonitorVersion()
    {
        try
        {
            var version = typeof(LibreHardwareMonitor.Hardware.Computer).Assembly.GetName().Version;
            return version is null ? null : $"{version.Major}.{version.Minor}.{version.Build}";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>nvml.dll ships in System32 on current drivers; older installs kept it in NVSMI.</summary>
    public static string? NvmlPath()
    {
        var system = Path.Combine(Environment.SystemDirectory, "nvml.dll");
        if (File.Exists(system))
        {
            return system;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles))
        {
            return null;
        }

        var legacy = Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvml.dll");
        return File.Exists(legacy) ? legacy : null;
    }

    public static string? FileVersion(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            return TrimVersion(version.ProductVersion ?? version.FileVersion);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Executable path of an installed Windows service. Used for the Fan Broker version when the
    /// current build folder does not contain its own copy; the service is installed separately.
    /// </summary>
    public static string? ServiceImagePath(string serviceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\" + serviceName);
            return ParseServiceImagePath(key?.GetValue("ImagePath") as string);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Handles quoted paths, unquoted paths with arguments and the \??\ device prefix.</summary>
    public static string? ParseServiceImagePath(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string path;
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            path = end > 1 ? text[1..end] : text.Trim('"');
        }
        else
        {
            var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            path = exe >= 0 ? text[..(exe + 4)] : text.Split(' ')[0];
        }

        path = Environment.ExpandEnvironmentVariables(path.Trim());
        if (path.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            path = path[4..];
        }

        return path.Length == 0 ? null : path;
    }

    /// <summary>Strips a trailing ".0" so a four-part driver version reads like the vendor's own.</summary>
    public static string? TrimVersion(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Split('.');
        if (parts.Length == 4 && parts[3] == "0")
        {
            return string.Join(".", parts.Take(3));
        }

        return text;
    }
}
