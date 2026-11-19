using HardwareOSD.Core.Config;
using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.Hardware.Providers;

namespace HardwareOSD.Tests;

public class FanBrokerTests
{
    private const long Now = 1_700_000_000_000;

    private static MsiFanReadResult SuccessfulRead(params (int Period, double Rpm)[] readings)
    {
        var list = readings.Select((reading, index) =>
                new MsiFanReading(index, reading.Period, reading.Rpm))
            .ToArray();
        return new MsiFanReadResult(MsiFanReadStatus.Ok, list, "ok", Now, Now);
    }

    private static FanBrokerSnapshot Snapshot(
        FanBrokerStatus status = FanBrokerStatus.Ok,
        FanBrokerReason reason = FanBrokerReason.None)
        => new(
            status,
            reason,
            "test detail",
            Now,
            Now,
            2400,
            2892,
            200,
            166,
            new[]
            {
                new FanBrokerChannel(0, FanComponent.Cpu, 200, 2400, true),
                new FanBrokerChannel(1, FanComponent.Gpu, 166, 2892, true),
            });

    [Fact]
    public void Request_RoundTripsAndRejectsMalformedHeaders()
    {
        var request = FanBrokerProtocolCodec.CreateRequest();
        Assert.True(FanBrokerProtocolCodec.TryReadLengthPrefix(request, out var length, out _));
        Assert.Equal(FanBrokerProtocol.RequestPayloadBytes, length);
        Assert.True(FanBrokerProtocolCodec.TryReadRequest(request.AsSpan(4), out var reason));
        Assert.Equal(FanBrokerReason.None, reason);

        var badMagic = request.AsSpan(4).ToArray();
        badMagic[0] = 0;
        Assert.False(FanBrokerProtocolCodec.TryReadRequest(badMagic, out _));

        var badVersion = request.AsSpan(4).ToArray();
        badVersion[4] = 99;
        Assert.False(FanBrokerProtocolCodec.TryReadRequest(badVersion, out _));

        var badOperation = request.AsSpan(4).ToArray();
        badOperation[6] = 99;
        Assert.False(FanBrokerProtocolCodec.TryReadRequest(badOperation, out _));
    }

    [Fact]
    public void LengthPrefix_RejectsOversizedAndZeroLengths()
    {
        var oversized = BitConverter.GetBytes(FanBrokerProtocol.MaxWireMessageBytes + 1);
        Assert.False(FanBrokerProtocolCodec.TryReadLengthPrefix(oversized, out _, out _));

        var zero = BitConverter.GetBytes(0);
        Assert.False(FanBrokerProtocolCodec.TryReadLengthPrefix(zero, out _, out _));
    }

    [Fact]
    public void Response_RoundTripsBigEndianPeriodsZeroPeriodsAndChannels()
    {
        var snapshot = new FanBrokerSnapshot(
            FanBrokerStatus.Ok,
            FanBrokerReason.None,
            "live",
            Now,
            Now - 100,
            2400,
            0,
            200,
            0,
            new[]
            {
                new FanBrokerChannel(0, FanComponent.Cpu, 200, 2400, true),
                new FanBrokerChannel(1, FanComponent.Gpu, 0, 0, true),
                new FanBrokerChannel(2, FanComponent.System, 300, 1600, true),
            });

        var wire = FanBrokerProtocolCodec.CreateResponse(snapshot);
        Assert.Equal(FanBrokerProtocol.MaxResponsePayloadBytes, BitConverter.ToInt32(wire, 0));
        Assert.True(FanBrokerProtocolCodec.TryReadResponse(wire.AsSpan(4), out var decoded, out var reason));
        Assert.Equal(FanBrokerReason.None, reason);
        Assert.Equal(FanBrokerStatus.Ok, decoded.Status);
        Assert.Equal(200, decoded.CpuPeriod);
        Assert.Equal(0, decoded.GpuPeriod);
        Assert.Equal(0, decoded.GpuRpm);
        Assert.Equal(3, decoded.Channels.Count);
        Assert.Equal(FanComponent.System, decoded.Channels[2].Component);
        Assert.Equal(1600, decoded.Channels[2].Rpm);
    }

    [Fact]
    public void Response_RoundTripsUnavailableReasonAndTruncatesDetail()
    {
        var snapshot = FanBrokerSnapshot.Unavailable(
            FanBrokerReason.ServiceNotRunning,
            new string('x', 400),
            Now,
            Now - 1);
        var wire = FanBrokerProtocolCodec.CreateResponse(snapshot);
        Assert.True(FanBrokerProtocolCodec.TryReadResponse(wire.AsSpan(4), out var decoded, out _));
        Assert.Equal(FanBrokerStatus.Unavailable, decoded.Status);
        Assert.Equal(FanBrokerReason.ServiceNotRunning, decoded.Reason);
        Assert.Equal(-1, decoded.CpuRpm);
        Assert.Equal(-1, decoded.GpuRpm);
        Assert.Equal(FanBrokerProtocol.DetailBytes, decoded.Detail.Length);
    }

