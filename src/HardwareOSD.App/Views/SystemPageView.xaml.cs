using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using HardwareOSD.App.Localization;
using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.SystemInfo;

namespace HardwareOSD.App.Views;

/// <summary>
/// System page rebuilt against the CapFrameX Info reference: full-width OS banner, 2×2 hardware
/// cards, then full-width Storage, Software Components, Platform Security and Diagnostics sections.
/// Static inventory is rebuilt on navigation; sensor text and bars update in place on every tick.
/// </summary>
public partial class SystemPageView : UserControl
{
    private const double TileGap = 9;
    private const double SectionGap = 10;
    private const double ChipHeight = 29;
    private const double SoftwareCardHeight = 66;
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    private readonly Dictionary<string, TextBlock> _cpuTileValues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> _gpuTileValues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> _memoryTileValues = new(StringComparer.Ordinal);
    private readonly List<ChipVisual> _diagnosticChips = new();
    private readonly DispatcherTimer _copyTimer;

    private SystemPageModel? _model;
    private SensorSnapshot _sensors = SensorSnapshot.Empty;
    private IReadOnlyList<SystemDiagnostic> _diagnostics = Array.Empty<SystemDiagnostic>();
    private string _diagnosticSignature = string.Empty;
    private double _cpuLoad;
    private double _gpuLoad;
    private double _memoryUsage;

