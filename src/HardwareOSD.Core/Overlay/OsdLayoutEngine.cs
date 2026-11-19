using System.Globalization;
using HardwareOSD.Core.Config;
using HardwareOSD.Core.Fps;
using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.Metrics;

namespace HardwareOSD.Core.Overlay;

/// <summary>
/// Layout engine for custom profiles and the three new styles. It is WPF-free and only reads the
/// immutable snapshot/history it receives. Classic profiles that still follow the old Metrics page
/// use the legacy path in <see cref="OverlayLayoutBuilder"/> so existing visuals and tests are kept.
/// </summary>
public static class OsdLayoutEngine
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static OverlayLayout Build(
        AppSettings settings,
        OsdProfile profile,
        SensorSnapshot snapshot,
        FpsSnapshot? fps = null,
        SensorHistory? history = null,
        OsdRenderContext? context = null)
    {
        profile.Normalize();
        var rows = new List<OverlayRow>(24);
        var hasAnyValue = false;

        if (profile.RootLayout == OsdGroupLayout.Columns)
        {
            var inlineRow = BuildInlineRoot(settings, profile, snapshot, fps, history, context, ref hasAnyValue);
            if (inlineRow is not null)
            {
                rows.Add(inlineRow);
                var inlineCores = settings.Overlay.Layout == OverlayLayoutMode.Detailed
                    ? BuildCores(snapshot)
                    : Array.Empty<CoreEntry>();
                return new OverlayLayout(rows, inlineCores, snapshot.Sequence, hasAnyValue);
            }
        }

        foreach (var element in profile.Elements)
        {
            Append(element, settings, profile, snapshot, fps, history, context, rows, ref hasAnyValue, 0);
        }

        var cores = settings.Overlay.Layout == OverlayLayoutMode.Detailed
            ? BuildCores(snapshot)
            : Array.Empty<CoreEntry>();
        return new OverlayLayout(rows, cores, snapshot.Sequence, hasAnyValue);
    }

    private static OverlayRow? BuildInlineRoot(
        AppSettings settings,
        OsdProfile profile,
        SensorSnapshot snapshot,
        FpsSnapshot? fps,
        SensorHistory? history,
        OsdRenderContext? context,
        ref bool hasAnyValue)
    {
        var collected = new List<(OsdElement Element, string Label)>();
        CollectInlineElements(profile.Elements, string.Empty, collected);
        if (collected.Count == 0)
        {
            return null;
        }

        var style = profile.StyleSettings;
        var cells = new List<OverlayCell>(collected.Count);
        foreach (var (element, label) in collected)
        {
            var formatted = OsdValueFormatter.Format(element, snapshot, fps, history);
            if (!formatted.Available && !settings.Monitoring.ShowUnavailableMetrics)
            {
                continue;
            }

            if (formatted.Available)
            {
                hasAnyValue = true;
            }

            var cell = ToCell(element, formatted, style);
            cells.Add(style.ShowLabels ? cell with { Label = label } : cell);
        }

        if (cells.Count == 0)
        {
            return null;
        }

        return new OverlayRow(
            string.Empty,
            cells[0].Definition.Group,
            cells,
            null,
            kind: OverlayRowKind.SensorGroup,
            valueSize: style.FontSize);
    }

    private static void CollectInlineElements(IEnumerable<OsdElement> elements, string parentLabel,
        List<(OsdElement Element, string Label)> collected)
    {
        foreach (var element in elements)
        {
            if (!element.Visible)
            {
                continue;
            }

            if (element.Kind == OsdElementKind.Group)
            {
                var groupLabel = string.IsNullOrWhiteSpace(element.Label) ? parentLabel : element.Label.Trim();
                CollectInlineElements(element.Children, groupLabel, collected);
                continue;
            }

            if (!element.IsSensorLike)
            {
                continue;
            }

            var ownLabel = element.DisplayLabel();
            if (string.IsNullOrWhiteSpace(ownLabel))
            {
                ownLabel = MetricCatalog.Find(element.SensorId)?.DisplayName ?? element.SensorId;
            }

            var label = !string.IsNullOrWhiteSpace(parentLabel) &&
                        !ownLabel.Contains(parentLabel, StringComparison.OrdinalIgnoreCase)
                ? parentLabel + " " + ownLabel
                : ownLabel;
            collected.Add((element, label));
        }
    }

    private static void Append(
        OsdElement element,
        AppSettings settings,
        OsdProfile profile,
        SensorSnapshot snapshot,
        FpsSnapshot? fps,
        SensorHistory? history,
        OsdRenderContext? context,
        List<OverlayRow> rows,
        ref bool hasAnyValue,
        int depth)
    {
        if (!element.Visible || depth > 8)
        {
            return;
        }

        var style = profile.StyleSettings;
        if (element.Kind == OsdElementKind.Group)
        {
            AppendGroup(element, settings, profile, snapshot, fps, history, context, rows, ref hasAnyValue, depth);
            return;
        }

        if (element.Kind == OsdElementKind.EmptyLine)
        {
            rows.Add(new OverlayRow(string.Empty, MetricGroup.System, new List<OverlayCell>(), null,
                kind: OverlayRowKind.EmptyLine, minHeight: element.Height ?? Math.Max(4, style.FontSize * 0.5),
                indent: element.Indent * style.ColumnSpacing));
            return;
        }

        if (element.Kind == OsdElementKind.Divider)
        {
            rows.Add(new OverlayRow(string.Empty, MetricGroup.System, new List<OverlayCell>(), null,
                kind: OverlayRowKind.Divider, minHeight: element.Height ?? 1,
                indent: element.Indent * style.ColumnSpacing));
            return;
        }

        if (element.Kind is OsdElementKind.GroupHeader or OsdElementKind.DeviceHeader)
        {
            rows.Add(TextRow(element, style, context,
                element.Kind == OsdElementKind.DeviceHeader ? OverlayRowKind.DeviceHeader : OverlayRowKind.GroupHeader));
            return;
        }

        if (element.Kind == OsdElementKind.CustomText)
        {
            rows.Add(TextRow(element, style, context, OverlayRowKind.CustomText));
            return;
        }

        AppendSensorElement(element, settings, profile, snapshot, fps, history, context, rows, ref hasAnyValue);
    }

    private static OverlayRow TextRow(OsdElement element, OsdStyleSettings style,
        OsdRenderContext? context, OverlayRowKind kind)
    {
        var size = element.FontSize > 0
            ? element.FontSize
            : kind == OverlayRowKind.DeviceHeader ? style.FontSize * 1.1 : style.FontSize * 0.92;
        var text = string.IsNullOrWhiteSpace(element.Text) ? element.Label : element.Text;
        text = context?.Expand(text) ?? text;
        return new OverlayRow(string.Empty, MetricGroup.System, new List<OverlayCell>(), null,
            kind: kind,
            text: text,
            indent: element.Indent * style.ColumnSpacing,
            labelSize: size,
            labelColor: LabelColor(element, context),
            minHeight: element.Height,
            align: element.Align);
    }

    private static void AppendGroup(
        OsdElement group,
        AppSettings settings,
        OsdProfile profile,
        SensorSnapshot snapshot,
        FpsSnapshot? fps,
        SensorHistory? history,
        OsdRenderContext? context,
        List<OverlayRow> rows,
        ref bool hasAnyValue,
        int depth)
    {
        var style = profile.StyleSettings;
        if (group.GroupLayout == OsdGroupLayout.Columns)
        {
            var cells = new List<OverlayCell>();
            string? sparklineId = null;
            var forceSparkline = false;
            string? stats = null;
            var anyAvailable = false;
            foreach (var child in group.Children)
            {
                if (!child.Visible || !child.IsSensorLike)
                {
                    continue;
                }

                var formatted = OsdValueFormatter.Format(child, snapshot, fps, history);
                if (!formatted.Available && !settings.Monitoring.ShowUnavailableMetrics)
                {
                    continue;
                }

                if (formatted.Available)
                {
                    anyAvailable = true;
                    hasAnyValue = true;
                    if (sparklineId is null || (sparklineId.EndsWith(".temp", StringComparison.Ordinal) &&
                                                child.SensorId.EndsWith(".usage", StringComparison.Ordinal)))
                    {
                        sparklineId = child.SensorId;
                    }
                }

                forceSparkline |= child.ShowGraph;
                if (stats is null && child.ShowStatistics)
                {
                    stats = BuildStats(child.SensorId, formatted.Unit, history);
                }

                if (child.ShowValue)
                {
                    cells.Add(ToCell(child, formatted, style));
                }
            }

            if (cells.Count == 0 && sparklineId is null)
            {
                return;
            }

            _ = anyAvailable;
            var subtitle = GroupFrameSubtitle(group, fps);
            var graphChild = group.Children.FirstOrDefault(c => c.ShowGraph || c.Kind == OsdElementKind.Graph);
            rows.Add(new OverlayRow(
                group.Label,
                GroupOf(group),
                cells,
                style.ShowSparklines || forceSparkline ? sparklineId : null,
                subtitle,
                subtitle is null ? 0 : OverlayRow.FpsNameSlotWidth,
                OverlayRowKind.SensorGroup,
                indent: group.Indent * style.ColumnSpacing,
                labelSize: group.FontSize > 0 ? group.FontSize : style.FontSize * style.LabelScale,
                valueSize: style.FontSize,
                labelColor: LabelColor(group, context),
                forceSparkline: forceSparkline,
                stats: stats,
                minHeight: group.Height,
                graphColor: graphChild?.GraphColor,
                graphThickness: graphChild?.GraphThickness ?? 1.3,
                graphHistoryPoints: graphChild?.GraphHistoryPoints ?? 120,
                graphScaleMin: graphChild?.GraphScaleMin,
                graphScaleMax: graphChild?.GraphScaleMax));
            return;
        }

        if (!string.IsNullOrWhiteSpace(group.Label))
        {
            rows.Add(new OverlayRow(string.Empty, GroupOf(group), new List<OverlayCell>(), null,
                kind: OverlayRowKind.GroupHeader,
                text: group.Label,
                indent: group.Indent * style.ColumnSpacing,
                labelSize: group.FontSize > 0 ? group.FontSize : style.FontSize * style.LabelScale,
                labelColor: LabelColor(group, context),
                minHeight: group.Height,
                align: group.Align));
        }

        foreach (var child in group.Children)
        {
            Append(child, settings, profile, snapshot, fps, history, context, rows, ref hasAnyValue, depth + 1);
        }
    }

    private static void AppendSensorElement(
        OsdElement element,
        AppSettings settings,
        OsdProfile profile,
        SensorSnapshot snapshot,
        FpsSnapshot? fps,
        SensorHistory? history,
        OsdRenderContext? context,
        List<OverlayRow> rows,
        ref bool hasAnyValue)
    {
        var style = profile.StyleSettings;
        var formatted = OsdValueFormatter.Format(element, snapshot, fps, history);
        if (!formatted.Available && !settings.Monitoring.ShowUnavailableMetrics)
        {
            return;
        }

        if (formatted.Available)
        {
            hasAnyValue = true;
        }

        var definition = formatted.Definition;
        var kind = element.Kind switch
        {
            OsdElementKind.Graph => OverlayRowKind.Graph,
            OsdElementKind.ProgressBar => OverlayRowKind.ProgressBar,
            OsdElementKind.CustomValue => OverlayRowKind.CustomValue,
            OsdElementKind.Ring => OverlayRowKind.ProgressRing,
            _ => OverlayRowKind.SensorLine,
        };

        var cells = new List<OverlayCell>(1);
        if (element.ShowValue)
        {
            cells.Add(ToCell(element, formatted, style));
        }

        var forceGraph = element.ShowGraph || element.Kind == OsdElementKind.Graph;
        var sparklineId = forceGraph ? element.SensorId : null;
        if (sparklineId is null && style.ShowSparklines && definition.Sparkline)
        {
            sparklineId = element.SensorId;
        }

        double? progress = null;
        if (element.Kind is OsdElementKind.ProgressBar or OsdElementKind.Ring || element.ShowProgress)
        {
            progress = Progress(element, formatted, snapshot);
        }

        var stats = element.ShowStatistics
            ? BuildStats(element.SensorId, formatted.Unit, history)
            : null;
        var subtitle = IsFrameMetric(element.SensorId) ? FrameSubtitle(element, fps) : null;
        rows.Add(new OverlayRow(
            element.ShowLabel ? element.DisplayLabel() : string.Empty,
            definition.Group,
            cells,
            sparklineId,
            subtitle,
            subtitle is null ? 0 : OverlayRow.FpsNameSlotWidth,
            kind,
            text: element.Text,
            indent: element.Indent * style.ColumnSpacing,
            labelSize: element.FontSize > 0 ? element.FontSize : style.FontSize * style.LabelScale,
            valueSize: element.FontSize > 0 ? element.FontSize : style.FontSize,
            labelColor: LabelColor(element, context),
            forceSparkline: forceGraph,
            stats: stats,
            minHeight: element.Height,
            progress: progress,
            align: element.Align,
            graphColor: element.GraphColor,
            graphThickness: element.GraphThickness,
            graphHistoryPoints: element.GraphHistoryPoints,
            graphScaleMin: element.GraphScaleMin,
            graphScaleMax: element.GraphScaleMax));
    }

    private static OverlayCell ToCell(OsdElement element, OsdFormattedValue formatted, OsdStyleSettings style)
    {
        var scale = element.FontSize > 0 ? element.FontSize / style.FontSize : 1;
        var samples = MetricWidths.For(formatted.Definition, element.Unit);
        return new OverlayCell(
            formatted.Value,
            formatted.Unit,
            formatted.Severity,
            formatted.Available,
            formatted.Definition,
            formatted.Available ? formatted.Threshold.Role : OsdColorRole.Unavailable,
            string.IsNullOrWhiteSpace(element.ValueColor) ? null : element.ValueColor,
            scale,
            samples.Value,
            samples.Unit,
            element.Width);
    }

    private static double? Progress(OsdElement element, OsdFormattedValue formatted, SensorSnapshot snapshot)
    {
        if (!formatted.Available || !double.IsFinite(formatted.RawValue))
        {
            return null;
        }

        var min = element.ProgressMin;
        var max = element.ProgressMax;
        if (min is null || max is null)
        {
            var unit = formatted.Unit;
            if (unit == "%")
            {
                min ??= 0;
                max ??= 100;
            }
            else if (element.SensorId is "gpu.power" &&
                     snapshot.TryGet(SensorIds.GpuPowerLimit, out var limit) &&
                     limit.Available && limit.Value > 0)
            {
                min ??= 0;
                max ??= limit.Value;
            }
            else if (IsTemperature(formatted.Definition))
            {
                min ??= 0;
                max ??= 100;
            }
        }

        if (min is null || max is null || max <= min)
        {
            return null;
        }

        return Math.Clamp((formatted.RawValue - min.Value) / (max.Value - min.Value), 0, 1);
    }

    private static string? BuildStats(string sensorId, string unit, SensorHistory? history)
    {
        if (history is null || string.IsNullOrWhiteSpace(sensorId))
        {
            return null;
        }

        var values = new double[history.Capacity];
        var count = history.TryCopy(sensorId, values);
        if (count <= 0)
        {
            return null;
        }

        double min = double.MaxValue, max = double.MinValue, sum = 0;
        var finite = 0;
        for (var i = values.Length - count; i < values.Length; i++)
        {
            var value = values[i];
            if (!double.IsFinite(value))
            {
                continue;
            }

            min = Math.Min(min, value);
            max = Math.Max(max, value);
            sum += value;
            finite++;
        }

        if (finite == 0)
        {
            return null;
        }

        var suffix = string.IsNullOrWhiteSpace(unit) ? string.Empty : " " + unit;
        return string.Format(Culture, "min {0:0.#}  max {1:0.#}  avg {2:0.#}{3}",
            min, max, sum / finite, suffix);
    }

    private static MetricGroup GroupOf(OsdElement group)
        => group.Children.Select(c => MetricCatalog.Find(c.SensorId)?.Group)
            .FirstOrDefault(g => g is not null) ?? MetricGroup.System;

    private static bool IsFrameMetric(string sensorId)
        => sensorId.StartsWith("frame.", StringComparison.Ordinal);

    private static string? FrameSubtitle(OsdElement element, FpsSnapshot? fps)
        => IsFrameMetric(element.SensorId) && fps is { HasValue: true, ProcessName: { Length: > 0 } name }
            ? name.Trim().Replace('\r', ' ').Replace('\n', ' ')
            : null;

    private static string? GroupFrameSubtitle(OsdElement group, FpsSnapshot? fps)
    {
        if (fps is not { HasValue: true, ProcessName: { Length: > 0 } name })
        {
            return null;
        }

        return group.Children.Any(c => IsFrameMetric(c.SensorId))
            ? name.Trim().Replace('\r', ' ').Replace('\n', ' ')
            : null;
    }

    private static IReadOnlyList<CoreEntry> BuildCores(SensorSnapshot snapshot)
    {
        var entries = new List<CoreEntry>(16);
        for (var index = 0; index < 32; index++)
        {
            var hasTemp = snapshot.TryGet(SensorIds.CoreTemperature(index), out var temp) && temp.Available;
            var hasUsage = snapshot.TryGet(SensorIds.CoreUsage(index), out var usage) && usage.Available;
            if (!hasTemp && !hasUsage)
            {
                continue;
            }

            entries.Add(new CoreEntry(
                index,
                hasTemp ? temp.Value.ToString("0", Culture) : "N/A",
                hasUsage ? usage.Value.ToString("0", Culture) : "N/A"));
        }

        return entries;
    }

    private static bool IsTemperature(MetricDefinition definition)
        => definition.Unit.Contains("C", StringComparison.Ordinal) ||
           definition.Id.Contains(".temp", StringComparison.Ordinal);

    private static string? LabelColor(OsdElement element, OsdRenderContext? context)
    {
        if (!string.IsNullOrWhiteSpace(element.LabelColor))
        {
            return element.LabelColor;
        }

        return element.VendorRole == OsdVendorRole.None
            ? null
            : context?.LabelColor(element.VendorRole);
    }
}
