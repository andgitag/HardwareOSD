using System.Globalization;

namespace HardwareOSD.Core.SystemInfo;

/// <summary>Hardware component a brand tile is drawn for. The kind restricts which vendors are valid.</summary>
public enum HardwareComponentKind
{
    Cpu,
    Gpu,
    Mainboard,
    Memory,
    Storage,
}

/// <summary>Vendors with a local wordmark tile. Unknown means "draw the neutral chip icon".</summary>
public enum HardwareBrand
{
    Unknown,
    Intel,
    Amd,
    Nvidia,
    Msi,
    Asus,
    Gigabyte,
    Asrock,
    Crucial,
    Kingston,
    Corsair,
    Samsung,
    SkHynix,
    Adata,
    GSkill,
    WesternDigital,
    Seagate,
    Kioxia,
    Sandisk,
}

/// <summary>
/// Resolves a vendor from the real WMI inventory strings. The component kind is part of the input:
/// a GPU card only reports NVIDIA when the adapter itself is NVIDIA, never because some other
/// component in the machine is. Everything unrecognised falls back to <see cref="HardwareBrand.Unknown"/>.
/// </summary>
public static class BrandCatalog
{
    private static readonly IReadOnlyDictionary<HardwareBrand, string> Wordmarks =
        new Dictionary<HardwareBrand, string>
        {
            [HardwareBrand.Intel] = "intel",
            [HardwareBrand.Amd] = "AMD",
            [HardwareBrand.Nvidia] = "NVIDIA",
            [HardwareBrand.Msi] = "msi",
            [HardwareBrand.Asus] = "ASUS",
            [HardwareBrand.Gigabyte] = "GIGABYTE",
            [HardwareBrand.Asrock] = "ASRock",
            [HardwareBrand.Crucial] = "crucial",
            [HardwareBrand.Kingston] = "Kingston",
            [HardwareBrand.Corsair] = "CORSAIR",
            [HardwareBrand.Samsung] = "SAMSUNG",
            [HardwareBrand.SkHynix] = "SK hynix",
            [HardwareBrand.Adata] = "ADATA",
            [HardwareBrand.GSkill] = "G.SKILL",
            [HardwareBrand.WesternDigital] = "WD",
            [HardwareBrand.Seagate] = "Seagate",
            [HardwareBrand.Kioxia] = "KIOXIA",
            [HardwareBrand.Sandisk] = "SanDisk",
        };

    public static string? Wordmark(HardwareBrand brand)
        => Wordmarks.TryGetValue(brand, out var value) ? value : null;

    /// <summary>Human-readable brand name for titles, not the stylised wordmark.</summary>
    public static string? DisplayName(HardwareBrand brand)
        => brand switch
        {
            HardwareBrand.Intel => "Intel",
            HardwareBrand.Amd => "AMD",
            HardwareBrand.Nvidia => "NVIDIA",
            HardwareBrand.Msi => "MSI",
            HardwareBrand.Asus => "ASUS",
            HardwareBrand.Gigabyte => "GIGABYTE",
            HardwareBrand.Asrock => "ASRock",
            HardwareBrand.Crucial => "Crucial",
            HardwareBrand.Kingston => "Kingston",
            HardwareBrand.Corsair => "Corsair",
            HardwareBrand.Samsung => "Samsung",
            HardwareBrand.SkHynix => "SK hynix",
            HardwareBrand.Adata => "ADATA",
            HardwareBrand.GSkill => "G.SKILL",
            HardwareBrand.WesternDigital => "WD",
            HardwareBrand.Seagate => "Seagate",
            HardwareBrand.Kioxia => "KIOXIA",
            HardwareBrand.Sandisk => "SanDisk",
            _ => null,
        };

    public static HardwareBrand Resolve(HardwareComponentKind kind, string? vendor, string? product = null)
    {
        var text = ((vendor ?? string.Empty) + " " + (product ?? string.Empty)).Trim().ToLowerInvariant();
        if (text.Length == 0)
        {
            return HardwareBrand.Unknown;
        }

        return kind switch
        {
            HardwareComponentKind.Cpu => ResolveCpu(text),
            HardwareComponentKind.Gpu => ResolveGpu(text),
            HardwareComponentKind.Mainboard => ResolveBoard(text),
            HardwareComponentKind.Memory => ResolveMemory(text),
            HardwareComponentKind.Storage => ResolveStorage(text),
            _ => HardwareBrand.Unknown,
        };
    }

    private static HardwareBrand ResolveCpu(string text)
    {
        if (text.Contains("intel", StringComparison.Ordinal) ||
            text.Contains("genuineintel", StringComparison.Ordinal))
        {
            return HardwareBrand.Intel;
        }

        return text.Contains("amd", StringComparison.Ordinal) ||
               text.Contains("authenticamd", StringComparison.Ordinal)
            ? HardwareBrand.Amd
            : HardwareBrand.Unknown;
    }

    private static HardwareBrand ResolveGpu(string text)
    {
        if (text.Contains("nvidia", StringComparison.Ordinal))
        {
            return HardwareBrand.Nvidia;
        }

        if (text.Contains("amd", StringComparison.Ordinal) ||
            text.Contains("radeon", StringComparison.Ordinal) ||
            text.Contains("ati ", StringComparison.Ordinal))
        {
            return HardwareBrand.Amd;
        }

        return text.Contains("intel", StringComparison.Ordinal)
            ? HardwareBrand.Intel
            : HardwareBrand.Unknown;
    }

