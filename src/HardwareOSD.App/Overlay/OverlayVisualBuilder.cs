using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using HardwareOSD.Core.Config;
using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.Metrics;
using HardwareOSD.Core.Overlay;

namespace HardwareOSD.App.Overlay;

public sealed class VisualCell
{
    public required FormattedText Value { get; init; }
    public FormattedText? Unit { get; init; }
    public FormattedText? Label { get; init; }
    public double Left { get; set; }
    public double LabelLeft { get; set; }
    public double LabelTop { get; set; }
    public double ValueLeft { get; set; }
    public double UnitLeft { get; set; }
    public double Width { get; init; }
}

public sealed class VisualSparkline
{
    public Rect Bounds { get; init; }
    public required double[] Values { get; init; }
    public int Count { get; init; }
    public required Brush Stroke { get; init; }
    public required Brush Fill { get; init; }
    public double ScaleMin { get; init; } = double.NaN;
    public double ScaleMax { get; init; } = double.NaN;
    public double Thickness { get; init; } = 1.3;
}

public sealed class VisualProgress
{
    public Rect Bounds { get; init; }
    public double Value { get; init; }
    public required Brush Fill { get; init; }
    public required Brush Track { get; init; }
}

public sealed class VisualRing
{
    public Rect Bounds { get; init; }
    public double Value { get; init; }
    public required Brush Fill { get; init; }
    public required Brush Track { get; init; }
}

public sealed class VisualRow
{
    public required MetricGroup Group { get; init; }
    public OverlayRowKind Kind { get; init; }
    public double Top { get; set; }
    public double Height { get; set; }
    public double ContentHeight { get; set; }
    public FormattedText? Label { get; set; }
    public double LabelLeft { get; set; }
    public double LabelTop { get; set; }
    public FormattedText? Subtitle { get; set; }
    public double SubtitleLeft { get; set; }
    public double SubtitleTop { get; set; }
    public FormattedText? Stats { get; set; }
    public double StatsLeft { get; set; }
    public double StatsTop { get; set; }
    public double Indent { get; set; }
    public OsdTextAlign Align { get; set; }
    public VisualProgress? Progress { get; set; }
    public VisualRing? Ring { get; set; }
    public string? GraphColor { get; set; }
    public double GraphThickness { get; set; } = 1.3;
    public int GraphHistoryPoints { get; set; } = 120;
    public double? GraphScaleMin { get; set; }
    public double? GraphScaleMax { get; set; }
    public List<VisualCell> Cells { get; } = new(5);
    public VisualSparkline? Sparkline { get; set; }
}

public sealed class VisualCoreItem
{
    public required FormattedText Label { get; init; }
    public required FormattedText Temperature { get; init; }
    public required FormattedText Usage { get; init; }
    public double LabelLeft { get; set; }
    public double TemperatureLeft { get; set; }
    public double UsageLeft { get; set; }
    public double Top { get; set; }
}

/// <summary>Complete draw model for one frame. Contains only positioned text and simple geometry.</summary>
public sealed class OverlayVisual
{
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Inline { get; set; }
    public bool ShowBackground { get; set; }
    public List<VisualRow> Rows { get; } = new(5);
    public List<VisualCoreItem> Cores { get; } = new();
    public List<double> InlineSeparators { get; } = new(4);
    public double CoreBlockTop { get; set; } = -1;
    public FormattedText? DemoBadge { get; set; }
    public double DemoBadgeTop { get; set; }
    public bool IsEmpty => Rows.Count == 0 && Cores.Count == 0;

    public static readonly OverlayVisual Empty = new();
}

/// <summary>
/// Converts a snapshot-derived layout into WPF text geometry. All measurement happens here, once per
/// rendered frame, and reserved widths (from metric sample strings) keep the overlay from jittering.
/// </summary>
public static class OverlayVisualBuilder
{
    private const double AccentWidth = 3;
    private const double AccentGap = 7;
    private const double LabelGap = 10;
    private const double UnitGap = 4;
    private const double GraphWidth = 54;
    private const double GraphGap = 12;

