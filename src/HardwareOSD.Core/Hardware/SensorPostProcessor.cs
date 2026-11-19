namespace HardwareOSD.Core.Hardware;

/// <summary>
/// Validates raw readings and derives aggregate metrics. All operations are pure and allocation light.
/// Invalid values are converted to explicit "unavailable" entries so the UI can show N/A rather than
/// a fabricated number.
/// </summary>
public static class SensorPostProcessor
{
    public static void Sanitize(Dictionary<string, SensorValue> values, long timestampMs)
    {
        foreach (var key in values.Keys.ToArray())
        {
            var value = values[key];
            if (!value.Available)
            {
                continue;
            }

            if (!double.IsFinite(value.Value) || !IsPlausible(value))
            {
                var note = string.IsNullOrWhiteSpace(value.Note)
                    ? $"{SensorNotes.ReadingRejected}: raw {value.Value:0.###} {value.Unit}".TrimEnd()
                    : value.Note;
                values[key] = SensorValue.Unavailable(value.Id, value.Kind, value.Source, timestampMs, note);
                continue;
            }

            if (value.Kind == SensorKind.Load && value.Value is < 0 or > 100)
            {
                values[key] = new SensorValue(
                    value.Id,
                    Math.Clamp(value.Value, 0, 100),
                    value.Unit,
                    value.Kind,
                    value.Origin,
                    value.Source,
                    value.TimestampMs,
                    true,
                    value.Note);
            }
        }
    }

    private static bool IsPlausible(SensorValue value) => value.Kind switch
    {
        // LibreHardwareMonitor reports 0 W when MSR access is unavailable; a CPU/GPU never
        // idles at a true 0.00 W while the machine is running, so treat it as "unknown".
        // Windows EMI can report a real sub-50 mW GT/DRAM domain; keep positive Windows API
        // power values down to the milliwatt range, while still rejecting exact zero.
        SensorKind.Power => value.Value > (value.Origin == SensorOrigin.WindowsApi ? 0.0 : 0.05)
                            && value.Value < 3000,
        SensorKind.Temperature => value.Value is > -20 and < 160,
        SensorKind.Voltage => value.Value is > 0.05 and < 2.0,
        SensorKind.Clock => value.Value is >= 0 and < 100_000,
        SensorKind.Load => value.Value is >= -0.01 and <= 1000,
        SensorKind.Fan => value.Value >= 0 && IsPlausibleFanReading(value),
        _ => true,
    };

    private static bool IsPlausibleFanReading(SensorValue value)
    {
        // Percentage fan readings are usually 0-100; NVML may report slightly above 100 for
        // high-speed modes. Anything beyond 150 is not a usable fan percentage.
        if (value.Unit.Contains('%'))
        {
            return value.Value <= 150;
        }

        // Tachometer readings are RPM. Zero is valid for a stopped fan; 50k is beyond any desktop
        // or laptop fan and almost certainly a decoding/unit error.
        return value.Value < 50_000;
    }

