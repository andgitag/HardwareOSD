using HardwareOSD.Core.Config;
using HardwareOSD.Core.Fps;
using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.Metrics;

namespace HardwareOSD.Core.Overlay;

public enum OverlayRowKind
{
    SensorGroup = 0,
    GroupHeader = 1,
    DeviceHeader = 2,
    CustomText = 3,
    EmptyLine = 4,
    Divider = 5,
    SensorLine = 6,
    Graph = 7,
    ProgressBar = 8,
    CustomValue = 9,
    ProgressRing = 10,
}

/// <summary>One value cell inside an overlay row: the number, its unit and its warning state.</summary>
public readonly record struct OverlayCell(
    string Value,
    string Unit,
    MetricSeverity Severity,
    bool Available,
    MetricDefinition Definition,
    OsdColorRole ColorRole = OsdColorRole.Normal,
    string? ValueColor = null,
    double FontScale = 1,
    string? SampleValue = null,
    string? SampleUnit = null,
    double? MinWidth = null,
    string? Label = null);

/// <summary>A labelled row of cells, optionally with a sparkline drawn at its end.</summary>
public sealed class OverlayRow
{
    /// <summary>
    /// Width reserved for the FPS application name below the value. The name is measured with this
    /// maximum width and ellipsized, so switching processes can never widen the overlay.
    /// </summary>
    public const double FpsNameSlotWidth = 132;

    public OverlayRow(string label, MetricGroup group, List<OverlayCell> cells, string? sparklineId,
        string? subtitle = null, double subtitleSlotWidth = 0,
        OverlayRowKind kind = OverlayRowKind.SensorGroup, string? text = null,
        double indent = 0, double labelSize = 0, double valueSize = 0,
        string? labelColor = null, bool forceSparkline = false, string? stats = null,
        double? minHeight = null, double? progress = null, OsdTextAlign align = OsdTextAlign.Left,
        string? graphColor = null, double graphThickness = 1.3, int graphHistoryPoints = 120,
        double? graphScaleMin = null, double? graphScaleMax = null)
    {
        Label = label;
        Group = group;
        Cells = cells;
        SparklineId = sparklineId;
        Subtitle = subtitle;
        SubtitleSlotWidth = subtitleSlotWidth;
        Kind = kind;
        Text = text;
        Indent = indent;
        LabelSize = labelSize;
        ValueSize = valueSize;
        LabelColor = labelColor;
        ForceSparkline = forceSparkline;
        Stats = stats;
        MinHeight = minHeight;
        Progress = progress;
        Align = align;
        GraphColor = graphColor;
        GraphThickness = graphThickness;
        GraphHistoryPoints = graphHistoryPoints;
        GraphScaleMin = graphScaleMin;
        GraphScaleMax = graphScaleMax;
    }

    public string Label { get; }
    public MetricGroup Group { get; }
    public IReadOnlyList<OverlayCell> Cells { get; }
    public string? SparklineId { get; }
    public OverlayRowKind Kind { get; }
    public string? Text { get; }
    public double Indent { get; }
    public double LabelSize { get; }
    public double ValueSize { get; }
    public string? LabelColor { get; }
    public bool ForceSparkline { get; }
    public string? Stats { get; }
    public double? MinHeight { get; }
    public double? Progress { get; }
    public OsdTextAlign Align { get; }
    public string? GraphColor { get; }
    public double GraphThickness { get; }
    public int GraphHistoryPoints { get; }
    public double? GraphScaleMin { get; }
    public double? GraphScaleMax { get; }

    /// <summary>Secondary line drawn below the first cell (the FPS application/process name).</summary>
    public string? Subtitle { get; }

    /// <summary>Fixed horizontal slot for <see cref="Subtitle"/>; 0 when the row has no subtitle.</summary>
    public double SubtitleSlotWidth { get; }
}

public readonly record struct CoreEntry(int Index, string Temperature, string Usage);

/// <summary>
/// Immutable presentation model produced from one snapshot. Contains no WPF types so it can be
/// unit tested and shared between the overlay and the settings preview.
/// </summary>
public sealed class OverlayLayout
{
    public static readonly OverlayLayout Empty = new(
        Array.Empty<OverlayRow>(), Array.Empty<CoreEntry>(), 0, false);

    public OverlayLayout(IReadOnlyList<OverlayRow> rows, IReadOnlyList<CoreEntry> cores, long sequence, bool hasAnyValue)
    {
        Rows = rows;
        Cores = cores;
        Sequence = sequence;
        HasAnyValue = hasAnyValue;
    }

