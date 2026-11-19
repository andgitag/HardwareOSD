using System.ComponentModel;
using System.IO.Pipes;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using HardwareOSD.Core.Diagnostics;
using HardwareOSD.Core.Hardware;
using Microsoft.Win32.SafeHandles;

namespace HardwareOSD.FanBroker;

/// <summary>
/// Fixed named-pipe endpoint. The server only answers one operation: GetFanSnapshot. It rejects
/// remote clients, limits concurrent connections and applies read/write deadlines.
/// </summary>
internal sealed class FanBrokerPipeServer
{
    private readonly string _pipeName;
    private readonly BrokerSnapshotStore _store;
    private readonly ILog _log;
    private readonly SemaphoreSlim _slots = new(FanBrokerProtocol.MaxClients, FanBrokerProtocol.MaxClients);

    public FanBrokerPipeServer(string pipeName, BrokerSnapshotStore store, ILog log)
    {
        _pipeName = pipeName;
        _store = store;
        _log = log;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = CreateServer();
            }
            catch (Exception ex)
            {
                _log.Error("Pipe", "Could not create broker pipe", ex);
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                using var registration = cancellationToken.Register(static state =>
                    ((NamedPipeServerStream)state!).Dispose(), server);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                server.Dispose();
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                server.Dispose();
                break;
            }
            catch (Exception ex)
            {
                _log.Warn("Pipe", "Waiting for a broker client failed", ex);
                server.Dispose();
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!PipeClientIdentity.IsLocalClient(server.SafePipeHandle, out var computerName, out var identityError))
            {
                _log.Warn("Pipe", $"Rejected non-local or unverifiable client ({identityError})");
                server.Dispose();
                continue;
            }

            if (!_slots.Wait(0))
            {
                _log.Warn("Pipe", "Rejected client because the connection limit was reached");
                server.Dispose();
                continue;
            }

            _log.Info("Pipe", $"Client connected from {computerName}");
            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleClientAsync(server, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Warn("Pipe", "Client request failed", ex);
                }
                finally
                {
                    server.Dispose();
                    _slots.Release();
                }
            }, CancellationToken.None);
        }
    }

    private NamedPipeServerStream CreateServer()
    {
        var security = new PipeSecurity();
        AddRule(security, WellKnownSidType.LocalSystemSid, PipeAccessRights.FullControl);
        AddRule(security, WellKnownSidType.BuiltinAdministratorsSid, PipeAccessRights.FullControl);
        AddRule(security, WellKnownSidType.InteractiveSid, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize);

        return NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
            FanBrokerProtocol.MaxServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            security);
    }

    private static void AddRule(PipeSecurity security, WellKnownSidType sidType, PipeAccessRights rights)
    {
        var sid = new SecurityIdentifier(sidType, null);
        security.AddAccessRule(new PipeAccessRule(sid, rights, AccessControlType.Allow));
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken hostToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
        timeout.CancelAfter(FanBrokerProtocol.IoTimeoutMs);
        var token = timeout.Token;

        var prefix = new byte[4];
        try
        {
            await ReadExactlyAsync(server, prefix, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log.Warn("Pipe", "Client timed out before sending a request");
            return;
        }
        catch (EndOfStreamException)
        {
            _log.Warn("Pipe", "Client disconnected before sending a request");
            return;
        }

        if (!FanBrokerProtocolCodec.TryReadLengthPrefix(prefix, out var requestLength, out _) ||
            requestLength != FanBrokerProtocol.RequestPayloadBytes)
        {
            _log.Warn("Pipe", $"Rejected malformed request length ({requestLength})");
            await TrySendErrorAsync(server, FanBrokerReason.ProtocolError,
                "request length is invalid", token).ConfigureAwait(false);
            return;
        }

        var request = new byte[requestLength];
        try
        {
            await ReadExactlyAsync(server, request, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log.Warn("Pipe", "Client timed out while sending the request body");
            return;
        }

        if (!FanBrokerProtocolCodec.TryReadRequest(request, out var requestReason))
        {
            _log.Warn("Pipe", $"Rejected malformed request ({requestReason})");
            await TrySendErrorAsync(server, requestReason, "request validation failed", token).ConfigureAwait(false);
            return;
        }

        var response = FanBrokerProtocolCodec.CreateResponse(_store.Current);
        try
        {
            await WriteAllAsync(server, response, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log.Warn("Pipe", "Client timed out while receiving the response");
        }
    }

    private async Task TrySendErrorAsync(
        NamedPipeServerStream server,
        FanBrokerReason reason,
        string detail,
        CancellationToken token)
    {
        try
        {
            var response = FanBrokerProtocolCodec.CreateResponse(
                FanBrokerSnapshot.Unavailable(reason, detail, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            await WriteAllAsync(server, response, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Debug("Pipe", "Could not send protocol error response: " + ex.Message);
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), token)
                .ConfigureAwait(false);
            if (read <= 0)
            {
                throw new EndOfStreamException("client closed the pipe");
            }

            offset += read;
        }
    }

    private static async Task WriteAllAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        await stream.WriteAsync(buffer, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
}

/// <summary>Verifies that the connected pipe client is on this machine.</summary>
internal static class PipeClientIdentity
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetNamedPipeClientComputerName(
        SafePipeHandle pipe,
        [Out] StringBuilder clientComputerName,
        uint clientComputerNameLength);

    public static bool IsLocalClient(
        SafePipeHandle pipe,
        out string computerName,
        out string error)
    {
        computerName = string.Empty;
        error = string.Empty;
        var buffer = new StringBuilder(256);
        if (!GetNamedPipeClientComputerName(pipe, buffer, (uint)buffer.Capacity))
        {
            var win32 = Marshal.GetLastWin32Error();
            // ERROR_PIPE_LOCAL means the client is on this machine; Windows intentionally returns
            // FALSE for that case instead of a computer name.
            if (win32 == 229)
            {
                computerName = Environment.MachineName;
                return true;
            }

            error = new Win32Exception(win32).Message;
            return false;
        }

        computerName = buffer.ToString().Trim().TrimStart('\\');
        if (string.IsNullOrWhiteSpace(computerName))
        {
            error = "client computer name is empty";
            return false;
        }

        var clientName = computerName;
        var localNames = new[]
        {
            Environment.MachineName,
            Dns.GetHostName(),
            "localhost",
            "127.0.0.1",
            ".",
        };
        if (localNames.Any(name => string.Equals(name, clientName, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        error = "client computer is not local: " + computerName;
        return false;
    }
}
