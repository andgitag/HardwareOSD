using System.Globalization;
using HardwareOSD.Core.Metrics;

namespace HardwareOSD.Core.TileOsd;

public enum TileSourceKind
{
    Numeric = 0,
    Text = 1,
}

/// <summary>
/// One binding the builder offers to the user. <see cref="Key"/> is the exact string stored in
/// <c>TileNode.key</c> and resolved by <see cref="TileSensorMap"/>, so every entry maps to a real
/// HardwareOSD sensor, FPS reading or machine fact. Nothing here invents a value.
/// </summary>
public sealed record TileSource(
    string Key,
    string Label,
    string Unit,
    string Category,
    string Device,
    TileSourceKind Kind = TileSourceKind.Numeric);

/// <summary>A set of readings that becomes one tile (a heatmap of cores, for example).</summary>
public sealed record TileSourceGroup(
    string Id,
    string Label,
    string Unit,
    string Category,
    string Device,
    IReadOnlyList<string> Keys);

/// <summary>Read-only catalogue behind the Sources tab and the tile source picker.</summary>
public static class TileSourceCatalog
{
    private static readonly Lazy<IReadOnlyList<TileSource>> LazySources = new(CreateSources);
    private static readonly Lazy<IReadOnlyList<TileSourceGroup>> LazyGroups = new(CreateGroups);

    public static IReadOnlyList<TileSource> Sources => LazySources.Value;

    public static IReadOnlyList<TileSourceGroup> Groups => LazyGroups.Value;

    public static IReadOnlyList<string> Categories { get; } = new[]
    {
        "Frames", "GPU", "CPU", "Memory", "System",
    };

    public static IReadOnlyList<string> Devices { get; } = new[]
    {
        "Frame data", "Graphics card", "Processor", "System memory", "Windows",
    };

    public static TileSource? Find(string? key)
        => key is null
            ? null
            : Sources.FirstOrDefault(source => string.Equals(source.Key, key, StringComparison.Ordinal));

    public static string Label(string? key)
        => Find(key)?.Label ?? (string.IsNullOrWhiteSpace(key) ? "No source" : key);

