using System.Diagnostics;
using HardwareOSD.Core.Config;
using HardwareOSD.Core.Diagnostics;
using HardwareOSD.Core.Fps;
using HardwareOSD.Core.Hardware;

namespace HardwareOSD.Tests;

public class FpsMonitorServiceTests
{
    private sealed class FakeFrameSource : IFrameEventSource
    {
        public string Name => "fake";
        public string Description => "fake ETW source";
        public FpsCaptureStatus Status { get; set; } = FpsCaptureStatus.Running;
        public string? LastError { get; set; }
        public bool Started { get; private set; }
        public bool Stopped { get; private set; }
        public int StartCount { get; private set; }

        public event Action<int, double>? FramePresented;

        public bool Start()
        {
            Started = true;
            StartCount++;
            return true;
        }

        public void Stop()
        {
            Stopped = true;
            Status = FpsCaptureStatus.Stopped;
        }

        public void Emit(int pid, double timestampMs) => FramePresented?.Invoke(pid, timestampMs);

        public IEnumerable<string> Describe() => new[] { "Status: fake", "Synthetic test source" };

        public void Dispose() => Stop();
    }

    private sealed class FakeProbe : IForegroundAppProbe, IForegroundWindowProbe
    {
        public int OwnProcessId => 1000;
        public int ForegroundProcessId { get; set; } = 2000;
        public bool ForegroundIsFullscreen { get; set; } = true;
        public Dictionary<int, string> Names { get; } = new()
        {
            [2000] = "game-a",
            [2001] = "game-b",
            [3000] = "notepad",
            [5000] = "chrome",
            [5001] = "chrome",
        };

        public int GetForegroundProcessId() => ForegroundProcessId;

        public ForegroundWindowInfo GetForegroundWindowInfo()
            => new(ForegroundProcessId, ForegroundIsFullscreen);

        public string? GetProcessName(int processId) => Names.TryGetValue(processId, out var name) ? name : null;
    }

    private static async Task<FpsSnapshot> WaitFor(FpsMonitorService service, Func<FpsSnapshot, bool> predicate, int timeoutMs = 2500)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        FpsSnapshot snapshot = service.Current;
        while (DateTime.UtcNow < deadline)
        {
            snapshot = service.Current;
            if (predicate(snapshot))
            {
                return snapshot;
            }

            await Task.Delay(20);
        }

