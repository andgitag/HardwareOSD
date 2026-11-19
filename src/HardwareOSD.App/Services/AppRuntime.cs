using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using HardwareOSD.App.Localization;
using HardwareOSD.App.Native;
using HardwareOSD.App.Overlay;
using HardwareOSD.App.Theming;
using HardwareOSD.App.Views;
using HardwareOSD.Core.Config;
using HardwareOSD.Core.Diagnostics;
using HardwareOSD.Core.Fps;
using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.Hardware.Providers;
using HardwareOSD.Core.Input;
using HardwareOSD.Core.Metrics;
using HardwareOSD.Core.Overlay;
using HardwareOSD.Core.Recording;
using HardwareOSD.Core.Security;
using HardwareOSD.Core.SystemInfo;
using HardwareOSD.Core.TileOsd;

namespace HardwareOSD.App.Services;

/// <summary>
/// Owns the application composition root: configuration, sensor polling, presentation timer,
/// overlay window, tray icon and the settings window. All UI state lives on the WPF dispatcher
/// thread; hardware polling runs on its own background thread.
/// </summary>
public sealed class AppRuntime : IDisposable
{
    private const int HotkeyOverlayId = 0x4F53;
    private const int HotkeyMainWindowId = 0x4F54;
    private const int HotkeyStartRecordingId = 0x4F55;
    private const int HotkeyStopRecordingId = 0x4F56;
    private const int HotkeyRestartId = 0x4F57;

    private readonly ILog _log;
    private readonly SettingsStore _store;
    private readonly DispatcherTimer _presentationTimer;
    private readonly DispatcherTimer _saveTimer;
    private readonly long _sessionStartMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private OverlayTheme _theme;
    private Window? _hotkeyWindow;
    private IntPtr _hotkeyHandle;
    private bool _overlayHotkeyRegistered;
    private bool _mainHotkeyRegistered;
    private bool _startRecordingHotkeyRegistered;
    private bool _stopRecordingHotkeyRegistered;
    private bool _restartHotkeyRegistered;
    private HotkeyGesture _registeredOverlayGesture;
    private HotkeyGesture _registeredMainGesture;
    private HotkeyGesture _registeredStartRecordingGesture;
    private HotkeyGesture _registeredStopRecordingGesture;
    private HotkeyGesture _registeredRestartGesture;
    private int _topmostCounter;
    private bool _disposed;
    private bool _exiting;
    private readonly bool _manageStartupRegistration;
    private EtwAccessState? _etwAccess;
    private long _etwAccessTimestamp;

    public AppRuntime(ILog log, SettingsStore store, AppSettings settings, bool demo, string? configStatus,
        bool manageStartupRegistration = true)
    {
        _log = log;
        _store = store;
        Settings = settings;
        DemoMode = demo;
        ConfigStatus = configStatus;
        _manageStartupRegistration = manageStartupRegistration;
        _theme = OverlayTheme.Create(settings);

        Cache = new SensorCache();
        History = new SensorHistory(180);
        History.Track(SensorProviderFactory.HistorySensorIds());
        Recorder = new SensorRecorder(Cache, _log);
        SystemInfo = new SystemInfoService(new WindowsSystemInfoCollector());

        _presentationTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = ComputePresentationInterval(),
        };
        _presentationTimer.Tick += OnPresentationTick;

