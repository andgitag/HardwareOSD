using System.Runtime.InteropServices;
using HardwareOSD.Core.Hardware;

namespace HardwareOSD.Core.Hardware.Providers;

/// <summary>
/// CPU/RAM readings from documented Windows interfaces:
///  - NtQuerySystemInformation(SystemProcessorPerformanceInformation) for per-logical-processor load
///  - CallNtPowerInformation(ProcessorInformation) for current CPU clock
///  - GlobalMemoryStatusEx for physical memory
/// No kernel driver, no elevation and no WMI are required.
/// </summary>
public sealed class WindowsSystemProvider : ISensorProvider
{
    private const int SystemProcessorPerformanceInformation = 8;
    private const int ProcessorInformation = 11;

    private const uint PdhFmtDouble = 0x00000200;

    private readonly (long Idle, long Kernel, long User)[] _previousCpu;
    private readonly int _processorCount;
    private bool _hasPrevious;
    private string _cpuName = "CPU";
    private IntPtr _pdhQuery;
    private IntPtr _performanceCounter;
    private IntPtr _frequencyCounter;
    private bool _pdhReady;

    public WindowsSystemProvider()
    {
        _processorCount = Math.Clamp(Environment.ProcessorCount, 1, 256);
        _previousCpu = new (long, long, long)[_processorCount + 1];
    }

    public string Key => SensorMergePolicy.WindowsKey;
    public string Name => "Windows system counters";
    public string Category => "Windows API";
    public bool IsAvailable { get; private set; }
    public string? LastError { get; private set; }

    public void Initialize()
    {
        try
        {
            _cpuName = ReadCpuName();
            SampleCpuTimes(out _);
            InitializeProcessorPerformanceCounters();
            IsAvailable = true;
            LastError = null;
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            LastError = ex.Message;
            throw;
        }
    }

