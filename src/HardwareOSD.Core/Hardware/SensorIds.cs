namespace HardwareOSD.Core.Hardware;

/// <summary>
/// Stable sensor identifiers shared by every provider, the cache and the renderer.
/// Identifiers never contain user visible text so layouts survive localisation changes.
/// </summary>
public static class SensorIds
{
    public const string CpuTemperature = "cpu.temp";
    public const string CpuPackageTemperature = "cpu.temp.package";
    public const string CpuUsage = "cpu.usage";
    public const string CpuUsageMax = "cpu.usage.max";
    public const string CpuClock = "cpu.clock";
    public const string CpuPower = "cpu.power";
    public const string CpuPowerCores = "cpu.power.cores";
    public const string CpuPowerMemory = "cpu.power.memory";
    public const string CpuPowerGt = "cpu.power.gt";
    public const string CpuVoltage = "cpu.voltage";

    public const string GpuTemperature = "gpu.temp";
    public const string GpuHotspot = "gpu.temp.hotspot";
    public const string GpuMemoryJunction = "gpu.temp.memory";
    public const string GpuUsage = "gpu.usage";
    public const string GpuClock = "gpu.clock";
    public const string GpuMemoryClock = "gpu.clock.memory";
    public const string GpuPower = "gpu.power";
    public const string GpuPowerLimit = "gpu.power.limit";
    public const string GpuMemoryControllerUsage = "gpu.memcontroller";

    public const string VramUsed = "vram.used";
    public const string VramTotal = "vram.total";
    public const string VramPercent = "vram.percent";

    public const string RamUsed = "ram.used";
    public const string RamAvailable = "ram.available";
    public const string RamTotal = "ram.total";
    public const string RamPercent = "ram.percent";

    /// <summary>Frame presentation metrics are intentionally unsupported (see docs/FPS.md).</summary>
    public const string Fps = "frame.fps";
    public const string FrameTime = "frame.time";

    public const string CoreTemperaturePrefix = "cpu.core.";
    public const string CoreSuffixTemperature = ".temp";
    public const string CoreSuffixUsage = ".usage";
    public const string CoreSuffixClock = ".clock";

    public static string CoreTemperature(int index) => $"{CoreTemperaturePrefix}{index}{CoreSuffixTemperature}";
    public static string CoreUsage(int index) => $"{CoreTemperaturePrefix}{index}{CoreSuffixUsage}";
    public static string CoreClock(int index) => $"{CoreTemperaturePrefix}{index}{CoreSuffixClock}";

    public static bool TryParseCoreIndex(string id, string suffix, out int index)
    {
        index = -1;
        if (!id.StartsWith(CoreTemperaturePrefix, StringComparison.Ordinal) ||
            !id.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var middle = id.AsSpan(CoreTemperaturePrefix.Length, id.Length - CoreTemperaturePrefix.Length - suffix.Length);
        return int.TryParse(middle, out index) && index >= 0;
    }

    public static IEnumerable<int> CoreIndexes(SensorSnapshot snapshot, string suffix)
    {
        // Core counts are small (<= 256), so a bounded scan is both cheap and allocation free.
        for (var i = 0; i < 256; i++)
        {
            if (snapshot.Contains($"{CoreTemperaturePrefix}{i}{suffix}"))
            {
                yield return i;
            }
        }
    }
}
