using HardwareOSD.Core.Diagnostics;

namespace HardwareOSD.App.Services;

/// <summary>
/// Prevents duplicate instances and lets a second launch bring the existing window to the front
/// instead of starting a second monitoring stack.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\HardwareOSD.Instance.v1";
    private const string EventName = @"Local\HardwareOSD.Activate.v1";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activateEvent;
    private readonly ILog _log;
    private Thread? _listener;
    private volatile bool _running;

    private SingleInstanceGuard(Mutex mutex, EventWaitHandle activateEvent, bool isFirstInstance, ILog log)
    {
        _mutex = mutex;
        _activateEvent = activateEvent;
        IsFirstInstance = isFirstInstance;
        _log = log;
    }

    public bool IsFirstInstance { get; }

    public static SingleInstanceGuard Create(ILog log, string? instanceKey, out bool isFirstInstance)
    {
        var suffix = BuildSuffix(instanceKey);
        var mutex = new Mutex(initiallyOwned: true, MutexName + suffix, out var createdNew);
        var activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName + suffix);
        isFirstInstance = createdNew;
        var guard = new SingleInstanceGuard(mutex, activateEvent, createdNew, log);
        return guard;
    }

    /// <summary>Signals the running instance to show its configuration window. Returns false if none.</summary>
    public static bool SignalExistingInstance(string? instanceKey)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(EventName + BuildSuffix(instanceKey), out var handle))
            {
                return false;
            }

            using (handle)
            {
                handle.Set();
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Independent profiles (portable data directories) get independent instance names.</summary>
    private static string BuildSuffix(string? instanceKey)
    {
        if (string.IsNullOrWhiteSpace(instanceKey) ||
            string.Equals(instanceKey, "default", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(instanceKey.ToUpperInvariant()));
        return "." + Convert.ToHexString(bytes, 0, 6).ToLowerInvariant();
    }

    public void StartListening(Action onActivate)
    {
        if (!IsFirstInstance || _running)
        {
            return;
        }

        _running = true;
        _listener = new Thread(() =>
        {
            while (_running)
            {
                try
                {
                    if (_activateEvent.WaitOne(500))
                    {
                        onActivate();
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _log.Warn("SingleInstance", "Activation listener error", ex);
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "HardwareOSD.Activation",
        };
        _listener.Start();
    }

    public void Dispose()
    {
        _running = false;
        try
        {
            _activateEvent.Set();
            _listener?.Join(1000);
        }
        catch
        {
            // ignored
        }

        try
        {
            if (IsFirstInstance)
            {
                _mutex.ReleaseMutex();
            }
        }
        catch
        {
            // ignored
        }

        _activateEvent.Dispose();
        _mutex.Dispose();
    }
}
