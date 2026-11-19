using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HardwareOSD.Core.TileOsd;

namespace HardwareOSD.App.Overlay;

/// <summary>
/// Draws a <see cref="TileScene"/> exactly as the tile design describes it. Geometry is expressed in
/// design pixels: one design pixel is one WPF device-independent pixel before the caller's zoom
/// transform. The renderer has no knowledge of CapFrameX; it consumes the WPF-free scene produced by
/// <see cref="TileSceneBuilder"/> and the values are already resolved from HardwareOSD sensors.
/// </summary>
public static class TileSceneRenderer
{
    private static readonly Dictionary<uint, SolidColorBrush> BrushCache = new();
    private static readonly Dictionary<string, BitmapSource?> ImageCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<TypefaceKey, Typeface> TypefaceCache = new();

    public static void Draw(
        DrawingContext dc,
        TileScene scene,
        double pixelsPerDip,
        double scale = 1,
        Action<DrawingContext, TileRect>? classicRows = null)
    {
        if (scene.Width <= 0 || scene.Height <= 0 || scene.Elements.Count == 0)
        {
            return;
        }

        var push = scale > 0 && Math.Abs(scale - 1) > 0.0001;
        if (push)
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
        }

        var sceneClip = new Rect(0, 0, scene.Width, scene.Height);
        dc.PushClip(new RectangleGeometry(sceneClip));
        foreach (var element in scene.Elements)
        {
            DrawElement(dc, element, pixelsPerDip, classicRows);
        }

