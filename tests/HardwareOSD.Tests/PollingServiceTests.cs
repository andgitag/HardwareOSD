using HardwareOSD.Core.Diagnostics;
using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.Hardware.Providers;

namespace HardwareOSD.Tests;

public class PollingServiceTests
{
    private sealed class FakeProvider : ISensorProvider
    {
        private int _reads;

        public FakeProvider(string key, string name, Func<int, IReadOnlyList<SensorValue>> read)
        {
            Key = key;
            Name = name;
            ReadFunc = read;
        }

        public string Key { get; }
        public string Name { get; }
        public string Category => "test";
        public bool IsAvailable { get; private set; }
        public string? LastError { get; private set; }
        public Func<int, IReadOnlyList<SensorValue>> ReadFunc { get; set; }
        public int ReadCount => Volatile.Read(ref _reads);
        public int DisposeCount { get; private set; }
        public bool ThrowOnRead { get; set; }

        public void Initialize() => IsAvailable = true;

        public IReadOnlyList<SensorValue> Read(long timestampMs)
        {
            Interlocked.Increment(ref _reads);
            if (ThrowOnRead)
            {
                LastError = "boom";
                throw new InvalidOperationException("boom");
            }

            return ReadFunc(ReadCount);
        }

        public IEnumerable<string> Describe() => new[] { "fake" };

        public void Dispose()
        {
            DisposeCount++;
            IsAvailable = false;
        }
    }

    [Fact]
    public async Task Polling_PublishesSnapshotsAndStatistics()
    {
        var provider = new FakeProvider("win", "fake", _ => new[]
        {
            new SensorValue(SensorIds.CpuUsage, 42, "%", SensorKind.Load, SensorOrigin.WindowsApi, "fake", 0),
        });

        var cache = new SensorCache();
        var history = new SensorHistory(16);
        history.Track(new[] { SensorIds.CpuUsage });
        using var service = new SensorPollingService(new[] { provider }, cache, history, NullLog.Instance, 100);

        service.Start();
        await Task.Delay(450);

        var stats = service.Statistics;
        Assert.True(stats.PollCount >= 2, $"expected several polls, got {stats.PollCount}");
        Assert.True(cache.Latest.Sequence > 0);
        Assert.Equal(42, cache.Latest.ValueOrNaN(SensorIds.CpuUsage), 3);
        Assert.Contains(stats.Providers, p => p.Name == "fake" && p.Available);

        service.Stop();
        Assert.False(service.IsRunning);
        Assert.Empty(cache.Latest.Values); // stopping discards stale hardware values

        var buffer = new double[16];
        Assert.True(history.TryCopy(SensorIds.CpuUsage, buffer) > 0);
    }

    [Fact]
    public async Task FailingProvider_DoesNotStopOtherProviders()
    {
        var broken = new FakeProvider("nvml", "broken", _ => Array.Empty<SensorValue>()) { ThrowOnRead = true };
        var healthy = new FakeProvider("win", "healthy", _ => new[]
        {
            new SensorValue(SensorIds.RamPercent, 33, "%", SensorKind.Load, SensorOrigin.WindowsApi, "fake", 0),
        });

        var cache = new SensorCache();
        using var service = new SensorPollingService(
            new ISensorProvider[] { broken, healthy }, cache, new SensorHistory(8), NullLog.Instance, 100);

        service.Start();
        await Task.Delay(600);

        Assert.Equal(33, cache.Latest.ValueOrNaN(SensorIds.RamPercent), 3);
        service.Stop();

        var stats = service.Statistics;
        Assert.Contains(stats.Providers, p => p.Name == "broken" && p.ConsecutiveFailures >= 1);
        Assert.Contains(stats.Providers, p => p.Name == "healthy" && p.ConsecutiveFailures == 0);
    }

    [Fact]
    public async Task ValueThatDisappearsFromAProviderIsNotKeptAsStale()
    {
        var provider = new FakeProvider("win", "temporary", count => count == 1
            ? new[]
            {
                new SensorValue(SensorIds.CpuTemperature, 55, "C", SensorKind.Temperature,
                    SensorOrigin.WindowsApi, "fake", 0),
            }
            : Array.Empty<SensorValue>());

        var cache = new SensorCache();
        using var service = new SensorPollingService(
            new[] { provider }, cache, new SensorHistory(8), NullLog.Instance, 100);

        service.Start();
        await Task.Delay(600);

        Assert.True(provider.ReadCount >= 2);
        Assert.False(cache.Latest.IsAvailable(SensorIds.CpuTemperature));
        service.Stop();
    }

    [Fact]
    public async Task Stop_EndsPolling()
    {
        var provider = new FakeProvider("win", "fake", _ => new[]
        {
            new SensorValue(SensorIds.CpuUsage, 1, "%", SensorKind.Load, SensorOrigin.WindowsApi, "fake", 0),
        });
        var cache = new SensorCache();
        using var service = new SensorPollingService(new[] { provider }, cache, new SensorHistory(8), NullLog.Instance, 100);

        service.Start();
        await Task.Delay(250);
        service.Stop();

        var readsAfterStop = provider.ReadCount;
        await Task.Delay(300);
        Assert.Equal(readsAfterStop, provider.ReadCount);
        Assert.Empty(cache.Latest.Values); // cache is cleared on stop so stale data is never shown
    }

    [Fact]
    public async Task IntervalChange_IsClampedAndApplied()
    {
        var provider = new FakeProvider("win", "fake", _ => Array.Empty<SensorValue>());
        using var service = new SensorPollingService(new[] { provider }, new SensorCache(), new SensorHistory(8), NullLog.Instance, 1000);

        service.IntervalMs = 5;
        Assert.Equal(100, service.IntervalMs);
        service.IntervalMs = 99_999;
        Assert.Equal(10_000, service.IntervalMs);

        service.Start();
        await Task.Delay(150);
        service.Stop();
        Assert.True(provider.ReadCount >= 1);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var provider = new FakeProvider("win", "fake", _ => Array.Empty<SensorValue>());
        var service = new SensorPollingService(new[] { provider }, new SensorCache(), new SensorHistory(8), NullLog.Instance, 100);
        service.Dispose();
        service.Dispose();
        Assert.Equal(1, provider.DisposeCount);
    }
}
