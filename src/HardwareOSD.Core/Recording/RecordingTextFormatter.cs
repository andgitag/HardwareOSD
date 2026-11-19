using System.Globalization;
using System.Text;

namespace HardwareOSD.Core.Recording;

/// <summary>
/// Serializes recording samples. CSV uses one row per sensor value (long format) so columns stay
/// identical for every recording, values remain numeric for Excel, and unavailable readings are
/// explicit blanks rather than zeros. TXT uses one readable block per sample.
/// </summary>
public static class RecordingTextFormatter
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static string CsvHeader()
        => "Timestamp,ElapsedSeconds,SensorId,SensorName,Value,Unit,Status,Note,Source,Origin,SensorTimestamp,AgeMs\r\n";

    public static string CsvRow(RecordingSample sample, RecordingValue value)
    {
        var builder = new StringBuilder(160);
        builder.Append(Csv(sample.Timestamp.ToString("O", Culture))).Append(',')
            .Append(sample.ElapsedSeconds.ToString("0.000", Culture)).Append(',')
            .Append(Csv(value.SensorId)).Append(',')
            .Append(Csv(value.DisplayName)).Append(',')
            .Append(value.Available && double.IsFinite(value.Value)
                ? value.Value.ToString("0.######", Culture)
                : string.Empty).Append(',')
            .Append(Csv(value.Unit)).Append(',')
            .Append(value.Available ? "available" : "unavailable").Append(',')
            .Append(Csv(value.Note ?? string.Empty)).Append(',')
            .Append(Csv(value.Source ?? string.Empty)).Append(',')
            .Append(value.SensorTimestampMs > 0 ? value.Origin.ToString() : string.Empty).Append(',')
            .Append(value.SensorTimestampMs > 0
                ? Csv(DateTimeOffset.FromUnixTimeMilliseconds(value.SensorTimestampMs)
                    .ToString("O", Culture))
                : string.Empty).Append(',')
            .Append(AgeMs(sample, value))
            .Append("\r\n");
        return builder.ToString();
    }

    private static string AgeMs(RecordingSample sample, RecordingValue value)
    {
        if (value.SensorTimestampMs <= 0)
        {
            return string.Empty;
        }

        var age = sample.Timestamp.ToUnixTimeMilliseconds() - value.SensorTimestampMs;
        return age >= 0 ? age.ToString(Culture) : string.Empty;
    }

    public static string TxtHeader(RecordingSettings settings, IReadOnlyList<RecordingSensor> sensors, DateTimeOffset started)
    {
        var builder = new StringBuilder(256);
        builder.AppendLine("HardwareOSD sensor recording");
        builder.Append("Started: ").AppendLine(started.ToString("O", Culture));
        builder.Append("Interval: ").Append(settings.IntervalMs.ToString(Culture)).AppendLine(" ms");
        builder.Append("Sensors: ").AppendLine(string.Join(", ", sensors.Select(s => s.Id)));
        builder.AppendLine("---");
        return builder.ToString();
    }

    public static string TxtSample(RecordingSample sample)
    {
        var builder = new StringBuilder(256);
        builder.Append('[').Append(sample.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", Culture))
            .Append("] elapsed=").Append(sample.ElapsedSeconds.ToString("0.000", Culture)).AppendLine(" s");

        foreach (var value in sample.Values)
        {
            builder.Append("  ").Append(value.SensorId).Append(" = ");
            if (value.Available && double.IsFinite(value.Value))
            {
                builder.Append(value.Value.ToString("0.###", Culture));
                if (!string.IsNullOrWhiteSpace(value.Unit))
                {
                    builder.Append(' ').Append(value.Unit);
                }
            }
            else
            {
                builder.Append("N/A");
            }

            if (!string.IsNullOrWhiteSpace(value.Note))
            {
                builder.Append(" (").Append(value.Note).Append(')');
            }

            if (!string.IsNullOrWhiteSpace(value.Source))
            {
                builder.Append(" [source=").Append(value.Source).Append(']');
            }

            var age = AgeMs(sample, value);
            if (age.Length > 0)
            {
                builder.Append(" [age=").Append(age).Append(" ms]");
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    public static string TxtFooter(DateTimeOffset stopped, long samples, long rows, long bytes)
        => $"---\r\nStopped: {stopped.ToString("O", Culture)}\r\nSamples: {samples.ToString(Culture)}\r\nRows: {rows.ToString(Culture)}\r\nBytes: {bytes.ToString(Culture)}\r\n";

    private static string Csv(string value)
        => value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
}