        dc.Pop();
        if (push)
        {
            dc.Pop();
        }
    }

    private static void DrawElement(
        DrawingContext dc,
        TileSceneElement element,
        double pixelsPerDip,
        Action<DrawingContext, TileRect>? classicRows)
    {
        if (element.Bounds.IsEmpty)
        {
            return;
        }

        var pushedClip = false;
        if (element.Clip is { IsEmpty: false } clip)
        {
            dc.PushClip(new RectangleGeometry(ToRect(clip)));
            pushedClip = true;
        }

        switch (element)
        {
            case TilePanelScene panel:
                DrawPanel(dc, panel);
                break;
            case TileTextScene text:
                DrawText(dc, text, pixelsPerDip);
                break;
            case TileMetricScene metric:
                DrawMetric(dc, metric, pixelsPerDip);
                break;
            case TileChartScene chart:
                DrawChart(dc, chart, pixelsPerDip);
                break;
            case TileBarScene bar:
                DrawBar(dc, bar, pixelsPerDip);
                break;
            case TileGaugeScene gauge:
                DrawGauge(dc, gauge, pixelsPerDip);
                break;
            case TileImageScene image:
                DrawImage(dc, image);
                break;
            case TileIconScene icon:
                DrawIcon(dc, icon, pixelsPerDip);
                break;
            case TileHeatmapScene heatmap:
                DrawHeatmap(dc, heatmap, pixelsPerDip);
                break;
            case TileClassicRowsScene rows:
                classicRows?.Invoke(dc, rows.Bounds);
                break;
        }

        if (pushedClip)
        {
            dc.Pop();
        }
    }

    private static void DrawPanel(DrawingContext dc, TilePanelScene panel)
    {
        var rect = ToRect(panel.Bounds);
        var radius = Math.Clamp(panel.Radius, 0, Math.Min(rect.Width, rect.Height) / 2);
        var fill = panel.Fill.A > 0 ? Brush(panel.Fill) : null;
        var border = panel.BorderWidth > 0 && panel.Border.A > 0
            ? new Pen(Brush(panel.Border), panel.BorderWidth)
            : null;
        if (fill is null && border is null)
        {
            return;
        }

        if (border is not null)
        {
            border.Freeze();
        }

        if (radius <= 0.1)
        {
            dc.DrawRectangle(fill, border, rect);
        }
        else
        {
            dc.DrawRoundedRectangle(fill, border, rect, radius, radius);
        }
    }

    private static void DrawText(DrawingContext dc, TileTextScene text, double pixelsPerDip)
    {
        if (string.IsNullOrEmpty(text.Text))
        {
            return;
        }

        var bounds = text.Bounds;
        var lines = LimitLines(text.Text, text.MaxLines);
        var formatted = Format(lines, text.Family, text.FontSize, text.Semibold,
            Brush(text.Color), pixelsPerDip, bounds.Width);
        var y = bounds.Y + Math.Max(0, (bounds.Height - formatted.Height) / 2);
        dc.DrawText(formatted, new Point(bounds.X, y));
    }

    private static void DrawMetric(DrawingContext dc, TileMetricScene metric, double pixelsPerDip)
    {
        var originalBounds = metric.Bounds;
        var graphHeight = metric.MiniGraph && metric.GraphValues.Count > 1
            ? Math.Min(22, Math.Max(8, originalBounds.Height * 0.32))
            : 0;
        var bounds = graphHeight > 0
            ? new TileRect(originalBounds.X, originalBounds.Y, originalBounds.Width,
                Math.Max(1, originalBounds.Height - graphHeight - 2))
            : originalBounds;
        var labelBrush = Brush(metric.LabelColor);
        var valueBrush = Brush(metric.ValueColor);
        var unitBrush = Brush(metric.UnitColor);
        var hasLabel = !string.IsNullOrWhiteSpace(metric.Label) &&
                       !string.Equals(metric.LabelPosition, "none", StringComparison.OrdinalIgnoreCase);

        FormattedText? label = hasLabel
            ? Format(metric.Label!, metric.Family, metric.LabelSize, metric.Semibold, labelBrush,
                pixelsPerDip, bounds.Width)
            : null;

        var valueSegments = BuildMetricSegments(metric, metric.Family, valueBrush, unitBrush, pixelsPerDip);
        var valueWidth = valueSegments.Sum(segment => segment.Text.Width);
        var available = bounds.Width;
        if (string.Equals(metric.LabelPosition, "left", StringComparison.OrdinalIgnoreCase) &&
            label is not null)
        {
            available = Math.Max(1, bounds.Width - label.Width - 6);
        }

        if (valueWidth > available && valueWidth > 0.5)
        {
            var factor = Math.Max(0.6, available / valueWidth);
            valueSegments = BuildMetricSegments(metric, metric.Family, valueBrush, unitBrush,
                pixelsPerDip, factor);
            valueWidth = valueSegments.Sum(segment => segment.Text.Width);
        }

        var valueHeight = valueSegments.Count == 0 ? 0 : valueSegments.Max(segment => segment.Text.Height);

        var position = (metric.LabelPosition ?? "top").Trim().ToLowerInvariant();
        double valueY;
        double valueX;
        switch (position)
        {
            case "bottom":
                valueY = bounds.Y;
                break;
            case "left":
                valueY = bounds.Y + Math.Max(0, (bounds.Height - valueHeight) / 2);
                break;
            case "none":
                valueY = bounds.Y + Math.Max(0, (bounds.Height - valueHeight) / 2);
                break;
            default:
                valueY = bounds.Y + (label?.Height ?? 0) + 1;
                break;
        }

        if (label is not null)
        {
            var labelY = position == "bottom"
                ? bounds.Y + bounds.Height - label.Height
                : bounds.Y;
            DrawAligned(dc, label, bounds, metric.LabelAlign, labelY);
        }

        if (position == "left" && label is not null)
        {
            valueX = bounds.X + Math.Min(label.Width + 6, bounds.Width * 0.55);
            valueY = bounds.Y + Math.Max(0, (bounds.Height - valueHeight) / 2);
            DrawSegments(dc, valueSegments, valueX, valueY, Math.Max(0, bounds.Right - valueX),
                metric.Align);
        }
        else
        {
            valueX = metric.Align switch
            {
                TileTextAlign.Center => bounds.X + Math.Max(0, (bounds.Width - valueWidth) / 2),
                TileTextAlign.Right => bounds.Right - valueWidth,
                _ => bounds.X,
            };
            DrawSegments(dc, valueSegments, valueX, valueY, Math.Max(0, bounds.Right - valueX),
                metric.Align);
        }

        DrawMiniGraph(dc, metric, originalBounds, graphHeight);
    }

    private static void DrawMiniGraph(DrawingContext dc, TileMetricScene metric, TileRect bounds,
        double height)
    {
        if (!metric.MiniGraph || height <= 1 || metric.GraphValues.Count < 2)
        {
            return;
        }

        var values = metric.GraphValues.Where(double.IsFinite).ToArray();
        if (values.Length < 2)
        {
            return;
        }

        var min = values.Min();
        var max = values.Max();
        if (max - min < 0.001)
        {
            max = min + 1;
        }

        var top = bounds.Bottom - height;
        var step = bounds.Width / (values.Length - 1);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var started = false;
            for (var index = 0; index < values.Length; index++)
            {
                var point = new Point(bounds.X + step * index,
                    top + height - (values[index] - min) / (max - min) * height);
                if (!started)
                {
                    context.BeginFigure(point, false, false);
                    started = true;
                }
                else
                {
                    context.LineTo(point, true, false);
                }
            }
        }

        geometry.Freeze();
        var opacity = Math.Clamp(metric.GraphOpacity, 0.05, 1);
        if (opacity < 0.999)
        {
            dc.PushOpacity(opacity);
        }

        var pen = new Pen(Brush(metric.GraphColor), Math.Clamp(metric.GraphThickness, 1, 6))
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        dc.DrawGeometry(null, pen, geometry);
        if (opacity < 0.999)
        {
            dc.Pop();
        }
    }

    private static void DrawAligned(
        DrawingContext dc,
        FormattedText text,
        TileRect bounds,
        TileTextAlign align,
        double y)
    {
        var x = align switch
        {
            TileTextAlign.Center => bounds.X + Math.Max(0, (bounds.Width - text.Width) / 2),
            TileTextAlign.Right => bounds.Right - text.Width,
            _ => bounds.X,
        };
        dc.DrawText(text, new Point(x, y));
    }

    private static List<Segment> BuildMetricSegments(
        TileMetricScene metric,
        string family,
        Brush valueBrush,
        Brush unitBrush,
        double pixelsPerDip,
        double sizeFactor = 1)
    {
        var segments = new List<Segment>(6);
        AppendValue(segments, metric.Value, metric.Unit, metric.ValueSize * sizeFactor,
            metric.UnitSize * sizeFactor, family, valueBrush, unitBrush, pixelsPerDip);
        if (!string.IsNullOrWhiteSpace(metric.SecondaryValue))
        {
            if (!string.IsNullOrWhiteSpace(metric.Separator))
            {
                segments.Add(new Segment(
                    Format(metric.Separator!, family, metric.UnitSize * sizeFactor, false, unitBrush,
                        pixelsPerDip, 10000),
                    false));
            }

            AppendValue(segments, metric.SecondaryValue, metric.SecondaryUnit,
                metric.ValueSize * sizeFactor, metric.UnitSize * sizeFactor, family, valueBrush,
                unitBrush, pixelsPerDip);
        }

        return segments;
    }

    private static void AppendValue(
        List<Segment> segments,
        string value,
        string unit,
        double valueSize,
        double unitSize,
        string family,
        Brush valueBrush,
        Brush unitBrush,
        double pixelsPerDip)
    {
        var valueText = Format(value, family, valueSize, false, valueBrush, pixelsPerDip, 100000);
        segments.Add(new Segment(valueText, false));
        if (!string.IsNullOrWhiteSpace(unit))
        {
            var unitText = Format(" " + unit, family, unitSize, false, unitBrush, pixelsPerDip, 100000);
            segments.Add(new Segment(unitText, true));
        }
    }

    private static void DrawSegments(
        DrawingContext dc,
        IReadOnlyList<Segment> segments,
        double x,
        double y,
        double availableWidth,
        TileTextAlign align)
    {
        if (segments.Count == 0)
        {
            return;
        }

        var total = segments.Sum(segment => segment.Text.Width);
        var start = align switch
        {
            TileTextAlign.Center => x + Math.Max(0, (availableWidth - total) / 2),
            TileTextAlign.Right => x + Math.Max(0, availableWidth - total),
            _ => x,
        };

        var baselineHeight = segments.Where(segment => !segment.IsUnit)
            .Select(segment => segment.Text.Height)
            .DefaultIfEmpty(segments[0].Text.Height)
            .Max();
        var cursor = start;
        foreach (var segment in segments)
        {
            var segmentY = segment.IsUnit ? y + baselineHeight - segment.Text.Height : y;
            dc.DrawText(segment.Text, new Point(cursor, segmentY));
            cursor += segment.Text.Width;
        }
    }

    private static void DrawChart(DrawingContext dc, TileChartScene chart, double pixelsPerDip)
    {
        var opacity = Math.Clamp(chart.Opacity, 0.05, 1);
        if (opacity < 0.999)
        {
            dc.PushOpacity(opacity);
        }

        try
        {
            DrawChartCore(dc, chart, pixelsPerDip);
        }
        finally
        {
            if (opacity < 0.999)
            {
                dc.Pop();
            }
        }
    }

    private static void DrawChartCore(DrawingContext dc, TileChartScene chart, double pixelsPerDip)
    {
        var bounds = chart.Bounds;
        var rect = ToRect(bounds);
        if (chart.ShowBackground || chart.ShowBorder)
        {
            var fill = chart.ShowBackground ? Brush(chart.BackgroundColor) : null;
            var pen = chart.ShowBorder ? new Pen(Brush(chart.BorderColor), 1) : null;
            dc.DrawRoundedRectangle(fill, pen, rect, 3, 3);
        }

        var titleHeight = string.IsNullOrWhiteSpace(chart.Title) || !chart.ShowLabels ? 0 : 16;
        if (titleHeight > 0)
        {
            var title = Format(chart.Title, chart.Family, 11, false, Brush(chart.LabelColor),
                pixelsPerDip, bounds.Width);
            dc.DrawText(title, new Point(bounds.X, bounds.Y + 1));
        }

        var labelWidth = chart.ShowLabels ? 48 : 0;
        var plot = new TileRect(bounds.X + 2, bounds.Y + titleHeight + 2,
            Math.Max(1, bounds.Width - labelWidth - 4),
            Math.Max(1, bounds.Height - titleHeight - 4));

        var min = chart.ScaleMin;
        var max = chart.ScaleMax;
        if (!double.IsFinite(min) || !double.IsFinite(max) || max <= min)
        {
            return;
        }

        if (chart.ShowGridLines)
        {
            var gridPen = new Pen(Brush(new TileColor(255, 255, 255, 26)), 1);
            for (var i = 1; i < 4; i++)
            {
                var y = plot.Y + plot.Height * i / 4.0;
                dc.DrawLine(gridPen, new Point(plot.X, y), new Point(plot.Right, y));
            }
        }

        foreach (var line in chart.Lines)
        {
            if (line.Kind == TileChartLineKind.Stutter)
            {
                if (line.Values.Length == 0 || line.Values[0] < min || line.Values[0] > max)
                {
                    continue;
                }

                var y = plot.Bottom - (line.Values[0] - min) / (max - min) * plot.Height;
                var pen = new Pen(Brush(line.Color), 1) { DashStyle = DashStyles.Dash };
                dc.DrawLine(pen, new Point(plot.X, y), new Point(plot.Right, y));
                continue;
            }

            var values = line.Values;
            if (values.Length < 2)
            {
                continue;
            }

            var step = plot.Width / (values.Length - 1);
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                var started = false;
                for (var i = 0; i < values.Length; i++)
                {
                    var value = values[i];
                    if (!double.IsFinite(value))
                    {
                        started = false;
                        continue;
                    }

                    var normalized = Math.Clamp((value - min) / (max - min), 0, 1);
                    var point = new Point(plot.X + step * i, plot.Bottom - normalized * plot.Height);
                    if (!started)
                    {
                        context.BeginFigure(point, false, false);
                        started = true;
                    }
                    else
                    {
                        context.LineTo(point, true, false);
                    }
                }
            }

            geometry.Freeze();
            var linePen = new Pen(Brush(line.Color), Math.Clamp(chart.LineThickness, 1, 8))
            {
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            linePen.Freeze();
            dc.DrawGeometry(null, linePen, geometry);
        }

        if (chart.ShowLabels)
        {
            var unit = string.IsNullOrWhiteSpace(chart.Unit) ? string.Empty : chart.Unit!.Trim();
            var maxText = Format(max.ToString("0.#", CultureInfo.InvariantCulture) + unit,
                chart.Family, 10, false, Brush(chart.LabelColor), pixelsPerDip, 100);
            var minText = Format(min.ToString("0.#", CultureInfo.InvariantCulture) + unit,
                chart.Family, 10, false, Brush(chart.LabelColor), pixelsPerDip, 100);
            dc.DrawText(maxText, new Point(bounds.Right - maxText.Width, bounds.Y + titleHeight));
            dc.DrawText(minText, new Point(bounds.Right - minText.Width, bounds.Bottom - minText.Height));
        }
    }

    private static void DrawBar(DrawingContext dc, TileBarScene bar, double pixelsPerDip)
    {
        var bounds = bar.Bounds;
        var barRect = bar.Orientation == TileBarOrientation.Horizontal
            ? ToRect(bar.BarBounds)
            : new Rect(bounds.X, bounds.Y, Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));

        if (bar.Orientation == TileBarOrientation.Horizontal)
        {
            if (bar.ShowValue)
            {
                if (!string.IsNullOrWhiteSpace(bar.Label))
                {
                    var label = Format(bar.Label, bar.Family, 12, false, Brush(bar.LabelColor),
                        pixelsPerDip, bounds.Width);
                    dc.DrawText(label, new Point(bounds.X, bounds.Y));
                }

                if (!string.IsNullOrWhiteSpace(bar.Value))
                {
                    var unit = string.IsNullOrWhiteSpace(bar.Unit) ? string.Empty : " " + bar.Unit.Trim();
                    var value = Format(bar.Value + unit, bar.Family, 12, false, Brush(bar.ValueColor),
                        pixelsPerDip, bounds.Width);
                    dc.DrawText(value, new Point(bounds.Right - value.Width, bounds.Y));
                }
            }

            var radius = Math.Max(0, barRect.Height / 2);
            dc.DrawRoundedRectangle(Brush(bar.Track), null, barRect, radius, radius);
            var width = Math.Max(0, barRect.Width * Math.Clamp(bar.Fraction, 0, 1));
            if (width > 0.5)
            {
                dc.DrawRoundedRectangle(Brush(bar.Fill), null,
                    new Rect(barRect.X, barRect.Y, width, barRect.Height), radius, radius);
            }

            return;
        }

        if (bar.IsChip)
        {
            var chipRect = ToRect(bar.BarBounds);
            dc.DrawRoundedRectangle(Brush(bar.Track), null, chipRect, 4, 4);
            var lineWidth = Math.Max(0, chipRect.Width * Math.Clamp(bar.Fraction, 0, 1));
            if (lineWidth > 0.5)
            {
                var line = new Rect(chipRect.X, chipRect.Y + chipRect.Height / 2 - 1,
                    lineWidth, 2);
                dc.DrawRoundedRectangle(Brush(bar.Fill), null, line, 1, 1);
            }

            if (!string.IsNullOrWhiteSpace(bar.Label))
            {
                var label = Format(bar.Label, bar.Family, 12, false, Brush(bar.LabelColor),
                    pixelsPerDip, bounds.Width);
                dc.DrawText(label, new Point(bounds.X + (bounds.Width - label.Width) / 2,
                    chipRect.Bottom + 2));
            }

            return;
        }

        if (bar.ShowValue)
        {
            var unit = string.IsNullOrWhiteSpace(bar.Unit) ? string.Empty : " " + bar.Unit.Trim();
            var value = Format(bar.Value + unit, bar.Family, 10, false, Brush(bar.ValueColor),
                pixelsPerDip, bounds.Width);
            dc.DrawText(value, new Point(bounds.X + (bounds.Width - value.Width) / 2, bounds.Y - 2));
        }

        var trackRect = new Rect(bounds.X, bounds.Y, Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
        dc.DrawRoundedRectangle(Brush(bar.Track), null, trackRect, 2, 2);
        var fillHeight = trackRect.Height * Math.Clamp(bar.Fraction, 0, 1);
        if (fillHeight > 0.5)
        {
            dc.DrawRoundedRectangle(Brush(bar.Fill), null,
                new Rect(trackRect.X, trackRect.Bottom - fillHeight, trackRect.Width, fillHeight), 2, 2);
        }

        if (!string.IsNullOrWhiteSpace(bar.Label))
        {
            var label = Format(bar.Label, bar.Family, 10, false, Brush(bar.LabelColor),
                pixelsPerDip, bounds.Width);
            dc.DrawText(label, new Point(bounds.X + (bounds.Width - label.Width) / 2,
                bounds.Bottom + 1));
        }
    }

    private static void DrawGauge(DrawingContext dc, TileGaugeScene gauge, double pixelsPerDip)
    {
        var bounds = gauge.Bounds;
        var labelHeight = string.IsNullOrWhiteSpace(gauge.Label) ? 0 : 18;
        var size = Math.Min(bounds.Width, Math.Max(1, bounds.Height - labelHeight));
        var radius = Math.Max(2, (size - gauge.Thickness) / 2);
        var center = new Point(bounds.X + bounds.Width / 2, bounds.Y + size / 2);
        var start = gauge.StartAngle * Math.PI / 180.0;
        var sweep = Math.Clamp(gauge.SweepAngle, 1, 360) * Math.PI / 180.0;

        DrawArc(dc, center, radius, start, sweep, new Pen(Brush(gauge.Track), gauge.Thickness));

        var fraction = Math.Clamp(gauge.Fraction, 0, 1);
        if (fraction > 0.001)
        {
            DrawArc(dc, center, radius, start, sweep * fraction,
                new Pen(Brush(gauge.Fill), gauge.Thickness)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                });
        }

        if (gauge.ShowValue)
        {
            var unit = string.IsNullOrWhiteSpace(gauge.Unit) ? string.Empty : " " + gauge.Unit.Trim();
            var value = Format(gauge.Value + unit, gauge.Family, 14, true, Brush(gauge.ValueColor),
                pixelsPerDip, size);
            dc.DrawText(value, new Point(center.X - value.Width / 2, center.Y - value.Height / 2));
        }

        if (!string.IsNullOrWhiteSpace(gauge.Label))
        {
            var label = Format(gauge.Label, gauge.Family, 10, false, Brush(gauge.LabelColor),
                pixelsPerDip, bounds.Width);
            dc.DrawText(label, new Point(bounds.X + (bounds.Width - label.Width) / 2,
                bounds.Y + size + 2));
        }
    }

    private static void DrawArc(
        DrawingContext dc,
        Point center,
        double radius,
        double start,
        double sweep,
        Pen pen)
    {
        pen.Freeze();
        if (sweep >= Math.PI * 2 - 0.001)
        {
            dc.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        var end = start + sweep;
        var startPoint = new Point(center.X + radius * Math.Cos(start), center.Y + radius * Math.Sin(start));
        var endPoint = new Point(center.X + radius * Math.Cos(end), center.Y + radius * Math.Sin(end));
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(startPoint, false, false);
            context.ArcTo(endPoint, new Size(radius, radius), 0, sweep > Math.PI,
                SweepDirection.Clockwise, true, false);
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private static void DrawImage(DrawingContext dc, TileImageScene image)
    {
        var source = LoadImage(image.DataUri);
        if (source is null)
        {
            return;
        }

        var bounds = image.Bounds;
        var fit = (image.Fit ?? "contain").Trim().ToLowerInvariant();
        double width = bounds.Width;
        double height = bounds.Height;
        if (fit != "stretch" && source.PixelWidth > 0 && source.PixelHeight > 0)
        {
            var imageRatio = (double)source.PixelWidth / source.PixelHeight;
            var boxRatio = bounds.Width / Math.Max(1, bounds.Height);
            if (fit == "cover" ? imageRatio > boxRatio : imageRatio < boxRatio)
            {
                height = bounds.Width / imageRatio;
            }
            else
            {
                width = bounds.Height * imageRatio;
            }
        }

        var x = bounds.X + (bounds.Width - width) / 2;
        var y = bounds.Y + (bounds.Height - height) / 2;
        var opacity = Math.Clamp(image.Opacity, 0, 1);
        if (opacity < 0.999)
        {
            dc.PushOpacity(opacity);
        }

        dc.DrawImage(source, new Rect(x, y, width, height));
        if (opacity < 0.999)
        {
            dc.Pop();
        }
    }

    private static void DrawIcon(DrawingContext dc, TileIconScene icon, double pixelsPerDip)
    {
        if (string.IsNullOrEmpty(icon.Glyph))
        {
            return;
        }

        var text = Format(icon.Glyph, icon.Family, icon.Size, false, Brush(icon.Color),
            pixelsPerDip, icon.Bounds.Width);
        dc.DrawText(text, new Point(icon.Bounds.X + (icon.Bounds.Width - text.Width) / 2,
            icon.Bounds.Y + (icon.Bounds.Height - text.Height) / 2));
    }

    private static void DrawHeatmap(DrawingContext dc, TileHeatmapScene heatmap, double pixelsPerDip)
    {
        var bounds = heatmap.Bounds;
        if (!string.IsNullOrWhiteSpace(heatmap.Title))
        {
            var title = Format(heatmap.Title, "Segoe UI", 10, false, Brush(heatmap.LabelColor),
                pixelsPerDip, bounds.Width);
            dc.DrawText(title, new Point(bounds.X, bounds.Y));
        }

        foreach (var cell in heatmap.Cells)
        {
            var color = Blend(heatmap.EmptyColor, heatmap.CellColor, Math.Clamp(cell.Fraction, 0, 1));
            var rect = ToRect(cell.Bounds);
            dc.DrawRectangle(Brush(color), null, rect);
        }
    }

    private static BitmapSource? LoadImage(string dataUri)
    {
        if (string.IsNullOrWhiteSpace(dataUri))
        {
            return null;
        }

        if (ImageCache.TryGetValue(dataUri, out var cached))
        {
            return cached;
        }

        BitmapSource? source = null;
        try
        {
            var comma = dataUri.IndexOf(',');
            if (comma > 0 && dataUri[..comma].Contains("base64", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = Convert.FromBase64String(dataUri[(comma + 1)..]);
                using var stream = new MemoryStream(bytes, writable: false);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                source = bitmap;
            }
        }
        catch (Exception)
        {
            source = null;
        }

        if (ImageCache.Count > 160)
        {
            ImageCache.Clear();
        }

        ImageCache[dataUri] = source;
        return source;
    }

    private static FormattedText Format(
        string text,
        string family,
        double size,
        bool semibold,
        Brush brush,
        double pixelsPerDip,
        double maxWidth)
    {
        var typeface = Typeface(family, semibold);
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            Math.Max(1, size),
            brush,
            pixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            Trimming = TextTrimming.CharacterEllipsis,
        };
        return formatted;
    }

    private static Typeface Typeface(string family, bool semibold)
    {
        var key = new TypefaceKey(family, semibold);
        if (TypefaceCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var typeface = new Typeface(
            new FontFamily(string.IsNullOrWhiteSpace(family) ? "Segoe UI" : family),
            FontStyles.Normal,
            semibold ? FontWeights.SemiBold : FontWeights.Normal,
            FontStretches.Normal);
        TypefaceCache[key] = typeface;
        return typeface;
    }

    private static string LimitLines(string text, int maxLines)
    {
        if (maxLines <= 0 || text.IndexOf('\n') < 0)
        {
            return text;
        }

        var lines = text.Split('\n');
        return lines.Length <= maxLines ? text : string.Join('\n', lines.Take(maxLines));
    }

    private static SolidColorBrush Brush(TileColor color)
    {
        var key = ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
        if (BrushCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var brush = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
        brush.Freeze();
        BrushCache[key] = brush;
        return brush;
    }

    private static TileColor Blend(TileColor from, TileColor to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return new TileColor(
            (byte)Math.Round(from.R + (to.R - from.R) * amount),
            (byte)Math.Round(from.G + (to.G - from.G) * amount),
            (byte)Math.Round(from.B + (to.B - from.B) * amount),
            (byte)Math.Round(from.A + (to.A - from.A) * amount));
    }

    private static Rect ToRect(TileRect rect)
        => new(rect.X, rect.Y, Math.Max(0, rect.Width), Math.Max(0, rect.Height));

    private readonly record struct Segment(FormattedText Text, bool IsUnit);
    private readonly record struct TypefaceKey(string Family, bool Semibold);
}
