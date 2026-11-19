using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HardwareOSD.App.Overlay;
using HardwareOSD.Core.Config;
using HardwareOSD.Core.Fps;
using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.Hardware.Providers;
using HardwareOSD.Core.Overlay;

namespace HardwareOSD.App.Diagnostics;

/// <summary>
/// QA-only offscreen renderer. Produces PNG previews of every layout plus a geometry report so
/// layout regressions (clipped text, negative sizes, overflowing cells) can be detected without a
/// display. Triggered by the hidden "--render &lt;directory&gt;" switch; never used in production.
/// </summary>
internal static class VisualQa
{
    public static int Render(string directory)
    {
        Directory.CreateDirectory(directory);
        var report = new StringBuilder();
        report.AppendLine($"HardwareOSD layout QA — {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine();

        var settings = AppSettings.CreateDefault();
        settings.Overlay.ShowSparklines = true;
        settings.Overlay.BackgroundOpacity = 74;
        settings.Overlay.Shadow = true;

        var history = new SensorHistory(180);
        history.Track(SensorProviderFactory.HistorySensorIds());

        using var demo = new DemoSensorProvider();
        demo.Initialize();
        var snapshot = SensorSnapshot.Empty;
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var i = 0; i < 90; i++)
        {
            var now = start + i * 1000L;
            var values = demo.Read(now);
            var dictionary = new Dictionary<string, SensorValue>(StringComparer.Ordinal);
            foreach (var value in values)
            {
                dictionary[value.Id] = value;
            }

            SensorPostProcessor.Sanitize(dictionary, now);
            SensorPostProcessor.Derive(dictionary, now);
            snapshot = SensorSnapshot.Create(i + 1, now, dictionary);
            history.Append(snapshot);
        }

        foreach (var mode in new[] { OverlayLayoutMode.Stacked, OverlayLayoutMode.Inline, OverlayLayoutMode.Detailed })
        {
            foreach (var sparklines in new[] { false, true })
            {
                settings.Overlay.Layout = mode;
                settings.Overlay.ShowSparklines = sparklines;
                var theme = OverlayTheme.Create(settings);
                var layout = OverlayLayoutBuilder.Build(settings, snapshot);
                var visual = OverlayVisualBuilder.Build(layout, theme, history, mode, 1.0, demo: true);
                var name = $"{mode.ToString().ToLowerInvariant()}-{(sparklines ? "graphs" : "plain")}";
                var path = Path.Combine(directory, $"overlay-{name}.png");
                RenderToPng(visual, theme, path);
                report.AppendLine($"{name,-22} {visual.Width,7:0.0} x {visual.Height,6:0.0} dp   rows={visual.Rows.Count} cores={visual.Cores.Count} -> {Path.GetFileName(path)}");
                foreach (var issue in FindOverflow(visual, theme))
                {
                    report.AppendLine("    ! " + issue);
                }
            }
        }

        // A compact 12 px variant, as used in a corner of a 1080p screen.
        settings.Overlay.Layout = OverlayLayoutMode.Stacked;
        settings.Overlay.FontSize = 12;
        settings.Overlay.PaddingX = 10;
        settings.Overlay.PaddingY = 6;
        settings.Overlay.RowSpacing = 2;
        settings.Overlay.ShowSparklines = false;
        var smallTheme = OverlayTheme.Create(settings);
        var smallVisual = OverlayVisualBuilder.Build(
            OverlayLayoutBuilder.Build(settings, snapshot), smallTheme, history, OverlayLayoutMode.Stacked, 1.0, demo: true);
        RenderToPng(smallVisual, smallTheme, Path.Combine(directory, "overlay-compact.png"));
        report.AppendLine($"{"compact",-22} {smallVisual.Width,7:0.0} x {smallVisual.Height,6:0.0} dp");
        foreach (var issue in FindOverflow(smallVisual, smallTheme))
        {
            report.AppendLine("    ! " + issue);
        }

        // FPS subtitle: the process name must sit below the value in a fixed-width slot. Render a
        // long and a short name with identical settings; the reported widths must match.
        var subtitleSettings = AppSettings.CreateDefault();
        subtitleSettings.Overlay.ShowSparklines = false;
        subtitleSettings.Fps.Enabled = true;
        subtitleSettings.SetMetricVisible("frame.fps", true);
        var subtitleTheme = OverlayTheme.Create(subtitleSettings);
        var longFps = new FpsSnapshot(1, snapshot.CapturedAtMs, FpsStatus.Capturing, FpsSource.Presented,
            false, 4242, "a-very-long-process-name-that-would-otherwise-widen-the-overlay.exe",
            144, 6.94, 120, null);
        var shortFps = longFps with { ProcessName = "cs2.exe" };
        var longVisual = OverlayVisualBuilder.Build(
            OverlayLayoutBuilder.Build(subtitleSettings, snapshot, longFps), subtitleTheme, history,
            OverlayLayoutMode.Stacked, 1.0, demo: true);
        var shortVisual = OverlayVisualBuilder.Build(
            OverlayLayoutBuilder.Build(subtitleSettings, snapshot, shortFps), subtitleTheme, history,
            OverlayLayoutMode.Stacked, 1.0, demo: true);
        RenderToPng(longVisual, subtitleTheme, Path.Combine(directory, "overlay-fps-long-name.png"));
        RenderToPng(shortVisual, subtitleTheme, Path.Combine(directory, "overlay-fps-short-name.png"));
        report.AppendLine($"{"fps-long-name",-22} {longVisual.Width,7:0.0} x {longVisual.Height,6:0.0} dp");
        report.AppendLine($"{"fps-short-name",-22} {shortVisual.Width,7:0.0} x {shortVisual.Height,6:0.0} dp   " +
                          $"width-equal={Math.Abs(longVisual.Width - shortVisual.Width) < 0.01}");

        File.WriteAllText(Path.Combine(directory, "layout-report.txt"), report.ToString());
        Console.WriteLine(report.ToString());
        return 0;
    }

