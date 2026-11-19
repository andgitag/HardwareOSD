using System.Windows;
using System.Windows.Media;
using HardwareOSD.Core.Input;
using HardwareOSD.Core.Overlay;
using HardwareOSD.Core.TileOsd;

namespace HardwareOSD.App.Overlay;

/// <summary>
/// Draws an <see cref="OverlayVisual"/>. The element allocates nothing between frames beyond the
/// geometries it must rebuild for sparklines; all brushes, pens and fonts come from the frozen theme.
/// </summary>
public sealed class OverlaySurface : FrameworkElement
{
    private OverlayVisual _visual = OverlayVisual.Empty;
    private TileScene _tileScene = TileScene.Empty;
    private double _tileScale = 1;
    private OverlayTheme? _theme;
    private readonly Dictionary<GraphPenKey, Pen> _graphPens = new();
    private FormattedText? _moveHint;
    private string? _moveHintText;
    private readonly Brush _moveHintBackground = CreateMoveHintBackground();
    private readonly Pen _moveBorder = CreateMoveBorder();
    private static readonly Pen ModifierBorder = CreateModifierBorder();
    private static readonly Brush ModifierFill = CreateModifierFill();
    private Point? _modifierHover;

    /// <summary>Move mode makes the whole surface hit-testable so the user can drag the overlay.</summary>
    public bool IsInteractive { get; set; }

    public bool IsMoveMode { get; private set; }

    private bool _modifierInteractive;

    public void SetMoveMode(bool enabled, string? hint)
    {
        IsMoveMode = enabled;
        // The surface is hit-test invisible while click-through is active; move mode must opt in,
        // otherwise the override below is never consulted.
        IsHitTestVisible = enabled || _modifierInteractive;
        if (!string.Equals(_moveHintText, hint, StringComparison.Ordinal))
        {
            _moveHintText = hint;
            _moveHint = null;
        }

        InvalidateVisual();
    }

