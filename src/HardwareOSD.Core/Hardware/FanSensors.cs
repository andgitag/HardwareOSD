namespace HardwareOSD.Core.Hardware;

/// <summary>Physical role of a fan channel. Pumps are deliberately separate from fans.</summary>
public enum FanComponent
{
    Cpu = 0,
    Gpu = 1,
    System = 2,
    Pump = 3,
    Other = 4,
}

public enum FanMeasure
{
    Rpm = 0,
    Percent = 1,
}

/// <summary>
/// Stable identifiers for individual fan channels. These are separate from user-facing metric ids
/// such as <c>cpu.fan.rpm</c>; the metric aggregates whichever CPU/GPU channels exist.
/// </summary>
public static class FanSensorIds
{
    public const string CpuPrefix = "fan.cpu.";
    public const string GpuPrefix = "fan.gpu.";
    public const string SystemPrefix = "fan.system.";
    public const string PumpPrefix = "fan.pump.";
    public const string OtherPrefix = "fan.other.";

    public static string Id(FanComponent component, int index, FanMeasure measure)
        => $"{Prefix(component)}{index}.{MeasureKey(measure)}";

    public static bool TryParse(string? id, out FanComponent component, out int index, out FanMeasure measure)
    {
        component = FanComponent.Other;
        index = -1;
        measure = FanMeasure.Rpm;
        if (string.IsNullOrWhiteSpace(id) || !id.StartsWith("fan.", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = id.Split('.');
        if (parts.Length != 4 || parts[0] != "fan" || !int.TryParse(parts[2], out index) || index < 0)
        {
            return false;
        }

        switch (parts[1])
        {
            case "cpu":
                component = FanComponent.Cpu;
                break;
            case "gpu":
                component = FanComponent.Gpu;
                break;
            case "system":
                component = FanComponent.System;
                break;
            case "pump":
                component = FanComponent.Pump;
                break;
            case "other":
                component = FanComponent.Other;
                break;
            default:
                return false;
        }

        switch (parts[3])
        {
            case "rpm":
                measure = FanMeasure.Rpm;
                return true;
            case "percent":
                measure = FanMeasure.Percent;
                return true;
            default:
                return false;
        }
    }

    public static bool IsFanSensor(string? id) => TryParse(id, out _, out _, out _);

    public static string ComponentLabel(FanComponent component) => component switch
    {
        FanComponent.Cpu => "CPU",
        FanComponent.Gpu => "GPU",
        FanComponent.System => "System",
        FanComponent.Pump => "Pump",
        _ => "Other",
    };

    public static string Prefix(FanComponent component) => component switch
    {
        FanComponent.Cpu => CpuPrefix,
        FanComponent.Gpu => GpuPrefix,
        FanComponent.System => SystemPrefix,
        FanComponent.Pump => PumpPrefix,
        _ => OtherPrefix,
    };

    public static string ComponentKey(FanComponent component) => component switch
    {
        FanComponent.Cpu => "cpu",
        FanComponent.Gpu => "gpu",
        FanComponent.System => "system",
        FanComponent.Pump => "pump",
        _ => "other",
    };

    private static string MeasureKey(FanMeasure measure) => measure == FanMeasure.Percent ? "percent" : "rpm";
}

/// <summary>
/// Maps vendor/library sensor names to a physical role without guessing from channel numbers.
/// A generic "Fan #1" remains Other; it is not silently promoted to CPU.
/// </summary>
public static class FanClassifier
{
    public static FanComponent Classify(string sensorName, bool gpuHardware = false)
    {
        var name = sensorName?.Trim().ToLowerInvariant() ?? string.Empty;

        // Pumps often appear next to fan channels on the same SuperIO or AIO controller.
        if (name.Contains("pump", StringComparison.Ordinal) ||
            name.Contains("water block", StringComparison.Ordinal) ||
            name.Contains("liquid cooling", StringComparison.Ordinal))
        {
            return FanComponent.Pump;
        }

        if (gpuHardware)
        {
            return FanComponent.Gpu;
        }

        // A motherboard "GPU fan" header is a chassis/cooling header, not GPU telemetry.
        if (name.Contains("cpu", StringComparison.Ordinal) ||
            name.Contains("processor", StringComparison.Ordinal))
        {
            return FanComponent.Cpu;
        }

        if (name.Contains("system", StringComparison.Ordinal) ||
            name.Contains("chassis", StringComparison.Ordinal) ||
            name.Contains("case", StringComparison.Ordinal) ||
            name.Contains("aux", StringComparison.Ordinal) ||
            name.Contains("mos", StringComparison.Ordinal) ||
            name.Contains("vrm", StringComparison.Ordinal) ||
            name.Contains("chipset", StringComparison.Ordinal) ||
            name.Contains("pch", StringComparison.Ordinal) ||
            name.Contains("gpu", StringComparison.Ordinal))
        {
            return FanComponent.System;
        }

        return FanComponent.Other;
    }
}

/// <summary>One physical fan channel, with an optional real RPM and/or percentage reading.</summary>
public sealed record FanChannel(
    FanComponent Component,
    int Index,
    string Name,
    string Hardware,
    string Source,
    double? Rpm,
    double? Percent,
    bool Available,
    long TimestampMs,
    string? Note);

/// <summary>Reads fan channels from an immutable snapshot without touching hardware.</summary>
public static class FanSensorReader
{
    public static IReadOnlyList<FanChannel> Channels(SensorSnapshot snapshot)
        => BuildChannels(snapshot, component: null);

    public static IReadOnlyList<FanChannel> Channels(SensorSnapshot snapshot, FanComponent component)
        => BuildChannels(snapshot, component);

    private static IReadOnlyList<FanChannel> BuildChannels(SensorSnapshot snapshot, FanComponent? component)
    {
        var channels = new Dictionary<(FanComponent Component, int Index), ChannelAccumulator>();
        foreach (var pair in snapshot.Values)
        {
            if (!FanSensorIds.TryParse(pair.Key, out var parsedComponent, out var index, out var measure) ||
                component is { } wanted && parsedComponent != wanted)
            {
                continue;
            }

            if (!channels.TryGetValue((parsedComponent, index), out var accumulator))
            {
                accumulator = new ChannelAccumulator(parsedComponent, index);
                channels[(parsedComponent, index)] = accumulator;
            }

            accumulator.Add(pair.Value, measure);
        }

        var result = new List<FanChannel>(channels.Count);
        foreach (var accumulator in channels.Values)
        {
            result.Add(accumulator.ToChannel());
        }

        result.Sort((left, right) =>
        {
            var componentOrder = left.Component.CompareTo(right.Component);
            return componentOrder != 0 ? componentOrder : left.Index.CompareTo(right.Index);
        });
        return result;
    }

    private sealed class ChannelAccumulator(FanComponent component, int index)
    {
        private double? _rpm;
        private double? _percent;
        private string? _rpmNote;
        private string? _percentNote;
        private long _timestamp;
        private string _source = string.Empty;
        private string _name = string.Empty;
        private string _hardware = string.Empty;

        public void Add(SensorValue value, FanMeasure measure)
        {
            if (value.Available && double.IsFinite(value.Value))
            {
                if (measure == FanMeasure.Rpm)
                {
                    _rpm = value.Value;
                    _rpmNote = null;
                }
                else
                {
                    _percent = value.Value;
                    _percentNote = null;
                }

                _timestamp = Math.Max(_timestamp, value.TimestampMs);
            }
            else if (!string.IsNullOrWhiteSpace(value.Note))
            {
                if (measure == FanMeasure.Rpm)
                {
                    _rpmNote = value.Note;
                }
                else
                {
                    _percentNote = value.Note;
                }
            }

            if (!string.IsNullOrWhiteSpace(value.Source))
            {
                _source = value.Source;
                var separator = value.Source.LastIndexOf(": ", StringComparison.Ordinal);
                if (separator > 0)
                {
                    _hardware = value.Source[..separator];
                    _name = value.Source[(separator + 2)..].Trim();
                    var periodMarker = _name.IndexOf(" (period ", StringComparison.Ordinal);
                    if (periodMarker > 0)
                    {
                        _name = _name[..periodMarker].Trim();
                    }
                }
                else
                {
                    _hardware = value.Source;
                }
            }

            if (string.IsNullOrWhiteSpace(_name))
            {
                _name = $"Fan {index + 1}";
            }
        }

        public FanChannel ToChannel()
        {
            var available = _rpm.HasValue || _percent.HasValue;
            string? note = null;
            if (available && !_rpm.HasValue)
            {
                note = _rpmNote ?? SensorNotes.FanPercentOnly;
            }
            else if (available && !_percent.HasValue)
            {
                note = _percentNote ?? SensorNotes.FanRpmOnly;
            }
            else if (!available)
            {
                note = _rpmNote ?? _percentNote ?? "unavailable";
            }

            return new FanChannel(
                component,
                index,
                _name,
                _hardware,
                _source,
                _rpm,
                _percent,
                available,
                _timestamp,
                note);
        }
    }
}
