using System.Globalization;
using HardwareOSD.Core.Config;

namespace HardwareOSD.Core.TileOsd;

/// <summary>
/// Structural editing of a tile design document: stable ids, tile creation, z-order, geometry,
/// clamps and the limits the CapFrameX editor documents (64 tiles, 4 container levels).
/// All functions mutate the document in place; callers snapshot it for undo.
/// </summary>
public static class TileDocumentEditor
{
    public const int MaxTiles = 64;
    public const int MaxContainerDepth = 4;
    public const double DefaultWidth = 520;
    public const double MinTileSize = 8;
    public const double MaxDesignWidth = 1600;
    public const double MinDesignWidth = 220;
    public const double MaxCanvasHeight = 4096;
    public const double MinCanvasHeight = 64;

    private const string TransparentPng =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    public static string NewNodeId() => Guid.NewGuid().ToString("N");

    /// <summary>Assigns an id to the root and every descendant that lacks one.</summary>
    public static void EnsureIds(TileDesignDocument document)
    {
        document.Root.NodeId ??= NewNodeId();
        foreach (var node in document.Root.Descendants())
        {
            node.NodeId ??= NewNodeId();
        }
    }

    public static TileNode? Find(TileDesignDocument document, string? nodeId)
    {
        if (string.IsNullOrEmpty(nodeId))
        {
            return null;
        }

        if (string.Equals(document.Root.NodeId, nodeId, StringComparison.Ordinal))
        {
            return document.Root;
        }

        return document.Root.Descendants()
            .FirstOrDefault(node => string.Equals(node.NodeId, nodeId, StringComparison.Ordinal));
    }

    public static TileNode? ParentOf(TileDesignDocument document, string? nodeId)
    {
        if (string.IsNullOrEmpty(nodeId) ||
            string.Equals(document.Root.NodeId, nodeId, StringComparison.Ordinal))
        {
            return null;
        }

        if (document.Root.Children.Any(child => string.Equals(child.NodeId, nodeId, StringComparison.Ordinal)))
        {
            return document.Root;
        }

        foreach (var node in document.Root.Descendants())
        {
            if (node.Children.Any(child => string.Equals(child.NodeId, nodeId, StringComparison.Ordinal)))
            {
                return node;
            }
        }

        return null;
    }

    /// <summary>Index path used by <see cref="TileOverrideSettings"/> and the renderer.</summary>
    public static string? PathOf(TileDesignDocument document, string? nodeId)
    {
        foreach (var (path, node) in document.Walk())
        {
            if (string.Equals(node.NodeId, nodeId, StringComparison.Ordinal))
            {
                return path;
            }
        }

        return null;
    }

    public static int DepthOf(TileDesignDocument document, TileNode? node)
    {
        var depth = 0;
        var current = node;
        while (current is not null)
        {
            current = ParentOf(document, current.NodeId);
            if (current is not null)
            {
                depth++;
            }
        }

        return depth;
    }

    /// <summary>True when the node is a panel that the builder treats as a container.</summary>
    public static bool IsContainer(TileNode node)
        => node.NodeKind == TileNodeKind.Panel &&
           (node.FlowLayout == TileFlowLayout.Canvas || node.Children.Count > 1);

    /// <summary>
    /// CapFrameX files wrap a container in a single-child panel. Editing must skip that wrapper,
    /// otherwise a nested design can be selected but never entered.
    /// </summary>
    public static TileNode ResolveEditableScope(TileNode node)
    {
        var current = node;
        while (current.NodeKind == TileNodeKind.Panel &&
               current.FlowLayout != TileFlowLayout.Canvas &&
               current.Children.Count == 1 &&
               current.Children[0].NodeKind == TileNodeKind.Panel)
        {
            current = current.Children[0];
        }

        return current;
    }

    /// <summary>True when double-clicking the tile should open a scope with editable children.</summary>
    public static bool HasEditableChildren(TileNode node)
        => node.NodeKind == TileNodeKind.Panel && ResolveEditableScope(node).Children.Count > 0;

    /// <summary>
    /// The widget inside a wrapper tile: the single child of a non-canvas panel. Saves and
    /// CapFrameX files wrap every top-level widget in one. Returns the node itself otherwise.
    /// </summary>
    public static TileNode WidgetOf(TileNode node)
        => node.NodeKind == TileNodeKind.Panel &&
           node.FlowLayout != TileFlowLayout.Canvas &&
           node.Children.Count == 1
            ? node.Children[0]
            : node;

