using HardwareOSD.Core.Config;

namespace HardwareOSD.Core.Overlay;

/// <summary>
/// Slowly changing machine facts that the overlay may need in addition to sensor telemetry.
/// It is built once per render from cached SystemInfo, never from live hardware calls.
/// </summary>
public sealed record OsdRenderContext(
    string? GpuName = null,
    string? CpuName = null,
    string? GpuVendor = null,
    string? CpuVendor = null,
    string? GpuDriver = null,
    long SessionStartMs = 0,
    long NowMs = 0)
{
    public static readonly OsdRenderContext Empty = new();

    /// <summary>CapFrameX's vendor colours, applied only to the three row templates.</summary>
    public string? LabelColor(OsdVendorRole role) => role switch
    {
        OsdVendorRole.GpuGroup => VendorColor(GpuVendor, "#76B900", "#ED1C24", "#9865EB"),
        OsdVendorRole.CpuGroup => VendorColor(CpuVendor, "#0071C5", "#ED1C24", "#0071C5"),
        OsdVendorRole.RamGroup => "#87CEEB",
        OsdVendorRole.LatencyGroup => "#0271F9",
        OsdVendorRole.MetricGroup => "#9CD200",
        OsdVendorRole.FrameGroup => "#AEEA00",
        OsdVendorRole.White => "#FFFFFF",
        _ => null,
    };

    public string Expand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.IndexOf('{') < 0)
        {
            return text ?? string.Empty;
        }

        var value = text
            .Replace("{gpuName}", GpuName ?? "N/A", StringComparison.OrdinalIgnoreCase)
            .Replace("{gpuShort}", Shorten(GpuName), StringComparison.OrdinalIgnoreCase)
            .Replace("{cpuName}", CpuName ?? "N/A", StringComparison.OrdinalIgnoreCase)
            .Replace("{cpuShort}", Shorten(CpuName), StringComparison.OrdinalIgnoreCase)
            .Replace("{gpuDriver}", GpuDriver ?? "N/A", StringComparison.OrdinalIgnoreCase);
        if (value.Contains("{session}", StringComparison.OrdinalIgnoreCase))
        {
            value = value.Replace("{session}", SessionText(), StringComparison.OrdinalIgnoreCase);
        }

        return value;
    }

    private string SessionText()
    {
        if (SessionStartMs <= 0 || NowMs <= SessionStartMs)
        {
            return "00:00:00";
        }

        var elapsed = TimeSpan.FromMilliseconds(NowMs - SessionStartMs);
        return $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    private static string VendorColor(string? vendor, string nvidia, string amd, string intel)
    {
        var text = vendor ?? string.Empty;
        if (text.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Advanced Micro", StringComparison.OrdinalIgnoreCase))
        {
            return amd;
        }

        if (text.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Arc", StringComparison.OrdinalIgnoreCase))
        {
            return intel;
        }

        if (text.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("GeForce", StringComparison.OrdinalIgnoreCase))
        {
            return nvidia;
        }

        return nvidia;
    }

    private static string Shorten(string? value)
        => string.IsNullOrWhiteSpace(value) ? "N/A" : value.Trim();
}
