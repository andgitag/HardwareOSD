using HardwareOSD.Core.Fps;
using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.TileOsd;

namespace HardwareOSD.Tests;

public sealed class TileBuilder030Tests
{
    [Fact]
    public void ElementLibraryCoversEveryCategoryAndOnlyRealSources()
    {
        var presets = TileElementLibrary.All;
        Assert.NotEmpty(presets);
        Assert.Equal(Enum.GetValues<TileElementCategory>().Length,
            presets.Select(preset => preset.Category).Distinct().Count());

        foreach (var preset in presets)
        {
            Assert.False(string.IsNullOrWhiteSpace(preset.Id));
            Assert.False(TileElementLibrary.All.Count(other => other.Id == preset.Id) > 1);

            if (preset.SourceKey is not null)
            {
                Assert.NotNull(TileSourceCatalog.Find(preset.SourceKey));
            }

            if (preset.Category == TileElementCategory.Charts)
            {
                Assert.False(string.IsNullOrWhiteSpace(preset.ChartSeries));
            }
        }
    }

    [Fact]
    public void ElementLibraryCreatesConfiguredNodesWithoutInventingValues()
    {
        var document = TileDocumentEditor.CreateNew("library");

        var metric = TileElementLibrary.Create(document, "metric.cpu.temp");
        Assert.Equal(TileNodeKind.Panel, metric.NodeKind);
        var metricLeaf = Assert.Single(metric.Children);
        Assert.Equal(TileNodeKind.Metric, metricLeaf.NodeKind);
        Assert.Equal("cpuTemp", metricLeaf.Key);
        Assert.Equal("°C", metricLeaf.Unit);
        Assert.NotNull(TileSourceCatalog.Find(metricLeaf.Key));

        var chart = TileElementLibrary.Create(document, "chart.cpu.load");
        var chartLeaf = Assert.Single(chart.Children);
        Assert.Equal(TileNodeKind.Chart, chartLeaf.NodeKind);
        Assert.Equal("cpu.usage", chartLeaf.Series);
        Assert.Equal(90, chartLeaf.HistoryPoints);
        Assert.True(chartLeaf.ShowGridLines);
        Assert.True(chartLeaf.CurrentValue);

        var row = TileElementLibrary.Create(document, "layout.row");
        Assert.Equal(TileNodeKind.Panel, row.NodeKind);
        Assert.Equal(TileFlowLayout.Horizontal, row.FlowLayout);
        Assert.Empty(row.Children);

        var separator = TileElementLibrary.Create(document, "layout.separator");
        Assert.Equal(2, separator.FrameHeight);
        Assert.Equal(0, separator.Padding);
        Assert.Empty(separator.Children);
    }

    [Fact]
    public void SevenStarterTemplatesAreEditableDocuments()
    {
        Assert.Equal(7, TileStarterTemplates.All.Count);
        Assert.Equal(7, TileStarterTemplates.All.Select(template => template.Id).Distinct().Count());

        foreach (var template in TileStarterTemplates.All)
        {
            var document = TileStarterTemplates.Create(template.Id, template.Name);
            Assert.NotNull(document);
            Assert.Equal(template.Name, document!.Name);
            Assert.Empty(document.Validate());

            var widgets = TileDocumentEditor.CountWidgets(document.Root);
            if (template.IsBlank)
            {
                Assert.Equal(0, widgets);
            }
            else
            {
                Assert.True(widgets > 0, template.Id + " should contain widgets");
            }
        }

        // A second copy is independent: editing one starter must not leak into the next.
        var first = TileStarterTemplates.Create(TileStarterTemplates.MinimalId, "first")!;
        first.Root.Children.Clear();
        var second = TileStarterTemplates.Create(TileStarterTemplates.MinimalId, "second")!;
        Assert.True(TileDocumentEditor.CountWidgets(second.Root) > 0);
    }

    [Fact]
    public void ClassicStarterKeepsTheSixRowShape()
    {
        var document = TileStarterTemplates.Create(TileStarterTemplates.ClassicId, "Classic")!;
        Assert.Equal(520, document.CanvasWidth, 3);
        Assert.Equal(430, document.CanvasHeight, 3);
        Assert.True(TileDocumentEditor.CountWidgets(document.Root) >= 18);
        Assert.Equal(6, document.Root.Children.Count);
        Assert.All(document.Root.Children, row => Assert.Equal(TileFlowLayout.Horizontal, row.FlowLayout));
    }