    public static int CountWidgets(TileNode parent)
    {
        var count = 0;
        foreach (var child in parent.Children)
        {
            count++;
            if (child.NodeKind == TileNodeKind.Panel &&
                child.FlowLayout != TileFlowLayout.Canvas &&
                child.Children.Count == 1)
            {
                continue; // wrapper tile: its child is the widget, not another tile
            }

            count += CountWidgets(child);
        }

        return count;
    }

    public static TileDesignDocument CreateNew(string name)
    {
        var document = new TileDesignDocument
        {
            Id = NewNodeId(),
            Name = string.IsNullOrWhiteSpace(name) ? "Untitled" : name.Trim(),
            Family = "Custom",
            Anchor = "top_left",
            Root = new TileNode
            {
                NodeId = NewNodeId(),
                Type = "panel",
                Kind = "panel",
                Layout = "canvas",
                Columns = 1,
                X = 0,
                Y = 0,
                Padding = 0,
                Gap = 8,
                Width = DefaultWidth,
                FrameWidth = DefaultWidth,
                Height = 100,
                FrameHeight = 100,
                Background = false,
            },
        };
        return document;
    }

    /// <summary>Converts any parsed design (flow or canvas) into an editable canvas document.</summary>
    public static void PrepareForEditing(TileDesignDocument document)
    {
        EnsureIds(document);
        document.Root.Layout = "canvas";
        document.Root.Padding ??= 0;
        if (document.CanvasWidth <= 0)
        {
            document.Root.Width = DefaultWidth;
            document.Root.FrameWidth = DefaultWidth;
        }

        if (document.CanvasHeight <= 0)
        {
            document.Root.Height = 120;
            document.Root.FrameHeight = 120;
        }

        // Every direct child of the canvas needs an anchor, otherwise the layout engine would fall
        // back to a flow position and the tile would jump when the user first drags it. Frames are
        // left untouched when the design already has none: the layout engine estimates them and the
        // first resize writes a real frame.
        var y = 0.0;
        foreach (var child in document.Root.Children)
        {
            if (child.X is null || child.Y is null)
            {
                child.X = 0;
                child.Y = y;
            }

            y = (child.Y ?? 0) + (child.FrameHeight ?? child.Height ?? 32) + (document.Theme.Gap);
        }

        FitCanvasToContent(document, growOnly: true);
        document.NormalizeEditorSettings();
    }

    public static TileNode CreateLeaf(TileNodeKind kind, TileTheme theme)
    {
        var node = new TileNode
        {
            NodeId = NewNodeId(),
            Type = KindToken(kind),
            Kind = KindToken(kind),
        };

        switch (kind)
        {
            case TileNodeKind.Metric:
                node.Key = "cpuLoad";
                node.Label = "CPU load";
                node.Unit = "%";
                node.Digits = 0;
                node.Size = 12;
                node.ValueSize = 22;
                node.LabelPosition = "top";
                node.Format = "%.0f";
                break;
            case TileNodeKind.Text:
                node.Text = "Text";
                node.Size = 14;
                node.Align = "left";
                break;
            case TileNodeKind.Chart:
                node.Series = "frametimes";
                node.Title = "Frametime";
                node.Unit = "ms";
                node.ShowLabels = true;
                node.ShowGridLines = true;
                break;
            case TileNodeKind.Bar:
                node.Key = "gpuLoad";
                node.Label = "GPU load";
                node.Unit = "%";
                node.Min = 0;
                node.Max = 100;
                node.Digits = 0;
                node.ShowValue = true;
                node.Orientation = "horizontal";
                break;
            case TileNodeKind.Gauge:
                node.Key = "cpuLoad";
                node.Label = "CPU load";
                node.Unit = "%";
                node.Min = 0;
                node.Max = 100;
                node.Diameter = 100;
                node.Thickness = 6;
                node.StartAngle = 140;
                node.SweepAngle = 260;
                node.ShowValue = true;
                break;
            case TileNodeKind.Icon:
                node.Glyph = "\uE946";
                node.Family = "Segoe MDL2 Assets";
                node.Size = 24;
                break;
            case TileNodeKind.Image:
                node.DataUri = TransparentPng;
                node.Fit = "contain";
                node.Opacity = 1;
                break;
            case TileNodeKind.Heatmap:
                node.Keys = Enumerable.Range(0, Math.Min(8, Math.Max(1, Environment.ProcessorCount)))
                    .Select(index => "role:cpu-loads/" + index.ToString(CultureInfo.InvariantCulture))
                    .ToList();
                node.Title = "CPU cores";
                node.Min = 0;
                node.Max = 100;
                break;
            case TileNodeKind.ClassicRows:
                node.RowScale = 1;
                break;
        }

        node.Color ??= theme.Text;
        return node;
    }

