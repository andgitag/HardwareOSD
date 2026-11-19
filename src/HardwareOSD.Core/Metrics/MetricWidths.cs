namespace HardwareOSD.Core.Metrics;

/// <summary>
/// Generous worst-case text reserved for every metric family. The overlay renderer measures these
/// strings once and keeps the cell width fixed, so a reading moving from "45 W" to "105 W" or from
/// "9 %" to "100 %" cannot move neighbouring cells.
/// </summary>
public static class MetricWidths
{
    public readonly record struct Samples(string Value, string Unit);

    public static Samples For(MetricDefinition definition, string? customUnit = null)
    {
        var (value, unit) = definition.Id switch
        {
            "cpu.power" or "cpu.power.cores" or "cpu.power.memory" or "cpu.power.gt" or
                "gpu.power" or "gpu.power.limit" => ("1888.8", "W"),
            "cpu.voltage" => ("1.888", "V"),
            "cpu.clock" or "gpu.clock" or "gpu.clock.memory" => ("188888", "MHz"),
            "cpu.fan.rpm" or "gpu.fan.rpm" => ("18888/18888", "RPM"),
            "cpu.fan.percent" or "gpu.fan.percent" => ("188/188", "%"),
            "vram.used" => ("188888 / 188888", "MB"),
            "ram.used" => ("188.8 / 188.8", "GB"),
            "ram.available" => ("188.8", "GB"),
            "frame.fps" => ("1888", "FPS\u00B7p"),
            "frame.fps.1low" => ("1888", "FPS\u00B71%"),
            "frame.time" => ("188.8", "ms"),
            "cpu.temp" or "cpu.temp.package" or "gpu.temp" or "gpu.temp.hotspot" or
                "gpu.temp.memory" or "thermalzone.temp" => ("188", "\u00B0C"),
            "cpu.usage" or "cpu.usage.max" or "gpu.usage" or "gpu.memcontroller" or
                "vram.percent" or "ram.percent" => ("188", "%"),
            _ => ("188888.8", definition.Unit),
        };

        var requestedUnit = string.IsNullOrWhiteSpace(customUnit) ? definition.Unit : customUnit!;
        if (requestedUnit.Length > unit.Length)
        {
            unit = requestedUnit;
        }

        // Never reserve less than the catalog's own sample, even for a new metric added later.
        if (definition.SampleValue.Length > value.Length)
        {
            value = definition.SampleValue;
        }

        if (definition.SampleUnit.Length > unit.Length)
        {
            unit = definition.SampleUnit;
        }

        return new Samples(value, unit);
    }

    /// <summary>Generic sample for a custom value with no metric definition.</summary>
    public static Samples Generic(string? unit)
        => new("188888.8", string.IsNullOrWhiteSpace(unit) ? string.Empty : unit!);
}
