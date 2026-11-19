using System.Diagnostics;
using System.Globalization;
using System.Text;
using HardwareOSD.Core.Config;
using HardwareOSD.Core.Diagnostics;
using HardwareOSD.Core.Fps;
using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.Hardware.Providers;
using HardwareOSD.Core.Metrics;
using HardwareOSD.Core.Overlay;
using HardwareOSD.Core.Security;
using HardwareOSD.Core.SystemInfo;

namespace HardwareOSD.Cli;

/// <summary>
/// Headless diagnostics companion. Exercises exactly the same core (providers, cache, polling,
/// history, formatting) as the GUI so sensor behaviour can be verified without a display.
/// </summary>
internal static class Program
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    private static int Main(string[] args)
    {
        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        var log = new FileLogger(Path.Combine(SettingsStore.DefaultDirectory, "logs"), "cli");
        try
        {
            return command switch
            {
                "sensors" => Sensors(args),
                "audit" => Audit(args),
                "watch" => Watch(args),
                "fps" => Fps(args),
                "etw-status" => EtwStatus(),
                "grant-etw" => GrantEtw(args),
                "diagnostics" => Diagnostics(args),
                "system" => SystemInventory(),
                "stress" => Stress(args),
                "bench" => Bench(args),
                "help" or "--help" or "-h" => Help(),
                _ => Help(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FATAL: " + ex);
            log.Error("Cli", "Unhandled failure", ex);
            return 2;
        }
        finally
        {
            log.Dispose();
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            HardwareOSD diagnostics

              sensors [--demo]          Read every sensor once and print a table.
              audit [path]              Audit every metric/sensor and write a CSV diagnostic log.
              watch [seconds] [ms]      Poll continuously (default 10 s, 1000 ms).
              fps [seconds]             Exercise the ETW FPS pipeline and print its state.
              etw-status                Show administrator/Performance Log Users ETW access.
              grant-etw [user]          Elevated helper: add user to Performance Log Users.
              diagnostics [path]        Write a full diagnostic report (Markdown).
              system                    Read the cached Windows/CPU/GPU/RAM/board/storage inventory.
              stress [seconds]          Concurrency test of the cache/history pipeline.
              bench [seconds]           Measure polling cost and memory usage.

            No arguments are required by the GUI; this tool exists for QA.
            """);
        return 0;
    }

    private static AppSettings LoadSettings()
    {
        var store = new SettingsStore(null, NullLog.Instance);
        var settings = store.Load(out _);
        return settings;
    }

    private static int Sensors(string[] args)
    {
        var demo = args.Contains("--demo");
        var settings = LoadSettings();
        var providers = SensorProviderFactory.Create(settings, demo);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var all = new List<SensorValue>();

        foreach (var provider in providers)
        {
            try
            {
                provider.Initialize();
                Console.WriteLine($"== {provider.Name} ({provider.Category}) ==");
                foreach (var line in provider.Describe())
                {
                    Console.WriteLine("   " + line);
                }

                var values = provider.Read(now);
                if (values.Count == 0)
                {
                    Console.WriteLine("   (no readings)");
                }

                foreach (var value in values.OrderBy(v => v.Id, StringComparer.Ordinal))
                {
                    Console.WriteLine($"   {value.Id,-28} {FormatValue(value),14}  {value.Origin,-14} {value.Source}");
                }

                all.AddRange(values);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   FAILED: {ex.Message}");
            }
            finally
            {
                provider.Dispose();
            }

            Console.WriteLine();
        }

        Console.WriteLine("== Overlay formatting (default visible metrics) ==");
        var dictionary = new Dictionary<string, SensorValue>(StringComparer.Ordinal);
        foreach (var value in all)
        {
            dictionary[value.Id] = value;
        }

        SensorPostProcessor.Sanitize(dictionary, now);
        SensorPostProcessor.Derive(dictionary, now);
        var snapshot = SensorSnapshot.Create(1, now, dictionary);
        var layout = OverlayLayoutBuilder.Build(AppSettings.CreateDefault(), snapshot);
        foreach (var row in layout.Rows)
        {
            var text = new StringBuilder(row.Label).Append(' ');
            foreach (var cell in row.Cells)
            {
                text.Append(cell.Value);
                if (cell.Unit.Length > 0)
                {
                    text.Append(' ').Append(cell.Unit);
                }

                text.Append("   ");
            }

            Console.WriteLine("   " + text.ToString().TrimEnd());
        }

        if (layout.Rows.Count == 0)
        {
            Console.WriteLine("   (no metrics available)");
        }

        return 0;
    }

    private static int Watch(string[] args)
    {
        var seconds = args.Length > 1 && int.TryParse(args[1], out var s) ? Math.Clamp(s, 1, 3600) : 10;
        var interval = args.Length > 2 && int.TryParse(args[2], out var ms) ? Math.Clamp(ms, 100, 10_000) : 1000;
        var settings = LoadSettings();
        var cache = new SensorCache();
        var history = new SensorHistory(120);
        history.Track(SensorProviderFactory.HistorySensorIds());
        using var service = new SensorPollingService(
            SensorProviderFactory.Create(settings), cache, history, NullLog.Instance, interval);

        service.Start();
        var end = DateTime.UtcNow.AddSeconds(seconds);

        Console.WriteLine($"{"time",-10} {"seq",5} {"cpu°",6} {"cpu%",6} {"clk",8} {"gpu°",6} {"gpu%",6} {"gpuW",6} {"vram",18} {"ram%",6} poll");
        while (DateTime.UtcNow < end)
        {
            Thread.Sleep(interval);
            var snapshot = cache.Latest;
            var stats = service.Statistics;
            Console.WriteLine(
                $"{DateTime.Now:HH:mm:ss}  {snapshot.Sequence,5} " +
                $"{Fmt(snapshot, SensorIds.CpuTemperature),6} " +
                $"{Fmt(snapshot, SensorIds.CpuUsage, "0"),6} " +
                $"{Fmt(snapshot, SensorIds.CpuClock, "0"),8} " +
                $"{Fmt(snapshot, SensorIds.GpuTemperature),6} " +
                $"{Fmt(snapshot, SensorIds.GpuUsage, "0"),6} " +
                $"{Fmt(snapshot, SensorIds.GpuPower, "0.0"),6} " +
                $"{Vram(snapshot),18} " +
                $"{Fmt(snapshot, SensorIds.RamPercent, "0.0"),6} " +
                $"{stats.LastPollDurationMs:0.0} ms");
        }

        service.Stop();
        Console.WriteLine($"Polls: {service.Statistics.PollCount}, failures: {service.Statistics.FailedPollCount}, avg poll: {service.Statistics.AveragePollDurationMs:0.00} ms");
        return 0;
    }

    private static string Fmt(SensorSnapshot snapshot, string id, string format = "0.0")
    {
        var value = snapshot.ValueOrNaN(id);
        return double.IsNaN(value) ? "N/A" : value.ToString(format, Culture);
    }

    private static string Vram(SensorSnapshot snapshot)
    {
        var used = snapshot.ValueOrNaN(SensorIds.VramUsed);
        var total = snapshot.ValueOrNaN(SensorIds.VramTotal);
        if (double.IsNaN(used) || double.IsNaN(total))
        {
            return "N/A";
        }

        return $"{used:0}/{total:0} MB";
    }

    private static int Fps(string[] args)
    {
        var seconds = args.Length > 1 && int.TryParse(args[1], out var s) ? Math.Clamp(s, 2, 120) : 10;
        var settings = LoadSettings().Fps;
        var log = new FileLogger(Path.Combine(SettingsStore.DefaultDirectory, "logs"), "cli-fps");
        var cache = new FpsCache();
        using var source = new EtwFrameEventSource(log);
        using var service = new FpsMonitorService(
            cache,
            source,
            new Win32ForegroundAppProbe(),
            log,
            settings,
            targetResolver: new AllApplicationsTargetResolver(
                new Win32ProcessRelationshipProbe(),
                Environment.ProcessId));

        Console.WriteLine($"FPS source  : {source.Description}");
        Console.WriteLine($"Elevated    : {LibreHardwareMonitorProvider.IsElevated}");
        Console.WriteLine($"Interval    : {settings.UpdateIntervalMs} ms, mode: {settings.Mode}, capture: {settings.CaptureMode}");
        Console.WriteLine();

        service.Start();
        var end = DateTime.UtcNow.AddSeconds(seconds);
        var lastSequence = -1L;
        while (DateTime.UtcNow < end)
        {
            Thread.Sleep(200);
            var snapshot = cache.Latest;
            if (snapshot.Sequence == lastSequence)
            {
                continue;
            }

            lastSequence = snapshot.Sequence;
            Console.WriteLine(
                $"{DateTime.Now:HH:mm:ss.fff}  {snapshot.Status,-20} " +
                $"reason={snapshot.Reason,-16} " +
                $"proc={(snapshot.ProcessName ?? "-") + (snapshot.IsProcessFallback ? " [busiest]" : ""),-24} " +
                $"fps={(snapshot.HasValue ? $"{snapshot.Fps:0.0}" : "--"),7} " +
                $"ft={(snapshot.HasValue ? $"{snapshot.FrameTimeMs:0.00} ms" : "--"),10} " +
                $"1%={(snapshot.OnePercentLowFps > 0 ? $"{snapshot.OnePercentLowFps:0.0}" : "--"),6} " +
                $"src={snapshot.Source}");
            if (!string.IsNullOrEmpty(snapshot.Message))
            {
                Console.WriteLine("             " + snapshot.Message);
            }
        }

        service.Stop();
        Console.WriteLine();
        foreach (var line in source.Describe())
        {
            Console.WriteLine("  " + line);
        }

        return 0;
    }

    private static int EtwStatus()
    {
        var state = PerformanceLogUsers.GetState();
        Console.WriteLine($"User            : {state.UserName}");
        Console.WriteLine($"Elevated        : {state.IsElevated}");
        Console.WriteLine($"Token has group : {state.TokenHasGroup}");
        Console.WriteLine($"Group has user  : {state.GroupHasUser}");
        Console.WriteLine($"Group           : {state.GroupName}");
        Console.WriteLine($"Access          : {state.Kind}");
        if (!string.IsNullOrEmpty(state.Error))
        {
            Console.WriteLine($"Group query     : {state.Error}");
        }

        return 0;
    }

    private static int GrantEtw(string[] args)
    {
        if (!PerformanceLogUsers.IsElevated())
        {
            Console.Error.WriteLine("grant-etw must run elevated (the GUI launches it through UAC).");
            return 2;
        }

        var user = args.Length > 1 && !string.IsNullOrWhiteSpace(args[1])
            ? args[1]
            : PerformanceLogUsers.CurrentUserName;
        var result = PerformanceLogUsers.GrantUser(user);
        Console.WriteLine(result.Message);
        return result.Success ? 0 : 1;
    }

    private static int Audit(string[] args)
    {
        var settings = LoadSettings();
        var providers = SensorProviderFactory.Create(settings);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var outputs = new List<(string, IReadOnlyList<SensorValue>)>(providers.Count);

        foreach (var provider in providers)
        {
            try
            {
                provider.Initialize();
                provider.Read(now - 1000); // prime rate/delta counters
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Provider {provider.Name} failed to initialise: {ex.Message}");
            }
        }

        Thread.Sleep(400);

        foreach (var provider in providers)
        {
            try
            {
                outputs.Add((provider.Key, provider.Read(now)));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Provider {provider.Name} read failed: {ex.Message}");
            }
        }

        var merged = new SensorMergePolicy().Merge(outputs);
        SensorPostProcessor.Sanitize(merged, now);
        SensorPostProcessor.Derive(merged, now);
        var snapshot = SensorSnapshot.Create(1, now, merged);
        var fpsState = new FpsSnapshot(0, now, FpsStatus.RequiresElevation, FpsSource.None, false, 0, null, 0, 0, 0,
            "FPS is not sampled by the offline audit");
        var entries = SensorAudit.Build(settings, snapshot, fpsState, now);

        Console.WriteLine("== Metric audit ==");
        Console.WriteLine(SensorAudit.FormatTable(entries));
        Console.WriteLine();
        Console.WriteLine("== Fan sensors ==");
        Console.WriteLine(SensorAudit.FormatFanTable(SensorAudit.BuildFanSensors(snapshot, now)));
        Console.WriteLine();
        Console.WriteLine("== Raw sensors (merged) ==");
        foreach (var pair in snapshot.Values.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var value = pair.Value;
            var printed = value.Available ? $"{value.Value:0.###} {value.Unit}" : "N/A";
            Console.WriteLine($"  {pair.Key,-28} {printed,-16} {value.Kind,-12} {value.Origin,-14} {value.Source}");
            if (!string.IsNullOrEmpty(value.Note))
            {
                Console.WriteLine($"      note: {value.Note}");
            }
        }

        foreach (var provider in providers)
        {
            if (provider is LibreHardwareMonitorProvider lhm)
            {
                Console.WriteLine();
                Console.WriteLine("== LibreHardwareMonitor inventory (all sensors) ==");
                foreach (var line in lhm.DescribeAllSensors())
                {
                    Console.WriteLine("  " + line);
                }
            }
        }

        var path = args.Length > 1
            ? args[1]
            : Path.Combine(SettingsStore.DefaultDirectory, "logs",
                $"sensor-audit-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        SensorAudit.WriteCsv(path, entries, snapshot);
        Console.WriteLine();
        Console.WriteLine("Sensor audit CSV: " + path);

        foreach (var provider in providers)
        {
            provider.Dispose();
        }

        return 0;
    }

    private static int SystemInventory()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var snapshot = new WindowsSystemInfoCollector().Collect(now);
        Console.WriteLine("== System inventory ==");
        Console.WriteLine($"Captured: {DateTimeOffset.FromUnixTimeMilliseconds(snapshot.CapturedAtMs):O}");
        if (!string.IsNullOrWhiteSpace(snapshot.Error))
        {
            Console.WriteLine("Collection error: " + snapshot.Error);
        }

        PrintGroup("Windows", snapshot.Windows);
        PrintGroup("CPU", snapshot.Cpu);
        PrintGroup("RAM", snapshot.Ram);
        PrintGroup("Motherboard", snapshot.Motherboard);

        Console.WriteLine();
        Console.WriteLine("GPUs");
        if (snapshot.Gpus.Count == 0)
        {
            Console.WriteLine("  N/A");
        }
        else
        {
            foreach (var gpu in snapshot.Gpus)
            {
                Console.WriteLine($"  {gpu.Name}");
                Console.WriteLine($"    vendor: {gpu.Vendor ?? "N/A"}");
                Console.WriteLine($"    driver: {gpu.DriverVersion ?? "N/A"}");
                Console.WriteLine($"    VRAM  : {gpu.Vram ?? "N/A"}" +
                                  (string.IsNullOrWhiteSpace(gpu.VramNote) ? string.Empty : $" ({gpu.VramNote})"));
            }
        }

        Console.WriteLine();
        Console.WriteLine("Storage");
        if (snapshot.Storage.Count == 0)
        {
            Console.WriteLine("  N/A");
        }
        else
        {
            foreach (var disk in snapshot.Storage)
            {
                Console.WriteLine($"  {disk.Name}");
                Console.WriteLine($"    type  : {disk.Type ?? "N/A"}");
                Console.WriteLine($"    total : {SystemInfoFormat.FormatBytes(disk.TotalBytes) ?? "N/A"}");
                Console.WriteLine($"    free  : {SystemInfoFormat.FormatBytes(disk.FreeBytes) ?? "N/A"}");
                Console.WriteLine($"    used  : {SystemInfoFormat.FormatBytes(disk.UsedBytes) ?? "N/A"}" +
                                  (string.IsNullOrWhiteSpace(disk.Note) ? string.Empty : $" ({disk.Note})"));
            }
        }

        return 0;
    }

    private static void PrintGroup(string title, IReadOnlyList<SystemInfoItem> items)
    {
        Console.WriteLine();
        Console.WriteLine(title);
        if (items.Count == 0)
        {
            Console.WriteLine("  N/A");
            return;
        }

        foreach (var item in items)
        {
            Console.WriteLine($"  {item.Key,-18} {item.Value ?? "N/A"}" +
                              (string.IsNullOrWhiteSpace(item.Note) ? string.Empty : $" ({item.Note})"));
        }
    }

    private static int Diagnostics(string[] args)
    {
        var path = args.Length > 1
            ? args[1]
            : Path.Combine(SettingsStore.DefaultDirectory, $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.md");

        var builder = new StringBuilder();
        builder.AppendLine("# HardwareOSD diagnostic report");
        builder.AppendLine();
        builder.AppendLine($"Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine();
        builder.AppendLine("## Environment");
        builder.AppendLine();
        builder.AppendLine($"- OS: {Environment.OSVersion}");
        builder.AppendLine($"- Runtime: {Environment.Version}");
        builder.AppendLine($"- 64-bit process: {Environment.Is64BitProcess}");
        builder.AppendLine($"- Logical processors: {Environment.ProcessorCount}");
        builder.AppendLine($"- Elevated: {LibreHardwareMonitorProvider.IsElevated}");
        builder.AppendLine($"- Working set: {Environment.WorkingSet / 1024 / 1024} MB");
        builder.AppendLine();

        var settings = LoadSettings();
        var providers = SensorProviderFactory.Create(settings);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var dictionary = new Dictionary<string, SensorValue>(StringComparer.Ordinal);

        builder.AppendLine("## Providers");
        builder.AppendLine();
        foreach (var provider in providers)
        {
            try
            {
                provider.Initialize();
                builder.AppendLine($"### {provider.Name} ({provider.Category})");
                builder.AppendLine();
                foreach (var line in provider.Describe())
                {
                    builder.AppendLine("- " + line);
                }

                var values = provider.Read(now);
                builder.AppendLine($"- Readings: {values.Count} ({values.Count(v => v.Available)} available)");
                builder.AppendLine();
                foreach (var value in values)
                {
                    dictionary[value.Id] = value;
                }
            }
            catch (Exception ex)
            {
                builder.AppendLine($"### {provider.Name} — FAILED");
                builder.AppendLine();
                builder.AppendLine("```");
                builder.AppendLine(ex.ToString());
                builder.AppendLine("```");
                builder.AppendLine();
            }
            finally
            {
                provider.Dispose();
            }
        }

        SensorPostProcessor.Sanitize(dictionary, now);
        SensorPostProcessor.Derive(dictionary, now);

        builder.AppendLine("## Sensor readings");
        builder.AppendLine();
        builder.AppendLine("| Sensor | Value | Unit | Kind | Origin | Source |");
        builder.AppendLine("| --- | ---: | --- | --- | --- | --- |");
        foreach (var value in dictionary.Values.OrderBy(v => v.Id, StringComparer.Ordinal))
        {
            builder.AppendLine($"| {value.Id} | {FormatValue(value)} | {value.Unit} | {value.Kind} | {value.Origin} | {value.Source} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Overlay preview (text form)");
        builder.AppendLine();
        builder.AppendLine("```");
        var snapshot = SensorSnapshot.Create(1, now, dictionary);
        foreach (var row in OverlayLayoutBuilder.Build(settings, snapshot).Rows)
        {
            builder.Append(row.Label);
            foreach (var cell in row.Cells)
            {
                builder.Append("  ").Append(cell.Value);
                if (cell.Unit.Length > 0)
                {
                    builder.Append(' ').Append(cell.Unit);
                }
            }

            builder.AppendLine();
        }

        builder.AppendLine("```");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, builder.ToString());
        Console.WriteLine("Diagnostic report written to " + path);
        return 0;
    }

    private static int Stress(string[] args)
    {
        var seconds = args.Length > 1 && int.TryParse(args[1], out var s) ? Math.Clamp(s, 2, 600) : 10;
        var settings = LoadSettings();
        var cache = new SensorCache();
        var history = new SensorHistory(120);
        history.Track(SensorProviderFactory.HistorySensorIds());
        using var service = new SensorPollingService(
            SensorProviderFactory.Create(settings, useDemoData: true), cache, history, NullLog.Instance, 100);

        var errors = 0;
        var reads = 0L;
        var tornSnapshots = 0L;
        var stop = false;

        var readers = Enumerable.Range(0, 4).Select(_ => new Thread(() =>
        {
            var lastSequence = -1L;
            var buffer = new double[128];
            while (!Volatile.Read(ref stop))
            {
                try
                {
                    var snapshot = cache.Latest;
                    if (snapshot.Sequence < lastSequence - 1)
                    {
                        Interlocked.Increment(ref tornSnapshots);
                    }

                    lastSequence = snapshot.Sequence;
                    foreach (var pair in snapshot.Values)
                    {
                        if (!double.IsFinite(pair.Value.Value) && pair.Value.Available)
                        {
                            Interlocked.Increment(ref tornSnapshots);
                        }
                    }

                    history.TryCopy(SensorIds.CpuUsage, buffer);
                    Interlocked.Increment(ref reads);
                }
                catch
                {
                    Interlocked.Increment(ref errors);
                }
            }
        })).ToList();

        service.Start();
        readers.ForEach(t => t.Start());
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        Volatile.Write(ref stop, true);
        readers.ForEach(t => t.Join());
        service.Stop();

        var stats = service.Statistics;
        Console.WriteLine($"Duration:       {seconds} s");
        Console.WriteLine($"Polls:          {stats.PollCount} (failures {stats.FailedPollCount})");
        Console.WriteLine($"Cache reads:    {reads:N0}");
        Console.WriteLine($"Torn reads:     {tornSnapshots}");
        Console.WriteLine($"Reader errors:  {errors}");
        Console.WriteLine($"Average poll:   {stats.AveragePollDurationMs:0.00} ms");
        Console.WriteLine($"Working set:    {Environment.WorkingSet / 1024 / 1024} MB");
        return errors == 0 && tornSnapshots == 0 ? 0 : 1;
    }

    private static int Bench(string[] args)
    {
        var seconds = args.Length > 1 && int.TryParse(args[1], out var s) ? Math.Clamp(s, 2, 300) : 5;
        var settings = LoadSettings();
        var providers = SensorProviderFactory.Create(settings);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (var provider in providers)
        {
            try
            {
                provider.Initialize();
            }
            catch
            {
                // measured below
            }
        }

        var durations = new List<double>(256);
        var perProvider = providers.ToDictionary(p => p.Name, _ => new List<double>(128), StringComparer.Ordinal);
        for (var i = 0; i < 5; i++)
        {
            foreach (var provider in providers)
            {
                try { provider.Read(now + i); } catch { }
            }
        }

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            foreach (var provider in providers)
            {
                var start = Stopwatch.GetTimestamp();
                try { provider.Read(now); } catch { }
                var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                durations.Add(elapsed);
                perProvider[provider.Name].Add(elapsed);
            }

            Thread.Sleep(50);
        }

        durations.Sort();
        Console.WriteLine($"Samples:   {durations.Count}");
        Console.WriteLine($"Mean:      {durations.Average():0.000} ms");
        Console.WriteLine($"Median:    {durations[durations.Count / 2]:0.000} ms");
        Console.WriteLine($"p95:       {durations[(int)(durations.Count * 0.95)]:0.000} ms");
        Console.WriteLine($"Max:       {durations[^1]:0.000} ms");
        Console.WriteLine($"Working set: {Environment.WorkingSet / 1024 / 1024} MB");
        Console.WriteLine();
        Console.WriteLine("Per provider:");
        foreach (var (name, samples) in perProvider)
        {
            if (samples.Count == 0)
            {
                continue;
            }

            var sorted = samples.OrderBy(v => v).ToList();
            Console.WriteLine($"  {name,-28} mean {sorted.Average(),7:0.000}  median {sorted[sorted.Count / 2],7:0.000}  p95 {sorted[(int)(sorted.Count * 0.95)],7:0.000}  max {sorted[^1],7:0.000} ms");
        }

        foreach (var provider in providers)
        {
            provider.Dispose();
        }

        return 0;
    }

    private static string FormatValue(SensorValue value)
    {
        if (!value.Available)
        {
            return "N/A";
        }

        return value.Kind switch
        {
            SensorKind.Temperature => value.Value.ToString("0.0", Culture),
            SensorKind.Load => value.Value.ToString("0.0", Culture),
            SensorKind.Power => value.Value.ToString("0.0", Culture),
            SensorKind.Clock => value.Value.ToString("0", Culture),
            _ => value.Value.ToString("0.0", Culture),
        };
    }
}

