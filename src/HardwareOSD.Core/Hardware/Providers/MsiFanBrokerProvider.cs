using HardwareOSD.Core.Hardware;

namespace HardwareOSD.Core.Hardware.Providers;

/// <summary>
/// Read-only fan provider backed by the optional LocalSystem broker. It is selected only for a
/// non-elevated process when broker integration is enabled and the service is installed. The
/// provider never falls back to PawnIO or direct EC access.
/// </summary>
public sealed class MsiFanBrokerProvider : ISensorProvider
{
    private const long RetryUnavailableMs = 5_000;

    private readonly IMsiFanBrokerClient _client;
    private FanBrokerSnapshot? _lastSnapshot;
    private long _lastAttemptMs;
    private string _lastReport = "no readings";

    public MsiFanBrokerProvider()
        : this(new MsiFanBrokerClient())
    {
    }

    public MsiFanBrokerProvider(IMsiFanBrokerClient client)
    {
        _client = client;
        Status = client.Status;
    }

    public string Key => SensorMergePolicy.MsiBrokerKey;
    public string Name => "MSI fan broker";
    public string Category => "OEM ACPI/WMI (LocalSystem broker)";
    public bool IsAvailable => true;
    public string? LastError { get; private set; }
    public MsiFanBrokerStatus Status { get; private set; }

    public void Initialize()
    {
        // Probe once so diagnostics are accurate before the first sensor read. The broker may still
        // be installed/started after the app; Read() keeps a bounded retry cache for that case.
        _lastSnapshot = _client.ReadSnapshot();
        _lastAttemptMs = Environment.TickCount64;
        Status = _client.Status;
        LastError = _lastSnapshot.Status == FanBrokerStatus.Ok ? null : _lastSnapshot.Detail;
        if (_lastSnapshot.Status == FanBrokerStatus.Ok)
        {
            _lastReport = string.Join(", ", _lastSnapshot.Channels.Select(channel =>
                $"F{channel.Index + 1}:{channel.Period}->{channel.Rpm} RPM"));
        }
    }

    public IReadOnlyList<SensorValue> Read(long timestampMs)
    {
        var snapshot = ReadSnapshot();
        var values = new List<SensorValue>(10);

        if (snapshot.Status != FanBrokerStatus.Ok)
        {
            LastError = snapshot.Detail;
            AddUnavailablePair(values, NoteFor(snapshot.Reason), timestampMs, "MSI_ACPI Get_Fan (broker)");
            return values;
        }

        LastError = null;
        _lastReport = string.Join(", ", snapshot.Channels.Select(channel =>
            $"F{channel.Index + 1}:{channel.Period}->{channel.Rpm} RPM"));

        var ordinals = new Dictionary<FanComponent, int>();
        foreach (var channel in snapshot.Channels)
        {
            if (!channel.Available)
            {
                continue;
            }

            ordinals.TryGetValue(channel.Component, out var componentIndex);
            ordinals[channel.Component] = componentIndex + 1;

            var name = channel.Component switch
            {
                FanComponent.Cpu => "CPU Fan",
                FanComponent.Gpu => "GPU Fan",
                FanComponent.System => "System Fan",
                _ => "Aux Fan",
            };
            var source = $"MSI_ACPI Get_Fan (broker): {name} (period {channel.Period})";
            var note = channel.Period == 0 ? SensorNotes.MsiFanZero : null;
            values.Add(new SensorValue(
                FanSensorIds.Id(channel.Component, componentIndex, FanMeasure.Rpm),
                channel.Rpm,
                "RPM",
                SensorKind.Fan,
                SensorOrigin.HardwareSensor,
                source,
                snapshot.LastWmiReadMs > 0 ? snapshot.LastWmiReadMs : timestampMs,
                note: note));

            values.Add(SensorValue.Unavailable(
                FanSensorIds.Id(channel.Component, componentIndex, FanMeasure.Percent),
                SensorKind.Fan,
                source,
                snapshot.LastWmiReadMs > 0 ? snapshot.LastWmiReadMs : timestampMs,
                SensorNotes.MsiFanNoPercentage));
        }

        if (!values.Any(value => value.Id.StartsWith(FanSensorIds.CpuPrefix, StringComparison.Ordinal)))
        {
            AddUnavailableComponent(values, FanComponent.Cpu, SensorNotes.NoPlatformFanChannel,
                timestampMs, "MSI_ACPI Get_Fan (broker)");
        }

        if (!values.Any(value => value.Id.StartsWith(FanSensorIds.GpuPrefix, StringComparison.Ordinal)))
        {
            AddUnavailableComponent(values, FanComponent.Gpu, SensorNotes.NoPlatformFanChannel,
                timestampMs, "MSI_ACPI Get_Fan (broker)");
        }

        return values;
    }

