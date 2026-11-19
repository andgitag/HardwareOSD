using System.Runtime.InteropServices;
using System.Text;
using HardwareOSD.Core.Hardware;

namespace HardwareOSD.Core.Hardware.Providers;

/// <summary>
/// NVIDIA NVML provider. NVML ships with the display driver and is the official, documented,
/// user-mode monitoring interface for NVIDIA GPUs; it needs neither elevation nor a kernel driver.
/// Every call is individually fault tolerant so a partially supported laptop GPU still reports
/// whatever it exposes.
/// </summary>
public sealed class NvmlProvider : ISensorProvider
{
    private const int NvmlSuccess = 0;
    private const int TemperatureGpu = 0;
    private const int ClockGraphics = 0;
    private const int ClockMemory = 2;
    private const int TemperatureThresholdSlowdown = 0;

    private IntPtr _device = IntPtr.Zero;
    private uint _deviceCount;
    private uint _fanCount;
    private bool _fanRpmUnsupported;
    private string _gpuName = "NVIDIA GPU";
    private bool _initialized;
    private bool _bar1Probed;
    private string _fanStatus = "not queried";

    public string FanStatus => _fanStatus;

    /// <summary>NVML library version reported by the installed driver, or null when unavailable.</summary>
    public string? NvmlVersion { get; private set; }

    /// <summary>
    /// BAR1 aperture size in bytes. With Resizable BAR the aperture maps the whole frame buffer;
    /// without it the legacy 256 MB window remains. Null when the driver does not expose it.
    /// </summary>
    public long? Bar1TotalBytes { get; private set; }

    public string Key => SensorMergePolicy.NvmlKey;
    public string Name => "NVIDIA NVML";
    public string Category => "NVML";
    public bool IsAvailable { get; private set; }
    public string? LastError { get; private set; }

    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        var result = nvmlInit_v2();
        if (result != NvmlSuccess)
        {
            LastError = $"nvmlInit_v2 failed with status {result}";
            IsAvailable = false;
            throw new InvalidOperationException(LastError);
        }

        _initialized = true;

        result = nvmlDeviceGetCount_v2(out _deviceCount);
        if (result != NvmlSuccess || _deviceCount == 0)
        {
            LastError = "No NVML devices found";
            IsAvailable = false;
            throw new InvalidOperationException(LastError);
        }

        result = nvmlDeviceGetHandleByIndex_v2(0, out _device);
        if (result != NvmlSuccess)
        {
            LastError = $"nvmlDeviceGetHandleByIndex_v2 failed with status {result}";
            IsAvailable = false;
            throw new InvalidOperationException(LastError);
        }

        var buffer = new byte[96];
        if (nvmlDeviceGetName(_device, buffer, (uint)buffer.Length) == NvmlSuccess)
        {
            var end = Array.IndexOf(buffer, (byte)0);
            _gpuName = Encoding.ASCII.GetString(buffer, 0, end < 0 ? buffer.Length : end).Trim();
        }

        var versionBuffer = new byte[80];
        try
        {
            if (nvmlSystemGetNVMLVersion(versionBuffer, (uint)versionBuffer.Length) == NvmlSuccess)
            {
                var end = Array.IndexOf(versionBuffer, (byte)0);
                NvmlVersion = Encoding.ASCII.GetString(versionBuffer, 0,
                    end < 0 ? versionBuffer.Length : end).Trim();
            }
        }
        catch
        {
            // Optional metadata; an older nvml.dll must not disable the sensor provider.
        }

