using System.Security.Principal;
using HardwareOSD.Core.Hardware;
using LibreHardwareMonitor.Hardware;

namespace HardwareOSD.Core.Hardware.Providers;

/// <summary>
/// CPU / GPU sensor access through LibreHardwareMonitorLib (MPL-2.0).
/// Only CPU and GPU hardware is enabled: storage, network, controller and motherboard/SuperIO
/// access is intentionally disabled to keep the footprint minimal.
/// CPU temperature, package power and per-core clocks require the LibreHardwareMonitor kernel
/// driver, which is only available to elevated processes; without it the provider still exposes
/// GPU sensors and every value it can read in user mode.
/// </summary>
public sealed class LibreHardwareMonitorProvider : ISensorProvider
{
    private Computer? _computer;
    private bool _gpuMapped;
    private long _timestampMs;
    private long _lastGpuUpdateMs;
    private List<SensorValue> _cachedGpuValues = new(16);
    private string _inventory = string.Empty;
    private string _motherboardName = string.Empty;
    private int _motherboardSensorCount;
    private int _cpuFanChannels;
    private int _gpuFanChannels;
    private PawnIoStatus _pawnIo = new(PawnIoState.Unknown, "not probed");
    private string _cpuMsrNote = SensorNotes.MsrReadZero;

    /// <summary>
    /// NVIDIA's NVAPI path inside LibreHardwareMonitor costs 60-70 ms per update on a laptop GPU,
    /// while NVML covers the same metrics in under 0.1 ms. GPU sensor access is therefore refreshed
    /// on a slower cadence; cached values keep hotspot/memory-junction readings visible in between.
    /// </summary>
    private const int GpuRefreshIntervalMs = 3000;

    public string Key => SensorMergePolicy.LhmKey;
    public string Name => "LibreHardwareMonitor";
    public string Category => "Hardware sensor library";
    public bool IsAvailable { get; private set; }
    public string? LastError { get; private set; }

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    public void Initialize()
    {
        if (_computer is not null)
        {
            return;
        }

        var computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = false,
            IsMotherboardEnabled = true,
            IsControllerEnabled = false,
            IsNetworkEnabled = false,
            IsStorageEnabled = false,
        };

        computer.Open();
        _computer = computer;
        _pawnIo = PawnIoAccessProbe.Query();
        _cpuMsrNote = SensorNotes.ForCpuMsr(IsElevated, _pawnIo.State);

        var names = new List<string>();
        foreach (var hardware in computer.Hardware)
        {
            names.Add($"{hardware.HardwareType}: {hardware.Name}");
        }

