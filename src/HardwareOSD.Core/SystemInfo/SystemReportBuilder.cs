using System.Globalization;
using System.Text;
using HardwareOSD.Core.Hardware;

namespace HardwareOSD.Core.SystemInfo;

/// <summary>One label/value pair contributed by the live diagnostics list.</summary>
public sealed record SystemReportRow(string Label, string Value);

/// <summary>
/// Plain-text System report for the clipboard. Deliberately English and stable: it is a technical
/// dump, not a localised screen. Missing values stay "N/A"; nothing is derived from the model name.
/// </summary>
public static class SystemReportBuilder
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static string Build(
        string appVersion,
        SystemInfoSnapshot info,
        PlatformSecuritySnapshot security,
        PlatformFeatureSnapshot features,
        IReadOnlyList<SoftwareComponentInfo> software,
        SensorSnapshot sensors,
        IReadOnlyList<SystemReportRow> diagnostics,
        DateTimeOffset capturedAt)
    {
        var text = new StringBuilder(4096);
        text.AppendLine("HardwareOSD System Report");
        text.AppendLine("HardwareOSD " + appVersion);
        text.AppendLine("Captured " + capturedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", Culture));
        text.AppendLine();

        Section(text, "Operating system");
        Row(text, "Name", info.Windows.Value("os.name"));
        Row(text, "Edition", info.Windows.Value("os.edition"));
        Row(text, "Version", info.Windows.Value("os.version"));
        Row(text, "Build", info.Windows.Value("os.build"));
        Row(text, "Architecture", info.Windows.Value("os.arch"));
        Row(text, "Inventory state", info.Error is null ? "collected" : "failed: " + info.Error);
        text.AppendLine();

        Section(text, "Processor");
        Row(text, "Model", SystemInfoFormat.CleanProcessorName(info.Cpu.Value("cpu.name")));
        Row(text, "Manufacturer", info.Cpu.Value("cpu.vendor"));
        Row(text, "Generation", info.Cpu.Value("cpu.generation"));
        Row(text, "Cores", info.Cpu.Value("cpu.cores"));
        Row(text, "Threads", info.Cpu.Value("cpu.threads"));
        Row(text, "Rated clock", info.Cpu.Value("cpu.max_clock"));
        Row(text, "Socket", info.Cpu.Value("cpu.socket"));
        Row(text, "Load", Percent(sensors, SensorIds.CpuUsage));
        Row(text, "Power", Watts(sensors, SensorIds.CpuPower));
        Row(text, "Temperature", Celsius(sensors, SensorIds.CpuTemperature));
        Row(text, "Clock", Megahertz(sensors, SensorIds.CpuClock));
        text.AppendLine();

        Section(text, "Graphics card");
        var gpu = info.Gpus.PrimaryGpu();
        Row(text, "Model", gpu?.Name);
        Row(text, "Manufacturer", gpu?.Vendor);
        Row(text, "Driver", gpu?.DriverVersion);
        Row(text, "VRAM", gpu?.Vram);
        Row(text, "Load", Percent(sensors, SensorIds.GpuUsage));
        Row(text, "Power", Watts(sensors, SensorIds.GpuPower));
        Row(text, "Temperature", Celsius(sensors, SensorIds.GpuTemperature));
        Row(text, "Clock", Megahertz(sensors, SensorIds.GpuClock));
        Row(text, "VRAM used", Gigabytes(sensors, SensorIds.VramUsed, 1));
        Row(text, "VRAM total", Gigabytes(sensors, SensorIds.VramTotal, 1));
        text.AppendLine();

        Section(text, "Mainboard");
        Row(text, "Manufacturer", info.Motherboard.Value("board.vendor"));
        Row(text, "Model", info.Motherboard.Value("board.model"));
        Row(text, "Revision", info.Motherboard.Value("board.version"));
        Row(text, "BIOS vendor", info.Motherboard.Value("bios.vendor"));
        Row(text, "BIOS version", info.Motherboard.Value("bios.version"));
        Row(text, "BIOS date", info.Motherboard.Value("bios.date"));
        Row(text, "Resizable BAR", Feature(features.ResizableBar));
        Row(text, "Hardware-accelerated GPU scheduling", Feature(features.HardwareGpuScheduling));
        Row(text, "Windows Game Mode", Feature(features.GameMode));
        text.AppendLine();

        Section(text, "Memory");
        Row(text, "Total", info.Ram.Value("ram.total"));
        Row(text, "Manufacturer", info.Ram.Value("ram.manufacturer"));
        Row(text, "Type", info.Ram.Value("ram.type"));
        Row(text, "Speed", info.Ram.Value("ram.speed"));
        Row(text, "Module configuration", info.Ram.Value("ram.module_config"));
        Row(text, "Modules", info.Ram.Value("ram.modules"));
        Row(text, "Usage", Percent(sensors, SensorIds.RamPercent));
        Row(text, "In use", Gigabytes(sensors, SensorIds.RamUsed, 1));
        Row(text, "Available", Gigabytes(sensors, SensorIds.RamAvailable, 1));
        text.AppendLine();

        Section(text, "Storage");
        if (info.Storage.Count == 0)
        {
            text.AppendLine("  N/A (no storage device reported by Windows)");
        }
        else
        {
            for (var index = 0; index < info.Storage.Count; index++)
            {
                var disk = info.Storage[index];
                text.AppendLine($"  Disk {index + 1}");
                Row(text, "Model", disk.Name, "    ");
                Row(text, "Type", disk.Type, "    ");
                Row(text, "Total", SystemInfoFormat.FormatBytes(disk.TotalBytes), "    ");
                Row(text, "Used", SystemInfoFormat.FormatBytes(disk.UsedBytes), "    ");
                Row(text, "Free", SystemInfoFormat.FormatBytes(disk.FreeBytes), "    ");
            }
        }

        text.AppendLine();

        Section(text, "Software components");
        foreach (var component in software)
        {
            var state = component.State switch
            {
                SoftwareComponentState.Available => "Available",
                SoftwareComponentState.Running => "Running",
                SoftwareComponentState.Installed => "Installed",
                SoftwareComponentState.Bundled => "Bundled",
                SoftwareComponentState.AccessDenied => "Access denied",
                SoftwareComponentState.Missing => "Missing",
                _ => "Unknown",
            };
            var version = string.IsNullOrWhiteSpace(component.Version) ? string.Empty : " " + component.Version;
            text.AppendLine($"  {component.Id}: {state}{version}");
        }

        text.AppendLine();

        Section(text, "Platform security");
        Row(text, "Secure Boot", Bool(security.SecureBoot));
        Row(text, "VBS", Vbs(security.VbsStatus));
        Row(text, "Memory integrity", Bool(security.MemoryIntegrity));
        Row(text, "Test signing", Bool(security.TestSigning));
        text.AppendLine();

        Section(text, "Diagnostics");
        foreach (var row in diagnostics)
        {
            Row(text, row.Label, row.Value);
        }

        return text.ToString();
    }

    private static void Section(StringBuilder text, string title)
    {
        text.AppendLine(title);
    }

    private static void Row(StringBuilder text, string label, string? value, string indent = "  ")
        => text.AppendLine($"{indent}{label}: {(string.IsNullOrWhiteSpace(value) ? "N/A" : value.Trim())}");

    private static string Bool(bool? value)
        => value switch
        {
            true => "On",
            false => "Off",
            _ => "Unknown",
        };

    private static string Vbs(int? status)
        => status switch
        {
            2 => "Running",
            1 => "Enabled",
            0 => "Off",
            _ => "Unknown",
        };

    private static string Feature(FeatureStatus status)
        => status switch
        {
            FeatureStatus.On => "On",
            FeatureStatus.Off => "Off",
            _ => "Unknown",
        };

    private static string Percent(SensorSnapshot snapshot, string id)
        => TryValue(snapshot, id, out var value) ? value.ToString("0", Culture) + " %" : "N/A";

    private static string Watts(SensorSnapshot snapshot, string id)
        => TryValue(snapshot, id, out var value) ? value.ToString("0.#", Culture) + " W" : "N/A";

    private static string Celsius(SensorSnapshot snapshot, string id)
        => TryValue(snapshot, id, out var value) ? value.ToString("0", Culture) + " °C" : "N/A";

    private static string Megahertz(SensorSnapshot snapshot, string id)
        => TryValue(snapshot, id, out var value) ? value.ToString("0", Culture) + " MHz" : "N/A";

    private static string Gigabytes(SensorSnapshot snapshot, string id, int decimals)
    {
        if (!TryValue(snapshot, id, out var megabytes))
        {
            return "N/A";
        }

        var format = decimals <= 0 ? "0" : "0." + new string('0', decimals);
        return (megabytes / 1024.0).ToString(format, Culture) + " GB";
    }

    private static bool TryValue(SensorSnapshot snapshot, string id, out double value)
    {
        if (snapshot.TryGet(id, out var sensor) && sensor.Available && double.IsFinite(sensor.Value))
        {
            value = sensor.Value;
            return true;
        }

        value = double.NaN;
        return false;
    }
}
