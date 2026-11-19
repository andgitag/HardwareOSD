namespace HardwareOSD.Core.TileOsd;

public enum TileElementCategory
{
    Layout = 0,
    Metrics = 1,
    Indicators = 2,
    Charts = 3,
    Text = 4,
}

/// <summary>
/// One entry in the builder's element library. Presets are data, not code paths: the builder renders
/// them as drag sources and <see cref="TileElementLibrary.Create"/> turns the id into a real node.
/// Adding a preset automatically adds it to the library UI.
/// </summary>
public sealed record TileElementPreset(
    string Id,
    TileElementCategory Category,
    TileNodeKind Kind,
    string LabelKey,
    string? SourceKey = null,
    string? ChartSeries = null,
    string? ChartTitle = null,
    string? ChartUnit = null,
    string? PanelLayout = null,
    string? Text = null,
    double Width = 180,
    double Height = 72,
    double FontSize = 14,
    string? Weight = null);

/// <summary>
/// The palette behind the builder's Add tab. Every source key is a real <see cref="TileSourceCatalog"/>
/// entry and every chart series is a real history id; nothing here invents a reading.
/// </summary>
public static class TileElementLibrary
{
    private static readonly Lazy<IReadOnlyList<TileElementPreset>> LazyAll = new(CreateAll);

    public static IReadOnlyList<TileElementPreset> All => LazyAll.Value;

    public static TileElementPreset? Find(string? id)
        => id is null
            ? null
            : All.FirstOrDefault(preset => string.Equals(preset.Id, id, StringComparison.Ordinal));

    public static string DisplayName(TileElementPreset preset)
        => preset.SourceKey is not null
            ? TileSourceCatalog.Label(preset.SourceKey)
            : !string.IsNullOrWhiteSpace(preset.LabelKey)
                ? preset.LabelKey
                : preset.ChartTitle ?? preset.Text ?? preset.Id;

    public static TileNode Create(TileDesignDocument document, string presetId)
    {
        var preset = Find(presetId) ?? throw new ArgumentOutOfRangeException(nameof(presetId));
        if (preset.Kind == TileNodeKind.Panel)
        {
            return CreatePanel(document, preset);
        }

        var tile = TileDocumentEditor.CreateTile(preset.Kind, 0, 0, preset.Width, preset.Height,
            document.Theme);
        tile.Name = DisplayName(preset);
        ConfigureLeaf(tile.Children[0], preset, document);
        return tile;
    }

    private static TileNode CreatePanel(TileDesignDocument document, TileElementPreset preset)
    {
        var separator = string.Equals(preset.Id, "layout.separator", StringComparison.Ordinal);
        return new TileNode
        {
            NodeId = TileDocumentEditor.NewNodeId(),
            Name = DisplayName(preset),
            Type = "panel",
            Kind = "panel",
            Layout = preset.PanelLayout ?? "canvas",
            X = 0,
            Y = 0,
            FrameWidth = preset.Width,
            FrameHeight = preset.Height,
            Width = preset.Width,
            Height = preset.Height,
            Padding = separator ? 0 : 6,
            Gap = separator ? 0 : 6,
            Background = true,
            Color = separator ? document.Theme.Subtext : document.Theme.Panel,
            BorderColor = separator ? null : "#FFFFFF1F",
            BorderWidth = separator ? null : 1,
            Radius = separator ? 0 : document.Theme.Radius,
            Clip = true,
            Children = new List<TileNode>(),
        };
    }

    private static void ConfigureLeaf(TileNode leaf, TileElementPreset preset, TileDesignDocument document)
    {
        var source = preset.SourceKey is null ? null : TileSourceCatalog.Find(preset.SourceKey);
        switch (preset.Kind)
        {
            case TileNodeKind.Metric:
                Bind(leaf, source, preset, valueSize: 22, labelSize: 11);
                break;
            case TileNodeKind.Bar:
                Bind(leaf, source, preset, valueSize: 14, labelSize: 11);
                leaf.Min = 0;
                leaf.Max = 100;
                leaf.Orientation = "horizontal";
                leaf.ShowValue = true;
                break;
            case TileNodeKind.Gauge:
                Bind(leaf, source, preset, valueSize: 16, labelSize: 10);
                leaf.Min = 0;
                leaf.Max = 100;
                leaf.Diameter = 100;
                leaf.Thickness = 6;
                leaf.StartAngle = 140;
                leaf.SweepAngle = 260;
                leaf.ShowValue = true;
                break;
            case TileNodeKind.Chart:
                leaf.Series = preset.ChartSeries ?? "frame.fps";
                leaf.Title = preset.ChartTitle ?? "Chart";
                leaf.Unit = preset.ChartUnit ?? string.Empty;
                leaf.ShowGridLines = true;
                leaf.ShowLabels = true;
                leaf.CurrentValue = true;
                leaf.Thickness = 2;
                leaf.Opacity = 1;
                leaf.HistoryPoints = 90;
                leaf.Color = document.Theme.Accent;
                break;
            case TileNodeKind.Text:
                leaf.Text = preset.Text ?? "Text";
                leaf.Size = preset.FontSize;
                leaf.Weight = preset.Weight;
                leaf.Align = "left";
                leaf.Color = document.Theme.Text;
                break;
        }
    }