        _inventory = string.Join(", ", names);
        IsAvailable = true;
        LastError = null;
    }

    public IReadOnlyList<SensorValue> Read(long timestampMs)
    {
        var output = new List<SensorValue>(128);
        _timestampMs = timestampMs;
        var computer = _computer;
        if (computer is null)
        {
            return output;
        }

        IHardware? cpu = null;
        IHardware? motherboard = null;
        var gpus = new List<IHardware>(2);

        foreach (var hardware in computer.Hardware)
        {
            switch (hardware.HardwareType)
            {
                case HardwareType.Cpu when cpu is null:
                    cpu = hardware;
                    break;
                case HardwareType.Motherboard when motherboard is null:
                    motherboard = hardware;
                    break;
                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                case HardwareType.GpuIntel:
                    gpus.Add(hardware);
                    break;
            }
        }

        if (cpu is not null)
        {
            UpdateAndMap(cpu, output, timestampMs, MapCpu);
        }

        if (motherboard is not null)
        {
            _motherboardName = motherboard.Name;
            _motherboardSensorCount = CountSensors(motherboard);
            // SuperIO fan tachometers and their control/PWM channels live beneath the motherboard.
            // Map every fan channel but never expose pumps as CPU fans.
            var fanOrdinals = new Dictionary<FanComponent, int>();
            UpdateAndMap(motherboard, output, timestampMs,
                (hardware, values) => MapFanSensors(hardware, values, fanOrdinals));
        }

        if (gpus.Count > 0)
        {
            // Prefer a discrete NVIDIA/AMD adapter over an integrated one.
            gpus.Sort((a, b) => GpuPriority(a.HardwareType).CompareTo(GpuPriority(b.HardwareType)));
            if (_cachedGpuValues.Count == 0 || timestampMs - _lastGpuUpdateMs >= GpuRefreshIntervalMs)
            {
                var fresh = new List<SensorValue>(20);
                _gpuMapped = false;
                UpdateAndMap(gpus[0], fresh, timestampMs, MapGpu);
                _cachedGpuValues = fresh;
                _lastGpuUpdateMs = timestampMs;
            }

            output.AddRange(_cachedGpuValues);
        }

        _cpuFanChannels = CountFanChannels(output, FanComponent.Cpu);
        _gpuFanChannels = CountFanChannels(output, FanComponent.Gpu);

        // Keep the reason visible even when the library finds no fan channel at all. These
        // unavailable entries never override a real MSI/NVML value; the merge policy only keeps
        // the first unavailable note and any available reading wins.
        if (_cpuFanChannels == 0)
        {
            AddUnavailableFan(output, FanComponent.Cpu, timestampMs,
                "LibreHardwareMonitor/motherboard: no SuperIO/EC fan channel");
        }

        if (_gpuFanChannels == 0)
        {
            AddUnavailableFan(output, FanComponent.Gpu, timestampMs,
                "LibreHardwareMonitor/GPU: no fan channel exposed");
        }

        return output;
    }

    private static void AddUnavailableFan(List<SensorValue> output, FanComponent component, long timestampMs, string source)
    {
        foreach (var measure in new[] { FanMeasure.Rpm, FanMeasure.Percent })
        {
            output.Add(SensorValue.Unavailable(
                FanSensorIds.Id(component, 0, measure),
                SensorKind.Fan,
                source,
                timestampMs,
                SensorNotes.NoPlatformFanChannel));
        }
    }

    private static int CountSensors(IHardware hardware)
    {
        var count = hardware.Sensors.Length;
        foreach (var sub in hardware.SubHardware)
        {
            count += CountSensors(sub);
        }

        return count;
    }

    private static int CountFanChannels(List<SensorValue> values, FanComponent component)
    {
        var channels = new HashSet<int>();
        foreach (var value in values)
        {
            if (FanSensorIds.TryParse(value.Id, out var parsed, out var index, out _) &&
                parsed == component)
            {
                channels.Add(index);
            }
        }

        return channels.Count;
    }

    private static int GpuPriority(HardwareType type) => type switch
    {
        HardwareType.GpuNvidia => 0,
        HardwareType.GpuAmd => 1,
        HardwareType.GpuIntel => 2,
        _ => 3,
    };

    private void UpdateAndMap(IHardware hardware, List<SensorValue> output, long timestampMs, Action<IHardware, List<SensorValue>> mapper)
    {
        try
        {
            hardware.Update();
            mapper(hardware, output);

            foreach (var sub in hardware.SubHardware)
            {
                try
                {
                    sub.Update();
                    mapper(sub, output);
                }
                catch (Exception ex)
                {
                    LastError = $"Sub-hardware read failed: {ex.Message}";
                }
            }
        }
        catch (Exception ex)
        {
            // A GPU driver reset (TDR) can make a whole device unreadable for a moment.
            LastError = ex.Message;
        }
    }

    private void MapCpu(IHardware hardware, List<SensorValue> output)
    {
        // LHM lists the P-core load only as a pair of thread sensors on hybrid Intel CPUs; the
        // aggregate "CPU Core #N" sensor exists for E-cores. Aggregate the thread pair so every
        // physical core has one reading and cpu.usage.max is not derived from half the CPU.
        var threadLoads = new Dictionary<int, (double Sum, int Count)>();

        foreach (var sensor in hardware.Sensors)
        {
            var name = sensor.Name;
            if (sensor.Value is not float raw || float.IsNaN(raw) || float.IsInfinity(raw))
            {
                // Keep the reason for an unreadable package sensor visible instead of dropping the
                // entry: the audit then says "requires elevation"/"PawnIO missing", not "not reported".
                if (sensor.SensorType == SensorType.Temperature &&
                    (NameIs(name, "CPU Package") || NameIs(name, "Core (Tctl/Tdie)")))
                {
                    AddUnavailable(output, SensorIds.CpuPackageTemperature, SensorKind.Temperature, name, _cpuMsrNote);
                }
                else if (sensor.SensorType == SensorType.Power &&
                         name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                {
                    AddUnavailable(output, SensorIds.CpuPower, SensorKind.Power, name, _cpuMsrNote);
                }
                else if (sensor.SensorType == SensorType.Power &&
                         TryCpuPowerDomainId(name, out var domainId))
                {
                    AddUnavailable(output, domainId, SensorKind.Power, name, _cpuMsrNote);
                }
                else if (sensor.SensorType == SensorType.Voltage && NameIs(name, "CPU Core"))
                {
                    AddUnavailable(output, SensorIds.CpuVoltage, SensorKind.Voltage, name, _cpuMsrNote);
                }

                continue;
            }

            var value = (double)raw;
            switch (sensor.SensorType)
            {
                case SensorType.Temperature:
                    if (NameIs(name, "CPU Package") || NameIs(name, "Core (Tctl/Tdie)"))
                    {
                        Add(output, SensorIds.CpuPackageTemperature, value, "°C", SensorKind.Temperature, "CPU Package");
                        AddIfMissing(output, SensorIds.CpuTemperature, value, "°C", SensorKind.Temperature);
                    }
                    else if (name.Contains("Core Average", StringComparison.OrdinalIgnoreCase) ||
                             name.Contains("Core Max", StringComparison.OrdinalIgnoreCase))
                    {
                        AddIfMissing(output, SensorIds.CpuTemperature, value, "°C", SensorKind.Temperature);
                        if (name.Contains("Core Max", StringComparison.OrdinalIgnoreCase))
                        {
                            Add(output, "cpu.temp.max", value, "°C", SensorKind.Temperature, name);
                        }
                    }
                    else if (TryCoreIndex(name, out var index))
                    {
                        Add(output, SensorIds.CoreTemperature(index), value, "°C", SensorKind.Temperature, name);
                    }

                    break;

                case SensorType.Load:
                    if (name.Contains("Thread", StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryThreadCoreIndex(name, out var threadCore))
                        {
                            threadLoads.TryGetValue(threadCore, out var aggregate);
                            threadLoads[threadCore] = (aggregate.Sum + value, aggregate.Count + 1);
                        }

                        break;
                    }

                    if (name.Contains("Total", StringComparison.OrdinalIgnoreCase))
                    {
                        Add(output, SensorIds.CpuUsage, value, "%", SensorKind.Load, name);
                    }
                    else if (TryCoreIndex(name, out var loadIndex))
                    {
                        Add(output, SensorIds.CoreUsage(loadIndex), value, "%", SensorKind.Load, name);
                    }

                    break;

                case SensorType.Clock:
                    if (!name.Contains("Bus", StringComparison.OrdinalIgnoreCase) && TryCoreIndex(name, out var clockIndex))
                    {
                        Add(output, SensorIds.CoreClock(clockIndex), value, "MHz", SensorKind.Clock, name);
                    }

                    break;

                case SensorType.Power:
                    if (name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                    {
                        // LHM reports 0 W when it has the sensor name but cannot execute the MSR read.
                        if (value <= 0.05)
                        {
                            AddUnavailable(output, SensorIds.CpuPower, SensorKind.Power, name, _cpuMsrNote);
                        }
                        else
                        {
                            Add(output, SensorIds.CpuPower, value, "W", SensorKind.Power, name);
                        }
                    }
                    else if (TryCpuPowerDomainId(name, out var domainId))
                    {
                        if (value <= 0.05)
                        {
                            AddUnavailable(output, domainId, SensorKind.Power, name, _cpuMsrNote);
                        }
                        else
                        {
                            Add(output, domainId, value, "W", SensorKind.Power, name);
                        }
                    }

                    break;

                case SensorType.Voltage:
                    if (NameIs(name, "CPU Core"))
                    {
                        if (value is > 0.05 and < 2.0)
                        {
                            Add(output, SensorIds.CpuVoltage, value, "V", SensorKind.Voltage, name);
                        }
                        else
                        {
                            AddUnavailable(output, SensorIds.CpuVoltage, SensorKind.Voltage, name, _cpuMsrNote);
                        }
                    }

                    break;
            }
        }

        foreach (var (index, aggregate) in threadLoads)
        {
            var id = SensorIds.CoreUsage(index);
            if (output.Any(v => string.Equals(v.Id, id, StringComparison.Ordinal)))
            {
                continue;
            }

            Add(output, id, aggregate.Sum / aggregate.Count, "%", SensorKind.Load,
                $"average of {aggregate.Count} threads");
        }

        // Aggregate the per-core MSR clocks so the merge policy can prefer real effective clocks
        // over the PDH estimate when the sensor library has driver access.
        var clockSum = 0.0;
        var clockCount = 0;
        foreach (var value in output)
        {
            if (value.Available &&
                value.Kind == SensorKind.Clock &&
                value.Id.StartsWith("cpu.core.", StringComparison.Ordinal) &&
                value.Id.EndsWith(".clock", StringComparison.Ordinal))
            {
                clockSum += value.Value;
                clockCount++;
            }
        }

        if (clockCount > 0)
        {
            Add(output, SensorIds.CpuClock, clockSum / clockCount, "MHz", SensorKind.Clock,
                $"average of {clockCount} core clocks");
        }

        if (!output.Any(value => value.Id == SensorIds.CpuVoltage))
        {
            AddUnavailable(output, SensorIds.CpuVoltage, SensorKind.Voltage, "CPU Core", _cpuMsrNote);
        }
    }

    private void MapGpu(IHardware hardware, List<SensorValue> output)
    {
        if (_gpuMapped)
        {
            return;
        }

        _gpuMapped = true;

        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.Value is not float raw || float.IsNaN(raw) || float.IsInfinity(raw))
            {
                continue;
            }

            var value = (double)raw;
            var name = sensor.Name;
            switch (sensor.SensorType)
            {
                case SensorType.Temperature:
                    if (NameIs(name, "GPU Core"))
                    {
                        Add(output, SensorIds.GpuTemperature, value, "°C", SensorKind.Temperature, name);
                    }
                    else if (name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase) ||
                             name.Contains("Hotspot", StringComparison.OrdinalIgnoreCase))
                    {
                        Add(output, SensorIds.GpuHotspot, value, "°C", SensorKind.Temperature, name);
                    }
                    else if (name.Contains("Memory Junction", StringComparison.OrdinalIgnoreCase))
                    {
                        Add(output, SensorIds.GpuMemoryJunction, value, "°C", SensorKind.Temperature, name);
                    }

                    break;

                case SensorType.Load:
                    if (NameIs(name, "GPU Core"))
                    {
                        Add(output, SensorIds.GpuUsage, value, "%", SensorKind.Load, name);
                    }
                    else if (name.Contains("Memory Controller", StringComparison.OrdinalIgnoreCase))
                    {
                        Add(output, SensorIds.GpuMemoryControllerUsage, value, "%", SensorKind.Load, name);
                    }

                    break;

                case SensorType.Clock:
                    if (NameIs(name, "GPU Core"))
                    {
                        Add(output, SensorIds.GpuClock, value, "MHz", SensorKind.Clock, name);
                    }
                    else if (NameIs(name, "GPU Memory"))
                    {
                        Add(output, SensorIds.GpuMemoryClock, value, "MHz", SensorKind.Clock, name);
                    }

                    break;

                case SensorType.Power:
                    if (name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                    {
                        Add(output, SensorIds.GpuPower, value, "W", SensorKind.Power, name);
                    }
                    else if (NameIs(name, "GPU Core"))
                    {
                        AddIfMissing(output, SensorIds.GpuPower, value, "W", SensorKind.Power);
                    }

                    break;

                case SensorType.SmallData:
                    if (name.Contains("GPU Memory Total", StringComparison.OrdinalIgnoreCase))
                    {
                        Add(output, SensorIds.VramTotal, value, "MB", SensorKind.Data, name);
                    }
                    else if (name.Contains("GPU Memory Free", StringComparison.OrdinalIgnoreCase))
                    {
                        Add(output, "gpu.mem.free", value, "MB", SensorKind.Data, name);
                    }

                    break;
            }
        }

        AddFanSensors(hardware, output, gpuHardware: true);
    }

    private void MapFanSensors(IHardware hardware, List<SensorValue> output, Dictionary<FanComponent, int> ordinals)
        => AddFanSensors(hardware, output, gpuHardware: false, ordinals);

    /// <summary>
    /// Maps physical fan tachometers and their paired PWM/control percentages. Pump channels are
    /// classified separately and never contribute to the CPU fan metrics. Generic "Fan #N" names
    /// stay in the Other bucket instead of being guessed to be the CPU fan.
    /// </summary>
    private void AddFanSensors(
        IHardware hardware,
        List<SensorValue> output,
        bool gpuHardware,
        Dictionary<FanComponent, int>? ordinals = null)
    {
        var controls = new Dictionary<int, (string Name, double Value)>();
        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.SensorType == SensorType.Control &&
                sensor.Value is float rawControl &&
                !float.IsNaN(rawControl) &&
                !float.IsInfinity(rawControl))
            {
                controls[sensor.Index] = (sensor.Name, rawControl);
            }
        }

        ordinals ??= new Dictionary<FanComponent, int>();
        var hardwareSource = string.IsNullOrWhiteSpace(_motherboardName) || gpuHardware
            ? $"LibreHardwareMonitor/{hardware.Name}"
            : $"LibreHardwareMonitor/{_motherboardName}/{hardware.Name}";
        var pairedControls = new HashSet<int>();

        foreach (var sensor in hardware.Sensors
                     .Where(s => s.SensorType == SensorType.Fan)
                     .OrderBy(s => s.Index)
                     .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (sensor.Value is not float raw ||
                float.IsNaN(raw) ||
                float.IsInfinity(raw))
            {
                continue;
            }

            var component = gpuHardware
                ? FanClassifier.Classify(sensor.Name, gpuHardware: true)
                : FanClassifier.Classify(sensor.Name);
            ordinals.TryGetValue(component, out var next);
            ordinals[component] = next + 1;

            output.Add(new SensorValue(
                FanSensorIds.Id(component, next, FanMeasure.Rpm),
                raw,
                "RPM",
                SensorKind.Fan,
                SensorOrigin.HardwareSensor,
                $"{hardwareSource}: {sensor.Name}",
                _timestampMs));

            if (!controls.TryGetValue(sensor.Index, out var control))
            {
                continue;
            }

            var controlComponent = gpuHardware
                ? FanClassifier.Classify(control.Name, gpuHardware: true)
                : FanClassifier.Classify(control.Name);
            if (controlComponent != component)
            {
                continue;
            }

            pairedControls.Add(sensor.Index);
            output.Add(new SensorValue(
                FanSensorIds.Id(component, next, FanMeasure.Percent),
                control.Value,
                "%",
                SensorKind.Fan,
                SensorOrigin.HardwareSensor,
                $"{hardwareSource}: {control.Name}",
                _timestampMs));
        }

        // NVIDIA's NVAPI path often exposes the fan PWM control without a tachometer. The control
        // channel is still a real hardware reading, so map it as a percentage channel instead of
        // discarding it. Motherboard controls are mapped only when their name identifies a fan or
        // pump; generic "Control #N" channels stay out of the fan metrics.
        foreach (var (index, control) in controls.OrderBy(pair => pair.Key))
        {
            if (pairedControls.Contains(index) || !LooksLikeFanControl(control.Name))
            {
                continue;
            }

            var component = gpuHardware
                ? FanClassifier.Classify(control.Name, gpuHardware: true)
                : FanClassifier.Classify(control.Name);
            if (!gpuHardware && component == FanComponent.Other)
            {
                continue;
            }

            ordinals.TryGetValue(component, out var next);
            ordinals[component] = next + 1;
            output.Add(new SensorValue(
                FanSensorIds.Id(component, next, FanMeasure.Percent),
                control.Value,
                "%",
                SensorKind.Fan,
                SensorOrigin.HardwareSensor,
                $"{hardwareSource}: {control.Name}",
                _timestampMs));
        }
    }

    private static bool LooksLikeFanControl(string name)
        => name.Contains("fan", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("pump", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("water", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("liquid", StringComparison.OrdinalIgnoreCase);

    private static bool NameIs(string name, string expected) =>
        string.Equals(name.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>Maps LHM's Intel RAPL power-domain labels to HardwareOSD's stable sensor ids.</summary>
    public static bool TryCpuPowerDomainId(string name, out string sensorId)
    {
        if (NameIs(name, "CPU Cores"))
        {
            sensorId = SensorIds.CpuPowerCores;
            return true;
        }

        if (NameIs(name, "CPU Memory"))
        {
            sensorId = SensorIds.CpuPowerMemory;
            return true;
        }

        if (NameIs(name, "CPU Graphics"))
        {
            sensorId = SensorIds.CpuPowerGt;
            return true;
        }

        sensorId = string.Empty;
        return false;
    }

    /// <summary>
    /// Extracts a 0-based physical core index from names such as "P-Core #3", "CPU Core #2" or
    /// "Core 5". Intel hybrid E-cores are placed after the P-core range (P0..P7, E0..E7 => ids
    /// 0..7 and 8..15) so P-Core #1 and E-Core #1 can never overwrite the same sensor id.
    /// </summary>
    public static bool TryCoreIndex(string name, out int index)
    {
        index = -1;
        if (name.Contains("Thread", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Distance", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Average", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var isEcore = name.Contains("E-Core", StringComparison.OrdinalIgnoreCase);
        var hash = name.LastIndexOf('#');
        if (hash >= 0 && int.TryParse(name.AsSpan(hash + 1).Trim(), out var parsed))
        {
            index = Math.Clamp(parsed - 1, 0, 255);
            if (isEcore)
            {
                index = Math.Clamp(index + 8, 0, 255);
            }

            return true;
        }

        if (name.StartsWith("Core ", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(name.AsSpan(5).Trim(), out var plain))
        {
            index = Math.Clamp(plain - 1, 0, 255);
            return true;
        }

        return false;
    }

    /// <summary>Extracts a 0-based physical core index from a thread sensor such as "CPU Core #1 Thread #2".</summary>
    internal static bool TryThreadCoreIndex(string name, out int index)
    {
        index = -1;
        var marker = name.IndexOf("Thread", StringComparison.OrdinalIgnoreCase);
        if (marker <= 0)
        {
            return false;
        }

        return TryCoreIndex(name[..marker].Trim(), out index);
    }

    private void Add(List<SensorValue> output, string id, double value, string unit, SensorKind kind, string sensorName)
        => output.Add(new SensorValue(id, value, unit, kind, SensorOrigin.HardwareSensor, $"LibreHardwareMonitor/{sensorName}", _timestampMs));

    private void AddUnavailable(List<SensorValue> output, string id, SensorKind kind, string sensorName, string note)
    {
        if (output.Any(v => string.Equals(v.Id, id, StringComparison.Ordinal)))
        {
            return;
        }

        output.Add(SensorValue.Unavailable(id, kind, $"LibreHardwareMonitor/{sensorName}", _timestampMs, note));
    }

    private void AddIfMissing(List<SensorValue> output, string id, double value, string unit, SensorKind kind)
    {
        foreach (var existing in output)
        {
            if (existing.Id == id)
            {
                return;
            }
        }

        output.Add(new SensorValue(id, value, unit, kind, SensorOrigin.HardwareSensor, "LibreHardwareMonitor", _timestampMs));
    }

    public IEnumerable<string> Describe()
    {
        yield return $"Inventory: {(_inventory.Length == 0 ? "(none)" : _inventory)}";
        yield return $"Elevated: {(IsElevated ? "yes (full CPU sensor access)" : "no (CPU temperature/power may be unavailable)")}";
        yield return $"PawnIO: {_pawnIo.State} ({_pawnIo.Detail})";
        yield return $"CPU MSR note: {_cpuMsrNote}";
        yield return $"Motherboard sensors: {_motherboardSensorCount} ({(_motherboardSensorCount == 0 ? "no SuperIO/EC fan channels exposed" : "SuperIO channels present")})";
        yield return $"Fan channels: CPU {_cpuFanChannels}, GPU {_gpuFanChannels}";
        yield return "Enabled categories: CPU, GPU, Motherboard/SuperIO fan channels";
    }

    /// <summary>Full inventory of every sensor the library exposes, for the audit report.</summary>
    public IEnumerable<string> DescribeAllSensors()
    {
        var computer = _computer;
        if (computer is null)
        {
            yield break;
        }

        foreach (var hardware in computer.Hardware)
        {
            try
            {
                hardware.Update();
            }
            catch
            {
                // keep auditing the remaining hardware
            }

            yield return $"== {hardware.HardwareType}: {hardware.Name} ==";
            foreach (var sensor in hardware.Sensors)
            {
                yield return $"{sensor.SensorType}|{sensor.Name}|{sensor.Value?.ToString("0.###") ?? "N/A"}";
            }

            foreach (var sub in hardware.SubHardware)
            {
                try
                {
                    sub.Update();
                }
                catch
                {
                    // ignore
                }

                yield return $"== {sub.HardwareType}: {sub.Name} (sub) ==";
                foreach (var sensor in sub.Sensors)
                {
                    yield return $"{sensor.SensorType}|{sensor.Name}|{sensor.Value?.ToString("0.###") ?? "N/A"}";
                }
            }
        }
    }

    public void Dispose()
    {
        IsAvailable = false;
        try
        {
            _computer?.Close();
        }
        catch
        {
            // ignored
        }

        _computer = null;
    }
}