    public static OverlayVisual Build(
        OverlayLayout layout,
        OverlayTheme theme,
        SensorHistory history,
        OverlayLayoutMode mode,
        double pixelsPerDip,
        bool demo,
        string? badgeText = null)
    {
        var visual = new OverlayVisual
        {
            ShowBackground = theme.ShowBackground,
            // Complex builder rows (headers, dividers, progress bars) need the stacked geometry.
            // Classic/compact sensor-only rows can still use the original inline path.
            Inline = mode == OverlayLayoutMode.Inline &&
                     layout.Rows.All(row => row.Kind is OverlayRowKind.SensorGroup or OverlayRowKind.SensorLine),
        };

        if (layout.IsEmpty)
        {
            visual.Width = 0;
            visual.Height = 0;
            return visual;
        }

        if (visual.Inline)
        {
            BuildInline(visual, layout, theme, pixelsPerDip);
        }
        else
        {
            BuildStacked(visual, layout, theme, pixelsPerDip, history);
        }

        if (layout.Cores.Count > 0 && visual.Rows.Count > 0)
        {
            BuildCores(visual, layout.Cores, theme, pixelsPerDip);
        }

        if (demo)
        {
            var badge = MakeText(badgeText ?? "DEMO — synthetic data", theme.UnitTypeface,
                theme.UnitSize * 0.9, theme.WarningBrush, pixelsPerDip);
            visual.DemoBadge = badge;
            visual.DemoBadgeTop = visual.Height - theme.PaddingY + 2;
            visual.Height += badge.Height + 2;
            visual.Width = Math.Max(visual.Width, badge.Width + theme.PaddingX * 2);
        }

        return visual;
    }

