using HardwareOSD.Core.Hardware;

namespace HardwareOSD.Core.Metrics;

public enum MetricGroup
{
    Cpu = 0,
    Gpu = 1,
    Vram = 2,
    Ram = 3,
    System = 4,
    Frame = 5,
}

public enum MetricSeverity
{
    Normal = 0,
    Warning = 1,
    Critical = 2,
}

/// <summary>
/// Declarative description of one overlay metric. Formatting lives in <see cref="MetricFormatter"/>;
/// definitions carry layout hints (sample strings reserve a stable width so the overlay never jitters).
/// </summary>
public sealed record MetricDefinition(
    string Id,
    MetricGroup Group,
    string DisplayName,
    string Unit,
    string SampleValue,
    string SampleUnit,
    bool DefaultVisible,
    double? WarningThreshold = null,
    double? CriticalThreshold = null,
    bool Sparkline = false,
    string? Help = null)
{
    public string GroupLabel => Group switch
    {
        MetricGroup.Cpu => "CPU",
        MetricGroup.Gpu => "GPU",
        MetricGroup.Vram => "VRAM",
        MetricGroup.Ram => "RAM",
        MetricGroup.Frame => "FPS",
        _ => "SYS",
    };
}

/// <summary>Ordered catalogue of every metric the overlay can display.</summary>
public static class MetricCatalog
{
    private static readonly Dictionary<string, MetricDefinition> ById;
    private static readonly Dictionary<string, MetricDefinition> BySensorId;

    public static IReadOnlyList<MetricDefinition> All { get; } = new List<MetricDefinition>
    {
        new("cpu.temp", MetricGroup.Cpu, "CPU temperature", "°C", "888", "°C", true, 85, 95, true,
            "Package temperature when available, otherwise the average of the core sensors."),
        new("cpu.usage", MetricGroup.Cpu, "CPU utilization", "%", "888", "%", true, null, null, true),
        new("cpu.clock", MetricGroup.Cpu, "CPU clock", "MHz", "8.88", "GHz", true),
        new("cpu.power", MetricGroup.Cpu, "CPU package power", "W", "888", "W", true),
        new("cpu.power.cores", MetricGroup.Cpu, "CPU cores power (IA)", "W", "888", "W", false),
        new("cpu.power.memory", MetricGroup.Cpu, "CPU memory power", "W", "888", "W", false),
        new("cpu.power.gt", MetricGroup.Cpu, "CPU graphics power (GT)", "W", "888", "W", false),
        new("cpu.voltage", MetricGroup.Cpu, "CPU core voltage", "V", "8.888", "V", false),
        new("cpu.fan.rpm", MetricGroup.Cpu, "CPU fan speed (RPM)", "RPM", "8888/8888", "RPM", false),
        new("cpu.fan.percent", MetricGroup.Cpu, "CPU fan speed (%)", "%", "888/888", "%", false),
        new("cpu.temp.package", MetricGroup.Cpu, "CPU package temperature", "°C", "888", "°C", false, 85, 95),
        new("cpu.usage.max", MetricGroup.Cpu, "Busiest core utilization", "%", "888", "%", false),

        new("thermalzone.temp", MetricGroup.System, "ACPI thermal zone temperature", "°C", "888", "°C", false, 80, 90),

        new("gpu.temp", MetricGroup.Gpu, "GPU temperature", "°C", "888", "°C", true, 83, 92, true),
        new("gpu.usage", MetricGroup.Gpu, "GPU utilization", "%", "888", "%", true, null, null, true),
        new("gpu.power", MetricGroup.Gpu, "GPU power", "W", "888", "W", true, null, null, true),
        new("gpu.clock", MetricGroup.Gpu, "GPU core clock", "MHz", "8888", "MHz", false),
        new("gpu.temp.hotspot", MetricGroup.Gpu, "GPU hotspot temperature", "°C", "888", "°C", false, 97, 105, true),
        new("gpu.temp.memory", MetricGroup.Gpu, "GPU memory junction", "°C", "888", "°C", false, 95, 105),
        new("gpu.clock.memory", MetricGroup.Gpu, "GPU memory clock", "MHz", "88888", "MHz", false),
        new("gpu.memcontroller", MetricGroup.Gpu, "GPU memory controller load", "%", "888", "%", false),
        new("gpu.fan.rpm", MetricGroup.Gpu, "GPU fan speed (RPM)", "RPM", "8888/8888", "RPM", false),
        new("gpu.fan.percent", MetricGroup.Gpu, "GPU fan speed (%)", "%", "888/888", "%", false),
        new("gpu.power.limit", MetricGroup.Gpu, "GPU power limit", "W", "888", "W", false),

        new("vram.used", MetricGroup.Vram, "VRAM usage", "MB", "88888 / 88888", "MB", true, 90, 97),
        new("vram.percent", MetricGroup.Vram, "VRAM utilization", "%", "888", "%", false, 90, 97, true),

        new("ram.used", MetricGroup.Ram, "RAM usage", "MB", "888.8 / 888.8", "GB", true),
        new("ram.percent", MetricGroup.Ram, "RAM utilization", "%", "888", "%", false, 90, 97, true),
        new("ram.available", MetricGroup.Ram, "Available RAM", "MB", "888.8", "GB", false),

        new("frame.fps", MetricGroup.Frame, "FPS (foreground app)", "FPS", "8888", "FPS", true, null, null, true,
            "Frame rate measured from ETW present events (presented timing). Displayed FPS requires DWM correlation and is not available with the current backend."),
        new("frame.time", MetricGroup.Frame, "Frame time", "ms", "88.8", "ms", false),
        new("frame.fps.1low", MetricGroup.Frame, "1% low FPS", "FPS", "888", "FPS·1%", false),
    };

