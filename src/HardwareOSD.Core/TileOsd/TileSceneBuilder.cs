using System.Globalization;
using HardwareOSD.Core.Config;
using HardwareOSD.Core.Fps;
using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.Metrics;

namespace HardwareOSD.Core.TileOsd;

/// <summary>
/// Resolves a saved tile design against one sensor/FPS snapshot and produces a WPF-free draw list.
/// Geometry comes from the design document's lifted editor frames; widgets without a saved frame
/// (group members) are arranged by the container's flow rule. Values are never invented: a key with
/// no real source becomes "N/A" and an empty bar/gauge.
/// </summary>
public static class TileSceneBuilder
{
    public static TileScene Build(
        TileDesignDocument design,
        SensorSnapshot snapshot,
        FpsSnapshot? fps,
        SensorHistory? history,
        TileOverrideSettings? overrides = null,
        TileRenderContext? context = null,
        IDictionary<string, TileRect>? bounds = null)
    {
        ArgumentNullException.ThrowIfNull(design);
        overrides ??= new TileOverrideSettings();
        context ??= TileRenderContext.Empty;

        var elements = new List<TileSceneElement>(256);
        var rootRect = new TileRect(0, 0, design.CanvasWidth, design.CanvasHeight);
        var root = design.Root;
        bounds?.Clear();
        bounds?[string.Empty] = rootRect;
        if (root.Background == true)
        {
            AddPanel(elements, root, rootRect, null, design.Theme, overrides, context);
        }

        AppendChildren(root, string.Empty, rootRect, rootRect, design, snapshot, fps, history,
            overrides, context, elements, bounds);
        return new TileScene
        {
            Width = rootRect.Width,
            Height = rootRect.Height,
            Elements = elements,
        };
    }

    private static void AppendChildren(
        TileNode parent,
        string path,
        TileRect parentRect,
        TileRect clip,
        TileDesignDocument design,
        SensorSnapshot snapshot,
        FpsSnapshot? fps,
        SensorHistory? history,
        TileOverrideSettings overrides,
        TileRenderContext context,
        List<TileSceneElement> elements,
        IDictionary<string, TileRect>? bounds)
    {
        var padding = Math.Max(0, parent.Padding ?? 0);
        var content = parentRect.Inset(padding);
        var gap = Math.Max(0, parent.Gap ?? design.Theme.Gap);

        // Explicit frames win. Anything without one is laid out by the parent's flow rule.
        var flow = BuildFlow(parent, content, gap, design.Theme);

        // A panel with one child is the wrapper the CapFrameX compiler emits around every
        // top-level tile; its child fills the padded content box. A canvas panel is authored
        // in the builder instead, so its children keep the geometry the user dragged.
        var singleChild = parent.Children.Count == 1 && parent.FlowLayout != TileFlowLayout.Canvas;

        for (var i = 0; i < parent.Children.Count; i++)
        {
            var child = parent.Children[i];
            if (child.Visible == false)
            {
                continue;
            }

            var childPath = path.Length == 0 ? i.ToString() : path + "/" + i;
            var nodeOverride = overrides.For(childPath);
            if (nodeOverride?.Visible == false)
            {
                continue;
            }

            TileRect rect;
            if (singleChild)
            {
                // A wrapper panel exists only to give its single widget the panel chrome; the
                // widget's saved frame is the wrapper's absolute frame, not a local offset.
                rect = content;
            }
            else if (child.X is not null && child.Y is not null)
            {
                rect = LocalRect(child, content);
            }
            else
            {
                rect = flow[i];
            }

            bounds?[childPath] = rect;
            AppendNode(child, childPath, rect, content, clip, design, snapshot, fps, history,
                overrides, context, elements, bounds);
        }
    }

    private static void AppendNode(
        TileNode node,
        string path,
        TileRect rect,
        TileRect containerRect,
        TileRect clip,
        TileDesignDocument design,
        SensorSnapshot snapshot,
        FpsSnapshot? fps,
        SensorHistory? history,
        TileOverrideSettings overrides,
        TileRenderContext context,
        List<TileSceneElement> elements,
        IDictionary<string, TileRect>? bounds)
    {
        if (rect.IsEmpty)
        {
            return;
        }

        var nodeOverride = overrides.For(path);
        if (node.NodeKind == TileNodeKind.Panel)
        {
            if (node.Background == true || node.BackgroundColor is not null || node.BorderWidth is > 0)
            {
                AddPanel(elements, node, rect, clip, design.Theme, overrides, context);
            }

            var childClip = node.Clip == false ? clip : TileRect.Intersect(clip, rect);
            AppendChildren(node, path, rect, childClip, design, snapshot, fps, history,
                overrides, context, elements, bounds);
            return;
        }

        var theme = design.Theme;
        var opacity = Math.Clamp(overrides.Opacity, 0, 1);
        switch (node.NodeKind)
        {
            case TileNodeKind.Metric:
                AddMetric(elements, node, nodeOverride, rect, clip, theme, snapshot, fps, history, context);
                break;
            case TileNodeKind.Text:
                AddText(elements, node, nodeOverride, rect, clip, theme, context);
                break;
            case TileNodeKind.Chart:
                AddChart(elements, node, nodeOverride, rect, clip, theme, history);
                break;
            case TileNodeKind.Bar:
                AddBar(elements, node, nodeOverride, rect, containerRect, clip, theme, snapshot, fps,
                    history, context);
                break;
            case TileNodeKind.Gauge:
                AddGauge(elements, node, nodeOverride, rect, containerRect, clip, theme, snapshot, fps,
                    history, context);
                break;
            case TileNodeKind.Icon:
                AddIcon(elements, node, rect, clip, theme);
                break;
            case TileNodeKind.Image:
                AddImage(elements, node, rect, clip, opacity, context);
                break;
            case TileNodeKind.Heatmap:
                AddHeatmap(elements, node, nodeOverride, rect, clip, theme, snapshot, fps, history,
                    context);
                break;
            case TileNodeKind.ClassicRows:
                elements.Add(new TileClassicRowsScene
                {
                    Bounds = rect,
                    Clip = clip,
                    RowScale = Math.Clamp(node.RowScale ?? 1, 0.5, 2),
                });
                break;
        }
    }

