using System.Text.Json;
using HardwareOSD.Core.Config;
using HardwareOSD.Core.Fps;
using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.TileOsd;

namespace HardwareOSD.Tests;

public sealed class TileOsdTests
{
    private static SensorSnapshot Snapshot()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var values = new Dictionary<string, SensorValue>(StringComparer.Ordinal)
        {
            [SensorIds.CpuUsage] = Value(SensorIds.CpuUsage, 37, "%", SensorKind.Load, now),
            [SensorIds.CpuTemperature] = Value(SensorIds.CpuTemperature, 61, "°C", SensorKind.Temperature, now),
            [SensorIds.CpuPower] = Value(SensorIds.CpuPower, 54, "W", SensorKind.Power, now),
            [SensorIds.CpuClock] = Value(SensorIds.CpuClock, 4300, "MHz", SensorKind.Clock, now),
            [SensorIds.GpuUsage] = Value(SensorIds.GpuUsage, 88, "%", SensorKind.Load, now),
            [SensorIds.GpuTemperature] = Value(SensorIds.GpuTemperature, 67, "°C", SensorKind.Temperature, now),
            [SensorIds.GpuPower] = Value(SensorIds.GpuPower, 171, "W", SensorKind.Power, now),
            [SensorIds.GpuClock] = Value(SensorIds.GpuClock, 2610, "MHz", SensorKind.Clock, now),
            [SensorIds.GpuMemoryJunction] = Value(SensorIds.GpuMemoryJunction, 78, "°C", SensorKind.Temperature, now),
            [SensorIds.VramUsed] = Value(SensorIds.VramUsed, 8234, "MB", SensorKind.Data, now),
            [SensorIds.VramTotal] = Value(SensorIds.VramTotal, 12282, "MB", SensorKind.Data, now),
            [SensorIds.RamUsed] = Value(SensorIds.RamUsed, 21913, "MB", SensorKind.Data, now),
            [SensorIds.RamTotal] = Value(SensorIds.RamTotal, 65227, "MB", SensorKind.Data, now),
            [SensorIds.RamPercent] = Value(SensorIds.RamPercent, 33.6, "%", SensorKind.Load, now),
        };
        for (var core = 0; core < 16; core++)
        {
            values[SensorIds.CoreUsage(core)] = Value(SensorIds.CoreUsage(core), 20 + core,
                "%", SensorKind.Load, now);
            values[SensorIds.CoreClock(core)] = Value(SensorIds.CoreClock(core), 3900 + core * 10,
                "MHz", SensorKind.Clock, now);
            values[SensorIds.CoreTemperature(core)] = Value(SensorIds.CoreTemperature(core),
                55 + core, "°C", SensorKind.Temperature, now);
        }

