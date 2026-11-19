using HardwareOSD.Core.Config;
using HardwareOSD.Core.Diagnostics;
using HardwareOSD.Core.Fps;
using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.Metrics;

namespace HardwareOSD.Tests;

public class SettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetDirectoryName(typeof(SettingsTests).Assembly.Location)!,
        "testdata",
        Guid.NewGuid().ToString("N"));

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

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [Fact]
    public void Defaults_AreNormalisedAndComplete()
    {
        var settings = AppSettings.CreateDefault();
        Assert.Equal(OverlayLayoutMode.Stacked, settings.Overlay.Layout);
        Assert.Equal(OverlayAnchor.TopLeft, settings.Overlay.Anchor);
        Assert.Equal(AppThemeMode.Dark, settings.Behavior.AppTheme);
        Assert.Equal(MetricCatalog.All.Count, settings.Metrics.Count);
        Assert.All(MetricCatalog.All, definition => Assert.Contains(settings.Metrics, m => m.Id == definition.Id));
        Assert.True(settings.IsMetricVisible("cpu.temp"));
        Assert.True(settings.IsMetricVisible("vram.used"));
        Assert.False(settings.IsMetricVisible("gpu.fan.rpm"));
        Assert.False(settings.IsMetricVisible("gpu.fan.percent"));
        Assert.False(settings.IsMetricVisible("cpu.fan.rpm"));
        Assert.False(settings.IsMetricVisible("cpu.fan.percent"));
        Assert.True(settings.Fps.Enabled);
        Assert.Equal(FpsSourceMode.Automatic, settings.Fps.Mode);
        Assert.Equal(FpsCaptureMode.GameOnly, settings.Fps.CaptureMode);
        Assert.Equal(150, settings.Fps.UpdateIntervalMs);
        Assert.False(settings.Fps.MeasureWindowedApps);
        Assert.True(settings.Monitoring.EnableNvml);
        Assert.True(settings.Monitoring.EnableLibreHardwareMonitor);
        Assert.True(settings.Monitoring.EnableMsiAcpi);
        Assert.Equal("#000000", settings.Overlay.BackgroundColor);
        Assert.Equal(OverlayThemeMode.Dark, settings.Overlay.Theme);
    }

    [Fact]
    public void Normalize_ClampsHostileValues()
    {
        var settings = new AppSettings
        {
            Overlay = new OverlaySettings
            {
                FontSize = 400,
                BackgroundOpacity = -20,
                PaddingX = 900,
                OffsetX = int.MaxValue,
                Layout = (OverlayLayoutMode)42,
                Anchor = (OverlayAnchor)99,
                FontFamily = "   ",
                LabelColor = "not-a-color",
            },
            Monitoring = new MonitoringSettings
            {
                PollIntervalMs = 1,
                OverlayRefreshHz = 500,
            },
            Fps = new FpsSettings
            {
                Enabled = false,
                Mode = (FpsSourceMode)99,
                CaptureMode = (FpsCaptureMode)99,
                UpdateIntervalMs = 5,
            },
            Metrics = new List<MetricPreference>
            {
                new() { Id = "does.not.exist", Visible = true },
                new() { Id = "cpu.temp", Visible = false },
                new() { Id = "cpu.temp", Visible = true },
            },
        };

        settings.Normalize();

        Assert.Equal(32, settings.Overlay.FontSize);
        Assert.Equal(0, settings.Overlay.BackgroundOpacity);
        Assert.Equal(64, settings.Overlay.PaddingX);
        Assert.Equal(16_000, settings.Overlay.OffsetX);
        Assert.Equal(OverlayLayoutMode.Stacked, settings.Overlay.Layout);
        Assert.Equal(OverlayAnchor.TopLeft, settings.Overlay.Anchor);
        Assert.Equal("Segoe UI Variable Text", settings.Overlay.FontFamily);
        Assert.Equal("#6FD8CC", settings.Overlay.LabelColor);
        Assert.Equal(200, settings.Monitoring.PollIntervalMs);
        Assert.Equal(30, settings.Monitoring.OverlayRefreshHz);
        Assert.Equal(FpsSourceMode.Automatic, settings.Fps.Mode);
        Assert.Equal(FpsCaptureMode.GameOnly, settings.Fps.CaptureMode);
        Assert.Equal(100, settings.Fps.UpdateIntervalMs);
        settings.Fps.UpdateIntervalMs = 5000;
        settings.Normalize();
        Assert.Equal(250, settings.Fps.UpdateIntervalMs);
        Assert.DoesNotContain(settings.Metrics, m => m.Id == "does.not.exist");
        Assert.Equal(MetricCatalog.All.Count, settings.Metrics.Count);
        Assert.False(settings.IsMetricVisible("cpu.temp")); // explicit user preference preserved
    }

    [Fact]
    public void Normalize_UsesSupportedLanguageAndClampsCustomPosition()
    {
        var settings = AppSettings.CreateDefault();
        settings.Behavior.Language = " UK ";
        settings.Overlay.MonitorDevice = "  \\\\.\\DISPLAY2  ";
        settings.Overlay.UseCustomPosition = true;
        settings.Overlay.CustomX = int.MaxValue;
        settings.Overlay.CustomY = int.MinValue;
        settings.Normalize();

        Assert.Equal("uk", settings.Behavior.Language);
        Assert.Equal("\\\\.\\DISPLAY2", settings.Overlay.MonitorDevice);
        Assert.Equal(32_000, settings.Overlay.CustomX);
        Assert.Equal(-32_000, settings.Overlay.CustomY);

        settings.Behavior.Language = "de-DE";
        settings.Behavior.AppTheme = (AppThemeMode)99;
        settings.Normalize();
        Assert.Equal("en", settings.Behavior.Language);
        Assert.Equal(AppThemeMode.Dark, settings.Behavior.AppTheme);
    }

    [Fact]
    public void Clone_CopiesMonitorAndCustomPosition()
    {
        var original = AppSettings.CreateDefault();
        original.Overlay.MonitorDevice = "\\\\.\\DISPLAY2";
        original.Overlay.UseCustomPosition = true;
        original.Overlay.CustomX = 123;
        original.Overlay.CustomY = 456;
        original.Behavior.Language = "uk";
        original.Behavior.AppTheme = AppThemeMode.Light;
        original.Monitoring.EnableMsiAcpi = false;

        var clone = original.Clone();
        Assert.Equal("\\\\.\\DISPLAY2", clone.Overlay.MonitorDevice);
        Assert.True(clone.Overlay.UseCustomPosition);
        Assert.Equal(123, clone.Overlay.CustomX);
        Assert.Equal(456, clone.Overlay.CustomY);
        Assert.Equal("uk", clone.Behavior.Language);
        Assert.Equal(AppThemeMode.Light, clone.Behavior.AppTheme);
        Assert.False(clone.Monitoring.EnableMsiAcpi);
    }

    [Fact]
    public void Clone_IsDeep()
    {
        var original = AppSettings.CreateDefault();
        var clone = original.Clone();
        clone.Overlay.FontSize = 28;
        clone.Metrics[0].Visible = !clone.Metrics[0].Visible;
        clone.Behavior.StartWithWindows = true;

        Assert.NotEqual(28, original.Overlay.FontSize);
        Assert.NotEqual(clone.Metrics[0].Visible, original.Metrics[0].Visible);
        Assert.False(original.Behavior.StartWithWindows);
    }

    [Fact]
    public void Store_RoundTripsEverything()
    {
        var logger = new FileLogger(_directory, "test-log");
        var store = new SettingsStore(SettingsPath, logger);
        var settings = AppSettings.CreateDefault();
        settings.Overlay.Layout = OverlayLayoutMode.Detailed;
        settings.Overlay.Anchor = OverlayAnchor.BottomRight;
        settings.Overlay.FontSize = 19;
        settings.Overlay.ShowSparklines = true;
        settings.Monitoring.PollIntervalMs = 1500;
        settings.Behavior.StartWithWindows = true;
        settings.Behavior.MinimumLogLevel = LogLevel.Warning;
        settings.Behavior.Language = "uk";
        settings.Behavior.AppTheme = AppThemeMode.Light;
        settings.Overlay.MonitorDevice = "\\\\.\\DISPLAY2";
        settings.Overlay.UseCustomPosition = true;
        settings.Overlay.CustomX = 321;
        settings.Overlay.CustomY = 654;
        settings.Fps.Enabled = false;
        settings.Fps.Mode = FpsSourceMode.Presented;
        settings.Fps.CaptureMode = FpsCaptureMode.AllApplications;
        settings.Fps.UpdateIntervalMs = 250;
        settings.Fps.MeasureWindowedApps = true;
        settings.Overlay.Theme = OverlayThemeMode.Light;
        settings.Overlay.BackgroundColor = "#F2F1EE";
        settings.SetMetricVisible("gpu.temp.hotspot", true);
        settings.SetMetricVisible("cpu.clock", false);

        Assert.True(store.Save(settings));

        var loaded = store.Load(out var status);
        Assert.Equal("Settings loaded", status);
        Assert.Equal(OverlayLayoutMode.Detailed, loaded.Overlay.Layout);
        Assert.Equal(OverlayAnchor.BottomRight, loaded.Overlay.Anchor);
        Assert.Equal(19, loaded.Overlay.FontSize);
        Assert.True(loaded.Overlay.ShowSparklines);
        Assert.Equal(1500, loaded.Monitoring.PollIntervalMs);
        Assert.True(loaded.Behavior.StartWithWindows);
        Assert.Equal(LogLevel.Warning, loaded.Behavior.MinimumLogLevel);
        Assert.Equal("uk", loaded.Behavior.Language);
        Assert.Equal(AppThemeMode.Light, loaded.Behavior.AppTheme);
        Assert.Equal("\\\\.\\DISPLAY2", loaded.Overlay.MonitorDevice);
        Assert.True(loaded.Overlay.UseCustomPosition);
        Assert.Equal(321, loaded.Overlay.CustomX);
        Assert.Equal(654, loaded.Overlay.CustomY);
        Assert.False(loaded.Fps.Enabled);
        Assert.Equal(FpsSourceMode.Presented, loaded.Fps.Mode);
        Assert.Equal(FpsCaptureMode.AllApplications, loaded.Fps.CaptureMode);
        Assert.Equal(250, loaded.Fps.UpdateIntervalMs);
        Assert.True(loaded.Fps.MeasureWindowedApps);
        Assert.Equal(OverlayThemeMode.Light, loaded.Overlay.Theme);
        Assert.Equal("#F2F1EE", loaded.Overlay.BackgroundColor);
        Assert.True(loaded.IsMetricVisible("gpu.temp.hotspot"));
        Assert.False(loaded.IsMetricVisible("cpu.clock"));
        Assert.True(File.Exists(SettingsPath));
        Assert.Contains("\"captureMode\": \"allApplications\"", File.ReadAllText(SettingsPath));
        logger.Dispose();
    }

    [Fact]
    public void Store_LegacyJsonWithoutCaptureMode_LoadsAsGameOnly()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath,
            "{ \"fps\": { \"enabled\": true, \"mode\": \"presented\", \"updateIntervalMs\": 200 } }");

        var logger = new FileLogger(_directory, "test-log");
        var store = new SettingsStore(SettingsPath, logger);
        var loaded = store.Load(out _);

        Assert.Equal(FpsSourceMode.Presented, loaded.Fps.Mode);
        Assert.Equal(FpsCaptureMode.GameOnly, loaded.Fps.CaptureMode);
        Assert.True(loaded.Monitoring.EnableMsiAcpi);
        logger.Dispose();
    }

    [Fact]
    public void Store_RecoversFromCorruptJson()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, "{ this is not json ");

        var logger = new FileLogger(_directory, "test-log");
        var store = new SettingsStore(SettingsPath, logger);
        var loaded = store.Load(out var status);

        Assert.Contains("defaults restored", status);
        Assert.Equal(OverlayLayoutMode.Stacked, loaded.Overlay.Layout);
        Assert.NotEmpty(Directory.GetFiles(_directory, "settings.json.corrupt-*.json"));
        logger.Dispose();
    }

    [Fact]
    public void Store_CreatesDefaultsWhenMissing()
    {
        var logger = new FileLogger(_directory, "test-log");
        var store = new SettingsStore(SettingsPath, logger);
        var loaded = store.Load(out var status);
        Assert.Equal("Created default settings", status);
        Assert.True(File.Exists(SettingsPath));
        Assert.Equal(AppSettings.CreateDefault().Metrics.Count, loaded.Metrics.Count);
        logger.Dispose();
    }

    [Fact]
    public void ThemePresets_UseNeutralBackgrounds()
    {
        var settings = AppSettings.CreateDefault();

        OverlayThemePresets.Apply(settings, OverlayThemeMode.Dark);
        Assert.Equal(OverlayThemeMode.Dark, settings.Overlay.Theme);
        Assert.Equal("#000000", settings.Overlay.BackgroundColor);
        Assert.Equal("#F4F7FA", settings.Overlay.ValueColor);

        OverlayThemePresets.Apply(settings, OverlayThemeMode.Light);
        Assert.Equal(OverlayThemeMode.Light, settings.Overlay.Theme);
        Assert.Equal("#F2F1EE", settings.Overlay.BackgroundColor);
        Assert.Equal("#14181C", settings.Overlay.ValueColor);
        Assert.True(settings.Overlay.BackgroundOpacity >= 90);

        var black = OverlayThemePresets.Backgrounds.Single(p => p.Name == "Black");
        OverlayThemePresets.ApplyBackground(settings.Overlay, black);
        Assert.Equal("#000000", settings.Overlay.BackgroundColor);
        Assert.Equal(70, settings.Overlay.BackgroundOpacity);
    }

    [Fact]
    public void SensorHistory_SupportsIndependentSeries()
    {
        var history = new SensorHistory(4);
        history.Track(new[] { SensorIds.CpuUsage });
        history.Append(SnapshotFactory.Build((SensorIds.CpuUsage, 10, "%", SensorKind.Load)));
        history.AppendValue("frame.fps", 144);

        var usage = new double[4];
        var fps = new double[4];
        Assert.Equal(4, history.TryCopy(SensorIds.CpuUsage, usage));
        Assert.Equal(4, history.TryCopy("frame.fps", fps));

        // The snapshot append must not push NaN into the independent FPS series.
        history.Append(SnapshotFactory.Build((SensorIds.CpuUsage, 20, "%", SensorKind.Load)));
        Assert.Equal(4, history.TryCopy("frame.fps", fps));
        Assert.Equal(144, fps[^1]);
    }

    [Fact]
    public void Store_KeepsPreviousGenerationAsBackup()
    {
        var logger = new FileLogger(_directory, "test-log");
        var store = new SettingsStore(SettingsPath, logger);

        var first = AppSettings.CreateDefault();
        first.Overlay.FontSize = 14;
        store.Save(first);

        var second = first.Clone();
        second.Overlay.FontSize = 22;
        store.Save(second);

        Assert.True(store.Load(out _).Overlay.FontSize == 22);
        var backup = File.ReadAllText(SettingsPath + ".bak");
        Assert.Contains("\"fontSize\": 14", backup);
        logger.Dispose();
    }
}