    /// <summary>Creates a tile the way the compiler wraps widgets: panel chrome + one widget.</summary>
    public static TileNode CreateTile(TileNodeKind kind, double x, double y, double? width,
        double? height, TileTheme theme)
    {
        var (w, h) = DefaultSize(kind);
        w = width ?? w;
        h = height ?? h;

        if (kind == TileNodeKind.Panel)
        {
            return new TileNode
            {
                NodeId = NewNodeId(),
                Type = "panel",
                Kind = "panel",
                Layout = "canvas",
                X = x,
                Y = y,
                FrameWidth = w,
                FrameHeight = h,
                Width = w,
                Height = h,
                Padding = Math.Max(0, theme.Pad - 4),
                Gap = theme.Gap,
                Background = false,
                BorderColor = "#FFFFFF1F",
                BorderWidth = 1,
                Radius = theme.Radius,
                Children = new List<TileNode>(),
            };
        }

        var leaf = CreateLeaf(kind, theme);
        return new TileNode
        {
            NodeId = NewNodeId(),
            Type = "panel",
            Kind = "panel",
            X = x,
            Y = y,
            FrameWidth = w,
            FrameHeight = h,
            Width = w,
            Height = h,
            Padding = theme.Pad,
            Gap = theme.Gap,
            Background = true,
            Color = theme.Panel,
            Radius = theme.Radius,
            Clip = true,
            Children = new List<TileNode> { leaf },
        };
    }

    public static (double Width, double Height) DefaultSize(TileNodeKind kind) => kind switch
    {
        TileNodeKind.Metric => (180, 72),
        TileNodeKind.Text => (180, 32),
        TileNodeKind.Chart => (280, 96),
        TileNodeKind.Bar => (200, 44),
        TileNodeKind.Gauge => (130, 130),
        TileNodeKind.Icon => (36, 36),
        TileNodeKind.Image => (120, 48),
        TileNodeKind.Heatmap => (220, 64),
        TileNodeKind.ClassicRows => (320, 120),
        _ => (240, 160),
    };

    /// <summary>Adds a tile to the scope. Returns null when a documented limit is reached.</summary>
    public static TileNode? AddTile(TileDesignDocument document, TileNode scope, TileNodeKind kind,
        double x, double y, double? width = null, double? height = null)
    {
        if (scope.NodeKind != TileNodeKind.Panel)
        {
            return null;
        }

        if (CountWidgets(document.Root) >= MaxTiles)
        {
            return null;
        }

        if (kind == TileNodeKind.Panel &&
            DepthOf(document, scope) >= MaxContainerDepth)
        {
            return null;
        }

        // The root is always a canvas; a container the user adds is a canvas too so its children
        // keep their dragged geometry.
        scope.Layout = "canvas";
        var tile = CreateTile(kind, Math.Max(0, x), Math.Max(0, y), width, height, document.Theme);
        scope.Children.Add(tile);
        GrowCanvas(document, tile);
        return tile;
    }

    public static TileNode? DuplicateTile(TileDesignDocument document, TileNode node)
    {
        var parent = ParentOf(document, node.NodeId);
        if (parent is null || CountWidgets(document.Root) >= MaxTiles)
        {
            return null;
        }

        var copy = node.CloneNode();
        Renumber(copy);
        copy.X = (node.X ?? 0) + 12;
        copy.Y = (node.Y ?? 0) + 12;

        var index = parent.Children.IndexOf(node);
        parent.Children.Insert(index + 1, copy);
        GrowCanvas(document, copy);
        return copy;
    }

