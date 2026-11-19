using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HardwareOSD.Core.TileOsd;

/// <summary>Widget kinds used by the CapFrameX v1 tile design document.</summary>
public enum TileNodeKind
{
    Panel = 0,
    Metric = 1,
    Text = 2,
    Chart = 3,
    Bar = 4,
    Gauge = 5,
    Icon = 6,
    Image = 7,
    Heatmap = 8,
    ClassicRows = 9,
}

public enum TileFlowLayout
{
    Vertical = 0,
    Horizontal = 1,
    Grid = 2,
    Canvas = 3,
}

public enum TileBarOrientation
{
    Horizontal = 0,
    Vertical = 1,
}

/// <summary>One parsed #RRGGBB / #RRGGBBAA colour. Alpha is the last component, as in CapFrameX.</summary>
public readonly record struct TileColor(byte R, byte G, byte B, byte A)
{
    public static readonly TileColor Transparent = new(0, 0, 0, 0);
    public static readonly TileColor White = new(255, 255, 255, 255);

    public static TileColor Parse(string? text, TileColor fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        var value = text.Trim();
        if (value.StartsWith('#'))
        {
            value = value[1..];
        }

        if (value.Length is not (6 or 8) ||
            !uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var number))
        {
            return fallback;
        }

        if (value.Length == 6)
        {
            return new TileColor(
                (byte)((number >> 16) & 0xFF),
                (byte)((number >> 8) & 0xFF),
                (byte)(number & 0xFF),
                255);
        }

        return new TileColor(
            (byte)((number >> 24) & 0xFF),
            (byte)((number >> 16) & 0xFF),
            (byte)((number >> 8) & 0xFF),
            (byte)(number & 0xFF));
    }

    public TileColor WithAlpha(byte alpha) => this with { A = alpha };

    public string ToHex() => A == 255
        ? $"#{R:X2}{G:X2}{B:X2}"
        : $"#{R:X2}{G:X2}{B:X2}{A:X2}";
}

public sealed class TileTheme
{
    [JsonPropertyName("panel")] public string Panel { get; set; } = "#00000000";
    [JsonPropertyName("accent")] public string Accent { get; set; } = "#FFFFFFFF";
    [JsonPropertyName("text")] public string Text { get; set; } = "#FFFFFFFF";
    [JsonPropertyName("subtext")] public string Subtext { get; set; } = "#FFFFFFFF";
    [JsonPropertyName("fontFamily")] public string FontFamily { get; set; } = "Segoe UI";
    [JsonPropertyName("fontSize")] public double FontSize { get; set; } = 14;
    [JsonPropertyName("titleSize")] public double TitleSize { get; set; } = 12;
    [JsonPropertyName("pad")] public double Pad { get; set; } = 10;
    [JsonPropertyName("gap")] public double Gap { get; set; } = 8;
    [JsonPropertyName("radius")] public double Radius { get; set; } = 6;

    public TileColor PanelColor => TileColor.Parse(Panel, TileColor.Transparent);
    public TileColor AccentColor => TileColor.Parse(Accent, TileColor.White);
    public TileColor TextColor => TileColor.Parse(Text, TileColor.White);
    public TileColor SubtextColor => TileColor.Parse(Subtext, TextColor);

    public TileTheme Clone() => (TileTheme)MemberwiseClone();
}

/// <summary>A chart line, including the adaptive stutter threshold supported by CapFrameX.</summary>
public sealed class TileChartLine
{
    [JsonPropertyName("series")] public string? Series { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("factor")] public double? Factor { get; set; }
    [JsonPropertyName("color")] public string? Color { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
}

/// <summary>
/// One node of a v1 tile document. The JSON shipped by CapFrameX uses <c>type</c> for the kind and
/// nested <c>children</c>; the MCP editor guide uses <c>kind</c> and flat tiles. Both are accepted.
/// Geometry on a node is in design pixels. The catalog generator lifts the editor's absolute
/// frame onto every node, so a saved design has a complete measured layout.
/// </summary>
public sealed class TileNode
{
    /// <summary>Stable editor identity. Renderers ignore it; absent on CapFrameX-authored files.</summary>
    [JsonPropertyName("nid")] public string? NodeId { get; set; }
    /// <summary>Layer name shown in the builder. Falls back to the label or kind.</summary>
    [JsonPropertyName("name")] public string? Name { get; set; }
    /// <summary>Builder lock flag. A locked tile cannot be moved, resized or edited.</summary>
    [JsonPropertyName("locked")] public bool? Locked { get; set; }
    /// <summary>Visibility flag. Absent means visible; false keeps the tile in the document but hides it.</summary>
    [JsonPropertyName("visible")] public bool? Visible { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("layout")] public string? Layout { get; set; }
    [JsonPropertyName("children")] public List<TileNode> Children { get; set; } = new();

