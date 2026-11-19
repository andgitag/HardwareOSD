using HardwareOSD.Core.Config;
using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.Metrics;

namespace HardwareOSD.Tests;

public class FanPolicyTests
{
    private const long Now = 1_700_000_000_000;

    [Fact]
    public void Normalize_MigratesLegacyGpuFanPreferenceToNewRpmAndPercentMetrics()
    {
        var settings = new AppSettings
        {
            Metrics = new List<MetricPreference>
            {
                new() { Id = "gpu.fan", Visible = true },
            },
        };

        settings.Normalize();

        Assert.Contains(settings.Metrics, m => m.Id == "gpu.fan.rpm" && m.Visible);
        Assert.Contains(settings.Metrics, m => m.Id == "gpu.fan.percent" && m.Visible);
        Assert.DoesNotContain(settings.Metrics, m => m.Id == "gpu.fan");
    }

    [Fact]
    public void FanPostProcessor_RejectsImpossibleRpmAndPercentageButAllowsZeroRpm()
    {
        var values = new Dictionary<string, SensorValue>(StringComparer.Ordinal)
        {
            ["fan.cpu.0.rpm"] = new("fan.cpu.0.rpm", 0, "RPM", SensorKind.Fan,
                SensorOrigin.HardwareSensor, "test", Now),
            ["fan.gpu.0.rpm"] = new("fan.gpu.0.rpm", 60_000, "RPM", SensorKind.Fan,
                SensorOrigin.HardwareSensor, "test", Now),
            ["fan.cpu.0.percent"] = new("fan.cpu.0.percent", 120, "%", SensorKind.Fan,
                SensorOrigin.HardwareSensor, "test", Now),
            ["fan.gpu.0.percent"] = new("fan.gpu.0.percent", 200, "%", SensorKind.Fan,
                SensorOrigin.HardwareSensor, "test", Now),
        };

        SensorPostProcessor.Sanitize(values, Now);

        Assert.True(values["fan.cpu.0.rpm"].Available);
        Assert.False(values["fan.gpu.0.rpm"].Available);
        Assert.True(values["fan.cpu.0.percent"].Available);
        Assert.False(values["fan.gpu.0.percent"].Available);
    }

    [Fact]
    public void ProviderFactory_AddsTheReadOnlyMsiAcpiProviderOnlyWhenEnabled()
    {
        var settings = AppSettings.CreateDefault();
        // Force the direct-WMI branch: the test machine may have the optional broker installed,
        // which would otherwise be selected for a standard user and change the expected provider.
        var providers = SensorProviderFactory.Create(settings,
            elevatedOverride: true, brokerInstalledOverride: false);
        try
        {
            Assert.Contains(providers, provider => provider.Key == SensorMergePolicy.MsiAcpiKey);
        }
        finally
        {
            foreach (var provider in providers)
            {
                provider.Dispose();
            }
        }

        settings.Monitoring.EnableMsiAcpi = false;
        var disabled = SensorProviderFactory.Create(settings);
        try
        {
            Assert.DoesNotContain(disabled, provider => provider.Key == SensorMergePolicy.MsiAcpiKey);
        }
        finally
        {
            foreach (var provider in disabled)
            {
                provider.Dispose();
            }
        }
    }

    [Fact]
    public void FanMetrics_AreOptionalAndHaveStableCombinedWidthSamples()
    {
        foreach (var id in new[] { "cpu.fan.rpm", "cpu.fan.percent", "gpu.fan.rpm", "gpu.fan.percent" })
        {
            var definition = MetricCatalog.Find(id);
            Assert.NotNull(definition);
            Assert.False(definition!.DefaultVisible);
            Assert.Contains("/", definition.SampleValue);
            Assert.True(MetricCatalog.IsFanMetric(id));
        }

        Assert.Equal(FanComponent.Cpu, MetricCatalog.FanComponentForMetric("cpu.fan.rpm"));
        Assert.Equal(FanComponent.Gpu, MetricCatalog.FanComponentForMetric("gpu.fan.percent"));
        Assert.Null(MetricCatalog.SensorIdFor(MetricCatalog.Find("cpu.fan.rpm")!));
    }
}
