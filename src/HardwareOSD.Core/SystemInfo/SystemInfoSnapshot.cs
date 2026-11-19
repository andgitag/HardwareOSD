namespace HardwareOSD.Core.SystemInfo;

/// <summary>Stable reason codes for missing system-inventory fields; the UI localizes them.</summary>
public static class SystemInfoNotes
{
    public const string WmiUnavailable = "wmi-unavailable";
    public const string WmiAccessDenied = "wmi-access-denied";
    public const string NotReported = "not-reported";
    public const string SharedMemory = "shared-memory";
    public const string VramNotReported = "vram-not-reported";
    public const string StorageVolumesUnavailable = "storage-volumes-unavailable";
    public const string StorageTypeUnavailable = "storage-type-unavailable";
    public const string RatedClock = "rated-clock";
    public const string CollectionFailed = "collection-failed";
}

/// <summary>One label/value row in a system-information group.</summary>
public sealed record SystemInfoItem(string Key, string? Value, string? Note = null)
{
    public bool Available => !string.IsNullOrWhiteSpace(Value);

    public static SystemInfoItem Missing(string key, string? note = SystemInfoNotes.NotReported)
        => new(key, null, note);
}

/// <summary>One physical or logical GPU. VRAM is only present when the driver reports it.</summary>
public sealed record GpuInfo(
    string Name,
    string? Vendor,
    string? DriverVersion,
    string? Vram,
    string? VramNote);

/// <summary>One physical storage device with aggregated free space from its logical volumes.</summary>
public sealed record StorageInfo(
    string Name,
    string? Type,
    long? TotalBytes,
    long? FreeBytes,
    string? Note)
{
    public long? UsedBytes => TotalBytes is { } total && FreeBytes is { } free
        ? Math.Max(0, total - free)
        : null;
}

/// <summary>Immutable snapshot of slowly changing machine information. Never contains sensor telemetry.</summary>
public sealed class SystemInfoSnapshot
{
    public static readonly SystemInfoSnapshot Empty = new(
        0,
        Array.Empty<SystemInfoItem>(),
        Array.Empty<SystemInfoItem>(),
        Array.Empty<SystemInfoItem>(),
        Array.Empty<SystemInfoItem>(),
        Array.Empty<GpuInfo>(),
        Array.Empty<StorageInfo>(),
        null);

    public SystemInfoSnapshot(
        long capturedAtMs,
        IReadOnlyList<SystemInfoItem> windows,
        IReadOnlyList<SystemInfoItem> cpu,
        IReadOnlyList<SystemInfoItem> ram,
        IReadOnlyList<SystemInfoItem> motherboard,
        IReadOnlyList<GpuInfo> gpus,
        IReadOnlyList<StorageInfo> storage,
        string? error)
    {
        CapturedAtMs = capturedAtMs;
        Windows = windows;
        Cpu = cpu;
        Ram = ram;
        Motherboard = motherboard;
        Gpus = gpus;
        Storage = storage;
        Error = error;
    }

    public long CapturedAtMs { get; }
    public IReadOnlyList<SystemInfoItem> Windows { get; }
    public IReadOnlyList<SystemInfoItem> Cpu { get; }
    public IReadOnlyList<SystemInfoItem> Ram { get; }
    public IReadOnlyList<SystemInfoItem> Motherboard { get; }
    public IReadOnlyList<GpuInfo> Gpus { get; }
    public IReadOnlyList<StorageInfo> Storage { get; }
    public string? Error { get; }

    public static SystemInfoSnapshot Failed(long capturedAtMs, string error)
        => new(
            capturedAtMs,
            Array.Empty<SystemInfoItem>(),
            Array.Empty<SystemInfoItem>(),
            Array.Empty<SystemInfoItem>(),
            Array.Empty<SystemInfoItem>(),
            Array.Empty<GpuInfo>(),
            Array.Empty<StorageInfo>(),
            error);
}
