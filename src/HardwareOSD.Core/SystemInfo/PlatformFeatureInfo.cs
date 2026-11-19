using Microsoft.Win32;

namespace HardwareOSD.Core.SystemInfo;

/// <summary>Tri-state rather than bool: "not reported" must stay distinguishable from "off".</summary>
public enum FeatureStatus
{
    Unknown = 0,
    On = 1,
    Off = 2,
}

/// <summary>
/// Platform/gaming features shown as rows in the Mainboard card. Every value is read from a
/// documented Windows location or derived from an NVML reading; nothing is assumed from the CPU/GPU
/// model. ReBAR is a derived signal (see <see cref="FromBar1"/>), the other two are registry values.
/// </summary>
public sealed record PlatformFeatureSnapshot(
    FeatureStatus ResizableBar,
    FeatureStatus HardwareGpuScheduling,
    FeatureStatus GameMode,
    string? Note = null);

public static class PlatformFeatureInfo
{
    private const string GraphicsDriversKey = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string GameBarKey = @"Software\Microsoft\GameBar";

    /// <summary>BAR1 at or above 90% of VRAM means the full aperture is mapped (ReBAR on).</summary>
    private const double ReBarOnRatio = 0.9;

    /// <summary>A legacy 256 MB aperture never exceeds this.</summary>
    private const long ReBarOffCeilingBytes = 512L * 1024 * 1024;

    public static PlatformFeatureSnapshot Read(long? bar1TotalBytes = null, long? vramTotalBytes = null)
    {
        var hwsch = ReadDword(RegistryHive.LocalMachine, GraphicsDriversKey, "HwSchMode");
        var gameMode = ReadDword(RegistryHive.CurrentUser, GameBarKey, "AutoGameModeEnabled") ??
                       ReadDword(RegistryHive.CurrentUser, GameBarKey, "AllowAutoGameMode");

        return new PlatformFeatureSnapshot(
            FromBar1(bar1TotalBytes, vramTotalBytes),
            FromHwSchMode(hwsch),
            FromGameMode(gameMode));
    }

    /// <summary>HwSchMode: 2 = hardware-accelerated GPU scheduling enabled, 1 = disabled.</summary>
    public static FeatureStatus FromHwSchMode(int? value)
        => value switch
        {
            2 => FeatureStatus.On,
            1 => FeatureStatus.Off,
            _ => FeatureStatus.Unknown,
        };

    /// <summary>GameBar AutoGameModeEnabled/AllowAutoGameMode: 1 = on, 0 = off.</summary>
    public static FeatureStatus FromGameMode(int? value)
        => value switch
        {
            1 => FeatureStatus.On,
            0 => FeatureStatus.Off,
            _ => FeatureStatus.Unknown,
        };

    /// <summary>
    /// NVML reports the BAR1 aperture size. With Resizable BAR the aperture covers the whole frame
    /// buffer; without it the legacy 256 MB window remains. Values between the two are treated as
    /// unknown instead of being guessed.
    /// </summary>
    public static FeatureStatus FromBar1(long? bar1TotalBytes, long? vramTotalBytes)
    {
        if (bar1TotalBytes is not { } bar1 || bar1 <= 0)
        {
            return FeatureStatus.Unknown;
        }

        if (vramTotalBytes is { } vram && vram > 0 && bar1 >= vram * ReBarOnRatio)
        {
            return FeatureStatus.On;
        }

        return bar1 <= ReBarOffCeilingBytes ? FeatureStatus.Off : FeatureStatus.Unknown;
    }

    private static int? ReadDword(RegistryHive hive, string path, string value)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(path);
            return key?.GetValue(value) is int number ? number : null;
        }
        catch
        {
            return null;
        }
    }
}
