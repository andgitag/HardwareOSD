using System.ComponentModel;
using System.IO.Pipes;
using System.ServiceProcess;
using TimeoutException = System.TimeoutException;

namespace HardwareOSD.Core.Hardware;

public interface IMsiFanBrokerClient : IDisposable
{
    MsiFanBrokerStatus Status { get; }
    FanBrokerSnapshot ReadSnapshot();
}

/// <summary>
/// Client for the optional LocalSystem fan broker. It only sends the fixed GetFanSnapshot request
/// and validates a fixed-size response. The client never sends class names, method names, register
/// addresses, ports or paths.
/// </summary>
public sealed class MsiFanBrokerClient : IMsiFanBrokerClient
{
    private readonly object _gate = new();
    private FanBrokerSnapshot? _lastSnapshot;
    private DateTimeOffset? _lastSuccessfulResponseUtc;

    public MsiFanBrokerStatus Status { get; private set; } =
        MsiFanBrokerStatus.Unavailable(FanBrokerReason.ServiceNotInstalled, "broker has not been probed");

    public static bool IsServiceInstalled()
    {
        try
        {
            using var service = new ServiceController(FanBrokerProtocol.ServiceName);
            _ = service.Status;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool IsServiceRunning()
    {
        try
        {
            using var service = new ServiceController(FanBrokerProtocol.ServiceName);
            return service.Status == ServiceControllerStatus.Running;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public FanBrokerSnapshot ReadSnapshot()
    {
        lock (_gate)
        {
            var installed = IsServiceInstalled();
            var running = IsServiceRunning();
            if (!installed)
            {
                return SetUnavailable(FanBrokerReason.ServiceNotInstalled,
                    "the HardwareOSD fan broker service is not installed", installed, running);
            }

            if (!running)
            {
                return SetUnavailable(FanBrokerReason.ServiceNotRunning,
                    "the HardwareOSD fan broker service is installed but not running", installed, running);
            }

            try
            {
                using var pipe = new NamedPipeClientStream(".", FanBrokerProtocol.PipeName,
                    PipeDirection.InOut, PipeOptions.None);
                pipe.Connect(FanBrokerProtocol.ConnectTimeoutMs);
                pipe.ReadMode = PipeTransmissionMode.Byte;

                var request = FanBrokerProtocolCodec.CreateRequest();
                WriteWithTimeout(pipe, request);

                var prefix = new byte[4];
                ReadExactlyWithTimeout(pipe, prefix);
                if (!FanBrokerProtocolCodec.TryReadLengthPrefix(prefix, out var responseLength, out var lengthReason))
                {
                    return SetUnavailable(lengthReason, "broker returned an invalid response length", installed, running);
                }

                if (responseLength != FanBrokerProtocol.MaxResponsePayloadBytes)
                {
                    return SetUnavailable(FanBrokerReason.ProtocolError,
                        $"broker returned an unexpected response length ({responseLength})", installed, running);
                }

                var payload = new byte[responseLength];
                ReadExactlyWithTimeout(pipe, payload);
                if (!FanBrokerProtocolCodec.TryReadResponse(payload, out var snapshot, out var reason))
                {
                    return SetUnavailable(reason, "broker response failed validation", installed, running);
                }

                _lastSnapshot = snapshot;
                _lastSuccessfulResponseUtc = DateTimeOffset.UtcNow;
                Status = BuildStatus(installed, running, true, snapshot);
                return snapshot;
            }
            catch (TimeoutException ex)
            {
                return SetUnavailable(FanBrokerReason.Timeout, "broker response timed out: " + ex.Message, installed, running);
            }
            catch (IOException ex)
            {
                return SetUnavailable(FanBrokerReason.PipeUnavailable, "broker pipe unavailable: " + ex.Message, installed, running);
            }
            catch (UnauthorizedAccessException ex)
            {
                return SetUnavailable(FanBrokerReason.ConnectionRejected,
                    "broker pipe rejected the client: " + ex.Message, installed, running);
            }
            catch (Exception ex)
            {
                return SetUnavailable(FanBrokerReason.ProtocolError, "broker protocol failure: " + ex.Message, installed, running);
            }
        }
    }

    private FanBrokerSnapshot SetUnavailable(FanBrokerReason reason, string detail, bool installed, bool running)
    {
        var snapshot = FanBrokerSnapshot.Unavailable(reason, detail,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            _lastSnapshot?.LastWmiReadMs ?? 0);
        Status = BuildStatus(installed, running, false, snapshot);
        return snapshot;
    }

    private MsiFanBrokerStatus BuildStatus(bool installed, bool running, bool pipeReachable, FanBrokerSnapshot snapshot)
    {
        DateTimeOffset? lastWmiRead = null;
        if (snapshot.LastWmiReadMs > 0)
        {
            lastWmiRead = DateTimeOffset.FromUnixTimeMilliseconds(snapshot.LastWmiReadMs);
        }
        else if (_lastSnapshot is { LastWmiReadMs: > 0 } previous)
        {
            lastWmiRead = DateTimeOffset.FromUnixTimeMilliseconds(previous.LastWmiReadMs);
        }

        return new MsiFanBrokerStatus(
            installed,
            running,
            pipeReachable,
            _lastSuccessfulResponseUtc,
            lastWmiRead,
            snapshot.Channels.Count(channel => channel.Component == FanComponent.Cpu && channel.Available),
            snapshot.Channels.Count(channel => channel.Component == FanComponent.Gpu && channel.Available),
            snapshot.Reason,
            snapshot.Detail);
    }

    private static void ReadExactlyWithTimeout(Stream stream, byte[] buffer)
    {
        var task = ReadExactlyAsync(stream, buffer);
        if (!task.Wait(FanBrokerProtocol.IoTimeoutMs))
        {
            ObserveFault(task);
            throw new TimeoutException("broker did not respond within the I/O timeout");
        }

        task.GetAwaiter().GetResult();
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset)).ConfigureAwait(false);
            if (read <= 0)
            {
                throw new EndOfStreamException("broker closed the pipe before the response was complete");
            }

            offset += read;
        }
    }

    private static void WriteWithTimeout(Stream stream, byte[] buffer)
    {
        var task = WriteAsync(stream, buffer);
        if (!task.Wait(FanBrokerProtocol.IoTimeoutMs))
        {
            ObserveFault(task);
            throw new TimeoutException("broker write did not complete within the I/O timeout");
        }

        task.GetAwaiter().GetResult();
    }

    private static async Task WriteAsync(Stream stream, byte[] buffer)
    {
        await stream.WriteAsync(buffer).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static void ObserveFault(Task task)
    {
        _ = task.ContinueWith(static completed => _ = completed.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    public void Dispose()
    {
        // The client owns no persistent handle between calls.
    }
}
