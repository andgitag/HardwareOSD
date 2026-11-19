using System.Globalization;
using HardwareOSD.Core.Config;
using HardwareOSD.Core.Metrics;

namespace HardwareOSD.Core.TileOsd;

/// <summary>
/// One-time conversion of the five original row profiles to the tile document model. The converter
/// reads the user's current element tree (order, labels, visibility, colours, group layout and
/// root layout) and emits ordinary <see cref="TileNode"/> widgets, so the result remains fully
/// editable in the CapFrameX-style builder. The old element tree is kept on the profile as a
/// migration/fallback record; it is never used to overwrite a document the user has already edited.
/// </summary>
public static class RowProfileTileMigrator
{
    public const int CurrentVersion = 1;

    public static readonly string[] ClassicProfileIds =
    {
        "classic",
        "cfx-green",
        "cfx-split",
        "cfx-info",
        "minimal-rings",
    };

    private const double ValueGap = 4;
    private const double LabelGap = 6;
    private const double SectionGap = 2;
    private const double TextWidthFactor = 0.60;
    private const double LabelWidthFactor = 0.68;

    /// <summary>
    /// Converts any of the five classic profiles that is still a row profile. Safe to call on every
    /// normalization: a profile that is already a tile is left untouched, which preserves builder
    /// edits and makes a later library re-seed migrate exactly once.
    /// </summary>
    public static int Migrate(OsdSettings osd)
    {
        ArgumentNullException.ThrowIfNull(osd);
        var migrated = 0;
        foreach (var id in ClassicProfileIds)
        {
            var profile = osd.FindProfile(id);
            if (profile is null || profile.IsTile)
            {
                continue;
            }

            var document = BuildDesign(profile);
            profile.Layout = OsdProfileLayout.Tile;
            profile.DesignId = string.Empty;
            profile.TileDesignJson = document.ToJson();
            profile.Tile = new TileOverrideSettings();
            profile.FollowMetricPreferences = false;
            migrated++;
        }

        osd.TileMigrationVersion = Math.Max(osd.TileMigrationVersion, CurrentVersion);
        return migrated;
    }

    public static TileDesignDocument BuildDesign(OsdProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var style = profile.StyleSettings.Clone();
        style.Normalize();
        var theme = BuildTheme(style);
        var root = new TileNode
        {
            NodeId = NodeId(profile, "root"),
            Type = "panel",
            Kind = "panel",
            Name = "Root",
            Layout = "canvas",
            X = 0,
            Y = 0,
            Padding = 0,
            Gap = 0,
            Background = style.ShowBackground,
            BackgroundColor = Background(style),
            Color = Background(style),
            Radius = style.CornerRadius,
            Clip = true,
        };

        var background = style.ShowBackground ? Background(style) : null;
        root.BackgroundColor = background;
        root.Background = style.ShowBackground;

        var sections = profile.RootLayout == OsdGroupLayout.Columns
            ? new List<TileNode> { BuildInlineRoot(profile, style, theme) }
            : BuildSections(profile, style, theme);

        var contentWidth = sections.Count == 0
            ? 120
            : sections.Max(section => section.FrameWidth ?? section.Width ?? 0);
        var width = Math.Max(120, contentWidth + style.PaddingX * 2);
        var y = style.PaddingY;
        var contentHeight = 0.0;
        foreach (var section in sections)
        {
            var sectionWidth = Math.Min(width - style.PaddingX * 2,
                Math.Max(24, section.FrameWidth ?? section.Width ?? contentWidth));
            section.FrameWidth = sectionWidth;
            section.Width ??= sectionWidth;
            section.X = style.PaddingX;
            section.Y = y;
            y += (section.FrameHeight ?? section.Height ?? 20) + (profile.RootLayout == OsdGroupLayout.Columns
                ? 0
                : style.RowSpacing);
            contentHeight = y;
        }

        var height = Math.Max(34, contentHeight + style.PaddingY - style.RowSpacing);
        root.FrameWidth = width;
        root.FrameHeight = height;
        root.Width = width;
        root.Height = height;
        root.Children = sections;

        return new TileDesignDocument
        {
            Id = profile.Id,
            Name = profile.Name,
            Family = "HardwareOSD",
            Anchor = "top_left",
            MarginX = 24,
            MarginY = 24,
            Theme = theme,
            Root = root,
            Scale = 1,
        };
    }

