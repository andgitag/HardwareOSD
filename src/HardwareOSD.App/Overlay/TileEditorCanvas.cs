using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using HardwareOSD.Core.TileOsd;

namespace HardwareOSD.App.Overlay;

/// <summary>
/// The builder's design surface. It draws the same <see cref="TileScene"/> the overlay draws and
/// adds the editor layer: grid, selection outline, resize handles and pointer editing. Geometry is
/// read from the scene builder's node map, so a tile's hit box is exactly the rectangle it renders
/// in at that moment.
/// </summary>
public sealed class TileEditorCanvas : FrameworkElement
{
    private enum Mode
    {
        None,
        Move,
        ResizeWidth,
        ResizeHeight,
        ResizeCorner,
    }

    private static readonly Pen ScopePen = FrozenPen(Color.FromArgb(0x50, 0x6F, 0xD8, 0xCC), 1);
    private static readonly Brush EmptyTextBrush = Frozen(Color.FromArgb(0x99, 0xF4, 0xF7, 0xFA));
    private static readonly Brush HandleFill = Frozen(Colors.White);
    private static readonly Brush CanvasFallback = Frozen(Color.FromRgb(0x14, 0x17, 0x1B));
    private static readonly Pen SelectionPen = FrozenPen(Color.FromRgb(0x4C, 0x8D, 0xFF), 2);
    private static readonly Pen HandlePen = FrozenPen(Color.FromRgb(0x4C, 0x8D, 0xFF), 1.5);

    private TileScene _scene = TileScene.Empty;
    private TileDesignDocument? _design;
    private Dictionary<string, TileRect> _bounds = new(StringComparer.Ordinal);
    private Dictionary<string, TileNode> _byId = new(StringComparer.Ordinal);
    private TileNode? _scope;
    private TileNode? _selected;
    private string? _selectedId;

    private Mode _mode;
    private Point _startDesign;
    private TileRect _startRect;
    private Rect? _previewRect;
    private double _startX;
    private double _startY;
    private double _startWidth;
    private double _startHeight;
    private bool _changed;
    private bool _duplicateOnDrag;
    private bool _duplicated;
    private Rect? _dropMarker;

    public TileEditorCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        SnapsToDevicePixels = true;
        AutomationProperties.SetAutomationId(this, "OsdEditorCanvas");
        AutomationProperties.SetName(this, "OSD design canvas");
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    public double Zoom { get; private set; } = 1;

    /// <summary>Hint drawn on an empty canvas. The host supplies the localized text.</summary>
    public string EmptyHint { get; set; } = string.Empty;

    public Action<DrawingContext, TileRect>? ClassicRowsRenderer { get; set; }

    public string? SelectedId => _selectedId;

    public event EventHandler? SelectionChanged;

    /// <summary>Raised before the first geometry change of a gesture so the host can snapshot undo.</summary>
    public event EventHandler? InteractionStarted;

    /// <summary>Raised after every geometry change. Final is true on mouse-up.</summary>
    public event EventHandler<bool>? GeometryChanged;

    public event EventHandler? ContainerEntered;

    public event EventHandler? DeleteRequested;

    public event Action<string>? Status;

    public TileNode? SelectedNode => _selected;

    public TileNode? ScopeNode => _scope;

    public void SetDocument(TileDesignDocument design, TileNode? scope, string? selectedId)
    {
        _design = design;
        _scope = scope ?? design.Root;
        _selectedId = selectedId;
        RebuildIndex();
        _selected = _selectedId is null ? null : _byId.GetValueOrDefault(_selectedId);
        InvalidateVisual();
    }

    public void SetScene(TileScene scene, IDictionary<string, TileRect> bounds)
    {
        _scene = scene;
        _bounds = new Dictionary<string, TileRect>(bounds, StringComparer.Ordinal);
        InvalidateVisual();
    }

    public void SetSelection(string? nodeId)
    {
        _selectedId = nodeId;
        _selected = nodeId is null ? null : _byId.GetValueOrDefault(nodeId);
        InvalidateVisual();
    }

    public void SetScope(TileNode scope)
    {
        _scope = scope;
        _selectedId = null;
        _selected = null;
        InvalidateVisual();
    }

