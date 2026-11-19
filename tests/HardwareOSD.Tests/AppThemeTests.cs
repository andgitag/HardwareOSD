using System.Text.Json;
using HardwareOSD.Core.Config;
using HardwareOSD.Core.Diagnostics;
using HardwareOSD.Core.Theming;

namespace HardwareOSD.Tests;

public class AppThemeTests
{
    private static readonly string[] RequiredKeys =
    {
        "AppBackgroundBrush", "ChromeBrush", "PanelBrush", "PanelAltBrush", "FieldBrush",
        "BorderBrush", "BorderStrongBrush", "TextBrush", "MutedBrush", "DimBrush",
        "AccentBrush", "AccentDimBrush", "BlueBrush", "WarnBrush", "DangerBrush",
        "SelectionBrush", "HoverBrush", "PressedBrush", "TableRowHoverBrush",
        "PreviewBackgroundBrush", "AccentTextBrush", "SuccessBrush",
        "SystemPageBrush", "SystemCardBrush", "SystemTileBrush", "SystemAccentBrush",
        "SystemHeadingBrush", "SystemValueBrush", "SystemBodyBrush", "SystemDimBrush",
        "SystemIconBrush", "SystemSuccessBrush", "SystemWarnBrush", "SystemBrandTextBrush",
    };

    [Fact]
    public void EveryTheme_HasTheCompletePalette()
    {
        foreach (var mode in AppThemeCatalog.Themes)
        {
            var palette = AppThemeCatalog.Palette(mode, "#4C8DFF");
            foreach (var key in RequiredKeys)
            {
                Assert.True(palette.TryGetValue(key, out var value), $"{mode} is missing {key}");
                Assert.Matches("^#[0-9A-F]{6}$", value!);
            }
        }
    }

    [Fact]
    public void ThemeOrder_IsBlackDarkLightWhiteCustom()
    {
        Assert.Equal(new[]
        {
            AppThemeMode.Black,
            AppThemeMode.Dark,
            AppThemeMode.WarmLight,
            AppThemeMode.Light,
            AppThemeMode.Custom,
        }, AppThemeCatalog.Themes);
    }

    [Fact]
    public void BlackTheme_UsesPureBlackAndNeutralDarkSurfaces()
    {
        var palette = AppThemeCatalog.Palette(AppThemeMode.Black);
        Assert.Equal("#000000", palette["AppBackgroundBrush"]);
        Assert.Equal("#000000", palette["PreviewBackgroundBrush"]);

        foreach (var key in new[]
                 {
                     "AppBackgroundBrush", "ChromeBrush", "PanelBrush", "PanelAltBrush", "FieldBrush",
                     "BorderBrush", "BorderStrongBrush", "HoverBrush", "PressedBrush",
                     "TableRowHoverBrush", "SystemPageBrush", "SystemCardBrush", "SystemTileBrush",
                 })
        {
            var (r, g, b) = Rgb(palette[key]);
            Assert.True(r == g && g == b, $"{key} should be a neutral black shade, was {palette[key]}");
            Assert.InRange(r, 0, 64);
        }

        Assert.True(AppThemeCatalog.ContrastRatio(palette["TextBrush"], palette["AppBackgroundBrush"]) >= 7.0);
        Assert.True(AppThemeCatalog.ContrastRatio(palette["MutedBrush"], palette["PanelBrush"]) >= 4.5);
        Assert.True(AppThemeCatalog.ContrastRatio(palette["AccentBrush"], palette["AppBackgroundBrush"]) >= 3.0);
        Assert.True(AppThemeCatalog.ContrastRatio(palette["TextBrush"], palette["SelectionBrush"]) >= 4.5);
    }

    [Fact]
    public void WhiteAndWarmThemes_AreDistinctAndReadable()
    {
        var white = AppThemeCatalog.Palette(AppThemeMode.Light);
        var warm = AppThemeCatalog.Palette(AppThemeMode.WarmLight);

        Assert.Equal("#F4F4F4", white["AppBackgroundBrush"]);
        Assert.Equal("#F4EDDF", warm["AppBackgroundBrush"]);
        Assert.NotEqual(white["AppBackgroundBrush"], warm["AppBackgroundBrush"]);
        Assert.NotEqual(white["PanelBrush"], warm["PanelBrush"]);

        var (wr, wg, wb) = Rgb(warm["AppBackgroundBrush"]);
        Assert.True(wr > wg && wg > wb, "the warm theme should be cream-tinted, not neutral white");

        Assert.True(AppThemeCatalog.ContrastRatio(warm["TextBrush"], warm["AppBackgroundBrush"]) >= 7.0);
        Assert.True(AppThemeCatalog.ContrastRatio(warm["MutedBrush"], warm["PanelBrush"]) >= 4.5);
        Assert.True(AppThemeCatalog.ContrastRatio(warm["AccentBrush"], warm["PanelBrush"]) >= 4.0);
        Assert.True(AppThemeCatalog.ContrastRatio(warm["AccentTextBrush"], warm["AccentDimBrush"]) >= 4.5);
    }