    private static TileTheme BuildTheme(OsdStyleSettings style) => new()
    {
        Panel = style.ShowBackground ? Background(style) : "#00000000",
        Accent = AppSettings.SanitizeColor(style.LabelColor, "#6FD8CC"),
        Text = AppSettings.SanitizeColor(style.ValueColor, "#F4F7FA"),
        Subtext = AppSettings.SanitizeColor(style.UnitColor, "#9AA3AC"),
        FontFamily = style.FontFamily,
        FontSize = style.FontSize,
        TitleSize = Math.Max(8, style.FontSize * style.LabelScale),
        Pad = style.PaddingX,
        Gap = style.RowSpacing,
        Radius = style.CornerRadius,
    };

    private static string Background(OsdStyleSettings style)
    {
        var color = TileColor.Parse(AppSettings.SanitizeColor(style.BackgroundColor, "#000000"),
            TileColor.Transparent);
        var alpha = (byte)Math.Clamp(Math.Round(style.BackgroundOpacity * 255.0 / 100.0), 0, 255);
        return color.WithAlpha(alpha).ToHex();
    }

    private static List<TileNode> BuildSections(OsdProfile profile, OsdStyleSettings style,
        TileTheme theme)
    {
        var sections = new List<TileNode>(profile.Elements.Count);
        foreach (var element in profile.Elements)
        {
            if (element.Kind == OsdElementKind.Group)
            {
                var group = BuildGroup(element, profile, style, theme, 0);
                if (group is not null)
                {
                    sections.Add(group);
                }

                continue;
            }

            var node = BuildElementRow(element, profile, style, theme, style.ShowLabels);
            if (node is not null)
            {
                sections.Add(node);
            }
        }

        return sections;
    }

    private static TileNode BuildGroup(OsdElement group, OsdProfile profile, OsdStyleSettings style,
        TileTheme theme, int depth)
    {
        if (group.GroupLayout == OsdGroupLayout.Columns)
        {
            return BuildColumnsGroup(group, profile, style, theme, depth);
        }

        var children = new List<TileNode>();
        double width = 0;
        double height = 0;
        if (style.ShowLabels && !string.IsNullOrWhiteSpace(group.Label))
        {
            var label = TextNode(group, profile, style, DisplayLabel(group),
                group.FontSize > 0 ? group.FontSize : style.FontSize * style.LabelScale,
                LabelColor(group, style), int.MaxValue, Align(group.Align));
            children.Add(label);
            width = Math.Max(width, label.FrameWidth ?? 0);
            height += label.FrameHeight ?? 0;
        }

        foreach (var child in group.Children)
        {
            TileNode? node;
            if (child.Kind == OsdElementKind.Group)
            {
                node = BuildGroup(child, profile, style, theme, depth + 1);
            }
            else
            {
                node = BuildElementRow(child, profile, style, theme, style.ShowLabels);
            }

            if (node is null)
            {
                continue;
            }

            children.Add(node);
            width = Math.Max(width, node.FrameWidth ?? node.Width ?? 0);
            height += node.FrameHeight ?? node.Height ?? 0;
            if (children.Count > 1)
            {
                height += style.RowSpacing;
            }
        }

        if (children.Count == 0)
        {
            return new TileNode
            {
                NodeId = NodeId(profile, "empty-group", group.Id),
                Type = "panel",
                Kind = "panel",
                Name = group.Label,
                Layout = "vertical",
                FrameWidth = 0,
                FrameHeight = 0,
                Visible = group.Visible,
            };
        }

        // Give every child the final section width so a user resizing the group in the builder
        // gets a predictable left-aligned layout instead of clipped text.
        foreach (var child in children)
        {
            if (child.FrameWidth is null || child.FrameWidth < width)
            {
                child.FrameWidth = width;
                child.Width = width;
            }
        }

        var panel = new TileNode
        {
            NodeId = NodeId(profile, "group", group.Id),
            Type = "panel",
            Kind = "panel",
            Name = group.Label,
            Layout = "vertical",
            Gap = style.RowSpacing,
            Padding = depth > 0 ? 4 : 0,
            FrameWidth = width,
            FrameHeight = Math.Max(1, height),
            Width = width,
            Height = Math.Max(1, height),
            Visible = group.Visible,
            Children = children,
        };
        return panel;
    }

