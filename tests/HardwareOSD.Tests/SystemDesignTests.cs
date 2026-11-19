using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.SystemInfo;

namespace HardwareOSD.Tests;

public class SystemDesignTests
{
    [Fact]
    public void BrandCatalog_ResolvesCpuVendors()
    {
        Assert.Equal(HardwareBrand.Intel,
            BrandCatalog.Resolve(HardwareComponentKind.Cpu, "GenuineIntel",
                "12th Gen Intel(R) Core(TM) i9-12900HX"));
        Assert.Equal(HardwareBrand.Amd,
            BrandCatalog.Resolve(HardwareComponentKind.Cpu, "AuthenticAMD", "AMD Ryzen 7 5800X3D"));
        Assert.Equal(HardwareBrand.Unknown,
            BrandCatalog.Resolve(HardwareComponentKind.Cpu, "Other", "Generic CPU"));
    }

    [Fact]
    public void BrandCatalog_NeverShowsNvidiaForNonNvidiaAdapters()
    {
        Assert.Equal(HardwareBrand.Nvidia,
            BrandCatalog.Resolve(HardwareComponentKind.Gpu, "NVIDIA", "NVIDIA GeForce RTX 4080 Laptop GPU"));
        Assert.Equal(HardwareBrand.Amd,
            BrandCatalog.Resolve(HardwareComponentKind.Gpu, "AMD", "AMD Radeon RX 7900 XTX"));
        Assert.Equal(HardwareBrand.Amd,
            BrandCatalog.Resolve(HardwareComponentKind.Gpu, null, "Radeon RX 6800"));
        Assert.Equal(HardwareBrand.Intel,
            BrandCatalog.Resolve(HardwareComponentKind.Gpu, "Intel Corporation", "Intel(R) UHD Graphics"));
        Assert.Equal(HardwareBrand.Unknown,
            BrandCatalog.Resolve(HardwareComponentKind.Gpu, "Unknown Vendor", "Mystery GPU"));
    }

    [Fact]
    public void BrandCatalog_ResolvesBoardMemoryAndStorageVendors()
    {
        Assert.Equal(HardwareBrand.Msi, BrandCatalog.Resolve(HardwareComponentKind.Mainboard,
            "Micro-Star International Co., Ltd.", "MS-15M1"));
        Assert.Equal(HardwareBrand.Asus, BrandCatalog.Resolve(HardwareComponentKind.Mainboard,
            "ASUSTeK COMPUTER INC.", "ROG STRIX B650E-F"));
        Assert.Equal(HardwareBrand.Gigabyte, BrandCatalog.Resolve(HardwareComponentKind.Mainboard,
            "Gigabyte Technology Co., Ltd.", "B650 AORUS"));
        Assert.Equal(HardwareBrand.Asrock, BrandCatalog.Resolve(HardwareComponentKind.Mainboard,
            "ASRock", "X670E Taichi"));

        Assert.Equal(HardwareBrand.Crucial, BrandCatalog.Resolve(HardwareComponentKind.Memory,
            "Crucial Technology", "CT32G48C40S5"));
        Assert.Equal(HardwareBrand.Kingston, BrandCatalog.Resolve(HardwareComponentKind.Memory,
            "Kingston", "KF560C36"));
        Assert.Equal(HardwareBrand.Corsair, BrandCatalog.Resolve(HardwareComponentKind.Memory,
            "Corsair", "CMK32GX5M2"));
        Assert.Equal(HardwareBrand.Samsung, BrandCatalog.Resolve(HardwareComponentKind.Memory,
            "Samsung", "M323R2GA3BB0"));
        Assert.Equal(HardwareBrand.SkHynix, BrandCatalog.Resolve(HardwareComponentKind.Memory,
            "SK Hynix", "HMCG88AEBRA"));
        Assert.Equal(HardwareBrand.GSkill, BrandCatalog.Resolve(HardwareComponentKind.Memory,
            "G.SKILL", "F5-6000J3038F16G"));
        Assert.Equal(HardwareBrand.Adata, BrandCatalog.Resolve(HardwareComponentKind.Memory,
            "ADATA", "AX5U6000"));

        Assert.Equal(HardwareBrand.WesternDigital, BrandCatalog.Resolve(HardwareComponentKind.Storage,
            null, "NVMe WD Blue SN580 2TB"));
        Assert.Equal(HardwareBrand.Samsung, BrandCatalog.Resolve(HardwareComponentKind.Storage,
            null, "Samsung SSD 990 PRO 2TB"));
        Assert.Equal(HardwareBrand.Seagate, BrandCatalog.Resolve(HardwareComponentKind.Storage,
            null, "Seagate FireCuda 530"));
        Assert.Equal(HardwareBrand.Kioxia, BrandCatalog.Resolve(HardwareComponentKind.Storage,
            null, "KIOXIA EXCERIA PRO"));
        Assert.Equal(HardwareBrand.Unknown, BrandCatalog.Resolve(HardwareComponentKind.Storage,
            null, "Generic NVMe Disk"));
    }

