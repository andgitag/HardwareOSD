namespace HardwareOSD.Core.Hardware;

/// <summary>
/// Converts the shared MSI read result into the fixed broker wire model. It keeps the raw period and
/// never invents an RPM for a failed WMI read. CPU and GPU are always represented when Get_Fan
/// succeeds (a zero period is a real stopped/unused channel); fans 3/4 are included only when their
/// period is non-zero.
/// </summary>
public static class MsiFanBrokerSnapshotFactory
{
    public static FanBrokerSnapshot FromReadResult(MsiFanReadResult result, long responseTimestampMs)
    {
        if (result.Status != MsiFanReadStatus.Ok)
        {
            return FanBrokerSnapshot.Unavailable(ToReason(result.Status), result.Detail,
                responseTimestampMs, result.LastSuccessfulReadMs);
        }

        var channels = new List<FanBrokerChannel>(4);
        foreach (var reading in result.Readings)
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
            channels.Add(new FanBrokerChannel(
                reading.Index,
                component,
                reading.Period,
                (int)Math.Round(reading.Rpm),
                true));
        }

        var cpu = result.Readings.FirstOrDefault(reading => reading.Index == 0);
        var gpu = result.Readings.FirstOrDefault(reading => reading.Index == 1);
        var hasCpu = result.Readings.Any(reading => reading.Index == 0);
        var hasGpu = result.Readings.Any(reading => reading.Index == 1);

        return new FanBrokerSnapshot(
            FanBrokerStatus.Ok,
            FanBrokerReason.None,
            result.Detail,
            responseTimestampMs,
            result.LastSuccessfulReadMs,
            hasCpu ? (int)Math.Round(cpu.Rpm) : -1,
            hasGpu ? (int)Math.Round(gpu.Rpm) : -1,
            hasCpu ? cpu.Period : 0,
            hasGpu ? gpu.Period : 0,
            channels);
    }

    private static FanBrokerReason ToReason(MsiFanReadStatus status) => status switch
    {
        MsiFanReadStatus.AccessDenied => FanBrokerReason.AccessDenied,
        MsiFanReadStatus.ClassMissing => FanBrokerReason.ClassMissing,
        MsiFanReadStatus.InstanceUnavailable => FanBrokerReason.InstanceUnavailable,
        MsiFanReadStatus.ReadFailed => FanBrokerReason.WmiReadFailed,
        _ => FanBrokerReason.InternalError,
    };
}