    [Fact]
    public void Response_RejectsBadLengthMagicAndChannelCount()
    {
        var wire = FanBrokerProtocolCodec.CreateResponse(Snapshot());
        Assert.False(FanBrokerProtocolCodec.TryReadResponse(wire.AsSpan(4, wire.Length - 5), out _, out _));

        var badMagic = wire.AsSpan(4).ToArray();
        badMagic[0] = 0;
        Assert.False(FanBrokerProtocolCodec.TryReadResponse(badMagic, out _, out _));

        var badCount = wire.AsSpan(4).ToArray();
        badCount[8] = 5;
        Assert.False(FanBrokerProtocolCodec.TryReadResponse(badCount, out _, out _));
    }

    [Fact]
    public void SnapshotFactory_KeepsCpuZeroAndOmitsUnusedThirdChannel()
    {
        var result = SuccessfulRead((200, 2400), (0, 0), (0, 0), (0, 0));
        var snapshot = MsiFanBrokerSnapshotFactory.FromReadResult(result, Now);
        Assert.Equal(FanBrokerStatus.Ok, snapshot.Status);
        Assert.Equal(2400, snapshot.CpuRpm);
        Assert.Equal(0, snapshot.GpuRpm);
        Assert.Equal(0, snapshot.GpuPeriod);
        Assert.Equal(2, snapshot.Channels.Count);
        Assert.Equal(0, snapshot.Channels[1].Rpm);
    }

    [Fact]
    public void SnapshotFactory_AddsOnlyNonZeroAdditionalFan()
    {
        var result = SuccessfulRead((200, 2400), (166, 2892), (300, 1600), (0, 0));
        var snapshot = MsiFanBrokerSnapshotFactory.FromReadResult(result, Now);
        Assert.Equal(3, snapshot.Channels.Count);
        Assert.Equal(FanComponent.System, snapshot.Channels[2].Component);
        Assert.Equal(1600, snapshot.Channels[2].Rpm);
    }

    [Fact]
    public void SnapshotFactory_MapsAccessDeniedWithoutInventingValues()
    {
        var result = new MsiFanReadResult(MsiFanReadStatus.AccessDenied, Array.Empty<MsiFanReading>(),
            "access denied", Now, 0);
        var snapshot = MsiFanBrokerSnapshotFactory.FromReadResult(result, Now);
        Assert.Equal(FanBrokerStatus.Unavailable, snapshot.Status);
        Assert.Equal(FanBrokerReason.AccessDenied, snapshot.Reason);
        Assert.Equal(-1, snapshot.CpuRpm);
        Assert.Equal(-1, snapshot.GpuRpm);
        Assert.Empty(snapshot.Channels);
    }

    [Fact]
    public void SourceSelector_ReturnsExactlyOnePath()
    {
        Assert.Equal(MsiFanSource.None, MsiFanSourceSelector.Select(false, true, false, true));
        Assert.Equal(MsiFanSource.DirectWmi, MsiFanSourceSelector.Select(true, true, true, true));
        Assert.Equal(MsiFanSource.Broker, MsiFanSourceSelector.Select(true, true, false, true));
        Assert.Equal(MsiFanSource.DirectWmi, MsiFanSourceSelector.Select(true, false, false, true));
        Assert.Equal(MsiFanSource.DirectWmi, MsiFanSourceSelector.Select(true, true, false, false));
    }

    [Fact]
    public void ProviderFactory_SelectsOneMsiSourceAndKeepsOtherProviders()
    {
        var settings = AppSettings.CreateDefault();
        var elevated = SensorProviderFactory.Create(settings, elevatedOverride: true, brokerInstalledOverride: true);
        Assert.Single(elevated.OfType<MsiAcpiFanProvider>());
        Assert.Empty(elevated.OfType<MsiFanBrokerProvider>());
        Assert.Contains(elevated, provider => provider is WindowsSystemProvider);
        Assert.Contains(elevated, provider => provider is ThermalZoneProvider);

        var brokered = SensorProviderFactory.Create(settings, elevatedOverride: false, brokerInstalledOverride: true);
        Assert.Empty(brokered.OfType<MsiAcpiFanProvider>());
        Assert.Single(brokered.OfType<MsiFanBrokerProvider>());

        var direct = SensorProviderFactory.Create(settings, elevatedOverride: false, brokerInstalledOverride: false);
        Assert.Single(direct.OfType<MsiAcpiFanProvider>());
        Assert.Empty(direct.OfType<MsiFanBrokerProvider>());

        settings.Monitoring.EnableMsiAcpi = false;
        var disabled = SensorProviderFactory.Create(settings, elevatedOverride: false, brokerInstalledOverride: true);
        Assert.DoesNotContain(disabled, provider => provider is MsiAcpiFanProvider or MsiFanBrokerProvider);
    }

