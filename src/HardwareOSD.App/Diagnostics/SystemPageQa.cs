using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HardwareOSD.App.Localization;
using HardwareOSD.App.Services;
using HardwareOSD.App.Views;
using HardwareOSD.Core.Config;
using HardwareOSD.Core.Diagnostics;
using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.Hardware.Providers;
using HardwareOSD.Core.Security;
using HardwareOSD.Core.SystemInfo;

namespace HardwareOSD.App.Diagnostics;

/// <summary>
/// QA-only offscreen renderer for the System page. Reads the real machine inventory and one round of
/// real sensor values (no demo data), renders the page at the 1080×720 and 920×600 viewport widths in
/// English and Ukrainian, and writes an overflow report. Triggered by "--render-system &lt;dir&gt;".
/// </summary>
internal static class SystemPageQa
{
    private const double WindowChromeWidth = 33;

    public static int Render(string directory)
    {
        Directory.CreateDirectory(directory);
        var report = new StringBuilder();
        report.AppendLine($"HardwareOSD System page QA — {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine();

        Loc.Instance.Initialize("en");
        var settings = AppSettings.CreateDefault();
        settings.Monitoring.PollIntervalMs = 300;

        var cache = new SensorCache();
        var history = new SensorHistory(180);
        history.Track(SensorProviderFactory.HistorySensorIds());
        using var polling = new SensorPollingService(
            SensorProviderFactory.Create(settings), cache, history, NullLog.Instance, 300)
        {
            UseThermalZoneAsCpuTemperature = true,
        };

        polling.Start();
        Thread.Sleep(10000);
        var sensors = cache.Latest;
        var statistics = polling.Statistics;
        var pawnIo = PawnIoAccessProbe.Query();
        var broker = polling.FindProvider<MsiFanBrokerProvider>()?.Status;
        var nvml = polling.FindProvider<NvmlProvider>();
        polling.Stop();

        var info = new WindowsSystemInfoCollector().Collect(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var security = PlatformSecurityInfo.Read();
        var features = SystemPageComposer.ReadFeatures(sensors, nvml);
        var software = SystemPageComposer.ReadSoftware(nvml, broker, pawnIo);

        report.AppendLine($"OS        {info.Windows.Value("os.name")} {info.Windows.Value("os.build")}");
        report.AppendLine($"CPU       {info.Cpu.Value("cpu.name")} | {info.Cpu.Value("cpu.cores")}C/{info.Cpu.Value("cpu.threads")}T");
        var gpu = info.Gpus.PrimaryGpu();
        report.AppendLine($"GPU       {gpu?.Name} | {gpu?.Vram}");
        report.AppendLine($"Board     {info.Motherboard.Value("board.vendor")} {info.Motherboard.Value("board.model")} | BIOS {info.Motherboard.Value("bios.version")}");
        report.AppendLine($"RAM       {info.Ram.Value("ram.total")} {info.Ram.Value("ram.module_config")} {info.Ram.Value("ram.speed")} | {info.Ram.Value("ram.manufacturer")}");
        report.AppendLine($"Sensors   {sensors.Count} values, {statistics.AvailableSensors} available, {statistics.Providers.Count(p => p.Available)}/{statistics.Providers.Count} providers");
        report.AppendLine($"PawnIO    {pawnIo.State} ({pawnIo.Detail})");
        report.AppendLine($"NVML      {nvml?.NvmlVersion ?? "N/A"} | BAR1 {nvml?.Bar1TotalBytes?.ToString(CultureInfo.InvariantCulture) ?? "N/A"}");
        report.AppendLine($"Features  ReBAR={features.ResizableBar} HAGS={features.HardwareGpuScheduling} GameMode={features.GameMode}");
        report.AppendLine($"Elevated  {LibreHardwareMonitorProvider.IsElevated}");
        report.AppendLine();

        var languages = new[] { "en", "uk" };
        var viewports = new[]
        {
            (Window: 1080, Height: 720),
            (Window: 920, Height: 600),
        };

        var failures = 0;
        foreach (var language in languages)
        {
            Loc.Instance.SetLanguage(language);
            foreach (var viewport in viewports)
            {
                var width = viewport.Window - WindowChromeWidth;
                var view = new SystemPageView();
                var model = new SystemPageModel
                {
                    Info = info,
                    Security = security,
                    Features = features,
                    Software = software,
                    Diagnostics = SystemPageComposer.BuildDiagnostics(new SystemDiagnosticInput(
                        LibreHardwareMonitorProvider.IsElevated,
                        pawnIo,
                        broker,
                        sensors.Contains("fan.cpu.0.rpm"),
                        sensors.Contains("fan.gpu.0.rpm"),
                        statistics,
                        CountAvailable(sensors, SensorIds.CpuUsage, SensorIds.CpuTemperature, SensorIds.CpuClock, SensorIds.CpuPower),
                        CountAvailable(sensors, SensorIds.GpuUsage, SensorIds.GpuTemperature, SensorIds.GpuClock, SensorIds.VramUsed, SensorIds.GpuPower),
                        PerformanceLogUsers.GetState(),
                        false,
                        null)),
                };

                var host = Render(view, model, sensors, width);
                var height = (int)Math.Ceiling(host.DesiredSize.Height);
                var fullPath = Path.Combine(directory,
                    $"system-020-{viewport.Window}x{viewport.Height}-{language}-full.png");
                Save(host, width, height, fullPath);

                var cropHeight = Math.Min(viewport.Height, height);
                SaveCrop(fullPath, width, cropHeight,
                    Path.Combine(directory, $"system-020-{viewport.Window}x{viewport.Height}-{language}.png"));

                var overflow = FindOverflow(host, width).ToArray();
                report.AppendLine(
                    $"{language.ToUpperInvariant()} {viewport.Window}x{viewport.Height}: page {width:0}×{height} dp, " +
                    $"crop {cropHeight} px, overflow {overflow.Length}");
                foreach (var line in overflow)
                {
                    report.AppendLine("    ! " + line);
                    failures++;
                }
            }
        }

        var reportPath = Path.Combine(directory, "system-020-report.txt");
        File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
        Console.WriteLine(report.ToString());
        return failures == 0 ? 0 : 1;
    }

    private static Border Render(SystemPageView view, SystemPageModel model, SensorSnapshot sensors, double width)
    {
        view.Render(model, sensors);
        var host = new Border
        {
            Width = width,
            Background = (Brush)Application.Current.FindResource("SystemPageBrush"),
            Child = view,
        };

        host.Measure(new Size(width, double.PositiveInfinity));
        host.Arrange(new Rect(0, 0, width, Math.Max(1, host.DesiredSize.Height)));
        host.UpdateLayout();
        return host;
    }

    private static void Save(FrameworkElement host, double width, int height, string path)
    {
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(width)),
            Math.Max(1, height),
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(host);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void SaveCrop(string sourcePath, double width, int height, string path)
    {
        using var stream = File.OpenRead(sourcePath);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var source = decoder.Frames[0];
        var crop = new CroppedBitmap(source,
            new Int32Rect(0, 0, Math.Min((int)width, source.PixelWidth), Math.Min(height, source.PixelHeight)));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(crop));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static IEnumerable<string> FindOverflow(FrameworkElement root, double width)
    {
        foreach (var element in Descendants(root))
        {
            if (element is not FrameworkElement { IsVisible: true, ActualWidth: > 0 } item)
            {
                continue;
            }

            var bounds = item.TransformToAncestor(root)
                .TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
            if (bounds.Right > width + 0.6)
            {
                var label = item is TextBlock text ? "'" + text.Text + "'" : item.GetType().Name;
                yield return $"{label} right={bounds.Right:0.0} > {width:0.0}";
            }
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static int CountAvailable(SensorSnapshot snapshot, params string[] ids)
        => ids.Count(snapshot.IsAvailable);
}
