using HardwareOSD.Core.Config;
using HardwareOSD.Core.Fps;
using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.TileOsd;

namespace HardwareOSD.Tests;

public sealed class TileEditorTests
{
    private static SensorSnapshot Snapshot()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var values = new Dictionary<string, SensorValue>(StringComparer.Ordinal)
        {
            [SensorIds.CpuUsage] = Value(SensorIds.CpuUsage, 37, "%", SensorKind.Load, now),
            [SensorIds.GpuUsage] = Value(SensorIds.GpuUsage, 88, "%", SensorKind.Load, now),
            [SensorIds.GpuTemperature] = Value(SensorIds.GpuTemperature, 67, "°C",
                SensorKind.Temperature, now),
            [SensorIds.RamPercent] = Value(SensorIds.RamPercent, 33.6, "%", SensorKind.Load, now),
        };
        for (var core = 0; core < 4; core++)
        {
            values[SensorIds.CoreUsage(core)] = Value(SensorIds.CoreUsage(core), 20 + core, "%",
                SensorKind.Load, now);
        }

        return SensorSnapshot.Create(1, now, values);
    }

    private static SensorValue Value(string id, double value, string unit, SensorKind kind, long now)
        => new(id, value, unit, kind, SensorOrigin.HardwareSensor, "test", now);

    private static FpsSnapshot Fps() => new(1, Environment.TickCount64, FpsStatus.Capturing,
        FpsSource.Presented, false, 42, "game.exe", 144.2, 6.94, 121.5, null);

    [Fact]
    public void EveryCatalogDesignPreparesForEditingWithoutChangingItsGeometry()
    {
        var context = TileRenderContext.Empty;
        foreach (var design in TileDesignCatalog.All)
        {
            var before = new Dictionary<string, TileRect>(StringComparer.Ordinal);
            TileSceneBuilder.Build(design, Snapshot(), Fps(), null, null, context, before);

            var editable = design.Clone();
            TileDocumentEditor.PrepareForEditing(editable);
            var after = new Dictionary<string, TileRect>(StringComparer.Ordinal);
            TileSceneBuilder.Build(editable, Snapshot(), Fps(), null, null, context, after);

            Assert.All(editable.Root.Descendants(), node => Assert.False(string.IsNullOrEmpty(node.NodeId),
                design.Id + " left a node without an id"));
            Assert.Equal(editable.CanvasWidth, design.CanvasWidth, 3);
            Assert.Equal(editable.CanvasHeight, design.CanvasHeight, 3);

            foreach (var (path, rect) in before)
            {
                Assert.True(after.ContainsKey(path), design.Id + " lost bounds for " + path);
                var other = after[path];
                Assert.True(Math.Abs(rect.X - other.X) < 0.01, $"{design.Id} {path} x {rect.X} -> {other.X}");
                Assert.True(Math.Abs(rect.Y - other.Y) < 0.01, $"{design.Id} {path} y {rect.Y} -> {other.Y}");
                Assert.True(Math.Abs(rect.Width - other.Width) < 0.01,
                    $"{design.Id} {path} width {rect.Width} -> {other.Width}");
                Assert.True(Math.Abs(rect.Height - other.Height) < 0.01,
                    $"{design.Id} {path} height {rect.Height} -> {other.Height}");
            }
        }
    }

    [Fact]
    public void WrapperPanelWidgetsFillThePaddedPanel()
    {
        var design = TileDesignCatalog.Find("cfx-benchmark")!;
        var scene = TileSceneBuilder.Build(design, Snapshot(), Fps(), null);
        var metric = scene.Elements.OfType<TileMetricScene>().First(item => item.Label == "FPS");

        // The FPS tile starts at y = 55.941 with 12 px padding; the widget must sit inside that
        // content box, not at the panel's absolute position again.
        Assert.Equal(12, metric.Bounds.X, 1);
        Assert.Equal(67.941, metric.Bounds.Y, 1);
        Assert.Equal(232, metric.Bounds.Width, 1);
        Assert.False(string.IsNullOrWhiteSpace(metric.Value));

        var title = scene.Elements.OfType<TileTextScene>()
            .First(item => item.Text.Contains("BENCHMARK", StringComparison.Ordinal));
        Assert.Equal(12, title.Bounds.X, 1);
        Assert.Equal(12, title.Bounds.Y, 1);
        Assert.Equal(496, title.Bounds.Width, 1);
    }

    [Fact]
    public void CanvasPanelWithOneChildKeepsItsAuthoredFrame()
    {
        var document = TileDocumentEditor.CreateNew("one");
        var tile = TileDocumentEditor.CreateTile(TileNodeKind.Metric, 120, 64, 200, 80,
            document.Theme);
        document.Root.Children.Add(tile);
        TileDocumentEditor.SetCanvasSize(document, 520, 400);

        var bounds = new Dictionary<string, TileRect>(StringComparer.Ordinal);
        TileSceneBuilder.Build(document, Snapshot(), Fps(), null, null, null, bounds);
        var rect = bounds["0"];
        Assert.Equal(120, rect.X, 2);
        Assert.Equal(64, rect.Y, 2);
        Assert.Equal(200, rect.Width, 2);
        Assert.Equal(80, rect.Height, 2);
    }

    [Fact]
    public void AddDuplicateRemoveAndOrderingKeepTheDocumentValid()
    {
        var document = TileDocumentEditor.CreateNew("edit");
        var first = TileDocumentEditor.AddTile(document, document.Root, TileNodeKind.Metric, 0, 0);
        var second = TileDocumentEditor.AddTile(document, document.Root, TileNodeKind.Chart, 0, 90);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(2, TileDocumentEditor.CountWidgets(document.Root));
        Assert.Empty(document.Validate());

        var copy = TileDocumentEditor.DuplicateTile(document, first!);
        Assert.NotNull(copy);
        Assert.NotEqual(first!.NodeId, copy!.NodeId);
        Assert.NotEqual(first.Children[0].NodeId, copy.Children[0].NodeId);
        Assert.Equal(3, TileDocumentEditor.CountWidgets(document.Root));

        Assert.True(TileDocumentEditor.MoveOrder(document, copy, -1));
        Assert.Equal(copy.NodeId, document.Root.Children[0].NodeId);

        Assert.True(TileDocumentEditor.Remove(document, copy));
        Assert.Equal(2, TileDocumentEditor.CountWidgets(document.Root));
        Assert.Empty(document.Validate());
    }

    [Fact]
    public void TileLimitAndContainerDepthAreEnforced()
    {
        var document = TileDocumentEditor.CreateNew("limits");
        var scope = document.Root;
        for (var level = 0; level < TileDocumentEditor.MaxContainerDepth; level++)
        {
            var container = TileDocumentEditor.AddTile(document, scope, TileNodeKind.Panel, 0, 0);
            Assert.NotNull(container);
            scope = container!;
        }

        Assert.Null(TileDocumentEditor.AddTile(document, scope, TileNodeKind.Panel, 0, 0));

        var flat = TileDocumentEditor.CreateNew("flat");
        for (var index = 0; index < TileDocumentEditor.MaxTiles; index++)
        {
            Assert.NotNull(TileDocumentEditor.AddTile(flat, flat.Root, TileNodeKind.Text, 0,
                index * 10));
        }

        Assert.Null(TileDocumentEditor.AddTile(flat, flat.Root, TileNodeKind.Text, 0, 0));
    }

    [Fact]
    public void SnappingUsesTheDocumentGridAndCanBeDisabled()
    {
        var document = TileDocumentEditor.CreateNew("snap");
        document.GridSize = 8;
        document.SnapToGrid = true;
        Assert.Equal(24, TileDocumentEditor.Snap(document, 21));
        Assert.Equal(0, TileDocumentEditor.Snap(document, 3));
        document.SnapToGrid = false;
        Assert.Equal(21, TileDocumentEditor.Snap(document, 21));
    }

    [Fact]
    public void CustomDesignSurvivesProfileRoundTripAndKeepsLegacyProfilesOnTheCatalog()
    {
        var profile = new OsdProfile
        {
            Id = "custom",
            Name = "Custom",
            Layout = OsdProfileLayout.Tile,
            DesignId = string.Empty,
        };
        var document = TileDocumentEditor.CreateNew("Custom");
        TileDocumentEditor.AddTile(document, document.Root, TileNodeKind.Gauge, 12, 16);
        profile.SetTileDesign(document);

        Assert.True(profile.HasCustomDesign);
        Assert.NotNull(profile.TileDesign);
        Assert.Equal(1, TileDocumentEditor.CountWidgets(profile.TileDesign!.Root));
        Assert.Equal(12, profile.TileDesign.Root.Children[0].X!.Value, 2);

        var copy = profile.Clone();
        Assert.NotNull(copy.TileDesign);
        Assert.Equal(profile.TileDesignJson, copy.TileDesignJson);

        var legacy = new OsdProfile
        {
            Id = "legacy",
            Name = "Legacy",
            Layout = OsdProfileLayout.Tile,
            DesignId = "cfx-benchmark",
        };
        Assert.False(legacy.HasCustomDesign);
        Assert.Equal("cfx-benchmark", legacy.TileDesign!.Id);
    }

    [Fact]
    public void LegacyOverridesFoldIntoTheEditableDocument()
    {
        var design = TileDesignCatalog.Find("cfx-benchmark")!.Clone();
        var overrides = new TileOverrideSettings();
        var target = overrides.Ensure("1/0");
        target.Label = "Custom FPS";
        target.Key = "fpsCurrent";
        target.Digits = 2;

        TileDocumentEditor.MergeOverrides(design, overrides);
        var node = design.FindNode("1/0")!;
        Assert.Equal("Custom FPS", node.Label);
        Assert.Equal("fpsCurrent", node.Key);
        Assert.Equal(2, node.Digits!.Value);
    }

    [Fact]
    public void TemplateCatalogOffersEveryStarterAndReturnsIndependentCopies()
    {
        Assert.Equal(TileDesignCatalog.All.Count(design => !TileDesignCatalog.IsRetired(design.Id)) + 1 +
                     TileStarterTemplates.All.Count(template => !template.IsBlank),
            TileTemplateCatalog.All.Count);
        Assert.Contains(TileTemplateCatalog.All, template => template.IsBlank);
        foreach (var starter in TileStarterTemplates.All.Where(template => !template.IsBlank))
        {
            Assert.Contains(TileTemplateCatalog.All, template => template.Id == starter.Id);
        }

        var first = TileTemplateCatalog.CreateDocument("cfx-benchmark", "First")!;
        var second = TileTemplateCatalog.CreateDocument("cfx-benchmark", "Second")!;
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("First", first.Name);

        first.Root.Children.Clear();
        Assert.NotEmpty(second.Root.Children);
        Assert.NotEmpty(TileDesignCatalog.Find("cfx-benchmark")!.Root.Children);
    }

    [Fact]
    public void SourceCatalogCoversRealHardwareAndFrameKeys()
    {
        Assert.Contains(TileSourceCatalog.Sources, source => source.Key == "fps");
        Assert.Contains(TileSourceCatalog.Sources, source => source.Key == "cpuLoad");
        Assert.Contains(TileSourceCatalog.Sources, source => source.Key == "gpuTemp");
        Assert.Contains(TileSourceCatalog.Sources, source => source.Key == "role:cpu-loads/0");
        Assert.Contains(TileSourceCatalog.Sources, source => source.Kind == TileSourceKind.Text);

        var snapshot = Snapshot();
        var fps = Fps();
        foreach (var key in new[] { "cpuLoad", "gpuLoad", "gpuTemp", "ramLoad",
                     "role:cpu-loads/0" })
        {
            var reading = TileSensorMap.Resolve(key, snapshot, fps, null, TileRenderContext.Empty);
            Assert.True(reading.Available, key + " did not resolve to a real reading");
        }

        Assert.All(TileSourceCatalog.Groups, group => Assert.NotEmpty(group.Keys));
    }

    [Fact]
    public void BoundsMapMatchesTheRenderedTilesAndSkipsHiddenOnes()
    {
        var document = TileDesignCatalog.Find("cfx-benchmark")!.Clone();
        var bounds = new Dictionary<string, TileRect>(StringComparer.Ordinal);
        TileSceneBuilder.Build(document, Snapshot(), Fps(), null, null, null, bounds);

        for (var index = 0; index < document.Root.Children.Count; index++)
        {
            var child = document.Root.Children[index];
            Assert.True(bounds.ContainsKey(index.ToString()));
            Assert.Equal(child.FrameWidth!.Value, bounds[index.ToString()].Width, 2);
            Assert.Equal(child.FrameHeight!.Value, bounds[index.ToString()].Height, 2);
        }

        var overrides = new TileOverrideSettings();
        overrides.Ensure("1").Visible = false;
        var hidden = new Dictionary<string, TileRect>(StringComparer.Ordinal);
        TileSceneBuilder.Build(document, Snapshot(), Fps(), null, overrides, null, hidden);
        Assert.False(hidden.ContainsKey("1"));
    }

    [Fact]
    public void WrapperPanelsResolveToTheContainerTheyWrap()
    {
        var wrapper = new TileNode
        {
            NodeId = "wrapper",
            Type = "panel",
            FrameWidth = 300,
            Children =
            {
                new TileNode
                {
                    NodeId = "canvas",
                    Type = "panel",
                    Layout = "canvas",
                    FrameWidth = 300,
                    Children =
                    {
                        new TileNode { NodeId = "a", Type = "text", Text = "A" },
                        new TileNode { NodeId = "b", Type = "text", Text = "B" },
                    },
                },
            },
        };

        var scope = TileDocumentEditor.ResolveEditableScope(wrapper);
        Assert.True(scope.NodeId == "canvas", "resolved to " + (scope.NodeId ?? "<null>"));
        Assert.True(TileDocumentEditor.HasEditableChildren(wrapper));
        Assert.True(TileDocumentEditor.HasEditableChildren(scope));

        var leaf = scope.Children[0];
        Assert.False(TileDocumentEditor.HasEditableChildren(leaf));
        Assert.Same(leaf, TileDocumentEditor.ResolveEditableScope(leaf));

        var empty = new TileNode { NodeId = "empty", Type = "panel", Layout = "canvas" };
        Assert.False(TileDocumentEditor.HasEditableChildren(empty));

        // The catalog designs really use this wrapper shape, which is why the resolver is needed.
        var wrapped = TileDesignCatalog.All
            .SelectMany(design => design.Root.Descendants())
            .Count(node => node.NodeKind == TileNodeKind.Panel &&
                           node.FlowLayout != TileFlowLayout.Canvas &&
                           node.Children.Count == 1 &&
                           node.Children[0].NodeKind == TileNodeKind.Panel);
        Assert.True(wrapped > 0);
    }

    [Fact]
    public void DesignsWithoutAGridSettingShowThePlacementGrid()
    {
        var json = "{\"version\":1,\"name\":\"legacy\",\"root\":{\"type\":\"panel\",\"frameWidth\":100," +
                   "\"frameHeight\":50}}";
        var document = TileDesignDocument.Parse(json, out var error);
        Assert.Null(error);
        Assert.NotNull(document);
        Assert.True(document!.ShowGrid);
        Assert.Equal(8, document.GridSize);
    }

    [Fact]
    public void FullWidthStretchScalesOnlyHorizontalGeometry()
    {
        var source = TileDesignCatalog.Find("cfx-benchmark")!.Clone();
        source.FullWidth = true;
        var width = source.CanvasWidth;
        var height = source.CanvasHeight;
        var before = TileSceneBuilder.Build(source, Snapshot(), Fps(), null, null,
            TileRenderContext.Empty);

        var stretched = source.StretchToWidth(width * 2);
        Assert.False(stretched.FullWidth);
        Assert.Equal(width * 2, stretched.CanvasWidth, 3);
        Assert.Equal(height, stretched.CanvasHeight, 3);

        for (var index = 0; index < source.Root.Children.Count; index++)
        {
            var original = source.Root.Children[index];
            var scaled = stretched.Root.Children[index];
            Assert.Equal((original.X ?? 0) * 2, scaled.X ?? 0, 3);
            Assert.Equal(original.FrameWidth!.Value * 2, scaled.FrameWidth!.Value, 3);
            Assert.Equal(original.Y ?? 0, scaled.Y ?? 0, 3);
            Assert.Equal(original.FrameHeight!.Value, scaled.FrameHeight!.Value, 3);
        }

        var after = TileSceneBuilder.Build(stretched, Snapshot(), Fps(), null, null,
            TileRenderContext.Empty);
        Assert.Equal(before.Width * 2, after.Width, 3);
        Assert.Equal(before.Height, after.Height, 3);
        Assert.Equal(before.Elements.OfType<TileTextScene>().Select(text => text.FontSize),
            after.Elements.OfType<TileTextScene>().Select(text => text.FontSize));

        // The source document is untouched: the builder preview still shows the authored width.
        Assert.True(source.FullWidth);
        Assert.Equal(width, source.CanvasWidth, 3);
    }

    [Fact]
    public void StretchIgnoresAnInvalidTargetWidth()
    {
        var source = TileDesignCatalog.Find("cfx-compact")!.Clone();
        var width = source.CanvasWidth;
        var copy = source.StretchToWidth(0);
        Assert.Equal(width, copy.CanvasWidth, 3);
        Assert.False(copy.FullWidth);
        Assert.NotSame(source, copy);
    }

    [Fact]
    public void SetGeometryWritesPositionAndSizeAndClampsMinimum()
    {
        var document = TileDocumentEditor.CreateNew("geometry");
        var tile = TileDocumentEditor.AddTile(document, document.Root, TileNodeKind.Text, 0, 0);
        Assert.NotNull(tile);

        TileDocumentEditor.SetGeometry(tile!, 12, 7, 88, 24);
        Assert.Equal(12, tile!.X);
        Assert.Equal(7, tile.Y);
        Assert.Equal(88, tile.Width);
        Assert.Equal(88, tile.FrameWidth);
        Assert.Equal(24, tile.Height);
        Assert.Equal(24, tile.FrameHeight);
        Assert.Empty(document.Validate());

        TileDocumentEditor.SetGeometry(tile, null, null, 1, 1);
        Assert.Equal(TileDocumentEditor.MinTileSize, tile.Width);
        Assert.Equal(TileDocumentEditor.MinTileSize, tile.Height);
    }

    [Fact]
    public void RemovingTheLastTileLeavesAValidEmptyDocument()
    {
        var document = TileDocumentEditor.CreateNew("remove-last");
        var tile = TileDocumentEditor.AddTile(document, document.Root, TileNodeKind.Metric, 0, 0);
        Assert.NotNull(tile);
        Assert.Equal(1, TileDocumentEditor.CountWidgets(document.Root));

        Assert.True(TileDocumentEditor.Remove(document, tile!));
        Assert.Empty(document.Root.Children);
        Assert.Equal(0, TileDocumentEditor.CountWidgets(document.Root));
        Assert.Empty(document.Validate());
    }
}
