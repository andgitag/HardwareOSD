using System.Diagnostics;

namespace HardwareOSD.Core.Hardware;

/// <summary>
/// Single-writer / many-reader cache. The polling thread publishes complete immutable snapshots;
/// the renderer reads the latest reference with a single volatile read and never blocks.
/// </summary>
public sealed class SensorCache
{
    private SensorSnapshot _latest = SensorSnapshot.Empty;
    private long _publishCount;

    /// <summary>Latest published snapshot. Never null.</summary>
    public SensorSnapshot Latest => Volatile.Read(ref _latest);

    public long PublishCount => Interlocked.Read(ref _publishCount);

    /// <summary>Publishes a snapshot. Intended to be called from the polling thread only.</summary>
    public void Publish(SensorSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _latest, snapshot);
        Interlocked.Increment(ref _publishCount);
    }

    public bool TryGet(string id, out SensorValue value) => Latest.TryGet(id, out value);

    /// <summary>Resets the cache; used when monitoring is restarted so stale values are not shown.</summary>
    public void Clear()
    {
        Volatile.Write(ref _latest, SensorSnapshot.Empty);
    }
}

/// <summary>Health information about a single provider, published as an immutable record.</summary>
public sealed record ProviderHealth(
    string Name,
    string Category,
    bool Available,
    int ConsecutiveFailures,
    long LastSuccessMs,
    string? LastError);

/// <summary>Immutable polling statistics consumed by the UI/diagnostics.</summary>
public sealed record PollingStatistics(
    bool Running,
    int IntervalMs,
    long PollCount,
    long FailedPollCount,
    double LastPollDurationMs,
    double AveragePollDurationMs,
    long LastPollMs,
    long LastPublishSequence,
    int AvailableSensors,
    IReadOnlyList<ProviderHealth> Providers)
{
    public static readonly PollingStatistics Idle = new(
        false, 0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<ProviderHealth>());
}

/// <summary>Non-blocking access to the statistics of a polling service.</summary>
public sealed class PollingStatisticsBoard
{
    private PollingStatistics _current = PollingStatistics.Idle;

    public PollingStatistics Current => Volatile.Read(ref _current);

    public void Publish(PollingStatistics statistics) => Volatile.Write(ref _current, statistics);
}

/// <summary>Measures elapsed milliseconds without allocating.</summary>
internal static class Clock
{
    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public static long Timestamp() => Stopwatch.GetTimestamp();
    public static double ElapsedMs(long startTimestamp) => Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
}