    [JsonPropertyName("x")] public double? X { get; set; }
    [JsonPropertyName("y")] public double? Y { get; set; }
    [JsonPropertyName("frameWidth")] public double? FrameWidth { get; set; }
    [JsonPropertyName("frameHeight")] public double? FrameHeight { get; set; }
    [JsonPropertyName("width")] public double? Width { get; set; }
    [JsonPropertyName("height")] public double? Height { get; set; }
    [JsonPropertyName("padding")] public double? Padding { get; set; }
    [JsonPropertyName("gap")] public double? Gap { get; set; }
    [JsonPropertyName("radius")] public double? Radius { get; set; }
    [JsonPropertyName("columns")] public int? Columns { get; set; }
    [JsonPropertyName("colSpan")] public int? ColumnSpan { get; set; }
    [JsonPropertyName("clip")] public bool? Clip { get; set; }

    [JsonPropertyName("background")] public bool? Background { get; set; }
    [JsonPropertyName("color")] public string? Color { get; set; }
    [JsonPropertyName("backgroundColor")] public string? BackgroundColor { get; set; }
    [JsonPropertyName("borderColor")] public string? BorderColor { get; set; }
    [JsonPropertyName("borderWidth")] public double? BorderWidth { get; set; }
    [JsonPropertyName("tileOpacity")] public double? TileOpacity { get; set; }

    // Text / label
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("weight")] public string? Weight { get; set; }
    [JsonPropertyName("align")] public string? Align { get; set; }
    /// <summary>Optional independent alignment for the label; null falls back to <see cref="Align"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("labelAlign")] public string? LabelAlign { get; set; }
    [JsonPropertyName("size")] public double? Size { get; set; }
    [JsonPropertyName("labelSize")] public double? LabelSize { get; set; }
    [JsonPropertyName("labelColor")] public string? LabelColor { get; set; }
    [JsonPropertyName("labelPosition")] public string? LabelPosition { get; set; }
    [JsonPropertyName("valueLines")] public int? ValueLines { get; set; }
    [JsonPropertyName("family")] public string? Family { get; set; }
    [JsonPropertyName("glyph")] public string? Glyph { get; set; }
    [JsonPropertyName("textColor")] public string? TextColor { get; set; }

    // Metric / bar / gauge
    [JsonPropertyName("key")] public string? Key { get; set; }
    [JsonPropertyName("secondaryKey")] public string? SecondaryKey { get; set; }
    [JsonPropertyName("unit")] public string? Unit { get; set; }
    [JsonPropertyName("secondaryUnit")] public string? SecondaryUnit { get; set; }
    [JsonPropertyName("format")] public string? Format { get; set; }
    [JsonPropertyName("digits")] public int? Digits { get; set; }
    [JsonPropertyName("secondaryDigits")] public int? SecondaryDigits { get; set; }
    [JsonPropertyName("separator")] public string? Separator { get; set; }
    [JsonPropertyName("valueSize")] public double? ValueSize { get; set; }
    [JsonPropertyName("unitSize")] public double? UnitSize { get; set; }
    [JsonPropertyName("unitColor")] public string? UnitColor { get; set; }
    [JsonPropertyName("valueColor")] public string? ValueColor { get; set; }
    [JsonPropertyName("valueScale")] public double? ValueScale { get; set; }
    [JsonPropertyName("secondaryScale")] public double? SecondaryScale { get; set; }
    [JsonPropertyName("valueFormat")] public string? ValueFormat { get; set; }
    [JsonPropertyName("showValue")] public bool? ShowValue { get; set; }
    /// <summary>Value at which the warning colour is used. Null disables the warning state.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("warningThreshold")] public double? WarningThreshold { get; set; }
    /// <summary>Value at which the critical colour is used; takes priority over warning.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("criticalThreshold")] public double? CriticalThreshold { get; set; }