public class SensorMergePolicyTests
{
    private readonly SensorMergePolicy _policy = new();

    private static SensorValue Value(string id, double value, string source, long ts = 1)
        => new(id, value, "u", SensorKind.Data, SensorOrigin.HardwareSensor, source, ts);

    private static SensorValue Missing(string id)
        => SensorValue.Unavailable(id, SensorKind.Data, "x", 1);

    [Fact]
    public void HigherRankWins()
    {
        var merged = _policy.Merge(new List<(string, IReadOnlyList<SensorValue>)>
        {
            (SensorMergePolicy.LhmKey, new[] { Value("gpu.temp", 60, "lhm") }),
            (SensorMergePolicy.NvmlKey, new[] { Value("gpu.temp", 61, "nvml") }),
        });

        Assert.Equal(61, merged["gpu.temp"].Value);
    }

    [Fact]
    public void PerSensorOverrides_AreRespected()
    {
        // CPU usage prefers Windows counters over the sensor library.
        var merged = _policy.Merge(new List<(string, IReadOnlyList<SensorValue>)>
        {
            (SensorMergePolicy.WindowsKey, new[] { Value(SensorIds.CpuUsage, 20, "win") }),
            (SensorMergePolicy.LhmKey, new[] { Value(SensorIds.CpuUsage, 25, "lhm") }),
        });

        Assert.Equal(20, merged[SensorIds.CpuUsage].Value);

        // CPU clock prefers the sensor library (MSR effective clock) over NtPowerInformation.
        var clock = _policy.Merge(new List<(string, IReadOnlyList<SensorValue>)>
        {
            (SensorMergePolicy.WindowsKey, new[] { Value(SensorIds.CpuClock, 2400, "win") }),
            (SensorMergePolicy.LhmKey, new[] { Value(SensorIds.CpuClock, 3050, "lhm") }),
        });

        Assert.Equal(3050, clock[SensorIds.CpuClock].Value);
    }