    [Fact]
    public void CustomTheme_PreservesTheExactAccentAndKeepsItReadable()
    {
        foreach (var accent in new[] { "#E5484D", "#22A06B", "#4C8DFF", "#123456", "#000000", "#FFFFFF" })
        {
            var palette = AppThemeCatalog.Palette(AppThemeMode.Custom, accent);
            Assert.Equal(accent, palette["AccentBrush"]);
            Assert.Equal(accent, palette["BlueBrush"]);
            Assert.Equal(accent, palette["SystemAccentBrush"]);
            Assert.True(AppThemeCatalog.ContrastRatio(palette["AccentBrush"], palette["AppBackgroundBrush"]) >= 3.0,
                $"accent {accent} should contrast with the chosen base");
            Assert.True(AppThemeCatalog.ContrastRatio(palette["TextBrush"], palette["AppBackgroundBrush"]) >= 7.0);
            Assert.True(AppThemeCatalog.ContrastRatio(palette["AccentTextBrush"], palette["AccentDimBrush"]) >= 4.5);
        }
    }

    [Fact]
    public void CustomTheme_TintsTheWholeShellNotOnlyButtons()
    {
        var accent = "#22A06B";
        var dark = AppThemeCatalog.Palette(AppThemeMode.Dark);
        var custom = AppThemeCatalog.Palette(AppThemeMode.Custom, accent);

        foreach (var key in new[]
                 {
                     "AppBackgroundBrush", "ChromeBrush", "PanelBrush", "PanelAltBrush", "FieldBrush",
                     "BorderBrush", "BorderStrongBrush", "HoverBrush", "PressedBrush",
                     "TableRowHoverBrush", "PreviewBackgroundBrush", "SystemCardBrush", "SystemTileBrush",
                 })
        {
            Assert.NotEqual(dark[key], custom[key]);
        }

        Assert.Equal(accent, custom["AccentBrush"]);
        Assert.True(AppThemeCatalog.ContrastRatio(custom["TextBrush"], custom["PanelBrush"]) >= 7.0);
        Assert.True(AppThemeCatalog.ContrastRatio(custom["MutedBrush"], custom["PanelBrush"]) >= 4.5);
    }

    [Fact]
    public void CustomTheme_ChoosesALightBaseForDarkAccentsAndDarkBaseForLightAccents()
    {
        Assert.False(AppThemeCatalog.CustomBaseIsDark("#123456"));
        Assert.False(AppThemeCatalog.CustomBaseIsDark("#000000"));
        Assert.True(AppThemeCatalog.CustomBaseIsDark("#FFFFFF"));
        Assert.True(AppThemeCatalog.CustomBaseIsDark("#F59E0B"));

        var light = AppThemeCatalog.Palette(AppThemeMode.Custom, "#123456");
        var dark = AppThemeCatalog.Palette(AppThemeMode.Custom, "#FFFFFF");
        Assert.NotEqual("#1E1E1E", light["AppBackgroundBrush"]);
        Assert.NotEqual("#F4F4F4", dark["AppBackgroundBrush"]);
        Assert.True(AppThemeCatalog.ContrastRatio(light["TextBrush"], light["AppBackgroundBrush"]) >= 7.0);
        Assert.True(AppThemeCatalog.ContrastRatio(dark["TextBrush"], dark["AppBackgroundBrush"]) >= 7.0);
    }

    [Fact]
    public void CustomTheme_DoesNotMutateTheStandardPalettes()
    {
        var whiteBefore = AppThemeCatalog.Palette(AppThemeMode.Light);
        var darkBefore = AppThemeCatalog.Palette(AppThemeMode.Dark);
        _ = AppThemeCatalog.Palette(AppThemeMode.Custom, "#ABCDEF");
        var whiteAfter = AppThemeCatalog.Palette(AppThemeMode.Light);
        var darkAfter = AppThemeCatalog.Palette(AppThemeMode.Dark);

        Assert.Equal(whiteBefore.OrderBy(p => p.Key), whiteAfter.OrderBy(p => p.Key));
        Assert.Equal(darkBefore.OrderBy(p => p.Key), darkAfter.OrderBy(p => p.Key));
    }