    [Fact]
    public void BrokerProvider_PublishesRpmAndExplicitlyOmitsPercentage()
    {
        var fake = new FakeBrokerClient(Snapshot());
        using var provider = new MsiFanBrokerProvider(fake);
        var values = provider.Read(Now);

        var cpu = Assert.Single(values, value => value.Id == "fan.cpu.0.rpm");
        Assert.True(cpu.Available);
        Assert.Equal(2400, cpu.Value);
        Assert.StartsWith("MSI_ACPI Get_Fan (broker)", cpu.Source);

        var percent = Assert.Single(values, value => value.Id == "fan.cpu.0.percent");
        Assert.False(percent.Available);
        Assert.Equal(SensorNotes.MsiFanNoPercentage, percent.Note);
        Assert.Single(values, value => value.Id == "fan.gpu.0.rpm");
    }

    [Fact]
    public void BrokerProvider_MapsUnavailableReasonsWithoutSensors()
    {
        var fake = new FakeBrokerClient(FanBrokerSnapshot.Unavailable(
            FanBrokerReason.ServiceNotRunning, "service stopped", Now));
        using var provider = new MsiFanBrokerProvider(fake);
        var values = provider.Read(Now);
        Assert.Equal(4, values.Count);
        Assert.All(values, value => Assert.False(value.Available));
        Assert.All(values, value => Assert.Equal(SensorNotes.MsiBrokerUnavailable, value.Note));
        Assert.Equal("service stopped", provider.LastError);
    }

    [Fact]
    public void BrokerProvider_MapsAccessDeniedToSpecificNote()
    {
        var fake = new FakeBrokerClient(FanBrokerSnapshot.Unavailable(
            FanBrokerReason.AccessDenied, "WMI denied", Now));
        using var provider = new MsiFanBrokerProvider(fake);
        var values = provider.Read(Now);
        Assert.All(values, value => Assert.Equal(SensorNotes.MsiBrokerAccessDenied, value.Note));
    }

    [Fact]
    public void BrokerProvider_DoesNotReturnStaleRpmAfterUnavailableResponse()
    {
        var fake = new FakeBrokerClient(Snapshot(), FanBrokerSnapshot.Unavailable(
            FanBrokerReason.PipeUnavailable, "pipe died", Now + 1));
        using var provider = new MsiFanBrokerProvider(fake);
        var first = provider.Read(Now);
        Assert.Contains(first, value => value.Id == "fan.cpu.0.rpm" && value.Available);

        var second = provider.Read(Now + 1);
        Assert.All(second, value => Assert.False(value.Available));
        Assert.All(second, value => Assert.Equal(SensorNotes.MsiBrokerUnavailable, value.Note));
    }

    [Fact]
    public void Settings_DefaultAndCloneKeepBrokerToggle()
    {
        var defaults = AppSettings.CreateDefault();
        Assert.True(defaults.Monitoring.EnableMsiFanBroker);

        defaults.Monitoring.EnableMsiFanBroker = false;
        var clone = defaults.Clone();
        Assert.False(clone.Monitoring.EnableMsiFanBroker);
    }

    private sealed class FakeBrokerClient : IMsiFanBrokerClient
    {
        private readonly FanBrokerSnapshot[] _snapshots;
        private int _index;

        public FakeBrokerClient(params FanBrokerSnapshot[] snapshots)
        {
            _snapshots = snapshots;
        }

        public MsiFanBrokerStatus Status { get; private set; } = MsiFanBrokerStatus.Unavailable(
            FanBrokerReason.ServiceNotRunning, "test");

        public FanBrokerSnapshot ReadSnapshot()
        {
            var snapshot = _snapshots[Math.Min(_index, _snapshots.Length - 1)];
            _index++;
            Status = new MsiFanBrokerStatus(
                true,
                true,
                snapshot.Status == FanBrokerStatus.Ok,
                DateTimeOffset.UtcNow,
                DateTimeOffset.FromUnixTimeMilliseconds(snapshot.LastWmiReadMs),
                snapshot.Channels.Count(channel => channel.Component == FanComponent.Cpu && channel.Available),
                snapshot.Channels.Count(channel => channel.Component == FanComponent.Gpu && channel.Available),
                snapshot.Reason,
                snapshot.Detail);
            return snapshot;
        }

        public void Dispose()
        {
        }
    }
}
