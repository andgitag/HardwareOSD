using HardwareOSD.Core.Config;

namespace HardwareOSD.Core.TileOsd;

/// <summary>
/// The three classic CapFrameX row templates, recreated on HardwareOSD's existing element model.
/// Composition and colours follow OSD-Research.md §3.1/§3.3. Entries whose HardwareOSD source does
/// not exist (display time, PC latency, DIMM temperature, per-core-type topology) are kept as
/// explicit N/A rows or omitted only where the reference omits them too.
/// </summary>
public static class CapFrameXRowTemplates
{
    public const string BasicId = "cfx-row-basic";
    public const string DetailedId = "cfx-row-detailed";
    public const string EnthusiastId = "cfx-row-enthusiast";

    private const string Gold = "#FFD700";
    private const string Lime = "#9CD200";
    private const string YellowGreen = "#AEEA00";
    private const string White = "#FFFFFF";
    private const string RamBlue = "#87CEEB";
    private const string LatencyBlue = "#0271F9";

    public static List<OsdProfile> CreateProfiles() => new()
    {
        Create("Basic", BasicId, BuildBasic()),
        Create("Detailed", DetailedId, BuildDetailed()),
        Create("Enthusiast", EnthusiastId, BuildEnthusiast()),
    };

    private static OsdProfile Create(string name, string id, List<OsdElement> elements) => new()
    {
        Id = id,
        Name = name,
        Library = OsdDesignLibrary.CapFrameX,
        Layout = OsdProfileLayout.Rows,
        Style = OsdStyle.HighContrast,
        FollowMetricPreferences = false,
        Elements = elements,
        StyleSettings = RowStyle(),
        Tile = new TileOverrideSettings(),
    };

    private static OsdStyleSettings RowStyle() => new()
    {
        FontFamily = "Segoe UI",
        FontSize = 14,
        LabelScale = 0.94,
        UnitScale = 0.92,
        LabelColor = "#76B900",
        ValueColor = Gold,
        UnitColor = Gold,
        NormalColor = Gold,
        WarningColor = Gold,
        CriticalColor = Gold,
        HeaderColor = "#76B900",
        TextColor = White,
        SeparatorColor = "#2A2E33",
        BackgroundColor = "#0E1116",
        BackgroundOpacity = 97,
        ShowBackground = true,
        ShowLabels = true,
        ShowUnits = true,
        ShowSeparators = false,
        ShowSparklines = false,
        HighlightWarnings = false,
        Shadow = false,
        UseUpperLabels = false,
        CornerRadius = 0,
        PaddingX = 8,
        PaddingY = 6,
        RowSpacing = 1,
        ColumnSpacing = 12,
    };

    private static List<OsdElement> BuildBasic() => new()
    {
        Group("GPU", OsdVendorRole.GpuGroup, OsdGroupLayout.Columns,
            Sensor("gpu.clock", "GPU clock", "MHz", OsdVendorRole.GpuGroup),
            Sensor("gpu.clock.memory", "GPU memory", "MHz", OsdVendorRole.GpuGroup),
            Sensor("gpu.usage", "GPU load", "%", OsdVendorRole.GpuGroup),
            Sensor("gpu.temp", "GPU temp", "°C", OsdVendorRole.GpuGroup)),
        Gap(),
        Group("CPU", OsdVendorRole.CpuGroup, OsdGroupLayout.Columns,
            Sensor("cpu.clock", "CPU max", "MHz", OsdVendorRole.CpuGroup),
            Sensor("cpu.usage", "CPU total", "%", OsdVendorRole.CpuGroup),
            Sensor("cpu.power", "CPU package", "W", OsdVendorRole.CpuGroup),
            Sensor("cpu.temp", "CPU temp", "°C", OsdVendorRole.CpuGroup)),
        Gap(),
        Group("Metrics", OsdVendorRole.MetricGroup, OsdGroupLayout.Columns,
            Sensor("frame.fps.average", "Average", "FPS", OsdVendorRole.MetricGroup, Lime),
            Sensor("frame.fps.1low", "1% low", "FPS", OsdVendorRole.MetricGroup, Lime)),
        Gap(),
        Group("Framerate", OsdVendorRole.FrameGroup, OsdGroupLayout.Columns,
            Sensor("frame.fps", "Framerate", "FPS", OsdVendorRole.FrameGroup, YellowGreen,
                graph: true, graphColor: YellowGreen),
            Sensor("frame.time", "Frametime", "ms", OsdVendorRole.FrameGroup, YellowGreen,
                graph: true, graphColor: "#35E0D0")),
    };