    static MetricCatalog()
    {
        ById = All.ToDictionary(d => d.Id, StringComparer.Ordinal);
        BySensorId = new Dictionary<string, MetricDefinition>(StringComparer.Ordinal);
        foreach (var definition in All)
        {
            var sensorId = SensorIdFor(definition);
            if (!string.IsNullOrWhiteSpace(sensorId) && !BySensorId.ContainsKey(sensorId))
            {
                BySensorId[sensorId] = definition;
            }
        }
    }

    public static MetricDefinition? Find(string id)
        => id is not null && ById.TryGetValue(id, out var definition) ? definition : null;

    /// <summary>Finds the first metric definition backed by a raw sensor id.</summary>
    public static MetricDefinition? FindBySensorId(string? sensorId)
        => sensorId is not null && BySensorId.TryGetValue(sensorId, out var definition) ? definition : null;

    /// <summary>
    /// Resolves the CapFrameX/editor source keys used by tile documents (for example <c>gpuTemp</c>)
    /// to the matching catalogue metric. This keeps group insertion, chart history and the Metrics
    /// page on the same definition instead of treating an alias as an unknown source.
    /// </summary>
    public static MetricDefinition? FindByTileKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var raw = key.StartsWith("metric:", StringComparison.OrdinalIgnoreCase) ? key[7..] : key;
        if (Find(raw) is { } direct)
        {
            return direct;
        }

        if (FindBySensorId(raw) is { } bySensor)
        {
            return bySensor;
        }

