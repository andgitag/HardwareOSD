using HardwareOSD.Core.Config;
using HardwareOSD.Core.Diagnostics;
using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.Recording;

namespace HardwareOSD.Tests;

public class RecordingTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetDirectoryName(typeof(RecordingTests).Assembly.Location)!,
        "testdata",
        Guid.NewGuid().ToString("N"));

    public RecordingTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch
        {
            // ignored
        }
    }

    [Fact]
    public void CsvFormatter_WritesStableColumnsAndExplicitUnavailableStatus()
    {
        var sample = new RecordingSample(DateTimeOffset.Parse("2026-01-02T03:04:05Z"), 1.25,
            new[]
            {
                new RecordingValue("cpu.temp", "CPU, \"package\"", true, 61.5, "°C", null),
                new RecordingValue("gpu.fan.0.rpm", "GPU fan 1", false, double.NaN, "RPM",
                    SensorNotes.NoPlatformFanChannel),
            });

        Assert.Equal("Timestamp,ElapsedSeconds,SensorId,SensorName,Value,Unit,Status,Note,Source,Origin,SensorTimestamp,AgeMs\r\n",
            RecordingTextFormatter.CsvHeader());

        var available = RecordingTextFormatter.CsvRow(sample, sample.Values[0]);
        Assert.Contains("\"CPU, \"\"package\"\"\"", available);
        Assert.Contains(",61.5,°C,available,", available);

        var unavailable = RecordingTextFormatter.CsvRow(sample, sample.Values[1]);
        Assert.Contains(",GPU fan 1,,RPM,unavailable,no-platform-fan-channel", unavailable);
        Assert.DoesNotContain(",0,", unavailable);
    }

    [Fact]
    public void CsvFormatter_RecordsSourceOriginSensorTimestampAndAge()
    {
        var sampleTime = DateTimeOffset.Parse("2026-01-02T03:04:05Z");
        var sample = new RecordingSample(sampleTime, 1.25,
            new[]
            {
                new RecordingValue("cpu.power", "CPU package power", true, 42.5, "W", null,
                    "PDH Energy Meter: RAPL_Package0_PKG", SensorOrigin.WindowsApi,
                    sampleTime.AddMilliseconds(-250).ToUnixTimeMilliseconds()),
            });

        var row = RecordingTextFormatter.CsvRow(sample, sample.Values[0]);

        Assert.Contains("PDH Energy Meter: RAPL_Package0_PKG", row);
        Assert.Contains(",WindowsApi,2026-01-02T03:04:04.7500000+00:00,250", row);
    }

    [Fact]
    public void TxtFormatter_MarksUnavailableValuesAsNa()
    {
        var sample = new RecordingSample(DateTimeOffset.Parse("2026-01-02T03:04:05Z"), 2.5,
            new[]
            {
                new RecordingValue("cpu.temp", "CPU", false, double.NaN, "°C",
                    SensorNotes.RequiresElevation),
            });

        var text = RecordingTextFormatter.TxtSample(sample);
        Assert.Contains("cpu.temp = N/A", text);
        Assert.Contains("requires-elevation", text);
        Assert.DoesNotContain("cpu.temp = 0", text);
    }

    [Fact]
    public void Settings_NormalizeSanitizesNameIntervalAndSensors()
    {
        var settings = new RecordingSettings
        {
            OutputDirectory = "   ",
            FileName = "bad:name?.txt",
            Format = (RecordingFormat)42,
            IntervalMs = 1234,
            Sensors = new List<string> { " cpu.temp ", "", "cpu.temp", "gpu.usage" },
        };

        settings.Normalize();

        Assert.Equal(RecordingSettings.DefaultDirectory(), settings.OutputDirectory);
        Assert.Equal("bad-name-", settings.FileName);
        Assert.Equal(RecordingFormat.Csv, settings.Format);
        Assert.Equal(RecordingIntervals.Default, settings.IntervalMs);
        Assert.Equal(new[] { "cpu.temp", "gpu.usage" }, settings.Sensors);
    }

    [Theory]
    [InlineData(50, 100)]
    [InlineData(140, 100)]
    [InlineData(260, 200)]
    [InlineData(400, 500)]
    [InlineData(900, 1000)]
    [InlineData(5000, 1000)]
    public void Intervals_ClampToSupportedValues(int requested, int expected)
    {
        Assert.Equal(expected, RecordingIntervals.Normalize(requested));
    }

    [Fact]
    public async Task Recorder_WritesFromSensorCacheAndNeverFabricatesUnavailableValues()
    {
        var cache = new SensorCache();
        cache.Publish(SensorSnapshot.Create(1, 1000, new Dictionary<string, SensorValue>
        {
            ["cpu.temp"] = new("cpu.temp", 60.5, "°C", SensorKind.Temperature,
                SensorOrigin.HardwareSensor, "test", 1000),
            ["gpu.power"] = SensorValue.Unavailable("gpu.power", SensorKind.Power, "test", 1000,
                SensorNotes.RequiresElevation),
        }));

        using var recorder = new SensorRecorder(cache, NullLog.Instance);
        var settings = new RecordingSettings
        {
            OutputDirectory = _directory,
            FileName = "capture",
            Format = RecordingFormat.Csv,
            IntervalMs = 100,
        };

        var result = recorder.Start(settings, new[]
        {
            new RecordingSensor("cpu.temp", "CPU temperature", "°C"),
            new RecordingSensor("gpu.power", "GPU power", "W"),
        });

        Assert.True(result.Success, result.Detail);
        await Task.Delay(350);
        var status = await recorder.StopAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RecordingError.None, status.ErrorCode);
        Assert.NotNull(status.FilePath);
        Assert.True(status.Samples >= 2, $"expected at least two samples, got {status.Samples}");
        var lines = await File.ReadAllLinesAsync(status.FilePath!);
        Assert.Equal("Timestamp,ElapsedSeconds,SensorId,SensorName,Value,Unit,Status,Note,Source,Origin,SensorTimestamp,AgeMs", lines[0]);
        Assert.Contains(lines, line => line.Contains("cpu.temp") && line.Contains(",60.5,°C,available"));
        Assert.Contains(lines, line =>
            line.Contains("gpu.power") && line.Contains(",W,unavailable,requires-elevation"));
    }

    [Fact]
    public async Task Recorder_DeduplicatesRepeatedSensorIds()
    {
        var cache = new SensorCache();
        cache.Publish(SensorSnapshot.Create(1, 1000, new Dictionary<string, SensorValue>
        {
            ["cpu.temp"] = new("cpu.temp", 60.5, "°C", SensorKind.Temperature,
                SensorOrigin.HardwareSensor, "test", 1000),
        }));

        using var recorder = new SensorRecorder(cache, NullLog.Instance);
        var result = recorder.Start(
            new RecordingSettings { OutputDirectory = _directory, FileName = "dedup", IntervalMs = 1000 },
            new[]
            {
                new RecordingSensor("cpu.temp", "CPU temperature", "°C"),
                new RecordingSensor("cpu.temp", "CPU temperature duplicate", "°C"),
            });

        Assert.True(result.Success, result.Detail);
        await Task.Delay(250);
        var status = await recorder.StopAsync(TimeSpan.FromSeconds(5));
        Assert.True(status.Samples >= 1);
        var lines = await File.ReadAllLinesAsync(status.FilePath!);
        Assert.Single(lines, line => line.Contains("cpu.temp") && line.Contains(",60.5,"));
    }

    [Fact]
    public async Task Recorder_RejectsEmptySensorListAndDoubleStart()
    {
        var cache = new SensorCache();
        cache.Publish(SensorSnapshot.Create(1, 1000, new Dictionary<string, SensorValue>
        {
            ["cpu.temp"] = new("cpu.temp", 60.5, "°C", SensorKind.Temperature,
                SensorOrigin.HardwareSensor, "test", 1000),
        }));

        using var recorder = new SensorRecorder(cache, NullLog.Instance);
        var settings = new RecordingSettings { OutputDirectory = _directory, FileName = "reject" };
        var sensors = new[] { new RecordingSensor("cpu.temp", "CPU temperature", "°C") };

        var empty = recorder.Start(settings, Array.Empty<RecordingSensor>());
        Assert.False(empty.Success);
        Assert.Equal(RecordingError.NoSensors, empty.Error);

        var first = recorder.Start(settings, sensors);
        Assert.True(first.Success, first.Detail);
        var second = recorder.Start(settings, sensors);
        Assert.False(second.Success);
        Assert.Equal(RecordingError.AlreadyRecording, second.Error);
        await recorder.StopAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Recorder_NeverOverwritesAnExistingRecording()
    {
        var existing = Path.Combine(_directory, "taken.csv");
        File.WriteAllText(existing, "keep");

        var cache = new SensorCache();
        cache.Publish(SensorSnapshot.Create(1, 1000, new Dictionary<string, SensorValue>
        {
            ["cpu.temp"] = new("cpu.temp", 60.5, "°C", SensorKind.Temperature,
                SensorOrigin.HardwareSensor, "test", 1000),
        }));

        using var recorder = new SensorRecorder(cache, NullLog.Instance);
        var result = recorder.Start(
            new RecordingSettings { OutputDirectory = _directory, FileName = "taken" },
            new[] { new RecordingSensor("cpu.temp", "CPU temperature", "°C") });

        Assert.True(result.Success, result.Detail);
        await recorder.StopAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("keep", File.ReadAllText(existing));
        Assert.True(File.Exists(Path.Combine(_directory, "taken-1.csv")));
    }

    [Fact]
    public void AppSettings_DefaultRecordingIsNormalizedAndCloned()
    {
        var settings = AppSettings.CreateDefault();
        settings.Normalize();

        Assert.Equal(RecordingFormat.Csv, settings.Recording.Format);
        Assert.Equal(RecordingIntervals.Default, settings.Recording.IntervalMs);
        Assert.False(string.IsNullOrWhiteSpace(settings.Recording.OutputDirectory));
        Assert.True(settings.Overlay.ModifierDragEnabled);
        Assert.Equal(OverlayDragModifier.Control, settings.Overlay.ModifierDragKey);

        settings.Recording.Sensors.Add("cpu.temp");
        var clone = settings.Clone();
        Assert.Equal(new[] { "cpu.temp" }, clone.Recording.Sensors);
        Assert.Equal(settings.Recording.OutputDirectory, clone.Recording.OutputDirectory);
    }
}
