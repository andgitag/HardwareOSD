using HardwareOSD.Core.SystemInfo;

namespace HardwareOSD.Tests;

public class SystemInfoTests
{
    private sealed class CountingCollector : ISystemInfoCollector
    {
        public int Calls { get; private set; }

        public SystemInfoSnapshot Collect(long capturedAtMs)
        {
            Calls++;
            return new SystemInfoSnapshot(
                capturedAtMs,
                new[] { new SystemInfoItem("os.name", "Test OS") },
                Array.Empty<SystemInfoItem>(),
                Array.Empty<SystemInfoItem>(),
                Array.Empty<SystemInfoItem>(),
                Array.Empty<GpuInfo>(),
                Array.Empty<StorageInfo>(),
                null);
        }
    }

    [Theory]
    [InlineData(68_719_476_736L, "64 GB")]
    [InlineData(2_000_396_321_280L, "1.82 TB")]
    [InlineData(0L, null)]
    public void FormatBytes_UsesBinaryUnits(long bytes, string? expected)
        => Assert.Equal(expected, SystemInfoFormat.FormatBytes(bytes));

    [Fact]
    public void FormatBytes_KeepsGigabyteScale()
    {
        Assert.Equal("512 MB", SystemInfoFormat.FormatBytes(512L * 1024 * 1024));
        Assert.Equal("12 GB", SystemInfoFormat.FormatBytes(12L * 1024 * 1024 * 1024));
    }

    [Theory]
    [InlineData("Microsoft Windows 11 Pro", "Pro")]
    [InlineData("Microsoft Windows 11 Home Single Language", "Home Single Language")]
    [InlineData("Windows 10 Enterprise", "Enterprise")]
    [InlineData("", null)]
    public void ParseEdition_ExtractsTheCaptionSuffix(string caption, string? expected)
        => Assert.Equal(expected, SystemInfoFormat.ParseEdition(caption));

    [Theory]
    [InlineData(20, "DDR")]
    [InlineData(26, "DDR4")]
    [InlineData(34, "DDR5")]
    [InlineData(35, "LPDDR5")]
    [InlineData(0, null)]
    public void MemoryTypeName_MapsSmbiosValues(int value, string? expected)
        => Assert.Equal(expected, SystemInfoFormat.MemoryTypeName(value));

    [Fact]
    public void CpuVendorName_NormalizesCpuidStrings()
    {
        Assert.Equal("Intel", SystemInfoFormat.CpuVendorName("GenuineIntel"));
        Assert.Equal("AMD", SystemInfoFormat.CpuVendorName("AuthenticAMD"));
        Assert.Equal("Other", SystemInfoFormat.CpuVendorName("Other"));
        Assert.Null(SystemInfoFormat.CpuVendorName(null));
    }

    [Fact]
    public void StorageTypeName_CombinesBusAndMedia()
    {
        Assert.Equal("NVMe SSD", SystemInfoFormat.StorageTypeName(4, 17));
        Assert.Equal("SATA HDD", SystemInfoFormat.StorageTypeName(3, 11));
        Assert.Equal("SSD", SystemInfoFormat.StorageTypeName(4, null));
        Assert.Null(SystemInfoFormat.StorageTypeName(null, null));
    }

    [Fact]
    public void StorageInfo_ComputesUsedBytesWithoutInventingMissingData()
    {
        var disk = new StorageInfo("Disk", "NVMe SSD", 1000, 400, null);
        Assert.Equal(600, disk.UsedBytes);

        var unavailable = new StorageInfo("Disk", "NVMe SSD", 1000, null, SystemInfoNotes.StorageVolumesUnavailable);
        Assert.Null(unavailable.UsedBytes);
    }

    [Fact]
    public async Task Service_CachesUntilForcedAndAvoidsConcurrentQueries()
    {
        var collector = new CountingCollector();
        var service = new SystemInfoService(collector, TimeSpan.FromMinutes(1));

        var first = await service.GetAsync();
        var second = await service.GetAsync();
        Assert.Same(first, second);
        Assert.Equal(1, collector.Calls);

        var refreshed = await service.GetAsync(force: true);
        Assert.NotSame(first, refreshed);
        Assert.Equal(2, collector.Calls);
    }

    [Fact]
    public void Snapshot_FailureContainsAReason()
    {
        var failed = SystemInfoSnapshot.Failed(123, SystemInfoNotes.CollectionFailed + ": boom");
        Assert.Equal(123, failed.CapturedAtMs);
        Assert.Contains(SystemInfoNotes.CollectionFailed, failed.Error);
        Assert.Empty(failed.Windows);
        Assert.Empty(failed.Gpus);
    }
}
