using System.Diagnostics;
using HardwareOSD.Core.Hardware;

namespace HardwareOSD.Core.Hardware.Providers;

/// <summary>
/// Reads the Windows Energy Meter Interface (EMI) through the built-in Performance Counter
/// provider. Windows 11 exposes RAPL package and subdomain counters here without administrator
/// rights. Power is reported by Windows in milliwatts; HardwareOSD converts it to watts.
///
/// The Windows provider is part of the OS (PowerMeterCounter/Energy Meter) and does not depend on
/// CapFrameX, MSI Center, LHM, WinRing0 or any kernel driver installed by HardwareOSD.
/// </summary>
public sealed class WindowsEnergyMeterProvider : ISensorProvider
{
    private const string CategoryName = "Energy Meter";
    private const string PowerCounter = "Power";
    private const string EnergyCounter = "Energy";
    private const long RetryInitializationMs = 30_000;

    private static readonly Domain[] Domains =
    {
        new("RAPL_Package0_PKG", SensorIds.CpuPower, "package"),
        new("RAPL_Package0_PP0", SensorIds.CpuPowerCores, "cores/IA"),
        new("RAPL_Package0_PP1", SensorIds.CpuPowerGt, "GT"),
        new("RAPL_Package0_DRAM", SensorIds.CpuPowerMemory, "memory"),
    };

    private readonly Dictionary<string, CounterPair> _counters =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized;
    private long _lastInitAttemptMs;
    private string _detail = "not probed";
    private string _failureNote = SensorNotes.EnergyMeterUnavailable;

    public string Key => SensorMergePolicy.EnergyMeterKey;
    public string Name => "Windows Energy Meter (EMI)";
    public string Category => "Windows performance counters";
    public bool IsAvailable => true;
    public string? LastError { get; private set; }

    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _lastInitAttemptMs = Environment.TickCount64;
        try
        {
            var category = new PerformanceCounterCategory(CategoryName);
            var instances = category.GetInstanceNames();
            DisposeCounters();
            foreach (var instance in instances)
            {
                if (instance.Equals("_Total", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var power = new PerformanceCounter(CategoryName, PowerCounter, instance, readOnly: true);
                    var energy = new PerformanceCounter(CategoryName, EnergyCounter, instance, readOnly: true);
                    // Prime the first sample. PerformanceCounter needs two calls for an average.
                    _ = power.NextValue();
                    _ = energy.NextValue();
                    _counters[instance] = new CounterPair(power, energy);
                }
                catch
                {
                    // A single malformed instance must not disable the other RAPL domains.
                }
            }

            if (_counters.Count == 0)
            {
                throw new InvalidOperationException("Energy Meter has no readable instances");
            }

            Thread.Sleep(120);
            foreach (var pair in _counters.Values)
            {
                try
                {
                    _ = pair.Power.NextValue();
                    _ = pair.Energy.NextValue();
                }
                catch
                {
                    // The first read is still allowed to produce a valid subset.
                }
            }

            _initialized = true;
            _detail = "Energy Meter instances: " + string.Join(", ", _counters.Keys);
            LastError = null;
        }
        catch (Exception ex)
        {
            DisposeCounters();
            _initialized = false;
            _detail = "Energy Meter unavailable: " + ex.Message;
            LastError = ex.Message;
            _failureNote = SensorNotes.EnergyMeterUnavailable;
        }
    }

