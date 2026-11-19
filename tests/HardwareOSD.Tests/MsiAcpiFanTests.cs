using HardwareOSD.Core.Config;
using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.Hardware.Providers;
using HardwareOSD.Core.Metrics;

namespace HardwareOSD.Tests;

public class MsiAcpiFanTests
{
    private const long Now = 1_700_000_000_000;

    private static SensorSnapshot Snapshot(params SensorValue[] values)
    {
        var dictionary = values.ToDictionary(v => v.Id, v => v, StringComparer.Ordinal);
        return SensorSnapshot.Create(1, Now, dictionary);
    }

    [Fact]
    public void ParseReport_UsesBigEndianPeriodsAndRpmFormula()
    {
        var bytes = new byte[32];
        bytes[0] = 1;
        bytes[1] = 0x00;
        bytes[2] = 0xAA;
        bytes[3] = 0x00;
        bytes[4] = 0x85;

        var readings = MsiAcpiFanProvider.ParseReport(bytes);

        Assert.Equal(4, readings.Count);
        Assert.Equal(0xAA, readings[0].Period);
        Assert.Equal(480_000.0 / 0xAA, readings[0].Rpm, 3);
        Assert.Equal(0x85, readings[1].Period);
        Assert.Equal(480_000.0 / 0x85, readings[1].Rpm, 3);
        Assert.Equal(0, readings[2].Period);
        Assert.Equal(0, readings[3].Rpm);
    }

    [Fact]
    public void ParseReport_RejectsFailureStatusAndShortPackages()
    {
        var failure = new byte[32];
        Assert.Empty(MsiAcpiFanProvider.ParseReport(failure));
        Assert.Empty(MsiAcpiFanProvider.ParseReport(new byte[8]));
        Assert.Empty(MsiAcpiFanProvider.ParseReport(null));
    }

    [Fact]
    public void ParseReport_ZeroPeriodIsAValidZeroRpmReading()
    {
        var bytes = new byte[32];
        bytes[0] = 1;

        var readings = MsiAcpiFanProvider.ParseReport(bytes);

        Assert.All(readings, reading =>
        {
            Assert.Equal(0, reading.Period);
            Assert.Equal(0, reading.Rpm);
        });
    }

    [Fact]
    public void FanReader_PairsMsiRpmOnlyChannelAndStripsTheRawPeriodFromTheName()
    {
        var snapshot = Snapshot(
            new SensorValue("fan.cpu.0.rpm", 2400, "RPM", SensorKind.Fan,
                SensorOrigin.HardwareSensor, "MSI_ACPI Get_Fan: CPU Fan (period 200)", Now),
            new SensorValue("fan.cpu.0.percent", double.NaN, string.Empty, SensorKind.Fan,
                SensorOrigin.HardwareSensor, "MSI_ACPI Get_Fan: CPU Fan (period 200)", Now, false,
                SensorNotes.MsiFanNoPercentage));

        var channel = Assert.Single(FanSensorReader.Channels(snapshot, FanComponent.Cpu));
        Assert.Equal("CPU Fan", channel.Name);
        Assert.Equal(2400, channel.Rpm);
        Assert.Null(channel.Percent);
        Assert.True(channel.Available);
        Assert.Equal(SensorNotes.MsiFanNoPercentage, channel.Note);
    }

    [Fact]
    public void FanReader_KeepsComponentIndexesIndependentOfTheMsiGlobalFanPosition()
    {
        var snapshot = Snapshot(
            new SensorValue("fan.cpu.0.rpm", 2400, "RPM", SensorKind.Fan,
                SensorOrigin.HardwareSensor, "MSI_ACPI Get_Fan: CPU Fan (period 200)", Now),
            new SensorValue("fan.gpu.0.rpm", 2892, "RPM", SensorKind.Fan,
                SensorOrigin.HardwareSensor, "MSI_ACPI Get_Fan: GPU Fan (period 166)", Now));

        var gpu = Assert.Single(FanSensorReader.Channels(snapshot, FanComponent.Gpu));
        Assert.Equal(0, gpu.Index);
        Assert.Equal(2892, gpu.Rpm);
    }

