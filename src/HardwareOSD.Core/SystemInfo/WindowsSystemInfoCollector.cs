using System.Globalization;
using System.Management;
using Microsoft.Win32;

namespace HardwareOSD.Core.SystemInfo;

/// <summary>Collects slowly changing machine information. Implementations may use WMI/registry.</summary>
public interface ISystemInfoCollector
{
    SystemInfoSnapshot Collect(long capturedAtMs);
}

/// <summary>
/// Windows system-inventory collector. Every query is isolated: one unavailable WMI class results
/// in N/A rows with a reason, never in fabricated hardware specifications. The collector is called
/// on a background thread and its result is cached by <see cref="SystemInfoService"/>.
/// </summary>
public sealed class WindowsSystemInfoCollector : ISystemInfoCollector
{
    private const string CimV2 = @"root\cimv2";
    private const string StorageNamespace = @"root\Microsoft\Windows\Storage";
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public SystemInfoSnapshot Collect(long capturedAtMs)
    {
        try
        {
            return new SystemInfoSnapshot(
                capturedAtMs,
                ReadWindows(),
                ReadCpu(),
                ReadRam(),
                ReadMotherboard(),
                ReadGpus(),
                ReadStorage(),
                null);
        }
        catch (Exception ex)
        {
            return SystemInfoSnapshot.Failed(capturedAtMs, ex.Message);
        }
    }

    private static IReadOnlyList<SystemInfoItem> ReadWindows()
    {
        var rows = new List<SystemInfoItem>(5);
        var items = Query(CimV2,
            "SELECT Caption, Version, BuildNumber, OSArchitecture FROM Win32_OperatingSystem",
            o => new WindowsInfo(
                Str(o, "Caption"),
                Str(o, "Version"),
                Str(o, "BuildNumber"),
                Str(o, "OSArchitecture")), out var error);

        var info = items.FirstOrDefault();
        if (info is null || error is not null)
        {
            return MissingGroup(new[]
            {
                "os.name", "os.edition", "os.version", "os.build", "os.arch",
            }, error);
        }

        rows.Add(Info("os.name", info.Caption));
        rows.Add(Info("os.edition", SystemInfoFormat.ParseEdition(info.Caption),
            SystemInfoNotes.NotReported));
        rows.Add(Info("os.version", info.Version));
        rows.Add(Info("os.build", info.Build));
        rows.Add(Info("os.arch", info.Architecture));
        return rows;
    }

    private static IReadOnlyList<SystemInfoItem> ReadCpu()
    {
        var keys = new[]
        {
            "cpu.name", "cpu.vendor", "cpu.generation", "cpu.cores", "cpu.threads", "cpu.max_clock", "cpu.socket",
        };
        var items = Query(CimV2,
            "SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, SocketDesignation FROM Win32_Processor",
            o => new CpuInfo(
                Str(o, "Name"),
                Str(o, "Manufacturer"),
                Int(o, "NumberOfCores"),
                Int(o, "NumberOfLogicalProcessors"),
                Long(o, "MaxClockSpeed"),
                Str(o, "SocketDesignation")), out var error);

        var info = items.FirstOrDefault();
        if (info is null || error is not null)
        {
            return MissingGroup(keys, error);
        }

        var maxClock = SystemInfoFormat.FormatMegahertz(info.MaxClockSpeed);
        return new[]
        {
            Info("cpu.name", info.Name),
            Info("cpu.vendor", SystemInfoFormat.CpuVendorName(info.Manufacturer), SystemInfoNotes.NotReported),
            Info("cpu.generation", SystemInfoFormat.CpuGeneration(info.Name), SystemInfoNotes.NotReported),
            Info("cpu.cores", info.Cores?.ToString(CultureInfo.InvariantCulture), SystemInfoNotes.NotReported),
            Info("cpu.threads", info.Threads?.ToString(CultureInfo.InvariantCulture), SystemInfoNotes.NotReported),
            Info("cpu.max_clock", maxClock, SystemInfoNotes.RatedClock),
            Info("cpu.socket", info.Socket),
        };
    }