    public SystemPageView()
    {
        InitializeComponent();
        CopyIconHost.Content = SystemVisuals.AccentIcon(SystemGlyph.Copy, 16);
        CpuBar.SizeChanged += (_, _) => ApplyBars();
        GpuBar.SizeChanged += (_, _) => ApplyBars();
        MemoryBar.SizeChanged += (_, _) => ApplyBars();

        _copyTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2.2),
        };
        _copyTimer.Tick += (_, _) =>
        {
            _copyTimer.Stop();
            CopyInfoLabel.Text = Loc.T("system_copy_info");
        };
    }

    /// <summary>Rebuilds every static part of the page from a fresh inventory snapshot.</summary>
    public void Render(SystemPageModel model, SensorSnapshot sensors)
    {
        _model = model;
        _sensors = sensors;
        _diagnostics = model.Diagnostics;

        RenderOperatingSystem(model.Info);
        RenderProcessor(model.Info);
        RenderGraphics(model.Info);
        RenderMainboard(model.Info);
        RenderMemory(model.Info);
        RenderStorage(model.Info);
        RenderSoftware(model.Software);
        RenderSecurity(model.Security);
        RenderDiagnostics(model.Diagnostics, force: true);
        UpdateSensors(sensors);
    }

    /// <summary>Fast path: text and bar geometry only, called from the runtime tick.</summary>
    public void UpdateLive(SensorSnapshot sensors, IReadOnlyList<SystemDiagnostic> diagnostics)
    {
        UpdateSensors(sensors);
        RenderDiagnostics(diagnostics, force: false);
    }

    private void RenderOperatingSystem(SystemInfoSnapshot info)
    {
        var build = info.Windows.Value("os.build");
        var caption = info.Windows.Value("os.name");

        OsEyebrow.Text = Loc.T("system_eyebrow_os");
        OsTitle.Text = caption is null
            ? Loc.T("system_info_unknown")
            : caption + (build is null ? string.Empty : " Build " + build);
        OsTitle.ToolTip = OsTitle.Text;
        CopyInfoLabel.Text = Loc.T("system_copy_info");
        CopyInfoButton.ToolTip = Loc.T("system_copy_info");

        SystemErrorText.Visibility = info.Error is null ? Visibility.Collapsed : Visibility.Visible;
        SystemErrorText.Text = info.Error is null ? string.Empty : Loc.F("system_info_error", info.Error);
    }

    private void RenderProcessor(SystemInfoSnapshot info)
    {
        var items = info.Cpu;
        var name = SystemInfoFormat.CleanProcessorName(items.Value("cpu.name"));
        CpuBrandHost.Content = SystemVisuals.BrandTile(
            BrandCatalog.Resolve(HardwareComponentKind.Cpu, items.Value("cpu.vendor"), items.Value("cpu.name")));
        CpuTitle.Text = name ?? Loc.T("system_info_unknown");
        CpuTitle.ToolTip = CpuTitle.Text;
        CpuSubtitle.Text = ProcessorSubtitle(items);

        CpuTiles.Children.Clear();
        _cpuTileValues.Clear();
        var tiles = new List<StatTile>
        {
            BuildTile("power", SystemGlyph.Power, "system_power"),
            BuildTile("temp", SystemGlyph.Temperature, "system_temperature"),
            BuildTile("clock", SystemGlyph.Clock, "system_clock"),
        };
        foreach (var tile in tiles)
        {
            _cpuTileValues[tile.Key] = tile.Value;
        }

        LayoutItems(CpuTiles, tiles.Select(tile => (FrameworkElement)tile.Root).ToList(), 3, TileGap);
    }

    private void RenderGraphics(SystemInfoSnapshot info)
    {
        var gpu = info.Gpus.PrimaryGpu();
        GpuBrandHost.Content = SystemVisuals.BrandTile(
            BrandCatalog.Resolve(HardwareComponentKind.Gpu, gpu?.Vendor, gpu?.Name));
        GpuTitle.Text = gpu?.Name ?? Loc.T("system_info_unknown");
        GpuSubtitle.Text = gpu?.Vram is { Length: > 0 } vram
            ? Loc.F("system_vram_total", vram)
            : Loc.T("system_note_vram_not_reported");
        GpuSubtitle.ToolTip = GpuSubtitle.Text;
        GpuTitle.ToolTip = gpu?.Vram is { Length: > 0 } vramTitle
            ? GpuTitle.Text + " · " + Loc.F("system_vram_total", vramTitle)
            : GpuTitle.Text;

        GpuTiles.Children.Clear();
        _gpuTileValues.Clear();
        var tiles = new List<StatTile>
        {
            BuildTile("power", SystemGlyph.Power, "system_power"),
            BuildTile("temp", SystemGlyph.Temperature, "system_temperature"),
            BuildTile("clock", SystemGlyph.Clock, "system_clock"),
            BuildTile("vram", SystemGlyph.Chip, "system_vram"),
        };
        foreach (var tile in tiles)
        {
            _gpuTileValues[tile.Key] = tile.Value;
        }

        LayoutItems(GpuTiles, tiles.Select(tile => (FrameworkElement)tile.Root).ToList(), 4, TileGap);
    }

    private void RenderMainboard(SystemInfoSnapshot info)
    {
        var items = info.Motherboard;
        var vendor = items.Value("board.vendor");
        var model = items.Value("board.model");
        var brand = BrandCatalog.Resolve(HardwareComponentKind.Mainboard, vendor, model);
        BoardBrandHost.Content = SystemVisuals.BrandTile(brand);
        BoardTitle.Text = BoardTitleText(brand, vendor, model);
        BoardTitle.ToolTip = BoardTitle.Text;

        var bios = items.Value("bios.version");
        BoardSubtitle.Text = bios is null
            ? Loc.T("system_info_unknown")
            : Loc.F("system_bios", bios);
        BoardSubtitle.ToolTip = BoardSubtitle.Text;

        BoardRows.Children.Clear();
        BoardRows.ColumnDefinitions.Clear();
        BoardRows.RowDefinitions.Clear();
        var rows = new List<FrameworkElement>
        {
            BuildFeatureRow(Loc.T("system_feature_rebar"), _model?.Features.ResizableBar ?? FeatureStatus.Unknown),
            BuildFeatureRow(Loc.T("system_feature_hags"), _model?.Features.HardwareGpuScheduling ?? FeatureStatus.Unknown),
            BuildFeatureRow(Loc.T("system_feature_game_mode"), _model?.Features.GameMode ?? FeatureStatus.Unknown),
        };
        LayoutItems(BoardRows, rows, 1, 8, 33);
    }

    private void RenderMemory(SystemInfoSnapshot info)
    {
        var items = info.Ram;
        var manufacturer = items.Value("ram.manufacturer");
        var total = items.Value("ram.total");
        var configuration = items.Value("ram.module_config");
        var speed = items.Value("ram.speed");
        var title = CompactMemoryTitle(total, configuration, speed, Loc.T("system_info_unknown"));

        var brand = BrandCatalog.Resolve(HardwareComponentKind.Memory, manufacturer, items.Value("ram.type"));
        MemoryBrandHost.Content = SystemVisuals.BrandTile(brand);
        MemoryTitle.Text = title;
        MemoryTitle.ToolTip = title;
        MemorySubtitle.Text = BrandCatalog.DisplayName(brand) ??
                             manufacturer ??
                             items.Value("ram.type") ??
                             Loc.T("system_info_unknown");
        MemorySubtitle.ToolTip = MemorySubtitle.Text;

        MemoryTiles.Children.Clear();
        _memoryTileValues.Clear();
        var tiles = new List<StatTile>
        {
            BuildTile("used", SystemGlyph.Download, "system_in_use"),
            BuildTile("available", SystemGlyph.Cube, "system_available"),
        };
        foreach (var tile in tiles)
        {
            _memoryTileValues[tile.Key] = tile.Value;
        }

        LayoutItems(MemoryTiles, tiles.Select(tile => (FrameworkElement)tile.Root).ToList(), 2, TileGap);
    }

    private void RenderStorage(SystemInfoSnapshot info)
    {
        var disks = info.Storage;
        StorageList.Children.Clear();
        StorageList.ColumnDefinitions.Clear();
        StorageList.RowDefinitions.Clear();

        var total = disks.Where(disk => disk.TotalBytes is > 0).Sum(disk => disk.TotalBytes ?? 0);
        var count = disks.Count == 1
            ? Loc.T("system_storage_device")
            : Loc.F("system_storage_devices", disks.Count);
        StorageSummary.Text = total > 0
            ? count + " · " + SystemInfoFormat.FormatBytes(total)
            : count;

        if (disks.Count == 0)
        {
            var empty = new Border
            {
                Style = (Style)FindResource("SystemTile"),
                Height = 52,
            };
            empty.Child = new TextBlock
            {
                Text = Loc.T("system_storage_empty"),
                Style = (Style)FindResource("SystemTileLabel"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            LayoutItems(StorageList, new List<FrameworkElement> { empty }, 1, SectionGap, 52);
            return;
        }

        var columns = disks.Count == 1 ? 1 : 2;
        var cards = disks
            .Select(disk => (FrameworkElement)BuildDiskCard(disk))
            .ToList();
        LayoutItems(StorageList, cards, columns, SectionGap, 88);
    }

    private void RenderSoftware(IReadOnlyList<SoftwareComponentInfo> software)
    {
        SoftwareGrid.Children.Clear();
        var cards = software
            .Select(component => (FrameworkElement)BuildSoftwareCard(component))
            .ToList();
        LayoutItems(SoftwareGrid, cards, 4, SectionGap, SoftwareCardHeight);
    }

    private void RenderSecurity(PlatformSecuritySnapshot security)
    {
        SecurityGrid.Children.Clear();
        var chips = new List<FrameworkElement>
        {
            BuildChip(
                Loc.T("security_secure_boot"),
                OnOff(security.SecureBoot),
                security.SecureBoot switch
                {
                    true => SystemTone.Good,
                    false => SystemTone.Warn,
                    _ => SystemTone.Unknown,
                }).Root,
            BuildChip(
                Loc.T("security_test_signing"),
                OnOff(security.TestSigning),
                security.TestSigning switch
                {
                    false => SystemTone.Good,
                    true => SystemTone.Warn,
                    _ => SystemTone.Unknown,
                }).Root,
            BuildChip(
                Loc.T("security_vbs"),
                VbsText(security.VbsStatus),
                security.VbsStatus switch
                {
                    0 => SystemTone.Good,
                    1 or 2 => SystemTone.Warn,
                    _ => SystemTone.Unknown,
                }).Root,
            BuildChip(
                Loc.T("security_memory_integrity"),
                OnOff(security.MemoryIntegrity),
                security.MemoryIntegrity is null ? SystemTone.Unknown : SystemTone.Good).Root,
        };
        LayoutItems(SecurityGrid, chips, 4, SectionGap, ChipHeight);
    }

    private void RenderDiagnostics(IReadOnlyList<SystemDiagnostic> diagnostics, bool force)
    {
        _diagnostics = diagnostics;
        var signature = string.Join("|", diagnostics.Select(item => item.Label));
        if (!force && signature == _diagnosticSignature && _diagnosticChips.Count == diagnostics.Count)
        {
            for (var index = 0; index < diagnostics.Count; index++)
            {
                _diagnosticChips[index].Value.Text = diagnostics[index].Value;
                _diagnosticChips[index].Dot.SetResourceReference(Shape.FillProperty, ToneBrush(diagnostics[index].Tone));
            }

            return;
        }

        _diagnosticSignature = signature;
        _diagnosticChips.Clear();
        DiagnosticsGrid.Children.Clear();
        DiagnosticsGrid.ColumnDefinitions.Clear();
        DiagnosticsGrid.RowDefinitions.Clear();

        if (diagnostics.Count == 0)
        {
            LayoutItems(DiagnosticsGrid, new List<FrameworkElement>
            {
                BuildChip(Loc.T("system_info_unknown"), Loc.T("common_na"), SystemTone.Unknown).Root,
            }, 3, SectionGap, ChipHeight);
            return;
        }

        var chips = new List<FrameworkElement>(diagnostics.Count);
        foreach (var item in diagnostics)
        {
            var chip = BuildChip(item.Label, item.Value, item.Tone);
            _diagnosticChips.Add(chip);
            chips.Add(chip.Root);
        }

        LayoutItems(DiagnosticsGrid, chips, 3, SectionGap, ChipHeight);
    }

    private void UpdateSensors(SensorSnapshot sensors)
    {
        _sensors = sensors;
        _cpuLoad = SetPercent(CpuLoadPercent, sensors, SensorIds.CpuUsage);
        _gpuLoad = SetPercent(GpuLoadPercent, sensors, SensorIds.GpuUsage);
        _memoryUsage = SetPercent(MemoryUsagePercent, sensors, SensorIds.RamPercent);
        ApplyBars();

        SetTile(_cpuTileValues, "power", Watts(sensors, SensorIds.CpuPower));
        SetTile(_cpuTileValues, "temp", Celsius(sensors, SensorIds.CpuTemperature));
        SetTile(_cpuTileValues, "clock", Megahertz(sensors, SensorIds.CpuClock));

        SetTile(_gpuTileValues, "power", Watts(sensors, SensorIds.GpuPower));
        SetTile(_gpuTileValues, "temp", Celsius(sensors, SensorIds.GpuTemperature));
        SetTile(_gpuTileValues, "clock", Megahertz(sensors, SensorIds.GpuClock));
        SetTile(_gpuTileValues, "vram", Gigabytes(sensors, SensorIds.VramUsed));

        SetTile(_memoryTileValues, "used", Gigabytes(sensors, SensorIds.RamUsed));
        SetTile(_memoryTileValues, "available", Gigabytes(sensors, SensorIds.RamAvailable));
    }

    private void ApplyBars()
    {
        SetBar(CpuBar, CpuBarFill, _cpuLoad);
        SetBar(GpuBar, GpuBarFill, _gpuLoad);
        SetBar(MemoryBar, MemoryBarFill, _memoryUsage);
    }

    private static void SetBar(Grid track, Border fill, double fraction)
    {
        var width = track.ActualWidth;
        if (width <= 0 || double.IsNaN(width))
        {
            fill.Width = 0;
            return;
        }

        fill.Width = Math.Round(width * Math.Clamp(fraction, 0, 1));
    }

    private async void CopyInfo_Click(object sender, RoutedEventArgs e)
    {
        if (_model is null)
        {
            return;
        }

        try
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            var versionText = version is null
                ? string.Empty
                : $"{version.Major}.{version.Minor}.{version.Build}";
            var rows = _diagnostics.Select(item => new SystemReportRow(item.Label, item.Value)).ToArray();
            var report = SystemReportBuilder.Build(
                versionText,
                _model.Info,
                _model.Security,
                _model.Features,
                _model.Software,
                _sensors,
                rows,
                DateTimeOffset.Now);
            Clipboard.SetText(report);
            CopyInfoLabel.Text = Loc.T("system_copied");
        }
        catch
        {
            CopyInfoLabel.Text = Loc.T("system_copy_failed");
        }

        _copyTimer.Stop();
        _copyTimer.Start();
    }

    private StatTile BuildTile(string key, SystemGlyph glyph, string labelKey)
    {
        var root = new Border { Style = (Style)FindResource("SystemTile") };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = SystemVisuals.Icon(glyph, 11);
        icon.VerticalAlignment = VerticalAlignment.Center;
        top.Children.Add(icon);
        var labelHost = new Viewbox
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Height = 16,
            Margin = new Thickness(8, 0, 0, 0),
        };
        var label = new TextBlock
        {
            Text = Loc.T(labelKey),
            Style = (Style)FindResource("SystemTileLabel"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        labelHost.Child = label;
        Grid.SetColumn(labelHost, 1);
        top.Children.Add(labelHost);
        grid.Children.Add(top);

        var value = new TextBlock
        {
            Style = (Style)FindResource("SystemTileValue"),
        };
        var valueHost = new Viewbox
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Height = 21,
            Margin = new Thickness(0, 2, 0, 0),
            Child = value,
        };
        root.SizeChanged += (_, _) =>
            valueHost.MaxWidth = Math.Max(0, root.ActualWidth - 24);
        Grid.SetRow(valueHost, 1);
        grid.Children.Add(valueHost);

        root.Child = grid;
        return new StatTile(key, root, value);
    }

    private FrameworkElement BuildFeatureRow(string label, FeatureStatus status)
    {
        var root = new Border
        {
            Style = (Style)FindResource("SystemTile"),
            Height = 33,
            Padding = new Thickness(0),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new TextBlock
        {
            Text = label,
            Style = (Style)FindResource("SystemChipText"),
            Margin = new Thickness(13, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = label,
        };
        grid.Children.Add(text);

        var dot = SystemVisuals.Dot(status == FeatureStatus.On ? "SystemSuccessBrush" : "SystemDimBrush");
        dot.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(dot, 1);
        grid.Children.Add(dot);

        root.Child = grid;
        return root;
    }

    private FrameworkElement BuildSoftwareCard(SoftwareComponentInfo component)
    {
        var (labelKey, glyph) = SoftwareMeta(component.Id);
        var (statusKey, tone) = SoftwareStatus(component);

        var root = new Border
        {
            Style = (Style)FindResource("SystemTile"),
            Height = SoftwareCardHeight,
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = SystemVisuals.Icon(glyph, 11);
        icon.VerticalAlignment = VerticalAlignment.Center;
        top.Children.Add(icon);
        var label = new TextBlock
        {
            Text = Loc.T(labelKey),
            Style = (Style)FindResource("SystemTileLabel"),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = Loc.T(labelKey),
        };
        top.Children.Add(label);
        grid.Children.Add(top);

        var value = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(component.Version) ? Loc.T("common_na") : component.Version!,
            Style = (Style)FindResource("SystemTileValue"),
            Margin = new Thickness(0, 1, 0, 0),
        };
        Grid.SetRow(value, 1);
        grid.Children.Add(value);

        var status = new TextBlock
        {
            Text = Loc.T(statusKey),
            Style = (Style)FindResource("SystemTileLabel"),
            Margin = new Thickness(0, 1, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = component.Detail,
        };
        Grid.SetRow(status, 2);
        grid.Children.Add(status);

        var dot = SystemVisuals.Dot(ToneBrush(tone));
        dot.VerticalAlignment = VerticalAlignment.Top;
        dot.Margin = new Thickness(9, 5, 0, 0);
        Grid.SetColumn(dot, 1);
        grid.Children.Add(dot);

        root.Child = grid;
        return root;
    }

    private FrameworkElement BuildDiskCard(StorageInfo disk)
    {
        var root = new Border
        {
            Style = (Style)FindResource("SystemTile"),
            Height = 88,
            Padding = new Thickness(14, 10, 14, 10),
        };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = SystemVisuals.Icon(SystemGlyph.Storage, 11);
        icon.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(icon);
        var model = new TextBlock
        {
            Text = disk.Name,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(9, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = disk.Name,
        };
        model.SetResourceReference(TextBlock.ForegroundProperty, "SystemHeadingBrush");
        Grid.SetColumn(model, 1);
        header.Children.Add(model);
        var type = new TextBlock
        {
            Text = disk.Type ?? Loc.T("common_na"),
            FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"),
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = disk.Note,
        };
        type.SetResourceReference(TextBlock.ForegroundProperty, "SystemDimBrush");
        Grid.SetColumn(type, 2);
        header.Children.Add(type);
        grid.Children.Add(header);

        var bar = new Grid { Height = 6, Margin = new Thickness(0, 9, 0, 0) };
        var track = new Border { CornerRadius = new CornerRadius(3) };
        track.SetResourceReference(Border.BackgroundProperty, "SystemCardBrush");
        bar.Children.Add(track);
        var fill = new Border
        {
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0,
        };
        fill.SetResourceReference(Border.BackgroundProperty, "SystemAccentBrush");
        bar.Children.Add(fill);
        Grid.SetRow(bar, 1);
        grid.Children.Add(bar);

        var used = disk.UsedBytes;
        var fraction = disk.TotalBytes is > 0 && used is not null
            ? Math.Clamp(used.Value / (double)disk.TotalBytes.Value, 0, 1)
            : 0;
        bar.SizeChanged += (_, _) =>
        {
            if (bar.ActualWidth > 0)
            {
                fill.Width = Math.Round(bar.ActualWidth * fraction);
            }
        };

        var values = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        values.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        values.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var usage = new TextBlock
        {
            Text = Loc.F("system_storage_used_of",
                SystemInfoFormat.FormatBytes(used) ?? Loc.T("common_na"),
                SystemInfoFormat.FormatBytes(disk.TotalBytes) ?? Loc.T("common_na")),
            Style = (Style)FindResource("SystemTileLabel"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        values.Children.Add(usage);
        var percent = new TextBlock
        {
            Text = disk.TotalBytes is > 0 && used is not null
                ? (fraction * 100).ToString("0", Culture) + "%"
                : Loc.T("common_na"),
            FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"),
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        percent.SetResourceReference(TextBlock.ForegroundProperty, "SystemHeadingBrush");
        Grid.SetColumn(percent, 1);
        values.Children.Add(percent);
        Grid.SetRow(values, 2);
        grid.Children.Add(values);

        root.Child = grid;
        return root;
    }

    private ChipVisual BuildChip(string label, string value, SystemTone tone)
    {
        var root = new Border
        {
            Style = (Style)FindResource("SystemTile"),
            Height = ChipHeight,
            Padding = new Thickness(12, 0, 12, 0),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labelText = new TextBlock
        {
            Text = label,
            Style = (Style)FindResource("SystemChipText"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = label,
        };
        grid.Children.Add(labelText);

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var valueText = new TextBlock
        {
            Text = value,
            Style = (Style)FindResource("SystemChipText"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        right.Children.Add(valueText);
        var dot = SystemVisuals.Dot(ToneBrush(tone));
        dot.Margin = new Thickness(9, 0, 0, 0);
        right.Children.Add(dot);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        root.Child = grid;
        return new ChipVisual(root, valueText, dot);
    }

    private static void LayoutItems(
        Grid host,
        IReadOnlyList<FrameworkElement> items,
        int columns,
        double gap,
        double rowHeight = double.NaN)
    {
        host.Children.Clear();
        host.ColumnDefinitions.Clear();
        host.RowDefinitions.Clear();
        if (items.Count == 0)
        {
            return;
        }

        var rows = (items.Count + columns - 1) / columns;
        for (var column = 0; column < columns; column++)
        {
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (column < columns - 1)
            {
                host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(gap) });
            }
        }

        for (var row = 0; row < rows; row++)
        {
            host.RowDefinitions.Add(new RowDefinition
            {
                Height = double.IsNaN(rowHeight)
                    ? new GridLength(1, GridUnitType.Star)
                    : new GridLength(rowHeight),
            });
            if (row < rows - 1)
            {
                host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(gap) });
            }
        }

        for (var index = 0; index < items.Count; index++)
        {
            Grid.SetColumn(items[index], (index % columns) * 2);
            Grid.SetRow(items[index], (index / columns) * 2);
            host.Children.Add(items[index]);
        }
    }

    private static void SetTile(IReadOnlyDictionary<string, TextBlock> tiles, string key, string value)
    {
        if (tiles.TryGetValue(key, out var text))
        {
            text.Text = value;
        }
    }

    private static double SetPercent(TextBlock text, SensorSnapshot sensors, string id)
    {
        if (sensors.TryGet(id, out var value) && value.Available && double.IsFinite(value.Value))
        {
            var percent = Math.Clamp(value.Value, 0, 100);
            text.Text = percent.ToString("0", Culture) + "%";
            return percent / 100.0;
        }

        text.Text = Loc.T("common_na");
        return 0;
    }

    private static string Watts(SensorSnapshot sensors, string id)
    {
        if (!TryValue(sensors, id, out var value))
        {
            return Loc.T("common_na");
        }

        return value.ToString("0", Culture) + " W";
    }

    private static string Celsius(SensorSnapshot sensors, string id)
        => TryValue(sensors, id, out var value)
            ? value.ToString("0", Culture) + " °C"
            : Loc.T("common_na");

    private static string Megahertz(SensorSnapshot sensors, string id)
        => TryValue(sensors, id, out var value)
            ? value.ToString("0", Culture) + " MHz"
            : Loc.T("common_na");

    private static string Gigabytes(SensorSnapshot sensors, string id)
        => TryValue(sensors, id, out var value)
            ? (value / 1024.0).ToString("0.0", Culture) + " GB"
            : Loc.T("common_na");

    private static bool TryValue(SensorSnapshot sensors, string id, out double value)
    {
        if (sensors.TryGet(id, out var sensor) && sensor.Available && double.IsFinite(sensor.Value))
        {
            value = sensor.Value;
            return true;
        }

        value = double.NaN;
        return false;
    }

    private static string ProcessorSubtitle(IReadOnlyList<SystemInfoItem> items)
    {
        var cores = items.Value("cpu.cores");
        var threads = items.Value("cpu.threads");
        return cores is not null && threads is not null
            ? Loc.F("system_cores_threads", cores, threads)
            : cores is not null
                ? Loc.F("system_cores_only", cores)
                : threads is not null
                    ? Loc.F("system_threads_only", threads)
                    : Loc.T("system_info_unknown");
    }

    private static string BoardTitleText(HardwareBrand brand, string? vendor, string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return BrandCatalog.DisplayName(brand) ?? vendor ?? Loc.T("system_info_unknown");
        }

        var name = BrandCatalog.DisplayName(brand);
        if (name is not null && !model.Contains(name, StringComparison.OrdinalIgnoreCase))
        {
            return name + " " + model;
        }

        return model;
    }

    /// <summary>Compacts "64 GB (2 × 32 GB) 4800 MT/s" to the reference form "64GB (2x32GB) 4800MT/s".</summary>
    private static string CompactMemoryTitle(
        string? total, string? configuration, string? speed, string unknown)
    {
        var title = total?.Replace(" GB", "GB").Replace(" TB", "TB") ?? unknown;
        if (!string.IsNullOrWhiteSpace(configuration))
        {
            title += " (" + configuration
                .Replace(" × ", "x")
                .Replace(" GB", "GB")
                .Replace(" TB", "TB") + ")";
        }

        if (!string.IsNullOrWhiteSpace(speed))
        {
            title += " " + speed.Replace(" MT/s", "MT/s");
        }

        return title;
    }

    private static string OnOff(bool? value)
        => value switch
        {
            true => Loc.T("common_on"),
            false => Loc.T("common_off"),
            _ => Loc.T("common_unknown"),
        };

    private static string VbsText(int? status)
        => status switch
        {
            2 => Loc.T("common_running"),
            1 => Loc.T("common_enabled"),
            0 => Loc.T("common_off"),
            _ => Loc.T("common_unknown"),
        };

    private static (string LabelKey, SystemGlyph Glyph) SoftwareMeta(string id)
        => id switch
        {
            "pawnio" => ("system_software_pawnio", SystemGlyph.Chip),
            "broker" => ("system_software_broker", SystemGlyph.Monitor),
            "nvml" => ("system_software_nvml", SystemGlyph.Pulse),
            "lhm" => ("system_software_lhm", SystemGlyph.Layers),
            _ => (id, SystemGlyph.Layers),
        };

    private static (string StatusKey, SystemTone Tone) SoftwareStatus(SoftwareComponentInfo component)
    {
        if (component.Id == "broker")
        {
            return component.State switch
            {
                SoftwareComponentState.Running => ("system_status_running", SystemTone.Good),
                SoftwareComponentState.Installed => ("system_status_installed", SystemTone.Warn),
                SoftwareComponentState.Missing => ("system_status_not_installed", SystemTone.Unknown),
                _ => ("system_status_unknown", SystemTone.Unknown),
            };
        }

        return component.State switch
        {
            SoftwareComponentState.Available => ("system_status_available", SystemTone.Good),
            SoftwareComponentState.Running => ("system_status_running", SystemTone.Good),
            SoftwareComponentState.Bundled => ("system_status_bundled", SystemTone.Good),
            SoftwareComponentState.Installed => ("system_status_installed", SystemTone.Good),
            SoftwareComponentState.AccessDenied => ("system_status_access_denied", SystemTone.Warn),
            SoftwareComponentState.Missing => ("system_status_not_installed", SystemTone.Unknown),
            _ => ("system_status_unknown", SystemTone.Unknown),
        };
    }

    private static string ToneBrush(SystemTone tone)
        => tone switch
        {
            SystemTone.Good => "SystemSuccessBrush",
            SystemTone.Warn => "SystemWarnBrush",
            _ => "SystemDimBrush",
        };

    private sealed record StatTile(string Key, Border Root, TextBlock Value);

    private sealed record ChipVisual(Border Root, TextBlock Value, Ellipse Dot);
}
