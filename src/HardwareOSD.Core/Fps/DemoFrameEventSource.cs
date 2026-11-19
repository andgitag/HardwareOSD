using System.Diagnostics;

namespace HardwareOSD.Core.Fps;

/// <summary>
/// Synthetic frame source used by <c>--demo</c> and by tests. It presents frames at a configurable
/// rate so the complete FPS pipeline (timing math, publishing, rendering) can be exercised without
/// administrator rights. Values produced by this source are clearly marked as demo data.
/// </summary>
public sealed class DemoFrameEventSource : IFrameEventSource
{
    private readonly double _fps;
    private Thread? _thread;
    private volatile bool _running;
    private volatile FpsCaptureStatus _status = FpsCaptureStatus.Stopped;

    public DemoFrameEventSource(double fps = 144.0, int? processId = null)
    {
        _fps = Math.Clamp(fps, 1, 1000);
        ProcessId = processId ?? Environment.ProcessId;
    }

    public int ProcessId { get; }

    public string Name => "Synthetic frame source";

    public string Description => $"Generated present timestamps at {_fps:0} FPS (QA only)";

    public FpsCaptureStatus Status => _status;

    public string? LastError => null;

    public event Action<int, double>? FramePresented;

    public bool Start()
    {
        if (_running)
        {
            return true;
        }

        _running = true;
        _status = FpsCaptureStatus.Running;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "HardwareOSD.FpsDemo",
        };
        _thread.Start();
        return true;
    }

    private void Loop()
    {
        var clock = Stopwatch.StartNew();
        var interval = 1000.0 / _fps;
        var next = 0.0;
        while (_running)
        {
            // Batch a few frames per wake-up with exact synthetic timestamps. Sleeping per frame
            // would be limited by the system timer resolution and under-report the demo rate.
            Thread.Sleep(5);
            var now = clock.Elapsed.TotalMilliseconds;
            var guard = 0;
            while (next <= now && guard++ < 500)
            {
                try
                {
                    FramePresented?.Invoke(ProcessId, next);
                }
                catch
                {
                    // ignored
                }

                next += interval;
            }

            if (next < now - 1000)
            {
                // Recover from a long suspension instead of flooding catch-up frames.
                next = now;
            }
        }
    }

    public void Stop()
    {
        _running = false;
        _status = FpsCaptureStatus.Stopped;
        try
        {
            _thread?.Join(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // ignored
        }

        _thread = null;
    }

    public IEnumerable<string> Describe()
    {
        yield return $"Status: {_status}";
        yield return $"Synthetic present timestamps at {_fps:0} FPS";
    }

    public void Dispose() => Stop();
}
