using HardwareOSD.Core.Config;
using HardwareOSD.Core.Fps;
using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.Metrics;
using HardwareOSD.Core.Overlay;
using HardwareOSD.Core.TileOsd;

namespace HardwareOSD.Tests;

public sealed class TileOverlay100Tests
{
    private static readonly string[] RetiredDesignIds =
    {
        "cfx-blackwell-thermals-rtx-5060-and-5060-ti-8-gb",
        "cfx-blackwell-thermals-rtx-5070",
        "cfx-blackwell-thermals-rtx-5080-and-5070-ti",
        "cfx-blackwell-thermals-rtx-5090",
    };

    [Fact]
    public void RetiredProfilesStayHiddenUnlessActiveOrEdited()
    {
        var retired = new OsdProfile
        {
            Id = "retired",
            Name = "Retired",
            Layout = OsdProfileLayout.Tile,
            Library = OsdDesignLibrary.CapFrameX,
            DesignId = RetiredDesignIds[0],
        };
        Assert.True(OsdLibraryFilter.IsHiddenFromLibrary(retired, null));
        Assert.False(OsdLibraryFilter.IsHiddenFromLibrary(retired, retired.Id));

        retired.TileDesignJson = "{}";
        Assert.False(OsdLibraryFilter.IsHiddenFromLibrary(retired, null));

        var userProfile = new OsdProfile
        {
            Id = "user",
            Name = "User",
            Layout = OsdProfileLayout.Tile,
            Library = OsdDesignLibrary.HardwareOsd,
            DesignId = RetiredDesignIds[0],
        };
        Assert.False(OsdLibraryFilter.IsHiddenFromLibrary(userProfile, null));

        var current = new OsdProfile
        {
            Id = "current",
            Name = "Current",
            Layout = OsdProfileLayout.Tile,
            Library = OsdDesignLibrary.CapFrameX,
            DesignId = "cfx-benchmark",
        };
        Assert.False(OsdLibraryFilter.IsHiddenFromLibrary(current, null));
    }

    [Fact]
    public void RetiredBlackwellProfilesAreHiddenButRemainResolvable()
    {
        foreach (var id in RetiredDesignIds)
        {
            Assert.True(TileDesignCatalog.IsRetired(id));
            Assert.NotNull(TileDesignCatalog.Find(id));
        }

        Assert.DoesNotContain(TileTemplateCatalog.All,
            template => TileDesignCatalog.IsRetired(template.Id));
        Assert.DoesNotContain(OsdSettings.CreateCapFrameXProfiles(),
            profile => TileDesignCatalog.IsRetired(profile.DesignId));
    }

    [Theory]
    [InlineData("cfx-duoline-geforce-rtx-plus-intel-core")]
    [InlineData("cfx-duoline-geforce-rtx-plus-ryzen")]
    [InlineData("cfx-duoline-arc-plus-intel-core")]
    [InlineData("cfx-duoline-radeon-plus-intel-core")]
    [InlineData("cfx-duoline-radeon-plus-ryzen")]
    [InlineData("cfx-iceline-vertical")]
    public void DualLineAndIceLanLabelsAreLeftAlignedAndValuesRightAligned(string designId)
    {
        var design = TileDesignCatalog.Find(designId);
        Assert.NotNull(design);
        var scene = TileSceneBuilder.Build(design!.Clone(), BuilderDemoSnapshot.Snapshot,
            BuilderDemoSnapshot.Fps, BuilderDemoSnapshot.History);
        var metric = scene.Elements.OfType<TileMetricScene>()
            .First(candidate => !string.IsNullOrWhiteSpace(candidate.Label) &&
                                candidate.LabelPosition == "left");
        Assert.Equal(TileTextAlign.Left, metric.LabelAlign);
        Assert.Equal(TileTextAlign.Right, metric.Align);
    }

