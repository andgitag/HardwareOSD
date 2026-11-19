using System.Globalization;
using System.Text;
using HardwareOSD.Core.Config;
using HardwareOSD.Core.Fps;
using HardwareOSD.Core.Metrics;

namespace HardwareOSD.Core.Hardware;

/// <summary>
/// One auditable sensor reading: what the metric shows, which raw sensor feeds it, where that
/// sensor came from, and how old the reading is. Used by the CLI audit command and the
/// Diagnostics page; never fabricates a value.
/// </summary>
public sealed record SensorAuditEntry(
    string MetricId,
    MetricGroup Group,
    string DisplayName,
    double RawValue,
    string RawUnit,
    string DisplayValue,
    string DisplayUnit,
    bool Available,
    string Origin,
    string Source,
    long TimestampMs,
    long AgeMs,
    string? Note);

/// <summary>One physical fan channel as recorded in the diagnostic export.</summary>
public sealed record FanAuditEntry(
    string ChannelId,
    string Component,
    int Index,
    string FanName,
    string Hardware,
    string Source,
    double? Rpm,
    double? Percent,
    bool Available,
    long TimestampMs,
    long AgeMs,
    string? Note);

public static class SensorAudit
{
    public static IReadOnlyList<SensorAuditEntry> Build(
        AppSettings settings,
        SensorSnapshot snapshot,
        FpsSnapshot? fps,
        long nowMs)
    {
        var entries = new List<SensorAuditEntry>(settings.Metrics.Count);
        foreach (var preference in settings.Metrics)
        {
            var definition = MetricCatalog.Find(preference.Id);
            if (definition is null)
            {
                continue;
            }

            var formatted = MetricFormatter.Format(definition, snapshot, fps);
            var sensorId = MetricCatalog.SensorIdFor(definition);
            var available = false;
            var raw = double.NaN;
            var rawUnit = string.Empty;
            var source = "—";
            var origin = "unsupported";
            var timestamp = 0L;
            string? note = null;

            if (definition.Group == MetricGroup.Frame)
            {
                if (fps is { HasValue: true } && definition.Id != "frame.fps.1low" ||
                    fps is { HasValue: true, OnePercentLowFps: > 0 } && definition.Id == "frame.fps.1low")
                {
                    available = true;
                    raw = definition.Id switch
                    {
                        "frame.fps" => fps.Fps,
                        "frame.time" => fps.FrameTimeMs,
                        _ => fps.OnePercentLowFps,
                    };
                    rawUnit = definition.Id == "frame.time" ? "ms" : "fps";
                    source = "ETW " + fps.Source;
                    origin = "ETW";
                    timestamp = fps.TimestampMs;
                    note = fps.Status == FpsStatus.Holding
                        ? "held last valid value (no new presents)"
                        : fps.IsProcessFallback
                            ? "busiest presenting process fallback"
                            : fps.IsFallback ? "presented fallback" : null;
                }
                else
                {
                    note = fps?.Status.ToString();
                }
            }
            else if (MetricCatalog.FanComponentForMetric(definition.Id) is { } fanComponent)
            {
                var fans = FanSensorReader.Channels(snapshot, fanComponent);
                var wantPercent = definition.Id.EndsWith(".percent", StringComparison.Ordinal);
                var values = fans
                    .Select(f => wantPercent ? f.Percent : f.Rpm)
                    .Where(v => v.HasValue)
                    .Select(v => v!.Value)
                    .ToArray();
                available = values.Length > 0;
                raw = available ? values[0] : double.NaN;
                rawUnit = wantPercent ? "%" : "RPM";
                source = fans.Count == 0
                    ? "-"
                    : string.Join(" · ", fans.Select(f => f.Source).Distinct(StringComparer.Ordinal));
                origin = fans.Count == 0
                    ? "unsupported"
                    : available ? nameof(SensorOrigin.HardwareSensor) : "unavailable";
                timestamp = fans.Count == 0 ? 0 : fans.Max(f => f.TimestampMs);
                note = fans.Count == 0
                    ? $"no {FanSensorIds.ComponentLabel(fanComponent)} fan sensor reported by any provider"
                    : !available
                        ? fans.Select(f => f.Note).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                          ?? (wantPercent
                              ? $"{FanSensorIds.ComponentLabel(fanComponent)} fan RPM channel(s) exist but no percentage channel is exposed"
                              : $"{FanSensorIds.ComponentLabel(fanComponent)} fan sensor(s) exist but no RPM channel is exposed")
                        : fans.Count == 1
                            ? null
                            : $"{fans.Count} fan channels: " + string.Join(", ", fans.Select(f => f.Name));
            }
            else if (sensorId is not null && snapshot.TryGet(sensorId, out var sensor))
            {
                available = sensor.Available;
                raw = sensor.Value;
                rawUnit = sensor.Unit;
                source = sensor.Source;
                origin = sensor.Origin.ToString();
                timestamp = sensor.TimestampMs;
                note = sensor.Note;
            }
            else
            {
                note = "sensor not reported by any provider";
            }

            entries.Add(new SensorAuditEntry(
                definition.Id,
                definition.Group,
                definition.DisplayName,
                raw,
                rawUnit,
                formatted.Value,
                formatted.Unit,
                available,
                origin,
                source,
                timestamp,
                timestamp == 0 ? 0 : nowMs - timestamp,
                note));
        }

        return entries;
    }