    [Fact]
    public void MergePolicy_PrefersLhmThenMsiThenNvmlForFanChannels()
    {
        var policy = new SensorMergePolicy();
        Assert.True(policy.GetRank("fan.gpu.0.rpm", SensorMergePolicy.LhmKey) >
                    policy.GetRank("fan.gpu.0.rpm", SensorMergePolicy.MsiAcpiKey));
        Assert.True(policy.GetRank("fan.gpu.0.rpm", SensorMergePolicy.MsiAcpiKey) >
                    policy.GetRank("fan.gpu.0.rpm", SensorMergePolicy.NvmlKey));
        Assert.True(policy.GetRank("fan.cpu.0.rpm", SensorMergePolicy.MsiAcpiKey) > 0);
    }

    [Fact]
    public void Audit_ExplainsThatMsiExposesRpmButNoPercentage()
    {
        var settings = AppSettings.CreateDefault();
        var snapshot = Snapshot(
            new SensorValue("fan.cpu.0.rpm", 2400, "RPM", SensorKind.Fan,
                SensorOrigin.HardwareSensor, "MSI_ACPI Get_Fan: CPU Fan (period 200)", Now),
            new SensorValue("fan.cpu.0.percent", double.NaN, string.Empty, SensorKind.Fan,
                SensorOrigin.HardwareSensor, "MSI_ACPI Get_Fan: CPU Fan (period 200)", Now, false,
                SensorNotes.MsiFanNoPercentage));

        var entries = SensorAudit.Build(settings, snapshot, null, Now);
        var rpm = Assert.Single(entries, e => e.MetricId == "cpu.fan.rpm");
        Assert.True(rpm.Available);
        Assert.Equal("2400", rpm.DisplayValue);
        Assert.Contains("period 200", rpm.Source);

        var percent = Assert.Single(entries, e => e.MetricId == "cpu.fan.percent");
        Assert.False(percent.Available);
        Assert.Equal(SensorNotes.MsiFanNoPercentage, percent.Note);
    }

    [Fact]
    public void Audit_UnavailableMsiChannelKeepsTheElevationReason()
    {
        var settings = AppSettings.CreateDefault();
        var snapshot = Snapshot(
            new SensorValue("fan.cpu.0.rpm", double.NaN, string.Empty, SensorKind.Fan,
                SensorOrigin.HardwareSensor, "MSI_ACPI Get_Fan", Now, false,
                SensorNotes.MsiAcpiRequiresElevation),
            new SensorValue("fan.cpu.0.percent", double.NaN, string.Empty, SensorKind.Fan,
                SensorOrigin.HardwareSensor, "MSI_ACPI Get_Fan", Now, false,
                SensorNotes.MsiAcpiRequiresElevation));

        var entries = SensorAudit.Build(settings, snapshot, null, Now);
        var rpm = Assert.Single(entries, e => e.MetricId == "cpu.fan.rpm");
        Assert.False(rpm.Available);
        Assert.Equal(SensorNotes.MsiAcpiRequiresElevation, rpm.Note);
    }

    [Fact]
    public void MetricFormatter_ShowsRpmButNotAFabricatedPercentage()
    {
        var snapshot = Snapshot(
            new SensorValue("fan.gpu.0.rpm", 2892, "RPM", SensorKind.Fan,
                SensorOrigin.HardwareSensor, "MSI_ACPI Get_Fan: GPU Fan (period 166)", Now));

        var rpm = MetricFormatter.Format(MetricCatalog.Find("gpu.fan.rpm")!, snapshot);
        var percent = MetricFormatter.Format(MetricCatalog.Find("gpu.fan.percent")!, snapshot);

        Assert.True(rpm.Available);
        Assert.Equal("2892", rpm.Value);
        Assert.Equal("RPM", rpm.Unit);
        Assert.False(percent.Available);
        Assert.Equal("N/A", percent.Value);
    }
}
