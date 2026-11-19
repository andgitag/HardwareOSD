using HardwareOSD.Core.Hardware;

namespace HardwareOSD.Tests;

public class SensorHistoryTests
{
    [Fact]
    public void TryCopy_ReturnsSamplesOldestFirst()
    {
        var history = new SensorHistory(8);
        history.Track(new[] { SensorIds.CpuUsage });

        for (var i = 1; i <= 5; i++)
        {
            history.Append(SnapshotFactory.Build((SensorIds.CpuUsage, i * 10, "%", SensorKind.Load)));
        }

        var buffer = new double[8];
        var count = history.TryCopy(SensorIds.CpuUsage, buffer);
        Assert.Equal(8, count);
        Assert.Equal(new double[] { double.NaN, double.NaN, double.NaN, 10, 20, 30, 40, 50 }, buffer);
    }

    [Fact]
    public void TryCopy_WrapsAtCapacity()
    {
        var history = new SensorHistory(4);
        history.Track(new[] { SensorIds.CpuUsage });
        for (var i = 1; i <= 6; i++)
        {
            history.Append(SnapshotFactory.Build((SensorIds.CpuUsage, i, "%", SensorKind.Load)));
        }

        var buffer = new double[4];
        var count = history.TryCopy(SensorIds.CpuUsage, buffer);
        Assert.Equal(4, count);
        Assert.Equal(new double[] { 3, 4, 5, 6 }, buffer);
    }

    [Fact]
    public void TryCopy_UnknownSeriesReturnsZero()
    {
        var history = new SensorHistory(4);
        Assert.Equal(0, history.TryCopy("missing", new double[4]));
    }

    [Fact]
    public void Append_IgnoresUnavailableSensorsAsNaN()
    {
        var history = new SensorHistory(4);
        history.Track(new[] { SensorIds.CpuTemperature });
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var snapshot = SensorSnapshot.Create(1, now, new Dictionary<string, SensorValue>
        {
            [SensorIds.CpuTemperature] = SensorValue.Unavailable(SensorIds.CpuTemperature, SensorKind.Temperature, "test", now),
        });

        history.Append(snapshot);
        var buffer = new double[4];
        history.TryCopy(SensorIds.CpuTemperature, buffer);
        Assert.True(double.IsNaN(buffer[^1]));
    }

    [Fact]
    public async Task TryCopy_DoesNotBlockWhileAppending()
    {
        var history = new SensorHistory(64);
        history.Track(new[] { SensorIds.CpuUsage });
        var buffer = new double[64];
        var errors = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));

        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var count = history.TryCopy(SensorIds.CpuUsage, buffer);
                if (count < 0 || count > 64)
                {
                    Interlocked.Increment(ref errors);
                }
            }
        });

        var writer = Task.Run(() =>
        {
            var i = 0.0;
            while (!stop.IsCancellationRequested)
            {
                history.Append(SnapshotFactory.Build((SensorIds.CpuUsage, i++, "%", SensorKind.Load)));
            }
        });

        await Task.WhenAll(reader, writer);
        Assert.Equal(0, errors);
    }
}