    /// <summary>
    /// Adds a preconfigured node (library preset or pasted clipboard subtree) to the current scope.
    /// Unlike <see cref="AddTile"/> it does not wrap the node in a panel, so layout containers and
    /// nested groups keep their own children and geometry.
    /// </summary>
    public static TileNode? AddNode(TileDesignDocument document, TileNode scope, TileNode node,
        double? x = null, double? y = null)
    {
        if (scope.NodeKind != TileNodeKind.Panel)
        {
            return null;
        }

        if (CountWidgets(document.Root) + CountWidgets(node) > MaxTiles)
        {
            return null;
        }

        if (node.NodeKind == TileNodeKind.Panel &&
            DepthOf(document, scope) >= MaxContainerDepth)
        {
            return null;
        }

        if (x is not null)
        {
            node.X = Math.Max(0, x.Value);
        }

        if (y is not null)
        {
            node.Y = Math.Max(0, y.Value);
        }

        scope.Children.Add(node);
        GrowCanvas(document, node);
        return node;
    }

    /// <summary>Deep copy of a node with fresh editor ids, ready to be inserted again.</summary>
    public static TileNode CloneForPaste(TileNode node)
    {
        var copy = node.CloneNode();
        Renumber(copy);
        return copy;
    }

    private static void Renumber(TileNode node)
    {
        node.NodeId = NewNodeId();
        foreach (var descendant in node.Descendants())
        {
            descendant.NodeId = NewNodeId();
        }
    }

    /// <summary>
    /// Moves a node to another container. The target may not be the node itself or one of its
    /// descendants, which keeps the tree acyclic; x/y are relative to the target canvas when given.
    /// </summary>
    public static bool Reparent(TileDesignDocument document, TileNode node, TileNode target,
        double? x = null, double? y = null)
    {
        if (target.NodeKind != TileNodeKind.Panel ||
            ReferenceEquals(node, target) ||
            node.Descendants().Any(descendant => ReferenceEquals(descendant, target)))
        {
            return false;
        }

        var parent = ParentOf(document, node.NodeId);
        if (parent is null || ReferenceEquals(parent, target))
        {
            return false;
        }

        if (DepthOf(document, target) + SubtreeDepth(node) > MaxContainerDepth)
        {
            return false;
        }

        parent.Children.Remove(node);
        if (x is not null)
        {
            node.X = Math.Max(0, x.Value);
        }

        if (y is not null)
        {
            node.Y = Math.Max(0, y.Value);
        }

        target.Children.Add(node);
        GrowCanvas(document, node);
        return true;
    }

    private static int SubtreeDepth(TileNode node)
    {
        var depth = 0;
        foreach (var child in node.Children)
        {
            depth = Math.Max(depth, 1 + SubtreeDepth(child));
        }

        return depth;
    }

    public static bool Remove(TileDesignDocument document, TileNode node)
    {
        var parent = ParentOf(document, node.NodeId);
        return parent is not null && parent.Children.Remove(node);
    }

    public static bool MoveOrder(TileDesignDocument document, TileNode node, int delta)
    {
        var parent = ParentOf(document, node.NodeId);
        if (parent is null)
        {
            return false;
        }

        var index = parent.Children.IndexOf(node);
        var target = Math.Clamp(index + delta, 0, parent.Children.Count - 1);
        if (target == index)
        {
            return false;
        }

        parent.Children.RemoveAt(index);
        parent.Children.Insert(target, node);
        return true;
    }

    public static bool MoveToFront(TileDesignDocument document, TileNode node)
    {
        var parent = ParentOf(document, node.NodeId);
        if (parent is null)
        {
            return false;
        }

        var index = parent.Children.IndexOf(node);
        if (index == parent.Children.Count - 1)
        {
            return false;
        }

        parent.Children.RemoveAt(index);
        parent.Children.Add(node);
        return true;
    }

    public static void SetGeometry(TileNode node, double? x, double? y, double? width, double? height)
    {
        if (x is not null)
        {
            node.X = Math.Max(0, x.Value);
        }

        if (y is not null)
        {
            node.Y = Math.Max(0, y.Value);
        }

        if (width is not null)
        {
            var value = Math.Max(MinTileSize, width.Value);
            node.Width = value;
            node.FrameWidth = value;
        }

        if (height is not null)
        {
            var value = Math.Max(MinTileSize, height.Value);
            node.Height = value;
            node.FrameHeight = value;
        }
    }

