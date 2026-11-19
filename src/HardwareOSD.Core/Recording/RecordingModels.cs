using System.Globalization;
using HardwareOSD.Core.Hardware;

namespace HardwareOSD.Core.Recording;

public enum RecordingFormat
{
    Csv = 0,
    Txt = 1,
    Json = 2,
    CsvJson = 3,
}

public static class RecordingIntervals
{
    public static readonly int[] Supported = { 100, 200, 500, 1000 };
    public const int Default = 1000;

    /// <summary>Clamps an arbitrary value to the nearest supported interval.</summary>
    public static int Normalize(int milliseconds)
    {
        var best = Default;
        var bestDistance = int.MaxValue;
        foreach (var interval in Supported)
        {
            var distance = Math.Abs(interval - milliseconds);
            if (distance < bestDistance)
            {
                best = interval;
                bestDistance = distance;
            }
        }

        return best;
    }

    public static bool IsSupported(int milliseconds) => Array.IndexOf(Supported, milliseconds) >= 0;
}

public sealed class RecordingSettings
{
    public string OutputDirectory { get; set; } = string.Empty;
    public string FileName { get; set; } = "hardwareosd-recording";
    public RecordingFormat Format { get; set; } = RecordingFormat.Csv;
    public int IntervalMs { get; set; } = RecordingIntervals.Default;

    /// <summary>Raw sensor ids selected for recording. Empty means "use the page's default set".</summary>
    public List<string> Sensors { get; set; } = new();

    public RecordingSettings Clone() => new()
    {
        OutputDirectory = OutputDirectory,
        FileName = FileName,
        Format = Format,
        IntervalMs = IntervalMs,
        Sensors = new List<string>(Sensors ?? new List<string>()),
    };

    public void Normalize()
    {
        OutputDirectory = (OutputDirectory ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            OutputDirectory = DefaultDirectory();
        }

        var name = (FileName ?? string.Empty).Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '-');
        }

        name = name.Trim().TrimEnd('.');
        if (name.Length == 0)
        {
            name = "hardwareosd-recording";
        }

        // An extension in the text box is ignored: the format selector owns the extension.
        var extension = Path.GetExtension(name);
        if (extension.Equals(".csv", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            name = Path.GetFileNameWithoutExtension(name);
        }

        FileName = name;
        IntervalMs = RecordingIntervals.Normalize(IntervalMs);
        if (!Enum.IsDefined(Format))
        {
            Format = RecordingFormat.Csv;
        }

        Sensors ??= new List<string>();
        Sensors = Sensors
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(512)
            .ToList();
    }

    public static string DefaultDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "HardwareOSD Recordings");

    public string Extension => Format switch
    {
        RecordingFormat.Txt => ".txt",
        RecordingFormat.Json => ".json",
        _ => ".csv",
    };
}

/// <summary>One selected sensor, resolved at recording start so file columns stay stable.</summary>
public sealed record RecordingSensor(string Id, string DisplayName, string Unit);

public readonly record struct RecordingValue(
    string SensorId,
    string DisplayName,
    bool Available,
    double Value,
    string Unit,
    string? Note,
    string Source = "",
    SensorOrigin Origin = SensorOrigin.HardwareSensor,
    long SensorTimestampMs = 0);

public sealed record RecordingSample(DateTimeOffset Timestamp, double ElapsedSeconds, IReadOnlyList<RecordingValue> Values);