    private static List<OsdElement> BuildDetailed()
    {
        var elements = new List<OsdElement>
        {
            Group("GPU", OsdVendorRole.GpuGroup, OsdGroupLayout.Columns,
                Sensor("gpu.clock", "GPU clock", "MHz", OsdVendorRole.GpuGroup),
                Sensor("gpu.clock.memory", "GPU memory", "MHz", OsdVendorRole.GpuGroup),
                Sensor("gpu.usage", "GPU load", "%", OsdVendorRole.GpuGroup),
                Sensor("gpu.temp", "GPU temp", "°C", OsdVendorRole.GpuGroup),
                Sensor("gpu.power", "GPU power", "W", OsdVendorRole.GpuGroup),
                Sensor("vram.used", "VRAM", "MB", OsdVendorRole.GpuGroup)),
            Gap(),
            Text("{cpuName}", White, 14),
            Group("CPU", OsdVendorRole.CpuGroup, OsdGroupLayout.Columns,
                Sensor("cpu.clock", "CPU max", "MHz", OsdVendorRole.CpuGroup),
                Sensor("cpu.usage", "CPU total", "%", OsdVendorRole.CpuGroup),
                Sensor("cpu.power", "CPU package", "W", OsdVendorRole.CpuGroup),
                Sensor("cpu.temp", "CPU temp", "°C", OsdVendorRole.CpuGroup)),
            Gap(),
            Group("RAM", OsdVendorRole.RamGroup, OsdGroupLayout.Columns,
                Sensor("ram.used", "RAM used", "GB", OsdVendorRole.RamGroup, White),
                Sensor("ram.available", "Available", "GB", OsdVendorRole.RamGroup, White),
                Sensor("ram.percent", "RAM usage", "%", OsdVendorRole.RamGroup, White)),
            Gap(),
        };
        elements.AddRange(MetricsAndFrame());
        return elements;
    }

    private static List<OsdElement> BuildEnthusiast()
    {
        var elements = new List<OsdElement>
        {
            Group("GPU", OsdVendorRole.GpuGroup, OsdGroupLayout.Columns,
                Sensor("gpu.clock", "GPU clock", "MHz", OsdVendorRole.GpuGroup),
                Sensor("gpu.clock.memory", "GPU memory", "MHz", OsdVendorRole.GpuGroup),
                Sensor("gpu.usage", "GPU load", "%", OsdVendorRole.GpuGroup),
                Sensor("gpu.temp", "GPU temp", "°C", OsdVendorRole.GpuGroup),
                Sensor("gpu.power", "GPU power", "W", OsdVendorRole.GpuGroup),
                Sensor("vram.used", "VRAM", "MB", OsdVendorRole.GpuGroup),
                Sensor("gpu.temp.hotspot", "Hot spot", "°C", OsdVendorRole.GpuGroup),
                Sensor("gpu.temp.memory", "VRAM temp", "°C", OsdVendorRole.GpuGroup)),
            Gap(),
            Text("{cpuName}", White, 14),
            Group("CPU", OsdVendorRole.CpuGroup, OsdGroupLayout.Columns,
                Sensor("cpu.clock", "CPU max", "MHz", OsdVendorRole.CpuGroup),
                Sensor("cpu.usage", "CPU total", "%", OsdVendorRole.CpuGroup),
                Sensor("cpu.power", "CPU package", "W", OsdVendorRole.CpuGroup),
                Sensor("cpu.temp", "CPU temp", "°C", OsdVendorRole.CpuGroup)),
            Gap(),
        };

        for (var index = 0; index < 8; index++)
        {
            elements.Add(CoreRow(index, "P"));
        }

        for (var index = 8; index < 16; index++)
        {
            elements.Add(CoreRow(index, "E"));
        }

        elements.Add(Gap());
        elements.Add(Group("RAM", OsdVendorRole.RamGroup, OsdGroupLayout.Columns,
            Sensor("ram.used", "RAM used", "GB", OsdVendorRole.RamGroup, White),
            Sensor("ram.available", "Available", "GB", OsdVendorRole.RamGroup, White),
            Sensor("ram.percent", "RAM usage", "%", OsdVendorRole.RamGroup, White)));
        elements.Add(Gap());
        elements.Add(Sensor("pc.latency", "PC latency", "ms", OsdVendorRole.LatencyGroup, Gold,
            labelColor: LatencyBlue));
        elements.Add(Gap());
        elements.AddRange(MetricsAndFrame());
        return elements;
    }