    private static void BuildStacked(OverlayVisual visual, OverlayLayout layout, OverlayTheme theme, double pixelsPerDip, SensorHistory history)
    {
        double maxContentEnd = theme.PaddingX;
        var rowTop = theme.PaddingY;
        var rowsHeight = 0.0;
        var pendingGraphs = new List<(VisualRow Row, string SensorId, bool Forced)>();

        foreach (var row in layout.Rows)
        {
            var indent = Math.Max(0, row.Indent);
            if (row.Kind == OverlayRowKind.EmptyLine)
            {
                var height = Math.Max(1, row.MinHeight ?? Math.Max(4, theme.LineHeight * 0.5));
                visual.Rows.Add(new VisualRow
                {
                    Group = row.Group,
                    Kind = row.Kind,
                    Top = rowTop,
                    ContentHeight = height,
                    Height = height,
                    Indent = indent,
                });
                rowsHeight += height;
                rowTop += height + theme.RowSpacing;
                continue;
            }

            if (row.Kind == OverlayRowKind.Divider)
            {
                var height = Math.Max(1, row.MinHeight ?? 1);
                visual.Rows.Add(new VisualRow
                {
                    Group = row.Group,
                    Kind = row.Kind,
                    Top = rowTop,
                    ContentHeight = height,
                    Height = height + 3,
                    Indent = indent,
                });
                rowsHeight += height + 3;
                rowTop += height + 3 + theme.RowSpacing;
                continue;
            }

            if (row.Kind is OverlayRowKind.GroupHeader or OverlayRowKind.DeviceHeader or OverlayRowKind.CustomText)
            {
                var size = row.LabelSize > 0 ? row.LabelSize : theme.LabelSize;
                var brush = TextBrushFor(row, theme, pixelsPerDip);
                var text = MakeText(row.Text ?? row.Label, theme.LabelTypeface, size, brush, pixelsPerDip);
                var height = Math.Max(theme.LineHeight, row.MinHeight ?? 0);
                var textRow = new VisualRow
                {
                    Group = row.Group,
                    Kind = row.Kind,
                    Top = rowTop,
                    ContentHeight = height,
                    Height = height,
                    Indent = indent,
                    Align = row.Align,
                };
                textRow.Label = text;
                textRow.LabelLeft = theme.PaddingX + indent;
                textRow.LabelTop = rowTop + (height - text.Height) / 2;
                visual.Rows.Add(textRow);
                maxContentEnd = Math.Max(maxContentEnd, textRow.LabelLeft + text.Width);
                rowsHeight += height;
                rowTop += height + theme.RowSpacing;
                continue;
            }

            double labelWidth = 0;
            FormattedText? label = null;
            if (theme.ShowLabels && !string.IsNullOrWhiteSpace(row.Label))
            {
                var labelSize = row.LabelSize > 0 ? row.LabelSize : theme.LabelSize;
                var labelBrush = TextBrushFor(row, theme, pixelsPerDip);
                label = MakeText(theme.UpperLabels ? row.Label.ToUpperInvariant() : row.Label,
                    theme.LabelTypeface, labelSize, labelBrush, pixelsPerDip);
                labelWidth = label.Width;
            }

            var accentWidth = theme.ShowLabels ? AccentWidth : 0;
            var accentGap = theme.ShowLabels ? AccentGap : 0;
            var labelGap = theme.ShowLabels && label is not null ? LabelGap : 0;
            var contentLeft = theme.PaddingX + indent + accentWidth + accentGap + labelWidth + labelGap;
            var contentHeight = Math.Max(theme.LineHeight, row.MinHeight ?? 0);
            var subtitle = BuildSubtitle(row, theme, pixelsPerDip);
            var visualRow = new VisualRow
            {
                Group = row.Group,
                Kind = row.Kind,
                Top = rowTop,
                ContentHeight = contentHeight,
                Height = contentHeight + (subtitle is null ? 0 : subtitle.Height + 2),
                Indent = indent,
                Align = row.Align,
                GraphColor = row.GraphColor,
                GraphThickness = row.GraphThickness,
                GraphHistoryPoints = row.GraphHistoryPoints,
                GraphScaleMin = row.GraphScaleMin,
                GraphScaleMax = row.GraphScaleMax,
            };

            if (label is not null)
            {
                visualRow.Label = label;
                visualRow.LabelLeft = theme.PaddingX + indent + accentWidth + accentGap;
                visualRow.LabelTop = rowTop + (contentHeight - label.Height) / 2;
            }

            var cursor = contentLeft;
            if (row.Kind == OverlayRowKind.ProgressRing && row.Progress is { } ringValue)
            {
                var ringSize = Math.Max(16, Math.Min(28, contentHeight - 2));
                visualRow.Ring = new VisualRing
                {
                    Bounds = new Rect(cursor, rowTop + (contentHeight - ringSize) / 2, ringSize, ringSize),
                    Value = Math.Clamp(ringValue, 0, 1),
                    Fill = theme.AccentBrushFor(row.Group),
                    Track = theme.SeparatorBrush,
                };
                cursor += ringSize + 7;
                maxContentEnd = Math.Max(maxContentEnd, cursor + ringSize);
            }

            foreach (var cell in row.Cells)
            {
                var valueSize = theme.ValueSize * Math.Max(0.5, cell.FontScale);
                var unitSize = theme.UnitSize * Math.Max(0.5, cell.FontScale);
                var valueBrush = ValueBrushFor(cell, theme);
                var value = MakeText(cell.Value, theme.ValueTypeface, valueSize, valueBrush, pixelsPerDip);

                FormattedText? cellLabel = null;
                var labelSpace = 0.0;
                if (!string.IsNullOrWhiteSpace(cell.Label))
                {
                    var labelSize = Math.Max(8, theme.LabelSize * Math.Max(0.5, cell.FontScale) * 0.92);
                    cellLabel = MakeText(cell.Label!, theme.LabelTypeface, labelSize, theme.LabelBrush, pixelsPerDip);
                    labelSpace = cellLabel.Width + 6;
                }

                FormattedText? unit = null;
                var reservedUnit = 0.0;
                if (theme.ShowUnits && cell.Unit.Length > 0)
                {
                    unit = MakeText(cell.Unit, theme.UnitTypeface, unitSize, theme.UnitBrush, pixelsPerDip);
                    var sampleUnitText = cell.SampleUnit ?? cell.Definition.SampleUnit;
                    var sampleUnit = MakeText(sampleUnitText, theme.UnitTypeface, unitSize, theme.UnitBrush, pixelsPerDip);
                    reservedUnit = Math.Max(unit.Width, sampleUnit.Width);
                }

                var sampleValueText = cell.SampleValue ?? cell.Definition.SampleValue;
                var sampleValue = MakeText(sampleValueText, theme.ValueTypeface, valueSize, theme.ValueBrush, pixelsPerDip);
                var reservedValue = Math.Max(value.Width, sampleValue.Width);
                if (cell.MinWidth is > 0)
                {
                    reservedValue = Math.Max(reservedValue, cell.MinWidth.Value);
                }

                var width = labelSpace + reservedValue + (reservedUnit > 0 ? UnitGap + reservedUnit : 0);
                var valueStart = cursor + labelSpace;
                visualRow.Cells.Add(new VisualCell
                {
                    Value = value,
                    Unit = unit,
                    Label = cellLabel,
                    Left = cursor,
                    LabelLeft = cursor,
                    LabelTop = rowTop + (contentHeight - (cellLabel?.Height ?? 0)) / 2,
                    ValueLeft = valueStart + reservedValue - value.Width,
                    UnitLeft = valueStart + reservedValue + UnitGap,
                    Width = width,
                });

                cursor += width + theme.ColumnSpacing;
            }

            if (visualRow.Cells.Count > 0)
            {
                maxContentEnd = Math.Max(maxContentEnd, cursor - theme.ColumnSpacing);
            }

            if (row.Progress is { } progress)
            {
                cursor += visualRow.Cells.Count > 0 ? 8 : 0;
                var barWidth = 58.0;
                var barHeight = 6.0;
                visualRow.Progress = new VisualProgress
                {
                    Bounds = new Rect(cursor, rowTop + (contentHeight - barHeight) / 2, barWidth, barHeight),
                    Value = Math.Clamp(progress, 0, 1),
                    Fill = theme.AccentBrushFor(row.Group),
                    Track = theme.SeparatorBrush,
                };
                cursor += barWidth + 8;
                maxContentEnd = Math.Max(maxContentEnd, cursor - 8);
            }

            if (!string.IsNullOrWhiteSpace(row.Stats))
            {
                var stats = MakeText(row.Stats, theme.UnitTypeface, theme.UnitSize, theme.UnitBrush, pixelsPerDip);
                visualRow.Stats = stats;
                visualRow.StatsLeft = cursor + 8;
                visualRow.StatsTop = rowTop + (contentHeight - stats.Height) / 2;
                cursor += stats.Width + 8;
                maxContentEnd = Math.Max(maxContentEnd, visualRow.StatsLeft + stats.Width);
            }

            if (subtitle is not null && visualRow.Cells.Count > 0)
            {
                visualRow.Subtitle = subtitle;
                visualRow.SubtitleLeft = visualRow.Cells[0].Left;
                visualRow.SubtitleTop = rowTop + contentHeight + 1;
                maxContentEnd = Math.Max(maxContentEnd, contentLeft + row.SubtitleSlotWidth);
            }

            if (row.SparklineId is not null && (theme.ShowSparklines || row.ForceSparkline))
            {
                pendingGraphs.Add((visualRow, row.SparklineId, row.ForceSparkline));
            }

            visual.Rows.Add(visualRow);
            rowsHeight += visualRow.Height;
            rowTop += visualRow.Height + theme.RowSpacing;
        }

        var hasGraphs = pendingGraphs.Count > 0;
        var graphLeft = maxContentEnd + GraphGap;
        visual.Width = (hasGraphs ? graphLeft + GraphWidth : maxContentEnd) + theme.PaddingX;
        visual.Height = theme.PaddingY * 2 + rowsHeight +
                        Math.Max(0, layout.Rows.Count - 1) * theme.RowSpacing;

        // Alignment is resolved after the widest row is known. This is used by custom text and
        // single-value rows; multi-cell group rows stay left-aligned to avoid jitter.
        foreach (var alignedRow in visual.Rows)
        {
            if (alignedRow.Align == OsdTextAlign.Left || alignedRow.Label is null)
            {
                continue;
            }

            var rowStart = alignedRow.LabelLeft;
            var rowEnd = alignedRow.Cells.Count > 0
                ? alignedRow.Cells[^1].Left + alignedRow.Cells[^1].Width
                : rowStart + alignedRow.Label.Width;
            if (alignedRow.Cells.Count > 1)
            {
                continue;
            }

            var shift = alignedRow.Align == OsdTextAlign.Right
                ? maxContentEnd - rowEnd
                : (maxContentEnd - rowEnd) / 2;
            if (shift <= 0)
            {
                continue;
            }

            alignedRow.LabelLeft += shift;
            foreach (var cell in alignedRow.Cells)
            {
                cell.Left += shift;
                cell.LabelLeft += shift;
                cell.ValueLeft += shift;
                cell.UnitLeft += shift;
            }

            if (alignedRow.Subtitle is not null)
            {
                alignedRow.SubtitleLeft += shift;
            }
        }

        if (!hasGraphs)
        {
            visual.Height = Math.Max(visual.Height, theme.PaddingY * 2 + theme.LineHeight);
            return;
        }

        var graphHeight = Math.Max(6, theme.LineHeight - 3);
        foreach (var pending in pendingGraphs)
        {
            var row = pending.Row;
            var capacity = Math.Clamp(row.GraphHistoryPoints, 8, history.Capacity);
            var values = new double[capacity];
            var count = history.TryCopy(pending.SensorId, values);
            if (count < 2)
            {
                continue;
            }

            var scale = SparklineScale(pending.SensorId, values, count);
            if (row.GraphScaleMin is not null && row.GraphScaleMax is not null &&
                row.GraphScaleMax > row.GraphScaleMin)
            {
                scale = (row.GraphScaleMin.Value, row.GraphScaleMax.Value);
            }

            row.Sparkline = new VisualSparkline
            {
                Bounds = new Rect(graphLeft, row.Top + (row.ContentHeight - graphHeight) / 2,
                    GraphWidth, graphHeight),
                Values = values,
                Count = count,
                Stroke = string.IsNullOrWhiteSpace(row.GraphColor)
                    ? theme.GraphStrokeFor(row.Group)
                    : ParseBrush(row.GraphColor, theme.GraphStrokeFor(row.Group)),
                Fill = theme.GraphFillFor(row.Group),
                ScaleMin = scale.Min,
                ScaleMax = scale.Max,
                Thickness = row.GraphThickness,
            };
        }
    }