    private static void Bind(TileNode leaf, TileSource? source, TileElementPreset preset,
        double valueSize, double labelSize)
    {
        leaf.Key = preset.SourceKey;
        leaf.Label = source?.Label ?? preset.SourceKey;
        leaf.Unit = source?.Unit ?? string.Empty;
        leaf.Digits = DigitsFor(source?.Unit);
        leaf.ValueSize = valueSize;
        leaf.LabelSize = labelSize;
        leaf.LabelPosition = "top";
        leaf.Format = null;
    }

    private static int DigitsFor(string? unit) => unit switch
    {
        "%" => 0,
        "°C" => 0,
        "MHz" => 0,
        "W" => 0,
        "MB" => 0,
        "FPS" => 0,
        "ms" => 2,
        _ => 1,
    };

    private static IReadOnlyList<TileElementPreset> CreateAll() => new List<TileElementPreset>
    {
        // Layout
        new("layout.row", TileElementCategory.Layout, TileNodeKind.Panel, "osd_lib_row",
            PanelLayout: "horizontal", Width: 260, Height: 64),
        new("layout.column", TileElementCategory.Layout, TileNodeKind.Panel, "osd_lib_column",
            PanelLayout: "vertical", Width: 220, Height: 180),
        new("layout.group", TileElementCategory.Layout, TileNodeKind.Panel, "osd_lib_group",
            PanelLayout: "canvas", Width: 280, Height: 180),
        new("layout.separator", TileElementCategory.Layout, TileNodeKind.Panel, "osd_lib_separator",
            PanelLayout: "canvas", Width: 220, Height: 2),

        // Metrics
        new("metric.cpu.temp", TileElementCategory.Metrics, TileNodeKind.Metric, string.Empty,
            SourceKey: "cpuTemp", Width: 170, Height: 64),
        new("metric.cpu.load", TileElementCategory.Metrics, TileNodeKind.Metric, string.Empty,
            SourceKey: "cpuLoad", Width: 170, Height: 64),
        new("metric.cpu.clock", TileElementCategory.Metrics, TileNodeKind.Metric, string.Empty,
            SourceKey: "cpuClock", Width: 170, Height: 64),
        new("metric.cpu.power", TileElementCategory.Metrics, TileNodeKind.Metric, string.Empty,
            SourceKey: "cpuPower", Width: 170, Height: 64),
        new("metric.gpu.temp", TileElementCategory.Metrics, TileNodeKind.Metric, string.Empty,
            SourceKey: "gpuTemp", Width: 170, Height: 64),
        new("metric.gpu.load", TileElementCategory.Metrics, TileNodeKind.Metric, string.Empty,
            SourceKey: "gpuLoad", Width: 170, Height: 64),
        new("metric.gpu.clock", TileElementCategory.Metrics, TileNodeKind.Metric, string.Empty,
            SourceKey: "gpuClock", Width: 170, Height: 64),
        new("metric.gpu.power", TileElementCategory.Metrics, TileNodeKind.Metric, string.Empty,
            SourceKey: "gpuPower", Width: 170, Height: 64),
        new("metric.ram.used", TileElementCategory.Metrics, TileNodeKind.Metric, string.Empty,
            SourceKey: "ramUsed", Width: 170, Height: 64),
        new("metric.ram.free", TileElementCategory.Metrics, TileNodeKind.Metric, string.Empty,
            SourceKey: "ramFree", Width: 170, Height: 64),
        new("metric.ram.load", TileElementCategory.Metrics, TileNodeKind.Metric, string.Empty,
            SourceKey: "ramLoad", Width: 170, Height: 64),
        new("metric.vram.used", TileElementCategory.Metrics, TileNodeKind.Metric, string.Empty,
            SourceKey: "vramUsed", Width: 170, Height: 64),
        new("metric.vram.free", TileElementCategory.Metrics, TileNodeKind.Metric, string.Empty,
            SourceKey: "vramFree", Width: 170, Height: 64),
        new("metric.vram.load", TileElementCategory.Metrics, TileNodeKind.Metric, string.Empty,
            SourceKey: "vramLoad", Width: 170, Height: 64),
        new("metric.fps", TileElementCategory.Metrics, TileNodeKind.Metric, string.Empty,
            SourceKey: "fpsCurrent", Width: 150, Height: 64),
        new("metric.frametime", TileElementCategory.Metrics, TileNodeKind.Metric, string.Empty,
            SourceKey: "frametimeCurrent", Width: 150, Height: 64),
        new("metric.fps.1pctlow", TileElementCategory.Metrics, TileNodeKind.Metric, string.Empty,
            SourceKey: "fps1pctLow", Width: 150, Height: 64),

        // Indicators
        new("bar.cpu", TileElementCategory.Indicators, TileNodeKind.Bar, string.Empty,
            SourceKey: "cpuLoad", Width: 200, Height: 48),
        new("bar.gpu", TileElementCategory.Indicators, TileNodeKind.Bar, string.Empty,
            SourceKey: "gpuLoad", Width: 200, Height: 48),
        new("gauge.cpu", TileElementCategory.Indicators, TileNodeKind.Gauge, string.Empty,
            SourceKey: "cpuLoad", Width: 130, Height: 130),
        new("gauge.gpu", TileElementCategory.Indicators, TileNodeKind.Gauge, string.Empty,
            SourceKey: "gpuTemp", Width: 130, Height: 130),
        new("indicator.heatmap", TileElementCategory.Indicators, TileNodeKind.Heatmap,
            "osd_lib_heatmap", Width: 240, Height: 72),

        // Charts
        new("chart.fps", TileElementCategory.Charts, TileNodeKind.Chart, string.Empty,
            ChartSeries: "frame.fps", ChartTitle: "FPS", ChartUnit: "FPS",
            Width: 280, Height: 110),
        new("chart.frametime", TileElementCategory.Charts, TileNodeKind.Chart, string.Empty,
            ChartSeries: "frame.time", ChartTitle: "Frame time", ChartUnit: "ms",
            Width: 280, Height: 110),
        new("chart.cpu.temp", TileElementCategory.Charts, TileNodeKind.Chart, string.Empty,
            ChartSeries: "cpu.temp", ChartTitle: "CPU temperature", ChartUnit: "°C",
            Width: 280, Height: 110),
        new("chart.cpu.load", TileElementCategory.Charts, TileNodeKind.Chart, string.Empty,
            ChartSeries: "cpu.usage", ChartTitle: "CPU load", ChartUnit: "%",
            Width: 280, Height: 110),
        new("chart.gpu.temp", TileElementCategory.Charts, TileNodeKind.Chart, string.Empty,
            ChartSeries: "gpu.temp", ChartTitle: "GPU temperature", ChartUnit: "°C",
            Width: 280, Height: 110),
        new("chart.gpu.load", TileElementCategory.Charts, TileNodeKind.Chart, string.Empty,
            ChartSeries: "gpu.usage", ChartTitle: "GPU load", ChartUnit: "%",
            Width: 280, Height: 110),
        new("chart.ram", TileElementCategory.Charts, TileNodeKind.Chart, string.Empty,
            ChartSeries: "ram.percent", ChartTitle: "RAM load", ChartUnit: "%",
            Width: 280, Height: 110),
        new("chart.vram", TileElementCategory.Charts, TileNodeKind.Chart, string.Empty,
            ChartSeries: "vram.percent", ChartTitle: "VRAM load", ChartUnit: "%",
            Width: 280, Height: 110),
        new("chart.cpu.clock", TileElementCategory.Charts, TileNodeKind.Chart, string.Empty,
            ChartSeries: "cpu.clock", ChartTitle: "CPU clock", ChartUnit: "MHz",
            Width: 280, Height: 110),
        new("chart.gpu.clock", TileElementCategory.Charts, TileNodeKind.Chart, string.Empty,
            ChartSeries: "gpu.clock", ChartTitle: "GPU clock", ChartUnit: "MHz",
            Width: 280, Height: 110),
        new("chart.cpu.power", TileElementCategory.Charts, TileNodeKind.Chart, string.Empty,
            ChartSeries: "cpu.power", ChartTitle: "CPU power", ChartUnit: "W",
            Width: 280, Height: 110),
        new("chart.gpu.power", TileElementCategory.Charts, TileNodeKind.Chart, string.Empty,
            ChartSeries: "gpu.power", ChartTitle: "GPU power", ChartUnit: "W",
            Width: 280, Height: 110),

        // Text
        new("text.body", TileElementCategory.Text, TileNodeKind.Text, "osd_lib_text_block",
            Text: "Text", Width: 160, Height: 28, FontSize: 14),
        new("text.title", TileElementCategory.Text, TileNodeKind.Text, "osd_lib_title",
            Text: "TITLE", Width: 180, Height: 26, FontSize: 18, Weight: "semibold"),
        new("visual.icon", TileElementCategory.Text, TileNodeKind.Icon, "osd_lib_icon",
            Width: 40, Height: 40),
        new("visual.image", TileElementCategory.Text, TileNodeKind.Image, "osd_lib_image",
            Width: 120, Height: 60),
    };
}
