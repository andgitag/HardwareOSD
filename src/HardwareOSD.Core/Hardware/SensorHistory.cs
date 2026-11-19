namespace HardwareOSD.Core.Hardware;

/// <summary>
/// Fixed-capacity history ring buffers for sparklines. The writer appends from the polling thread;
/// readers use <see cref="TryCopy"/> which never blocks (it degrades to "no data" instead).
///
/// Series are either snapshot-driven (shifted by every <see cref="Append(SensorSnapshot)"/> call)
/// or independent (shifted only by <see cref="AppendValue"/>, e.g. FPS sampled at its own rate).
/// </summary>
public sealed class SensorHistory
{
    private readonly object _gate = new();
    private readonly Dictionary<string, double[]> _series = new(StringComparer.Ordinal);
    private readonly HashSet<string> _tracked = new(StringComparer.Ordinal);
    private readonly int _capacity;

    public SensorHistory(int capacity = 120)
    {
        _capacity = Math.Clamp(capacity, 8, 4096);
    }

    public int Capacity => _capacity;

    public IReadOnlyCollection<string> TrackedSeries
    {
        get
        {
            lock (_gate)
            {
                return _tracked.ToArray();
            }
        }
    }

    /// <summary>Sets the series shifted by snapshot polls. Independent series are created on demand.</summary>
    public void Track(IEnumerable<string> ids)
    {
        lock (_gate)
        {
            _tracked.Clear();
            _series.Clear();
            foreach (var id in ids)
            {
                _tracked.Add(id);
            }
        }
    }

    /// <summary>Appends the current values of tracked series. Called from the polling thread.</summary>
    public void Append(SensorSnapshot snapshot)
    {
        lock (_gate)
        {
            foreach (var id in _tracked)
            {
                if (!_series.TryGetValue(id, out var buffer))
                {
                    buffer = NewBuffer();
                    _series[id] = buffer;
                }

                Shift(buffer, snapshot.ValueOrNaN(id));
            }
        }
    }

    /// <summary>
    /// Appends a single value to a series that is not driven by hardware snapshots (for example
    /// <c>frame.fps</c>). Safe to call from the FPS publisher thread.
    /// </summary>
    public void AppendValue(string id, double value)
    {
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        lock (_gate)
        {
            if (!_series.TryGetValue(id, out var buffer))
            {
                buffer = NewBuffer();
                _series[id] = buffer;
            }

            Shift(buffer, value);
        }
    }

    /// <summary>
    /// Copies the series (oldest first) into <paramref name="destination"/> and returns the number of samples.
    /// Returns 0 without waiting when the writer is busy.
    /// </summary>
    public int TryCopy(string id, Span<double> destination)
    {
        if (!System.Threading.Monitor.TryEnter(_gate))
        {
            return 0;
        }

        try
        {
            if (!_series.TryGetValue(id, out var buffer))
            {
                return 0;
            }

            var count = Math.Min(destination.Length, buffer.Length);
            buffer.AsSpan(buffer.Length - count).CopyTo(destination);
            return count;
        }
        finally
        {
            System.Threading.Monitor.Exit(_gate);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            // Independent series (FPS) are re-created lazily; keeping them would show stale data.
            _series.Clear();
        }
    }

    private double[] NewBuffer()
    {
        var buffer = new double[_capacity];
        Array.Fill(buffer, double.NaN);
        return buffer;
    }

    private void Shift(double[] buffer, double value)
    {
        // Shift-by-one keeps the buffer as a plain chronological array, which makes rendering and
        // testing trivial at this tiny capacity (<= a few thousand doubles).
        Array.Copy(buffer, 1, buffer, 0, _capacity - 1);
        buffer[^1] = value;
    }
}