    private static Brush ValueBrushFor(OverlayCell cell, OverlayTheme theme)
    {
        if (!string.IsNullOrWhiteSpace(cell.ValueColor))
        {
            return ParseBrush(cell.ValueColor, theme.ValueBrush);
        }

        return theme.Style == OsdStyle.Classic
            ? theme.ValueBrushFor(cell.Severity, cell.Available)
            : theme.ValueBrushFor(cell.ColorRole, cell.Available);
    }

    private static Brush TextBrushFor(OverlayRow row, OverlayTheme theme, double pixelsPerDip)
    {
        if (!string.IsNullOrWhiteSpace(row.LabelColor))
        {
            return ParseBrush(row.LabelColor, theme.LabelBrush);
        }

        return row.Kind switch
        {
            OverlayRowKind.CustomText => theme.TextBrush,
            OverlayRowKind.DeviceHeader => theme.HeaderBrush,
            OverlayRowKind.GroupHeader => theme.HeaderBrush,
            _ => theme.LabelBrush,
        };
    }

    private static FormattedText? BuildSubtitle(OverlayRow row, OverlayTheme theme, double pixelsPerDip)
    {
        if (string.IsNullOrWhiteSpace(row.Subtitle) || row.SubtitleSlotWidth <= 0)
        {
            return null;
        }

        var text = MakeText(row.Subtitle, theme.LabelTypeface,
            Math.Max(8, Math.Round(theme.UnitSize * 0.92, 1)), theme.UnitBrush, pixelsPerDip);
        text.MaxTextWidth = row.SubtitleSlotWidth;
        text.MaxLineCount = 1;
        text.Trimming = TextTrimming.CharacterEllipsis;
        return text;
    }

