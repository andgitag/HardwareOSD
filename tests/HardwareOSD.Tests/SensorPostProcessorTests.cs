using HardwareOSD.Core.Hardware;

namespace HardwareOSD.Tests;

public class SensorPostProcessorTests
{
    private const long Now = 1_700_000_000_000;

    private static Dictionary<string, SensorValue> Values()
        => new(StringComparer.Ordinal);

    private static void Add(Dictionary<string, SensorValue> values, string id, double value, string unit, SensorKind kind,
        SensorOrigin origin = SensorOrigin.HardwareSensor, string source = "test")
        => values[id] = new SensorValue(id, value, unit, kind, origin, source, Now);

    [Fact]
    public void ZeroPower_IsRejected()
    {
        var values = Values();
        Add(values, SensorIds.CpuPower, 0, "W", SensorKind.Power);
        SensorPostProcessor.Sanitize(values, Now);
        Assert.False(values[SensorIds.CpuPower].Available);
    }

    [Fact]
    public void ImpossibleTemperature_IsRejected()
    {
        var values = Values();
        Add(values, SensorIds.CpuTemperature, 999, "°C", SensorKind.Temperature);
        SensorPostProcessor.Sanitize(values, Now);
        Assert.False(values[SensorIds.CpuTemperature].Available);
    }

    [Fact]
    public void LoadAboveHundred_IsClamped()
    {
        var values = Values();
        Add(values, SensorIds.CpuUsage, 140, "%", SensorKind.Load);
        SensorPostProcessor.Sanitize(values, Now);
        Assert.Equal(100, values[SensorIds.CpuUsage].Value);
    }

    [Fact]
    public void VramPercent_IsDerivedFromUsedAndTotal()
    {
        var values = Values();
        Add(values, SensorIds.VramUsed, 4096, "MB", SensorKind.Data);
        Add(values, SensorIds.VramTotal, 12288, "MB", SensorKind.Data);
        SensorPostProcessor.Derive(values, Now);
        Assert.True(values[SensorIds.VramPercent].Available);
        Assert.Equal(33.333, values[SensorIds.VramPercent].Value, 2);
        Assert.Equal(SensorOrigin.Derived, values[SensorIds.VramPercent].Origin);
    }

    [Fact]
    public void VramUsed_IsDerivedFromFreeMemory()
    {
        var values = Values();
        Add(values, SensorIds.VramTotal, 12282, "MB", SensorKind.Data);
        Add(values, "gpu.mem.free", 10282, "MB", SensorKind.Data);
        SensorPostProcessor.Derive(values, Now);
        Assert.Equal(2000, values[SensorIds.VramUsed].Value, 3);
    }

    [Fact]
    public void CpuTemperature_FallsBackToPackageThenCores()
    {
        var values = Values();
        Add(values, SensorIds.CpuPackageTemperature, 71, "°C", SensorKind.Temperature);
        SensorPostProcessor.Derive(values, Now);
        Assert.Equal(71, values[SensorIds.CpuTemperature].Value);

        var coreOnly = Values();
        Add(coreOnly, SensorIds.CoreTemperature(0), 60, "°C", SensorKind.Temperature);
        Add(coreOnly, SensorIds.CoreTemperature(1), 70, "°C", SensorKind.Temperature);
        SensorPostProcessor.Derive(coreOnly, Now);
        Assert.Equal(65, coreOnly[SensorIds.CpuTemperature].Value, 3);
    }

    [Fact]
    public void ThermalZone_ProvidesCpuTemperatureOnlyWhenEnabledAndMissing()
    {
        var values = Values();
        Add(values, "thermalzone.temp", 44.5, "°C", SensorKind.Temperature, SensorOrigin.WindowsApi, "PDH");
        SensorPostProcessor.Derive(values, Now, useThermalZoneAsCpuTemperature: true);
        Assert.True(values[SensorIds.CpuTemperature].Available);
        Assert.Equal(44.5, values[SensorIds.CpuTemperature].Value);
        Assert.Equal("PDH ACPI thermal zone", values[SensorIds.CpuTemperature].Source);

        var disabled = Values();
        Add(disabled, "thermalzone.temp", 44.5, "°C", SensorKind.Temperature, SensorOrigin.WindowsApi, "PDH");
        SensorPostProcessor.Derive(disabled, Now, useThermalZoneAsCpuTemperature: false);
        Assert.False(disabled.ContainsKey(SensorIds.CpuTemperature));

        var real = Values();
        Add(real, SensorIds.CpuTemperature, 62, "°C", SensorKind.Temperature);
        Add(real, "thermalzone.temp", 44.5, "°C", SensorKind.Temperature, SensorOrigin.WindowsApi, "PDH");
        SensorPostProcessor.Derive(real, Now, useThermalZoneAsCpuTemperature: true);
        Assert.Equal(62, real[SensorIds.CpuTemperature].Value); // real sensor wins
    }