    public static double Snap(TileDesignDocument document, double value)
    {
        if (!document.SnapToGrid || document.GridSize <= 0)
        {
            return Math.Round(value);
        }

        return Math.Round(value / document.GridSize) * document.GridSize;
    }

    /// <summary>Grows the canvas down/right when a tile is dragged past its current bounds.</summary>
    public static void GrowCanvas(TileDesignDocument document, TileNode tile)
    {
        var right = (tile.X ?? 0) + (tile.FrameWidth ?? tile.Width ?? 0);
        var bottom = (tile.Y ?? 0) + (tile.FrameHeight ?? tile.Height ?? 0);
        if (ParentOf(document, tile.NodeId) != document.Root)
        {
            return;
        }

        var width = document.CanvasWidth;
        var height = document.CanvasHeight;
        if (right > width)
        {
            document.Root.Width = right;
            document.Root.FrameWidth = right;
        }

        if (bottom > height)
        {
            document.Root.Height = bottom;
            document.Root.FrameHeight = bottom;
        }
    }

    public static void SetCanvasSize(TileDesignDocument document, double? width, double? height)
    {
        if (width is not null)
        {
            var value = Math.Clamp(width.Value, MinDesignWidth, MaxDesignWidth);
            document.Root.Width = value;
            document.Root.FrameWidth = value;
        }

        if (height is not null)
        {
            var value = Math.Clamp(height.Value, MinCanvasHeight, MaxCanvasHeight);
            document.Root.Height = value;
            document.Root.FrameHeight = value;
        }
    }

    /// <summary>Canvas height that exactly covers the tiles. growOnly never shrinks a design.</summary>
    public static void FitCanvasToContent(TileDesignDocument document, bool growOnly = false,
        bool ignoreHidden = false)
    {
        var bottom = 0.0;
        var right = 0.0;
        foreach (var child in document.Root.Children)
        {
            if (ignoreHidden && child.Visible == false)
            {
                continue;
            }

            bottom = Math.Max(bottom, (child.Y ?? 0) + (child.FrameHeight ?? child.Height ?? 0));
            right = Math.Max(right, (child.X ?? 0) + (child.FrameWidth ?? child.Width ?? 0));
        }

        var height = Math.Clamp(bottom, MinCanvasHeight, MaxCanvasHeight);
        if (!growOnly || height > document.CanvasHeight)
        {
            document.Root.Height = height;
            document.Root.FrameHeight = height;
        }

        if (!growOnly && right > document.CanvasWidth)
        {
            document.Root.Width = Math.Clamp(right, MinDesignWidth, MaxDesignWidth);
            document.Root.FrameWidth = document.Root.Width;
        }

        document.NormalizeEditorSettings();
    }

    /// <summary>
    /// Folds legacy TileNodeOverride entries into the design so an existing user's labels and keys
    /// survive the move to the editable document.
    /// </summary>
    public static void MergeOverrides(TileDesignDocument document, TileOverrideSettings? overrides)
    {
        if (overrides is null || overrides.Nodes.Count == 0)
        {
            return;
        }

        foreach (var (path, node) in document.Walk())
        {
            var value = overrides.For(path);
            if (value is null || value.IsEmpty)
            {
                continue;
            }

            node.Key = value.Key ?? node.Key;
            node.SecondaryKey = value.SecondaryKey ?? node.SecondaryKey;
            node.Label = value.Label ?? node.Label;
            node.Unit = value.Unit ?? node.Unit;
            node.Digits = value.Digits ?? node.Digits;
            node.ValueScale = value.ValueScale ?? node.ValueScale;
            node.ValueColor = value.ValueColor ?? node.ValueColor;
            node.LabelColor = value.LabelColor ?? node.LabelColor;
            node.ValueSize = value.ValueSize ?? node.ValueSize;
            node.Min = value.BarMinimum ?? node.Min;
            node.Max = value.BarMaximum ?? node.Max;
            if (value.ChartColor is not null || value.ChartThickness is not null)
            {
                node.Lines ??= new List<TileChartLine>();
                if (node.Lines.Count == 0)
                {
                    node.Lines.Add(new TileChartLine());
                }

                node.Lines[0].Color = value.ChartColor ?? node.Lines[0].Color;
            }
        }
    }

