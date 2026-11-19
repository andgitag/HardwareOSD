using HardwareOSD.Core.Config;
using HardwareOSD.Core.Fps;
using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.Metrics;
using HardwareOSD.Core.Overlay;

namespace HardwareOSD.Tests;

public class OverlayLayoutBuilderTests
{
    /// <summary>
    /// The five original profiles are tile documents in 0.26.0. These tests exercise the legacy
    /// row engine that remains for the three preserved CapFrameX row templates and imported row
    /// profiles, so they use an explicit Classic-style row profile that follows the metric matrix.
    /// </summary>
    private static AppSettings LegacySettings()
    {
        var settings = AppSettings.CreateDefault();
        var profile = new OsdProfile
        {
            Id = "legacy-test",
            Name = "Legacy test",
            Style = OsdStyle.Classic,
            FollowMetricPreferences = true,
            Elements = OsdProfile.BuildDefaultElements(settings.Metrics, settings.Fps.Enabled,
                OsdStyle.Classic),
            StyleSettings = OsdStyleSettings.CreateClassic(settings.Overlay),
        };
        settings.Osd.Profiles.Add(profile);
        settings.Osd.ActiveProfileId = profile.Id;
        return settings;
    }

    [Fact]
    public void DefaultSettings_ProduceFiveRows()
    {
        var layout = OverlayLayoutBuilder.Build(LegacySettings(), SnapshotFactory.Typical());
        Assert.Equal(new[] { "CPU", "GPU", "VRAM", "RAM", "FPS" }, layout.Rows.Select(r => r.Label));
        Assert.True(layout.HasAnyValue);
    }

    [Fact]
    public void DefaultRows_ContainExpectedMetrics()
    {
        var layout = OverlayLayoutBuilder.Build(LegacySettings(), SnapshotFactory.Typical());
        var cpu = layout.Rows.Single(r => r.Label == "CPU");
        Assert.Equal(new[] { "62", "24", "4.12", "45.5" }, cpu.Cells.Select(c => c.Value));

        var gpu = layout.Rows.Single(r => r.Label == "GPU");
        Assert.Equal(new[] { "67", "98", "175" }, gpu.Cells.Select(c => c.Value));
    }

    [Fact]
    public void HiddenMetric_IsNotRendered()
    {
        var settings = LegacySettings();
        settings.SetMetricVisible("cpu.power", false);
        settings.SetMetricVisible("cpu.clock", false);

        var layout = OverlayLayoutBuilder.Build(settings, SnapshotFactory.Typical());
        var cpu = layout.Rows.Single(r => r.Label == "CPU");
        Assert.Equal(2, cpu.Cells.Count);
    }

    [Fact]
    public void MetricOrder_ControlsCellOrder()
    {
        var settings = LegacySettings();
        var metrics = settings.Metrics;
        var usage = metrics.First(m => m.Id == "cpu.usage");
        metrics.Remove(usage);
        metrics.Insert(0, usage);

        var layout = OverlayLayoutBuilder.Build(settings, SnapshotFactory.Typical());
        Assert.Equal("24", layout.Rows.Single(r => r.Label == "CPU").Cells[0].Value);
    }

    [Fact]
    public void UnavailableMetrics_AreOmittedWhenConfigured()
    {
        var settings = LegacySettings();
        settings.Monitoring.ShowUnavailableMetrics = false;
        settings.SetMetricVisible("gpu.temp.hotspot", true);
        settings.SetMetricVisible("gpu.temp.memory", true);

        var layout = OverlayLayoutBuilder.Build(settings, SnapshotFactory.Typical());
        var gpu = layout.Rows.Single(r => r.Label == "GPU");
        Assert.Equal(3, gpu.Cells.Count); // hotspot and memory junction are missing from the snapshot
    }

    [Fact]
    public void RowsWithNoAvailableMetrics_AreOmittedWhenUnavailableHidden()
    {
        var settings = LegacySettings();
        settings.Monitoring.ShowUnavailableMetrics = false;
        foreach (var metric in settings.Metrics)
        {
            metric.Visible = metric.Id.StartsWith("ram.", StringComparison.Ordinal);
        }

        var layout = OverlayLayoutBuilder.Build(settings, SnapshotFactory.Build());
        Assert.Empty(layout.Rows);
    }

    [Fact]
    public void DetailedLayout_IncludesCoreEntries()
    {
        var settings = LegacySettings();
        settings.Overlay.Layout = OverlayLayoutMode.Detailed;
        var snapshot = SnapshotFactory.Build(
            (SensorIds.CoreTemperature(0), 55, "°C", SensorKind.Temperature),
            (SensorIds.CoreUsage(0), 12, "%", SensorKind.Load),
            (SensorIds.CoreTemperature(1), 61, "°C", SensorKind.Temperature),
            (SensorIds.CoreUsage(1), 44, "%", SensorKind.Load));

        var layout = OverlayLayoutBuilder.Build(settings, snapshot);
        Assert.Equal(2, layout.Cores.Count);
        Assert.Equal("55", layout.Cores[0].Temperature);
        Assert.Equal("44", layout.Cores[1].Usage);
    }

