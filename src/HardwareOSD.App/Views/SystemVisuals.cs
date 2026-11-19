using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using HardwareOSD.Core.SystemInfo;

namespace HardwareOSD.App.Views;

/// <summary>Small line icons drawn as vectors so the System page needs no downloaded assets.</summary>
public enum SystemGlyph
{
    Power,
    Temperature,
    Clock,
    Chip,
    Download,
    Cube,
    Pulse,
    Monitor,
    Layers,
    Copy,
    Refresh,
    Storage,
}

/// <summary>
/// Local visuals for the System page: vector icons, brand wordmark tiles and status dots.
/// Nothing is fetched from the network; unknown vendors deliberately get a neutral chip tile.
/// </summary>
public static class SystemVisuals
{
    private const string IconBrushKey = "SystemIconBrush";

    public static FrameworkElement Icon(SystemGlyph glyph, double size)
    {
        var path = new Path
        {
            Data = Geometry.Parse(GeometryFor(glyph)),
            StrokeThickness = 1.1,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Stretch = Stretch.None,
        };

        if (IsFilled(glyph))
        {
            path.SetResourceReference(Shape.FillProperty, IconBrushKey);
        }
        else
        {
            path.SetResourceReference(Shape.StrokeProperty, IconBrushKey);
            path.Fill = null;
        }

        var canvas = new Canvas { Width = 12, Height = 12 };
        canvas.Children.Add(path);
        return new Viewbox
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Child = canvas,
            SnapsToDevicePixels = true,
        };
    }

    public static FrameworkElement AccentIcon(SystemGlyph glyph, double size)
    {
        var path = new Path
        {
            Data = Geometry.Parse(GeometryFor(glyph)),
            StrokeThickness = 1.2,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Stretch = Stretch.None,
        };
        path.SetResourceReference(Shape.StrokeProperty, "SystemAccentBrush");
        path.Fill = null;
        var canvas = new Canvas { Width = 12, Height = 12 };
        canvas.Children.Add(path);
        return new Viewbox { Width = size, Height = size, Stretch = Stretch.Uniform, Child = canvas };
    }

    public static Ellipse Dot(string brushKey, double size = 9)
    {
        var dot = new Ellipse
        {
            Width = size,
            Height = size,
            VerticalAlignment = VerticalAlignment.Center,
        };
        dot.SetResourceReference(Shape.FillProperty, brushKey);
        return dot;
    }

    /// <summary>74 × 38 brand tile: coloured fill with a white wordmark, or the neutral chip icon.</summary>
    public static Border BrandTile(HardwareBrand brand)
    {
        var tile = new Border
        {
            Width = 74,
            Height = 38,
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(BrandColor(brand)),
            VerticalAlignment = VerticalAlignment.Top,
        };

        var wordmark = BrandCatalog.Wordmark(brand);
        if (wordmark is null)
        {
            var icon = Icon(SystemGlyph.Chip, 20);
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
            tile.Child = icon;
            return tile;
        }

        var text = new TextBlock
        {
            Text = wordmark,
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont"),
            FontSize = WordmarkSize(brand),
            FontWeight = brand is HardwareBrand.Nvidia or HardwareBrand.Msi ? FontWeights.Bold : FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        TextOptions.SetTextFormattingMode(text, TextFormattingMode.Ideal);
        text.SetResourceReference(TextBlock.ForegroundProperty, "SystemBrandTextBrush");
        tile.Child = new Viewbox
        {
            Margin = new Thickness(9, 6, 9, 6),
            Stretch = Stretch.Uniform,
            Child = text,
        };
        return tile;
    }

    public static Color BrandColor(HardwareBrand brand)
        => brand switch
        {
            HardwareBrand.Intel => Hex("#0068B5"),
            HardwareBrand.Amd => Hex("#ED1C24"),
            HardwareBrand.Nvidia => Hex("#76B900"),
            HardwareBrand.Msi => Hex("#E4002B"),
            HardwareBrand.Asus => Hex("#00539B"),
            HardwareBrand.Gigabyte => Hex("#B8860B"),
            HardwareBrand.Asrock => Hex("#0066B3"),
            HardwareBrand.Crucial => Hex("#2E7BBF"),
            HardwareBrand.Kingston => Hex("#D71920"),
            HardwareBrand.Corsair => Hex("#B08D00"),
            HardwareBrand.Samsung => Hex("#1428A0"),
            HardwareBrand.SkHynix => Hex("#D6002B"),
            HardwareBrand.Adata => Hex("#C8102E"),
            HardwareBrand.GSkill => Hex("#B01E28"),
            HardwareBrand.WesternDigital => Hex("#005A9C"),
            HardwareBrand.Seagate => Hex("#4E9A2A"),
            HardwareBrand.Kioxia => Hex("#D6002A"),
            HardwareBrand.Sandisk => Hex("#D71920"),
            _ => Hex("#3A3A3D"),
        };

    private static double WordmarkSize(HardwareBrand brand)
        => brand switch
        {
            HardwareBrand.Intel => 20,
            HardwareBrand.Amd => 19,
            HardwareBrand.Nvidia => 15,
            HardwareBrand.Msi => 21,
            HardwareBrand.Asus => 17,
            HardwareBrand.Gigabyte => 12.5,
            HardwareBrand.Asrock => 15,
            HardwareBrand.Crucial => 17,
            HardwareBrand.Kingston => 12.5,
            HardwareBrand.Corsair => 13,
            HardwareBrand.Samsung => 12.5,
            HardwareBrand.SkHynix => 13,
            HardwareBrand.Adata => 15,
            HardwareBrand.GSkill => 13,
            HardwareBrand.WesternDigital => 18,
            HardwareBrand.Seagate => 14,
            HardwareBrand.Kioxia => 14,
            HardwareBrand.Sandisk => 14,
            _ => 14,
        };

    private static Color Hex(string value) => (Color)ColorConverter.ConvertFromString(value);

    private static bool IsFilled(SystemGlyph glyph) => glyph == SystemGlyph.Power;

    private static string GeometryFor(SystemGlyph glyph)
        => glyph switch
        {
            SystemGlyph.Power => "M6.7,0.8 L2.3,6.6 H5.2 L4.8,11.2 L9.7,5.1 H6.5 Z",
            SystemGlyph.Temperature =>
                "M4.7,1.7 A1.3,1.3 0 0 1 7.3,1.7 V7.2 A2.6,2.6 0 1 1 4.7,7.2 Z M6,8.5 V3.6",
            SystemGlyph.Clock => "M6,0.9 A5.1,5.1 0 1 1 5.99,0.9 Z M6,3.1 V6.2 L8.2,7.4",
            SystemGlyph.Chip =>
                "M3.4,3.4 H8.6 V8.6 H3.4 Z M5.1,5.1 H6.9 V6.9 H5.1 Z " +
                "M3.4,1.4 V2.4 M6,1.4 V2.4 M8.6,1.4 V2.4 " +
                "M3.4,9.6 V10.6 M6,9.6 V10.6 M8.6,9.6 V10.6 " +
                "M1.4,3.4 H2.4 M1.4,6 H2.4 M1.4,8.6 H2.4 " +
                "M9.6,3.4 H10.6 M9.6,6 H10.6 M9.6,8.6 H10.6",
            SystemGlyph.Download => "M6,1.4 V7.3 M3.4,4.9 L6,7.5 L8.6,4.9 M1.7,9.4 V10.7 H10.3 V9.4",
            SystemGlyph.Cube => "M6,1.2 L10.4,3.5 V8.5 L6,10.8 L1.6,8.5 V3.5 Z M1.6,3.5 L6,5.8 L10.4,3.5 M6,5.8 V10.8",
            SystemGlyph.Pulse => "M1,6.1 H3.1 L4.2,3.3 L6.1,8.9 L7.4,5.3 L8.5,6.1 H11",
            SystemGlyph.Monitor => "M1.4,1.9 H10.6 V7.9 H1.4 Z M4.4,10.6 H7.6 M6,7.9 V10.6",
            SystemGlyph.Layers => "M6,1.2 L10.5,3.7 L6,6.2 L1.5,3.7 Z M1.5,6.2 L6,8.7 L10.5,6.2 M1.5,8.7 L6,11.2 L10.5,8.7",
            SystemGlyph.Copy =>
                "M4.2,1.6 H9.2 A1.2,1.2 0 0 1 10.4,2.8 V7.8 " +
                "M2.8,4 H7.6 A1.2,1.2 0 0 1 8.8,5.2 V10.4 A1.2,1.2 0 0 1 7.6,11.6 H2.8 A1.2,1.2 0 0 1 1.6,10.4 V5.2 A1.2,1.2 0 0 1 2.8,4 Z",
            SystemGlyph.Refresh => "M9.9,3.2 A4.3,4.3 0 1 0 10.3,7.6 M9.9,1.2 V3.6 H7.5",
            SystemGlyph.Storage => "M1.8,3.2 H10.2 V8.8 H1.8 Z M1.8,6 H10.2 M7.4,7.4 H8.8",
            _ => "M3.4,3.4 H8.6 V8.6 H3.4 Z",
        };
}