    public void SetZoom(double zoom)
    {
        Zoom = Math.Clamp(zoom, 0.15, 3);
        if (_design is null)
        {
            return;
        }

        Width = Math.Max(1, _design.CanvasWidth * Zoom);
        Height = Math.Max(1, _design.CanvasHeight * Zoom);
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>
    /// Shows where a dragged widget, source or existing tile will land. The outline is the tile's
    /// snapped footprint on the placement grid, so the drop point is visible before releasing.
    /// </summary>
    public void ShowDropMarker(double x, double y, double width, double height)
    {
        var rect = new Rect(x, y, Math.Max(1, width), Math.Max(1, height));
        if (_dropMarker == rect)
        {
            return;
        }

        _dropMarker = rect;
        InvalidateVisual();
    }

    public void ClearDropMarker()
    {
        if (_dropMarker is null)
        {
            return;
        }

        _dropMarker = null;
        InvalidateVisual();
    }

    public Point ToDesign(Point surfacePoint)
        => new(surfacePoint.X / Zoom, surfacePoint.Y / Zoom);

    public TileRect? RectOf(TileNode node)
    {
        if (_design is null)
        {
            return null;
        }

        var path = TileDocumentEditor.PathOf(_design, node.NodeId);
        return path is not null && _bounds.TryGetValue(path, out var rect) ? rect : null;
    }

    /// <summary>
    /// Deepest visible container under a design-space point. Used by the builder's drag-and-drop to
    /// reparent a node into a nested group without entering it first.
    /// </summary>
    public TileNode? FindContainerAt(Point designPoint)
    {
        if (_design is null)
        {
            return null;
        }

        TileNode? best = null;
        var bestArea = double.MaxValue;
        foreach (var node in _design.Root.Descendants().Prepend(_design.Root))
        {
            if (node.NodeKind != TileNodeKind.Panel || node.Visible == false)
            {
                continue;
            }

            if (RectOf(node) is not { } rect || rect.IsEmpty || !Contains(rect, designPoint))
            {
                continue;
            }

            var area = rect.Width * rect.Height;
            if (area < bestArea)
            {
                best = node;
                bestArea = area;
            }
        }

        return best;
    }

    private void RebuildIndex()
    {
        _byId = new Dictionary<string, TileNode>(StringComparer.Ordinal);
        if (_design is null)
        {
            return;
        }

        foreach (var node in _design.Root.Descendants())
        {
            if (node.NodeId is { Length: > 0 } id)
            {
                _byId[id] = node;
            }
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_design is null)
        {
            return;
        }

        DrawCanvasSurface(dc, ActualWidth, ActualHeight);

        dc.PushTransform(new ScaleTransform(Zoom, Zoom));
        TileSceneRenderer.Draw(dc, _scene, VisualTreeHelper.GetDpi(this).PixelsPerDip, 1.0,
            ClassicRowsRenderer);

        // The scope border makes it obvious that clicks outside it are ignored while editing a
        // container.
        if (_scope is not null && _scope.NodeId != _design.Root.NodeId &&
            RectOf(_scope) is { } scopeRect)
        {
            dc.DrawRectangle(null, ScopePen, ToRect(scopeRect));
        }

        // Selection and handles live in the same transform as the rendered scene, so the outline
        // always sits exactly on the tile it describes.
        if (_selected is not null && RectOf(_selected) is { } rect && !rect.IsEmpty)
        {
            DrawSelection(dc, rect);
        }

        // A resize gesture previews a snapped outline and only commits on release, so the tile
        // content does not reflow under the pointer.
        if (_previewRect is { } preview)
        {
            var fill = new SolidColorBrush(Color.FromArgb(0x22, 0x4C, 0x8D, 0xFF));
            fill.Freeze();
            dc.DrawRectangle(fill, new Pen(SelectionPen.Brush, 2 / Math.Max(0.15, Zoom)), preview);
        }

        if (_dropMarker is { } drop)
        {
            var outline = new Pen(Frozen(Color.FromArgb(0xCC, 0x6F, 0xD8, 0xCC)),
                1.5 / Math.Max(0.15, Zoom)) { DashStyle = DashStyles.Dash };
            outline.Freeze();
            var fill = Frozen(Color.FromArgb(0x1E, 0x6F, 0xD8, 0xCC));
            dc.DrawRectangle(fill, outline, drop);

            // A cross at the snapped origin shows the exact grid cell the drop uses.
            var arm = 6 / Math.Max(0.15, Zoom);
            var pen = FrozenPen(Color.FromArgb(0xCC, 0x6F, 0xD8, 0xCC), 1 / Math.Max(0.15, Zoom));
            dc.DrawLine(pen, new Point(drop.Left - arm, drop.Top), new Point(drop.Left + arm, drop.Top));
            dc.DrawLine(pen, new Point(drop.Left, drop.Top - arm), new Point(drop.Left, drop.Top + arm));
        }

        dc.Pop();

        if (_scope is null || _scope.Children.Count == 0)
        {
            DrawEmptyHint(dc);
        }
    }

    private void DrawEmptyHint(DrawingContext dc)
    {
        var text = new FormattedText(
            string.IsNullOrWhiteSpace(EmptyHint)
                ? "Drag a widget from the palette onto the canvas"
                : EmptyHint,
            CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 13, EmptyTextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(40, ActualWidth - 40),
            TextAlignment = TextAlignment.Center,
        };
        dc.DrawText(text, new Point(20, Math.Max(20, ActualHeight / 2 - text.Height / 2)));
    }

    /// <summary>
    /// Fills the design surface. With the grid enabled this is the fine checkerboard CapFrameX
    /// uses as its placement grid; with it disabled it is a flat preview surface.
    /// </summary>
    private void DrawCanvasSurface(DrawingContext dc, double width, double height)
    {
        if (!_design!.ShowGrid || _design.GridSize <= 0)
        {
            dc.DrawRectangle(CanvasFallback, null, new Rect(0, 0, width, height));
            return;
        }

        // Cells are drawn in design pixels so the grid lines up with the snap step.
        var cell = Math.Max(1, _design.GridSize);
        dc.DrawRectangle(Frozen(Color.FromRgb(0x14, 0x17, 0x1B)), null, new Rect(0, 0, width, height));
        var light = Frozen(Color.FromRgb(0x1B, 0x1F, 0x25));
        var columns = (int)Math.Ceiling(width / cell);
        var rows = (int)Math.Ceiling(height / cell);
        for (var row = 0; row < rows; row++)
        {
            for (var column = row % 2; column < columns; column += 2)
            {
                dc.DrawRectangle(light, null,
                    new Rect(column * cell, row * cell, cell, cell));
            }
        }
    }

    private void DrawSelection(DrawingContext dc, TileRect rect)
    {
        var box = ToRect(rect);
        var scale = Math.Max(0.15, Zoom);

        // Two-pixel outline and pill handles, matching the reference editor. Sizes are divided by
        // the zoom so they stay constant on screen at any scale.
        var pen = new Pen(SelectionPen.Brush, 2 / scale);
        pen.Freeze();
        dc.DrawRectangle(null, pen, box);

        const double handleLong = 22;
        const double handleShort = 10;
        var radius = handleShort / 2;
        var border = new Pen(HandlePen.Brush, 1.5 / scale);
        border.Freeze();

        foreach (var handle in HandleBoxes(box, handleLong, handleShort))
        {
            dc.DrawRoundedRectangle(HandleFill, border, handle, radius, radius);
        }
    }

    private static IEnumerable<Rect> HandleBoxes(Rect box, double longSide, double shortSide)
    {
        // Keep both handles fully inside the tile. Drawing them half outside the tile made the
        // outer half unclickable when the tile touched the canvas edge, because the canvas clips
        // to its bounds. The hit-test tolerance still accepts a click on the visible edge.
        yield return new Rect(box.Right - shortSide, box.Top + box.Height / 2 - longSide / 2,
            shortSide, longSide);
        yield return new Rect(box.Left + box.Width / 2 - longSide / 2, box.Bottom - shortSide,
            longSide, shortSide);
    }

    private static Rect ToRect(TileRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_design is null || _scope is null)
        {
            return;
        }

        Focus();
        var point = ToDesign(e.GetPosition(this));
        _previewRect = null;

        if (_selected is not null && RectOf(_selected) is { } selectedRect)
        {
            var handle = HitHandle(selectedRect, point);
            if (handle != Mode.None)
            {
                if (_selected.Locked == true)
                {
                    Status?.Invoke("This tile is locked. Clear the lock in the Tile tab to edit it.");
                    return;
                }

                BeginGesture(handle, point, selectedRect);
                e.Handled = true;
                return;
            }
        }

        var hit = HitTestScope(point);
        if (hit is null)
        {
            if (_selected is not null)
            {
                Select(null);
            }

            return;
        }

        if (!string.Equals(hit.NodeId, _selectedId, StringComparison.Ordinal))
        {
            Select(hit.NodeId);
        }

        if (e.ClickCount >= 2 && TileDocumentEditor.HasEditableChildren(hit))
        {
            _scope = TileDocumentEditor.ResolveEditableScope(hit);
            Select(null);
            ContainerEntered?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (TileDocumentEditor.HasEditableChildren(hit) &&
            Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            _scope = TileDocumentEditor.ResolveEditableScope(hit);
            Select(null);
            ContainerEntered?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (hit.Locked == true)
        {
            Status?.Invoke("This tile is locked. Clear the lock in the Tile tab to edit it.");
            e.Handled = true;
            return;
        }

        var rect = RectOf(hit) ?? default;
        BeginGesture(Mode.Move, point, rect);
        _duplicateOnDrag = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        e.Handled = true;
    }

    private void BeginGesture(Mode mode, Point point, TileRect rect)
    {
        _mode = mode;
        _startDesign = point;
        _startRect = rect;
        _startX = _selected!.X ?? rect.X;
        _startY = _selected.Y ?? rect.Y;
        _startWidth = _selected.FrameWidth ?? _selected.Width ?? rect.Width;
        _startHeight = _selected.FrameHeight ?? _selected.Height ?? rect.Height;
        _changed = false;
        _duplicated = false;
        Mouse.Capture(this);
    }

    private Mode HitHandle(TileRect rect, Point point)
    {
        var tolerance = 12 / Math.Max(0.15, Zoom);
        var box = ToRect(rect);
        var corner = new Point(box.Right, box.Bottom);
        if (Distance(point, corner) <= tolerance * 1.4)
        {
            return Mode.ResizeCorner;
        }

        if (Math.Abs(point.X - box.Right) <= tolerance &&
            point.Y >= box.Top && point.Y <= box.Bottom)
        {
            return Mode.ResizeWidth;
        }

        if (Math.Abs(point.Y - box.Bottom) <= tolerance &&
            point.X >= box.Left && point.X <= box.Right)
        {
            return Mode.ResizeHeight;
        }

        return Mode.None;
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private TileNode? HitTestScope(Point point)
    {
        if (_scope is null || _design is null)
        {
            return null;
        }

        for (var index = _scope.Children.Count - 1; index >= 0; index--)
        {
            var child = _scope.Children[index];
            if (RectOf(child) is { } rect && Contains(rect, point))
            {
                return child;
            }
        }

        return null;
    }

    private static bool Contains(TileRect rect, Point point)
        => point.X >= rect.X && point.X <= rect.Right && point.Y >= rect.Y && point.Y <= rect.Bottom;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_design is null)
        {
            return;
        }

        var point = ToDesign(e.GetPosition(this));

        if (_mode == Mode.None)
        {
            UpdateCursor(point);
            return;
        }

        if (_selected is null)
        {
            return;
        }

        var dx = point.X - _startDesign.X;
        var dy = point.Y - _startDesign.Y;
        if (!_changed && Math.Abs(dx) < 1 && Math.Abs(dy) < 1)
        {
            return;
        }

        if (!_changed)
        {
            if (_duplicateOnDrag && _mode == Mode.Move && !_duplicated)
            {
                var copy = TileDocumentEditor.DuplicateTile(_design, _selected);
                if (copy is not null)
                {
                    _duplicated = true;
                    _selected = copy;
                    _selectedId = copy.NodeId;
                    _startX = copy.X ?? _startX;
                    _startY = copy.Y ?? _startY;
                    _startRect = RectOf(copy) ?? _startRect;
                    InteractionStarted?.Invoke(this, EventArgs.Empty);
                    SelectionChanged?.Invoke(this, EventArgs.Empty);
                    Status?.Invoke("Tile duplicated. Drag to place it.");
                }
            }
            else
            {
                InteractionStarted?.Invoke(this, EventArgs.Empty);
            }

            _changed = true;
        }

        switch (_mode)
        {
            case Mode.Move:
                var x = TileDocumentEditor.Snap(_design, _startX + dx);
                var y = TileDocumentEditor.Snap(_design, _startY + dy);
                TileDocumentEditor.SetGeometry(_selected, Math.Max(0, x), Math.Max(0, y), null, null);
                break;
            case Mode.ResizeWidth:
                var width = TileDocumentEditor.Snap(_design, Math.Max(TileDocumentEditor.MinTileSize,
                    _startWidth + dx));
                _previewRect = Forecast(_startRect.X, _startRect.Y,
                    Math.Min(width, Math.Max(TileDocumentEditor.MinTileSize,
                        _design.CanvasWidth - _startRect.X)), _startRect.Height);
                break;
            case Mode.ResizeHeight:
                var height = TileDocumentEditor.Snap(_design, Math.Max(TileDocumentEditor.MinTileSize,
                    _startHeight + dy));
                _previewRect = Forecast(_startRect.X, _startRect.Y, _startRect.Width,
                    Math.Min(height, Math.Max(TileDocumentEditor.MinTileSize,
                        _design.CanvasHeight - _startRect.Y)));
                break;
            case Mode.ResizeCorner:
                var w = TileDocumentEditor.Snap(_design, Math.Max(TileDocumentEditor.MinTileSize,
                    _startWidth + dx));
                var h = TileDocumentEditor.Snap(_design, Math.Max(TileDocumentEditor.MinTileSize,
                    _startHeight + dy));
                _previewRect = Forecast(_startRect.X, _startRect.Y, w, h);
                break;
        }

        if (_previewRect is not null)
        {
            InvalidateVisual();
            Status?.Invoke(string.Format(CultureInfo.InvariantCulture, "{0:0} x {1:0}",
                _previewRect.Value.Width, _previewRect.Value.Height));
            return;
        }

        // The canvas is centred, so growing it while the pointer is down would move the origin
        // under the gesture and feed back into the drag distance. Growth is applied on release.
        GeometryChanged?.Invoke(this, false);
    }

    private static Rect Forecast(double x, double y, double width, double height)
        => new(x, y, Math.Max(TileDocumentEditor.MinTileSize, width),
            Math.Max(TileDocumentEditor.MinTileSize, height));

    private void GrowthFeedback()
    {
        if (_design is null || _selected is null)
        {
            return;
        }

        var right = (_selected.X ?? 0) + (_selected.FrameWidth ?? 0);
        var bottom = (_selected.Y ?? 0) + (_selected.FrameHeight ?? 0);
        var width = _design.CanvasWidth;
        var height = _design.CanvasHeight;
        if (right > width + 0.5)
        {
            TileDocumentEditor.SetCanvasSize(_design, right, null);
        }

        if (bottom > height + 0.5)
        {
            TileDocumentEditor.SetCanvasSize(_design, null, bottom);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_mode == Mode.None)
        {
            return;
        }

        var mode = _mode;
        var preview = _previewRect;
        var wasChanged = _changed;
        _previewRect = null;
        _mode = Mode.None;
        _changed = false;
        _duplicateOnDrag = false;
        ReleaseMouseCapture();

        if (preview is { } box && _selected is not null)
        {
            var width = mode is Mode.ResizeWidth or Mode.ResizeCorner ? box.Width : (double?)null;
            var height = mode is Mode.ResizeHeight or Mode.ResizeCorner ? box.Height : (double?)null;
            TileDocumentEditor.SetGeometry(_selected, null, null, width, height);
            wasChanged = true;
        }

        if (wasChanged)
        {
            GrowthFeedback();
            InvalidateVisual();
            GeometryChanged?.Invoke(this, true);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && _mode != Mode.None && _selected is not null && _design is not null)
        {
            TileDocumentEditor.SetGeometry(_selected, _startX, _startY,
                _mode is Mode.ResizeWidth or Mode.ResizeCorner ? _startWidth : null,
                _mode is Mode.ResizeHeight or Mode.ResizeCorner ? _startHeight : null);
            _previewRect = null;
            _mode = Mode.None;
            ReleaseMouseCapture();
            InvalidateVisual();
            GeometryChanged?.Invoke(this, true);
            Status?.Invoke("Drag cancelled.");
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Delete or Key.Back && _selected is not null)
        {
            if (_selected.Locked == true)
            {
                Status?.Invoke("This tile is locked and was not removed.");
            }
            else
            {
                DeleteRequested?.Invoke(this, EventArgs.Empty);
            }

            e.Handled = true;
        }
    }

    private void UpdateCursor(Point point)
    {
        if (_selected is not null && RectOf(_selected) is { } rect)
        {
            Cursor = HitHandle(rect, point) switch
            {
                Mode.ResizeWidth => Cursors.SizeWE,
                Mode.ResizeHeight => Cursors.SizeNS,
                Mode.ResizeCorner => Cursors.SizeNWSE,
                _ => Cursors.Arrow,
            };
            if (Cursor != Cursors.Arrow)
            {
                return;
            }
        }

        Cursor = HitTestScope(point)?.Locked == true ? Cursors.No : Cursors.Arrow;
    }

    private void Select(string? nodeId)
    {
        _selectedId = nodeId;
        _selected = nodeId is null ? null : _byId.GetValueOrDefault(nodeId);
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(Frozen(color), thickness);
        pen.Freeze();
        return pen;
    }
}