    [Fact]
    public void UnavailableValue_NeverOverridesAvailableOne()
    {
        var merged = _policy.Merge(new List<(string, IReadOnlyList<SensorValue>)>
        {
            (SensorMergePolicy.NvmlKey, new[] { Value(SensorIds.GpuTemperature, 55, "nvml") }),
            (SensorMergePolicy.LhmKey, new[] { Missing(SensorIds.GpuTemperature) }),
        });

        Assert.True(merged[SensorIds.GpuTemperature].Available);
        Assert.Equal(55, merged[SensorIds.GpuTemperature].Value);
    }

    [Fact]
    public void UnavailableValueIsKeptWhenNothingElseExists()
    {
        var merged = _policy.Merge(new List<(string, IReadOnlyList<SensorValue>)>
        {
            (SensorMergePolicy.LhmKey, new[] { Missing(SensorIds.CpuPower) }),
        });

        Assert.True(merged.ContainsKey(SensorIds.CpuPower));
        Assert.False(merged[SensorIds.CpuPower].Available);
    }

    [Fact]
    public void UnknownProvider_IsIgnored()
    {
        var merged = _policy.Merge(new List<(string, IReadOnlyList<SensorValue>)>
        {
            ("unknown", new[] { Value("gpu.temp", 99, "x") }),
            (SensorMergePolicy.NvmlKey, new[] { Value("gpu.temp", 60, "nvml") }),
        });

        Assert.Equal(60, merged["gpu.temp"].Value);
    }
}
