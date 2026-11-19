using System.Diagnostics;
using HardwareOSD.Core.Diagnostics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace HardwareOSD.Core.Fps;

/// <summary>
/// Frame presentation telemetry from a real-time ETW session (the same approach PresentMon uses
/// in its "simple" mode). It consumes the documented Microsoft-Windows-DXGI and
/// Microsoft-Windows-D3D9 manifest providers and counts <c>Present::Start</c> events per process;
/// the interval between consecutive presents is the application's presented frame rate.
///
/// No process is opened, injected or read. ETW session creation requires administrator rights, so
/// <see cref="Start"/> reports <see cref="FpsCaptureStatus.RequiresElevation"/> instead of
/// attempting an elevation prompt.
/// </summary>
public sealed class EtwFrameEventSource : IFrameEventSource
{
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3d9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");

    private const ulong PresentKeyword = ulong.MaxValue; // all keywords, matching the reference ETW collector
    private const int DxgiPresentStart = 42;
    private const int DxgiPresentMultiplaneStart = 55;
    private const int D3d9PresentStart = 1;

    private readonly ILog _log;
    private TraceEventSession? _session;
    private Thread? _thread;
    private volatile FpsCaptureStatus _status = FpsCaptureStatus.Stopped;
    private volatile bool _stopping;
    private string? _lastError;
    private long _totalEvents;
    private long _presentEvents;
    private long _lastPresentWallMs;
    private int _lastPresentProcessId;

    public EtwFrameEventSource(ILog log)
    {
        _log = log;
    }

    public string Name => "ETW frame presentation";

    public string Description =>
        "Microsoft-Windows-DXGI / Microsoft-Windows-D3D9 Present events (real-time ETW session)";

    public FpsCaptureStatus Status => _status;

    public string? LastError => _lastError;

    public event Action<int, double>? FramePresented;

    public long TotalEventCount => Interlocked.Read(ref _totalEvents);

    public long PresentEventCount => Interlocked.Read(ref _presentEvents);

    public long LastPresentWallMs => Interlocked.Read(ref _lastPresentWallMs);

    public int LastPresentProcessId => Volatile.Read(ref _lastPresentProcessId);

    public bool DxgiEnabled { get; private set; }

    public bool D3d9Enabled { get; private set; }

    public string? SessionName { get; private set; }

    public bool Start()
    {
        if (_status is FpsCaptureStatus.Running or FpsCaptureStatus.Starting)
        {
            return true;
        }

        _stopping = false;
        _lastError = null;
        _status = FpsCaptureStatus.Starting;

        TraceEventSession? session = null;
        try
        {
            StopStaleSessions();

            var sessionName = EtwSessionPolicy.NameFor(Environment.ProcessId);
            session = new TraceEventSession(sessionName)
            {
                StopOnDispose = true,
                BufferSizeMB = 16,
            };

            var dxgi = false;
            var d3d9 = false;
            try
            {
                // TraceEvent's return value means "an existing session was restarted", not success.
                // A failed enable throws instead, so arriving here without an exception means the
                // provider is enabled. All keywords are enabled, matching the reference collector;
                // present events are selected by provider/id in OnEvent.
                session.EnableProvider(DxgiProvider, TraceEventLevel.Verbose, PresentKeyword);
                dxgi = true;
            }
            catch (UnauthorizedAccessException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warn("FPS", "Could not enable the DXGI present provider", ex);
            }

            try
            {
                session.EnableProvider(D3d9Provider, TraceEventLevel.Verbose, PresentKeyword);
                d3d9 = true;
            }
            catch (UnauthorizedAccessException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warn("FPS", "Could not enable the D3D9 present provider", ex);
            }

            if (!dxgi && !d3d9)
            {
                SafeDispose(session);
                _status = FpsCaptureStatus.Failed;
                _lastError = "Neither the DXGI nor the D3D9 ETW provider could be enabled.";
                _log.Error("FPS", _lastError);
                return false;
            }

            session.Source.AllEvents += OnEvent;
            _session = session;
            _thread = new Thread(ProcessLoop)
            {
                IsBackground = true,
                Name = "HardwareOSD.FpsEtw",
                Priority = ThreadPriority.BelowNormal,
            };
            _thread.Start();

            DxgiEnabled = dxgi;
            D3d9Enabled = d3d9;
            SessionName = sessionName;
            _status = FpsCaptureStatus.Running;
            _log.Info("FPS", $"ETW capture session started (DXGI={dxgi}, D3D9={d3d9}, session={sessionName})");
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            SafeDispose(session);
            _status = FpsCaptureStatus.RequiresElevation;
            _lastError = "Starting an ETW capture session requires administrator rights or membership " +
                         "in the Performance Log Users group. Restart HardwareOSD elevated to measure FPS.";
            _log.Warn("FPS", _lastError, ex);
            return false;
        }
        catch (Exception ex)
        {
            SafeDispose(session);
            _status = FpsCaptureStatus.Failed;
            _lastError = ex.Message;
            _log.Error("FPS", "Failed to start the ETW capture session", ex);
            return false;
        }
    }

