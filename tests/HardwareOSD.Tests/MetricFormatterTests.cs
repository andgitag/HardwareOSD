using HardwareOSD.Core.Fps;
using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.Metrics;

namespace HardwareOSD.Tests;

public class MetricFormatterTests
{
    private static FormattedMetric Format(string id) => MetricFormatter.Format(MetricCatalog.Find(id)!, SnapshotFactory.Typical());

    [Fact]
    public void Temperature_UsesDegreeCelsiusWithoutDecimals()
    {
        var formatted = Format("cpu.temp");
        Assert.Equal("62", formatted.Value);
        Assert.Equal("°C", formatted.Unit);
        Assert.True(formatted.Available);
    }

    [Fact]
    public void Load_UsesPercent()
    {
        var formatted = Format("gpu.usage");
        Assert.Equal("98", formatted.Value);
        Assert.Equal("%", formatted.Unit);
    }

    [Fact]
    public void Clock_SwitchesBetweenGigahertzAndMegahertz()
    {
        Assert.Equal("4.12", Format("cpu.clock").Value);
        Assert.Equal("GHz", Format("cpu.clock").Unit);

        var snapshot = SnapshotFactory.Build((SensorIds.GpuClock, 210, "MHz", SensorKind.Clock));
        var formatted = MetricFormatter.Format(MetricCatalog.Find("gpu.clock")!, snapshot);
        Assert.Equal("210", formatted.Value);
        Assert.Equal("MHz", formatted.Unit);
    }

    [Fact]
    public void Power_UsesWatts()
    {
        var formatted = Format("gpu.power");
        Assert.Equal("175", formatted.Value);
        Assert.Equal("W", formatted.Unit);
    }

    [Fact]
    public void Vram_FormatsUsedOverTotalInMegabytes()
    {
        var formatted = Format("vram.used");
        Assert.Equal("8234 / 12282", formatted.Value);
        Assert.Equal("MB", formatted.Unit);
    }

    [Fact]
    public void Ram_FormatsGigabytesWithOneDecimal()
    {
        var formatted = Format("ram.used");
        Assert.Equal("21.4 / 63.7", formatted.Value);
        Assert.Equal("GB", formatted.Unit);
    }

    [Fact]
    public void MissingSensor_ReturnsNa()
    {
        var formatted = MetricFormatter.Format(MetricCatalog.Find("gpu.temp.hotspot")!, SnapshotFactory.Typical());
        Assert.False(formatted.Available);
        Assert.Equal("N/A", formatted.Value);
        Assert.Equal(string.Empty, formatted.Unit);
    }

    [Fact]
    public void WarningAndCriticalThresholds_AreApplied()
    {
        var warning = MetricFormatter.Format(MetricCatalog.Find("cpu.temp")!,
            SnapshotFactory.Build((SensorIds.CpuTemperature, 87, "°C", SensorKind.Temperature)));
        Assert.Equal(MetricSeverity.Warning, warning.Severity);

        var critical = MetricFormatter.Format(MetricCatalog.Find("cpu.temp")!,
            SnapshotFactory.Build((SensorIds.CpuTemperature, 97, "°C", SensorKind.Temperature)));
        Assert.Equal(MetricSeverity.Critical, critical.Severity);
    }

    [Fact]
    public void VramPercentSeverity_UsesUtilization()
    {
        var formatted = MetricFormatter.Format(MetricCatalog.Find("vram.used")!,
            SnapshotFactory.Build(
                (SensorIds.VramUsed, 11900, "MB", SensorKind.Data),
                (SensorIds.VramTotal, 12282, "MB", SensorKind.Data)));
        Assert.Equal(MetricSeverity.Warning, formatted.Severity);
    }

    [Fact]
    public void EveryCatalogueMetric_HasAFormatterCase()
    {
        var fps = new FpsSnapshot(1, 0, FpsStatus.Capturing, FpsSource.Presented, true, 42, "game", 144, 6.9, 120, null);
        foreach (var definition in MetricCatalog.All)
        {
            var formatted = MetricFormatter.Format(definition, SnapshotFactory.Typical(), fps);
            // A metric with no formatter would silently return the static N/A even though data exists.
            if (definition.Id is "thermalzone.temp")
            {
                continue; // not in the typical snapshot
            }

            if (!formatted.Available)
            {
                // Only optional GPU metrics may be absent from the typical snapshot.
                Assert.True(definition.DefaultVisible == false, $"{definition.Id} unexpectedly unavailable");
            }
        }
    }

