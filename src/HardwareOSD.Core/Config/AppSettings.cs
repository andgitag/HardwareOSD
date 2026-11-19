using HardwareOSD.Core.Input;
using HardwareOSD.Core.Metrics;
using HardwareOSD.Core.Recording;
using HardwareOSD.Core.Theming;

namespace HardwareOSD.Core.Config;

public enum OverlayLayoutMode
{
    /// <summary>One line per metric group: CPU, GPU, VRAM, RAM.</summary>
    Stacked = 0,

    /// <summary>Everything on a single compact line, separated by vertical bars.</summary>
    Inline = 1,

    /// <summary>Stacked rows plus a per-core temperature/utilization block.</summary>
    Detailed = 2,
}

public enum OverlayAnchor
{
    TopLeft = 0,
    TopCenter = 1,
    TopRight = 2,
    MiddleLeft = 3,
    Center = 4,
    MiddleRight = 5,
    BottomLeft = 6,
    BottomCenter = 7,
    BottomRight = 8,
}

public enum OverlayThemeMode
{
    /// <summary>Neutral dark theme with a pure black background by default.</summary>
    Dark = 0,

    /// <summary>Neutral light theme with a warm off-white background.</summary>
    Light = 1,
}

/// <summary>Application chrome theme (independent of the OSD overlay theme).</summary>
public enum AppThemeMode
{
    /// <summary>Neutral dark grey surfaces.</summary>
    Dark = 0,

    /// <summary>
    /// Formerly called "Light". The numeric and JSON value are unchanged for configuration
    /// compatibility; the user-visible name is now "White".
    /// </summary>
    Light = 1,

    /// <summary>Alias for <see cref="Light"/> so new code can use the current display name.</summary>
    White = Light,

    /// <summary>Absolute black surfaces with high-contrast light text.</summary>
    Black = 2,

    /// <summary>Warm milk/cream light surfaces, distinct from the pure-white theme.</summary>
    WarmLight = 3,

    /// <summary>User-selected accent colour; the base adapts for readability.</summary>
    Custom = 4,
}

/// <summary>Modifier held while dragging the overlay with the left mouse button.</summary>
public enum OverlayDragModifier
{
    Control = 0,
    Shift = 1,
}

public sealed class MetricPreference
{
    public string Id { get; set; } = string.Empty;
    public bool Visible { get; set; }
}

public sealed class OverlaySettings
{
    public bool Enabled { get; set; } = true;
    public OverlayLayoutMode Layout { get; set; } = OverlayLayoutMode.Stacked;
    public OverlayAnchor Anchor { get; set; } = OverlayAnchor.TopLeft;

    /// <summary>Offset in device-independent pixels relative to the anchored screen corner.</summary>
    public int OffsetX { get; set; } = 24;
    public int OffsetY { get; set; } = 24;

    /// <summary>Monitor device name (e.g. \\.\DISPLAY1); empty means the primary monitor.</summary>
    public string MonitorDevice { get; set; } = string.Empty;

    /// <summary>True after the user drags the overlay; X/Y are physical virtual-desktop pixels.</summary>
    public bool UseCustomPosition { get; set; }
    public int CustomX { get; set; }
    public int CustomY { get; set; }

    public string FontFamily { get; set; } = "Segoe UI Variable Text";
    public double FontSize { get; set; } = 15;

    public string LabelColor { get; set; } = "#6FD8CC";
    public string ValueColor { get; set; } = "#F4F7FA";
    public string UnitColor { get; set; } = "#9AA3AC";
    public string WarningColor { get; set; } = "#FFB020";
    public string CriticalColor { get; set; } = "#FF5C5C";
    public string BackgroundColor { get; set; } = "#000000";
    public int BackgroundOpacity { get; set; } = 70;

    /// <summary>Active theme preset. Changing it applies the preset colours in the UI.</summary>
    public OverlayThemeMode Theme { get; set; } = OverlayThemeMode.Dark;