    // Bar
    [JsonPropertyName("minimum")] public double? Minimum { get; set; }
    [JsonPropertyName("maximum")] public double? Maximum { get; set; }
    [JsonPropertyName("min")] public double? Min { get; set; }
    [JsonPropertyName("max")] public double? Max { get; set; }
    [JsonPropertyName("orientation")] public string? Orientation { get; set; }
    [JsonPropertyName("trackColor")] public string? TrackColor { get; set; }

    // Gauge
    [JsonPropertyName("diameter")] public double? Diameter { get; set; }
    [JsonPropertyName("thickness")] public double? Thickness { get; set; }
    [JsonPropertyName("startAngle")] public double? StartAngle { get; set; }
    [JsonPropertyName("sweepAngle")] public double? SweepAngle { get; set; }

    // Chart
    [JsonPropertyName("series")] public string? Series { get; set; }
    [JsonPropertyName("lines")] public List<TileChartLine>? Lines { get; set; }
    [JsonPropertyName("showBackground")] public bool? ShowBackground { get; set; }
    [JsonPropertyName("showBorder")] public bool? ShowBorder { get; set; }
    [JsonPropertyName("showLabels")] public bool? ShowLabels { get; set; }
    [JsonPropertyName("showGridLines")] public bool? ShowGridLines { get; set; }
    [JsonPropertyName("gridLines")] public bool? GridLines { get; set; }
    [JsonPropertyName("currentValue")] public bool? CurrentValue { get; set; }
    /// <summary>How many history samples the chart draws (10..240). Null uses the full buffer.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("historyPoints")] public int? HistoryPoints { get; set; }
    /// <summary>Draws the metric's history as a small sparkline inside the metric bounds.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("miniGraph")] public bool? MiniGraph { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("graphColor")] public string? GraphColor { get; set; }

    // Image
    [JsonPropertyName("dataUri")] public string? DataUri { get; set; }
    [JsonPropertyName("fit")] public string? Fit { get; set; }
    [JsonPropertyName("opacity")] public double? Opacity { get; set; }
    /// <summary>Optional hardware gate for embedded vendor artwork, e.g. cpu:AMD or gpu:NVIDIA.</summary>
    [JsonPropertyName("vendor")] public string? Vendor { get; set; }
    /// <summary>Optional model substring gate, e.g. "Core Ultra" or "RTX 50".</summary>
    [JsonPropertyName("model")] public string? Model { get; set; }

    // Heatmap
    [JsonPropertyName("keys")] public List<string>? Keys { get; set; }

    // Embedded classic rows
    [JsonPropertyName("rowScale")] public double? RowScale { get; set; }
    [JsonPropertyName("classicRowsAutoHeight")] public bool? ClassicRowsAutoHeight { get; set; }

    [JsonIgnore] public TileNodeKind NodeKind => ParseKind(Kind ?? Type);
    [JsonIgnore] public TileFlowLayout FlowLayout => ParseLayout(Layout);
    [JsonIgnore] public TileBarOrientation BarOrientation =>
        string.Equals(Orientation, "vertical", StringComparison.OrdinalIgnoreCase)
            ? TileBarOrientation.Vertical
            : TileBarOrientation.Horizontal;
    [JsonIgnore] public double EffectiveMinimum => Minimum ?? Min ?? 0;
    [JsonIgnore] public double EffectiveMaximum => Maximum ?? Max ?? 100;
    [JsonIgnore] public bool IsContainer => NodeKind == TileNodeKind.Panel;

    public static TileNodeKind ParseKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "panel" or "container" or "group" => TileNodeKind.Panel,
        "metric" => TileNodeKind.Metric,
        "text" => TileNodeKind.Text,
        "chart" => TileNodeKind.Chart,
        "bar" => TileNodeKind.Bar,
        "gauge" => TileNodeKind.Gauge,
        "icon" => TileNodeKind.Icon,
        "image" => TileNodeKind.Image,
        "heatmap" => TileNodeKind.Heatmap,
        "classicrows" => TileNodeKind.ClassicRows,
        _ => TileNodeKind.Metric,
    };

    private static TileFlowLayout ParseLayout(string? layout) => layout?.Trim().ToLowerInvariant() switch
    {
        "horizontal" => TileFlowLayout.Horizontal,
        "grid" => TileFlowLayout.Grid,
        "canvas" => TileFlowLayout.Canvas,
        _ => TileFlowLayout.Vertical,
    };

    public IEnumerable<TileNode> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var descendant in child.Descendants())
            {
                yield return descendant;
            }
        }
    }
}

