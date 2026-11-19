using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using HardwareOSD.App.Localization;
using HardwareOSD.App.Services;
using HardwareOSD.App.Theming;
using HardwareOSD.Core.Config;
using HardwareOSD.Core.Diagnostics;
using HardwareOSD.Core.Security;

namespace HardwareOSD.App;

/// <summary>
/// Entry point. Handles single-instance activation, configuration loading and the lifetime of the
/// runtime. The configuration window is owned by the runtime; closing it never stops monitoring.
/// </summary>
public partial class App : Application
{
    private SingleInstanceGuard? _guard;
    private AppRuntime? _runtime;
    private FileLogger? _log;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var demo = e.Args.Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase));
        var startHidden = e.Args.Any(a =>
            a.Equals("--startup", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

        // Hidden QA switch: render every layout offscreen and exit. Used by the build/test scripts.
        var renderIndex = Array.FindIndex(e.Args, a => a.Equals("--render", StringComparison.OrdinalIgnoreCase));
        if (renderIndex >= 0)
        {
            var directory = renderIndex + 1 < e.Args.Length ? e.Args[renderIndex + 1] : "layout-qa";
            try
            {
                Diagnostics.VisualQa.Render(directory);
                Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                Shutdown(2);
            }

            return;
        }

        // Hidden QA switch: render every tile design with the production renderer and exit.
        var renderTilesIndex = Array.FindIndex(e.Args,
            a => a.Equals("--render-tiles", StringComparison.OrdinalIgnoreCase));
        if (renderTilesIndex >= 0)
        {
            var directory = renderTilesIndex + 1 < e.Args.Length ? e.Args[renderTilesIndex + 1] : "tile-qa";
            var onlyIndex = Array.FindIndex(e.Args,
                a => a.Equals("--design", StringComparison.OrdinalIgnoreCase));
            var only = onlyIndex >= 0 && onlyIndex + 1 < e.Args.Length ? e.Args[onlyIndex + 1] : null;
            try
            {
                Shutdown(Diagnostics.TileQa.Render(directory, only));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                Shutdown(2);
            }

            return;
        }

        // Hidden QA switch: open the OSD builder off-screen, load every tile profile, exercise the
        // editor and write screenshots plus a report. Never writes the user's settings.
        var renderBuilderIndex = Array.FindIndex(e.Args,
            a => a.Equals("--render-builder", StringComparison.OrdinalIgnoreCase));
        if (renderBuilderIndex >= 0)
        {
            var directory = renderBuilderIndex + 1 < e.Args.Length
                ? e.Args[renderBuilderIndex + 1]
                : "builder-qa";
            try
            {
                Shutdown(Diagnostics.BuilderQa.Render(directory));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                Shutdown(2);
            }

            return;
        }

        // Hidden QA switch: render the System page with real machine data at both required viewports.
        var renderSystemIndex = Array.FindIndex(e.Args,
            a => a.Equals("--render-system", StringComparison.OrdinalIgnoreCase));
        if (renderSystemIndex >= 0)
        {
            var directory = renderSystemIndex + 1 < e.Args.Length ? e.Args[renderSystemIndex + 1] : "system-qa";
            try
            {
                var exitCode = Diagnostics.SystemPageQa.Render(directory);
                Shutdown(exitCode);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                Shutdown(2);
            }

            return;
        }

        // Hidden elevated helper: grant the current user one-time Performance Log Users membership.
        // Launched by the UI via ShellExecute("runas"); it never creates a window or a scheduled task.
        var grantIndex = Array.FindIndex(e.Args, a => a.Equals("--grant-etw", StringComparison.OrdinalIgnoreCase));
        if (grantIndex >= 0)
        {
            var targetUser = grantIndex + 1 < e.Args.Length ? e.Args[grantIndex + 1] : PerformanceLogUsers.CurrentUserName;
            var resultPath = grantIndex + 2 < e.Args.Length
                ? e.Args[grantIndex + 2]
                : Path.Combine(Path.GetTempPath(), "HardwareOSD-etw-grant.txt");
            EtwGrantResult grantResult;
            try
            {
                grantResult = PerformanceLogUsers.GrantUser(targetUser);
            }
            catch (Exception ex)
            {
                grantResult = new EtwGrantResult(false, false, ex.Message);
            }

            try
            {
                File.WriteAllText(resultPath, grantResult.Message, new UTF8Encoding(false));
            }
            catch
            {
                // The parent can still use the exit code if the result file is unavailable.
            }

            Shutdown(grantResult.Success ? 0 : PerformanceLogUsers.IsElevated() ? 1 : 2);
            return;
        }

        var dataDirectory = ResolveDataDirectory(e.Args);
        var instanceKey = dataDirectory ?? "default";
        _log = dataDirectory is null
            ? FileLogger.CreateDefault(LogLevel.Info)
            : new FileLogger(Path.Combine(dataDirectory, "logs"));
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            _log?.Error("App", "Unhandled exception", args.ExceptionObject as Exception);
        };

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Diagnostic trail for startup audits: distinguishes a normal launch, the Run key's
        // --startup argument and an isolated QA profile without guessing from timings.
        try
        {
            _log.Info("App", $"Executable: {Environment.ProcessPath}");
            _log.Info("App", "Launch arguments: " +
                (e.Args.Length == 0 ? "<none>" : string.Join(' ', e.Args)));
        }
        catch
        {
            // Diagnostics must never block startup.
        }

        // Persist the latest configuration before Windows ends the session.
        SessionEnding += (_, _) =>
        {
            try
            {
                _runtime?.SaveNow();
                _log?.Info("App", "Session ending; settings saved");
            }
            catch
            {
                // ignored
            }
        };

        SingleInstanceGuard? guard = null;
        bool isFirstInstance;
        try
        {
            guard = SingleInstanceGuard.Create(_log, instanceKey, out isFirstInstance);
        }
        catch (Exception ex)
        {
            _log.Error("App", "Single-instance guard failed; continuing", ex);
            isFirstInstance = true;
        }

        _guard = guard;
        if (!isFirstInstance)
        {
            _log.Info("App", "Another instance is running; signalling it and exiting");
            SingleInstanceGuard.SignalExistingInstance(instanceKey);
            Shutdown();
            return;
        }

        try
        {
            var store = new SettingsStore(
                dataDirectory is null ? null : Path.Combine(dataDirectory, "settings.json"),
                _log);
            if (dataDirectory is not null)
            {
                _log.Info("App", $"Portable data directory: {dataDirectory}");
            }
            var settings = store.Load(out var configStatus);
            Loc.Instance.Initialize(settings.Behavior.Language);
            AppThemeManager.Apply(settings.Behavior.AppTheme, settings.Behavior.AppAccentColor);
            _log.MinimumLevel = settings.Behavior.MinimumLogLevel;
            _log.Info("App", $"Starting HardwareOSD {typeof(App).Assembly.GetName().Version} (demo={demo})");

            _runtime = new AppRuntime(_log, store, settings, demo, configStatus,
                StartupRegistrationPolicy.CanManage(dataDirectory, demo));
            _runtime.Initialize(showWindow: !startHidden && !settings.Behavior.StartMinimized);

            _guard?.StartListening(() => Dispatcher.BeginInvoke(_runtime.ShowSettings));

            if (demo)
            {
                _runtime.Tray?.ShowBalloon(Loc.T("tray_demo_title"), Loc.T("tray_demo_text"));
            }
        }
        catch (Exception ex)
        {
            _log.Error("App", "Startup failed", ex);
            MessageBox.Show(
                Loc.F("msg_startup_failed", ex.Message),
                Loc.T("msg_startup_failed_title"), MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("App", "Unhandled UI exception", e.Exception);

        // Keep the overlay alive for recoverable UI errors; surface the problem once.
        if (e.Exception is OutOfMemoryException or StackOverflowException)
        {
            return;
        }

        e.Handled = true;
        try
        {
            MessageBox.Show(
                Loc.F("msg_nonfatal", e.Exception.Message),
                Loc.T("msg_nonfatal_title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch
        {
            // ignored
        }
    }

    private static string? ResolveDataDirectory(string[] args)
    {
        // Portable/QA profiles: --data-dir <path> (or --data-dir=<path>) keeps settings, logs and
        // the single-instance name separate from the default installation.
        for (var i = 0; i < args.Length; i++)
        {
            string? value = null;
            if (args[i].Equals("--data-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                value = args[i + 1];
            }
            else if (args[i].StartsWith("--data-dir=", StringComparison.OrdinalIgnoreCase))
            {
                value = args[i]["--data-dir=".Length..];
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                var fullPath = Path.GetFullPath(value.Trim('"'));
                Directory.CreateDirectory(fullPath);
                return fullPath;
            }
        }

        return null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _runtime?.Dispose();
            _guard?.Dispose();
            _log?.Dispose();
        }
        catch
        {
            // ignored
        }

        base.OnExit(e);
    }
}
