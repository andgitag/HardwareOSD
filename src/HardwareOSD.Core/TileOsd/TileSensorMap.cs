using System.Globalization;
using System.Text.RegularExpressions;
using HardwareOSD.Core.Fps;
using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.Metrics;

namespace HardwareOSD.Core.TileOsd;

/// <summary>Names and session values that are not sensors but are real system data.</summary>
public sealed record TileRenderContext(
    string? GpuName = null,
    string? CpuName = null,
    string? GpuDriver = null,
    string? GraphicsApi = null,
    long SessionStartMs = 0,
    long NowMs = 0,
    string? GpuVendor = null,
    string? CpuVendor = null,
    string? WarningColor = null,
    string? CriticalColor = null)
{
    public static readonly TileRenderContext Empty = new();

    public string GpuNameShort => ShortenGpu(GpuName);
    public string CpuNameShort => ShortenCpu(CpuName);

    public string SessionText
    {
        get
        {
            if (SessionStartMs <= 0 || NowMs <= SessionStartMs)
            {
                return "00:00:00";
            }

            var elapsed = TimeSpan.FromMilliseconds(NowMs - SessionStartMs);
            return $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        }
    }

    /// <summary>
    /// Expands the machine-fact tokens used by static captions in the saved designs. Unknown
    /// tokens are left untouched so a literal brace in a design is never silently lost.
    /// </summary>
    public string Expand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.IndexOf('{') < 0)
        {
            return text ?? string.Empty;
        }

        return text
            .Replace("{gpuName}", GpuName ?? "N/A", StringComparison.OrdinalIgnoreCase)
            .Replace("{gpuShort}", GpuNameShort, StringComparison.OrdinalIgnoreCase)
            .Replace("{gpuVendor}", Vendor(GpuVendor, GpuName) ?? "GPU", StringComparison.OrdinalIgnoreCase)
            .Replace("{cpuName}", CpuName ?? "N/A", StringComparison.OrdinalIgnoreCase)
            .Replace("{cpuShort}", CpuNameShort, StringComparison.OrdinalIgnoreCase)
            .Replace("{cpuVendor}", Vendor(CpuVendor, CpuName) ?? "CPU", StringComparison.OrdinalIgnoreCase)
            .Replace("{gpuDriver}", GpuDriver ?? "N/A", StringComparison.OrdinalIgnoreCase)
            .Replace("{graphicsApi}", GraphicsApi ?? "N/A", StringComparison.OrdinalIgnoreCase)
            .Replace("{session}", SessionText, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Vendor(string? vendor, string? name)
    {
        var text = string.IsNullOrWhiteSpace(vendor) ? name : vendor;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (text.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Advanced Micro", StringComparison.OrdinalIgnoreCase))
        {
            return "AMD";
        }

        if (text.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Arc", StringComparison.OrdinalIgnoreCase))
        {
            return "INTEL";
        }

        if (text.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("GeForce", StringComparison.OrdinalIgnoreCase))
        {
            return "NVIDIA";
        }

        return text.Trim();
    }

    private static string ShortenGpu(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "N/A";
        }

        var text = Clean(value);
        foreach (var token in new[] { "NVIDIA", "GeForce", "AMD", "Radeon", "Intel", "Arc", "Graphics" })
        {
            text = text.Replace(token, string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        text = text.Replace("GPU", string.Empty, StringComparison.OrdinalIgnoreCase);
        text = Collapse(text);
        return text.Length <= 30 ? text : text[..30].TrimEnd() + "…";
    }

    private static string ShortenCpu(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "N/A";
        }

        var text = Clean(value);
        foreach (var token in new[] { "Intel", "AMD", "Core", "Ryzen", "Processor" })
        {
            text = text.Replace(token, string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"\b\d{1,2}(st|nd|rd|th)\s+Gen\b", string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = Collapse(text);
        return text.Length <= 30 ? text : text[..30].TrimEnd() + "…";
    }

    private static string Clean(string value)
        => value.Trim()
            .Replace("(R)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", string.Empty, StringComparison.OrdinalIgnoreCase);

    private static string Collapse(string value)
        => System.Text.RegularExpressions.Regex.Replace(value, @"\s{2,}", " ").Trim();

    private static string Shorten(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "N/A";
        }

        var text = Clean(value);
        text = Collapse(text);
        return text.Length <= 30 ? text : text[..30].TrimEnd() + "…";
    }
}

/// <summary>One resolved tile reading. Text readings carry no numeric value.</summary>
public readonly record struct TileReading(
    string Text,
    string Unit,
    bool Available,
    double Numeric,
    bool IsText,
    string? SensorId,
    MetricSeverity Severity = MetricSeverity.Normal)
{
    public static TileReading Unavailable(string unit = "") =>
        new("N/A", unit, false, double.NaN, false, null);

    public static TileReading TextValue(string text, string unit = "") =>
        new(text, unit, true, double.NaN, true, null);
}

/// <summary>
/// Maps CapFrameX tile keys to HardwareOSD sensors. The map is deliberately explicit: a key that
/// has no real source returns <see cref="TileReading.Unavailable"/> instead of a fabricated number.
/// Core-type roles use the reference machine's 8P+8E logical layout (P 0..7, E 8..15, LPE 16..19);
/// sensors outside the installed CPU simply resolve unavailable.
/// </summary>
public static class TileSensorMap
{
    private static readonly Regex CoreRole = new(
        @"^role:cpu-(loads|clocks|effective-clocks|temperatures)(?:\|(P|E|LPE|D|LP))?/(\d+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex CoreSensor = new(
        @"/load/Core #(\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static TileReading Resolve(
        string? key,
        SensorSnapshot snapshot,
        FpsSnapshot? fps,
        SensorHistory? history,
        TileRenderContext? context = null)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return TileReading.Unavailable();
        }

        context ??= TileRenderContext.Empty;
        var raw = key.Trim();

        // Heatmap keys are averaged family members: "avg:10:role:cpu-loads/3".
        if (raw.StartsWith("avg:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = raw.Split(':', 3);
            if (parts.Length == 3)
            {
                raw = parts[2];
            }
        }

        // CapFrameX numeric source keys and semantic aliases.
        switch (raw)
        {
            case "fpsCurrent":
                return FrameNumber(fps?.Fps, fps, "FPS");
            case "fps":
                return AverageFrame(history, "frame.fps", fps?.Fps, "FPS");
            case "frametimeCurrent":
                return FrameNumber(fps?.FrameTimeMs, fps, "ms");
            case "frametime":
                return AverageFrame(history, "frame.time", fps?.FrameTimeMs, "ms");
            case "fps1pctLow":
                return FrameNumber(fps?.OnePercentLowFps, fps, "FPS");
            case "fps0dot1pctLow":
                return TileReading.Unavailable("FPS");
            case "stuttering":
                return Stutter(history);
            case "gpuLoad":
                return Sensor(snapshot, SensorIds.GpuUsage, "%");
            case "gpuTemp":
                return Sensor(snapshot, SensorIds.GpuTemperature, "°C");
            case "gpuPower":
                return Sensor(snapshot, SensorIds.GpuPower, "W");
            case "gpuClock":
                return Sensor(snapshot, SensorIds.GpuClock, "MHz");
            case "gpuMemoryClock":
                return Sensor(snapshot, SensorIds.GpuMemoryClock, "MHz");
            case "gpuHotSpot":
                return Sensor(snapshot, SensorIds.GpuHotspot, "°C");
            case "gpuFan":
            case "gpuFanRpm":
                return Fan(snapshot, MetricCatalog.Find("gpu.fan.rpm"));
            case "gpuFanPercent":
                return Fan(snapshot, MetricCatalog.Find("gpu.fan.percent"));
            case "cpuFan":
            case "cpuFanRpm":
                return Fan(snapshot, MetricCatalog.Find("cpu.fan.rpm"));
            case "cpuFanPercent":
                return Fan(snapshot, MetricCatalog.Find("cpu.fan.percent"));
            case "vramUsed":
                return Sensor(snapshot, SensorIds.VramUsed, "MB");
            case "vramTotal":
                return Sensor(snapshot, SensorIds.VramTotal, "MB");
            case "vramLoad":
                return Sensor(snapshot, SensorIds.VramPercent, "%");
            case "vramFree":
                return Difference(snapshot, SensorIds.VramTotal, SensorIds.VramUsed, "MB");
            case "vramTemp":
                return Sensor(snapshot, SensorIds.GpuMemoryJunction, "°C");
            case "vramTempSpread":
                return TileReading.Unavailable("°C");
            case "cpuLoad":
                return Sensor(snapshot, SensorIds.CpuUsage, "%");
            case "cpuTemp":
                return Sensor(snapshot, SensorIds.CpuTemperature, "°C");
            case "cpuPower":
                return Sensor(snapshot, SensorIds.CpuPower, "W");
            case "cpuClock":
                return Sensor(snapshot, SensorIds.CpuClock, "MHz");
            case "cpuPCoreClock":
            case "cpuECoreClock":
                return MaxCoreClock(snapshot);
            case "ramUsed":
                return Sensor(snapshot, SensorIds.RamUsed, "MB");
            case "ramTotal":
                return Sensor(snapshot, SensorIds.RamTotal, "MB");
            case "ramFree":
                return Difference(snapshot, SensorIds.RamTotal, SensorIds.RamUsed, "MB");
            case "ramLoad":
                return Sensor(snapshot, SensorIds.RamPercent, "%");
            case "pcLatency":
                return TileReading.Unavailable("ms");
            case "gpuName":
                return Name(context.GpuName);
            case "gpuNameShort":
                return Name(context.GpuNameShort);
            case "cpuName":
                return Name(context.CpuName);
            case "cpuNameShort":
                return Name(context.CpuNameShort);
            case "gpuDriver":
                return Name(context.GpuDriver);
            case "graphicsApi":
                return Name(context.GraphicsApi);
            case "captureTimer":
                return TileReading.TextValue(context.SessionText);
            case "metric/CustomGPU":
                return Name(context.GpuName);
            case "metric/CustomCPU":
                return Name(context.CpuName);
            case "metric/Framerate":
                return FrameNumber(fps?.Fps, fps, "FPS");
            case "metric/OnlineAverage":
                return AverageFrame(history, "frame.fps", fps?.Fps, "FPS");
            case "metric/Online1PercentLow":
                return FrameNumber(fps?.OnePercentLowFps, fps, "FPS");
            case "metric/CaptureTimer":
                return TileReading.TextValue(context.SessionText);
            case "metric/OnlineCpuActiveTimeAverage":
            case "metric/OnlineGpuActiveTimeAverage":
            case "cpuActiveTime":
            case "gpuActiveTime":
                return TileReading.Unavailable("ms");
        }

        if (raw.StartsWith("vramChipTemp", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "vramTemp", StringComparison.OrdinalIgnoreCase))
        {
            return Sensor(snapshot, SensorIds.GpuMemoryJunction, "°C");
        }

        // Every HardwareOSD catalogue metric can also be bound directly. This keeps the Tile
        // Builder on the same registry as Metrics and lets migrated row profiles use fan, power
        // limit and other metrics that have no CapFrameX semantic alias.
        if (raw.StartsWith("metric:", StringComparison.OrdinalIgnoreCase))
        {
            var definition = MetricCatalog.Find(raw[7..]);
            if (definition is not null)
            {
                return Metric(definition, snapshot, fps);
            }
        }

        if (MetricCatalog.Find(raw) is { } directMetric)
        {
            return Metric(directMetric, snapshot, fps);
        }

        if (raw.StartsWith("derived/ram-total/", StringComparison.OrdinalIgnoreCase))
        {
            return Sensor(snapshot, SensorIds.RamTotal, "MB");
        }

        if (raw.StartsWith("sensor:", StringComparison.OrdinalIgnoreCase))
        {
            return FromSensorName(raw[7..], snapshot);
        }

        var core = CoreRole.Match(raw);
        if (core.Success)
        {
            var family = core.Groups[1].Value.ToLowerInvariant();
            var type = core.Groups[2].Success ? core.Groups[2].Value.ToUpperInvariant() : string.Empty;
            var number = int.Parse(core.Groups[3].Value, CultureInfo.InvariantCulture);
            var index = LogicalCoreIndex(type, number);
            return family switch
            {
                "loads" => Sensor(snapshot, SensorIds.CoreUsage(index), "%"),
                "clocks" or "effective-clocks" => Sensor(snapshot, SensorIds.CoreClock(index), "MHz"),
                "temperatures" => Sensor(snapshot, SensorIds.CoreTemperature(index), "°C"),
                _ => TileReading.Unavailable(),
            };
        }

        // Unknown semantic keys: accept a raw HardwareOSD sensor id so imported designs can bind
        // directly to the hardware catalogue.
        if (snapshot.Contains(raw))
        {
            return Sensor(snapshot, raw, string.Empty);
        }

        return TileReading.Unavailable();
    }

    public static string Format(TileReading reading, string? format, int? digits, double scale)
    {
        if (!reading.Available)
        {
            return "N/A";
        }

        if (reading.IsText)
        {
            return reading.Text;
        }

        var value = reading.Numeric * scale;
        if (!double.IsFinite(value))
        {
            return "N/A";
        }

        if (digits is >= 0 and <= 6)
        {
            return value.ToString("F" + digits, CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(format))
        {
            var decimals = format.Trim() switch
            {
                "%.0f" => 0,
                "%.1f" => 1,
                "%.2f" => 2,
                "%.3f" => 3,
                _ => -1,
            };
            if (decimals >= 0)
            {
                return value.ToString("F" + decimals, CultureInfo.InvariantCulture);
            }

            try
            {
                return value.ToString(format, CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                // Fall through to the default.
            }
        }

        return value.ToString("0.#", CultureInfo.InvariantCulture);
    }

    private static int LogicalCoreIndex(string type, int number)
    {
        var ordinal = Math.Max(0, number - 1);
        return type switch
        {
            "P" => ordinal,
            "E" => 8 + ordinal,
            "LPE" or "D" or "LP" => 16 + ordinal,
            _ => ordinal,
        };
    }

    private static TileReading FromSensorName(string encoded, SensorSnapshot snapshot)
    {
        var text = Uri.UnescapeDataString(encoded);
        var separator = text.LastIndexOf('/');
        if (separator < 0 || separator == text.Length - 1)
        {
            return TileReading.Unavailable();
        }

        var leaf = text[(separator + 1)..];
        var core = CoreSensor.Match(text);
        if (core.Success && int.TryParse(core.Groups[1].Value, out var coreNumber))
        {
            return Sensor(snapshot, SensorIds.CoreUsage(coreNumber - 1), "%");
        }

        if (text.Contains("/clock/CPU Max", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("/clock/CPU Core", StringComparison.OrdinalIgnoreCase))
        {
            return Sensor(snapshot, SensorIds.CpuClock, "MHz");
        }

        if (text.Contains("/load/CPU Total", StringComparison.OrdinalIgnoreCase))
        {
            return Sensor(snapshot, SensorIds.CpuUsage, "%");
        }

        if (text.Contains("/load/CPU Max", StringComparison.OrdinalIgnoreCase))
        {
            return Sensor(snapshot, SensorIds.CpuUsageMax, "%");
        }

        if (text.Contains("/temperature/CPU Package", StringComparison.OrdinalIgnoreCase))
        {
            return Sensor(snapshot, SensorIds.CpuPackageTemperature, "°C");
        }

        if (text.Contains("/power/CPU Package", StringComparison.OrdinalIgnoreCase))
        {
            return Sensor(snapshot, SensorIds.CpuPower, "W");
        }

        if (text.Contains("/temperature/GPU Core", StringComparison.OrdinalIgnoreCase))
        {
            return Sensor(snapshot, SensorIds.GpuTemperature, "°C");
        }

        if (text.Contains("/load/GPU Core", StringComparison.OrdinalIgnoreCase))
        {
            return Sensor(snapshot, SensorIds.GpuUsage, "%");
        }

        if (text.Contains("/power/GPU Power", StringComparison.OrdinalIgnoreCase))
        {
            return Sensor(snapshot, SensorIds.GpuPower, "W");
        }

        if (text.Contains("/clock/GPU Core", StringComparison.OrdinalIgnoreCase))
        {
            return Sensor(snapshot, SensorIds.GpuClock, "MHz");
        }

        if (text.Contains("/clock/GPU Memory", StringComparison.OrdinalIgnoreCase))
        {
            return Sensor(snapshot, SensorIds.GpuMemoryClock, "MHz");
        }

        if (text.Contains("/data/GPU Memory Dedicated", StringComparison.OrdinalIgnoreCase))
        {
            return Sensor(snapshot, SensorIds.VramUsed, "MB");
        }

        if (text.Contains("/data/RAM Game Used", StringComparison.OrdinalIgnoreCase))
        {
            return TileReading.Unavailable("MB");
        }

        if (text.Contains("/data/RAM Used", StringComparison.OrdinalIgnoreCase))
        {
            return Sensor(snapshot, SensorIds.RamUsed, "MB");
        }

        if (text.Contains("/load/RAM Usage", StringComparison.OrdinalIgnoreCase))
        {
            return Sensor(snapshot, SensorIds.RamPercent, "%");
        }

        if (text.Contains("D3D Shared Memory Used", StringComparison.OrdinalIgnoreCase))
        {
            return TileReading.Unavailable("MB");
        }

        _ = leaf;
        return TileReading.Unavailable();
    }

    private static TileReading Name(string? text)
        => string.IsNullOrWhiteSpace(text) ? TileReading.Unavailable() : TileReading.TextValue(text);

    private static TileReading Difference(SensorSnapshot snapshot, string totalId, string usedId,
        string unit)
    {
        if (!snapshot.TryGet(totalId, out var total) || !total.Available ||
            !snapshot.TryGet(usedId, out var used) || !used.Available ||
            !double.IsFinite(total.Value) || !double.IsFinite(used.Value))
        {
            return TileReading.Unavailable(unit);
        }

        var free = Math.Max(0, total.Value - used.Value);
        return new TileReading(free.ToString("0.###", CultureInfo.InvariantCulture),
            unit, true, free, false, totalId);
    }

    private static TileReading Sensor(SensorSnapshot snapshot, string id, string unit)
    {
        if (!snapshot.TryGet(id, out var value) || !value.Available || !double.IsFinite(value.Value))
        {
            return TileReading.Unavailable(unit);
        }

        return new TileReading(value.Value.ToString("0.###", CultureInfo.InvariantCulture),
            string.IsNullOrWhiteSpace(value.Unit) ? unit : value.Unit, true, value.Value, false, id);
    }

    private static TileReading Fan(SensorSnapshot snapshot, MetricDefinition? definition)
    {
        if (definition is null)
        {
            return TileReading.Unavailable();
        }

        var component = MetricCatalog.FanComponentForMetric(definition.Id);
        if (component is null)
        {
            return TileReading.Unavailable(definition.Unit);
        }

        var percent = definition.Id.EndsWith(".percent", StringComparison.OrdinalIgnoreCase);
        var values = FanSensorReader.Channels(snapshot, component.Value)
            .Select(channel => percent ? channel.Percent : channel.Rpm)
            .Where(value => value is { } number && double.IsFinite(number))
            .Select(value => value!.Value)
            .ToList();
        if (values.Count == 0)
        {
            return TileReading.Unavailable(definition.Unit);
        }

        // Individual physical readings are joined, never summed or averaged. A single channel also
        // provides a numeric value for bars/gauges; multiple channels remain a text reading so one
        // fan cannot masquerade as the whole component.
        var text = string.Join("/", values.Select(value =>
            value.ToString("0", CultureInfo.InvariantCulture)));
        return new TileReading(text, percent ? "%" : "RPM", true,
            values.Count == 1 ? values[0] : double.NaN, values.Count != 1, null);
    }

    /// <summary>
    /// Resolves a catalogue metric through the same formatter used by the row engine. Numeric
    /// metrics backed by a raw sensor keep their numeric value so gauges and bars work; aggregate
    /// metrics (fan RPM/percentage) fall back to the formatted string.
    /// </summary>
    private static TileReading Metric(MetricDefinition definition, SensorSnapshot snapshot,
        FpsSnapshot? fps)
    {
        var rawId = MetricCatalog.SensorIdFor(definition);
        if (rawId is not null && snapshot.TryGet(rawId, out var sensor) &&
            sensor.Available && double.IsFinite(sensor.Value))
        {
            return Sensor(snapshot, rawId, definition.Unit);
        }

        var formatted = MetricFormatter.Format(definition, snapshot, fps);
        if (!formatted.Available)
        {
            return TileReading.Unavailable(definition.Unit);
        }

        var numeric = double.TryParse(formatted.Value, NumberStyles.Float,
            CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : double.NaN;
        return new TileReading(formatted.Value, formatted.Unit, true, numeric,
            double.IsNaN(numeric), rawId, formatted.Severity);
    }

    private static TileReading MaxCoreClock(SensorSnapshot snapshot)
    {
        var max = double.MinValue;
        foreach (var index in SensorIds.CoreIndexes(snapshot, SensorIds.CoreSuffixClock))
        {
            if (snapshot.TryGet(SensorIds.CoreClock(index), out var value) && value.Available &&
                double.IsFinite(value.Value) && value.Value > max)
            {
                max = value.Value;
            }
        }

        if (max <= double.MinValue)
        {
            return Sensor(snapshot, SensorIds.CpuClock, "MHz");
        }

        return new TileReading(max.ToString("0", CultureInfo.InvariantCulture), "MHz", true, max, false, null);
    }

    private static TileReading FrameNumber(double? value, FpsSnapshot? fps, string unit)
    {
        if (fps is null || !fps.HasValue || value is null || !double.IsFinite(value.Value))
        {
            return TileReading.Unavailable(unit);
        }

        return new TileReading(value.Value.ToString("0.###", CultureInfo.InvariantCulture),
            unit, true, value.Value, false, null);
    }

    private static TileReading AverageFrame(SensorHistory? history, string series, double? fallback, string unit)
    {
        if (history is not null)
        {
            var values = new double[Math.Min(history.Capacity, 24)];
            var count = history.TryCopy(series, values);
            var sum = 0.0;
            var valid = 0;
            for (var i = Math.Max(0, count - 12); i < count; i++)
            {
                var value = values[i];
                if (double.IsFinite(value))
                {
                    sum += value;
                    valid++;
                }
            }

            if (valid > 0)
            {
                var average = sum / valid;
                return new TileReading(average.ToString("0.###", CultureInfo.InvariantCulture),
                    unit, true, average, false, null);
            }
        }

        return fallback is { } number && double.IsFinite(number) && number > 0
            ? new TileReading(number.ToString("0.###", CultureInfo.InvariantCulture), unit, true, number,
                false, null)
            : TileReading.Unavailable(unit);
    }

    private static TileReading Stutter(SensorHistory? history)
    {
        if (history is null)
        {
            return TileReading.Unavailable("%");
        }

        var values = new double[Math.Min(history.Capacity, 64)];
        var count = history.TryCopy("frame.time", values);
        var samples = new List<double>(count);
        for (var i = 0; i < count; i++)
        {
            if (double.IsFinite(values[i]) && values[i] > 0)
            {
                samples.Add(values[i]);
            }
        }

        if (samples.Count < 8)
        {
            return TileReading.Unavailable("%");
        }

        samples.Sort();
        var median = samples[samples.Count / 2];
        var threshold = median * 2.5;
        var stutters = samples.Count(value => value > threshold);
        var percent = stutters * 100.0 / samples.Count;
        return new TileReading(percent.ToString("0.##", CultureInfo.InvariantCulture),
            "%", true, percent, false, null);
    }
}
