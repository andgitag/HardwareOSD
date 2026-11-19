using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.Metrics;

namespace HardwareOSD.Tests;

public class FanSensorTests
{
    private const long Now = 1_700_000_000_000;

    private static SensorSnapshot Snapshot(params (string Id, double Value, string Unit, string Source)[] values)
    {
        var dictionary = new Dictionary<string, SensorValue>(StringComparer.Ordinal);
        foreach (var (id, value, unit, source) in values)
        {
            dictionary[id] = new SensorValue(id, value, unit, SensorKind.Fan,
                SensorOrigin.HardwareSensor, source, Now);
        }

        return SensorSnapshot.Create(1, Now, dictionary);
    }

    [Fact]
    public void Parser_RecognisesComponentsAndMeasures()
    {
        Assert.True(FanSensorIds.TryParse("fan.cpu.0.rpm", out var cpu, out var cpuIndex, out var cpuMeasure));
        Assert.Equal(FanComponent.Cpu, cpu);
        Assert.Equal(0, cpuIndex);
        Assert.Equal(FanMeasure.Rpm, cpuMeasure);

        Assert.True(FanSensorIds.TryParse("fan.gpu.2.percent", out var gpu, out var gpuIndex, out var gpuMeasure));
        Assert.Equal(FanComponent.Gpu, gpu);
        Assert.Equal(2, gpuIndex);
        Assert.Equal(FanMeasure.Percent, gpuMeasure);

        Assert.True(FanSensorIds.TryParse("fan.pump.0.rpm", out var pump, out _, out _));
        Assert.Equal(FanComponent.Pump, pump);
        Assert.False(FanSensorIds.TryParse("gpu.fan.rpm", out _, out _, out _));
        Assert.False(FanSensorIds.TryParse("fan.cpu.0.temp", out _, out _, out _));
    }

    [Fact]
    public void Classifier_SeparatesCpuFanOptSystemPumpAndUnknown()
    {
        Assert.Equal(FanComponent.Cpu, FanClassifier.Classify("CPU Fan"));
        Assert.Equal(FanComponent.Cpu, FanClassifier.Classify("CPU Optional Fan"));
        Assert.Equal(FanComponent.System, FanClassifier.Classify("System Fan #2"));
        Assert.Equal(FanComponent.System, FanClassifier.Classify("Chassis Fan"));
        Assert.Equal(FanComponent.System, FanClassifier.Classify("GPU Fan")); // motherboard header
        Assert.Equal(FanComponent.Pump, FanClassifier.Classify("Water Pump"));
        Assert.Equal(FanComponent.Other, FanClassifier.Classify("Fan #1"));
        Assert.Equal(FanComponent.Gpu, FanClassifier.Classify("GPU Fan 2", gpuHardware: true));
        Assert.Equal(FanComponent.Pump, FanClassifier.Classify("Pump Fan", gpuHardware: true));
    }

    [Fact]
    public void Reader_PairsRpmAndPercentWithoutMixingPumps()
    {
        var snapshot = Snapshot(
            ("fan.cpu.0.rpm", 1200, "RPM", "LibreHardwareMonitor/Nuvoton NCT6798D: CPU Fan"),
            ("fan.cpu.0.percent", 55, "%", "LibreHardwareMonitor/Nuvoton NCT6798D: CPU Fan"),
            ("fan.pump.0.rpm", 2200, "RPM", "LibreHardwareMonitor/Nuvoton NCT6798D: Pump"));

        var cpu = FanSensorReader.Channels(snapshot, FanComponent.Cpu);
        var channel = Assert.Single(cpu);
        Assert.Equal("CPU Fan", channel.Name);
        Assert.Equal("LibreHardwareMonitor/Nuvoton NCT6798D", channel.Hardware);
        Assert.Equal(1200, channel.Rpm);
        Assert.Equal(55, channel.Percent);
        Assert.True(channel.Available);
        Assert.Null(channel.Note);

        var all = FanSensorReader.Channels(snapshot);
        Assert.Equal(2, all.Count);
        Assert.Contains(all, c => c.Component == FanComponent.Pump && c.Rpm == 2200);
    }

    [Fact]
    public void Formatter_JoinsEveryPhysicalFanAndNeverAverages()
    {
        var snapshot = Snapshot(
            ("fan.cpu.0.rpm", 1200, "RPM", "LibreHardwareMonitor/Nuvoton: CPU Fan"),
            ("fan.cpu.1.rpm", 900, "RPM", "LibreHardwareMonitor/Nuvoton: CPU Optional Fan"),
            ("fan.cpu.0.percent", 50, "%", "LibreHardwareMonitor/Nuvoton: CPU Fan"),
            ("fan.cpu.1.percent", 42, "%", "LibreHardwareMonitor/Nuvoton: CPU Optional Fan"),
            ("fan.pump.0.rpm", 2200, "RPM", "LibreHardwareMonitor/Nuvoton: Pump"));

        var rpm = MetricFormatter.Format(MetricCatalog.Find("cpu.fan.rpm")!, snapshot);
        Assert.True(rpm.Available);
        Assert.Equal("1200/900", rpm.Value);
        Assert.Equal("RPM", rpm.Unit);

        var percent = MetricFormatter.Format(MetricCatalog.Find("cpu.fan.percent")!, snapshot);
        Assert.True(percent.Available);
        Assert.Equal("50/42", percent.Value);
        Assert.Equal("%", percent.Unit);

        var gpu = MetricFormatter.Format(MetricCatalog.Find("gpu.fan.rpm")!, snapshot);
        Assert.False(gpu.Available);
    }

