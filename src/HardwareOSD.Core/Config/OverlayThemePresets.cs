namespace HardwareOSD.Core.Config;

/// <summary>
/// Theme presets. The dark theme uses a pure black background (no blue tint); the light theme uses
/// a neutral warm off-white. Presets only set colours — layout, opacity and metrics are untouched.
/// </summary>
public static class OverlayThemePresets
{
    public sealed record BackgroundPreset(string Name, string Color, int Opacity);

    public static readonly IReadOnlyList<BackgroundPreset> Backgrounds = new[]
    {
        new BackgroundPreset("Black", "#000000", 70),
        new BackgroundPreset("Graphite", "#121212", 74),
        new BackgroundPreset("Light neutral", "#F2F1EE", 92),
        new BackgroundPreset("Transparent", "#000000", 30),
    };

    public static void Apply(AppSettings settings, OverlayThemeMode mode)
    {
        var overlay = settings.Overlay;
        overlay.Theme = mode;

        if (mode == OverlayThemeMode.Light)
        {
            overlay.LabelColor = "#0F766E";
            overlay.ValueColor = "#14181C";
            overlay.UnitColor = "#5B6770";
            overlay.WarningColor = "#B45309";
            overlay.CriticalColor = "#B91C1C";
            overlay.BackgroundColor = "#F2F1EE";
            overlay.BackgroundOpacity = 92;
            overlay.Shadow = true;
            overlay.ShowSeparators = false;
        }
        else
        {
            overlay.LabelColor = "#6FD8CC";
            overlay.ValueColor = "#F4F7FA";
            overlay.UnitColor = "#9AA3AC";
            overlay.WarningColor = "#FFB020";
            overlay.CriticalColor = "#FF5C5C";
            overlay.BackgroundColor = "#000000";
            overlay.BackgroundOpacity = 70;
            overlay.Shadow = true;
            overlay.ShowSeparators = false;
        }
    }

    public static void ApplyBackground(OverlaySettings overlay, BackgroundPreset preset)
    {
        overlay.BackgroundColor = preset.Color;
        overlay.BackgroundOpacity = preset.Opacity;
        overlay.Theme = preset.Opacity >= 80 && preset.Color is "#F2F1EE"
            ? OverlayThemeMode.Light
            : overlay.Theme;
    }
}