    private static void BuildInline(OverlayVisual visual, OverlayLayout layout, OverlayTheme theme, double pixelsPerDip)
    {
        var cursor = theme.PaddingX;
        var lineHeight = theme.LineHeight;
        var maxWidth = 0.0;
        var maxSubtitleHeight = 0.0;

        for (var i = 0; i < layout.Rows.Count; i++)
        {
            var row = layout.Rows[i];
            if (i > 0)
            {
                cursor += 11;
                visual.InlineSeparators.Add(cursor);
                cursor += 11;
            }

            var subtitle = BuildSubtitle(row, theme, pixelsPerDip);
            var visualRow = new VisualRow
            {
                Group = row.Group,
                Top = theme.PaddingY,
                ContentHeight = lineHeight,
                Height = lineHeight,
            };

            if (theme.ShowLabels)
            {
                var label = MakeText(theme.UpperLabels ? row.Label.ToUpperInvariant() : row.Label, theme.LabelTypeface,
                    row.LabelSize > 0 ? row.LabelSize : theme.LabelSize,
                    TextBrushFor(row, theme, pixelsPerDip), pixelsPerDip);
                visualRow.Label = label;
                visualRow.LabelLeft = cursor;
                visualRow.LabelTop = theme.PaddingY + (lineHeight - label.Height) / 2;
                cursor += label.Width + 8;
            }

            foreach (var cell in row.Cells)
            {
                var valueSize = theme.ValueSize * Math.Max(0.5, cell.FontScale);
                var unitSize = theme.UnitSize * Math.Max(0.5, cell.FontScale);
                var value = MakeText(cell.Value, theme.ValueTypeface, valueSize,
                    ValueBrushFor(cell, theme), pixelsPerDip);

                FormattedText? cellLabel = null;
                var labelSpace = 0.0;
                if (!string.IsNullOrWhiteSpace(cell.Label))
                {
                    var labelSize = Math.Max(8, theme.LabelSize * Math.Max(0.5, cell.FontScale) * 0.92);
                    cellLabel = MakeText(cell.Label!, theme.LabelTypeface, labelSize, theme.LabelBrush, pixelsPerDip);
                    labelSpace = cellLabel.Width + 6;
                }

                FormattedText? unit = null;
                var reservedUnit = 0.0;
                if (theme.ShowUnits && cell.Unit.Length > 0)
                {
                    unit = MakeText(cell.Unit, theme.UnitTypeface, unitSize, theme.UnitBrush, pixelsPerDip);
                    var sampleUnit = MakeText(cell.SampleUnit ?? cell.Definition.SampleUnit,
                        theme.UnitTypeface, unitSize, theme.UnitBrush, pixelsPerDip);
                    reservedUnit = Math.Max(unit.Width, sampleUnit.Width);
                }

                var sampleValue = MakeText(cell.SampleValue ?? cell.Definition.SampleValue,
                    theme.ValueTypeface, valueSize, theme.ValueBrush, pixelsPerDip);
                var reservedValue = Math.Max(value.Width, sampleValue.Width);
                if (cell.MinWidth is > 0)
                {
                    reservedValue = Math.Max(reservedValue, cell.MinWidth.Value);
                }

                var width = labelSpace + reservedValue + (reservedUnit > 0 ? UnitGap + reservedUnit : 0);
                var valueStart = cursor + labelSpace;
                visualRow.Cells.Add(new VisualCell
                {
                    Value = value,
                    Unit = unit,
                    Label = cellLabel,
                    Left = cursor,
                    LabelLeft = cursor,
                    LabelTop = theme.PaddingY + (lineHeight - (cellLabel?.Height ?? 0)) / 2,
                    ValueLeft = valueStart + reservedValue - value.Width,
                    UnitLeft = valueStart + reservedValue + UnitGap,
                    Width = width,
                });

                cursor += width + theme.ColumnSpacing;
            }

            if (subtitle is not null && visualRow.Cells.Count > 0)
            {
                visualRow.Subtitle = subtitle;
                visualRow.SubtitleLeft = visualRow.Cells[0].Left;
                visualRow.SubtitleTop = theme.PaddingY + lineHeight + 1;
                visualRow.Height = lineHeight + subtitle.Height + 2;
                maxWidth = Math.Max(maxWidth, visualRow.SubtitleLeft + row.SubtitleSlotWidth);
                maxSubtitleHeight = Math.Max(maxSubtitleHeight, subtitle.Height + 2);
            }

            visual.Rows.Add(visualRow);
        }

        visual.Width = Math.Max(cursor - theme.ColumnSpacing, maxWidth) + theme.PaddingX;
        visual.Height = theme.PaddingY * 2 + lineHeight + maxSubtitleHeight;
    }