    /// <summary>Adds derived metrics without overwriting real readings.</summary>
    public static void Derive(Dictionary<string, SensorValue> values, long timestampMs, bool useThermalZoneAsCpuTemperature = true)
    {
        // Driver quirks: a single NVML/NVAPI sample can report a power draw far above the card's
        // enforced limit (observed after resuming from sleep). Such a reading is not a measurement.
        if (values.TryGetValue(SensorIds.GpuPower, out var gpuPower) &&
            gpuPower.Available &&
            values.TryGetValue(SensorIds.GpuPowerLimit, out var gpuLimit) &&
            gpuLimit.Available &&
            gpuLimit.Value > 10 &&
            gpuPower.Value > gpuLimit.Value * 1.3)
        {
            values[SensorIds.GpuPower] = SensorValue.Unavailable(
                SensorIds.GpuPower,
                SensorKind.Power,
                gpuPower.Source,
                timestampMs,
                $"{SensorNotes.PowerAboveLimit}: {gpuPower.Value:0} W > {gpuLimit.Value:0} W");
        }

        // VRAM used = total - free when the provider only exposes free memory.
        if (!values.TryGetValue(SensorIds.VramTotal, out var total) || !total.Available)
        {
            total = FindAvailable(values, SensorIds.VramTotal);
        }

        if (!IsAvailable(values, SensorIds.VramUsed) && IsAvailable(values, SensorIds.VramTotal))
        {
            var t = values[SensorIds.VramTotal].Value;
            if (values.TryGetValue("gpu.mem.free", out var free) && free.Available && free.Value <= t)
            {
                Set(values, SensorIds.VramUsed, t - free.Value, "MB", SensorKind.Data, SensorOrigin.Derived, free.Source, timestampMs);
            }
        }

        if (IsAvailable(values, SensorIds.VramUsed) && IsAvailable(values, SensorIds.VramTotal))
        {
            var used = values[SensorIds.VramUsed].Value;
            var tot = values[SensorIds.VramTotal].Value;
            if (tot > 0)
            {
                Set(values, SensorIds.VramPercent, Math.Clamp(used / tot * 100.0, 0, 100), "%",
                    SensorKind.Load, SensorOrigin.Derived, "derived", timestampMs);
            }
        }

        if (IsAvailable(values, SensorIds.RamUsed) && IsAvailable(values, SensorIds.RamTotal))
        {
            var used = values[SensorIds.RamUsed].Value;
            var tot = values[SensorIds.RamTotal].Value;
            if (tot > 0)
            {
                Set(values, SensorIds.RamPercent, Math.Clamp(used / tot * 100.0, 0, 100), "%",
                    SensorKind.Load, SensorOrigin.Derived, "derived", timestampMs);
            }

            if (!IsAvailable(values, SensorIds.RamAvailable))
            {
                Set(values, SensorIds.RamAvailable, Math.Max(0, tot - used), values[SensorIds.RamUsed].Unit,
                    SensorKind.Data, SensorOrigin.Derived, "derived", timestampMs);
            }
        }

        // CPU package temperature fallback: average of per-core temperatures.
        if (!IsAvailable(values, SensorIds.CpuTemperature))
        {
            var package = values.TryGetValue(SensorIds.CpuPackageTemperature, out var p) && p.Available ? p : (SensorValue?)null;
            if (package is { Available: true })
            {
                Set(values, SensorIds.CpuTemperature, package.Value.Value, "°C", SensorKind.Temperature,
                    package.Value.Origin, package.Value.Source, timestampMs);
            }
            else
            {
                var avg = AverageCore(values, SensorIds.CoreSuffixTemperature);
                if (!double.IsNaN(avg))
                {
                    Set(values, SensorIds.CpuTemperature, avg, "°C", SensorKind.Temperature,
                        SensorOrigin.Derived, "derived", timestampMs);
                }
            }
        }

        // Last resort for non-elevated systems: the documented ACPI thermal zone counter.
        // Tagged with its real source so it is never mistaken for an MSR package sensor.
        if (!IsAvailable(values, SensorIds.CpuTemperature) &&
            useThermalZoneAsCpuTemperature &&
            values.TryGetValue("thermalzone.temp", out var zone) &&
            zone.Available)
        {
            Set(values, SensorIds.CpuTemperature, zone.Value, "°C", SensorKind.Temperature,
                SensorOrigin.WindowsApi, "PDH ACPI thermal zone", timestampMs);
        }

        if (!IsAvailable(values, SensorIds.CpuClock))
        {
            var avg = AverageCore(values, SensorIds.CoreSuffixClock);
            if (!double.IsNaN(avg) && avg > 0)
            {
                Set(values, SensorIds.CpuClock, avg, "MHz", SensorKind.Clock, SensorOrigin.Derived, "derived", timestampMs);
            }
        }

        if (!IsAvailable(values, SensorIds.CpuUsage))
        {
            var avg = AverageCore(values, SensorIds.CoreSuffixUsage);
            if (!double.IsNaN(avg))
            {
                Set(values, SensorIds.CpuUsage, avg, "%", SensorKind.Load, SensorOrigin.Derived, "derived", timestampMs);
            }
        }

        // The library's "CPU Core Max" sensor can lag or disagree with its own per-core values;
        // derive the busiest-core figure from the core sensors instead.
        if (TryMaxCore(values, SensorIds.CoreSuffixUsage, out var busiestCore))
        {
            values[SensorIds.CpuUsageMax] = new SensorValue(
                SensorIds.CpuUsageMax,
                busiestCore,
                "%",
                SensorKind.Load,
                SensorOrigin.Derived,
                "derived from per-core load",
                timestampMs);
        }
    }

    private static SensorValue FindAvailable(Dictionary<string, SensorValue> values, string id)
        => values.TryGetValue(id, out var v) ? v : default;

    private static bool IsAvailable(Dictionary<string, SensorValue> values, string id)
        => values.TryGetValue(id, out var v) && v.Available;

    private static void Set(
        Dictionary<string, SensorValue> values,
        string id,
        double value,
        string unit,
        SensorKind kind,
        SensorOrigin origin,
        string source,
        long timestampMs)
    {
        if (values.TryGetValue(id, out var existing) && existing.Available)
        {
            return;
        }

        values[id] = new SensorValue(id, value, unit, kind, origin, source, timestampMs);
    }

    private static double AverageCore(Dictionary<string, SensorValue> values, string suffix)
    {
        var sum = 0.0;
        var count = 0;
        foreach (var pair in values)
        {
            if (pair.Value.Available &&
                SensorIds.TryParseCoreIndex(pair.Key, suffix, out _) &&
                double.IsFinite(pair.Value.Value))
            {
                sum += pair.Value.Value;
                count++;
            }
        }

        return count == 0 ? double.NaN : sum / count;
    }

    private static bool TryMaxCore(Dictionary<string, SensorValue> values, string suffix, out double maximum)
    {
        maximum = double.MinValue;
        foreach (var pair in values)
        {
            if (pair.Value.Available &&
                SensorIds.TryParseCoreIndex(pair.Key, suffix, out _) &&
                double.IsFinite(pair.Value.Value))
            {
                maximum = Math.Max(maximum, pair.Value.Value);
            }
        }

        if (maximum < 0)
        {
            return false;
        }

        maximum = Math.Clamp(maximum, 0, 100);
        return true;
    }
}
