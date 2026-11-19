using HardwareOSD.Core.Hardware;

namespace HardwareOSD.FanBroker;

/// <summary>Latest immutable snapshot, published by the WMI poll loop and read by pipe clients.</summary>
internal sealed class BrokerSnapshotStore
{
    private FanBrokerSnapshot _current = FanBrokerSnapshot.Unavailable(
        FanBrokerReason.InternalError,
        "broker is starting",
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public FanBrokerSnapshot Current => Volatile.Read(ref _current);

    public void Publish(FanBrokerSnapshot snapshot) => Volatile.Write(ref _current, snapshot);
}
