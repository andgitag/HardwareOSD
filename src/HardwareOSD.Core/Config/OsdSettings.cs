using System.Globalization;
using HardwareOSD.Core.Metrics;
using HardwareOSD.Core.TileOsd;

namespace HardwareOSD.Core.Config;

public enum OsdStyle
{
    Classic = 0,
    HighContrast = 1,
    BlueLabels = 2,
    AdaptiveColors = 3,
}

/// <summary>Which built-in library a profile belongs to. The split is additive and user visible.</summary>
public enum OsdDesignLibrary
{
    /// <summary>The original HardwareOSD profiles (Classic, Green HUD, ...).</summary>
    HardwareOsd = 0,

    /// <summary>Designs recreated from the CapFrameX 1.9.2 research.</summary>
    CapFrameX = 1,
}

/// <summary>Row list (the original engine) or the absolute tile/canvas engine.</summary>
public enum OsdProfileLayout
{
    Rows = 0,
    Tile = 1,
}

/// <summary>Vendor-dependent group colour used by the three CapFrameX row templates.</summary>
public enum OsdVendorRole
{
    None = 0,
    GpuGroup = 1,
    CpuGroup = 2,
    RamGroup = 3,
    LatencyGroup = 4,
    MetricGroup = 5,
    FrameGroup = 6,
    White = 7,
}

public enum OsdElementKind
{
    Sensor = 0,
    Group = 1,
    GroupHeader = 2,
    CustomText = 3,
    DeviceHeader = 4,
    EmptyLine = 5,
    Divider = 6,
    Graph = 7,
    ProgressBar = 8,
    CustomValue = 9,
    Ring = 10,
}

public enum OsdGroupLayout
{
    /// <summary>All child sensors share one row, separated into columns (the Classic layout).</summary>
    Columns = 0,

    /// <summary>Each child sensor gets its own row (the compact builder layout).</summary>
    Rows = 1,
}

public enum OsdTextAlign
{
    Left = 0,
    Center = 1,
    Right = 2,
}

public enum OsdThresholdDirection
{
    /// <summary>Temperature, load, RAM/VRAM and power: bigger values cross warning then critical.</summary>
    HigherIsWorse = 0,

    /// <summary>FPS: lower values cross warning then critical.</summary>
    LowerIsWorse = 1,
}

public enum OsdColorRole
{
    Normal = 0,
    Warning = 1,
    Critical = 2,
    Unavailable = 3,
}

/// <summary>
/// Style-specific visual settings. Classic is seeded from <see cref="OverlaySettings"/> so the
/// existing controls and configurations keep working; the other styles own an independent copy.
/// </summary>
public sealed class OsdStyleSettings
{
    public string FontFamily { get; set; } = "Segoe UI Variable Text";
    public double FontSize { get; set; } = 15;
    public double LabelScale { get; set; } = 0.82;
    public double UnitScale { get; set; } = 0.78;

    public string LabelColor { get; set; } = "#6FD8CC";
    public string ValueColor { get; set; } = "#F4F7FA";
    public string UnitColor { get; set; } = "#9AA3AC";
    public string NormalColor { get; set; } = "#F4F7FA";
    public string WarningColor { get; set; } = "#FFB020";
    public string CriticalColor { get; set; } = "#FF5C5C";
    public string HeaderColor { get; set; } = "#6FD8CC";
    public string TextColor { get; set; } = "#F4F7FA";
    public string SeparatorColor { get; set; } = "#46515D";
    public string BackgroundColor { get; set; } = "#000000";
    public int BackgroundOpacity { get; set; } = 70;

    public bool ShowBackground { get; set; } = true;
    public bool ShowLabels { get; set; } = true;
    public bool ShowUnits { get; set; } = true;
    public bool ShowSeparators { get; set; }
    public bool ShowSparklines { get; set; }
    public bool HighlightWarnings { get; set; } = true;
    public bool Shadow { get; set; } = true;
    public bool UseUpperLabels { get; set; } = true;

    public double CornerRadius { get; set; } = 7;
    public double PaddingX { get; set; } = 14;
    public double PaddingY { get; set; } = 9;
    public double RowSpacing { get; set; } = 4;
    public double ColumnSpacing { get; set; } = 13;

    public OsdStyleSettings Clone() => (OsdStyleSettings)MemberwiseClone();

    public void Normalize()
    {
        FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? "Segoe UI Variable Text" : FontFamily.Trim();
        FontSize = Math.Clamp(FontSize, 9, 40);
        LabelScale = Math.Clamp(LabelScale, 0.5, 1.2);
        UnitScale = Math.Clamp(UnitScale, 0.5, 1.2);
        LabelColor = AppSettings.SanitizeColor(LabelColor, "#6FD8CC");
        ValueColor = AppSettings.SanitizeColor(ValueColor, "#F4F7FA");
        UnitColor = AppSettings.SanitizeColor(UnitColor, "#9AA3AC");
        NormalColor = AppSettings.SanitizeColor(NormalColor, ValueColor);
        WarningColor = AppSettings.SanitizeColor(WarningColor, "#FFB020");
        CriticalColor = AppSettings.SanitizeColor(CriticalColor, "#FF5C5C");
        HeaderColor = AppSettings.SanitizeColor(HeaderColor, LabelColor);
        TextColor = AppSettings.SanitizeColor(TextColor, ValueColor);
        SeparatorColor = AppSettings.SanitizeColor(SeparatorColor, "#46515D");
        BackgroundColor = AppSettings.SanitizeColor(BackgroundColor, "#000000");
        BackgroundOpacity = Math.Clamp(BackgroundOpacity, 0, 100);
        CornerRadius = Math.Clamp(CornerRadius, 0, 32);
        PaddingX = Math.Clamp(PaddingX, 0, 64);
        PaddingY = Math.Clamp(PaddingY, 0, 64);
        RowSpacing = Math.Clamp(RowSpacing, 0, 32);
        ColumnSpacing = Math.Clamp(ColumnSpacing, 0, 48);
    }

