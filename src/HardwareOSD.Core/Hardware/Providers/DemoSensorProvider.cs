using HardwareOSD.Core.Hardware;

namespace HardwareOSD.Core.Hardware.Providers;

/// <summary>
/// QA-only provider that produces deterministic synthetic readings so the overlay can be
/// verified on machines without supported hardware or without elevation. Never enabled unless
/// the application is started with --demo; every value is tagged <see cref="SensorOrigin.Demo"/>
/// and the UI shows a DEMO badge so synthetic data can never be mistaken for real hardware data.
/// </summary>
public sealed class DemoSensorProvider : ISensorProvider
{
    private readonly Random _random = new(20260101);
    private double _cpuTemp = 58;
    private double _gpuTemp = 64;
    private double _cpuUsage = 22;
    private double _gpuUsage = 78;
    private double _cpuClock = 4120;
    private double _ramUsed = 21400;
    private double _vramUsed = 7300;

    public string Key => SensorMergePolicy.DemoKey;
    public string Name => "Synthetic demo data";
    public string Category => "QA fixture";
    public bool IsAvailable { get; private set; } = true;
    public string? LastError => null;

    public void Initialize() => IsAvailable = true;

    public IReadOnlyList<SensorValue> Read(long timestampMs)
    {
        _cpuUsage = Drift(_cpuUsage, 22, 70, 12);
        _gpuUsage = Drift(_gpuUsage, 78, 100, 18);
        _cpuTemp = Drift(_cpuTemp, 58, 84, 1.5);
        _gpuTemp = Drift(_gpuTemp, 64, 82, 1.2);
        _cpuClock = 3900 + _cpuUsage * 12 + Noise(120);
        _ramUsed = Drift(_ramUsed, 21500, 24000, 40);
        _vramUsed = Drift(_vramUsed, 7000, 8200, 90);

        var values = new List<SensorValue>(48)
        {
            V(SensorIds.CpuTemperature, _cpuTemp, "°C", SensorKind.Temperature, timestampMs),
            V(SensorIds.CpuPackageTemperature, _cpuTemp + 1.5, "°C", SensorKind.Temperature, timestampMs),
            V(SensorIds.CpuUsage, _cpuUsage, "%", SensorKind.Load, timestampMs),
            V(SensorIds.CpuUsageMax, Math.Min(100, _cpuUsage + 28), "%", SensorKind.Load, timestampMs),
            V(SensorIds.CpuClock, _cpuClock, "MHz", SensorKind.Clock, timestampMs),
            V(SensorIds.CpuPower, 38 + _cpuUsage * 0.85, "W", SensorKind.Power, timestampMs),
            V(SensorIds.GpuTemperature, _gpuTemp, "°C", SensorKind.Temperature, timestampMs),
            V(SensorIds.GpuHotspot, _gpuTemp + 13, "°C", SensorKind.Temperature, timestampMs),
            V(SensorIds.GpuUsage, _gpuUsage, "%", SensorKind.Load, timestampMs),
            V(SensorIds.GpuClock, 1800 + _gpuUsage * 3.4, "MHz", SensorKind.Clock, timestampMs),
            V(SensorIds.GpuMemoryClock, 9000, "MHz", SensorKind.Clock, timestampMs),
            V(SensorIds.GpuPower, 60 + _gpuUsage * 1.15, "W", SensorKind.Power, timestampMs),
            V(SensorIds.GpuPowerLimit, 175, "W", SensorKind.Power, timestampMs),
            V(SensorIds.GpuMemoryControllerUsage, 46, "%", SensorKind.Load, timestampMs),
            V(SensorIds.VramTotal, 12282, "MB", SensorKind.Data, timestampMs),
            V(SensorIds.VramUsed, _vramUsed, "MB", SensorKind.Data, timestampMs),
            V(SensorIds.RamTotal, 65227, "MB", SensorKind.Data, timestampMs),
            V(SensorIds.RamUsed, _ramUsed, "MB", SensorKind.Data, timestampMs),
            V("cpu.threads", 24, string.Empty, SensorKind.Data, timestampMs),
        };

        for (var core = 0; core < 16; core++)
        {
            var offset = core % 4 == 0 ? 9 : 0;
            values.Add(V(SensorIds.CoreTemperature(core), _cpuTemp - 6 + offset + Noise(2), "°C", SensorKind.Temperature, timestampMs));
            values.Add(V(SensorIds.CoreUsage(core), Math.Clamp(_cpuUsage + Noise(18), 0, 100), "%", SensorKind.Load, timestampMs));
            values.Add(V(SensorIds.CoreClock(core), _cpuClock + Noise(150), "MHz", SensorKind.Clock, timestampMs));
        }

        return values;
    }

    private SensorValue V(string id, double value, string unit, SensorKind kind, long timestampMs)
        => new(id, Math.Max(0, value), unit, kind, SensorOrigin.Demo, "demo", timestampMs);

    private double Drift(double current, double min, double max, double step)
        => Math.Clamp(current + (double)(_random.NextDouble() - 0.5) * 2 * step, min, max);

    private double Noise(double amplitude) => (double)(_random.NextDouble() - 0.5) * 2 * amplitude;

    public IEnumerable<string> Describe()
    {
        yield return "Synthetic QA fixture (not real hardware)";
    }

    public void Dispose() => IsAvailable = false;
}
