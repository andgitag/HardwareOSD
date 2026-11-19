namespace HardwareOSD.Core.Hardware.Providers;

/// <summary>
/// A source of sensor readings. Implementations are used exclusively from the polling thread,
/// so they do not need to be internally synchronised (except Dispose, which may race with Stop).
/// </summary>
public interface ISensorProvider : IDisposable
{
    /// <summary>Stable machine key used by the merge policy ("lhm", "nvml", "win").</summary>
    string Key { get; }

    /// <summary>Human readable name shown in diagnostics.</summary>
    string Name { get; }

    /// <summary>Category shown in diagnostics, e.g. "LibreHardwareMonitor".</summary>
    string Category { get; }

    bool IsAvailable { get; }

    /// <summary>Last error message, or null.</summary>
    string? LastError { get; }

    /// <summary>Opens the underlying interface. May throw; the polling service handles failures.</summary>
    void Initialize();

    /// <summary>Reads all currently available values. May throw; the polling service isolates failures.</summary>
    IReadOnlyList<SensorValue> Read(long timestampMs);

    /// <summary>Optional diagnostic inventory (hardware names, driver info, ...).</summary>
    IEnumerable<string> Describe();
}
