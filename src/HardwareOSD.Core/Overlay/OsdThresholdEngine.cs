using HardwareOSD.Core.Config;
using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.Metrics;

namespace HardwareOSD.Core.Overlay;

public readonly record struct OsdThresholdDecision(
    OsdColorRole Role,
    double? Warning,
    double? Critical,
    OsdThresholdDirection Direction,
    bool AutoEnabled,
    bool LimitKnown);

/// <summary>
/// Universal threshold evaluator used by the adaptive style and by any element that opts into
/// automatic colours. It never invents a device power limit: GPU power can use the real NVML
/// limit, CPU power stays fixed unless the user supplies explicit thresholds.
/// </summary>
public static class OsdThresholdEngine
{
    public static OsdThresholdDecision Evaluate(
        OsdElement? element,
        string sensorId,
        double value,
        SensorSnapshot snapshot,
        MetricDefinition? definition)
    {
        var auto = element?.AutoColor ?? false;
        var direction = element?.Direction ??
                        (sensorId.StartsWith("frame.", StringComparison.Ordinal)
                            ? OsdThresholdDirection.LowerIsWorse
                            : OsdThresholdDirection.HigherIsWorse);

        double? warning = element?.WarningThreshold;
        double? critical = element?.CriticalThreshold;

        if (auto && warning is null && critical is null)
        {
            if (sensorId is "gpu.power")
            {
                if (snapshot.TryGet(SensorIds.GpuPowerLimit, out var limit) &&
                    limit.Available && limit.Value > 0)
                {
                    warning = limit.Value * 0.75;
                    critical = limit.Value;
                }
            }
            else if (sensorId is "frame.fps" or "frame.fps.1low")
            {
                warning = 60;
                critical = 30;
                direction = OsdThresholdDirection.LowerIsWorse;
            }
            else if (sensorId.EndsWith(".usage", StringComparison.Ordinal) ||
                     sensorId.EndsWith(".percent", StringComparison.Ordinal) ||
                     sensorId is "gpu.memcontroller")
            {
                warning = 70;
                critical = 90;
            }
            else
            {
                warning = definition?.WarningThreshold;
                critical = definition?.CriticalThreshold;
            }
        }

        if (!auto || (warning is null && critical is null) || !double.IsFinite(value))
        {
            return new OsdThresholdDecision(
                OsdColorRole.Normal,
                warning,
                critical,
                direction,
                auto,
                sensorId is "gpu.power" &&
                snapshot.TryGet(SensorIds.GpuPowerLimit, out var knownLimit) &&
                knownLimit.Available && knownLimit.Value > 0);
        }

        var role = direction == OsdThresholdDirection.LowerIsWorse
            ? value <= (critical ?? double.NegativeInfinity)
                ? OsdColorRole.Critical
                : value <= (warning ?? double.NegativeInfinity)
                    ? OsdColorRole.Warning
                    : OsdColorRole.Normal
            : value >= (critical ?? double.PositiveInfinity)
                ? OsdColorRole.Critical
                : value >= (warning ?? double.PositiveInfinity)
                    ? OsdColorRole.Warning
                    : OsdColorRole.Normal;

        return new OsdThresholdDecision(
            role,
            warning,
            critical,
            direction,
            auto,
            sensorId is "gpu.power" &&
            snapshot.TryGet(SensorIds.GpuPowerLimit, out var gpuLimit) &&
            gpuLimit.Available && gpuLimit.Value > 0);
    }
}