        return TileKeyAliases.TryGetValue(raw, out var metricId) ? Find(metricId) : null;
    }

    private static readonly Dictionary<string, string> TileKeyAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["cpuTemp"] = "cpu.temp",
            ["cpuLoad"] = "cpu.usage",
            ["cpuClock"] = "cpu.clock",
            ["cpuPower"] = "cpu.power",
            ["cpuFan"] = "cpu.fan.rpm",
            ["cpuFanPercent"] = "cpu.fan.percent",
            ["gpuTemp"] = "gpu.temp",
            ["gpuLoad"] = "gpu.usage",
            ["gpuClock"] = "gpu.clock",
            ["gpuPower"] = "gpu.power",
            ["gpuHotSpot"] = "gpu.temp.hotspot",
            ["gpuMemoryClock"] = "gpu.clock.memory",
            ["gpuFan"] = "gpu.fan.rpm",
            ["gpuFanPercent"] = "gpu.fan.percent",
            ["vramUsed"] = "vram.used",
            ["vramLoad"] = "vram.percent",
            ["ramUsed"] = "ram.used",
            ["ramLoad"] = "ram.percent",
            ["ramAvailable"] = "ram.available",
            ["fpsCurrent"] = "frame.fps",
            ["fps"] = "frame.fps",
            ["fps1pctLow"] = "frame.fps.1low",
            ["frametimeCurrent"] = "frame.time",
            ["frametime"] = "frame.time",
        };

    /// <summary>
    /// Raw sensor that feeds a metric. Centralised so diagnostics, source labels and formatting
    /// always agree.
    /// </summary>
    public static string? SensorIdFor(MetricDefinition definition) => definition.Id switch
    {
        "cpu.temp" => SensorIds.CpuTemperature,
        "cpu.usage" => SensorIds.CpuUsage,
        "cpu.clock" => SensorIds.CpuClock,
        "cpu.power" => SensorIds.CpuPower,
        "cpu.power.cores" => SensorIds.CpuPowerCores,
        "cpu.power.memory" => SensorIds.CpuPowerMemory,
        "cpu.power.gt" => SensorIds.CpuPowerGt,
        "cpu.voltage" => SensorIds.CpuVoltage,
        "cpu.fan.rpm" or "cpu.fan.percent" or "gpu.fan.rpm" or "gpu.fan.percent" => null,
        "cpu.temp.package" => SensorIds.CpuPackageTemperature,
        "cpu.usage.max" => SensorIds.CpuUsageMax,
        "gpu.temp" => SensorIds.GpuTemperature,
        "gpu.usage" => SensorIds.GpuUsage,
        "gpu.power" => SensorIds.GpuPower,
        "gpu.clock" => SensorIds.GpuClock,
        "gpu.temp.hotspot" => SensorIds.GpuHotspot,
        "gpu.temp.memory" => SensorIds.GpuMemoryJunction,
        "gpu.clock.memory" => SensorIds.GpuMemoryClock,
        "gpu.memcontroller" => SensorIds.GpuMemoryControllerUsage,
        "gpu.power.limit" => SensorIds.GpuPowerLimit,
        "vram.used" => SensorIds.VramUsed,
        "vram.percent" => SensorIds.VramPercent,
        "ram.used" => SensorIds.RamUsed,
        "ram.percent" => SensorIds.RamPercent,
        "ram.available" => SensorIds.RamAvailable,
        "thermalzone.temp" => "thermalzone.temp",
        "frame.fps" or "frame.time" or "frame.fps.1low" => null,
        _ => null,
    };

    public static IEnumerable<string> SparklineIds => All.Where(d => d.Sparkline).Select(d => d.Id);

    public static bool IsFanMetric(string id)
        => id is "cpu.fan.rpm" or "cpu.fan.percent" or "gpu.fan.rpm" or "gpu.fan.percent";

    public static FanComponent? FanComponentForMetric(string id) => id switch
    {
        "cpu.fan.rpm" or "cpu.fan.percent" => FanComponent.Cpu,
        "gpu.fan.rpm" or "gpu.fan.percent" => FanComponent.Gpu,
        _ => null,
    };

    public static string GroupTitle(MetricGroup group) => group switch
    {
        MetricGroup.Cpu => "CPU",
        MetricGroup.Gpu => "GPU",
        MetricGroup.Vram => "VRAM",
        MetricGroup.Ram => "RAM",
        MetricGroup.Frame => "FPS",
        _ => "SYS",
    };
}
