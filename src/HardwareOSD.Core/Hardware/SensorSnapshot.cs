using System.Collections.ObjectModel;

namespace HardwareOSD.Core.Hardware;

/// <summary>
/// Immutable, atomically published view of every known reading.
/// Readers obtain one reference and never observe partially updated state.
/// </summary>
public sealed class SensorSnapshot
{
    public static readonly SensorSnapshot Empty = new(
        0,
        0,
        new ReadOnlyDictionary<string, SensorValue>(new Dictionary<string, SensorValue>(0)));

    private readonly IReadOnlyDictionary<string, SensorValue> _values;

    public SensorSnapshot(long sequence, long capturedAtMs, IReadOnlyDictionary<string, SensorValue> values)
    {
        Sequence = sequence;
        CapturedAtMs = capturedAtMs;
        _values = values;
    }

    /// <summary>Monotonically increasing publication counter. Renderers use it to skip redundant work.</summary>
    public long Sequence { get; }

    public long CapturedAtMs { get; }

    public IReadOnlyDictionary<string, SensorValue> Values => _values;

    public int Count => _values.Count;

    public bool Contains(string id) => _values.ContainsKey(id);

    public bool TryGet(string id, out SensorValue value) => _values.TryGetValue(id, out value);

    public double GetOrDefault(string id, double fallback)
        => _values.TryGetValue(id, out var v) && v.Available && !double.IsNaN(v.Value) ? v.Value : fallback;

    public bool IsAvailable(string id) => _values.TryGetValue(id, out var v) && v.Available;

    public double ValueOrNaN(string id) => _values.TryGetValue(id, out var v) && v.Available ? v.Value : double.NaN;

    public static SensorSnapshot Create(long sequence, long capturedAtMs, Dictionary<string, SensorValue> values)
        => new(sequence, capturedAtMs, new ReadOnlyDictionary<string, SensorValue>(values));
}
