namespace HardwareOSD.Core.SystemInfo;

/// <summary>
/// Caches the slow machine inventory. WMI/registry queries are never run on the sensor polling
/// thread or the renderer; the UI asks for one background refresh and reuses it for a minute.
/// </summary>
public sealed class SystemInfoService
{
    private readonly ISystemInfoCollector _collector;
    private readonly TimeSpan _ttl;
    private readonly object _gate = new();
    private SystemInfoSnapshot? _cached;
    private Task<SystemInfoSnapshot>? _inflight;

    public SystemInfoService(ISystemInfoCollector collector, TimeSpan? ttl = null)
    {
        _collector = collector;
        _ttl = ttl ?? TimeSpan.FromSeconds(60);
    }

    public SystemInfoSnapshot? Cached
    {
        get
        {
            lock (_gate)
            {
                return _cached;
            }
        }
    }

    public Task<SystemInfoSnapshot> GetAsync(bool force = false)
    {
        lock (_gate)
        {
            if (!force && _cached is not null &&
                DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(_cached.CapturedAtMs) < _ttl)
            {
                return Task.FromResult(_cached);
            }

            if (_inflight is not null)
            {
                return _inflight;
            }

            _inflight = Task.Run(() =>
            {
                SystemInfoSnapshot snapshot;
                try
                {
                    snapshot = _collector.Collect(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                }
                catch (Exception ex)
                {
                    snapshot = SystemInfoSnapshot.Failed(
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        SystemInfoNotes.CollectionFailed + ": " + ex.Message);
                }

                lock (_gate)
                {
                    _cached = snapshot;
                    _inflight = null;
                }

                return snapshot;
            });

            return _inflight;
        }
    }
}
