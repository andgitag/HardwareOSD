using System.Buffers.Binary;
using System.Text;

namespace HardwareOSD.Core.Hardware;

public enum FanBrokerStatus : byte
{
    Ok = 0,
    Unavailable = 1,
    Error = 2,
}

public enum FanBrokerReason : byte
{
    None = 0,
    ServiceNotInstalled = 1,
    ServiceNotRunning = 2,
    PipeUnavailable = 3,
    Timeout = 4,
    ProtocolError = 5,
    AccessDenied = 6,
    ClassMissing = 7,
    InstanceUnavailable = 8,
    WmiReadFailed = 9,
    NoFanChannels = 10,
    InternalError = 11,
    Disabled = 12,
    Unsupported = 13,
    Busy = 14,
    ConnectionRejected = 15,
}

/// <summary>One fan channel as transported by the broker.</summary>
public sealed record FanBrokerChannel(
    int Index,
    FanComponent Component,
    int Period,
    int Rpm,
    bool Available);

/// <summary>
/// Complete broker answer. This is an immutable value; it never contains invented sensor values.
/// CPU/GPU RPM are -1 when unavailable, while channels only contain decoded firmware readings.
/// </summary>
public sealed record FanBrokerSnapshot(
    FanBrokerStatus Status,
    FanBrokerReason Reason,
    string Detail,
    long TimestampMs,
    long LastWmiReadMs,
    int CpuRpm,
    int GpuRpm,
    int CpuPeriod,
    int GpuPeriod,
    IReadOnlyList<FanBrokerChannel> Channels)
{
    public bool IsAvailable => Status == FanBrokerStatus.Ok;

    public static FanBrokerSnapshot Unavailable(
        FanBrokerReason reason,
        string detail,
        long timestampMs,
        long lastWmiReadMs = 0)
        => new(FanBrokerStatus.Unavailable, reason, detail, timestampMs, lastWmiReadMs,
            -1, -1, 0, 0, Array.Empty<FanBrokerChannel>());
}

/// <summary>
/// Local, serializable status used by the diagnostics page. Reference semantics keep publication
/// atomic when the polling thread updates diagnostics while the UI reads them.
/// </summary>
public sealed record MsiFanBrokerStatus(
    bool ServiceInstalled,
    bool ServiceRunning,
    bool PipeReachable,
    DateTimeOffset? LastSuccessfulResponseUtc,
    DateTimeOffset? LastWmiReadUtc,
    int CpuChannels,
    int GpuChannels,
    FanBrokerReason Reason,
    string Detail)
{
    public static MsiFanBrokerStatus Unavailable(FanBrokerReason reason, string detail, bool installed = false, bool running = false)
        => new(installed, running, false, null, null, 0, 0, reason, detail);
}

/// <summary>Fixed names and bounds shared by the service and client.</summary>
public static class FanBrokerProtocol
{
    public const string ServiceName = "HardwareOSDFanBroker";
    public const string DisplayName = "HardwareOSD Fan Broker";
    public const string PipeName = "HardwareOSD.FanBroker.v1";
    public const ushort Version = 1;
    public const ushort OperationGetFanSnapshot = 1;
    public const int RequestPayloadBytes = 8;
    public const int RequestTotalBytes = 12;
    public const int MaxResponsePayloadBytes = 217;
    public const int MaxWireMessageBytes = 256;
    public const int ChannelCount = 4;
    public const int DetailBytes = 128;
    public const int ConnectTimeoutMs = 750;
    public const int IoTimeoutMs = 1500;
    public const int PollIntervalMs = 1500;
    public const int MaxClients = 4;
    public const int MaxServerInstances = 8;

    public static ReadOnlySpan<byte> Magic => "HOSB"u8;
}

/// <summary>
/// Fixed, versioned wire codec. Requests are exactly 8 bytes behind a 4-byte length prefix.
/// Responses are exactly 217 bytes behind a 4-byte length prefix. No caller-controlled WMI
/// operation, class name, method name, register, port or executable path is accepted.
/// </summary>
public static class FanBrokerProtocolCodec
{
    private const int LengthPrefixBytes = 4;
    private const int ChannelEntryBytes = 12;

    public static byte[] CreateRequest()
    {
        var message = new byte[FanBrokerProtocol.RequestTotalBytes];
        BinaryPrimitives.WriteInt32LittleEndian(message, FanBrokerProtocol.RequestPayloadBytes);
        WriteRequestPayload(message.AsSpan(LengthPrefixBytes));
        return message;
    }

