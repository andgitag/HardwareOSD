using System.Runtime.InteropServices;
using HardwareOSD.Core.Hardware;

namespace HardwareOSD.Core.Hardware.Providers;

/// <summary>
/// Reads the ACPI thermal zones through the documented Performance Data Helper (PDH) interface,
/// using "Thermal Zone Information(*)\Temperature". This works without elevation and gives a real
/// platform temperature on many laptops where no other user-mode CPU temperature source exists.
/// It is a thermal-zone reading, not an MSR package sensor, and is reported with that distinction
/// in the data source metadata.
/// </summary>
public sealed class ThermalZoneProvider : ISensorProvider
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhMoreData = 0x800007D2;

    private IntPtr _query;
    private IntPtr _counter;
    private bool _initialized;

    public string Key => SensorMergePolicy.WindowsKey;
    public string Name => "ACPI thermal zone (PDH)";
    public string Category => "Windows performance counters";
    public bool IsAvailable { get; private set; }
    public string? LastError { get; private set; }

    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        var status = PdhOpenQueryW(null, IntPtr.Zero, out _query);
        if (status != 0)
        {
            throw new InvalidOperationException($"PdhOpenQuery failed: 0x{status:X8}");
        }

        status = PdhAddEnglishCounterW(_query, @"\Thermal Zone Information(*)\Temperature", IntPtr.Zero, out _counter);
        if (status != 0)
        {
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
            throw new InvalidOperationException($"Thermal zone counter unavailable: 0x{status:X8}");
        }

        PdhCollectQueryData(_query);
        _initialized = true;
        IsAvailable = true;
        LastError = null;
    }

    public IReadOnlyList<SensorValue> Read(long timestampMs)
    {
        var result = new List<SensorValue>(4);
        if (!_initialized || _query == IntPtr.Zero || _counter == IntPtr.Zero)
        {
            return result;
        }

        var status = PdhCollectQueryData(_query);
        if (status != 0 && status != PdhMoreData)
        {
            LastError = $"PdhCollectQueryData failed: 0x{status:X8}";
            return result;
        }

        var size = 0;
        var count = 0;
        status = PdhGetFormattedCounterArrayW(_counter, PdhFmtDouble, ref size, ref count, IntPtr.Zero);
        if (status != PdhMoreData || size <= 0)
        {
            // No samples yet on the first poll; not an error worth logging.
            return result;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            status = PdhGetFormattedCounterArrayW(_counter, PdhFmtDouble, ref size, ref count, buffer);
            if (status != 0 || count == 0)
            {
                return result;
            }

            double hottest = double.NaN;
            var itemSize = Marshal.SizeOf<PdhCounterValueItem>();
            var valid = 0;
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PdhCounterValueItem>(IntPtr.Add(buffer, i * itemSize));
                // 0x00000000 == PDH_CSTATUS_VALID_DATA
                if (item.Value.Status != 0)
                {
                    continue;
                }

                var celsius = item.Value.DoubleValue - 273.15;
                if (!double.IsFinite(celsius) || celsius is < -20 or > 160)
                {
                    continue;
                }

                valid++;
                if (double.IsNaN(hottest) || celsius > hottest)
                {
                    hottest = celsius;
                }
            }

            if (valid > 0)
            {
                result.Add(new SensorValue(
                    "thermalzone.temp", hottest, "°C", SensorKind.Temperature,
                    SensorOrigin.WindowsApi,
                    "PDH Thermal Zone Information", timestampMs));

                result.Add(new SensorValue(
                    "thermalzone.count", valid, string.Empty, SensorKind.Data,
                    SensorOrigin.WindowsApi, "PDH", timestampMs));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    public IEnumerable<string> Describe()
    {
        yield return "Counter: \\Thermal Zone Information(*)\\Temperature (PDH, no elevation required)";
        yield return "Semantics: ACPI thermal zone temperature, not an MSR package sensor";
    }

    public void Dispose()
    {
        IsAvailable = false;
        if (_query != IntPtr.Zero)
        {
            try
            {
                PdhCloseQuery(_query);
            }
            catch
            {
                // ignored
            }

            _query = IntPtr.Zero;
            _counter = IntPtr.Zero;
        }

        _initialized = false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValue
    {
        public uint Status;
        public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhCounterValueItem
    {
        public IntPtr Name;
        public PdhFmtCounterValue Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(
        IntPtr counter, uint format, ref int bufferSize, ref int itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