    public static string KindToken(TileNodeKind kind) => kind switch
    {
        TileNodeKind.Panel => "panel",
        TileNodeKind.Metric => "metric",
        TileNodeKind.Text => "text",
        TileNodeKind.Chart => "chart",
        TileNodeKind.Bar => "bar",
        TileNodeKind.Gauge => "gauge",
        TileNodeKind.Icon => "icon",
        TileNodeKind.Image => "image",
        TileNodeKind.Heatmap => "heatmap",
        TileNodeKind.ClassicRows => "classicRows",
        _ => "metric",
    };
}

internal static class TileNodeCloneExtensions
{
    /// <summary>Deep copy of a node subtree; used by duplicate.</summary>
    public static TileNode CloneNode(this TileNode node)
    {
        var copy = new TileNode
        {
            NodeId = node.NodeId,
            Name = node.Name,
            Locked = node.Locked,
            Type = node.Type,
            Kind = node.Kind,
            Layout = node.Layout,
            X = node.X,
            Y = node.Y,
            FrameWidth = node.FrameWidth,
            FrameHeight = node.FrameHeight,
            Width = node.Width,
            Height = node.Height,
            Padding = node.Padding,
            Gap = node.Gap,
            Radius = node.Radius,
            Columns = node.Columns,
            ColumnSpan = node.ColumnSpan,
            Clip = node.Clip,
            Background = node.Background,
            Color = node.Color,
            BackgroundColor = node.BackgroundColor,
            BorderColor = node.BorderColor,
            BorderWidth = node.BorderWidth,
            TileOpacity = node.TileOpacity,
            Text = node.Text,
            Label = node.Label,
            Title = node.Title,
            Weight = node.Weight,
            Align = node.Align,
            LabelAlign = node.LabelAlign,
            Size = node.Size,
            LabelSize = node.LabelSize,
            LabelColor = node.LabelColor,
            LabelPosition = node.LabelPosition,
            ValueLines = node.ValueLines,
            Family = node.Family,
            Glyph = node.Glyph,
            TextColor = node.TextColor,
            Key = node.Key,
            SecondaryKey = node.SecondaryKey,
            Unit = node.Unit,
            SecondaryUnit = node.SecondaryUnit,
            Format = node.Format,
            Digits = node.Digits,
            SecondaryDigits = node.SecondaryDigits,
            Separator = node.Separator,
            ValueSize = node.ValueSize,
            UnitSize = node.UnitSize,
            UnitColor = node.UnitColor,
            ValueColor = node.ValueColor,
            ValueScale = node.ValueScale,
            SecondaryScale = node.SecondaryScale,
            ValueFormat = node.ValueFormat,
            ShowValue = node.ShowValue,
            WarningThreshold = node.WarningThreshold,
            CriticalThreshold = node.CriticalThreshold,
            Minimum = node.Minimum,
            Maximum = node.Maximum,
            Min = node.Min,
            Max = node.Max,
            Orientation = node.Orientation,
            TrackColor = node.TrackColor,
            Diameter = node.Diameter,
            Thickness = node.Thickness,
            StartAngle = node.StartAngle,
            SweepAngle = node.SweepAngle,
            Series = node.Series,
            Lines = node.Lines?.Select(line => new TileChartLine
            {
                Series = line.Series,
                Kind = line.Kind,
                Factor = line.Factor,
                Color = line.Color,
                Label = line.Label,
            }).ToList(),
            ShowBackground = node.ShowBackground,
            ShowBorder = node.ShowBorder,
            ShowLabels = node.ShowLabels,
            ShowGridLines = node.ShowGridLines,
            GridLines = node.GridLines,
            CurrentValue = node.CurrentValue,
            HistoryPoints = node.HistoryPoints,
            MiniGraph = node.MiniGraph,
            GraphColor = node.GraphColor,
            DataUri = node.DataUri,
            Fit = node.Fit,
            Opacity = node.Opacity,
            Vendor = node.Vendor,
            Model = node.Model,
            Keys = node.Keys?.ToList(),
            RowScale = node.RowScale,
            ClassicRowsAutoHeight = node.ClassicRowsAutoHeight,
            Children = node.Children.Select(child => child.CloneNode()).ToList(),
        };
        return copy;
    }
}