/// <summary>A complete saved tile design. Version 1 is the only supported schema.</summary>
public sealed class TileDesignDocument
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("family")] public string Family { get; set; } = "Everyday";
    [JsonPropertyName("anchor")] public string Anchor { get; set; } = "top_left";
    [JsonPropertyName("marginX")] public double MarginX { get; set; } = 24;
    [JsonPropertyName("marginY")] public double MarginY { get; set; } = 24;
    [JsonPropertyName("theme")] public TileTheme Theme { get; set; } = new();
    [JsonPropertyName("root")] public TileNode Root { get; set; } = new();

    // Builder settings. Additive: CapFrameX-authored documents leave them at the defaults.
    [JsonPropertyName("gridSize")] public double GridSize { get; set; } = 8;
    [JsonPropertyName("snapToGrid")] public bool SnapToGrid { get; set; } = true;
    [JsonPropertyName("showGrid")] public bool ShowGrid { get; set; } = true;
    [JsonPropertyName("scale")] public double Scale { get; set; } = 1;
    [JsonPropertyName("fullWidth")] public bool FullWidth { get; set; }
    [JsonPropertyName("edge")] public string Edge { get; set; } = "placement";

    [JsonIgnore]
    public double CanvasWidth
    {
        get
        {
            if (Root.FrameWidth is > 0) return Root.FrameWidth.Value;
            if (Root.Width is > 0) return Root.Width.Value;
            return ChildrenRight(Root);
        }
    }

    [JsonIgnore]
    public double CanvasHeight
    {
        get
        {
            if (Root.FrameHeight is > 0) return Root.FrameHeight.Value;
            if (Root.Height is > 0) return Root.Height.Value;
            return ChildrenBottom(Root);
        }
    }

    [JsonIgnore]
    public int TileCount => Root.Descendants().Count(node => node.NodeKind != TileNodeKind.Panel);

    /// <summary>Deep copy through JSON so every nested node, line and key list is independent.</summary>
    public TileDesignDocument Clone()
    {
        var copy = JsonSerializer.Deserialize<TileDesignDocument>(ToJson(), TileJson.Options)
                   ?? throw new InvalidOperationException("tile design clone failed");
        return copy;
    }

    /// <summary>
    /// A copy stretched horizontally to <paramref name="targetWidth"/> design pixels. Only x
    /// positions and widths scale; heights, padding, gaps and text sizes stay, which is what
    /// CapFrameX does with a full-width scene. The copy clears <see cref="FullWidth"/> so applying
    /// it twice cannot stretch a design again.
    /// </summary>
    public TileDesignDocument StretchToWidth(double targetWidth)
    {
        var copy = Clone();
        var source = CanvasWidth;
        copy.FullWidth = false;
        if (targetWidth <= 0 || source <= 0 || Math.Abs(targetWidth - source) < 0.01)
        {
            return copy;
        }

        var ratio = targetWidth / source;
        copy.Root.Width = targetWidth;
        copy.Root.FrameWidth = targetWidth;
        ScaleChildrenX(copy.Root, ratio);
        copy.NormalizeEditorSettings();
        return copy;
    }

    private static void ScaleChildrenX(TileNode node, double ratio)
    {
        foreach (var child in node.Children)
        {
            if (child.X is not null)
            {
                child.X = Math.Max(0, child.X.Value * ratio);
            }

            if (child.Width is not null)
            {
                child.Width = Math.Max(0.01, child.Width.Value * ratio);
            }

            if (child.FrameWidth is not null)
            {
                child.FrameWidth = Math.Max(0.01, child.FrameWidth.Value * ratio);
            }

            ScaleChildrenX(child, ratio);
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, TileJson.Options);

    /// <summary>Clamps the builder settings to their documented ranges.</summary>
    public void NormalizeEditorSettings()
    {
        GridSize = Math.Clamp(GridSize <= 0 ? 8 : GridSize, 1, 128);
        Scale = Math.Clamp(Scale <= 0 ? 1 : Scale, 0.5, 2);
        Theme ??= new TileTheme();
        Theme.Pad = Math.Clamp(Theme.Pad, 0, 40);
        Theme.Gap = Math.Clamp(Theme.Gap, 0, 40);
        Theme.Radius = Math.Clamp(Theme.Radius, 0, 32);
        Theme.FontSize = Math.Clamp(Theme.FontSize, 8, 72);
        Theme.TitleSize = Math.Clamp(Theme.TitleSize, 8, 72);
        Edge = string.Equals(Edge, "top", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(Edge, "bottom", StringComparison.OrdinalIgnoreCase)
            ? Edge.ToLowerInvariant()
            : "placement";
    }

    public TileNode? FindNode(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = Root;
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var index) || index < 0 || index >= current.Children.Count)
            {
                return null;
            }

            current = current.Children[index];
        }

        return current;
    }

    public IEnumerable<(string Path, TileNode Node)> Walk()
    {
        foreach (var item in Walk(Root, string.Empty))
        {
            yield return item;
        }
    }

    private static IEnumerable<(string Path, TileNode Node)> Walk(TileNode node, string path)
    {
        for (var i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            var childPath = path.Length == 0 ? i.ToString(CultureInfo.InvariantCulture) : path + "/" + i.ToString(CultureInfo.InvariantCulture);
            yield return (childPath, child);
            foreach (var nested in Walk(child, childPath))
            {
                yield return nested;
            }
        }
    }

    private static double ChildrenRight(TileNode node)
    {
        var right = 0.0;
        foreach (var child in node.Children)
        {
            var x = child.X ?? 0;
            var width = child.FrameWidth ?? child.Width ?? 0;
            right = Math.Max(right, x + width);
            right = Math.Max(right, ChildrenRight(child));
        }

        return right;
    }

    private static double ChildrenBottom(TileNode node)
    {
        var bottom = 0.0;
        foreach (var child in node.Children)
        {
            var y = child.Y ?? 0;
            var height = child.FrameHeight ?? child.Height ?? 0;
            bottom = Math.Max(bottom, y + height);
            bottom = Math.Max(bottom, ChildrenBottom(child));
        }

        return bottom;
    }

    /// <summary>Validates the parts of the document HardwareOSD depends on.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Version != 1)
        {
            errors.Add($"unsupported version {Version}");
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add("missing name");
        }

        if (Root is null)
        {
            errors.Add("missing root");
            return errors;
        }

        if (CanvasWidth <= 0 || CanvasHeight <= 0)
        {
            errors.Add("canvas has no size");
        }

        foreach (var (path, node) in Walk())
        {
            if (node.FrameWidth is < 0 || node.FrameHeight is < 0)
            {
                errors.Add($"{path}: negative frame");
            }

            if (node.NodeKind == TileNodeKind.Image && string.IsNullOrWhiteSpace(node.DataUri))
            {
                errors.Add($"{path}: image without dataUri");
            }
        }

        return errors;
    }

    public static TileDesignDocument? Parse(string json, out string? error)
    {
        try
        {
            var document = JsonSerializer.Deserialize<TileDesignDocument>(json, TileJson.Options);
            if (document is null)
            {
                error = "empty document";
                return null;
            }

            error = null;
            return document;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return null;
        }
    }
}