    private static FpsSnapshot Fps(double value, bool fallback = true, double frameTime = 6.9, double low = 120)
        => new(1, 0, FpsStatus.Capturing, FpsSource.Presented, fallback, 42, "game", value, frameTime, low, null);

    [Fact]
    public void Fps_HoldingStatus_StillShowsTheLastRealValue()
    {
        var definition = MetricCatalog.Find("frame.fps")!;
        var held = new FpsSnapshot(1, 0, FpsStatus.Holding, FpsSource.Presented, true,
            42, "cs2", 118, 8.47, 100, "holding", IsProcessFallback: false);
        var formatted = MetricFormatter.Format(definition, SnapshotFactory.Typical(), held);

        Assert.True(formatted.Available);
        Assert.Equal("118", formatted.Value);
        Assert.Equal("FPS·p", formatted.Unit);
    }

    [Fact]
    public void Fps_ShowsDashWhenNoMeasurementExists()
    {
        var definition = MetricCatalog.Find("frame.fps")!;
        var formatted = MetricFormatter.Format(definition, SnapshotFactory.Typical(), null);
        Assert.Equal("--", formatted.Value);
        Assert.False(formatted.Available);

        var unavailable = MetricFormatter.Format(definition, SnapshotFactory.Typical(),
            new FpsSnapshot(1, 0, FpsStatus.NoFrames, FpsSource.None, false, 0, null, 0, 0, 0, "waiting"));
        Assert.Equal("--", unavailable.Value);
        Assert.False(unavailable.Available);
    }

    [Fact]
    public void Fps_MarksPresentedFallbackClearly()
    {
        var definition = MetricCatalog.Find("frame.fps")!;

        var automatic = MetricFormatter.Format(definition, SnapshotFactory.Typical(), Fps(143.6, fallback: true));
        Assert.Equal("144", automatic.Value);
        Assert.Equal("FPS·p", automatic.Unit);

        var explicitPresented = MetricFormatter.Format(definition, SnapshotFactory.Typical(), Fps(143.6, fallback: false));
        Assert.Equal("FPS", explicitPresented.Unit);
    }

    [Fact]
    public void Fps_UsesWarningSeverityForLowRates()
    {
        var definition = MetricCatalog.Find("frame.fps")!;
        Assert.Equal(MetricSeverity.Warning, MetricFormatter.Format(definition, SnapshotFactory.Typical(), Fps(25)).Severity);
        Assert.Equal(MetricSeverity.Critical, MetricFormatter.Format(definition, SnapshotFactory.Typical(), Fps(12)).Severity);
        Assert.Equal(MetricSeverity.Normal, MetricFormatter.Format(definition, SnapshotFactory.Typical(), Fps(144)).Severity);
    }

    [Fact]
    public void FrameTimeAndOnePercentLow_AreFormatted()
    {
        var frameTime = MetricFormatter.Format(MetricCatalog.Find("frame.time")!, SnapshotFactory.Typical(), Fps(144, frameTime: 6.94));
        Assert.Equal("6.9", frameTime.Value);
        Assert.Equal("ms", frameTime.Unit);
        Assert.True(frameTime.Available);

        var low = MetricFormatter.Format(MetricCatalog.Find("frame.fps.1low")!, SnapshotFactory.Typical(), Fps(144, low: 118));
        Assert.Equal("118", low.Value);
        Assert.Equal("FPS·1%", low.Unit);
    }

    [Fact]
    public void FrameMetrics_ReturnDashWithoutMeasurement()
    {
        foreach (var id in new[] { "frame.time", "frame.fps.1low" })
        {
            var formatted = MetricFormatter.Format(MetricCatalog.Find(id)!, SnapshotFactory.Typical(), null);
            Assert.Equal("--", formatted.Value);
            Assert.False(formatted.Available);
        }
    }
}
