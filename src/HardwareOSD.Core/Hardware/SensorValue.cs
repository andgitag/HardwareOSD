namespace HardwareOSD.Core.Hardware;

public enum SensorKind
{
    Temperature,
    Load,
    Clock,
    Power,
    Voltage,
    Data,
    Fan,
    Other,
}

/// <summary>Where a reading comes from. Used to keep real sensors distinguishable from derived values.</summary>
public enum SensorOrigin
{
    /// <summary>Read from a hardware monitoring interface (NVML, LibreHardwareMonitor, MSR, EC).</summary>
    HardwareSensor,

    /// <summary>Read from a documented Windows API (GlobalMemoryStatusEx, NtPowerInformation, system time counters).</summary>
    WindowsApi,

    /// <summary>Computed from other readings, e.g. used = total - free or used / total.</summary>
    Derived,

    /// <summary>Synthetic data produced only by the QA/demo provider.</summary>
    Demo,
}

/// <summary>
/// An immutable sensor reading. Unavailable readings are represented explicitly instead of
/// disappearing, so consumers can render "N/A" and keep a stable layout.
/// </summary>
public readonly struct SensorValue
{
    public SensorValue(
        string id,
        double value,
        string unit,
        SensorKind kind,
        SensorOrigin origin,
        string source,
        long timestampMs,
        bool available = true,
        string? note = null)
    {
        Id = id;
        Value = value;
        Unit = unit;
        Kind = kind;
        Origin = origin;
        Source = source;
        TimestampMs = timestampMs;
        Available = available;
        Note = note;
    }

    public string Id { get; }
    public double Value { get; }
    public string Unit { get; }
    public SensorKind Kind { get; }
    public SensorOrigin Origin { get; }
    public string Source { get; }
    public long TimestampMs { get; }
    public bool Available { get; }
    public string? Note { get; }

    public static SensorValue Unavailable(string id, SensorKind kind, string source, long timestampMs, string? note = null)
        => new(id, double.NaN, string.Empty, kind, SensorOrigin.HardwareSensor, source, timestampMs, false, note);

    public double AgeMs(long nowMs) => nowMs - TimestampMs;
}
