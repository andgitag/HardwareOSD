using HardwareOSD.Core.Fps;

namespace HardwareOSD.Tests;

public class FrameRateTrackerTests
{
    private const long WallBase = 1_000_000;

    private static void AddFrames(FrameRateTracker tracker, int pid, double fps, int count, long wallStart = WallBase, double startMs = 0)
    {
        var interval = 1000.0 / fps;
        for (var i = 0; i < count; i++)
        {
            tracker.Add(pid, startMs + i * interval, wallStart + i);
        }
    }

    [Theory]
    [InlineData(60)]
    [InlineData(144)]
    [InlineData(240)]
    public void ConstantFrameRate_IsMeasuredAccurately(double fps)
    {
        var tracker = new FrameRateTracker();
        AddFrames(tracker, 42, fps, 400);

        var stats = tracker.Compute(42, WallBase + 500);

        Assert.True(stats.HasData);
        Assert.Equal(fps, stats.Fps, 1.0);
        Assert.Equal(1000.0 / fps, stats.FrameTimeMs, 0.5);
    }

    [Fact]
    public void BusiestProcess_SelectsTheProcessWithMostFreshFrames()
    {
        var tracker = new FrameRateTracker();
        AddFrames(tracker, 1, 30, 10);
        AddFrames(tracker, 2, 144, 100);

        Assert.True(tracker.TryGetBusiestProcess(WallBase + 100, out var busiest));
        Assert.Equal(2, busiest);
        Assert.False(tracker.TryGetBusiestProcess(WallBase + 5000, out _));
    }

    [Fact]
    public void ShortEtwDeliveryGap_DoesNotDiscardAFreshProcess()
    {
        var tracker = new FrameRateTracker();
        AddFrames(tracker, 42, 240, 50);

        // Real-time ETW buffers are typically flushed about once per second. A 700 ms gap between
        // deliveries must not turn a continuously rendering game into "no frames".
        Assert.True(tracker.HasFreshFrames(42, WallBase + 700));
        Assert.True(tracker.Compute(42, WallBase + 700).HasData);

        // A stopped process is still detected after the 1.5 s freshness window.
        Assert.False(tracker.HasFreshFrames(42, WallBase + 2000));
    }

    [Fact]
    public void StaleProcess_ReportsNoData()
    {
        var tracker = new FrameRateTracker();
        AddFrames(tracker, 42, 60, 100);

        Assert.False(tracker.Compute(42, WallBase + 2000).HasData);
        Assert.False(tracker.HasFreshFrames(42, WallBase + 2000));
        Assert.True(tracker.HasFreshFrames(42, WallBase + 100));
    }

    [Fact]
    public void UsesOnlyTrailingWindow_SoRateChangesAreReflectedQuickly()
    {
        var tracker = new FrameRateTracker();
        // 1 second at 30 FPS, a 1 s pause, then 400 ms at 120 FPS.
        AddFrames(tracker, 7, 30, 31);
        AddFrames(tracker, 7, 120, 49, WallBase + 31, startMs: 2000);

        var stats = tracker.Compute(7, WallBase + 80);

        // The 500 ms window only sees the fast segment.
        Assert.True(stats.HasData);
        Assert.InRange(stats.Fps, 110, 130);
    }

    [Fact]
    public void SingleHitch_LowersTheMeasuredRate()
    {
        var steady = new FrameRateTracker();
        AddFrames(steady, 1, 60, 20);
        var steadyStats = steady.Compute(1, WallBase + 25);

        var hitching = new FrameRateTracker();
        AddFrames(hitching, 1, 60, 10);
        // 100 ms stall in the middle of a short burst, then frames resume.
        AddFrames(hitching, 1, 60, 10, WallBase + 10, startMs: 150 + 100);
        var hitchingStats = hitching.Compute(1, WallBase + 15);

        Assert.True(hitchingStats.HasData);
        Assert.True(hitchingStats.Fps < steadyStats.Fps - 5,
            $"expected the hitch to reduce FPS ({hitchingStats.Fps:0.0} vs {steadyStats.Fps:0.0})");
    }