    public IReadOnlyList<OverlayRow> Rows { get; }
    public IReadOnlyList<CoreEntry> Cores { get; }
    public long Sequence { get; }
    public bool HasAnyValue { get; }
    public bool IsEmpty => Rows.Count == 0;
}

/// <summary>
/// Builds the overlay layout from configuration + the latest cached snapshot.
/// Never touches hardware: it only reads the snapshot it is given.
/// </summary>
public static class OverlayLayoutBuilder
{
    private const int MaxCoreEntries = 32;

    public static OverlayLayout Build(
        AppSettings settings,
        SensorSnapshot snapshot,
        FpsSnapshot? fps = null,
        SensorHistory? history = null,
        OsdRenderContext? context = null)
    {
        var activeProfile = settings.Osd?.ActiveProfile;
        if (activeProfile is not null &&
            !(activeProfile.Style == OsdStyle.Classic && activeProfile.FollowMetricPreferences))
        {
            return OsdLayoutEngine.Build(settings, activeProfile, snapshot, fps, history, context);
        }

        var overlay = settings.Overlay;
        var rows = new List<OverlayRow>(6);
        var hasAnyValue = false;

        foreach (var group in new[] { MetricGroup.Cpu, MetricGroup.Gpu, MetricGroup.Vram, MetricGroup.Ram, MetricGroup.System, MetricGroup.Frame })
        {
            if (group == MetricGroup.Frame && !settings.Fps.Enabled)
            {
                // A disabled FPS module removes its row entirely; an enabled one shows "--" until a
                // measurement exists.
                continue;
            }

            var definitions = new List<MetricDefinition>(6);
            foreach (var preference in settings.Metrics)
            {
                if (!preference.Visible)
                {
                    continue;
                }

                var definition = MetricCatalog.Find(preference.Id);
                if (definition is not null && definition.Group == group)
                {
                    definitions.Add(definition);
                }
            }

            if (definitions.Count == 0)
            {
                continue;
            }

            var cells = new List<OverlayCell>(definitions.Count);
            string? sparklineId = null;
            var anyAvailable = false;

            foreach (var definition in definitions)
            {
                var formatted = MetricFormatter.Format(definition, snapshot, fps);
                if (!formatted.Available && !settings.Monitoring.ShowUnavailableMetrics)
                {
                    continue;
                }

                if (formatted.Available)
                {
                    anyAvailable = true;
                    hasAnyValue = true;
                    if (overlay.ShowSparklines && definition.Sparkline)
                    {
                        // Prefer the utilization series so the graph shows the load profile
                        // rather than the temperature curve when both are enabled.
                        if (sparklineId is null || (sparklineId.EndsWith(".temp", StringComparison.Ordinal) &&
                                                    definition.Id.EndsWith(".usage", StringComparison.Ordinal)))
                        {
                            sparklineId = definition.Id;
                        }
                    }
                }

                var samples = MetricWidths.For(definition);
                cells.Add(new OverlayCell(formatted.Value, formatted.Unit, formatted.Severity,
                    formatted.Available, definition, SampleValue: samples.Value, SampleUnit: samples.Unit));
            }

            if (cells.Count == 0)
            {
                continue;
            }

            var label = MetricCatalog.GroupTitle(group);
            string? subtitle = null;
            if (group == MetricGroup.Frame &&
                fps is { HasValue: true, ProcessName: { Length: > 0 } processName })
            {
                // The process name belongs below the FPS value, not beside it. The builder clips it
                // to a fixed slot so a longer name never changes the overlay width.
                subtitle = processName.Trim().Replace('\r', ' ').Replace('\n', ' ');
            }

            rows.Add(new OverlayRow(label, group, cells, sparklineId, subtitle,
                subtitle is null ? 0 : OverlayRow.FpsNameSlotWidth));
            _ = anyAvailable;
        }

        var cores = overlay.Layout == OverlayLayoutMode.Detailed ? BuildCores(snapshot) : Array.Empty<CoreEntry>();

        return new OverlayLayout(rows, cores, snapshot.Sequence, hasAnyValue);
    }

    private static IReadOnlyList<CoreEntry> BuildCores(SensorSnapshot snapshot)
    {
        var entries = new List<CoreEntry>(16);
        for (var index = 0; index < MaxCoreEntries; index++)
        {
            var hasTemp = snapshot.TryGet(SensorIds.CoreTemperature(index), out var temp) && temp.Available;
            var hasUsage = snapshot.TryGet(SensorIds.CoreUsage(index), out var usage) && usage.Available;
            if (!hasTemp && !hasUsage)
            {
                continue;
            }

            entries.Add(new CoreEntry(
                index,
                hasTemp ? temp.Value.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : "N/A",
                hasUsage ? usage.Value.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : "N/A"));
        }

        return entries;
    }
}

