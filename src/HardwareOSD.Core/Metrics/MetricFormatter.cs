using System.Globalization;
using HardwareOSD.Core.Fps;
using HardwareOSD.Core.Hardware;

namespace HardwareOSD.Core.Metrics;

public readonly record struct FormattedMetric(string Value, string Unit, MetricSeverity Severity, bool Available)
{
    public static readonly FormattedMetric Unavailable = new("N/A", string.Empty, MetricSeverity.Normal, false);
}

/// <summary>
/// Converts cached sensor values into display strings. Pure and side-effect free, so it can be
/// unit tested without any hardware and reused by the live preview and the overlay.
/// </summary>
public static class MetricFormatter
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static FormattedMetric Format(MetricDefinition definition, SensorSnapshot snapshot, FpsSnapshot? fps = null)
    {
        return definition.Id switch
        {
            "frame.fps" => FpsValue(fps),
            "frame.time" => FrameTime(fps),
            "frame.fps.1low" => OnePercentLow(fps),
            "cpu.temp" => Temperature(snapshot, SensorIds.CpuTemperature, definition),
            "cpu.temp.package" => Temperature(snapshot, SensorIds.CpuPackageTemperature, definition),
            "gpu.temp" => Temperature(snapshot, SensorIds.GpuTemperature, definition),
            "gpu.temp.hotspot" => Temperature(snapshot, SensorIds.GpuHotspot, definition),
            "gpu.temp.memory" => Temperature(snapshot, SensorIds.GpuMemoryJunction, definition),
            "cpu.usage" => Percent(snapshot, SensorIds.CpuUsage, definition),
            "cpu.usage.max" => Percent(snapshot, SensorIds.CpuUsageMax, definition),
            "thermalzone.temp" => Temperature(snapshot, "thermalzone.temp", definition),            "gpu.usage" => Percent(snapshot, SensorIds.GpuUsage, definition),
            "gpu.memcontroller" => Percent(snapshot, SensorIds.GpuMemoryControllerUsage, definition),
            "vram.percent" => Percent(snapshot, SensorIds.VramPercent, definition),
            "ram.percent" => Percent(snapshot, SensorIds.RamPercent, definition),
            "cpu.clock" => Megahertz(snapshot, SensorIds.CpuClock, definition, preferGigahertz: true),
            "gpu.clock" => Megahertz(snapshot, SensorIds.GpuClock, definition, preferGigahertz: false),
            "gpu.clock.memory" => Megahertz(snapshot, SensorIds.GpuMemoryClock, definition, preferGigahertz: false),
            "cpu.power" => Watt(snapshot, SensorIds.CpuPower, definition),
            "cpu.power.cores" => Watt(snapshot, SensorIds.CpuPowerCores, definition),
            "cpu.power.memory" => Watt(snapshot, SensorIds.CpuPowerMemory, definition),
            "cpu.power.gt" => Watt(snapshot, SensorIds.CpuPowerGt, definition),
            "cpu.voltage" => Voltage(snapshot, SensorIds.CpuVoltage, definition),
            "gpu.power" => Watt(snapshot, SensorIds.GpuPower, definition),
            "gpu.power.limit" => Watt(snapshot, SensorIds.GpuPowerLimit, definition),
            "cpu.fan.rpm" => FanChannels(snapshot, FanComponent.Cpu, FanMeasure.Rpm),
            "cpu.fan.percent" => FanChannels(snapshot, FanComponent.Cpu, FanMeasure.Percent),
            "gpu.fan.rpm" => FanChannels(snapshot, FanComponent.Gpu, FanMeasure.Rpm),
            "gpu.fan.percent" => FanChannels(snapshot, FanComponent.Gpu, FanMeasure.Percent),
            "vram.used" => UsedOfTotal(snapshot, SensorIds.VramUsed, SensorIds.VramTotal, definition, "MB", 0),
            "ram.used" => UsedOfTotal(snapshot, SensorIds.RamUsed, SensorIds.RamTotal, definition, "GB", 1, 1024.0),
            "ram.available" => UsedOfTotal(snapshot, SensorIds.RamAvailable, null, definition, "GB", 1, 1024.0),
            _ => FormattedMetric.Unavailable,
        };
    }

    private static FormattedMetric FpsValue(FpsSnapshot? fps)
    {
        if (fps is null || !fps.HasValue)
        {
            // FPS deliberately renders "--" rather than a fabricated rate.
            return new FormattedMetric("--", string.Empty, MetricSeverity.Normal, false);
        }

        var severity = fps.Fps < 15 ? MetricSeverity.Critical
            : fps.Fps < 30 ? MetricSeverity.Warning
            : MetricSeverity.Normal;

        // Presented timing derived in Automatic mode is marked so it can never be mistaken for
        // displayed-frame timing.
        var unit = fps.IsFallback ? "FPS·p" : "FPS";
        return new FormattedMetric(fps.Fps.ToString("0", Culture), unit, severity, true);
    }

    private static FormattedMetric FrameTime(FpsSnapshot? fps)
    {
        if (fps is null || !fps.HasValue || fps.FrameTimeMs <= 0)
        {
            return new FormattedMetric("--", string.Empty, MetricSeverity.Normal, false);
        }

        var severity = fps.FrameTimeMs > 66 ? MetricSeverity.Critical
            : fps.FrameTimeMs > 33 ? MetricSeverity.Warning
            : MetricSeverity.Normal;

        return new FormattedMetric(fps.FrameTimeMs.ToString("0.0", Culture), "ms", severity, true);
    }

    private static FormattedMetric OnePercentLow(FpsSnapshot? fps)
    {
        if (fps is null || !fps.HasValue || fps.OnePercentLowFps <= 0)
        {
            return new FormattedMetric("--", string.Empty, MetricSeverity.Normal, false);
        }

        return new FormattedMetric(fps.OnePercentLowFps.ToString("0", Culture), "FPS·1%", MetricSeverity.Normal, true);
    }

    private static FormattedMetric Temperature(SensorSnapshot snapshot, string id, MetricDefinition definition)    {
        if (!TryValue(snapshot, id, out var value))
        {
            return FormattedMetric.Unavailable;
        }

        var text = value.ToString("0", Culture);
        return new FormattedMetric(text, "°C", Severity(value, definition), true);
    }

    private static FormattedMetric Percent(SensorSnapshot snapshot, string id, MetricDefinition definition)
    {
        if (!TryValue(snapshot, id, out var value))
        {
            return FormattedMetric.Unavailable;
        }

        var text = value.ToString("0", Culture);
        return new FormattedMetric(text, "%", Severity(value, definition), true);
    }

    private static FormattedMetric Megahertz(SensorSnapshot snapshot, string id, MetricDefinition definition, bool preferGigahertz)
    {
        if (!TryValue(snapshot, id, out var value))
        {
            return FormattedMetric.Unavailable;
        }

        if ((preferGigahertz && value >= 1000) || (!preferGigahertz && value >= 10_000))
        {
            return new FormattedMetric((value / 1000.0).ToString("0.00", Culture), "GHz", MetricSeverity.Normal, true);
        }

        return new FormattedMetric(value.ToString("0", Culture), "MHz", MetricSeverity.Normal, true);
    }

    private static FormattedMetric Watt(SensorSnapshot snapshot, string id, MetricDefinition definition)
    {
        if (!TryValue(snapshot, id, out var value))
        {
            return FormattedMetric.Unavailable;
        }

        var text = value >= 100
            ? value.ToString("0", Culture)
            : value >= 1
                ? value.ToString("0.0", Culture)
                : value.ToString("0.000", Culture);
        return new FormattedMetric(text, "W", Severity(value, definition), true);
    }

    private static FormattedMetric Voltage(SensorSnapshot snapshot, string id, MetricDefinition definition)
    {
        if (!TryValue(snapshot, id, out var value))
        {
            return FormattedMetric.Unavailable;
        }

        return new FormattedMetric(value.ToString("0.000", Culture), "V",
            Severity(value, definition), true);
    }

    private static FormattedMetric FanChannels(
        SensorSnapshot snapshot,
        FanComponent component,
        FanMeasure measure)
    {
        var values = new List<double>();
        foreach (var channel in FanSensorReader.Channels(snapshot, component))
        {
            var value = measure == FanMeasure.Rpm ? channel.Rpm : channel.Percent;
            if (value is { } number && double.IsFinite(number))
            {
                values.Add(number);
            }
        }

        if (values.Count == 0)
        {
            return FormattedMetric.Unavailable;
        }

        // Every value is an individual physical fan reading. They are joined, never summed or
        // averaged, so two fans cannot masquerade as one fabricated number.
        var text = string.Join("/", values.Select(v => v.ToString("0", Culture)));
        return new FormattedMetric(
            text,
            measure == FanMeasure.Rpm ? "RPM" : "%",
            MetricSeverity.Normal,
            true);
    }

    private static FormattedMetric UsedOfTotal(
        SensorSnapshot snapshot,
        string usedId,
        string? totalId,
        MetricDefinition definition,
        string displayUnit,
        int decimals,
        double scale = 1.0)
    {
        if (!TryValue(snapshot, usedId, out var used))
        {
            return FormattedMetric.Unavailable;
        }

        var format = decimals > 0 ? "0." + new string('0', decimals) : "0";
        used /= scale;

        if (totalId is null)
        {
            return new FormattedMetric(used.ToString(format, Culture), displayUnit, MetricSeverity.Normal, true);
        }

        if (!TryValue(snapshot, totalId, out var total))
        {
            return FormattedMetric.Unavailable;
        }

        total /= scale;
        var severity = Severity(total > 0 ? used / total * 100 : 0, definition);
        return new FormattedMetric(
            used.ToString(format, Culture) + " / " + total.ToString(format, Culture),
            displayUnit,
            severity,
            true);
    }

    private static MetricSeverity Severity(double value, MetricDefinition definition)
    {
        if (definition.CriticalThreshold is { } critical && value >= critical)
        {
            return MetricSeverity.Critical;
        }

        if (definition.WarningThreshold is { } warning && value >= warning)
        {
            return MetricSeverity.Warning;
        }

        return MetricSeverity.Normal;
    }

    private static bool TryValue(SensorSnapshot snapshot, string id, out double value)
    {
        if (snapshot.TryGet(id, out var sensor) && sensor.Available && double.IsFinite(sensor.Value))
        {
            value = sensor.Value;
            return true;
        }

        value = 0;
        return false;
    }
}