    private static IEnumerable<string> FindOverflow(OverlayVisual visual, OverlayTheme theme)
    {
        var limit = visual.Width - theme.PaddingX + 0.6;
        foreach (var row in visual.Rows)
        {
            foreach (var cell in row.Cells)
            {
                var right = cell.Unit is not null
                    ? cell.UnitLeft + cell.Unit.Width
                    : cell.ValueLeft + cell.Value.Width;
                if (right > limit)
                {
                    yield return $"row {row.Group}: '{cell.Value.Text}' overflows by {right - limit:0.0} dp";
                }
            }

            if (row.Label is not null && row.LabelLeft + row.Label.Width > limit)
            {
                yield return $"row {row.Group}: label overflows";
            }

            if (row.Sparkline is { } graph && graph.Bounds.Right > visual.Width - theme.PaddingX + 0.6)
            {
                yield return $"row {row.Group}: sparkline overflows";
            }
        }

        foreach (var core in visual.Cores)
        {
            if (core.UsageLeft + core.Usage.Width > limit)
            {
                yield return $"core {core.Label.Text}: overflow";
            }
        }
    }

    private static void RenderToPng(OverlayVisual visual, OverlayTheme theme, string path)
    {
        var margin = 22;
        var width = Math.Max(60, visual.Width + margin * 2);
        var height = Math.Max(40, visual.Height + margin * 2);

        var surface = new OverlaySurface();
        surface.SetContent(visual, theme);

        var host = new Grid
        {
            Width = width,
            Height = height,
            Background = new LinearGradientBrush(
                Color.FromRgb(0x24, 0x33, 0x42),
                Color.FromRgb(0x0C, 0x12, 0x19),
                new Point(0, 0),
                new Point(1, 1)),
        };
        host.Children.Add(surface);
        surface.Margin = new Thickness(margin);
        surface.HorizontalAlignment = HorizontalAlignment.Left;
        surface.VerticalAlignment = VerticalAlignment.Top;

        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(width),
            (int)Math.Ceiling(height),
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(host);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string Invariant(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}
