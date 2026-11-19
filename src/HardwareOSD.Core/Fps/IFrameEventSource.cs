namespace HardwareOSD.Core.Fps;

public enum FpsCaptureStatus
{
    Stopped = 0,
    Starting = 1,
    Running = 2,
    RequiresElevation = 3,
    Failed = 4,
}

/// <summary>
/// Source of frame-presentation timestamps. Implementations must be thread safe for
/// <see cref="FramePresented"/> (raised from the capture thread) and must never throw from a
/// capture callback.
/// </summary>
public interface IFrameEventSource : IDisposable
{
    string Name { get; }

    /// <summary>Human readable description of the underlying interface, shown in diagnostics.</summary>
    string Description { get; }

    FpsCaptureStatus Status { get; }

    string? LastError { get; }

    /// <summary>Raised for every presented frame: process id and monotonic timestamp in ms.</summary>
    event Action<int, double>? FramePresented;

    /// <summary>Starts capture. Returns false when the source cannot run (see Status/LastError).</summary>
    bool Start();

    void Stop();

    /// <summary>Diagnostic lines for the log/report (providers, counters, last error).</summary>
    IEnumerable<string> Describe();
}
