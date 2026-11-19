using System.Windows;
using System.Windows.Media;
using HardwareOSD.Core.Overlay;

namespace HardwareOSD.App.Overlay;

/// <summary>
/// Draws a built row visual. Kept separate from <see cref="OverlaySurface"/> so an embedded
/// classicRows tile can render the active row profile inside a tile design without a second window.
/// </summary>
public static class OverlayVisualRenderer
{
    private static readonly Dictionary<GraphPenKey, Pen> GraphPens = new();

    public static void Draw(
        DrawingContext dc,
        OverlayVisual visual,
        OverlayTheme theme,
        double pixelsPerDip,
        bool drawBackground = true)
    {
        if (visual.Width <= 0 || visual.Height <= 0)
        {
            return;
        }

        if (drawBackground && visual.ShowBackground)
        {
            if (theme.Shadow)
            {
                var shadowRect = new Rect(1, 2, visual.Width, visual.Height);
                dc.DrawRoundedRectangle(theme.ShadowBrush, null, shadowRect,
                    theme.CornerRadius + 1, theme.CornerRadius + 1);
            }

            var rect = new Rect(0, 0, visual.Width, visual.Height);
            var pen = theme.CornerRadius > 0 ? new Pen(theme.BorderBrush, 1) : null;
            if (pen is not null)
            {
                pen.Freeze();
            }

            dc.DrawRoundedRectangle(theme.BackgroundBrush, pen, rect, theme.CornerRadius,
                theme.CornerRadius);
        }

        for (var i = 0; i < visual.Rows.Count; i++)
        {
            var row = visual.Rows[i];
            if (row.Kind == OverlayRowKind.EmptyLine)
            {
                continue;
            }

            if (row.Kind == OverlayRowKind.Divider)
            {
                var y = Math.Round(row.Top + row.Height / 2) + 0.5;
                dc.DrawLine(theme.SeparatorPen,
                    new Point(theme.PaddingX + row.Indent, y),
                    new Point(visual.Width - theme.PaddingX, y));
                continue;
            }

            if (!visual.Inline && theme.ShowLabels &&
                row.Kind is OverlayRowKind.SensorGroup or OverlayRowKind.SensorLine
                    or OverlayRowKind.Graph or OverlayRowKind.ProgressBar or OverlayRowKind.CustomValue)
            {
                var barHeight = Math.Max(6, row.ContentHeight * 0.62);
                var barRect = new Rect(theme.PaddingX + row.Indent,
                    row.Top + (row.ContentHeight - barHeight) / 2, 3, barHeight);
                dc.DrawRoundedRectangle(theme.AccentBrushFor(row.Group), null, barRect, 1.5, 1.5);
            }

            if (row.Label is not null)
            {
                dc.DrawText(row.Label, new Point(row.LabelLeft, row.LabelTop));
            }

            if (row.Ring is { } ring)
            {
                DrawRing(dc, ring);
            }

            foreach (var cell in row.Cells)
            {
                if (cell.Label is not null)
                {
                    dc.DrawText(cell.Label, new Point(cell.LabelLeft, cell.LabelTop));
                }

                var valueY = row.Top + (row.ContentHeight - cell.Value.Height) / 2;
                dc.DrawText(cell.Value, new Point(cell.ValueLeft, valueY));
                if (cell.Unit is not null)
                {
                    var unitY = valueY + cell.Value.Height - cell.Unit.Height - 1;
                    dc.DrawText(cell.Unit, new Point(cell.UnitLeft, unitY));
                }
            }

            if (row.Progress is { } progress)
            {
                dc.DrawRoundedRectangle(progress.Track, null, progress.Bounds, 2, 2);
                var fill = new Rect(progress.Bounds.X, progress.Bounds.Y,
                    Math.Max(0, progress.Bounds.Width * progress.Value), progress.Bounds.Height);
                if (fill.Width > 0)
                {
                    dc.DrawRoundedRectangle(progress.Fill, null, fill, 2, 2);
                }
            }

            if (row.Stats is not null)
            {
                dc.DrawText(row.Stats, new Point(row.StatsLeft, row.StatsTop));
            }

            if (row.Subtitle is not null)
            {
                dc.DrawText(row.Subtitle, new Point(row.SubtitleLeft, row.SubtitleTop));
            }

            if (theme.ShowSeparators && !visual.Inline && i < visual.Rows.Count - 1 &&
                row.Kind != OverlayRowKind.GroupHeader && row.Kind != OverlayRowKind.DeviceHeader)
            {
                var y = Math.Round(row.Top + row.Height + theme.RowSpacing / 2) + 0.5;
                dc.DrawLine(theme.SeparatorPen,
                    new Point(theme.PaddingX, y),
                    new Point(visual.Width - theme.PaddingX, y));
            }

            if (row.Sparkline is { } graph)
            {
                DrawSparkline(dc, graph);
            }
        }

        if (visual.Inline)
        {
            var top = theme.PaddingY + 2;
            var bottom = visual.Height - theme.PaddingY - 2;
            foreach (var x in visual.InlineSeparators)
            {
                dc.DrawLine(theme.SeparatorPen, new Point(x, top), new Point(x, bottom));
            }
        }

        if (visual.Cores.Count > 0 && visual.CoreBlockTop > 0)
        {
            var y = Math.Round(visual.CoreBlockTop) + 0.5;
            dc.DrawLine(theme.DividerPen,
                new Point(theme.PaddingX, y),
                new Point(visual.Width - theme.PaddingX, y));

            foreach (var core in visual.Cores)
            {
                dc.DrawText(core.Label, new Point(core.LabelLeft, core.Top));
                dc.DrawText(core.Temperature, new Point(core.TemperatureLeft, core.Top));
                dc.DrawText(core.Usage, new Point(core.UsageLeft, core.Top));
            }
        }

        if (visual.DemoBadge is not null)
        {
            dc.DrawText(visual.DemoBadge, new Point(theme.PaddingX, visual.DemoBadgeTop));
        }
    }

