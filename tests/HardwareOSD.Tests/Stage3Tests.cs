using System.Text.Json;
using HardwareOSD.Core.Config;
using HardwareOSD.Core.Diagnostics;
using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.Recording;
using HardwareOSD.Core.SystemInfo;

namespace HardwareOSD.Tests;

public class Stage3Tests
{
    [Fact]
    public void FpsIgnoreList_NormalizesAndMatchesProcessNames()
    {
        var settings = new FpsSettings
        {
            IgnoredProcesses = new List<string>
            {
                " steam.exe ", "steam", "", "obs64.exe", "OBS64.EXE",
            },
            CaptureDurationSeconds = 99999,
            CaptureDelaySeconds = -4,
        };
        settings.Normalize();

        Assert.Equal(new[] { "steam.exe", "obs64.exe" }, settings.IgnoredProcesses);
        Assert.True(settings.IsIgnored("steam"));
        Assert.True(settings.IsIgnored("C:\\Games\\steam.exe"));
        Assert.True(settings.IsIgnored("obs64"));
        Assert.False(settings.IsIgnored("game.exe"));
        Assert.Equal(3600, settings.CaptureDurationSeconds);
        Assert.Equal(0, settings.CaptureDelaySeconds);
    }

    [Fact]
    public void FpsIgnoreList_IsDeepCloned()
    {
        var settings = AppSettings.CreateDefault();
        settings.Fps.IgnoredProcesses.Add("steam.exe");
        var clone = settings.Clone();
        clone.Fps.IgnoredProcesses.Add("obs64.exe");

        Assert.Single(settings.Fps.IgnoredProcesses);
        Assert.Equal(2, clone.Fps.IgnoredProcesses.Count);
    }

    [Fact]
    public async Task Recording_JsonAndCsvJsonCreateValidJson()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hwosd-json-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var cache = new SensorCache();
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            cache.Publish(SensorSnapshot.Create(1, nowMs, new Dictionary<string, SensorValue>
            {
                ["cpu.temp"] = new("cpu.temp", 55, "°C", SensorKind.Temperature,
                    SensorOrigin.HardwareSensor, "test", nowMs - 10),
            }));
            var sensors = new[] { new RecordingSensor("cpu.temp", "CPU temperature", "°C") };
            using (var recorder = new SensorRecorder(cache, NullLog.Instance))
            {
                var result = recorder.Start(new RecordingSettings
                {
                    OutputDirectory = directory,
                    FileName = "json-test",
                    Format = RecordingFormat.Json,
                    IntervalMs = 1000,
                }, sensors);
                Assert.True(result.Success, result.Detail);
                await Task.Delay(250);
                var status = await recorder.StopAsync(TimeSpan.FromSeconds(5));
                Assert.True(File.Exists(status.FilePath));
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(status.FilePath!));
                var sample = document.RootElement.GetProperty("samples")[0];
                var value = sample.GetProperty("values")[0];
                Assert.Equal("cpu.temp", value.GetProperty("sensorId").GetString());
                Assert.Equal("HardwareSensor", value.GetProperty("origin").GetString());
                Assert.InRange(value.GetProperty("ageMs").GetInt64(), 10, 1000);
            }

            using (var recorder = new SensorRecorder(cache, NullLog.Instance))
            {
                var result = recorder.Start(new RecordingSettings
                {
                    OutputDirectory = directory,
                    FileName = "both-test",
                    Format = RecordingFormat.CsvJson,
                    IntervalMs = 1000,
                }, sensors);
                Assert.True(result.Success, result.Detail);
                await Task.Delay(250);
                var status = await recorder.StopAsync(TimeSpan.FromSeconds(5));
                Assert.True(File.Exists(status.FilePath));
                Assert.True(File.Exists(status.AdditionalFilePath));
                Assert.EndsWith(".csv", status.FilePath, StringComparison.OrdinalIgnoreCase);
                Assert.EndsWith(".json", status.AdditionalFilePath, StringComparison.OrdinalIgnoreCase);
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(status.AdditionalFilePath!));
                Assert.True(document.RootElement.GetProperty("samples").GetArrayLength() >= 1);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PlatformSecurity_ReadDoesNotThrowAndReturnsKnownShape()
    {
        var snapshot = PlatformSecurityInfo.Read();
        Assert.NotNull(snapshot);
        Assert.True(snapshot.VbsStatus is null or >= 0);
    }
}
