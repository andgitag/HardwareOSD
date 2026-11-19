namespace HardwareOSD.Core.Fps;

/// <summary>
/// Short-lived cache of <see cref="ProcessIdentity"/> values. The resolver runs on the publisher
/// thread every 100–250 ms; image paths and parent ids do not change while a process lives, so a
/// 30 s TTL removes almost all process queries without hiding a restarted application for long.
/// A changed start time invalidates the entry so a reused pid is not attributed to the old process.
/// </summary>
public sealed class AppProcessCatalog
{
    public const long DefaultTtlMs = 30_000;

    private readonly IProcessRelationshipProbe _probe;
    private readonly long _ttlMs;
    private readonly object _gate = new();
    private readonly Dictionary<int, Entry> _entries = new();

    private readonly record struct Entry(ProcessIdentity? Identity, long ExpiresMs, long LastSeenMs);

    public AppProcessCatalog(IProcessRelationshipProbe probe, long ttlMs = DefaultTtlMs)
    {
        _probe = probe;
        _ttlMs = Math.Max(0, ttlMs);
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Identity for a pid, or null when the process is gone/protected.</summary>
    public ProcessIdentity? Get(int processId, long nowMs = 0)
    {
        if (processId <= 0)
        {
            return null;
        }

        var now = nowMs != 0 ? nowMs : Environment.TickCount64;
        lock (_gate)
        {
            if (_entries.TryGetValue(processId, out var cached) && cached.ExpiresMs > now)
            {
                return cached.Identity;
            }        }

        ProcessIdentity? identity;
        try
        {
            identity = _probe.GetIdentity(processId);
        }
        catch
        {
            identity = null;
        }

        var expiry = _ttlMs == 0
            ? now + 1
            : now + (identity is null ? Math.Min(_ttlMs, 2000) : _ttlMs);
        lock (_gate)
        {
            _entries[processId] = new Entry(identity, expiry, now);
        }

        return identity;
    }

    /// <summary>Drops a cached identity (pid reuse, process exit, mode switch).</summary>
    public void Clear(int processId)
    {
        lock (_gate)
        {
            _entries.Remove(processId);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    /// <summary>Removes entries whose process has not been seen for <paramref name="maxIdleMs"/>.</summary>
    public void Prune(long nowMs, long maxIdleMs = 10_000)
    {
        lock (_gate)
        {
            if (_entries.Count == 0)
            {
                return;
            }

            List<int>? remove = null;
            foreach (var pair in _entries)
            {
                if (nowMs - pair.Value.LastSeenMs > maxIdleMs)
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
                _entries.Remove(pid);
            }
        }
    }
}