        IsAvailable = true;
        LastError = null;
    }

    public IReadOnlyList<SensorValue> Read(long timestampMs)
    {
        var result = new List<SensorValue>(16);
        if (!_initialized || _device == IntPtr.Zero)
        {
            return result;
        }

        try
        {
            if (nvmlDeviceGetTemperature(_device, TemperatureGpu, out var temperature) == NvmlSuccess)
            {
                result.Add(Value(SensorIds.GpuTemperature, temperature, "°C", SensorKind.Temperature, timestampMs));
            }

            if (nvmlDeviceGetTemperatureThreshold(_device, TemperatureThresholdSlowdown, out var limit) == NvmlSuccess && limit > 0)
            {
                result.Add(Value("gpu.temp.limit", limit, "°C", SensorKind.Temperature, timestampMs));
            }

            if (nvmlDeviceGetUtilizationRates(_device, out var utilization) == NvmlSuccess)
            {
                result.Add(Value(SensorIds.GpuUsage, utilization.Gpu, "%", SensorKind.Load, timestampMs));
                result.Add(Value(SensorIds.GpuMemoryControllerUsage, utilization.Memory, "%", SensorKind.Load, timestampMs));
            }

            if (nvmlDeviceGetClockInfo(_device, ClockGraphics, out var coreClock) == NvmlSuccess && coreClock > 0)
            {
                result.Add(Value(SensorIds.GpuClock, coreClock, "MHz", SensorKind.Clock, timestampMs));
            }

            if (nvmlDeviceGetClockInfo(_device, ClockMemory, out var memClock) == NvmlSuccess && memClock > 0)
            {
                result.Add(Value(SensorIds.GpuMemoryClock, memClock, "MHz", SensorKind.Clock, timestampMs));
            }

            if (nvmlDeviceGetPowerUsage(_device, out var powerMw) == NvmlSuccess && powerMw > 0)
            {
                result.Add(Value(SensorIds.GpuPower, powerMw / 1000.0, "W", SensorKind.Power, timestampMs));
            }

            if (nvmlDeviceGetEnforcedPowerLimit(_device, out var limitMw) == NvmlSuccess && limitMw > 0)
            {
                result.Add(Value(SensorIds.GpuPowerLimit, limitMw / 1000.0, "W", SensorKind.Power, timestampMs));
            }

            if (nvmlDeviceGetMemoryInfo(_device, out var memory) == NvmlSuccess && memory.Total > 0)
            {
                const double bytesPerMb = 1024.0 * 1024.0;
                var totalMb = memory.Total / bytesPerMb;
                var usedMb = (memory.Used > 0 ? memory.Used : memory.Total - memory.Free) / bytesPerMb;
                result.Add(Value(SensorIds.VramTotal, totalMb, "MB", SensorKind.Data, timestampMs));
                result.Add(Value(SensorIds.VramUsed, usedMb, "MB", SensorKind.Data, timestampMs));
                result.Add(Value("gpu.mem.free", memory.Free / bytesPerMb, "MB", SensorKind.Data, timestampMs));
            }

            if (!_bar1Probed)
            {
                try
                {
                    if (nvmlDeviceGetBAR1MemoryInfo(_device, out var bar1) == NvmlSuccess)
                    {
                        _bar1Probed = true;
                        Bar1TotalBytes = bar1.Bar1Total > 0 && bar1.Bar1Total <= long.MaxValue
                            ? (long)bar1.Bar1Total
                            : null;
                    }
                }
                catch
                {
                    // Not exported by very old drivers; leave the feature Unknown.
                    _bar1Probed = true;
                }
            }

            if (nvmlDeviceGetNumFans(_device, out var fanCount) == NvmlSuccess)
            {
                _fanCount = fanCount;
            }

            if (_fanCount > 0)
            {
                _fanStatus = $"{_fanCount} controllable fan(s)";
                for (uint fan = 0; fan < Math.Min(_fanCount, 16u); fan++)
                {
                    if (TryGetFanPercent(fan, out var percent))
                    {
                        result.Add(FanValue(fan, FanMeasure.Percent, percent, "%", timestampMs));
                    }

                    if (TryGetFanRpm(fan, out var rpm))
                    {
                        result.Add(FanValue(fan, FanMeasure.Rpm, rpm, "RPM", timestampMs));
                    }
                }
            }
            else
            {
                // Older drivers expose only one aggregate fan. Use it only when the per-fan count
                // is unavailable/zero and the legacy query actually succeeds.
                if (nvmlDeviceGetFanSpeed(_device, out var fanPercent) == NvmlSuccess)
                {
                    _fanCount = Math.Max(_fanCount, 1);
                    _fanStatus = "legacy aggregate fan speed";
                    result.Add(FanValue(0, FanMeasure.Percent, fanPercent, "%", timestampMs));
                    if (TryGetFanRpm(0, out var legacyRpm))
                    {
                        result.Add(FanValue(0, FanMeasure.Rpm, legacyRpm, "RPM", timestampMs));
                    }
                }
                else
                {
                    // Laptop GPUs commonly delegate fan control to the embedded controller; NVML
                    // then reports zero fans and rejects every fan-speed query. This is a platform
                    // limitation, not a missing HardwareOSD call.
                    _fanStatus = _fanCount == 0
                        ? "NVML reports 0 fans; EC-managed fan control is not exposed"
                        : "NVML fan queries are not supported by this driver";
                }
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }

        return result;
    }

    private bool TryGetFanPercent(uint fan, out uint percent)
    {
        percent = 0;
        try
        {
            return nvmlDeviceGetFanSpeed_v2(_device, fan, out percent) == NvmlSuccess;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private bool TryGetFanRpm(uint fan, out uint rpm)
    {
        rpm = 0;
        if (_fanRpmUnsupported)
        {
            return false;
        }

        try
        {
            var info = new NvmlFanSpeedInfo
            {
                Version = (uint)(Marshal.SizeOf<NvmlFanSpeedInfo>() | (1 << 24)),
                Fan = fan,
            };
            if (nvmlDeviceGetFanSpeedRPM(_device, ref info) != NvmlSuccess)
            {
                return false;
            }

            rpm = info.Speed;
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            _fanRpmUnsupported = true;
            return false;
        }
    }

    private static SensorValue FanValue(uint fan, FanMeasure measure, double value, string unit, long timestampMs)
        => new(
            FanSensorIds.Id(FanComponent.Gpu, (int)fan, measure),
            value,
            unit,
            SensorKind.Fan,
            SensorOrigin.HardwareSensor,
            $"NVML: GPU Fan {fan + 1}",
            timestampMs);

    private static SensorValue Value(string id, double value, string unit, SensorKind kind, long timestampMs)
        => new(id, value, unit, kind, SensorOrigin.HardwareSensor, "NVML", timestampMs);

    public IEnumerable<string> Describe()
    {
        yield return $"NVML devices: {_deviceCount}";
        yield return $"Primary GPU: {_gpuName}";
        yield return $"GPU fans: {_fanCount}";
        yield return $"Fan status: {_fanStatus}";
        yield return "Interface: nvml.dll (driver-provided, user mode)";
    }

    public void Dispose()
    {
        if (_initialized)
        {
            try
            {
                nvmlShutdown();
            }
            catch
            {
                // ignored
            }

            _initialized = false;
        }

        IsAvailable = false;
        _device = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlFanSpeedInfo
    {
        public uint Version;
        public uint Fan;
        public uint Speed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlBar1Memory
    {
        public ulong Bar1Total;
        public ulong Bar1Free;
        public ulong Bar1Used;
    }

    [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
    private static extern int nvmlInit_v2();

    [DllImport("nvml.dll", EntryPoint = "nvmlShutdown")]
    private static extern int nvmlShutdown();

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2")]
    private static extern int nvmlDeviceGetCount_v2(out uint deviceCount);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetName")]
    private static extern int nvmlDeviceGetName(IntPtr device, byte[] name, uint length);

    [DllImport("nvml.dll", EntryPoint = "nvmlSystemGetNVMLVersion")]
    private static extern int nvmlSystemGetNVMLVersion(byte[] version, uint length);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetBAR1MemoryInfo")]
    private static extern int nvmlDeviceGetBAR1MemoryInfo(IntPtr device, out NvmlBar1Memory bar1);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")]
    private static extern int nvmlDeviceGetTemperature(IntPtr device, int sensorType, out uint temperature);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperatureThreshold")]
    private static extern int nvmlDeviceGetTemperatureThreshold(IntPtr device, int thresholdType, out uint temperature);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates")]
    private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetClockInfo")]
    private static extern int nvmlDeviceGetClockInfo(IntPtr device, int clockType, out uint clock);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerUsage")]
    private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint power);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetEnforcedPowerLimit")]
    private static extern int nvmlDeviceGetEnforcedPowerLimit(IntPtr device, out uint limit);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo")]
    private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetNumFans")]
    private static extern int nvmlDeviceGetNumFans(IntPtr device, out uint fanCount);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetFanSpeed")]
    private static extern int nvmlDeviceGetFanSpeed(IntPtr device, out uint speed);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetFanSpeed_v2")]
    private static extern int nvmlDeviceGetFanSpeed_v2(IntPtr device, uint fan, out uint speed);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetFanSpeedRPM")]
    private static extern int nvmlDeviceGetFanSpeedRPM(IntPtr device, ref NvmlFanSpeedInfo fanSpeed);
}