    public static OsdStyleSettings FromOverlay(OverlaySettings overlay) => new()
    {
        FontFamily = overlay.FontFamily,
        FontSize = overlay.FontSize,
        LabelColor = overlay.LabelColor,
        ValueColor = overlay.ValueColor,
        UnitColor = overlay.UnitColor,
        NormalColor = overlay.ValueColor,
        WarningColor = overlay.WarningColor,
        CriticalColor = overlay.CriticalColor,
        HeaderColor = overlay.LabelColor,
        TextColor = overlay.ValueColor,
        SeparatorColor = "#46515D",
        BackgroundColor = overlay.BackgroundColor,
        BackgroundOpacity = overlay.BackgroundOpacity,
        ShowBackground = overlay.ShowBackground,
        ShowLabels = overlay.ShowLabels,
        ShowUnits = overlay.ShowUnits,
        ShowSeparators = overlay.ShowSeparators,
        ShowSparklines = overlay.ShowSparklines,
        HighlightWarnings = overlay.HighlightWarnings,
        Shadow = overlay.Shadow,
        CornerRadius = overlay.CornerRadius,
        PaddingX = overlay.PaddingX,
        PaddingY = overlay.PaddingY,
        RowSpacing = overlay.RowSpacing,
        ColumnSpacing = overlay.ColumnSpacing,
    };

    /// <summary>Applies the Classic style back to the legacy controls after a builder edit.</summary>
    public void ApplyToOverlay(OverlaySettings overlay)
    {
        overlay.FontFamily = FontFamily;
        overlay.FontSize = FontSize;
        overlay.LabelColor = LabelColor;
        overlay.ValueColor = ValueColor;
        overlay.UnitColor = UnitColor;
        overlay.WarningColor = WarningColor;
        overlay.CriticalColor = CriticalColor;
        overlay.BackgroundColor = BackgroundColor;
        overlay.BackgroundOpacity = BackgroundOpacity;
        overlay.ShowBackground = ShowBackground;
        overlay.ShowLabels = ShowLabels;
        overlay.ShowUnits = ShowUnits;
        overlay.ShowSeparators = ShowSeparators;
        overlay.ShowSparklines = ShowSparklines;
        overlay.HighlightWarnings = HighlightWarnings;
        overlay.Shadow = Shadow;
        overlay.CornerRadius = CornerRadius;
        overlay.PaddingX = PaddingX;
        overlay.PaddingY = PaddingY;
        overlay.RowSpacing = RowSpacing;
        overlay.ColumnSpacing = ColumnSpacing;
    }

    public static OsdStyleSettings CreateDefault(OsdStyle style) => style switch
    {
        OsdStyle.HighContrast => new OsdStyleSettings
        {
            FontFamily = "Segoe UI Variable Text",
            FontSize = 15,
            LabelColor = "#FFE45C",
            ValueColor = "#FFFFFF",
            UnitColor = "#D7DF4C",
            NormalColor = "#FFFFFF",
            WarningColor = "#FFD400",
            CriticalColor = "#FF5C5C",
            HeaderColor = "#FFE45C",
            TextColor = "#FFFFFF",
            SeparatorColor = "#6F6A2B",
            BackgroundColor = "#050505",
            BackgroundOpacity = 82,
            ShowSeparators = true,
            HighlightWarnings = true,
            PaddingX = 12,
            PaddingY = 8,
            RowSpacing = 3,
            ColumnSpacing = 12,
        },
        OsdStyle.BlueLabels => new OsdStyleSettings
        {
            FontFamily = "Segoe UI Variable Text",
            FontSize = 15,
            LabelColor = "#5CA9FF",
            ValueColor = "#EAF4FF",
            UnitColor = "#8FB7E8",
            NormalColor = "#EAF4FF",
            WarningColor = "#FFB020",
            CriticalColor = "#FF5C5C",
            HeaderColor = "#7BBDFF",
            TextColor = "#DCEBFA",
            SeparatorColor = "#274A70",
            BackgroundColor = "#05080D",
            BackgroundOpacity = 78,
            ShowSeparators = true,
            HighlightWarnings = true,
            PaddingX = 12,
            PaddingY = 8,
            RowSpacing = 3,
            ColumnSpacing = 12,
        },
        OsdStyle.AdaptiveColors => new OsdStyleSettings
        {
            FontFamily = "Segoe UI Variable Text",
            FontSize = 15,
            LabelColor = "#FFFFFF",
            ValueColor = "#62E06F",
            UnitColor = "#B8C4CE",
            NormalColor = "#62E06F",
            WarningColor = "#FFA53D",
            CriticalColor = "#FF5151",
            HeaderColor = "#FFFFFF",
            TextColor = "#FFFFFF",
            SeparatorColor = "#39434C",
            BackgroundColor = "#050505",
            BackgroundOpacity = 78,
            ShowSeparators = true,
            HighlightWarnings = true,
            PaddingX = 12,
            PaddingY = 8,
            RowSpacing = 3,
            ColumnSpacing = 12,
        },
        _ => new OsdStyleSettings(),
    };

    /// <summary>Seeds the Classic style from the legacy overlay controls.</summary>
    public static OsdStyleSettings CreateClassic(OverlaySettings overlay)
        => FromOverlay(overlay);
}