    [Fact]
    public void AccentNormalisation_AcceptsHexWithAlphaAndFallsBackSafely()
    {
        Assert.Equal("#AABBCC", AppThemeCatalog.NormalizeAccent("#aabbcc"));
        Assert.Equal("#AABBCC", AppThemeCatalog.NormalizeAccent("aabbcc"));
        Assert.Equal("#AABBCC", AppThemeCatalog.NormalizeAccent("#AABBCC80"));
        Assert.Equal(AppThemeCatalog.DefaultAccent, AppThemeCatalog.NormalizeAccent("not-a-colour"));
        Assert.Equal(AppThemeCatalog.DefaultAccent, AppThemeCatalog.NormalizeAccent(null));
    }

    [Fact]
    public void ThemeEnum_KeepsLegacyLightValueAndAddsNewThemes()
    {
        Assert.Equal(1, (int)AppThemeMode.Light);
        Assert.Equal(AppThemeMode.Light, AppThemeMode.White);
        Assert.NotEqual(AppThemeMode.Dark, AppThemeMode.Black);
        Assert.NotEqual(AppThemeMode.Light, AppThemeMode.WarmLight);
        Assert.NotEqual(AppThemeMode.Custom, AppThemeMode.WarmLight);
    }

    [Fact]
    public void Settings_RoundTripCustomThemeAndAccent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "HardwareOSD-theme-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var logger = new FileLogger(directory, "theme-test");
        try
        {
            var path = Path.Combine(directory, "settings.json");
            var store = new SettingsStore(path, logger);
            var settings = AppSettings.CreateDefault();
            settings.Behavior.AppTheme = AppThemeMode.Custom;
            settings.Behavior.AppAccentColor = "#e5484d";

            Assert.True(store.Save(settings));
            var json = File.ReadAllText(path);
            Assert.Contains("\"appTheme\": \"custom\"", json);
            Assert.Contains("\"appAccentColor\": \"#E5484D\"", json);

            var loaded = store.Load(out _);
            Assert.Equal(AppThemeMode.Custom, loaded.Behavior.AppTheme);
            Assert.Equal("#E5484D", loaded.Behavior.AppAccentColor);
        }
        finally
        {
            logger.Dispose();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // ignored
            }
        }
    }

    [Fact]
    public void Settings_LegacyLightThemeNameStillLoadsAsWhite()
    {
        var directory = Path.Combine(Path.GetTempPath(), "HardwareOSD-theme-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var logger = new FileLogger(directory, "theme-legacy");
        try
        {
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path,
                "{ \"behavior\": { \"appTheme\": \"light\", \"appAccentColor\": \"#123456\" } }");

            var store = new SettingsStore(path, logger);
            var loaded = store.Load(out var status);
            Assert.Equal("Settings loaded", status);
            Assert.Equal(AppThemeMode.White, loaded.Behavior.AppTheme);
            Assert.Equal(AppThemeMode.Light, loaded.Behavior.AppTheme);
            Assert.Equal("#123456", loaded.Behavior.AppAccentColor);

            Assert.True(store.Save(loaded));
            Assert.Contains("\"appTheme\": \"light\"", File.ReadAllText(path));
        }
        finally
        {
            logger.Dispose();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // ignored
            }
        }
    }

    [Fact]
    public void CloneAndNormalize_KeepIndependentCustomAccent()
    {
        var settings = AppSettings.CreateDefault();
        settings.Behavior.AppTheme = AppThemeMode.White;
        settings.Behavior.AppAccentColor = "#123abc";

        var clone = settings.Clone();
        Assert.Equal("#123abc", clone.Behavior.AppAccentColor);
        Assert.Equal(AppThemeMode.White, clone.Behavior.AppTheme);

        clone.Normalize();
        Assert.Equal("#123ABC", clone.Behavior.AppAccentColor);
        Assert.Equal("#123abc", settings.Behavior.AppAccentColor);

        clone.Behavior.AppAccentColor = "nope";
        clone.Normalize();
        Assert.Equal(AppThemeCatalog.DefaultAccent, clone.Behavior.AppAccentColor);
    }

    private static (int R, int G, int B) Rgb(string hex)
    {
        Assert.Matches("^#[0-9A-Fa-f]{6}$", hex);
        return (Convert.ToInt32(hex.Substring(1, 2), 16),
                Convert.ToInt32(hex.Substring(3, 2), 16),
                Convert.ToInt32(hex.Substring(5, 2), 16));
    }
}
