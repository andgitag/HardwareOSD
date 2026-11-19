namespace HardwareOSD.Core.Fps;

public readonly record struct FrameRateStats(
    bool HasData,
    double Fps,
    double FrameTimeMs,
    double OnePercentLowFps,
    bool HasOnePercentLow,
    int SampleCount,
    long LastFrameWallMs)
{
    public static readonly FrameRateStats None = new(false, 0, 0, 0, false, 0, 0);
}

/// <summary>
/// Read-only activity summary for one process: freshness wall clock and the number of presents in
/// the last second of event time. <see cref="RecentPresents"/> is a dominance tie-break for target
/// selection only; it is never published as a frame rate and never summed across processes.
/// </summary>
public readonly record struct PresenterActivity(
    int ProcessId,
    long LastPresentWallMs,
    int RecentPresents);

/// <summary>
/// Frame-timing math over per-process ETW present timestamps.
///
/// FPS is computed from the actual interval between the newest frames inside a short trailing
/// window (500 ms by default), so the value reacts to changes within a few hundred milliseconds
/// without inventing intermediate frames or smoothing them away. The 1 % low is the 99th
/// percentile frame time over a longer 2 s window, expressed as FPS.
///
/// The tracker is written by the ETW consumer thread and read by the FPS publisher thread; both
/// paths take a short lock. <see cref="Compute"/> uses preallocated scratch buffers and must be
/// called from a single thread (the publisher).
/// </summary>
public sealed class FrameRateTracker
{
    public const double DefaultWindowMs = 500.0;
    public const double OnePercentLowWindowMs = 2000.0;

    /// <summary>
    /// How long a process may go without a delivered ETW event before it is considered stale.
    /// Real-time ETW buffers are normally flushed about once per second, so a sub-second threshold
    /// makes a continuously rendering game flicker to "--" between flushes. 1.5 s tolerates the
    /// normal delivery cadence without hiding a stopped game for multiple seconds.
    /// </summary>
    public const double StaleThresholdMs = 1500.0;
    private const int Capacity = 2048;
    private const int MinimumOnePercentSamples = 30;

    private sealed class Series
    {
        public readonly double[] Timestamps = new double[Capacity];
        public int Head;
        public int Count;
        public long LastWallMs;
    }

    private readonly object _gate = new();
    private readonly Dictionary<int, Series> _series = new();
    private readonly double[] _samples = new double[Capacity];
    private readonly double[] _window = new double[Capacity];
    private readonly double[] _lowWindow = new double[Capacity];
    private readonly double[] _intervals = new double[Capacity];

    /// <summary>Records one presented frame. Timestamps come from the ETW session clock (ms).</summary>
    public void Add(int processId, double timestampMs, long wallMs)
    {
        if (processId <= 0 || !double.IsFinite(timestampMs))
        {
            return;
        }

        lock (_gate)
        {
            if (!_series.TryGetValue(processId, out var series))
            {
                series = new Series();
                _series[processId] = series;
            }

            var index = (series.Head + series.Count) % Capacity;
            series.Timestamps[index] = timestampMs;
            if (series.Count < Capacity)
            {
                series.Count++;
            }
            else
            {
                series.Head = (series.Head + 1) % Capacity;
            }

            series.LastWallMs = wallMs;
        }
    }

    /// <summary>True when frames for this process arrived recently.</summary>
    public bool HasFreshFrames(int processId, long nowWallMs)
    {
        lock (_gate)
        {
            return _series.TryGetValue(processId, out var series) &&
                   series.Count > 0 &&
                   nowWallMs - series.LastWallMs <= StaleThresholdMs;
        }
    }

    /// <summary>
    /// Returns the freshest process with the most buffered frames. Used only when the foreground
    /// process has no presents, mirroring the reference collector's busiest-process fallback; the
    /// caller still publishes the measured process name so the value is never misattributed.
    /// </summary>
    public bool TryGetBusiestProcess(long nowWallMs, out int processId)
    {
        processId = 0;
        var best = 0;
        lock (_gate)
        {
            foreach (var pair in _series)
            {
                var series = pair.Value;
                if (series.Count < 2 ||
                    nowWallMs - series.LastWallMs > StaleThresholdMs)
                {
                    continue;
                }

                if (series.Count > best)
                {
                    best = series.Count;
                    processId = pair.Key;
                }
            }
        }

        return processId != 0;
    }

