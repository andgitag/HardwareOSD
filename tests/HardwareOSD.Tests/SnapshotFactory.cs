using HardwareOSD.Core.Hardware;

namespace HardwareOSD.Tests;

public static class SnapshotFactory
{
    public static SensorSnapshot Build(params (string Id, double Value, string Unit, SensorKind Kind)[] sensors)
    {
        var dict = new Dictionary<string, SensorValue>(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var (id, value, unit, kind) in sensors)
        {
            dict[id] = new SensorValue(id, value, unit, kind, SensorOrigin.HardwareSensor, "test", now);
        }

        return SensorSnapshot.Create(1, now, dict);
    }

    public static SensorSnapshot Typical()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var dict = new Dictionary<string, SensorValue>(StringComparer.Ordinal)
        {
            [SensorIds.CpuTemperature] = new(SensorIds.CpuTemperature, 62, "°C", SensorKind.Temperature, SensorOrigin.HardwareSensor, "lhm", now),
            [SensorIds.CpuUsage] = new(SensorIds.CpuUsage, 24, "%", SensorKind.Load, SensorOrigin.WindowsApi, "win", now),
            [SensorIds.CpuClock] = new(SensorIds.CpuClock, 4120, "MHz", SensorKind.Clock, SensorOrigin.WindowsApi, "win", now),
            [SensorIds.CpuPower] = new(SensorIds.CpuPower, 45.5, "W", SensorKind.Power, SensorOrigin.HardwareSensor, "lhm", now),
            [SensorIds.GpuTemperature] = new(SensorIds.GpuTemperature, 67, "°C", SensorKind.Temperature, SensorOrigin.HardwareSensor, "nvml", now),
            [SensorIds.GpuUsage] = new(SensorIds.GpuUsage, 98, "%", SensorKind.Load, SensorOrigin.HardwareSensor, "nvml", now),
            [SensorIds.GpuPower] = new(SensorIds.GpuPower, 175, "W", SensorKind.Power, SensorOrigin.HardwareSensor, "nvml", now),
            [SensorIds.VramUsed] = new(SensorIds.VramUsed, 8234, "MB", SensorKind.Data, SensorOrigin.HardwareSensor, "nvml", now),
            [SensorIds.VramTotal] = new(SensorIds.VramTotal, 12282, "MB", SensorKind.Data, SensorOrigin.HardwareSensor, "nvml", now),
            [SensorIds.RamUsed] = new(SensorIds.RamUsed, 21913, "MB", SensorKind.Data, SensorOrigin.WindowsApi, "win", now),
            [SensorIds.RamTotal] = new(SensorIds.RamTotal, 65227, "MB", SensorKind.Data, SensorOrigin.WindowsApi, "win", now),
        };

        return SensorSnapshot.Create(7, now, dict);
    }
}
