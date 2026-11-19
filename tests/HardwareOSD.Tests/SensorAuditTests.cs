using HardwareOSD.Core.Config;
using HardwareOSD.Core.Fps;
using HardwareOSD.Core.Hardware;

namespace HardwareOSD.Tests;

public class SensorAuditTests
{
    private static FpsSnapshot MissingFps(long now) => new(
        1, now, FpsStatus.RequiresElevation, FpsSource.None, false, 0, null, 0, 0, 0,
        "administrator rights required");

    [Fact]
    public void Build_MarksUnavailableSensorsAsMissing()
    {
        var settings = AppSettings.CreateDefault();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entries = SensorAudit.Build(settings, SnapshotFactory.Typical(), MissingFps(now), now);

        var cpuTemperature = Assert.Single(entries, e => e.MetricId == "cpu.temp");
        Assert.True(cpuTemperature.Available);
        Assert.Equal("62", cpuTemperature.DisplayValue);
        Assert.Equal("°C", cpuTemperature.DisplayUnit);
        Assert.Equal("lhm", cpuTemperature.Source);

        var fanRpm = Assert.Single(entries, e => e.MetricId == "gpu.fan.rpm");
        Assert.False(fanRpm.Available);
        Assert.Equal("N/A", fanRpm.DisplayValue);
        Assert.Contains("no GPU fan sensor", fanRpm.Note);

        var fanPercent = Assert.Single(entries, e => e.MetricId == "gpu.fan.percent");
        Assert.False(fanPercent.Available);
        Assert.Contains("no GPU fan sensor", fanPercent.Note);

        var fps = Assert.Single(entries, e => e.MetricId == "frame.fps");
        Assert.False(fps.Available);
        Assert.Equal("RequiresElevation", fps.Note);
    }

    [Fact]
    public void Build_IncludesEveryConfiguredMetric()
    {
        var settings = AppSettings.CreateDefault();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entries = SensorAudit.Build(settings, SnapshotFactory.Typical(), MissingFps(now), now);

        Assert.Equal(settings.Metrics.Count, entries.Count);
        Assert.All(settings.Metrics, preference =>
            Assert.Contains(entries, entry => entry.MetricId == preference.Id));
    }

    [Fact]
    public void WriteCsv_ContainsMetricAndRawSensorSections()
    {
        var settings = AppSettings.CreateDefault();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var snapshot = SnapshotFactory.Typical();
        var entries = SensorAudit.Build(settings, snapshot, MissingFps(now), now);
        var path = Path.Combine(Path.GetTempPath(), $"hwosd-audit-{Guid.NewGuid():N}.csv");

        try
        {
            SensorAudit.WriteCsv(path, entries, snapshot);
            var text = File.ReadAllText(path);

            Assert.Contains("timestamp_utc,metric_id,group,display_name,value,unit,available,origin,source", text);
            Assert.Contains("cpu.temp", text);
            Assert.Contains("raw_sensor_id,value,unit,kind,origin,source,timestamp_utc,available,note", text);
            Assert.Contains("fan_channel_id,component,fan_index,fan_name,hardware,source,rpm,percent,available", text);
            Assert.Contains("gpu.temp", text);
            Assert.Contains("nvml", text);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void FormatTable_ShowsNaForMissingMeasurements()
    {
        var settings = AppSettings.CreateDefault();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var table = SensorAudit.FormatTable(
            SensorAudit.Build(settings, SnapshotFactory.Typical(), MissingFps(now), now));

        Assert.Contains("cpu.temp", table);
        Assert.Contains("available", table);
        Assert.Contains("N/A", table);
    }
}