    [Theory]
    [InlineData("metric.ram.used", "GB")]
    [InlineData("metric.vram.used", "GB")]
    [InlineData("metric.ram.used", "MB")]
    [InlineData("metric.vram.used", "MB")]
    public void RamAndVramUsedMetricsRenderUsedOverTheConfiguredTotalUnit(string preset, string unit)
    {
        var document = TileDocumentEditor.CreateNew("memory");
        var tile = TileElementLibrary.Create(document, preset);
        var node = tile.Children[0];
        node.Unit = unit;
        node.Digits = unit == "GB" ? 1 : 0;
        Assert.NotNull(TileDocumentEditor.AddNode(document, document.Root, tile));
        var scene = TileSceneBuilder.Build(document, BuilderDemoSnapshot.Snapshot,
            BuilderDemoSnapshot.Fps, BuilderDemoSnapshot.History);
        var metric = Assert.Single(scene.Elements.OfType<TileMetricScene>());
        Assert.Equal(string.Empty, metric.Unit);
        Assert.Equal(unit, metric.SecondaryUnit);
        Assert.False(string.IsNullOrWhiteSpace(metric.SecondaryValue));
        Assert.True(double.Parse(metric.SecondaryValue, System.Globalization.CultureInfo.InvariantCulture) >
                    double.Parse(metric.Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void MiniGraphUsesMetricHistoryAndWarningThresholds()
    {
        var document = TileDocumentEditor.CreateNew("mini-graph");
        var tile = TileElementLibrary.Create(document, "metric.cpu.temp");
        var metric = tile.Children[0];
        metric.MiniGraph = true;
        metric.Series = SensorIds.CpuTemperature;
        metric.WarningThreshold = 60;
        metric.CriticalThreshold = 90;
        Assert.NotNull(TileDocumentEditor.AddNode(document, document.Root, tile));

        var scene = TileSceneBuilder.Build(document, BuilderDemoSnapshot.Snapshot,
            BuilderDemoSnapshot.Fps, BuilderDemoSnapshot.History, null,
            new TileRenderContext(WarningColor: "#FF0000FF", CriticalColor: "#00FF00FF"));
        var rendered = Assert.Single(scene.Elements.OfType<TileMetricScene>());
        Assert.True(rendered.MiniGraph);
        Assert.True(rendered.GraphValues.Count > 1);
        Assert.Equal(MetricSeverity.Warning, rendered.Severity);
        Assert.Equal(new TileColor(255, 0, 0, 255), rendered.ValueColor);
    }

    [Fact]
    public void FanRpmAndPercentUseRealChannelsAndNeverInventAMaximum()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var rpmId = FanSensorIds.Id(FanComponent.Gpu, 0, FanMeasure.Rpm);
        var percentId = FanSensorIds.Id(FanComponent.Gpu, 0, FanMeasure.Percent);
        var values = new Dictionary<string, SensorValue>(StringComparer.Ordinal)
        {
            [rpmId] = new(rpmId, 1250, "RPM", SensorKind.Fan, SensorOrigin.HardwareSensor,
                "test", now),
            [percentId] = new(percentId, 52, "%", SensorKind.Fan, SensorOrigin.HardwareSensor,
                "test", now),
        };
        var snapshot = SensorSnapshot.Create(1, now, values);

        var rpm = TileSensorMap.Resolve("gpuFan", snapshot, null, null, TileRenderContext.Empty);
        Assert.True(rpm.Available);
        Assert.Equal(1250, rpm.Numeric, 3);
        Assert.Equal("RPM", rpm.Unit);

        var percent = TileSensorMap.Resolve("gpuFanPercent", snapshot, null, null,
            TileRenderContext.Empty);
        Assert.True(percent.Available);
        Assert.Equal(52, percent.Numeric, 3);
        Assert.Equal("%", percent.Unit);

        values.Remove(percentId);
        var rpmOnly = SensorSnapshot.Create(2, now, values);
        var unavailable = TileSensorMap.Resolve("gpuFanPercent", rpmOnly, null, null,
            TileRenderContext.Empty);
        Assert.False(unavailable.Available);
    }

    [Fact]
    public void MetricSetInsertsPowerLimitIntoAnAdjacentGpuRowInsteadOfTheBottom()
    {
        var definition = MetricCatalog.Find("gpu.power.limit");
        Assert.NotNull(definition);
        var tileKey = MetricTileKeys.For(definition!);

        var document = TileDocumentEditor.CreateNew("group");
        var row = new TileNode
        {
            NodeId = TileDocumentEditor.NewNodeId(),
            Type = "panel",
            Kind = "panel",
            Layout = "horizontal",
            X = 0,
            Y = 0,
            FrameWidth = 400,
            FrameHeight = 40,
            Width = 400,
            Height = 40,
            Padding = 4,
            Gap = 4,
            Background = true,
            Color = document.Theme.Panel,
            Children = new List<TileNode>
            {
                new()
                {
                    NodeId = TileDocumentEditor.NewNodeId(),
                    Type = "text",
                    Kind = "text",
                    Text = "GPU",
                    Size = 12,
                    FrameWidth = 40,
                    FrameHeight = 32,
                    Width = 40,
                    Height = 32,
                },
                new()
                {
                    NodeId = TileDocumentEditor.NewNodeId(),
                    Type = "metric",
                    Kind = "metric",
                    Key = "gpuTemp",
                    Label = "TEMP",
                    LabelPosition = "none",
                    Align = "right",
                    Unit = "°C",
                    FrameWidth = 120,
                    FrameHeight = 32,
                    Width = 120,
                    Height = 32,
                    Visible = true,
                },
            },
        };
        document.Root.Children.Add(row);
        var profile = new OsdProfile
        {
            Id = "group-profile",
            Name = "Group profile",
            Layout = OsdProfileLayout.Tile,
            Library = OsdDesignLibrary.HardwareOsd,
        };
        profile.SetTileDesign(document);
        var originalCount = row.Children.Count;
        var originalWidths = row.Children.Select(child => child.FrameWidth).ToArray();

        OsdMetricBridge.ApplyTileMetricSet(profile, new[]
        {
            new MetricPreference { Id = definition.Id, Visible = true },
        });

        var updated = profile.TileDesign!;
        var gpuRow = Assert.Single(updated.Root.Children, candidate =>
            candidate.NodeId == row.NodeId);
        Assert.Equal(originalCount, gpuRow.Children.Count);
        Assert.Equal(originalWidths, gpuRow.Children.Select(child => child.FrameWidth));
        var added = Assert.Single(updated.Root.Children, candidate =>
            candidate.NodeId?.StartsWith("metric-row-gpu-", StringComparison.Ordinal) == true);
        Assert.Contains(added.Children, child =>
            string.Equals(child.Key, tileKey, StringComparison.Ordinal));
        Assert.Contains(added.Children, child =>
            child.NodeKind == TileNodeKind.Text && child.Text == "GPU");
        var originalIndex = updated.Root.Children.IndexOf(gpuRow);
        Assert.Equal(originalIndex + 1, updated.Root.Children.IndexOf(added));
        Assert.DoesNotContain(updated.Root.Children, child =>
            child.NodeId?.StartsWith("metric-panel-", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void ShowingAllMetricsAddsGroupRowsAndDefaultSetRemovesThem()
    {
        var document = TileDocumentEditor.CreateNew("group-bulk");
        document.Root.FrameHeight = TileDocumentEditor.MinCanvasHeight;
        document.Root.Height = TileDocumentEditor.MinCanvasHeight;
        var originalHeight = document.CanvasHeight;
        var row = new TileNode
        {
            NodeId = TileDocumentEditor.NewNodeId(),
            Type = "panel",
            Kind = "panel",
            Layout = "horizontal",
            X = 0,
            Y = 0,
            FrameWidth = 320,
            FrameHeight = 24,
            Width = 320,
            Height = 24,
            Children = new List<TileNode>
            {
                new()
                {
                    NodeId = TileDocumentEditor.NewNodeId(),
                    Type = "text",
                    Kind = "text",
                    Text = "GPU",
                    Size = 12,
                    FrameWidth = 34,
                    FrameHeight = 24,
                    Width = 34,
                    Height = 24,
                },
                new()
                {
                    NodeId = TileDocumentEditor.NewNodeId(),
                    Type = "metric",
                    Kind = "metric",
                    Key = "gpuTemp",
                    LabelPosition = "none",
                    Unit = "°C",
                    FrameWidth = 100,
                    FrameHeight = 24,
                    Width = 100,
                    Height = 24,
                    Visible = true,
                },
            },
        };
        document.Root.Children.Add(row);
        var profile = new OsdProfile
        {
            Id = "group-bulk-profile",
            Name = "Group bulk profile",
            Layout = OsdProfileLayout.Tile,
            Library = OsdDesignLibrary.HardwareOsd,
        };
        profile.SetTileDesign(document);
        var originalWidths = row.Children.Select(child => child.FrameWidth).ToArray();
        var visible = new[] { "gpu.power.limit", "gpu.clock", "gpu.usage", "gpu.fan.rpm" }
            .Select(id => new MetricPreference { Id = id, Visible = true })
            .ToList();

        OsdMetricBridge.ApplyTileMetricSet(profile, visible);

        var all = profile.TileDesign!;
        Assert.True(all.CanvasHeight > originalHeight,
            $"initial={originalHeight} after={all.CanvasHeight} rows={all.Root.Children.Count}");
        Assert.Equal(originalWidths, all.Root.Children
            .Single(child => child.NodeId == row.NodeId).Children
            .Select(child => child.FrameWidth));
        var addedRows = all.Root.Children.Where(child =>
            child.NodeId?.StartsWith("metric-row-gpu-", StringComparison.Ordinal) == true).ToList();
        Assert.NotEmpty(addedRows);
        Assert.Equal(visible.Count, addedRows.Sum(addedRow => addedRow.Children.Count(child =>
            child.NodeKind == TileNodeKind.Metric)));

        OsdMetricBridge.ApplyTileMetricSet(profile, visible
            .Select(preference => new MetricPreference
            {
                Id = preference.Id,
                Visible = false,
            })
            .ToList());

        var defaults = profile.TileDesign!;
        Assert.Equal(originalHeight, defaults.CanvasHeight);
        Assert.DoesNotContain(defaults.Root.Children, child =>
            child.NodeId?.StartsWith("metric-row-gpu-", StringComparison.Ordinal) == true);
        Assert.Contains(defaults.Root.Children, child => child.NodeId == row.NodeId);
    }

    [Fact]
    public void HidingMetricsRemovesAnEmptyBridgePanelAndShrinksTheCanvas()
    {
        var definition = MetricCatalog.Find("gpu.power.limit")!;
        var document = TileDocumentEditor.CreateNew("shrink");
        document.Root.FrameHeight = 300;
        document.Root.Height = 300;
        var row = new TileNode
        {
            NodeId = TileDocumentEditor.NewNodeId(),
            Type = "panel",
            Kind = "panel",
            Layout = "vertical",
            X = 0,
            Y = 0,
            FrameWidth = 300,
            FrameHeight = 40,
            Width = 300,
            Height = 40,
            Children = new List<TileNode>
            {
                new()
                {
                    NodeId = TileDocumentEditor.NewNodeId(),
                    Type = "metric",
                    Kind = "metric",
                    Key = "gpuTemp",
                    LabelPosition = "none",
                    FrameWidth = 300,
                    FrameHeight = 40,
                    Width = 300,
                    Height = 40,
                    Visible = true,
                },
            },
        };
        var bridgePanel = new TileNode
        {
            NodeId = "metric-panel-gpu",
            Type = "panel",
            Kind = "panel",
            Name = "Metrics",
            Layout = "vertical",
            X = 0,
            Y = 220,
            FrameWidth = 300,
            FrameHeight = 30,
            Width = 300,
            Height = 30,
            Gap = 2,
            Children = new List<TileNode>
            {
                new()
                {
                    NodeId = TileDocumentEditor.NewNodeId(),
                    Type = "metric",
                    Kind = "metric",
                    Key = MetricTileKeys.For(definition),
                    LabelPosition = "none",
                    FrameWidth = 300,
                    FrameHeight = 28,
                    Width = 300,
                    Height = 28,
                    Visible = true,
                },
            },
        };
        document.Root.Children.Add(row);
        document.Root.Children.Add(bridgePanel);
        var profile = new OsdProfile
        {
            Id = "shrink-profile",
            Name = "Shrink profile",
            Layout = OsdProfileLayout.Tile,
            Library = OsdDesignLibrary.HardwareOsd,
        };
        profile.SetTileDesign(document);

        OsdMetricBridge.ApplyTileMetricSet(profile, new[]
        {
            new MetricPreference { Id = definition.Id, Visible = false },
        });

        var updated = profile.TileDesign!;
        Assert.DoesNotContain(updated.Root.Children,
            child => child.NodeId == "metric-panel-gpu");
        Assert.True(updated.CanvasHeight < 300);
    }
}
