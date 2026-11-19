using HardwareOSD.Core.SystemInfo;

namespace HardwareOSD.App.Views;

/// <summary>Colour role of a status dot. Only the three roles present in the reference are used.</summary>
public enum SystemTone
{
    Unknown,
    Good,
    Warn,
}

/// <summary>One compact status chip in the Diagnostics section.</summary>
public sealed record SystemDiagnostic(string Label, string Value, SystemTone Tone);

/// <summary>Everything the System page needs that is not a fast-changing sensor reading.</summary>
public sealed class SystemPageModel
{
    public SystemInfoSnapshot Info { get; init; } = SystemInfoSnapshot.Empty;

    public PlatformSecuritySnapshot Security { get; init; } = new(null, null, null, null, null);

    public PlatformFeatureSnapshot Features { get; init; } =
        new(FeatureStatus.Unknown, FeatureStatus.Unknown, FeatureStatus.Unknown);

    public IReadOnlyList<SoftwareComponentInfo> Software { get; init; } = Array.Empty<SoftwareComponentInfo>();

    public IReadOnlyList<SystemDiagnostic> Diagnostics { get; init; } = Array.Empty<SystemDiagnostic>();
}
