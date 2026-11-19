using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using HardwareOSD.App.Localization;
using HardwareOSD.App.Native;
using HardwareOSD.App.Overlay;
using HardwareOSD.App.Services;
using HardwareOSD.App.Theming;
using HardwareOSD.Core.Config;
using HardwareOSD.Core.Diagnostics;
using HardwareOSD.Core.Fps;
using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.Input;
using HardwareOSD.Core.Hardware.Providers;
using HardwareOSD.Core.Metrics;
using HardwareOSD.Core.Overlay;
using HardwareOSD.Core.Recording;
using HardwareOSD.Core.TileOsd;
using HardwareOSD.Core.SystemInfo;
using HardwareOSD.Core.Theming;
using Microsoft.Win32;

namespace HardwareOSD.App.Views;

/// <summary>
/// Configuration window. The layout follows the conventions of classic hardware-monitoring
/// utilities: a narrow navigation rail, flat sections and dense, readable controls. Every change is
/// applied live to the running overlay and persisted with a short debounce.
///
/// All user-visible strings come from <see cref="Loc"/>; dynamically generated controls are rebuilt
/// when the language changes, while static XAML text updates through the LocText binding.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly Regex HexColor = new("^#?[0-9a-fA-F]{6}$", RegexOptions.Compiled);

    private readonly AppRuntime _runtime;
    private readonly Dictionary<string, TextBlock> _metricValueTexts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> _metricUnitTexts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> _metricSourceTexts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CheckBox> _metricChecks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Border> _metricRowBorders = new(StringComparer.Ordinal);
    private string? _selectedMetricId;
    private TextBox? _metricNameBox;
    private TextBox? _metricUnitBox;
    private TextBox? _metricDecimalsBox;
    private TextBox? _metricWarningBox;
    private TextBox? _metricCriticalBox;
    private TextBox? _metricColorBox;
    private TextBox? _metricGraphColorBox;
    private TextBox? _metricGraphThicknessBox;
    private TextBox? _metricGraphHistoryBox;
    private TextBox? _metricGraphMinBox;
    private TextBox? _metricGraphMaxBox;
    private CheckBox? _metricVisibleCheck;
    private CheckBox? _metricGraphCheck;
    private CheckBox? _metricProgressCheck;
    private CheckBox? _metricAutoColorCheck;
    private readonly Dictionary<string, TextBox> _colorBoxes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Border> _colorSwatches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> _colorLabels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _colorPickers = new(StringComparer.Ordinal);
    private readonly List<ToggleButton> _layoutButtons = new();
    private readonly List<ToggleButton> _anchorButtons = new();
    private readonly List<ToggleButton> _navButtons = new();
    private readonly List<ToggleButton> _themeButtons = new();
    private readonly List<ToggleButton> _appThemeButtons = new();
    private readonly List<ToggleButton> _settingsTabButtons = new();
    private readonly List<(Button Button, string Name)> _presetButtons = new();

    private OverlayLayoutMode _layoutMode = OverlayLayoutMode.Stacked;
    private OverlayAnchor _anchor = OverlayAnchor.TopLeft;
    private OverlayThemeMode _themeMode = OverlayThemeMode.Dark;
    private AppThemeMode _appThemeMode = AppThemeMode.Dark;
    private string _customAccent = AppThemeCatalog.DefaultAccent;
    private string _activeSettingsTab = "general";
    private bool _syncingSettingsTabs;
    private string? _capturingHotkeyTarget;
    private string _hotkeyBeforeCapture = HotkeyGesture.DefaultText;
    private uint _dragCaptureModifiers;
    private bool _dragCapturePending;
    private bool _loading;
    private bool _selfApplying;
    private bool _syncingButtons;
    private bool _grantingEtw;
    private long _lastStatusTick;
    private readonly Dictionary<string, CheckBox> _recordingChecks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RecordingSensor> _recordingSensors = new(StringComparer.Ordinal);
    private bool _recordingSensorSync;
    private bool _recordingListBuilt;
    private long _lastRecordingStatusTick;
    private bool _systemInfoLoading;
    private bool _systemPageLoading;
    private long _lastSystemLiveTick;
    private bool _fpsInfoExpanded;
    private readonly PawnIoStatus _pawnIoStatus;
    private readonly DispatcherTimer _saveFeedbackTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };

    public MainWindow(AppRuntime runtime)
    {
        _runtime = runtime;
        _pawnIoStatus = PawnIoAccessProbe.Query();
        InitializeComponent();

        _saveFeedbackTimer.Tick += (_, _) =>
        {
            _saveFeedbackTimer.Stop();
            ShowSaveFeedback(saved: false);
        };

        _navButtons.AddRange(new[]
        {
            NavSystem, NavOverlay, NavMetrics, NavRecording, NavSettings,
        });
        foreach (var button in _navButtons)
        {
            button.Checked += Nav_Checked;
            button.Unchecked += (sender, _) =>
            {
                if (!_syncingButtons)
                {
                    ((ToggleButton)sender!).IsChecked = true;
                }
            };
        }
        UpdateNavAutomationNames();

        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnStateChanged;
        Closed += OnClosed;
        SourceInitialized += (_, _) => ApplyTitleBarTheme();
        AppThemeManager.ThemeChanged += OnAppThemeChanged;

        _runtime.SettingsApplied += OnRuntimeSettingsApplied;
        _runtime.MonitoringStateChanged += OnMonitoringStateChanged;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
    }

    public bool AllowClose { get; set; }

    private void OnClosed(object? sender, EventArgs e)
    {
        _saveFeedbackTimer.Stop();
        AppThemeManager.ThemeChanged -= OnAppThemeChanged;
        _runtime.SettingsApplied -= OnRuntimeSettingsApplied;
        _runtime.MonitoringStateChanged -= OnMonitoringStateChanged;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
    }

    private void OnRuntimeSettingsApplied(object? sender, EventArgs e)
    {
        if (!_selfApplying)
        {
            LoadFromSettings();
        }
    }

    private void OnMonitoringStateChanged(object? sender, EventArgs e) => UpdateStatus(force: true);

    private void OnAppThemeChanged(object? sender, EventArgs e) => ApplyTitleBarTheme();

    private void UpdateNavAutomationNames()
    {
        foreach (var button in _navButtons)
        {
            var key = (string)button.Tag switch
            {
                "recording" => "nav_recording",
                "metrics" => "nav_metrics",
                "system" => "nav_system",
                "settings" => "nav_settings",
                _ => "nav_overlay",
            };
            System.Windows.Automation.AutomationProperties.SetName(button, Loc.T(key));
        }
    }

    private void ApplyTitleBarTheme()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            NativeMethods.SetTitleBarTheme(handle, AppThemeManager.IsDark);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
            if (_layoutButtons.Count == 0)
            {
                BuildStaticUi();
            }
        }
        finally
        {
            _loading = false;
        }

        LoadFromSettings();
        UpdateStatus(force: true);
        UpdatePreview();
        _ = RefreshSystemPageAsync();
    }

    // ------------------------------------------------------------------
    // UI construction
    // ------------------------------------------------------------------
    private void BuildStaticUi()
    {
        // Settings sub-tabs
        foreach (var (tag, key) in new[]
                 {
                     ("general", "settings_general"),
                     ("appearance", "settings_appearance"),
                     ("languages", "settings_languages"),
                     ("hotkeys", "settings_hotkeys"),
                     ("system", "settings_system"),
                     ("diagnostics", "settings_diagnostics"),
                     ("about", "settings_about"),
                 })
        {
            var button = new ToggleButton
            {
                Content = Loc.T(key),
                Style = (Style)FindResource("TabButton"),
                Margin = new Thickness(0, 0, 1, 0),
                Tag = tag,
            };
            button.Checked += (_, _) =>
            {
                if (_syncingSettingsTabs)
                {
                    return;
                }

                SelectSettingsTab((string)button.Tag);
            };
            button.Unchecked += (_, _) =>
            {
                if (!_syncingSettingsTabs)
                {
                    button.IsChecked = true;
                }
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, "SettingsTab_" + tag);
            _settingsTabButtons.Add(button);
            SettingsTabsPanel.Children.Add(button);
        }

        SelectSettingsTab("general");

        // Layout segments
        foreach (var (mode, key) in new[]
                 {
                     (OverlayLayoutMode.Stacked, "behavior_stacked"),
                     (OverlayLayoutMode.Inline, "behavior_inline"),
                     (OverlayLayoutMode.Detailed, "behavior_detailed"),
                 })
        {
            var button = new ToggleButton
            {
                Content = Loc.T(key),
                Style = (Style)FindResource("SegmentButton"),
                Margin = new Thickness(0, 0, 5, 0),
                Tag = mode,
            };
            button.Checked += (_, _) =>
            {
                if (_syncingButtons)
                {
                    return;
                }

                _layoutMode = (OverlayLayoutMode)button.Tag;
                SyncLayoutButtons();
                ApplyLive();
            };
            button.Unchecked += (_, _) =>
            {
                if (!_syncingButtons)
                {
                    button.IsChecked = true;
                }
            };
            _layoutButtons.Add(button);
            LayoutButtons.Children.Add(button);
        }

        // 3x3 anchor grid
        var anchors = new[]
        {
            OverlayAnchor.TopLeft, OverlayAnchor.TopCenter, OverlayAnchor.TopRight,
            OverlayAnchor.MiddleLeft, OverlayAnchor.Center, OverlayAnchor.MiddleRight,
            OverlayAnchor.BottomLeft, OverlayAnchor.BottomCenter, OverlayAnchor.BottomRight,
        };

        foreach (var anchor in anchors)
        {
            var button = new ToggleButton
            {
                Content = AnchorShortLabel(anchor),
                ToolTip = Loc.T(AnchorKey(anchor)),
                Style = (Style)FindResource("SegmentButton"),
                Margin = new Thickness(1),
                Padding = new Thickness(0),
                FontSize = 10,
                Tag = anchor,
            };
            button.Checked += (_, _) =>
            {
                if (_syncingButtons)
                {
                    return;
                }

                _anchor = (OverlayAnchor)button.Tag;
                SyncAnchorButtons();
                ApplyLive();
            };
            button.Unchecked += (_, _) =>
            {
                if (!_syncingButtons)
                {
                    button.IsChecked = true;
                }
            };
            _anchorButtons.Add(button);
            AnchorGrid.Children.Add(button);
        }

        // Font families and the old Overlay typography block were removed in 0.26.0. Font
        // selection now belongs to each tile in the OSD Builder.

        BuildMetricCategoryChoices();

        // Theme presets
        foreach (var (mode, key) in new[]
                 {
                     (OverlayThemeMode.Dark, "appearance_dark"),
                     (OverlayThemeMode.Light, "appearance_light"),
                 })
        {
            var button = new ToggleButton
            {
                Content = Loc.T(key),
                Style = (Style)FindResource("SegmentButton"),
                Margin = new Thickness(0, 0, 5, 0),
                Tag = mode,
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(button,
                mode == OverlayThemeMode.Light ? "OverlayThemeLight" : "OverlayThemeDark");
            button.Checked += (_, _) =>
            {
                if (_syncingButtons)
                {
                    return;
                }

                _themeMode = (OverlayThemeMode)button.Tag;
                SyncThemeButtons();
                ApplyThemeMode(_themeMode);
            };
            button.Unchecked += (_, _) =>
            {
                if (!_syncingButtons)
                {
                    button.IsChecked = true;
                }
            };
            _themeButtons.Add(button);
            ThemeButtons.Children.Add(button);
        }

        // Application chrome themes are independent of the OSD palette.
        foreach (var (mode, key, automationId) in new[]
                 {
                     (AppThemeMode.Black, "appearance_app_black", "AppThemeBlack"),
                     (AppThemeMode.Dark, "appearance_app_dark", "AppThemeDark"),
                     (AppThemeMode.WarmLight, "appearance_app_warm", "AppThemeWarm"),
                     (AppThemeMode.Light, "appearance_app_white", "AppThemeLight"),
                     (AppThemeMode.Custom, "appearance_app_custom", "AppThemeCustom"),
                 })
        {
            var button = new ToggleButton
            {
                Content = Loc.T(key),
                Style = (Style)FindResource("SegmentButton"),
                Margin = new Thickness(0, 0, 5, 5),
                Tag = mode,
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, automationId);
            button.Checked += (_, _) =>
            {
                if (_syncingButtons)
                {
                    return;
                }

                _appThemeMode = (AppThemeMode)button.Tag;
                SyncAppThemeButtons();
                AppThemeManager.Apply(_appThemeMode, _customAccent);
                ApplyTitleBarTheme();
                ApplyLive();
                FooterStatus.Text = Loc.F("appearance_theme_applied", AppThemeName(_appThemeMode));
            };
            button.Unchecked += (_, _) =>
            {
                if (!_syncingButtons)
                {
                    button.IsChecked = true;
                }
            };
            _appThemeButtons.Add(button);
            AppThemeButtons.Children.Add(button);
        }

        BuildCustomAccentPalette();
        CustomAccentPicker.Click += (_, _) => PickCustomAccent();
        UpdateCustomAccentControls();

        foreach (var preset in OverlayThemePresets.Backgrounds)
        {
            var button = new Button
            {
                Content = Loc.T(PresetKey(preset.Name)),
                Style = (Style)FindResource("FlatButton"),
                Margin = new Thickness(0, 0, 5, 5),
                Tag = preset,
                ToolTip = $"{preset.Color} · {preset.Opacity}%",
            };
            button.Click += (_, _) => ApplyBackgroundPreset(preset);
            _presetButtons.Add((button, preset.Name));
            BackgroundPresetPanel.Children.Add(button);
        }

        // Log levels
        BuildLogLevelChoices(_runtime.Settings.Behavior.MinimumLogLevel);
        LogLevelCombo.SelectionChanged += (_, _) => ApplyLive();

        // Color editors
        AddColorRow("appearance_label_color", "LabelColor", "#6FD8CC");
        AddColorRow("appearance_value_color", "ValueColor", "#F4F7FA");
        AddColorRow("appearance_unit_color", "UnitColor", "#9AA3AC");
        AddColorRow("appearance_warning_color", "WarningColor", "#FFB020");
        AddColorRow("appearance_critical_color", "CriticalColor", "#FF5C5C");
        AddColorRow("appearance_background_color", "BackgroundColor", "#000000");

        // Checkboxes
        ChkOverlayEnabled.Checked += (_, _) => ApplyLive();
        ChkOverlayEnabled.Unchecked += (_, _) => ApplyLive();
        foreach (var check in new[]
                 {
                     ChkClickThrough, ChkBackground, ChkShadow, ChkLabels, ChkUnits, ChkSeparators,
                     ChkSparklines, ChkWarnings, ChkNvml, ChkLhm, ChkTzone, ChkUnavailable,
                     ChkMinimizeToTray, ChkCloseToTray, ChkStartMinimized,
                     ChkHotkey, ChkTrayNotifications, ChkMeasureWindowedApps, ChkModifierDrag,
                 })
        {
            check.Checked += (_, _) => ApplyLive();
            check.Unchecked += (_, _) => ApplyLive();
        }

        // Windows startup is a system side effect: only an explicit checkbox change may write it.
        ChkStartWithWindows.Checked += (_, _) => OnStartWithWindowsChanged();
        ChkStartWithWindows.Unchecked += (_, _) => OnStartWithWindowsChanged();

        // FPS display mode
        BuildFpsModeChoices(_runtime.Settings.Fps.Mode);
        FpsModeCombo.SelectionChanged += (_, _) => ApplyLive();

        // FPS capture mode (Game Only / All Applications)
        BuildFpsCaptureModeChoices(_runtime.Settings.Fps.CaptureMode);
        FpsCaptureModeCombo.SelectionChanged += (_, _) =>
        {
            UpdateFpsCaptureModeUi();
            ApplyLive();
        };

        // Language selector
        BuildLanguageChoices(_runtime.Settings.Behavior.Language);
        LanguageCombo.SelectionChanged += OnLanguageSelectionChanged;

        // Monitor selector
        BuildMonitorChoices(_runtime.Settings.Overlay.MonitorDevice);
        MonitorCombo.SelectionChanged += (_, _) => ApplyLive();

        // Recording
        BuildRecordingChoices();
        RecordingFormatCombo.SelectionChanged += (_, _) => ApplyLive();
        RecordingIntervalCombo.SelectionChanged += (_, _) => ApplyLive();
        RecordingDirectoryBox.TextChanged += (_, _) => ApplyLive();
        RecordingFileNameBox.TextChanged += (_, _) => ApplyLive();

        HotkeyBox.Text = HotkeyGesture.DefaultText;
        MainHotkeyBox.Text = HotkeyGesture.MainWindowDefaultText;
        UpdateHotkeyStatus();

        WireSlider(BgOpacitySlider, BgOpacityValue, "%");
        WireSlider(CornerSlider, CornerValue, " px");
        WireSlider(PadXSlider, PadXValue, " px");
        WireSlider(PadYSlider, PadYValue, " px");
        WireSlider(RowGapSlider, RowGapValue, " px");
        WireSlider(ColGapSlider, ColGapValue, " px");
        WireSlider(PollSlider, PollValue, " ms");
        WireSlider(RefreshSlider, RefreshValue, " Hz");
        WireSlider(FpsIntervalSlider, FpsIntervalValue, " ms");

        OffsetXBox.TextChanged += (_, _) => ApplyLive();
        OffsetYBox.TextChanged += (_, _) => ApplyLive();

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(4) ?? "0.0.0.0";
        VersionInfoText.Text = Loc.F("diagnostics_version", version) + $"  ·  .NET {Environment.Version}  ·  x64";
        InterfacesText.Text = Loc.T("diagnostics_interfaces") + ": NVIDIA NVML, LibreHardwareMonitor (MPL-2.0), " +
                              "Windows PDH, NtQuerySystemInformation, GlobalMemoryStatusEx, ETW present events.";
    }

    private void WireSlider(Slider slider, TextBlock label, string suffix)
    {
        slider.ValueChanged += (_, _) =>
        {
            label.Text = slider.Value.ToString("0") + suffix;
            ApplyLive();
        };
    }

    private void AddColorRow(string locKey, string key, string fallback)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });

        var label = new TextBlock
        {
            Text = Loc.T(locKey),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var box = new TextBox { Text = fallback, VerticalAlignment = VerticalAlignment.Center };
        var swatch = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(2),
            BorderBrush = (Brush)FindResource("BorderStrongBrush"),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(ParseColor(fallback)),
        };

        var picker = new Button
        {
            Content = "…",
            Style = (Style)FindResource("FlatButton"),
            Width = 26,
            Height = 23,
            MinHeight = 0,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = Loc.F("color_title_for", Loc.T(locKey)),
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(picker, "ColorPicker_" + key);
        picker.Click += (_, _) =>
        {
            var picked = ColorPickerWindow.Pick(this, Loc.F("color_title_for", Loc.T(locKey)), box.Text);
            if (picked is { } color)
            {
                box.Text = ColorUtil.ToHex(color);
            }
        };

        box.TextChanged += (_, _) =>
        {
            if (!HexColor.IsMatch(box.Text))
            {
                return;
            }

            swatch.Background = new SolidColorBrush(ParseColor(box.Text));
            if (!_loading)
            {
                ApplyLive();
            }
        };

        Grid.SetColumn(box, 1);
        Grid.SetColumn(swatch, 2);
        Grid.SetColumn(picker, 3);
        grid.Children.Add(label);
        grid.Children.Add(box);
        grid.Children.Add(swatch);
        grid.Children.Add(picker);
        ColorRows.Children.Add(grid);

        _colorBoxes[key] = box;
        _colorSwatches[key] = swatch;
        _colorLabels[key] = label;
        _colorPickers[key] = picker;
    }

    // ------------------------------------------------------------------
    // Localized choices
    // ------------------------------------------------------------------
    private sealed class Choice<T>
    {
        public Choice(T value, string text)
        {
            Value = value;
            Text = text;
        }

        public T Value { get; }

        public string Text { get; }

        public override string ToString() => Text;
    }

    private static void SelectChoice<T>(ComboBox combo, T value)
    {
        foreach (var item in combo.Items)
        {
            if (item is Choice<T> choice && EqualityComparer<T>.Default.Equals(choice.Value, value))
            {
                combo.SelectedItem = choice;
                return;
            }
        }

        combo.SelectedIndex = -1;
    }

    private static T SelectedChoice<T>(ComboBox combo, T fallback)
        => combo.SelectedItem is Choice<T> choice ? choice.Value : fallback;

    private void BuildLogLevelChoices(LogLevel selected)
    {
        LogLevelCombo.Items.Clear();
        foreach (var level in new[] { LogLevel.Debug, LogLevel.Info, LogLevel.Warning, LogLevel.Error })
        {
            LogLevelCombo.Items.Add(new Choice<LogLevel>(level,
                Loc.T("loglevel_" + level.ToString().ToLowerInvariant())));
        }

        SelectChoice(LogLevelCombo, selected);
    }

    private void BuildFpsModeChoices(FpsSourceMode selected)
    {
        FpsModeCombo.Items.Clear();
        FpsModeCombo.Items.Add(new Choice<FpsSourceMode>(FpsSourceMode.Automatic, Loc.T("fps_mode_auto")));
        FpsModeCombo.Items.Add(new Choice<FpsSourceMode>(FpsSourceMode.Presented, Loc.T("fps_mode_presented")));
        FpsModeCombo.Items.Add(new Choice<FpsSourceMode>(FpsSourceMode.Displayed, Loc.T("fps_mode_displayed")));
        SelectChoice(FpsModeCombo, selected);
    }

    private void BuildFpsCaptureModeChoices(FpsCaptureMode selected)
    {
        FpsCaptureModeCombo.Items.Clear();
        FpsCaptureModeCombo.Items.Add(new Choice<FpsCaptureMode>(
            FpsCaptureMode.GameOnly, Loc.T("fps_capture_game_only")));
        FpsCaptureModeCombo.Items.Add(new Choice<FpsCaptureMode>(
            FpsCaptureMode.AllApplications, Loc.T("fps_capture_all_apps")));
        SelectChoice(FpsCaptureModeCombo, selected);
    }

    private void BuildRecordingChoices()
    {
        var settings = _runtime.Settings.Recording;
        RecordingFormatCombo.Items.Clear();
        RecordingFormatCombo.Items.Add(new Choice<RecordingFormat>(
            RecordingFormat.Csv, Loc.T("recording_format_csv")));
        RecordingFormatCombo.Items.Add(new Choice<RecordingFormat>(
            RecordingFormat.Txt, Loc.T("recording_format_txt")));
        RecordingFormatCombo.Items.Add(new Choice<RecordingFormat>(
            RecordingFormat.Json, Loc.T("recording_format_json")));
        RecordingFormatCombo.Items.Add(new Choice<RecordingFormat>(
            RecordingFormat.CsvJson, Loc.T("recording_format_csv_json")));
        SelectChoice(RecordingFormatCombo, settings.Format);

        RecordingIntervalCombo.Items.Clear();
        foreach (var interval in RecordingIntervals.Supported)
        {
            RecordingIntervalCombo.Items.Add(new Choice<int>(
                interval, Loc.F("recording_interval_value", interval)));
        }

        SelectChoice(RecordingIntervalCombo, RecordingIntervals.Normalize(settings.IntervalMs));
    }

    private void UpdateFpsCaptureModeUi()
    {
        if (FpsCaptureModeCombo is null || ChkMeasureWindowedApps is null)
        {
            return;
        }

        var allApplications = SelectedChoice(
            FpsCaptureModeCombo, _runtime.Settings.Fps.CaptureMode) == FpsCaptureMode.AllApplications;

        // All Applications already includes windowed apps. Keep the persisted Game Only value
        // untouched so switching back restores it; only the control is disabled.
        ChkMeasureWindowedApps.IsEnabled = !allApplications;
        ChkMeasureWindowedApps.ToolTip = Loc.T(allApplications
            ? "fps_measure_windowed_all_apps"
            : "fps_measure_windowed_hint");
    }

    private void BuildLanguageChoices(string selected)
    {
        LanguageCombo.Items.Clear();
        LanguageCombo.Items.Add(new Choice<string>("en", Loc.T("languages_english")));
        LanguageCombo.Items.Add(new Choice<string>("uk", Loc.T("languages_ukrainian")));
        SelectChoice(LanguageCombo, Loc.Normalize(selected));
    }

    private void BuildMonitorChoices(string? selectedDevice)
    {
        MonitorCombo.Items.Clear();
        MonitorCombo.Items.Add(new Choice<string>(string.Empty, Loc.T("overlay_monitor_auto")));

        var monitors = Monitors.All();
        foreach (var monitor in monitors)
        {
            var label = Loc.F("overlay_monitor_item", monitor.Index, monitor.Width, monitor.Height,
                monitor.Scale * 100);
            if (monitor.IsPrimary)
            {
                label += " · " + Loc.T("overlay_monitor_primary");
            }

            MonitorCombo.Items.Add(new Choice<string>(monitor.DeviceName, label));
        }

        SelectChoice(MonitorCombo, selectedDevice ?? string.Empty);
        if (MonitorCombo.SelectedIndex < 0)
        {
            // The stored monitor disappeared; fall back to automatic without rewriting the file.
            MonitorCombo.SelectedIndex = 0;
        }
    }

    // ------------------------------------------------------------------
    // Settings <-> UI
    // ------------------------------------------------------------------
    /// <summary>
    /// Classic keeps using the legacy OverlaySettings controls. Every other row style owns an
    /// independent OsdStyleSettings block, so edits to Surface/typography/colours must target that
    /// block instead of the global Classic settings.
    /// </summary>
    private static OsdStyleSettings? ActiveStyleSettings(AppSettings settings)
        => settings.Osd?.ActiveProfile is { } profile &&
           (profile.Style != OsdStyle.Classic || profile.IsTile)
            ? profile.StyleSettings
            : null;

    private void LoadFromSettings()
    {
        _loading = true;
        try
        {
            var settings = _runtime.Settings;
            var overlay = settings.Overlay;
            var style = ActiveStyleSettings(settings);

            ChkOverlayEnabled.IsChecked = overlay.Enabled;
            ChkClickThrough.IsChecked = overlay.ClickThrough;
            ChkBackground.IsChecked = style?.ShowBackground ?? overlay.ShowBackground;
            ChkShadow.IsChecked = style?.Shadow ?? overlay.Shadow;
            ChkLabels.IsChecked = style?.ShowLabels ?? overlay.ShowLabels;
            ChkUnits.IsChecked = style?.ShowUnits ?? overlay.ShowUnits;
            ChkSeparators.IsChecked = style?.ShowSeparators ?? overlay.ShowSeparators;
            ChkSparklines.IsChecked = style?.ShowSparklines ?? overlay.ShowSparklines;
            ChkWarnings.IsChecked = style?.HighlightWarnings ?? overlay.HighlightWarnings;

            _layoutMode = overlay.Layout;
            _anchor = overlay.Anchor;
            _themeMode = overlay.Theme;
            _appThemeMode = settings.Behavior.AppTheme;
            _customAccent = AppThemeCatalog.NormalizeAccent(settings.Behavior.AppAccentColor);
            SyncLayoutButtons();
            var tileProfile = settings.Osd?.ActiveProfile is { IsTile: true };
            LegacyLayoutSection.Visibility = tileProfile ? Visibility.Collapsed : Visibility.Visible;
            TileLayoutHint.Visibility = tileProfile ? Visibility.Visible : Visibility.Collapsed;
            SyncAnchorButtons();
            SyncThemeButtons();
            SyncAppThemeButtons();
            UpdateCustomAccentControls();
            AppThemeManager.Apply(_appThemeMode, _customAccent);

            OffsetXBox.Text = overlay.OffsetX.ToString();
            OffsetYBox.Text = overlay.OffsetY.ToString();

            var backgroundOpacity = style?.BackgroundOpacity ?? overlay.BackgroundOpacity;
            var cornerRadius = style?.CornerRadius ?? overlay.CornerRadius;
            var paddingX = style?.PaddingX ?? overlay.PaddingX;
            var paddingY = style?.PaddingY ?? overlay.PaddingY;
            var rowSpacing = style?.RowSpacing ?? overlay.RowSpacing;
            var columnSpacing = style?.ColumnSpacing ?? overlay.ColumnSpacing;

            SetColor("LabelColor", style?.LabelColor ?? overlay.LabelColor);
            SetColor("ValueColor", style?.ValueColor ?? overlay.ValueColor);
            SetColor("UnitColor", style?.UnitColor ?? overlay.UnitColor);
            SetColor("WarningColor", style?.WarningColor ?? overlay.WarningColor);
            SetColor("CriticalColor", style?.CriticalColor ?? overlay.CriticalColor);
            SetColor("BackgroundColor", style?.BackgroundColor ?? overlay.BackgroundColor);

            BgOpacitySlider.Value = backgroundOpacity;
            BgOpacityValue.Text = backgroundOpacity + "%";
            CornerSlider.Value = cornerRadius;
            CornerValue.Text = cornerRadius.ToString("0") + " px";
            PadXSlider.Value = paddingX;
            PadXValue.Text = paddingX.ToString("0") + " px";
            PadYSlider.Value = paddingY;
            PadYValue.Text = paddingY.ToString("0") + " px";
            RowGapSlider.Value = rowSpacing;
            RowGapValue.Text = rowSpacing.ToString("0") + " px";
            ColGapSlider.Value = columnSpacing;
            ColGapValue.Text = columnSpacing.ToString("0") + " px";

            var monitoring = settings.Monitoring;
            PollSlider.Value = monitoring.PollIntervalMs;
            PollValue.Text = monitoring.PollIntervalMs + " ms";
            RefreshSlider.Value = monitoring.OverlayRefreshHz;
            RefreshValue.Text = monitoring.OverlayRefreshHz + " Hz";
            ChkNvml.IsChecked = monitoring.EnableNvml;
            ChkLhm.IsChecked = monitoring.EnableLibreHardwareMonitor;
            ChkMsiAcpi.IsChecked = monitoring.EnableMsiAcpi;
            ChkMsiFanBroker.IsChecked = monitoring.EnableMsiFanBroker;
            ChkTzone.IsChecked = monitoring.UseThermalZoneAsCpuTemperature;
            ChkUnavailable.IsChecked = monitoring.ShowUnavailableMetrics;

            var behavior = settings.Behavior;
            // Reflect the real Windows state: a Run entry removed outside the app (or disabled in
            // Task Manager) must not look enabled. Portable profiles cannot manage it at all.
            ChkStartWithWindows.IsEnabled = _runtime.CanManageStartupRegistration;
            ChkStartWithWindows.IsChecked = _runtime.CanManageStartupRegistration
                ? StartupRegistration.IsActive()
                : behavior.StartWithWindows;
            ChkMinimizeToTray.IsChecked = behavior.MinimizeToTray;
            ChkCloseToTray.IsChecked = behavior.CloseToTray;
            ChkStartMinimized.IsChecked = behavior.StartMinimized;
            ChkHotkey.IsChecked = behavior.EnableGlobalHotkey;
            ChkTrayNotifications.IsChecked = behavior.ShowTrayNotifications;
            SelectChoice(LogLevelCombo, behavior.MinimumLogLevel);

            BuildLanguageChoices(behavior.Language);
            BuildMonitorChoices(overlay.MonitorDevice);
            BuildOsdProfileChoices();
            RebuildOverlayItems();

            ChkModifierDrag.IsChecked = overlay.ModifierDragEnabled;

            HotkeyBox.Text = behavior.HotkeyToggleOverlay;
            MainHotkeyBox.Text = behavior.HotkeyToggleMainWindow;
            StartRecordHotkeyBox.Text = behavior.HotkeyStartRecording;
            StopRecordHotkeyBox.Text = behavior.HotkeyStopRecording;
            RestartHotkeyBox.Text = behavior.HotkeyRestartApplication;
            UpdateHotkeyStatus();

            var recording = settings.Recording;
            BuildRecordingChoices();
            RecordingDirectoryBox.Text = recording.OutputDirectory;
            RecordingFileNameBox.Text = recording.FileName;
            ApplyRecordingSelection(recording.Sensors);

            FpsIntervalSlider.Value = settings.Fps.UpdateIntervalMs;
            FpsIntervalValue.Text = settings.Fps.UpdateIntervalMs + " ms";
            SelectChoice(FpsModeCombo, settings.Fps.Mode);
            SelectChoice(FpsCaptureModeCombo, settings.Fps.CaptureMode);
            ChkMeasureWindowedApps.IsChecked = settings.Fps.MeasureWindowedApps;
            UpdateFpsCaptureModeUi();

            RebuildMetricRows();
            if (_selectedMetricId is null && MetricSettingsHint is not null)
            {
                MetricSettingsHint.Text = Loc.T("metric_settings_select_hint");
            }

            UpdatePositionInfo();
        }
        finally
        {
            _loading = false;
        }

        SetFpsInfo(false);
        UpdateFpsStatus();
        UpdateStatus(force: true);
        UpdateOverlaySummary();
        UpdatePreview();
    }

    private void BuildOsdProfileChoices()
    {
        if (OsdProfileCombo is null)
        {
            return;
        }

        var activeId = _runtime.Settings.Osd?.ActiveProfileId;
        OsdProfileCombo.Items.Clear();
        AddProfileLibraryHeader(Loc.T("osd_library_hardwareosd"));
        foreach (var profile in _runtime.Settings.Osd?.Profiles ?? new List<OsdProfile>())
        {
            if (profile.Library == OsdDesignLibrary.HardwareOsd)
            {
                OsdProfileCombo.Items.Add(ProfileItem(profile));
            }
        }

        AddProfileLibraryHeader(Loc.T("osd_library_capframex"));
        foreach (var profile in _runtime.Settings.Osd?.Profiles ?? new List<OsdProfile>())
        {
            if (profile.Library == OsdDesignLibrary.CapFrameX &&
                !OsdLibraryFilter.IsHiddenFromLibrary(profile, activeId))
            {
                OsdProfileCombo.Items.Add(ProfileItem(profile));
            }
        }

        var selected = OsdProfileCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag is string id &&
                                    string.Equals(id, activeId, StringComparison.Ordinal));
        OsdProfileCombo.SelectedItem = selected ?? OsdProfileCombo.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }


    private void AddProfileLibraryHeader(string text)
    {
        OsdProfileCombo.Items.Add(new ComboBoxItem
        {
            Content = "— " + text + " —",
            IsEnabled = false,
            FontWeight = FontWeights.SemiBold,
        });
    }

    private static ComboBoxItem ProfileItem(OsdProfile profile) => new()
    {
        Content = (profile.Layout == OsdProfileLayout.Tile ? "Tile · " : "Row · ") + profile.Name,
        Tag = profile.Id,
    };

    private void OsdProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _selfApplying || OsdProfileCombo.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string id ||
            string.Equals(id, _runtime.Settings.Osd.ActiveProfileId, StringComparison.Ordinal))
        {
            return;
        }

        var settings = _runtime.Settings.Clone();
        settings.Osd.ActiveProfileId = id;
        _selfApplying = true;
        try
        {
            _runtime.ApplySettings(settings, persist: true);
        }
        finally
        {
            _selfApplying = false;
        }

        // Reload the appearance controls from the newly active profile. Without this, the
        // Surface/typography controls keep the previous style's values and the next edit
        // overwrites the newly selected style with stale data.
        LoadFromSettings();
        UpdatePreview();
        UpdateOverlaySummary();
        UpdateStatus(force: true);
    }

    private void OpenOsdBuilder_Click(object sender, RoutedEventArgs e)
    {
        var active = _runtime.Settings.Osd?.ActiveProfile;
        if (active is { IsTile: false })
        {
            // 0.26.0: the five original profiles are tile documents. This fallback remains
            // only for the three preserved CapFrameX row templates.
            var rowsWindow = new ClassicRowsWindow(_runtime, active.Id) { Owner = this };
            rowsWindow.ShowDialog();
        }
        else
        {
            var window = new OverlayBuilderWindow(_runtime) { Owner = this };
            window.ShowDialog();
        }

        LoadFromSettings();
    }

    private void OpenProcessSelection_Click(object sender, RoutedEventArgs e)
    {
        var window = new ProcessSelectionWindow(_runtime) { Owner = this };
        window.ShowDialog();
        LoadFromSettings();
    }

    private void FpsInfoToggle_Click(object sender, RoutedEventArgs e) => SetFpsInfo(!_fpsInfoExpanded);

    private void SetFpsInfo(bool expanded)
    {
        _fpsInfoExpanded = expanded;
        if (FpsHintText is not null)
        {
            FpsHintText.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        }

        if (FpsDetailText is not null)
        {
            FpsDetailText.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        }

        if (FpsCaptureModeHint is not null)
        {
            FpsCaptureModeHint.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void SetColor(string key, string value)
    {
        if (_colorBoxes.TryGetValue(key, out var box))
        {
            box.Text = value;
        }

        if (_colorSwatches.TryGetValue(key, out var swatch))
        {
            swatch.Background = new SolidColorBrush(ParseColor(value));
        }
    }

    private AppSettings CollectSettings()
    {
        var settings = _runtime.Settings.Clone();
        var overlay = settings.Overlay;
        var style = ActiveStyleSettings(settings);

        overlay.Enabled = ChkOverlayEnabled.IsChecked == true;
        overlay.ClickThrough = ChkClickThrough.IsChecked == true;
        overlay.Layout = _layoutMode;
        overlay.Anchor = _anchor;
        overlay.Theme = _themeMode;
        overlay.MonitorDevice = SelectedChoice(MonitorCombo, overlay.MonitorDevice);

        if (int.TryParse(OffsetXBox.Text, out var offsetX))
        {
            overlay.OffsetX = offsetX;
        }

        if (int.TryParse(OffsetYBox.Text, out var offsetY))
        {
            overlay.OffsetY = offsetY;
        }

        var showBackground = ChkBackground.IsChecked == true;
        var shadow = ChkShadow.IsChecked == true;
        var showLabels = ChkLabels.IsChecked == true;
        var showUnits = ChkUnits.IsChecked == true;
        var showSeparators = ChkSeparators.IsChecked == true;
        var showSparklines = ChkSparklines.IsChecked == true;
        var highlightWarnings = ChkWarnings.IsChecked == true;
        var fontSize = style?.FontSize ?? overlay.FontSize;
        var fontFamily = style?.FontFamily ?? overlay.FontFamily;
        var labelColor = ValidColor("LabelColor", style?.LabelColor ?? overlay.LabelColor);
        var valueColor = ValidColor("ValueColor", style?.ValueColor ?? overlay.ValueColor);
        var unitColor = ValidColor("UnitColor", style?.UnitColor ?? overlay.UnitColor);
        var warningColor = ValidColor("WarningColor", style?.WarningColor ?? overlay.WarningColor);
        var criticalColor = ValidColor("CriticalColor", style?.CriticalColor ?? overlay.CriticalColor);
        var backgroundColor = ValidColor("BackgroundColor", style?.BackgroundColor ?? overlay.BackgroundColor);
        var backgroundOpacity = (int)Math.Round(BgOpacitySlider.Value);
        var cornerRadius = Math.Round(CornerSlider.Value);
        var paddingX = Math.Round(PadXSlider.Value);
        var paddingY = Math.Round(PadYSlider.Value);
        var rowSpacing = Math.Round(RowGapSlider.Value);
        var columnSpacing = Math.Round(ColGapSlider.Value);

        if (style is null)
        {
            overlay.ShowBackground = showBackground;
            overlay.Shadow = shadow;
            overlay.ShowLabels = showLabels;
            overlay.ShowUnits = showUnits;
            overlay.ShowSeparators = showSeparators;
            overlay.ShowSparklines = showSparklines;
            overlay.HighlightWarnings = highlightWarnings;
            overlay.FontSize = fontSize;
            overlay.FontFamily = fontFamily;
            overlay.LabelColor = labelColor;
            overlay.ValueColor = valueColor;
            overlay.UnitColor = unitColor;
            overlay.WarningColor = warningColor;
            overlay.CriticalColor = criticalColor;
            overlay.BackgroundColor = backgroundColor;
            overlay.BackgroundOpacity = backgroundOpacity;
            overlay.CornerRadius = cornerRadius;
            overlay.PaddingX = paddingX;
            overlay.PaddingY = paddingY;
            overlay.RowSpacing = rowSpacing;
            overlay.ColumnSpacing = columnSpacing;

            // Classic still renders from OverlaySettings; keep its profile style block in sync so
            // the builder sees the same values and a later style switch does not lose them.
            if (settings.Osd?.ActiveProfile is { Style: OsdStyle.Classic } classic)
            {
                var synced = OsdStyleSettings.FromOverlay(overlay);
                // Preserve builder-only fields that the legacy appearance controls do not expose.
                synced.LabelScale = classic.StyleSettings.LabelScale;
                synced.UnitScale = classic.StyleSettings.UnitScale;
                synced.HeaderColor = classic.StyleSettings.HeaderColor;
                synced.TextColor = classic.StyleSettings.TextColor;
                synced.NormalColor = classic.StyleSettings.NormalColor;
                synced.SeparatorColor = classic.StyleSettings.SeparatorColor;
                synced.UseUpperLabels = classic.StyleSettings.UseUpperLabels;
                classic.StyleSettings = synced;
            }
        }
        else
        {
            style.ShowBackground = showBackground;
            style.Shadow = shadow;
            style.ShowLabels = showLabels;
            style.ShowUnits = showUnits;
            style.ShowSeparators = showSeparators;
            style.ShowSparklines = showSparklines;
            style.HighlightWarnings = highlightWarnings;
            style.FontSize = fontSize;
            style.FontFamily = fontFamily;
            style.LabelColor = labelColor;
            style.ValueColor = valueColor;
            style.UnitColor = unitColor;
            style.WarningColor = warningColor;
            style.CriticalColor = criticalColor;
            style.BackgroundColor = backgroundColor;
            style.BackgroundOpacity = backgroundOpacity;
            style.CornerRadius = cornerRadius;
            style.PaddingX = paddingX;
            style.PaddingY = paddingY;
            style.RowSpacing = rowSpacing;
            style.ColumnSpacing = columnSpacing;
        }
        if (settings.Osd?.ActiveProfile is { IsTile: true } tileProfile)
        {
            ApplyOverlayAppearanceToTile(tileProfile, showBackground, labelColor, valueColor,
                unitColor, backgroundColor, backgroundOpacity, cornerRadius, paddingX, rowSpacing);
        }

        overlay.ModifierDragEnabled = ChkModifierDrag.IsChecked == true;

        settings.Monitoring.PollIntervalMs = (int)Math.Round(PollSlider.Value / 100.0) * 100;
        settings.Monitoring.OverlayRefreshHz = (int)Math.Round(RefreshSlider.Value);
        settings.Monitoring.EnableNvml = ChkNvml.IsChecked == true;
        settings.Monitoring.EnableLibreHardwareMonitor = ChkLhm.IsChecked == true;
        settings.Monitoring.EnableMsiAcpi = ChkMsiAcpi.IsChecked == true;
        settings.Monitoring.EnableMsiFanBroker = ChkMsiFanBroker.IsChecked == true;
        settings.Monitoring.UseThermalZoneAsCpuTemperature = ChkTzone.IsChecked == true;
        settings.Monitoring.ShowUnavailableMetrics = ChkUnavailable.IsChecked == true;

        settings.Fps.UpdateIntervalMs = (int)Math.Round(FpsIntervalSlider.Value / 10.0) * 10;
        settings.Fps.Mode = SelectedChoice(FpsModeCombo, settings.Fps.Mode);
        settings.Fps.CaptureMode = SelectedChoice(FpsCaptureModeCombo, settings.Fps.CaptureMode);
        settings.Fps.MeasureWindowedApps = ChkMeasureWindowedApps.IsChecked == true;

        settings.Behavior.StartWithWindows = ChkStartWithWindows.IsChecked == true;
        settings.Behavior.MinimizeToTray = ChkMinimizeToTray.IsChecked == true;
        settings.Behavior.CloseToTray = ChkCloseToTray.IsChecked == true;
        settings.Behavior.StartMinimized = ChkStartMinimized.IsChecked == true;
        settings.Behavior.EnableGlobalHotkey = ChkHotkey.IsChecked == true;
        settings.Behavior.ShowTrayNotifications = ChkTrayNotifications.IsChecked == true;
        settings.Behavior.HotkeyStartRecording = StartRecordHotkeyBox.Text;
        settings.Behavior.HotkeyStopRecording = StopRecordHotkeyBox.Text;
        settings.Behavior.HotkeyRestartApplication = RestartHotkeyBox.Text;
        settings.Behavior.Language = SelectedChoice(LanguageCombo, settings.Behavior.Language);
        settings.Behavior.AppTheme = _appThemeMode;
        settings.Behavior.AppAccentColor = _customAccent;
        settings.Behavior.MinimumLogLevel = SelectedChoice(LogLevelCombo, settings.Behavior.MinimumLogLevel);

        var recording = settings.Recording;
        recording.Format = SelectedChoice(RecordingFormatCombo, recording.Format);
        recording.IntervalMs = SelectedChoice(RecordingIntervalCombo, recording.IntervalMs);
        recording.OutputDirectory = RecordingDirectoryBox.Text.Trim();
        recording.FileName = RecordingFileNameBox.Text.Trim();
        if (_recordingChecks.Count > 0 && !_recordingSensorSync)
        {
            recording.Sensors = _recordingChecks
                .Where(pair => pair.Value.IsChecked == true)
                .Select(pair => pair.Key)
                .ToList();
        }

        settings.Metrics = _runtime.Settings.Metrics
            .Select(m => new MetricPreference
            {
                Id = m.Id,
                Visible = _metricChecks.TryGetValue(m.Id, out var check) && check.IsChecked == true,
            })
            .ToList();

        return settings;
    }

    private string ValidColor(string key, string fallback)
        => _colorBoxes.TryGetValue(key, out var box) && HexColor.IsMatch(box.Text) ? box.Text : fallback;

    private static Color ParseColor(string hex)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex.StartsWith('#') ? hex : "#" + hex);
        }
        catch
        {
            return Colors.White;
        }
    }

    private void ApplyMetricVisibility(string id, bool visible)
    {
        if (_loading || MetricCatalog.Find(id) is not { } definition)
        {
            return;
        }

        var settings = _runtime.Settings.Clone();
        settings.SetMetricVisible(id, visible);
        var profile = settings.Osd.ActiveProfile;
        if (profile is { IsTile: true })
        {
            OsdMetricBridge.SetTileMetricVisible(profile, definition, visible,
                materialiseWhenMissing: true);
        }
        else if (profile is not null &&
                 !(profile.Style == OsdStyle.Classic && profile.FollowMetricPreferences))
        {
            OsdMetricBridge.SetMetricVisible(profile, definition, visible, materialiseWhenMissing: true);
        }

        _selfApplying = true;
        try
        {
            _runtime.ApplySettings(settings, persist: true);
        }
        catch (Exception ex)
        {
            FooterStatus.Text = Loc.F("msg_apply_failed", ex.Message);
        }
        finally
        {
            _selfApplying = false;
        }

        if (_metricChecks.TryGetValue(id, out var check) && check.IsChecked != visible)
        {
            _loading = true;
            try
            {
                check.IsChecked = visible;
            }
            finally
            {
                _loading = false;
            }
        }

        RebuildOverlayItems();
        UpdatePreview();
        UpdateStatus(force: true);
    }

    /// <summary>
    /// Keeps the legacy Overlay colour/surface controls meaningful for tile profiles by applying
    /// them to the document theme and to widgets that still use the old theme colour. Per-tile
    /// colours that differ from the theme are preserved.
    /// </summary>
    private static void ApplyOverlayAppearanceToTile(OsdProfile profile, bool showBackground,
        string labelColor, string valueColor, string unitColor, string backgroundColor,
        int backgroundOpacity, double cornerRadius, double paddingX, double rowSpacing)
    {
        var document = profile.TileDesign?.Clone();
        if (document is null)
        {
            return;
        }

        var oldAccent = document.Theme.Accent;
        var oldText = document.Theme.Text;
        var oldSubtext = document.Theme.Subtext;
        var oldPanel = document.Theme.Panel;
        var oldRootBackground = document.Root.BackgroundColor ?? document.Root.Color;
        var alpha = (byte)Math.Clamp(Math.Round(backgroundOpacity * 255.0 / 100.0), 0, 255);
        var panel = TileColor.Parse(backgroundColor, TileColor.Transparent).WithAlpha(alpha).ToHex();
        document.Theme.Accent = labelColor;
        document.Theme.Text = valueColor;
        document.Theme.Subtext = unitColor;
        document.Theme.Panel = showBackground ? panel : "#00000000";
        document.Theme.Radius = cornerRadius;
        document.Theme.Pad = paddingX;
        document.Theme.Gap = rowSpacing;
        document.Root.Background = showBackground;
        document.Root.BackgroundColor = showBackground ? panel : null;
        document.Root.Color = showBackground ? panel : null;
        document.Root.Radius = cornerRadius;

        foreach (var node in document.Root.Descendants())
        {
            if (node.NodeKind == TileNodeKind.Panel)
            {
                if (node.Background == true &&
                    (string.IsNullOrWhiteSpace(node.Color) || SameColor(node.Color, oldPanel) ||
                     SameColor(node.Color, oldRootBackground)))
                {
                    node.Color = showBackground ? panel : "#00000000";
                }

                if (!string.IsNullOrWhiteSpace(node.BackgroundColor) &&
                    (SameColor(node.BackgroundColor, oldPanel) ||
                     SameColor(node.BackgroundColor, oldRootBackground)))
                {
                    node.BackgroundColor = showBackground ? panel : null;
                }
            }

            if (string.IsNullOrWhiteSpace(node.LabelColor) || SameColor(node.LabelColor, oldAccent))
            {
                node.LabelColor = labelColor;
            }

            if (string.IsNullOrWhiteSpace(node.ValueColor) || SameColor(node.ValueColor, oldText))
            {
                node.ValueColor = valueColor;
            }

            if (string.IsNullOrWhiteSpace(node.UnitColor) || SameColor(node.UnitColor, oldSubtext))
            {
                node.UnitColor = unitColor;
            }

            if (node.NodeKind == TileNodeKind.Text &&
                (string.IsNullOrWhiteSpace(node.Color) || SameColor(node.Color, oldText) ||
                 SameColor(node.Color, oldAccent)))
            {
                node.Color = valueColor;
            }
        }

        profile.SetTileDesign(document);
    }

    private static bool SameColor(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
           string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool FollowsMetricMatrix(OsdProfile? profile)
        => profile is { Style: OsdStyle.Classic, FollowMetricPreferences: true };

    private void OnStartWithWindowsChanged()
    {
        if (_loading || _selfApplying)
        {
            return;
        }

        _runtime.SetStartWithWindows(ChkStartWithWindows.IsChecked == true);
        ApplyLive();
    }

    private void ApplyLive()
    {
        if (_loading)
        {
            return;
        }

        _selfApplying = true;
        try
        {
            _runtime.ApplySettings(CollectSettings(), persist: true);
        }
        catch (Exception ex)
        {
            FooterStatus.Text = Loc.F("msg_apply_failed", ex.Message);
        }
        finally
        {
            _selfApplying = false;
        }

        UpdatePreview();
        UpdateOverlaySummary();
        UpdatePositionInfo();
        UpdateStatus(force: true);
        UpdateHotkeyStatus();
    }

    private void SyncLayoutButtons()
    {
        _syncingButtons = true;
        try
        {
            foreach (var button in _layoutButtons)
            {
                button.IsChecked = (OverlayLayoutMode)button.Tag == _layoutMode;
            }
        }
        finally
        {
            _syncingButtons = false;
        }
    }

    private void SyncAnchorButtons()
    {
        _syncingButtons = true;
        try
        {
            foreach (var button in _anchorButtons)
            {
                button.IsChecked = (OverlayAnchor)button.Tag == _anchor;
            }
        }
        finally
        {
            _syncingButtons = false;
        }
    }

    private void SyncThemeButtons()
    {
        _syncingButtons = true;
        try
        {
            foreach (var button in _themeButtons)
            {
                button.IsChecked = (OverlayThemeMode)button.Tag == _themeMode;
            }
        }
        finally
        {
            _syncingButtons = false;
        }
    }

    private void SyncAppThemeButtons()
    {
        _syncingButtons = true;
        try
        {
            foreach (var button in _appThemeButtons)
            {
                button.IsChecked = (AppThemeMode)button.Tag == _appThemeMode;
            }
        }
        finally
        {
            _syncingButtons = false;
        }
    }

    private void ApplyThemeMode(OverlayThemeMode mode)
    {
        var settings = _runtime.Settings.Clone();
        OverlayThemePresets.Apply(settings, mode);
        if (settings.Osd?.ActiveProfile is { IsTile: true } tile)
        {
            ApplyOverlayAppearanceToTile(tile, settings.Overlay.ShowBackground,
                settings.Overlay.LabelColor, settings.Overlay.ValueColor,
                settings.Overlay.UnitColor, settings.Overlay.BackgroundColor,
                settings.Overlay.BackgroundOpacity, settings.Overlay.CornerRadius,
                settings.Overlay.PaddingX, settings.Overlay.RowSpacing);
        }

        _selfApplying = true;
        try
        {
            _runtime.ApplySettings(settings, persist: true);
        }
        finally
        {
            _selfApplying = false;
        }

        LoadFromSettings();
        FooterStatus.Text = Loc.F("appearance_theme_applied", ThemeName(mode));
    }

    private void ApplyBackgroundPreset(OverlayThemePresets.BackgroundPreset preset)
    {
        var settings = _runtime.Settings.Clone();
        OverlayThemePresets.ApplyBackground(settings.Overlay, preset);
        if (settings.Osd?.ActiveProfile is { IsTile: true } tile)
        {
            ApplyOverlayAppearanceToTile(tile, settings.Overlay.ShowBackground,
                settings.Overlay.LabelColor, settings.Overlay.ValueColor,
                settings.Overlay.UnitColor, settings.Overlay.BackgroundColor,
                settings.Overlay.BackgroundOpacity, settings.Overlay.CornerRadius,
                settings.Overlay.PaddingX, settings.Overlay.RowSpacing);
        }

        _selfApplying = true;
        try
        {
            _runtime.ApplySettings(settings, persist: true);
        }
        finally
        {
            _selfApplying = false;
        }

        LoadFromSettings();
        FooterStatus.Text = Loc.F("appearance_preset_applied", Loc.T(PresetKey(preset.Name)));
    }

    private void BuildCustomAccentPalette()
    {
        CustomAccentPalette.Children.Clear();
        foreach (var hex in AppThemeCatalog.AccentPresets)
        {
            var color = ParseColor(hex);
            var chip = new Border
            {
                Width = 28,
                Height = 22,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(color),
                BorderBrush = (Brush)FindResource("BorderStrongBrush"),
                BorderThickness = new Thickness(1),
            };
            chip.SetResourceReference(Border.BorderBrushProperty, "BorderStrongBrush");
            var swatch = new Button
            {
                Width = 30,
                Height = 24,
                Margin = new Thickness(0, 0, 5, 5),
                Padding = new Thickness(0),
                MinHeight = 0,
                Style = (Style)FindResource("FlatButton"),
                Content = chip,
                ToolTip = hex,
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = hex,
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(swatch,
                "CustomAccent_" + hex.TrimStart('#'));
            System.Windows.Automation.AutomationProperties.SetName(swatch, hex);
            swatch.Click += (_, _) => ApplyCustomAccent(color, selectTheme: true);
            CustomAccentPalette.Children.Add(swatch);
        }
    }

    private void PickCustomAccent()
    {
        var picked = ColorPickerWindow.Pick(this, Loc.T("appearance_custom_accent"), _customAccent);
        if (picked is { } color)
        {
            ApplyCustomAccent(color, selectTheme: true);
        }
    }

    private void ApplyCustomAccent(Color color, bool selectTheme)
    {
        _customAccent = AppThemeCatalog.NormalizeAccent(ColorUtil.ToHex(color));
        if (selectTheme)
        {
            _appThemeMode = AppThemeMode.Custom;
        }

        SyncAppThemeButtons();
        UpdateCustomAccentControls();
        AppThemeManager.Apply(_appThemeMode, _customAccent);
        ApplyTitleBarTheme();
        ApplyLive();
        FooterStatus.Text = Loc.F("appearance_custom_applied", _customAccent);
    }

    private void UpdateCustomAccentControls()
    {
        if (CustomAccentBox is null)
        {
            return;
        }

        CustomAccentBox.Text = _customAccent;
        CustomAccentSwatch.Background = new SolidColorBrush(ParseColor(_customAccent));
    }

    private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var language = SelectedChoice(LanguageCombo, _runtime.Settings.Behavior.Language);
        if (string.Equals(language, _runtime.Settings.Behavior.Language, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var settings = _runtime.Settings.Clone();
        settings.Behavior.Language = language;
        _selfApplying = true;
        try
        {
            _runtime.ApplySettings(settings, persist: true);
        }
        finally
        {
            _selfApplying = false;
        }

        FooterStatus.Text = Loc.T("msg_language_changed");
    }

    // ------------------------------------------------------------------
    // Metrics list
    // ------------------------------------------------------------------
    private void BuildMetricCategoryChoices()
    {
        if (MetricCategoryCombo is null)
        {
            return;
        }

        MetricCategoryCombo.Items.Clear();
        foreach (var (key, tag) in new[]
                 {
                     ("metrics_category_all", "all"),
                     ("group_cpu", "cpu"),
                     ("group_gpu", "gpu"),
                     ("group_vram", "vram"),
                     ("group_ram", "ram"),
                     ("group_system", "system"),
                     ("group_frame", "fps"),
                     ("metrics_category_fans", "fans"),
                     ("metrics_category_power", "power"),
                     ("metrics_category_temperature", "temperature"),
                     ("metrics_category_clock", "clock"),
                 })
        {
            MetricCategoryCombo.Items.Add(new ComboBoxItem { Content = Loc.T(key), Tag = tag });
        }

        MetricCategoryCombo.SelectedIndex = 0;
    }

    private void MetricSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyMetricFilter();

    private void MetricCategoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
        {
            ApplyMetricFilter();
        }
    }

    private void ApplyMetricFilter()
    {
        var search = MetricSearchBox?.Text?.Trim() ?? string.Empty;
        var category = (MetricCategoryCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        foreach (var (id, border) in _metricRowBorders)
        {
            var definition = MetricCatalog.Find(id);
            if (definition is null)
            {
                border.Visibility = Visibility.Collapsed;
                continue;
            }

            var textMatch = search.Length == 0 ||
                            MetricName(definition).Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            id.Contains(search, StringComparison.OrdinalIgnoreCase);
            border.Visibility = textMatch && MatchesMetricCategory(id, definition, category)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private static bool MatchesMetricCategory(string id, MetricDefinition definition, string category)
        => category switch
        {
            "all" => true,
            "cpu" => definition.Group == MetricGroup.Cpu,
            "gpu" => definition.Group == MetricGroup.Gpu,
            "vram" => definition.Group == MetricGroup.Vram,
            "ram" => definition.Group == MetricGroup.Ram,
            "system" => definition.Group == MetricGroup.System,
            "fps" => definition.Group == MetricGroup.Frame,
            "fans" => id.Contains("fan", StringComparison.OrdinalIgnoreCase),
            "power" => id.Contains("power", StringComparison.OrdinalIgnoreCase),
            "temperature" => id.Contains("temp", StringComparison.OrdinalIgnoreCase),
            "clock" => id.Contains("clock", StringComparison.OrdinalIgnoreCase),
            _ => true,
        };

    private void SelectMetricForSettings(string id)
    {
        var definition = MetricCatalog.Find(id);
        if (definition is null)
        {
            return;
        }

        _selectedMetricId = id;
        BuildMetricSettingsFields();
        MetricSettingsTitle.Text = MetricName(definition);
        var profile = _runtime.Settings.Osd?.ActiveProfile;
        var element = FindOsdElement(id);
        var tileNodes = profile is { IsTile: true }
            ? OsdMetricBridge.FindTileNodes(profile, definition)
            : Array.Empty<HardwareOSD.Core.TileOsd.TileNode>();
        var tileNode = tileNodes.FirstOrDefault();
        MetricSettingsHint.Text = profile is { IsTile: true }
            ? (tileNodes.Count == 0
                ? Loc.T("metric_settings_not_in_osd")
                : Loc.F("osd_sensor_id", tileNode?.Key ?? definition.Id))
            : element is null
                ? Loc.T("metric_settings_not_in_osd")
                : Loc.F("osd_sensor_id", element.SensorId);
        var tileChart = tileNodes.FirstOrDefault(node => node.NodeKind == HardwareOSD.Core.TileOsd.TileNodeKind.Chart);
        var tileProgress = tileNodes.FirstOrDefault(node =>
            node.NodeKind is HardwareOSD.Core.TileOsd.TileNodeKind.Bar or HardwareOSD.Core.TileOsd.TileNodeKind.Gauge);
        var tileVisible = profile is { IsTile: true }
            ? OsdMetricBridge.IsTileMetricVisible(profile, definition)
            : _runtime.Settings.IsMetricVisible(id);
        _metricVisibleCheck!.IsChecked = tileVisible;
        _metricNameBox!.Text = profile is { IsTile: true }
            ? tileNode?.Label ?? MetricName(definition)
            : element?.Label ?? MetricName(definition);
        _metricUnitBox!.Text = profile is { IsTile: true }
            ? tileNode?.Unit ?? definition.Unit
            : element?.Unit ?? string.Empty;
        _metricDecimalsBox!.Text = profile is { IsTile: true }
            ? (tileNode?.Digits ?? -1).ToString(CultureInfo.InvariantCulture)
            : (element?.Decimals ?? -1).ToString(CultureInfo.InvariantCulture);
        _metricWarningBox!.Text = profile is { IsTile: true }
            ? tileNode?.WarningThreshold?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty
            : element?.WarningThreshold?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty;
        _metricCriticalBox!.Text = profile is { IsTile: true }
            ? tileNode?.CriticalThreshold?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty
            : element?.CriticalThreshold?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty;
        _metricColorBox!.Text = profile is { IsTile: true }
            ? tileNode?.ValueColor ?? string.Empty
            : element?.ValueColor ?? string.Empty;
        _metricGraphColorBox!.Text = profile is { IsTile: true }
            ? tileNode?.GraphColor ?? tileChart?.Color ?? string.Empty
            : element?.GraphColor ?? string.Empty;
        _metricGraphThicknessBox!.Text = (profile is { IsTile: true }
                ? tileNode?.Thickness ?? tileChart?.Thickness ?? 1.3
                : element?.GraphThickness ?? 1.3)
            .ToString("0.##", CultureInfo.InvariantCulture);
        _metricGraphHistoryBox!.Text = (profile is { IsTile: true }
                ? tileNode?.HistoryPoints ?? tileChart?.HistoryPoints ?? 120
                : element?.GraphHistoryPoints ?? 120)
            .ToString(CultureInfo.InvariantCulture);
        _metricGraphMinBox!.Text = (profile is { IsTile: true } ? tileProgress?.Minimum : element?.GraphScaleMin)
            ?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty;
        _metricGraphMaxBox!.Text = (profile is { IsTile: true } ? tileProgress?.Maximum : element?.GraphScaleMax)
            ?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty;
        _metricGraphCheck!.IsChecked = profile is { IsTile: true }
            ? tileNode?.MiniGraph == true || tileChart is { Visible: not false }
            : element?.ShowGraph ?? false;
        _metricProgressCheck!.IsChecked = profile is { IsTile: true }
            ? tileProgress is { Visible: not false }
            : element?.ShowProgress ?? false;
        _metricAutoColorCheck!.IsChecked = element?.AutoColor ?? false;
    }

    private void BuildMetricSettingsFields()
    {
        if (MetricSettingsFields is null || _metricNameBox is not null)
        {
            return;
        }

        (_metricNameBox, var nameRow) = BuildMetricTextField("osd_display_name");
        (_metricUnitBox, var unitRow) = BuildMetricTextField("osd_unit");
        (_metricDecimalsBox, var decimalsRow) = BuildMetricTextField("osd_decimals");
        (_metricWarningBox, var warningRow) = BuildMetricTextField("osd_warning_threshold");
        (_metricCriticalBox, var criticalRow) = BuildMetricTextField("osd_critical_threshold");
        (_metricColorBox, var colorRow) = BuildMetricTextField("osd_value_color");
        (_metricGraphColorBox, var graphColorRow) = BuildMetricTextField("osd_graph_color");
        (_metricGraphThicknessBox, var graphThicknessRow) = BuildMetricTextField("osd_graph_thickness");
        (_metricGraphHistoryBox, var graphHistoryRow) = BuildMetricTextField("osd_graph_history");
        (_metricGraphMinBox, var graphMinRow) = BuildMetricTextField("osd_graph_min");
        (_metricGraphMaxBox, var graphMaxRow) = BuildMetricTextField("osd_graph_max");
        MetricSettingsFields.Children.Add(nameRow);
        MetricSettingsFields.Children.Add(unitRow);
        MetricSettingsFields.Children.Add(decimalsRow);
        MetricSettingsFields.Children.Add(warningRow);
        MetricSettingsFields.Children.Add(criticalRow);
        MetricSettingsFields.Children.Add(colorRow);
        MetricSettingsFields.Children.Add(graphColorRow);
        MetricSettingsFields.Children.Add(graphThicknessRow);
        MetricSettingsFields.Children.Add(graphHistoryRow);
        MetricSettingsFields.Children.Add(graphMinRow);
        MetricSettingsFields.Children.Add(graphMaxRow);
        _metricVisibleCheck = new CheckBox { Content = Loc.T("osd_visible"), Margin = new Thickness(0, 3, 0, 0) };
        _metricGraphCheck = new CheckBox { Content = Loc.T("osd_show_graph"), Margin = new Thickness(0, 3, 0, 0) };
        _metricProgressCheck = new CheckBox { Content = Loc.T("osd_show_progress"), Margin = new Thickness(0, 3, 0, 0) };
        _metricAutoColorCheck = new CheckBox { Content = Loc.T("osd_auto_color"), Margin = new Thickness(0, 3, 0, 0) };
        MetricSettingsFields.Children.Add(_metricVisibleCheck);
        MetricSettingsFields.Children.Add(_metricGraphCheck);
        MetricSettingsFields.Children.Add(_metricProgressCheck);
        MetricSettingsFields.Children.Add(_metricAutoColorCheck);
    }

    private (TextBox Box, Grid Row) BuildMetricTextField(string labelKey)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock
        {
            Text = Loc.T(labelKey),
            Style = (Style)FindResource("FieldLabel"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });
        var box = new TextBox { VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetAutomationId(box, "MetricSetting_" + labelKey);
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);
        return (box, grid);
    }

    private OsdElement? FindOsdElement(string metricId)
    {
        var profile = _runtime.Settings.Osd?.ActiveProfile;
        if (profile is null)
        {
            return null;
        }

        var definition = MetricCatalog.Find(metricId);
        var rawId = definition is null ? null : MetricCatalog.SensorIdFor(definition);
        return OsdProfile.Flatten(profile.Elements)
            .FirstOrDefault(element => element.SensorId == metricId ||
                                      (rawId is not null && element.SensorId == rawId));
    }

    private void MetricSettingsApply_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMetricId is null)
        {
            return;
        }

        var settings = _runtime.Settings.Clone();
        var profile = settings.Osd.ActiveProfile;
        if (profile is null)
        {
            return;
        }

        var definition = MetricCatalog.Find(_selectedMetricId);
        if (definition is null)
        {
            return;
        }

        if (profile.IsTile)
        {
            var tileVisible = _metricVisibleCheck?.IsChecked == true;
            var tileDecimals = int.TryParse(_metricDecimalsBox?.Text, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var parsedDecimals)
                ? Math.Clamp(parsedDecimals, -1, 6)
                : -1;
            var tileValueColor = HexColor.IsMatch(_metricColorBox?.Text ?? string.Empty)
                ? AppSettings.SanitizeColor(_metricColorBox!.Text, string.Empty)
                : null;
            var tileGraphColor = HexColor.IsMatch(_metricGraphColorBox?.Text ?? string.Empty)
                ? AppSettings.SanitizeColor(_metricGraphColorBox!.Text, string.Empty)
                : null;
            var tileWarning = ParseNullableDouble(_metricWarningBox?.Text);
            var tileCritical = ParseNullableDouble(_metricCriticalBox?.Text);
            double? tileGraphThickness = double.TryParse(_metricGraphThicknessBox?.Text,
                NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedThickness)
                ? Math.Clamp(parsedThickness, 0.5, 6)
                : null;
            int? tileGraphHistory = int.TryParse(_metricGraphHistoryBox?.Text,
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedHistory)
                ? Math.Clamp(parsedHistory, 8, 240)
                : null;
            settings.SetMetricVisible(_selectedMetricId, tileVisible);
            OsdMetricBridge.UpdateTileMetric(profile, definition, tileVisible,
                _metricNameBox?.Text.Trim(), _metricUnitBox?.Text.Trim(), tileDecimals,
                tileValueColor, tileGraphColor,
                ParseNullableDouble(_metricGraphMinBox?.Text),
                ParseNullableDouble(_metricGraphMaxBox?.Text),
                _metricGraphCheck?.IsChecked == true,
                _metricProgressCheck?.IsChecked == true,
                tileGraphThickness, tileGraphHistory, tileWarning, tileCritical);

            _selfApplying = true;
            try
            {
                _runtime.ApplySettings(settings, persist: true);
            }
            finally
            {
                _selfApplying = false;
            }

            FooterStatus.Text = Loc.T("metric_settings_saved");
            RebuildMetricRows();
            RebuildOverlayItems();
            UpdatePreview();
            return;
        }

        if (profile.FollowMetricPreferences)
        {
            OsdMetricBridge.MaterializeVisibleMetrics(profile, settings.Metrics);
        }

        var element = OsdMetricBridge.FindElement(profile, definition.Id) ??
                      OsdMetricBridge.EnsureElement(profile, definition);
        element.Visible = _metricVisibleCheck?.IsChecked == true;

        element.Label = _metricNameBox?.Text.Trim() ?? string.Empty;
        element.Unit = _metricUnitBox?.Text.Trim() ?? string.Empty;
        if (int.TryParse(_metricDecimalsBox?.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var decimals))
        {
            element.Decimals = Math.Clamp(decimals, -1, 6);
        }

        element.WarningThreshold = ParseNullableDouble(_metricWarningBox?.Text);
        element.CriticalThreshold = ParseNullableDouble(_metricCriticalBox?.Text);
        element.ValueColor = HexColor.IsMatch(_metricColorBox?.Text ?? string.Empty)
            ? AppSettings.SanitizeColor(_metricColorBox!.Text, string.Empty)
            : string.Empty;
        element.GraphColor = HexColor.IsMatch(_metricGraphColorBox?.Text ?? string.Empty)
            ? AppSettings.SanitizeColor(_metricGraphColorBox!.Text, string.Empty)
            : string.Empty;
        if (double.TryParse(_metricGraphThicknessBox?.Text, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var thickness))
        {
            element.GraphThickness = Math.Clamp(thickness, 0.5, 6);
        }

        if (int.TryParse(_metricGraphHistoryBox?.Text, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var historyPoints))
        {
            element.GraphHistoryPoints = Math.Clamp(historyPoints, 8, 4096);
        }

        element.GraphScaleMin = ParseNullableDouble(_metricGraphMinBox?.Text);
        element.GraphScaleMax = ParseNullableDouble(_metricGraphMaxBox?.Text);
        element.Visible = _metricVisibleCheck?.IsChecked == true;
        element.ShowGraph = _metricGraphCheck?.IsChecked == true;
        element.ShowProgress = _metricProgressCheck?.IsChecked == true;
        element.AutoColor = _metricAutoColorCheck?.IsChecked == true;
        profile.FollowMetricPreferences = false;
        settings.SetMetricVisible(_selectedMetricId, element.Visible);

        _selfApplying = true;
        try
        {
            _runtime.ApplySettings(settings, persist: true);
        }
        finally
        {
            _selfApplying = false;
        }

        FooterStatus.Text = Loc.T("metric_settings_saved");
        RebuildMetricRows();
        RebuildOverlayItems();
    }

    private static OsdElement? FindOsdElementInProfile(OsdProfile profile, string metricId)
    {
        var definition = MetricCatalog.Find(metricId);
        var rawId = definition is null ? null : MetricCatalog.SensorIdFor(definition);
        return OsdProfile.Flatten(profile.Elements)
            .FirstOrDefault(element => element.SensorId == metricId ||
                                      (rawId is not null && element.SensorId == rawId));
    }

    private static double? ParseNullableDouble(string? text)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private void RebuildMetricRows()
    {
        var previousLoading = _loading;
        _loading = true;
        try
        {
            MetricRowsPanel.Children.Clear();
            _metricChecks.Clear();
            _metricValueTexts.Clear();
            _metricUnitTexts.Clear();
            _metricSourceTexts.Clear();
            _metricRowBorders.Clear();

            var metrics = _runtime.Settings.Metrics;
            MetricGroup? lastGroup = null;

            foreach (var preference in metrics)
            {
                var definition = MetricCatalog.Find(preference.Id);
                if (definition is null)
                {
                    continue;
                }

                if (lastGroup != definition.Group)
                {
                    lastGroup = definition.Group;
                    var header = new TextBlock
                    {
                        Text = GroupName(definition.Group),
                        Style = (Style)FindResource("SectionTitle"),
                        Margin = new Thickness(9, 9, 0, 4),
                        FontSize = 10,
                    };
                    header.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
                    MetricRowsPanel.Children.Add(header);
                }

                var grid = new Grid { Margin = new Thickness(8, 0, 8, 0), Height = 27 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 48 });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });

                var isFpsMetric = string.Equals(definition.Id, "frame.fps", StringComparison.Ordinal);
                var check = new CheckBox
                {
                    IsChecked = isFpsMetric
                        ? _runtime.Settings.Fps.Enabled && preference.Visible
                        : preference.Visible,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0),
                    Tag = definition.Id,
                };
                if (isFpsMetric)
                {
                    check.Checked += (_, _) => ApplyFpsEnabled(true);
                    check.Unchecked += (_, _) => ApplyFpsEnabled(false);
                }
                else
                {
                    check.Checked += (_, _) => ApplyMetricVisibility(definition.Id, true);
                    check.Unchecked += (_, _) => ApplyMetricVisibility(definition.Id, false);
                }

                _metricChecks[definition.Id] = check;
                System.Windows.Automation.AutomationProperties.SetName(check, MetricName(definition));

                var name = new TextBlock
                {
                    Text = MetricName(definition),
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 11,
                    ToolTip = MetricName(definition),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };

                var unit = new TextBlock
                {
                    Text = definition.Unit,
                    Foreground = (Brush)FindResource("MutedBrush"),
                    FontFamily = (FontFamily)FindResource("MonoFont"),
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                _metricUnitTexts[definition.Id] = unit;

                var value = new TextBlock
                {
                    Text = "—",
                    Style = (Style)FindResource("ValueText"),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                _metricValueTexts[definition.Id] = value;

                var source = new TextBlock
                {
                    Text = "—",
                    Foreground = (Brush)FindResource("DimBrush"),
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = Loc.T("metrics_source_status"),
                };
                _metricSourceTexts[definition.Id] = source;

                var orderPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var up = MakeOrderButton("▲", definition.Id, -1, "tooltip_move_up");
                var down = MakeOrderButton("▼", definition.Id, +1, "tooltip_move_down");
                var settingsButton = new Button
                {
                    Content = "…",
                    Style = (Style)FindResource("ToolButton"),
                    ToolTip = Loc.T("metric_settings_title"),
                    Margin = new Thickness(0, 0, 2, 0),
                };
                System.Windows.Automation.AutomationProperties.SetAutomationId(settingsButton,
                    "MetricSettings_" + definition.Id);
                settingsButton.Click += (_, _) => SelectMetricForSettings(definition.Id);
                orderPanel.Children.Add(settingsButton);
                orderPanel.Children.Add(up);
                orderPanel.Children.Add(down);

                Grid.SetColumn(check, 0);
                Grid.SetColumn(name, 1);
                Grid.SetColumn(unit, 2);
                Grid.SetColumn(value, 3);
                Grid.SetColumn(source, 4);
                Grid.SetColumn(orderPanel, 5);
                grid.Children.Add(check);
                grid.Children.Add(name);
                grid.Children.Add(unit);
                grid.Children.Add(value);
                grid.Children.Add(source);
                grid.Children.Add(orderPanel);

                grid.Tag = definition.Id;
                grid.Cursor = Cursors.Hand;
                grid.MouseLeftButtonDown += (_, _) => SelectMetricForSettings(definition.Id);
                var rowBorder = new Border
                {
                    Style = (Style)FindResource("TableRow"),
                    Child = grid,
                    Tag = definition.Id,
                };
                _metricRowBorders[definition.Id] = rowBorder;
                MetricRowsPanel.Children.Add(rowBorder);
            }

            ApplyMetricFilter();
            if (_selectedMetricId is not null)
            {
                SelectMetricForSettings(_selectedMetricId);
            }

            UpdateLiveValues();
        }
        finally
        {
            _loading = previousLoading;
        }
    }

    private Button MakeOrderButton(string glyph, string metricId, int delta, string tooltipKey)
    {
        var button = new Button
        {
            Content = glyph,
            Style = (Style)FindResource("FlatButton"),
            Width = 24,
            Height = 21,
            MinHeight = 0,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 3, 0),
            FontSize = 8,
            ToolTip = Loc.T(tooltipKey),
        };
        button.Click += (_, _) => MoveMetric(metricId, delta);
        return button;
    }

    private void ApplyFpsEnabled(bool enabled)
    {
        if (_loading)
        {
            return;
        }

        var settings = _runtime.Settings.Clone();
        settings.Fps.Enabled = enabled;
        var preference = settings.Metrics.FirstOrDefault(m => m.Id == "frame.fps");
        if (preference is not null)
        {
            preference.Visible = enabled;
        }

        _selfApplying = true;
        try
        {
            _runtime.ApplySettings(settings, persist: true);
        }
        finally
        {
            _selfApplying = false;
        }

        LoadFromSettings();
        FooterStatus.Text = Loc.T(enabled ? "msg_fps_enabled" : "msg_fps_disabled");
    }

    // ------------------------------------------------------------------
    // Hotkey capture
    // ------------------------------------------------------------------
    private void HotkeyChange_Click(object sender, RoutedEventArgs e) => BeginHotkeyCapture("overlay");

    private void MainHotkeyChange_Click(object sender, RoutedEventArgs e) => BeginHotkeyCapture("main");

    private void StartRecordHotkeyChange_Click(object sender, RoutedEventArgs e) => BeginHotkeyCapture("start");

    private void StopRecordHotkeyChange_Click(object sender, RoutedEventArgs e) => BeginHotkeyCapture("stop");

    private void RestartHotkeyChange_Click(object sender, RoutedEventArgs e) => BeginHotkeyCapture("restart");

    private void DragHotkeyChange_Click(object sender, RoutedEventArgs e) => BeginHotkeyCapture("drag");

    private static bool IsHoldTarget(string target)
        => string.Equals(target, "drag", StringComparison.Ordinal);

    private TextBox? HotkeyBoxFor(string target) => target switch
    {
        "main" => MainHotkeyBox,
        "start" => StartRecordHotkeyBox,
        "stop" => StopRecordHotkeyBox,
        "restart" => RestartHotkeyBox,
        "drag" => DragHotkeyBox,
        _ => HotkeyBox,
    };

    private TextBlock? HotkeyStatusFor(string target) => target switch
    {
        "main" => MainHotkeyStatusText,
        "drag" => DragHotkeyStatusText,
        _ => HotkeyStatusText,
    };

    private static string HotkeyValue(AppSettings settings, string target) => target switch
    {
        "main" => settings.Behavior.HotkeyToggleMainWindow,
        "start" => settings.Behavior.HotkeyStartRecording,
        "stop" => settings.Behavior.HotkeyStopRecording,
        "restart" => settings.Behavior.HotkeyRestartApplication,
        "drag" => settings.Behavior.HotkeyDragOverlay,
        _ => settings.Behavior.HotkeyToggleOverlay,
    };

    private static string DefaultHotkey(string target) => target switch
    {
        "main" => HotkeyGesture.MainWindowDefaultText,
        "start" => "Ctrl+Shift+R",
        "stop" => "Ctrl+Shift+S",
        "restart" => "Ctrl+Alt+F12",
        "drag" => HoldGesture.DefaultText,
        _ => HotkeyGesture.DefaultText,
    };

    private static void SetHotkeyValue(AppSettings settings, string target, string value)
    {
        switch (target)
        {
            case "main":
                settings.Behavior.HotkeyToggleMainWindow = value;
                break;
            case "start":
                settings.Behavior.HotkeyStartRecording = value;
                break;
            case "stop":
                settings.Behavior.HotkeyStopRecording = value;
                break;
            case "restart":
                settings.Behavior.HotkeyRestartApplication = value;
                break;
            case "drag":
                settings.Behavior.HotkeyDragOverlay = value;
                break;
            default:
                settings.Behavior.HotkeyToggleOverlay = value;
                break;
        }
    }

    private static uint ModifierBit(Key key) => key switch
    {
        Key.LeftCtrl or Key.RightCtrl => HotkeyGesture.ModControl,
        Key.LeftAlt or Key.RightAlt => HotkeyGesture.ModAlt,
        Key.LeftShift or Key.RightShift => HotkeyGesture.ModShift,
        Key.LWin or Key.RWin => HotkeyGesture.ModWindows,
        _ => 0,
    };

    private static bool IsModifierKey(Key key) => ModifierBit(key) != 0;

    private void BeginHotkeyCapture(string target)
    {
        if (!IsHoldTarget(target) && !_runtime.Settings.Behavior.EnableGlobalHotkey)
        {
            FooterStatus.Text = Loc.T("hotkeys_enable_first");
            return;
        }

        _capturingHotkeyTarget = target;
        _dragCaptureModifiers = 0;
        _dragCapturePending = false;
        _hotkeyBeforeCapture = HotkeyValue(_runtime.Settings, target);
        if (HotkeyBoxFor(target) is { } box)
        {
            box.Text = Loc.T("hotkeys_press");
        }

        if (HotkeyStatusFor(target) is { } status)
        {
            status.Text = Loc.T("hotkeys_listening");
        }

        UpdateHotkeyButtons();
        Focus();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var target = _capturingHotkeyTarget;
        if (target is null)
        {
            return;
        }

        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            CancelHotkeyCapture();
            return;
        }

        var status = HotkeyStatusFor(target);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (IsHoldTarget(target))
        {
            if (IsModifierKey(key))
            {
                _dragCaptureModifiers = (uint)Keyboard.Modifiers | ModifierBit(key);
                _dragCapturePending = true;
                if (status is not null)
                {
                    var preview = new HoldGesture(_dragCaptureModifiers, null);
                    status.Text = Loc.F("hotkeys_drag_modifier_pending", preview.DisplayText);
                }

                return;
            }

            var hold = new HoldGesture((uint)Keyboard.Modifiers,
                (uint)KeyInterop.VirtualKeyFromKey(key));
            if (!hold.IsValid)
            {
                if (status is not null)
                {
                    status.Text = Loc.T("hotkeys_unsupported");
                }

                return;
            }

            if (HoldHotkeyConflicts(target, hold))
            {
                if (status is not null)
                {
                    status.Text = Loc.T("hotkeys_duplicate");
                }

                return;
            }

            CommitHotkey(target, hold.DisplayText);
            return;
        }

        if (IsModifierKey(key))
        {
            return;
        }

        var modifiers = (uint)Keyboard.Modifiers;
        if (modifiers == 0)
        {
            if (status is not null)
            {
                status.Text = Loc.T("hotkeys_modifier_required");
            }

            return;
        }

        var gesture = new HotkeyGesture(modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
        if (!gesture.IsValid)
        {
            if (status is not null)
            {
                status.Text = Loc.T("hotkeys_unsupported");
            }

            return;
        }

        if (HotkeyConflicts(target, gesture))
        {
            if (status is not null)
            {
                status.Text = Loc.T("hotkeys_duplicate");
            }

            return;
        }

        CommitHotkey(target, gesture.DisplayText);
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (_capturingHotkeyTarget is null || !_dragCapturePending)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!IsModifierKey(key))
        {
            return;
        }

        e.Handled = true;
        _dragCapturePending = false;
        var hold = new HoldGesture(_dragCaptureModifiers, null);
        if (HoldHotkeyConflicts("drag", hold))
        {
            DragHotkeyStatusText.Text = Loc.T("hotkeys_duplicate");
            return;
        }

        CommitHotkey("drag", hold.DisplayText);
    }

    private bool HotkeyConflicts(string target, HotkeyGesture candidate)
    {
        var settings = _runtime.Settings;
        foreach (var other in new[] { "overlay", "main", "start", "stop", "restart" })
        {
            if (other == target)
            {
                continue;
            }

            if (HotkeyGesture.TryParse(HotkeyValue(settings, other), out var gesture) && gesture == candidate)
            {
                return true;
            }
        }

        if (HoldGesture.TryParse(settings.Behavior.HotkeyDragOverlay, out var drag) &&
            drag.VirtualKey is { } dragKey && drag.Modifiers == candidate.Modifiers && dragKey == candidate.VirtualKey)
        {
            return true;
        }

        return false;
    }

    private bool HoldHotkeyConflicts(string target, HoldGesture candidate)
    {
        if (candidate.VirtualKey is not { } candidateKey)
        {
            return false;
        }

        var settings = _runtime.Settings;
        foreach (var other in new[] { "overlay", "main", "start", "stop", "restart" })
        {
            if (other == target)
            {
                continue;
            }

            if (HotkeyGesture.TryParse(HotkeyValue(settings, other), out var gesture) &&
                gesture.VirtualKey == candidateKey && gesture.Modifiers == candidate.Modifiers)
            {
                return true;
            }
        }

        return false;
    }

    private void CommitHotkey(string target, string value)
    {
        var settings = _runtime.Settings.Clone();
        SetHotkeyValue(settings, target, value);
        _capturingHotkeyTarget = null;
        _dragCapturePending = false;
        _selfApplying = true;
        try
        {
            _runtime.ApplySettings(settings, persist: true);
        }
        finally
        {
            _selfApplying = false;
        }

        LoadFromSettings();
        FooterStatus.Text = Loc.F("hotkeys_changed", value);
    }

    private void CancelHotkeyCapture()
    {
        var target = _capturingHotkeyTarget;
        _capturingHotkeyTarget = null;
        _dragCapturePending = false;
        if (target is not null && HotkeyBoxFor(target) is { } box)
        {
            box.Text = _hotkeyBeforeCapture;
        }

        UpdateHotkeyStatus();
        FooterStatus.Text = Loc.T("hotkeys_cancelled");
    }

    private void HotkeyReset_Click(object sender, RoutedEventArgs e) => ResetHotkey("overlay");

    private void MainHotkeyReset_Click(object sender, RoutedEventArgs e) => ResetHotkey("main");

    private void StartRecordHotkeyReset_Click(object sender, RoutedEventArgs e) => ResetHotkey("start");

    private void StopRecordHotkeyReset_Click(object sender, RoutedEventArgs e) => ResetHotkey("stop");

    private void RestartHotkeyReset_Click(object sender, RoutedEventArgs e) => ResetHotkey("restart");

    private void DragHotkeyReset_Click(object sender, RoutedEventArgs e) => ResetHotkey("drag");

    private void ResetHotkey(string target)
    {
        _capturingHotkeyTarget = null;
        _dragCapturePending = false;
        var text = DefaultHotkey(target);
        var settings = _runtime.Settings.Clone();
        if (IsHoldTarget(target))
        {
            if (!HoldGesture.TryParse(text, out var hold))
            {
                return;
            }

            if (HoldHotkeyConflicts(target, hold))
            {
                FooterStatus.Text = Loc.T("hotkeys_duplicate");
                UpdateHotkeyStatus();
                return;
            }
        }
        else
        {
            if (!HotkeyGesture.TryParse(text, out var gesture) || HotkeyConflicts(target, gesture))
            {
                FooterStatus.Text = Loc.T("hotkeys_duplicate");
                UpdateHotkeyStatus();
                return;
            }
        }

        SetHotkeyValue(settings, target, text);
        _selfApplying = true;
        try
        {
            _runtime.ApplySettings(settings, persist: true);
        }
        finally
        {
            _selfApplying = false;
        }

        LoadFromSettings();
        FooterStatus.Text = Loc.F("hotkeys_reset_done", text);
    }

    private void UpdateHotkeyButtons()
    {
        var enabled = _runtime.Settings.Behavior.EnableGlobalHotkey;
        var capturing = _capturingHotkeyTarget;
        HotkeyChangeButton.IsEnabled = enabled && capturing is not "overlay";
        MainHotkeyChangeButton.IsEnabled = enabled && capturing is not "main";
        StartRecordHotkeyChangeButton.IsEnabled = enabled && capturing is not "start";
        StopRecordHotkeyChangeButton.IsEnabled = enabled && capturing is not "stop";
        RestartHotkeyChangeButton.IsEnabled = enabled && capturing is not "restart";
        DragHotkeyChangeButton.IsEnabled = capturing is not "drag";
        HotkeyResetButton.IsEnabled = capturing is not "overlay";
        MainHotkeyResetButton.IsEnabled = capturing is not "main";
        StartRecordHotkeyResetButton.IsEnabled = capturing is not "start";
        StopRecordHotkeyResetButton.IsEnabled = capturing is not "stop";
        RestartHotkeyResetButton.IsEnabled = capturing is not "restart";
        DragHotkeyResetButton.IsEnabled = capturing is not "drag";
    }

    private void UpdateHotkeyStatus()
    {
        if (HotkeyStatusText is null)
        {
            return;
        }

        var behavior = _runtime.Settings.Behavior;
        foreach (var target in new[] { "overlay", "main", "start", "stop", "restart", "drag" })
        {
            if (!string.Equals(_capturingHotkeyTarget, target, StringComparison.Ordinal) &&
                HotkeyBoxFor(target) is { } box)
            {
                box.Text = HotkeyValue(_runtime.Settings, target);
            }
        }

        UpdateHotkeyButtons();
        if (_capturingHotkeyTarget is not "overlay")
        {
            HotkeyStatusText.Text = HotkeyStatusTextFor(behavior.EnableGlobalHotkey,
                _runtime.HotkeyError, _runtime.IsHotkeyRegistered, _runtime.HotkeyDisplay);
        }

        if (_capturingHotkeyTarget is not "main")
        {
            MainHotkeyStatusText.Text = HotkeyStatusTextFor(behavior.EnableGlobalHotkey,
                _runtime.MainWindowHotkeyError, _runtime.IsMainWindowHotkeyRegistered,
                _runtime.MainWindowHotkeyDisplay);
        }

        if (_capturingHotkeyTarget is not "start" and not "stop" and not "restart")
        {
            var actionError = _runtime.StartRecordingHotkeyError ??
                              _runtime.StopRecordingHotkeyError ??
                              _runtime.RestartHotkeyError;
            ActionHotkeyStatusText.Text = !behavior.EnableGlobalHotkey
                ? Loc.T("hotkeys_status_disabled")
                : actionError is not null
                    ? Loc.F("hotkeys_status_conflict", actionError)
                    : Loc.F("hotkeys_actions_status", behavior.HotkeyStartRecording,
                        behavior.HotkeyStopRecording, behavior.HotkeyRestartApplication);
        }

        if (_capturingHotkeyTarget is not "drag")
        {
            DragHotkeyStatusText.Text = _runtime.Settings.Overlay.ModifierDragEnabled
                ? Loc.F("hotkeys_drag_status", behavior.HotkeyDragOverlay)
                : Loc.T("hotkeys_drag_disabled");
        }
    }

    private static string HotkeyStatusTextFor(bool enabled, string? error, bool registered, string display)
    {
        if (!enabled)
        {
            return Loc.T("hotkeys_status_disabled");
        }

        if (!string.IsNullOrEmpty(error))
        {
            return Loc.F("hotkeys_status_conflict", error);
        }

        return registered
            ? Loc.F("hotkeys_status_registered", display)
            : Loc.T("hotkeys_status_none");
    }

    // ------------------------------------------------------------------
    // Recording
    // ------------------------------------------------------------------
    private void ApplyRecordingSelection(IReadOnlyList<string> selected)
    {
        if (_recordingChecks.Count == 0)
        {
            return;
        }

        var wanted = new HashSet<string>(selected ?? Array.Empty<string>(), StringComparer.Ordinal);
        _recordingSensorSync = true;
        try
        {
            foreach (var (id, check) in _recordingChecks)
            {
                check.IsChecked = wanted.Contains(id);
            }
        }
        finally
        {
            _recordingSensorSync = false;
        }

        UpdateRecordingSummary();
    }

    private void RebuildRecordingSensors()
    {
        var snapshot = _runtime.Cache.Latest;
        if (snapshot.Count == 0)
        {
            _recordingListBuilt = false;
            RecordingSensorSummaryText.Text = Loc.T("recording_waiting_sensors");
            return;
        }

        _recordingListBuilt = true;
        var previous = _recordingChecks.ToDictionary(pair => pair.Key,
            pair => pair.Value.IsChecked == true, StringComparer.Ordinal);
        var firstBuild = previous.Count == 0;
        var saved = new HashSet<string>(_runtime.Settings.Recording.Sensors ?? new List<string>(),
            StringComparer.Ordinal);
        var selectAllByDefault = firstBuild && saved.Count == 0;

        bool Selected(string id) => previous.TryGetValue(id, out var wasChecked)
            ? wasChecked
            : selectAllByDefault || saved.Contains(id);

        _recordingSensorSync = true;
        try
        {
            RecordingSensorPanel.Children.Clear();
            _recordingChecks.Clear();
            _recordingSensors.Clear();

            MetricGroup? lastGroup = null;
            foreach (var definition in MetricCatalog.All)
            {
                if (definition.Group == MetricGroup.Frame)
                {
                    continue;
                }

                var sensorId = MetricCatalog.SensorIdFor(definition);
                if (sensorId is null)
                {
                    continue;
                }

                if (lastGroup != definition.Group)
                {
                    lastGroup = definition.Group;
                    AddRecordingGroupHeader(GroupName(definition.Group));
                }

                AddRecordingSensor(sensorId, MetricName(definition), definition.Unit, Selected(sensorId));
            }

            var fanChannels = FanSensorReader.Channels(snapshot);
            if (fanChannels.Count > 0)
            {
                AddRecordingGroupHeader(Loc.T("group_fans"));
                foreach (var channel in fanChannels)
                {
                    foreach (var measure in new[] { FanMeasure.Rpm, FanMeasure.Percent })
                    {
                        var id = FanSensorIds.Id(channel.Component, channel.Index, measure);
                        if (!snapshot.Contains(id))
                        {
                            continue;
                        }

                        var measureText = Loc.T(measure == FanMeasure.Rpm
                            ? "recording_unit_rpm"
                            : "recording_unit_percent");
                        var name = Loc.F("recording_fan_name",
                            FanSensorIds.ComponentLabel(channel.Component), channel.Index + 1, measureText);
                        AddRecordingSensor(id, name, measureText, Selected(id));
                    }
                }
            }
        }
        finally
        {
            _recordingSensorSync = false;
        }

        UpdateRecordingSummary();
        UpdateRecordingStatus(force: true);
    }

    private void AddRecordingGroupHeader(string title)
    {
        var header = new TextBlock
        {
            Text = title,
            Style = (Style)FindResource("SectionTitle"),
            Margin = new Thickness(2, 7, 0, 3),
            FontSize = 10,
        };
        header.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        RecordingSensorPanel.Children.Add(header);
    }

    private void AddRecordingSensor(string sensorId, string name, string unit, bool selected)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock
        {
            Text = unit,
            Margin = new Thickness(7, 0, 0, 0),
            FontSize = 10,
            Foreground = (Brush)FindResource("DimBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var check = new CheckBox
        {
            Content = content,
            IsChecked = selected,
            Tag = sensorId,
            Margin = new Thickness(2, 1, 0, 1),
            ToolTip = sensorId,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(check, "RecordingSensor_" + sensorId);
        System.Windows.Automation.AutomationProperties.SetName(check, name);
        check.Checked += RecordingSensor_Changed;
        check.Unchecked += RecordingSensor_Changed;

        _recordingChecks[sensorId] = check;
        _recordingSensors[sensorId] = new RecordingSensor(sensorId, name, unit);
        RecordingSensorPanel.Children.Add(check);
    }

    private void RecordingSensor_Changed(object sender, RoutedEventArgs e)
    {
        if (_recordingSensorSync || _loading)
        {
            return;
        }

        UpdateRecordingSummary();
        ApplyLive();
    }

    private void UpdateRecordingSummary()
    {
        var selected = _recordingChecks.Count(pair => pair.Value.IsChecked == true);
        RecordingSensorSummaryText.Text = Loc.F("recording_sensor_summary", selected, _recordingChecks.Count);
        RecordingStartButton.IsEnabled = !_runtime.Recorder.IsRecording && selected > 0;
    }

    private void UpdateRecordingStatus(bool force = false)
    {
        if (RecordingStatusText is null)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (!force && now - _lastRecordingStatusTick < 500)
        {
            return;
        }

        _lastRecordingStatusTick = now;
        var status = _runtime.Recorder.Status;
        var active = status.State is RecordingState.Recording or RecordingState.Stopping;

        RecordingStatusText.Text = Loc.T(status.State switch
        {
            RecordingState.Recording => "recording_state_recording",
            RecordingState.Stopping => "recording_state_stopping",
            RecordingState.Error => "recording_state_error",
            _ => "recording_state_idle",
        });
        RecordingStatusDot.Fill = status.State switch
        {
            RecordingState.Recording => (Brush)FindResource("AccentBrush"),
            RecordingState.Stopping => (Brush)FindResource("WarnBrush"),
            RecordingState.Error => (Brush)FindResource("WarnBrush"),
            _ => (Brush)FindResource("DimBrush"),
        };

        RecordingElapsedText.Text = active ? status.Elapsed.ToString(@"hh\:mm\:ss") : "—";
        RecordingSamplesText.Text = status.Samples.ToString("N0");
        RecordingRowsText.Text = status.Rows.ToString("N0");
        RecordingSizeText.Text = FormatBytes(status.Bytes);
        RecordingPathText.Text = string.IsNullOrEmpty(status.FilePath) ? "—" : status.FilePath;
        RecordingDroppedText.Text = status.DroppedSamples.ToString("N0");

        if (status.ErrorCode == RecordingError.None)
        {
            RecordingErrorText.Visibility = Visibility.Collapsed;
            RecordingErrorText.Text = string.Empty;
        }
        else
        {
            var reason = Loc.T(RecordingErrorKey(status.ErrorCode));
            RecordingErrorText.Text = string.IsNullOrWhiteSpace(status.ErrorDetail)
                ? reason
                : Loc.F("recording_error_text", reason, status.ErrorDetail!);
            RecordingErrorText.Visibility = Visibility.Visible;
        }

        var selected = _recordingChecks.Count(pair => pair.Value.IsChecked == true);
        RecordingStartButton.IsEnabled = !active && selected > 0;
        RecordingStopButton.IsEnabled = active;
        RecordingOpenFolderButton.IsEnabled = Directory.Exists(RecordingDirectoryBox.Text.Trim());
    }

    private static string RecordingErrorKey(RecordingError error) => error switch
    {
        RecordingError.AlreadyRecording => "recording_error_already_recording",
        RecordingError.NoSensors => "recording_error_no_sensors",
        RecordingError.InvalidDirectory => "recording_error_invalid_directory",
        RecordingError.AccessDenied => "recording_error_access_denied",
        RecordingError.DiskFull => "recording_error_disk_full",
        RecordingError.IoError => "recording_error_io_error",
        _ => "recording_error_unknown",
    };

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes + " B";
        }

        if (bytes < 1024 * 1024)
        {
            return (bytes / 1024.0).ToString("0.0") + " KB";
        }

        return (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MB";
    }

    private bool _osdTreeSyncing;
    private ListBoxItem? _recordingHistorySelection;

    private void RebuildOverlayItems()
        => RebuildOsdStructureList(OverlayItemsList, OverlayItemSearchBox?.Text);

    private void RebuildOsdStructureList(ListBox? list, string? search)
    {
        if (list is null)
        {
            return;
        }

        _osdTreeSyncing = true;
        try
        {
            list.Items.Clear();
            var profile = _runtime.Settings.Osd?.ActiveProfile;
            if (profile is null)
            {
                return;
            }

            var term = search?.Trim() ?? string.Empty;
            if (!profile.IsTile)
            {
                AddMetricListItems(list, profile, term);
                AddOsdListItems(list, profile.Elements, 0, term, skipCatalogSensors: true);
            }
            else
            {
                AddMetricListItems(list, profile, term);
                AddTileListItems(list, profile, term);
            }
        }
        finally
        {
            _osdTreeSyncing = false;
        }
    }

    private void AddMetricListItems(ListBox list, OsdProfile profile, string search)
    {
        MetricGroup? lastGroup = null;
        foreach (var definition in MetricCatalog.All)
        {
            var label = MetricName(definition);
            if (search.Length > 0 &&
                !label.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                !definition.Id.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (lastGroup != definition.Group)
            {
                list.Items.Add(new ListBoxItem
                {
                    Content = new TextBlock
                    {
                        Text = MetricCatalog.GroupTitle(definition.Group),
                        FontSize = 9,
                        FontWeight = FontWeights.SemiBold,
                    },
                    IsEnabled = false,
                    Padding = new Thickness(3, 4, 3, 1),
                    Tag = "group:" + definition.Group,
                });
                lastGroup = definition.Group;
            }

            var preference = _runtime.Settings.Metrics.FirstOrDefault(m => m.Id == definition.Id);
            var element = OsdMetricBridge.FindElement(profile, definition.Id);
            var isChecked = profile.IsTile
                ? OsdMetricBridge.IsTileMetricVisible(profile, definition)
                : FollowsMetricMatrix(profile)
                    ? preference?.Visible == true
                    : preference?.Visible == true && element?.Visible == true;

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 1, 0, 1),
            };
            var check = new CheckBox
            {
                IsChecked = isChecked,
                Tag = definition.Id,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0),
            };
            check.Checked += (_, _) => ApplyMetricVisibility(definition.Id, true);
            check.Unchecked += (_, _) => ApplyMetricVisibility(definition.Id, false);
            System.Windows.Automation.AutomationProperties.SetName(check, label);
            panel.Children.Add(check);
            panel.Children.Add(new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
            });
            panel.Children.Add(new TextBlock
            {
                Text = "  " + definition.Id,
                FontSize = 9,
                Foreground = (Brush)FindResource("DimBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            });

            list.Items.Add(new ListBoxItem
            {
                Content = panel,
                Tag = definition.Id,
                Padding = new Thickness(3, 1, 3, 1),
            });
        }
    }

    private void AddOsdListItems(ListBox list, IEnumerable<OsdElement> elements, int depth, string search,
        bool skipCatalogSensors)
    {
        foreach (var element in elements)
        {
            var isCatalogSensor = element.IsSensorLike &&
                                  (MetricCatalog.Find(element.SensorId) is not null ||
                                   MetricCatalog.FindBySensorId(element.SensorId) is not null);
            if (skipCatalogSensors && isCatalogSensor)
            {
                continue;
            }

            var label = string.IsNullOrWhiteSpace(element.DisplayLabel())
                ? element.Kind.ToString()
                : element.DisplayLabel();
            var matches = search.Length == 0 ||
                          label.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                          element.SensorId.Contains(search, StringComparison.OrdinalIgnoreCase);
            if (matches)
            {
                var panel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(depth * 12, 1, 0, 1),
                };
                var check = new CheckBox
                {
                    IsChecked = element.Visible,
                    Tag = element.Id,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 5, 0),
                };
                check.Checked += OsdVisibility_Changed;
                check.Unchecked += OsdVisibility_Changed;
                panel.Children.Add(check);
                panel.Children.Add(new TextBlock
                {
                    Text = label,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 11,
                });
                if (!string.IsNullOrWhiteSpace(element.SensorId))
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = "  " + element.SensorId,
                        FontSize = 9,
                        Foreground = (Brush)FindResource("DimBrush"),
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                }

                list.Items.Add(new ListBoxItem
                {
                    Content = panel,
                    Tag = element.Id,
                    Padding = new Thickness(3, 1, 3, 1),
                });
            }

            if (element.Kind == OsdElementKind.Group)
            {
                AddOsdListItems(list, element.Children, depth + 1, search, skipCatalogSensors);
            }
        }
    }

    private void AddTileListItems(ListBox list, OsdProfile profile, string search)
    {
        var document = profile.TileDesign;
        if (document is null)
        {
            return;
        }

        foreach (var (path, node) in document.Walk())
        {
            var definition = OsdMetricBridge.DefinitionForTileKey(node.Key);
            if (definition is not null)
            {
                // The catalogue list above is the single editable entry for this metric.
                continue;
            }

            var label = !string.IsNullOrWhiteSpace(node.Name)
                ? node.Name
                : !string.IsNullOrWhiteSpace(node.Label)
                    ? node.Label
                    : !string.IsNullOrWhiteSpace(node.Text)
                        ? node.Text
                        : !string.IsNullOrWhiteSpace(node.Key)
                            ? node.Key
                            : node.NodeKind.ToString();
            var matches = search.Length == 0 ||
                          label.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                          (node.Key?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);
            if (!matches)
            {
                continue;
            }

            var depth = path.Count(character => character == '/');
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(depth * 10, 1, 0, 1),
            };
            var check = new CheckBox
            {
                IsChecked = node.Visible != false,
                Tag = "tile:" + path,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0),
            };
            check.Checked += TileVisibility_Changed;
            check.Unchecked += TileVisibility_Changed;
            System.Windows.Automation.AutomationProperties.SetName(check, label);
            panel.Children.Add(check);
            panel.Children.Add(new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
            });
            panel.Children.Add(new TextBlock
            {
                Text = "  " + node.NodeKind,
                FontSize = 9,
                Foreground = (Brush)FindResource("DimBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            });

            list.Items.Add(new ListBoxItem
            {
                Content = panel,
                Tag = "tile:" + path,
                Padding = new Thickness(3, 1, 3, 1),
            });
        }
    }

    private void TileVisibility_Changed(object sender, RoutedEventArgs e)
    {
        if (_osdTreeSyncing || sender is not CheckBox check || check.Tag is not string tag ||
            !tag.StartsWith("tile:", StringComparison.Ordinal))
        {
            return;
        }

        var settings = _runtime.Settings.Clone();
        var profile = settings.Osd?.ActiveProfile;
        if (profile is not { IsTile: true })
        {
            return;
        }

        var document = profile.TileDesign?.Clone();
        var node = document?.FindNode(tag[5..]);
        if (document is null || node is null)
        {
            return;
        }

        node.Visible = check.IsChecked == true;
        profile.SetTileDesign(document);
        _selfApplying = true;
        try
        {
            _runtime.ApplySettings(settings, persist: true);
        }
        finally
        {
            _selfApplying = false;
        }

        RebuildOverlayItems();
        UpdatePreview();
    }

    private void OsdVisibility_Changed(object sender, RoutedEventArgs e)
    {
        if (_osdTreeSyncing || sender is not CheckBox check || check.Tag is not string id)
        {
            return;
        }

        var settings = _runtime.Settings.Clone();
        var element = settings.Osd.ActiveProfile is { } profile
            ? OsdProfile.Flatten(profile.Elements).FirstOrDefault(item => item.Id == id)
            : null;
        if (element is null)
        {
            return;
        }

        element.Visible = check.IsChecked == true;
        var definition = MetricCatalog.Find(element.SensorId) ??
                         MetricCatalog.FindBySensorId(element.SensorId);
        if (definition is not null)
        {
            settings.SetMetricVisible(definition.Id, element.Visible);
        }

        if (settings.Osd.ActiveProfile!.FollowMetricPreferences)
        {
            // Turning a profile into a custom layout must not silently drop metrics that were
            // visible in the shared Metrics matrix.
            OsdMetricBridge.MaterializeVisibleMetrics(settings.Osd.ActiveProfile, settings.Metrics);
        }

        settings.Osd.ActiveProfile!.FollowMetricPreferences = false;
        _selfApplying = true;
        try
        {
            _runtime.ApplySettings(settings, persist: true);
        }
        finally
        {
            _selfApplying = false;
        }

        RebuildOverlayItems();
        UpdatePreview();
    }

    private void OverlayItemSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RebuildOverlayItems();

    private void OverlayItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The list is intentionally a compact structure navigator; the full editor is the builder.
    }

    private void RecordingBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = Loc.T("recording_browse_title"),
            InitialDirectory = Directory.Exists(RecordingDirectoryBox.Text.Trim())
                ? RecordingDirectoryBox.Text.Trim()
                : RecordingSettings.DefaultDirectory(),
        };
        if (dialog.ShowDialog(this) == true)
        {
            RecordingDirectoryBox.Text = dialog.FolderName;
        }
    }

    private void RecordingSelectAll_Click(object sender, RoutedEventArgs e)
    {
        SetAllRecordingSensors(true);
    }

    private void RecordingClear_Click(object sender, RoutedEventArgs e)
    {
        SetAllRecordingSensors(false);
    }

    private void SetAllRecordingSensors(bool selected)
    {
        _recordingSensorSync = true;
        try
        {
            foreach (var check in _recordingChecks.Values)
            {
                check.IsChecked = selected;
            }
        }
        finally
        {
            _recordingSensorSync = false;
        }

        UpdateRecordingSummary();
        ApplyLive();
    }

    private void RecordingStart_Click(object sender, RoutedEventArgs e)
    {
        var sensors = _recordingChecks
            .Where(pair => pair.Value.IsChecked == true)
            .Select(pair => _recordingSensors[pair.Key])
            .ToList();
        if (sensors.Count == 0)
        {
            FooterStatus.Text = Loc.T("recording_no_sensors");
            return;
        }

        _selfApplying = true;
        try
        {
            _runtime.ApplySettings(CollectSettings(), persist: true);
        }
        finally
        {
            _selfApplying = false;
        }

        var result = _runtime.StartRecording(sensors);
        if (!result.Success)
        {
            FooterStatus.Text = Loc.F("recording_start_failed",
                Loc.T(RecordingErrorKey(result.Error)));
            UpdateRecordingStatus(force: true);
            return;
        }

        _lastRecordingStatusTick = 0;
        UpdateRecordingStatus(force: true);
        FooterStatus.Text = Loc.F("recording_started", result.FilePath ?? string.Empty);
    }

    private async void RecordingStop_Click(object sender, RoutedEventArgs e)
    {
        RecordingStopButton.IsEnabled = false;
        var status = await _runtime.StopRecordingAsync();
        _lastRecordingStatusTick = 0;
        UpdateRecordingStatus(force: true);
        FooterStatus.Text = status.ErrorCode == RecordingError.None
            ? Loc.F("recording_stopped", status.FilePath ?? string.Empty)
            : Loc.F("recording_stop_failed", Loc.T(RecordingErrorKey(status.ErrorCode)));
        await RefreshRecordingHistoryAsync();
    }

    private void RecordingOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var directory = RecordingDirectoryBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = RecordingSettings.DefaultDirectory();
        }

        try
        {
            Directory.CreateDirectory(directory);
            TrayIcon.OpenFolder(directory);
        }
        catch (Exception ex)
        {
            FooterStatus.Text = Loc.F("recording_open_failed", ex.Message);
        }
    }

    private async void RecordingHistoryRefresh_Click(object sender, RoutedEventArgs e)
        => await RefreshRecordingHistoryAsync();

    private async void RecordingHistoryValidate_Click(object sender, RoutedEventArgs e)
    {
        var path = SelectedRecordingPath();
        if (path is null)
        {
            RecordingHistoryDetail.Text = Loc.T("recording_history_none");
            return;
        }

        RecordingHistoryDetail.Text = Loc.T("recording_history_validating");
        try
        {
            var report = await Task.Run(() => ValidateRecording(path));
            RecordingHistoryDetail.Text = report;
        }
        catch (Exception ex)
        {
            RecordingHistoryDetail.Text = Loc.F("recording_history_validate_failed", ex.Message);
        }
    }

    private async void RecordingHistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _recordingHistorySelection = RecordingHistoryList?.SelectedItem as ListBoxItem;
        var path = SelectedRecordingPath();
        if (path is null || RecordingHistoryDetail is null)
        {
            return;
        }

        RecordingHistoryDetail.Text = Loc.T("recording_history_reading");
        try
        {
            RecordingHistoryDetail.Text = await Task.Run(() => RecordingPreview(path));
        }
        catch (Exception ex)
        {
            RecordingHistoryDetail.Text = Loc.F("recording_history_validate_failed", ex.Message);
        }
    }

    private async Task RefreshRecordingHistoryAsync()
    {
        if (RecordingHistoryList is null)
        {
            return;
        }

        var directory = RecordingDirectoryBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = RecordingSettings.DefaultDirectory();
        }

        try
        {
            var files = await Task.Run(() =>
                Directory.Exists(directory)
                    ? Directory.EnumerateFiles(directory)
                        .Where(path => path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ||
                                       path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                        .Select(path => new FileInfo(path))
                        .OrderByDescending(info => info.LastWriteTimeUtc)
                        .Take(50)
                        .ToArray()
                    : Array.Empty<FileInfo>());
            RecordingHistoryList.Items.Clear();
            foreach (var file in files)
            {
                RecordingHistoryList.Items.Add(new ListBoxItem
                {
                    Content = $"{file.Name}  ·  {FormatBytes(file.Length)}  ·  {file.LastWriteTime:g}",
                    Tag = file.FullName,
                    Padding = new Thickness(4, 2, 4, 2),
                    ToolTip = file.FullName,
                });
            }

            if (files.Length == 0)
            {
                RecordingHistoryDetail.Text = Loc.T("recording_history_none");
            }
            else
            {
                RecordingHistoryList.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            RecordingHistoryDetail.Text = Loc.F("recording_history_validate_failed", ex.Message);
        }
    }

    private string? SelectedRecordingPath()
        => (RecordingHistoryList?.SelectedItem as ListBoxItem)?.Tag as string;

    private static string RecordingPreview(string path)
    {
        var info = new FileInfo(path);
        var lines = File.ReadLines(path).Take(6).ToArray();
        var columns = lines.FirstOrDefault() ?? string.Empty;
        return $"{info.Name} · {info.Length / 1024.0:0.0} KB · {info.LastWriteTime:g}\n{columns}";
    }

    private static string ValidateRecording(string path)
    {
        var lines = File.ReadLines(path).ToArray();
        if (lines.Length == 0)
        {
            return "empty-file";
        }

        var header = ParseCsvLine(lines[0]);
        var rows = 0;
        var unavailable = 0;
        var sensorIds = new HashSet<string>(StringComparer.Ordinal);
        var duplicateGroups = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < lines.Length; i++)
        {
            var fields = ParseCsvLine(lines[i]);
            if (fields.Length < header.Length || fields.Length < 4)
            {
                continue;
            }

            rows++;
            sensorIds.Add(fields[2]);
            if (fields.Length > 6 && string.Equals(fields[6], "unavailable", StringComparison.OrdinalIgnoreCase))
            {
                unavailable++;
            }

            var key = fields[0] + "\u001f" + fields[2];
            if (!seen.Add(key))
            {
                duplicateGroups++;
            }
        }

        return $"rows={rows}; sensors={sensorIds.Count}; unavailable={unavailable}; duplicate-sample-sensor={duplicateGroups}; columns={header.Length}";
    }

    private static string[] ParseCsvLine(string line)
    {
        var values = new List<string>();
        var builder = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    builder.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (c == ',' && !quoted)
            {
                values.Add(builder.ToString());
                builder.Clear();
            }
            else
            {
                builder.Append(c);
            }
        }

        values.Add(builder.ToString());
        return values.ToArray();
    }

    private void MoveMetric(string metricId, int delta)
    {
        var metrics = _runtime.Settings.Metrics;
        var index = metrics.FindIndex(m => m.Id == metricId);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= metrics.Count)
        {
            return;
        }

        foreach (var (id, check) in _metricChecks)
        {
            var preference = metrics.FirstOrDefault(m => m.Id == id);
            if (preference is not null)
            {
                preference.Visible = check.IsChecked == true;
            }
        }

        (metrics[index], metrics[target]) = (metrics[target], metrics[index]);
        RebuildMetricRows();
        ApplyLive();
    }

    private static string MetricName(MetricDefinition definition)
        => Loc.T("metric_" + definition.Id.Replace('.', '_'));

    private static string GroupName(MetricGroup group) => Loc.T(group switch
    {
        MetricGroup.Cpu => "group_cpu",
        MetricGroup.Gpu => "group_gpu",
        MetricGroup.Vram => "group_vram",
        MetricGroup.Ram => "group_ram",
        MetricGroup.Frame => "group_frame",
        _ => "group_system",
    });

    // ------------------------------------------------------------------
    // Live updates
    // ------------------------------------------------------------------
    public void OnRuntimeTick()
    {
        if (!IsVisible)
        {
            return;
        }

        UpdateLiveValues();
        UpdatePreview();
        UpdateStatus();
        UpdateDiagnostics();
        UpdateSystemPageLive();

        if (PanelRecording.Visibility == Visibility.Visible && !_recordingListBuilt)
        {
            RebuildRecordingSensors();
        }
    }

    private void UpdateLiveValues()
    {
        var snapshot = _runtime.Cache.Latest;
        if (snapshot.Count == 0)
        {
            return;
        }

        foreach (var (id, text) in _metricValueTexts)
        {
            var definition = MetricCatalog.Find(id);
            if (definition is null)
            {
                continue;
            }

            var formatted = MetricFormatter.Format(definition, snapshot, _runtime.CurrentFps);
            text.Text = formatted.Available ? formatted.Value : "N/A";
            text.Foreground = formatted.Available
                ? (Brush)FindResource("TextBrush")
                : (Brush)FindResource("DimBrush");

            if (_metricUnitTexts.TryGetValue(id, out var unitText))
            {
                unitText.Text = formatted.Available && formatted.Unit.Length > 0
                    ? formatted.Unit
                    : definition.Unit;
                unitText.Foreground = formatted.Available
                    ? (Brush)FindResource("MutedBrush")
                    : (Brush)FindResource("DimBrush");
            }

            if (_metricSourceTexts.TryGetValue(id, out var sourceText))
            {
                var status = Loc.T(formatted.Available ? "metrics_status_available" : "metrics_status_unavailable");
                sourceText.Text = status + " · " + DescribeSource(definition, snapshot);
                sourceText.ToolTip = sourceText.Text;
                sourceText.Foreground = formatted.Available
                    ? (Brush)FindResource("MutedBrush")
                    : (Brush)FindResource("DimBrush");
            }
        }
    }

    private string DescribeSource(MetricDefinition definition, SensorSnapshot snapshot)
    {
        if (MetricCatalog.FanComponentForMetric(definition.Id) is { } fanComponent)
        {
            var channels = FanSensorReader.Channels(snapshot, fanComponent);
            if (channels.Count == 0)
            {
                return Loc.T("source_no_fans");
            }

            var wantPercent = definition.Id.EndsWith(".percent", StringComparison.Ordinal);
            if (!channels.Any(c => wantPercent ? c.Percent.HasValue : c.Rpm.HasValue))
            {
                var reason = channels
                    .Select(c => c.Note)
                    .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
                return string.IsNullOrWhiteSpace(reason)
                    ? Loc.T(wantPercent ? "source_fan_no_percent" : "source_fan_no_rpm")
                    : SensorNoteText(reason!);
            }

            var sources = string.Join(" · ", channels
                .Select(c => c.Hardware)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.Ordinal));
            return Loc.F("source_fans", channels.Count, sources.Length == 0 ? Loc.T("source_sensor") : sources);
        }

        if (definition.Group == MetricGroup.Frame)
        {
            var fps = _runtime.CurrentFps;
            if (!fps.HasValue)
            {
                return fps.Status switch
                {
                    FpsStatus.RequiresElevation => Loc.T("source_needs_admin"),
                    FpsStatus.DisplayedUnavailable => Loc.T("source_unsupported"),
                    FpsStatus.NoFrames or FpsStatus.NoForegroundApp or FpsStatus.NoPresenter
                        => Loc.T("source_no_frames"),
                    FpsStatus.Disabled => Loc.T("source_disabled"),
                    _ => Loc.T("source_unavailable"),
                };
            }

            return Loc.T(fps.IsFallback ? "source_etw_fallback" : "source_etw");
        }

        var sensorId = MetricCatalog.SensorIdFor(definition);
        if (sensorId is null || !snapshot.TryGet(sensorId, out var sensor))
        {
            return Loc.T("source_unavailable");
        }

        if (!sensor.Available)
        {
            return string.IsNullOrWhiteSpace(sensor.Note)
                ? Loc.T("source_unavailable")
                : Loc.T("source_unavailable") + " · " + SensorNoteText(sensor.Note);
        }

        var origin = sensor.Origin switch
        {
            SensorOrigin.HardwareSensor => Loc.T("source_sensor"),
            SensorOrigin.WindowsApi => Loc.T("source_windows_api"),
            SensorOrigin.Derived => Loc.T("source_derived"),
            SensorOrigin.Demo => Loc.T("source_demo"),
            _ => Loc.T("source_unsupported"),
        };

        if (sensor.Origin == SensorOrigin.Derived)
        {
            // Derived notes are internal explanations; the origin alone is the user-facing source.
            return origin;
        }

        var source = sensor.Source;
        if (source.Length > 32)
        {
            source = source[..29] + "…";
        }

        return $"{origin} · {source}";
    }

    private static string SensorNoteText(string note)
    {
        // Codes may carry a raw diagnostic suffix after ':'; only the code is translated.
        var code = note;
        var separator = note.IndexOf(':', StringComparison.Ordinal);
        if (separator > 0)
        {
            code = note[..separator];
        }

        return code switch
        {
            SensorNotes.RequiresElevation => Loc.T("sensor_note_requires_elevation"),
            SensorNotes.PawnIoNotInstalled => Loc.T("sensor_note_pawnio_not_installed"),
            SensorNotes.PawnIoAccessDenied => Loc.T("sensor_note_pawnio_access_denied"),
            SensorNotes.MsrReadZero => Loc.T("sensor_note_msr_read_zero"),
            SensorNotes.ReadingRejected => Loc.T("sensor_note_reading_rejected"),
            SensorNotes.PowerAboveLimit => Loc.T("sensor_note_power_above_limit"),
            SensorNotes.NoPlatformFanChannel => Loc.T("sensor_note_no_platform_fan_channel"),
            SensorNotes.NvmlReportsNoFans => Loc.T("sensor_note_nvml_reports_no_fans"),
            SensorNotes.MsiAcpiRequiresElevation => Loc.T("sensor_note_msi_acpi_requires_elevation"),
            SensorNotes.MsiAcpiUnavailable => Loc.T("sensor_note_msi_acpi_unavailable"),
            SensorNotes.MsiAcpiReadFailed => Loc.T("sensor_note_msi_acpi_read_failed"),
            SensorNotes.MsiFanNoPercentage => Loc.T("sensor_note_msi_fan_no_percentage"),
            SensorNotes.MsiFanZero => Loc.T("sensor_note_msi_fan_zero"),
            SensorNotes.MsiBrokerUnavailable => Loc.T("sensor_note_msi_broker_unavailable"),
            SensorNotes.MsiBrokerAccessDenied => Loc.T("sensor_note_msi_broker_access_denied"),
            SensorNotes.MsiBrokerClassMissing => Loc.T("sensor_note_msi_broker_class_missing"),
            SensorNotes.MsiBrokerReadFailed => Loc.T("sensor_note_msi_broker_read_failed"),
            SensorNotes.EnergyMeterUnavailable => Loc.T("sensor_note_energy_meter_unavailable"),
            SensorNotes.EnergyMeterChannelUnavailable => Loc.T("sensor_note_energy_meter_channel_unavailable"),
            SensorNotes.EnergyMeterZero => Loc.T("sensor_note_energy_meter_zero"),
            SensorNotes.EnergyMeterReadFailed => Loc.T("sensor_note_energy_meter_read_failed"),
            SensorNotes.FanRpmOnly => Loc.T("sensor_note_fan_rpm_only"),
            SensorNotes.FanPercentOnly => Loc.T("sensor_note_fan_percent_only"),
            _ => note,
        };
    }

    private void UpdatePreview()
    {
        if (PreviewSurface is null)
        {
            return;
        }

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var active = _runtime.Settings.Osd?.ActiveProfile;
        if (active is { IsTile: true })
        {
            var scene = _runtime.BuildTileScene(_runtime.Cache.Latest);
            PreviewSurface.SetTileContent(scene, active.TileDesign?.Scale ?? active.Tile.Scale,
                _runtime.Theme);
        }
        else
        {
            var visual = _runtime.BuildVisual(pixelsPerDip);
            PreviewSurface.SetContent(visual, _runtime.Theme);
        }

        PreviewScaleText.Text = _runtime.Settings.Overlay.Enabled
            ? Loc.T("overlay_preview_live")
            : Loc.T("overlay_preview_off");
    }

    private void UpdateOverlaySummary()
    {
        if (OverlaySummaryText is null)
        {
            return;
        }

        var overlay = _runtime.Settings.Overlay;
        var active = _runtime.Settings.Osd?.ActiveProfile;
        var position = overlay.UseCustomPosition
            ? $"{overlay.CustomX}, {overlay.CustomY}"
            : $"{AnchorName(overlay.Anchor)} + {overlay.OffsetX}, {overlay.OffsetY}";

        if (active is { IsTile: true } && active.TileDesign is { } design)
        {
            var library = Loc.T(active.Library == OsdDesignLibrary.HardwareOsd
                ? "osd_library_hardwareosd"
                : "osd_library_capframex");
            OverlaySummaryText.Text = string.Join("\n",
                Loc.F("overlay_setup_layout", Loc.F("overlay_summary_tile",
                    Math.Round(design.CanvasWidth), Math.Round(design.CanvasHeight))),
                Loc.F("overlay_setup_position", position),
                Loc.F("overlay_setup_font", design.Theme.FontSize),
                Loc.F("overlay_setup_theme", library),
                Loc.F("overlay_setup_refresh", _runtime.Settings.Monitoring.OverlayRefreshHz));
            return;
        }

        OverlaySummaryText.Text = string.Join("\n",
            Loc.F("overlay_setup_layout", LayoutName(overlay.Layout)),
            Loc.F("overlay_setup_position", position),
            Loc.F("overlay_setup_font", overlay.FontSize),
            Loc.F("overlay_setup_theme", ThemeName(overlay.Theme)),
            Loc.F("overlay_setup_refresh", _runtime.Settings.Monitoring.OverlayRefreshHz));
    }

    private void UpdatePositionInfo()
    {
        if (CustomPositionText is null)
        {
            return;
        }

        var overlay = _runtime.Settings.Overlay;
        CustomPositionText.Text = overlay.UseCustomPosition
            ? Loc.F("overlay_custom_position", overlay.CustomX, overlay.CustomY)
            : Loc.T("behavior_anchored_active");
        ResetPositionButton.IsEnabled = overlay.UseCustomPosition;
        var moveLabel = Loc.T(_runtime.IsOverlayMoving ? "tray_cancel_move" : "behavior_move");
        MoveOverlayButton.Content = moveLabel;
    }

    private void UpdateStatus(bool force = false)
    {
        var now = Environment.TickCount64;
        if (!force && now - _lastStatusTick < 700)
        {
            return;
        }

        _lastStatusTick = now;
        var stats = _runtime.Statistics;
        var snapshot = _runtime.Cache.Latest;

        var available = 0;
        var total = 0;
        foreach (var pair in snapshot.Values)
        {
            total++;
            if (pair.Value.Available)
            {
                available++;
            }
        }

        StatusText.Text = Loc.T(_runtime.IsMonitoring ? "status_monitoring" : "status_paused");
        StatusDot.Fill = _runtime.IsMonitoring
            ? (Brush)FindResource("AccentBrush")
            : (Brush)FindResource("WarnBrush");
        ToggleMonitoringButton.Content = Loc.T(_runtime.IsMonitoring ? "button_pause" : "button_resume");
        var elevated = LibreHardwareMonitorProvider.IsElevated;
        RestartAdminButton.Visibility = elevated ? Visibility.Collapsed : Visibility.Visible;

        QuickSensors.Text = Loc.F("system_cache_value", available, total);
        QuickPoll.Text = stats.PollCount == 0
            ? Loc.T("system_waiting")
            : Loc.F("system_poll_value", stats.AveragePollDurationMs, stats.IntervalMs);

        var providerBits = new StringBuilder();
        foreach (var provider in stats.Providers)
        {
            if (providerBits.Length > 0)
            {
                providerBits.Append(" · ");
            }

            providerBits.Append(provider.Name.Split('(')[0].Trim());
            providerBits.Append(provider.Available ? " ✓" : " ✗");
        }

        QuickSources.Text = providerBits.Length > 0 ? providerBits.ToString() : "—";
        MonitoringSummaryText.Text = (_runtime.DemoMode ? Loc.T("source_demo") + " · " : string.Empty) + SettingsSummary(stats);
        ElevationText.Text = Loc.T("system_elevation") + ": " +
                             Loc.T(LibreHardwareMonitorProvider.IsElevated
                                 ? "system_elevated"
                                 : "system_not_elevated");

        UpdateFpsStatus();
        UpdateHotkeyStatus();
        UpdateRecordingStatus();
        UpdatePositionInfo();
        UpdateEtwAccessUi();
        UpdateDiagnostics();
    }

    private void UpdateFpsStatus()
    {
        if (FpsStateText is null)
        {
            return;
        }

        var fps = _runtime.CurrentFps;
        FpsStateText.Text = fps.Status switch
        {
            FpsStatus.Disabled => Loc.T("fps_status_disabled"),
            FpsStatus.Starting => Loc.T("fps_status_starting"),
            FpsStatus.RequiresElevation => Loc.T("fps_status_requires_elevation"),
            FpsStatus.Capturing => Loc.F("fps_status_capturing", fps.ProcessLabel),
            FpsStatus.Holding => Loc.F("fps_status_holding", fps.ProcessLabel),
            FpsStatus.NoGame => Loc.T("fps_status_no_game"),
            FpsStatus.NoPresenter => Loc.T("fps_status_no_presenter"),
            FpsStatus.NoForegroundApp => Loc.T("fps_status_no_foreground"),
            FpsStatus.NoFrames => Loc.F("fps_status_no_frames", fps.ProcessLabel),
            FpsStatus.DisplayedUnavailable => Loc.T("fps_status_displayed_unavailable"),
            FpsStatus.Error => Loc.T("fps_status_error"),
            _ => Loc.T("fps_status_measuring_unknown"),
        };

        FpsProcessText.Text = fps.ProcessId > 0
            ? $"{fps.ProcessLabel} (pid {fps.ProcessId})"
            : Loc.T("diagnostics_no_process");
        FpsValueText.Text = fps.HasValue
            ? $"{fps.Fps:0.0} FPS · {fps.FrameTimeMs:0.0} ms" +
              (fps.OnePercentLowFps > 0 ? $" · 1% low {fps.OnePercentLowFps:0}" : string.Empty)
            : "--";
        FpsIntervalValue.Text = _runtime.Settings.Fps.UpdateIntervalMs + " ms";
        FpsBackendText.Text = _runtime.DemoMode
            ? Loc.T("fps_backend_demo")
            : Loc.T("fps_backend_etw");

        FpsDetailText.Text = fps.Status switch
        {
            FpsStatus.Capturing => fps.IsProcessFallback
                ? Loc.F("fps_detail_busiest", fps.ProcessLabel)
                : Loc.T(fps.IsFallback
                    ? "fps_detail_capturing_fallback"
                    : "fps_detail_capturing"),
            FpsStatus.Holding => Loc.T(fps.Reason == FpsStatusReason.FocusChange
                ? "fps_detail_focus_grace"
                : "fps_detail_holding"),
            FpsStatus.NoGame => Loc.T("fps_detail_no_game"),
            FpsStatus.NoPresenter => fps.Reason switch
            {
                FpsStatusReason.Desktop => Loc.T("fps_detail_desktop"),
                FpsStatusReason.Ambiguous => Loc.T("fps_detail_ambiguous"),
            FpsStatusReason.Ignored => Loc.T("fps_detail_ignored"),
                _ => Loc.T("fps_detail_no_presenter"),
            },
            FpsStatus.RequiresElevation => Loc.T("fps_detail_requires_elevation"),
            FpsStatus.NoForegroundApp => Loc.T("fps_detail_no_foreground"),
            FpsStatus.NoFrames => Loc.T("fps_detail_no_frames"),
            FpsStatus.DisplayedUnavailable => Loc.T("fps_detail_displayed_unavailable"),
            FpsStatus.Disabled => Loc.T("fps_detail_disabled"),
            FpsStatus.Starting => Loc.T("fps_detail_starting"),
            FpsStatus.Error => Loc.F("fps_detail_error", fps.Message ?? Loc.T("source_unavailable")),
            _ => string.Empty,
        };

        FpsElevateButton.Content = Loc.T("fps_elevate");
        FpsElevateButton.Visibility = fps.Status == FpsStatus.RequiresElevation
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateEtwAccessUi()
    {
        if (EtwPermissionPanel is null || _grantingEtw)
        {
            return;
        }

        var state = _runtime.EtwAccess;
        if (state.HasAccess)
        {
            EtwPermissionPanel.Visibility = Visibility.Collapsed;
            return;
        }

        EtwPermissionPanel.Visibility = Visibility.Visible;
        GrantEtwButton.Content = Loc.T("fps_permission_grant");
        GrantEtwButton.Visibility = state.NeedsRelogon ? Visibility.Collapsed : Visibility.Visible;
        EtwPermissionText.Text = state.NeedsRelogon
            ? Loc.T("fps_permission_relogon")
            : Loc.T("fps_permission_needed");
    }

    private async void GrantEtw_Click(object sender, RoutedEventArgs e)
    {
        _grantingEtw = true;
        GrantEtwButton.IsEnabled = false;
        EtwPermissionText.Text = Loc.T("fps_permission_requesting");
        try
        {
            var result = await _runtime.GrantEtwAccessAsync();
            if (result.Cancelled)
            {
                FooterStatus.Text = Loc.T("fps_permission_cancelled");
            }
            else if (result.Success)
            {
                FooterStatus.Text = Loc.T("fps_permission_granted");
            }
            else
            {
                FooterStatus.Text = Loc.F("fps_permission_failed", result.Message);
            }
        }
        catch (Exception ex)
        {
            FooterStatus.Text = Loc.F("fps_permission_failed", ex.Message);
        }
        finally
        {
            _grantingEtw = false;
            GrantEtwButton.IsEnabled = true;
            UpdateEtwAccessUi();
            UpdateStatus(force: true);
        }
    }

    private void UpdateDiagnostics()
    {
        if (DiagFpsEventsText is null)
        {
            return;
        }

        var (pid, name) = _runtime.GetForegroundApp();
        DiagFpsEventsText.Text = _runtime.FpsEventCount.ToString();
        DiagFpsPresentsText.Text = _runtime.FpsPresentCount.ToString();
        DiagFpsForegroundText.Text = pid > 0
            ? $"{name ?? Loc.T("diagnostics_none")} (pid {pid})"
            : Loc.T("diagnostics_no_process");
        DiagFpsSessionText.Text = string.IsNullOrEmpty(_runtime.FpsSessionName)
            ? Loc.T("diagnostics_none")
            : _runtime.FpsSessionName!;

        var last = _runtime.FpsLastPresentMs;
        DiagFpsLastText.Text = last > 0
            ? Loc.F("diagnostics_fps_last_value", Math.Max(0, Environment.TickCount64 - last),
                _runtime.FpsLastPresentProcessId)
            : Loc.T("diagnostics_none");
        DiagFpsCaptureModeText.Text = Loc.T(_runtime.Settings.Fps.CaptureMode == FpsCaptureMode.AllApplications
            ? "fps_capture_all_apps"
            : "fps_capture_game_only");

        var fps = _runtime.CurrentFps;
        DiagFpsTargetText.Text = fps.ProcessId > 0
            ? $"{fps.ProcessLabel} (pid {fps.ProcessId}, {Loc.T(ReasonKey(fps.Reason))})"
            : Loc.T("diagnostics_none");

        DiagElevationText.Text = Loc.T(LibreHardwareMonitorProvider.IsElevated
            ? "system_elevated"
            : "system_not_elevated");
        DiagPawnIoText.Text = Loc.T(_pawnIoStatus.State switch
        {
            PawnIoState.Available => "pawnio_state_available",
            PawnIoState.AccessDenied => "pawnio_state_access_denied",
            PawnIoState.NotInstalled => "pawnio_state_not_installed",
            _ => "pawnio_state_unknown",
        });

        var channels = FanSensorReader.Channels(_runtime.Cache.Latest);
        if (channels.Count == 0)
        {
            DiagFanChannelsText.Text = Loc.T("diagnostics_fan_channels_none");
        }
        else
        {
            var cpu = channels.Count(channel => channel.Component == FanComponent.Cpu);
            var gpu = channels.Count(channel => channel.Component == FanComponent.Gpu);
            var text = Loc.F("diagnostics_fan_channels_value",
                cpu, gpu, channels.Count - cpu - gpu);
            var unavailableReason = channels
                .Where(channel => !channel.Available)
                .Select(channel => channel.Note)
                .FirstOrDefault(note => !string.IsNullOrWhiteSpace(note));
            if (!string.IsNullOrWhiteSpace(unavailableReason))
            {
                text += " · " + SensorNoteText(unavailableReason!);
            }

            DiagFanChannelsText.Text = text;
        }

        var msiChannels = channels
            .Where(channel => channel.Source.StartsWith("MSI_ACPI", StringComparison.Ordinal))
            .ToArray();
        if (msiChannels.Length == 0)
        {
            DiagMsiAcpiText.Text = Loc.T("diagnostics_msi_acpi_absent");
        }
        else if (msiChannels.Any(channel => channel.Available))
        {
            DiagMsiAcpiText.Text = Loc.T("system_provider_ok") + " · " +
                                   string.Join(", ", msiChannels
                                       .Where(channel => channel.Rpm.HasValue)
                                       .Select(channel => $"{channel.Name}: {channel.Rpm:0} RPM"));
        }
        else
        {
            var reason = msiChannels.Select(channel => channel.Note)
                .FirstOrDefault(note => !string.IsNullOrWhiteSpace(note));
            DiagMsiAcpiText.Text = string.IsNullOrWhiteSpace(reason)
                ? Loc.T("system_provider_failed")
                : SensorNoteText(reason!);
        }

        var broker = _runtime.Polling?.FindProvider<MsiFanBrokerProvider>();
        var brokerStatus = broker?.Status;
        var brokerInstalled = brokerStatus?.ServiceInstalled ?? MsiFanBrokerClient.IsServiceInstalled();
        var brokerRunning = brokerStatus?.ServiceRunning ?? MsiFanBrokerClient.IsServiceRunning();
        var brokerReachable = brokerStatus?.PipeReachable ?? false;
        var brokerLastResponse = FormatBrokerTime(brokerStatus?.LastSuccessfulResponseUtc);
        var brokerLastWmi = FormatBrokerTime(brokerStatus?.LastWmiReadUtc);
        var brokerReason = broker is null
            ? Loc.T("broker_reason_not_used")
            : BrokerReasonText(brokerStatus?.Reason ?? FanBrokerReason.InternalError);
        DiagMsiBrokerText.Text = Loc.F("diagnostics_msi_broker_value",
            brokerInstalled ? Loc.T("common_yes") : Loc.T("common_no"),
            brokerRunning ? Loc.T("common_yes") : Loc.T("common_no"),
            brokerReachable ? Loc.T("common_yes") : Loc.T("common_no"),
            brokerLastResponse,
            brokerLastWmi,
            brokerStatus?.CpuChannels ?? 0,
            brokerStatus?.GpuChannels ?? 0,
            brokerReason);
    }

    private static string FormatBrokerTime(DateTimeOffset? value)
        => value?.ToLocalTime().ToString("HH:mm:ss") ?? Loc.T("diagnostics_none");

    private static string BrokerReasonText(FanBrokerReason reason) => Loc.T(reason switch
    {
        FanBrokerReason.None => "broker_reason_none",
        FanBrokerReason.ServiceNotInstalled => "broker_reason_service_not_installed",
        FanBrokerReason.ServiceNotRunning => "broker_reason_service_not_running",
        FanBrokerReason.PipeUnavailable => "broker_reason_pipe_unavailable",
        FanBrokerReason.Timeout => "broker_reason_timeout",
        FanBrokerReason.ProtocolError => "broker_reason_protocol_error",
        FanBrokerReason.AccessDenied => "broker_reason_access_denied",
        FanBrokerReason.ClassMissing => "broker_reason_class_missing",
        FanBrokerReason.InstanceUnavailable => "broker_reason_instance_unavailable",
        FanBrokerReason.WmiReadFailed => "broker_reason_wmi_read_failed",
        FanBrokerReason.NoFanChannels => "broker_reason_no_fan_channels",
        FanBrokerReason.InternalError => "broker_reason_internal_error",
        FanBrokerReason.Disabled => "broker_reason_disabled",
        FanBrokerReason.Unsupported => "broker_reason_unsupported",
        FanBrokerReason.Busy => "broker_reason_busy",
        FanBrokerReason.ConnectionRejected => "broker_reason_connection_rejected",
        _ => "broker_reason_not_used",
    });

    private static string ReasonKey(FpsStatusReason reason) => reason switch
    {
        FpsStatusReason.Foreground => "fps_reason_foreground",
        FpsStatusReason.UwpChild => "fps_reason_uwpchild",
        FpsStatusReason.WindowOwner => "fps_reason_windowowner",
        FpsStatusReason.SiblingRenderer => "fps_reason_siblingrenderer",
        FpsStatusReason.HelperRenderer => "fps_reason_helperrenderer",
        FpsStatusReason.Sticky => "fps_reason_sticky",
        FpsStatusReason.FocusChange => "fps_reason_focuschange",
        FpsStatusReason.EtwGap => "fps_reason_etwgap",
        FpsStatusReason.NotPresenting => "fps_reason_notpresenting",
        FpsStatusReason.Desktop => "fps_reason_desktop",
        FpsStatusReason.NoForeground => "fps_reason_noforeground",
        FpsStatusReason.Ambiguous => "fps_reason_ambiguous",
        FpsStatusReason.Ignored => "fps_reason_ignored",
        _ => "fps_reason_none",
    };

    private string SettingsSummary(PollingStatistics stats)
    {
        var overlay = _runtime.Settings.Overlay;
        var monitoring = _runtime.Settings.Monitoring;
        var poll = stats.PollCount == 0 ? "--" : stats.LastPollDurationMs.ToString("0.0");
        return Loc.F("monitoring_summary",
            LayoutName(overlay.Layout),
            overlay.FontSize.ToString("0"),
            poll,
            monitoring.OverlayRefreshHz.ToString());
    }

    private static string LayoutName(OverlayLayoutMode mode) => Loc.T(mode switch
    {
        OverlayLayoutMode.Inline => "behavior_inline",
        OverlayLayoutMode.Detailed => "behavior_detailed",
        _ => "behavior_stacked",
    });

    private static string AppThemeKey(AppThemeMode mode) => mode switch
    {
        AppThemeMode.Light => "appearance_app_white",
        AppThemeMode.Black => "appearance_app_black",
        AppThemeMode.WarmLight => "appearance_app_warm",
        AppThemeMode.Custom => "appearance_app_custom",
        _ => "appearance_app_dark",
    };

    private static string AppThemeName(AppThemeMode mode) => Loc.T(AppThemeKey(mode));

    private static string ThemeName(OverlayThemeMode mode) => Loc.T(mode switch
    {
        OverlayThemeMode.Light => "appearance_light",
        _ => "appearance_dark",
    });

    private static string AnchorName(OverlayAnchor anchor) => Loc.T(AnchorKey(anchor));

    private static string AnchorKey(OverlayAnchor anchor) => anchor switch
    {
        OverlayAnchor.TopLeft => "overlay_anchor_top_left",
        OverlayAnchor.TopCenter => "overlay_anchor_top_center",
        OverlayAnchor.TopRight => "overlay_anchor_top_right",
        OverlayAnchor.MiddleLeft => "overlay_anchor_middle_left",
        OverlayAnchor.Center => "overlay_anchor_center",
        OverlayAnchor.MiddleRight => "overlay_anchor_middle_right",
        OverlayAnchor.BottomLeft => "overlay_anchor_bottom_left",
        OverlayAnchor.BottomCenter => "overlay_anchor_bottom_center",
        _ => "overlay_anchor_bottom_right",
    };

    private static string AnchorShortLabel(OverlayAnchor anchor) => anchor switch
    {
        OverlayAnchor.TopLeft => "TL",
        OverlayAnchor.TopCenter => "TC",
        OverlayAnchor.TopRight => "TR",
        OverlayAnchor.MiddleLeft => "ML",
        OverlayAnchor.Center => "C",
        OverlayAnchor.MiddleRight => "MR",
        OverlayAnchor.BottomLeft => "BL",
        OverlayAnchor.BottomCenter => "BC",
        _ => "BR",
    };

    private static string PresetKey(string name) => name switch
    {
        "Black" => "appearance_preset_black",
        "Graphite" => "appearance_preset_graphite",
        "Light neutral" => "appearance_preset_light",
        "Transparent" => "appearance_preset_transparent",
        _ => "appearance_preset_black",
    };

    // ------------------------------------------------------------------
    // Language refresh
    // ------------------------------------------------------------------
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RefreshLocalizedUi);
            return;
        }

        RefreshLocalizedUi();
    }

    private void RefreshLocalizedUi()
    {
        var previousLoading = _loading;
        _loading = true;
        try
        {
            foreach (var button in _settingsTabButtons)
            {
                button.Content = Loc.T(SettingsTabKey((string)button.Tag));
            }

            UpdateNavAutomationNames();

            foreach (var button in _layoutButtons)
            {
                button.Content = Loc.T((OverlayLayoutMode)button.Tag switch
                {
                    OverlayLayoutMode.Inline => "behavior_inline",
                    OverlayLayoutMode.Detailed => "behavior_detailed",
                    _ => "behavior_stacked",
                });
            }

            foreach (var button in _anchorButtons)
            {
                button.ToolTip = Loc.T(AnchorKey((OverlayAnchor)button.Tag));
            }

            foreach (var button in _themeButtons)
            {
                button.Content = Loc.T((OverlayThemeMode)button.Tag == OverlayThemeMode.Light
                    ? "appearance_light"
                    : "appearance_dark");
            }

            foreach (var button in _appThemeButtons)
            {
                button.Content = Loc.T(AppThemeKey((AppThemeMode)button.Tag));
            }

            CustomAccentPicker.Content = Loc.T("appearance_custom_choose");

            foreach (var (button, name) in _presetButtons)
            {
                button.Content = Loc.T(PresetKey(name));
            }

            foreach (var (key, label) in _colorLabels)
            {
                var locKey = key switch
                {
                    "LabelColor" => "appearance_label_color",
                    "ValueColor" => "appearance_value_color",
                    "UnitColor" => "appearance_unit_color",
                    "WarningColor" => "appearance_warning_color",
                    "CriticalColor" => "appearance_critical_color",
                    _ => "appearance_background_color",
                };
                label.Text = Loc.T(locKey);
                if (_colorPickers.TryGetValue(key, out var picker))
                {
                    picker.ToolTip = Loc.F("color_title_for", label.Text);
                }
            }

            var logLevel = SelectedChoice(LogLevelCombo, _runtime.Settings.Behavior.MinimumLogLevel);
            BuildLogLevelChoices(logLevel);

            var fpsMode = SelectedChoice(FpsModeCombo, _runtime.Settings.Fps.Mode);
            BuildFpsModeChoices(fpsMode);

            var captureMode = SelectedChoice(FpsCaptureModeCombo, _runtime.Settings.Fps.CaptureMode);
            BuildFpsCaptureModeChoices(captureMode);
            UpdateFpsCaptureModeUi();

            BuildLanguageChoices(Loc.Instance.Language);
            BuildMonitorChoices(_runtime.Settings.Overlay.MonitorDevice);

            BuildRecordingChoices();
            if (_recordingChecks.Count > 0)
            {
                RebuildRecordingSensors();
            }

            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(4) ?? "0.0.0.0";
            VersionInfoText.Text = Loc.F("diagnostics_version", version) + $"  ·  .NET {Environment.Version}  ·  x64";

            RebuildMetricRows();
            if (_runtime.SystemInfo.Cached is { } systemInfo)
            {
                RenderSystemInfo(systemInfo);
            }
            else if (SystemInfoUpdatedText is not null)
            {
                SystemInfoUpdatedText.Text = Loc.T("system_info_not_read");
            }
        }
        finally
        {
            _loading = previousLoading;
        }

        UpdateHotkeyStatus();
        UpdateFpsStatus();
        UpdateStatus(force: true);
        UpdateOverlaySummary();
        UpdatePositionInfo();
        UpdatePreview();
        UpdateDiagnostics();
        _ = RefreshSystemPageAsync();
    }

    private static string SettingsTabKey(string tag) => tag switch
    {
        "appearance" => "settings_appearance",
        "languages" => "settings_languages",
        "hotkeys" => "settings_hotkeys",
        "system" => "settings_system",
        "diagnostics" => "settings_diagnostics",
        "about" => "settings_about",
        _ => "settings_general",
    };

    // ------------------------------------------------------------------
    // Commands
    // ------------------------------------------------------------------
    private void PresetCompact_Click(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
            _layoutMode = OverlayLayoutMode.Inline;
            PadXSlider.Value = 10;
            PadYSlider.Value = 6;
            RowGapSlider.Value = 2;
            ColGapSlider.Value = 8;
            BgOpacitySlider.Value = 70;
            ChkSparklines.IsChecked = false;
            ChkSeparators.IsChecked = false;
            ChkLabels.IsChecked = true;
        }
        finally
        {
            _loading = false;
        }

        SyncLayoutButtons();
        ApplyLive();
        FooterStatus.Text = Loc.F("behavior_preset_applied", Loc.T("behavior_preset_compact"));
    }

    private void PresetBalanced_Click(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
            _layoutMode = OverlayLayoutMode.Stacked;
            PadXSlider.Value = 14;
            PadYSlider.Value = 9;
            RowGapSlider.Value = 4;
            ColGapSlider.Value = 13;
            BgOpacitySlider.Value = 55;
            ChkSparklines.IsChecked = false;
            ChkSeparators.IsChecked = false;
            ChkLabels.IsChecked = true;
        }
        finally
        {
            _loading = false;
        }

        SyncLayoutButtons();
        ApplyLive();
        FooterStatus.Text = Loc.F("behavior_preset_applied", Loc.T("behavior_preset_balanced"));
    }

    private void PresetDetailed_Click(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
            _layoutMode = OverlayLayoutMode.Detailed;
            PadXSlider.Value = 14;
            PadYSlider.Value = 9;
            RowGapSlider.Value = 4;
            ColGapSlider.Value = 12;
            BgOpacitySlider.Value = 62;
            ChkSparklines.IsChecked = true;
            ChkSeparators.IsChecked = true;
            ChkLabels.IsChecked = true;
        }
        finally
        {
            _loading = false;
        }

        SyncLayoutButtons();
        ApplyLive();
        FooterStatus.Text = Loc.F("behavior_preset_applied", Loc.T("behavior_preset_detailed"));
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingButtons)
        {
            return;
        }

        var tag = (string)((ToggleButton)sender).Tag;
        _syncingButtons = true;
        try
        {
            foreach (var button in _navButtons)
            {
                button.IsChecked = ReferenceEquals(button, sender);
            }
        }
        finally
        {
            _syncingButtons = false;
        }

        PanelOverlay.Visibility = tag == "overlay" ? Visibility.Visible : Visibility.Collapsed;
        PanelMetrics.Visibility = tag == "metrics" ? Visibility.Visible : Visibility.Collapsed;
        PanelRecording.Visibility = tag == "recording" ? Visibility.Visible : Visibility.Collapsed;
        PanelSystem.Visibility = tag == "system" ? Visibility.Visible : Visibility.Collapsed;
        PanelSettings.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;

        if (tag != "metrics")
        {
            SetFpsInfo(false);
        }

        PageScroll?.ScrollToTop();

        if (tag == "system")
        {
            _ = RefreshSystemPageAsync();
        }

        if (tag == "recording")
        {
            RebuildRecordingSensors();
            UpdateRecordingStatus(force: true);
            _ = RefreshRecordingHistoryAsync();
        }

        if (tag == "settings")
        {
            SelectSettingsTab(_activeSettingsTab);
        }
    }

    private void SelectSettingsTab(string tag)
    {
        _activeSettingsTab = tag;
        _syncingSettingsTabs = true;
        try
        {
            foreach (var button in _settingsTabButtons)
            {
                button.IsChecked = string.Equals((string)button.Tag, tag, StringComparison.Ordinal);
            }
        }
        finally
        {
            _syncingSettingsTabs = false;
        }

        SettingsGeneralPanel.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        SettingsAppearancePanel.Visibility = tag == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        SettingsLanguagesPanel.Visibility = tag == "languages" ? Visibility.Visible : Visibility.Collapsed;
        SettingsHotkeysPanel.Visibility = tag == "hotkeys" ? Visibility.Visible : Visibility.Collapsed;
        SettingsSystemPanel.Visibility = tag == "system" ? Visibility.Visible : Visibility.Collapsed;
        SettingsDiagnosticsPanel.Visibility = tag == "diagnostics" ? Visibility.Visible : Visibility.Collapsed;
        SettingsAboutPanel.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;

        if (tag == "system")
        {
            _ = RefreshSystemInfoAsync(force: false);
        }
    }

    private async void RefreshSystemInfo_Click(object sender, RoutedEventArgs e)
        => await RefreshSystemInfoAsync(force: true);

    private async Task RefreshSystemPageAsync(bool force = false)
    {
        if (_systemPageLoading)
        {
            return;
        }

        _systemPageLoading = true;
        try
        {
            var snapshot = await _runtime.SystemInfo.GetAsync(force);
            var security = await Task.Run(PlatformSecurityInfo.Read);
            var features = await Task.Run(ReadPlatformFeatures);
            var software = await Task.Run(ReadSoftwareComponents);
            if (!IsLoaded)
            {
                return;
            }

            SystemView.Render(new SystemPageModel
            {
                Info = snapshot,
                Security = security,
                Features = features,
                Software = software,
                Diagnostics = BuildSystemDiagnostics(),
            }, _runtime.Cache.Latest);
            _lastSystemLiveTick = Environment.TickCount64;
        }
        finally
        {
            _systemPageLoading = false;
        }
    }

    private PlatformFeatureSnapshot ReadPlatformFeatures()
        => SystemPageComposer.ReadFeatures(_runtime.Cache.Latest,
            _runtime.Polling?.FindProvider<NvmlProvider>());

    private IReadOnlyList<SoftwareComponentInfo> ReadSoftwareComponents()
        => SystemPageComposer.ReadSoftware(
            _runtime.Polling?.FindProvider<NvmlProvider>(),
            _runtime.Polling?.FindProvider<MsiFanBrokerProvider>()?.Status,
            _pawnIoStatus);

    private void UpdateSystemPageLive(bool force = false)
    {
        if (PanelSystem.Visibility != Visibility.Visible)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (!force && now - _lastSystemLiveTick < 500)
        {
            return;
        }

        _lastSystemLiveTick = now;
        SystemView.UpdateLive(_runtime.Cache.Latest, BuildSystemDiagnostics());
    }

    private IReadOnlyList<SystemDiagnostic> BuildSystemDiagnostics()
    {
        var snapshot = _runtime.Cache.Latest;
        return SystemPageComposer.BuildDiagnostics(new SystemDiagnosticInput(
            LibreHardwareMonitorProvider.IsElevated,
            _pawnIoStatus,
            _runtime.Polling?.FindProvider<MsiFanBrokerProvider>()?.Status,
            snapshot.Contains("fan.cpu.0.rpm"),
            snapshot.Contains("fan.gpu.0.rpm"),
            _runtime.Statistics,
            CountAvailable(snapshot, SensorIds.CpuUsage, SensorIds.CpuTemperature, SensorIds.CpuClock,
                SensorIds.CpuPower),
            CountAvailable(snapshot, SensorIds.GpuUsage, SensorIds.GpuTemperature, SensorIds.GpuClock,
                SensorIds.VramUsed, SensorIds.GpuPower),
            _runtime.EtwAccess,
            _runtime.Recorder.IsRecording,
            _runtime.CurrentFps));
    }

    private static int CountAvailable(SensorSnapshot snapshot, params string[] ids)
        => ids.Count(snapshot.IsAvailable);

    private async Task RefreshSystemInfoAsync(bool force)
    {
        if (_systemInfoLoading)
        {
            return;
        }

        _systemInfoLoading = true;
        RefreshSystemInfoButton.IsEnabled = false;
        if (_runtime.SystemInfo.Cached is null)
        {
            SystemInfoUpdatedText.Text = Loc.T("system_info_reading");
        }

        try
        {
            var snapshot = await _runtime.SystemInfo.GetAsync(force);
            RenderSystemInfo(snapshot);
        }
        catch (Exception ex)
        {
            SystemInfoErrorText.Text = Loc.F("system_info_error", ex.Message);
            SystemInfoErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            _systemInfoLoading = false;
            RefreshSystemInfoButton.IsEnabled = true;
        }
    }

    private void RenderSystemInfo(SystemInfoSnapshot snapshot)
    {
        if (SystemWindowsRows is null)
        {
            return;
        }

        SystemWindowsRows.Children.Clear();
        SystemCpuRows.Children.Clear();
        SystemRamRows.Children.Clear();
        SystemMotherboardRows.Children.Clear();
        SystemGpuRows.Children.Clear();
        SystemStorageRows.Children.Clear();

        SystemInfoUpdatedText.Text = snapshot.CapturedAtMs == 0
            ? Loc.T("system_info_not_read")
            : Loc.F("system_info_updated",
                DateTimeOffset.FromUnixTimeMilliseconds(snapshot.CapturedAtMs).ToLocalTime().ToString("HH:mm:ss"));
        SystemInfoErrorText.Visibility = string.IsNullOrWhiteSpace(snapshot.Error)
            ? Visibility.Collapsed
            : Visibility.Visible;
        SystemInfoErrorText.Text = string.IsNullOrWhiteSpace(snapshot.Error)
            ? string.Empty
            : Loc.F("system_info_error", snapshot.Error);

        RenderInventory(SystemWindowsRows, snapshot.Windows);
        RenderInventory(SystemCpuRows, snapshot.Cpu);
        RenderInventory(SystemRamRows, snapshot.Ram);
        RenderInventory(SystemMotherboardRows, snapshot.Motherboard);

        if (snapshot.Gpus.Count == 0)
        {
            AddInfoRow(SystemGpuRows, Loc.T("system_info_gpu_name"), null, SystemInfoNotes.NotReported);
        }
        else
        {
            AddInfoRow(SystemGpuRows, Loc.T("system_info_gpu_count"),
                snapshot.Gpus.Count.ToString(CultureInfo.InvariantCulture));
            for (var index = 0; index < snapshot.Gpus.Count; index++)
            {
                var gpu = snapshot.Gpus[index];
                SystemGpuRows.Children.Add(new TextBlock
                {
                    Text = Loc.F("system_gpu_index", index + 1),
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, index == 0 ? 0 : 8, 0, 2),
                });
                AddInfoRow(SystemGpuRows, Loc.T("system_info_gpu_name"), gpu.Name);
                AddInfoRow(SystemGpuRows, Loc.T("system_info_gpu_vendor"), gpu.Vendor,
                    gpu.Vendor is null ? SystemInfoNotes.NotReported : null);
                AddInfoRow(SystemGpuRows, Loc.T("system_info_gpu_driver"), gpu.DriverVersion,
                    gpu.DriverVersion is null ? SystemInfoNotes.NotReported : null);
                AddInfoRow(SystemGpuRows, Loc.T("system_info_gpu_vram"),
                    gpu.Vram ?? Loc.T("common_na"), gpu.VramNote);
            }
        }

        if (snapshot.Storage.Count == 0)
        {
            AddInfoRow(SystemStorageRows, Loc.T("system_info_storage_name"), null, SystemInfoNotes.NotReported);
        }
        else
        {
            for (var index = 0; index < snapshot.Storage.Count; index++)
            {
                var disk = snapshot.Storage[index];
                SystemStorageRows.Children.Add(new TextBlock
                {
                    Text = Loc.F("system_storage_index", index + 1),
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, index == 0 ? 0 : 8, 0, 2),
                });
                AddInfoRow(SystemStorageRows, Loc.T("system_info_storage_name"), disk.Name);
                AddInfoRow(SystemStorageRows, Loc.T("system_info_storage_type"), disk.Type,
                    disk.Type is null ? SystemInfoNotes.StorageTypeUnavailable : null);
                AddInfoRow(SystemStorageRows, Loc.T("system_info_storage_total"),
                    SystemInfoFormat.FormatBytes(disk.TotalBytes),
                    disk.TotalBytes is null ? SystemInfoNotes.NotReported : null);
                AddInfoRow(SystemStorageRows, Loc.T("system_info_storage_free"),
                    SystemInfoFormat.FormatBytes(disk.FreeBytes),
                    disk.FreeBytes is null ? disk.Note ?? SystemInfoNotes.NotReported : null);
                AddInfoRow(SystemStorageRows, Loc.T("system_info_storage_used"),
                    SystemInfoFormat.FormatBytes(disk.UsedBytes),
                    disk.UsedBytes is null ? disk.Note ?? SystemInfoNotes.NotReported : null);
            }
        }
    }

    private void RenderInventory(StackPanel panel, IReadOnlyList<SystemInfoItem> items)
    {
        if (items.Count == 0)
        {
            AddInfoRow(panel, Loc.T("system_info_unknown"), null, SystemInfoNotes.NotReported);
            return;
        }

        foreach (var item in items)
        {
            AddInfoRow(panel,
                Loc.T("system_info_" + item.Key.Replace('.', '_')),
                item.Value,
                item.Note);
        }
    }

    private void AddInfoRow(StackPanel panel, string label, string? value, string? noteCode = null)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelText = new TextBlock
        {
            Text = label,
            Style = (Style)FindResource("FieldLabel"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(labelText, 0);
        row.Children.Add(labelText);

        var values = new StackPanel();
        values.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(value) ? Loc.T("common_na") : value,
            Style = (Style)FindResource("ValueText"),
            TextWrapping = TextWrapping.Wrap,
        });

        var note = SystemNoteText(noteCode);
        if (!string.IsNullOrWhiteSpace(note))
        {
            values.Children.Add(new TextBlock
            {
                Text = note,
                Style = (Style)FindResource("Muted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 0),
            });
        }

        Grid.SetColumn(values, 1);
        row.Children.Add(values);
        panel.Children.Add(row);
    }

    private static string SystemNoteText(string? noteCode)
    {
        if (string.IsNullOrWhiteSpace(noteCode))
        {
            return string.Empty;
        }

        var code = noteCode;
        var separator = noteCode.IndexOf(':', StringComparison.Ordinal);
        if (separator > 0)
        {
            code = noteCode[..separator];
        }

        return code switch
        {
            SystemInfoNotes.WmiUnavailable => Loc.T("system_note_wmi_unavailable"),
            SystemInfoNotes.WmiAccessDenied => Loc.T("system_note_wmi_access_denied"),
            SystemInfoNotes.NotReported => Loc.T("system_note_not_reported"),
            SystemInfoNotes.SharedMemory => Loc.T("system_note_shared_memory"),
            SystemInfoNotes.VramNotReported => Loc.T("system_note_vram_not_reported"),
            SystemInfoNotes.StorageVolumesUnavailable => Loc.T("system_note_storage_volumes_unavailable"),
            SystemInfoNotes.StorageTypeUnavailable => Loc.T("system_note_storage_type_unavailable"),
            SystemInfoNotes.RatedClock => Loc.T("system_note_rated_clock"),
            SystemInfoNotes.CollectionFailed => Loc.T("system_note_collection_failed"),
            _ => noteCode,
        };
    }

    private void OpenOverlaySettings_Click(object sender, RoutedEventArgs e)
    {
        NavOverlay.IsChecked = true;
    }

    private void MoveOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (_runtime.IsOverlayMoving)
        {
            _runtime.CancelOverlayMove();
            FooterStatus.Text = Loc.T("overlay_move_cancelled");
            return;
        }

        _runtime.BeginOverlayMove();
        FooterStatus.Text = Loc.T("msg_move_started");
    }

    private void ResetPosition_Click(object sender, RoutedEventArgs e)
    {
        var settings = _runtime.Settings.Clone();
        settings.Overlay.UseCustomPosition = false;
        _selfApplying = true;
        try
        {
            _runtime.ApplySettings(settings, persist: true);
        }
        finally
        {
            _selfApplying = false;
        }

        LoadFromSettings();
        FooterStatus.Text = Loc.T("overlay_reset_position");
    }

    public void OnOverlayMoveStateChanged() => UpdatePositionInfo();

    public void OnOverlayPositionSaved()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(OnOverlayPositionSaved);
            return;
        }

        LoadFromSettings();
        FooterStatus.Text = Loc.T("overlay_move_saved");
    }

    private void ToggleMonitoring_Click(object sender, RoutedEventArgs e)
    {
        _runtime.ToggleMonitoring();
        UpdateStatus(force: true);
        FooterStatus.Text = Loc.T(_runtime.IsMonitoring ? "msg_monitoring_resumed" : "msg_monitoring_paused");
    }

    private void Elevate_Click(object sender, RoutedEventArgs e) => _runtime.RestartElevated();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _selfApplying = true;
        _runtime.ApplySettings(CollectSettings(), persist: true);
        _selfApplying = false;
        _runtime.SaveNow();

        // Keep the acknowledgement on the button itself so the footer does not grow a
        // transient message row. Both labels stay in the layout, so the button width is
        // reserved and Reload/Reset do not move while the label changes.
        FooterStatus.Text = string.Empty;
        ShowSaveFeedback(saved: true);
        _saveFeedbackTimer.Stop();
        _saveFeedbackTimer.Start();
    }

    private void ShowSaveFeedback(bool saved)
    {
        SaveNowLabel.Opacity = saved ? 0 : 1;
        SaveSavedLabel.Opacity = saved ? 1 : 0;
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        LoadFromSettings();
        FooterStatus.Text = Loc.T("msg_reloaded");
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
            _runtime.ApplySettings(AppSettings.CreateDefault(), persist: true);
        }
        finally
        {
            _loading = false;
        }

        LoadFromSettings();
        FooterStatus.Text = Loc.T("msg_defaults");
    }

    private void HideToTray_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        FooterStatus.Text = Loc.T("msg_hide_tray");
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => _runtime.Exit();

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => _runtime.OpenSettingsFolder();

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e) => _runtime.OpenLogFolder();

    private void ShowAllMetrics_Click(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
            foreach (var check in _metricChecks.Values.ToList())
            {
                check.IsChecked = true;
            }
        }
        finally
        {
            _loading = false;
        }

        ApplyMetricSelection();
        FooterStatus.Text = Loc.T("metrics_all_applied");
    }

    private void DefaultMetrics_Click(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
            foreach (var (id, check) in _metricChecks.ToList())
            {
                var definition = MetricCatalog.Find(id);
                check.IsChecked = definition?.DefaultVisible ?? false;
            }
        }
        finally
        {
            _loading = false;
        }

        ApplyMetricSelection();
        FooterStatus.Text = Loc.T("metrics_defaults_applied");
    }

    /// <summary>
    /// Persists the current checkbox matrix after bulk changes (Show all / Default set). The FPS
    /// row doubles as the module switch, so it also drives <see cref="FpsSettings.Enabled"/>.
    /// </summary>
    private void ApplyMetricSelection()
    {
        var settings = _runtime.Settings.Clone();
        settings.Fps.Enabled = _metricChecks.TryGetValue("frame.fps", out var fpsCheck) &&
                               fpsCheck.IsChecked == true;
        settings.Metrics = _runtime.Settings.Metrics
            .Select(m => new MetricPreference
            {
                Id = m.Id,
                Visible = _metricChecks.TryGetValue(m.Id, out var check) && check.IsChecked == true,
            })
            .ToList();

        var activeProfile = settings.Osd.ActiveProfile;
        if (activeProfile is { IsTile: true })
        {
            OsdMetricBridge.ApplyTileMetricSet(activeProfile, settings.Metrics);
        }
        else if (activeProfile is not null && !FollowsMetricMatrix(activeProfile))
        {
            foreach (var preference in settings.Metrics)
            {
                if (MetricCatalog.Find(preference.Id) is { } definition)
                {
                    OsdMetricBridge.SetMetricVisible(activeProfile, definition, preference.Visible,
                        materialiseWhenMissing: preference.Visible);
                }
            }
        }

        _selfApplying = true;
        try
        {
            _runtime.ApplySettings(settings, persist: true);
        }
        finally
        {
            _selfApplying = false;
        }

        LoadFromSettings();
        UpdatePreview();
        UpdateStatus(force: true);
    }

    private void SaveSensorAudit_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = Loc.T("diagnostics_sensor_audit"),
            Filter = Loc.T("audit_filter"),
            FileName = $"sensor-audit-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            DefaultExt = ".csv",
            AddExtension = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var snapshot = _runtime.Cache.Latest;
            var entries = SensorAudit.Build(_runtime.Settings, snapshot, _runtime.CurrentFps, now);
            SensorAudit.WriteCsv(dialog.FileName, entries, snapshot);
            FooterStatus.Text = Loc.F("diagnostics_audit_saved", dialog.FileName);
        }
        catch (Exception ex)
        {
            FooterStatus.Text = Loc.F("diagnostics_audit_failed", ex.Message);
        }
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(BuildDiagnosticsText());
            FooterStatus.Text = Loc.T("diagnostics_copied");
        }
        catch (Exception ex)
        {
            FooterStatus.Text = Loc.F("diagnostics_copy_failed", ex.Message);
        }
    }

    private string BuildDiagnosticsText()
    {
        var builder = new StringBuilder();
        var stats = _runtime.Statistics;
        builder.AppendLine($"HardwareOSD diagnostics — {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"Version: {Assembly.GetExecutingAssembly().GetName().Version}");
        builder.AppendLine($"OS: {Environment.OSVersion}  Runtime: {Environment.Version}  x64: {Environment.Is64BitProcess}");
        builder.AppendLine($"Elevated: {LibreHardwareMonitorProvider.IsElevated}  Demo: {_runtime.DemoMode}  Language: {Loc.Instance.Language}");
        builder.AppendLine($"Working set: {Environment.WorkingSet / 1024 / 1024} MB");
        builder.AppendLine($"Overlay: {(_runtime.IsOverlayVisible ? "visible" : "hidden")}  Monitoring: {_runtime.IsMonitoring}");
        builder.AppendLine($"Polls: {stats.PollCount}  failures: {stats.FailedPollCount}  avg: {stats.AveragePollDurationMs:0.00} ms");
        builder.AppendLine($"FPS events: {_runtime.FpsEventCount}  presents: {_runtime.FpsPresentCount}  session: {_runtime.FpsSessionName ?? "—"}");
        var (foregroundPid, foregroundName) = _runtime.GetForegroundApp();
        builder.AppendLine($"Foreground: {foregroundName ?? "none"} ({foregroundPid})");
        builder.AppendLine();
        builder.AppendLine("Providers:");
        foreach (var provider in stats.Providers)
        {
            builder.AppendLine($"  {provider.Name} [{provider.Category}] available={provider.Available} failures={provider.ConsecutiveFailures}");
            if (!string.IsNullOrEmpty(provider.LastError))
            {
                builder.AppendLine($"    error: {provider.LastError}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Provider details:");
        if (_runtime.Polling is { } polling)
        {
            foreach (var line in polling.DescribeProviders())
            {
                builder.AppendLine("  " + line);
            }
        }
        else
        {
            builder.AppendLine("  monitoring is not running");
        }

        builder.AppendLine();
        builder.AppendLine("Sensors:");
        foreach (var pair in _runtime.Cache.Latest.Values.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var value = pair.Value;
            builder.AppendLine(value.Available
                ? $"  {value.Id,-28} {value.Value,12:0.00} {value.Unit,-4} {value.Origin,-14} {value.Source}"
                : $"  {value.Id,-28} {"N/A",12}      {value.Origin,-14} {value.Source}");
        }

        builder.AppendLine();
        builder.AppendLine("FPS:");
        if (_runtime.Fps is { } fpsMonitor)
        {
            foreach (var line in fpsMonitor.Describe())
            {
                builder.AppendLine("  " + line);
            }
        }
        else
        {
            builder.AppendLine("  FPS monitoring is disabled");
        }

        return builder.ToString();
    }

    // ------------------------------------------------------------------
    // Window behaviour
    // ------------------------------------------------------------------
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (AllowClose)
        {
            return;
        }

        e.Cancel = true;
        if (_runtime.Settings.Behavior.CloseToTray)
        {
            Hide();
        }
        else
        {
            // Exit closes MainWindow itself. Calling it inside Closing would re-enter Close while
            // WPF still owns the closing event, which throws InvalidOperationException and leaves
            // the process running. Let the current close event finish, then start the real exit.
            Dispatcher.BeginInvoke(new Action(_runtime.Exit), DispatcherPriority.Normal);
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _runtime.Settings.Behavior.MinimizeToTray)
        {
            Hide();
            WindowState = WindowState.Normal;
        }
    }
}