    private static IEnumerable<OsdElement> MetricsAndFrame() => new[]
    {
        Group("Metrics", OsdVendorRole.MetricGroup, OsdGroupLayout.Columns,
            Sensor("frame.fps.average", "Average", "FPS", OsdVendorRole.MetricGroup, Lime),
            Sensor("frame.fps.1low", "1% low", "FPS", OsdVendorRole.MetricGroup, Lime)),
        Gap(),
        Group("Framerate", OsdVendorRole.FrameGroup, OsdGroupLayout.Columns,
            Sensor("frame.fps", "Framerate", "FPS", OsdVendorRole.FrameGroup, YellowGreen,
                graph: true, graphColor: YellowGreen),
            Sensor("frame.time", "Frametime", "ms", OsdVendorRole.FrameGroup, YellowGreen,
                graph: true, graphColor: "#35E0D0")),
    };

    private static OsdElement CoreRow(int index, string type)
    {
        var coreLabel = $"Core #{index + 1} {type}";
        var clock = Sensor(IdForCore(index, ".clock"), "Clock", "MHz", OsdVendorRole.CpuGroup,
            graph: false, coreIndex: index);
        var load = Sensor(IdForCore(index, ".usage"), "Load", "%", OsdVendorRole.CpuGroup,
            graph: false, coreIndex: index);
        var temp = Sensor(IdForCore(index, ".temp"), "Temp", "°C", OsdVendorRole.CpuGroup,
            graph: false, coreIndex: index);
        return new OsdElement
        {
            Id = "row-core-" + index,
            Kind = OsdElementKind.Group,
            Label = coreLabel,
            GroupLayout = OsdGroupLayout.Columns,
            VendorRole = OsdVendorRole.CpuGroup,
            Children = { clock, load, temp },
        };
    }

    private static string IdForCore(int index, string suffix)
        => $"cpu.core.{index}{suffix}";

    private static OsdElement Group(string label, OsdVendorRole vendor, OsdGroupLayout layout,
        params OsdElement[] children) => new()
    {
        Id = "row-group-" + label.ToLowerInvariant().Replace(' ', '-'),
        Kind = OsdElementKind.Group,
        Label = label,
        GroupLayout = layout,
        VendorRole = vendor,
        Children = children.ToList(),
    };

    private static OsdElement Sensor(string sensorId, string label, string unit,
        OsdVendorRole vendor, string? valueColor = null, bool graph = false,
        string? graphColor = null, string? labelColor = null, int? coreIndex = null)
    {
        _ = coreIndex;
        return new OsdElement
        {
            Id = "row-" + sensorId.Replace('.', '-') + "-" + Slug(label),
            Kind = OsdElementKind.Sensor,
            Label = label,
            SensorId = sensorId,
            Unit = unit,
            VendorRole = vendor,
            LabelColor = labelColor ?? string.Empty,
            ValueColor = valueColor ?? Gold,
            ShowGraph = graph,
            GraphColor = graphColor ?? string.Empty,
            GraphThickness = 1.4,
            GraphHistoryPoints = 120,
        };
    }

    private static OsdElement Text(string text, string color, double size) => new()
    {
        Id = "row-text-" + Slug(text.Replace("{", string.Empty).Replace("}", string.Empty)),
        Kind = OsdElementKind.CustomText,
        Text = text,
        LabelColor = color,
        FontSize = size,
    };

    private static int _gapSequence;

    private static OsdElement Gap() => new()
    {
        Id = "row-gap-" + (++_gapSequence),
        Kind = OsdElementKind.EmptyLine,
        Height = 6,
    };

    private static string Slug(string text)
    {
        var buffer = new char[text.Length];
        var length = 0;
        foreach (var character in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                buffer[length++] = character;
            }
            else if (length > 0 && buffer[length - 1] != '-')
            {
                buffer[length++] = '-';
            }
        }

        return new string(buffer, 0, Math.Max(0, length)).Trim('-');
    }
}
