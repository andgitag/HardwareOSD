using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HardwareOSD.App.Localization;
using HardwareOSD.App.Overlay;
using HardwareOSD.App.Services;
using HardwareOSD.Core.Config;
using HardwareOSD.Core.Fps;
using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.Hardware.Providers;
using HardwareOSD.Core.Metrics;
using HardwareOSD.Core.Overlay;
using HardwareOSD.Core.TileOsd;
using Microsoft.Win32;

namespace HardwareOSD.App.Views;

public partial class ClassicRowsWindow : Window
{
    private readonly AppRuntime _runtime;
    private readonly DispatcherTimer _previewTimer;
    private readonly Stack<OsdProfile> _undo = new();
    private readonly Stack<OsdProfile> _redo = new();
    private readonly Dictionary<string, TextBox> _styleColorBoxes = new(StringComparer.Ordinal);
    private AppSettings _draft;
    private OsdProfile _profile;
    private OsdElement? _selected;
    private TileWidgetRef? _selectedTile;
    private bool _loading;
    private OsdProfile? _editSnapshot;
    private Point _dragStart;
    private OsdElement? _dragElement;

    public ClassicRowsWindow(AppRuntime runtime)
        : this(runtime, null)
    {
    }

    public ClassicRowsWindow(AppRuntime runtime, string? profileId)
    {
        _runtime = runtime;
        _draft = runtime.Settings.Clone();
        var start = profileId is null ? null : _draft.Osd.FindProfile(profileId);
        if (start is null || start.IsTile)
        {
            start = _draft.Osd.ActiveProfile is { IsTile: false } active
                ? active
                : _draft.Osd.Profiles.FirstOrDefault(profile => !profile.IsTile);
        }

        _profile = (start ?? _draft.Osd.Profiles[0]).Clone();
        InitializeComponent();
        _previewTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(700),
        };
        _previewTimer.Tick += (_, _) => UpdatePreview();
        Loaded += (_, _) =>
        {
            BuildAddKindChoices();
            BuildStyleChoices();
            BuildTileFamilyChoices();
            BuildRootLayoutChoices();
            RefreshFromProfile(selectElementId: null);
            _previewTimer.Start();
        };
        Closed += (_, _) => _previewTimer.Stop();
    }

    private void BuildRootLayoutChoices()
    {
        RootLayoutCombo.Items.Clear();
        RootLayoutCombo.Items.Add(new ComboBoxItem
        {
            Content = Loc.T("osd_root_rows"),
            Tag = OsdGroupLayout.Rows,
        });
        RootLayoutCombo.Items.Add(new ComboBoxItem
        {
            Content = Loc.T("osd_root_columns"),
            Tag = OsdGroupLayout.Columns,
        });
    }

    private void RootLayoutCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || RootLayoutCombo.SelectedItem is not ComboBoxItem item ||
            item.Tag is not OsdGroupLayout layout || layout == _profile.RootLayout)
        {
            return;
        }

        PushUndo();
        _profile.RootLayout = layout;
        _profile.FollowMetricPreferences = false;
        UpdatePreview();
        StatusText.Text = Loc.T("osd_root_layout_changed");
    }

    private void BuildAddKindChoices()
    {
        AddKindCombo.Items.Clear();
        foreach (var kind in Enum.GetValues<OsdElementKind>())
        {
            AddKindCombo.Items.Add(new ComboBoxItem
            {
                Content = KindName(kind),
                Tag = kind,
            });
        }

        AddKindCombo.SelectedIndex = 0;
    }

    private void BuildStyleChoices()
    {
        StyleCombo.Items.Clear();
        foreach (var style in Enum.GetValues<OsdStyle>())
        {
            StyleCombo.Items.Add(new ComboBoxItem { Content = StyleName(style), Tag = style });
        }
    }

    private void BuildTileFamilyChoices()
    {
        TileFamilyCombo.Items.Clear();
        TileFamilyCombo.Items.Add("All designs");
        foreach (var family in TileDesignCatalog.Families)
        {
            TileFamilyCombo.Items.Add(family);
        }

        TileFamilyCombo.SelectedIndex = 0;
    }

    private void RefreshTilePanel()
    {
        var isTile = _profile.IsTile;
        TileDesignPanel.Visibility = isTile ? Visibility.Visible : Visibility.Collapsed;
        StyleSectionPanel.Visibility = isTile ? Visibility.Collapsed : Visibility.Visible;
        if (!isTile)
        {
            return;
        }

        var family = TileFamilyCombo.SelectedItem as string;
        var designs = string.IsNullOrWhiteSpace(family) || family == "All designs"
            ? TileDesignCatalog.Entries
            : TileDesignCatalog.Entries.Where(entry =>
                string.Equals(entry.Family, family, StringComparison.OrdinalIgnoreCase)).ToList();
        TileDesignList.Items.Clear();
        foreach (var entry in designs)
        {
            var card = new StackPanel { Orientation = Orientation.Horizontal };
            card.Children.Add(new Image
            {
                Source = TileThumbnailFactory.Get(entry.Id, 150, 96),
                Width = 150,
                Height = 96,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 8, 0),
            });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Width = 170 };
            text.Children.Add(new TextBlock
            {
                Text = entry.Name,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            text.Children.Add(new TextBlock
            {
                Text = "Tile · " + entry.Family + " · " + entry.Width.ToString("0") + "×" +
                       entry.Height.ToString("0"),
                Style = (Style)FindResource("Caption"),
                Margin = new Thickness(0, 3, 0, 0),
            });
            card.Children.Add(text);
            TileDesignList.Items.Add(new ListBoxItem
            {
                Content = card,
                Tag = entry.Id,
                Padding = new Thickness(4),
            });
        }

        var selected = TileDesignList.Items.OfType<ListBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, _profile.DesignId,
                StringComparison.Ordinal));
        TileDesignList.SelectedItem = selected;
        var current = TileDesignCatalog.FindEntry(_profile.DesignId);
        TileDesignInfo.Text = current is null
            ? "No design selected"
            : $"{current.Name} — Tile layout, {current.Width:0} × {current.Height:0} px, " +
              $"{current.TopTiles} top-level tiles, {current.Nodes} nodes";

        TileSettingsPanel.Children.Clear();
        AddTileNumberRow(TileSettingsPanel, "Scale", 0.5, 2, () => _profile.Tile.Scale,
            value => _profile.Tile.Scale = value);
        AddTileNumberRow(TileSettingsPanel, "Opacity", 0, 1, () => _profile.Tile.Opacity,
            value => _profile.Tile.Opacity = value);
    }

    private void TileFamilyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
        {
            RefreshTilePanel();
        }
    }

    private void TileDesignList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TileDesignList.SelectedItem is not ListBoxItem item ||
            item.Tag is not string designId ||
            string.Equals(designId, _profile.DesignId, StringComparison.Ordinal))
        {
            return;
        }

        var entry = TileDesignCatalog.FindEntry(designId);
        if (entry is null)
        {
            return;
        }

        PushUndo();
        _profile.Library = OsdDesignLibrary.CapFrameX;
        _profile.Layout = OsdProfileLayout.Tile;
        _profile.DesignId = designId;
        _profile.FollowMetricPreferences = false;
        _profile.Tile = new TileOverrideSettings();
        _profile.StyleSettings = OsdSettings.TileStyleSettingsFor(entry);
        _selectedTile = null;
        _selected = null;
        RefreshFromProfile(null, refreshProfileCombo: false);
        StatusText.Text = Loc.F("osd_tile_selected", entry.Name);
    }

    private void AddTileNumberRow(Panel panel, string label, double min, double max,
        Func<double> get, Action<double> set)
    {
        var grid = NewFieldGrid();
        grid.Children.Add(new TextBlock { Text = label, Style = FieldLabelStyle() });
        var text = new TextBox
        {
            Text = get().ToString("0.##", CultureInfo.InvariantCulture),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(text, 1);
        Grid.SetColumnSpan(text, 2);
        text.GotFocus += (_, _) => BeginEditSnapshot();
        text.LostFocus += (_, _) =>
        {
            if (!double.TryParse(text.Text, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out var value) &&
                !double.TryParse(text.Text, NumberStyles.Float, CultureInfo.CurrentCulture,
                    out value))
            {
                text.Text = get().ToString("0.##", CultureInfo.InvariantCulture);
                return;
            }

            PushUndo();
            set(Math.Clamp(value, min, max));
            text.Text = get().ToString("0.##", CultureInfo.InvariantCulture);
            UpdatePreview();
        };
        grid.Children.Add(text);
        panel.Children.Add(grid);
    }

    private void RefreshFromProfile(string? selectElementId, bool refreshProfileCombo = true)
    {
        _loading = true;
        try
        {
            ProfileNameBox.Text = _profile.Name;
            SelectChoice(StyleCombo, _profile.Style);
            RootLayoutCombo.Visibility = _profile.IsTile ? Visibility.Collapsed : Visibility.Visible;
            if (!_profile.IsTile)
            {
                SelectChoice(RootLayoutCombo, _profile.RootLayout);
            }

            BuildStylePanel();
            RefreshTilePanel();
            if (_profile.IsTile)
            {
                _selected = null;
                BuildTileWidgetList();
            }
            else
            {
                _selectedTile = null;
                BuildElementList(selectElementId);
            }

            UpdateUndoButtons();
            if (refreshProfileCombo)
            {
                BuildProfileChoices();
            }
        }
        finally
        {
            _loading = false;
        }

        BuildPropertyPanel();
        UpdatePreview();
    }

    private void BuildProfileChoices()
    {
        var selectedId = _profile.Id;
        ProfileCombo.Items.Clear();
        ProfileCombo.Items.Add(new ComboBoxItem
        {
            Content = "— " + Loc.T("osd_library_hardwareosd") + " —",
            IsEnabled = false,
            FontWeight = FontWeights.SemiBold,
        });
        foreach (var profile in _draft.Osd.Profiles.Where(p => !p.IsTile &&
                     p.Library == OsdDesignLibrary.HardwareOsd))
        {
            ProfileCombo.Items.Add(new ComboBoxItem
            {
                Content = "Row · " + profile.Name,
                Tag = profile.Id,
            });
        }

        ProfileCombo.Items.Add(new ComboBoxItem
        {
            Content = "— " + Loc.T("osd_library_capframex") + " —",
            IsEnabled = false,
            FontWeight = FontWeights.SemiBold,
        });
        foreach (var profile in _draft.Osd.Profiles.Where(p => !p.IsTile &&
                     p.Library == OsdDesignLibrary.CapFrameX))
        {
            ProfileCombo.Items.Add(new ComboBoxItem
            {
                Content = "Row · " + profile.Name,
                Tag = profile.Id,
            });
        }

        var index = ProfileCombo.Items.OfType<ComboBoxItem>()
            .ToList()
            .FindIndex(item => string.Equals(item.Tag as string, selectedId, StringComparison.Ordinal));
        ProfileCombo.SelectedIndex = index >= 0 ? index : 0;
    }

    private void BuildStylePanel()
    {
        StyleColorPanel.Children.Clear();
        _styleColorBoxes.Clear();
        var style = _profile.StyleSettings;
        AddColorRow(StyleColorPanel, "osd_color_label", () => style.LabelColor, value => style.LabelColor = value);
        AddColorRow(StyleColorPanel, "osd_color_value", () => style.ValueColor, value => style.ValueColor = value);
        AddColorRow(StyleColorPanel, "osd_color_unit", () => style.UnitColor, value => style.UnitColor = value);
        AddColorRow(StyleColorPanel, "osd_color_normal", () => style.NormalColor, value => style.NormalColor = value);
        AddColorRow(StyleColorPanel, "osd_color_warning", () => style.WarningColor, value => style.WarningColor = value);
        AddColorRow(StyleColorPanel, "osd_color_critical", () => style.CriticalColor, value => style.CriticalColor = value);
        AddColorRow(StyleColorPanel, "osd_color_header", () => style.HeaderColor, value => style.HeaderColor = value);
        AddColorRow(StyleColorPanel, "osd_color_text", () => style.TextColor, value => style.TextColor = value);
        AddColorRow(StyleColorPanel, "osd_color_background", () => style.BackgroundColor, value => style.BackgroundColor = value);
        AddColorRow(StyleColorPanel, "osd_color_separator", () => style.SeparatorColor, value => style.SeparatorColor = value);

        StyleTypographyPanel.Children.Clear();
        AddComboRow(StyleTypographyPanel, "osd_font_family",
            new[] { "Segoe UI Variable Text", "Segoe UI", "Bahnschrift", "Consolas", "Cascadia Mono" },
            () => style.FontFamily,
            value => style.FontFamily = value);
        AddNumberRow(StyleTypographyPanel, "osd_font_size", () => style.FontSize,
            value => style.FontSize = Math.Clamp(value, 9, 40));
        AddCheckRow(StyleTypographyPanel, "osd_labels", () => style.ShowLabels, value => style.ShowLabels = value);
        AddCheckRow(StyleTypographyPanel, "osd_units", () => style.ShowUnits, value => style.ShowUnits = value);
        AddCheckRow(StyleTypographyPanel, "osd_separators", () => style.ShowSeparators, value => style.ShowSeparators = value);
        AddCheckRow(StyleTypographyPanel, "osd_sparklines", () => style.ShowSparklines, value => style.ShowSparklines = value);
        AddCheckRow(StyleTypographyPanel, "osd_warnings", () => style.HighlightWarnings, value => style.HighlightWarnings = value);

        StyleSurfacePanel.Children.Clear();
        AddCheckRow(StyleSurfacePanel, "osd_background", () => style.ShowBackground, value => style.ShowBackground = value);
        AddCheckRow(StyleSurfacePanel, "osd_shadow", () => style.Shadow, value => style.Shadow = value);
        AddNumberRow(StyleSurfacePanel, "osd_background_opacity", () => style.BackgroundOpacity,
            value => style.BackgroundOpacity = Math.Clamp((int)Math.Round(value), 0, 100));
        AddNumberRow(StyleSurfacePanel, "osd_corner_radius", () => style.CornerRadius,
            value => style.CornerRadius = Math.Clamp(value, 0, 32));
        AddNumberRow(StyleSurfacePanel, "osd_padding_x", () => style.PaddingX,
            value => style.PaddingX = Math.Clamp(value, 0, 64));
        AddNumberRow(StyleSurfacePanel, "osd_padding_y", () => style.PaddingY,
            value => style.PaddingY = Math.Clamp(value, 0, 64));
        AddNumberRow(StyleSurfacePanel, "osd_row_spacing", () => style.RowSpacing,
            value => style.RowSpacing = Math.Clamp(value, 0, 32));
        AddNumberRow(StyleSurfacePanel, "osd_column_spacing", () => style.ColumnSpacing,
            value => style.ColumnSpacing = Math.Clamp(value, 0, 48));
    }

    private void AddColorRow(Panel panel, string labelKey, Func<string> get, Action<string> set)
    {
        var grid = NewFieldGrid();
        var label = new TextBlock { Text = Loc.T(labelKey), Style = FieldLabelStyle() };
        grid.Children.Add(label);
        var text = new TextBox { Text = get(), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var pick = new Button
        {
            Width = 28,
            Height = 22,
            Margin = new Thickness(5, 0, 0, 0),
            Background = ParseBrush(get()),
            ToolTip = Loc.T("osd_pick_color"),
            Style = (Style)FindResource("FlatButton"),
        };
        Grid.SetColumn(pick, 2);
        grid.Children.Add(pick);
        _styleColorBoxes[labelKey] = text;
        text.GotFocus += (_, _) => BeginEditSnapshot();
        text.LostFocus += (_, _) =>
        {
            if (!HexColor(text.Text))
            {
                text.Text = get();
                return;
            }

            if (!string.Equals(text.Text, get(), StringComparison.OrdinalIgnoreCase))
            {
                set(text.Text);
                text.Text = get();
                CommitEditSnapshot();
                pick.Background = ParseBrush(get());
                UpdatePreview();
            }
        };
        pick.Click += (_, _) =>
        {
            var picked = ColorPickerWindow.Pick(this, Loc.T(labelKey), get());
            if (picked is null)
            {
                return;
            }

            PushUndo();
            set(ColorUtil.ToHex(picked.Value));
            text.Text = get();
            pick.Background = ParseBrush(get());
            UpdatePreview();
        };
        panel.Children.Add(grid);
    }

    private void AddComboRow(Panel panel, string labelKey, IReadOnlyList<string> choices,
        Func<string> get, Action<string> set)
    {
        var grid = NewFieldGrid();
        grid.Children.Add(new TextBlock { Text = Loc.T(labelKey), Style = FieldLabelStyle() });
        var combo = new ComboBox { ItemsSource = choices, SelectedItem = get() };
        Grid.SetColumn(combo, 1);
        Grid.SetColumnSpan(combo, 2);
        combo.SelectionChanged += (_, _) =>
        {
            if (_loading || combo.SelectedItem is not string value ||
                string.Equals(value, get(), StringComparison.Ordinal))
            {
                return;
            }

            PushUndo();
            set(value);
            UpdatePreview();
        };
        grid.Children.Add(combo);
        panel.Children.Add(grid);
    }

    private void AddCheckRow(Panel panel, string labelKey, Func<bool> get, Action<bool> set)
    {
        var check = new CheckBox
        {
            Content = Loc.T(labelKey),
            IsChecked = get(),
            Margin = new Thickness(0, 3, 0, 3),
        };
        check.Checked += (_, _) => ChangeCheck(check, true, set);
        check.Unchecked += (_, _) => ChangeCheck(check, false, set);
        panel.Children.Add(check);
    }

    private void ChangeCheck(CheckBox check, bool value, Action<bool> set)
    {
        if (_loading || check.IsChecked != value)
        {
            return;
        }

        PushUndo();
        set(value);
        UpdatePreview();
    }

    private void AddNumberRow(Panel panel, string labelKey, Func<double> get, Action<double> set)
    {
        var grid = NewFieldGrid();
        grid.Children.Add(new TextBlock { Text = Loc.T(labelKey), Style = FieldLabelStyle() });
        var text = new TextBox
        {
            Text = get().ToString("0.##", CultureInfo.InvariantCulture),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(text, 1);
        Grid.SetColumnSpan(text, 2);
        Grid.SetColumnSpan(text, 2);
        text.GotFocus += (_, _) => BeginEditSnapshot();
        text.LostFocus += (_, _) =>
        {
            if (!double.TryParse(text.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
                !double.TryParse(text.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            {
                text.Text = get().ToString("0.##", CultureInfo.InvariantCulture);
                return;
            }

            set(value);
            text.Text = get().ToString("0.##", CultureInfo.InvariantCulture);
            CommitEditSnapshot();
            UpdatePreview();
        };
        grid.Children.Add(text);
        panel.Children.Add(grid);
    }

    private Grid NewFieldGrid() => new()
    {
        Margin = new Thickness(0, 2, 0, 2),
        ColumnDefinitions =
        {
            new ColumnDefinition { Width = new GridLength(118) },
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
            new ColumnDefinition { Width = GridLength.Auto },
        },
    };

    private Style FieldLabelStyle() => (Style)FindResource("FieldLabel");

    private void RefreshElementList()
    {
        if (_profile.IsTile)
        {
            BuildTileWidgetList();
        }
        else
        {
            BuildElementList(_selected?.Id);
        }
    }

    private void BuildElementList(string? selectElementId)
    {        ElementList.Items.Clear();
        AddElementItems(_profile.Elements, 0);
        if (selectElementId is not null)
        {
            SelectElement(selectElementId);
        }
        else if (ElementList.Items.Count > 0)
        {
            ElementList.SelectedIndex = 0;
        }
    }

    private void AddElementItems(IEnumerable<OsdElement> elements, int depth)
    {
        foreach (var element in elements)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(depth * 15, 0, 0, 0),
            };
            panel.Children.Add(new TextBlock
            {
                Text = KindGlyph(element.Kind),
                Width = 18,
                Foreground = (Brush)FindResource("MutedBrush"),
            });
            panel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(element.DisplayLabel()) ? KindName(element.Kind) : element.DisplayLabel(),
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (element.IsSensorLike && !string.IsNullOrWhiteSpace(element.SensorId))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "  " + element.SensorId,
                    Foreground = (Brush)FindResource("DimBrush"),
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }

            ElementList.Items.Add(new ListBoxItem
            {
                Content = panel,
                Tag = element,
                Padding = new Thickness(5, 3, 5, 3),
            });
            if (element.Kind == OsdElementKind.Group)
            {
                AddElementItems(element.Children, depth + 1);
            }
        }
    }

    private void BuildTileWidgetList()
    {
        ElementList.Items.Clear();
        var design = _profile.TileDesign;
        if (design is null)
        {
            return;
        }

        foreach (var (path, node) in design.Walk())
        {
            if (node.NodeKind == TileNodeKind.Panel)
            {
                continue;
            }

            var label = !string.IsNullOrWhiteSpace(node.Label)
                ? node.Label
                : !string.IsNullOrWhiteSpace(node.Text)
                    ? node.Text
                    : node.Title ?? node.Key ?? node.NodeKind.ToString();
            var key = node.Key ?? node.SecondaryKey ?? string.Empty;
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock
            {
                Text = TileKindGlyph(node.NodeKind),
                Width = 18,
                Foreground = (Brush)FindResource("MutedBrush"),
            });
            panel.Children.Add(new TextBlock
            {
                Text = label.Length > 42 ? label[..42] : label,
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (key.Length > 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "  " + key,
                    Foreground = (Brush)FindResource("DimBrush"),
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }

            var item = new ListBoxItem
            {
                Content = panel,
                Tag = new TileWidgetRef(path, node),
                Padding = new Thickness(5, 3, 5, 3),
            };
            ElementList.Items.Add(item);
        }

        if (_selectedTile is not null)
        {
            foreach (var item in ElementList.Items.OfType<ListBoxItem>())
            {
                if (item.Tag is TileWidgetRef reference && reference.Path == _selectedTile.Path)
                {
                    ElementList.SelectedItem = item;
                    item.BringIntoView();
                    break;
                }
            }
        }
    }

    private static string TileKindGlyph(TileNodeKind kind) => kind switch
    {
        TileNodeKind.Metric => "M",
        TileNodeKind.Text => "T",
        TileNodeKind.Chart => "≈",
        TileNodeKind.Bar => "▬",
        TileNodeKind.Gauge => "◔",
        TileNodeKind.Image => "▣",
        TileNodeKind.Icon => "◇",
        TileNodeKind.Heatmap => "▦",
        TileNodeKind.ClassicRows => "≡",
        _ => "·",
    };

    private void SelectElement(string id)
    {
        foreach (var item in ElementList.Items.OfType<ListBoxItem>())
        {
            if (item.Tag is OsdElement element && element.Id == id)
            {
                ElementList.SelectedItem = item;
                item.BringIntoView();
                return;
            }
        }
    }

    private void ElementList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var tag = (ElementList.SelectedItem as ListBoxItem)?.Tag;
        if (tag is TileWidgetRef tile)
        {
            _selectedTile = tile;
            _selected = null;
        }
        else
        {
            _selected = tag as OsdElement;
            _selectedTile = null;
        }

        BuildPropertyPanel();
    }

    private void BuildPropertyPanel()
    {
        PropertyPanel.Children.Clear();
        if (_profile.IsTile)
        {
            if (_selectedTile is null)
            {
                PropertyPanel.Children.Add(new TextBlock
                {
                    Text = "Select a tile widget to edit its sensor, label, unit, colours or visibility.",
                    Style = (Style)FindResource("Muted"),
                    TextWrapping = TextWrapping.Wrap,
                });
                return;
            }

            BuildTilePropertyPanel(_selectedTile);
            return;
        }

        if (_selected is null)
        {
            PropertyPanel.Children.Add(new TextBlock
            {
                Text = Loc.T("osd_select_item_hint"),
                Style = (Style)FindResource("Muted"),
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        var element = _selected;
        PropertyPanel.Children.Add(new TextBlock
        {
            Text = KindName(element.Kind),
            Style = (Style)FindResource("SectionTitle"),
            Margin = new Thickness(0, 0, 0, 6),
        });

        if (element.Kind is OsdElementKind.Group or OsdElementKind.GroupHeader
            or OsdElementKind.DeviceHeader or OsdElementKind.CustomText)
        {
            AddElementTextRow("osd_item_name", () => element.Label, value => element.Label = value);
        }

        if (element.Kind is OsdElementKind.CustomText or OsdElementKind.GroupHeader or OsdElementKind.DeviceHeader)
        {
            AddElementTextRow("osd_item_text", () => element.Text, value => element.Text = value);
        }

        if (element.IsSensorLike)
        {
            AddSensorRow(element);
            AddElementTextRow("osd_display_name", () => element.Label, value => element.Label = value);
            AddElementTextRow("osd_unit", () => element.Unit, value => element.Unit = value);
            AddElementTextRow("osd_number_format", () => element.NumberFormat, value => element.NumberFormat = value);
            AddElementNumberRow("osd_decimals", () => element.Decimals, value => element.Decimals = (int)value);
            AddElementTextRow("osd_prefix", () => element.Prefix, value => element.Prefix = value);
            AddElementTextRow("osd_suffix", () => element.Suffix, value => element.Suffix = value);
            AddElementColorRow("osd_value_color", () => element.ValueColor, value => element.ValueColor = value);
            AddElementColorRow("osd_label_color", () => element.LabelColor, value => element.LabelColor = value);
            AddElementCheckRow("osd_auto_color", () => element.AutoColor, value => element.AutoColor = value);
            AddElementNumberRow("osd_warning_threshold", () => element.WarningThreshold ?? 0, value => element.WarningThreshold = value, nullable: true);
            AddElementNumberRow("osd_critical_threshold", () => element.CriticalThreshold ?? 0, value => element.CriticalThreshold = value, nullable: true);
            AddElementChoiceRow("osd_threshold_direction",
                new[] { ("osd_direction_higher", OsdThresholdDirection.HigherIsWorse), ("osd_direction_lower", OsdThresholdDirection.LowerIsWorse) },
                () => element.Direction, value => element.Direction = value);
            AddElementCheckRow("osd_show_graph", () => element.ShowGraph, value => element.ShowGraph = value);
            AddElementCheckRow("osd_show_progress", () => element.ShowProgress, value => element.ShowProgress = value);
            AddElementCheckRow("osd_show_statistics", () => element.ShowStatistics, value => element.ShowStatistics = value);
            AddElementNumberRow("osd_progress_min", () => element.ProgressMin ?? 0, value => element.ProgressMin = value, nullable: true);
            AddElementNumberRow("osd_progress_max", () => element.ProgressMax ?? 0, value => element.ProgressMax = value, nullable: true);
        }

        if (element.Kind is OsdElementKind.Sensor or OsdElementKind.Graph or OsdElementKind.ProgressBar
            or OsdElementKind.CustomValue)
        {
            AddElementCheckRow("osd_show_label", () => element.ShowLabel, value => element.ShowLabel = value);
            AddElementCheckRow("osd_show_value", () => element.ShowValue, value => element.ShowValue = value);
        }

        AddElementNumberRow("osd_font_size_item", () => element.FontSize, value => element.FontSize = value);
        AddElementNumberRow("osd_width", () => element.Width ?? 0, value => element.Width = value, nullable: true);
        AddElementNumberRow("osd_height", () => element.Height ?? 0, value => element.Height = value, nullable: true);
        AddElementNumberRow("osd_indent", () => element.Indent, value => element.Indent = (int)value);
        AddElementNumberRow("osd_margin_top", () => element.MarginTop, value => element.MarginTop = value);
        AddElementNumberRow("osd_margin_bottom", () => element.MarginBottom, value => element.MarginBottom = value);
        AddElementChoiceRow("osd_alignment",
            new[] { ("osd_align_left", OsdTextAlign.Left), ("osd_align_center", OsdTextAlign.Center), ("osd_align_right", OsdTextAlign.Right) },
            () => element.Align, value => element.Align = value);

        if (element.Kind == OsdElementKind.Group)
        {
            AddElementChoiceRow("osd_group_layout",
                new[] { ("osd_group_columns", OsdGroupLayout.Columns), ("osd_group_rows", OsdGroupLayout.Rows) },
                () => element.GroupLayout, value => element.GroupLayout = value);
        }

        AddElementCheckRow("osd_visible", () => element.Visible, value => element.Visible = value);
    }

    private void AddSensorRow(OsdElement element)
    {
        var grid = NewFieldGrid();
        grid.Children.Add(new TextBlock { Text = Loc.T("osd_sensor"), Style = FieldLabelStyle() });
        var combo = new ComboBox { MaxHeight = 260 };
        foreach (var definition in MetricCatalog.All)
        {
            combo.Items.Add(new ComboBoxItem { Content = SensorName(definition.Id), Tag = definition.Id });
        }

        var selected = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals((string)item.Tag, element.SensorId, StringComparison.Ordinal));
        combo.SelectedItem = selected;
        combo.SelectionChanged += (_, _) =>
        {
            if (_loading || combo.SelectedItem is not ComboBoxItem item ||
                item.Tag is not string id || string.Equals(id, element.SensorId, StringComparison.Ordinal))
            {
                return;
            }

            PushUndo();
            var oldDefault = SensorName(element.SensorId);
            element.SensorId = id;
            if (string.IsNullOrWhiteSpace(element.Label) || element.Label == oldDefault)
            {
                element.Label = SensorName(id);
            }

            _profile.FollowMetricPreferences = false;
            BuildElementList(element.Id);
            BuildPropertyPanel();
            UpdatePreview();
        };
        Grid.SetColumn(combo, 1);
        Grid.SetColumnSpan(combo, 2);
        grid.Children.Add(combo);
        PropertyPanel.Children.Add(grid);
        PropertyPanel.Children.Add(new TextBlock
        {
            Text = Loc.F("osd_sensor_id", element.SensorId),
            Style = (Style)FindResource("Caption"),
            Margin = new Thickness(0, 0, 0, 4),
        });
    }

    private void BuildTilePropertyPanel(TileWidgetRef reference)
    {
        var node = reference.Node;
        var path = reference.Path;
        var overrides = _profile.Tile.Ensure(path);
        PropertyPanel.Children.Add(new TextBlock
        {
            Text = node.NodeKind + " · " + path,
            Style = (Style)FindResource("SectionTitle"),
            Margin = new Thickness(0, 0, 0, 4),
        });
        PropertyPanel.Children.Add(new TextBlock
        {
            Text = "Base key: " + (node.Key ?? node.SecondaryKey ?? "none") +
                   (node.NodeKind == TileNodeKind.Panel ? string.Empty : " · design geometry is fixed"),
            Style = (Style)FindResource("Caption"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        });

        AddTileKeyRow(path, () => overrides.Key ?? node.Key ?? string.Empty,
            value => overrides.Key = string.IsNullOrWhiteSpace(value) ? null : value.Trim());
        AddElementTextRow("osd_label", () => overrides.Label ?? node.Label ?? string.Empty,
            value => overrides.Label = string.IsNullOrWhiteSpace(value) ? null : value);
        AddElementTextRow("osd_unit", () => overrides.Unit ?? node.Unit ?? string.Empty,
            value => overrides.Unit = string.IsNullOrWhiteSpace(value) ? null : value);
        AddElementNumberRow("osd_decimals",
            () => overrides.Digits ?? node.Digits ?? 0,
            value => overrides.Digits = (int)Math.Clamp(Math.Round(value), 0, 6));
        AddElementNumberRow("osd_value_size",
            () => overrides.ValueSize ?? node.ValueSize ?? node.Size ?? 14,
            value => overrides.ValueSize = Math.Clamp(value, 6, 96));
        AddElementColorRow("osd_color_value",
            () => overrides.ValueColor ?? node.ValueColor ?? node.Color ?? string.Empty,
            value => overrides.ValueColor = value);
        AddElementColorRow("osd_color_label",
            () => overrides.LabelColor ?? node.LabelColor ?? string.Empty,
            value => overrides.LabelColor = value);
        AddElementCheckRow("behavior_enabled",
            () => overrides.Visible ?? true,
            value => overrides.Visible = value);

        if (node.NodeKind == TileNodeKind.Chart)
        {
            AddElementColorRow("osd_chart_color",
                () => overrides.ChartColor ?? node.Color ?? string.Empty,
                value => overrides.ChartColor = value);
            AddElementNumberRow("osd_chart_thickness",
                () => overrides.ChartThickness ?? 2,
                value => overrides.ChartThickness = Math.Clamp(value, 0.5, 8));
        }

        if (node.NodeKind is TileNodeKind.Bar or TileNodeKind.Gauge)
        {
            AddElementNumberRow("osd_bar_min",
                () => overrides.BarMinimum ?? node.EffectiveMinimum,
                value => overrides.BarMinimum = value);
            AddElementNumberRow("osd_bar_max",
                () => overrides.BarMaximum ?? node.EffectiveMaximum,
                value => overrides.BarMaximum = value);
        }

        var reset = new Button
        {
            Content = "Reset widget overrides",
            Style = (Style)FindResource("FlatButton"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0),
        };
        reset.Click += (_, _) =>
        {
            PushUndo();
            _profile.Tile.Nodes.Remove(path);
            BuildTileWidgetList();
            BuildPropertyPanel();
            UpdatePreview();
        };
        PropertyPanel.Children.Add(reset);
    }

    private void AddTileKeyRow(string path, Func<string> get, Action<string> set)
    {
        var grid = NewFieldGrid();
        grid.Children.Add(new TextBlock { Text = Loc.T("osd_sensor"), Style = FieldLabelStyle() });
        var combo = new ComboBox { IsEditable = true, MaxHeight = 280 };
        var keys = TileSensorKeys();
        foreach (var key in keys)
        {
            combo.Items.Add(key);
        }

        var current = get();
        if (!string.IsNullOrWhiteSpace(current) && !keys.Contains(current))
        {
            combo.Items.Insert(0, current);
        }

        combo.Text = current;
        combo.SelectionChanged += (_, _) =>
        {
            if (_loading || combo.SelectedItem is not string key ||
                string.Equals(key, get(), StringComparison.Ordinal))
            {
                return;
            }

            BeginEditSnapshot();
            set(key);
            CommitEditSnapshot();
            UpdatePreview();
        };
        combo.LostFocus += (_, _) =>
        {
            if (_loading || string.IsNullOrWhiteSpace(combo.Text) ||
                string.Equals(combo.Text, get(), StringComparison.Ordinal))
            {
                return;
            }

            BeginEditSnapshot();
            set(combo.Text.Trim());
            CommitEditSnapshot();
            UpdatePreview();
        };
        Grid.SetColumn(combo, 1);
        Grid.SetColumnSpan(combo, 2);
        grid.Children.Add(combo);
        PropertyPanel.Children.Add(grid);
    }

    private static IReadOnlyList<string> TileSensorKeys()
    {
        var keys = new List<string>
        {
            "fpsCurrent", "fps", "fps1pctLow", "frametimeCurrent", "frametime", "stuttering",
            "gpuLoad", "gpuTemp", "gpuPower", "gpuClock", "gpuMemoryClock", "gpuHotSpot",
            "gpuFan", "gpuFanPercent", "vramUsed", "vramTotal", "vramTemp",
            "cpuLoad", "cpuTemp", "cpuPower", "cpuClock", "ramUsed", "ramTotal", "ramLoad",
            "captureTimer", "gpuName", "gpuNameShort", "cpuName", "cpuNameShort", "gpuDriver",
            "role:cpu-loads/1", "role:cpu-clocks/1", "role:cpu-effective-clocks/1",
            "role:cpu-temperatures/1",
        };
        keys.AddRange(MetricCatalog.All.Select(definition => "sensor:" + definition.Id));
        return keys.Distinct(StringComparer.Ordinal).ToList();
    }

    private void AddElementTextRow(string labelKey, Func<string> get, Action<string> set)
    {
        var grid = NewFieldGrid();
        grid.Children.Add(new TextBlock { Text = Loc.T(labelKey), Style = FieldLabelStyle() });
        var text = new TextBox { Text = get(), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        Grid.SetColumnSpan(text, 2);
        text.GotFocus += (_, _) => BeginEditSnapshot();
        text.LostFocus += (_, _) =>
        {
            if (string.Equals(text.Text, get(), StringComparison.Ordinal))
            {
                return;
            }

            set(text.Text);
            _profile.FollowMetricPreferences = false;
            CommitEditSnapshot();
            RefreshElementList();
            UpdatePreview();
        };
        grid.Children.Add(text);
        PropertyPanel.Children.Add(grid);
    }

    private void AddElementNumberRow(string labelKey, Func<double> get, Action<double> set, bool nullable = false)
    {
        var grid = NewFieldGrid();
        grid.Children.Add(new TextBlock { Text = Loc.T(labelKey), Style = FieldLabelStyle() });
        var text = new TextBox
        {
            Text = get().ToString("0.##", CultureInfo.InvariantCulture),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(text, 1);
        Grid.SetColumnSpan(text, 2);
        text.GotFocus += (_, _) => BeginEditSnapshot();
        text.LostFocus += (_, _) =>
        {
            if (!double.TryParse(text.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
                !double.TryParse(text.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            {
                text.Text = get().ToString("0.##", CultureInfo.InvariantCulture);
                return;
            }

            set(value);
            _profile.FollowMetricPreferences = false;
            CommitEditSnapshot();
            UpdatePreview();
        };
        grid.Children.Add(text);
        PropertyPanel.Children.Add(grid);
    }

    private void AddElementColorRow(string labelKey, Func<string> get, Action<string> set)
    {
        var grid = NewFieldGrid();
        grid.Children.Add(new TextBlock { Text = Loc.T(labelKey), Style = FieldLabelStyle() });
        var text = new TextBox { Text = get(), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var pick = new Button
        {
            Width = 28,
            Height = 22,
            Margin = new Thickness(5, 0, 0, 0),
            Background = ParseBrush(get()),
            Style = (Style)FindResource("FlatButton"),
        };
        Grid.SetColumn(pick, 2);
        grid.Children.Add(pick);
        text.GotFocus += (_, _) => BeginEditSnapshot();
        text.LostFocus += (_, _) =>
        {
            if (!HexColor(text.Text))
            {
                text.Text = get();
                return;
            }

            set(text.Text);
            CommitEditSnapshot();
            pick.Background = ParseBrush(get());
            UpdatePreview();
        };
        pick.Click += (_, _) =>
        {
            var picked = ColorPickerWindow.Pick(this, Loc.T(labelKey), get());
            if (picked is null)
            {
                return;
            }

            PushUndo();
            set(ColorUtil.ToHex(picked.Value));
            text.Text = get();
            pick.Background = ParseBrush(get());
            UpdatePreview();
        };
        PropertyPanel.Children.Add(grid);
    }

    private void AddElementCheckRow(string labelKey, Func<bool> get, Action<bool> set)
    {
        var check = new CheckBox
        {
            Content = Loc.T(labelKey),
            IsChecked = get(),
            Margin = new Thickness(0, 3, 0, 3),
        };
        check.Checked += (_, _) =>
        {
            if (_loading)
            {
                return;
            }

            PushUndo();
            set(true);
            _profile.FollowMetricPreferences = false;
            UpdatePreview();
        };
        check.Unchecked += (_, _) =>
        {
            if (_loading)
            {
                return;
            }

            PushUndo();
            set(false);
            _profile.FollowMetricPreferences = false;
            UpdatePreview();
        };
        PropertyPanel.Children.Add(check);
    }

    private void AddElementChoiceRow<T>(string labelKey, (string Key, T Value)[] choices,
        Func<T> get, Action<T> set) where T : struct, Enum
    {
        var grid = NewFieldGrid();
        grid.Children.Add(new TextBlock { Text = Loc.T(labelKey), Style = FieldLabelStyle() });
        var combo = new ComboBox();
        foreach (var choice in choices)
        {
            combo.Items.Add(new ComboBoxItem { Content = Loc.T(choice.Key), Tag = choice.Value });
        }

        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag is T value && EqualityComparer<T>.Default.Equals(value, get()));
        combo.SelectionChanged += (_, _) =>
        {
            if (_loading || combo.SelectedItem is not ComboBoxItem item || item.Tag is not T value)
            {
                return;
            }

            PushUndo();
            set(value);
            _profile.FollowMetricPreferences = false;
            UpdatePreview();
        };
        Grid.SetColumn(combo, 1);
        Grid.SetColumnSpan(combo, 2);
        grid.Children.Add(combo);
        PropertyPanel.Children.Add(grid);
    }

    private void AddElement_Click(object sender, RoutedEventArgs e)
    {
        if (AddKindCombo.SelectedItem is not ComboBoxItem choice || choice.Tag is not OsdElementKind kind)
        {
            return;
        }

        PushUndo();
        var element = NewElement(kind);
        InsertElement(element);
        _profile.FollowMetricPreferences = false;
        RefreshFromProfile(element.Id, refreshProfileCombo: false);
        StatusText.Text = Loc.T("osd_added");
    }

    private OsdElement NewElement(OsdElementKind kind)
    {
        var firstMetric = MetricCatalog.All.FirstOrDefault(d => d.DefaultVisible) ?? MetricCatalog.All[0];
        var element = new OsdElement
        {
            Kind = kind,
            Label = kind switch
            {
                OsdElementKind.Group => Loc.T("osd_new_group"),
                OsdElementKind.GroupHeader => Loc.T("osd_new_header"),
                OsdElementKind.DeviceHeader => Loc.T("osd_new_device"),
                OsdElementKind.CustomText => Loc.T("osd_custom_text"),
                OsdElementKind.EmptyLine => string.Empty,
                OsdElementKind.Divider => string.Empty,
                _ => SensorName(firstMetric.Id),
            },
            Text = kind == OsdElementKind.CustomText ? Loc.T("osd_custom_text_hint") : string.Empty,
            SensorId = kind is OsdElementKind.Sensor or OsdElementKind.Graph or OsdElementKind.ProgressBar
                or OsdElementKind.CustomValue
                ? firstMetric.Id
                : string.Empty,
            GroupLayout = OsdGroupLayout.Rows,
            ShowGraph = kind == OsdElementKind.Graph,
            ShowProgress = kind == OsdElementKind.ProgressBar,
            AutoColor = _profile.Style == OsdStyle.AdaptiveColors,
            Height = kind == OsdElementKind.EmptyLine ? 8 : kind == OsdElementKind.Divider ? 1 : null,
        };
        return element;
    }

    private void InsertElement(OsdElement element)
    {
        if (_selected is { Kind: OsdElementKind.Group } group)
        {
            group.Children.Add(element);
            return;
        }

        var parent = FindParent(_selected);
        if (parent is not null)
        {
            var index = parent.Children.IndexOf(_selected!);
            parent.Children.Insert(Math.Max(0, index + 1), element);
            return;
        }

        var rootIndex = _selected is null ? _profile.Elements.Count : _profile.Elements.IndexOf(_selected) + 1;
        _profile.Elements.Insert(Math.Clamp(rootIndex, 0, _profile.Elements.Count), element);
    }

    private void DeleteElement_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            return;
        }

        PushUndo();
        var parent = FindParent(_selected);
        if (parent is not null)
        {
            parent.Children.Remove(_selected);
        }
        else
        {
            _profile.Elements.Remove(_selected);
        }

        _profile.FollowMetricPreferences = false;
        _selected = null;
        RefreshFromProfile(null, refreshProfileCombo: false);
        StatusText.Text = Loc.T("osd_deleted");
    }

    private void DuplicateElement_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            return;
        }

        PushUndo();
        var copy = _selected.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        foreach (var child in OsdProfile.Flatten(copy.Children))
        {
            child.Id = Guid.NewGuid().ToString("N");
        }

        copy.Label += " ";
        InsertElement(copy);
        _profile.FollowMetricPreferences = false;
        RefreshFromProfile(copy.Id, refreshProfileCombo: false);
        StatusText.Text = Loc.T("osd_duplicated");
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int delta)
    {
        if (_selected is null)
        {
            return;
        }

        var parent = FindParent(_selected);
        var list = parent?.Children ?? _profile.Elements;
        var index = list.IndexOf(_selected);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= list.Count)
        {
            return;
        }

        PushUndo();
        (list[index], list[target]) = (list[target], list[index]);
        _profile.FollowMetricPreferences = false;
        RefreshFromProfile(_selected.Id, refreshProfileCombo: false);
    }

    private OsdElement? FindParent(OsdElement? element)
    {
        if (element is null)
        {
            return null;
        }

        return FindParent(_profile.Elements, element);
    }

    private static OsdElement? FindParent(IEnumerable<OsdElement> elements, OsdElement target)
    {
        foreach (var element in elements)
        {
            if (element.Children.Contains(target))
            {
                return element;
            }

            var parent = FindParent(element.Children, target);
            if (parent is not null)
            {
                return parent;
            }
        }

        return null;
    }

    private void ElementList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(ElementList);
        _dragElement = (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject))?.Tag as OsdElement;
    }

    private void ElementList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragElement is null)
        {
            return;
        }

        var point = e.GetPosition(ElementList);
        if (Math.Abs(point.X - _dragStart.X) < 5 && Math.Abs(point.Y - _dragStart.Y) < 5)
        {
            return;
        }

        DragDrop.DoDragDrop(ElementList, new DataObject("HardwareOSD.OsdElement", _dragElement),
            DragDropEffects.Move);
    }

    private void ElementList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("HardwareOSD.OsdElement")
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void ElementList_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("HardwareOSD.OsdElement") ||
            e.Data.GetData("HardwareOSD.OsdElement") is not OsdElement dragged ||
            ReferenceEquals(dragged, _selected))
        {
            return;
        }

        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        var target = item?.Tag as OsdElement;
        if (target is null || ReferenceEquals(target, dragged) || IsDescendant(dragged, target))
        {
            return;
        }

        PushUndo();
        RemoveElement(dragged);
        if (target.Kind == OsdElementKind.Group)
        {
            target.Children.Add(dragged);
        }
        else
        {
            var parent = FindParent(target);
            var list = parent?.Children ?? _profile.Elements;
            var index = list.IndexOf(target);
            list.Insert(Math.Clamp(index + 1, 0, list.Count), dragged);
        }

        _profile.FollowMetricPreferences = false;
        RefreshFromProfile(dragged.Id, refreshProfileCombo: false);
        StatusText.Text = Loc.T("osd_moved");
    }

    private static bool IsDescendant(OsdElement ancestor, OsdElement candidate)
        => descendant(ancestor.Children, candidate);

    private static bool descendant(IEnumerable<OsdElement> children, OsdElement candidate)
    {
        foreach (var child in children)
        {
            if (ReferenceEquals(child, candidate) || descendant(child.Children, candidate))
            {
                return true;
            }
        }

        return false;
    }

    private void RemoveElement(OsdElement element)
    {
        var parent = FindParent(element);
        if (parent is not null)
        {
            parent.Children.Remove(element);
        }
        else
        {
            _profile.Elements.Remove(element);
        }
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_undo.Count == 0)
        {
            return;
        }

        _redo.Push(_profile.Clone());
        _profile = _undo.Pop();
        _draft.Osd.Replace(_profile);
        RefreshFromProfile(_selected?.Id, refreshProfileCombo: false);
        StatusText.Text = Loc.T("osd_undo_done");
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (_redo.Count == 0)
        {
            return;
        }

        _undo.Push(_profile.Clone());
        _profile = _redo.Pop();
        _draft.Osd.Replace(_profile);
        RefreshFromProfile(_selected?.Id, refreshProfileCombo: false);
        StatusText.Text = Loc.T("osd_redo_done");
    }

    private void PushUndo()
    {
        PushUndoSnapshot(_profile.Clone());
    }

    private void PushUndoSnapshot(OsdProfile snapshot)
    {
        _undo.Push(snapshot);
        TrimUndo();
        _redo.Clear();
        UpdateUndoButtons();
    }

    private void TrimUndo()
    {
        if (_undo.Count <= 50)
        {
            return;
        }

        var keep = _undo.ToArray().Take(50).ToArray();
        _undo.Clear();
        for (var i = keep.Length - 1; i >= 0; i--)
        {
            _undo.Push(keep[i]);
        }
    }

    private void BeginEditSnapshot() => _editSnapshot ??= _profile.Clone();

    private void CommitEditSnapshot()
    {
        if (_editSnapshot is null)
        {
            return;
        }

        PushUndoSnapshot(_editSnapshot);
        _editSnapshot = null;
    }

    private void UpdateUndoButtons()
    {
        if (UndoButton is not null)
        {
            UndoButton.IsEnabled = _undo.Count > 0;
            RedoButton.IsEnabled = _redo.Count > 0;
        }
    }

    private void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProfileCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string id)
        {
            return;
        }

        var profile = _draft.Osd.FindProfile(id);
        if (profile is null || profile.Id == _profile.Id)
        {
            return;
        }

        _profile = profile.Clone();
        _draft.Osd.ActiveProfileId = profile.Id;
        _undo.Clear();
        _redo.Clear();
        _selected = null;
        RefreshFromProfile(null);
        StatusText.Text = Loc.F("osd_profile_loaded", profile.Name);
    }

    private void StyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || StyleCombo.SelectedItem is not ComboBoxItem item || item.Tag is not OsdStyle style ||
            style == _profile.Style)
        {
            return;
        }

        PushUndo();
        _profile.Style = style;
        _profile.StyleSettings = style == OsdStyle.Classic
            ? OsdStyleSettings.FromOverlay(_draft.Overlay)
            : OsdStyleSettings.CreateDefault(style);
        _profile.FollowMetricPreferences = false;
        RefreshFromProfile(_selected?.Id, refreshProfileCombo: false);
        UpdatePreview();
    }

    private void RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = ProfileNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || name == _profile.Name)
        {
            return;
        }

        PushUndo();
        _profile.Name = name;
        _draft.Osd.Replace(_profile);
        BuildProfileChoices();
        StatusText.Text = Loc.T("osd_renamed");
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        var profile = OsdSettings.CreateProfileForStyle(_profile.Style, _draft.Metrics, _draft.Fps.Enabled,
            Loc.F("osd_new_profile_name", _draft.Osd.Profiles.Count + 1));
        _draft.Osd.Profiles.Add(profile);
        _draft.Osd.ActiveProfileId = profile.Id;
        _profile = profile.Clone();
        _undo.Clear();
        _redo.Clear();
        RefreshFromProfile(null);
        StatusText.Text = Loc.T("osd_profile_created");
    }

    private void DuplicateProfile_Click(object sender, RoutedEventArgs e)
    {
        var copy = _profile.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = Loc.F("osd_copy_name", _profile.Name);
        foreach (var element in OsdProfile.Flatten(copy.Elements))
        {
            element.Id = Guid.NewGuid().ToString("N");
        }

        _draft.Osd.Profiles.Add(copy);
        _draft.Osd.ActiveProfileId = copy.Id;
        _profile = copy.Clone();
        _undo.Clear();
        _redo.Clear();
        RefreshFromProfile(null);
        StatusText.Text = Loc.T("osd_profile_duplicated");
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_draft.Osd.Profiles.Count <= 1)
        {
            StatusText.Text = Loc.T("osd_need_one_profile");
            return;
        }

        var index = _draft.Osd.Profiles.FindIndex(p => p.Id == _profile.Id);
        _draft.Osd.Profiles.RemoveAll(p => p.Id == _profile.Id);
        _profile = _draft.Osd.Profiles[Math.Clamp(index, 0, _draft.Osd.Profiles.Count - 1)].Clone();
        _draft.Osd.ActiveProfileId = _profile.Id;
        _undo.Clear();
        _redo.Clear();
        RefreshFromProfile(null);
        StatusText.Text = Loc.T("osd_profile_deleted");
    }

    private void DefaultProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = Loc.F("osd_default_profile_name", StyleName(_profile.Style));
        var profile = OsdSettings.CreateProfileForStyle(_profile.Style, _draft.Metrics, _draft.Fps.Enabled, name);
        _draft.Osd.Profiles.Add(profile);
        _draft.Osd.ActiveProfileId = profile.Id;
        _profile = profile.Clone();
        _undo.Clear();
        _redo.Clear();
        RefreshFromProfile(null);
        StatusText.Text = Loc.T("osd_default_profile_created");
    }

    private void ResetStyle_Click(object sender, RoutedEventArgs e)
    {
        PushUndo();
        _profile.StyleSettings = _profile.Style == OsdStyle.Classic
            ? OsdStyleSettings.FromOverlay(_draft.Overlay)
            : OsdStyleSettings.CreateDefault(_profile.Style);
        RefreshFromProfile(_selected?.Id, refreshProfileCombo: false);
        StatusText.Text = Loc.T("osd_style_reset");
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!_profile.IsTile && _profile.Elements.Count == 0)
        {
            StatusText.Text = Loc.T("osd_empty_profile");
            return;
        }

        SyncProfileToDraft();
        var settings = _draft.Clone();
        if (_profile.Style == OsdStyle.Classic)
        {
            _profile.StyleSettings.ApplyToOverlay(settings.Overlay);
        }

        settings.Osd.Replace(_profile);
        settings.Osd.ActiveProfileId = _profile.Id;
        settings.Normalize();
        _runtime.ApplySettings(settings, persist: true);
        _draft = settings.Clone();
        _profile = (_draft.Osd.ActiveProfile ?? _draft.Osd.Profiles[0]).Clone();
        RefreshFromProfile(_selected?.Id);
        StatusText.Text = Loc.T("osd_applied");
    }

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        _draft = _runtime.Settings.Clone();
        _profile = (_draft.Osd.ActiveProfile ?? _draft.Osd.Profiles[0]).Clone();
        _undo.Clear();
        _redo.Clear();
        _selected = null;
        RefreshFromProfile(null);
        StatusText.Text = Loc.T("osd_reverted");
    }

    private void ImportProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = Loc.T("osd_profile_filter"),
            DefaultExt = ".json",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var profile = OsdProfileSerializer.Deserialize(File.ReadAllText(dialog.FileName), out var error);
        if (profile is null)
        {
            StatusText.Text = Loc.F("osd_import_failed", error ?? "invalid");
            return;
        }

        if (_draft.Osd.Profiles.Any(p => p.Id == profile.Id))
        {
            profile.Id = Guid.NewGuid().ToString("N");
            foreach (var element in OsdProfile.Flatten(profile.Elements))
            {
                element.Id = Guid.NewGuid().ToString("N");
            }
        }

        _draft.Osd.Profiles.Add(profile);
        _draft.Osd.ActiveProfileId = profile.Id;
        _profile = profile.Clone();
        _undo.Clear();
        _redo.Clear();
        RefreshFromProfile(null);
        StatusText.Text = Loc.T("osd_imported");
    }

    private void ExportProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = Loc.T("osd_profile_filter"),
            DefaultExt = ".json",
            FileName = SafeFileName(_profile.Name) + ".osdprofile.json",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        File.WriteAllText(dialog.FileName, OsdProfileSerializer.Serialize(_profile));
        StatusText.Text = Loc.T("osd_exported");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void SyncProfileToDraft()
    {
        _draft.Osd.Replace(_profile);
        _draft.Osd.ActiveProfileId = _profile.Id;
    }

    private void UpdatePreview()
    {
        if (PreviewSurface is null)
        {
            return;
        }

        var settings = _draft.Clone();
        var previewProfile = _profile.Clone();
        previewProfile.FollowMetricPreferences = false;
        settings.Osd.Replace(previewProfile);
        settings.Osd.ActiveProfileId = previewProfile.Id;
        settings.Normalize();
        var snapshot = BuildPreviewSnapshot(previewProfile);
        var fps = _runtime.CurrentFps;
        if (!fps.HasValue)
        {
            fps = new FpsSnapshot(1, Environment.TickCount64, FpsStatus.Capturing,
                FpsSource.Presented, false, 0, Loc.T("osd_preview_process"), 144, 6.94, 120, null);
        }

        if (previewProfile.IsTile && previewProfile.TileDesign is { } design)
        {
            var tileSnapshot = BuildTilePreviewSnapshot();
            var context = _runtime.BuildTileRenderContext();
            var scene = TileSceneBuilder.Build(design, tileSnapshot, fps, _runtime.History,
                previewProfile.Tile, context);
            var tileTheme = OverlayTheme.Create(settings);
            PreviewSurface.SetTileContent(scene, previewProfile.Tile.Scale, tileTheme);
            PreviewStateText.Text = Loc.F("osd_tile_preview",
                design.CanvasWidth.ToString("0"), design.CanvasHeight.ToString("0"));
            return;
        }

        var layout = OverlayLayoutBuilder.Build(settings, snapshot, fps, _runtime.History);
        var theme = OverlayTheme.Create(settings);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var visual = OverlayVisualBuilder.Build(
            layout,
            theme,
            _runtime.History,
            settings.Overlay.Layout,
            pixelsPerDip,
            demo: true,
            badgeText: Loc.T("osd_preview_badge"));
        PreviewSurface.SetContent(visual, theme);
        PreviewStateText.Text = Loc.T("osd_preview_state");
    }

    private SensorSnapshot BuildPreviewSnapshot(OsdProfile profile)
    {
        var source = _runtime.Cache.Latest;
        var values = source.Values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var now = Environment.TickCount64;
        foreach (var element in OsdProfile.Flatten(profile.Elements))
        {
            if (!element.IsSensorLike || string.IsNullOrWhiteSpace(element.SensorId))
            {
                continue;
            }

            var metric = MetricCatalog.Find(element.SensorId);
            var rawId = metric is null
                ? element.SensorId
                : MetricCatalog.SensorIdFor(metric) ?? element.SensorId;
            if (values.TryGetValue(rawId, out var existing) && existing.Available)
            {
                continue;
            }

            var demo = DemoValue(element.SensorId, rawId);
            values[rawId] = new SensorValue(rawId, demo.Value, demo.Unit, demo.Kind,
                SensorOrigin.Demo, Loc.T("osd_preview_demo_source"), now);
            if (element.SensorId == "cpu.fan.rpm")
            {
                values["fan.cpu.0.rpm"] = new SensorValue("fan.cpu.0.rpm", 1800, "RPM",
                    SensorKind.Fan, SensorOrigin.Demo, Loc.T("osd_preview_demo_source"), now);
            }
            else if (element.SensorId == "gpu.fan.rpm")
            {
                values["fan.gpu.0.rpm"] = new SensorValue("fan.gpu.0.rpm", 2200, "RPM",
                    SensorKind.Fan, SensorOrigin.Demo, Loc.T("osd_preview_demo_source"), now);
            }
        }

        return SensorSnapshot.Create(Math.Max(1, source.Sequence + 1), now, values);
    }

    /// <summary>
    /// Tile designs bind sensors directly, not through profile elements, so the preview needs the
    /// complete demo catalogue. Real readings win over demo readings; demo fills only the gaps.
    /// </summary>
    private SensorSnapshot BuildTilePreviewSnapshot()
    {
        var source = _runtime.Cache.Latest;
        var values = source.Values.ToDictionary(pair => pair.Key, pair => pair.Value,
            StringComparer.Ordinal);
        var now = Environment.TickCount64;
        using var demo = new DemoSensorProvider();
        demo.Initialize();
        foreach (var value in demo.Read(now))
        {
            if (values.TryGetValue(value.Id, out var real) && real.Available)
            {
                continue;
            }

            values[value.Id] = value;
        }

        SensorPostProcessor.Derive(values, now);
        return SensorSnapshot.Create(Math.Max(1, source.Sequence + 1), now, values);
    }

    private static (double Value, string Unit, SensorKind Kind) DemoValue(string id, string rawId)
    {
        var definition = MetricCatalog.Find(id) ?? MetricCatalog.FindBySensorId(rawId);
        var unit = definition?.Unit ?? string.Empty;
        var value = id switch
        {
            "cpu.temp" or "cpu.temp.package" or "thermalzone.temp" => 64,
            "cpu.usage" or "cpu.usage.max" => 38,
            "cpu.clock" => 3600,
            "cpu.power" => 55,
            "cpu.power.cores" => 40,
            "cpu.power.gt" => 5,
            "cpu.power.memory" => 8,
            "cpu.voltage" => 1.1,
            "gpu.temp" => 67,
            "gpu.temp.hotspot" => 75,
            "gpu.temp.memory" => 78,
            "gpu.usage" => 88,
            "gpu.power" => 170,
            "gpu.power.limit" => 175,
            "gpu.clock" => 2200,
            "gpu.clock.memory" => 9000,
            "gpu.memcontroller" => 42,
            "vram.used" => 8000,
            "vram.percent" => 65,
            "ram.used" => 28000,
            "ram.percent" => 45,
            "ram.available" => 36000,
            "cpu.fan.rpm" => 1800,
            "gpu.fan.rpm" => 2200,
            "frame.fps" => 144,
            "frame.fps.1low" => 120,
            "frame.time" => 6.94,
            _ => 50,
        };
        var kind = definition?.Unit switch
        {
            "°C" => SensorKind.Temperature,
            "%" => SensorKind.Load,
            "MHz" => SensorKind.Clock,
            "W" => SensorKind.Power,
            "V" => SensorKind.Voltage,
            "RPM" => SensorKind.Fan,
            _ => SensorKind.Data,
        };
        return (value, unit, kind);
    }

    private static string SensorName(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return string.Empty;
        }

        var key = "metric_" + id.Replace('.', '_');
        var localized = Loc.T(key);
        if (!string.Equals(localized, key, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(localized))
        {
            return localized;
        }

        return MetricCatalog.Find(id)?.DisplayName ??
               MetricCatalog.FindBySensorId(id)?.DisplayName ??
               id;
    }

    private static string StyleName(OsdStyle style) => style switch
    {
        OsdStyle.HighContrast => Loc.T("osd_style_high_contrast"),
        OsdStyle.BlueLabels => Loc.T("osd_style_blue_labels"),
        OsdStyle.AdaptiveColors => Loc.T("osd_style_adaptive"),
        _ => Loc.T("osd_style_classic"),
    };

    private static string KindName(OsdElementKind kind) => kind switch
    {
        OsdElementKind.Sensor => Loc.T("osd_kind_sensor"),
        OsdElementKind.Group => Loc.T("osd_kind_group"),
        OsdElementKind.GroupHeader => Loc.T("osd_kind_group_header"),
        OsdElementKind.CustomText => Loc.T("osd_kind_custom_text"),
        OsdElementKind.DeviceHeader => Loc.T("osd_kind_device_header"),
        OsdElementKind.EmptyLine => Loc.T("osd_kind_empty"),
        OsdElementKind.Divider => Loc.T("osd_kind_divider"),
        OsdElementKind.Graph => Loc.T("osd_kind_graph"),
        OsdElementKind.ProgressBar => Loc.T("osd_kind_progress"),
        _ => Loc.T("osd_kind_custom_value"),
    };

    private static string KindGlyph(OsdElementKind kind) => kind switch
    {
        OsdElementKind.Group => "▾",
        OsdElementKind.GroupHeader or OsdElementKind.DeviceHeader => "H",
        OsdElementKind.CustomText => "T",
        OsdElementKind.EmptyLine => "·",
        OsdElementKind.Divider => "—",
        OsdElementKind.Graph => "⌁",
        OsdElementKind.ProgressBar => "▰",
        _ => "•",
    };

    private static bool HexColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.StartsWith('#') ? value : "#" + value;
        return text.Length is 7 or 9 && text.Skip(1).All(Uri.IsHexDigit);
    }

    private static Brush ParseBrush(string? value)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                ColorConverter.ConvertFromString(value.StartsWith('#') ? value : "#" + value) is Color color)
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }
        }
        catch
        {
            // ignored
        }

        return Brushes.Transparent;
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }

    private static void SelectChoice<T>(ComboBox combo, T value) where T : struct, Enum
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is T candidate && EqualityComparer<T>.Default.Equals(candidate, value))
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }
}

internal sealed record TileWidgetRef(string Path, TileNode Node);