/// <summary>One independently configurable OSD element. Groups contain child elements.</summary>
public sealed class OsdElement
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public OsdElementKind Kind { get; set; }
    public string Label { get; set; } = string.Empty;
    public string SensorId { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public bool Visible { get; set; } = true;
    public List<OsdElement> Children { get; set; } = new();
    public OsdGroupLayout GroupLayout { get; set; } = OsdGroupLayout.Rows;
    public int Indent { get; set; }
    public double FontSize { get; set; }
    public string LabelColor { get; set; } = string.Empty;
    public string ValueColor { get; set; } = string.Empty;
    public string UnitColor { get; set; } = string.Empty;
    public bool UseStyleColors { get; set; } = true;
    public bool ShowLabel { get; set; } = true;
    public bool ShowValue { get; set; } = true;
    public bool ShowGraph { get; set; }
    public bool ShowProgress { get; set; }
    public bool ShowStatistics { get; set; }
    public string GraphColor { get; set; } = string.Empty;
    public double GraphThickness { get; set; } = 1.3;
    public int GraphHistoryPoints { get; set; } = 120;
    public double? GraphScaleMin { get; set; }
    public double? GraphScaleMax { get; set; }
    public string Unit { get; set; } = string.Empty;
    public string NumberFormat { get; set; } = string.Empty;
    public int Decimals { get; set; } = -1;
    public string Prefix { get; set; } = string.Empty;
    public string Suffix { get; set; } = string.Empty;
    public double? Width { get; set; }
    public double? Height { get; set; }
    public double? ProgressMin { get; set; }
    public double? ProgressMax { get; set; }
    public double? WarningThreshold { get; set; }
    public double? CriticalThreshold { get; set; }
    public OsdThresholdDirection Direction { get; set; } = OsdThresholdDirection.HigherIsWorse;
    public bool AutoColor { get; set; }
    public OsdTextAlign Align { get; set; } = OsdTextAlign.Left;
    public double MarginTop { get; set; }
    public double MarginBottom { get; set; }

    /// <summary>Group colour resolved at render time from the installed GPU/CPU vendor.</summary>
    public OsdVendorRole VendorRole { get; set; }

    public bool IsSensorLike => Kind is OsdElementKind.Sensor or OsdElementKind.Graph
        or OsdElementKind.ProgressBar or OsdElementKind.CustomValue or OsdElementKind.Ring;

    public OsdElement Clone() => new()
    {
        Id = Id,
        Kind = Kind,
        Label = Label,
        SensorId = SensorId,
        Text = Text,
        Visible = Visible,
        Children = Children.Select(child => child.Clone()).ToList(),
        GroupLayout = GroupLayout,
        Indent = Indent,
        FontSize = FontSize,
        LabelColor = LabelColor,
        ValueColor = ValueColor,
        UnitColor = UnitColor,
        UseStyleColors = UseStyleColors,
        ShowLabel = ShowLabel,
        ShowValue = ShowValue,
        ShowGraph = ShowGraph,
        ShowProgress = ShowProgress,
        ShowStatistics = ShowStatistics,
        GraphColor = GraphColor,
        GraphThickness = GraphThickness,
        GraphHistoryPoints = GraphHistoryPoints,
        GraphScaleMin = GraphScaleMin,
        GraphScaleMax = GraphScaleMax,
        Unit = Unit,
        NumberFormat = NumberFormat,
        Decimals = Decimals,
        Prefix = Prefix,
        Suffix = Suffix,
        Width = Width,
        Height = Height,
        ProgressMin = ProgressMin,
        ProgressMax = ProgressMax,
        WarningThreshold = WarningThreshold,
        CriticalThreshold = CriticalThreshold,
        Direction = Direction,
        AutoColor = AutoColor,
        Align = Align,
        MarginTop = MarginTop,
        MarginBottom = MarginBottom,
        VendorRole = VendorRole,
    };

    public void Normalize()
    {
        Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id.Trim();
        Label = (Label ?? string.Empty).Trim();
        SensorId = (SensorId ?? string.Empty).Trim();
        Text = (Text ?? string.Empty).Trim();
        Children ??= new List<OsdElement>();
        if (!Enum.IsDefined(Kind))
        {
            Kind = OsdElementKind.CustomText;
        }

        if (Kind != OsdElementKind.Group)
        {
            Children.Clear();
        }
        else
        {
            foreach (var child in Children)
            {
                child.Normalize();
            }
        }

        if (!Enum.IsDefined(GroupLayout))
        {
            GroupLayout = OsdGroupLayout.Rows;
        }

        if (!Enum.IsDefined(Direction))
        {
            Direction = OsdThresholdDirection.HigherIsWorse;
        }

        if (!Enum.IsDefined(Align))
        {
            Align = OsdTextAlign.Left;
        }

        if (!Enum.IsDefined(VendorRole))
        {
            VendorRole = OsdVendorRole.None;
        }

        Indent = Math.Clamp(Indent, 0, 8);
        FontSize = FontSize <= 0 ? 0 : Math.Clamp(FontSize, 8, 72);
        Width = Width is null ? null : Math.Clamp(Width.Value, 0, 2000);
        Height = Height is null ? null : Math.Clamp(Height.Value, 0, 400);
        Decimals = Math.Clamp(Decimals, -1, 6);
        GraphColor = NormalizeOptionalColor(GraphColor);
        GraphThickness = Math.Clamp(GraphThickness, 0.5, 6);
        GraphHistoryPoints = Math.Clamp(GraphHistoryPoints, 8, 4096);
        GraphScaleMin = NormalizeThreshold(GraphScaleMin);
        GraphScaleMax = NormalizeThreshold(GraphScaleMax);
        if (GraphScaleMin is not null && GraphScaleMax is not null && GraphScaleMax <= GraphScaleMin)
        {
            GraphScaleMax = GraphScaleMin + 1;
        }

        MarginTop = Math.Clamp(MarginTop, 0, 64);
        MarginBottom = Math.Clamp(MarginBottom, 0, 64);
        LabelColor = NormalizeOptionalColor(LabelColor);
        ValueColor = NormalizeOptionalColor(ValueColor);
        UnitColor = NormalizeOptionalColor(UnitColor);
        WarningThreshold = NormalizeThreshold(WarningThreshold);
        CriticalThreshold = NormalizeThreshold(CriticalThreshold);
        ProgressMin = NormalizeThreshold(ProgressMin);
        ProgressMax = NormalizeThreshold(ProgressMax);
        if (ProgressMin is not null && ProgressMax is not null && ProgressMax <= ProgressMin)
        {
            ProgressMax = ProgressMin + 1;
        }
    }

    private static string NormalizeOptionalColor(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : AppSettings.SanitizeColor(value, string.Empty);

    private static double? NormalizeThreshold(double? value)
        => value is { } number && double.IsFinite(number) ? number : null;

    public string DisplayLabel()
        => string.IsNullOrWhiteSpace(Label) ? Text : Label;

    public string ExampleValue()
    {
        if (Kind == OsdElementKind.CustomText || Kind == OsdElementKind.GroupHeader ||
            Kind == OsdElementKind.DeviceHeader || Kind == OsdElementKind.EmptyLine ||
            Kind == OsdElementKind.Divider)
        {
            return Text;
        }

        var definition = MetricCatalog.Find(SensorId) ?? FindDefinition(SensorId);
        return definition?.SampleValue ?? "888";
    }

    private static MetricDefinition? FindDefinition(string sensorId)
        => MetricCatalog.All.FirstOrDefault(d => string.Equals(MetricCatalog.SensorIdFor(d), sensorId,
            StringComparison.Ordinal));
}

/// <summary>
/// One user edit over a saved tile widget. Paths are tile indices joined by '/', matching the
/// CapFrameX editor paths. Only changed fields are stored; the design catalog stays immutable.
/// </summary>
public sealed class TileNodeOverride
{
    public string? Key { get; set; }
    public string? SecondaryKey { get; set; }
    public string? Label { get; set; }
    public string? Unit { get; set; }
    public bool? Visible { get; set; }
    public int? Digits { get; set; }
    public double? ValueScale { get; set; }
    public string? ValueColor { get; set; }
    public string? LabelColor { get; set; }
    public string? ChartColor { get; set; }
    public double? ChartThickness { get; set; }
    public double? BarMinimum { get; set; }
    public double? BarMaximum { get; set; }
    public double? ValueSize { get; set; }
    public int? Order { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty =>
        Key is null && SecondaryKey is null && Label is null && Unit is null && Visible is null &&
        Digits is null && ValueScale is null && ValueColor is null && LabelColor is null &&
        ChartColor is null && ChartThickness is null && BarMinimum is null && BarMaximum is null &&
        ValueSize is null && Order is null;

    public TileNodeOverride Clone() => (TileNodeOverride)MemberwiseClone();

    public void Normalize()
    {
        Key = Clean(Key);
        SecondaryKey = Clean(SecondaryKey);
        Label = Clean(Label);
        Unit = Clean(Unit);
        Digits = Digits is null ? null : Math.Clamp(Digits.Value, 0, 6);
        ValueScale = ValueScale is { } scale && double.IsFinite(scale) && scale > 0
            ? Math.Clamp(scale, 0.000001, 1_000_000)
            : null;
        ValueColor = CleanColor(ValueColor);
        LabelColor = CleanColor(LabelColor);
        ChartColor = CleanColor(ChartColor);
        ChartThickness = ChartThickness is { } thickness && double.IsFinite(thickness)
            ? Math.Clamp(thickness, 0.5, 8)
            : null;
        ValueSize = ValueSize is { } size && double.IsFinite(size) ? Math.Clamp(size, 6, 96) : null;
        BarMinimum = Finite(BarMinimum);
        BarMaximum = Finite(BarMaximum);
        if (BarMinimum is not null && BarMaximum is not null && BarMaximum <= BarMinimum)
        {
            BarMaximum = BarMinimum + 1;
        }

        Order = Order is null ? null : Math.Clamp(Order.Value, 0, 512);
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? CleanColor(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : AppSettings.SanitizeColor(value, string.Empty) is { Length: > 0 } clean
            ? clean
            : null;

    private static double? Finite(double? value)
        => value is { } number && double.IsFinite(number) ? number : null;
}

/// <summary>All user edits for one tile profile (per-node overrides and whole-canvas scale).</summary>
public sealed class TileOverrideSettings
{
    public double Scale { get; set; } = 1;
    public double Opacity { get; set; } = 1;
    public Dictionary<string, TileNodeOverride> Nodes { get; set; } = new(StringComparer.Ordinal);

    public TileOverrideSettings Clone() => new()
    {
        Scale = Scale,
        Opacity = Opacity,
        Nodes = Nodes.ToDictionary(pair => pair.Key,
            pair => pair.Value.Clone(), StringComparer.Ordinal),
    };

    public void Normalize()
    {
        Scale = double.IsFinite(Scale) ? Math.Clamp(Scale, 0.5, 2) : 1;
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, 0, 1) : 1;
        Nodes ??= new Dictionary<string, TileNodeOverride>(StringComparer.Ordinal);
        foreach (var key in Nodes.Keys.ToList())
        {
            var value = Nodes[key];
            if (value is null)
            {
                Nodes.Remove(key);
                continue;
            }

            value.Normalize();
            if (value.IsEmpty)
            {
                Nodes.Remove(key);
            }
        }
    }

    public TileNodeOverride? For(string path)
        => Nodes.TryGetValue(path, out var value) ? value : null;

    public TileNodeOverride Ensure(string path)
    {
        if (!Nodes.TryGetValue(path, out var value))
        {
            value = new TileNodeOverride();
            Nodes[path] = value;
        }

        return value;
    }
}

public sealed class OsdProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "OSD Profile";
    public OsdStyle Style { get; set; } = OsdStyle.Classic;
    public bool FollowMetricPreferences { get; set; }

    /// <summary>
    /// Top-level layout. Rows keeps the familiar stacked sections; Columns flattens every visible
    /// metric into one horizontal line (the Minimal Rings one-row request). Group layout still
    /// controls nested sections.
    /// </summary>
    public OsdGroupLayout RootLayout { get; set; } = OsdGroupLayout.Rows;

    public List<OsdElement> Elements { get; set; } = new();
    public OsdStyleSettings StyleSettings { get; set; } = new();

    /// <summary>Original HardwareOSD library or the additive CapFrameX library.</summary>
    public OsdDesignLibrary Library { get; set; } = OsdDesignLibrary.HardwareOsd;

    /// <summary>Rows (element list) or Tile (saved canvas document).</summary>
    public OsdProfileLayout Layout { get; set; } = OsdProfileLayout.Rows;

    /// <summary>Catalog id for a tile profile. Empty for row profiles.</summary>
    public string DesignId { get; set; } = string.Empty;

    /// <summary>
    /// The profile's own editable design (v1 JSON), written by the OSD builder. When empty the
    /// immutable catalog document for <see cref="DesignId"/> is used instead, so profiles created
    /// before 0.23.0 keep rendering exactly as they did.
    /// </summary>
    public string? TileDesignJson { get; set; }

    public TileOverrideSettings Tile { get; set; } = new();

    private TileDesignDocument? _cachedDesign;
    private string? _cachedJson;
    private string? _cachedDesignId;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsTile => Layout == OsdProfileLayout.Tile;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasCustomDesign => !string.IsNullOrWhiteSpace(TileDesignJson);

    [System.Text.Json.Serialization.JsonIgnore]
    public TileDesignDocument? TileDesign
    {
        get
        {
            if (!IsTile)
            {
                return null;
            }

            var json = TileDesignJson;
            if (!string.IsNullOrWhiteSpace(json))
            {
                if (_cachedDesign is not null &&
                    string.Equals(_cachedJson, json, StringComparison.Ordinal))
                {
                    return _cachedDesign;
                }

                var parsed = TileDesignDocument.Parse(json, out _);
                if (parsed is not null)
                {
                    _cachedDesign = parsed;
                    _cachedJson = json;
                    _cachedDesignId = DesignId;
                    return parsed;
                }

                // A corrupt document must not blank the overlay: fall through to the catalog copy.
            }

            if (_cachedDesign is not null && _cachedJson is null &&
                string.Equals(_cachedDesignId, DesignId, StringComparison.Ordinal))
            {
                return _cachedDesign;
            }

            var catalog = TileDesignCatalog.Find(DesignId);
            if (catalog is not null)
            {
                _cachedDesign = catalog;
                _cachedJson = null;
                _cachedDesignId = DesignId;
            }

            return catalog;
        }
    }

    /// <summary>Stores an edited document on the profile and drops legacy per-node overrides.</summary>
    public void SetTileDesign(TileDesignDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.NormalizeEditorSettings();
        TileDesignJson = document.ToJson();
        Tile = new TileOverrideSettings();
        RotateStyleSettingsFrom(document);
        _cachedDesign = null;
        _cachedJson = null;
    }

    /// <summary>Keeps the profile card tokens in step with the design the builder saved.</summary>
    private void RotateStyleSettingsFrom(TileDesignDocument document)
    {
        StyleSettings ??= new OsdStyleSettings();
        var theme = document.Theme;
        StyleSettings.FontFamily = theme.FontFamily;
        StyleSettings.FontSize = theme.FontSize;
        StyleSettings.LabelColor = TileColor.Parse(theme.Accent, TileColor.White).ToHex();
        StyleSettings.ValueColor = TileColor.Parse(theme.Text, TileColor.White).ToHex();
        StyleSettings.UnitColor = TileColor.Parse(theme.Subtext, TileColor.White).ToHex();
        StyleSettings.NormalColor = StyleSettings.ValueColor;
        StyleSettings.HeaderColor = StyleSettings.LabelColor;
        StyleSettings.TextColor = StyleSettings.ValueColor;
        StyleSettings.BackgroundColor = TileColor.Parse(theme.Panel, TileColor.Transparent).ToHex();
    }

    public OsdProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        Style = Style,
        FollowMetricPreferences = FollowMetricPreferences,
        RootLayout = RootLayout,
        Elements = Elements.Select(e => e.Clone()).ToList(),
        StyleSettings = StyleSettings.Clone(),
        Library = Library,
        Layout = Layout,
        DesignId = DesignId,
        TileDesignJson = TileDesignJson,
        Tile = Tile.Clone(),
    };

    public void Normalize()
    {
        Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id.Trim();
        Name = string.IsNullOrWhiteSpace(Name) ? "OSD Profile" : Name.Trim();
        if (!Enum.IsDefined(Style))
        {
            Style = OsdStyle.Classic;
        }

        if (!Enum.IsDefined(Library))
        {
            Library = OsdDesignLibrary.HardwareOsd;
        }

        if (!Enum.IsDefined(Layout))
        {
            Layout = OsdProfileLayout.Rows;
        }

        if (!Enum.IsDefined(RootLayout))
        {
            RootLayout = OsdGroupLayout.Rows;
        }

        DesignId = string.IsNullOrWhiteSpace(DesignId) ? string.Empty : DesignId.Trim();
        TileDesignJson = string.IsNullOrWhiteSpace(TileDesignJson) ? null : TileDesignJson.Trim();
        Tile ??= new TileOverrideSettings();
        Tile.Normalize();
        if (Layout == OsdProfileLayout.Tile && DesignId.Length == 0 &&
            string.IsNullOrWhiteSpace(TileDesignJson))
        {
            // A tile profile with neither a catalog id nor its own document cannot render; fall
            // back to its element tree so the rest of the configuration stays usable.
            Layout = OsdProfileLayout.Rows;
        }

        Elements ??= new List<OsdElement>();
        StyleSettings ??= OsdStyleSettings.CreateDefault(Style);
        StyleSettings.Normalize();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in Flatten(Elements))
        {
            element.Normalize();
            if (!seen.Add(element.Id))
            {
                element.Id = Guid.NewGuid().ToString("N");
                seen.Add(element.Id);
            }
        }
    }

    public static IEnumerable<OsdElement> Flatten(IEnumerable<OsdElement> elements)
    {
        foreach (var element in elements)
        {
            yield return element;
            if (element.Kind == OsdElementKind.Group)
            {
                foreach (var child in Flatten(element.Children))
                {
                    yield return child;
                }
            }
        }
    }

    public static List<OsdElement> BuildDefaultElements(
        IEnumerable<MetricPreference> preferences,
        bool fpsEnabled,
        OsdStyle style)
    {
        var elements = new List<OsdElement>();
        var prefersRows = style != OsdStyle.Classic;
        foreach (var group in new[]
                 {
                     MetricGroup.Cpu, MetricGroup.Gpu, MetricGroup.Vram, MetricGroup.Ram,
                     MetricGroup.System, MetricGroup.Frame,
                 })
        {
            if (group == MetricGroup.Frame && !fpsEnabled)
            {
                continue;
            }

            var children = preferences
                .Where(p => p.Visible && MetricCatalog.Find(p.Id) is { } d && d.Group == group)
                .Select(p => MetricCatalog.Find(p.Id)!)
                .Select(definition => new OsdElement
                {
                    Id = $"osd-{style.ToString().ToLowerInvariant()}-{definition.Id.Replace('.', '-')}",
                    Kind = OsdElementKind.Sensor,
                    Label = definition.DisplayName,
                    SensorId = definition.Id,
                    AutoColor = style == OsdStyle.AdaptiveColors,
                    Direction = definition.Id.StartsWith("frame.", StringComparison.Ordinal)
                        ? OsdThresholdDirection.LowerIsWorse
                        : OsdThresholdDirection.HigherIsWorse,
                    WarningThreshold = style == OsdStyle.AdaptiveColors ? AdaptiveWarning(definition) : null,
                    CriticalThreshold = style == OsdStyle.AdaptiveColors ? AdaptiveCritical(definition) : null,
                })
                .ToList();
            if (children.Count == 0)
            {
                continue;
            }

            elements.Add(new OsdElement
            {
                Id = $"osd-{style.ToString().ToLowerInvariant()}-group-{group.ToString().ToLowerInvariant()}",
                Kind = OsdElementKind.Group,
                Label = MetricCatalog.GroupTitle(group),
                GroupLayout = prefersRows ? OsdGroupLayout.Rows : OsdGroupLayout.Columns,
                Children = children,
            });
        }

        return elements;
    }

    private static double? AdaptiveWarning(MetricDefinition definition)
    {
        if (definition.Id.StartsWith("frame.fps", StringComparison.Ordinal)) return 60;
        if (definition.Unit == "°C") return 60;
        if (definition.Unit == "%") return 70;
        return null;
    }

    private static double? AdaptiveCritical(MetricDefinition definition)
    {
        if (definition.Id.StartsWith("frame.fps", StringComparison.Ordinal)) return 30;
        if (definition.Unit == "°C") return 85;
        if (definition.Unit == "%") return 90;
        return null;
    }
}