    public bool ShowBackground { get; set; } = true;
    public double CornerRadius { get; set; } = 7;
    public double PaddingX { get; set; } = 14;
    public double PaddingY { get; set; } = 9;
    public double RowSpacing { get; set; } = 4;
    public double ColumnSpacing { get; set; } = 13;

    public bool ShowLabels { get; set; } = true;
    public bool ShowUnits { get; set; } = true;
    public bool ShowSeparators { get; set; }
    public bool ShowSparklines { get; set; }
    public bool HighlightWarnings { get; set; } = true;
    public bool ClickThrough { get; set; } = true;
    public bool Shadow { get; set; } = true;

    /// <summary>Hold a modifier while hovering the overlay to drag it without Move Overlay mode.</summary>
    public bool ModifierDragEnabled { get; set; } = true;

    public OverlayDragModifier ModifierDragKey { get; set; } = OverlayDragModifier.Control;
}

public sealed class MonitoringSettings
{
    /// <summary>Hardware polling interval in milliseconds. Hardware is polled independently of rendering.</summary>
    public int PollIntervalMs { get; set; } = 1000;

    /// <summary>Overlay repaint rate. Rendering only reads the cache.</summary>
    public int OverlayRefreshHz { get; set; } = 4;

    public bool EnableLibreHardwareMonitor { get; set; } = true;
    public bool EnableNvml { get; set; } = true;

    /// <summary>
    /// Query the read-only MSI ACPI fan interface on MSI notebooks. The provider is inert when the
    /// WMI class is absent and reports an explicit access reason when the process is not elevated.
    /// </summary>
    public bool EnableMsiAcpi { get; set; } = true;

    /// <summary>
    /// When the app is not elevated, use the optional LocalSystem fan broker for read-only MSI fan
    /// telemetry. This never installs or starts the service; it only enables/shows the client path.
    /// </summary>
    public bool EnableMsiFanBroker { get; set; } = true;

    public bool ShowUnavailableMetrics { get; set; } = true;

    /// <summary>When no CPU package sensor is available (no elevation), surface the ACPI thermal zone reading.</summary>
    public bool UseThermalZoneAsCpuTemperature { get; set; } = true;
}

public sealed class BehaviorSettings
{
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool EnableGlobalHotkey { get; set; } = true;

    /// <summary>UI language: "en" or "uk".</summary>
    public string Language { get; set; } = "en";

    /// <summary>Application chrome theme; the OSD overlay has its own theme.</summary>
    public AppThemeMode AppTheme { get; set; } = AppThemeMode.Dark;

    /// <summary>Accent colour for the custom application theme (#RRGGBB).</summary>
    public string AppAccentColor { get; set; } = AppThemeCatalog.DefaultAccent;

    /// <summary>Shortcut that toggles the overlay, e.g. "Ctrl+Alt+F10".</summary>
    public string HotkeyToggleOverlay { get; set; } = HotkeyGesture.DefaultText;

    /// <summary>Shortcut that shows/hides the main window, e.g. "Ctrl+Alt+F9".</summary>
    public string HotkeyToggleMainWindow { get; set; } = HotkeyGesture.MainWindowDefaultText;

    public string HotkeyStartRecording { get; set; } = "Ctrl+Shift+R";
    public string HotkeyStopRecording { get; set; } = "Ctrl+Shift+S";
    public string HotkeyRestartApplication { get; set; } = "Ctrl+Alt+F12";

    /// <summary>Held chord that arms overlay dragging while the cursor is over the overlay.</summary>
    public string HotkeyDragOverlay { get; set; } = HoldGesture.DefaultText;

    /// <summary>True after the legacy Ctrl/Shift enum has been imported into the editable chord.</summary>
    public bool HotkeyDragOverlayMigrated { get; set; }

    public bool ShowTrayNotifications { get; set; } = true;
    public Diagnostics.LogLevel MinimumLogLevel { get; set; } = Diagnostics.LogLevel.Info;

