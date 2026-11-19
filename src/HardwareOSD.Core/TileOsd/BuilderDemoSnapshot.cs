using HardwareOSD.Core.Fps;
using HardwareOSD.Core.Hardware;

namespace HardwareOSD.Core.TileOsd;

/// <summary>
/// Builder-only sample data for previewing a design when no live reading exists. Every value is
/// marked <see cref="SensorOrigin.Demo"/>, so it can never be mistaken for a hardware reading.
/// The overlay itself never uses this snapshot.
/// </summary>
public static class BuilderDemoSnapshot
{
    private const string Source = "Demo preview";

    public static SensorSnapshot Snapshot { get; } = CreateSnapshot();

    public static FpsSnapshot Fps { get; } = new(
        Sequence: 0,
        TimestampMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Status: FpsStatus.Capturing,
        Source: FpsSource.None,
        IsFallback: false,
        ProcessId: 0,
        ProcessName: "Demo preview",
        Fps: 144,
        FrameTimeMs: 6.94,
        OnePercentLowFps: 121,
        Message: "Demo values",
        IsProcessFallback: false);

    public static SensorHistory History { get; } = CreateHistory();

    private static SensorSnapshot CreateSnapshot()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var values = new Dictionary<string, SensorValue>(StringComparer.Ordinal);

        Add(values, SensorIds.CpuTemperature, 62, "°C", SensorKind.Temperature, now);
        Add(values, SensorIds.CpuPackageTemperature, 60, "°C", SensorKind.Temperature, now);
        Add(values, SensorIds.CpuUsage, 38, "%", SensorKind.Load, now);
        Add(values, SensorIds.CpuClock, 4200, "MHz", SensorKind.Clock, now);
        Add(values, SensorIds.CpuPower, 65, "W", SensorKind.Power, now);
        Add(values, SensorIds.CpuVoltage, 1.15, "V", SensorKind.Voltage, now);

        Add(values, SensorIds.GpuTemperature, 68, "°C", SensorKind.Temperature, now);
        Add(values, SensorIds.GpuHotspot, 78, "°C", SensorKind.Temperature, now);
        Add(values, SensorIds.GpuMemoryJunction, 72, "°C", SensorKind.Temperature, now);
        Add(values, SensorIds.GpuUsage, 74, "%", SensorKind.Load, now);
        Add(values, SensorIds.GpuClock, 2100, "MHz", SensorKind.Clock, now);
        Add(values, SensorIds.GpuMemoryClock, 10500, "MHz", SensorKind.Clock, now);
        Add(values, SensorIds.GpuPower, 180, "W", SensorKind.Power, now);
        Add(values, SensorIds.GpuPowerLimit, 175, "W", SensorKind.Power, now);

        Add(values, SensorIds.VramUsed, 6200, "MB", SensorKind.Data, now);
        Add(values, SensorIds.VramTotal, 12288, "MB", SensorKind.Data, now);
        Add(values, SensorIds.VramPercent, 50.5, "%", SensorKind.Load, now);

        Add(values, SensorIds.RamUsed, 18400, "MB", SensorKind.Data, now);
        Add(values, SensorIds.RamAvailable, 47136, "MB", SensorKind.Data, now);
        Add(values, SensorIds.RamTotal, 65536, "MB", SensorKind.Data, now);
        Add(values, SensorIds.RamPercent, 28.1, "%", SensorKind.Load, now);

        Add(values, SensorIds.Fps, 144, "FPS", SensorKind.Data, now);
        Add(values, SensorIds.FrameTime, 6.94, "ms", SensorKind.Data, now);

        return SensorSnapshot.Create(0, now, values);
    }

    private static void Add(Dictionary<string, SensorValue> values, string id, double value,
        string unit, SensorKind kind, long now)
        => values[id] = new SensorValue(id, value, unit, kind, SensorOrigin.Demo, Source, now);

    private static SensorHistory CreateHistory()
    {
        var history = new SensorHistory(180);
        for (var index = 0; index < 90; index++)
        {
            var phase = index / 89.0 * Math.PI * 4;
            history.AppendValue("frame.fps", 132 + 18 * Math.Sin(phase));
            history.AppendValue("frame.time", 6.9 + 0.7 * Math.Sin(phase + 1));
            history.AppendValue(SensorIds.CpuUsage, 34 + 14 * Math.Sin(phase + 0.5));
            history.AppendValue(SensorIds.CpuTemperature, 60 + 5 * Math.Sin(phase + 0.2));
            history.AppendValue(SensorIds.CpuClock, 3900 + 400 * Math.Sin(phase));
            history.AppendValue(SensorIds.CpuPower, 58 + 12 * Math.Sin(phase + 0.8));
            history.AppendValue(SensorIds.GpuUsage, 70 + 14 * Math.Sin(phase + 1.4));
            history.AppendValue(SensorIds.GpuTemperature, 66 + 5 * Math.Sin(phase + 0.9));
            history.AppendValue(SensorIds.GpuClock, 2000 + 160 * Math.Sin(phase + 0.4));
            history.AppendValue(SensorIds.GpuPower, 170 + 24 * Math.Sin(phase + 1.1));
            history.AppendValue(SensorIds.RamPercent, 27 + 3 * Math.Sin(phase + 0.6));
            history.AppendValue(SensorIds.VramPercent, 48 + 6 * Math.Sin(phase + 1.2));
        }

        return history;
    }
}