    private static IReadOnlyList<SystemInfoItem> ReadRam()
    {
        var keys = new[]
        {
            "ram.total", "ram.manufacturer", "ram.type", "ram.speed", "ram.modules", "ram.module_config",
        };
        var modules = Query(CimV2,
            "SELECT Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, Manufacturer, PartNumber, DeviceLocator FROM Win32_PhysicalMemory",
            o => new RamModule(
                ULong(o, "Capacity"),
                Long(o, "ConfiguredClockSpeed") ?? Long(o, "Speed"),
                Int(o, "SMBIOSMemoryType"),
                Str(o, "Manufacturer"),
                Str(o, "PartNumber"),
                Str(o, "DeviceLocator")), out var error);

        long? total = null;
        foreach (var module in modules)
        {
            if (module.Capacity is { } capacity && capacity <= long.MaxValue)
            {
                total = (total ?? 0) + (long)capacity;
            }
        }

        if (total is null or <= 0)
        {
            var systems = Query(CimV2, "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem",
                o => ULong(o, "TotalPhysicalMemory"), out _);
            total = (long?)systems.FirstOrDefault();
        }

        if (error is not null && modules.Count == 0)
        {
            return MissingGroup(keys, error);
        }

        var types = modules
            .Select(module => SystemInfoFormat.MemoryTypeName(module.SmbiosType))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var speeds = modules
            .Select(module => module.Speed)
            .Where(speed => speed is > 0)
            .Distinct()
            .OrderBy(speed => speed)
            .Select(speed => SystemInfoFormat.FormatMemorySpeed(speed))
            .Where(text => text is not null)
            .ToArray();
        var manufacturers = modules
            .Select(module => module.Manufacturer?.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new[]
        {
            Info("ram.total", SystemInfoFormat.FormatBytes(total), SystemInfoNotes.NotReported),
            Info("ram.manufacturer", manufacturers.Length == 0 ? null : string.Join(" / ", manufacturers)),
            Info("ram.type", types.Length == 0 ? null : string.Join("/", types), SystemInfoNotes.NotReported),
            Info("ram.speed", speeds.Length == 0 ? null : string.Join(" / ", speeds), SystemInfoNotes.NotReported),
            Info("ram.modules", modules.Count == 0 ? null : modules.Count.ToString(CultureInfo.InvariantCulture),
                SystemInfoNotes.NotReported),
            Info("ram.module_config", SystemInfoFormat.FormatModuleConfiguration(
                modules.Select(module => module.Capacity)), SystemInfoNotes.NotReported),
        };
    }

    private static IReadOnlyList<SystemInfoItem> ReadMotherboard()
    {
        var keys = new[]
        {
            "board.vendor", "board.model", "board.version", "bios.vendor", "bios.version", "bios.date",
        };
        var boards = Query(CimV2, "SELECT Manufacturer, Product, Version FROM Win32_BaseBoard",
            o => new BoardInfo(Str(o, "Manufacturer"), Str(o, "Product"), Str(o, "Version")),
            out var boardError);
        var bioses = Query(CimV2, "SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS",
            o => new BiosInfo(Str(o, "Manufacturer"), Str(o, "SMBIOSBIOSVersion"), Date(o, "ReleaseDate")),
            out var biosError);

        var board = boards.FirstOrDefault();
        var bios = bioses.FirstOrDefault();
        if (board is null && bios is null)
        {
            return MissingGroup(keys, boardError ?? biosError);
        }

        return new[]
        {
            Info("board.vendor", board?.Manufacturer),
            Info("board.model", board?.Product),
            Info("board.version", board?.Version),
            Info("bios.vendor", bios?.Manufacturer),
            Info("bios.version", bios?.Version),
            Info("bios.date", SystemInfoFormat.FormatDate(bios?.ReleaseDate), SystemInfoNotes.NotReported),
        };
    }

    private static IReadOnlyList<GpuInfo> ReadGpus()
    {
        var registryVram = ReadRegistryVram();
        var gpus = Query(CimV2,
            "SELECT Name, AdapterCompatibility, DriverVersion, AdapterRAM, PNPDeviceID FROM Win32_VideoController",
            o =>
            {
                var name = Str(o, "Name") ?? "GPU";
                var vendor = Str(o, "AdapterCompatibility");
                var vram = FindRegistryVram(registryVram, name);
                var vramText = SystemInfoFormat.FormatBytes(vram);
                var note = vramText is not null
                    ? null
                    : IsIntegratedGpu(name, vendor)
                        ? SystemInfoNotes.SharedMemory
                        : SystemInfoNotes.VramNotReported;
                return new GpuInfo(name, vendor, Str(o, "DriverVersion"), vramText, note);
            }, out _);

        return gpus;
    }

    private static IReadOnlyList<StorageInfo> ReadStorage()
    {
        var physical = Query(StorageNamespace,
            "SELECT DeviceId, FriendlyName, MediaType, BusType, Size FROM MSFT_PhysicalDisk",
            o => new PhysicalDiskInfo(
                Str(o, "DeviceId"),
                Str(o, "FriendlyName"),
                Int(o, "MediaType"),
                Int(o, "BusType"),
                ULong(o, "Size")), out _);
        var byId = physical
            .Where(disk => !string.IsNullOrWhiteSpace(disk.DeviceId))
            .GroupBy(disk => disk.DeviceId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var disks = Query(CimV2,
            "SELECT Model, Size, Index, InterfaceType, MediaType, DeviceID FROM Win32_DiskDrive",
            o =>
            {
                var index = Int(o, "Index");
                byId.TryGetValue((index ?? -1).ToString(CultureInfo.InvariantCulture), out var physicalDisk);
                var name = Str(o, "Model") ?? physicalDisk?.FriendlyName ?? "Storage device";
                var type = SystemInfoFormat.StorageTypeName(physicalDisk?.MediaType, physicalDisk?.BusType);
                var total = ULong(o, "Size") ?? physicalDisk?.Size;
                var volumes = ReadVolumes(o);
                var free = volumes.FreeBytes;
                var volumeTotal = volumes.TotalBytes;
                var note = volumes.Error is not null
                    ? SystemInfoNotes.StorageVolumesUnavailable
                    : type is null
                        ? SystemInfoNotes.StorageTypeUnavailable
                        : null;
                return new StorageInfo(
                    name,
                    type,
                    ToLong(total) ?? volumeTotal,
                    free,
                    note);
            }, out var error);

        if (error is not null && disks.Count == 0)
        {
            return Array.Empty<StorageInfo>();
        }

        return disks;
    }

    private static VolumeTotals ReadVolumes(ManagementObject disk)
    {
        long? total = null;
        long? free = null;
        string? error = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var partitions = disk.GetRelated("Win32_DiskPartition");
            foreach (ManagementObject partition in partitions)
            {
                using var volumes = partition.GetRelated("Win32_LogicalDisk");
                foreach (ManagementObject volume in volumes)
                {
                    var id = Str(volume, "DeviceID") ?? Guid.NewGuid().ToString("N");
                    if (!seen.Add(id))
                    {
                        continue;
                    }

                    var size = ULong(volume, "Size");
                    if (size is > 0)
                    {
                        total = (total ?? 0) + (long)size.Value;
                    }

                    var freeSpace = ULong(volume, "FreeSpace");
                    if (freeSpace is not null)
                    {
                        free = (free ?? 0) + (long)freeSpace.Value;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        return new VolumeTotals(total, free, error);
    }

    private static IReadOnlyDictionary<string, long> ReadRegistryVram()
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var displayClass = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (displayClass is null)
            {
                return result;
            }

            foreach (var subKeyName in displayClass.GetSubKeyNames())
            {
                try
                {
                    using var subKey = displayClass.OpenSubKey(subKeyName);
                    if (subKey is null)
                    {
                        continue;
                    }

                    var name = subKey.GetValue("DriverDesc") as string;
                    var bytes = ToInt64(subKey.GetValue("HardwareInformation.qwMemorySize"));
                    if (!string.IsNullOrWhiteSpace(name) && bytes is > 0)
                    {
                        result[name.Trim()] = bytes.Value;
                    }
                }
                catch
                {
                    // A single driver key may be unreadable; keep the rest of the inventory.
                }
            }
        }
        catch
        {
            // Registry access is best-effort; a missing value is reported as N/A.
        }

        return result;
    }

    private static long? FindRegistryVram(IReadOnlyDictionary<string, long> values, string gpuName)
    {
        if (values.Count == 0)
        {
            return null;
        }

        if (values.TryGetValue(gpuName.Trim(), out var exact))
        {
            return exact;
        }

        var normalized = NormalizeName(gpuName);
        var match = values
            .Where(pair => NormalizeName(pair.Key).Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                           normalized.Contains(NormalizeName(pair.Key), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(pair => pair.Value)
            .Select(pair => (long?)pair.Value)
            .FirstOrDefault();
        return match;
    }

    private static bool IsIntegratedGpu(string name, string? vendor)
        => name.Contains("UHD Graphics", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("Iris", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("Integrated", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase) ||
           (vendor?.Contains("Intel", StringComparison.OrdinalIgnoreCase) == true &&
            !name.Contains("Arc", StringComparison.OrdinalIgnoreCase));

    private static string NormalizeName(string value)
        => string.Join(" ", value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static IReadOnlyList<SystemInfoItem> MissingGroup(IEnumerable<string> keys, Exception? error)
    {
        var note = error is null ? SystemInfoNotes.NotReported : NoteFor(error);
        return keys.Select(key => SystemInfoItem.Missing(key, note)).ToArray();
    }

    private static string NoteFor(Exception error)
        => error is ManagementException management && management.ErrorCode == ManagementStatus.AccessDenied
            ? SystemInfoNotes.WmiAccessDenied
            : SystemInfoNotes.WmiUnavailable;

    private static SystemInfoItem Info(string key, string? value, string? missingNote = SystemInfoNotes.NotReported)
        => string.IsNullOrWhiteSpace(value) ? SystemInfoItem.Missing(key, missingNote) : new SystemInfoItem(key, value.Trim());

    private static List<T> Query<T>(
        string scope,
        string query,
        Func<ManagementObject, T> map,
        out Exception? error)
    {
        var result = new List<T>();
        error = null;
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, query);
            using var values = searcher.Get();
            foreach (ManagementObject value in values)
            {
                result.Add(map(value));
            }
        }
        catch (Exception ex)
        {
            error = ex;
        }

        return result;
    }

    private static string? Str(ManagementBaseObject value, string property)
    {
        try
        {
            return value[property]?.ToString()?.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static int? Int(ManagementBaseObject value, string property)
    {
        try
        {
            var raw = value[property];
            return raw is null ? null : Convert.ToInt32(raw, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static long? Long(ManagementBaseObject value, string property)
    {
        try
        {
            var raw = value[property];
            return raw is null ? null : Convert.ToInt64(raw, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static ulong? ULong(ManagementBaseObject value, string property)
    {
        try
        {
            var raw = value[property];
            return raw is null ? null : Convert.ToUInt64(raw, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static DateTime? Date(ManagementBaseObject value, string property)
    {
        var text = Str(value, property);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return ManagementDateTimeConverter.ToDateTime(text);
        }
        catch
        {
            return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : null;
        }
    }

    private static long? ToLong(ulong? value)
        => value is { } number && number <= long.MaxValue ? (long)number : null;

    private static long? ToInt64(object? value)
    {
        try
        {
            return value switch
            {
                null => null,
                long l => l,
                int i => i,
                uint ui => ui,
                ulong ul => ul > long.MaxValue ? null : (long)ul,
                byte[] bytes => BitConverter.ToInt64(bytes, 0),
                _ => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            };
        }
        catch
        {
            return null;
        }
    }

    private sealed record WindowsInfo(string? Caption, string? Version, string? Build, string? Architecture);

    private sealed record CpuInfo(
        string? Name,
        string? Manufacturer,
        int? Cores,
        int? Threads,
        long? MaxClockSpeed,
        string? Socket);

    private sealed record RamModule(
        ulong? Capacity,
        long? Speed,
        int? SmbiosType,
        string? Manufacturer,
        string? PartNumber,
        string? DeviceLocator);

    private sealed record BoardInfo(string? Manufacturer, string? Product, string? Version);

    private sealed record BiosInfo(string? Manufacturer, string? Version, DateTime? ReleaseDate);

    private sealed record PhysicalDiskInfo(
        string? DeviceId,
        string? FriendlyName,
        int? MediaType,
        int? BusType,
        ulong? Size);

    private readonly record struct VolumeTotals(long? TotalBytes, long? FreeBytes, string? Error);
}