    [Fact]
    public void BrandCatalog_HasWordmarksForEveryNamedBrand()
    {
        foreach (var brand in Enum.GetValues<HardwareBrand>())
        {
            var wordmark = BrandCatalog.Wordmark(brand);
            if (brand == HardwareBrand.Unknown)
            {
                Assert.Null(wordmark);
                continue;
            }

            Assert.False(string.IsNullOrWhiteSpace(wordmark), "missing wordmark for " + brand);
        }
    }

    [Theory]
    [InlineData("12th Gen Intel(R) Core(TM) i9-12900HX", "12th Gen Intel Core i9-12900HX")]
    [InlineData("Intel(R) Core(TM) i7-8700K CPU @ 3.70GHz", "Intel Core i7-8700K")]
    [InlineData("AMD Ryzen 7 5800X3D 8-Core Processor", "AMD Ryzen 7 5800X3D 8-Core Processor")]
    public void CleanProcessorName_RemovesTrademarksAndClockSuffix(string raw, string expected)
        => Assert.Equal(expected, SystemInfoFormat.CleanProcessorName(raw));

    [Theory]
    [InlineData("12th Gen Intel(R) Core(TM) i9-12900HX", "12th Gen")]
    [InlineData("13th Gen Intel Core i5-13600K", "13th Gen")]
    [InlineData("AMD Ryzen 7 5800X3D", null)]
    [InlineData(null, null)]
    public void CpuGeneration_UsesOnlyTheMarketingName(string? name, string? expected)
        => Assert.Equal(expected, SystemInfoFormat.CpuGeneration(name));

    [Fact]
    public void MemoryFormatting_UsesRequirementUnits()
    {
        Assert.Equal("2 × 32 GB", SystemInfoFormat.FormatModuleConfiguration(
            new ulong?[] { 32UL * 1024 * 1024 * 1024, 32UL * 1024 * 1024 * 1024 }));
        Assert.Equal("1 × 16 GB + 1 × 32 GB", SystemInfoFormat.FormatModuleConfiguration(
            new ulong?[] { 32UL * 1024 * 1024 * 1024, 16UL * 1024 * 1024 * 1024 }));
        Assert.Null(SystemInfoFormat.FormatModuleConfiguration(Array.Empty<ulong?>()));
        Assert.Equal("4800 MT/s", SystemInfoFormat.FormatMemorySpeed(4800));
        Assert.Null(SystemInfoFormat.FormatMemorySpeed(null));
    }

    [Fact]
    public void PrimaryGpu_PrefersTheDiscreteAdapter()
    {
        var gpus = new[]
        {
            new GpuInfo("Intel(R) UHD Graphics", "Intel Corporation", "31.0", null,
                SystemInfoNotes.SharedMemory),
            new GpuInfo("NVIDIA GeForce RTX 4080 Laptop GPU", "NVIDIA", "32.0.15.9579", "12 GB", null),
        };
        Assert.Equal("NVIDIA GeForce RTX 4080 Laptop GPU", gpus.PrimaryGpu()!.Name);
        Assert.Null(Array.Empty<GpuInfo>().PrimaryGpu());
    }

    [Theory]
    [InlineData(2, FeatureStatus.On)]
    [InlineData(1, FeatureStatus.Off)]
    [InlineData(0, FeatureStatus.Unknown)]
    [InlineData(null, FeatureStatus.Unknown)]
    public void HardwareGpuScheduling_MapsHwSchMode(int? value, FeatureStatus expected)
        => Assert.Equal(expected, PlatformFeatureInfo.FromHwSchMode(value));

    [Theory]
    [InlineData(1, FeatureStatus.On)]
    [InlineData(0, FeatureStatus.Off)]
    [InlineData(null, FeatureStatus.Unknown)]
    public void GameMode_MapsAutoGameModeEnabled(int? value, FeatureStatus expected)
        => Assert.Equal(expected, PlatformFeatureInfo.FromGameMode(value));

