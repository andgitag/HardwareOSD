using HardwareOSD.Core.Config;
using HardwareOSD.Core.Metrics;
using HardwareOSD.Core.TileOsd;

namespace HardwareOSD.Core.Overlay;

/// <summary>
/// One bridge between the metric catalogue and the editable OSD element trees. Metrics and OSD
/// elements must never drift: checking a metric in the Metrics page either updates the virtual
/// Classic preferences or materialises the corresponding sensor element in the active profile.
/// </summary>
public static class OsdMetricBridge
{
    public static OsdElement? FindElement(OsdProfile profile, string metricId)
    {
        var definition = MetricCatalog.Find(metricId);
        var rawId = definition is null ? null : MetricCatalog.SensorIdFor(definition);
        return OsdProfile.Flatten(profile.Elements).FirstOrDefault(element =>
            string.Equals(element.SensorId, metricId, StringComparison.Ordinal) ||
            (rawId is not null && string.Equals(element.SensorId, rawId, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Finds or creates the sensor element for a metric. The element is placed in the group matching
    /// its metric group (CPU/GPU/VRAM/RAM/FPS), so an added GPU fan row appears next to GPU metrics
    /// instead of at the end of an unrelated section.
    /// </summary>
    public static OsdElement EnsureElement(OsdProfile profile, MetricDefinition definition)
    {
        var existing = FindElement(profile, definition.Id);
        if (existing is not null)
        {
            return existing;
        }

        var element = new OsdElement
        {
            Id = $"metric-{definition.Id.Replace('.', '-')}",
            Kind = OsdElementKind.Sensor,
            SensorId = definition.Id,
            Label = definition.DisplayName,
            Visible = true,
        };

        var title = MetricCatalog.GroupTitle(definition.Group);
        var group = profile.Elements.FirstOrDefault(candidate =>
            candidate.Kind == OsdElementKind.Group &&
            (string.Equals(candidate.Label, title, StringComparison.OrdinalIgnoreCase) ||
             candidate.Children.Any(child => MetricCatalog.Find(child.SensorId)?.Group == definition.Group)));

        if (group is null)
        {
            group = new OsdElement
            {
                Id = $"metric-group-{definition.Group.ToString().ToLowerInvariant()}",
                Kind = OsdElementKind.Group,
                Label = title,
                GroupLayout = OsdGroupLayout.Rows,
            };
            profile.Elements.Add(group);
        }

        if (group.Children.All(child => child.Id != element.Id))
        {
            group.Children.Add(element);
        }

        profile.FollowMetricPreferences = false;
        return element;
    }

    /// <summary>
    /// Applies a metric visibility change to a profile. Classic profiles that still follow the
    /// metric matrix keep doing so; every other profile materialises or hides its own element.
    /// </summary>
    public static OsdElement? SetMetricVisible(OsdProfile profile, MetricDefinition definition, bool visible,
        bool materialiseWhenMissing)
    {
        var element = FindElement(profile, definition.Id);
        if (element is null && visible && materialiseWhenMissing)
        {
            element = EnsureElement(profile, definition);
        }

        if (element is not null)
        {
            element.Visible = visible;
            profile.FollowMetricPreferences = false;
        }

        return element;
    }

    /// <summary>Ensures that every currently visible metric exists before a profile stops following the matrix.</summary>
    public static void MaterializeVisibleMetrics(OsdProfile profile, IEnumerable<MetricPreference> preferences)
    {
        foreach (var preference in preferences.Where(p => p.Visible))
        {
            if (MetricCatalog.Find(preference.Id) is { } definition)
            {
                EnsureElement(profile, definition);
            }
        }
    }

    // ---------------------------------------------------------------- tile profiles

    /// <summary>
    /// Finds the document widgets that represent one catalogue metric. A metric can appear more
    /// than once (for example CPU load in a summary row and in a detail row), so visibility is
    /// applied to every matching node.
    /// </summary>
    public static IReadOnlyList<TileNode> FindTileNodes(OsdProfile profile,
        MetricDefinition definition)
    {
        var document = profile.TileDesign;
        if (document is null)
        {
            return Array.Empty<TileNode>();
        }

        var tileKey = MetricTileKeys.For(definition);
        var rawId = MetricCatalog.SensorIdFor(definition);
        return document.Root.Descendants()
            .Where(node => node.NodeKind is TileNodeKind.Metric or TileNodeKind.Bar
                or TileNodeKind.Gauge or TileNodeKind.Chart)
            .Where(node => Matches(node, definition.Id, tileKey, rawId))
            .ToList();
    }

    public static bool IsTileMetricVisible(OsdProfile profile, MetricDefinition definition)
        => FindTileNodes(profile, definition).Any(node => node.Visible != false);

    /// <summary>
    /// Applies a Metrics-page selection to a tile profile. Existing widgets are toggled; when the
    /// user enables a metric that is not in the document yet, one editable metric tile is appended
    /// in a "Metrics" section at the bottom of the canvas.
    /// </summary>
    public static void SetTileMetricVisible(OsdProfile profile, MetricDefinition definition,
        bool visible, bool materialiseWhenMissing)
    {
        var source = profile.TileDesign;
        if (source is null)
        {
            return;
        }

        var document = source.Clone();
        var tileKey = MetricTileKeys.For(definition);
        var rawId = MetricCatalog.SensorIdFor(definition);
        var matches = document.Root.Descendants()
            .Where(node => node.NodeKind is TileNodeKind.Metric or TileNodeKind.Bar
                or TileNodeKind.Gauge or TileNodeKind.Chart)
            .Where(node => Matches(node, definition.Id, tileKey, rawId))
            .ToList();
        if (matches.Count == 0 && visible && materialiseWhenMissing)
        {
            AddTileMetricToGroup(document, definition);
        }
        else
        {
            foreach (var node in matches)
            {
                node.Visible = visible;
            }
        }

        RemoveEmptyAddedRows(document);
        RemoveEmptyBridgePanels(document);
        TileDocumentEditor.FitCanvasToContent(document, growOnly: false, ignoreHidden: true);
        profile.SetTileDesign(document);
        profile.FollowMetricPreferences = false;
    }

    /// <summary>
    /// Applies a whole metric matrix to a tile profile in one clone. Visible metrics that are not
    /// in the document yet are inserted into their logical group (CPU/GPU/VRAM/RAM/FPS) instead of a
    /// catch-all panel at the bottom; hidden metrics stay in place and the canvas shrinks to the
    /// visible content.
    /// </summary>
    public static void ApplyTileMetricSet(OsdProfile profile, IEnumerable<MetricPreference> preferences)
    {
        if (profile.TileDesign is null)
        {
            return;
        }

        var document = profile.TileDesign.Clone();
        var list = preferences.ToList();
        foreach (var preference in list.Where(item => item.Visible))
        {
            if (MetricCatalog.Find(preference.Id) is not { } definition)
            {
                continue;
            }

            var matches = FindTileNodes(document, definition);
            if (matches.Count == 0)
            {
                AddTileMetricToGroup(document, definition);
            }
            else
            {
                foreach (var node in matches)
                {
                    node.Visible = true;
                }
            }
        }

        foreach (var preference in list.Where(item => !item.Visible))
        {
            if (MetricCatalog.Find(preference.Id) is not { } definition)
            {
                continue;
            }

            foreach (var node in FindTileNodes(document, definition))
            {
                node.Visible = false;
            }
        }

        RemoveEmptyAddedRows(document);
        RemoveEmptyBridgePanels(document);
        TileDocumentEditor.FitCanvasToContent(document, growOnly: false, ignoreHidden: true);
        profile.SetTileDesign(document);
        profile.FollowMetricPreferences = false;
    }

    private static List<TileNode> FindTileNodes(TileDesignDocument document,
        MetricDefinition definition)
    {
        var tileKey = MetricTileKeys.For(definition);
        var rawId = MetricCatalog.SensorIdFor(definition);
        return document.Root.Descendants()
            .Where(node => node.NodeKind is TileNodeKind.Metric or TileNodeKind.Bar
                or TileNodeKind.Gauge or TileNodeKind.Chart)
            .Where(node => Matches(node, definition.Id, tileKey, rawId))
            .ToList();
    }

    /// <summary>
    /// Marker for rows this bridge adds inside an existing metric group. Keeping the marker lets a
    /// later Standard Set remove exactly those rows instead of leaving invisible reserved height.
    /// </summary>
    private const string AddedMetricRowPrefix = "metric-row-";

    private static void AddTileMetricToGroup(TileDesignDocument document,
        MetricDefinition definition)
    {
        var groupNodes = document.Root.Descendants()
            .Where(node => node.Visible != false &&
                (node.NodeKind is TileNodeKind.Metric or TileNodeKind.Bar
                    or TileNodeKind.Gauge or TileNodeKind.Chart) &&
                DefinitionForTileKey(node.Key)?.Group == definition.Group)
            .ToList();
        var reference = groupNodes.OrderByDescending(node => node.Y ?? 0).FirstOrDefault();
        if (reference is null)
        {
            AddTileMetric(document, definition);
            return;
        }

        var row = TileDocumentEditor.ParentOf(document, reference.NodeId);
        if (row is null || row.NodeKind != TileNodeKind.Panel)
        {
            AddTileMetric(document, definition);
            return;
        }

        var groupRows = document.Root.Descendants()
            .Where(node => node.NodeKind == TileNodeKind.Panel &&
                node.Children.Any(child => IsMetricNode(child) &&
                    DefinitionForTileKey(child.Key)?.Group == definition.Group))
            .Distinct()
            .ToList();
        var lastRow = (groupRows.Count == 0 ? row : groupRows
                .OrderBy(candidate => candidate.Y ?? 0)
                .Last());
        if (lastRow.NodeId?.StartsWith(AddedMetricRowPrefix, StringComparison.Ordinal) == true &&
            TryAppendToAddedRow(document, lastRow, reference, definition))
        {
            return;
        }

        AddGroupRowAfter(document, lastRow, reference, definition);
    }

    private static bool IsMetricNode(TileNode node)
        => node.NodeKind is TileNodeKind.Metric or TileNodeKind.Bar
            or TileNodeKind.Gauge or TileNodeKind.Chart;

    /// <summary>
    /// Appends a metric to a row this bridge created. Authored design rows are never resized: their
    /// frames were laid out for the complete metric set, including widgets that are currently hidden.
    /// </summary>
    private static bool TryAppendToAddedRow(TileDesignDocument document, TileNode row,
        TileNode reference, MetricDefinition definition)
    {
        var padding = Math.Max(0, row.Padding ?? 0);
        var gap = Math.Max(0, row.Gap ?? document.Theme.Gap);
        var available = Math.Max(80, (row.FrameWidth ?? row.Width ?? 200) - padding * 2);
        var visible = row.Children.Where(child => child.Visible != false).ToList();
        var used = visible.Sum(child => child.FrameWidth ?? child.Width ?? 40) +
                   Math.Max(0, visible.Count - 1) * gap;
        var width = AddedMetricWidth(reference);
        if (used + gap + width > available + 0.5)
        {
            return false;
        }

        var childHeight = Math.Max(12,
            (row.FrameHeight ?? row.Height ?? 24) - padding * 2);
        var hasGroupLabel = row.Children.Any(child => child.NodeKind == TileNodeKind.Text);
        var node = CreateMetricNode(document, definition, reference, width, childHeight,
            hasGroupLabel);
        node.X = null;
        node.Y = null;
        row.Children.Add(node);
        return true;
    }

    private static void AddGroupRowAfter(TileDesignDocument document, TileNode row,
        TileNode reference, MetricDefinition definition)
    {
        var parent = TileDocumentEditor.ParentOf(document, row.NodeId);
        if (parent is null)
        {
            AddTileMetric(document, definition);
            return;
        }

        var rowWidth = Math.Max(80, row.FrameWidth ?? row.Width ?? 200);
        if (parent.FrameWidth is { } parentWidth && parentWidth > 0)
        {
            rowWidth = Math.Min(rowWidth, Math.Max(80, parentWidth));
        }

        var rowHeight = Math.Max(18, row.FrameHeight ?? row.Height ?? 22);
        var padding = Math.Max(0, row.Padding ?? 0);
        var gap = Math.Max(0, row.Gap ?? document.Theme.Gap);
        var newRow = new TileNode
        {
            NodeId = AddedMetricRowPrefix + definition.Group.ToString().ToLowerInvariant() + "-" +
                     Guid.NewGuid().ToString("N"),
            Name = MetricCatalog.GroupTitle(definition.Group) + " metrics",
            Type = "panel",
            Kind = "panel",
            Layout = "horizontal",
            X = row.X,
            Y = row.Y is null ? null : (row.Y ?? 0) + rowHeight + gap,
            FrameWidth = rowWidth,
            FrameHeight = rowHeight,
            Width = rowWidth,
            Height = rowHeight,
            Padding = row.Padding,
            Gap = row.Gap,
            Background = row.Background,
            Color = row.Color,
            BackgroundColor = row.BackgroundColor,
            BorderColor = row.BorderColor,
            BorderWidth = row.BorderWidth,
            Radius = row.Radius,
            Clip = row.Clip,
            Children = new List<TileNode>(),
        };

        var groupTitle = MetricCatalog.GroupTitle(definition.Group);
        var labelSource = document.Root.Descendants()
            .Where(node => node.NodeKind == TileNodeKind.Text && node.Visible != false &&
                string.Equals(node.Text, groupTitle, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(node => node.Y ?? 0)
            .FirstOrDefault();
        var hasGroupLabel = labelSource is not null;
        if (labelSource is not null)
        {
            var labelWidth = Math.Max(20, labelSource.FrameWidth ?? labelSource.Width ?? 40);
            newRow.Children.Add(new TileNode
            {
                NodeId = TileDocumentEditor.NewNodeId(),
                Name = labelSource.Text,
                Type = "text",
                Kind = "text",
                Text = labelSource.Text,
                Size = labelSource.Size,
                Family = labelSource.Family,
                Weight = labelSource.Weight,
                Align = labelSource.Align ?? "left",
                Color = labelSource.Color,
                TextColor = labelSource.TextColor,
                FrameWidth = labelWidth,
                FrameHeight = Math.Max(12, rowHeight - padding * 2),
                Width = labelWidth,
                Height = Math.Max(12, rowHeight - padding * 2),
                X = null,
                Y = null,
                Visible = true,
            });
        }

        var node = CreateMetricNode(document, definition, reference,
            AddedMetricWidth(reference), Math.Max(12, rowHeight - padding * 2), hasGroupLabel);
        node.X = null;
        node.Y = null;
        newRow.Children.Add(node);

        var index = parent.Children.IndexOf(row);
        parent.Children.Insert(index + 1, newRow);
        if (row.Y is not null)
        {
            var delta = rowHeight + gap;
            var rowBottom = (row.Y ?? 0) + rowHeight;
            foreach (var sibling in parent.Children)
            {
                if (ReferenceEquals(sibling, row) || ReferenceEquals(sibling, newRow) ||
                    sibling.Y is not { } y || y < rowBottom)
                {
                    continue;
                }

                sibling.Y = y + delta;
            }
        }

        if (parent.NodeId == document.Root.NodeId)
        {
            return;
        }

        if (parent.FlowLayout == TileFlowLayout.Vertical)
        {
            parent.FrameHeight = (parent.FrameHeight ?? parent.Height ?? 0) + rowHeight + gap;
            parent.Height = parent.FrameHeight;
        }
        else if (newRow.Y is { } newY && parent.FrameHeight is { } parentHeight &&
                 newY + rowHeight > parentHeight)
        {
            parent.FrameHeight = newY + rowHeight + padding;
            parent.Height = parent.FrameHeight;
        }
    }

    private static double AddedMetricWidth(TileNode reference)
        => Math.Clamp(reference.FrameWidth ?? reference.Width ?? 84, 64, 96);

    private static TileNode CreateMetricNode(TileDesignDocument document, MetricDefinition definition,
        TileNode reference, double width, double height, bool hasGroupLabel)
    {
        var shortLabel = definition.DisplayName
            .Replace("GPU ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("CPU ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .ToUpperInvariant();
        return new TileNode
        {
            NodeId = TileDocumentEditor.NewNodeId(),
            Name = definition.DisplayName,
            Type = "metric",
            Kind = "metric",
            Key = MetricTileKeys.For(definition),
            Label = shortLabel,
            LabelPosition = hasGroupLabel ? "none" : "left",
            LabelAlign = "left",
            Align = "right",
            Unit = definition.SampleUnit,
            Digits = reference.Digits,
            ValueSize = reference.ValueSize ?? 16,
            LabelSize = reference.LabelSize ?? 10,
            UnitSize = reference.UnitSize ?? 10,
            Family = reference.Family ?? document.Theme.FontFamily,
            ValueColor = reference.ValueColor ?? document.Theme.Text,
            LabelColor = reference.LabelColor ?? document.Theme.Subtext,
            UnitColor = reference.UnitColor ?? document.Theme.Subtext,
            Weight = reference.Weight,
            Format = reference.Format,
            FrameWidth = width,
            FrameHeight = height,
            Width = width,
            Height = height,
            Visible = true,
            Series = MetricCatalog.SensorIdFor(definition),
        };
    }

    /// <summary>
    /// Removes group rows added by this bridge when every metric inside them is hidden again. This
    /// is what lets Standard Set return the canvas to its authored height instead of keeping the
    /// extra space reserved by empty rows.
    /// </summary>
    private static void RemoveEmptyAddedRows(TileDesignDocument document)
    {
        foreach (var row in document.Root.Descendants()
                     .Where(node => node.NodeId?.StartsWith(AddedMetricRowPrefix,
                         StringComparison.Ordinal) == true)
                     .ToList())
        {
            if (row.Descendants().Any(node => node.Visible != false && IsMetricNode(node)))
            {
                continue;
            }

            var parent = TileDocumentEditor.ParentOf(document, row.NodeId);
            if (parent is null)
            {
                continue;
            }

            var index = parent.Children.IndexOf(row);
            if (index < 0)
            {
                continue;
            }

            var height = row.FrameHeight ?? row.Height ?? 0;
            var gap = Math.Max(0, row.Gap ?? document.Theme.Gap);
            var top = row.Y;
            parent.Children.RemoveAt(index);
            if (top is { } y)
            {
                var delta = height + gap;
                foreach (var sibling in parent.Children)
                {
                    if (sibling.Y is { } siblingY && siblingY >= y + height)
                    {
                        sibling.Y = Math.Max(0, siblingY - delta);
                    }
                }
            }

            if (parent.NodeId == document.Root.NodeId ||
                parent.FlowLayout != TileFlowLayout.Vertical)
            {
                continue;
            }

            var newHeight = Math.Max(0,
                (parent.FrameHeight ?? parent.Height ?? 0) - height - gap);
            parent.FrameHeight = newHeight;
            parent.Height = newHeight;
        }
    }

    private static void RemoveEmptyBridgePanels(TileDesignDocument document)
    {
        foreach (var panel in document.Root.Children
                     .Where(child => child.NodeId?.StartsWith("metric-panel-",
                         StringComparison.Ordinal) == true)
                     .ToList())
        {
            var visible = panel.Descendants().Any(node =>
                node.Visible != false && node.NodeKind != TileNodeKind.Panel);
            if (!visible)
            {
                document.Root.Children.Remove(panel);
                continue;
            }

            if (panel.FlowLayout == TileFlowLayout.Vertical)
            {
                var children = panel.Children.Where(child => child.Visible != false).ToList();
                var height = Math.Max(18, (panel.Padding ?? 0) * 2 +
                    children.Sum(child => child.FrameHeight ?? child.Height ?? 0) +
                    Math.Max(0, children.Count - 1) * (panel.Gap ?? 2));
                panel.FrameHeight = height;
                panel.Height = height;
            }
        }
    }

    /// <summary>
    /// Applies the Metrics presentation fields that have a direct tile equivalent (label, unit,
    /// decimals, value colour, visible state and chart/bar options) to every matching widget.
    /// </summary>
    public static void UpdateTileMetric(OsdProfile profile, MetricDefinition definition, bool visible,
        string? label, string? unit, int decimals, string? valueColor, string? graphColor,
        double? progressMin, double? progressMax, bool showGraph,
        bool showProgress, double? graphThickness = null, int? graphHistory = null,
        double? warningThreshold = null, double? criticalThreshold = null)
    {
        if (profile.TileDesign is null)
        {
            return;
        }

        if (visible && FindTileNodes(profile, definition).Count == 0)
        {
            SetTileMetricVisible(profile, definition, true, materialiseWhenMissing: true);
        }

        var source = profile.TileDesign;
        if (source is null)
        {
            return;
        }

        var document = source.Clone();
        var tileKey = MetricTileKeys.For(definition);
        var rawId = MetricCatalog.SensorIdFor(definition);
        foreach (var node in document.Root.Descendants().Where(node =>
                     (node.NodeKind is TileNodeKind.Metric or TileNodeKind.Bar
                         or TileNodeKind.Gauge or TileNodeKind.Chart) &&
                     Matches(node, definition.Id, tileKey, rawId)))
        {
            node.Visible = visible && node.NodeKind switch
            {
                TileNodeKind.Chart => showGraph,
                TileNodeKind.Bar or TileNodeKind.Gauge => showProgress,
                _ => true,
            };
            if (node.NodeKind == TileNodeKind.Metric)
            {
                node.Label = label;
                node.Unit = unit;
                node.Digits = decimals >= 0 ? Math.Clamp(decimals, 0, 6) : null;
                node.ValueColor = string.IsNullOrWhiteSpace(valueColor) ? null : valueColor;
                node.MiniGraph = showGraph;
                node.GraphColor = string.IsNullOrWhiteSpace(graphColor) ? null : graphColor;
                node.Thickness = graphThickness;
                node.HistoryPoints = graphHistory;
                node.WarningThreshold = warningThreshold;
                node.CriticalThreshold = criticalThreshold;
                node.Series ??= MetricCatalog.SensorIdFor(definition);
            }
            else if (node.NodeKind == TileNodeKind.Chart)
            {
                node.Color = string.IsNullOrWhiteSpace(graphColor) ? null : graphColor;
                node.Thickness = graphThickness;
                node.HistoryPoints = graphHistory;
            }
            else
            {
                node.Color = string.IsNullOrWhiteSpace(graphColor) ? null : graphColor;
                node.Minimum = progressMin;
                node.Maximum = progressMax;
                node.WarningThreshold = warningThreshold;
                node.CriticalThreshold = criticalThreshold;
            }
        }

        RemoveEmptyAddedRows(document);
        RemoveEmptyBridgePanels(document);
        TileDocumentEditor.FitCanvasToContent(document, growOnly: false, ignoreHidden: true);
        profile.SetTileDesign(document);
        profile.FollowMetricPreferences = false;
    }

    /// <summary>Finds the catalogue metric behind a tile widget key, if there is one.</summary>
    public static MetricDefinition? DefinitionForTileKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var raw = key.StartsWith("metric:", StringComparison.OrdinalIgnoreCase) ? key[7..] : key;
        return MetricCatalog.FindByTileKey(raw);
    }

    private static void AddTileMetric(TileDesignDocument document, MetricDefinition definition)
    {
        var theme = document.Theme;
        var root = document.Root;
        var panel = root.Children.FirstOrDefault(child =>
            child.NodeKind == TileNodeKind.Panel &&
            string.Equals(child.Name, "Metrics", StringComparison.OrdinalIgnoreCase));
        var width = Math.Max(80, document.CanvasWidth - 36);
        if (panel is null)
        {
            var bottom = root.Children
                .Select(child => (child.Y ?? 0) + (child.FrameHeight ?? child.Height ?? 0))
                .DefaultIfEmpty(0)
                .Max();
            panel = new TileNode
            {
                NodeId = "metric-panel-" + definition.Group.ToString().ToLowerInvariant(),
                Type = "panel",
                Kind = "panel",
                Name = "Metrics",
                Layout = "vertical",
                Gap = 2,
                Padding = 0,
                X = 18,
                Y = bottom + 4,
                FrameWidth = width,
                FrameHeight = 22,
                Width = width,
                Height = 22,
            };
            root.Children.Add(panel);
        }

        var fontSize = Math.Max(8, theme.FontSize);
        var unitSize = Math.Max(8, fontSize * 0.78);
        var labelSize = Math.Max(8, fontSize * 0.82);
        var node = new TileNode
        {
            NodeId = "metric-" + definition.Id.Replace('.', '-'),
            Type = "metric",
            Kind = "metric",
            Name = definition.DisplayName,
            Key = MetricTileKeys.For(definition),
            Label = definition.DisplayName,
            LabelPosition = "left",
            LabelSize = labelSize,
            ValueSize = fontSize,
            UnitSize = unitSize,
            Unit = definition.SampleUnit,
            Family = theme.FontFamily,
            Align = "left",
            ValueColor = theme.Text,
            LabelColor = theme.Accent,
            UnitColor = theme.Subtext,
            FrameWidth = width,
            FrameHeight = Math.Max(18, fontSize * 1.35 + 2),
            Width = width,
            Height = Math.Max(18, fontSize * 1.35 + 2),
            Visible = true,
        };
        panel.Children.Add(node);
        panel.FrameHeight = panel.Children.Sum(child => child.FrameHeight ?? child.Height ?? 0) +
                            Math.Max(0, panel.Children.Count - 1) * (panel.Gap ?? 2);
        panel.Height = panel.FrameHeight;
        var rootBottom = root.Children
            .Select(child => (child.Y ?? 0) + (child.FrameHeight ?? child.Height ?? 0))
            .DefaultIfEmpty(0)
            .Max();
        var height = Math.Max(document.CanvasHeight, rootBottom + 8);
        root.FrameHeight = height;
        root.Height = height;
    }

    private static bool Matches(TileNode node, string metricId, string tileKey, string? rawId)
    {
        if (string.IsNullOrWhiteSpace(node.Key))
        {
            return false;
        }

        return string.Equals(node.Key, metricId, StringComparison.Ordinal) ||
               string.Equals(node.Key, tileKey, StringComparison.Ordinal) ||
               (rawId is not null && string.Equals(node.Key, rawId, StringComparison.Ordinal)) ||
               string.Equals(node.Key, "metric:" + metricId, StringComparison.Ordinal);
    }
}
