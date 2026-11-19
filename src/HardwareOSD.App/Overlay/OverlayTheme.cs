using System.Windows;
using System.Windows.Media;
using HardwareOSD.Core.Config;
using HardwareOSD.Core.Metrics;

namespace HardwareOSD.App.Overlay;

/// <summary>
/// Immutable, frozen WPF resources derived from user settings. Rebuilt only when settings change,
/// never per frame, so the render loop performs no brush or font construction.
/// </summary>
public sealed class OverlayTheme
{
    public static readonly Color CpuAccent = Color.FromRgb(0x4F, 0xD1, 0xC5);
    public static readonly Color GpuAccent = Color.FromRgb(0x7C, 0xA8, 0xFF);
    public static readonly Color VramAccent = Color.FromRgb(0xC7, 0x9B, 0xFF);
    public static readonly Color RamAccent = Color.FromRgb(0xFF, 0xC3, 0x6B);
    public static readonly Color SystemAccent = Color.FromRgb(0x8F, 0xA3, 0xB4);

    private OverlayTheme(AppSettings settings)
    {
        var profile = settings.Osd?.ActiveProfile;
        Style = profile?.Style ?? OsdStyle.Classic;
        var style = profile is null || Style == OsdStyle.Classic
            ? OsdStyleSettings.FromOverlay(settings.Overlay)
            : profile.StyleSettings.Clone();
        style.Normalize();

        var family = new FontFamily(string.IsNullOrWhiteSpace(style.FontFamily) ? "Segoe UI" : style.FontFamily);
        Family = family;
        LabelTypeface = new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        ValueTypeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        UnitTypeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        ValueSize = style.FontSize;
        LabelSize = Math.Max(8, Math.Round(style.FontSize * style.LabelScale, 1));
        UnitSize = Math.Max(8, Math.Round(style.FontSize * style.UnitScale, 1));
        LineHeight = Math.Round(style.FontSize * 1.32, 1);
        PaddingX = style.PaddingX;
        PaddingY = style.PaddingY;
        RowSpacing = style.RowSpacing;
        ColumnSpacing = style.ColumnSpacing;
        CornerRadius = style.CornerRadius;

        ShowBackground = style.ShowBackground;
        ShowLabels = style.ShowLabels;
        ShowUnits = style.ShowUnits;
        ShowSeparators = style.ShowSeparators;
        ShowSparklines = style.ShowSparklines;
        HighlightWarnings = style.HighlightWarnings;
        Shadow = style.Shadow;
        UpperLabels = style.UseUpperLabels;

        LabelBrush = Freeze(ColorUtil.Parse(style.LabelColor, Color.FromRgb(0x7F, 0xE0, 0xD4)));
        ValueBrush = Freeze(ColorUtil.Parse(style.ValueColor, Color.FromRgb(0xF3, 0xF8, 0xFB)));
        UnitBrush = Freeze(ColorUtil.Parse(style.UnitColor, Color.FromRgb(0x93, 0xA7, 0xB4)));
        NormalBrush = Freeze(ColorUtil.Parse(style.NormalColor, Color.FromRgb(0xF3, 0xF8, 0xFB)));
        WarningBrush = Freeze(ColorUtil.Parse(style.WarningColor, Color.FromRgb(0xFF, 0xB8, 0x4D)));
        CriticalBrush = Freeze(ColorUtil.Parse(style.CriticalColor, Color.FromRgb(0xFF, 0x6B, 0x6B)));
        HeaderBrush = Freeze(ColorUtil.Parse(style.HeaderColor, Color.FromRgb(0x7F, 0xE0, 0xD4)));
        TextBrush = Freeze(ColorUtil.Parse(style.TextColor, Color.FromRgb(0xF3, 0xF8, 0xFB)));

        var background = ColorUtil.Parse(style.BackgroundColor, Color.FromRgb(0x07, 0x0B, 0x10));
        background.A = (byte)Math.Clamp(style.BackgroundOpacity * 255 / 100, 0, 255);
        BackgroundBrush = Freeze(background);

        var border = Colors.White;
        border.A = 26;
        BorderBrush = Freeze(border);

        var shadow = Colors.Black;
        shadow.A = 90;
        ShadowBrush = Freeze(shadow);

        var unavailable = ColorUtil.Parse(style.UnitColor, Color.FromRgb(0x93, 0xA7, 0xB4));
        unavailable.A = 130;
        UnavailableBrush = Freeze(unavailable);

        var separator = Style == OsdStyle.Classic
            ? Colors.White
            : ColorUtil.Parse(style.SeparatorColor, Color.FromRgb(0x46, 0x51, 0x5D));
        separator.A = Style == OsdStyle.Classic ? (byte)26 : (byte)150;
        SeparatorBrush = Freeze(separator);

        Accents = new Dictionary<MetricGroup, Brush>
        {
            [MetricGroup.Cpu] = Freeze(Style == OsdStyle.HighContrast ? Color.FromRgb(0xFF, 0xE4, 0x5C) : CpuAccent),
            [MetricGroup.Gpu] = Freeze(Style == OsdStyle.HighContrast ? Color.FromRgb(0xFF, 0xD4, 0x00) : GpuAccent),
            [MetricGroup.Vram] = Freeze(Style == OsdStyle.HighContrast ? Color.FromRgb(0xD7, 0xDF, 0x4C) : VramAccent),
            [MetricGroup.Ram] = Freeze(Style == OsdStyle.HighContrast ? Color.FromRgb(0xFF, 0xC3, 0x6B) : RamAccent),
            [MetricGroup.System] = Freeze(Style == OsdStyle.HighContrast ? Color.FromRgb(0xFF, 0xE4, 0x5C) : SystemAccent),
        };

        GraphBrush = Freeze(Color.FromArgb(200, CpuAccent.R, CpuAccent.G, CpuAccent.B));
        GraphFill = Freeze(Color.FromArgb(38, CpuAccent.R, CpuAccent.G, CpuAccent.B));
        SeparatorPen = CreatePen(SeparatorBrush, 1);
        DividerPen = CreatePen(SeparatorBrush, 1);

        GraphStrokes = new Dictionary<MetricGroup, Brush>
        {
            [MetricGroup.Cpu] = Freeze(Color.FromArgb(210, CpuAccent.R, CpuAccent.G, CpuAccent.B)),
            [MetricGroup.Gpu] = Freeze(Color.FromArgb(210, GpuAccent.R, GpuAccent.G, GpuAccent.B)),
            [MetricGroup.Vram] = Freeze(Color.FromArgb(210, VramAccent.R, VramAccent.G, VramAccent.B)),
            [MetricGroup.Ram] = Freeze(Color.FromArgb(210, RamAccent.R, RamAccent.G, RamAccent.B)),
            [MetricGroup.System] = Freeze(Color.FromArgb(210, SystemAccent.R, SystemAccent.G, SystemAccent.B)),
        };

        GraphFills = new Dictionary<MetricGroup, Brush>
        {
            [MetricGroup.Cpu] = Freeze(Color.FromArgb(26, CpuAccent.R, CpuAccent.G, CpuAccent.B)),
            [MetricGroup.Gpu] = Freeze(Color.FromArgb(26, GpuAccent.R, GpuAccent.G, GpuAccent.B)),
            [MetricGroup.Vram] = Freeze(Color.FromArgb(26, VramAccent.R, VramAccent.G, VramAccent.B)),
            [MetricGroup.Ram] = Freeze(Color.FromArgb(26, RamAccent.R, RamAccent.G, RamAccent.B)),
            [MetricGroup.System] = Freeze(Color.FromArgb(26, SystemAccent.R, SystemAccent.G, SystemAccent.B)),
        };
    }

