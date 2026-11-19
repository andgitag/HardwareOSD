using System.Globalization;
using System.Text.RegularExpressions;

namespace HardwareOSD.Core.SystemInfo;

/// <summary>Pure formatters shared by the WMI collector and unit tests.</summary>
public static class SystemInfoFormat
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static string? FormatBytes(long? bytes)
    {
        if (bytes is null or <= 0)
        {
            return null;
        }

        const double kib = 1024.0;
        const double mib = kib * 1024.0;
        const double gib = mib * 1024.0;
        const double tib = gib * 1024.0;
        var value = (double)bytes.Value;
        if (value >= tib)
        {
            return (value / tib).ToString("0.##", Culture) + " TB";
        }

        if (value >= gib)
        {
            return (value / gib).ToString("0.#", Culture) + " GB";
        }

        return value >= mib
            ? (value / mib).ToString("0", Culture) + " MB"
            : (value / kib).ToString("0", Culture) + " KB";
    }

    public static string? CpuVendorName(string? manufacturer)
    {
        if (string.IsNullOrWhiteSpace(manufacturer))
        {
            return null;
        }

        var value = manufacturer.Trim();
        if (value.Contains("GenuineIntel", StringComparison.OrdinalIgnoreCase))
        {
            return "Intel";
        }

        if (value.Contains("AuthenticAMD", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("AMD", StringComparison.OrdinalIgnoreCase))
        {
            return "AMD";
        }

        return value;
    }

    /// <summary>
    /// Extracts the edition from a Win32_OperatingSystem Caption, e.g.
    /// "Microsoft Windows 11 Pro" -> "Pro". Returns null when the caption has no edition suffix.
    /// </summary>
    public static string? ParseEdition(string? caption)
    {
        if (string.IsNullOrWhiteSpace(caption))
        {
            return null;
        }

        var match = Regex.Match(caption.Trim(),
            @"^(?:Microsoft\s+)?Windows(?:\s+\S+)?\s+(?<edition>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["edition"].Value.Trim() : null;
    }

    /// <summary>SMBIOS memory type names for the values exposed by Win32_PhysicalMemory.</summary>
    public static string? MemoryTypeName(int? smbiosType)
    {
        return smbiosType switch
        {
            20 => "DDR",
            21 => "DDR2",
            22 => "DDR2 FB-DIMM",
            24 => "DDR3",
            26 => "DDR4",
            27 => "LPDDR",
            28 => "LPDDR2",
            29 => "LPDDR3",
            30 => "LPDDR4",
            32 => "HBM",
            33 => "HBM2",
            34 => "DDR5",
            35 => "LPDDR5",
            _ => null,
        };
    }

    public static string? StorageTypeName(int? mediaType, int? busType)
    {
        var media = mediaType switch
        {
            3 => "HDD",
            4 => "SSD",
            5 => "SCM",
            _ => null,
        };
        var bus = busType switch
        {
            7 => "USB",
            8 => "RAID",
            10 => "SAS",
            11 => "SATA",
            14 => "Virtual",
            17 => "NVMe",
            18 => "SCM",
            19 => "UFS",
            _ => null,
        };

        if (media is null && bus is null)
        {
            return null;
        }

        return string.Join(" ", new[] { bus, media }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    public static string? FormatDate(DateTime? value)
        => value is null ? null : value.Value.ToString("yyyy-MM-dd", Culture);

    public static string? FormatMegahertz(long? value)
        => value is > 0 ? value.Value.ToString("0", Culture) + " MHz" : null;

    /// <summary>
    /// Marketing name without the (R)/(TM) marks and the trailing "CPU @ 3.20GHz" suffix that WMI
    /// appends. Only formatting: the model string itself is never rewritten.
    /// </summary>
    public static string? CleanProcessorName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var text = Regex.Replace(name, @"\s*\((?:R|TM|C)\)", string.Empty, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\s+CPU\s*@\s*[\d.]+\s*GHz", string.Empty, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\s+@\s*[\d.]+\s*GHz", string.Empty, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\s{2,}", " ").Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// "12th Gen" from the marketing name. AMD model names carry no generation token, so the
    /// result is null instead of a guessed Zen generation.
    /// </summary>
    public static string? CpuGeneration(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var match = Regex.Match(name, @"(?<gen>\d{1,2}(?:st|nd|rd|th))\s+Gen", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["gen"].Value + " Gen" : null;
    }

    /// <summary>Speed is reported by SMBIOS in MT/s; Windows exposes the same number as "MHz".</summary>
    public static string? FormatMemorySpeed(long? value)
        => value is > 0 ? value.Value.ToString("0", Culture) + " MT/s" : null;

    /// <summary>
    /// "2 × 32 GB" for identical modules or "1 × 16 GB + 1 × 32 GB" for a mixed kit. Uses only
    /// capacities Windows reported; unknown modules are omitted rather than assumed.
    /// </summary>
    public static string? FormatModuleConfiguration(IEnumerable<ulong?> capacities)
    {
        var groups = capacities
            .Where(capacity => capacity is > 0)
            .Select(capacity => capacity!.Value)
            .GroupBy(capacity => capacity)
            .OrderBy(group => group.Key)
            .Select(group => $"{group.Count()} × {FormatBytes((long)group.Key)}")
            .ToArray();
        return groups.Length == 0 ? null : string.Join(" + ", groups);
    }
}