    [Fact]
    public void OnePercentLow_UsesWorstFrameTimes()
    {
        var tracker = new FrameRateTracker();
        var timestamp = 0.0;
        for (var i = 0; i < 400; i++)
        {
            // 10 % of frames take 40 ms (25 FPS); the rest run at 120 FPS.
            timestamp += i % 10 == 0 ? 40.0 : 1000.0 / 120.0;
            tracker.Add(3, timestamp, WallBase + (long)timestamp);
        }

        var stats = tracker.Compute(3, WallBase + (long)timestamp + 5);
        Assert.True(stats.HasData);
        Assert.True(stats.HasOnePercentLow);
        Assert.InRange(stats.OnePercentLowFps, 24, 26);
        Assert.True(stats.OnePercentLowFps < stats.Fps, "the 1 % low must be lower than the average rate");
    }

    [Fact]
    public void ProcessesAreTrackedIndependently()
    {
        var tracker = new FrameRateTracker();
        AddFrames(tracker, 10, 60, 200);
        AddFrames(tracker, 20, 144, 200);

        Assert.Equal(60, tracker.Compute(10, WallBase + 300).Fps, 1);
        Assert.Equal(144, tracker.Compute(20, WallBase + 300).Fps, 1);
        Assert.Equal(2, tracker.TrackedProcessCount);
    }

    [Fact]
    public void OutOfOrderTimestamps_AreHandled()
    {
        var tracker = new FrameRateTracker();
        var values = Enumerable.Range(0, 200).Select(i => i * 1000.0 / 60.0).ToList();
        // Swap some neighbours to simulate interleaved providers.
        for (var i = 0; i < values.Count - 1; i += 7)
        {
            (values[i], values[i + 1]) = (values[i + 1], values[i]);
        }

        foreach (var value in values)
        {
            tracker.Add(5, value, WallBase + (long)value);
        }

        var stats = tracker.Compute(5, WallBase + (long)values.Max() + 5);
        Assert.True(stats.HasData);
        Assert.Equal(60, stats.Fps, 1);
    }

    [Fact]
    public void Prune_RemovesIdleProcesses()
    {
        var tracker = new FrameRateTracker();
        AddFrames(tracker, 1, 60, 20);
        AddFrames(tracker, 2, 60, 20);

        tracker.Prune(WallBase + 60_000);
        Assert.Equal(0, tracker.TrackedProcessCount);
    }

    [Fact]
    public void InvalidSamples_AreIgnored()
    {
        var tracker = new FrameRateTracker();
        tracker.Add(0, 0, WallBase);
        tracker.Add(-5, 10, WallBase);
        tracker.Add(1, double.NaN, WallBase);
        Assert.Equal(0, tracker.TrackedProcessCount);
    }

    [Fact]
    public void CopyFreshProcesses_ReturnsOnlyFreshProcesses()
    {
        var tracker = new FrameRateTracker();
        AddFrames(tracker, 1, 60, 60);            // newest event time 983 ms, wall 1_000_059
        AddFrames(tracker, 2, 60, 10, wallStart: WallBase + 900);

        var fresh = new List<PresenterActivity>();
        var count = tracker.CopyFreshProcesses(WallBase + 1000, 1500, fresh);

        Assert.Equal(2, count);
        var first = fresh.Single(a => a.ProcessId == 1);
        var second = fresh.Single(a => a.ProcessId == 2);
        Assert.True(first.RecentPresents > 0);
        Assert.Equal(10, second.RecentPresents); // all ten events are inside the last second

        // Both become stale beyond the 1.5 s freshness threshold.
        Assert.Equal(0, tracker.CopyFreshProcesses(WallBase + 3000, 1500, fresh));
    }

    [Fact]
    public void Clear_RemovesOneProcessOnly()
    {
        var tracker = new FrameRateTracker();
        AddFrames(tracker, 1, 60, 20);
        AddFrames(tracker, 2, 60, 20);

        tracker.Clear(1);

        Assert.Equal(1, tracker.TrackedProcessCount);
        Assert.False(tracker.HasFreshFrames(1, WallBase + 10));
        Assert.True(tracker.HasFreshFrames(2, WallBase + 10));
    }
}
