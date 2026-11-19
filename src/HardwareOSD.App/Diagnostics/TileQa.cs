using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HardwareOSD.App.Overlay;
using HardwareOSD.Core.Config;
using HardwareOSD.Core.Fps;
using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.Hardware.Providers;
using HardwareOSD.Core.TileOsd;

namespace HardwareOSD.App.Diagnostics;

/// <summary>
/// QA-only renderer for the tile design library. It builds every saved design from the same demo
/// sensor stream as the layout QA, adds the reference machine's names and the real FPS history, then
/// draws the scene through the production renderer. Output is canvas + 8 px padding, matching the
/// CapFrameX research renders so the two sets can be compared pixel for pixel.
/// </summary>
internal static class TileQa
{
    public static int Render(string directory, string? onlyId = null)
    {
        Directory.CreateDirectory(directory);
        var (snapshot, history, fps) = DemoData();
        var context = new TileRenderContext(
            "NVIDIA GeForce RTX 4080 Laptop GPU",
            "12th Gen Intel Core i9-12900HX",
            "551.23",
            "DX12",
            fps.TimestampMs - 3_723_000,
            fps.TimestampMs);

        var manifest = new StringBuilder();
        manifest.AppendLine("id,name,family,layout,width,height,elements,metrics,unavailable,charts,bars,gauges,images");
        var rendered = 0;
        foreach (var design in TileDesignCatalog.All)
        {
            if (onlyId is not null && !string.Equals(design.Id, onlyId, StringComparison.Ordinal))
            {
                continue;
            }

            var scene = TileSceneBuilder.Build(design, snapshot, fps, history, new TileOverrideSettings(),
                context);
            var path = Path.Combine(directory, design.Id + ".png");
            RenderToPng(scene, path);
            var metrics = scene.Elements.OfType<TileMetricScene>().ToList();
            manifest.AppendLine(string.Join(',',
                design.Id,
                Quote(design.Name),
                Quote(design.Family),
                "Tile",
                scene.Width.ToString("0.##", CultureInfo.InvariantCulture),
                scene.Height.ToString("0.##", CultureInfo.InvariantCulture),
                scene.Elements.Count.ToString(CultureInfo.InvariantCulture),
                metrics.Count.ToString(CultureInfo.InvariantCulture),
                metrics.Count(metric => !metric.Available).ToString(CultureInfo.InvariantCulture),
                scene.Elements.OfType<TileChartScene>().Count().ToString(CultureInfo.InvariantCulture),
                scene.Elements.OfType<TileBarScene>().Count().ToString(CultureInfo.InvariantCulture),
                scene.Elements.OfType<TileGaugeScene>().Count().ToString(CultureInfo.InvariantCulture),
                scene.Elements.OfType<TileImageScene>().Count().ToString(CultureInfo.InvariantCulture)));
            rendered++;
        }

        File.WriteAllText(Path.Combine(directory, "tile-render-manifest.csv"), manifest.ToString(),
            new UTF8Encoding(false));
        Console.WriteLine($"Rendered {rendered} tile designs to {directory}");
        return 0;
    }

    private static (SensorSnapshot Snapshot, SensorHistory History, FpsSnapshot Fps) DemoData()
    {
        var history = new SensorHistory(180);
        history.Track(SensorProviderFactory.HistorySensorIds());

        using var demo = new DemoSensorProvider();
        demo.Initialize();
        var snapshot = SensorSnapshot.Empty;
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var i = 0; i < 180; i++)
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
            history.AppendValue("frame.fps", 138 + Math.Sin(i / 6.0) * 9);
            history.AppendValue("frame.time", 7.2 + Math.Sin(i / 8.0) * 1.4);
            history.AppendValue("frame.fps.1low", 118 + Math.Sin(i / 11.0) * 4);
        }

        var fps = new FpsSnapshot(180, start + 179_000, FpsStatus.Capturing, FpsSource.Presented,
            false, 4242, "game.exe", 144.2, 6.94, 121.5, null);
        return (snapshot, history, fps);
    }

    private static void RenderToPng(TileScene scene, string path)
    {
        const double padding = 8;
        var width = Math.Max(1, (int)Math.Ceiling(scene.Width + padding * 2));
        var height = Math.Max(1, (int)Math.Ceiling(scene.Height + padding * 2));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // The reference renders use a checkerboard for alpha. Drawing the same pattern here
            // keeps white text on transparent designs visible when the PNG is reviewed.
            for (var y = 0; y < height; y += 8)
            {
                for (var x = 0; x < width; x += 8)
                {
                    var color = ((x / 8) + (y / 8)) % 2 == 0
                        ? Color.FromRgb(0x2B, 0x2B, 0x2B)
                        : Color.FromRgb(0x1E, 0x1E, 0x1E);
                    dc.DrawRectangle(new SolidColorBrush(color), null,
                        new Rect(x, y, Math.Min(8, width - x), Math.Min(8, height - y)));
                }
            }

            dc.PushTransform(new TranslateTransform(padding, padding));
            TileSceneRenderer.Draw(dc, scene, 1.0);
            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string Quote(string value)
        => value.IndexOfAny(new[] { ',', '"', '\n' }) < 0
            ? value
            : '"' + value.Replace("\"", "\"\"") + '"';
}