    private static void DrawRing(DrawingContext dc, VisualRing ring)
    {
        var radius = Math.Min(ring.Bounds.Width, ring.Bounds.Height) / 2;
        var center = new Point(ring.Bounds.Left + radius, ring.Bounds.Top + radius);
        var pen = new Pen(ring.Track, 3);
        pen.Freeze();
        dc.DrawEllipse(null, pen, center, radius - 1.5, radius - 1.5);
        var value = Math.Clamp(ring.Value, 0, 1);
        if (value <= 0.001)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var start = -Math.PI / 2;
            var end = start + Math.PI * 2 * value;
            var startPoint = new Point(center.X + (radius - 1.5) * Math.Cos(start),
                center.Y + (radius - 1.5) * Math.Sin(start));
            var endPoint = new Point(center.X + (radius - 1.5) * Math.Cos(end),
                center.Y + (radius - 1.5) * Math.Sin(end));
            context.BeginFigure(startPoint, false, false);
            context.ArcTo(endPoint, new Size(radius - 1.5, radius - 1.5), 0,
                value > 0.5, SweepDirection.Clockwise, true, false);
        }

        geometry.Freeze();
        var fillPen = new Pen(ring.Fill, 3);
        fillPen.Freeze();
        dc.DrawGeometry(null, fillPen, geometry);
    }

    private static void DrawSparkline(DrawingContext dc, VisualSparkline graph)
    {
        var bounds = graph.Bounds;
        var values = graph.Values;
        var count = Math.Min(graph.Count, values.Length);
        if (count < 2)
        {
            return;
        }

        double min = double.MaxValue, max = double.MinValue;
        for (var i = 0; i < count; i++)
        {
            var value = values[values.Length - count + i];
            if (double.IsNaN(value))
            {
                continue;
            }

            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }

        if (!double.IsNaN(graph.ScaleMin) && !double.IsNaN(graph.ScaleMax))
        {
            min = graph.ScaleMin;
            max = graph.ScaleMax;
        }

        if (min > max)
        {
            return;
        }

        if (max - min < 1.0)
        {
            max = min + 1.0;
        }

        var range = max - min;
        var offset = values.Length - count;
        var firstValid = 0;
        while (firstValid < count && double.IsNaN(values[offset + firstValid]))
        {
            firstValid++;
        }

        var validCount = count - firstValid;
        if (validCount < 4)
        {
            return;
        }

        var stepX = bounds.Width / (validCount - 1);
        var line = new StreamGeometry();
        var hasLine = false;
        using (var lineContext = line.Open())
        {
            var index = firstValid;
            while (index < count)
            {
                while (index < count && double.IsNaN(values[offset + index]))
                {
                    index++;
                }

                var start = index;
                if (start >= count)
                {
                    break;
                }

                var end = start;
                while (end + 1 < count && !double.IsNaN(values[offset + end + 1]))
                {
                    end++;
                }

                if (end > start)
                {
                    Point PointAt(int sample)
                    {
                        var value = values[offset + sample];
                        var normalized = Math.Clamp((value - min) / range, 0, 1);
                        return new Point(
                            bounds.Left + stepX * (sample - firstValid),
                            bounds.Bottom - normalized * bounds.Height);
                    }

                    lineContext.BeginFigure(PointAt(start), false, false);
                    for (var sample = start + 1; sample <= end; sample++)
                    {
                        lineContext.LineTo(PointAt(sample), true, false);
                    }

                    hasLine = true;
                }

                index = end + 1;
            }
        }

        if (!hasLine)
        {
            return;
        }

        line.Freeze();
        dc.DrawGeometry(null, GetGraphPen(graph.Stroke, graph.Thickness), line);
    }

    private static Pen GetGraphPen(Brush brush, double thickness)
    {
        var key = new GraphPenKey(brush, Math.Round(Math.Clamp(thickness, 0.5, 6), 1));
        if (GraphPens.TryGetValue(key, out var pen))
        {
            return pen;
        }

        pen = new Pen(brush, key.Thickness)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        pen.Freeze();
        GraphPens[key] = pen;
        return pen;
    }

    private readonly record struct GraphPenKey(Brush Brush, double Thickness);
}
