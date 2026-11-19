using System.ServiceProcess;
using HardwareOSD.Core.Diagnostics;
using HardwareOSD.Core.Hardware;

namespace HardwareOSD.FanBroker;

internal static class Program
{
    private const string InstanceMutexName = @"Global\HardwareOSD.FanBroker.Instance.v1";

    private static int Main(string[] args)
    {
        if (args.Any(arg => arg.Equals("--console", StringComparison.OrdinalIgnoreCase)) ||
            Environment.UserInteractive)
        {
            var logDirectory = ReadOption(args, "--log-dir")
                               ?? Path.Combine(Path.GetTempPath(), "HardwareOSD.FanBroker");
            return RunConsoleAsync(logDirectory).GetAwaiter().GetResult();
        }

        ServiceBase.Run(new FanBrokerWindowsService());
        return 0;
    }

    private static async Task<int> RunConsoleAsync(string logDirectory)
    {
        using var mutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var created);
        if (!created)
        {
            Console.Error.WriteLine("Another HardwareOSD fan broker instance is already running.");
            return 2;
        }

        using var log = new StructuredBrokerLog(logDirectory);
        using var host = new FanBrokerHost(log);
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        log.Info("Console", "Starting in console mode; no service was installed");
        try
        {
            await host.RunAsync(cancellation.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            log.Error("Console", "Fan broker console host failed", ex);
            return 1;
        }
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }

            if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                return args[i][(name.Length + 1)..];
            }
        }

        return null;
    }
}

/// <summary>Windows service host. All work is delegated to FanBrokerHost with cancellation.</summary>
internal sealed class FanBrokerWindowsService : ServiceBase
{
    private const string InstanceMutexName = @"Global\HardwareOSD.FanBroker.Instance.v1";

    private CancellationTokenSource? _cancellation;
    private FanBrokerHost? _host;
    private Task? _hostTask;
    private StructuredBrokerLog? _log;
    private Mutex? _mutex;

    public FanBrokerWindowsService()
    {
        ServiceName = FanBrokerProtocol.ServiceName;
        CanStop = true;
        CanShutdown = true;
        AutoLog = true;
    }

    protected override void OnStart(string[] args)
    {
        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "HardwareOSD",
            "FanBroker",
            "logs");
        _log = new StructuredBrokerLog(logDirectory);
        _log.Info("Service", "OnStart");

        _mutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var created);
        if (!created)
        {
            _log.Error("Service", "Another broker instance already owns the instance mutex");
            _log.Dispose();
            _log = null;
            throw new InvalidOperationException("Another HardwareOSD fan broker instance is already running.");
        }

        _cancellation = new CancellationTokenSource();
        _host = new FanBrokerHost(_log);
        _hostTask = Task.Run(async () =>
        {
            try
            {
                await _host.RunAsync(_cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected on stop
            }
            catch (Exception ex)
            {
                _log?.Error("Service", "Host failed", ex);
                try
                {
                    Stop();
                }
                catch
                {
                    // SCM may already be stopping the service.
                }
            }
        });
    }

    protected override void OnStop()
    {
        _log?.Info("Service", "OnStop");
        try
        {
            _cancellation?.Cancel();
        }
        catch
        {
            // ignored
        }

        try
        {
            _hostTask?.Wait(TimeSpan.FromSeconds(8));
        }
        catch (Exception ex)
        {
            _log?.Warn("Service", "Host did not stop cleanly", ex);
        }

        _host?.Dispose();
        _host = null;
        _hostTask = null;
        _cancellation?.Dispose();
        _cancellation = null;
        if (_mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
                // not owned
            }

            _mutex.Dispose();
            _mutex = null;
        }

        _log?.Info("Service", "Stopped");
        _log?.Dispose();
        _log = null;
    }

    protected override void OnShutdown()
    {
        OnStop();
        base.OnShutdown();
    }
}
