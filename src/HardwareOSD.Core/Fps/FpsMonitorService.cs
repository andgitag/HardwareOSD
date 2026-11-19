using HardwareOSD.Core.Config;
using HardwareOSD.Core.Diagnostics;
using HardwareOSD.Core.Hardware;

namespace HardwareOSD.Core.Fps;

/// <summary>
/// Owns the FPS measurement pipeline: frame events arrive on the capture thread, land in a
/// per-process timing tracker, and a publisher thread converts them into an immutable
/// <see cref="FpsSnapshot"/> every 100–500 ms. The overlay only reads the snapshot.
///
/// The service never elevates, never prompts and never invents a rate: when capture is not
/// permitted or the foreground application is not presenting, the snapshot says so and the
/// overlay renders "--".
/// </summary>
public sealed class FpsMonitorService : IDisposable
{
    private readonly FpsCache _cache;
    private readonly IFrameEventSource _source;
    private readonly IForegroundAppProbe _probe;
    private readonly ILog _log;
    private readonly SensorHistory? _history;
    private readonly FrameRateTracker _tracker = new();
    private readonly AllApplicationsTargetResolver? _resolver;
    private readonly List<PresenterActivity> _fresh = new(16);
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly object _settingsGate = new();
    private readonly int _demoProcessId;

    private FpsSettings _settings;
    private Thread? _publisher;
    private volatile bool _running;
    private long _sequence;
    private int _lastPresentingProcessId;
    private int _iteration;
    private bool _disposed;

    private const int ValueHoldMs = 2500;
    private const int FocusGraceMs = 750;
    private long _lastValidTimestampMs;
    private double _lastValidFps;
    private double _lastValidFrameTime;
    private double _lastValidOnePercentLow;
    private int _lastValidProcessId;
    private string? _lastValidProcessName;
    private bool _lastValidProcessFallback;
    private int _lastForegroundPid;
    private long _lastForegroundChangeMs;

    public FpsMonitorService(
        FpsCache cache,
        IFrameEventSource source,
        IForegroundAppProbe probe,
        ILog log,
        FpsSettings settings,
        SensorHistory? history = null,
        int demoProcessId = 0,
        AllApplicationsTargetResolver? targetResolver = null)
    {
        _cache = cache;
        _source = source;
        _probe = probe;
        _log = log;
        _history = history;
        _demoProcessId = demoProcessId;
        _resolver = targetResolver ??
                    new AllApplicationsTargetResolver(
                        new Win32ProcessRelationshipProbe(),
                        _probe.OwnProcessId);
        _settings = settings.Clone();
        _settings.Normalize();
    }

    public FpsSnapshot Current => _cache.Latest;

    public FpsCaptureStatus SourceStatus => _source.Status;

    public string SourceName => _source.Name;

    public string SourceDescription => _source.Description;

    public string? SourceError => _source.LastError;

    public bool IsRunning => _running;

    public FrameRateTracker Tracker => _tracker;

    public long CaptureEventCount => (_source as EtwFrameEventSource)?.TotalEventCount ?? 0;

    public long CapturePresentCount => (_source as EtwFrameEventSource)?.PresentEventCount ?? 0;

    public long LastPresentWallMs => (_source as EtwFrameEventSource)?.LastPresentWallMs ?? 0;

    public int LastPresentProcessId => (_source as EtwFrameEventSource)?.LastPresentProcessId ?? 0;

    public string? CaptureSessionName => (_source as EtwFrameEventSource)?.SessionName;