    [Fact]
    public void CpuClock_IsDerivedFromCoreClocks()
    {
        var values = Values();
        Add(values, SensorIds.CoreClock(0), 4000, "MHz", SensorKind.Clock);
        Add(values, SensorIds.CoreClock(1), 5000, "MHz", SensorKind.Clock);
        SensorPostProcessor.Derive(values, Now);
        Assert.Equal(4500, values[SensorIds.CpuClock].Value, 3);
    }

    [Fact]
    public void GpuPowerAboveTheEnforcedLimit_IsRejected()
    {
        var values = Values();
        Add(values, SensorIds.GpuPower, 593, "W", SensorKind.Power);
        Add(values, SensorIds.GpuPowerLimit, 155, "W", SensorKind.Power);
        SensorPostProcessor.Derive(values, Now);
        Assert.False(values[SensorIds.GpuPower].Available);
        Assert.StartsWith(SensorNotes.PowerAboveLimit, values[SensorIds.GpuPower].Note);

        var normal = Values();
        Add(normal, SensorIds.GpuPower, 140, "W", SensorKind.Power);
        Add(normal, SensorIds.GpuPowerLimit, 155, "W", SensorKind.Power);
        SensorPostProcessor.Derive(normal, Now);
        Assert.True(normal[SensorIds.GpuPower].Available);
        Assert.Equal(140, normal[SensorIds.GpuPower].Value);
    }

    [Fact]
    public void CpuUsageMax_IsDerivedFromPerCoreLoad()
    {
        var values = Values();
        Add(values, SensorIds.CoreUsage(0), 10, "%", SensorKind.Load);
        Add(values, SensorIds.CoreUsage(1), 83, "%", SensorKind.Load);
        Add(values, SensorIds.CoreUsage(2), 25, "%", SensorKind.Load);
        SensorPostProcessor.Derive(values, Now);

        Assert.True(values[SensorIds.CpuUsageMax].Available);
        Assert.Equal(83, values[SensorIds.CpuUsageMax].Value);
        Assert.Equal(SensorOrigin.Derived, values[SensorIds.CpuUsageMax].Origin);
    }

    [Fact]
    public void NaNValues_AreRejected()
    {
        var values = Values();
        Add(values, SensorIds.GpuTemperature, double.NaN, "°C", SensorKind.Temperature);
        SensorPostProcessor.Sanitize(values, Now);
        Assert.False(values[SensorIds.GpuTemperature].Available);
        Assert.StartsWith(SensorNotes.ReadingRejected, values[SensorIds.GpuTemperature].Note);
    }

    [Fact]
    public void UnavailableReason_IsPreservedInsteadOfRewrittenToEnglishProse()
    {
        var values = Values();
        values[SensorIds.CpuPower] = SensorValue.Unavailable(SensorIds.CpuPower, SensorKind.Power,
            "LibreHardwareMonitor", Now, SensorNotes.RequiresElevation);
        SensorPostProcessor.Sanitize(values, Now);
        Assert.False(values[SensorIds.CpuPower].Available);
        Assert.Equal(SensorNotes.RequiresElevation, values[SensorIds.CpuPower].Note);
    }

    [Fact]
    public void CpuMsrNote_MapsTokenAndDriverStateToStableReasonCodes()
    {
        Assert.Equal(SensorNotes.RequiresElevation,
            SensorNotes.ForCpuMsr(elevated: false, PawnIoState.Available));
        Assert.Equal(SensorNotes.PawnIoNotInstalled,
            SensorNotes.ForCpuMsr(elevated: true, PawnIoState.NotInstalled));
        Assert.Equal(SensorNotes.PawnIoAccessDenied,
            SensorNotes.ForCpuMsr(elevated: true, PawnIoState.AccessDenied));
        Assert.Equal(SensorNotes.MsrReadZero,
            SensorNotes.ForCpuMsr(elevated: true, PawnIoState.Available));
    }
}