    public IReadOnlyList<SensorValue> Read(long timestampMs)
    {
        var result = new List<SensorValue>(_processorCount + 12);

        // --- CPU load -------------------------------------------------------
        try
        {
            var haveSamples = SampleCpuTimes(out var samples);
            if (haveSamples && _hasPrevious)
            {
                double totalBusy = 0, totalAll = 0;
                for (var i = 0; i < samples.Length; i++)
                {
                    var (idle, kernel, user) = samples[i];
                    var (prevIdle, prevKernel, prevUser) = _previousCpu[i];
                    var idleDelta = idle - prevIdle;
                    var kernelDelta = kernel - prevKernel;
                    var userDelta = user - prevUser;
                    var totalDelta = kernelDelta + userDelta;
                    if (totalDelta <= 0)
                    {
                        continue;
                    }

                    var usage = Math.Clamp((1.0 - (double)idleDelta / totalDelta) * 100.0, 0, 100);
                    if (i == 0)
                    {
                        totalBusy = totalDelta - idleDelta;
                        totalAll = totalDelta;
                    }
                    else
                    {
                        result.Add(new SensorValue(
                            $"cpu.thread.{i - 1}.usage", usage, "%", SensorKind.Load,
                            SensorOrigin.WindowsApi, "NtQuerySystemInformation", timestampMs));
                    }
                }

                if (totalAll > 0)
                {
                    result.Add(new SensorValue(
                        SensorIds.CpuUsage, Math.Clamp(totalBusy / totalAll * 100.0, 0, 100), "%",
                        SensorKind.Load, SensorOrigin.WindowsApi, "NtQuerySystemInformation", timestampMs));
                }

                result.Add(new SensorValue(
                    "cpu.threads", _processorCount, string.Empty, SensorKind.Data,
                    SensorOrigin.WindowsApi, "Environment", timestampMs));
            }

            if (haveSamples)
            {
                Array.Copy(samples, _previousCpu, samples.Length);
                _hasPrevious = true;
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }

        // --- CPU clock ------------------------------------------------------
        try
        {
            var clock = ReadEffectiveCpuClock();
            var source = "PDH Processor Information";
            if (clock <= 0)
            {
                // Fallback for systems without the Processor Information counter set.
                clock = ReadCurrentCpuClock();
                source = "CallNtPowerInformation";
            }

            if (clock > 0)
            {
                result.Add(new SensorValue(
                    SensorIds.CpuClock, clock, "MHz", SensorKind.Clock,
                    SensorOrigin.WindowsApi, source, timestampMs));
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }

        // --- Physical memory ------------------------------------------------
        try
        {
            var status = new MemoryStatusEx();
            status.Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
            if (GlobalMemoryStatusEx(ref status))
            {
                const double bytesPerMb = 1024.0 * 1024.0;
                var totalMb = status.TotalPhys / bytesPerMb;
                var availMb = status.AvailPhys / bytesPerMb;
                var usedMb = Math.Max(0, totalMb - availMb);

                result.Add(new SensorValue(SensorIds.RamTotal, totalMb, "MB", SensorKind.Data,
                    SensorOrigin.WindowsApi, "GlobalMemoryStatusEx", timestampMs));
                result.Add(new SensorValue(SensorIds.RamAvailable, availMb, "MB", SensorKind.Data,
                    SensorOrigin.WindowsApi, "GlobalMemoryStatusEx", timestampMs));
                result.Add(new SensorValue(SensorIds.RamUsed, usedMb, "MB", SensorKind.Data,
                    SensorOrigin.WindowsApi, "GlobalMemoryStatusEx", timestampMs));
                result.Add(new SensorValue(SensorIds.RamPercent, totalMb > 0 ? usedMb / totalMb * 100 : 0, "%",
                    SensorKind.Load, SensorOrigin.WindowsApi, "GlobalMemoryStatusEx", timestampMs));
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }

        return result;
    }

    private bool SampleCpuTimes(out (long Idle, long Kernel, long User)[] samples)
    {
        samples = new (long, long, long)[_processorCount + 1];
        var size = 48 * _processorCount;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var status = NtQuerySystemInformation(SystemProcessorPerformanceInformation, buffer, size, out var returned);
            if (status != 0 || returned < 48)
            {
                return false;
            }

            var count = Math.Min(_processorCount, returned / 48);
            long totalIdle = 0, totalKernel = 0, totalUser = 0;
            for (var i = 0; i < count; i++)
            {
                var ptr = IntPtr.Add(buffer, i * 48);
                var idle = Marshal.ReadInt64(ptr, 0);
                var kernel = Marshal.ReadInt64(ptr, 8);
                var user = Marshal.ReadInt64(ptr, 16);
                samples[i + 1] = (idle, kernel, user);
                totalIdle += idle;
                totalKernel += kernel;
                totalUser += user;
            }

            samples[0] = (totalIdle, totalKernel, totalUser);
            return count > 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Current effective clock from the documented Processor Information counter set:
    /// nominal MHz × % Processor Performance. This reports boost clocks without MSR access and is
    /// far more accurate than CallNtPowerInformation's static P-state value.
    /// </summary>
    private double ReadEffectiveCpuClock()
    {
        if (!_pdhReady)
        {
            return 0;
        }

        if (PdhCollectQueryData(_pdhQuery) != 0)
        {
            return 0;
        }

        if (!TryGetPdhDouble(_performanceCounter, out var performance) ||
            !TryGetPdhDouble(_frequencyCounter, out var nominalMhz) ||
            performance <= 0 ||
            nominalMhz <= 0)
        {
            return 0;
        }

        var effective = nominalMhz * performance / 100.0;
        return effective is > 200 and < 12_000 ? effective : 0;
    }

    private static bool TryGetPdhDouble(IntPtr counter, out double value)
    {
        value = 0;
        if (counter == IntPtr.Zero)
        {
            return false;
        }

        var status = PdhGetFormattedCounterValue(counter, PdhFmtDouble, out _, out var formatted);
        if (status != 0 || formatted.Status != 0 || !double.IsFinite(formatted.DoubleValue))
        {
            return false;
        }

        value = formatted.DoubleValue;
        return true;
    }

    private void InitializeProcessorPerformanceCounters()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out _pdhQuery) != 0)
        {
            return;
        }

        var perf = PdhAddEnglishCounterW(_pdhQuery,
            @"\Processor Information(_Total)\% Processor Performance", IntPtr.Zero, out _performanceCounter);
        var freq = PdhAddEnglishCounterW(_pdhQuery,
            @"\Processor Information(_Total)\Processor Frequency", IntPtr.Zero, out _frequencyCounter);
        if (perf != 0 || freq != 0)
        {
            PdhCloseQuery(_pdhQuery);
            _pdhQuery = IntPtr.Zero;
            _performanceCounter = IntPtr.Zero;
            _frequencyCounter = IntPtr.Zero;
            return;
        }

        // Prime the rate counter so the first Read has a usable sample.
        PdhCollectQueryData(_pdhQuery);
        _pdhReady = true;
    }

    private double ReadCurrentCpuClock()
    {
        var size = 24 * _processorCount;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var status = CallNtPowerInformation(ProcessorInformation, IntPtr.Zero, 0, buffer, size);
            if (status != 0)
            {
                return 0;
            }

            long sum = 0;
            var count = 0;
            for (var i = 0; i < _processorCount; i++)
            {
                var mhz = Marshal.ReadInt32(buffer, i * 24 + 8); // PROCESSOR_POWER_INFORMATION.CurrentMhz
                if (mhz is > 0 and < 20_000)
                {
                    sum += mhz;
                    count++;
                }
            }

            return count == 0 ? 0 : (double)sum / count;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string ReadCpuName()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var name = key?.GetValue("ProcessorNameString") as string;
            return string.IsNullOrWhiteSpace(name) ? "CPU" : name.Trim();
        }
        catch
        {
            return "CPU";
        }
    }

    public IEnumerable<string> Describe()
    {
        yield return $"CPU: {_cpuName}";
        yield return $"Logical processors: {_processorCount}";
        yield return "CPU load: NtQuerySystemInformation(SystemProcessorPerformanceInformation)";
        yield return "CPU clock: PDH Processor Information (% Processor Performance × nominal MHz)";
        yield return "CPU clock fallback: CallNtPowerInformation(ProcessorInformation)";
        yield return "Memory: GlobalMemoryStatusEx";
    }

    public void Dispose()
    {
        IsAvailable = false;
        if (_pdhQuery != IntPtr.Zero)
        {
            try
            {
                PdhCloseQuery(_pdhQuery);
            }
            catch
            {
                // ignored
            }

            _pdhQuery = IntPtr.Zero;
            _performanceCounter = IntPtr.Zero;
            _frequencyCounter = IntPtr.Zero;
            _pdhReady = false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValue
    {
        public uint Status;
        public double DoubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterValue(
        IntPtr counter, uint format, out uint type, out PdhFmtCounterValue value);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(int infoLevel, IntPtr input, int inputLength, IntPtr output, int outputLength);
}