    private static void BuildCores(OverlayVisual visual, IReadOnlyList<CoreEntry> cores, OverlayTheme theme, double pixelsPerDip)
    {
        const int maxColumns = 4;
        const double cellGap = 14;
        const double partGap = 5;

        var labelSample = MakeText("C88", theme.LabelTypeface, theme.LabelSize, theme.LabelBrush, pixelsPerDip);
        var tempSample = MakeText("888°", theme.ValueTypeface, theme.ValueSize * 0.92, theme.ValueBrush, pixelsPerDip);
        var usageSample = MakeText("888%", theme.UnitTypeface, theme.UnitSize, theme.UnitBrush, pixelsPerDip);

        var labelWidth = labelSample.Width;
        var tempWidth = tempSample.Width;
        var usageWidth = usageSample.Width;
        var cellWidth = labelWidth + partGap + tempWidth + partGap + usageWidth;

        var columns = Math.Min(Math.Max(1, maxColumns), cores.Count);
        var rowCount = (cores.Count + columns - 1) / columns;
        var coreLineHeight = Math.Round(theme.ValueSize * 1.28, 1);
        var startX = theme.PaddingX;
        var startY = visual.Height + 8;

        visual.CoreBlockTop = startY - 5;
        visual.Cores.Clear();

        for (var i = 0; i < cores.Count; i++)
        {
            var entry = cores[i];
            var column = i % columns;
            var rowIndex = i / columns;

            var label = MakeText($"C{entry.Index + 1:00}", theme.LabelTypeface, theme.LabelSize, theme.UnitBrush, pixelsPerDip);
            var tempText = entry.Temperature == "N/A" ? "N/A" : entry.Temperature + "°";
            var usageText = entry.Usage == "N/A" ? "N/A" : entry.Usage + "%";
            var temp = MakeText(tempText, theme.ValueTypeface, theme.ValueSize * 0.92, theme.ValueBrush, pixelsPerDip);
            var usage = MakeText(usageText, theme.UnitTypeface, theme.UnitSize, theme.UnitBrush, pixelsPerDip);

            var left = startX + column * (cellWidth + cellGap);
            var top = startY + rowIndex * (coreLineHeight + 2);
            visual.Cores.Add(new VisualCoreItem
            {
                Label = label,
                Temperature = temp,
                Usage = usage,
                LabelLeft = left,
                TemperatureLeft = left + labelWidth + partGap + (tempWidth - temp.Width),
                UsageLeft = left + labelWidth + partGap + tempWidth + partGap,
                Top = top + (coreLineHeight - label.Height) / 2,
            });
        }

        var blockWidth = columns * cellWidth + Math.Max(0, columns - 1) * cellGap;
        visual.Width = Math.Max(visual.Width, blockWidth + theme.PaddingX * 2);
        visual.Height = startY + rowCount * (coreLineHeight + 2) - 2 + theme.PaddingY;
    }