    [Fact]
    public void Formatter_DoesNotInventPercentageWhenOnlyRpmIsAvailable()
    {
        var snapshot = Snapshot(("fan.gpu.0.rpm", 1320, "RPM", "LibreHardwareMonitor/NVIDIA: GPU Fan 1"));
        var rpm = MetricFormatter.Format(MetricCatalog.Find("gpu.fan.rpm")!, snapshot);
        var percent = MetricFormatter.Format(MetricCatalog.Find("gpu.fan.percent")!, snapshot);

        Assert.True(rpm.Available);
        Assert.Equal("1320", rpm.Value);
        Assert.False(percent.Available);
        Assert.Equal("N/A", percent.Value);
    }

    [Fact]
    public void Audit_RecordsEveryFanChannelWithSourceAndAvailability()
    {
        var snapshot = Snapshot(
            ("fan.cpu.0.rpm", 1200, "RPM", "LibreHardwareMonitor/Nuvoton NCT6798D: CPU Fan"),
            ("fan.cpu.0.percent", 55, "%", "LibreHardwareMonitor/Nuvoton NCT6798D: CPU Fan"),
            ("fan.system.0.rpm", 800, "RPM", "LibreHardwareMonitor/Nuvoton NCT6798D: System Fan #1"),
            ("fan.pump.0.rpm", 2200, "RPM", "LibreHardwareMonitor/Nuvoton NCT6798D: Pump"));

        var fans = SensorAudit.BuildFanSensors(snapshot, Now + 250);
        Assert.Equal(3, fans.Count);
        var cpu = Assert.Single(fans, f => f.Component == "CPU");
        Assert.Equal(1200, cpu.Rpm);
        Assert.Equal(55, cpu.Percent);
        Assert.Equal("LibreHardwareMonitor/Nuvoton NCT6798D", cpu.Hardware);
        Assert.Equal(250, cpu.AgeMs);
        var pump = Assert.Single(fans, f => f.Component == "Pump");
        Assert.Contains("Pump", pump.FanName);
    }

    [Fact]
    public void Audit_ExportContainsFanRowWithRpmAndPercentage()
    {
        var snapshot = Snapshot(
            ("fan.cpu.0.rpm", 1200, "RPM", "LibreHardwareMonitor/Nuvoton NCT6798D: CPU Fan"),
            ("fan.cpu.0.percent", 55, "%", "LibreHardwareMonitor/Nuvoton NCT6798D: CPU Fan"));
        var settings = HardwareOSD.Core.Config.AppSettings.CreateDefault();
        var entries = SensorAudit.Build(settings, snapshot, null, Now);

        var rpm = Assert.Single(entries, e => e.MetricId == "cpu.fan.rpm");
        Assert.True(rpm.Available);
        Assert.Equal("1200", rpm.DisplayValue);
        Assert.Contains("CPU Fan", rpm.Source);

        var path = Path.Combine(Path.GetTempPath(), $"hwosd-fan-audit-{Guid.NewGuid():N}.csv");
        try
        {
            SensorAudit.WriteCsv(path, entries, snapshot);
            var text = File.ReadAllText(path);
            Assert.Contains("fan_channel_id,component,fan_index,fan_name,hardware,source,rpm,percent", text);
            Assert.Contains("fan.cpu.0,CPU,0,CPU Fan", text);
            Assert.Contains(",1200,55,1,", text);
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
    public void MergePolicy_PrefersPairedLibreHardwareMonitorFanChannelOverNvmlFallback()
    {
        var policy = new SensorMergePolicy();
        var lhm = new SensorValue("fan.gpu.0.percent", 41, "%", SensorKind.Fan,
            SensorOrigin.HardwareSensor, "LibreHardwareMonitor/NVIDIA: GPU Fan 1", Now);
        var nvml = new SensorValue("fan.gpu.0.percent", 47, "%", SensorKind.Fan,
            SensorOrigin.HardwareSensor, "NVML: GPU Fan 1", Now);

        var merged = policy.Merge(new List<(string, IReadOnlyList<SensorValue>)>
        {
            (SensorMergePolicy.NvmlKey, new[] { nvml }),
            (SensorMergePolicy.LhmKey, new[] { lhm }),
        });

        Assert.Equal(41, merged["fan.gpu.0.percent"].Value);
        Assert.Equal("LibreHardwareMonitor/NVIDIA: GPU Fan 1", merged["fan.gpu.0.percent"].Source);
    }
}