    /// <summary>
    /// Enables hit-testing only while the configured modifier is held over the overlay. The window
    /// style still keeps WS_EX_NOACTIVATE, so the overlay cannot steal focus from the game.
    /// </summary>
    public void SetModifierInteraction(bool enabled)
    {
        if (_modifierInteractive == enabled)
        {
            return;
        }

        _modifierInteractive = enabled;
        IsHitTestVisible = enabled || IsMoveMode;
        if (!enabled)
        {
            _modifierHover = null;
        }

        InvalidateVisual();
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
    {
        if (!ModifierDragPolicy.AllowsSurfaceHitTest(IsInteractive, IsMoveMode, _modifierInteractive))
        {
            return null;
        }

        var bounds = new Rect(0, 0, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        return bounds.Contains(hitTestParameters.HitPoint)
            ? new PointHitTestResult(this, hitTestParameters.HitPoint)
            : null;
    }

    public OverlaySurface()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        RenderOptions.SetClearTypeHint(this, ClearTypeHint.Auto);
        MouseMove += (_, e) =>
        {
            if (_modifierInteractive && !IsMoveMode)
            {
                _modifierHover = e.GetPosition(this);
                InvalidateVisual();
            }
        };
        MouseLeave += (_, _) =>
        {
            if (_modifierHover is not null)
            {
                _modifierHover = null;
                InvalidateVisual();
            }
        };
    }

    public OverlayVisual Visual => _visual;

    public void SetContent(OverlayVisual visual, OverlayTheme theme)
    {
        _visual = visual;
        _tileScene = TileScene.Empty;
        _theme = theme;
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>Shows a tile design scene. Passing <see cref="TileScene.Empty"/> leaves the row row visual in place.</summary>
    public void SetTileContent(TileScene scene, double scale, OverlayTheme theme,
        Action<DrawingContext, TileRect>? classicRows = null)
    {
        _tileScene = scene;
        _tileScale = scale <= 0 ? 1 : scale;
        ClassicRowsRenderer = classicRows;
        if (!scene.IsEmpty)
        {
            _visual = OverlayVisual.Empty;
        }

        _theme = theme;
        InvalidateMeasure();
        InvalidateVisual();
    }

    public Action<DrawingContext, TileRect>? ClassicRowsRenderer { get; set; }

    public TileScene TileScene => _tileScene;

    public bool IsTile => !_tileScene.IsEmpty;

    protected override Size MeasureOverride(Size availableSize)
        => IsTile
            ? new Size(Math.Max(1, _tileScene.Width * _tileScale),
                Math.Max(1, _tileScene.Height * _tileScale))
            : new(_visual.Width, _visual.Height);

    protected override void OnRender(DrawingContext dc)
    {
        var theme = _theme;
        if (theme is null)
        {
            return;
        }

        if (IsTile)
        {
            TileSceneRenderer.Draw(dc, _tileScene, VisualTreeHelper.GetDpi(this).PixelsPerDip,
                _tileScale, ClassicRowsRenderer);
            if (IsMoveMode)
            {
                DrawMoveMode(dc, _tileScene.Width * _tileScale, _tileScene.Height * _tileScale, theme);
            }
            else if (_modifierInteractive)
            {
                DrawModifierHighlight(dc, _tileScene.Width * _tileScale, _tileScene.Height * _tileScale);
            }

            return;
        }

        OverlayVisualRenderer.Draw(dc, _visual, theme,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (IsMoveMode)
        {
            DrawMoveMode(dc, _visual.Width, _visual.Height, theme);
        }

        if (_modifierInteractive && !IsMoveMode)
        {
            DrawModifierHighlight(dc, _visual.Width, _visual.Height);
        }
    }

    private void DrawModifierHighlight(DrawingContext dc, double width, double height)
    {
        var rect = new Rect(1.0, 1.0, Math.Max(1, width - 2), Math.Max(1, height - 2));
        dc.DrawRoundedRectangle(null, ModifierBorder, rect, 4, 4);

        if (_modifierHover is not { } hover || IsTile)
        {
            return;
        }

        foreach (var row in _visual.Rows)
        {
            if (hover.Y < row.Top || hover.Y > row.Top + Math.Max(row.ContentHeight, row.Height))
            {
                continue;
            }

            foreach (var cell in row.Cells)
            {
                var cellRect = new Rect(cell.Left - 2, row.Top, cell.Width + 4,
                    Math.Max(row.ContentHeight, row.Height));
                if (cellRect.Contains(hover))
                {
                    dc.DrawRoundedRectangle(ModifierFill, ModifierBorder, cellRect, 3, 3);
                    return;
                }
            }

            var rowRect = new Rect(2, row.Top, Math.Max(1, width - 4),
                Math.Max(row.ContentHeight, row.Height));
            dc.DrawRoundedRectangle(ModifierFill, null, rowRect, 3, 3);
            return;
        }
    }

    private void DrawMoveMode(DrawingContext dc, double surfaceWidth, double surfaceHeight, OverlayTheme theme)
    {
        var border = new Rect(0.75, 0.75, Math.Max(1, surfaceWidth - 1.5), Math.Max(1, surfaceHeight - 1.5));
        dc.DrawRoundedRectangle(null, _moveBorder, border,
            Math.Max(1, theme.CornerRadius), Math.Max(1, theme.CornerRadius));

        if (string.IsNullOrWhiteSpace(_moveHintText))
        {
            return;
        }

        if (_moveHint is null)
        {
            _moveHint = new FormattedText(
                _moveHintText,
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                theme.ValueTypeface,
                Math.Max(10, theme.UnitSize),
                theme.ValueBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
        }

        var hintWidth = _moveHint.Width + 18;
        var hintHeight = _moveHint.Height + 10;
        var x = Math.Max(4, (surfaceWidth - hintWidth) / 2);
        var y = Math.Max(4, surfaceHeight - hintHeight - 8);
        var hintRect = new Rect(x, y, hintWidth, hintHeight);
        dc.DrawRoundedRectangle(_moveHintBackground, null, hintRect, 4, 4);
        dc.DrawText(_moveHint, new Point(x + 9, y + 5));
    }

    private static Brush CreateMoveHintBackground()
    {
        var brush = new SolidColorBrush(Color.FromArgb(215, 0, 0, 0));
        brush.Freeze();
        return brush;
    }

    private static Pen CreateMoveBorder()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x4D)), 1.4)
        {
            DashStyle = DashStyles.Dash,
        };
        pen.Freeze();
        return pen;
    }

    private static Pen CreateModifierBorder()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x6F, 0xD8, 0xCC)), 1.6)
        {
            DashStyle = DashStyles.Dash,
        };
        pen.Freeze();
        return pen;
    }

    private static Brush CreateModifierFill()
    {
        var brush = new SolidColorBrush(Color.FromArgb(34, 0x6F, 0xD8, 0xCC));
        brush.Freeze();
        return brush;
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

    private void DrawSparkline(DrawingContext dc, VisualSparkline graph)
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

        // Map the valid samples across the full graph width so the line grows in naturally while
        // the ring buffer is still filling, instead of appearing as a short dash on the right.
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

        if (hasLine)
        {
            line.Freeze();
            var pen = GetGraphPen(graph.Stroke, graph.Thickness);
            dc.DrawGeometry(null, pen, line);
        }
    }

    private readonly record struct GraphPenKey(Brush Brush, double Thickness);

    private Pen GetGraphPen(Brush brush, double thickness)
    {
        var key = new GraphPenKey(brush, Math.Round(Math.Clamp(thickness, 0.5, 6), 1));
        if (_graphPens.TryGetValue(key, out var pen))
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
        _graphPens[key] = pen;
        return pen;
    }
}
