using HardwareOSD.Core.Diagnostics;
using HardwareOSD.Core.Hardware;

namespace HardwareOSD.FanBroker;

/// <summary>
/// Owns the read-only WMI poll loop and the fixed IPC endpoint. There is exactly one WMI reader and
/// one poll loop per process; the reader serializes all Get_Fan calls.
/// </summary>
internal sealed class FanBrokerHost : IDisposable
{
    private readonly ILog _log;
    private readonly BrokerSnapshotStore _store = new();
    private readonly MsiAcpiFanReader _reader = new();
    private readonly FanBrokerPipeServer _pipe;
    private FanBrokerReason _lastReason = (FanBrokerReason)255;
    private FanBrokerStatus _lastStatus = (FanBrokerStatus)255;
    private long _lastLogMs;

    public FanBrokerHost(ILog log)
    {
        _log = log;
        _pipe = new FanBrokerPipeServer(FanBrokerProtocol.PipeName, _store, log);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _log.Info("Host", $"Starting read-only MSI fan broker, pipe={FanBrokerProtocol.PipeName}");
        _reader.Initialize();
        _log.Info("Wmi", "MSI_ACPI reader initialised: " + _reader.Detail);

        var pipeTask = _pipe.RunAsync(cancellationToken);
        var pollTask = PollAsync(cancellationToken);
        try
        {
            await Task.WhenAll(pipeTask, pollTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal service stop.
        }
        catch (Exception ex)
        {
            _log.Error("Host", "Fan broker stopped unexpectedly", ex);
            throw;
        }
        finally
        {
            _reader.Dispose();
            _log.Info("Host", "MSI fan broker stopped");
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        await ReadAndPublishAsync(cancellationToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(FanBrokerProtocol.PollIntervalMs));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await ReadAndPublishAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private Task ReadAndPublishAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            var result = _reader.Read(now);
            var snapshot = MsiFanBrokerSnapshotFactory.FromReadResult(result, now);
            _store.Publish(snapshot);
            LogState(snapshot, result.Detail);
        }
        catch (Exception ex)
        {
            var snapshot = FanBrokerSnapshot.Unavailable(FanBrokerReason.InternalError,
                "WMI poll failed: " + ex.Message, now, _reader.LastSuccessfulReadMs);
            _store.Publish(snapshot);
            _log.Error("Wmi", "Unhandled Get_Fan poll failure", ex);
        }

        return Task.CompletedTask;
    }

    private void LogState(FanBrokerSnapshot snapshot, string detail)
    {
        var changed = snapshot.Status != _lastStatus || snapshot.Reason != _lastReason;
        var now = Environment.TickCount64;
        if (!changed && now - _lastLogMs < 60_000)
        {
            return;
        }

        _lastStatus = snapshot.Status;
        _lastReason = snapshot.Reason;
        _lastLogMs = now;
        if (snapshot.Status == FanBrokerStatus.Ok)
        {
            var channels = string.Join(",", snapshot.Channels.Select(channel =>
                $"{channel.Component}:{channel.Period}->{channel.Rpm}"));
            _log.Info("Wmi",
                $"event=wmi_read status=ok cpu_period={snapshot.CpuPeriod} cpu_rpm={snapshot.CpuRpm} " +
                $"gpu_period={snapshot.GpuPeriod} gpu_rpm={snapshot.GpuRpm} channels={channels}");
        }
        else
        {
            _log.Warn("Wmi",
                $"event=wmi_unavailable status={snapshot.Status} reason={snapshot.Reason} detail={detail}");
        }
    }

    public void Dispose()
    {
        _reader.Dispose();
    }
}