        return service.Current;
    }

    /// <summary>Emits synthetic frames at a fixed cadence while keeping every sample "fresh".</summary>
    private static void EmitFrames(FakeFrameSource source, int pid, double fps, double durationMs)
    {
        var clock = Stopwatch.StartNew();
        var timestamp = 0.0;
        var interval = 1000.0 / fps;
        while (clock.Elapsed.TotalMilliseconds < durationMs)
        {
            source.Emit(pid, timestamp);
            timestamp += interval;
            Thread.Sleep(1);
        }
    }

    private static AllApplicationsTargetResolver AllApplications(
        FakeRelationshipProbe probe,
        int ownProcessId = 1000)
        => new(probe, ownProcessId, identityTtlMs: 0);

    private static async Task<bool> EmitUntil(
        FakeFrameSource source,
        int pid,
        double fps,
        Func<FpsSnapshot, bool> predicate,
        FpsMonitorService service,
        int timeoutMs)
    {
        var clock = Stopwatch.StartNew();
        var timestamp = 0.0;
        var interval = 1000.0 / fps;
        while (clock.Elapsed.TotalMilliseconds < timeoutMs)
        {
            source.Emit(pid, timestamp);
            timestamp += interval;
            if (predicate(service.Current))
            {
                return true;
            }

            await Task.Delay(15);
        }

        return predicate(service.Current);
    }

    [Fact]
    public async Task PublishesAtTheConfiguredInterval()
    {
        var cache = new FpsCache();
        var source = new FakeFrameSource();
        using var service = new FpsMonitorService(cache, source, new FakeProbe(), NullLog.Instance,
            new FpsSettings { Enabled = true, UpdateIntervalMs = 100 });
        service.Start();

        await Task.Delay(200);
        var start = cache.Latest.Sequence;
        await Task.Delay(500);
        var published = cache.Latest.Sequence - start;

        Assert.InRange(published, 3, 8); // ~5 updates in 500 ms at 100 ms
    }

    [Fact]
    public async Task MeasuresForegroundProcessFrameRate()
    {
        var cache = new FpsCache();
        var source = new FakeFrameSource();
        var probe = new FakeProbe();
        var history = new SensorHistory(64);
        using var service = new FpsMonitorService(cache, source, probe, NullLog.Instance,
            new FpsSettings { Enabled = true, Mode = FpsSourceMode.Automatic, UpdateIntervalMs = 100 }, history);

        service.Start();
        EmitFrames(source, 2000, 144, 350);

        var snapshot = await WaitFor(service, s => s.HasValue);

        Assert.Equal(FpsStatus.Capturing, snapshot.Status);
        Assert.Equal(FpsSource.Presented, snapshot.Source);
        Assert.True(snapshot.IsFallback, "automatic mode must mark presented timing as a fallback");
        Assert.InRange(snapshot.Fps, 135, 150);
        Assert.InRange(snapshot.FrameTimeMs, 6.0, 7.5);
        Assert.Equal(2000, snapshot.ProcessId);
        Assert.Equal("game-a", snapshot.ProcessName);
        Assert.True(history.TryCopy("frame.fps", new double[64]) > 0, "FPS history must feed the sparkline");
    }

    [Fact]
    public async Task HoldsLastValueAcrossAnEtwDeliveryGapThenExpires()
    {
        var cache = new FpsCache();
        var source = new FakeFrameSource();
        var probe = new FakeProbe { ForegroundProcessId = 2000 };
        using var service = new FpsMonitorService(cache, source, probe, NullLog.Instance,
            new FpsSettings { Enabled = true, Mode = FpsSourceMode.Presented, UpdateIntervalMs = 100 });
        service.Start();

        EmitFrames(source, 2000, 120, 350);
        var live = await WaitFor(service, s => s.HasValue && s.Status == FpsStatus.Capturing, 2000);
        var lastValue = live.Fps;
        Assert.True(lastValue > 100);

        // Tracker freshness (1.5 s) expires before the hold window (2.5 s); the row must keep the
        // last real value rather than flashing to "--".
        var held = await WaitFor(service, s => s.Status == FpsStatus.Holding, 3000);
        Assert.True(held.HasValue);
        Assert.True(held.IsValueHeld);
        Assert.Equal(lastValue, held.Fps, 3);
        Assert.Equal(2000, held.ProcessId);

        // After the hold timeout the value becomes unavailable; it is never held indefinitely.
        var expired = await WaitFor(service, s => !s.HasValue, 2500);
        Assert.NotEqual(FpsStatus.Holding, expired.Status);
        Assert.False(expired.HasValue);
    }

    [Fact]
    public async Task NonFullscreenDesktopWindow_ShowsNoGameInsteadOfItsUiFps()
    {
        var cache = new FpsCache();
        var source = new FakeFrameSource();
        var probe = new FakeProbe { ForegroundProcessId = 2000, ForegroundIsFullscreen = false };
        using var service = new FpsMonitorService(cache, source, probe, NullLog.Instance,
            new FpsSettings { Enabled = true, UpdateIntervalMs = 100 });
        service.Start();

        EmitFrames(source, 2000, 60, 300);
        var snapshot = await WaitFor(service, s => s.Status == FpsStatus.NoGame, 2000);

        Assert.Equal(FpsStatus.NoGame, snapshot.Status);
        Assert.False(snapshot.HasValue);
    }

    [Fact]
    public async Task MeasureWindowedAppsOption_AllowsAWindowedGame()
    {
        var cache = new FpsCache();
        var source = new FakeFrameSource();
        var probe = new FakeProbe { ForegroundProcessId = 2000, ForegroundIsFullscreen = false };
        using var service = new FpsMonitorService(cache, source, probe, NullLog.Instance,
            new FpsSettings { Enabled = true, UpdateIntervalMs = 100, MeasureWindowedApps = true });
        service.Start();

        EmitFrames(source, 2000, 60, 300);
        var snapshot = await WaitFor(service, s => s.HasValue, 1500);

        Assert.Equal(FpsStatus.Capturing, snapshot.Status);
        Assert.InRange(snapshot.Fps, 55, 65);
    }

    [Fact]
    public async Task DisplayedMode_DoesNotUseTheProcessFallback()
    {
        var cache = new FpsCache();
        var source = new FakeFrameSource();
        var probe = new FakeProbe { ForegroundProcessId = 2001 };
        using var service = new FpsMonitorService(cache, source, probe, NullLog.Instance,
            new FpsSettings { Enabled = true, Mode = FpsSourceMode.Displayed, UpdateIntervalMs = 100 });
        service.Start();

        EmitFrames(source, 2000, 60, 300);
        var snapshot = await WaitFor(service, s => s.Status == FpsStatus.DisplayedUnavailable, 1500);

        Assert.Equal(FpsStatus.DisplayedUnavailable, snapshot.Status);
        Assert.False(snapshot.IsProcessFallback);
    }

    [Fact]
    public async Task SwitchingForegroundApplication_SwitchesMeasurement()
    {
        var cache = new FpsCache();
        var source = new FakeFrameSource();
        var probe = new FakeProbe { ForegroundProcessId = 2000 };
        using var service = new FpsMonitorService(cache, source, probe, NullLog.Instance,
            new FpsSettings { Enabled = true, Mode = FpsSourceMode.Presented, UpdateIntervalMs = 100 });
        service.Start();

        var clock = Stopwatch.StartNew();
        double tsA = 0, tsB = 0;
        while (clock.ElapsedMilliseconds < 700)
        {
            source.Emit(2000, tsA);
            tsA += 1000.0 / 60.0;
            source.Emit(2001, tsB);
            tsB += 1000.0 / 144.0;
            Thread.Sleep(1);
        }

        var first = await WaitFor(service, s => s.HasValue && s.ProcessId == 2000);
        Assert.InRange(first.Fps, 55, 65);

        probe.ForegroundProcessId = 2001;
        var second = await WaitFor(service, s => s.HasValue && s.ProcessId == 2001);
        Assert.Equal("game-b", second.ProcessName);
        Assert.InRange(second.Fps, 135, 150);
        Assert.False(second.IsFallback, "explicit Presented mode must not be marked as a fallback");
    }

    [Fact]
    public async Task ForegroundProcessWithoutFrames_ShowsNoFrames()
    {
        var cache = new FpsCache();
        var source = new FakeFrameSource();
        var probe = new FakeProbe { ForegroundProcessId = 2000 };
        using var service = new FpsMonitorService(cache, source, probe, NullLog.Instance,
            new FpsSettings { Enabled = true, UpdateIntervalMs = 100 });
        service.Start();

        EmitFrames(source, 2000, 60, 200);
        await WaitFor(service, s => s.HasValue);

        probe.ForegroundProcessId = 3000;
        // The previous process remains trusted for the 1.5 s ETW freshness window, then NoFrames.
        var snapshot = await WaitFor(service, s => s.Status == FpsStatus.NoFrames, 3500);

        Assert.Equal(FpsStatus.NoFrames, snapshot.Status);
        Assert.False(snapshot.HasValue);
        Assert.False(snapshot.Fps > 0);
    }

    [Fact]
    public async Task NoForegroundWindow_ShowsNoForegroundApp()
    {
        var cache = new FpsCache();
        var source = new FakeFrameSource();
        var probe = new FakeProbe { ForegroundProcessId = 0 };
        using var service = new FpsMonitorService(cache, source, probe, NullLog.Instance,
            new FpsSettings { Enabled = true, UpdateIntervalMs = 100 });
        service.Start();

        var snapshot = await WaitFor(service, s => s.Status == FpsStatus.NoForegroundApp, 1500);
        Assert.Equal(FpsStatus.NoForegroundApp, snapshot.Status);
        Assert.False(snapshot.HasValue);
    }

    [Fact]
    public async Task OwnConfigurationWindow_KeepsPreviousMeasurement()
    {
        var cache = new FpsCache();
        var source = new FakeFrameSource();
        var probe = new FakeProbe { ForegroundProcessId = 2000 };
        using var service = new FpsMonitorService(cache, source, probe, NullLog.Instance,
            new FpsSettings { Enabled = true, UpdateIntervalMs = 100 });
        service.Start();

        EmitFrames(source, 2000, 60, 250);
        var before = await WaitFor(service, s => s.HasValue && s.ProcessId == 2000);
        Assert.True(before.HasValue);

        probe.ForegroundProcessId = probe.OwnProcessId;
        EmitFrames(source, 2000, 60, 250);
        var after = await WaitFor(service, s => s.ProcessId == 2000 && s.HasValue, 1500);
        Assert.True(after.HasValue, "opening the settings window must not blank a still-presenting game");
    }

    [Fact]
    public async Task RequiresElevation_IsReported()
    {
        var cache = new FpsCache();
        var source = new FakeFrameSource { Status = FpsCaptureStatus.RequiresElevation, LastError = "need admin" };
        using var service = new FpsMonitorService(cache, source, new FakeProbe(), NullLog.Instance,
            new FpsSettings { Enabled = true, UpdateIntervalMs = 100 });
        service.Start();

        var snapshot = await WaitFor(service, s => s.Status == FpsStatus.RequiresElevation, 1500);
        Assert.Equal(FpsStatus.RequiresElevation, snapshot.Status);
        Assert.Equal("need admin", snapshot.Message);
        Assert.False(snapshot.HasValue);
        Assert.Equal(2000, snapshot.ProcessId);
        Assert.Equal("game-a", snapshot.ProcessName);
    }

    [Fact]
    public async Task DisplayedMode_IsReportedAsUnavailableInsteadOfSubstituting()
    {
        var cache = new FpsCache();
        var source = new FakeFrameSource();
        var probe = new FakeProbe();
        using var service = new FpsMonitorService(cache, source, probe, NullLog.Instance,
            new FpsSettings { Enabled = true, Mode = FpsSourceMode.Displayed, UpdateIntervalMs = 100 });
        service.Start();

        EmitFrames(source, 2000, 60, 200);
        var snapshot = await WaitFor(service, s => s.Status == FpsStatus.DisplayedUnavailable, 1500);

        Assert.Equal(FpsStatus.DisplayedUnavailable, snapshot.Status);
        Assert.False(snapshot.HasValue, "presented data must never be substituted for displayed FPS");
    }

    [Fact]
    public async Task DisabledService_PublishesDisabled()
    {
        var cache = new FpsCache();
        var source = new FakeFrameSource();
        using var service = new FpsMonitorService(cache, source, new FakeProbe(), NullLog.Instance,
            new FpsSettings { Enabled = false, UpdateIntervalMs = 100 });
        service.Start();

        var snapshot = await WaitFor(service, s => s.Status == FpsStatus.Disabled, 1500);
        Assert.Equal(FpsStatus.Disabled, snapshot.Status);
        Assert.False(snapshot.HasValue);
    }

    [Fact]
    public async Task Stop_ResetsCacheAndStopsSource()
    {
        var cache = new FpsCache();
        var source = new FakeFrameSource();
        using var service = new FpsMonitorService(cache, source, new FakeProbe(), NullLog.Instance,
            new FpsSettings { Enabled = true, UpdateIntervalMs = 100 });
        service.Start();
        EmitFrames(source, 2000, 60, 200);
        await WaitFor(service, s => s.HasValue);

        service.Stop();

        Assert.True(source.Stopped);
        Assert.Equal(FpsStatus.Disabled, cache.Latest.Status);
        Assert.False(service.IsRunning);
    }

    [Fact]
    public async Task AllApplications_MeasuresAWindowedForegroundApp()
    {
        var cache = new FpsCache();
        var source = new FakeFrameSource();
        var probe = new FakeProbe { ForegroundProcessId = 2000, ForegroundIsFullscreen = false };
        var relationships = new FakeRelationshipProbe()
            .With(2000, @"C:\Games\game.exe", "game-a");
        using var service = new FpsMonitorService(
            cache,
            source,
            probe,
            NullLog.Instance,
            new FpsSettings
            {
                Enabled = true,
                CaptureMode = FpsCaptureMode.AllApplications,
                UpdateIntervalMs = 100,
            },
            targetResolver: AllApplications(relationships));

        service.Start();
        EmitFrames(source, 2000, 60, 300);

        var snapshot = await WaitFor(service, s => s.Status == FpsStatus.Capturing);

        Assert.True(snapshot.HasValue);
        Assert.Equal(2000, snapshot.ProcessId);
        Assert.InRange(snapshot.Fps, 55, 65);
        Assert.Equal(FpsStatusReason.Foreground, snapshot.Reason);
    }

    [Fact]
    public async Task AllApplications_DoesNotMeasureAnUnrelatedBackgroundPresenter()
    {
        var cache = new FpsCache();
        var source = new FakeFrameSource();
        var probe = new FakeProbe { ForegroundProcessId = 3000, ForegroundIsFullscreen = false };
        var relationships = new FakeRelationshipProbe()
            .With(3000, @"C:\Windows\System32\notepad.exe", "notepad")
            .With(2000, @"C:\Games\game.exe", "game-a");
        using var service = new FpsMonitorService(
            cache,
            source,
            probe,
            NullLog.Instance,
            new FpsSettings
            {
                Enabled = true,
                CaptureMode = FpsCaptureMode.AllApplications,
                UpdateIntervalMs = 100,
            },
            targetResolver: AllApplications(relationships));

        service.Start();
        EmitFrames(source, 2000, 144, 400);

        var snapshot = await WaitFor(service, s => s.Status == FpsStatus.NoPresenter);

        Assert.Equal(FpsStatus.NoPresenter, snapshot.Status);
        Assert.False(snapshot.HasValue);
        Assert.Equal(0, snapshot.ProcessId);
        Assert.Equal(FpsStatusReason.NotPresenting, snapshot.Reason);
    }

    [Fact]
    public async Task AllApplications_MeasuresTheBrowserGpuSibling()
    {
        var cache = new FpsCache();
        var source = new FakeFrameSource();
        var probe = new FakeProbe { ForegroundProcessId = 5000, ForegroundIsFullscreen = false };
        var relationships = new FakeRelationshipProbe()
            .With(5000, @"C:\Program Files\Google\Chrome\Application\chrome.exe", "chrome")
            .With(5001, @"C:\Program Files\Google\Chrome\Application\chrome.exe", "chrome");
        using var service = new FpsMonitorService(
            cache,
            source,
            probe,
            NullLog.Instance,
            new FpsSettings
            {
                Enabled = true,
                CaptureMode = FpsCaptureMode.AllApplications,
                UpdateIntervalMs = 100,
            },
            targetResolver: AllApplications(relationships));

        service.Start();
        EmitFrames(source, 5001, 120, 400);

        var snapshot = await WaitFor(service, s => s.HasValue);

        Assert.Equal(FpsStatus.Capturing, snapshot.Status);
        Assert.Equal(5001, snapshot.ProcessId);
        Assert.Equal("chrome", snapshot.ProcessName);
        Assert.Equal(FpsStatusReason.SiblingRenderer, snapshot.Reason);
        Assert.InRange(snapshot.Fps, 110, 130);
    }

    [Fact]
    public async Task FocusLoss_HoldsForTheGracePeriodThenShowsNoGame()
    {
        var cache = new FpsCache();
        var source = new FakeFrameSource();
        var probe = new FakeProbe { ForegroundProcessId = 2000 };
        using var service = new FpsMonitorService(cache, source, probe, NullLog.Instance,
            new FpsSettings { Enabled = true, UpdateIntervalMs = 100 });
        service.Start();

        EmitFrames(source, 2000, 60, 300);
        var live = await WaitFor(service, s => s.HasValue && s.Status == FpsStatus.Capturing);
        Assert.True(live.HasValue);

        // A transient focus loss while the game keeps presenting must hold, not blank.
        probe.ForegroundProcessId = 3000;
        probe.ForegroundIsFullscreen = false;
        var held = await EmitUntil(
            source, 2000, 60,
            s => s.Status == FpsStatus.Holding,
            service,
            800);

        Assert.True(held, "the last game value must be held during the 750 ms focus grace");
        Assert.Equal(FpsStatusReason.FocusChange, service.Current.Reason);
        Assert.True(service.Current.HasValue);

        // After the grace, Game Only reports NoGame and never keeps the stale value.
        var noGame = await EmitUntil(
            source, 2000, 60,
            s => s.Status == FpsStatus.NoGame,
            service,
            2000);
        Assert.True(noGame, "NoGame must follow the focus grace");
        Assert.False(service.Current.HasValue);
    }

    [Fact]
    public async Task AllApplications_SwitchesTargetWhenTheForegroundChanges()
    {
        var cache = new FpsCache();
        var source = new FakeFrameSource();
        var probe = new FakeProbe { ForegroundProcessId = 2000, ForegroundIsFullscreen = false };
        var relationships = new FakeRelationshipProbe()
            .With(2000, @"C:\Games\game-a.exe", "game-a")
            .With(2001, @"C:\Apps\app-b.exe", "game-b");
        using var service = new FpsMonitorService(
            cache,
            source,
            probe,
            NullLog.Instance,
            new FpsSettings
            {
                Enabled = true,
                CaptureMode = FpsCaptureMode.AllApplications,
                UpdateIntervalMs = 100,
            },
            targetResolver: AllApplications(relationships));
        service.Start();

        var clock = Stopwatch.StartNew();
        double tsA = 0, tsB = 0;
        while (clock.ElapsedMilliseconds < 500)
        {
            source.Emit(2000, tsA);
            tsA += 1000.0 / 60.0;
            source.Emit(2001, tsB);
            tsB += 1000.0 / 144.0;
            Thread.Sleep(1);
        }

        var first = await WaitFor(service, s => s.HasValue && s.ProcessId == 2000);
        Assert.InRange(first.Fps, 55, 65);

        probe.ForegroundProcessId = 2001;
        var second = await WaitFor(service, s => s.HasValue && s.ProcessId == 2001);
        Assert.Equal("game-b", second.ProcessName);
        Assert.InRange(second.Fps, 135, 150);
    }

    [Fact]
    public async Task ModeSwitch_ResetsTheHeldValueWithoutRestartingTheSource()
    {
        var cache = new FpsCache();
        var source = new FakeFrameSource();
        var probe = new FakeProbe { ForegroundProcessId = 2000, ForegroundIsFullscreen = false };
        var relationships = new FakeRelationshipProbe()
            .With(2000, @"C:\Games\game.exe", "game-a");
        using var service = new FpsMonitorService(
            cache,
            source,
            probe,
            NullLog.Instance,
            new FpsSettings
            {
                Enabled = true,
                CaptureMode = FpsCaptureMode.AllApplications,
                UpdateIntervalMs = 100,
            },
            targetResolver: AllApplications(relationships));
        service.Start();

        EmitFrames(source, 2000, 60, 300);
        await WaitFor(service, s => s.Status == FpsStatus.Capturing);

        service.ApplySettings(new FpsSettings
        {
            Enabled = true,
            CaptureMode = FpsCaptureMode.GameOnly,
            UpdateIntervalMs = 100,
        });

        var noGame = await EmitUntil(
            source, 2000, 60,
            s => s.Status == FpsStatus.NoGame,
            service,
            1500);

        Assert.True(noGame);
        Assert.False(service.Current.HasValue);
        Assert.Equal(1, source.StartCount);
    }

    [Fact]
    public async Task AllApplications_NoPresenterReportsNoValue()
    {
        var cache = new FpsCache();
        var source = new FakeFrameSource();
        var probe = new FakeProbe { ForegroundProcessId = 3000, ForegroundIsFullscreen = false };
        var relationships = new FakeRelationshipProbe()
            .With(3000, @"C:\Windows\System32\notepad.exe", "notepad");
        using var service = new FpsMonitorService(
            cache,
            source,
            probe,
            NullLog.Instance,
            new FpsSettings
            {
                Enabled = true,
                CaptureMode = FpsCaptureMode.AllApplications,
                UpdateIntervalMs = 100,
            },
            targetResolver: AllApplications(relationships));

        service.Start();
        var snapshot = await WaitFor(service, s => s.Status == FpsStatus.NoPresenter);

        Assert.Equal(FpsStatus.NoPresenter, snapshot.Status);
        Assert.False(snapshot.HasValue);
        Assert.Equal(0, snapshot.ProcessId);
    }
}