    public OsdStyle Style { get; }
    public FontFamily Family { get; }
    public Typeface LabelTypeface { get; }
    public Typeface ValueTypeface { get; }
    public Typeface UnitTypeface { get; }

    public double ValueSize { get; }
    public double LabelSize { get; }
    public double UnitSize { get; }
    public double LineHeight { get; }
    public double PaddingX { get; }
    public double PaddingY { get; }
    public double RowSpacing { get; }
    public double ColumnSpacing { get; }
    public double CornerRadius { get; }

    public bool ShowBackground { get; }
    public bool ShowLabels { get; }
    public bool ShowUnits { get; }
    public bool ShowSeparators { get; }
    public bool ShowSparklines { get; }
    public bool HighlightWarnings { get; }
    public bool Shadow { get; }
    public bool UpperLabels { get; }

    public Brush LabelBrush { get; }
    public Brush ValueBrush { get; }
    public Brush UnitBrush { get; }
    public Brush NormalBrush { get; }
    public Brush WarningBrush { get; }
    public Brush CriticalBrush { get; }
    public Brush HeaderBrush { get; }
    public Brush TextBrush { get; }
    public Brush SeparatorBrush { get; }
    public Brush UnavailableBrush { get; }
    public Brush BackgroundBrush { get; }
    public Brush BorderBrush { get; }
    public Brush ShadowBrush { get; }
    public Brush GraphBrush { get; }
    public Brush GraphFill { get; }
    public Pen SeparatorPen { get; }
    public Pen DividerPen { get; }
    public IReadOnlyDictionary<MetricGroup, Brush> Accents { get; }

    private IReadOnlyDictionary<MetricGroup, Brush> GraphStrokes { get; }
    private IReadOnlyDictionary<MetricGroup, Brush> GraphFills { get; }

    public Brush AccentBrushFor(MetricGroup group)
        => Accents.TryGetValue(group, out var brush) ? brush : ValueBrush;

    public Brush GraphStrokeFor(MetricGroup group)
        => GraphStrokes.TryGetValue(group, out var brush) ? brush : GraphBrush;

    public Brush GraphFillFor(MetricGroup group)
        => GraphFills.TryGetValue(group, out var brush) ? brush : GraphFill;

    public Brush ValueBrushFor(MetricSeverity severity, bool available)
    {
        if (!available)
        {
            return UnavailableBrush;
        }

        if (!HighlightWarnings)
        {
            return ValueBrush;
        }

        return severity switch
        {
            MetricSeverity.Critical => CriticalBrush,
            MetricSeverity.Warning => WarningBrush,
            _ => ValueBrush,
        };
    }

    /// <summary>Role-based colour path for the custom engine and the Adaptive style.</summary>
    public Brush ValueBrushFor(OsdColorRole role, bool available)
    {
        if (!available)
        {
            return UnavailableBrush;
        }

        if (role == OsdColorRole.Critical)
        {
            return CriticalBrush;
        }

        if (role == OsdColorRole.Warning)
        {
            return WarningBrush;
        }

        return Style == OsdStyle.AdaptiveColors ? NormalBrush : ValueBrush;
    }

    public static OverlayTheme Create(AppSettings settings) => new(settings);

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen CreatePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }
}

internal static class ColorUtil
{
    public static Color Parse(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return fallback;
        }

        try
        {
            var text = hex.StartsWith('#') ? hex : "#" + hex;
            if (ColorConverter.ConvertFromString(text) is Color color)
            {
                return color;
            }
        }
        catch
        {
            // fall through to the default
        }

        return fallback;
    }

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}

