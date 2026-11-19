using System.Collections.ObjectModel;

namespace HardwareOSD.Core.Hardware;

/// <summary>
/// Decides which provider wins when several sources report the same sensor.
/// Higher rank wins; only available values participate. Ranks are per-sensor so that
/// CPU usage can prefer the cheap Windows counters while CPU temperature prefers
/// LibreHardwareMonitor's real sensor access.
/// </summary>
public sealed class SensorMergePolicy
{
    public const string WindowsKey = "win";
    public const string EnergyMeterKey = "energy-meter";
    public const string NvmlKey = "nvml";
    public const string LhmKey = "lhm";
    public const string MsiAcpiKey = "msi-acpi";
    public const string MsiBrokerKey = "msi-broker";
    public const string DemoKey = "demo";

    private static readonly IReadOnlyDictionary<string, int> DefaultRanks =
        new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [DemoKey] = 100,
            [LhmKey] = 30,
            [MsiAcpiKey] = 25,
            [MsiBrokerKey] = 25,
            [NvmlKey] = 20,
            [WindowsKey] = 10,
            [EnergyMeterKey] = 10,
        });

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Overrides =
        new ReadOnlyDictionary<string, IReadOnlyDictionary<string, int>>(
            new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal)
            {
                [SensorIds.CpuUsage] = Ranks((LhmKey, 10), (WindowsKey, 40)),
                [SensorIds.CpuUsageMax] = Ranks((LhmKey, 10), (WindowsKey, 40)),
                [SensorIds.RamUsed] = Ranks((LhmKey, 10), (WindowsKey, 40)),
                [SensorIds.RamTotal] = Ranks((LhmKey, 10), (WindowsKey, 40)),
                [SensorIds.RamPercent] = Ranks((LhmKey, 10), (WindowsKey, 40)),
                [SensorIds.RamAvailable] = Ranks((LhmKey, 10), (WindowsKey, 40)),
                [SensorIds.CpuClock] = Ranks((WindowsKey, 15), (LhmKey, 45)),
                // LHM's MSR path remains preferred when it is available (elevated); Windows EMI
                // supplies the same domains to standard-user sessions.
                [SensorIds.CpuVoltage] = Ranks((LhmKey, 30)),
                [SensorIds.GpuTemperature] = Ranks((LhmKey, 25), (NvmlKey, 40)),
                [SensorIds.GpuUsage] = Ranks((LhmKey, 20), (NvmlKey, 40)),
                [SensorIds.GpuClock] = Ranks((LhmKey, 20), (NvmlKey, 40)),
                [SensorIds.GpuMemoryClock] = Ranks((LhmKey, 20), (NvmlKey, 40)),
                [SensorIds.GpuPower] = Ranks((LhmKey, 20), (NvmlKey, 40)),
                [SensorIds.GpuMemoryControllerUsage] = Ranks((LhmKey, 20), (NvmlKey, 40)),
                [SensorIds.VramUsed] = Ranks((LhmKey, 20), (NvmlKey, 40)),
                [SensorIds.VramTotal] = Ranks((LhmKey, 20), (NvmlKey, 40)),
            });

    private static IReadOnlyDictionary<string, int> Ranks(params (string Key, int Rank)[] ranks)
        => new ReadOnlyDictionary<string, int>(ranks.ToDictionary(r => r.Key, r => r.Rank, StringComparer.Ordinal));

    public int GetRank(string sensorId, string providerKey)
    {
        // Fan channels are physical and provider-specific: LibreHardwareMonitor's NVAPI/ADL/SuperIO
        // path supplies the paired RPM and PWM percentage, so it wins over NVML's aggregate/fallback
        // values. This avoids mixing an RPM from one API with a percentage from another API when the
        // physical fan ordering cannot be guaranteed.
        if (FanSensorIds.IsFanSensor(sensorId))
        {
            return providerKey switch
            {
                DemoKey => 100,
                LhmKey => 40,
                MsiAcpiKey when sensorId.StartsWith(FanSensorIds.CpuPrefix, StringComparison.Ordinal) ||
                                sensorId.StartsWith(FanSensorIds.GpuPrefix, StringComparison.Ordinal) => 35,
                MsiBrokerKey when sensorId.StartsWith(FanSensorIds.CpuPrefix, StringComparison.Ordinal) ||
                                  sensorId.StartsWith(FanSensorIds.GpuPrefix, StringComparison.Ordinal) => 35,
                NvmlKey when sensorId.StartsWith(FanSensorIds.GpuPrefix, StringComparison.Ordinal) => 20,
                _ => 0,
            };
        }

        var table = Overrides.TryGetValue(sensorId, out var overrides) ? overrides : DefaultRanks;
        return table.TryGetValue(providerKey, out var rank) ? rank : 0;
    }

    /// <summary>
    /// Merges provider output into a single dictionary, keeping the value with the highest rank.
    /// Values that are unavailable never override an available one.
    /// </summary>
    public Dictionary<string, SensorValue> Merge(IReadOnlyList<(string ProviderKey, IReadOnlyList<SensorValue> Values)> providerOutput)
    {
        var result = new Dictionary<string, SensorValue>(64, StringComparer.Ordinal);
        var ranks = new Dictionary<string, int>(64, StringComparer.Ordinal);

        foreach (var (providerKey, values) in providerOutput)
        {
            foreach (var value in values)
            {
                if (!value.Available)
                {
                    // Remember an unavailable entry only if nothing else produced this id.
                    if (!result.ContainsKey(value.Id))
                    {
                        result[value.Id] = value;
                        ranks[value.Id] = int.MinValue;
                    }

                    continue;
                }

                var rank = GetRank(value.Id, providerKey);
                if (result.TryGetValue(value.Id, out var existing) &&
                    existing.Available &&
                    ranks.TryGetValue(value.Id, out var existingRank) &&
                    existingRank >= rank)
                {
                    continue;
                }

                result[value.Id] = value;
                ranks[value.Id] = rank;
            }
        }

        return result;
    }
}