    private static void AddPanel(
        List<TileSceneElement> elements,
        TileNode node,
        TileRect rect,
        TileRect? clip,
        TileTheme theme,
        TileOverrideSettings overrides,
        TileRenderContext context)
    {
        var fill = node.Background == true
            ? TileColor.Parse(node.BackgroundColor ?? node.Color, theme.PanelColor)
            : TileColor.Parse(node.BackgroundColor, TileColor.Transparent);
        fill = WithOpacity(fill, overrides.Opacity);
        var border = TileColor.Parse(node.BorderColor, TileColor.Transparent);
        elements.Add(new TilePanelScene
        {
            Bounds = rect,
            Clip = clip,
            Fill = fill,
            Border = border,
            BorderWidth = Math.Clamp(node.BorderWidth ?? 0, 0, 32),
            Radius = Math.Clamp(node.Radius ?? theme.Radius, 0, 32),
        });
        _ = context;
    }

    private static void AddMetric(
        List<TileSceneElement> elements,
        TileNode node,
        TileNodeOverride? nodeOverride,
        TileRect rect,
        TileRect clip,
        TileTheme theme,
        SensorSnapshot snapshot,
        FpsSnapshot? fps,
        SensorHistory? history,
        TileRenderContext context)
    {
        var key = nodeOverride?.Key ?? node.Key;
        var secondaryKey = nodeOverride?.SecondaryKey ?? node.SecondaryKey;
        var definition = DefinitionForMetricKey(key);
        var primary = TileSensorMap.Resolve(key, snapshot, fps, history, context);
        var explicitScale = nodeOverride?.ValueScale ?? node.ValueScale;
        var digits = nodeOverride?.Digits ?? node.Digits;
        var unit = nodeOverride?.Unit ?? node.Unit ?? primary.Unit;
        var scale = ScaleFor(primary, explicitScale, unit);

        var value = TileSensorMap.Format(primary, node.Format, digits, scale);
        if (!primary.Available)
        {
            value = TileSensorMap.Format(primary, node.Format, digits, 1);
        }

        var secondary = string.IsNullOrWhiteSpace(secondaryKey)
            ? default
            : TileSensorMap.Resolve(secondaryKey, snapshot, fps, history, context);
        var secondaryUnit = string.IsNullOrWhiteSpace(secondaryKey) || !secondary.Available
            ? string.Empty
            : node.SecondaryUnit ?? secondary.Unit;
        var secondaryScale = ScaleFor(secondary, node.SecondaryScale, secondaryUnit);
        var secondaryValue = string.IsNullOrWhiteSpace(secondaryKey)
            ? string.Empty
            : TileSensorMap.Format(secondary, node.Format, node.SecondaryDigits ?? digits,
                secondaryScale);
        if (!string.IsNullOrWhiteSpace(secondaryKey) && !secondary.Available)
        {
            secondaryValue = TileSensorMap.Format(secondary, node.Format, node.SecondaryDigits ?? digits, 1);
            secondaryUnit = string.Empty;
        }

        // RAM/VRAM "used" metrics carry a real total reading next to them. This is the same
        // used/total pair the Metrics page shows; it is not a synthetic maximum.
        if (definition is { Id: "ram.used" or "vram.used" } &&
            node.SecondaryKey is null && primary.Available && double.IsFinite(primary.Numeric))
        {
            var totalKey = definition.Id == "ram.used" ? "ram.total" : "vram.total";
            var total = TileSensorMap.Resolve(totalKey, snapshot, fps, history, context);
            if (total.Available && double.IsFinite(total.Numeric))
            {
                var useGigabytes = !string.Equals(unit, "MB", StringComparison.OrdinalIgnoreCase);
                var divisor = useGigabytes ? 1024.0 : 1.0;
                var displayUnit = useGigabytes ? "GB" : "MB";
                var memoryDigits = digits ?? (useGigabytes ? 1 : 0);
                var format = "F" + Math.Clamp(memoryDigits, 0, 3);
                value = (primary.Numeric / divisor).ToString(format, CultureInfo.InvariantCulture);
                secondaryValue = (total.Numeric / divisor).ToString(format, CultureInfo.InvariantCulture);
                unit = string.Empty;
                secondaryUnit = displayUnit;
            }
        }

        var severity = MetricSeverity.Normal;
        if (primary.Available && double.IsFinite(primary.Numeric))
        {
            if (node.CriticalThreshold is { } critical && primary.Numeric >= critical)
            {
                severity = MetricSeverity.Critical;
            }
            else if (node.WarningThreshold is { } warning && primary.Numeric >= warning)
            {
                severity = MetricSeverity.Warning;
            }
        }

        var label = nodeOverride?.Label ?? node.Label ?? string.Empty;
        var valueColor = TileColor.Parse(nodeOverride?.ValueColor ?? node.ValueColor ?? node.Color,
            theme.TextColor);
        if (!primary.Available)
        {
            valueColor = valueColor.WithAlpha(120);
            unit = string.Empty;
        }
        else if (severity == MetricSeverity.Critical)
        {
            valueColor = TileColor.Parse(context.CriticalColor, valueColor);
        }
        else if (severity == MetricSeverity.Warning)
        {
            valueColor = TileColor.Parse(context.WarningColor, valueColor);
        }

        var graphValues = Array.Empty<double>();
        if (node.MiniGraph == true)
        {
            var graphSeries = node.Series;
            if (string.IsNullOrWhiteSpace(graphSeries) && definition is not null)
            {
                graphSeries = MetricCatalog.SensorIdFor(definition);
            }

            if (!string.IsNullOrWhiteSpace(graphSeries))
            {
                graphValues = LimitHistory(ReadSeries(history, graphSeries),
                    node.HistoryPoints ?? 60);
            }
        }

        var labelColor = TileColor.Parse(nodeOverride?.LabelColor ?? node.LabelColor,
            theme.SubtextColor);
        elements.Add(new TileMetricScene
        {
            Bounds = rect,
            Clip = clip,
            Label = label,
            Value = value,
            Unit = unit,
            SecondaryValue = secondaryValue,
            SecondaryUnit = secondaryUnit,
            Separator = node.Separator ?? " / ",
            LabelPosition = node.LabelPosition ?? "top",
            ValueSize = Math.Clamp(nodeOverride?.ValueSize ?? node.ValueSize ?? 20, 6, 96),
            LabelSize = Math.Clamp(node.LabelSize ?? node.Size ?? theme.FontSize, 6, 72),
            UnitSize = Math.Clamp(node.UnitSize ?? node.ValueSize ?? 12, 6, 72),
            Family = node.Family ?? theme.FontFamily,
            Semibold = IsSemibold(node.Weight),
            ValueColor = valueColor,
            LabelColor = labelColor,
            UnitColor = TileColor.Parse(node.UnitColor, valueColor.WithAlpha(190)),
            Align = ParseAlign(node.Align),
            LabelAlign = ParseAlign(node.LabelAlign ?? node.Align),
            Available = primary.Available,
            ValueLines = Math.Clamp(node.ValueLines ?? 1, 1, 16),
            Severity = severity,
            MiniGraph = node.MiniGraph == true && graphValues.Length > 1,
            GraphValues = graphValues,
            GraphColor = TileColor.Parse(node.GraphColor ?? node.Color, theme.AccentColor),
            GraphThickness = Math.Clamp(node.Thickness ?? 1.5, 1, 6),
            GraphOpacity = Math.Clamp(node.Opacity ?? 0.9, 0.05, 1),
        });
    }