    public IReadOnlyList<SensorValue> Read(long timestampMs)
    {
        var output = new List<SensorValue>(Domains.Length);
        if (!_initialized && Environment.TickCount64 - _lastInitAttemptMs >= RetryInitializationMs)
        {
            Initialize();
        }

        if (!_initialized)
        {
            AddUnavailableAll(output, timestampMs, _failureNote);
            return output;
        }

        var supported = 0;
        foreach (var domain in Domains)
        {
            if (!TryGetPair(domain.Instance, out var pair))
            {
                AddUnavailable(output, domain, timestampMs, SensorNotes.EnergyMeterChannelUnavailable);
                continue;
            }

            try
            {
                var powerMw = pair.Power.NextValue();
                var energyPwh = pair.Energy.NextValue();
                if (!float.IsFinite(powerMw) || powerMw < 0)
                {
                    AddUnavailable(output, domain, timestampMs, SensorNotes.EnergyMeterReadFailed);
                    continue;
                }

                if (powerMw <= 0.05f)
                {
                    // A real RAPL channel has a monotonically increasing energy counter. A
                    // channel that exists but stays at zero energy is not wired on this platform
                    // (for example DRAM on this HX laptop). Never publish a fabricated 0 W.
                    if (energyPwh <= 0 || !float.IsFinite(energyPwh))
                    {
                        AddUnavailable(output, domain, timestampMs, SensorNotes.EnergyMeterChannelUnavailable);
                    }
                    else
                    {
                        AddUnavailable(output, domain, timestampMs, SensorNotes.EnergyMeterZero);
                    }

                    continue;
                }

                supported++;
                output.Add(new SensorValue(
                    domain.SensorId,
                    powerMw / 1000.0,
                    "W",
                    SensorKind.Power,
                    SensorOrigin.WindowsApi,
                    $"PDH Energy Meter: {pair.Instance}",
                    timestampMs));
            }
            catch
            {
                AddUnavailable(output, domain, timestampMs, SensorNotes.EnergyMeterReadFailed);
            }
        }

        LastError = supported > 0 ? null : "Energy Meter has no supported domain";
        _detail = "Energy Meter available: " + string.Join(", ", _counters.Keys);
        return output;
    }

    private bool TryGetPair(string instance, out CounterPair pair)
    {
        if (_counters.TryGetValue(instance, out pair!))
        {
            return true;
        }

        // Instance names can differ in case between Windows builds; keep the lookup explicit.
        foreach (var candidate in _counters)
        {
            if (string.Equals(candidate.Key, instance, StringComparison.OrdinalIgnoreCase))
            {
                pair = candidate.Value;
                return true;
            }
        }

        pair = null!;
        return false;
    }

    private void AddUnavailableAll(List<SensorValue> output, long timestampMs, string note)
    {
        foreach (var domain in Domains)
        {
            AddUnavailable(output, domain, timestampMs, note);
        }
    }

    private static void AddUnavailable(List<SensorValue> output, Domain domain, long timestampMs, string note)
    {
        output.Add(SensorValue.Unavailable(
            domain.SensorId,
            SensorKind.Power,
            "PDH Energy Meter",
            timestampMs,
            note));
    }

    public IEnumerable<string> Describe()
    {
        yield return "Provider: Windows Energy Meter Interface (EMI), PerformanceCounter category";
        yield return "Category: Energy Meter; counters Power (mW) and Energy (pWh)";
        yield return "Elevation: not required; this is the Windows 11 EMI provider";
        yield return "Domains: RAPL_Package0_PKG, PP0 (cores/IA), PP1 (GT), DRAM";
        yield return $"State: {_detail}";
        if (!string.IsNullOrWhiteSpace(LastError))
        {
            yield return "Last error: " + LastError;
        }
    }

    public void Dispose()
    {
        _initialized = false;
        DisposeCounters();
    }

    private void DisposeCounters()
    {
        foreach (var pair in _counters.Values)
        {
            pair.Dispose();
        }

        _counters.Clear();
    }

    private sealed class CounterPair : IDisposable
    {
        public CounterPair(PerformanceCounter power, PerformanceCounter energy)
        {
            Power = power;
            Energy = energy;
            Instance = power.InstanceName;
        }

        public PerformanceCounter Power { get; }
        public PerformanceCounter Energy { get; }
        public string Instance { get; }

        public void Dispose()
        {
            Power.Dispose();
            Energy.Dispose();
        }
    }

    private readonly record struct Domain(string Instance, string SensorId, string Label);
}
