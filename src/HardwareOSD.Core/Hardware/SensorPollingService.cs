using System.Diagnostics;
using HardwareOSD.Core.Diagnostics;
using HardwareOSD.Core.Hardware.Providers;

namespace HardwareOSD.Core.Hardware;

/// <summary>
/// Background sensor polling service. Owns its own dedicated thread so hardware access can never
/// block the UI or the overlay renderer. Each poll publishes one immutable snapshot.
/// </summary>
public sealed class SensorPollingService : IDisposable
{
    private readonly ILog _log;
    private readonly ISensorProvider[] _providers;
    private readonly SensorCache _cache;
    private readonly SensorHistory _history;
    private readonly SensorMergePolicy _mergePolicy = new();
    private readonly PollingStatisticsBoard _statistics = new();
    private readonly object _lifecycle = new();

    private readonly ProviderRuntime[] _providerStates;
    private readonly ManualResetEventSlim _wake = new(false);

    private Thread? _thread;
    private volatile bool _running;
    private volatile int _intervalMs;
    private long _sequence;
    private long _pollCount;
    private long _failedPolls;
    private double _avgPollMs;
    private bool _disposed;

    public SensorPollingService(
        IEnumerable<ISensorProvider> providers,
        SensorCache cache,
        SensorHistory history,
        ILog log,
        int intervalMs = 1000)
    {
        _providers = providers.ToArray();
        _cache = cache;
        _history = history;
        _log = log;
        _intervalMs = Math.Clamp(intervalMs, 100, 10_000);
        _providerStates = _providers.Select(p => new ProviderRuntime(p)).ToArray();
    }

    public bool IsRunning => _running;

    /// <summary>Whether the ACPI thermal zone may stand in for a missing CPU package sensor.</summary>
    public bool UseThermalZoneAsCpuTemperature { get; set; } = true;

    public int IntervalMs
    {
        get => _intervalMs;
        set
        {
            var clamped = Math.Clamp(value, 100, 10_000);
            if (clamped == _intervalMs)
            {
                return;
            }

            _intervalMs = clamped;
            _wake.Set();
            _log.Info("Polling", $"Polling interval set to {clamped} ms");
        }
    }

    public PollingStatistics Statistics => _statistics.Current;

    /// <summary>Finds a provider instance for typed diagnostics; never triggers a hardware read.</summary>
    public T? FindProvider<T>() where T : class, ISensorProvider
        => _providerStates.Select(state => state.Provider).OfType<T>().FirstOrDefault();

    /// <summary>
    /// Snapshot of each provider's own diagnostic description. Called by the Diagnostics page and
    /// the copy-diagnostics report; it never triggers a sensor read.
    /// </summary>
    public IReadOnlyList<string> DescribeProviders()
    {
        var lines = new List<string>(_providerStates.Length * 6);
        foreach (var state in _providerStates)
        {
            lines.Add($"{state.Provider.Name} [{state.Provider.Category}]");
            lines.Add($"  available={state.Provider.IsAvailable} failures={state.ConsecutiveFailures}" +
                      (string.IsNullOrWhiteSpace(state.LastError) ? string.Empty : " error=" + state.LastError));
            try
            {
                foreach (var line in state.Provider.Describe())
                {
                    lines.Add("  " + line);
                }
            }
            catch (Exception ex)
            {
                lines.Add("  description failed: " + ex.Message);
            }
        }

        return lines;
    }