    [Fact]
    public void DemoSnapshotIsExplicitlyMarkedAndHasChartHistory()
    {
        var snapshot = BuilderDemoSnapshot.Snapshot;
        Assert.True(snapshot.Count > 10);
        Assert.All(snapshot.Values.Values, value =>
        {
            Assert.True(value.Available);
            Assert.Equal(SensorOrigin.Demo, value.Origin);
        });

        Assert.Equal(144, BuilderDemoSnapshot.Fps.Fps, 3);
        Assert.True(BuilderDemoSnapshot.Fps.HasValue);

        var buffer = new double[180];
        var count = BuilderDemoSnapshot.History.TryCopy("frame.fps", buffer);
        Assert.True(count > 0);
        Assert.Contains(buffer.Take(count), value => double.IsFinite(value));
    }

    [Fact]
    public void ChartSettingsFlowIntoTheRenderedScene()
    {
        var document = TileDocumentEditor.CreateNew("chart-scene");
        var tile = TileElementLibrary.Create(document, "chart.cpu.load");
        var chart = tile.Children[0];
        chart.Series = "cpu.usage";
        chart.Thickness = 5;
        chart.Opacity = 0.5;
        chart.HistoryPoints = 20;
        Assert.NotNull(TileDocumentEditor.AddNode(document, document.Root, tile));

        var scene = TileSceneBuilder.Build(document, BuilderDemoSnapshot.Snapshot,
            BuilderDemoSnapshot.Fps, BuilderDemoSnapshot.History);
        var rendered = Assert.Single(scene.Elements.OfType<TileChartScene>());
        Assert.Equal(5, rendered.LineThickness, 3);
        Assert.Equal(0.5, rendered.Opacity, 3);
        var line = Assert.Single(rendered.Lines);
        Assert.InRange(line.Values.Length, 1, 20);
    }

    [Fact]
    public void CopyForPasteAssignsFreshIdsToTheWholeSubtree()
    {
        var document = TileDocumentEditor.CreateNew("copy");
        var group = TileElementLibrary.Create(document, "layout.column");
        Assert.NotNull(TileDocumentEditor.AddNode(document, document.Root, group));
        var metric = TileElementLibrary.Create(document, "metric.gpu.temp");
        group.Children.Add(metric);

        var copy = TileDocumentEditor.CloneForPaste(group);
        var originalIds = group.Descendants().Prepend(group)
            .Select(node => node.NodeId).ToHashSet(StringComparer.Ordinal);
        var copyIds = copy.Descendants().Prepend(copy)
            .Select(node => node.NodeId).ToList();

        Assert.Equal(originalIds.Count, copyIds.Count);
        Assert.All(copyIds, id => Assert.DoesNotContain(id, originalIds));
        Assert.Equal(copyIds.Count, copyIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ReparentMovesNodeAndRejectsCycles()
    {
        var document = TileDocumentEditor.CreateNew("reparent");
        var left = TileElementLibrary.Create(document, "layout.column");
        var right = TileElementLibrary.Create(document, "layout.column");
        TileDocumentEditor.AddNode(document, document.Root, left);
        TileDocumentEditor.AddNode(document, document.Root, right);
        var metric = TileElementLibrary.Create(document, "metric.cpu.load");
        left.Children.Add(metric);

        Assert.True(TileDocumentEditor.Reparent(document, metric, right, 12, 8));
        Assert.Same(right, TileDocumentEditor.ParentOf(document, metric.NodeId));
        Assert.Equal(12, metric.X);
        Assert.Equal(8, metric.Y);

        // A container cannot be moved into one of its own descendants.
        Assert.False(TileDocumentEditor.Reparent(document, right, metric));
        Assert.False(TileDocumentEditor.Reparent(document, right, right));
        Assert.Same(document.Root, TileDocumentEditor.ParentOf(document, right.NodeId));
    }
}