    public void Start()
    {
        if (_disposed || _running)
        {
            return;
        }

        _running = true;
        _source.FramePresented += OnFramePresented;
        _source.Start();

        _publisher = new Thread(PublishLoop)
        {
            IsBackground = true,
            Name = "HardwareOSD.FpsPublisher",
            Priority = ThreadPriority.BelowNormal,
        };
        _publisher.Start();

        _log.Info("FPS", $"FPS monitor started ({_source.Name}, interval {_settings.UpdateIntervalMs} ms)");
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _wake.Set();
        _source.FramePresented -= OnFramePresented;
        _source.Stop();

        try
        {
            _publisher?.Join(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _log.Warn("FPS", "Failed to join the FPS publisher thread", ex);
        }

        _publisher = null;
        _tracker.Clear();
        _cache.Reset();
        _sequence = 0;
        _lastPresentingProcessId = 0;
        _lastForegroundPid = 0;
        _lastForegroundChangeMs = 0;
        _resolver?.Reset();
        ResetHeldValue();
        _log.Info("FPS", "FPS monitor stopped");
    }

    public void ApplySettings(FpsSettings settings)
    {
        var clone = settings.Clone();
        clone.Normalize();
        var previousMode = Settings.CaptureMode;
        lock (_settingsGate)
        {
            _settings = clone;
        }

        if (previousMode != clone.CaptureMode)
        {
            // The ETW session and tracker stay alive; only selection/hold state is reset so the new
            // mode cannot publish a target chosen under the old rules.
            ResetHeldValue();
            _lastPresentingProcessId = 0;
            _resolver?.Reset();
        }

        _wake.Set();
    }

    private FpsSettings Settings
    {
        get
        {
            lock (_settingsGate)
            {
                return _settings;
            }
        }
    }

    private void OnFramePresented(int processId, double timestampMs)
    {
        try
        {
            _tracker.Add(processId, timestampMs, Environment.TickCount64);
        }
        catch (Exception ex)
        {
            _log.Debug("FPS", "Frame timestamp was rejected: " + ex.Message);
        }
    }

    private void PublishLoop()
    {
        while (_running)
        {
            try
            {
                PublishOnce();
            }
            catch (Exception ex)
            {
                _log.Warn("FPS", "FPS publisher iteration failed", ex);
            }

            var interval = Settings.UpdateIntervalMs;
            _wake.Wait(interval);
            _wake.Reset();
        }
    }

    private void PublishOnce()
    {
        var settings = Settings;
        var now = Environment.TickCount64;
        if (++_iteration % 40 == 0)
        {
            _tracker.Prune(now);
            _resolver?.Prune(now);
        }

        var status = _source.Status;
        var resultStatus = FpsStatus.Disabled;
        var source = FpsSource.None;
        var fallback = false;
        var fps = 0.0;
        var frameTime = 0.0;
        var onePercentLow = 0.0;
        var processId = 0;
        string? processName = null;
        string? message = null;
        var processFallback = false;
        var reason = FpsStatusReason.None;

        // Detect the foreground application even while capture is unavailable, so diagnostics can
        // show which process the FPS state refers to (e.g. CS2 plus "administrator rights required").
        var foreground = 0;
        var foregroundEligible = true;
        var windowInfo = new ForegroundWindowInfo(0, false);
        if (settings.Enabled)
        {
            windowInfo = _probe is IForegroundWindowProbe windowProbe
                ? windowProbe.GetForegroundWindowInfo()
                : new ForegroundWindowInfo(_probe.GetForegroundProcessId(), true);
            foreground = _demoProcessId != 0 ? _demoProcessId : windowInfo.ProcessId;
            ObserveForeground(foreground, now);
            foregroundEligible = settings.MeasureWindowedApps ||
                                 windowInfo.IsFullscreen ||
                                 windowInfo.ProcessId == _probe.OwnProcessId ||
                                 windowInfo.ProcessId == 0;

            // Game Only: if our own configuration window is in front, keep reporting the application
            // that was being measured while it still has fresh frames. Any other foreground
            // application wins; desktop/UI windows are handled by the fullscreen gate above. All
            // Applications resolves this case in its own ladder.
            if (settings.CaptureMode == FpsCaptureMode.GameOnly &&
                _demoProcessId == 0 &&
                foreground != 0 &&
                foreground == _probe.OwnProcessId &&
                _lastPresentingProcessId != 0 &&
                _tracker.HasFreshFrames(_lastPresentingProcessId, now))
            {
                foreground = _lastPresentingProcessId;
            }

            if (foreground > 0)
            {
                processId = foreground;
                processName = ProcessNameFor(foreground);
            }
        }

        if (!settings.Enabled)
        {
            resultStatus = FpsStatus.Disabled;
        }
        else if (status == FpsCaptureStatus.RequiresElevation)
        {
            resultStatus = FpsStatus.RequiresElevation;
            message = _source.LastError;
        }
        else if (status == FpsCaptureStatus.Failed)
        {
            resultStatus = FpsStatus.Error;
            message = _source.LastError;
        }
        else if (status is FpsCaptureStatus.Stopped or FpsCaptureStatus.Starting)
        {
            resultStatus = FpsStatus.Starting;
        }
        else if (settings.CaptureMode == FpsCaptureMode.AllApplications)
        {
            var resolverForeground = _demoProcessId != 0
                ? windowInfo with { ProcessId = _demoProcessId }
                : windowInfo;
            PublishAllApplications(
                settings,
                now,
                resolverForeground,
                ref resultStatus,
                ref source,
                ref fallback,
                ref fps,
                ref frameTime,
                ref onePercentLow,
                ref processId,
                ref processName,
                ref processFallback,
                ref message,
                ref reason);
        }
        else
        {
            if (!foregroundEligible)
            {
                // A transient focus loss (alt-tab, UAC, launcher, notification) must not blank a
                // game that is still presenting. Hold the last real value for 750 ms only; after
                // that the normal NoGame state applies. Closing the game cannot be hidden by this
                // path because it also requires fresh frames.
                if (CanHoldAcrossFocusChange(now))
                {
                    ApplyHeldValue(ref resultStatus, ref source, ref fallback, ref fps, ref frameTime,
                        ref onePercentLow, ref processId, ref processName, ref processFallback, ref message, ref reason,
                        FpsStatusReason.FocusChange);
                }
                else
                {
                    resultStatus = FpsStatus.NoGame;
                    reason = FpsStatusReason.None;
                    message = "The foreground window is not a fullscreen game. " +
                              "Enable 'Measure windowed apps' for windowed applications.";
                }
            }
            else if (foreground > 0 && settings.Mode == FpsSourceMode.Displayed)
            {
                resultStatus = FpsStatus.DisplayedUnavailable;
                message = "Displayed FPS requires DWM present correlation, which the ETW backend does not provide. " +
                          "Use Automatic or Presented.";
            }
            else
            {
                // The ETW buffer normally flushes about once per second. If the foreground game is
                // still the process whose value we last measured, keep that real value for a short
                // grace period instead of flashing "--" between flushes. This is never interpolated:
                // it is the last computed rate, and it expires after ValueHoldMs.
                var holdAllowed = _lastValidProcessId != 0 &&
                                  now - _lastValidTimestampMs <= ValueHoldMs &&
                                  (foreground == _lastValidProcessId ||
                                   foreground == _probe.OwnProcessId ||
                                   foreground == 0);

                var measured = foreground;
                var hasFresh = measured > 0 && _tracker.HasFreshFrames(measured, now);

                if (holdAllowed && !hasFresh)
                {
                    ApplyHeldValue(ref resultStatus, ref source, ref fallback, ref fps, ref frameTime,
                        ref onePercentLow, ref processId, ref processName, ref processFallback, ref message, ref reason,
                        FpsStatusReason.EtwGap);
                }
                else if (measured <= 0)
                {
                    resultStatus = FpsStatus.NoForegroundApp;
                    reason = FpsStatusReason.NoForeground;
                }
                else if (!hasFresh)
                {
                    resultStatus = FpsStatus.NoFrames;
                    reason = FpsStatusReason.NotPresenting;
                    message = $"{processName ?? "The foreground application"} is not presenting frames through DXGI/D3D9.";
                }
                else
                {
                    var stats = _tracker.Compute(measured, now);
                    if (!stats.HasData)
                    {
                        if (holdAllowed && measured == _lastValidProcessId)
                        {
                            ApplyHeldValue(ref resultStatus, ref source, ref fallback, ref fps, ref frameTime,
                                ref onePercentLow, ref processId, ref processName, ref processFallback, ref message, ref reason,
                                FpsStatusReason.EtwGap);
                        }
                        else
                        {
                            resultStatus = FpsStatus.NoFrames;
                            reason = FpsStatusReason.NotPresenting;
                            message = "Waiting for enough frame samples.";
                        }
                    }
                    else
                    {
                        resultStatus = FpsStatus.Capturing;
                        source = FpsSource.Presented;
                        fallback = settings.Mode == FpsSourceMode.Automatic;
                        fps = stats.Fps;
                        frameTime = stats.FrameTimeMs;
                        onePercentLow = stats.OnePercentLowFps;
                        reason = FpsStatusReason.Foreground;
                        _lastPresentingProcessId = measured;
                        _lastValidTimestampMs = now;
                        _lastValidFps = fps;
                        _lastValidFrameTime = frameTime;
                        _lastValidOnePercentLow = onePercentLow;
                        _lastValidProcessId = measured;
                        _lastValidProcessName = processName;
                        _lastValidProcessFallback = processFallback;
                    }
                }
            }
        }

        if (settings.IsIgnored(processName) && resultStatus is FpsStatus.Capturing or FpsStatus.Holding)
        {
            resultStatus = FpsStatus.NoPresenter;
            reason = FpsStatusReason.Ignored;
            message = $"{processName} is on the FPS ignore list.";
            source = FpsSource.None;
            fallback = false;
            fps = 0;
            frameTime = 0;
            onePercentLow = 0;
        }

        var snapshot = new FpsSnapshot(
            Interlocked.Increment(ref _sequence),
            now,
            resultStatus,
            source,
            fallback,
            processId,
            processName,
            fps,
            frameTime,
            onePercentLow,
            message,
            processFallback,
            reason);

        _cache.Publish(snapshot);

        if (_history is not null)
        {
            _history.AppendValue("frame.fps", snapshot.HasValue ? fps : double.NaN);
            _history.AppendValue("frame.time", snapshot.HasValue ? frameTime : double.NaN);
            _history.AppendValue("frame.fps.1low", snapshot.HasValue ? onePercentLow : double.NaN);
        }
    }

    private void ObserveForeground(int processId, long nowMs)
    {
        if (processId != _lastForegroundPid)
        {
            _lastForegroundPid = processId;
            _lastForegroundChangeMs = nowMs;
        }
    }

    private bool CanHoldAcrossFocusChange(long nowMs)
        => _lastValidProcessId != 0 &&
           nowMs - _lastValidTimestampMs <= ValueHoldMs &&
           _tracker.HasFreshFrames(_lastValidProcessId, nowMs) &&
           nowMs - _lastForegroundChangeMs <= FocusGraceMs;

    /// <summary>
    /// All Applications selection and measurement. The ETW source, tracker and maths are the same
    /// as Game Only; only the chosen pid differs. Exactly one target is measured per publish.
    /// </summary>
    private void PublishAllApplications(
        FpsSettings settings,
        long now,
        ForegroundWindowInfo windowInfo,
        ref FpsStatus resultStatus,
        ref FpsSource source,
        ref bool fallback,
        ref double fps,
        ref double frameTime,
        ref double onePercentLow,
        ref int processId,
        ref string? processName,
        ref bool processFallback,
        ref string? message,
        ref FpsStatusReason reason)
    {
        if (settings.Mode == FpsSourceMode.Displayed)
        {
            resultStatus = FpsStatus.DisplayedUnavailable;
            message = "Displayed FPS requires DWM present correlation, which the ETW backend does not provide. " +
                      "Use Automatic or Presented.";
            return;
        }

        _fresh.Clear();
        _tracker.CopyFreshProcesses(now, (int)FrameRateTracker.StaleThresholdMs, _fresh);
        var target = _resolver!.Resolve(windowInfo, _fresh, now);
        reason = target.Reason;

        if (!target.IsEligible)
        {
            processId = 0;
            processName = null;
            switch (target.Reason)
            {
                case FpsStatusReason.NoForeground:
                    resultStatus = FpsStatus.NoForegroundApp;
                    reason = FpsStatusReason.NoForeground;
                    break;
                case FpsStatusReason.Desktop:
                    resultStatus = FpsStatus.NoPresenter;
                    message = "The Windows desktop is composed by DWM; application FPS is undefined.";
                    break;
                case FpsStatusReason.Ambiguous:
                    resultStatus = FpsStatus.NoPresenter;
                    message = "Several related processes are presenting; the target is ambiguous, so no value is shown.";
                    break;
                default:
                    resultStatus = FpsStatus.NoPresenter;
                    message = "The foreground application does not present frames through DXGI/D3D9.";
                    break;
            }

            return;
        }

        var measured = target.ProcessId;
        processId = measured;
        processName = target.ProcessName ?? ProcessNameFor(measured);

        var hasFresh = _tracker.HasFreshFrames(measured, now);
        var holdAllowed = measured == _lastValidProcessId &&
                          now - _lastValidTimestampMs <= ValueHoldMs;

        if (!hasFresh)
        {
            if (holdAllowed)
            {
                ApplyHeldValue(ref resultStatus, ref source, ref fallback, ref fps, ref frameTime,
                    ref onePercentLow, ref processId, ref processName, ref processFallback, ref message, ref reason,
                    FpsStatusReason.EtwGap);
            }
            else
            {
                resultStatus = FpsStatus.NoFrames;
                reason = FpsStatusReason.NotPresenting;
                message = $"{processName ?? "The foreground application"} is not presenting frames through DXGI/D3D9.";
            }

            return;
        }

        var stats = _tracker.Compute(measured, now);
        if (!stats.HasData)
        {
            if (holdAllowed)
            {
                ApplyHeldValue(ref resultStatus, ref source, ref fallback, ref fps, ref frameTime,
                    ref onePercentLow, ref processId, ref processName, ref processFallback, ref message, ref reason,
                    FpsStatusReason.EtwGap);
            }
            else
            {
                resultStatus = FpsStatus.NoFrames;
                reason = FpsStatusReason.NotPresenting;
                message = "Waiting for enough frame samples.";
            }

            return;
        }

        resultStatus = FpsStatus.Capturing;
        source = FpsSource.Presented;
        fallback = settings.Mode == FpsSourceMode.Automatic;
        fps = stats.Fps;
        frameTime = stats.FrameTimeMs;
        onePercentLow = stats.OnePercentLowFps;
        reason = target.Reason;
        _lastPresentingProcessId = measured;
        _lastValidTimestampMs = now;
        _lastValidFps = fps;
        _lastValidFrameTime = frameTime;
        _lastValidOnePercentLow = onePercentLow;
        _lastValidProcessId = measured;
        _lastValidProcessName = processName;
        _lastValidProcessFallback = processFallback;
    }

    private string? ProcessNameFor(int processId)
        => _demoProcessId != 0
            ? processId == _demoProcessId ? "HardwareOSD (demo)" : $"pid {processId}"
            : _probe.GetProcessName(processId);

    private void ApplyHeldValue(
        ref FpsStatus status,
        ref FpsSource source,
        ref bool fallback,
        ref double fps,
        ref double frameTime,
        ref double onePercentLow,
        ref int processId,
        ref string? processName,
        ref bool processFallback,
        ref string? message,
        ref FpsStatusReason reason,
        FpsStatusReason heldReason)
    {
        status = FpsStatus.Holding;
        source = FpsSource.Presented;
        fallback = Settings.Mode == FpsSourceMode.Automatic;
        fps = _lastValidFps;
        frameTime = _lastValidFrameTime;
        onePercentLow = _lastValidOnePercentLow;
        processId = _lastValidProcessId;
        processName = _lastValidProcessName;
        processFallback = _lastValidProcessFallback;
        message = heldReason == FpsStatusReason.FocusChange
            ? "The foreground changed; holding the last valid FPS briefly."
            : "No new present events; holding the last valid FPS briefly.";
        reason = heldReason;
    }

    private void ResetHeldValue()
    {
        _lastValidTimestampMs = 0;
        _lastValidFps = 0;
        _lastValidFrameTime = 0;
        _lastValidOnePercentLow = 0;
        _lastValidProcessId = 0;
        _lastValidProcessName = null;
        _lastValidProcessFallback = false;
    }

    public IEnumerable<string> Describe()
    {
        var snapshot = Current;
        yield return $"Source: {_source.Name} ({_source.Description})";
        yield return $"Capture status: {_source.Status}";
        foreach (var line in _source.Describe())
        {
            yield return line;
        }

        yield return $"Capture mode: {Settings.CaptureMode}";
        yield return $"State: {snapshot.Status}";
        if (snapshot.Reason != FpsStatusReason.None)
        {
            yield return $"Selection reason: {snapshot.Reason}";
        }

        yield return $"Foreground process: {snapshot.ProcessLabel} (pid {snapshot.ProcessId})";
        yield return snapshot.HasValue
            ? $"Presented FPS: {snapshot.Fps:0.0} (frame time {snapshot.FrameTimeMs:0.00} ms, 1% low {snapshot.OnePercentLowFps:0.0})"
            : "Presented FPS: --";
        if (snapshot.IsValueHeld)
        {
            yield return snapshot.Reason == FpsStatusReason.FocusChange
                ? "Value state: holding the last valid FPS during a foreground change"
                : "Value state: holding the last valid FPS during an ETW delivery gap";
        }
        if (_resolver is not null)
        {
            yield return _resolver.HasSelection
                ? $"All Applications target: pid {_resolver.SelectedProcessId} ({_resolver.LastReason})"
                : $"All Applications target: none ({_resolver.LastReason})";
        }
        if (snapshot.IsProcessFallback)
        {
            yield return "Process fallback: foreground had no presents; busiest presenter measured";
        }
        yield return $"Tracked processes: {_tracker.TrackedProcessCount}";
        if (!string.IsNullOrEmpty(snapshot.Message))
        {
            yield return "Message: " + snapshot.Message;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _source.Dispose();
        _wake.Dispose();
    }
}
