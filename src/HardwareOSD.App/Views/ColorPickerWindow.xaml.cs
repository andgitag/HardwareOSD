using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using HardwareOSD.App.Native;
using HardwareOSD.App.Overlay;
using HardwareOSD.App.Theming;

namespace HardwareOSD.App.Views;

/// <summary>
/// Modal colour picker with RGB sliders, hex/RGB text input and a set of neutral palettes.
/// Used for every overlay colour role (labels, values, units, warnings, critical, background).
/// </summary>
public partial class ColorPickerWindow : Window
{
    private static readonly string[] DefaultPalette =
    {
        "#FFFFFF", "#F4F4F4", "#CFCFCF", "#9A9A9A",
        "#000000", "#101010", "#1C1C1C", "#3A3A3A",
        "#6FD8CC", "#7FB2FF", "#FFB020", "#FF5C5C",
        "#0F766E", "#B45309", "#B91C1C", "#F2F1EE",
    };

    private bool _syncing;

    public ColorPickerWindow(string title, Color initial)
    {
        InitializeComponent();
        Title = title;
        SourceInitialized += (_, _) =>
            NativeMethods.SetTitleBarTheme(new WindowInteropHelper(this).Handle, AppThemeManager.IsDark);
        BuildPalette();
        AttachHandlers();
        SetColor(initial, updateHex: true);
        Preview.Background = new SolidColorBrush(initial);

        RedSlider.ValueChanged += (_, _) => OnSliderChanged();
        GreenSlider.ValueChanged += (_, _) => OnSliderChanged();
        BlueSlider.ValueChanged += (_, _) => OnSliderChanged();
        HexBox.TextChanged += OnHexChanged;
    }

    public Color SelectedColor { get; private set; }

    public static Color? Pick(Window owner, string title, string initialHex)
    {
        var initial = ColorUtil.Parse(initialHex, Colors.White);
        var dialog = new ColorPickerWindow(title, initial) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.SelectedColor : null;
    }

    private void AttachHandlers()
    {
        // Slider/hex handlers are wired in the constructor after the controls exist.
    }

    private void BuildPalette()
    {
        foreach (var hex in DefaultPalette)
        {
            var color = ColorUtil.Parse(hex, Colors.White);
            var swatch = new Border
            {
                Width = 30,
                Height = 24,
                Margin = new Thickness(0, 0, 6, 6),
                CornerRadius = new CornerRadius(2),
                BorderBrush = (Brush)FindResource("BorderStrongBrush"),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(color),
                ToolTip = hex,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            swatch.SetResourceReference(Border.BorderBrushProperty, "BorderStrongBrush");
            swatch.MouseLeftButtonUp += (_, _) => SetColor(color, updateHex: true);
            Palette.Children.Add(swatch);
        }
    }

    private void OnSliderChanged()
    {
        if (_syncing)
        {
            return;
        }

        SetColor(Color.FromRgb(
            (byte)Math.Round(RedSlider.Value),
            (byte)Math.Round(GreenSlider.Value),
            (byte)Math.Round(BlueSlider.Value)), updateHex: true);
    }

    private void OnHexChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        var text = HexBox.Text.Trim();
        if (text.Length is not (6 or 7))
        {
            return;
        }

        if (!text.StartsWith('#'))
        {
            text = "#" + text;
        }

        try
        {
            if (ColorConverter.ConvertFromString(text) is Color color)
            {
                SetColor(color, updateHex: false);
            }
        }
        catch
        {
            // Keep the previous colour while the user is typing.
        }
    }

    private void SetColor(Color color, bool updateHex)
    {
        SelectedColor = color;
        _syncing = true;
        try
        {
            RedSlider.Value = color.R;
            GreenSlider.Value = color.G;
            BlueSlider.Value = color.B;
            RedValue.Text = color.R.ToString();
            GreenValue.Text = color.G.ToString();
            BlueValue.Text = color.B.ToString();
            Preview.Background = new SolidColorBrush(color);
            if (updateHex)
            {
                HexBox.Text = ColorUtil.ToHex(color);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        SelectedColor = Color.FromRgb((byte)Math.Round(RedSlider.Value),
            (byte)Math.Round(GreenSlider.Value), (byte)Math.Round(BlueSlider.Value));
        DialogResult = true;
    }
}