    public static string FormatTable(IReadOnlyList<SensorAuditEntry> entries)
    {
        var builder = new StringBuilder(entries.Count * 120);
        builder.AppendLine("Metric                     Value               Availability  Age      Source");
        builder.AppendLine("-------------------------  ------------------  ------------  -------  ------------------------------");
        foreach (var entry in entries)
        {
            var value = entry.Available
                ? $"{entry.DisplayValue} {entry.DisplayUnit}".Trim()
                : "N/A";
            builder.AppendLine(
                $"{entry.MetricId,-26} {value,-19} {(entry.Available ? "available" : "missing"),-12} " +
                $"{(entry.TimestampMs == 0 ? "-" : entry.AgeMs + " ms"),-8} {entry.Source}");
            if (!string.IsNullOrEmpty(entry.Note))
            {
                builder.AppendLine($"  note: {entry.Note}");
            }
        }

        return builder.ToString();
    }

    public static IReadOnlyList<FanAuditEntry> BuildFanSensors(SensorSnapshot snapshot, long nowMs)
    {
        return FanSensorReader.Channels(snapshot)
            .Select(channel => new FanAuditEntry(
                $"fan.{FanSensorIds.ComponentKey(channel.Component)}.{channel.Index}",
                FanSensorIds.ComponentLabel(channel.Component),
                channel.Index,
                channel.Name,
                channel.Hardware,
                channel.Source,
                channel.Rpm,
                channel.Percent,
                channel.Available,
                channel.TimestampMs,
                channel.TimestampMs == 0 ? 0 : nowMs - channel.TimestampMs,
                channel.Note))
            .ToList();
    }

    public static string FormatFanTable(IReadOnlyList<FanAuditEntry> entries)
    {
        if (entries.Count == 0)
        {
            return "No fan sensors reported by any provider.";
        }

        var builder = new StringBuilder(entries.Count * 120);
        builder.AppendLine("Component  Fan                 Hardware/source                              RPM    %    Available");
        builder.AppendLine("---------  ------------------  -------------------------------------------  -----  ---  ---------");
        foreach (var entry in entries)
        {
            builder.AppendLine(
                $"{entry.Component,-9}  {entry.FanName,-18}  {entry.Hardware,-43}  " +
                $"{(entry.Rpm is { } rpm ? rpm.ToString("0", CultureInfo.InvariantCulture) : "-"),5}  " +
                $"{(entry.Percent is { } percent ? percent.ToString("0", CultureInfo.InvariantCulture) : "-"),3}  " +
                $"{(entry.Available ? "yes" : "no")}");
            if (!string.IsNullOrEmpty(entry.Note))
            {
                builder.AppendLine($"  note: {entry.Note}");
            }
        }

        return builder.ToString();
    }