        return SensorSnapshot.Create(7, now, values);
    }

    private static SensorValue Value(string id, double value, string unit, SensorKind kind, long now)
        => new(id, value, unit, kind, SensorOrigin.HardwareSensor, "test", now);

    private static FpsSnapshot Fps() => new(1, Environment.TickCount64, FpsStatus.Capturing,
        FpsSource.Presented, false, 42, "game.exe", 144.2, 6.94, 121.5, null);

    private static SensorHistory History()
    {
        var history = new SensorHistory(180);
        for (var i = 0; i < 120; i++)
        {
            history.AppendValue("frame.fps", 130 + i % 20);
            history.AppendValue("frame.time", 6.5 + i % 5 * 0.2);
        }

        return history;
    }

    [Fact]
    public void Catalog_HasAllSavedStarterDesigns()
    {
        Assert.Equal(45, TileDesignCatalog.Entries.Count);
        Assert.Equal(45, TileDesignCatalog.All.Count);
        Assert.All(TileDesignCatalog.All, design =>
        {
            Assert.False(string.IsNullOrWhiteSpace(design.Id));
            Assert.False(string.IsNullOrWhiteSpace(design.Name));
            Assert.True(design.CanvasWidth > 0);
            Assert.True(design.CanvasHeight > 0);
            Assert.Empty(design.Validate());
        });
        Assert.Equal(TileDesignCatalog.Entries.Count,
            TileDesignCatalog.All.Select(design => design.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Catalog_ImagesAreEmbeddedAndDecodable()
    {
        var images = TileDesignCatalog.All
            .SelectMany(design => design.Walk().Select(item => item.Node))
            .Where(node => node.NodeKind == TileNodeKind.Image)
            .ToList();
        Assert.NotEmpty(images);
        Assert.All(images, image =>
        {
            Assert.StartsWith("data:image/", image.DataUri, StringComparison.Ordinal);
            var comma = image.DataUri!.IndexOf(',');
            Assert.True(comma > 0);
            var bytes = Convert.FromBase64String(image.DataUri[(comma + 1)..]);
            Assert.True(bytes.Length > 32);
        });
    }

    [Fact]
    public void HardwareBranding_UsesRealVendorsAndNeverClaimsMismatchedHardware()
    {
        var context = new TileRenderContext(
            "NVIDIA GeForce RTX 4080 Laptop GPU",
            "12th Gen Intel Core i9-12900HX",
            "32.0.15.9579",
            "DX12",
            0,
            1,
            "NVIDIA",
            "GenuineIntel");
        var prohibited = new[]
        {
            "AMD", "RYZEN", "RADEON", "X3D", "BLACKWELL", "RTX 50",
            "CORE ULTRA", "PANTHER", "NOVA", "INTEL ARC",
        };

        foreach (var design in TileDesignCatalog.All)
        {
            var scene = TileSceneBuilder.Build(design, Snapshot(), Fps(), History(),
                new TileOverrideSettings(), context);
            foreach (var text in scene.Elements.OfType<TileTextScene>())
            {
                foreach (var claim in prohibited)
                {
                    Assert.DoesNotContain(claim, text.Text, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        var apex = TileSceneBuilder.Build(
            TileDesignCatalog.Find("cfx-apex-3d-rtx-50-series-plus-ryzen-3d")!,
            Snapshot(), Fps(), History(), new TileOverrideSettings(), context);
        var apexText = apex.Elements.OfType<TileTextScene>().Select(item => item.Text).ToList();
        Assert.Contains(apexText, text => text.Contains("INTEL", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(apexText, text => text.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
        var metrics = apex.Elements.OfType<TileMetricScene>().ToList();
        Assert.Contains(metrics, metric =>
            metric.Value.Contains("12900HX", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(metrics, metric =>
            metric.Value.Contains("4080", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VendorArtwork_IsHiddenWhenHardwareDoesNotMatch()
    {
        static TileRenderContext Context(string cpu, string gpu, string cpuVendor, string gpuVendor) =>
            new(gpu, cpu, "driver", "DX12", 0, 1, gpuVendor, cpuVendor);

        var intelNvidia = Context("12th Gen Intel Core i9-12900HX",
            "NVIDIA GeForce RTX 4080 Laptop GPU", "GenuineIntel", "NVIDIA");
        var amdNvidia = Context("AMD Ryzen 7 9800X3D",
            "NVIDIA GeForce RTX 4080 Laptop GPU", "AMD", "NVIDIA");
        var intelUltraNvidia = Context("Intel Core Ultra 9 285K",
            "NVIDIA GeForce RTX 4080 Laptop GPU", "GenuineIntel", "NVIDIA");

        static int ImageCount(string designId, TileRenderContext context) =>
            TileSceneBuilder.Build(TileDesignCatalog.Find(designId)!, Snapshot(), Fps(), History(),
                new TileOverrideSettings(), context).Elements.OfType<TileImageScene>().Count();

        Assert.Equal(2, ImageCount("cfx-fusion-ryzen-rtx-50-series-plus-ryzen", intelNvidia));
        Assert.Equal(3, ImageCount("cfx-fusion-ryzen-rtx-50-series-plus-ryzen", amdNvidia));
        Assert.Equal(2, ImageCount("cfx-fusion-arl-rtx-50-series-plus-intel-core-ultra", intelNvidia));
        Assert.Equal(3, ImageCount("cfx-fusion-arl-rtx-50-series-plus-intel-core-ultra", intelUltraNvidia));
        Assert.Equal(1, ImageCount("cfx-redline-ryzen-plus-radeon", intelNvidia));
        Assert.Equal(2, ImageCount("cfx-redline-ryzen-plus-radeon", amdNvidia));
    }

    [Fact]
    public void SceneBuilder_RendersEveryDesignWithoutFakeGeometry()
    {
        var snapshot = Snapshot();
        var fps = Fps();
        var history = History();
        var context = new TileRenderContext("GPU", "CPU", "driver", "DX12", 0, 1);
        foreach (var design in TileDesignCatalog.All)
        {
            var scene = TileSceneBuilder.Build(design, snapshot, fps, history,
                new TileOverrideSettings(), context);
            Assert.False(scene.IsEmpty, design.Id);
            Assert.Equal(design.CanvasWidth, scene.Width, 1);
            Assert.Equal(design.CanvasHeight, scene.Height, 1);
            Assert.All(scene.Elements, element =>
            {
                Assert.True(double.IsFinite(element.Bounds.X));
                Assert.True(double.IsFinite(element.Bounds.Y));
                Assert.True(double.IsFinite(element.Bounds.Width));
                Assert.True(double.IsFinite(element.Bounds.Height));
                Assert.True(element.Bounds.Width >= 0);
                Assert.True(element.Bounds.Height >= 0);
            });
        }
    }

    [Fact]
    public void SceneBuilder_UsesRealValuesAndNeverInventsMissingOnes()
    {
        var snapshot = Snapshot();
        var scene = TileSceneBuilder.Build(TileDesignCatalog.Find("cfx-benchmark")!, snapshot, Fps(),
            History(), new TileOverrideSettings(), new TileRenderContext("GPU", "CPU"));
        var metrics = scene.Elements.OfType<TileMetricScene>().ToList();
        var fps = metrics.Single(metric => metric.Label == "FPS");
        Assert.Equal("144", fps.Value);
        Assert.True(fps.Available);

        var missing = TileSceneBuilder.Build(TileDesignCatalog.Find("cfx-blackwell-thermals-rtx-5090")!,
            SensorSnapshot.Empty, null, null, new TileOverrideSettings(),
            new TileRenderContext("GPU", "CPU"));
        var unavailable = missing.Elements.OfType<TileMetricScene>()
            .Where(metric => !metric.Available)
            .ToList();
        Assert.NotEmpty(unavailable);
        Assert.All(unavailable, metric =>
        {
            Assert.Equal("N/A", metric.Value);
            Assert.Equal(string.Empty, metric.Unit);
        });
    }

    [Fact]
    public void SceneBuilder_ConvertsSensorBaseUnitsToDisplayUnits()
    {
        var design = TileDesignCatalog.Find("cfx-turquoise-reference")!;
        var scene = TileSceneBuilder.Build(design, Snapshot(), Fps(), History(),
            new TileOverrideSettings(), new TileRenderContext("GPU", "CPU"));
        var ram = scene.Elements.OfType<TileMetricScene>()
            .Single(metric => metric.Label == "SYSTEM MEMORY");
        Assert.Equal("21.4", ram.Value);
        Assert.Equal("GB", ram.Unit);
        Assert.Equal("64", ram.SecondaryValue);
        Assert.Equal("GB", ram.SecondaryUnit);
    }

    [Fact]
    public void SceneBuilder_AppliesUserOverridesWithoutChangingTheCatalog()
    {
        var design = TileDesignCatalog.Find("cfx-benchmark")!;
        var original = design.Root.Children[1].Children[0].Key;
        var overrides = new TileOverrideSettings();
        var node = overrides.Ensure("1/0");
        node.Key = "gpuTemp";
        node.Label = "GPU temperature custom";
        node.Unit = "°F";
        node.ValueScale = 1.8;
        node.Digits = 1;
        node.Visible = true;

        var scene = TileSceneBuilder.Build(design, Snapshot(), Fps(), History(), overrides,
            new TileRenderContext("GPU", "CPU"));
        var metric = scene.Elements.OfType<TileMetricScene>()
            .Single(item => item.Label == "GPU temperature custom");
        Assert.Equal("120.6", metric.Value);
        Assert.Equal("°F", metric.Unit);
        Assert.Equal(original, design.Root.Children[1].Children[0].Key);

        overrides.Nodes["1/0"].Visible = false;
        var hidden = TileSceneBuilder.Build(design, Snapshot(), Fps(), History(), overrides,
            new TileRenderContext("GPU", "CPU"));
        Assert.DoesNotContain(hidden.Elements.OfType<TileMetricScene>(),
            item => item.Label == "GPU temperature custom");
    }

    [Fact]
    public void SceneBuilder_UsesHistoryForCharts()
    {
        var scene = TileSceneBuilder.Build(TileDesignCatalog.Find("cfx-frame-pacing")!, Snapshot(),
            Fps(), History(), new TileOverrideSettings(), new TileRenderContext("GPU", "CPU"));
        var charts = scene.Elements.OfType<TileChartScene>().ToList();
        Assert.NotEmpty(charts);
        Assert.Contains(charts, chart => chart.Lines.Any(line => line.Values.Length >= 100));
    }

    [Fact]
    public void RowTemplates_HaveTheExpectedCompositionAndVendorRoles()
    {
        var profiles = CapFrameXRowTemplates.CreateProfiles();
        Assert.Equal(3, profiles.Count);
        Assert.All(profiles, profile =>
        {
            Assert.Equal(OsdDesignLibrary.CapFrameX, profile.Library);
            Assert.Equal(OsdProfileLayout.Rows, profile.Layout);
            Assert.NotEmpty(profile.Elements);
        });

        var basic = profiles.Single(profile => profile.Id == CapFrameXRowTemplates.BasicId);
        var detailed = profiles.Single(profile => profile.Id == CapFrameXRowTemplates.DetailedId);
        var enthusiast = profiles.Single(profile => profile.Id == CapFrameXRowTemplates.EnthusiastId);
        Assert.True(detailed.Elements.Count > basic.Elements.Count);
        Assert.True(enthusiast.Elements.Count > detailed.Elements.Count);
        Assert.Contains(OsdProfile.Flatten(basic.Elements),
            element => element.VendorRole == OsdVendorRole.GpuGroup);
        Assert.Contains(OsdProfile.Flatten(enthusiast.Elements),
            element => element.SensorId == "pc.latency");
    }

    [Fact]
    public void Seeding_AddsLibraryOnceAndKeepsOldProfilesByteIdentical()
    {
        var settings = AppSettings.CreateDefault();
        settings.Osd ??= new OsdSettings();
        var old = new OsdProfile
        {
            Id = "user-profile",
            Name = "My profile",
            Style = OsdStyle.Classic,
            Elements = { new OsdElement { Id = "e1", Kind = OsdElementKind.Sensor, SensorId = "gpu.usage" } },
        };
        settings.Osd.Profiles.Clear();
        settings.Osd.Profiles.Add(old);
        settings.Osd.ActiveProfileId = old.Id;
        settings.Osd.CapFrameXLibraryVersion = 0;

        settings.Osd.Normalize(settings.Overlay, settings.Metrics, settings.Fps.Enabled);

        Assert.Equal(49, settings.Osd.Profiles.Count);
        var preserved = settings.Osd.FindProfile(old.Id);
        Assert.NotNull(preserved);
        Assert.Equal("My profile", preserved!.Name);
        Assert.Equal(OsdStyle.Classic, preserved.Style);
        Assert.Single(preserved.Elements);
        Assert.Equal(old.Id, settings.Osd.ActiveProfileId);
        Assert.Equal(41, settings.Osd.Profiles.Count(profile =>
            profile.Library == OsdDesignLibrary.CapFrameX && profile.Layout == OsdProfileLayout.Tile));
        Assert.Equal(3, settings.Osd.Profiles.Count(profile =>
            profile.Library == OsdDesignLibrary.CapFrameX && profile.Layout == OsdProfileLayout.Rows));

        var count = settings.Osd.Profiles.Count;
        settings.Osd.Normalize(settings.Overlay, settings.Metrics, settings.Fps.Enabled);
        Assert.Equal(count, settings.Osd.Profiles.Count);

        settings.Osd.Profiles.RemoveAll(profile => profile.Id.StartsWith("cfx-tile-", StringComparison.Ordinal));
        settings.Osd.Normalize(settings.Overlay, settings.Metrics, settings.Fps.Enabled);
        Assert.DoesNotContain(settings.Osd.Profiles,
            profile => profile.Id.StartsWith("cfx-tile-", StringComparison.Ordinal));
    }

    [Fact]
    public void TileProfile_RoundTripsThroughJson()
    {
        var settings = AppSettings.CreateDefault();
        settings.Osd ??= new OsdSettings();
        settings.Osd.Profiles.Clear();
        settings.Osd.CapFrameXLibraryVersion = 0;
        settings.Osd.Normalize(settings.Overlay, settings.Metrics, settings.Fps.Enabled);
        var profile = settings.Osd.Profiles.Single(item => item.DesignId == "cfx-benchmark");
        profile.Tile.Scale = 1.25;
        profile.Tile.Opacity = 0.8;
        var node = profile.Tile.Ensure("1");
        node.Label = "Custom FPS";
        node.Key = "fpsCurrent";
        node.ValueColor = "#00FF00";

        var json = JsonSerializer.Serialize(profile);
        var restored = JsonSerializer.Deserialize<OsdProfile>(json);
        Assert.NotNull(restored);
        Assert.Equal(OsdDesignLibrary.CapFrameX, restored!.Library);
        Assert.Equal(OsdProfileLayout.Tile, restored.Layout);
        Assert.Equal("cfx-benchmark", restored.DesignId);
        Assert.Equal(1.25, restored.Tile.Scale, 3);
        Assert.Equal(0.8, restored.Tile.Opacity, 3);
        Assert.Equal("Custom FPS", restored.Tile.Nodes["1"].Label);
        Assert.Equal("fpsCurrent", restored.Tile.Nodes["1"].Key);
        Assert.Equal("#00FF00", restored.Tile.Nodes["1"].ValueColor);
    }

    [Fact]
    public void TileOverrides_NormalizeClampsInvalidValues()
    {
        var overrides = new TileOverrideSettings
        {
            Scale = 99,
            Opacity = -3,
            Nodes =
            {
                ["1"] = new TileNodeOverride
                {
                    Digits = 99,
                    ValueScale = -2,
                    ChartThickness = 99,
                    BarMinimum = 10,
                    BarMaximum = 5,
                    ValueColor = "not-a-colour",
                },
            },
        };
        overrides.Normalize();
        Assert.Equal(2, overrides.Scale);
        Assert.Equal(0, overrides.Opacity);
        Assert.Equal(6, overrides.Nodes["1"].Digits);
        Assert.Null(overrides.Nodes["1"].ValueScale);
        Assert.Equal(8, overrides.Nodes["1"].ChartThickness);
        Assert.Equal(11, overrides.Nodes["1"].BarMaximum);
        Assert.Null(overrides.Nodes["1"].ValueColor);
    }

    [Fact]
    public void SensorMap_ResolvesAliasesAndRejectsUnknownKeys()
    {
        var snapshot = Snapshot();
        var context = new TileRenderContext("GPU", "CPU");
        Assert.Equal(88, TileSensorMap.Resolve("gpuLoad", snapshot, null, null, context).Numeric);
        Assert.Equal(67, TileSensorMap.Resolve("gpuTemp", snapshot, null, null, context).Numeric);
        Assert.Equal(171, TileSensorMap.Resolve("gpuPower", snapshot, null, null, context).Numeric);
        Assert.False(TileSensorMap.Resolve("does.not.exist", snapshot, null, null, context).Available);
        Assert.Equal("N/A",
            TileSensorMap.Format(TileSensorMap.Resolve("does.not.exist", snapshot, null, null, context),
                null, null, 1));
    }
}
