using HardwareOSD.Core.Config;
using HardwareOSD.Core.Fps;
using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.Overlay;

namespace HardwareOSD.Tests;

public class OsdEngineTests
{
    private const long Now = 1_700_000_000_000;

    [Fact]
    public void Defaults_CreateFourStylesWithClassicActive()
    {
        var settings = AppSettings.CreateDefault();
        Assert.Equal(5, settings.Osd.Profiles.Count(p => p.Library == OsdDesignLibrary.HardwareOsd));
        Assert.Equal(44, settings.Osd.Profiles.Count(p => p.Library == OsdDesignLibrary.CapFrameX));
        Assert.Equal("classic", settings.Osd.ActiveProfileId);
        Assert.Contains(settings.Osd.Profiles, p => p.Style == OsdStyle.Classic);
        Assert.Contains(settings.Osd.Profiles, p => p.Style == OsdStyle.HighContrast);
        Assert.Contains(settings.Osd.Profiles, p => p.Style == OsdStyle.BlueLabels);
        Assert.Contains(settings.Osd.Profiles, p => p.Style == OsdStyle.AdaptiveColors);
    }

    [Fact]
    public void ReferenceProfileNames_DoNotUseCapFrameXBranding()
    {
        var settings = AppSettings.CreateDefault();
        Assert.DoesNotContain(settings.Osd.Profiles,
            profile => profile.Name.Contains("CapFrameX", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new[] { "Classic", "Green HUD", "Split HUD", "Info HUD", "Minimal Rings" },
            settings.Osd.Profiles
                .Where(profile => profile.Library == OsdDesignLibrary.HardwareOsd)
                .Select(profile => profile.Name));

        var legacy = AppSettings.CreateDefault();
        legacy.Osd.Profiles.Single(profile => profile.Id == "cfx-green").Name = "CapFrameX Green";
        legacy.Normalize();
        Assert.Equal("Green HUD", legacy.Osd.Profiles.Single(profile => profile.Id == "cfx-green").Name);
    }

    [Fact]
    public void LegacySettings_MigrateToClassicProfileWithoutChangingClassicLayout()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 2,
            Overlay = new OverlaySettings { FontSize = 18 },
            Metrics = new List<MetricPreference>
            {
                new() { Id = "cpu.temp", Visible = true },
                new() { Id = "cpu.usage", Visible = true },
            },
        };
        settings.Migrate();
        settings.Normalize();

