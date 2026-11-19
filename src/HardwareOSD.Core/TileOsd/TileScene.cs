using HardwareOSD.Core.Metrics;

namespace HardwareOSD.Core.TileOsd;

public readonly record struct TileRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool IsEmpty => Width <= 0.01 || Height <= 0.01;

    public TileRect Inset(double padding)
        => new(X + padding, Y + padding, Math.Max(0, Width - padding * 2), Math.Max(0, Height - padding * 2));

    public TileRect Inset(double left, double top, double right, double bottom)
        => new(X + left, Y + top, Math.Max(0, Width - left - right), Math.Max(0, Height - top - bottom));

    public static TileRect Intersect(TileRect a, TileRect b)
    {
        var x = Math.Max(a.X, b.X);
        var y = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.Right, b.Right);
        var bottom = Math.Min(a.Bottom, b.Bottom);
        return right <= x || bottom <= y ? default : new TileRect(x, y, right - x, bottom - y);
    }
}

public enum TileTextAlign
{
    Left = 0,
    Center = 1,
    Right = 2,
}

public enum TileChartLineKind
{
    Value = 0,
    Stutter = 1,
}

public sealed class TileChartLineScene
{
    public required string Label { get; init; }
    public required TileChartLineKind Kind { get; init; }
    public required TileColor Color { get; init; }
    public required double[] Values { get; init; }
    public double ScaleMin { get; init; }
    public double ScaleMax { get; init; }
}

public abstract class TileSceneElement
{
    public required TileRect Bounds { get; init; }
    public TileRect? Clip { get; init; }
}

public sealed class TilePanelScene : TileSceneElement
{
    public TileColor Fill { get; init; } = TileColor.Transparent;
    public TileColor Border { get; init; } = TileColor.Transparent;
    public double BorderWidth { get; init; }
    public double Radius { get; init; }
}

public sealed class TileTextScene : TileSceneElement
{
    public required string Text { get; init; }
    public double FontSize { get; init; } = 14;
    public string Family { get; init; } = "Segoe UI";
    public bool Semibold { get; init; }
    public TileColor Color { get; init; } = TileColor.White;
    public TileTextAlign Align { get; init; }
    public int MaxLines { get; init; } = 1;
}

public sealed class TileMetricScene : TileSceneElement
{
    public string? Label { get; init; }
    public string Value { get; init; } = "N/A";
    public string Unit { get; init; } = string.Empty;
    public string SecondaryValue { get; init; } = string.Empty;
    public string SecondaryUnit { get; init; } = string.Empty;
    public string Separator { get; init; } = " / ";
    public string LabelPosition { get; init; } = "top";
    public double ValueSize { get; init; } = 20;
    public double LabelSize { get; init; } = 12;
    public double UnitSize { get; init; } = 12;
    public string Family { get; init; } = "Segoe UI";
    public bool Semibold { get; init; }
    public TileColor ValueColor { get; init; } = TileColor.White;
    public TileColor LabelColor { get; init; } = TileColor.White;
    public TileColor UnitColor { get; init; } = TileColor.White;
    public TileTextAlign Align { get; init; }
    public TileTextAlign LabelAlign { get; init; }
    public bool Available { get; init; } = true;
    public int ValueLines { get; init; } = 1;
    public MetricSeverity Severity { get; init; }
    public bool MiniGraph { get; init; }
    public IReadOnlyList<double> GraphValues { get; init; } = Array.Empty<double>();
    public TileColor GraphColor { get; init; } = TileColor.White;
    public double GraphThickness { get; init; } = 1.5;
    public double GraphOpacity { get; init; } = 0.9;
}

