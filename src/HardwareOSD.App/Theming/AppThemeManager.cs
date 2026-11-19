using System.Windows;
using System.Windows.Media;
using HardwareOSD.Core.Config;
using HardwareOSD.Core.Theming;

namespace HardwareOSD.App.Theming;

/// <summary>
/// Swaps the application chrome brushes at runtime. DynamicResource references in the
/// stylesheets update immediately; the OSD overlay colours are separate and unaffected.
///
/// The resource brushes are mutated in place rather than replaced. Code-generated controls use
/// <c>FindResource</c> for their local Foreground/Background values; mutating the shared brush
/// keeps those controls in sync as well as the DynamicResource bindings.
/// </summary>
public static class AppThemeManager
{
    public static bool IsDark { get; private set; } = true;

    public static AppThemeMode CurrentMode { get; private set; } = AppThemeMode.Dark;

    public static string CurrentAccent { get; private set; } = AppThemeCatalog.DefaultAccent;

    public static event EventHandler? ThemeChanged;

    public static void Apply(AppThemeMode mode) => Apply(mode, CurrentAccent);

    public static void Apply(AppThemeMode mode, string? customAccent)
    {
        var accent = AppThemeCatalog.NormalizeAccent(customAccent);
        var colors = AppThemeCatalog.Palette(mode, accent);
        var dictionary = Application.Current?.Resources.MergedDictionaries
            .FirstOrDefault(d => d.Contains("AppBackgroundBrush"));
        if (dictionary is null)
        {
            CurrentMode = mode;
            CurrentAccent = accent;
            IsDark = AppThemeCatalog.IsDark(mode, accent);
            return;
        }

        foreach (var pair in colors)
        {
            var color = Parse(pair.Value);
            if (dictionary[pair.Key] is SolidColorBrush existing && !existing.IsFrozen)
            {
                existing.Color = color;
            }
            else
            {
                dictionary[pair.Key] = new SolidColorBrush(color);
            }
        }

        CurrentMode = mode;
        CurrentAccent = accent;
        IsDark = AppThemeCatalog.IsDark(mode, accent);
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    public static Brush? Resource(string key)
        => Application.Current?.TryFindResource(key) as Brush;

    private static Color Parse(string value)
        => (Color)ColorConverter.ConvertFromString(value);
}
