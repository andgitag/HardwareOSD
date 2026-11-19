using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.Hardware.Providers;
using HardwareOSD.Core.Metrics;

namespace HardwareOSD.Tests;

public class CpuPowerTests
{
    private const long Now = 1_700_000_000_000;

    private static SensorSnapshot Snapshot(params SensorValue[] values)
        => SensorSnapshot.Create(1, Now, values.ToDictionary(value => value.Id, value => value, StringComparer.Ordinal));

    [Fact]
    public void Catalog_ExposesCpuPowerDomainsAndVoltage()
    {
        Assert.Equal(SensorIds.CpuPowerCores, MetricCatalog.SensorIdFor(MetricCatalog.Find("cpu.power.cores")!));
        Assert.Equal(SensorIds.CpuPowerMemory, MetricCatalog.SensorIdFor(MetricCatalog.Find("cpu.power.memory")!));
        Assert.Equal(SensorIds.CpuPowerGt, MetricCatalog.SensorIdFor(MetricCatalog.Find("cpu.power.gt")!));
        Assert.Equal(SensorIds.CpuVoltage, MetricCatalog.SensorIdFor(MetricCatalog.Find("cpu.voltage")!));
    }

    [Fact]
    public void LhmDomainLabels_MapWithoutDuplicateChannels()
    {
        Assert.True(LibreHardwareMonitorProvider.TryCpuPowerDomainId("CPU Cores", out var cores));
        Assert.Equal(SensorIds.CpuPowerCores, cores);
        Assert.True(LibreHardwareMonitorProvider.TryCpuPowerDomainId("CPU Memory", out var memory));
        Assert.Equal(SensorIds.CpuPowerMemory, memory);
        Assert.True(LibreHardwareMonitorProvider.TryCpuPowerDomainId("CPU Graphics", out var graphics));
        Assert.Equal(SensorIds.CpuPowerGt, graphics);
        Assert.False(LibreHardwareMonitorProvider.TryCpuPowerDomainId("CPU Package", out _));
    }

    [Fact]
    public void LhmHybridCoreIndexes_DoNotCollide()
    {
        Assert.True(LibreHardwareMonitorProvider.TryCoreIndex("P-Core #1", out var p1));
        Assert.Equal(0, p1);
        Assert.True(LibreHardwareMonitorProvider.TryCoreIndex("P-Core #8", out var p8));
        Assert.Equal(7, p8);
        Assert.True(LibreHardwareMonitorProvider.TryCoreIndex("E-Core #1", out var e1));
        Assert.Equal(8, e1);
        Assert.True(LibreHardwareMonitorProvider.TryCoreIndex("E-Core #8", out var e8));
        Assert.Equal(15, e8);
    }

    [Fact]
    public void MergePolicy_PrefersLhmWhenAvailableAndKeepsEmiAsFallback()
    {
        var policy = new SensorMergePolicy();
        Assert.True(policy.GetRank(SensorIds.CpuPower, SensorMergePolicy.LhmKey) >
                    policy.GetRank(SensorIds.CpuPower, SensorMergePolicy.EnergyMeterKey));
        Assert.True(policy.GetRank(SensorIds.CpuPowerCores, SensorMergePolicy.LhmKey) >
                    policy.GetRank(SensorIds.CpuPowerCores, SensorMergePolicy.EnergyMeterKey));
    }

    [Fact]
    public void ProviderFactory_RegistersEnergyMeterAndOnlyOneMsiSource()
    {
        var settings = HardwareOSD.Core.Config.AppSettings.CreateDefault();
        var providers = SensorProviderFactory.Create(settings, elevatedOverride: false, brokerInstalledOverride: false);
        Assert.Contains(providers, provider => provider is WindowsEnergyMeterProvider);
        Assert.Single(providers.OfType<MsiAcpiFanProvider>());
        Assert.Empty(providers.OfType<MsiFanBrokerProvider>());
    }

    [Fact]
    public void Formatter_ShowsVoltageAndDomainPowerWithoutFabricatingMissingValues()
    {
        var snapshot = Snapshot(
            new SensorValue(SensorIds.CpuPowerCores, 12.5, "W", SensorKind.Power,
                SensorOrigin.WindowsApi, "PDH Energy Meter: RAPL_Package0_PP0", Now),
            new SensorValue(SensorIds.CpuVoltage, 1.234, "V", SensorKind.Voltage,
                SensorOrigin.HardwareSensor, "CPU Core", Now),
            new SensorValue(SensorIds.CpuPowerGt, 0.001, "W", SensorKind.Power,
                SensorOrigin.WindowsApi, "PDH Energy Meter: RAPL_Package0_PP1", Now));

        var cores = MetricFormatter.Format(MetricCatalog.Find("cpu.power.cores")!, snapshot);
        var voltage = MetricFormatter.Format(MetricCatalog.Find("cpu.voltage")!, snapshot);
        var memory = MetricFormatter.Format(MetricCatalog.Find("cpu.power.memory")!, snapshot);
        var gt = MetricFormatter.Format(MetricCatalog.Find("cpu.power.gt")!, snapshot);

        Assert.True(cores.Available);
        Assert.Equal("12.5", cores.Value);
        Assert.Equal("W", cores.Unit);
        Assert.True(voltage.Available);
        Assert.Equal("1.234", voltage.Value);
        Assert.Equal("V", voltage.Unit);
        Assert.False(memory.Available);
        Assert.Equal("N/A", memory.Value);
        Assert.True(gt.Available);
        Assert.Equal("0.001", gt.Value);
    }

    [Fact]
    public void PostProcessor_RejectsImpossibleVoltageAndZeroPower()
    {
        var values = new Dictionary<string, SensorValue>(StringComparer.Ordinal)
        {
            [SensorIds.CpuVoltage] = new(SensorIds.CpuVoltage, 3.5, "V", SensorKind.Voltage,
                SensorOrigin.HardwareSensor, "test", Now),
            [SensorIds.CpuPowerCores] = new(SensorIds.CpuPowerCores, 0, "W", SensorKind.Power,
                SensorOrigin.WindowsApi, "test", Now),
        };

        SensorPostProcessor.Sanitize(values, Now);

        Assert.False(values[SensorIds.CpuVoltage].Available);
        Assert.False(values[SensorIds.CpuPowerCores].Available);
    }

    [Fact]
    public void PostProcessor_AcceptsLowPositiveWindowsApiPower()
    {
        var values = new Dictionary<string, SensorValue>(StringComparer.Ordinal)
        {
            [SensorIds.CpuPowerGt] = new(SensorIds.CpuPowerGt, 0.001, "W", SensorKind.Power,
                SensorOrigin.WindowsApi, "PDH Energy Meter: RAPL_Package0_PP1", Now),
        };

        SensorPostProcessor.Sanitize(values, Now);

        Assert.True(values[SensorIds.CpuPowerGt].Available);
        Assert.Equal(0.001, values[SensorIds.CpuPowerGt].Value, 3);
    }
}
