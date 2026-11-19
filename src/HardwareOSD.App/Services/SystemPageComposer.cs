using HardwareOSD.App.Localization;
using HardwareOSD.App.Views;
using HardwareOSD.Core.Fps;
using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.Hardware.Providers;
using HardwareOSD.Core.Security;
using HardwareOSD.Core.SystemInfo;

namespace HardwareOSD.App.Services;

/// <summary>
/// Inputs the System page diagnostics need. Kept as plain values so the same builder is used by the
/// live window and by the offscreen QA renderer.
/// </summary>
public sealed record SystemDiagnosticInput(
    bool Elevated,
    PawnIoStatus PawnIo,
    MsiFanBrokerStatus? Broker,
    bool CpuFan,
    bool GpuFan,
    PollingStatistics Statistics,
    int CpuSensors,
    int GpuSensors,
    EtwAccessState Etw,
    bool Recording,
    FpsSnapshot? Fps);

/// <summary>
/// Maps real probes and provider state onto the System page cards. No provider is created here and
/// no value is synthesised: absent data becomes Unknown / N/A in the view.
/// </summary>
public static class SystemPageComposer
{
    public static PlatformFeatureSnapshot ReadFeatures(SensorSnapshot sensors, NvmlProvider? nvml)
    {
        var vramMb = sensors.TryGet(SensorIds.VramTotal, out var vram) && vram.Available && vram.Value > 0
            ? vram.Value
            : 0;
        var vramBytes = vramMb > 0 ? (long?)(vramMb * 1024.0 * 1024.0) : null;
        return PlatformFeatureInfo.Read(nvml?.Bar1TotalBytes, vramBytes);
    }

    public static IReadOnlyList<SoftwareComponentInfo> ReadSoftware(
        NvmlProvider? nvml,
        MsiFanBrokerStatus? broker,
        PawnIoStatus pawnIo)
    {
        var nvmlPath = SoftwareComponentProbe.NvmlPath();
        var brokerExe = System.IO.Path.Combine(
            AppContext.BaseDirectory, "FanBroker", "HardwareOSD.FanBroker.exe");
        if (!System.IO.File.Exists(brokerExe))
        {
            brokerExe = SoftwareComponentProbe.ServiceImagePath(FanBrokerProtocol.ServiceName) ?? brokerExe;
        }

        var pawnIoState = pawnIo.State switch
        {
            PawnIoState.Available => SoftwareComponentState.Running,
            PawnIoState.AccessDenied => SoftwareComponentState.AccessDenied,
            PawnIoState.NotInstalled => SoftwareComponentState.Missing,
            _ => SoftwareComponentState.Unknown,
        };

        var brokerState = broker?.ServiceRunning == true
            ? SoftwareComponentState.Running
            : broker?.ServiceInstalled == true
                ? SoftwareComponentState.Installed
                : SoftwareComponentState.Missing;

        var nvmlState = nvml?.IsAvailable == true
            ? SoftwareComponentState.Available
            : nvmlPath is not null
                ? SoftwareComponentState.Installed
                : SoftwareComponentState.Missing;

        return new[]
        {
            new SoftwareComponentInfo("pawnio", SoftwareComponentProbe.PawnIoVersion(), pawnIoState),
            new SoftwareComponentInfo("broker", SoftwareComponentProbe.FileVersion(brokerExe), brokerState),
            new SoftwareComponentInfo("nvml", nvml?.NvmlVersion ?? SoftwareComponentProbe.FileVersion(nvmlPath),
                nvmlState),
            new SoftwareComponentInfo("lhm", SoftwareComponentProbe.LibreHardwareMonitorVersion(),
                SoftwareComponentState.Bundled),
        };
    }

    public static IReadOnlyList<SystemDiagnostic> BuildDiagnostics(SystemDiagnosticInput input)
    {
        var yes = Loc.T("common_yes");
        var no = Loc.T("common_no");
        var broker = input.Broker;

        return new List<SystemDiagnostic>
        {
            new(Loc.T("diagnostics_elevation"),
                Loc.T(input.Elevated ? "system_elevated" : "system_not_elevated"),
                input.Elevated ? SystemTone.Good : SystemTone.Warn),
            new(Loc.T("diagnostics_pawnio"), PawnIoText(input.PawnIo), PawnIoTone(input.PawnIo)),
            new(Loc.T("diagnostics_msi_broker_installed"),
                broker?.ServiceInstalled == true ? yes : no,
                broker?.ServiceInstalled == true ? SystemTone.Good : SystemTone.Unknown),
            new(Loc.T("diagnostics_msi_broker_running"),
                broker?.ServiceRunning == true ? yes : no,
                broker?.ServiceRunning == true ? SystemTone.Good : SystemTone.Warn),
            new(Loc.T("diagnostics_msi_broker_pipe"),
                broker?.PipeReachable == true ? yes : no,
                broker?.PipeReachable == true ? SystemTone.Good : SystemTone.Warn),
            new(Loc.T("diagnostics_fan_cpu"), input.CpuFan ? yes : no,
                input.CpuFan ? SystemTone.Good : SystemTone.Warn),
            new(Loc.T("diagnostics_fan_gpu"), input.GpuFan ? yes : no,
                input.GpuFan ? SystemTone.Good : SystemTone.Warn),
            new(Loc.T("diagnostics_provider_status"),
                Loc.F("diagnostics_provider_value",
                    input.Statistics.Providers.Count(provider => provider.Available),
                    input.Statistics.Providers.Count),
                input.Statistics.FailedPollCount == 0 ? SystemTone.Good : SystemTone.Warn),
            new(Loc.T("diagnostics_cpu_sensors"),
                Loc.F("diagnostics_sensor_value", input.CpuSensors, 4),
                input.CpuSensors >= 3 ? SystemTone.Good : input.CpuSensors > 0 ? SystemTone.Warn : SystemTone.Unknown),
            new(Loc.T("diagnostics_gpu_sensors"),
                Loc.F("diagnostics_sensor_value", input.GpuSensors, 5),
                input.GpuSensors >= 4 ? SystemTone.Good : input.GpuSensors > 0 ? SystemTone.Warn : SystemTone.Unknown),
            new(Loc.T("diagnostics_fps_access"),
                input.Etw.HasAccess ? Loc.T("system_provider_ok") : Loc.T("system_provider_failed"),
                input.Etw.HasAccess ? SystemTone.Good : SystemTone.Warn),
            new(Loc.T("diagnostics_recording_status"),
                input.Recording ? Loc.T("recording_state_recording") : Loc.T("recording_state_idle"),
                input.Recording ? SystemTone.Good : SystemTone.Unknown),
            new(Loc.T("diagnostics_fps_status"),
                input.Fps?.HasValue == true ? input.Fps.Fps.ToString("0") + " FPS" : Loc.T("diagnostics_none"),
                input.Fps?.HasValue == true ? SystemTone.Good : SystemTone.Unknown),
        };
    }

    private static string PawnIoText(PawnIoStatus status)
        => status.State switch
        {
            PawnIoState.Available => Loc.T("pawnio_state_available"),
            PawnIoState.AccessDenied => Loc.T("system_status_access_denied"),
            PawnIoState.NotInstalled => Loc.T("pawnio_state_not_installed"),
            _ => Loc.T("pawnio_state_unknown"),
        };

    private static SystemTone PawnIoTone(PawnIoStatus status)
        => status.State switch
        {
            PawnIoState.Available => SystemTone.Good,
            PawnIoState.AccessDenied => SystemTone.Warn,
            _ => SystemTone.Unknown,
        };
}