    /// <summary>One-time notification that FPS needs Performance Log Users permission was shown.</summary>
    public bool EtwPermissionPrompted { get; set; }
}

/// <summary>Root persisted configuration object. Always normalised after loading.</summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 2;
    public OverlaySettings Overlay { get; set; } = new();
    public MonitoringSettings Monitoring { get; set; } = new();
    public BehaviorSettings Behavior { get; set; } = new();
    public FpsSettings Fps { get; set; } = new();
    public RecordingSettings Recording { get; set; } = new();
    public List<MetricPreference> Metrics { get; set; } = new();
    public OsdSettings Osd { get; set; } = new();

    public static AppSettings CreateDefault()
    {
        var settings = new AppSettings { SchemaVersion = 3 };
        settings.Normalize();
        return settings;
    }

    public AppSettings Clone()
    {
        var clone = new AppSettings
        {
            SchemaVersion = SchemaVersion,
            Overlay = new OverlaySettings
            {
                Enabled = Overlay.Enabled,
                Layout = Overlay.Layout,
                Anchor = Overlay.Anchor,
                OffsetX = Overlay.OffsetX,
                OffsetY = Overlay.OffsetY,
                MonitorDevice = Overlay.MonitorDevice,
                UseCustomPosition = Overlay.UseCustomPosition,
                CustomX = Overlay.CustomX,
                CustomY = Overlay.CustomY,
                FontFamily = Overlay.FontFamily,
                FontSize = Overlay.FontSize,
                LabelColor = Overlay.LabelColor,
                ValueColor = Overlay.ValueColor,
                UnitColor = Overlay.UnitColor,
                WarningColor = Overlay.WarningColor,
                CriticalColor = Overlay.CriticalColor,
                BackgroundColor = Overlay.BackgroundColor,
                BackgroundOpacity = Overlay.BackgroundOpacity,
                Theme = Overlay.Theme,
                ShowBackground = Overlay.ShowBackground,
                CornerRadius = Overlay.CornerRadius,
                PaddingX = Overlay.PaddingX,
                PaddingY = Overlay.PaddingY,
                RowSpacing = Overlay.RowSpacing,
                ColumnSpacing = Overlay.ColumnSpacing,
                ShowLabels = Overlay.ShowLabels,
                ShowUnits = Overlay.ShowUnits,
                ShowSeparators = Overlay.ShowSeparators,
                ShowSparklines = Overlay.ShowSparklines,
                HighlightWarnings = Overlay.HighlightWarnings,
                ClickThrough = Overlay.ClickThrough,
                Shadow = Overlay.Shadow,
                ModifierDragEnabled = Overlay.ModifierDragEnabled,
                ModifierDragKey = Overlay.ModifierDragKey,
            },
            Monitoring = new MonitoringSettings
            {
                PollIntervalMs = Monitoring.PollIntervalMs,
                OverlayRefreshHz = Monitoring.OverlayRefreshHz,
                EnableLibreHardwareMonitor = Monitoring.EnableLibreHardwareMonitor,
                EnableNvml = Monitoring.EnableNvml,
                EnableMsiAcpi = Monitoring.EnableMsiAcpi,
                EnableMsiFanBroker = Monitoring.EnableMsiFanBroker,
                ShowUnavailableMetrics = Monitoring.ShowUnavailableMetrics,
                UseThermalZoneAsCpuTemperature = Monitoring.UseThermalZoneAsCpuTemperature,
            },
            Behavior = new BehaviorSettings
            {
                StartWithWindows = Behavior.StartWithWindows,
                MinimizeToTray = Behavior.MinimizeToTray,
                CloseToTray = Behavior.CloseToTray,
                StartMinimized = Behavior.StartMinimized,
                EnableGlobalHotkey = Behavior.EnableGlobalHotkey,
                Language = Behavior.Language,
                AppTheme = Behavior.AppTheme,
                AppAccentColor = Behavior.AppAccentColor,
                HotkeyToggleOverlay = Behavior.HotkeyToggleOverlay,
                HotkeyToggleMainWindow = Behavior.HotkeyToggleMainWindow,
                HotkeyStartRecording = Behavior.HotkeyStartRecording,
                HotkeyStopRecording = Behavior.HotkeyStopRecording,
                HotkeyRestartApplication = Behavior.HotkeyRestartApplication,
                HotkeyDragOverlay = Behavior.HotkeyDragOverlay,
                HotkeyDragOverlayMigrated = Behavior.HotkeyDragOverlayMigrated,
                ShowTrayNotifications = Behavior.ShowTrayNotifications,
                MinimumLogLevel = Behavior.MinimumLogLevel,
                EtwPermissionPrompted = Behavior.EtwPermissionPrompted,
            },
            Fps = Fps.Clone(),
            Recording = Recording.Clone(),
            Metrics = Metrics.Select(m => new MetricPreference { Id = m.Id, Visible = m.Visible }).ToList(),
            Osd = Osd.Clone(),
        };

        return clone;
    }

    /// <summary>
    /// Upgrades a loaded configuration. Version 2 replaced the original navy-tinted default
    /// background with pure black; only untouched defaults are migrated.
    /// </summary>
    public void Migrate()
    {
        if (SchemaVersion < 2)
        {
            var background = Overlay?.BackgroundColor;
            if (string.Equals(background, "#070B10", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(background, "#070B10FF", StringComparison.OrdinalIgnoreCase))
            {
                Overlay!.BackgroundColor = "#000000";
                if (Overlay.BackgroundOpacity == 55)
                {
                    Overlay.BackgroundOpacity = 70;
                }
            }

            SchemaVersion = 2;
        }

        // Schema 3 adds the versioned OSD profile/engine model. Normalize builds the four default
        // styles from the legacy overlay when no OSD section exists; existing Classic settings stay
        // untouched and remain the active profile.
        SchemaVersion = Math.Max(SchemaVersion, 3);
    }

    /// <summary>Repairs out-of-range or missing values. Never throws.</summary>
    public void Normalize()
    {
        Overlay ??= new OverlaySettings();
        Monitoring ??= new MonitoringSettings();
        Behavior ??= new BehaviorSettings();
        Fps ??= new FpsSettings();
        Recording ??= new RecordingSettings();
        Metrics ??= new List<MetricPreference>();
        Osd ??= new OsdSettings();

        Fps.Normalize();
        Recording.Normalize();

        var overlay = Overlay;
        overlay.FontSize = Math.Clamp(overlay.FontSize, 9, 32);
        overlay.OffsetX = Math.Clamp(overlay.OffsetX, -16_000, 16_000);
        overlay.OffsetY = Math.Clamp(overlay.OffsetY, -16_000, 16_000);
        overlay.MonitorDevice = (overlay.MonitorDevice ?? string.Empty).Trim();
        overlay.CustomX = Math.Clamp(overlay.CustomX, -32_000, 32_000);
        overlay.CustomY = Math.Clamp(overlay.CustomY, -32_000, 32_000);
        overlay.BackgroundOpacity = Math.Clamp(overlay.BackgroundOpacity, 0, 100);
        overlay.CornerRadius = Math.Clamp(overlay.CornerRadius, 0, 32);
        overlay.PaddingX = Math.Clamp(overlay.PaddingX, 0, 64);
        overlay.PaddingY = Math.Clamp(overlay.PaddingY, 0, 64);
        overlay.RowSpacing = Math.Clamp(overlay.RowSpacing, 0, 32);
        overlay.ColumnSpacing = Math.Clamp(overlay.ColumnSpacing, 0, 48);
        if (!Enum.IsDefined(overlay.Layout))
        {
            overlay.Layout = OverlayLayoutMode.Stacked;
        }

        if (!Enum.IsDefined(overlay.Anchor))
        {
            overlay.Anchor = OverlayAnchor.TopLeft;
        }

        overlay.FontFamily = string.IsNullOrWhiteSpace(overlay.FontFamily) ? "Segoe UI Variable Text" : overlay.FontFamily.Trim();
        overlay.LabelColor = SanitizeColor(overlay.LabelColor, "#6FD8CC");
        overlay.ValueColor = SanitizeColor(overlay.ValueColor, "#F4F7FA");
        overlay.UnitColor = SanitizeColor(overlay.UnitColor, "#9AA3AC");
        overlay.WarningColor = SanitizeColor(overlay.WarningColor, "#FFB020");
        overlay.CriticalColor = SanitizeColor(overlay.CriticalColor, "#FF5C5C");
        overlay.BackgroundColor = SanitizeColor(overlay.BackgroundColor, "#000000");

        if (!Enum.IsDefined(overlay.Theme))
        {
            overlay.Theme = OverlayThemeMode.Dark;
        }

        if (!Enum.IsDefined(overlay.ModifierDragKey))
        {
            overlay.ModifierDragKey = OverlayDragModifier.Control;
        }

        Monitoring.PollIntervalMs = Math.Clamp(Monitoring.PollIntervalMs, 200, 10_000);
        Monitoring.OverlayRefreshHz = Math.Clamp(Monitoring.OverlayRefreshHz, 1, 30);
        Behavior.MinimumLogLevel = Enum.IsDefined(Behavior.MinimumLogLevel) ? Behavior.MinimumLogLevel : Diagnostics.LogLevel.Info;
        if (!Enum.IsDefined(Behavior.AppTheme))
        {
            Behavior.AppTheme = AppThemeMode.Dark;
        }
        Behavior.AppAccentColor = AppThemeCatalog.NormalizeAccent(Behavior.AppAccentColor);
        Behavior.Language = string.Equals(Behavior.Language?.Trim(), "uk", StringComparison.OrdinalIgnoreCase) ? "uk" : "en";
        if (!HotkeyGesture.TryParse(Behavior.HotkeyToggleOverlay, out var hotkey))
        {
            Behavior.HotkeyToggleOverlay = HotkeyGesture.DefaultText;
            hotkey = HotkeyGesture.Default;
        }
        else
        {
            Behavior.HotkeyToggleOverlay = hotkey.DisplayText;
        }

        if (!HotkeyGesture.TryParse(Behavior.HotkeyToggleMainWindow, out var mainHotkey))
        {
            Behavior.HotkeyToggleMainWindow = HotkeyGesture.MainWindowDefaultText;
            mainHotkey = HotkeyGesture.MainWindowDefault;
        }
        else
        {
            Behavior.HotkeyToggleMainWindow = mainHotkey.DisplayText;
        }

        var startRecordingHotkey = ParseHotkey(Behavior.HotkeyStartRecording,
            new HotkeyGesture(HotkeyGesture.ModControl | HotkeyGesture.ModShift, 0x52));
        var stopRecordingHotkey = ParseHotkey(Behavior.HotkeyStopRecording,
            new HotkeyGesture(HotkeyGesture.ModControl | HotkeyGesture.ModShift, 0x53));
        var restartHotkey = ParseHotkey(Behavior.HotkeyRestartApplication,
            new HotkeyGesture(HotkeyGesture.ModControl | HotkeyGesture.ModAlt, 0x7B));
        Behavior.HotkeyStartRecording = startRecordingHotkey.DisplayText;
        Behavior.HotkeyStopRecording = stopRecordingHotkey.DisplayText;
        Behavior.HotkeyRestartApplication = restartHotkey.DisplayText;

        // 0.25.x stored the drag chord as an enum (Ctrl/Shift). Migrate it into the editable hold
        // gesture once; the old fields stay untouched for older builds.
        if (!Behavior.HotkeyDragOverlayMigrated)
        {
            Behavior.HotkeyDragOverlay = Overlay.ModifierDragKey == OverlayDragModifier.Shift
                ? "Shift"
                : HoldGesture.DefaultText;
            Behavior.HotkeyDragOverlayMigrated = true;
        }

        Behavior.HotkeyDragOverlay = HoldGesture.TryParse(Behavior.HotkeyDragOverlay, out var dragGesture)
            ? dragGesture.DisplayText
            : HoldGesture.DefaultText;

        // Duplicate assignments are never registered twice: the overlay keeps its shortcut and the
        // main-window shortcut falls back to a free default. Users see the reset in both UI boxes.
        if (hotkey == mainHotkey)
        {
            mainHotkey = hotkey == HotkeyGesture.MainWindowDefault
                ? HotkeyGesture.Default
                : HotkeyGesture.MainWindowDefault;
            Behavior.HotkeyToggleMainWindow = mainHotkey.DisplayText;
        }

        var usedHotkeys = new HashSet<HotkeyGesture> { hotkey, mainHotkey };
        HotkeyGesture ResolveHotkey(HotkeyGesture candidate, HotkeyGesture fallback)
        {
            if (usedHotkeys.Contains(candidate) && !usedHotkeys.Contains(fallback))
            {
                candidate = fallback;
            }

            usedHotkeys.Add(candidate);
            return candidate;
        }

        startRecordingHotkey = ResolveHotkey(startRecordingHotkey,
            new HotkeyGesture(HotkeyGesture.ModControl | HotkeyGesture.ModShift, 0x52));
        stopRecordingHotkey = ResolveHotkey(stopRecordingHotkey,
            new HotkeyGesture(HotkeyGesture.ModControl | HotkeyGesture.ModShift, 0x53));
        restartHotkey = ResolveHotkey(restartHotkey,
            new HotkeyGesture(HotkeyGesture.ModControl | HotkeyGesture.ModAlt, 0x7B));
        Behavior.HotkeyStartRecording = startRecordingHotkey.DisplayText;
        Behavior.HotkeyStopRecording = stopRecordingHotkey.DisplayText;
        Behavior.HotkeyRestartApplication = restartHotkey.DisplayText;

        // Merge stored metric preferences with the catalogue: keep user order, drop unknown ids,
        // append newly added metrics with their defaults.
        var legacyGpuFanVisible = Metrics.Any(m => m.Id == "gpu.fan" && m.Visible);
        var order = new List<MetricPreference>(Metrics.Count + 8);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var metric in Metrics)
        {
            if (string.IsNullOrWhiteSpace(metric.Id) || !seen.Add(metric.Id))
            {
                continue;
            }

            if (MetricCatalog.Find(metric.Id) is null)
            {
                continue;
            }

            order.Add(metric);
        }

        foreach (var definition in MetricCatalog.All)
        {
            if (seen.Add(definition.Id))
            {
                var visible = definition.DefaultVisible ||
                              (legacyGpuFanVisible &&
                               (definition.Id is "gpu.fan.rpm" or "gpu.fan.percent"));
                order.Add(new MetricPreference { Id = definition.Id, Visible = visible });
            }
        }

        Metrics = order;

        Osd.Normalize(overlay, Metrics, Fps.Enabled);
    }

    private static HotkeyGesture ParseHotkey(string? value, HotkeyGesture fallback)
        => HotkeyGesture.TryParse(value, out var gesture) ? gesture : fallback;

    public static string SanitizeColor(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var text = value.Trim();
        if (!text.StartsWith('#'))
        {
            text = "#" + text;
        }

        if (text.Length is not (7 or 9))
        {
            return fallback;
        }

        for (var i = 1; i < text.Length; i++)
        {
            if (!Uri.IsHexDigit(text[i]))
            {
                return fallback;
            }
        }

        return text.ToUpperInvariant();
    }

    public bool IsMetricVisible(string id)
        => Metrics.Any(m => m.Id == id && m.Visible);

    public void SetMetricVisible(string id, bool visible)
    {
        var existing = Metrics.FirstOrDefault(m => m.Id == id);
        if (existing is null)
        {
            Metrics.Add(new MetricPreference { Id = id, Visible = visible });
        }
        else
        {
            existing.Visible = visible;
        }
    }
}