    private static HardwareBrand ResolveBoard(string text)
    {
        if (text.Contains("micro-star", StringComparison.Ordinal) ||
            text.Contains("msi", StringComparison.Ordinal))
        {
            return HardwareBrand.Msi;
        }

        if (text.Contains("asustek", StringComparison.Ordinal) ||
            text.Contains("asus", StringComparison.Ordinal))
        {
            return HardwareBrand.Asus;
        }

        if (text.Contains("gigabyte", StringComparison.Ordinal))
        {
            return HardwareBrand.Gigabyte;
        }

        return text.Contains("asrock", StringComparison.Ordinal)
            ? HardwareBrand.Asrock
            : HardwareBrand.Unknown;
    }

    private static HardwareBrand ResolveMemory(string text)
    {
        if (text.Contains("crucial", StringComparison.Ordinal) ||
            text.Contains("micron", StringComparison.Ordinal))
        {
            return HardwareBrand.Crucial;
        }

        if (text.Contains("kingston", StringComparison.Ordinal))
        {
            return HardwareBrand.Kingston;
        }

        if (text.Contains("corsair", StringComparison.Ordinal))
        {
            return HardwareBrand.Corsair;
        }

        if (text.Contains("samsung", StringComparison.Ordinal))
        {
            return HardwareBrand.Samsung;
        }

        if (text.Contains("hynix", StringComparison.Ordinal))
        {
            return HardwareBrand.SkHynix;
        }

        if (text.Contains("g.skill", StringComparison.Ordinal) ||
            text.Contains("gskill", StringComparison.Ordinal))
        {
            return HardwareBrand.GSkill;
        }

        return text.Contains("adata", StringComparison.Ordinal)
            ? HardwareBrand.Adata
            : HardwareBrand.Unknown;
    }

    private static HardwareBrand ResolveStorage(string text)
    {
        if (text.Contains("samsung", StringComparison.Ordinal))
        {
            return HardwareBrand.Samsung;
        }

        if (text.Contains("western digital", StringComparison.Ordinal) ||
            text.Contains("wd ", StringComparison.Ordinal) ||
            text.StartsWith("wd", StringComparison.Ordinal))
        {
            return HardwareBrand.WesternDigital;
        }

        if (text.Contains("crucial", StringComparison.Ordinal) ||
            text.Contains("micron", StringComparison.Ordinal))
        {
            return HardwareBrand.Crucial;
        }

        if (text.Contains("kingston", StringComparison.Ordinal))
        {
            return HardwareBrand.Kingston;
        }

        if (text.Contains("seagate", StringComparison.Ordinal))
        {
            return HardwareBrand.Seagate;
        }

        if (text.Contains("hynix", StringComparison.Ordinal))
        {
            return HardwareBrand.SkHynix;
        }

        if (text.Contains("kioxia", StringComparison.Ordinal) ||
            text.Contains("toshiba", StringComparison.Ordinal))
        {
            return HardwareBrand.Kioxia;
        }

        if (text.Contains("sandisk", StringComparison.Ordinal))
        {
            return HardwareBrand.Sandisk;
        }

        if (text.Contains("intel", StringComparison.Ordinal))
        {
            return HardwareBrand.Intel;
        }

        if (text.Contains("corsair", StringComparison.Ordinal))
        {
            return HardwareBrand.Corsair;
        }

        return text.Contains("adata", StringComparison.Ordinal)
            ? HardwareBrand.Adata
            : HardwareBrand.Unknown;
    }
}

/// <summary>Small lookups over the keyed <see cref="SystemInfoItem"/> groups.</summary>
public static class SystemInfoItems
{
    public static string? Value(this IReadOnlyList<SystemInfoItem> items, string key)
        => items.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal))?.Value;

    public static SystemInfoItem? Find(this IReadOnlyList<SystemInfoItem> items, string key)
        => items.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));

    /// <summary>
    /// Picks the card GPU: a discrete adapter wins over an integrated one so the System page never
    /// shows Intel UHD when an RTX/AMD card is present. Order is otherwise preserved.
    /// </summary>
    public static GpuInfo? PrimaryGpu(this IReadOnlyList<GpuInfo> gpus)
    {
        if (gpus.Count == 0)
        {
            return null;
        }

        foreach (var gpu in gpus)
        {
            if (!IsIntegrated(gpu))
            {
                return gpu;
            }
        }

        return gpus[0];
    }

    public static bool IsIntegrated(GpuInfo gpu)
    {
        var name = gpu.Name ?? string.Empty;
        var vendor = gpu.Vendor ?? string.Empty;
        return name.Contains("UHD Graphics", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Iris", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Integrated", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase) ||
               (vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("Arc", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("Battlemage", StringComparison.OrdinalIgnoreCase));
    }

    public static string FormatMegabytesAsGigabytes(double megabytes, int decimals)
    {
        var value = megabytes / 1024.0;
        var format = decimals <= 0 ? "0" : "0." + new string('0', decimals);
        return value.ToString(format, CultureInfo.InvariantCulture) + " GB";
    }
}