    private static IReadOnlyList<TileSource> CreateSources()
    {
        var sources = new List<TileSource>
        {
            new("fpsCurrent", "Current FPS", "FPS", "Frames", "Frame data"),
            new("fps", "Average FPS", "FPS", "Frames", "Frame data"),
            new("fps1pctLow", "1% low FPS", "FPS", "Frames", "Frame data"),
            new("frametimeCurrent", "Current frame time", "ms", "Frames", "Frame data"),
            new("frametime", "Average frame time", "ms", "Frames", "Frame data"),
            new("stuttering", "Stutter index", "%", "Frames", "Frame data"),

            new("gpuLoad", "GPU load", "%", "GPU", "Graphics card"),
            new("gpuTemp", "GPU temperature", "°C", "GPU", "Graphics card"),
            new("gpuHotSpot", "GPU hotspot", "°C", "GPU", "Graphics card"),
            new("gpuPower", "GPU power", "W", "GPU", "Graphics card"),
            new("gpuClock", "GPU clock", "MHz", "GPU", "Graphics card"),
            new("gpuMemoryClock", "GPU memory clock", "MHz", "GPU", "Graphics card"),
            new("gpuFan", "GPU fan speed (RPM)", "RPM", "GPU", "Graphics card"),
            new("gpuFanPercent", "GPU fan speed (%)", "%", "GPU", "Graphics card"),
            new("vramUsed", "VRAM used", "MB", "GPU", "Graphics card"),
            new("vramTotal", "VRAM total", "MB", "GPU", "Graphics card"),
            new("vramFree", "VRAM free", "MB", "GPU", "Graphics card"),
            new("vramLoad", "VRAM load", "%", "GPU", "Graphics card"),
            new("vramTemp", "VRAM temperature", "°C", "GPU", "Graphics card"),

            new("cpuLoad", "CPU load", "%", "CPU", "Processor"),
            new("cpuTemp", "CPU temperature", "°C", "CPU", "Processor"),
            new("cpuPower", "CPU package power", "W", "CPU", "Processor"),
            new("cpuClock", "CPU clock", "MHz", "CPU", "Processor"),
            new("cpuFan", "CPU fan speed (RPM)", "RPM", "CPU", "Processor"),
            new("cpuFanPercent", "CPU fan speed (%)", "%", "CPU", "Processor"),

            new("ramUsed", "RAM used", "MB", "Memory", "System memory"),
            new("ramTotal", "RAM total", "MB", "Memory", "System memory"),
            new("ramFree", "RAM free", "MB", "Memory", "System memory"),
            new("ramLoad", "RAM load", "%", "Memory", "System memory"),

            new("gpuName", "GPU name", "", "System", "Graphics card", TileSourceKind.Text),
            new("gpuNameShort", "GPU name (short)", "", "System", "Graphics card", TileSourceKind.Text),
            new("cpuName", "CPU name", "", "System", "Processor", TileSourceKind.Text),
            new("cpuNameShort", "CPU name (short)", "", "System", "Processor", TileSourceKind.Text),
            new("gpuDriver", "GPU driver version", "", "System", "Graphics card", TileSourceKind.Text),
            new("graphicsApi", "Graphics API", "", "System", "Windows", TileSourceKind.Text),
            new("captureTimer", "Capture timer", "", "System", "Windows", TileSourceKind.Text),
        };

        // Metrics added through the Metrics table are exposed here as well. Semantic keys already
        // present above are skipped so the picker does not show the same sensor twice.
        var represented = new HashSet<string>(StringComparer.Ordinal)
        {
            "frame.fps", "frame.time", "frame.fps.1low",
            "gpu.usage", "gpu.temp", "gpu.temp.hotspot", "gpu.power", "gpu.clock",
            "gpu.clock.memory", "gpu.fan.rpm", "gpu.fan.percent", "vram.used",
            "gpu.temp.memory", "cpu.usage", "cpu.temp", "cpu.power", "cpu.clock",
            "ram.used", "ram.percent",
        };
        foreach (var definition in MetricCatalog.All)
        {
            if (!represented.Add(definition.Id))
            {
                continue;
            }

            var key = MetricTileKeys.For(definition);
            if (sources.Any(source => string.Equals(source.Key, key, StringComparison.Ordinal)))
            {
                continue;
            }

            sources.Add(new TileSource(key, definition.DisplayName, definition.Unit,
                CategoryFor(definition.Group), DeviceFor(definition.Group),
                TileSourceKind.Numeric));
        }

        // Per-core readings exist for every logical processor the machine reports. The renderer
        // maps the CapFrameX role key onto HardwareOSD's CoreUsage/CoreClock/CoreTemperature ids.
        var cores = Math.Clamp(Environment.ProcessorCount, 1, 32);
        for (var core = 0; core < cores; core++)
        {
            var number = (core + 1).ToString(CultureInfo.InvariantCulture);
            sources.Add(new TileSource("role:cpu-loads/" + core, "Core #" + number + " load", "%",
                "CPU cores", "Processor"));
            sources.Add(new TileSource("role:cpu-temperatures/" + core, "Core #" + number + " temperature",
                "°C", "CPU cores", "Processor"));
            sources.Add(new TileSource("role:cpu-clocks/" + core, "Core #" + number + " clock", "MHz",
                "CPU cores", "Processor"));
        }

        return sources;
    }

    private static string CategoryFor(MetricGroup group) => group switch
    {
        MetricGroup.Cpu => "CPU",
        MetricGroup.Gpu or MetricGroup.Vram => "GPU",
        MetricGroup.Ram => "Memory",
        MetricGroup.Frame => "Frames",
        _ => "System",
    };

    private static string DeviceFor(MetricGroup group) => group switch
    {
        MetricGroup.Cpu => "Processor",
        MetricGroup.Gpu or MetricGroup.Vram => "Graphics card",
        MetricGroup.Ram => "System memory",
        MetricGroup.Frame => "Frame data",
        _ => "Windows",
    };

    private static IReadOnlyList<TileSourceGroup> CreateGroups()
    {
        var cores = Math.Clamp(Environment.ProcessorCount, 1, 32);
        var loads = Enumerable.Range(0, cores)
            .Select(core => "role:cpu-loads/" + core.ToString(CultureInfo.InvariantCulture))
            .ToList();
        var temperatures = Enumerable.Range(0, cores)
            .Select(core => "role:cpu-temperatures/" + core.ToString(CultureInfo.InvariantCulture))
            .ToList();
        var clocks = Enumerable.Range(0, cores)
            .Select(core => "role:cpu-clocks/" + core.ToString(CultureInfo.InvariantCulture))
            .ToList();
        return new List<TileSourceGroup>
        {
            new("cores-load", "CPU cores — load", "%", "CPU cores", "Processor", loads),
            new("cores-temp", "CPU cores — temperature", "°C", "CPU cores", "Processor", temperatures),
            new("cores-clock", "CPU cores — clock", "MHz", "CPU cores", "Processor", clocks),
        };
    }
}