    private FanBrokerSnapshot ReadSnapshot()
    {
        var now = Environment.TickCount64;
        // Successful snapshots are always re-read so a stopped/updated service cannot leave stale
        // RPM displayed as current. Only repeated unavailability is cached to avoid hammering a
        // missing service on every poll.
        if (_lastSnapshot is null || _lastSnapshot.Status == FanBrokerStatus.Ok ||
            now - _lastAttemptMs >= RetryUnavailableMs)
        {
            _lastAttemptMs = now;
            _lastSnapshot = _client.ReadSnapshot();
            Status = _client.Status;
            return _lastSnapshot;
        }

        Status = _client.Status;
        return _lastSnapshot;
    }

    private static string NoteFor(FanBrokerReason reason) => reason switch
    {
        FanBrokerReason.AccessDenied => SensorNotes.MsiBrokerAccessDenied,
        FanBrokerReason.ClassMissing => SensorNotes.MsiBrokerClassMissing,
        FanBrokerReason.InstanceUnavailable or FanBrokerReason.WmiReadFailed or FanBrokerReason.NoFanChannels
            or FanBrokerReason.InternalError => SensorNotes.MsiBrokerReadFailed,
        _ => SensorNotes.MsiBrokerUnavailable,
    };

    private static void AddUnavailablePair(
        List<SensorValue> output,
        string note,
        long timestampMs,
        string source)
    {
        AddUnavailableComponent(output, FanComponent.Cpu, note, timestampMs, source);
        AddUnavailableComponent(output, FanComponent.Gpu, note, timestampMs, source);
    }

    private static void AddUnavailableComponent(
        List<SensorValue> output,
        FanComponent component,
        string note,
        long timestampMs,
        string source)
    {
        foreach (var measure in new[] { FanMeasure.Rpm, FanMeasure.Percent })
        {
            output.Add(SensorValue.Unavailable(
                FanSensorIds.Id(component, 0, measure),
                SensorKind.Fan,
                source,
                timestampMs,
                note));
        }
    }

    public IEnumerable<string> Describe()
    {
        yield return $"Broker service: installed={(Status.ServiceInstalled ? "yes" : "no")} running={(Status.ServiceRunning ? "yes" : "no")}";
        yield return $"Pipe reachable: {(Status.PipeReachable ? "yes" : "no")}";
        yield return $"Last response: {FormatTime(Status.LastSuccessfulResponseUtc)}";
        yield return $"Last WMI read: {FormatTime(Status.LastWmiReadUtc)}";
        yield return $"Channels: CPU {Status.CpuChannels}, GPU {Status.GpuChannels}";
        yield return $"Unavailable reason: {Status.Reason} ({Status.Detail})";
        yield return $"Fan readings: {_lastReport}";
        yield return "Mode: fixed GetFanSnapshot over local named pipe; no writes/EC ports";
    }

    private static string FormatTime(DateTimeOffset? value)
        => value is null ? "never" : value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public void Dispose() => _client.Dispose();
}