        _saveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(700),
        };
        _saveTimer.Tick += (_, _) => SaveNow();

        // Machine names are used by the CapFrameX row templates and by text tiles. Collecting them
        // is asynchronous and cached, so the first overlay frame renders N/A rather than blocking.
        _ = SystemInfo.GetAsync();
    }

    public AppSettings Settings { get; private set; }

    public ILog Logger => _log;

    public SensorCache Cache { get; }

    public SensorHistory History { get; }

    public SensorRecorder Recorder { get; }

    /// <summary>Cached Windows/motherboard/storage inventory for Settings → System.</summary>
    public SystemInfoService SystemInfo { get; }

    public FpsCache FpsCache { get; } = new();

    public FpsMonitorService? Fps { get; private set; }

    public FpsSnapshot CurrentFps => FpsCache.Latest;

    public long FpsEventCount => Fps?.CaptureEventCount ?? 0;

    public long FpsPresentCount => Fps?.CapturePresentCount ?? 0;

    public long FpsLastPresentMs => Fps?.LastPresentWallMs ?? 0;

    public int FpsLastPresentProcessId => Fps?.LastPresentProcessId ?? 0;

    public string? FpsSessionName => Fps?.CaptureSessionName;

    public bool IsOverlayMoving => Overlay?.IsMoving ?? false;

    /// <summary>
    /// Cached ETW permission state. The local group is queried at most every five seconds; the
    /// FPS publisher runs far more often and must not hit NetAPI on every tick.
    /// </summary>
    public EtwAccessState EtwAccess
    {
        get
        {
            var now = Environment.TickCount64;
            if (_etwAccess is null || now - _etwAccessTimestamp > 5_000)
            {
                _etwAccess = PerformanceLogUsers.GetState();
                _etwAccessTimestamp = now;
            }

            return _etwAccess.Value;
        }
    }

    public void RefreshEtwAccess()
    {
        _etwAccess = null;
        _etwAccessTimestamp = 0;
    }

    /// <summary>
    /// Runs the tiny elevated grant helper (the same executable with --grant-etw). The helper writes
    /// its result to a temporary file, adds the current user to Performance Log Users, and exits; no
    /// administrator token or background task remains. The caller must show the UAC explanation first.
    /// </summary>
    public async Task<EtwGrantResult> GrantEtwAccessAsync(CancellationToken cancellationToken = default)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) ||
            !Path.GetFileNameWithoutExtension(exe).Contains("HardwareOSD", StringComparison.OrdinalIgnoreCase))
        {
            var candidate = Path.Combine(AppContext.BaseDirectory, "HardwareOSD.exe");
            if (File.Exists(candidate))
            {
                exe = candidate;
            }
        }

        if (string.IsNullOrEmpty(exe))
        {
            return new EtwGrantResult(false, false, Loc.T("fps_permission_helper_missing"));
        }

        var target = PerformanceLogUsers.CurrentUserName;
        var resultPath = Path.Combine(Path.GetTempPath(), $"HardwareOSD-etw-grant-{Environment.ProcessId}.txt");
        try
        {
            if (File.Exists(resultPath))
            {
                File.Delete(resultPath);
            }
        }
        catch
        {
            // A stale result file would only affect the diagnostic message.
        }

        try
        {
            var process = Process.Start(new ProcessStartInfo(exe,
                $"--grant-etw \"{target}\" \"{resultPath}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
            });

            if (process is null)
            {
                return new EtwGrantResult(false, false, Loc.T("fps_permission_failed"));
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var message = File.Exists(resultPath)
                ? await File.ReadAllTextAsync(resultPath, cancellationToken).ConfigureAwait(false)
                : Loc.T("fps_permission_helper_missing");
            var success = process.ExitCode == 0;
            RefreshEtwAccess();
            if (success)
            {
                _log.Info("FPS", "Performance Log Users grant helper completed: " + message);
            }
            else
            {
                _log.Warn("FPS", "Performance Log Users grant helper failed: " + message);
            }

            return new EtwGrantResult(success, false, message);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            _log.Info("FPS", "Performance Log Users grant request was cancelled at UAC");
            return new EtwGrantResult(false, true, Loc.T("fps_permission_cancelled"));
        }
        catch (OperationCanceledException)
        {
            return new EtwGrantResult(false, true, Loc.T("fps_permission_cancelled"));
        }
        catch (Exception ex)
        {
            _log.Warn("FPS", "Performance Log Users grant request failed", ex);
            return new EtwGrantResult(false, false, ex.Message);
        }
    }

    private readonly Win32ForegroundAppProbe _foregroundProbe = new();

    /// <summary>Foreground process for diagnostics, independent of FPS capture availability.</summary>
    public (int ProcessId, string? Name) GetForegroundApp()
    {
        var pid = _foregroundProbe.GetForegroundProcessId();
        return (pid, pid > 0 ? _foregroundProbe.GetProcessName(pid) : null);
    }

    public OverlayTheme Theme => _theme;

    public bool DemoMode { get; }

    /// <summary>False for portable QA profiles, where the real Run key must never be touched.</summary>
    public bool CanManageStartupRegistration => _manageStartupRegistration;

    public string? ConfigStatus { get; }

    public SensorPollingService? Polling { get; private set; }

    public OverlayWindow? Overlay { get; private set; }

    public TrayIcon? Tray { get; private set; }

    public MainWindow? MainWindow { get; private set; }

    public bool IsMonitoring => Polling?.IsRunning ?? false;

    public bool IsOverlayVisible => Overlay is { IsVisible: true };

    public bool IsExiting => _exiting;

    public PollingStatistics Statistics => Polling?.Statistics ?? PollingStatistics.Idle;

    public event EventHandler? SettingsApplied;

    public event EventHandler? MonitoringStateChanged;

    public event EventHandler? OverlayMoveStateChanged;

    public void NotifyOverlayMoveStateChanged()
    {
        OverlayMoveStateChanged?.Invoke(this, EventArgs.Empty);
        Tray?.Sync();
        MainWindow?.OnOverlayMoveStateChanged();
    }

    public void Initialize(bool showWindow)
    {
        StartPolling();

        if (Settings.Overlay.Enabled)
        {
            ShowOverlay();
        }

        if (Settings.Fps.Enabled)
        {
            StartFps();
        }

        Loc.Instance.LanguageChanged += (_, _) => Tray?.Sync();
        CreateHotkeyWindow();
        Tray = new TrayIcon(this);
        if (showWindow)
        {
            ShowSettings();
        }

        MaybePromptEtwPermission();
        _presentationTimer.Start();
        LogStartupRegistrationState();
        _log.Info("App", $"Runtime initialised (demo={DemoMode}, refresh={Settings.Monitoring.OverlayRefreshHz} Hz, poll={Settings.Monitoring.PollIntervalMs} ms, fps={Settings.Fps.Enabled})");
    }

    private void MaybePromptEtwPermission()
    {
        if (DemoMode || !Settings.Fps.Enabled || Settings.Behavior.EtwPermissionPrompted)
        {
            return;
        }

        var state = EtwAccess;
        if (!state.HasAccess && Tray is not null)
        {
            Tray.ShowBalloon(
                Loc.T("tray_fps_permission_title"),
                Loc.T(state.NeedsRelogon ? "tray_fps_relogon_text" : "tray_fps_permission_text"));
            _log.Info("FPS", $"One-time ETW permission prompt shown (state={state.Kind})");
        }

        Settings.Behavior.EtwPermissionPrompted = true;
        SaveNow();
    }

    /// <summary>Applies a settings snapshot (from the UI, tray or hotkey) and optionally persists it.</summary>
    public void ApplySettings(AppSettings settings, bool persist)
    {
        settings.Normalize();
        var previous = Settings;
        var providerChange = previous.Monitoring.EnableNvml != settings.Monitoring.EnableNvml ||
                             previous.Monitoring.EnableLibreHardwareMonitor != settings.Monitoring.EnableLibreHardwareMonitor ||
                             previous.Monitoring.EnableMsiAcpi != settings.Monitoring.EnableMsiAcpi ||
                             previous.Monitoring.EnableMsiFanBroker != settings.Monitoring.EnableMsiFanBroker ||
                             previous.Monitoring.UseThermalZoneAsCpuTemperature != settings.Monitoring.UseThermalZoneAsCpuTemperature;
        var languageChanged = !string.Equals(previous.Behavior.Language, settings.Behavior.Language, StringComparison.OrdinalIgnoreCase);
        var appThemeChanged = previous.Behavior.AppTheme != settings.Behavior.AppTheme;
        var appAccentChanged = !string.Equals(previous.Behavior.AppAccentColor, settings.Behavior.AppAccentColor,
            StringComparison.OrdinalIgnoreCase);
        var fpsEnabledChanged = previous.Fps.Enabled != settings.Fps.Enabled;
        var fpsConfigChanged = previous.Fps.Mode != settings.Fps.Mode ||
                               previous.Fps.UpdateIntervalMs != settings.Fps.UpdateIntervalMs ||
                               previous.Fps.MeasureWindowedApps != settings.Fps.MeasureWindowedApps ||
                               previous.Fps.CaptureMode != settings.Fps.CaptureMode;

        Settings = settings.Clone();
        _theme = OverlayTheme.Create(Settings);

        if (appThemeChanged || appAccentChanged)
        {
            AppThemeManager.Apply(Settings.Behavior.AppTheme, Settings.Behavior.AppAccentColor);
        }

        if (languageChanged)
        {
            Loc.Instance.SetLanguage(Settings.Behavior.Language);
            _log.Info("App", $"Interface language set to {Settings.Behavior.Language}");
        }

        if (Polling is not null)
        {
            Polling.IntervalMs = Settings.Monitoring.PollIntervalMs;
            Polling.UseThermalZoneAsCpuTemperature = Settings.Monitoring.UseThermalZoneAsCpuTemperature;
            if (providerChange && IsMonitoring)
            {
                RestartPolling();
            }
        }

        if (Settings.Fps.Enabled)
        {
            if (fpsEnabledChanged || Fps is null)
            {
                StartFps();
            }
            else if (fpsConfigChanged)
            {
                Fps.ApplySettings(Settings.Fps);
            }
        }
        else if (Fps is not null)
        {
            StopFps();
        }

        var interval = ComputePresentationInterval();
        if (_presentationTimer.Interval != interval)
        {
            _presentationTimer.Interval = interval;
        }

        if (Settings.Overlay.Enabled)
        {
            ShowOverlay();
            Overlay?.ApplySettings();
        }
        else
        {
            CloseOverlay();
        }

        if (_log is FileLogger fileLogger)
        {
            fileLogger.MinimumLevel = Settings.Behavior.MinimumLogLevel;
        }

        UpdateHotkey();
        Tray?.Sync();
        SettingsApplied?.Invoke(this, EventArgs.Empty);

        if (persist)
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }
    }

    public void SaveNow()
    {
        _saveTimer.Stop();
        if (_store.Save(Settings))
        {
            _log.Debug("Config", "Settings saved");
        }
    }

    public void StartPolling()
    {
        if (Polling is { IsRunning: true })
        {
            return;
        }

        if (Polling is null)
        {
            Polling = new SensorPollingService(
                SensorProviderFactory.Create(Settings, DemoMode),
                Cache,
                History,
                _log,
                Settings.Monitoring.PollIntervalMs)
            {
                UseThermalZoneAsCpuTemperature = Settings.Monitoring.UseThermalZoneAsCpuTemperature,
            };
        }

        Polling.Start();
        MonitoringStateChanged?.Invoke(this, EventArgs.Empty);
        Tray?.Sync();
    }

    public void StopPolling()
    {
        if (Polling is null || !Polling.IsRunning)
        {
            return;
        }

        Polling.Stop();
        Cache.Clear();
        MonitoringStateChanged?.Invoke(this, EventArgs.Empty);
        Tray?.Sync();
    }

    public void ToggleMonitoring()
    {
        if (IsMonitoring)
        {
            StopPolling();
            _log.Info("App", "Monitoring paused by user");
        }
        else
        {
            StartPolling();
            _log.Info("App", "Monitoring resumed by user");
        }
    }

    public RecordingStartResult StartRecording(IReadOnlyList<RecordingSensor> sensors)
    {
        var result = Recorder.Start(Settings.Recording, sensors);
        if (!result.Success)
        {
            _log.Warn("Recording", $"Recording could not start: {result.Error} {result.Detail}");
        }

        return result;
    }

    public Task<RecordingStatus> StopRecordingAsync() => Recorder.StopAsync();

    /// <summary>Starts recording from the globally configured sensor list. A second start is ignored.</summary>
    public RecordingStartResult StartRecordingFromHotkey()
    {
        if (Recorder.IsRecording)
        {
            return new RecordingStartResult(false, RecordingError.AlreadyRecording, "already recording",
                null, Recorder.Status);
        }

        var selected = Settings.Recording.Sensors ?? new List<string>();
        var sensors = new List<RecordingSensor>();
        foreach (var id in selected)
        {
            var definition = MetricCatalog.FindBySensorId(id) ?? MetricCatalog.Find(id);
            var unit = definition?.Unit ?? (id.EndsWith(".rpm", StringComparison.Ordinal) ? "RPM"
                : id.EndsWith(".percent", StringComparison.Ordinal) ? "%" : string.Empty);
            sensors.Add(new RecordingSensor(id, definition?.DisplayName ?? id, unit));
        }

        if (sensors.Count == 0)
        {
            foreach (var definition in MetricCatalog.All)
            {
                var id = MetricCatalog.SensorIdFor(definition);
                if (id is not null)
                {
                    sensors.Add(new RecordingSensor(id, definition.DisplayName, definition.Unit));
                }
            }
        }

        var result = StartRecording(sensors);
        _log.Info("Recording", result.Success
            ? $"Recording started by hotkey ({sensors.Count} sensors)"
            : $"Recording hotkey start failed: {result.Error} {result.Detail}");
        return result;
    }

    public async void StopRecordingFromHotkey()
    {
        if (!Recorder.IsRecording)
        {
            return;
        }

        await StopRecordingAsync();
        _log.Info("Recording", "Recording stopped by hotkey");
    }

    /// <summary>Restarts the app, preserving the current elevation mode and startup arguments.</summary>
    public void RestartApplication()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                return;
            }

            SaveNow();
            if (Recorder.IsRecording)
            {
                Recorder.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            }

            var args = string.Join(' ', Environment.GetCommandLineArgs().Skip(1).Select(QuoteArgument));
            var elevated = LibreHardwareMonitorProvider.IsElevated;
            _log.Info("App", $"Restart requested; preserving elevation={elevated}");
            Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = true,
                Verb = elevated ? "runas" : null,
            });
            Exit();
        }
        catch (Exception ex)
        {
            _log.Warn("App", "Restart hotkey was cancelled or failed", ex);
        }
    }

    private static string QuoteArgument(string argument)
        => argument.Contains(' ') ? "\"" + argument.Replace("\"", "\\\"") + "\"" : argument;

    private void RestartPolling()
    {
        var wasRunning = IsMonitoring;
        Polling?.Dispose();
        Polling = null;
        Cache.Clear();
        History.Clear();
        if (wasRunning)
        {
            StartPolling();
        }

        _log.Info("App", "Sensor providers restarted");
    }

    public void ShowOverlay()
    {
        if (Overlay is null)
        {
            Overlay = new OverlayWindow(this);
            Overlay.RenderFrame(force: true);
        }

        if (!Overlay.IsVisible)
        {
            Overlay.RenderFrame(force: true);
        }

        Tray?.Sync();
    }

    public void CloseOverlay()
    {
        if (Overlay is null)
        {
            return;
        }

        var overlay = Overlay;
        Overlay = null;
        overlay.Close();
        Tray?.Sync();
    }

    /// <summary>Called by the overlay window when it is closed by anything other than CloseOverlay.</summary>
    internal void NotifyOverlayClosed(OverlayWindow window)
    {
        if (ReferenceEquals(Overlay, window))
        {
            Overlay = null;
            Tray?.Sync();
            _log.Warn("Overlay", "Overlay window was closed externally; it will be recreated on the next tick");
        }
    }

    public void ToggleOverlay()
    {
        Settings.Overlay.Enabled = !Settings.Overlay.Enabled;
        ApplySettings(Settings, persist: true);
        _log.Info("App", $"Overlay {(Settings.Overlay.Enabled ? "enabled" : "disabled")} from tray/hotkey");
    }

    /// <summary>Enters drag-to-move mode. Click-through is disabled until the drag finishes.</summary>
    public void BeginOverlayMove()
    {
        if (!Settings.Overlay.Enabled)
        {
            Settings.Overlay.Enabled = true;
            ApplySettings(Settings, persist: true);
        }

        ShowOverlay();
        Overlay?.BeginMoveMode();
        _log.Info("Overlay", "Move mode started");
    }

    public void CancelOverlayMove()
    {
        Overlay?.CancelMoveMode();
        _log.Info("Overlay", "Move mode cancelled");
    }

    /// <summary>Persists a dragged position in physical screen pixels.</summary>
    public void CommitOverlayPosition(int x, int y, string monitorDevice)
    {
        var settings = Settings.Clone();
        settings.Overlay.UseCustomPosition = true;
        settings.Overlay.CustomX = x;
        settings.Overlay.CustomY = y;
        settings.Overlay.MonitorDevice = monitorDevice ?? string.Empty;
        ApplySettings(settings, persist: true);
        _log.Info("Overlay", $"Position saved at ({x}, {y}) on {monitorDevice}");
        MainWindow?.OnOverlayPositionSaved();
    }

    public void ShowSettings()
    {
        var window = EnsureMainWindow();
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    /// <summary>
    /// Global main-window toggle. The first press restores/activates a hidden, minimized or
    /// background window; a press while the window is visible and active hides it back to the tray.
    /// </summary>
    public void ToggleMainWindow()
    {
        var window = MainWindow;
        var action = MainWindowTogglePolicy.Next(
            window is not null,
            window?.IsVisible == true,
            window?.IsActive == true,
            window?.WindowState == WindowState.Minimized);

        if (action == MainWindowToggleAction.Hide && window is not null)
        {
            window.Hide();
            _log.Info("Hotkey", "Main window hidden from the global hotkey");
        }
        else
        {
            ShowSettings();
            _log.Info("Hotkey", "Main window shown/activated from the global hotkey");
        }
    }

    private MainWindow EnsureMainWindow()
    {
        if (MainWindow is not null)
        {
            return MainWindow;
        }

        // The configuration window is created lazily: starting with Windows should cost no WPF
        // control tree until the user actually opens settings.
        var window = new MainWindow(this);
        window.Closed += (_, _) => MainWindow = null;
        MainWindow = window;
        return window;
    }

    public void OpenLogFolder() => TrayIcon.OpenFolder(Path.Combine(SettingsStore.DefaultDirectory, "logs"));

    public void OpenSettingsFolder() => TrayIcon.OpenFolder(SettingsStore.DefaultDirectory);

    public void RestartElevated()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                return;
            }

            _log.Info("App", "Restarting with administrator rights for full CPU sensor access");
            SaveNow();
            Process.Start(new ProcessStartInfo(exe, "--elevated")
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            Exit();
        }
        catch (Exception ex)
        {
            _log.Warn("App", "Elevated restart was cancelled or failed", ex);
        }
    }

    public OverlayLayout BuildLayout(SensorSnapshot snapshot)
        => OverlayLayoutBuilder.Build(Settings, snapshot, FpsCache.Latest, History,
            BuildOsdRenderContext());

    /// <summary>Machine facts used by the CapFrameX-derived text tiles and row template names.</summary>
    public OsdRenderContext BuildOsdRenderContext()
    {
        var info = SystemInfo.Cached;
        var gpu = info?.Gpus.FirstOrDefault();
        var cpu = info?.Cpu.FirstOrDefault(item =>
            string.Equals(item.Key, "cpu.name", StringComparison.OrdinalIgnoreCase));
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new OsdRenderContext(gpu?.Name, cpu?.Value, gpu?.Vendor,
            CpuVendor(info?.Cpu), gpu?.DriverVersion, _sessionStartMs, now);
    }

    /// <summary>Machine facts for a tile scene, in the tile design's own vocabulary.</summary>
    public TileRenderContext BuildTileRenderContext()
    {
        var osd = BuildOsdRenderContext();
        return new TileRenderContext(osd.GpuName, osd.CpuName, osd.GpuDriver, GraphicsApi(),
            _sessionStartMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), osd.GpuVendor,
            osd.CpuVendor, Settings.Overlay.WarningColor, Settings.Overlay.CriticalColor);
    }

    /// <summary>Builds the active tile profile's draw list, or an empty scene for a row profile.</summary>
    /// <param name="targetWidth">
    /// Screen width in design pixels. A design marked full width is stretched to this width minus
    /// its left and right margins, as CapFrameX's full-width scenes are; null keeps the authored
    /// width (the builder preview always uses the authored width).
    /// </param>
    public TileScene BuildTileScene(SensorSnapshot snapshot, double? targetWidth = null)
    {
        var profile = Settings.Osd?.ActiveProfile;
        if (profile is not { IsTile: true })
        {
            return TileScene.Empty;
        }

        var design = profile.TileDesign;
        if (design is null)
        {
            return TileScene.Empty;
        }

        if (design.FullWidth && targetWidth is > 0)
        {
            var contentWidth = Math.Max(TileDocumentEditor.MinDesignWidth,
                targetWidth.Value - design.MarginX * 2);
            design = design.StretchToWidth(contentWidth);
        }

        return TileSceneBuilder.Build(design, snapshot, FpsCache.Latest, History, profile.Tile,
            BuildTileRenderContext());
    }

    /// <summary>
    /// The reference row block embedded by the three Hybrid designs. HardwareOSD has no separate
    /// "active row profile" while a tile design is shown, so the Detailed CapFrameX row template is
    /// the documented fallback; it is a real HardwareOSD profile with real sensors.
    /// </summary>
    public (OverlayVisual Visual, OverlayTheme Theme)? BuildClassicRowsVisual(SensorSnapshot snapshot)
    {
        var clone = Settings.Clone();
        clone.Osd.ActiveProfileId = CapFrameXRowTemplates.DetailedId;
        if (clone.Osd.FindProfile(CapFrameXRowTemplates.DetailedId) is null)
        {
            return null;
        }

        var theme = OverlayTheme.Create(clone);
        var layout = OverlayLayoutBuilder.Build(clone, snapshot, FpsCache.Latest, History,
            BuildOsdRenderContext());
        var visual = OverlayVisualBuilder.Build(layout, theme, History, OverlayLayoutMode.Stacked,
            1.0, DemoMode);
        return visual.IsEmpty ? null : (visual, theme);
    }

    private static string? CpuVendor(IReadOnlyList<SystemInfoItem>? cpu)
        => cpu?.FirstOrDefault(item =>
            string.Equals(item.Key, "cpu.vendor", StringComparison.OrdinalIgnoreCase))?.Value;

    private static string? GraphicsApi()
    {
        // PresentMon/ETW does not expose the API name. The row and tile designs render this as a
        // real "N/A" instead of guessing DirectX or Vulkan.
        return null;
    }

    public OverlayVisual BuildVisual(double pixelsPerDip)
        => OverlayVisualBuilder.Build(
            BuildLayout(Cache.Latest),
            _theme,
            History,
            Settings.Overlay.Layout,
            pixelsPerDip,
            DemoMode);

    private TimeSpan ComputePresentationInterval()
    {
        var intervalMs = 1000.0 / Math.Clamp(Settings.Monitoring.OverlayRefreshHz, 1, 30);

        // FPS values change between hardware polls, so the overlay must repaint at the FPS update
        // rate while the module is enabled (100-500 ms).
        if (Settings.Fps.Enabled && Settings.Fps.UpdateIntervalMs < intervalMs)
        {
            intervalMs = Settings.Fps.UpdateIntervalMs;
        }

        return TimeSpan.FromMilliseconds(Math.Clamp(intervalMs, 33, 2000));
    }

    private void StartFps()
    {
        if (Fps is not null)
        {
            return;
        }

        var settings = Settings.Fps.Clone();
        settings.Normalize();

        IFrameEventSource source;
        var demoPid = 0;
        if (DemoMode)
        {
            var demoSource = new DemoFrameEventSource(144);
            demoPid = demoSource.ProcessId;
            source = demoSource;
        }
        else
        {
            source = new EtwFrameEventSource(_log);
        }

        Fps = new FpsMonitorService(
            FpsCache,
            source,
            new Win32ForegroundAppProbe(),
            _log,
            settings,
            History,
            demoPid,
            new AllApplicationsTargetResolver(
                new Win32ProcessRelationshipProbe(),
                Environment.ProcessId));
        Fps.Start();
    }

    private void StopFps()
    {
        if (Fps is null)
        {
            return;
        }

        Fps.Dispose();
        Fps = null;
        FpsCache.Reset();
        History.AppendValue("frame.fps", double.NaN);
        _log.Info("App", "FPS monitoring stopped");
    }

    private void OnPresentationTick(object? sender, EventArgs e)
    {
        if (Overlay is null && Settings.Overlay.Enabled)
        {
            try
            {
                ShowOverlay();
            }
            catch (Exception ex)
            {
                _log.Error("Overlay", "Recreating the overlay window failed; disabling the overlay", ex);
                Settings.Overlay.Enabled = false;
            }
        }

        if (Overlay is not null)
        {
            Overlay.RenderFrame();
            if (++_topmostCounter % 20 == 0)
            {
                Overlay.ReassertTopmost();
            }
        }

        MainWindow?.OnRuntimeTick();
    }

    private void CreateHotkeyWindow()
    {
        try
        {
            // A never-shown 1x1 window provides a stable HWND for RegisterHotKey so the global
            // hotkey keeps working while the configuration window does not exist.
            _hotkeyWindow = new Window
            {
                Title = "HardwareOSD Hotkey",
                Width = 1,
                Height = 1,
                Left = -32000,
                Top = -32000,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                AllowsTransparency = false,
            };

            _hotkeyHandle = new WindowInteropHelper(_hotkeyWindow).EnsureHandle();
            HwndSource.FromHwnd(_hotkeyHandle)?.AddHook(HotkeyWndProc);
            UpdateHotkey();
        }
        catch (Exception ex)
        {
            _log.Warn("Hotkey", "Could not create the hotkey message window", ex);
        }
    }

    private void UpdateHotkey()
    {
        if (_hotkeyHandle == IntPtr.Zero)
        {
            return;
        }

        var behavior = Settings.Behavior;
        var overlayGesture = HotkeyGesture.TryParse(behavior.HotkeyToggleOverlay, out var parsedOverlay)
            ? parsedOverlay
            : HotkeyGesture.Default;
        var mainGesture = HotkeyGesture.TryParse(behavior.HotkeyToggleMainWindow, out var parsedMain)
            ? parsedMain
            : HotkeyGesture.MainWindowDefault;
        var startRecordingGesture = HotkeyGesture.TryParse(behavior.HotkeyStartRecording, out var parsedStart)
            ? parsedStart
            : new HotkeyGesture(HotkeyGesture.ModControl | HotkeyGesture.ModShift, 0x52);
        var stopRecordingGesture = HotkeyGesture.TryParse(behavior.HotkeyStopRecording, out var parsedStop)
            ? parsedStop
            : new HotkeyGesture(HotkeyGesture.ModControl | HotkeyGesture.ModShift, 0x53);
        var restartGesture = HotkeyGesture.TryParse(behavior.HotkeyRestartApplication, out var parsedRestart)
            ? parsedRestart
            : new HotkeyGesture(HotkeyGesture.ModControl | HotkeyGesture.ModAlt, 0x7B);
        HotkeyDisplay = overlayGesture.DisplayText;
        MainWindowHotkeyDisplay = mainGesture.DisplayText;

        if (!behavior.EnableGlobalHotkey)
        {
            UnregisterHotkey(HotkeyOverlayId, ref _overlayHotkeyRegistered);
            UnregisterHotkey(HotkeyMainWindowId, ref _mainHotkeyRegistered);
            UnregisterHotkey(HotkeyStartRecordingId, ref _startRecordingHotkeyRegistered);
            UnregisterHotkey(HotkeyStopRecordingId, ref _stopRecordingHotkeyRegistered);
            UnregisterHotkey(HotkeyRestartId, ref _restartHotkeyRegistered);
            HotkeyError = null;
            MainWindowHotkeyError = null;
            StartRecordingHotkeyError = null;
            StopRecordingHotkeyError = null;
            RestartHotkeyError = null;
            return;
        }

        SyncOneHotkey(HotkeyStartRecordingId, startRecordingGesture, "start recording",
            ref _startRecordingHotkeyRegistered, ref _registeredStartRecordingGesture,
            out var startRecordingError);
        StartRecordingHotkeyError = startRecordingError;
        SyncOneHotkey(HotkeyStopRecordingId, stopRecordingGesture, "stop recording",
            ref _stopRecordingHotkeyRegistered, ref _registeredStopRecordingGesture,
            out var stopRecordingError);
        StopRecordingHotkeyError = stopRecordingError;
        SyncOneHotkey(HotkeyRestartId, restartGesture, "restart HardwareOSD",
            ref _restartHotkeyRegistered, ref _registeredRestartGesture, out var restartError);
        RestartHotkeyError = restartError;

        if (overlayGesture == mainGesture)
        {
            // Defensive: AppSettings.Normalize already prevents this, but a hand-edited file must
            // never register the same chord twice. The overlay keeps the chord; the main-window
            // action reports the conflict instead of silently stealing it.
            SyncOneHotkey(HotkeyOverlayId, overlayGesture, "overlay",
                ref _overlayHotkeyRegistered, ref _registeredOverlayGesture, out var overlayError);
            HotkeyError = overlayError;
            UnregisterHotkey(HotkeyMainWindowId, ref _mainHotkeyRegistered);
            MainWindowHotkeyError = Loc.F("hotkeys_duplicate_runtime", mainGesture.DisplayText);
        }
        else
        {
            SyncOneHotkey(HotkeyOverlayId, overlayGesture, "overlay",
                ref _overlayHotkeyRegistered, ref _registeredOverlayGesture, out var overlayError);
            HotkeyError = overlayError;

            SyncOneHotkey(HotkeyMainWindowId, mainGesture, "main window",
                ref _mainHotkeyRegistered, ref _registeredMainGesture, out var mainError);
            MainWindowHotkeyError = mainError;
        }
    }

    private void SyncOneHotkey(int id, HotkeyGesture gesture, string purpose,
        ref bool registered, ref HotkeyGesture registeredGesture, out string? error)
    {
        error = null;
        if (registered && registeredGesture == gesture)
        {
            return;
        }

        UnregisterHotkey(id, ref registered);

        if (NativeMethods.RegisterHotkey(_hotkeyHandle, id, gesture.Modifiers, gesture.VirtualKey))
        {
            registered = true;
            registeredGesture = gesture;
            _log.Info("Hotkey", $"Registered {gesture.DisplayText} to toggle the {purpose}");
        }
        else
        {
            registered = false;
            error = Loc.F("hotkeys_registration_failed", gesture.DisplayText);
            _log.Warn("Hotkey", $"{gesture.DisplayText} could not be registered for the {purpose}; it is already in use");
        }
    }

    private void UnregisterHotkey(int id, ref bool registered)
    {
        if (registered && _hotkeyHandle != IntPtr.Zero)
        {
            NativeMethods.UnregisterHotkey(_hotkeyHandle, id);
        }

        registered = false;
    }

    /// <summary>Currently configured overlay hotkey (display form), regardless of registration state.</summary>
    public string HotkeyDisplay { get; private set; } = HotkeyGesture.DefaultText;

    /// <summary>Currently configured main-window hotkey (display form), regardless of registration state.</summary>
    public string MainWindowHotkeyDisplay { get; private set; } = HotkeyGesture.MainWindowDefaultText;

    /// <summary>Human readable registration error for the overlay hotkey, or null when registered.</summary>
    public string? HotkeyError { get; private set; }

    /// <summary>Human readable registration error for the main-window hotkey, or null when registered.</summary>
    public string? MainWindowHotkeyError { get; private set; }

    public string? StartRecordingHotkeyError { get; private set; }
    public string? StopRecordingHotkeyError { get; private set; }
    public string? RestartHotkeyError { get; private set; }

    public bool IsHotkeyRegistered => _overlayHotkeyRegistered;

    public bool IsMainWindowHotkeyRegistered => _mainHotkeyRegistered;

    private IntPtr HotkeyWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmHotkey)
        {
            switch (wParam.ToInt32())
            {
                case HotkeyOverlayId:
                    ToggleOverlay();
                    handled = true;
                    break;
                case HotkeyMainWindowId:
                    ToggleMainWindow();
                    handled = true;
                    break;
                case HotkeyStartRecordingId:
                    StartRecordingFromHotkey();
                    handled = true;
                    break;
                case HotkeyStopRecordingId:
                    StopRecordingFromHotkey();
                    handled = true;
                    break;
                case HotkeyRestartId:
                    RestartApplication();
                    handled = true;
                    break;
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Launch-time observation only. The Run key is never created here: a user who removed the
    /// entry (or disabled it in Task Manager) must not see it silently restored by the next launch.
    /// </summary>
    private void LogStartupRegistrationState()
    {
        if (!_manageStartupRegistration)
        {
            _log.Info("Startup", "Startup registration disabled for portable/test mode");
            return;
        }

        var active = StartupRegistration.IsActive();
        if (active == Settings.Behavior.StartWithWindows)
        {
            return;
        }

        _log.Warn("Startup",
            $"Start with Windows preference is {Settings.Behavior.StartWithWindows} but the Run key is " +
            $"{(active ? "active" : "inactive")}; leaving the Windows state unchanged");
    }

    /// <summary>Applies an explicit user toggle from the settings window.</summary>
    public void SetStartWithWindows(bool enabled)
    {
        if (!StartupRegistrationPolicy.ShouldWrite(
                _manageStartupRegistration, enabled, StartupRegistration.IsActive()))
        {
            _log.Info("Startup",
                $"Start with Windows is already {(enabled ? "enabled" : "disabled")}; no registry change");
            return;
        }

        StartupRegistration.Set(enabled, _log);
    }

    public void Exit()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _log.Info("App", "Exit requested");
        SaveNow();
        _presentationTimer.Stop();
        _saveTimer.Stop();

        UnregisterHotkey(HotkeyOverlayId, ref _overlayHotkeyRegistered);
        UnregisterHotkey(HotkeyMainWindowId, ref _mainHotkeyRegistered);
        UnregisterHotkey(HotkeyStartRecordingId, ref _startRecordingHotkeyRegistered);
        UnregisterHotkey(HotkeyStopRecordingId, ref _stopRecordingHotkeyRegistered);
        UnregisterHotkey(HotkeyRestartId, ref _restartHotkeyRegistered);

        try
        {
            Recorder.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log.Warn("Recording", "Recorder did not stop cleanly during exit", ex);
        }
        finally
        {
            Recorder.Dispose();
        }

        Tray?.Dispose();
        Tray = null;

        CloseOverlay();
        Polling?.Dispose();
        Polling = null;
        StopFps();

        if (MainWindow is not null)
        {
            MainWindow.AllowClose = true;
            MainWindow.Close();
            MainWindow = null;
        }

        CloseHotkeyWindow();
        Application.Current.Shutdown();
    }

    private void CloseHotkeyWindow()
    {
        if (_hotkeyWindow is null)
        {
            return;
        }

        try
        {
            HwndSource.FromHwnd(_hotkeyHandle)?.RemoveHook(HotkeyWndProc);
            _hotkeyWindow.Close();
        }
        catch
        {
            // ignored
        }

        _hotkeyWindow = null;
        _hotkeyHandle = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _presentationTimer.Stop();
        _saveTimer.Stop();
        Tray?.Dispose();
        Overlay?.Close();
        Polling?.Dispose();
        Fps?.Dispose();
        Fps = null;
        Recorder.Dispose();
        CloseHotkeyWindow();

        if (_log is IDisposable disposableLog)
        {
            disposableLog.Dispose();
        }
    }
}