public sealed class TileChartScene : TileSceneElement
{
    public string Title { get; init; } = string.Empty;
    public string Unit { get; init; } = string.Empty;
    public required IReadOnlyList<TileChartLineScene> Lines { get; init; }
    public TileColor LineColor { get; init; } = TileColor.White;
    public TileColor LabelColor { get; init; } = TileColor.White;
    public TileColor BackgroundColor { get; init; } = TileColor.Transparent;
    public TileColor BorderColor { get; init; } = TileColor.Transparent;
    public bool ShowBackground { get; init; }
    public bool ShowBorder { get; init; }
    public bool ShowLabels { get; init; } = true;
    public bool ShowGridLines { get; init; }
    public bool CurrentValue { get; init; }
    public string Family { get; init; } = "Segoe UI";
    public double ScaleMin { get; init; }
    public double ScaleMax { get; init; }
    public double LineThickness { get; init; } = 2;
    public double Opacity { get; init; } = 1;
}

public sealed class TileBarScene : TileSceneElement
{
    public string Label { get; init; } = string.Empty;
    public string Value { get; init; } = "N/A";
    public string Unit { get; init; } = string.Empty;
    public double Fraction { get; init; }
    public double BarThickness { get; init; } = 8;
    public TileRect BarBounds { get; init; }
    public TileBarOrientation Orientation { get; init; }
    public bool ShowValue { get; init; } = true;
    public bool IsChip { get; init; }
    public string Family { get; init; } = "Segoe UI";
    public TileColor Fill { get; init; } = TileColor.White;
    public TileColor Track { get; init; } = new(58, 58, 74, 128);
    public TileColor LabelColor { get; init; } = TileColor.White;
    public TileColor ValueColor { get; init; } = TileColor.White;
    public bool Available { get; init; } = true;
}

public sealed class TileGaugeScene : TileSceneElement
{
    public string Label { get; init; } = string.Empty;
    public string Value { get; init; } = "N/A";
    public string Unit { get; init; } = string.Empty;
    public double Fraction { get; init; }
    public double Thickness { get; init; } = 5;
    public double StartAngle { get; init; } = 140;
    public double SweepAngle { get; init; } = 260;
    public bool ShowValue { get; init; } = true;
    public string Family { get; init; } = "Segoe UI";
    public TileColor Fill { get; init; } = TileColor.White;
    public TileColor Track { get; init; } = new(58, 58, 74, 128);
    public TileColor LabelColor { get; init; } = TileColor.White;
    public TileColor ValueColor { get; init; } = TileColor.White;
    public bool Available { get; init; } = true;
}

public sealed class TileImageScene : TileSceneElement
{
    public required string DataUri { get; init; }
    public string Fit { get; init; } = "contain";
    public double Opacity { get; init; } = 1;
}

public sealed class TileIconScene : TileSceneElement
{
    public required string Glyph { get; init; }
    public string Family { get; init; } = "Segoe MDL2 Assets";
    public double Size { get; init; } = 16;
    public TileColor Color { get; init; } = TileColor.White;
}

public readonly record struct TileHeatCell(TileRect Bounds, double Fraction, string Text);

public sealed class TileHeatmapScene : TileSceneElement
{
    public string Title { get; init; } = string.Empty;
    public required IReadOnlyList<TileHeatCell> Cells { get; init; }
    public TileColor CellColor { get; init; } = TileColor.White;
    public TileColor LabelColor { get; init; } = TileColor.White;
    public TileColor EmptyColor { get; init; } = new(58, 58, 74, 128);
    public double CellGap { get; init; } = 1.5;
}

public sealed class TileClassicRowsScene : TileSceneElement
{
    public double RowScale { get; init; } = 1;
}

/// <summary>Complete draw list for one tile design frame. Geometry is in design pixels.</summary>
public sealed class TileScene
{
    public double Width { get; init; }
    public double Height { get; init; }
    public required IReadOnlyList<TileSceneElement> Elements { get; init; }
    public bool IsEmpty => Elements.Count == 0;

    public static readonly TileScene Empty = new()
    {
        Width = 0,
        Height = 0,
        Elements = Array.Empty<TileSceneElement>(),
    };
}