    public void Start()
    {
        lock (_lifecycle)
        {
            if (_disposed || _running)
            {
                return;
            }

            _running = true;
            _wake.Reset();
            _thread = new Thread(PollLoop)
            {
                IsBackground = true,
                Name = "HardwareOSD.SensorPolling",
                Priority = ThreadPriority.BelowNormal,
            };
            _thread.Start();
            _log.Info("Polling", $"Monitoring started (interval {_intervalMs} ms, {_providers.Length} providers)");
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (_lifecycle)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            _wake.Set();
            thread = _thread;
            _thread = null;
        }

        try
        {
            thread?.Join(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _log.Warn("Polling", "Failed to join polling thread", ex);
        }

        // Never keep stale hardware values around after monitoring stops.
        _cache.Clear();
        PublishStatistics();
        _log.Info("Polling", "Monitoring stopped");
    }

    private void PollLoop()
    {
        // Stagger initialisation so the first snapshot arrives as quickly as possible while
        // each provider is still isolated.
        foreach (var state in _providerStates)
        {
            TryInitialize(state);
        }

        while (_running)
        {
            try
            {
                PollOnce();
            }
            catch (Exception ex)
            {
                _failedPolls++;
                _log.Error("Polling", "Unhandled polling failure", ex);
            }

            var wait = _intervalMs;
            _wake.Wait(wait);
            _wake.Reset();
        }

        PublishStatistics();
    }

    private void PollOnce()
    {
        var start = Clock.Timestamp();
        var now = Clock.NowMs();
        var outputs = new List<(string, IReadOnlyList<SensorValue>)>(_providers.Length);

        foreach (var state in _providerStates)
        {
            if (!state.Provider.IsAvailable)
            {
                // Retry initialisation with backoff so a temporarily missing GPU/driver can recover.
                if (now - state.LastInitAttemptMs >= state.RetryDelayMs)
                {
                    TryInitialize(state);
                }

                if (!state.Provider.IsAvailable)
                {
                    continue;
                }
            }

            try
            {
                var values = state.Provider.Read(now);
                state.ConsecutiveFailures = 0;
                state.LastSuccessMs = now;
                state.LastError = null;
                outputs.Add((state.Provider.Key, values));
            }
            catch (Exception ex)
            {
                state.ConsecutiveFailures++;
                state.LastError = ex.Message;
                if (state.ConsecutiveFailures == 1)
                {
                    _log.Warn("Provider", $"{state.Provider.Name} read failed", ex);
                }

                if (state.ConsecutiveFailures >= 3)
                {
                    // Force a re-initialisation attempt on subsequent polls.
                    state.Provider.Dispose();
                    state.RetryDelayMs = 15_000;
                    state.LastInitAttemptMs = now - 10_000;
                    _log.Warn("Provider", $"{state.Provider.Name} disabled after {state.ConsecutiveFailures} consecutive failures");
                }
            }
        }

        var merged = _mergePolicy.Merge(outputs);
        SensorPostProcessor.Sanitize(merged, now);
        SensorPostProcessor.Derive(merged, now, UseThermalZoneAsCpuTemperature);

        var snapshot = SensorSnapshot.Create(Interlocked.Increment(ref _sequence), now, merged);
        _cache.Publish(snapshot);
        _history.Append(snapshot);

        var elapsed = Clock.ElapsedMs(start);
        _pollCount++;
        _avgPollMs = _avgPollMs <= 0 ? elapsed : (_avgPollMs * 0.9) + (elapsed * 0.1);
        LastDuration = elapsed;
        PublishStatistics(snapshot);
    }

    private double LastDuration { get; set; }

    private void TryInitialize(ProviderRuntime state)
    {
        state.LastInitAttemptMs = Clock.NowMs();
        state.RetryDelayMs = 15_000;
        try
        {
            state.Provider.Initialize();
            state.ConsecutiveFailures = 0;
            state.LastError = null;
            _log.Info("Provider", $"{state.Provider.Name} initialised");
        }
        catch (Exception ex)
        {
            state.LastError = ex.Message;
            _log.Warn("Provider", $"{state.Provider.Name} initialisation failed", ex);
        }
    }

    private void PublishStatistics(SensorSnapshot? snapshot = null)
    {
        var current = snapshot ?? _cache.Latest;
        var providers = new ProviderHealth[_providerStates.Length];
        for (var i = 0; i < _providerStates.Length; i++)
        {
            var s = _providerStates[i];
            providers[i] = new ProviderHealth(
                s.Provider.Name,
                s.Provider.Category,
                s.Provider.IsAvailable && string.IsNullOrWhiteSpace(s.LastError),
                s.ConsecutiveFailures,
                s.LastSuccessMs,
                s.LastError);
        }

        _statistics.Publish(new PollingStatistics(
            _running,
            _intervalMs,
            Interlocked.Read(ref _pollCount),
            _failedPolls,
            LastDuration,
            _avgPollMs,
            current.CapturedAtMs,
            current.Sequence,
            current.Values.Count(v => v.Value.Available),
            providers));
    }

    public void Dispose()
    {
        Stop();
        lock (_lifecycle)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        foreach (var state in _providerStates)
        {
            try
            {
                state.Provider.Dispose();
            }
            catch (Exception ex)
            {
                _log.Warn("Provider", $"Disposing {state.Provider.Name} failed", ex);
            }
        }

        _wake.Dispose();
    }

    private sealed class ProviderRuntime(ISensorProvider provider)
    {
        public ISensorProvider Provider { get; } = provider;
        public int ConsecutiveFailures { get; set; }
        public long LastSuccessMs { get; set; }
        public long LastInitAttemptMs { get; set; }
        public long RetryDelayMs { get; set; } = 15_000;
        public string? LastError { get; set; }
    }
}