    public static void WriteCsv(string path, IReadOnlyList<SensorAuditEntry> entries, SensorSnapshot snapshot)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new StringBuilder();
        builder.AppendLine("timestamp_utc,metric_id,group,display_name,value,unit,available,origin,source,raw_value,raw_unit,age_ms,note");
        var stamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        foreach (var entry in entries)
        {
            var raw = double.IsFinite(entry.RawValue)
                ? entry.RawValue.ToString("0.######", CultureInfo.InvariantCulture)
                : string.Empty;
            builder.Append(stamp).Append(',')
                .Append(Csv(entry.MetricId)).Append(',')
                .Append(entry.Group).Append(',')
                .Append(Csv(entry.DisplayName)).Append(',')
                .Append(Csv(entry.DisplayValue)).Append(',')
                .Append(Csv(entry.DisplayUnit)).Append(',')
                .Append(entry.Available ? "1" : "0").Append(',')
                .Append(Csv(entry.Origin)).Append(',')
                .Append(Csv(entry.Source)).Append(',')
                .Append(raw).Append(',')
                .Append(Csv(entry.RawUnit)).Append(',')
                .Append(entry.AgeMs.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(Csv(entry.Note ?? string.Empty))
                .AppendLine();
        }

        builder.AppendLine();
        builder.AppendLine("raw_sensor_id,value,unit,kind,origin,source,timestamp_utc,available,note");
        foreach (var pair in snapshot.Values.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var value = pair.Value;
            var timestamp = value.TimestampMs == 0
                ? string.Empty
                : DateTimeOffset.FromUnixTimeMilliseconds(value.TimestampMs).UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
            builder.Append(Csv(pair.Key)).Append(',')
                .Append(double.IsFinite(value.Value) ? value.Value.ToString("0.######", CultureInfo.InvariantCulture) : string.Empty).Append(',')
                .Append(Csv(value.Unit)).Append(',')
                .Append(value.Kind).Append(',')
                .Append(value.Origin).Append(',')
                .Append(Csv(value.Source)).Append(',')
                .Append(timestamp).Append(',')
                .Append(value.Available ? "1" : "0").Append(',')
                .Append(Csv(value.Note ?? string.Empty))
                .AppendLine();
        }

        var fanNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var fanEntries = BuildFanSensors(snapshot, fanNow);
        builder.AppendLine();
        builder.AppendLine("fan_channel_id,component,fan_index,fan_name,hardware,source,rpm,percent,available,timestamp_utc,age_ms,note");
        foreach (var entry in fanEntries)
        {
            var timestamp = entry.TimestampMs == 0
                ? string.Empty
                : DateTimeOffset.FromUnixTimeMilliseconds(entry.TimestampMs).UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
            builder.Append(Csv(entry.ChannelId)).Append(',')
                .Append(Csv(entry.Component)).Append(',')
                .Append(entry.Index.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(Csv(entry.FanName)).Append(',')
                .Append(Csv(entry.Hardware)).Append(',')
                .Append(Csv(entry.Source)).Append(',')
                .Append(entry.Rpm is { } rpm ? rpm.ToString("0.######", CultureInfo.InvariantCulture) : string.Empty).Append(',')
                .Append(entry.Percent is { } percent ? percent.ToString("0.######", CultureInfo.InvariantCulture) : string.Empty).Append(',')
                .Append(entry.Available ? "1" : "0").Append(',')
                .Append(timestamp).Append(',')
                .Append(entry.AgeMs.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(Csv(entry.Note ?? string.Empty))
                .AppendLine();
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }

    private static string Csv(string value)
        => value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
}