/// <summary>Versioned collection of OSD profiles and the active profile selection.</summary>
public sealed class OsdSettings
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Version of the additive CapFrameX starter library already seeded. Bumping this in a future
    /// release adds only new design ids; deleted profiles are never resurrected by a normal load.
    /// </summary>
    public const int CurrentCapFrameXLibraryVersion = 1;

    /// <summary>
    /// Version of the row-to-tile migration for the five classic profiles. Version 1 converts
    /// Classic, Green HUD, Split HUD, Info HUD and Minimal Rings to editable tile documents.
    /// </summary>
    public const int CurrentTileMigrationVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public int CapFrameXLibraryVersion { get; set; }
    public int TileMigrationVersion { get; set; }
    public string ActiveProfileId { get; set; } = string.Empty;
    public List<OsdProfile> Profiles { get; set; } = new();

    public OsdProfile? ActiveProfile
        => Profiles.FirstOrDefault(p => string.Equals(p.Id, ActiveProfileId, StringComparison.Ordinal))
           ?? Profiles.FirstOrDefault();

    public OsdSettings Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        CapFrameXLibraryVersion = CapFrameXLibraryVersion,
        TileMigrationVersion = TileMigrationVersion,
        ActiveProfileId = ActiveProfileId,
        Profiles = Profiles.Select(p => p.Clone()).ToList(),
    };

    public void Normalize(OverlaySettings legacy, IReadOnlyList<MetricPreference> preferences, bool fpsEnabled)
    {
        SchemaVersion = Math.Max(SchemaVersion, CurrentSchemaVersion);
        Profiles ??= new List<OsdProfile>();
        Profiles.RemoveAll(p => p is null);
        if (Profiles.Count == 0)
        {
            Profiles = CreateDefaults(legacy, preferences, fpsEnabled);
        }
        else
        {
            foreach (var reference in CreateReferenceProfiles(fpsEnabled))
            {
                if (Profiles.All(profile => !string.Equals(profile.Id, reference.Id, StringComparison.Ordinal)))
                {
                    Profiles.Add(reference);
                }
            }
        }

        SeedCapFrameXProfiles();

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in Profiles)
        {
            profile.Normalize();
            RenameLegacyProfileName(profile);
            if (!ids.Add(profile.Id))
            {
                profile.Id = Guid.NewGuid().ToString("N");
                ids.Add(profile.Id);
            }
        }

        if (!Profiles.Any(p => string.Equals(p.Id, ActiveProfileId, StringComparison.Ordinal)))
        {
            ActiveProfileId = Profiles[0].Id;
        }

        // The Classic profile follows the old Metrics page until the builder takes ownership of
        // its structure. That keeps existing configurations and tests visually identical.
        var active = ActiveProfile;
        if (active is { Style: OsdStyle.Classic, FollowMetricPreferences: true })
        {
            active.StyleSettings = OsdStyleSettings.CreateClassic(legacy);
            active.StyleSettings.Normalize();
            active.Elements = OsdProfile.BuildDefaultElements(preferences, fpsEnabled, OsdStyle.Classic);
        }

        // 0.26.0: the five original profiles are fully editable tile documents. This runs after
        // the legacy Classic matrix sync so the converted design includes the user's current
        // metric selection, and it is idempotent for profiles the builder already saved.
        RowProfileTileMigrator.Migrate(this);
    }

    public static List<OsdProfile> CreateDefaults(
        OverlaySettings legacy,
        IReadOnlyList<MetricPreference> preferences,
        bool fpsEnabled)
    {
        var classic = new OsdProfile
        {
            Id = "classic",
            Name = "Classic",
            Style = OsdStyle.Classic,
            FollowMetricPreferences = true,
            Elements = OsdProfile.BuildDefaultElements(preferences, fpsEnabled, OsdStyle.Classic),
            StyleSettings = OsdStyleSettings.CreateClassic(legacy),
        };

        var profiles = new List<OsdProfile> { classic };
        profiles.AddRange(CreateReferenceProfiles(fpsEnabled));
        return profiles;
    }

    /// <summary>Renames profiles saved by 0.19.0 without changing their ids or user data.</summary>
    /// <summary>
    /// Adds the additive CapFrameX library once. A future catalog version may add new ids, but a
    /// design the user deleted is not recreated because the seeded version is already current.
    /// </summary>
    private void SeedCapFrameXProfiles()
    {
        if (CapFrameXLibraryVersion >= CurrentCapFrameXLibraryVersion)
        {
            return;
        }

        var existing = new HashSet<string>(Profiles.Select(profile => profile.Id), StringComparer.Ordinal);
        foreach (var profile in CreateCapFrameXProfiles())
        {
            if (existing.Add(profile.Id))
            {
                Profiles.Add(profile);
            }
        }

        CapFrameXLibraryVersion = CurrentCapFrameXLibraryVersion;
    }

    /// <summary>
    /// Creates the 45 saved tile designs and the three row templates. This is called on first
    /// migration only, so the original five profiles are never touched.
    /// </summary>
    public static List<OsdProfile> CreateCapFrameXProfiles()
    {
        var profiles = new List<OsdProfile>(48);
        foreach (var entry in TileDesignCatalog.Entries)
        {
            if (TileDesignCatalog.IsRetired(entry.Id))
            {
                continue;
            }

            profiles.Add(new OsdProfile
            {
                Id = "cfx-tile-" + entry.Id,
                Name = entry.Name,
                Library = OsdDesignLibrary.CapFrameX,
                Layout = OsdProfileLayout.Tile,
                DesignId = entry.Id,
                Style = OsdStyle.Classic,
                FollowMetricPreferences = false,
                Elements = new List<OsdElement>(),
                StyleSettings = TileStyleSettings(entry),
                Tile = new TileOverrideSettings(),
            });
        }

        profiles.AddRange(CapFrameXRowTemplates.CreateProfiles());
        return profiles;
    }

    private static OsdStyleSettings TileStyleSettings(TileCatalogEntry entry)
        => TileStyleSettingsFor(entry);

    /// <summary>Colour tokens for the library card and the profile row list.</summary>
    public static OsdStyleSettings TileStyleSettingsFor(TileCatalogEntry entry)
    {
        var theme = TileColor.Parse(entry.Panel, TileColor.Transparent);
        var accent = TileColor.Parse(entry.Accent, TileColor.White);
        var text = TileColor.Parse(entry.Text, TileColor.White);
        return new OsdStyleSettings
        {
            FontFamily = entry.FontFamily,
            FontSize = entry.FontSize,
            LabelColor = accent.ToHex(),
            ValueColor = text.ToHex(),
            UnitColor = text.WithAlpha(200).ToHex(),
            NormalColor = text.ToHex(),
            HeaderColor = accent.ToHex(),
            TextColor = text.ToHex(),
            BackgroundColor = theme.ToHex(),
            BackgroundOpacity = theme.A <= 0 ? 0 : (int)Math.Round(theme.A * 100.0 / 255),
            ShowBackground = theme.A > 0,
            ShowLabels = true,
            ShowUnits = true,
            ShowSeparators = false,
            ShowSparklines = false,
            CornerRadius = entry.Width > 0 && entry.Height > 0 ? 6 : 0,
        };
    }

    private static void RenameLegacyProfileName(OsdProfile profile)
    {
        if (profile.Library != OsdDesignLibrary.HardwareOsd)
        {
            return;
        }

        var replacement = profile.Id switch
        {
            "cfx-green" => "Green HUD",
            "cfx-split" => "Split HUD",
            "cfx-info" => "Info HUD",
            _ => null,
        };
        if (replacement is not null && profile.Name.Contains("CapFrameX", StringComparison.OrdinalIgnoreCase))
        {
            profile.Name = replacement;
        }
        else if (profile.Name.Contains("CapFrameX", StringComparison.OrdinalIgnoreCase))
        {
            profile.Name = profile.Name.Replace("CapFrameX", "OSD", StringComparison.OrdinalIgnoreCase)
                .Replace("  ", " ").Trim();
        }
    }

    private static IEnumerable<OsdProfile> CreateReferenceProfiles(bool fpsEnabled)
    {
        yield return ReferenceProfile("cfx-green", "Green HUD", OsdStyle.HighContrast,
            new OsdStyleSettings
            {
                FontFamily = "Cascadia Mono",
                FontSize = 14,
                LabelColor = "#9CFF2E",
                ValueColor = "#FFB020",
                UnitColor = "#D8E0D0",
                NormalColor = "#FFFFFF",
                HeaderColor = "#9CFF2E",
                TextColor = "#FFFFFF",
                BackgroundColor = "#000000",
                BackgroundOpacity = 84,
                ShowBackground = true,
                ShowLabels = true,
                ShowUnits = true,
                ShowSeparators = false,
                ShowSparklines = true,
                PaddingX = 8,
                PaddingY = 7,
                RowSpacing = 2,
                ColumnSpacing = 10,
                CornerRadius = 0,
            },
            BuildCfxElements("green", fpsEnabled));

        yield return ReferenceProfile("cfx-split", "Split HUD", OsdStyle.BlueLabels,
            new OsdStyleSettings
            {
                FontFamily = "Cascadia Mono",
                FontSize = 14,
                LabelColor = "#9CFF2E",
                ValueColor = "#FFB020",
                UnitColor = "#BCE8FF",
                NormalColor = "#FFFFFF",
                HeaderColor = "#59B8FF",
                TextColor = "#FFFFFF",
                BackgroundColor = "#000000",
                BackgroundOpacity = 82,
                ShowBackground = true,
                ShowSeparators = false,
                ShowSparklines = true,
                PaddingX = 8,
                PaddingY = 7,
                RowSpacing = 2,
                ColumnSpacing = 10,
                CornerRadius = 0,
            },
            BuildCfxElements("split", fpsEnabled));

        yield return ReferenceProfile("cfx-info", "Info HUD", OsdStyle.AdaptiveColors,
            new OsdStyleSettings
            {
                FontFamily = "Cascadia Mono",
                FontSize = 14,
                LabelColor = "#B8FF38",
                ValueColor = "#FFFFFF",
                UnitColor = "#D0D0D0",
                NormalColor = "#FFFFFF",
                HeaderColor = "#59B8FF",
                TextColor = "#FFFFFF",
                BackgroundColor = "#000000",
                BackgroundOpacity = 82,
                ShowBackground = true,
                ShowSeparators = false,
                ShowSparklines = true,
                PaddingX = 8,
                PaddingY = 7,
                RowSpacing = 2,
                ColumnSpacing = 10,
                CornerRadius = 0,
            },
            BuildCfxElements("info", fpsEnabled));

        yield return ReferenceProfile("minimal-rings", "Minimal Rings", OsdStyle.AdaptiveColors,
            new OsdStyleSettings
            {
                FontFamily = "Segoe UI Variable Text",
                FontSize = 15,
                LabelColor = "#FFFFFF",
                ValueColor = "#FFFFFF",
                UnitColor = "#B8B8B8",
                NormalColor = "#FFFFFF",
                HeaderColor = "#FFFFFF",
                TextColor = "#FFFFFF",
                BackgroundColor = "#1B1B1B",
                BackgroundOpacity = 84,
                ShowBackground = true,
                ShowLabels = true,
                ShowUnits = true,
                ShowSeparators = false,
                ShowSparklines = false,
                PaddingX = 12,
                PaddingY = 9,
                RowSpacing = 3,
                ColumnSpacing = 10,
                CornerRadius = 16,
            },
            BuildCfxElements("rings", fpsEnabled));
    }

    private static OsdProfile ReferenceProfile(string id, string name, OsdStyle style,
        OsdStyleSettings styleSettings, List<OsdElement> elements)
    {
        if (style == OsdStyle.AdaptiveColors)
        {
            foreach (var element in OsdProfile.Flatten(elements))
            {
                if (element.IsSensorLike)
                {
                    element.AutoColor = true;
                    var definition = MetricCatalog.Find(element.SensorId);
                    var unit = definition?.Unit ?? element.Unit;
                    if (element.SensorId.StartsWith("frame.fps", StringComparison.Ordinal))
                    {
                        element.WarningThreshold = 60;
                        element.CriticalThreshold = 30;
                        element.Direction = OsdThresholdDirection.LowerIsWorse;
                    }
                    else if (unit == "°C")
                    {
                        element.WarningThreshold = 60;
                        element.CriticalThreshold = 85;
                    }
                    else if (unit == "%")
                    {
                        element.WarningThreshold = 70;
                        element.CriticalThreshold = 90;
                    }
                }
            }
        }

        return new OsdProfile
        {
            Id = id,
            Name = name,
            Style = style,
            FollowMetricPreferences = false,
            Elements = elements,
            StyleSettings = styleSettings,
        };
    }

    private static List<OsdElement> BuildCfxElements(string kind, bool fpsEnabled)
    {
        var elements = new List<OsdElement>();
        if (kind == "rings")
        {
            elements.Add(new OsdElement
            {
                Id = "rings-fps",
                Kind = OsdElementKind.Group,
                Label = "FPS",
                GroupLayout = OsdGroupLayout.Columns,
                Children =
                {
                    SensorElement("frame.fps", "FPS", graph: false),
                    SensorElement("frame.time", "frame", graph: false, unit: "ms"),
                },
            });
            elements.Add(new OsdElement
            {
                Id = "rings-cpu",
                Kind = OsdElementKind.Group,
                Label = "CPU",
                GroupLayout = OsdGroupLayout.Rows,
                Children =
                {
                    SensorElement("cpu.usage", "CPU", progress: true),
                    SensorElement("cpu.temp", "temp"),
                    SensorElement("cpu.power", "power"),
                },
            });
            elements.Add(new OsdElement
            {
                Id = "rings-gpu",
                Kind = OsdElementKind.Group,
                Label = "GPU",
                GroupLayout = OsdGroupLayout.Rows,
                Children =
                {
                    SensorElement("gpu.usage", "GPU", progress: true),
                    SensorElement("gpu.temp", "temp"),
                    SensorElement("gpu.power", "power"),
                },
            });
            elements.Add(new OsdElement
            {
                Id = "rings-ram",
                Kind = OsdElementKind.Group,
                Label = "RAM",
                GroupLayout = OsdGroupLayout.Rows,
                Children = { SensorElement("ram.used", "memory", progress: true) },
            });
            return elements;
        }

        var includeFps = fpsEnabled;
        elements.Add(new OsdElement
        {
            Id = $"cfx-{kind}-gpu",
            Kind = OsdElementKind.Group,
            Label = "GPU",
            GroupLayout = OsdGroupLayout.Rows,
            Children =
            {
                SensorElement("gpu.clock", "GPU", graph: false, unit: "MHz"),
                SensorElement("gpu.temp", "GPU Temp"),
                SensorElement("gpu.usage", "GPU Load", progress: true),
                SensorElement("gpu.power", "GPU Power"),
                SensorElement("gpu.clock.memory", "GPU Mem", unit: "MHz"),
                SensorElement("vram.used", "VRAM Used", unit: "MB"),
            },
        });
        elements.Add(new OsdElement
        {
            Id = $"cfx-{kind}-cpu",
            Kind = OsdElementKind.Group,
            Label = "CPU",
            GroupLayout = OsdGroupLayout.Rows,
            Children =
            {
                SensorElement("cpu.clock", "CPU Max", unit: "MHz"),
                SensorElement("cpu.usage", "CPU Total", progress: true),
                SensorElement("cpu.power", "CPU Package"),
                SensorElement("cpu.temp", "CPU Temp"),
                SensorElement("cpu.usage.max", "CPU Busiest"),
            },
        });
        elements.Add(new OsdElement
        {
            Id = $"cfx-{kind}-ram",
            Kind = OsdElementKind.Group,
            Label = "RAM",
            GroupLayout = OsdGroupLayout.Rows,
            Children =
            {
                SensorElement("ram.used", "RAM Info"),
                SensorElement("ram.percent", "RAM Usage", progress: true),
                SensorElement("ram.available", "Available"),
            },
        });
        if (kind == "split" && elements.Count > 0)
        {
            elements[0].GroupLayout = OsdGroupLayout.Columns;
            elements[0].Label = "GPU  MHz / °C / Load / W";
        }

        if (kind == "info")
        {
            elements = elements.OrderBy(element => element.Label == "CPU" ? 0 : 1).ToList();
            elements.Insert(0, new OsdElement
            {
                Id = "cfx-info-device",
                Kind = OsdElementKind.DeviceHeader,
                Text = "HARDWARE MONITOR",
                LabelColor = "#59B8FF",
                FontSize = 13,
            });
        }

        if (includeFps)
        {
            elements.Add(new OsdElement
            {
                Id = $"cfx-{kind}-fps",
                Kind = OsdElementKind.Group,
                Label = "FPS",
                GroupLayout = OsdGroupLayout.Rows,
                Children =
                {
                    SensorElement("frame.fps", "Framerate", graph: true),
                    SensorElement("frame.time", "Frametime", graph: true, unit: "ms"),
                    SensorElement("frame.fps.1low", "1% Low"),
                },
            });
        }

        return elements;
    }

    private static OsdElement SensorElement(string sensorId, string label, bool graph = false,
        bool progress = false, string? unit = null)
        => new()
        {
            Id = "ref-" + sensorId.Replace('.', '-') + "-" + label.Replace(' ', '-').ToLowerInvariant(),
            Kind = progress ? OsdElementKind.Ring : OsdElementKind.Sensor,
            Label = label,
            SensorId = sensorId,
            ShowGraph = graph,
            ShowProgress = progress,
            Unit = unit ?? string.Empty,
            GraphThickness = 1.5,
            GraphHistoryPoints = 120,
        };

    public static OsdProfile CreateProfileForStyle(
        OsdStyle style,
        IReadOnlyList<MetricPreference> preferences,
        bool fpsEnabled,
        string? name = null)
    {
        var profile = new OsdProfile
        {
            Name = string.IsNullOrWhiteSpace(name) ? StyleName(style) : name.Trim(),
            Style = style,
            FollowMetricPreferences = false,
            Elements = OsdProfile.BuildDefaultElements(preferences, fpsEnabled, style),
            StyleSettings = OsdStyleSettings.CreateDefault(style),
        };
        if (style == OsdStyle.Classic)
        {
            profile.StyleSettings = new OsdStyleSettings();
        }

        profile.Normalize();
        return profile;
    }

    public static string StyleName(OsdStyle style) => style switch
    {
        OsdStyle.HighContrast => "High Contrast",
        OsdStyle.BlueLabels => "Blue Labels",
        OsdStyle.AdaptiveColors => "Adaptive Colors",
        _ => "Classic",
    };

    public OsdProfile Replace(OsdProfile profile)
    {
        var clone = profile.Clone();
        var index = Profiles.FindIndex(p => string.Equals(p.Id, clone.Id, StringComparison.Ordinal));
        if (index >= 0)
        {
            Profiles[index] = clone;
        }
        else
        {
            Profiles.Add(clone);
        }

        ActiveProfileId = clone.Id;
        return clone;
    }

    public OsdProfile? FindProfile(string? id)
        => Profiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
}
