using HardwareOSD.Core.Hardware;

namespace HardwareOSD.Tests;

public class SensorCacheTests
{
    [Fact]
    public void Latest_IsEmptyBeforeFirstPublish()
    {
        var cache = new SensorCache();
        Assert.NotNull(cache.Latest);
        Assert.Equal(0, cache.Latest.Sequence);
        Assert.Empty(cache.Latest.Values);
    }

    [Fact]
    public void Publish_SwapsSnapshotAtomically()
    {
        var cache = new SensorCache();
        var snapshot = SnapshotFactory.Typical();
        cache.Publish(snapshot);

        Assert.Same(snapshot, cache.Latest);
        Assert.Equal(1, cache.PublishCount);
        Assert.True(cache.TryGet(SensorIds.CpuTemperature, out var value));
        Assert.Equal(62, value.Value);
    }

    [Fact]
    public async Task ConcurrentReaders_NeverObservePartialSnapshots()
    {
        var cache = new SensorCache();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var errors = 0;
        var reads = 0L;

        var readers = Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
        {
            long lastSequence = -1;
            while (!stop.IsCancellationRequested)
            {
                var snapshot = cache.Latest;
                if (snapshot.Sequence < lastSequence)
                {
                    Interlocked.Increment(ref errors);
                }

                lastSequence = snapshot.Sequence;

                // A published snapshot must always be internally consistent: all values belong to
                // the same sequence, so the CPU temperature is always present alongside the GPU one.
                if (snapshot.Sequence > 0 && snapshot.Count != 11)
                {
                    Interlocked.Increment(ref errors);
                }

                Interlocked.Increment(ref reads);
            }
        })).ToArray();

        var writer = Task.Run(async () =>
        {
            for (var i = 1; i <= 4000; i++)
            {
                var values = new Dictionary<string, SensorValue>(StringComparer.Ordinal);
                for (var s = 0; s < 11; s++)
                {
                    values[$"sensor.{s}"] = new SensorValue($"sensor.{s}", i, "u", SensorKind.Data, SensorOrigin.HardwareSensor, "test", i);
                }

                cache.Publish(SensorSnapshot.Create(i, i, values));
                if (i % 64 == 0)
                {
                    await Task.Yield();
                }
            }
        });

        await Task.WhenAll(readers.Append(writer));
        Assert.Equal(0, errors);
        Assert.True(reads > 1000, $"expected many reads, got {reads}");
        Assert.Equal(4000, cache.Latest.Sequence);
    }

    [Fact]
    public void Clear_ResetsToEmptySnapshot()
    {
        var cache = new SensorCache();
        cache.Publish(SnapshotFactory.Typical());
        cache.Clear();
        Assert.Equal(0, cache.Latest.Sequence);
        Assert.Empty(cache.Latest.Values);
    }
}
