using HardwareOSD.Core.Hardware;

namespace HardwareOSD.Core.Hardware.Providers;

/// <summary>
/// Read-only MSI notebook fan provider using the vendor ACPI WMI interface documented by the
/// Linux kernel (root\wmi:MSI_ACPI, Get_Fan subfeature 0x00). The interface is available on many
/// MSI laptops but its WMI instance ACL allows administrators only. It is deliberately a separate
/// provider from LibreHardwareMonitor/NVML: those libraries do not decode the MSI embedded
/// controller fan data on this platform.
///
/// Only Get_Fan is invoked. Set_Fan, EC writes and fan-control changes are not implemented.
/// </summary>
public sealed class MsiAcpiFanProvider : ISensorProvider
{
    private readonly MsiAcpiFanReader _reader = new();
    private string _lastReport = "no readings";
    private int _fanCount;

    public string Key => SensorMergePolicy.MsiAcpiKey;
    public string Name => "MSI ACPI fan interface";
    public string Category => "OEM ACPI/WMI";
    public bool IsAvailable => _reader.ClassPresent;
    public string? LastError => _reader.LastError;

    /// <summary>Human-readable state used by the diagnostics page and CLI.</summary>
    public string Detail => _reader.Detail;

    public void Initialize()
    {
        _reader.Initialize();
    }

    public IReadOnlyList<SensorValue> Read(long timestampMs)
    {
        var result = new List<SensorValue>(8);
        if (!_reader.ClassPresent)
        {
            return result;
        }

        var read = _reader.Read(timestampMs);
        if (!read.IsSuccess)
        {
            AddUnavailablePair(result, NoteFor(read.Status), timestampMs);
            return result;
        }

        _fanCount = read.Readings.Count(reading => reading.Period > 0);
        _lastReport = string.Join(", ", read.Readings.Select(r =>
            $"F{r.Index + 1}:{r.Period}->{r.Rpm:0} RPM"));

        // Fan 1 is the CPU fan and fan 2 is the GPU fan in MSI's EC layout. Fans 3 and 4 are
        // included only when the firmware reports a non-zero period; otherwise they would be
        // indistinguishable from unused channels on two-fan laptops.
        var ordinals = new Dictionary<FanComponent, int>();
        foreach (var reading in read.Readings)
        {
            if (reading.Index >= 2 && reading.Period == 0)
            {
                continue;
            }

            var component = reading.Index switch
            {
                0 => FanComponent.Cpu,
                1 => FanComponent.Gpu,
                2 => FanComponent.System,
                _ => FanComponent.Other,
            };
            var name = reading.Index switch
            {
                0 => "CPU Fan",
                1 => "GPU Fan",
                2 => "System Fan",
                _ => "Aux Fan",
            };
            ordinals.TryGetValue(component, out var componentIndex);
            ordinals[component] = componentIndex + 1;

            // The source carries the raw firmware period so the audit shows both the raw counter
            // and the derived RPM. FanSensorReader strips the parenthetical from the display name.
            var source = $"MSI_ACPI Get_Fan: {name} (period {reading.Period})";
            var note = reading.Period == 0 ? SensorNotes.MsiFanZero : null;
            result.Add(new SensorValue(
                FanSensorIds.Id(component, componentIndex, FanMeasure.Rpm),
                reading.Rpm,
                "RPM",
                SensorKind.Fan,
                SensorOrigin.HardwareSensor,
                source,
                timestampMs,
                note: note));

            // Get_Fan exposes no PWM/duty channel. Record the absence explicitly rather than
            // deriving a percentage from RPM; the audit then explains why the % metric is N/A.
            result.Add(SensorValue.Unavailable(
                FanSensorIds.Id(component, componentIndex, FanMeasure.Percent),
                SensorKind.Fan,
                source,
                timestampMs,
                SensorNotes.MsiFanNoPercentage));
        }

        return result;
    }

    /// <summary>
    /// Decodes a 32-byte Get_Fan response. Kept on the provider for compatibility with existing
    /// tests and callers; the implementation is shared with the broker reader.
    /// </summary>
    public static IReadOnlyList<MsiFanReading> ParseReport(byte[]? bytes)
        => MsiAcpiFanReader.ParseReport(bytes);

    public IEnumerable<string> Describe()
    {
        yield return $"WMI class: MSI_ACPI ({(_reader.ClassPresent ? "present" : "absent")})";
        yield return $"State: {_reader.Detail}";
        yield return $"Access: {(_reader.AccessDenied ? "requires administrator" : _reader.InstanceOpen ? "open" : "not open")}";
        yield return $"Fan readings: {_lastReport}";
        yield return $"Active fan periods: {_fanCount}";
        yield return "Mode: read-only Get_Fan; Set_Fan/EC writes are not used";
        if (!string.IsNullOrEmpty(_reader.LastError))
        {
            yield return "Last error: " + _reader.LastError;
        }
    }

    private static string NoteFor(MsiFanReadStatus status) => status switch
    {
        MsiFanReadStatus.AccessDenied => SensorNotes.MsiAcpiRequiresElevation,
        MsiFanReadStatus.ClassMissing or MsiFanReadStatus.InstanceUnavailable => SensorNotes.MsiAcpiUnavailable,
        _ => SensorNotes.MsiAcpiReadFailed,
    };

    private static void AddUnavailablePair(List<SensorValue> output, string note, long timestampMs)
    {
        foreach (var component in new[] { FanComponent.Cpu, FanComponent.Gpu })
        {
            foreach (var measure in new[] { FanMeasure.Rpm, FanMeasure.Percent })
            {
                output.Add(SensorValue.Unavailable(
                    FanSensorIds.Id(component, 0, measure),
                    SensorKind.Fan,
                    "MSI_ACPI Get_Fan",
                    timestampMs,
                    note));
            }
        }
    }

    public void Dispose()
    {
        _reader.Dispose();
    }
}
