using HardwareOSD.Core.Config;
using HardwareOSD.Core.Hardware.Providers;
using HardwareOSD.Core.Metrics;

namespace HardwareOSD.Core.Hardware;

/// <summary>Builds the provider set for the current configuration. Shared by the GUI and the CLI.</summary>
public static class SensorProviderFactory
{
    public static List<ISensorProvider> Create(
        AppSettings settings,
        bool useDemoData = false,
        bool? elevatedOverride = null,
        bool? brokerInstalledOverride = null)
    {
        var providers = new List<ISensorProvider>(5);
        if (useDemoData)
        {
            // Demo data replaces hardware providers entirely so synthetic and real values can never mix.
            providers.Add(new DemoSensorProvider());
            return providers;
        }

        providers.Add(new WindowsSystemProvider());
        providers.Add(new ThermalZoneProvider());
        // Windows 11 Energy Meter/EMI exposes RAPL package and subdomain power to standard users.
        // It is a built-in OS provider and does not depend on CapFrameX, MSI Center or a driver.
        providers.Add(new WindowsEnergyMeterProvider());

        var elevated = elevatedOverride ?? LibreHardwareMonitorProvider.IsElevated;
        var brokerInstalled = brokerInstalledOverride ??
                              (!elevated && settings.Monitoring.EnableMsiAcpi &&
                               settings.Monitoring.EnableMsiFanBroker &&
                               MsiFanBrokerClient.IsServiceInstalled());
        var msiSource = MsiFanSourceSelector.Select(
            settings.Monitoring.EnableMsiAcpi,
            settings.Monitoring.EnableMsiFanBroker,
            elevated,
            brokerInstalled);

        // Exactly one MSI source is registered. The elevated process keeps the proven direct WMI
        // path; a standard user can use the broker without ever falling back to direct EC/PawnIO.
        switch (msiSource)
        {
            case MsiFanSource.DirectWmi:
                providers.Add(new MsiAcpiFanProvider());
                break;
            case MsiFanSource.Broker:
                providers.Add(new MsiFanBrokerProvider());
                break;
        }

        if (settings.Monitoring.EnableNvml)
        {
            providers.Add(new NvmlProvider());
        }

        if (settings.Monitoring.EnableLibreHardwareMonitor)
        {
            providers.Add(new LibreHardwareMonitorProvider());
        }

        return providers;
    }

    /// <summary>Sensor ids tracked by the sparkline/statistics history.</summary>
    public static IEnumerable<string> HistorySensorIds()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in MetricCatalog.All)
        {
            var id = MetricCatalog.SensorIdFor(definition);
            if (!string.IsNullOrWhiteSpace(id) && seen.Add(id))
            {
                yield return id;
            }
        }

        // Fan metrics are aggregate channels; the raw channels are tracked when present.
        yield return SensorIds.CpuUsage;
        yield return SensorIds.CpuTemperature;
        yield return SensorIds.CpuPower;
        yield return SensorIds.GpuUsage;
        yield return SensorIds.GpuTemperature;
        yield return SensorIds.GpuPower;
        yield return SensorIds.VramPercent;
        yield return SensorIds.RamPercent;
        yield return SensorIds.CpuClock;
    }
}
