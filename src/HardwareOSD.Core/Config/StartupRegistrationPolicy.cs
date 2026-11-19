namespace HardwareOSD.Core.Config;

/// <summary>
/// Decides when the per-user Windows Run key may be touched. Portable QA runs
/// (<c>--data-dir</c>) and demo mode must never modify the real startup registration, even when the
/// copied settings still contain <c>startWithWindows=true</c>. A normal launch only observes the
/// existing state; it never writes the Run key, so removing the entry outside the app is permanent
/// until the user explicitly enables the option again.
/// </summary>
public static class StartupRegistrationPolicy
{
    public static bool CanManage(string? dataDirectory, bool demo)
        => !demo && string.IsNullOrWhiteSpace(dataDirectory);

    /// <summary>
    /// The Run key is written only for an explicit user toggle that actually changes the current
    /// state. Launching the app, applying unrelated settings, or restoring a portable profile never
    /// qualifies.
    /// </summary>
    public static bool ShouldWrite(bool canManage, bool requested, bool currentlyActive)
        => canManage && requested != currentlyActive;
}