public sealed class TileCatalogEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("family")] public string Family { get; set; } = "Everyday";
    [JsonPropertyName("file")] public string File { get; set; } = string.Empty;
    [JsonPropertyName("width")] public double Width { get; set; }
    [JsonPropertyName("height")] public double Height { get; set; }
    [JsonPropertyName("fontFamily")] public string FontFamily { get; set; } = "Segoe UI";
    [JsonPropertyName("fontSize")] public double FontSize { get; set; } = 14;
    [JsonPropertyName("titleSize")] public double TitleSize { get; set; } = 12;
    [JsonPropertyName("panel")] public string Panel { get; set; } = "#00000000";
    [JsonPropertyName("accent")] public string Accent { get; set; } = "#FFFFFFFF";
    [JsonPropertyName("text")] public string Text { get; set; } = "#FFFFFFFF";
    [JsonPropertyName("topTiles")] public int TopTiles { get; set; }
    [JsonPropertyName("nodes")] public int Nodes { get; set; }
    [JsonPropertyName("images")] public int Images { get; set; }
    [JsonPropertyName("metrics")] public int Metrics { get; set; }
    [JsonPropertyName("bars")] public int Bars { get; set; }
    [JsonPropertyName("charts")] public int Charts { get; set; }
    [JsonPropertyName("gauges")] public int Gauges { get; set; }
    [JsonPropertyName("heatmaps")] public int Heatmaps { get; set; }
    [JsonPropertyName("texts")] public int Texts { get; set; }
}

internal static class TileJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}