    [Fact]
    public void FpsProcessName_IsASubtitleWithAFixedSlot()
    {
        var settings = LegacySettings();
        settings.Fps.Enabled = true;
        settings.SetMetricVisible("frame.fps", true);
        var fps = new FpsSnapshot(1, 1000, FpsStatus.Capturing, FpsSource.Presented, false,
            42, "a-very-long-process-name-that-would-otherwise-widen-the-overlay.exe",
            144, 6.94, 120, null);

        var layout = OverlayLayoutBuilder.Build(settings, SnapshotFactory.Typical(), fps);
        var frame = layout.Rows.Single(row => row.Group == MetricGroup.Frame);
        Assert.Equal("FPS", frame.Label);
        Assert.Equal("a-very-long-process-name-that-would-otherwise-widen-the-overlay.exe",
            frame.Subtitle);
        Assert.Equal(OverlayRow.FpsNameSlotWidth, frame.SubtitleSlotWidth);

        var shortName = fps with { ProcessName = "cs2.exe" };
        var shortLayout = OverlayLayoutBuilder.Build(settings, SnapshotFactory.Typical(), shortName);
        Assert.Equal(OverlayRow.FpsNameSlotWidth,
            shortLayout.Rows.Single(row => row.Group == MetricGroup.Frame).SubtitleSlotWidth);

        var noValue = fps with { Status = FpsStatus.NoFrames, Fps = 0 };
        var noValueLayout = OverlayLayoutBuilder.Build(settings, SnapshotFactory.Typical(), noValue);
        Assert.Null(noValueLayout.Rows.Single(row => row.Group == MetricGroup.Frame).Subtitle);
    }

    [Fact]
    public void SparklineId_IsAssignedOnlyWhenEnabled()
    {
        var settings = LegacySettings();
        var without = OverlayLayoutBuilder.Build(settings, SnapshotFactory.Typical());
        Assert.All(without.Rows, row => Assert.Null(row.SparklineId));

        settings.Overlay.ShowSparklines = true;
        var with = OverlayLayoutBuilder.Build(settings, SnapshotFactory.Typical());
        Assert.Equal("cpu.usage", with.Rows.Single(r => r.Label == "CPU").SparklineId);
        Assert.Equal("gpu.usage", with.Rows.Single(r => r.Label == "GPU").SparklineId);
    }

    [Fact]
    public void SnapshotSequence_IsPropagated()
    {
        var layout = OverlayLayoutBuilder.Build(LegacySettings(), SnapshotFactory.Typical());
        Assert.Equal(7, layout.Sequence);
    }

    [Fact]
    public void EmptySnapshot_ProducesPlaceholderRowsWithNa()
    {
        var layout = OverlayLayoutBuilder.Build(LegacySettings(), SensorSnapshot.Empty);
        Assert.Equal(5, layout.Rows.Count);
        Assert.False(layout.HasAnyValue);
        Assert.All(layout.Rows.SelectMany(r => r.Cells), cell => Assert.False(cell.Available));
    }

    [Fact]
    public void FanMetrics_ProduceCombinedOverlayCellsWithoutAveraging()
    {
        var settings = LegacySettings();
        settings.SetMetricVisible("cpu.fan.rpm", true);
        settings.SetMetricVisible("cpu.fan.percent", true);
        var snapshot = SnapshotFactory.Build(
            ("fan.cpu.0.rpm", 1200, "RPM", SensorKind.Fan),
            ("fan.cpu.1.rpm", 900, "RPM", SensorKind.Fan),
            ("fan.cpu.0.percent", 50, "%", SensorKind.Fan),
            ("fan.cpu.1.percent", 42, "%", SensorKind.Fan),
            ("fan.pump.0.rpm", 2200, "RPM", SensorKind.Fan));

        var layout = OverlayLayoutBuilder.Build(settings, snapshot);
        var cpu = layout.Rows.Single(r => r.Group == MetricGroup.Cpu);
        var rpm = Assert.Single(cpu.Cells, c => c.Definition.Id == "cpu.fan.rpm");
        var percent = Assert.Single(cpu.Cells, c => c.Definition.Id == "cpu.fan.percent");

        Assert.Equal("1200/900", rpm.Value);
        Assert.Equal("RPM", rpm.Unit);
        Assert.Equal("50/42", percent.Value);
        Assert.Equal("%", percent.Unit);
        Assert.DoesNotContain(cpu.Cells, c => c.Definition.Id.Contains("pump", StringComparison.Ordinal));
    }

    private static FpsSnapshot Fps(double value)
        => new(1, 0, FpsStatus.Capturing, FpsSource.Presented, true, 4242, "cs2", value, 1000 / value, value * 0.8, null);

    [Fact]
    public void FpsRow_IsRenderedFromTheFpsSnapshot()
    {
        var layout = OverlayLayoutBuilder.Build(LegacySettings(), SnapshotFactory.Typical(), Fps(144));
        var row = layout.Rows.Single(r => r.Group == MetricGroup.Frame);
        Assert.Equal("FPS", row.Label);
        Assert.Equal("144", row.Cells[0].Value);
        Assert.Equal("FPS·p", row.Cells[0].Unit);
    }

    [Fact]
    public void FpsRow_ShowsDashWhenNothingIsMeasured()
    {
        var layout = OverlayLayoutBuilder.Build(LegacySettings(), SnapshotFactory.Typical(), null);
        var row = layout.Rows.Single(r => r.Group == MetricGroup.Frame);
        Assert.Equal("--", row.Cells[0].Value);
        Assert.False(row.Cells[0].Available);
    }

    [Fact]
    public void FpsRow_IsRemovedWhenTheModuleIsDisabled()
    {
        var settings = LegacySettings();
        settings.Fps.Enabled = false;
        var layout = OverlayLayoutBuilder.Build(settings, SnapshotFactory.Typical(), Fps(144));
        Assert.DoesNotContain(layout.Rows, r => r.Group == MetricGroup.Frame);
    }

    [Fact]
    public void FpsSparkline_IsAssignedWhenEnabled()
    {
        var settings = LegacySettings();
        settings.Overlay.ShowSparklines = true;
        var layout = OverlayLayoutBuilder.Build(settings, SnapshotFactory.Typical(), Fps(144));
        var row = layout.Rows.Single(r => r.Group == MetricGroup.Frame);
        Assert.Equal("frame.fps", row.SparklineId);
    }
}
