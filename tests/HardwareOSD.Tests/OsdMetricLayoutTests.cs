using HardwareOSD.Core.Config;
using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.Metrics;
using HardwareOSD.Core.Overlay;
using HardwareOSD.Core.TileOsd;

namespace HardwareOSD.Tests;

public class OsdMetricLayoutTests
{
    [Fact]
    public void Bridge_AddsGpuFanAndOtherMetricsToTheMatchingGroup()
    {
        var settings = AppSettings.CreateDefault();
        var profile = settings.Osd.Profiles.Single(p => p.Id == "classic");
        profile.Layout = OsdProfileLayout.Rows;
        profile.TileDesignJson = null;
        var fan = MetricCatalog.Find("gpu.fan.rpm")!;
        var gpuUsage = MetricCatalog.Find("gpu.usage")!;

        var element = OsdMetricBridge.EnsureElement(profile, fan);
        Assert.Equal("gpu.fan.rpm", element.SensorId);
        Assert.Contains(element, OsdProfile.Flatten(profile.Elements));
        var gpuGroup = profile.Elements.Single(e => e.Label == "GPU");
        Assert.Contains(element, gpuGroup.Children);
        Assert.Contains(gpuGroup.Children, child => child.SensorId == gpuUsage.Id);
        Assert.False(profile.FollowMetricPreferences);

        OsdMetricBridge.SetMetricVisible(profile, fan, false, materialiseWhenMissing: false);
        Assert.False(element.Visible);
        OsdMetricBridge.SetMetricVisible(profile, fan, true, materialiseWhenMissing: true);
        Assert.True(element.Visible);
    }

    [Fact]
    public void Bridge_FindsMetricByMetricIdAndMaterialisesEveryVisibleMetric()
    {
        var settings = AppSettings.CreateDefault();
        var profile = settings.Osd.Profiles.Single(p => p.Id == "classic");
        profile.Layout = OsdProfileLayout.Rows;
        profile.TileDesignJson = null;
        settings.SetMetricVisible("gpu.fan.rpm", true);
        profile.Elements.Clear();
        profile.FollowMetricPreferences = true;

        OsdMetricBridge.MaterializeVisibleMetrics(profile, settings.Metrics);
        foreach (var preference in settings.Metrics.Where(p => p.Visible))
        {
            Assert.NotNull(OsdMetricBridge.FindElement(profile, preference.Id));
        }

        Assert.Contains("gpu.fan.rpm",
            OsdProfile.Flatten(profile.Elements).Select(e => e.SensorId).ToHashSet());
    }

    [Fact]
    public void Widths_ReserveWorstCaseValuesForEveryMetric()
    {
        foreach (var definition in MetricCatalog.All)
        {
            var samples = MetricWidths.For(definition);
            Assert.False(string.IsNullOrWhiteSpace(samples.Value));
            Assert.True(samples.Value.Length >= definition.SampleValue.Length - 1,
                definition.Id + " value sample unexpectedly short");
            Assert.True(samples.Unit.Length >= definition.SampleUnit.Length,
                definition.Id + " unit sample unexpectedly short");
        }

        Assert.Equal("1888.8", MetricWidths.For(MetricCatalog.Find("gpu.power")!).Value);
        Assert.Equal("188888", MetricWidths.For(MetricCatalog.Find("gpu.clock")!).Value);
        Assert.Equal("18888/18888", MetricWidths.For(MetricCatalog.Find("gpu.fan.rpm")!).Value);
        Assert.Equal("188888 / 188888", MetricWidths.For(MetricCatalog.Find("vram.used")!).Value);
        Assert.Equal("FPS\u00B7p", MetricWidths.For(MetricCatalog.Find("frame.fps")!).Unit);
    }

    [Fact]
    public void ClassicTileMetrics_IncludeGpuFanAndCoreUsage()
    {
        var settings = AppSettings.CreateDefault();
        settings.Osd.ActiveProfileId = "classic";
        var profile = settings.Osd.ActiveProfile!;
        Assert.True(profile.IsTile);
        var fan = MetricCatalog.Find("gpu.fan.rpm")!;
        var busiest = MetricCatalog.Find("cpu.usage.max")!;
        OsdMetricBridge.SetTileMetricVisible(profile, fan, true, materialiseWhenMissing: true);
        OsdMetricBridge.SetTileMetricVisible(profile, busiest, true, materialiseWhenMissing: true);

        var keys = profile.TileDesign!.Root.Descendants().Select(node => node.Key).ToHashSet();
        Assert.Contains(MetricTileKeys.For(fan), keys);
        Assert.Contains(MetricTileKeys.For(busiest), keys);

        var snapshot = SnapshotFactory.Build(
            (SensorIds.CpuUsage, 42, "%", SensorKind.Load),
            (SensorIds.CpuUsageMax, 77, "%", SensorKind.Load),
            ("fan.gpu.0.rpm", 1320, "RPM", SensorKind.Fan));
        var scene = TileSceneBuilder.Build(profile.TileDesign!, snapshot, null,
            new SensorHistory(8), profile.Tile);
        Assert.Contains(scene.Elements.OfType<TileMetricScene>(),
            metric => metric.Value == "1320" && metric.Unit == "RPM");
        Assert.Contains(scene.Elements.OfType<TileMetricScene>(),
            metric => metric.Value == "77" && metric.Unit == "%");
    }