    /// <summary>
    /// Sparklines use meaningful, stable ranges: utilization is always 0-100 %, temperatures keep a
    /// minimum 12 °C span and power a 40 W span so small fluctuations do not look like dramatic swings.
    /// </summary>
    private static (double Min, double Max) SparklineScale(string sensorId, double[] values, int count)
    {
        if (sensorId.EndsWith(".usage", StringComparison.Ordinal) ||
            sensorId.EndsWith(".percent", StringComparison.Ordinal))
        {
            return (0, 100);
        }

        double min = double.MaxValue, max = double.MinValue;
        var offset = values.Length - count;
        for (var i = 0; i < count; i++)
        {
            var value = values[offset + i];
            if (double.IsNaN(value))
            {
                continue;
            }

            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }

        if (min > max)
        {
            return (0, 100);
        }

        var isTemperature = sensorId.Contains(".temp", StringComparison.Ordinal);
        var minimumSpan = isTemperature ? 12.0 : sensorId.Contains("power", StringComparison.Ordinal) ? 40.0 : 100.0;
        var span = max - min;

        if (span < minimumSpan)
        {
            var center = (min + max) / 2;
            min = center - minimumSpan / 2;
            max = center + minimumSpan / 2;
        }
        else
        {
            min -= span * 0.08;
            max += span * 0.08;
        }

        if (isTemperature)
        {
            min = Math.Max(0, min);
            max = Math.Min(125, max);
        }

        return (min, Math.Max(max, min + 1));
    }

    private static readonly ConcurrentDictionary<string, Brush> BrushCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static Brush ParseBrush(string hex, Brush fallback)
    {
        var text = hex.StartsWith('#') ? hex : "#" + hex;
        return BrushCache.GetOrAdd(text, _ =>
        {
            try
            {
                if (ColorConverter.ConvertFromString(text) is Color color)
                {
                    var brush = new SolidColorBrush(color);
                    brush.Freeze();
                    return brush;
                }
            }
            catch
            {
                // fall through
            }

            return fallback;
        });
    }

    private static FormattedText MakeText(string text, Typeface typeface, double size, Brush brush, double pixelsPerDip)        => new(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            size,
            brush,
            pixelsPerDip)
        {
            TextAlignment = TextAlignment.Left,
        };
}