    private static TileNode BuildColumnsGroup(OsdElement group, OsdProfile profile,
        OsdStyleSettings style, TileTheme theme, int depth)
    {
        var children = new List<TileNode>();
        var width = 0.0;
        var height = 0.0;
        if (style.ShowLabels && !string.IsNullOrWhiteSpace(group.Label))
        {
            var label = TextNode(group, profile, style, DisplayLabel(group),
                group.FontSize > 0 ? group.FontSize : style.FontSize * style.LabelScale,
                LabelColor(group, style), int.MaxValue, Align(group.Align));
            children.Add(label);
            width += label.FrameWidth ?? 0;
            height = Math.Max(height, label.FrameHeight ?? 0);
        }

        foreach (var child in group.Children)
        {
            if (child.Kind == OsdElementKind.Group)
            {
                var nested = BuildGroup(child, profile, style, theme, depth + 1);
                if (nested is not null)
                {
                    if (children.Count > 0)
                    {
                        width += style.ColumnSpacing;
                    }

                    children.Add(nested);
                    width += nested.FrameWidth ?? nested.Width ?? 0;
                    height = Math.Max(height, nested.FrameHeight ?? nested.Height ?? 0);
                }

                continue;
            }

            var value = BuildValueNode(child, profile, style, theme, showLabel: false);
            if (value is null)
            {
                continue;
            }

            if (children.Count > 0)
            {
                width += style.ColumnSpacing;
            }

            children.Add(value);
            width += value.FrameWidth ?? value.Width ?? 0;
            height = Math.Max(height, value.FrameHeight ?? value.Height ?? 0);
        }

        if (children.Count == 0)
        {
            return new TileNode
            {
                NodeId = NodeId(profile, "empty-group", group.Id),
                Type = "panel",
                Kind = "panel",
                Name = group.Label,
                Layout = "horizontal",
                Visible = group.Visible,
            };
        }

        return new TileNode
        {
            NodeId = NodeId(profile, "columns-group", group.Id),
            Type = "panel",
            Kind = "panel",
            Name = group.Label,
            Layout = "horizontal",
            Gap = style.ColumnSpacing,
            Padding = depth > 0 ? 4 : 0,
            FrameWidth = width,
            FrameHeight = Math.Max(1, height),
            Width = width,
            Height = Math.Max(1, height),
            Visible = group.Visible,
            Children = children,
        };
    }