        Assert.Equal(3, settings.SchemaVersion);
        Assert.Equal("classic", settings.Osd.ActiveProfileId);
        Assert.True(settings.Osd.ActiveProfile!.IsTile);
        Assert.False(settings.Osd.ActiveProfile.FollowMetricPreferences);
        Assert.NotNull(settings.Osd.ActiveProfile.TileDesignJson);
        Assert.Equal(1, settings.Osd.TileMigrationVersion);
        Assert.Equal(5, settings.Osd.Profiles.Count(p => p.Library == OsdDesignLibrary.HardwareOsd));
    }

    [Fact]
    public void HighContrastProfile_UsesRowsAndIndependentStyleSettings()
    {
        var settings = AppSettings.CreateDefault();
        var profile = settings.Osd.Profiles.Single(p => p.Id == "cfx-green");
        settings.Osd.ActiveProfileId = profile.Id;
        settings.Normalize();

        var layout = OverlayLayoutBuilder.Build(settings, SnapshotFactory.Typical());
        Assert.Contains(layout.Rows, row => row.Kind == OverlayRowKind.GroupHeader && row.Text == "CPU");
        Assert.Contains(layout.Rows, row => row.Kind == OverlayRowKind.SensorLine && row.Label == "CPU Temp");
        Assert.NotEqual(settings.Overlay.LabelColor, profile.StyleSettings.LabelColor);

        // Surface belongs to the active style block, not the legacy Classic overlay settings.
        profile.StyleSettings.CornerRadius = 17;
        profile.StyleSettings.BackgroundOpacity = 23;
        profile.StyleSettings.PaddingX = 7;
        profile.StyleSettings.PaddingY = 5;
        var split = settings.Osd.Profiles.Single(p => p.Id == "cfx-split");
        var splitCorner = split.StyleSettings.CornerRadius;
        var clone = settings.Clone();
        var clonedGreen = clone.Osd.FindProfile("cfx-green")!;
        Assert.Equal(17, clonedGreen.StyleSettings.CornerRadius);
        Assert.Equal(23, clonedGreen.StyleSettings.BackgroundOpacity);
        Assert.Equal(7, clonedGreen.StyleSettings.PaddingX);
        Assert.Equal(5, clonedGreen.StyleSettings.PaddingY);
        Assert.Equal(splitCorner, clone.Osd.FindProfile("cfx-split")!.StyleSettings.CornerRadius);
        Assert.NotEqual(clonedGreen.StyleSettings.CornerRadius,
            clone.Osd.FindProfile("cfx-split")!.StyleSettings.CornerRadius);
    }

    [Fact]
    public void AdaptiveProfile_AppliesWarningAndCriticalThresholds()
    {
        var settings = AppSettings.CreateDefault();
        var profile = settings.Osd.Profiles.Single(p => p.Id == "cfx-info");
        settings.Osd.ActiveProfileId = profile.Id;
        settings.Normalize();

        var hot = SnapshotFactory.Build(
            (SensorIds.CpuTemperature, 62, "°C", SensorKind.Temperature),
            (SensorIds.CpuUsage, 95, "%", SensorKind.Load));
        var layout = OverlayLayoutBuilder.Build(settings, hot);
        var temperature = layout.Rows.SelectMany(r => r.Cells)
            .Single(c => c.Definition.Id == "cpu.temp");
        var usage = layout.Rows.SelectMany(r => r.Cells)
            .Single(c => c.Definition.Id == "cpu.usage");
        Assert.Equal(OsdColorRole.Warning, temperature.ColorRole);
        Assert.Equal(OsdColorRole.Critical, usage.ColorRole);

        var cool = SnapshotFactory.Build((SensorIds.CpuTemperature, 42, "°C", SensorKind.Temperature));
        var coolLayout = OverlayLayoutBuilder.Build(settings, cool);
        var coolCell = coolLayout.Rows.SelectMany(r => r.Cells)
            .Single(c => c.Definition.Id == "cpu.temp");
        Assert.Equal(OsdColorRole.Normal, coolCell.ColorRole);
    }

    [Fact]
    public void CustomProfile_PreservesOrderAndElementKinds()
    {
        var settings = AppSettings.CreateDefault();
        var profile = new OsdProfile
        {
            Name = "Custom",
            Style = OsdStyle.HighContrast,
            StyleSettings = OsdStyleSettings.CreateDefault(OsdStyle.HighContrast),
            Elements = new List<OsdElement>
            {
                new()
                {
                    Kind = OsdElementKind.Group,
                    Label = "GPU",
                    GroupLayout = OsdGroupLayout.Rows,
                    Children = new List<OsdElement>
                    {
                        new() { Kind = OsdElementKind.Sensor, Label = "GPU heat", SensorId = "gpu.temp", Decimals = 1 },
                        new() { Kind = OsdElementKind.Graph, Label = "GPU load", SensorId = "gpu.usage", ShowGraph = true },
                    },
                },
                new() { Kind = OsdElementKind.CustomText, Text = "Custom note" },
                new() { Kind = OsdElementKind.Divider },
                new() { Kind = OsdElementKind.EmptyLine, Height = 12 },
                new() { Kind = OsdElementKind.ProgressBar, Label = "Power", SensorId = "gpu.power", ShowProgress = true, ProgressMin = 0, ProgressMax = 200 },
                new() { Kind = OsdElementKind.CustomValue, Label = "Voltage", SensorId = "cpu.voltage", Unit = "V" },
            },
        };
        profile.Normalize();
        settings.Osd.Profiles.Add(profile);
        settings.Osd.ActiveProfileId = profile.Id;
        settings.Normalize();

        var layout = OverlayLayoutBuilder.Build(settings, SnapshotFactory.Typical());
        Assert.Equal(new[]
        {
            OverlayRowKind.GroupHeader,
            OverlayRowKind.SensorLine,
            OverlayRowKind.Graph,
            OverlayRowKind.CustomText,
            OverlayRowKind.Divider,
            OverlayRowKind.EmptyLine,
            OverlayRowKind.ProgressBar,
            OverlayRowKind.CustomValue,
        }, layout.Rows.Select(r => r.Kind));
        Assert.Equal("GPU heat", layout.Rows[1].Label);
        Assert.Equal("67.0", layout.Rows[1].Cells[0].Value); // 67 formatted with one decimal
        Assert.Equal("Custom note", layout.Rows[3].Text);
        Assert.NotNull(layout.Rows[6].Progress);
    }

    [Fact]
    public void ElementFormatting_AppliesUnitPrefixSuffixAndDecimals()
    {
        var element = new OsdElement
        {
            Kind = OsdElementKind.Sensor,
            SensorId = "gpu.temp",
            Label = "GPU temperature",
            Unit = "deg",
            Decimals = 2,
            Prefix = "~",
            Suffix = "!",
        };
        var formatted = OsdValueFormatter.Format(element, SnapshotFactory.Typical(), null);
        Assert.Equal("~67.00!", formatted.Value);
        Assert.Equal("deg", formatted.Unit);
        Assert.True(formatted.Available);
    }

    [Fact]
    public void ThresholdEngine_UsesKnownGpuLimitButDoesNotInventCpuLimit()
    {
        var element = new OsdElement
        {
            Kind = OsdElementKind.Sensor,
            SensorId = "gpu.power",
            AutoColor = true,
        };
        var snapshot = SnapshotFactory.Build(
            (SensorIds.GpuPower, 170, "W", SensorKind.Power),
            (SensorIds.GpuPowerLimit, 175, "W", SensorKind.Power));
        var decision = OsdThresholdEngine.Evaluate(element, "gpu.power", 170, snapshot,
            HardwareOSD.Core.Metrics.MetricCatalog.Find("gpu.power"));
        Assert.True(decision.LimitKnown);
        Assert.Equal(175, decision.Critical);
        Assert.Equal(OsdColorRole.Warning, decision.Role);

        var noLimit = OsdThresholdEngine.Evaluate(element, "cpu.power", 90, SnapshotFactory.Typical(),
            HardwareOSD.Core.Metrics.MetricCatalog.Find("cpu.power"));
        Assert.False(noLimit.LimitKnown);
        Assert.Equal(OsdColorRole.Normal, noLimit.Role);
    }

    [Fact]
    public void FpsThreshold_UsesLowerIsWorse()
    {
        var element = new OsdElement
        {
            Kind = OsdElementKind.Sensor,
            SensorId = "frame.fps",
            AutoColor = true,
            Direction = OsdThresholdDirection.LowerIsWorse,
            WarningThreshold = 60,
            CriticalThreshold = 30,
        };
        var fps = new FpsSnapshot(1, Now, FpsStatus.Capturing, FpsSource.Presented, false,
            42, "game", 45, 22, 40, null);
        var warning = OsdThresholdEngine.Evaluate(element, "frame.fps", 45,
            SensorSnapshot.Empty, HardwareOSD.Core.Metrics.MetricCatalog.Find("frame.fps"));
        Assert.Equal(OsdColorRole.Warning, warning.Role);

        var critical = OsdThresholdEngine.Evaluate(element, "frame.fps", 20,
            SensorSnapshot.Empty, HardwareOSD.Core.Metrics.MetricCatalog.Find("frame.fps"));
        Assert.Equal(OsdColorRole.Critical, critical.Role);
        _ = fps;
    }

    [Fact]
    public void ProfileSerializer_RoundTripsVersionedProfile()
    {
        var profile = AppSettings.CreateDefault().Osd.Profiles.Single(p => p.Style == OsdStyle.BlueLabels);
        profile.Elements[0].Label = "Custom CPU";
        var json = OsdProfileSerializer.Serialize(profile);
        Assert.Contains("\"schemaVersion\"", json);
        Assert.Contains("\"profile\"", json);

        var restored = OsdProfileSerializer.Deserialize(json, out var error);
        Assert.Null(error);
        Assert.NotNull(restored);
        Assert.Equal(profile.Name, restored!.Name);
        Assert.Equal(OsdStyle.BlueLabels, restored.Style);
        Assert.Equal("Custom CPU", restored.Elements[0].Label);
    }

    [Fact]
    public void UnavailableSensor_IsExplicitNaInCustomLayout()
    {
        var settings = AppSettings.CreateDefault();
        var profile = new OsdProfile
        {
            Name = "Missing",
            Style = OsdStyle.BlueLabels,
            FollowMetricPreferences = false,
            StyleSettings = OsdStyleSettings.CreateDefault(OsdStyle.BlueLabels),
            Elements = new List<OsdElement>
            {
                new() { Kind = OsdElementKind.Sensor, Label = "DRAM power", SensorId = "cpu.power.memory" },
            },
        };
        settings.Osd.Profiles.Add(profile);
        settings.Osd.ActiveProfileId = profile.Id;
        settings.Normalize();

        var layout = OverlayLayoutBuilder.Build(settings, SensorSnapshot.Empty);
        var cell = layout.Rows.Single().Cells.Single();
        Assert.False(cell.Available);
        Assert.Equal("N/A", cell.Value);
        Assert.Equal(OsdColorRole.Unavailable, cell.ColorRole);
    }
}
