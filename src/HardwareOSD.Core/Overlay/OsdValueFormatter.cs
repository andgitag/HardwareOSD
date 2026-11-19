using System.Globalization;
using HardwareOSD.Core.Config;
using HardwareOSD.Core.Fps;
using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.Metrics;

namespace HardwareOSD.Core.Overlay;

public readonly record struct OsdFormattedValue(
    string Value,
    string Unit,
    bool Available,
    double RawValue,
    MetricDefinition Definition,
    MetricSeverity Severity,
    OsdThresholdDecision Threshold);

/// <summary>
/// Converts one configurable OSD element into display text. Sensor ids, metric ids, custom number
/// formats, units and prefixes are resolved here; hardware access stays in the snapshot cache.
/// </summary>
public static class OsdValueFormatter
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static OsdFormattedValue Format(
        OsdElement element,
        SensorSnapshot snapshot,
        FpsSnapshot? fps,
        SensorHistory? history = null)
    {
        var sensorId = element.SensorId?.Trim() ?? string.Empty;
        if (sensorId.Length == 0)
        {
            return Unavailable(element, snapshot);
        }

        if (sensorId is "frame.fps.average" or "frame.time.average")
        {
            return AverageFrame(sensorId, element, history);
        }

        var metric = MetricCatalog.Find(sensorId);
        var raw = RawValue(sensorId, metric, snapshot, fps, out var rawAvailable);
        FormattedMetric formatted;
        if (metric is not null)
        {
            formatted = MetricFormatter.Format(metric, snapshot, fps);
        }
        else
        {
            var value = snapshot.TryGet(sensorId, out var sensor) ? sensor : default;
            var available = sensor.Available && double.IsFinite(sensor.Value);
            var sensorUnit = sensor.Unit ?? string.Empty;
            formatted = new FormattedMetric(
                available ? FormatDefault(sensor.Value, sensor.Kind) : "N/A",
                sensorUnit,
                MetricSeverity.Normal,
                available);
            raw = available ? sensor.Value : double.NaN;
            rawAvailable = available;
        }

        var aggregateMetric = metric is not null &&
                              (MetricCatalog.IsFanMetric(metric.Id) ||
                               metric.Id.StartsWith("frame.", StringComparison.Ordinal));
        var availableValue = formatted.Available &&
                             ((rawAvailable && double.IsFinite(raw)) || aggregateMetric);
        if (!availableValue && metric is null)
        {
            return Unavailable(element, snapshot, formatted.Unit);
        }

        var text = availableValue && TryCustomFormat(element, raw, out var custom)
            ? custom
            : formatted.Value;
        if (string.IsNullOrWhiteSpace(text))
        {
            text = "N/A";
            availableValue = false;
        }

        var unit = !string.IsNullOrWhiteSpace(element.Unit)
            ? element.Unit
            : string.IsNullOrWhiteSpace(formatted.Unit) ? metric?.Unit ?? string.Empty : formatted.Unit;

        if (availableValue)
        {
            if (!string.IsNullOrEmpty(element.Prefix))
            {
                text = element.Prefix + text;
            }

            if (!string.IsNullOrEmpty(element.Suffix))
            {
                text += element.Suffix;
            }
        }

        var definition = metric ?? SyntheticDefinition(sensorId, unit, text);
        var threshold = availableValue
            ? OsdThresholdEngine.Evaluate(element, sensorId, raw, snapshot, metric)
            : new OsdThresholdDecision(
                OsdColorRole.Unavailable, null, null, element.Direction, element.AutoColor, false);
        var severity = threshold.Role switch
        {
            OsdColorRole.Critical => MetricSeverity.Critical,
            OsdColorRole.Warning => MetricSeverity.Warning,
            _ => MetricSeverity.Normal,
        };

        return new OsdFormattedValue(text, unit, availableValue, raw, definition, severity, threshold);
    }

    public static double RawValue(
        string sensorId,
        MetricDefinition? metric,
        SensorSnapshot snapshot,
        FpsSnapshot? fps,
        out bool available)
    {
        if (sensorId == "frame.fps")
        {
            available = fps is { HasValue: true };
            return available ? fps!.Fps : double.NaN;
        }

        if (sensorId == "frame.time")
        {
            available = fps is { HasValue: true };
            return available ? fps!.FrameTimeMs : double.NaN;
        }

        if (sensorId == "frame.fps.1low")
        {
            available = fps is { HasValue: true };
            return available ? fps!.OnePercentLowFps : double.NaN;
        }

        var rawId = metric is null ? sensorId : MetricCatalog.SensorIdFor(metric) ?? sensorId;
        if (snapshot.TryGet(rawId, out var sensor) && sensor.Available && double.IsFinite(sensor.Value))
        {
            available = true;
            return sensor.Value;
        }

        available = false;
        return double.NaN;
    }

    private static bool TryCustomFormat(OsdElement element, double value, out string text)
    {
        text = string.Empty;
        try
        {
            if (!string.IsNullOrWhiteSpace(element.NumberFormat))
            {
                text = value.ToString(element.NumberFormat, Culture);
                return text.Length > 0;
            }

            if (element.Decimals >= 0)
            {
                text = value.ToString("F" + element.Decimals, Culture);
                return true;
            }
        }
        catch (FormatException)
        {
            text = string.Empty;
        }

        return false;
    }

    private static string FormatDefault(double value, SensorKind kind)
    {
        var pattern = kind switch
        {
            SensorKind.Temperature => "0.#",
            SensorKind.Clock => "0",
            SensorKind.Load => "0.#",
            SensorKind.Power => value >= 100 ? "0" : "0.0##",
            _ => "0.###",
        };
        return value.ToString(pattern, Culture);
    }

    /// <summary>
    /// Trailing average over the FPS history. This is a real measurement window, not a fabricated
    /// session statistic: the row template's "Average" shows the mean of the last second of samples.
    /// </summary>
    private static OsdFormattedValue AverageFrame(
        string sensorId,
        OsdElement element,
        SensorHistory? history)
    {
        var series = sensorId == "frame.fps.average" ? "frame.fps" : "frame.time";
        var unit = sensorId == "frame.fps.average" ? "FPS" : "ms";
        var definition = new MetricDefinition(sensorId, MetricGroup.Frame,
            sensorId == "frame.fps.average" ? "Average FPS" : "Average frame time",
            unit, "888", unit, false);
        if (history is null)
        {
            return new OsdFormattedValue("N/A", unit, false, double.NaN, definition,
                MetricSeverity.Normal,
                new OsdThresholdDecision(OsdColorRole.Unavailable, null, null,
                    element.Direction, element.AutoColor, false));
        }

        var values = new double[Math.Min(history.Capacity, 24)];
        var count = history.TryCopy(series, values);
        var sum = 0.0;
        var valid = 0;
        for (var i = Math.Max(0, count - 12); i < count; i++)
        {
            if (double.IsFinite(values[i]) && values[i] > 0)
            {
                sum += values[i];
                valid++;
            }
        }

        if (valid == 0)
        {
            return new OsdFormattedValue("N/A", unit, false, double.NaN, definition,
                MetricSeverity.Normal,
                new OsdThresholdDecision(OsdColorRole.Unavailable, null, null,
                    element.Direction, element.AutoColor, false));
        }

        var average = sum / valid;
        var text = average.ToString(element.Decimals >= 0 ? "F" + element.Decimals : "0.#", Culture);
        return new OsdFormattedValue(text, string.IsNullOrWhiteSpace(element.Unit) ? unit : element.Unit,
            true, average, definition, MetricSeverity.Normal,
            new OsdThresholdDecision(OsdColorRole.Normal, null, null,
                element.Direction, false, false));
    }

    private static OsdFormattedValue Unavailable(
        OsdElement element,
        SensorSnapshot snapshot,
        string? unit = null)
    {
        var definition = MetricCatalog.Find(element.SensorId) ??
                         MetricCatalog.FindBySensorId(element.SensorId) ??
                         SyntheticDefinition(element.SensorId, unit ?? element.Unit, "N/A");
        var role = new OsdThresholdDecision(
            OsdColorRole.Unavailable, null, null, element.Direction, element.AutoColor, false);
        return new OsdFormattedValue(
            "N/A",
            string.IsNullOrWhiteSpace(element.Unit) ? definition.Unit : element.Unit,
            false,
            double.NaN,
            definition,
            MetricSeverity.Normal,
            role);
    }

    private static MetricDefinition SyntheticDefinition(string sensorId, string unit, string sample)
        => new(
            string.IsNullOrWhiteSpace(sensorId) ? "custom.value" : sensorId,
            MetricGroup.System,
            string.IsNullOrWhiteSpace(sensorId) ? "custom" : sensorId,
            unit,
            string.IsNullOrWhiteSpace(sample) ? "888" : sample,
            unit,
            false);
}