    public static void WriteRequestPayload(Span<byte> destination)
    {
        if (destination.Length < FanBrokerProtocol.RequestPayloadBytes)
        {
            throw new ArgumentException("Request buffer is too small", nameof(destination));
        }

        FanBrokerProtocol.Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(4), FanBrokerProtocol.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(6), FanBrokerProtocol.OperationGetFanSnapshot);
    }

    public static bool TryReadRequest(ReadOnlySpan<byte> payload, out FanBrokerReason reason)
    {
        reason = FanBrokerReason.ProtocolError;
        if (payload.Length != FanBrokerProtocol.RequestPayloadBytes ||
            !payload.Slice(0, 4).SequenceEqual(FanBrokerProtocol.Magic))
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(4)) != FanBrokerProtocol.Version)
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(6)) != FanBrokerProtocol.OperationGetFanSnapshot)
        {
            return false;
        }

        reason = FanBrokerReason.None;
        return true;
    }

    public static bool TryReadLengthPrefix(ReadOnlySpan<byte> prefix, out int payloadLength, out FanBrokerReason reason)
    {
        reason = FanBrokerReason.ProtocolError;
        payloadLength = 0;
        if (prefix.Length < LengthPrefixBytes)
        {
            return false;
        }

        payloadLength = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (payloadLength <= 0 || payloadLength > FanBrokerProtocol.MaxWireMessageBytes)
        {
            reason = FanBrokerReason.ProtocolError;
            return false;
        }

        reason = FanBrokerReason.None;
        return true;
    }

    public static byte[] CreateResponse(FanBrokerSnapshot snapshot)
    {
        var message = new byte[LengthPrefixBytes + FanBrokerProtocol.MaxResponsePayloadBytes];
        BinaryPrimitives.WriteInt32LittleEndian(message, FanBrokerProtocol.MaxResponsePayloadBytes);
        WriteResponsePayload(message.AsSpan(LengthPrefixBytes), snapshot);
        return message;
    }

    public static void WriteResponsePayload(Span<byte> destination, FanBrokerSnapshot snapshot)
    {
        if (destination.Length < FanBrokerProtocol.MaxResponsePayloadBytes)
        {
            throw new ArgumentException("Response buffer is too small", nameof(destination));
        }

        destination[..FanBrokerProtocol.MaxResponsePayloadBytes].Clear();
        FanBrokerProtocol.Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(4), FanBrokerProtocol.Version);
        destination[6] = (byte)snapshot.Status;
        destination[7] = (byte)snapshot.Reason;
        destination[8] = (byte)Math.Clamp(snapshot.Channels.Count, 0, FanBrokerProtocol.ChannelCount);
        destination[9] = 0;
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(10), snapshot.TimestampMs);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(18), snapshot.LastWmiReadMs);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(26), snapshot.CpuRpm);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(30), snapshot.GpuRpm);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(34), (ushort)Math.Max(0, snapshot.CpuPeriod));
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(36), (ushort)Math.Max(0, snapshot.GpuPeriod));
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(38), 0);

        var offset = 40;
        for (var i = 0; i < FanBrokerProtocol.ChannelCount; i++)
        {
            var channel = i < snapshot.Channels.Count ? snapshot.Channels[i] : null;
            destination[offset] = (byte)Math.Max(0, channel?.Index ?? 0);
            destination[offset + 1] = (byte)(channel?.Component ?? FanComponent.Other);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(offset + 2), (ushort)Math.Max(0, channel?.Period ?? 0));
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset + 4), channel?.Rpm ?? -1);
            destination[offset + 8] = channel is { Available: true } ? (byte)1 : (byte)0;
            destination[offset + 9] = 0;
            destination[offset + 10] = 0;
            destination[offset + 11] = 0;
            offset += ChannelEntryBytes;
        }

        var detail = Encoding.UTF8.GetBytes(snapshot.Detail ?? string.Empty);
        var detailLength = Math.Min(Math.Min(detail.Length, FanBrokerProtocol.DetailBytes), 255);
        destination[88] = (byte)detailLength;
        detail.AsSpan(0, detailLength).CopyTo(destination.Slice(89));
    }

    public static bool TryReadResponse(ReadOnlySpan<byte> payload, out FanBrokerSnapshot snapshot, out FanBrokerReason reason)
    {
        snapshot = FanBrokerSnapshot.Unavailable(FanBrokerReason.ProtocolError, "invalid response", 0);
        reason = FanBrokerReason.ProtocolError;
        if (payload.Length != FanBrokerProtocol.MaxResponsePayloadBytes ||
            !payload.Slice(0, 4).SequenceEqual(FanBrokerProtocol.Magic) ||
            BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(4)) != FanBrokerProtocol.Version)
        {
            return false;
        }

        var status = (FanBrokerStatus)payload[6];
        var responseReason = (FanBrokerReason)payload[7];
        var channelCount = payload[8];
        if (!Enum.IsDefined(status) || !Enum.IsDefined(responseReason) ||
            channelCount > FanBrokerProtocol.ChannelCount)
        {
            return false;
        }

        var timestamp = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(10));
        var lastWmiRead = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(18));
        var cpuRpm = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(26));
        var gpuRpm = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(30));
        var cpuPeriod = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(34));
        var gpuPeriod = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(36));

        var channels = new List<FanBrokerChannel>(channelCount);
        for (var i = 0; i < channelCount; i++)
        {
            var offset = 40 + (i * ChannelEntryBytes);
            var index = payload[offset];
            var component = (FanComponent)payload[offset + 1];
            var period = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(offset + 2));
            var rpm = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset + 4));
            var available = payload[offset + 8] != 0;
            if (!Enum.IsDefined(component) || index > FanBrokerProtocol.ChannelCount)
            {
                return false;
            }

            channels.Add(new FanBrokerChannel(index, component, period, rpm, available));
        }

        var detailLength = payload[88];
        if (detailLength > FanBrokerProtocol.DetailBytes)
        {
            return false;
        }

        var detail = detailLength == 0
            ? string.Empty
            : Encoding.UTF8.GetString(payload.Slice(89, detailLength));

        snapshot = new FanBrokerSnapshot(status, responseReason, detail, timestamp, lastWmiRead,
            cpuRpm, gpuRpm, cpuPeriod, gpuPeriod, channels);
        reason = FanBrokerReason.None;
        return true;
    }

    public static FanBrokerSnapshot ReadOnlyResponse(FanBrokerReason reason, string detail)
        => FanBrokerSnapshot.Unavailable(reason, detail, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}
