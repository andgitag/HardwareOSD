using System.Globalization;
using HardwareOSD.Core.Config;

namespace HardwareOSD.Core.Theming;

/// <summary>
/// Platform-neutral application-chrome palettes. The WPF theme manager only parses these hex
/// values and swaps the DynamicResource brushes, which keeps the colour definitions testable
/// without any WPF dependency.
/// </summary>
public static class AppThemeCatalog
{
    public const string DefaultAccent = "#4C8DFF";

    /// <summary>Accent choices offered in the custom-theme palette.</summary>
    public static readonly IReadOnlyList<string> AccentPresets = new[]
    {
        DefaultAccent, "#E5484D", "#F59E0B", "#22A06B", "#8B5CF6",
        "#EC4899", "#14B8A6", "#F97316", "#A3A3A3", "#D4AF37",
    };

    /// <summary>All application themes in display order. The custom theme is last.</summary>
    public static readonly IReadOnlyList<AppThemeMode> Themes = new[]
    {
        AppThemeMode.Black,
        AppThemeMode.Dark,
        AppThemeMode.WarmLight,
        AppThemeMode.Light,
        AppThemeMode.Custom,
    };

    private static readonly IReadOnlyDictionary<string, string> DarkPalette =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AppBackgroundBrush"] = "#1E1E1E",
            ["ChromeBrush"] = "#232323",
            ["PanelBrush"] = "#252525",
            ["PanelAltBrush"] = "#2B2B2B",
            ["FieldBrush"] = "#1A1A1A",
            ["BorderBrush"] = "#3A3A3A",
            ["BorderStrongBrush"] = "#4A4A4A",
            ["TextBrush"] = "#E5E5E5",
            ["MutedBrush"] = "#A0A0A0",
            ["DimBrush"] = "#707070",
            ["AccentBrush"] = "#4C8DFF",
            ["AccentDimBrush"] = "#1D2B44",
            ["BlueBrush"] = "#4C8DFF",
            ["WarnBrush"] = "#D6A24C",
            ["DangerBrush"] = "#C96A6A",
            ["SelectionBrush"] = "#1F2C44",
            ["HoverBrush"] = "#313131",
            ["PressedBrush"] = "#191919",
            ["TableRowHoverBrush"] = "#2A2A2A",
            ["PreviewBackgroundBrush"] = "#141414",
            ["AccentTextBrush"] = "#D8AE77",
            ["SuccessBrush"] = "#9AB87A",
            ["SystemPageBrush"] = "#252526",
            ["SystemCardBrush"] = "#2D2D30",
            ["SystemTileBrush"] = "#474749",
            ["SystemAccentBrush"] = "#0271F9",
            ["SystemHeadingBrush"] = "#EAEAEB",
            ["SystemValueBrush"] = "#EDEDED",
            ["SystemBodyBrush"] = "#C5C5C5",
            ["SystemDimBrush"] = "#BDBDBE",
            ["SystemIconBrush"] = "#AAAAAB",
            ["SystemSuccessBrush"] = "#4CAF50",
            ["SystemWarnBrush"] = "#FF9800",
            ["SystemBrandTextBrush"] = "#FFFFFF",
        };

    /// <summary>
    /// The former "Light" theme. Its values are unchanged; only the user-visible name is now
    /// "White". Keeping the palette separate from the warm theme preserves old configurations.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> WhitePalette =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AppBackgroundBrush"] = "#F4F4F4",
            ["ChromeBrush"] = "#ECECEC",
            ["PanelBrush"] = "#FFFFFF",
            ["PanelAltBrush"] = "#F7F7F7",
            ["FieldBrush"] = "#FFFFFF",
            ["BorderBrush"] = "#D0D0D0",
            ["BorderStrongBrush"] = "#B8B8B8",
            ["TextBrush"] = "#1F1F1F",
            ["MutedBrush"] = "#5F5F5F",
            ["DimBrush"] = "#8A8A8A",
            ["AccentBrush"] = "#1F6FEB",
            ["AccentDimBrush"] = "#DCE8FF",
            ["BlueBrush"] = "#1F6FEB",
            ["WarnBrush"] = "#9A6500",
            ["DangerBrush"] = "#A33A3A",
            ["SelectionBrush"] = "#DCE8FF",
            ["HoverBrush"] = "#E8E8E8",
            ["PressedBrush"] = "#DCDCDC",
            ["TableRowHoverBrush"] = "#F1F1F1",
            ["PreviewBackgroundBrush"] = "#E9E9E9",
            ["AccentTextBrush"] = "#8A4E14",
            ["SuccessBrush"] = "#4F7A3A",
            ["SystemPageBrush"] = "#F2F2F2",
            ["SystemCardBrush"] = "#FFFFFF",
            ["SystemTileBrush"] = "#E9E9E9",
            ["SystemAccentBrush"] = "#1F6FEB",
            ["SystemHeadingBrush"] = "#1B1B1B",
            ["SystemValueBrush"] = "#111111",
            ["SystemBodyBrush"] = "#3C3C3C",
            ["SystemDimBrush"] = "#6A6A6A",
            ["SystemIconBrush"] = "#5A5A5A",
            ["SystemSuccessBrush"] = "#2E7D32",
            ["SystemWarnBrush"] = "#B26A00",
            ["SystemBrandTextBrush"] = "#FFFFFF",
        };

    /// <summary>Absolute black surfaces with light, high-contrast text and clear blue accents.</summary>
    private static readonly IReadOnlyDictionary<string, string> BlackPalette =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AppBackgroundBrush"] = "#000000",
            ["ChromeBrush"] = "#050505",
            ["PanelBrush"] = "#0A0A0A",
            ["PanelAltBrush"] = "#101010",
            ["FieldBrush"] = "#000000",
            ["BorderBrush"] = "#222222",
            ["BorderStrongBrush"] = "#333333",
            ["TextBrush"] = "#F2F2F2",
            ["MutedBrush"] = "#A8A8A8",
            ["DimBrush"] = "#7A7A7A",
            ["AccentBrush"] = "#6EA8FF",
            ["AccentDimBrush"] = "#102844",
            ["BlueBrush"] = "#6EA8FF",
            ["WarnBrush"] = "#E0B15A",
            ["DangerBrush"] = "#F07A7A",
            ["SelectionBrush"] = "#122B4A",
            ["HoverBrush"] = "#151515",
            ["PressedBrush"] = "#0A0A0A",
            ["TableRowHoverBrush"] = "#111111",
            ["PreviewBackgroundBrush"] = "#000000",
            ["AccentTextBrush"] = "#C9DEFF",
            ["SuccessBrush"] = "#9CCB7A",
            ["SystemPageBrush"] = "#000000",
            ["SystemCardBrush"] = "#080808",
            ["SystemTileBrush"] = "#141414",
            ["SystemAccentBrush"] = "#6EA8FF",
            ["SystemHeadingBrush"] = "#F5F5F5",
            ["SystemValueBrush"] = "#FFFFFF",
            ["SystemBodyBrush"] = "#D0D0D0",
            ["SystemDimBrush"] = "#A8A8A8",
            ["SystemIconBrush"] = "#9A9A9A",
            ["SystemSuccessBrush"] = "#66BB6A",
            ["SystemWarnBrush"] = "#FFA726",
            ["SystemBrandTextBrush"] = "#FFFFFF",
        };

    /// <summary>Warm milk/cream surfaces; distinct from the pure-white theme.</summary>
    private static readonly IReadOnlyDictionary<string, string> WarmPalette =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AppBackgroundBrush"] = "#F4EDDF",
            ["ChromeBrush"] = "#EFE5D4",
            ["PanelBrush"] = "#FFFBF2",
            ["PanelAltBrush"] = "#F6EEDF",
            ["FieldBrush"] = "#FFFDF8",
            ["BorderBrush"] = "#DCCDB6",
            ["BorderStrongBrush"] = "#C8B79D",
            ["TextBrush"] = "#2C2822",
            ["MutedBrush"] = "#655C4F",
            ["DimBrush"] = "#756A5A",
            ["AccentBrush"] = "#A85E2A",
            ["AccentDimBrush"] = "#EAD6B8",
            ["BlueBrush"] = "#A85E2A",
            ["WarnBrush"] = "#956100",
            ["DangerBrush"] = "#A33A3A",
            ["SelectionBrush"] = "#EAD8BE",
            ["HoverBrush"] = "#EFE4D0",
            ["PressedBrush"] = "#E4D4BC",
            ["TableRowHoverBrush"] = "#F3E9D8",
            ["PreviewBackgroundBrush"] = "#EFE4D1",
            ["AccentTextBrush"] = "#6F3D14",
            ["SuccessBrush"] = "#4F7A3A",
            ["SystemPageBrush"] = "#F4EDDF",
            ["SystemCardBrush"] = "#FFFBF2",
            ["SystemTileBrush"] = "#EFE3CF",
            ["SystemAccentBrush"] = "#A85E2A",
            ["SystemHeadingBrush"] = "#2A2620",
            ["SystemValueBrush"] = "#1F1C17",
            ["SystemBodyBrush"] = "#4E463A",
            ["SystemDimBrush"] = "#6F6658",
            ["SystemIconBrush"] = "#6F6658",
            ["SystemSuccessBrush"] = "#2E7D32",
            ["SystemWarnBrush"] = "#A35F00",
            ["SystemBrandTextBrush"] = "#FFFFFF",
        };

    /// <summary>Returns a fresh palette dictionary; callers can never mutate a theme definition.</summary>
    public static IReadOnlyDictionary<string, string> Palette(AppThemeMode mode, string? customAccent = null)
    {
        return mode switch
        {
            AppThemeMode.Light => Copy(WhitePalette),
            AppThemeMode.Black => Copy(BlackPalette),
            AppThemeMode.WarmLight => Copy(WarmPalette),
            AppThemeMode.Custom => BuildCustomPalette(customAccent),
            _ => Copy(DarkPalette),
        };
    }

    private static IReadOnlyDictionary<string, string> Copy(IReadOnlyDictionary<string, string> palette)
        => new Dictionary<string, string>(palette, StringComparer.Ordinal);

    /// <summary>Sanitises a persisted accent value to an opaque #RRGGBB string.</summary>
    public static string NormalizeAccent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultAccent;
        }

        var text = value.Trim();
        if (!text.StartsWith('#'))
        {
            text = "#" + text;
        }

        if (text.Length is not (7 or 9))
        {
            return DefaultAccent;
        }

        for (var i = 1; i < text.Length; i++)
        {
            if (!Uri.IsHexDigit(text[i]))
            {
                return DefaultAccent;
            }
        }

        // Custom accents are always opaque; an optional alpha component is discarded.
        return "#" + text.Substring(1, 6).ToUpperInvariant();
    }

    /// <summary>
    /// The custom theme uses a light or dark base depending on which one keeps the chosen accent
    /// clearly visible. The accent itself is never altered, so the selected colour is preserved.
    /// </summary>
    public static bool CustomBaseIsDark(string? customAccent)
    {
        var accent = Parse(NormalizeAccent(customAccent));
        var darkContrast = Contrast(accent, Parse(DarkPalette["AppBackgroundBrush"]));
        var lightContrast = Contrast(accent, Parse(WhitePalette["AppBackgroundBrush"]));
        return darkContrast >= lightContrast;
    }

    public static bool IsDark(AppThemeMode mode, string? customAccent = null)
    {
        if (mode == AppThemeMode.Custom)
        {
            return CustomBaseIsDark(customAccent);
        }

        return mode is not (AppThemeMode.Light or AppThemeMode.WarmLight);
    }

    private static IReadOnlyDictionary<string, string> BuildCustomPalette(string? customAccent)
    {
        var normalizedAccent = NormalizeAccent(customAccent);
        var accent = Parse(normalizedAccent);
        var useDark = CustomBaseIsDark(normalizedAccent);
        var basePalette = useDark ? DarkPalette : WhitePalette;
        var palette = new Dictionary<string, string>(basePalette, StringComparer.Ordinal);
        var background = Parse(basePalette["AppBackgroundBrush"]);
        var target = useDark ? new Rgb(255, 255, 255) : new Rgb(0, 0, 0);

        // The custom theme is a full shell palette, not just an accent swap. Every neutral surface
        // is tinted with the chosen hue while the text colours keep their base contrast.
        string Tint(string key, double amount)
            => Hex(Blend(Parse(basePalette[key]), accent, amount));

        var tintStrength = useDark ? 1.0 : 0.78;
        var accentDim = Blend(background, accent, useDark ? 0.24 : 0.18);
        var selection = Blend(background, accent, useDark ? 0.34 : 0.25);
        var accentText = EnsureContrast(accent, accentDim, 4.5, target);

        palette["AppBackgroundBrush"] = Hex(Blend(background, accent, 0.045 * tintStrength));
        palette["ChromeBrush"] = Tint("ChromeBrush", 0.075 * tintStrength);
        palette["PanelBrush"] = Tint("PanelBrush", 0.105 * tintStrength);
        palette["PanelAltBrush"] = Tint("PanelAltBrush", 0.145 * tintStrength);
        palette["FieldBrush"] = Tint("FieldBrush", 0.065 * tintStrength);
        palette["BorderBrush"] = Tint("BorderBrush", 0.24 * tintStrength);
        palette["BorderStrongBrush"] = Tint("BorderStrongBrush", 0.36 * tintStrength);
        palette["HoverBrush"] = Tint("HoverBrush", 0.20 * tintStrength);
        palette["PressedBrush"] = Tint("PressedBrush", 0.13 * tintStrength);
        palette["TableRowHoverBrush"] = Tint("TableRowHoverBrush", 0.16 * tintStrength);
        palette["PreviewBackgroundBrush"] = Tint("PreviewBackgroundBrush", 0.035 * tintStrength);
        palette["SystemPageBrush"] = palette["AppBackgroundBrush"];
        palette["SystemCardBrush"] = palette["PanelBrush"];
        palette["SystemTileBrush"] = Tint("SystemTileBrush", 0.18 * tintStrength);

        palette["AccentBrush"] = Hex(accent);
        palette["BlueBrush"] = Hex(accent);
        palette["AccentDimBrush"] = Hex(accentDim);
        palette["SelectionBrush"] = Hex(selection);
        palette["AccentTextBrush"] = Hex(accentText);
        palette["SystemAccentBrush"] = Hex(accent);
        return palette;
    }

    public static double ContrastRatio(string first, string second)
        => Contrast(Parse(NormalizeAccent(first)), Parse(NormalizeAccent(second)));

    private static Rgb EnsureContrast(Rgb color, Rgb background, double minimum, Rgb target)
    {
        if (Contrast(color, background) >= minimum)
        {
            return color;
        }

        for (var step = 1; step <= 20; step++)
        {
            var candidate = Blend(color, target, step / 20.0);
            if (Contrast(candidate, background) >= minimum)
            {
                return candidate;
            }
        }

        return target;
    }

    private static double Contrast(Rgb first, Rgb second)
    {
        var a = RelativeLuminance(first);
        var b = RelativeLuminance(second);
        var lighter = Math.Max(a, b);
        var darker = Math.Min(a, b);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Rgb color)
        => 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);

    private static double Linear(byte channel)
    {
        var value = channel / 255.0;
        return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static Rgb Blend(Rgb first, Rgb second, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return new Rgb(
            (byte)Math.Round(first.R + (second.R - first.R) * amount),
            (byte)Math.Round(first.G + (second.G - first.G) * amount),
            (byte)Math.Round(first.B + (second.B - first.B) * amount));
    }

    private static Rgb Parse(string hex)
    {
        var text = NormalizeAccent(hex);
        var value = uint.Parse(text.AsSpan(1, 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new Rgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    private static string Hex(Rgb color)
        => "#" + color.R.ToString("X2", CultureInfo.InvariantCulture)
               + color.G.ToString("X2", CultureInfo.InvariantCulture)
               + color.B.ToString("X2", CultureInfo.InvariantCulture);

    private readonly record struct Rgb(byte R, byte G, byte B);
}