    private static TileNode? BuildElementRow(OsdElement element, OsdProfile profile,
        OsdStyleSettings style, TileTheme theme, bool showLabel)
    {
        switch (element.Kind)
        {
            case OsdElementKind.GroupHeader:
            case OsdElementKind.DeviceHeader:
            case OsdElementKind.CustomText:
            {
                var text = string.IsNullOrWhiteSpace(element.Text) ? element.Label : element.Text;
                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }

                return TextNode(element, profile, style, text,
                    element.FontSize > 0 ? element.FontSize : style.FontSize * 0.92,
                    LabelColor(element, style), int.MaxValue, Align(element.Align));
            }
            case OsdElementKind.EmptyLine:
                return new TileNode
                {
                    NodeId = NodeId(profile, "empty", element.Id),
                    Type = "panel",
                    Kind = "panel",
                    Layout = "vertical",
                    FrameWidth = 1,
                    FrameHeight = Math.Max(1, element.Height ?? 6),
                    Width = 1,
                    Height = Math.Max(1, element.Height ?? 6),
                    Visible = element.Visible,
                };
            case OsdElementKind.Divider:
                return new TileNode
                {
                    NodeId = NodeId(profile, "divider", element.Id),
                    Type = "panel",
                    Kind = "panel",
                    Layout = "vertical",
                    FrameWidth = 1,
                    FrameHeight = 1,
                    Width = 1,
                    Height = 1,
                    Background = true,
                    BackgroundColor = style.SeparatorColor,
                    Color = style.SeparatorColor,
                    Visible = element.Visible,
                };
            default:
                return BuildValueRow(element, profile, style, theme, showLabel);
        }
    }

    private static TileNode? BuildValueRow(OsdElement element, OsdProfile profile,
        OsdStyleSettings style, TileTheme theme, bool showLabel)
    {
        var value = BuildValueNode(element, profile, style, theme, showLabel: false);
        if (value is null)
        {
            return null;
        }

        var children = new List<TileNode>();
        var width = 0.0;
        var height = 0.0;
        if (showLabel && style.ShowLabels && !string.IsNullOrWhiteSpace(DisplayLabel(element)))
        {
            var label = TextNode(element, profile, style, DisplayLabel(element),
                LabelSize(element, style), LabelColor(element, style), int.MaxValue,
                Align(element.Align));
            children.Add(label);
            width += label.FrameWidth ?? 0;
            height = Math.Max(height, label.FrameHeight ?? 0);
        }

        if (children.Count > 0)
        {
            width += LabelGap;
        }

        children.Add(value);
        width += value.FrameWidth ?? value.Width ?? 0;
        height = Math.Max(height, value.FrameHeight ?? value.Height ?? 0);

        if (element.Kind is OsdElementKind.Graph || element.ShowGraph)
        {
            var chart = ChartNode(element, profile, style, theme);
            if (chart is not null)
            {
                width += 6 + (chart.FrameWidth ?? 54);
                height = Math.Max(height, chart.FrameHeight ?? 30);
                children.Add(chart);
            }
        }

        if (element.Kind == OsdElementKind.ProgressBar || element.ShowProgress)
        {
            var bar = BarNode(element, profile, style, theme);
            if (bar is not null)
            {
                width += 6 + (bar.FrameWidth ?? 54);
                height = Math.Max(height, bar.FrameHeight ?? 10);
                children.Add(bar);
            }
        }

        if (element.Kind == OsdElementKind.Ring)
        {
            var gauge = GaugeNode(element, profile, style, theme);
            if (gauge is not null)
            {
                children.Insert(children.Count - 1, gauge);
                width += (gauge.FrameWidth ?? 24) + 6;
                height = Math.Max(height, gauge.FrameHeight ?? 24);
            }
        }

        return new TileNode
        {
            NodeId = NodeId(profile, "row", element.Id),
            Type = "panel",
            Kind = "panel",
            Name = DisplayLabel(element),
            Layout = "horizontal",
            Gap = LabelGap,
            Padding = 0,
            FrameWidth = width,
            FrameHeight = Math.Max(1, height),
            Width = width,
            Height = Math.Max(1, height),
            Visible = element.Visible,
            Children = children,
        };
    }

    private static TileNode? BuildValueNode(OsdElement element, OsdProfile profile,
        OsdStyleSettings style, TileTheme theme, bool showLabel)
    {
        if (!element.IsSensorLike)
        {
            return null;
        }

        var definition = MetricCatalog.Find(element.SensorId) ??
                         MetricCatalog.FindBySensorId(element.SensorId);
        var key = MetricTileKeys.For(element.SensorId);
        var fontSize = element.FontSize > 0 ? element.FontSize : style.FontSize;
        var unitSize = Math.Max(8, fontSize * style.UnitScale);
        var labelSize = LabelSize(element, style);
        var samples = definition is not null
            ? MetricWidths.For(definition, element.Unit)
            : MetricWidths.Generic(element.Unit);
        var valueWidth = TextWidth(samples.Value, fontSize);
        var unitText = style.ShowUnits ? DisplayUnit(element, definition) : string.Empty;
        var unitWidth = string.IsNullOrWhiteSpace(unitText) ? 0 : TextWidth(unitText, unitSize) + ValueGap;
        var labelText = showLabel && style.ShowLabels ? DisplayLabel(element) : string.Empty;
        var labelWidth = string.IsNullOrWhiteSpace(labelText) ? 0 : LabelTextWidth(labelText, labelSize) + LabelGap;
        var width = labelWidth + valueWidth + unitWidth;
        var height = Math.Max(fontSize * 1.35, labelSize * 1.35);

        var node = new TileNode
        {
            NodeId = NodeId(profile, "value", element.Id),
            Type = "metric",
            Kind = "metric",
            Name = DisplayLabel(element),
            Key = key,
            Label = labelText,
            LabelPosition = string.IsNullOrWhiteSpace(labelText) ? "none" : "left",
            LabelSize = labelSize,
            ValueSize = fontSize,
            UnitSize = unitSize,
            Unit = unitText,
            Digits = element.Decimals >= 0 ? element.Decimals : null,
            Format = FormatFor(element),
            Family = style.FontFamily,
            Align = Align(element.Align),
            ValueColor = ValueColor(element, style),
            LabelColor = LabelColor(element, style),
            UnitColor = UnitColor(element, style),
            FrameWidth = width,
            FrameHeight = height,
            Width = width,
            Height = height,
            Visible = element.Visible,
        };

        if (element.Kind is OsdElementKind.Graph or OsdElementKind.ProgressBar or OsdElementKind.Ring)
        {
            node.LabelPosition = string.IsNullOrWhiteSpace(labelText) ? "none" : "left";
        }

        return node;
    }

    private static TileNode? ChartNode(OsdElement element, OsdProfile profile,
        OsdStyleSettings style, TileTheme theme)
    {
        var key = MetricTileKeys.For(element.SensorId);
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        return new TileNode
        {
            NodeId = NodeId(profile, "chart", element.Id),
            Type = "chart",
            Kind = "chart",
            Name = DisplayLabel(element),
            Series = SeriesForKey(key),
            Key = key,
            Unit = DisplayUnit(element, MetricCatalog.Find(element.SensorId)),
            Color = string.IsNullOrWhiteSpace(element.GraphColor)
                ? style.LabelColor
                : element.GraphColor,
            ValueSize = style.FontSize,
            LabelSize = Math.Max(8, style.FontSize * style.LabelScale),
            Family = style.FontFamily,
            ShowLabels = false,
            ShowGridLines = false,
            CurrentValue = false,
            FrameWidth = 54,
            FrameHeight = Math.Max(20, style.FontSize * 1.5),
            Width = 54,
            Height = Math.Max(20, style.FontSize * 1.5),
            Visible = element.Visible,
        };
    }

    private static TileNode? BarNode(OsdElement element, OsdProfile profile,
        OsdStyleSettings style, TileTheme theme)
    {
        var key = MetricTileKeys.For(element.SensorId);
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        return new TileNode
        {
            NodeId = NodeId(profile, "bar", element.Id),
            Type = "bar",
            Kind = "bar",
            Name = DisplayLabel(element),
            Key = key,
            Label = string.Empty,
            LabelPosition = "none",
            Minimum = element.ProgressMin ?? 0,
            Maximum = element.ProgressMax ?? 100,
            Color = style.LabelColor,
            TrackColor = style.SeparatorColor,
            ShowValue = false,
            FrameWidth = 54,
            FrameHeight = 10,
            Width = 54,
            Height = 10,
            Visible = element.Visible,
        };
    }

    private static TileNode? GaugeNode(OsdElement element, OsdProfile profile,
        OsdStyleSettings style, TileTheme theme)
    {
        var key = MetricTileKeys.For(element.SensorId);
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        if (element.ProgressMax is null && element.SensorId is "ram.used" or "vram.used")
        {
            // The row engine has no honest 0..100 scale for absolute memory usage unless the
            // element explicitly supplies one. Keep the value row instead of drawing a fake ring.
            return null;
        }

        var minimum = element.ProgressMin ?? 0;
        var maximum = element.ProgressMax;
        if (maximum is null)
        {
            maximum = 100;
        }

        return new TileNode
        {
            NodeId = NodeId(profile, "gauge", element.Id),
            Type = "gauge",
            Kind = "gauge",
            Name = DisplayLabel(element),
            Key = key,
            Label = string.Empty,
            LabelPosition = "none",
            Minimum = minimum,
            Maximum = maximum,
            Diameter = 24,
            Thickness = 3,
            ShowValue = false,
            Color = style.LabelColor,
            TrackColor = style.SeparatorColor,
            LabelColor = LabelColor(element, style),
            FrameWidth = 24,
            FrameHeight = 24,
            Width = 24,
            Height = 24,
            Visible = element.Visible,
        };
    }

    private static TileNode BuildInlineRoot(OsdProfile profile, OsdStyleSettings style,
        TileTheme theme)
    {
        var children = new List<TileNode>();
        var width = 0.0;
        var height = 0.0;
        foreach (var element in profile.Elements)
        {
            if (element.Kind == OsdElementKind.Group)
            {
                foreach (var child in element.Children)
                {
                    var label = InlineLabel(element, child);
                    var node = BuildValueNode(child, profile, style, theme, showLabel: false);
                    if (node is null)
                    {
                        continue;
                    }

                    if (!element.Visible || !child.Visible)
                    {
                        node.Visible = false;
                    }

                    if (style.ShowLabels && !string.IsNullOrWhiteSpace(label))
                    {
                        node.Label = label;
                        node.LabelPosition = "left";
                        node.LabelSize = Math.Max(8, style.FontSize * style.LabelScale);
                        node.FrameWidth = (node.FrameWidth ?? 0) + LabelTextWidth(label, node.LabelSize ?? 12) + LabelGap;
                        node.Width = node.FrameWidth;
                    }

                    children.Add(node);
                    width += node.FrameWidth ?? 0;
                    height = Math.Max(height, node.FrameHeight ?? 0);
                    if (children.Count > 1)
                    {
                        width += style.ColumnSpacing;
                    }
                }
            }
            else
            {
                var node = BuildValueRow(element, profile, style, theme, style.ShowLabels);
                if (node is null)
                {
                    continue;
                }

                children.Add(node);
                width += node.FrameWidth ?? 0;
                height = Math.Max(height, node.FrameHeight ?? 0);
                if (children.Count > 1)
                {
                    width += style.ColumnSpacing;
                }
            }
        }

        return new TileNode
        {
            NodeId = NodeId(profile, "inline-root"),
            Type = "panel",
            Kind = "panel",
            Name = profile.Name + " row",
            Layout = "horizontal",
            Gap = style.ColumnSpacing,
            FrameWidth = Math.Max(1, width),
            FrameHeight = Math.Max(1, height),
            Width = Math.Max(1, width),
            Height = Math.Max(1, height),
            Children = children,
        };
    }

    private static TileNode TextNode(OsdElement element, OsdProfile profile, OsdStyleSettings style,
        string text, double size, string color, int maxWidth, string align)
    {
        if (style.UseUpperLabels)
        {
            text = text.ToUpperInvariant();
        }

        var width = Math.Min(maxWidth, Math.Max(26, LabelTextWidth(text, size) + 6));
        var height = Math.Max(10, size * 1.35);
        return new TileNode
        {
            NodeId = NodeId(profile, "text", element.Id),
            Type = "text",
            Kind = "text",
            Name = text,
            Text = text,
            Size = size,
            Color = color,
            Family = style.FontFamily,
            Align = align,
            FrameWidth = width,
            FrameHeight = height,
            Width = width,
            Height = height,
            Visible = element.Visible,
        };
    }

    private static string DisplayLabel(OsdElement element)
    {
        if (!string.IsNullOrWhiteSpace(element.Label))
        {
            return element.Label.Trim();
        }

        if (!string.IsNullOrWhiteSpace(element.Text))
        {
            return element.Text.Trim();
        }

        return MetricCatalog.Find(element.SensorId)?.DisplayName ?? element.SensorId;
    }

    private static string InlineLabel(OsdElement group, OsdElement element)
    {
        var label = DisplayLabel(element);
        var groupLabel = string.IsNullOrWhiteSpace(group.Label) ? string.Empty : group.Label.Trim();
        return groupLabel.Length == 0 || label.Contains(groupLabel, StringComparison.OrdinalIgnoreCase)
            ? label
            : groupLabel + " " + label;
    }

    private static string DisplayUnit(OsdElement element, MetricDefinition? definition)
    {
        if (!string.IsNullOrWhiteSpace(element.Unit))
        {
            return element.Unit.Trim();
        }

        return definition?.SampleUnit ?? definition?.Unit ?? string.Empty;
    }

    private static string? FormatFor(OsdElement element)
        => element.Decimals switch
        {
            0 => "%.0f",
            1 => "%.1f",
            2 => "%.2f",
            3 => "%.3f",
            _ => null,
        };

    private static double LabelSize(OsdElement element, OsdStyleSettings style)
        => Math.Max(8, element.FontSize > 0 ? element.FontSize * style.LabelScale : style.FontSize * style.LabelScale);

    private static string LabelColor(OsdElement element, OsdStyleSettings style)
        => string.IsNullOrWhiteSpace(element.LabelColor) ? style.LabelColor :
            AppSettings.SanitizeColor(element.LabelColor, style.LabelColor);

    private static string ValueColor(OsdElement element, OsdStyleSettings style)
        => string.IsNullOrWhiteSpace(element.ValueColor) ? style.ValueColor :
            AppSettings.SanitizeColor(element.ValueColor, style.ValueColor);

    private static string UnitColor(OsdElement element, OsdStyleSettings style)
        => string.IsNullOrWhiteSpace(element.UnitColor) ? style.UnitColor :
            AppSettings.SanitizeColor(element.UnitColor, style.UnitColor);

    private static string Align(OsdTextAlign align) => align switch
    {
        OsdTextAlign.Center => "center",
        OsdTextAlign.Right => "right",
        _ => "left",
    };

    private static double TextWidth(string? text, double size)
        => Math.Max(1, (text?.Length ?? 0) * size * TextWidthFactor);

    private static double LabelTextWidth(string? text, double size)
        => Math.Max(1, (text?.Length ?? 0) * size * LabelWidthFactor);

    private static string SeriesForKey(string key) => key switch
    {
        "fpsCurrent" or "fps" => "fps",
        "frametimeCurrent" or "frametime" => "frame.time",
        "fps1pctLow" => "frame.fps.1low",
        _ => key,
    };

    private static string NodeId(OsdProfile profile, string kind, string? suffix = null)
        => "mig-" + profile.Id + "-" + kind + (string.IsNullOrWhiteSpace(suffix) ? string.Empty : "-" + suffix);
}