    [Fact]
    public void ResizableBar_IsDerivedFromTheBar1Aperture()
    {
        const long vram = 12L * 1024 * 1024 * 1024;
        Assert.Equal(FeatureStatus.On, PlatformFeatureInfo.FromBar1(16L * 1024 * 1024 * 1024, vram));
        Assert.Equal(FeatureStatus.Off, PlatformFeatureInfo.FromBar1(256L * 1024 * 1024, vram));
        Assert.Equal(FeatureStatus.Unknown, PlatformFeatureInfo.FromBar1(1L * 1024 * 1024 * 1024, vram));
        Assert.Equal(FeatureStatus.Unknown, PlatformFeatureInfo.FromBar1(null, vram));
        Assert.Equal(FeatureStatus.Off, PlatformFeatureInfo.FromBar1(256L * 1024 * 1024, null));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\HardwareOSD\\FanBroker\\HardwareOSD.FanBroker.exe\"",
        "C:\\Program Files\\HardwareOSD\\FanBroker\\HardwareOSD.FanBroker.exe")]
    [InlineData("C:\\Tools\\Broker.exe --service", "C:\\Tools\\Broker.exe")]
    [InlineData("\\??\\C:\\Tools\\Broker.exe", "C:\\Tools\\Broker.exe")]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void ServiceImagePath_IsParsedWithoutArguments(string? raw, string? expected)
        => Assert.Equal(expected, SoftwareComponentProbe.ParseServiceImagePath(raw));

    [Theory]
    [InlineData("2.1.0.0", "2.1.0")]
    [InlineData("13.595.79", "13.595.79")]
    [InlineData(null, null)]
    public void TrimVersion_DropsOnlyATrailingZero(string? raw, string? expected)
        => Assert.Equal(expected, SoftwareComponentProbe.TrimVersion(raw));

    [Fact]
    public void SystemReport_ContainsRealInventoryAndDiagnostics()
    {
        var info = new SystemInfoSnapshot(
            123,
            new[]
            {
                new SystemInfoItem("os.name", "Microsoft Windows 11 Pro"),
                new SystemInfoItem("os.edition", "Pro"),
                new SystemInfoItem("os.version", "10.0.26200"),
                new SystemInfoItem("os.build", "26200"),
                new SystemInfoItem("os.arch", "64-bit"),
            },
            new[]
            {
                new SystemInfoItem("cpu.name", "12th Gen Intel(R) Core(TM) i9-12900HX"),
                new SystemInfoItem("cpu.vendor", "Intel"),
                new SystemInfoItem("cpu.generation", "12th Gen"),
                new SystemInfoItem("cpu.cores", "16"),
                new SystemInfoItem("cpu.threads", "24"),
            },
            new[]
            {
                new SystemInfoItem("ram.total", "64 GB"),
                new SystemInfoItem("ram.manufacturer", "Crucial Technology"),
                new SystemInfoItem("ram.speed", "4800 MT/s"),
                new SystemInfoItem("ram.module_config", "2 × 32 GB"),
            },
            new[]
            {
                new SystemInfoItem("board.vendor", "Micro-Star International Co., Ltd."),
                new SystemInfoItem("board.model", "MS-15M1"),
                new SystemInfoItem("bios.version", "E15M1IMS.513"),
            },
            new[]
            {
                new GpuInfo("NVIDIA GeForce RTX 4080 Laptop GPU", "NVIDIA", "32.0.15.9579", "12 GB", null),
            },
            Array.Empty<StorageInfo>(),
            null);

        var report = SystemReportBuilder.Build(
            "0.25.0",
            info,
            new PlatformSecuritySnapshot(true, 2, false, false, null),
            new PlatformFeatureSnapshot(FeatureStatus.On, FeatureStatus.Unknown, FeatureStatus.Off),
            new[]
            {
                new SoftwareComponentInfo("pawnio", "2.1.0", SoftwareComponentState.AccessDenied),
                new SoftwareComponentInfo("nvml", "13.595.79", SoftwareComponentState.Available),
            },
            SnapshotFactory.Typical(),
            new[] { new SystemReportRow("Elevation", "standard user") },
            new DateTimeOffset(2026, 11, 17, 18, 0, 0, TimeSpan.Zero));

        Assert.Contains("HardwareOSD 0.25.0", report);
        Assert.Contains("Microsoft Windows 11 Pro", report);
        Assert.Contains("12th Gen Intel Core i9-12900HX", report);
        Assert.Contains("2 × 32 GB", report);
        Assert.Contains("NVIDIA GeForce RTX 4080 Laptop GPU", report);
        Assert.Contains("Resizable BAR: On", report);
        Assert.Contains("Hardware-accelerated GPU scheduling: Unknown", report);
        Assert.Contains("pawnio: Access denied 2.1.0", report);
        Assert.Contains("Elevation: standard user", report);
        Assert.Contains("Storage", report);
        Assert.Contains("N/A (no storage device reported by Windows)", report);
    }

    [Fact]
    public void SystemInfoItems_ValueLookupKeepsMissingDistinct()
    {
        var items = new[]
        {
            new SystemInfoItem("cpu.name", "CPU"),
            SystemInfoItem.Missing("cpu.generation"),
        };
        Assert.Equal("CPU", items.Value("cpu.name"));
        Assert.Null(items.Value("cpu.generation"));
        Assert.Null(items.Value("absent"));
    }
}
