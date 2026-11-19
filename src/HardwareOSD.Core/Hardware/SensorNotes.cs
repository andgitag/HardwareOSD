namespace HardwareOSD.Core.Hardware;

/// <summary>
/// Stable reason codes attached to unavailable sensor entries. The UI maps them to localized text
/// and the audit CSV keeps the code, so a missing reading can be explained without embedding
/// machine-specific English prose in the measurement layer.
/// </summary>
public static class SensorNotes
{
    /// <summary>The sensor exists but its driver/MSR path needs an elevated token.</summary>
    public const string RequiresElevation = "requires-elevation";

    /// <summary>The PawnIO kernel driver is not installed, so CPU MSR sensors cannot be opened.</summary>
    public const string PawnIoNotInstalled = "pawnio-not-installed";

    /// <summary>PawnIO is installed but the current token cannot open its device.</summary>
    public const string PawnIoAccessDenied = "pawnio-access-denied";

    /// <summary>A power/energy sensor returned zero, which LHM uses for an unreadable MSR.</summary>
    public const string MsrReadZero = "msr-read-zero";

    /// <summary>Windows Energy Meter/EMI PDH counters are absent or could not be opened.</summary>
    public const string EnergyMeterUnavailable = "energy-meter-unavailable";

    /// <summary>The EMI counter set exists but this RAPL domain is not wired on the platform.</summary>
    public const string EnergyMeterChannelUnavailable = "energy-meter-channel-unavailable";

    /// <summary>The EMI domain reported a zero sample; no fabricated zero is published.</summary>
    public const string EnergyMeterZero = "energy-meter-zero";

    /// <summary>An EMI counter value could not be read or was implausible.</summary>
    public const string EnergyMeterReadFailed = "energy-meter-read-failed";

    /// <summary>A reading was outside the physically plausible range.</summary>
    public const string ReadingRejected = "reading-rejected";

    /// <summary>A power reading exceeded the card's enforced limit and was discarded.</summary>
    public const string PowerAboveLimit = "power-above-limit";

    /// <summary>No SuperIO, EC or vendor fan channel is exposed by the platform.</summary>
    public const string NoPlatformFanChannel = "no-platform-fan-channel";

    /// <summary>NVML reports zero controllable fans (common on laptops with EC fan control).</summary>
    public const string NvmlReportsNoFans = "nvml-reports-no-fans";

    /// <summary>The MSI ACPI fan class exists, but its WMI instance is restricted to administrators.</summary>
    public const string MsiAcpiRequiresElevation = "msi-acpi-requires-elevation";

    /// <summary>MSI_ACPI exists but an instance could not be opened for a reason other than access.</summary>
    public const string MsiAcpiUnavailable = "msi-acpi-unavailable";

    /// <summary>The MSI Get_Fan call failed or returned a failure status byte.</summary>
    public const string MsiAcpiReadFailed = "msi-acpi-read-failed";

    /// <summary>The MSI Get_Fan interface exposes RPM (period) but no PWM percentage channel.</summary>
    public const string MsiFanNoPercentage = "msi-fan-no-percentage";

    /// <summary>The optional LocalSystem broker is not installed, stopped, unreachable or timed out.</summary>
    public const string MsiBrokerUnavailable = "msi-broker-unavailable";

    /// <summary>The broker service exists but cannot access the administrator-only MSI WMI instance.</summary>
    public const string MsiBrokerAccessDenied = "msi-broker-access-denied";

    /// <summary>The broker service cannot find the vendor MSI_ACPI schema.</summary>
    public const string MsiBrokerClassMissing = "msi-broker-class-missing";

    /// <summary>The broker reached WMI but Get_Fan failed or returned a failure status.</summary>
    public const string MsiBrokerReadFailed = "msi-broker-read-failed";

    /// <summary>The firmware reported a zero period; the fan may be stopped or the channel unused.</summary>
    public const string MsiFanZero = "msi-fan-zero";

    /// <summary>A fan RPM channel exists but the hardware exposes no percentage channel.</summary>
    public const string FanRpmOnly = "fan-rpm-only";

    /// <summary>A fan percentage channel exists but the hardware exposes no tachometer.</summary>
    public const string FanPercentOnly = "fan-percent-only";

    /// <summary>
    /// Builds the note for a CPU MSR sensor from the live access probe. The returned code is one of
    /// the constants above and never contains a value.
    /// </summary>
    public static string ForCpuMsr(bool elevated, PawnIoState pawnIo)
    {
        if (!elevated)
        {
            return RequiresElevation;
        }

        return pawnIo switch
        {
            PawnIoState.NotInstalled => PawnIoNotInstalled,
            PawnIoState.AccessDenied => PawnIoAccessDenied,
            _ => MsrReadZero,
        };
    }
}
