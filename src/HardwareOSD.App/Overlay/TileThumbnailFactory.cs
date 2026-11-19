using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HardwareOSD.Core.Fps;
using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.Hardware.Providers;
using HardwareOSD.Core.TileOsd;

namespace HardwareOSD.App.Overlay;

/// <summary>
/// Builds small real previews of the saved tile designs from their own scene documents. The preview
/// uses the same renderer as the overlay, scaled down; it is not a hand-drawn illustration.
/// </summary>
public static class TileThumbnailFactory
{
    private static readonly Dictionary<string, BitmapSource> Cache = new(StringComparer.Ordinal);
    private static readonly Lazy<PreviewData> Data = new(CreateData);

    public static BitmapSource Get(string designId, int width = 150, int height = 96)
    {
        var key = designId + "|" + width + "x" + height;
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var design = TileDesignCatalog.Find(designId);
        var bitmap = design is null ? Placeholder(width, height) : Render(design, width, height);
        Cache[key] = bitmap;
        return bitmap;
    }

    /// <summary>Renders a profile's own (possibly edited) design, not the catalog original.</summary>
    public static BitmapSource Get(Core.Config.OsdProfile profile, int width = 150, int height = 96)
    {
        var design = profile.TileDesign;
        if (design is null)
        {
            return Placeholder(width, height);
        }

        var revision = profile.HasCustomDesign
            ? profile.TileDesignJson!.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) +
              ":" + profile.TileDesignJson.GetHashCode().ToString("X8",
                  System.Globalization.CultureInfo.InvariantCulture)
            : "catalog";
        var key = "profile:" + profile.Id + ":" + revision + "|" + width + "x" + height;
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var bitmap = Render(design, width, height);
        Cache[key] = bitmap;
        return bitmap;
    }

    private static BitmapSource Render(TileDesignDocument design, int width, int height)
    {
        var data = Data.Value;
        var scene = TileSceneBuilder.Build(design, data.Snapshot, data.Fps, data.History,
            new Core.Config.TileOverrideSettings(), data.Context);
        if (scene.IsEmpty)
        {
            return Placeholder(width, height);
        }

        var scale = Math.Min(width / Math.Max(1, scene.Width), height / Math.Max(1, scene.Height));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x14, 0x18, 0x1E)), null,
                new Rect(0, 0, width, height));
            var x = (width - scene.Width * scale) / 2;
            var y = (height - scene.Height * scale) / 2;
            dc.PushTransform(new TranslateTransform(x, y));
            TileSceneRenderer.Draw(dc, scene, 1.0, scale);
            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource Placeholder(int width, int height)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x24, 0x2C)), null,
                new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static PreviewData CreateData()
    {
        var history = new SensorHistory(180);
        history.Track(SensorProviderFactory.HistorySensorIds());
        using var demo = new DemoSensorProvider();
        demo.Initialize();
        SensorSnapshot snapshot = SensorSnapshot.Empty;
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
        var context = new TileRenderContext("NVIDIA GeForce RTX 4080 Laptop GPU",
            "12th Gen Intel Core i9-12900HX", "551.23", "DX12", start, start + 179_000);
        return new PreviewData(snapshot, history, fps, context);
    }

    private sealed record PreviewData(
        SensorSnapshot Snapshot,
        SensorHistory History,
        FpsSnapshot Fps,
        TileRenderContext Context);
}