    private static void SafeDispose(TraceEventSession? session)
    {
        try
        {
            session?.Dispose();
        }
        catch
        {
            // Disposal of a partially started session must never escape Start().
        }
    }

    private void ProcessLoop()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        try
        {
            session.Source.Process();
        }
        catch (Exception ex)
        {
            if (!_stopping)
            {
                _lastError = ex.Message;
                _status = FpsCaptureStatus.Failed;
                _log.Error("FPS", "ETW processing loop terminated unexpectedly", ex);
            }
        }
        finally
        {
            if (!_stopping && _status == FpsCaptureStatus.Running)
            {
                _status = FpsCaptureStatus.Stopped;
                _log.Warn("FPS", "ETW processing loop ended");
            }
        }
    }

    private void OnEvent(TraceEvent traceEvent)
    {
        Interlocked.Increment(ref _totalEvents);

        try
        {
            var id = (int)traceEvent.ID;
            if (traceEvent.ProviderGuid == DxgiProvider)
            {
                if (id != DxgiPresentStart && id != DxgiPresentMultiplaneStart)
                {
                    return;
                }

                if (IsTestPresent(traceEvent))
                {
                    return;
                }
            }
            else if (traceEvent.ProviderGuid == D3d9Provider)
            {
                if (id != D3d9PresentStart)
                {
                    return;
                }
            }
            else
            {
                return;
            }

            var processId = traceEvent.ProcessID;
            if (processId <= 0 || processId == Environment.ProcessId)
            {
                // The overlay/settings process also presents WPF frames; never measure ourselves.
                return;
            }

            Interlocked.Increment(ref _presentEvents);
            Interlocked.Exchange(ref _lastPresentWallMs, Environment.TickCount64);
            Volatile.Write(ref _lastPresentProcessId, processId);
            FramePresented?.Invoke(processId, traceEvent.TimeStampRelativeMSec);
        }
        catch
        {
            // Never let a malformed event terminate the capture thread.
        }
    }

    /// <summary>DXGI_PRESENT_TEST calls do not present a frame and must not be counted.</summary>
    private static bool IsTestPresent(TraceEvent traceEvent)
    {
        try
        {
            var names = traceEvent.PayloadNames;
            for (var i = 0; i < names.Length; i++)
            {
                if (!string.Equals(names[i], "Flags", StringComparison.Ordinal))
                {
                    continue;
                }

                var value = traceEvent.PayloadValue(i);
                if (value is null)
                {
                    return false;
                }

                var flags = Convert.ToUInt32(value);
                return (flags & 1u) != 0; // DXGI_PRESENT_TEST
            }
        }
        catch
        {
            // Manifest payload names are not guaranteed for every Windows build.
        }

        return false;
    }

    /// <summary>
    /// Stops sessions left behind by a crashed HardwareOSD process. Sessions owned by another live
    /// instance are kept: portable/QA profiles have independent data directories and must not stop
    /// the main instance's FPS capture.
    /// </summary>
    private void StopStaleSessions()
    {
        try
        {
            foreach (var name in TraceEventSession.GetActiveSessionNames())
            {
                if (!EtwSessionPolicy.ShouldStop(name, Environment.ProcessId, IsProcessAlive))
                {
                    continue;
                }

                try
                {
                    var stale = TraceEventSession.GetActiveSession(name);
                    if (stale is not null)
                    {
                        stale.Stop();
                        stale.Dispose();
                        _log.Info("FPS", $"Stopped stale ETW session '{name}'");
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn("FPS", $"Could not stop stale ETW session '{name}'", ex);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Debug("FPS", "Session enumeration failed: " + ex.Message);
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public void Stop()
    {
        _stopping = true;
        _status = FpsCaptureStatus.Stopped;

        var session = _session;
        _session = null;

        if (session is not null)
        {
            try
            {
                session.Source.AllEvents -= OnEvent;
            }
            catch
            {
                // ignored
            }

            try
            {
                session.Stop();
            }
            catch
            {
                // ignored
            }

            try
            {
                session.Dispose();
            }
            catch
            {
                // ignored
            }
        }

        try
        {
            _thread?.Join(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // ignored
        }

        _thread = null;
        _log.Info("FPS", "ETW capture stopped");
    }

    public IEnumerable<string> Describe()
    {
        yield return $"Status: {_status}";
        yield return "Providers: Microsoft-Windows-DXGI Present::Start, Microsoft-Windows-D3D9 Present::Start";
        yield return "Mode: presented frames (displayed-frame correlation is not implemented)";
        yield return $"Providers enabled: DXGI={DxgiEnabled}, D3D9={D3d9Enabled}, session={SessionName ?? "—"}";
        yield return "Keyword filter: all provider keywords (event id filtering is applied in-process)";
        yield return $"Events observed: {TotalEventCount} (presents: {PresentEventCount})";
        if (!string.IsNullOrEmpty(_lastError))
        {
            yield return "Last error: " + _lastError;
        }
    }

    public void Dispose()
    {
        Stop();
        FramePresented = null;
    }
}