    private static MetricDefinition? DefinitionForMetricKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var raw = key.StartsWith("metric:", StringComparison.OrdinalIgnoreCase) ? key[7..] : key;
        return MetricCatalog.FindByTileKey(raw);
    }

    private static MetricSeverity SeverityFor(TileNode node, TileReading reading)
    {
        if (!reading.Available || !double.IsFinite(reading.Numeric))
        {
            return MetricSeverity.Normal;
        }

        if (node.CriticalThreshold is { } critical && reading.Numeric >= critical)
        {
            return MetricSeverity.Critical;
        }

        if (node.WarningThreshold is { } warning && reading.Numeric >= warning)
        {
            return MetricSeverity.Warning;
        }

        return MetricSeverity.Normal;
    }

    private static TileColor SeverityColor(MetricSeverity severity, TileRenderContext context,
        TileColor fallback) => severity switch
    {
        MetricSeverity.Critical => TileColor.Parse(context.CriticalColor, fallback),
        MetricSeverity.Warning => TileColor.Parse(context.WarningColor, fallback),
        _ => fallback,
    };

    private static void AddText(
        List<TileSceneElement> elements,
        TileNode node,
        TileNodeOverride? nodeOverride,
        TileRect rect,
        TileRect clip,
        TileTheme theme,
        TileRenderContext context)
    {
        var text = nodeOverride?.Label ?? node.Text ?? node.Label ?? string.Empty;
        text = context.Expand(text);
        var lines = Math.Max(1, text.Count(character => character == '\n') + 1);
        elements.Add(new TileTextScene
        {
            Bounds = rect,
            Clip = clip,
            Text = text,
            FontSize = Math.Clamp(nodeOverride?.ValueSize ?? node.Size ?? theme.FontSize, 6, 96),
            Family = node.Family ?? theme.FontFamily,
            Semibold = IsSemibold(node.Weight) ||
                       string.Equals(node.Weight, "bold", StringComparison.OrdinalIgnoreCase),
            Color = TileColor.Parse(node.Color ?? node.TextColor, theme.TextColor),
            Align = ParseAlign(node.Align),
            MaxLines = Math.Clamp(node.ValueLines ?? lines, 1, 16),
        });
    }

    private static void AddChart(
        List<TileSceneElement> elements,
        TileNode node,
        TileNodeOverride? nodeOverride,
        TileRect rect,
        TileRect clip,
        TileTheme theme,
        SensorHistory? history)
    {
        var primaryColor = TileColor.Parse(nodeOverride?.ChartColor ?? node.Color, theme.AccentColor);
        var lines = new List<TileChartLineScene>();
        if (node.Lines is { Count: > 0 })
        {
            foreach (var line in node.Lines)
            {
                var series = SeriesToHistory(line.Series ?? node.Series);
                var values = LimitHistory(ReadSeries(history, series), node.HistoryPoints);
                if (string.Equals(line.Kind, "stutter", StringComparison.OrdinalIgnoreCase))
                {
                    var threshold = StutterThreshold(values, line.Factor ?? 2.5);
                    lines.Add(new TileChartLineScene
                    {
                        Label = line.Label ?? "Stutter",
                        Kind = TileChartLineKind.Stutter,
                        Color = TileColor.Parse(line.Color, TileColor.Parse("#E5534B", theme.AccentColor)),
                        Values = double.IsFinite(threshold) ? new[] { threshold } : Array.Empty<double>(),
                        ScaleMin = 0,
                        ScaleMax = 1,
                    });
                }
                else
                {
                    lines.Add(new TileChartLineScene
                    {
                        Label = line.Label ?? line.Series ?? string.Empty,
                        Kind = TileChartLineKind.Value,
                        Color = TileColor.Parse(line.Color, primaryColor),
                        Values = values,
                        ScaleMin = 0,
                        ScaleMax = 1,
                    });
                }
            }
        }
        else
        {
            lines.Add(new TileChartLineScene
            {
                Label = node.Title ?? node.Label ?? string.Empty,
                Kind = TileChartLineKind.Value,
                Color = primaryColor,
                Values = LimitHistory(ReadSeries(history, SeriesToHistory(node.Series)),
                    node.HistoryPoints),
                ScaleMin = 0,
                ScaleMax = 1,
            });
        }

        var all = lines.Where(line => line.Kind == TileChartLineKind.Value)
            .SelectMany(line => line.Values)
            .Where(double.IsFinite)
            .ToArray();
        var min = all.Length == 0 ? 0 : all.Min();
        var max = all.Length == 0 ? 1 : all.Max();
        if (max - min < 1)
        {
            max = min + 1;
        }

        lines = lines.Select(line => new TileChartLineScene
        {
            Label = line.Label,
            Kind = line.Kind,
            Color = line.Color,
            Values = line.Values,
            ScaleMin = min,
            ScaleMax = max,
        }).ToList();

        elements.Add(new TileChartScene
        {
            Bounds = rect,
            Clip = clip,
            Title = node.Title ?? node.Label ?? string.Empty,
            Unit = node.Unit ?? "ms",
            Lines = lines,
            LineColor = primaryColor,
            LabelColor = TileColor.Parse(node.LabelColor, theme.SubtextColor),
            BackgroundColor = TileColor.Parse(node.BackgroundColor,
                TileColor.Parse("#0E0E16EE", TileColor.Transparent)),
            BorderColor = TileColor.Parse(node.BorderColor, new TileColor(58, 58, 74, 128)),
            ShowBackground = node.ShowBackground ?? node.BackgroundColor is not null,
            ShowBorder = node.ShowBorder ?? node.BorderColor is not null,
            ShowLabels = node.ShowLabels ?? true,
            ShowGridLines = node.ShowGridLines ?? node.GridLines ?? false,
            CurrentValue = node.CurrentValue ?? false,
            Family = node.Family ?? theme.FontFamily,
            ScaleMin = min,
            ScaleMax = max,
            LineThickness = Math.Clamp(node.Thickness ?? 2, 1, 8),
            Opacity = Math.Clamp(node.Opacity ?? 1, 0.05, 1),
        });
    }

    private static void AddBar(
        List<TileSceneElement> elements,
        TileNode node,
        TileNodeOverride? nodeOverride,
        TileRect rect,
        TileRect container,
        TileRect clip,
        TileTheme theme,
        SensorSnapshot snapshot,
        FpsSnapshot? fps,
        SensorHistory? history,
        TileRenderContext context)
    {
        var reading = TileSensorMap.Resolve(nodeOverride?.Key ?? node.Key, snapshot, fps, history, context);
        var minimum = nodeOverride?.BarMinimum ?? node.EffectiveMinimum;
        var maximum = nodeOverride?.BarMaximum ?? node.EffectiveMaximum;
        if (maximum <= minimum)
        {
            maximum = minimum + 1;
        }

        var fraction = reading.Available && double.IsFinite(reading.Numeric)
            ? Math.Clamp((reading.Numeric - minimum) / (maximum - minimum), 0, 1)
            : 0;
        var value = TileSensorMap.Format(reading, null, nodeOverride?.Digits ?? node.Digits,
            ScaleFor(reading, nodeOverride?.ValueScale ?? node.ValueScale, nodeOverride?.Unit ?? node.Unit));
        var unit = nodeOverride?.Unit ?? node.Unit ?? reading.Unit;
        var orientation = node.BarOrientation;
        var chip = orientation == TileBarOrientation.Vertical && rect.Width > rect.Height * 2;
        var barBounds = orientation == TileBarOrientation.Horizontal
            ? new TileRect(rect.X, rect.Bottom - Math.Max(4, node.Height ?? 8),
                rect.Width, Math.Max(4, node.Height ?? 8))
            : chip
                ? new TileRect(rect.X, rect.Y, rect.Width,
                    Math.Min(rect.Height, Math.Max(8, node.Height ?? 32)))
                : new TileRect(rect.X, rect.Y, rect.Width, rect.Height);

        var severity = SeverityFor(node, reading);
        var fill = SeverityColor(severity, context,
            TileColor.Parse(node.Color, theme.AccentColor));
        var valueColor = SeverityColor(severity, context,
            TileColor.Parse(nodeOverride?.ValueColor ?? node.ValueColor, theme.TextColor));
        elements.Add(new TileBarScene
        {
            Bounds = rect,
            BarBounds = barBounds,
            Clip = clip,
            Label = nodeOverride?.Label ?? node.Label ?? string.Empty,
            Value = value,
            Unit = unit,
            Fraction = fraction,
            BarThickness = Math.Max(2, node.Height ?? 8),
            Orientation = orientation,
            ShowValue = node.ShowValue ?? true,
            IsChip = chip,
            Family = node.Family ?? theme.FontFamily,
            Fill = fill,
            Track = TileColor.Parse(node.TrackColor, new TileColor(58, 58, 74, 128)),
            LabelColor = TileColor.Parse(nodeOverride?.LabelColor ?? node.LabelColor, theme.SubtextColor),
            ValueColor = valueColor,
            Available = reading.Available,
        });
    }

    private static void AddGauge(
        List<TileSceneElement> elements,
        TileNode node,
        TileNodeOverride? nodeOverride,
        TileRect rect,
        TileRect container,
        TileRect clip,
        TileTheme theme,
        SensorSnapshot snapshot,
        FpsSnapshot? fps,
        SensorHistory? history,
        TileRenderContext context)
    {
        var reading = TileSensorMap.Resolve(nodeOverride?.Key ?? node.Key, snapshot, fps, history, context);
        var minimum = nodeOverride?.BarMinimum ?? node.EffectiveMinimum;
        var maximum = nodeOverride?.BarMaximum ?? node.EffectiveMaximum;
        if (maximum <= minimum)
        {
            maximum = minimum + 1;
        }

        var fraction = reading.Available && double.IsFinite(reading.Numeric)
            ? Math.Clamp((reading.Numeric - minimum) / (maximum - minimum), 0, 1)
            : 0;
        var size = Math.Min(node.Diameter ?? Math.Min(rect.Width, rect.Height), Math.Min(rect.Width, rect.Height));
        size = Math.Max(16, size);
        var bounds = new TileRect(rect.X + (rect.Width - size) / 2, rect.Y,
            size, Math.Max(size, rect.Height));
        var severity = SeverityFor(node, reading);
        var fill = SeverityColor(severity, context,
            TileColor.Parse(node.Color, theme.AccentColor));
        var valueColor = SeverityColor(severity, context,
            TileColor.Parse(nodeOverride?.ValueColor ?? node.ValueColor, theme.TextColor));
        elements.Add(new TileGaugeScene
        {
            Bounds = bounds,
            Clip = clip,
            Label = nodeOverride?.Label ?? node.Label ?? string.Empty,
            Value = TileSensorMap.Format(reading, null, nodeOverride?.Digits ?? node.Digits,
                ScaleFor(reading, nodeOverride?.ValueScale ?? node.ValueScale,
                    nodeOverride?.Unit ?? node.Unit)),
            Unit = nodeOverride?.Unit ?? node.Unit ?? reading.Unit,
            Fraction = fraction,
            Thickness = Math.Clamp(node.Thickness ?? 5, 1, 32),
            StartAngle = node.StartAngle ?? 140,
            SweepAngle = Math.Clamp(node.SweepAngle ?? 260, 1, 360),
            ShowValue = node.ShowValue ?? true,
            Family = node.Family ?? theme.FontFamily,
            Fill = fill,
            Track = TileColor.Parse(node.TrackColor, new TileColor(58, 58, 74, 128)),
            LabelColor = TileColor.Parse(nodeOverride?.LabelColor ?? node.LabelColor, theme.SubtextColor),
            ValueColor = valueColor,
            Available = reading.Available,
        });
    }

    private static void AddIcon(
        List<TileSceneElement> elements,
        TileNode node,
        TileRect rect,
        TileRect clip,
        TileTheme theme)
    {
        elements.Add(new TileIconScene
        {
            Bounds = rect,
            Clip = clip,
            Glyph = node.Glyph ?? string.Empty,
            Family = node.Family ?? "Segoe MDL2 Assets",
            Size = Math.Clamp(node.Size ?? node.ValueSize ?? 16, 6, 96),
            Color = TileColor.Parse(node.Color, theme.AccentColor),
        });
    }

    private static void AddImage(
        List<TileSceneElement> elements,
        TileNode node,
        TileRect rect,
        TileRect clip,
        double opacity,
        TileRenderContext context)
    {
        if (string.IsNullOrWhiteSpace(node.DataUri) || !ImageMatchesHardware(node, context))
        {
            return;
        }

        elements.Add(new TileImageScene
        {
            Bounds = rect,
            Clip = clip,
            DataUri = node.DataUri,
            Fit = node.Fit ?? "contain",
            Opacity = Math.Clamp((node.Opacity ?? 1) * opacity, 0, 1),
        });
    }

    /// <summary>
    /// Hides vendor logo artwork when it does not match the machine HardwareOSD is running on.
    /// Template names may mention a reference platform; rendered vendor logos must not.
    /// </summary>
    private static bool ImageMatchesHardware(TileNode node, TileRenderContext context)
    {
        var isCpu = false;
        if (!string.IsNullOrWhiteSpace(node.Vendor))
        {
            var parts = node.Vendor.Split(':', 2, StringSplitOptions.TrimEntries);
            isCpu = string.Equals(parts[0], "cpu", StringComparison.OrdinalIgnoreCase);
            var actual = isCpu
                ? NormalizeVendor(context.CpuVendor ?? context.CpuName)
                : NormalizeVendor(context.GpuVendor ?? context.GpuName);
            var expected = parts.Length > 1 ? parts[1] : parts[0];
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(node.Model))
        {
            var actualName = isCpu ? context.CpuName : context.GpuName;
            if (string.IsNullOrWhiteSpace(actualName) ||
                !actualName.Contains(node.Model, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeVendor(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Advanced Micro", StringComparison.OrdinalIgnoreCase))
        {
            return "AMD";
        }

        if (text.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Arc", StringComparison.OrdinalIgnoreCase))
        {
            return "INTEL";
        }

        if (text.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("GeForce", StringComparison.OrdinalIgnoreCase))
        {
            return "NVIDIA";
        }

        return text.Trim();
    }

    private static void AddHeatmap(
        List<TileSceneElement> elements,
        TileNode node,
        TileNodeOverride? nodeOverride,
        TileRect rect,
        TileRect clip,
        TileTheme theme,
        SensorSnapshot snapshot,
        FpsSnapshot? fps,
        SensorHistory? history,
        TileRenderContext context)
    {
        var keys = node.Keys ?? new List<string>();
        if (keys.Count == 0)
        {
            return;
        }

        var titleHeight = string.IsNullOrWhiteSpace(node.Title) ? 0 : 18;
        var area = rect.Inset(0, titleHeight, 0, 0);
        var gap = Math.Max(0, node.Gap ?? 1.3);
        var columns = node.Columns is > 0
            ? Math.Clamp(node.Columns.Value, 1, 32)
            : Math.Clamp((int)Math.Floor((area.Width + gap) / Math.Max(4, node.Size ?? 12)), 1, 16);
        var rows = (keys.Count + columns - 1) / columns;
        var cellWidth = (area.Width - gap * (columns - 1)) / columns;
        var cellHeight = rows == 0 ? area.Height : (area.Height - gap * (rows - 1)) / rows;
        var cells = new List<TileHeatCell>(keys.Count);
        for (var i = 0; i < keys.Count; i++)
        {
            var row = i / columns;
            var column = i % columns;
            var reading = TileSensorMap.Resolve(keys[i], snapshot, fps, history, context);
            var minimum = nodeOverride?.BarMinimum ?? node.EffectiveMinimum;
            var maximum = nodeOverride?.BarMaximum ?? node.EffectiveMaximum;
            if (maximum <= minimum)
            {
                maximum = minimum + 1;
            }

            var fraction = reading.Available && double.IsFinite(reading.Numeric)
                ? Math.Clamp((reading.Numeric - minimum) / (maximum - minimum), 0, 1)
                : 0;
            cells.Add(new TileHeatCell(
                new TileRect(area.X + column * (cellWidth + gap), area.Y + row * (cellHeight + gap),
                    cellWidth, cellHeight),
                fraction,
                reading.Available ? reading.Text : "—"));
        }

        elements.Add(new TileHeatmapScene
        {
            Bounds = rect,
            Clip = clip,
            Title = node.Title ?? string.Empty,
            Cells = cells,
            CellColor = TileColor.Parse(nodeOverride?.ValueColor ?? node.Color, theme.TextColor),
            LabelColor = TileColor.Parse(nodeOverride?.LabelColor ?? node.LabelColor, theme.SubtextColor),
            CellGap = gap,
        });
    }

    private static TileRect LocalRect(TileNode node, TileRect content)
    {
        var width = node.FrameWidth ?? node.Width ?? content.Width;
        var height = node.FrameHeight ?? node.Height ?? EstimateHeight(node);
        return new TileRect(content.X + (node.X ?? 0), content.Y + (node.Y ?? 0),
            Math.Max(0, width), Math.Max(0, height));
    }

    private static TileRect[] BuildFlow(TileNode parent, TileRect content, double gap, TileTheme theme)
    {
        var result = new TileRect[parent.Children.Count];
        var indices = new List<int>();
        for (var i = 0; i < parent.Children.Count; i++)
        {
            var child = parent.Children[i];
            if (child.X is null || child.Y is null)
            {
                indices.Add(i);
            }
        }

        if (indices.Count == 0)
        {
            return result;
        }

        var verticalBars = parent.FlowLayout != TileFlowLayout.Grid && indices.All(index =>
            parent.Children[index].NodeKind == TileNodeKind.Bar &&
            parent.Children[index].BarOrientation == TileBarOrientation.Vertical);
        if (verticalBars)
        {
            var barWidth = indices.Select(index => parent.Children[index].Width ?? 10).DefaultIfEmpty(10).Max();
            var total = indices.Count * barWidth + Math.Max(0, indices.Count - 1) * gap;
            if (total > content.Width && indices.Count > 0)
            {
                barWidth = Math.Max(2, (content.Width - gap * (indices.Count - 1)) / indices.Count);
            }

            for (var position = 0; position < indices.Count; position++)
            {
                result[indices[position]] = new TileRect(content.X + position * (barWidth + gap),
                    content.Y, barWidth, content.Height);
            }

            return result;
        }

        if (parent.FlowLayout == TileFlowLayout.Horizontal)
        {
            var x = content.X;
            foreach (var index in indices)
            {
                var child = parent.Children[index];
                var width = child.FrameWidth ?? child.Width ?? EstimateWidth(child, theme, content.Width);
                var height = child.FrameHeight ?? child.Height ?? content.Height;
                result[index] = new TileRect(x, content.Y, width, height);
                x += width + gap;
            }

            return result;
        }

        if (parent.FlowLayout == TileFlowLayout.Grid)
        {
            var columns = Math.Clamp(parent.Columns ?? 2, 1, 32);
            var cellWidth = (content.Width - gap * (columns - 1)) / columns;
            var rows = (indices.Count + columns - 1) / columns;
            var cellHeight = (content.Height - gap * Math.Max(0, rows - 1)) / Math.Max(1, rows);
            var y = content.Y;
            var rowHeight = 0.0;
            var column = 0;
            foreach (var index in indices)
            {
                var child = parent.Children[index];
                var span = Math.Clamp(child.ColumnSpan ?? 1, 1, columns);
                if (column + span > columns)
                {
                    column = 0;
                    y += rowHeight + gap;
                    rowHeight = 0;
                }

                var width = cellWidth * span + gap * (span - 1);
                var height = cellHeight;
                result[index] = new TileRect(content.X + column * (cellWidth + gap), y, width, height);
                rowHeight = Math.Max(rowHeight, height);
                column += span;
            }

            return result;
        }

        var explicitHeight = 0.0;
        var flexible = 0;
        foreach (var index in indices)
        {
            var child = parent.Children[index];
            if (child.FrameHeight is > 0 || child.Height is > 0)
            {
                explicitHeight += child.FrameHeight ?? child.Height ?? 0;
            }
            else if (child.NodeKind == TileNodeKind.Panel)
            {
                flexible++;
            }
            else
            {
                explicitHeight += EstimateHeight(child);
            }
        }

        var remaining = Math.Max(0, content.Height - explicitHeight - gap * Math.Max(0, indices.Count - 1));
        var flexibleHeight = flexible == 0 ? 0 : Math.Max(20, remaining / flexible);

        var top = content.Y;
        foreach (var index in indices)
        {
            var child = parent.Children[index];
            double height;
            if (child.FrameHeight is > 0 || child.Height is > 0)
            {
                height = child.FrameHeight ?? child.Height ?? 0;
            }
            else if (child.NodeKind == TileNodeKind.Panel)
            {
                height = flexibleHeight;
            }
            else
            {
                height = EstimateHeight(child);
            }

            result[index] = new TileRect(content.X, top, content.Width, height);
            top += height + gap;
        }

        return result;
    }

    private static double EstimateHeight(TileNode node) => node.NodeKind switch
    {
        TileNodeKind.Text => Math.Max(14, (node.Size ?? 14) * 1.35 * Math.Max(1, node.ValueLines ?? 1)),
        TileNodeKind.Metric => (node.LabelSize ?? node.Size ?? 12) + (node.ValueSize ?? 20) * 1.35 + 10,
        TileNodeKind.Chart => node.Height ?? 80,
        TileNodeKind.Bar => node.BarOrientation == TileBarOrientation.Horizontal ? 34 : 84,
        TileNodeKind.Gauge => node.Diameter ?? node.Height ?? 84,
        TileNodeKind.Heatmap => node.Height ?? 44,
        TileNodeKind.Image => node.Height ?? 32,
        TileNodeKind.Icon => node.Size ?? 28,
        TileNodeKind.Panel => 24,
        _ => 24,
    };

    private static double EstimateWidth(TileNode node, TileTheme theme, double available) => node.NodeKind switch
    {
        TileNodeKind.Text => Math.Min(available, Math.Max(30, (node.Text?.Length ?? 8) * (node.Size ?? 14) * 0.55)),
        TileNodeKind.Icon => node.Size ?? 28,
        TileNodeKind.Image => node.Width ?? 32,
        _ => available,
    };

    private static string? SeriesToHistory(string? series) => series?.Trim().ToLowerInvariant() switch
    {
        "frametimes" or "frametime" => "frame.time",
        "framerates" or "framerate" or "framespersecond" => "frame.fps",
        "displaytimes" or "displaytime" => null,
        _ => series,
    };

    private static double[] LimitHistory(double[] values, int? points)
    {
        if (points is not > 0 || values.Length <= points.Value)
        {
            return values;
        }

        return values[^points.Value..];
    }

    private static double[] ReadSeries(SensorHistory? history, string? series)
    {
        if (history is null || string.IsNullOrWhiteSpace(series))
        {
            return Array.Empty<double>();
        }

        var capacity = Math.Min(history.Capacity, 240);
        var buffer = new double[capacity];
        var count = history.TryCopy(series, buffer);
        return count <= 0 ? Array.Empty<double>() : buffer[..count];
    }

    private static double StutterThreshold(double[] values, double factor)
    {
        var valid = values.Where(value => double.IsFinite(value) && value > 0).ToArray();
        return valid.Length == 0 ? double.NaN : valid.Average() * Math.Clamp(factor, 1, 10);
    }

    private static TileTextAlign ParseAlign(string? align) => align?.Trim().ToLowerInvariant() switch
    {
        "center" => TileTextAlign.Center,
        "right" => TileTextAlign.Right,
        _ => TileTextAlign.Left,
    };

    private static bool IsSemibold(string? weight)
        => string.Equals(weight, "semibold", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(weight, "bold", StringComparison.OrdinalIgnoreCase);

    private static double ScaleFor(TileReading reading, double? explicitScale, string? targetUnit)
    {
        if (explicitScale is { } scale)
        {
            // CapFrameX's source units differ from HardwareOSD's: MB tiles carry scale 1024
            // because CapFrameX reports GB, while HardwareOSD reports MB. When both units name the
            // same thing the printed unit already matches, so the design's scale would relabel a
            // correct number. Other explicit scales (MHz -> GHz) are the real conversion.
            return string.Equals(reading.Unit, targetUnit, StringComparison.OrdinalIgnoreCase)
                ? 1
                : scale;
        }

        return UnitFactor(reading.Unit, targetUnit);
    }

    private static double UnitFactor(string sourceUnit, string? targetUnit)
    {        if (string.IsNullOrWhiteSpace(sourceUnit) || string.IsNullOrWhiteSpace(targetUnit))
        {
            return 1;
        }

        var source = sourceUnit.Trim().ToUpperInvariant();
        var target = targetUnit.Trim().ToUpperInvariant();
        if (source == target)
        {
            return 1;
        }

        return (source, target) switch
        {
            ("MB", "GB") => 1.0 / 1024,
            ("MB", "TB") => 1.0 / (1024 * 1024),
            ("GB", "MB") => 1024,
            ("GB", "TB") => 1.0 / 1024,
            ("KB", "MB") => 1.0 / 1024,
            ("MHZ", "GHZ") => 1.0 / 1000,
            ("GHZ", "MHZ") => 1000,
            ("HZ", "KHZ") => 1.0 / 1000,
            ("HZ", "MHZ") => 1.0 / 1_000_000,
            ("KHZ", "MHZ") => 1.0 / 1000,
            ("MS", "S") => 1.0 / 1000,
            ("S", "MS") => 1000,
            ("W", "KW") => 1.0 / 1000,
            ("BYTES", "MB") => 1.0 / (1024 * 1024),
            ("B", "MB") => 1.0 / (1024 * 1024),
            _ => 1,
        };
    }

    private static TileColor WithOpacity(TileColor color, double opacity)
        => color.WithAlpha((byte)Math.Clamp(color.A * Math.Clamp(opacity, 0, 1), 0, 255));
}