    /// <summary>
    /// Copies every process with frames newer than <paramref name="maxAgeMs"/> into
    /// <paramref name="destination"/> and returns the count. The list carries only identity and
    /// activity; callers must select at most one process and never aggregate presents.
    /// </summary>
    public int CopyFreshProcesses(long nowWallMs, int maxAgeMs, List<PresenterActivity> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
        var threshold = Math.Max(1, maxAgeMs);
        lock (_gate)
        {
            foreach (var pair in _series)
            {
                var series = pair.Value;
                if (series.Count == 0 || nowWallMs - series.LastWallMs > threshold)
                {
                    continue;
                }

                var latest = double.MinValue;
                for (var i = 0; i < series.Count; i++)
                {
                    var value = series.Timestamps[(series.Head + i) % Capacity];
                    if (value > latest)
                    {
                        latest = value;
                    }
                }

                var recent = 0;
                for (var i = series.Count - 1; i >= 0; i--)
                {
                    var value = series.Timestamps[(series.Head + i) % Capacity];
                    if (value < latest - 1000.0)
                    {
                        break;
                    }

                    recent++;
                }

                destination.Add(new PresenterActivity(pair.Key, series.LastWallMs, recent));
            }
        }

        return destination.Count;
    }

    public FrameRateStats Compute(int processId, long nowWallMs)
    {
        double latest;
        long lastWall;
        int sampleCount;

        lock (_gate)
        {
            if (!_series.TryGetValue(processId, out var series) || series.Count < 2)
            {
                return FrameRateStats.None;
            }

            lastWall = series.LastWallMs;
            if (nowWallMs - lastWall > StaleThresholdMs)
            {
                return FrameRateStats.None;
            }

            // Copy the ring into the scratch buffer (oldest first) and find the newest frame.
            latest = double.MinValue;
            sampleCount = series.Count;
            for (var i = 0; i < sampleCount; i++)
            {
                var value = series.Timestamps[(series.Head + i) % Capacity];
                _samples[i] = value;
                if (value > latest)
                {
                    latest = value;
                }
            }
        }

        var windowCount = CopyWindow(_samples, sampleCount, latest - DefaultWindowMs, latest, _window);
        var lowCount = CopyWindow(_samples, sampleCount, latest - OnePercentLowWindowMs, latest, _lowWindow);

        if (windowCount < 2)
        {
            return new FrameRateStats(false, 0, 0, 0, false, windowCount, lastWall);
        }

        Array.Sort(_window, 0, windowCount);
        var span = _window[windowCount - 1] - _window[0];
        if (span <= 0)
        {
            return new FrameRateStats(false, 0, 0, 0, false, windowCount, lastWall);
        }

        var fps = (windowCount - 1) * 1000.0 / span;
        var frameTime = 1000.0 / fps;

        var hasLow = false;
        var lowFps = 0.0;
        if (lowCount >= 3)
        {
            Array.Sort(_lowWindow, 0, lowCount);
            var intervalCount = 0;
            for (var i = 1; i < lowCount; i++)
            {
                var delta = _lowWindow[i] - _lowWindow[i - 1];
                if (delta > 0 && double.IsFinite(delta))
                {
                    _intervals[intervalCount++] = delta;
                }
            }

            if (intervalCount >= MinimumOnePercentSamples)
            {
                Array.Sort(_intervals, 0, intervalCount);
                var index = (int)Math.Floor(intervalCount * 0.99);
                var worst = _intervals[Math.Clamp(index, 0, intervalCount - 1)];
                if (worst > 0)
                {
                    lowFps = 1000.0 / worst;
                    hasLow = true;
                }
            }
        }

        return new FrameRateStats(true, fps, frameTime, lowFps, hasLow, windowCount, lastWall);
    }

    /// <summary>Drops processes that have not presented for a while (exited or minimised apps).</summary>
    public void Prune(long nowWallMs, long maxIdleMs = 10_000)
    {
        lock (_gate)
        {
            if (_series.Count == 0)
            {
                return;
            }

            List<int>? remove = null;
            foreach (var pair in _series)
            {
                if (nowWallMs - pair.Value.LastWallMs > maxIdleMs)
                {
                    (remove ??= new List<int>()).Add(pair.Key);
                }
            }

            if (remove is null)
            {
                return;
            }

            foreach (var pid in remove)
            {
                _series.Remove(pid);
            }
        }
    }

    public int TrackedProcessCount
    {
        get
        {
            lock (_gate)
            {
                return _series.Count;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _series.Clear();
        }
    }

    /// <summary>Removes one process's samples (pid reuse or an identity change).</summary>
    public void Clear(int processId)
    {
        lock (_gate)
        {
            _series.Remove(processId);
        }
    }

    /// <summary>Copies samples in [from, to] from a chronological buffer into <paramref name="destination"/>.</summary>
    private static int CopyWindow(double[] source, int count, double from, double to, double[] destination)
    {
        var written = 0;
        for (var i = 0; i < count; i++)
        {
            var value = source[i];
            if (value >= from && value <= to)
            {
                destination[written++] = value;
            }
        }

        return written;
    }
}