    [Fact]
    public void TileMetricBridge_TogglesExistingNodesAndMaterialisesOnce()
    {
        var settings = AppSettings.CreateDefault();
        var profile = settings.Osd.ActiveProfile!;
        var temperature = MetricCatalog.Find("cpu.temp")!;
        var before = OsdMetricBridge.FindTileNodes(profile, temperature);
        Assert.NotEmpty(before);

        OsdMetricBridge.SetTileMetricVisible(profile, temperature, false, materialiseWhenMissing: false);
        Assert.False(OsdMetricBridge.IsTileMetricVisible(profile, temperature));
        Assert.Equal(before.Count, OsdMetricBridge.FindTileNodes(profile, temperature).Count);

        OsdMetricBridge.SetTileMetricVisible(profile, temperature, true, materialiseWhenMissing: true);
        Assert.True(OsdMetricBridge.IsTileMetricVisible(profile, temperature));

        var fan = MetricCatalog.Find("cpu.fan.rpm")!;
        Assert.Empty(OsdMetricBridge.FindTileNodes(profile, fan));
        OsdMetricBridge.SetTileMetricVisible(profile, fan, true, materialiseWhenMissing: true);
        Assert.Single(OsdMetricBridge.FindTileNodes(profile, fan));
        OsdMetricBridge.SetTileMetricVisible(profile, fan, false, materialiseWhenMissing: true);
        Assert.False(OsdMetricBridge.IsTileMetricVisible(profile, fan));
        OsdMetricBridge.SetTileMetricVisible(profile, fan, true, materialiseWhenMissing: true);
        Assert.Single(OsdMetricBridge.FindTileNodes(profile, fan));
    }

    [Fact]
    public void TileProfiles_KeepStableGeometryWhenValuesChange()
    {
        var settings = AppSettings.CreateDefault();
        var low = SnapshotFactory.Build(
            (SensorIds.CpuUsage, 9, "%", SensorKind.Load),
            (SensorIds.CpuPower, 45.5, "W", SensorKind.Power),
            (SensorIds.GpuUsage, 9, "%", SensorKind.Load),
            (SensorIds.GpuPower, 45.5, "W", SensorKind.Power));
        var high = SnapshotFactory.Build(
            (SensorIds.CpuUsage, 100, "%", SensorKind.Load),
            (SensorIds.CpuPower, 105, "W", SensorKind.Power),
            (SensorIds.GpuUsage, 100, "%", SensorKind.Load),
            (SensorIds.GpuPower, 105, "W", SensorKind.Power));
        foreach (var id in RowProfileTileMigrator.ClassicProfileIds)
        {
            var profile = settings.Osd.FindProfile(id)!;
            var a = TileSceneBuilder.Build(profile.TileDesign!, low, null, new SensorHistory(8));
            var b = TileSceneBuilder.Build(profile.TileDesign!, high, null, new SensorHistory(8));
            Assert.Equal(a.Width, b.Width);
            Assert.Equal(a.Height, b.Height);
            Assert.Equal(a.Elements.Count, b.Elements.Count);
            for (var index = 0; index < a.Elements.Count; index++)
            {
                Assert.Equal(a.Elements[index].Bounds, b.Elements[index].Bounds);
            }
        }
    }

    [Fact]
    public void TileScene_HidesNodesMarkedInvisible()
    {
        var document = TileDocumentEditor.CreateNew("visibility");
        document.Root.Children.Add(new TileNode
        {
            NodeId = "hidden",
            Type = "text",
            Kind = "text",
            Text = "HIDDEN",
            Visible = false,
            FrameWidth = 80,
            FrameHeight = 20,
        });
        document.Root.Children.Add(new TileNode
        {
            NodeId = "shown",
            Type = "text",
            Kind = "text",
            Text = "SHOWN",
            Visible = true,
            FrameWidth = 80,
            FrameHeight = 20,
        });
        var scene = TileSceneBuilder.Build(document, SnapshotFactory.Typical(), null,
            new SensorHistory(8));
        var texts = scene.Elements.OfType<TileTextScene>().Select(text => text.Text).ToList();
        Assert.DoesNotContain("HIDDEN", texts);
        Assert.Contains("SHOWN", texts);
    }

    [Fact]
    public void MinimalRings_RootColumnsBuildsOneStableHorizontalRow()
    {
        var settings = AppSettings.CreateDefault();
        var profile = settings.Osd.Profiles.Single(p => p.Id == "minimal-rings").Clone();
        profile.Layout = OsdProfileLayout.Rows;
        profile.TileDesignJson = null;
        profile.RootLayout = OsdGroupLayout.Columns;

        var design = RowProfileTileMigrator.BuildDesign(profile);
        var row = Assert.Single(design.Root.Children);
        Assert.Equal("horizontal", row.Layout);
        var metrics = row.Descendants().Where(node => node.NodeKind == TileNodeKind.Metric).ToList();
        Assert.True(metrics.Count >= 3);
        Assert.All(metrics, metric => Assert.False(string.IsNullOrWhiteSpace(metric.Key)));
    }

    [Fact]
    public void RootColumns_IsClonedAndNormalisedWithoutBreakingOldProfiles()
    {
        var settings = AppSettings.CreateDefault();
        var profile = settings.Osd.Profiles.Single(p => p.Id == "minimal-rings");
        profile.RootLayout = OsdGroupLayout.Columns;
        var clone = profile.Clone();
        Assert.Equal(OsdGroupLayout.Columns, clone.RootLayout);

        profile.RootLayout = (OsdGroupLayout)99;
        profile.Normalize();
        Assert.Equal(OsdGroupLayout.Rows, profile.RootLayout);
    }
}
