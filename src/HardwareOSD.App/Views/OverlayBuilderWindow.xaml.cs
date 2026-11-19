using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using HardwareOSD.App.Localization;
using HardwareOSD.App.Native;
using HardwareOSD.App.Overlay;
using HardwareOSD.App.Services;
using HardwareOSD.App.Theming;
using HardwareOSD.Core.Config;
using HardwareOSD.Core.Fps;
using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.Metrics;
using HardwareOSD.Core.Overlay;
using HardwareOSD.Core.TileOsd;
using Microsoft.Win32;

namespace HardwareOSD.App.Views;

/// <summary>
/// The 0.23.0 OSD builder: three-pane CapFrameX-style tile editor. The canvas, preview and overlay
/// all come from one document (<see cref="TileDesignDocument"/>), so what the user drags is exactly
/// what the overlay renders. In 0.26.0 the five original profiles are tile documents too; the only
/// remaining row profiles are the three preserved CapFrameX row templates.
/// </summary>
public partial class OverlayBuilderWindow : Window
{
    private enum InspectorTab
    {
        Tile,
        Layout,
        Style,
    }

    private const string TileDragFormat = "HardwareOSD.TileId";
    private const string SourceDragFormat = "HardwareOSD.SourceKey";
    private const string WidgetDragFormat = "HardwareOSD.WidgetKind";
    private const string WidgetPresetFormat = "HardwareOSD.WidgetPreset";
    private const string TemplateDragFormat = "HardwareOSD.TemplateId";

    private readonly AppRuntime _runtime;
    private sealed record EditSnapshot(string Json, string? SelectedId, string? ScopeId);

    private readonly Stack<EditSnapshot> _undo = new();
    private readonly Stack<EditSnapshot> _redo = new();
    private readonly DispatcherTimer _previewTimer;
    private readonly List<TileSource> _filteredSources = new();

    private AppSettings _draft;
    private OsdProfile _profile;
    private TileDesignDocument? _document;
    private string? _scopeId;
    private string? _selectedId;
    private string _savedJson = string.Empty;
    private InspectorTab _tab = InspectorTab.Tile;
    private bool _loading;
    private bool _live = true;
    private bool _dirty;
    private bool _galleryBuilt;
    private bool _settingLeftTab;
    private bool _settingInspectorTab;
    private SensorSnapshot? _frozenSnapshot;
    private bool _demoPreview;
    private TileNode? _clipboard;
    private Action<DrawingContext, TileRect>? _classicRows;

    private Point _listDragStart;
    private string? _listDragNodeId;
    private Point _widgetDragStart;
    private string? _widgetDragId;
    private UIElement? _widgetDragSource;
    private bool _suppressPaletteClick;

    public OverlayBuilderWindow(AppRuntime runtime)
    {
        _runtime = runtime;
        _draft = runtime.Settings.Clone();
        var active = _draft.Osd?.ActiveProfile;
        if (active is null || !active.IsTile)
        {
            active = _draft.Osd?.Profiles.FirstOrDefault(profile => profile.IsTile) ?? active;
        }

        _profile = (active ?? _draft.Osd!.Profiles[0]).Clone();

        InitializeComponent();
        SourceInitialized += (_, _) =>
            NativeMethods.SetTitleBarTheme(new WindowInteropHelper(this).Handle,
                AppThemeManager.IsDark);

        _previewTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _previewTimer.Tick += (_, _) =>
        {
            if (_live && IsVisible && _document is not null)
            {
                RefreshScene();
            }
        };

        Loaded += OnLoaded;
        Closed += (_, _) => _previewTimer.Stop();
        SizeChanged += (_, _) => FitZoom();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SetLeftTab(0);
        BuildWidgetPalette();
        BuildSourcePanel();
        BuildProfileChoices();
        EditorCanvas.EmptyHint = Loc.T("osd_ed_empty_hint");
        EditorCanvas.SelectionChanged += (_, _) => OnCanvasSelectionChanged();
        EditorCanvas.InteractionStarted += (_, _) => PushUndoSnapshot();
        EditorCanvas.RequestBringIntoView += (_, e) => e.Handled = true;
        EditorCanvas.GeometryChanged += (_, final) =>
        {
            _dirty = true;
            RefreshScene();
            UpdateChrome();
            if (final)
            {
                RefreshTileList();
                BuildInspector();
            }
        };
        EditorCanvas.ContainerEntered += (_, _) => OnCanvasContainerEntered();
        EditorCanvas.DeleteRequested += (_, _) => DeleteSelected();
        EditorCanvas.Status += message => SetStatus(message);
        if (EditorCanvas.Parent is FrameworkElement host)
        {
            host.SizeChanged += (_, _) => FitZoom();
        }

        LoadProfile(_profile, resetUndo: true);
        SetTab(InspectorTab.Tile);
        _previewTimer.Start();
        BuildStartupTemplatePanel();
        ShowStartupChooser();
    }

    // ---------------------------------------------------------------- start screen

    private void Start_Click(object sender, RoutedEventArgs e) => ShowStartupChooser();

    private void ShowStartupChooser()
    {
        StartupCurrentText.Text = Loc.F("osd_start_current", _profile.Name);
        StartupNameBox.Text = string.Empty;
        StartupChooser.Visibility = Visibility.Visible;
        StartupNameBox.Focus();
    }

    private void StartupContinue_Click(object sender, RoutedEventArgs e)
        => StartupChooser.Visibility = Visibility.Collapsed;

    private void StartupOpenProfile_Click(object sender, RoutedEventArgs e)
    {
        StartupChooser.Visibility = Visibility.Collapsed;
        ProfileCombo.Focus();
        ProfileCombo.IsDropDownOpen = true;
    }

    private void StartupBlank_Click(object sender, RoutedEventArgs e)
    {
        var name = string.IsNullOrWhiteSpace(StartupNameBox.Text)
            ? "Blank"
            : StartupNameBox.Text.Trim();
        CreateProfileFromTemplate(TileTemplateCatalog.BlankId, name);
    }

    private void StartupTemplates_Click(object sender, RoutedEventArgs e)
    {
        StartupChooser.Visibility = Visibility.Collapsed;
        BuildTemplateGallery();
        TemplateGallery.Visibility = Visibility.Visible;
    }

    private void BuildStartupTemplatePanel()
    {
        StartupTemplatePanel.Children.Clear();
        foreach (var starter in TileStarterTemplates.All)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = starter.Name,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextBrush"),
            });
            content.Children.Add(new TextBlock
            {
                Text = starter.Summary,
                Style = (Style)FindResource("Caption"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
            });
            var card = new Button
            {
                Content = content,
                Style = (Style)FindResource("FlatButton"),
                Width = 238,
                MinHeight = 62,
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(10, 8, 10, 8),
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            AutomationProperties.SetAutomationId(card, "OsdStartupTemplate_" + starter.Id);
            AutomationProperties.SetName(card, starter.Name);
            card.Click += (_, _) => StartupTemplate_Click(starter.Id);
            StartupTemplatePanel.Children.Add(card);
        }
    }

    private void StartupTemplate_Click(string templateId)
    {
        var name = string.IsNullOrWhiteSpace(StartupNameBox.Text)
            ? TileStarterTemplates.Find(templateId)?.Name ?? "New layout"
            : StartupNameBox.Text.Trim();
        CreateProfileFromTemplate(templateId, name);
    }

    private void CreateProfileFromTemplate(string templateId, string name)
    {
        var document = TileTemplateCatalog.CreateDocument(templateId, UniqueName(name));
        if (document is null)
        {
            return;
        }

        CreateProfileFromDocument(document);
    }

    /// <summary>Adds a new profile backed by an editable copy of the supplied document.</summary>
    private void CreateProfileFromDocument(TileDesignDocument design)
    {
        if (!ConfirmDiscard())
        {
            return;
        }

        var name = UniqueName(design.Name);
        design.Name = name;
        var profile = new OsdProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Library = OsdDesignLibrary.HardwareOsd,
            Layout = OsdProfileLayout.Tile,
            DesignId = string.Empty,
            Elements = new List<OsdElement>(),
        };
        profile.SetTileDesign(design);
        _draft.Osd!.Profiles.Add(profile);
        PersistDraft();
        _loading = true;
        try
        {
            LoadProfile(profile, resetUndo: true);
            BuildProfileChoices();
        }
        finally
        {
            _loading = false;
        }

        _dirty = true;
        StartupChooser.Visibility = Visibility.Collapsed;
        TemplateGallery.Visibility = Visibility.Collapsed;
        UpdateChrome();
        SetStatus(Loc.T("osd_ed_added_profile"));
    }

    // ---------------------------------------------------------------- profiles

    private void BuildProfileChoices()
    {
        _loading = true;
        try
        {
            ProfileCombo.Items.Clear();
            var profiles = _draft.Osd?.Profiles ?? new List<OsdProfile>();
            AddProfileGroup("— " + Loc.T("osd_library_hardwareosd") + " —",
                profiles.Where(profile => profile.IsTile && profile.Library == OsdDesignLibrary.HardwareOsd));
            AddProfileGroup("— " + Loc.T("osd_library_capframex") + " —",
                profiles.Where(profile => profile.IsTile &&
                    profile.Library == OsdDesignLibrary.CapFrameX &&
                    !OsdLibraryFilter.IsHiddenFromLibrary(profile, _profile.Id)));

            var index = ProfileCombo.Items.OfType<ComboBoxItem>()
                .ToList()
                .FindIndex(item => string.Equals(item.Tag as string, _profile.Id, StringComparison.Ordinal));
            ProfileCombo.SelectedIndex = index >= 0 ? index : 0;
        }
        finally
        {
            _loading = false;
        }
    }

    private void AddProfileGroup(string header, IEnumerable<OsdProfile> profiles)
    {
        ProfileCombo.Items.Add(new ComboBoxItem
        {
            Content = header,
            IsEnabled = false,
            FontWeight = FontWeights.SemiBold,
        });
        foreach (var profile in profiles)
        {
            ProfileCombo.Items.Add(new ComboBoxItem
            {
                Content = (profile.IsTile ? "Tile · " : "Row · ") + profile.Name,
                Tag = profile.Id,
            });
        }
    }

    private void LoadProfile(OsdProfile profile, bool resetUndo)
    {
        _profile = profile.Clone();
        ProfileNameBox.Text = _profile.Name;
        _scopeId = null;
        _selectedId = null;
        _document = null;

        if (_profile.IsTile)
        {
            var design = _profile.TileDesign?.Clone()
                         ?? TileDocumentEditor.CreateNew(_profile.Name);
            TileDocumentEditor.MergeOverrides(design, _profile.Tile);
            TileDocumentEditor.PrepareForEditing(design);
            _document = design;
            _savedJson = design.ToJson();
        }
        else
        {
            _savedJson = string.Empty;
        }

        if (resetUndo)
        {
            _undo.Clear();
            _redo.Clear();
        }

        _dirty = false;
        RefreshAll(resetScroll: true);
    }

    private void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProfileCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string id ||
            string.Equals(id, _profile.Id, StringComparison.Ordinal))
        {
            return;
        }

        if (!ConfirmDiscard())
        {
            _loading = true;
            try
            {
                var index = ProfileCombo.Items.OfType<ComboBoxItem>()
                    .ToList()
                    .FindIndex(candidate => string.Equals(candidate.Tag as string, _profile.Id,
                        StringComparison.Ordinal));
                ProfileCombo.SelectedIndex = index >= 0 ? index : 0;
            }
            finally
            {
                _loading = false;
            }

            return;
        }

        var profile = _draft.Osd?.FindProfile(id);
        if (profile is null)
        {
            return;
        }

        _loading = true;
        try
        {
            LoadProfile(profile, resetUndo: true);
            BuildProfileChoices();
        }
        finally
        {
            _loading = false;
        }
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = UniqueName(string.IsNullOrWhiteSpace(ProfileNameBox.Text)
            ? "New profile"
            : ProfileNameBox.Text.Trim());
        CreateProfileFromDocument(TileDocumentEditor.CreateNew(name));
    }

    private void DuplicateProfile_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard())
        {
            return;
        }

        var copy = _profile.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = UniqueName(_profile.Name + " copy");
        if (_document is not null)
        {
            var design = _document.Clone();
            design.Id = TileDocumentEditor.NewNodeId();
            design.Name = copy.Name;
            copy.SetTileDesign(design);
        }

        _draft.Osd!.Profiles.Add(copy);
        PersistDraft();
        _loading = true;
        try
        {
            LoadProfile(copy, resetUndo: true);
            BuildProfileChoices();
        }
        finally
        {
            _loading = false;
        }

        SetStatus(Loc.T("osd_ed_duplicated_profile"));
    }

    private void RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null)
        {
            return;
        }

        var name = ProfileNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            SetStatus(Loc.T("osd_ed_rename_hint"));
            return;
        }

        Apply(() =>
        {
            _profile.Name = name;
            _document.Name = name;
        });
        RefreshTileList();
        UpdateChrome();
        SetStatus(Loc.T("osd_ed_renamed"));
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        var profiles = _draft.Osd?.Profiles ?? new List<OsdProfile>();
        if (profiles.Count <= 1)
        {
            SetStatus(Loc.T("osd_ed_last_profile"));
            return;
        }

        var answer = MessageBox.Show(this,
            Loc.F("osd_ed_confirm_delete", _profile.Name),
            Loc.T("osd_ed_delete_title"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        var index = profiles.IndexOf(profiles.First(profile =>
            string.Equals(profile.Id, _profile.Id, StringComparison.Ordinal)));
        profiles.RemoveAt(index);
        PersistDraft();
        var next = profiles[Math.Clamp(index, 0, profiles.Count - 1)];
        _loading = true;
        try
        {
            LoadProfile(next, resetUndo: true);
            BuildProfileChoices();
        }
        finally
        {
            _loading = false;
        }

        SetStatus(Loc.T("osd_ed_deleted_profile"));
    }

    private string UniqueName(string candidate)
    {
        var profiles = _draft.Osd?.Profiles ?? new List<OsdProfile>();
        var name = candidate.Trim();
        if (profiles.All(profile => !string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return name;
        }

        for (var suffix = 2; suffix < 100; suffix++)
        {
            var candidateName = name + " " + suffix.ToString(CultureInfo.InvariantCulture);
            if (profiles.All(profile =>
                    !string.Equals(profile.Name, candidateName, StringComparison.OrdinalIgnoreCase)))
            {
                return candidateName;
            }
        }

        return name + " " + Guid.NewGuid().ToString("N")[..4];
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!CommitProfile())
        {
            return;
        }

        SetStatus(Loc.T("osd_ed_saved_status"));
    }

    private bool CommitProfile()
    {
        if (_document is null || !_profile.IsTile)
        {
            return false;
        }

        _document.Name = string.IsNullOrWhiteSpace(_document.Name) ? _profile.Name : _document.Name;
        _document.NormalizeEditorSettings();
        _profile.SetTileDesign(_document);
        _profile.Name = _document.Name;
        _draft.Osd!.Replace(_profile);
        _draft.Osd.ActiveProfileId = _profile.Id;
        _draft.Normalize();
        _runtime.ApplySettings(_draft.Clone(), persist: true);
        _draft = _runtime.Settings.Clone();
        _profile = _draft.Osd.FindProfile(_profile.Id)?.Clone() ?? _profile;
        _savedJson = _document.ToJson();
        _dirty = false;
        ProfileNameBox.Text = _profile.Name;
        BuildProfileChoices();
        UpdateChrome();
        return true;
    }

    private void PersistDraft()
    {
        if (_document is not null && _profile.IsTile)
        {
            _profile.SetTileDesign(_document);
            _draft.Osd!.Replace(_profile);
        }

        _draft.Normalize();
        _runtime.ApplySettings(_draft.Clone(), persist: true);
        _draft = _runtime.Settings.Clone();
    }

    private bool ConfirmDiscard()
    {
        if (!_dirty)
        {
            return true;
        }

        var answer = MessageBox.Show(this,
            Loc.F("osd_ed_dirty_prompt", _profile.Name), Loc.T("osd_ed_unsaved_title"),
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        switch (answer)
        {
            case MessageBoxResult.Yes:
                return CommitProfile();
            case MessageBoxResult.No:
                _dirty = false;
                return true;
            default:
                return false;
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmDiscard())
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    // ---------------------------------------------------------------- undo

    private EditSnapshot CaptureSnapshot()
        => new(_document!.ToJson(), _selectedId, _scopeId);

    private void PushUndoSnapshot()
    {
        if (_document is null)
        {
            return;
        }

        _undo.Push(CaptureSnapshot());
        if (_undo.Count > 50)
        {
            var items = _undo.ToList();
            _undo.Clear();
            for (var index = Math.Min(50, items.Count) - 1; index >= 0; index--)
            {
                _undo.Push(items[index]);
            }
        }

        _redo.Clear();
        UpdateChrome();
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        if (_document is null || _undo.Count == 0)
        {
            return;
        }

        _redo.Push(CaptureSnapshot());
        Restore(_undo.Pop());
        SetStatus(Loc.T("osd_ed_undo_status"));
    }

    private void Redo()
    {
        if (_document is null || _redo.Count == 0)
        {
            return;
        }

        _undo.Push(CaptureSnapshot());
        Restore(_redo.Pop());
        SetStatus(Loc.T("osd_ed_redo_status"));
    }

    private void Restore(EditSnapshot snapshot)
    {
        var document = TileDesignDocument.Parse(snapshot.Json, out var error);
        if (document is null)
        {
            SetStatus(Loc.F("osd_ed_import_failed", error ?? "invalid"));
            return;
        }

        TileDocumentEditor.EnsureIds(document);
        _document = document;
        _scopeId = snapshot.ScopeId is not null &&
                   TileDocumentEditor.Find(document, snapshot.ScopeId) is not null
            ? snapshot.ScopeId
            : null;
        _selectedId = snapshot.SelectedId is not null &&
                      TileDocumentEditor.Find(document, snapshot.SelectedId) is not null
            ? snapshot.SelectedId
            : null;

        _dirty = !string.Equals(document.ToJson(), _savedJson, StringComparison.Ordinal);
        RefreshAll();
    }

    // ---------------------------------------------------------------- document edits

    private TileNode? Scope => _document is null
        ? null
        : (_scopeId is null ? _document.Root : TileDocumentEditor.Find(_document, _scopeId)) ??
          _document.Root;

    private TileNode? Selected => _document is null
        ? null
        : TileDocumentEditor.Find(_document, _selectedId);

    private bool Apply(Action action)
    {
        if (_document is null)
        {
            return false;
        }

        var before = CaptureSnapshot();
        action();
        _undo.Push(before);
        _redo.Clear();
        _dirty = true;
        RefreshScene();
        UpdateChrome();
        return true;
    }

    private void AddTile(TileNodeKind kind, double? x = null, double? y = null)
    {
        if (_document is null || Scope is not { } scope)
        {
            return;
        }

        var (defaultWidth, _) = TileDocumentEditor.DefaultSize(kind);
        var (freeX, freeY) = NextFreePoint(scope, defaultWidth);
        var before = CaptureSnapshot();
        var tile = TileDocumentEditor.AddTile(_document, scope, kind, x ?? freeX, y ?? freeY);
        if (tile is null)
        {
            SetStatus(Loc.T("osd_ed_limit"));
            return;
        }

        _undo.Push(before);
        _redo.Clear();
        _selectedId = tile.NodeId;
        _dirty = true;
        RefreshAll();
        SetStatus(Loc.T("osd_ed_added"));
    }

    private (double X, double Y) NextFreePoint(TileNode scope, double width)
    {
        if (_document is null)
        {
            return (0, 0);
        }

        var bottom = 0.0;
        foreach (var child in scope.Children)
        {
            bottom = Math.Max(bottom, (child.Y ?? 0) + (child.FrameHeight ?? child.Height ?? 0));
        }

        var maxWidth = scope.NodeId == _document.Root.NodeId
            ? _document.CanvasWidth
            : scope.FrameWidth ?? scope.Width ?? _document.CanvasWidth;
        var x = Math.Max(0, Math.Min(24, Math.Max(0, maxWidth - width)));
        return (x, bottom > 0 ? bottom + Math.Max(0, _document.Theme.Gap) : 0);
    }

    private void AddWidget(TileNodeKind kind)
    {
        AddTile(kind);
        if (kind == TileNodeKind.Panel)
        {
            SetStatus(Loc.T("osd_ed_container_added"));
        }
    }

    private void AddPreset(string presetId, double? x = null, double? y = null)
    {
        if (_document is null || Scope is not { } scope)
        {
            return;
        }

        var preset = TileElementLibrary.Find(presetId);
        if (preset is null)
        {
            return;
        }

        var node = TileElementLibrary.Create(_document, presetId);
        node.Name = PresetLabel(preset);
        var width = node.FrameWidth ?? node.Width ?? preset.Width;
        var (freeX, freeY) = NextFreePoint(scope, width);
        var before = CaptureSnapshot();
        if (TileDocumentEditor.AddNode(_document, scope, node, x ?? freeX, y ?? freeY) is null)
        {
            SetStatus(Loc.T("osd_ed_limit"));
            return;
        }

        _undo.Push(before);
        _redo.Clear();
        _selectedId = node.NodeId;
        _dirty = true;
        RefreshAll();
        SetStatus(Loc.T("osd_ed_added"));
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => CopySelected();

    private void Paste_Click(object sender, RoutedEventArgs e) => PasteClipboard();

    private void CopySelected()
    {
        if (_document is null || Selected is not { } selected)
        {
            return;
        }

        _clipboard = TileDocumentEditor.CloneForPaste(selected);
        SetStatus(Loc.T("osd_copied"));
    }

    private void PasteClipboard()
    {
        if (_document is null || _clipboard is null || Scope is not { } scope)
        {
            SetStatus(Loc.T("osd_clipboard_empty"));
            return;
        }

        var copy = TileDocumentEditor.CloneForPaste(_clipboard);
        var width = copy.FrameWidth ?? copy.Width ?? 180;
        var (freeX, freeY) = NextFreePoint(scope, width);
        var before = CaptureSnapshot();
        if (TileDocumentEditor.AddNode(_document, scope, copy, freeX, freeY) is null)
        {
            SetStatus(Loc.T("osd_ed_limit"));
            return;
        }

        _undo.Push(before);
        _redo.Clear();
        _selectedId = copy.NodeId;
        _dirty = true;
        RefreshAll();
        SetStatus(Loc.T("osd_pasted"));
    }

    private void AddSourceTile(string key, TileNodeKind kind, double? x = null, double? y = null)
    {
        if (_document is null || Scope is not { } scope)
        {
            return;
        }

        var source = TileSourceCatalog.Find(key);
        var (freeX, freeY) = NextFreePoint(scope, 180);
        var before = CaptureSnapshot();
        TileNode? tile;
        if (kind == TileNodeKind.Heatmap)
        {
            tile = TileDocumentEditor.AddTile(_document, scope, TileNodeKind.Heatmap, x ?? freeX, y ?? freeY);
        }
        else
        {
            tile = TileDocumentEditor.AddTile(_document, scope, TileNodeKind.Metric, x ?? freeX, y ?? freeY);
        }

        if (tile is null)
        {
            SetStatus(Loc.T("osd_ed_limit"));
            return;
        }

        var widget = TileDocumentEditor.WidgetOf(tile);
        if (kind == TileNodeKind.Heatmap)
        {
            var group = TileSourceCatalog.Groups.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, key, StringComparison.Ordinal));
            if (group is not null)
            {
                widget.Keys = group.Keys.ToList();
                widget.Title = group.Label;
                widget.Unit = group.Unit;
                widget.Min = 0;
                widget.Max = 100;
            }

            widget.Key = null;
        }
        else
        {
            widget.Key = key;
            widget.Label = source?.Label ?? key;
            widget.Unit = source?.Unit ?? string.Empty;
            widget.Digits = source?.Kind == TileSourceKind.Text ? null : 0;
        }

        _undo.Push(before);
        _redo.Clear();
        _selectedId = tile.NodeId;
        _dirty = true;
        RefreshAll();
        SetStatus(Loc.F("osd_ed_source_added", widget.Label ?? key));
    }

    private void DeleteSelected()
    {
        if (_document is null || Selected is not { } selected)
        {
            return;
        }

        if (selected.Locked == true)
        {
            SetStatus(Loc.T("osd_ed_locked"));
            return;
        }

        DeleteNode(selected);
    }

    /// <summary>
    /// Removes one tile and keeps a predictable keyboard selection: the nearest sibling (or the
    /// previous one when the last child was removed). Undo snapshots capture this selection, so a
    /// later undo restores both the tile and the ability to press Delete again.
    /// </summary>
    private void DeleteNode(TileNode node)
    {
        if (_document is null)
        {
            return;
        }

        var parent = TileDocumentEditor.ParentOf(_document, node.NodeId);
        if (parent is null)
        {
            return;
        }

        var wasSelected = string.Equals(_selectedId, node.NodeId, StringComparison.Ordinal);
        var index = parent.Children.IndexOf(node);
        Apply(() => TileDocumentEditor.Remove(_document, node));
        if (wasSelected || _selectedId is null)
        {
            _selectedId = parent.Children.Count == 0
                ? null
                : parent.Children[Math.Min(index, parent.Children.Count - 1)].NodeId;
        }

        RefreshAll();
        SetStatus(Loc.T("osd_ed_deleted"));
    }

    private void DeleteElement_Click(object sender, RoutedEventArgs e) => DeleteSelected();

    private void DuplicateElement_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null || Selected is not { } selected)
        {
            return;
        }

        TileNode? copy = null;
        Apply(() => copy = TileDocumentEditor.DuplicateTile(_document, selected));
        if (copy is null)
        {
            SetStatus(Loc.T("osd_ed_limit"));
            return;
        }

        _selectedId = copy.NodeId;
        RefreshAll();
        SetStatus(Loc.T("osd_ed_duplicated"));
    }

    private void SendBackward_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null || Selected is not { } selected)
        {
            return;
        }

        Apply(() => TileDocumentEditor.MoveOrder(_document, selected, -1));
        RefreshTileList();
        RefreshScene();
    }

    private void BringForward_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null || Selected is not { } selected)
        {
            return;
        }

        Apply(() => TileDocumentEditor.MoveOrder(_document, selected, 1));
        RefreshTileList();
        RefreshScene();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null || _scopeId is null)
        {
            return;
        }

        var scope = TileDocumentEditor.Find(_document, _scopeId);
        var parent = scope is null ? null : TileDocumentEditor.ParentOf(_document, scope.NodeId);
        _scopeId = parent is null || parent.NodeId == _document.Root.NodeId ? null : parent.NodeId;
        _selectedId = null;
        RefreshAll();
    }

    private void EnterContainer(TileNode node)
    {
        var scope = TileDocumentEditor.ResolveEditableScope(node);
        if (scope.Children.Count == 0)
        {
            return;
        }

        _scopeId = scope.NodeId;
        _selectedId = null;
        RefreshAll(resetScroll: true);
        SetStatus(Loc.F("osd_ed_container_entered", scope.Name ?? node.Name ?? Loc.T("osd_kind_panel")));
    }

    // ---------------------------------------------------------------- preview

    private void RefreshAll(bool resetScroll = false)
    {
        if (_document is null)
        {
            EditorCanvas.Visibility = Visibility.Collapsed;
            RowProfilePanel.Visibility = Visibility.Visible;
            ProfileHeaderHint.Text = Loc.T("osd_ed_row_profile");
            LeftPanel.IsEnabled = false;
            DeleteZone.Visibility = Visibility.Collapsed;
            ScopeCaption.Text = _profile.Name + " · " + Loc.T("osd_ed_row_scope");
            PreviewInfoText.Text = string.Empty;
            TemplatesButton.IsEnabled = false;
            InspectorPanel.Children.Clear();
            BuildInspector();
            RefreshTileList();
            UpdateChrome();
            return;
        }

        var scrollX = CanvasScroller.HorizontalOffset;
        var scrollY = CanvasScroller.VerticalOffset;
        EditorCanvas.Visibility = Visibility.Visible;
        RowProfilePanel.Visibility = Visibility.Collapsed;
        LeftPanel.IsEnabled = true;
        DeleteZone.Visibility = Visibility.Visible;
        TemplatesButton.IsEnabled = true;
        ProfileHeaderHint.Text = string.Empty;
        EditorCanvas.SetDocument(_document, Scope, _selectedId);
        RefreshScene();
        FitZoom();
        if (resetScroll)
        {
            CanvasScroller.ScrollToHome();
        }
        else
        {
            CanvasScroller.ScrollToHorizontalOffset(scrollX);
            CanvasScroller.ScrollToVerticalOffset(scrollY);
        }

        RefreshTileList();
        BuildInspector();
        UpdateChrome();
    }

    private void RefreshScene()
    {
        if (_document is null)
        {
            return;
        }

        var snapshot = _demoPreview
            ? BuilderDemoSnapshot.Snapshot
            : _live ? _runtime.Cache.Latest : _frozenSnapshot ?? _runtime.Cache.Latest;
        var fps = _demoPreview ? BuilderDemoSnapshot.Fps : _runtime.FpsCache.Latest;
        var history = _demoPreview ? BuilderDemoSnapshot.History : _runtime.History;
        var bounds = new Dictionary<string, TileRect>(StringComparer.Ordinal);
        TileScene scene;
        try
        {
            scene = TileSceneBuilder.Build(_document, snapshot, fps,
                history, null, _runtime.BuildTileRenderContext(), bounds);
        }
        catch (Exception ex)
        {
            SetStatus(Loc.F("osd_ed_render_failed", ex.Message));
            return;
        }
        _classicRows = BuildClassicRowsCallback(_document, snapshot);
        EditorCanvas.ClassicRowsRenderer = _classicRows;
        EditorCanvas.SetScene(scene, bounds);
        EditorCanvas.SetDocument(_document, Scope, _selectedId);
        CanvasScroller.Background = _document.ShowGrid
            ? GetCheckerBrush()
            : (Brush)FindResource("PreviewBackgroundBrush");

        PreviewInfoText.Text = Loc.F("osd_ed_preview_info",
            _document.CanvasWidth.ToString("0", CultureInfo.InvariantCulture),
            _document.CanvasHeight.ToString("0", CultureInfo.InvariantCulture),
            TileDocumentEditor.CountWidgets(_document.Root).ToString(CultureInfo.InvariantCulture));
        UpdatePreviewModeBadge();
        if (_demoPreview)
        {
            PreviewInfoText.Text += " · " + Loc.T("osd_ed_demo_badge");
        }
        else if (snapshot.CapturedAtMs == 0)
        {
            PreviewInfoText.Text += " · " + Loc.T("osd_ed_waiting");
        }

        UpdateScopeCaption();
    }

    private Action<DrawingContext, TileRect>? BuildClassicRowsCallback(TileDesignDocument design,
        SensorSnapshot snapshot)
    {
        if (!design.Root.Descendants().Any(node => node.NodeKind == TileNodeKind.ClassicRows))
        {
            return null;
        }

        var rows = _runtime.BuildClassicRowsVisual(snapshot);
        if (rows is null)
        {
            return null;
        }

        return (dc, rect) =>
        {
            var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            dc.PushClip(new RectangleGeometry(new Rect(rect.X, rect.Y, rect.Width, rect.Height)));
            dc.PushTransform(new TranslateTransform(rect.X, rect.Y));
            OverlayVisualRenderer.Draw(dc, rows.Value.Visual, rows.Value.Theme, pixelsPerDip,
                drawBackground: false);
            dc.Pop();
            dc.Pop();
        };
    }

    private void FitZoom()
    {
        if (_document is null || EditorCanvas.Parent is not FrameworkElement)
        {
            return;
        }

        // The reference editor renders the design at 1:1 in design pixels and lets the canvas
        // scroll when a design is larger than the pane. Scaling to fit produced fractional zoom
        // factors, which blurred text and made every measured position off by the scale factor.
        EditorCanvas.SetZoom(1);
        UpdateScopeCaption();
    }

    private static Brush? _checkerBrush;

    /// <summary>Frozen 8-pixel checkerboard used behind the design surface, as in CapFrameX.</summary>
    private static Brush GetCheckerBrush()
    {
        if (_checkerBrush is not null)
        {
            return _checkerBrush;
        }

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x1B)), null,
            new RectangleGeometry(new Rect(0, 0, 16, 16))));
        var light = new SolidColorBrush(Color.FromRgb(0x1B, 0x1F, 0x25));
        light.Freeze();
        group.Children.Add(new GeometryDrawing(light, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        group.Children.Add(new GeometryDrawing(light, null,
            new RectangleGeometry(new Rect(8, 8, 8, 8))));
        group.Freeze();

        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 16, 16),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
        brush.Freeze();
        _checkerBrush = brush;
        return brush;
    }

    private void UpdateScopeCaption()
    {
        if (_document is null)
        {
            return;
        }

        var scope = Scope;
        if (scope is null || scope.NodeId == _document.Root.NodeId)
        {
            ScopeCaption.Text = _document.Name + " · " + Loc.T("osd_ed_root_scope");
            BackButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            ScopeCaption.Text = _document.Name + " / " + (scope.Name ?? Loc.T("osd_kind_panel"));
            BackButton.Visibility = Visibility.Visible;
        }

        LayerCaption.Text = scope is not null && scope.NodeId != _document.Root.NodeId
            ? Loc.T("osd_ed_elements")
            : Loc.T("osd_ed_layers");
    }

    private void LiveCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _live = LiveCheck.IsChecked == true;
        if (_live && DemoCheck is not null && DemoCheck.IsChecked == true)
        {
            _loading = true;
            DemoCheck.IsChecked = false;
            _loading = false;
        }

        _demoPreview = false;
        _frozenSnapshot = _live ? null : _runtime.Cache.Latest;
        UpdatePreviewModeBadge();
        RefreshScene();
    }

    private void DemoCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _demoPreview = DemoCheck.IsChecked == true;
        if (_demoPreview && LiveCheck.IsChecked == true)
        {
            _loading = true;
            LiveCheck.IsChecked = false;
            _loading = false;
            _live = false;
        }

        if (!_demoPreview)
        {
            _live = LiveCheck.IsChecked == true;
        }

        UpdatePreviewModeBadge();
        RefreshScene();
    }

    private void UpdatePreviewModeBadge()
    {
        PreviewModeBadge.Visibility = _demoPreview ? Visibility.Visible : Visibility.Collapsed;
        if (_demoPreview)
        {
            PreviewModeBadge.Text = Loc.T("osd_ed_demo_badge");
        }
    }

    // ---------------------------------------------------------------- tiles list

    private void RefreshTileList()
    {
        _loading = true;
        try
        {
            TileList.Items.Clear();
            var scope = Scope;
            if (scope is null)
            {
                return;
            }

            for (var index = scope.Children.Count - 1; index >= 0; index--)
            {
                var tile = scope.Children[index];
                var widget = TileDocumentEditor.WidgetOf(tile);
                var label = tile.Name ?? widget.Label ?? widget.Text ?? widget.Title ??
                            KindName(widget.NodeKind);
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                stack.Children.Add(new TextBlock
                {
                    Text = Trim(label, 34),
                    FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                var detail = KindName(widget.NodeKind);
                if (TileDocumentEditor.HasEditableChildren(tile))
                {
                    detail = Loc.F("osd_ed_container_summary", detail,
                        TileDocumentEditor.ResolveEditableScope(tile).Children.Count
                            .ToString(CultureInfo.InvariantCulture));
                }

                stack.Children.Add(new TextBlock
                {
                    Text = detail,
                    Style = (Style)FindResource("Caption"),
                });
                row.Children.Add(stack);

                var lockBox = new CheckBox
                {
                    IsChecked = tile.Locked == true,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = Loc.T("osd_ed_lock"),
                };
                lockBox.Checked += (_, _) => SetLock(tile, true);
                lockBox.Unchecked += (_, _) => SetLock(tile, false);
                Grid.SetColumn(lockBox, 1);
                row.Children.Add(lockBox);

                var item = new ListBoxItem
                {
                    Content = row,
                    Tag = tile.NodeId,
                    Padding = new Thickness(5, 4, 5, 4),
                };
                AutomationProperties.SetName(item, label);
                if (tile.NodeId is { Length: > 0 } layerId)
                {
                    AutomationProperties.SetAutomationId(item, "OsdLayer_" + layerId);
                }

                TileList.Items.Add(item);
            }

            var selected = TileList.Items.OfType<ListBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, _selectedId, StringComparison.Ordinal));
            TileList.SelectedItem = selected;
            if (selected is not null)
            {
                TileList.ScrollIntoView(selected);
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private void SetLock(TileNode tile, bool locked)
    {
        if (_loading || tile.Locked == locked)
        {
            return;
        }

        Apply(() => tile.Locked = locked);
        RefreshTileList();
        BuildInspector();
        SetStatus(Loc.T(locked ? "osd_ed_locked_now" : "osd_ed_unlocked"));
    }

    private void TileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TileList.SelectedItem is not ListBoxItem item || item.Tag is not string id)
        {
            return;
        }

        _selectedId = id;
        EditorCanvas.SetSelection(id);
        BuildInspector();
        UpdateChrome();
    }

    private void TileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected is { } selected && TileDocumentEditor.HasEditableChildren(selected))
        {
            EnterContainer(selected);
        }
    }

    private void TileList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _listDragStart = e.GetPosition(TileList);
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        _listDragNodeId = item?.Tag as string;
    }

    private void TileList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _listDragNodeId is null)
        {
            return;
        }

        var point = e.GetPosition(TileList);
        if (Math.Abs(point.X - _listDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _listDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var data = new DataObject(TileDragFormat, _listDragNodeId);
        _listDragNodeId = null;
        DragDrop.DoDragDrop(TileList, data, DragDropEffects.Move);
    }

    private void TileList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(TileDragFormat) || e.Data.GetDataPresent(WidgetDragFormat) ||
                   e.Data.GetDataPresent(WidgetPresetFormat) ||
                   e.Data.GetDataPresent(SourceDragFormat) || e.Data.GetDataPresent(TemplateDragFormat)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void TileList_Drop(object sender, DragEventArgs e)
    {
        if (_document is null || Scope is not { } scope)
        {
            return;
        }

        if (e.Data.GetData(WidgetPresetFormat) is string presetId)
        {
            AddPreset(presetId);
            return;
        }

        if (e.Data.GetData(WidgetDragFormat) is string kindName &&
            Enum.TryParse<TileNodeKind>(kindName, out var kind))
        {
            AddTile(kind);
            return;
        }

        if (e.Data.GetData(SourceDragFormat) is string sourceKey)
        {
            AddSourceTile(sourceKey, TileNodeKind.Metric);
            return;
        }

        if (e.Data.GetData(TemplateDragFormat) is string templateId)
        {
            ApplyTemplate(templateId);
            return;
        }

        if (e.Data.GetData(TileDragFormat) is not string nodeId || _document is null)
        {
            return;
        }

        var node = TileDocumentEditor.Find(_document, nodeId);
        if (node is null || !string.Equals(TileDocumentEditor.ParentOf(_document, nodeId)?.NodeId,
                scope.NodeId, StringComparison.Ordinal))
        {
            return;
        }

        var target = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        var targetId = target?.Tag as string;
        var index = targetId is null
            ? 0
            : scope.Children.FindIndex(child => string.Equals(child.NodeId, targetId, StringComparison.Ordinal));
        if (index < 0)
        {
            index = 0;
        }

        Apply(() =>
        {
            scope.Children.Remove(node);
            scope.Children.Insert(Math.Clamp(index, 0, scope.Children.Count), node);
        });
        RefreshTileList();
    }

    // ---------------------------------------------------------------- canvas

    private void OnCanvasSelectionChanged()
    {
        if (_loading)
        {
            return;
        }

        _selectedId = EditorCanvas.SelectedId;
        _loading = true;
        try
        {
            var item = TileList.Items.OfType<ListBoxItem>()
                .FirstOrDefault(candidate => string.Equals(candidate.Tag as string, _selectedId,
                    StringComparison.Ordinal));
            TileList.SelectedItem = item;
        }
        finally
        {
            _loading = false;
        }

        BuildInspector();
        UpdateChrome();
    }

    private void OnCanvasContainerEntered()
    {
        if (EditorCanvas.ScopeNode is { } scope)
        {
            _scopeId = scope.NodeId == _document?.Root.NodeId ? null : scope.NodeId;
        }

        _selectedId = null;
        RefreshAll();
    }

    // ---------------------------------------------------------------- palette

    private void BuildWidgetPalette()
    {
        WidgetPalette.Children.Clear();
        foreach (var group in TileElementLibrary.All.GroupBy(preset => preset.Category))
        {
            WidgetPalette.Children.Add(new TextBlock
            {
                Text = Loc.T(CategoryKey(group.Key)),
                Style = (Style)FindResource("SectionTitle"),
                Margin = new Thickness(0, 6, 0, 6),
            });

            foreach (var preset in group)
            {
                var label = PresetLabel(preset);
                var content = new DockPanel();
                content.Children.Add(new TextBlock
                {
                    Text = "•",
                    FontFamily = (FontFamily)FindResource("MonoFont"),
                    Foreground = (Brush)FindResource("MutedBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                });
                DockPanel.SetDock(content.Children[0], Dock.Left);
                content.Children.Add(new TextBlock
                {
                    Text = label,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });

                // A real button, not a decorated border: it takes focus, answers Enter/Space and is
                // reachable through UI Automation, while the preview handlers keep drag-and-drop.
                var item = new Button
                {
                    Content = content,
                    Style = (Style)FindResource("FlatButton"),
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(10, 7, 10, 7),
                    Margin = new Thickness(0, 0, 0, 4),
                    MinHeight = 32,
                    ToolTip = Loc.T("osd_ed_drag_hint"),
                };
                AutomationProperties.SetAutomationId(item, "OsdLibrary_" + preset.Id);
                AutomationProperties.SetName(item, label);
                var presetId = preset.Id;
                item.PreviewMouseLeftButtonDown += (_, e) => BeginWidgetDrag(item, presetId, e);
                item.PreviewMouseMove += (_, e) => ContinueWidgetDrag(item, presetId, e);
                item.Click += (_, _) =>
                {
                    if (_suppressPaletteClick)
                    {
                        _suppressPaletteClick = false;
                        return;
                    }

                    AddPreset(presetId);
                };
                WidgetPalette.Children.Add(item);
            }
        }
    }

    private static string CategoryKey(TileElementCategory category) => category switch
    {
        TileElementCategory.Layout => "osd_lib_layout",
        TileElementCategory.Metrics => "osd_lib_metrics",
        TileElementCategory.Indicators => "osd_lib_indicators",
        TileElementCategory.Charts => "osd_lib_charts",
        _ => "osd_lib_text",
    };

    private static string PresetLabel(TileElementPreset preset)
        => !string.IsNullOrWhiteSpace(preset.SourceKey)
            ? TileElementLibrary.DisplayName(preset)
            : !string.IsNullOrWhiteSpace(preset.LabelKey)
                ? Loc.T(preset.LabelKey)
                : preset.ChartTitle ?? preset.Text ?? preset.Id;

    private void BeginWidgetDrag(Button source, string presetId, MouseButtonEventArgs e)
    {
        _widgetDragStart = e.GetPosition(this);
        _widgetDragId = presetId;
        _widgetDragSource = source;
        _suppressPaletteClick = false;
        source.CaptureMouse();
    }

    private void ContinueWidgetDrag(Button source, string presetId, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _widgetDragId != presetId)
        {
            return;
        }

        var point = e.GetPosition(this);
        if (Math.Abs(point.X - _widgetDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _widgetDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _widgetDragId = null;
        _suppressPaletteClick = true;
        source.ReleaseMouseCapture();
        DragDrop.DoDragDrop(source, new DataObject(WidgetPresetFormat, presetId),
            DragDropEffects.Copy);
        e.Handled = true;
    }

    // ---------------------------------------------------------------- canvas drops

    private void EditorCanvas_DragOver(object sender, DragEventArgs e)
    {
        var accepts = e.Data.GetDataPresent(WidgetDragFormat) ||
                      e.Data.GetDataPresent(WidgetPresetFormat) ||
                      e.Data.GetDataPresent(SourceDragFormat) ||
                      e.Data.GetDataPresent(TemplateDragFormat) ||
                      e.Data.GetDataPresent(TileDragFormat);
        e.Effects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;

        if (!accepts || _document is null)
        {
            EditorCanvas.ClearDropMarker();
            return;
        }

        var point = EditorCanvas.ToDesign(e.GetPosition(EditorCanvas));
        var x = TileDocumentEditor.Snap(_document, Math.Max(0, point.X));
        var y = TileDocumentEditor.Snap(_document, Math.Max(0, point.Y));

        (double Width, double Height)? size = null;
        if (e.Data.GetData(WidgetPresetFormat) is string presetId &&
            TileElementLibrary.Find(presetId) is { } preset)
        {
            size = (preset.Width, preset.Height);
        }
        else if (e.Data.GetData(WidgetDragFormat) is string kindName &&
            Enum.TryParse<TileNodeKind>(kindName, out var kind))
        {
            size = TileDocumentEditor.DefaultSize(kind);
        }
        else if (e.Data.GetData(SourceDragFormat) is string)
        {
            size = TileDocumentEditor.DefaultSize(TileNodeKind.Metric);
        }
        else if (e.Data.GetData(TileDragFormat) is string nodeId &&
                 TileDocumentEditor.Find(_document, nodeId) is { } node)
        {
            size = (node.FrameWidth ?? node.Width ?? 180, node.FrameHeight ?? node.Height ?? 72);
        }

        if (e.Data.GetData(TileDragFormat) is string movingId &&
            TileDocumentEditor.Find(_document, movingId) is { } moving)
        {
            var container = EditorCanvas.FindContainerAt(point);
            if (container is not null &&
                !string.Equals(container.NodeId, moving.NodeId, StringComparison.Ordinal) &&
                !string.Equals(container.NodeId, Scope?.NodeId, StringComparison.Ordinal) &&
                !moving.Descendants().Any(descendant =>
                    string.Equals(descendant.NodeId, container.NodeId, StringComparison.Ordinal)) &&
                EditorCanvas.RectOf(container) is { } containerRect)
            {
                EditorCanvas.ShowDropMarker(containerRect.X, containerRect.Y,
                    containerRect.Width, containerRect.Height);
                return;
            }
        }

        if (size is { } footprint)
        {
            EditorCanvas.ShowDropMarker(x, y, footprint.Width, footprint.Height);
        }
        else
        {
            // A design template replaces the whole document, so a per-tile marker would mislead.
            EditorCanvas.ClearDropMarker();
        }
    }

    private void EditorCanvas_DragLeave(object sender, DragEventArgs e)
        => EditorCanvas.ClearDropMarker();

    private void EditorCanvas_Drop(object sender, DragEventArgs e)
    {
        if (_document is null)
        {
            return;
        }

        EditorCanvas.ClearDropMarker();

        var point = EditorCanvas.ToDesign(e.GetPosition(EditorCanvas));
        var x = TileDocumentEditor.Snap(_document, Math.Max(0, point.X));
        var y = TileDocumentEditor.Snap(_document, Math.Max(0, point.Y));

        if (e.Data.GetData(WidgetPresetFormat) is string presetId)
        {
            AddPreset(presetId, x, y);
            return;
        }

        if (e.Data.GetData(WidgetDragFormat) is string kindName &&
            Enum.TryParse<TileNodeKind>(kindName, out var kind))
        {
            AddTile(kind, x, y);
            return;
        }

        if (e.Data.GetData(SourceDragFormat) is string sourceKey)
        {
            AddSourceTile(sourceKey, TileNodeKind.Metric, x, y);
            return;
        }

        if (e.Data.GetData(TemplateDragFormat) is string templateId)
        {
            ApplyTemplate(templateId);
            return;
        }

        if (e.Data.GetData(TileDragFormat) is not string nodeId)
        {
            return;
        }

        var node = TileDocumentEditor.Find(_document, nodeId);
        if (node is null)
        {
            return;
        }

        var sameScope = string.Equals(TileDocumentEditor.ParentOf(_document, node.NodeId)?.NodeId,
            Scope?.NodeId, StringComparison.Ordinal);
        if (sameScope)
        {
            var target = EditorCanvas.FindContainerAt(point);
            if (target is not null &&
                !string.Equals(target.NodeId, node.NodeId, StringComparison.Ordinal) &&
                !string.Equals(target.NodeId, Scope?.NodeId, StringComparison.Ordinal) &&
                !node.Descendants().Any(descendant =>
                    string.Equals(descendant.NodeId, target.NodeId, StringComparison.Ordinal)) &&
                EditorCanvas.RectOf(target) is { } targetRect)
            {
                var moved = false;
                Apply(() => moved = TileDocumentEditor.Reparent(_document, node, target,
                    x - targetRect.X, y - targetRect.Y));
                if (moved)
                {
                    _selectedId = target.NodeId;
                    RefreshAll();
                    SetStatus(Loc.F("osd_ed_moved_into", target.Name ?? Loc.T("osd_kind_panel")));
                    return;
                }
            }
        }

        if (!sameScope)
        {
            return;
        }

        Apply(() => TileDocumentEditor.SetGeometry(node, x, y, null, null));
        _selectedId = node.NodeId;
        RefreshAll();
        SetStatus(Loc.T("osd_ed_moved"));
    }

    // ---------------------------------------------------------------- sources

    private void BuildSourcePanel()
    {
        SourceCategoryCombo.Items.Clear();
        SourceCategoryCombo.Items.Add(Loc.T("osd_ed_all_sources"));
        foreach (var category in TileSourceCatalog.Sources.Select(source => source.Category)
                     .Distinct(StringComparer.Ordinal).OrderBy(category => category,
                         StringComparer.Ordinal))
        {
            SourceCategoryCombo.Items.Add(category);
        }

        SourceCategoryCombo.SelectedIndex = 0;
        RebuildSourceList();
    }

    private void SourceFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
        {
            RebuildSourceList();
        }
    }

    private void SourceSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading)
        {
            RebuildSourceList();
        }
    }

    private void RebuildSourceList()
    {
        SourceList.Children.Clear();
        _filteredSources.Clear();
        var search = SourceSearch.Text.Trim();
        var category = SourceCategoryCombo.SelectedItem as string ?? Loc.T("osd_ed_all_sources");
        var allCategories = string.Equals(category, Loc.T("osd_ed_all_sources"), StringComparison.Ordinal);

        var matches = TileSourceCatalog.Sources.Where(source =>
            (allCategories || string.Equals(source.Category, category, StringComparison.Ordinal)) &&
            (search.Length == 0 ||
             source.Label.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             source.Key.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             source.Device.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             source.Unit.Contains(search, StringComparison.OrdinalIgnoreCase))).ToList();
        _filteredSources.AddRange(matches);

        var groups = TileSourceCatalog.Groups.Where(group =>
            (allCategories || string.Equals(group.Category, category, StringComparison.Ordinal)) &&
            (search.Length == 0 || group.Label.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        foreach (var group in groups)
        {
            SourceList.Children.Add(SourceRow(group.Label, group.Unit, group.Device, group.Id,
                TileNodeKind.Heatmap));
        }

        foreach (var source in matches)
        {
            SourceList.Children.Add(SourceRow(source.Label, source.Unit, source.Device, source.Key,
                TileNodeKind.Metric));
        }

        SourceSummary.Text = Loc.F("osd_ed_source_count", matches.Count, TileSourceCatalog.Sources.Count);
    }

    private Border SourceRow(string label, string unit, string device, string key, TileNodeKind kind)
    {
        var border = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(6, 4, 6, 4),
            Cursor = Cursors.Hand,
            ToolTip = Loc.T("osd_ed_source_tip"),
        };
        var panel = new DockPanel();
        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        panel.Children.Add(text);
        var caption = new TextBlock
        {
            Text = string.IsNullOrEmpty(unit) ? device : unit + " · " + device,
            Style = (Style)FindResource("Caption"),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        DockPanel.SetDock(caption, Dock.Right);
        panel.Children.Add(caption);
        border.Child = panel;
        border.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                AddSourceTile(key, kind);
            }
        };
        border.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragDrop.DoDragDrop(border, new DataObject(SourceDragFormat, key), DragDropEffects.Copy);
            }
        };
        return border;
    }

    // ---------------------------------------------------------------- drag onto canvas

    private void DeleteZone_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(TileDragFormat)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
        DeleteZone.BorderBrush = e.Effects == DragDropEffects.Move
            ? (Brush)FindResource("DangerBrush")
            : (Brush)FindResource("BorderBrush");
    }

    private void DeleteZone_DragLeave(object sender, DragEventArgs e)
        => DeleteZone.BorderBrush = (Brush)FindResource("BorderBrush");

    private void DeleteZone_Drop(object sender, DragEventArgs e)
    {
        DeleteZone.BorderBrush = (Brush)FindResource("BorderBrush");
        if (_document is null || e.Data.GetData(TileDragFormat) is not string nodeId)
        {
            return;
        }

        var node = TileDocumentEditor.Find(_document, nodeId);
        if (node is null || node.Locked == true)
        {
            return;
        }

        DeleteNode(node);
    }

    // ---------------------------------------------------------------- template gallery

    private void Templates_Click(object sender, RoutedEventArgs e)
    {
        if (!_galleryBuilt)
        {
            BuildTemplateGallery();
        }

        TemplateGallery.Visibility = Visibility.Visible;
        TemplateSearch.Focus();
    }

    private void CloseGallery_Click(object sender, RoutedEventArgs e)
        => TemplateGallery.Visibility = Visibility.Collapsed;

    private void BuildTemplateGallery()
    {
        _galleryBuilt = true;
        TemplateCategoryCombo.Items.Clear();
        foreach (var family in TileTemplateCatalog.Families)
        {
            TemplateCategoryCombo.Items.Add(family);
        }

        TemplateCategoryCombo.SelectedIndex = 0;
        RebuildTemplateCards();
    }

    private void TemplateSearch_TextChanged(object sender, TextChangedEventArgs e) => RebuildTemplateCards();

    private void TemplateFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_galleryBuilt)
        {
            RebuildTemplateCards();
        }
    }

    private void RebuildTemplateCards()
    {
        TemplatePanel.Children.Clear();
        var search = TemplateSearch.Text.Trim();
        var family = TemplateCategoryCombo.SelectedItem as string ??
                     TileTemplateCatalog.Families.FirstOrDefault() ?? "All categories";
        var all = string.Equals(family, TileTemplateCatalog.Families.FirstOrDefault(),
            StringComparison.OrdinalIgnoreCase);
        var templates = TileTemplateCatalog.All.Where(template =>
            (all || string.Equals(template.Family, family, StringComparison.OrdinalIgnoreCase)) &&
            (search.Length == 0 ||
             template.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             template.Family.Contains(search, StringComparison.OrdinalIgnoreCase))).ToList();

        foreach (var template in templates)
        {
            TemplatePanel.Children.Add(TemplateCard(template));
        }

        TemplateFooter.Text = Loc.F("osd_ed_template_count", templates.Count,
            TileTemplateCatalog.All.Count);
    }

    private Border TemplateCard(TileTemplate template)
    {
        var card = new Border
        {
            Width = 258,
            Height = 240,
            Margin = new Thickness(0, 0, 12, 12),
            Background = (Brush)FindResource("FieldBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10),
            Cursor = Cursors.Hand,
        };
        var stack = new StackPanel();
        var image = new Image
        {
            Height = 90,
            Stretch = Stretch.Uniform,
            Source = template.IsBlank
                ? null
                : TileThumbnailFactory.Get(template.Id, 236, 90),
        };
        stack.Children.Add(image);
        stack.Children.Add(new TextBlock
        {
            Text = template.Name,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 2),
        });
        stack.Children.Add(new TextBlock
        {
            Text = template.Family,
            Style = (Style)FindResource("Caption"),
        });
        stack.Children.Add(new TextBlock
        {
            Text = template.Description,
            Style = (Style)FindResource("Muted"),
            Margin = new Thickness(0, 4, 0, 8),
            MaxHeight = 52,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var use = new Button
        {
            Content = Loc.T("osd_ed_use_template"),
            Style = (Style)FindResource("PrimaryButton"),
        };
        AutomationProperties.SetAutomationId(use, "OsdTemplateUse_" + template.Id);
        AutomationProperties.SetName(use, Loc.T("osd_ed_use_template") + " " + template.Name);
        use.Click += (_, _) => ApplyTemplate(template.Id);
        stack.Children.Add(use);
        card.Child = stack;
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                ApplyTemplate(template.Id);
            }
        };
        card.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragDrop.DoDragDrop(card, new DataObject(TemplateDragFormat, template.Id),
                    DragDropEffects.Copy);
            }
        };
        return card;
    }

    private void ApplyTemplate(string templateId)
    {
        if (_document is null)
        {
            return;
        }

        var document = TileTemplateCatalog.CreateDocument(templateId, _profile.Name);
        if (document is null)
        {
            return;
        }

        document.Id = _document.Id;
        document.Name = _profile.Name;
        var before = CaptureSnapshot();
        _document = document;
        _undo.Push(before);
        _redo.Clear();
        _scopeId = null;
        _selectedId = _document.Root.Children.FirstOrDefault()?.NodeId;
        _dirty = true;
        TemplateGallery.Visibility = Visibility.Collapsed;
        RefreshAll();
        SetStatus(Loc.T("osd_ed_template_applied"));
    }

    // ---------------------------------------------------------------- inspector

    private void TileTab_Click(object sender, RoutedEventArgs e)
    {
        if (!_settingInspectorTab) SetTab(InspectorTab.Tile);
    }
    private void LayoutTab_Click(object sender, RoutedEventArgs e)
    {
        if (!_settingInspectorTab) SetTab(InspectorTab.Layout);
    }
    private void StyleTab_Click(object sender, RoutedEventArgs e)
    {
        if (!_settingInspectorTab) SetTab(InspectorTab.Style);
    }

    private void SetTab(InspectorTab tab)
    {
        _settingInspectorTab = true;
        try
        {
            _tab = tab;
            TileTab.IsChecked = tab == InspectorTab.Tile;
            LayoutTab.IsChecked = tab == InspectorTab.Layout;
            StyleTab.IsChecked = tab == InspectorTab.Style;
        }
        finally
        {
            _settingInspectorTab = false;
        }

        BuildInspector();
    }

    private void BuildInspector()
    {
        InspectorPanel.Children.Clear();
        if (_document is null)
        {
            InspectorPanel.Children.Add(new TextBlock
            {
                Text = Loc.T("osd_ed_row_profile_hint"),
                Style = (Style)FindResource("Muted"),
            });
            return;
        }

        switch (_tab)
        {
            case InspectorTab.Layout:
                BuildLayoutInspector();
                break;
            case InspectorTab.Style:
                BuildStyleInspector();
                break;
            default:
                BuildTileInspector();
                break;
        }
    }

    private void BuildTileInspector()
    {
        var selected = Selected;
        if (selected is null)
        {
            InspectorPanel.Children.Add(new TextBlock
            {
                Text = Loc.T("osd_ed_select_hint"),
                Style = (Style)FindResource("Muted"),
                Margin = new Thickness(0, 4, 0, 8),
            });
            AddDocumentSummary(InspectorPanel);
            return;
        }

        var widget = TileDocumentEditor.WidgetOf(selected);
        var chrome = selected.NodeKind == TileNodeKind.Panel ? selected : widget;

        var lockBox = new CheckBox
        {
            Content = Loc.T("osd_ed_lock"),
            IsChecked = selected.Locked == true,
            Margin = new Thickness(0, 2, 0, 6),
        };
        lockBox.Checked += (_, _) => SetLock(selected, true);
        lockBox.Unchecked += (_, _) => SetLock(selected, false);
        InspectorPanel.Children.Add(lockBox);

        if (widget.NodeKind == TileNodeKind.Panel)
        {
            // A container is not a widget kind; show it read-only instead of an empty combo that
            // would silently convert the container into a leaf.
            var grid = FieldGrid(Loc.T("osd_ed_widget_kind"), out _);
            var value = new TextBlock
            {
                Text = KindName(TileNodeKind.Panel),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("MutedBrush"),
            };
            Grid.SetColumn(value, 1);
            Grid.SetColumnSpan(value, 2);
            grid.Children.Add(value);
            InspectorPanel.Children.Add(grid);
        }
        else
        {
            AddComboRow<TileNodeKind>(InspectorPanel, Loc.T("osd_ed_widget_kind"),
                Enum.GetValues<TileNodeKind>()
                    .Where(kind => kind != TileNodeKind.Panel)
                    .Select(kind => (KindName(kind), kind))
                    .ToList(),
                widget.NodeKind, kind => ChangeWidgetKind(selected, widget, kind));
        }

        if (selected.NodeKind == TileNodeKind.Panel)
        {
            var container = AddExpander(InspectorPanel, Loc.T("osd_ed_container"), expanded: true);
            AddComboRow(container, Loc.T("osd_ed_container_layout"),
                new List<(string, string)>
                {
                    (Loc.T("osd_ed_layout_vertical"), "vertical"),
                    (Loc.T("osd_ed_layout_horizontal"), "horizontal"),
                    (Loc.T("osd_ed_layout_grid"), "grid"),
                    (Loc.T("osd_ed_layout_canvas"), "canvas"),
                },
                (widget.Layout ?? "canvas").ToLowerInvariant(),
                value => widget.Layout = value,
                "OsdContainerLayoutCombo");
            AddNumberRow(container, Loc.T("osd_ed_container_padding"), widget.Padding ?? 0, 0, 40,
                value => widget.Padding = value);
            AddNumberRow(container, Loc.T("osd_ed_gap"), widget.Gap ?? 0, 0, 40,
                value => widget.Gap = value);
            AddNumberRow(container, Loc.T("osd_ed_columns"), widget.Columns ?? 1, 1, 12,
                value => widget.Columns = (int)Math.Round(value), "0");
            AddTextBlock(container, Loc.T("osd_ed_container_hint"));
        }

        if (TileDocumentEditor.HasEditableChildren(selected))
        {
            var contents = new Button
            {
                Content = Loc.T("osd_ed_edit_contents"),
                Style = (Style)FindResource("FlatButton"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 4, 0, 4),
            };
            AutomationProperties.SetAutomationId(contents, "OsdEditContentsButton");
            contents.Click += (_, _) => EnterContainer(selected);
            InspectorPanel.Children.Add(contents);
            InspectorPanel.Children.Add(new TextBlock
            {
                Text = Loc.F("osd_ed_contents_hint",
                    TileDocumentEditor.ResolveEditableScope(selected).Children.Count
                        .ToString(CultureInfo.InvariantCulture)),
                Style = (Style)FindResource("Muted"),
                Margin = new Thickness(0, 0, 0, 6),
            });
        }

        AddDivider(InspectorPanel);

        AddTextRow(InspectorPanel, Loc.T("osd_ed_name"),
            selected.Name ?? DefaultTileName(selected), 60,
            value => selected.Name = string.IsNullOrWhiteSpace(value) ? null : value.Trim());

        var isChart = widget.NodeKind == TileNodeKind.Chart;
        var isText = widget.NodeKind == TileNodeKind.Text;
        var isIcon = widget.NodeKind == TileNodeKind.Icon;
        var isImage = widget.NodeKind == TileNodeKind.Image;
        var isGauge = widget.NodeKind == TileNodeKind.Gauge;
        var isRows = widget.NodeKind == TileNodeKind.ClassicRows;
        var hasSource = widget.NodeKind is TileNodeKind.Metric or TileNodeKind.Bar or TileNodeKind.Gauge;
        var hasLabel = widget.NodeKind is TileNodeKind.Metric or TileNodeKind.Bar or TileNodeKind.Gauge;

        AddCheckRow(InspectorPanel, Loc.T("osd_visible"), widget.Visible != false,
            value => widget.Visible = value);

        if (hasLabel)
        {
            AddTextRow(InspectorPanel, Loc.T("osd_ed_label"), widget.Label ?? string.Empty, 120,
                value => widget.Label = value);
        }

        if (isText)
        {
            AddMultilineRow(InspectorPanel, Loc.T("osd_ed_text"), widget.Text ?? string.Empty,
                value => widget.Text = value);
        }

        if (hasSource)
        {
            AddSourcePicker(InspectorPanel, widget);
            AddTextRow(InspectorPanel, Loc.T("osd_ed_unit"), widget.Unit ?? string.Empty, 32,
                value => widget.Unit = value);
            AddNumberRow(InspectorPanel, Loc.T("osd_ed_decimals"), widget.Digits ?? 1, 0, 3,
                value => widget.Digits = (int)Math.Round(value), "0");
            AddTextRow(InspectorPanel, Loc.T("osd_ed_format"), widget.Format ?? string.Empty, 24,
                value => widget.Format = string.IsNullOrWhiteSpace(value) ? null : value.Trim());
            AddCheckRow(InspectorPanel, Loc.T("osd_ed_show_value"), widget.ShowValue != false,
                value => widget.ShowValue = value);
        }
        else if (isChart)
        {
            var chartSources = new List<(string Label, string Series, string Unit)>
            {
                ("FPS", "frame.fps", "FPS"),
                ("Frame time", "frame.time", "ms"),
                ("CPU temperature", "cpu.temp", "°C"),
                ("CPU load", "cpu.usage", "%"),
                ("CPU clock", "cpu.clock", "MHz"),
                ("CPU power", "cpu.power", "W"),
                ("GPU temperature", "gpu.temp", "°C"),
                ("GPU load", "gpu.usage", "%"),
                ("GPU clock", "gpu.clock", "MHz"),
                ("GPU power", "gpu.power", "W"),
                ("RAM load", "ram.percent", "%"),
                ("VRAM load", "vram.percent", "%"),
            };
            AddComboRow(InspectorPanel, Loc.T("osd_ed_chart_source"),
                chartSources.Select(source => (source.Label, source.Series)).ToList(),
                (widget.Series ?? "frame.time").Trim().ToLowerInvariant() switch
                {
                    "frametimes" or "frametime" => "frame.time",
                    "framerates" or "framerate" or "framespersecond" => "frame.fps",
                    var series when series.Length > 0 => series,
                    _ => "frame.time",
                },
                value =>
                {
                    widget.Series = value;
                    widget.Lines = null;
                    var selected = chartSources.FirstOrDefault(source =>
                        string.Equals(source.Series, value, StringComparison.Ordinal));
                    if (!string.IsNullOrWhiteSpace(selected.Label))
                    {
                        widget.Title = selected.Label;
                        widget.Unit = selected.Unit;
                    }
                },
                "OsdChartSourceCombo");
            AddChartLines(InspectorPanel, widget);
            AddTextRow(InspectorPanel, Loc.T("osd_ed_unit"), widget.Unit ?? string.Empty, 32,
                value => widget.Unit = value);
        }
        else if (isIcon)
        {
            AddTextRow(InspectorPanel, Loc.T("osd_ed_glyph"), widget.Glyph ?? string.Empty, 8,
                value => widget.Glyph = value);
            AddSliderRow(InspectorPanel, Loc.T("osd_ed_icon_size"), widget.Size ?? 24, 10, 72,
                value => widget.Size = value);
        }
        else if (isImage)
        {
            AddImageSection(InspectorPanel, widget);
        }
        else if (isRows)
        {
            AddSliderRow(InspectorPanel, Loc.T("osd_ed_row_scale"), widget.RowScale ?? 1, 0.5, 2,
                value => widget.RowScale = value);
            AddCheckRow(InspectorPanel, Loc.T("osd_ed_auto_height"),
                widget.ClassicRowsAutoHeight == true,
                value => widget.ClassicRowsAutoHeight = value);
            AddTextBlock(InspectorPanel, Loc.T("osd_ed_rows_hint"));
        }

        if (widget.NodeKind == TileNodeKind.Heatmap)
        {
            AddTextBlock(InspectorPanel, Loc.F("osd_ed_heatmap_cells",
                (widget.Keys?.Count ?? 0).ToString(CultureInfo.InvariantCulture)));
        }

        AddDivider(InspectorPanel);
        BuildGeometrySection(selected);

        if (hasLabel)
        {
            AddComboRow(InspectorPanel, Loc.T("osd_ed_label_position"),
                new List<(string, string)>
                {
                    (Loc.T("osd_ed_top"), "top"),
                    (Loc.T("osd_ed_left"), "left"),
                    (Loc.T("osd_ed_bottom"), "bottom"),
                    (Loc.T("osd_ed_hidden"), "hidden"),
                },
                (widget.LabelPosition ?? "top").ToLowerInvariant(),
                value => widget.LabelPosition = value);
            AddNumberRow(InspectorPanel, Loc.T("osd_ed_text_lines"), widget.ValueLines ?? 1, 1, 16,
                value => widget.ValueLines = (int)Math.Round(value), "0");
        }

        if (widget.NodeKind == TileNodeKind.Text)
        {
            AddSliderRow(InspectorPanel, Loc.T("osd_ed_value_size"),
                widget.Size ?? 14, 10, 72, value => widget.Size = value);
        }
        else if (widget.NodeKind is TileNodeKind.Metric or TileNodeKind.Bar or TileNodeKind.Gauge)
        {
            AddSliderRow(InspectorPanel, Loc.T("osd_ed_value_size"),
                widget.ValueSize ?? widget.Size ?? 16, 10, 72, value => widget.ValueSize = value);
            if (widget.NodeKind == TileNodeKind.Metric)
            {
                AddSliderRow(InspectorPanel, Loc.T("osd_ed_label_size"),
                    widget.LabelSize ?? widget.Size ?? 12, 10, 72, value => widget.LabelSize = value);
            }
        }

        if (widget.NodeKind == TileNodeKind.Metric)
        {
            var graph = AddExpander(InspectorPanel, Loc.T("osd_ed_chart"), expanded: false);
            AddCheckRow(graph, Loc.T("osd_ed_show_graph"), widget.MiniGraph == true,
                value =>
                {
                    widget.MiniGraph = value;
                    if (value && string.IsNullOrWhiteSpace(widget.Series))
                    {
                        var metric = OsdMetricBridge.DefinitionForTileKey(widget.Key);
                        widget.Series = metric is null ? null : MetricCatalog.SensorIdFor(metric);
                    }
                });
            AddColorRow(graph, Loc.T("osd_ed_line_color"), widget.GraphColor ?? widget.Color ?? string.Empty,
                value => widget.GraphColor = string.IsNullOrWhiteSpace(value) ? null : value);
            AddSliderRow(graph, Loc.T("osd_ed_line_thickness"), widget.Thickness ?? 1.5, 1, 6,
                value => widget.Thickness = value);
            AddSliderRow(graph, Loc.T("osd_ed_opacity"), (widget.Opacity ?? 0.9) * 100, 10, 100,
                value => widget.Opacity = value / 100);
            AddNumberRow(graph, Loc.T("osd_ed_history_points"), widget.HistoryPoints ?? 60, 10, 240,
                value => widget.HistoryPoints = (int)Math.Round(value), "0");
        }

        if (widget.NodeKind is TileNodeKind.Metric or TileNodeKind.Bar or TileNodeKind.Gauge ||
            widget.NodeKind == TileNodeKind.Heatmap)
        {
            AddNumberRow(InspectorPanel, Loc.T("osd_ed_min"), widget.EffectiveMinimum, -100000, 100000,
                value => widget.Min = value);
            AddNumberRow(InspectorPanel, Loc.T("osd_ed_max"), widget.EffectiveMaximum, -100000, 100000,
                value => widget.Max = value);
        }

        if (isGauge)
        {
            var arc = AddExpander(InspectorPanel, Loc.T("osd_ed_gauge"), expanded: false);
            AddNumberRow(arc, Loc.T("osd_ed_start_angle"), widget.StartAngle ?? 140, 0, 360,
                value => widget.StartAngle = value);
            AddNumberRow(arc, Loc.T("osd_ed_sweep_angle"), widget.SweepAngle ?? 260, 10, 360,
                value => widget.SweepAngle = value);
            AddNumberRow(arc, Loc.T("osd_ed_thickness"), widget.Thickness ?? 6, 1, 32,
                value => widget.Thickness = value);
            AddCheckRow(arc, Loc.T("osd_ed_show_value"), widget.ShowValue != false,
                value => widget.ShowValue = value);
        }

        if (isChart)
        {
            var chart = AddExpander(InspectorPanel, Loc.T("osd_ed_chart"), expanded: false);
            AddColorRow(chart, Loc.T("osd_ed_line_color"), widget.Color ?? string.Empty,
                value => widget.Color = string.IsNullOrWhiteSpace(value) ? null : value);
            AddSliderRow(chart, Loc.T("osd_ed_line_thickness"), widget.Thickness ?? 2, 1, 8,
                value => widget.Thickness = value);
            AddSliderRow(chart, Loc.T("osd_ed_opacity"), (widget.Opacity ?? 1) * 100, 10, 100,
                value => widget.Opacity = value / 100);
            AddNumberRow(chart, Loc.T("osd_ed_history_points"), widget.HistoryPoints ?? 90, 10, 240,
                value => widget.HistoryPoints = (int)Math.Round(value), "0");
            AddCheckRow(chart, Loc.T("osd_ed_show_background"), widget.ShowBackground == true,
                value => widget.ShowBackground = value);
            AddCheckRow(chart, Loc.T("osd_ed_show_border"), widget.ShowBorder == true,
                value => widget.ShowBorder = value);
            AddCheckRow(chart, Loc.T("osd_ed_show_labels"), widget.ShowLabels != false,
                value => widget.ShowLabels = value);
            AddCheckRow(chart, Loc.T("osd_ed_show_grid"), widget.ShowGridLines == true,
                value => widget.ShowGridLines = value);
            AddCheckRow(chart, Loc.T("osd_ed_current_value"), widget.CurrentValue == true,
                value => widget.CurrentValue = value);
        }

        var typography = AddExpander(InspectorPanel, Loc.T("osd_ed_typography"), expanded: false);
        AddComboRow(typography, Loc.T("osd_ed_align"),
            new List<(string, string)>
            {
                (Loc.T("osd_ed_align_left"), "left"),
                (Loc.T("osd_ed_align_center"), "center"),
                (Loc.T("osd_ed_align_right"), "right"),
            },
            (widget.Align ?? "left").ToLowerInvariant(), value => widget.Align = value);
        AddComboRow(typography, Loc.T("osd_ed_weight"),
            new List<(string, string)>
            {
                (Loc.T("osd_ed_normal"), ""),
                (Loc.T("osd_ed_semibold"), "semibold"),
                (Loc.T("osd_ed_bold"), "bold"),
            },
            (widget.Weight ?? string.Empty).ToLowerInvariant(),
            value => widget.Weight = string.IsNullOrEmpty(value) ? null : value,
            "OsdWeightCombo");
        AddTextRow(typography, Loc.T("osd_ed_font_family"), widget.Family ?? string.Empty, 80,
            value => widget.Family = string.IsNullOrWhiteSpace(value) ? null : value.Trim());

        var style = AddExpander(InspectorPanel, Loc.T("osd_ed_tile_style"), expanded: true);
        AddColorRow(style, Loc.T("osd_ed_background"),
            chrome.Background == true ? chrome.BackgroundColor ?? chrome.Color ?? string.Empty : string.Empty,
            value =>
            {
                chrome.Background = !string.IsNullOrWhiteSpace(value);
                chrome.BackgroundColor = string.IsNullOrWhiteSpace(value) ? null : value;
            });
        AddColorRow(style, Loc.T("osd_ed_border"), chrome.BorderColor ?? string.Empty,
            value => chrome.BorderColor = string.IsNullOrWhiteSpace(value) ? null : value);
        AddNumberRow(style, Loc.T("osd_ed_border_width"), chrome.BorderWidth ?? 0, 0, 16,
            value => chrome.BorderWidth = value);
        AddNumberRow(style, Loc.T("osd_ed_radius"), chrome.Radius ?? _document!.Theme.Radius, 0, 32,
            value => chrome.Radius = value);
        if (hasLabel)
        {
            AddColorRow(style, Loc.T("osd_ed_label_color"), widget.LabelColor ?? string.Empty,
                value => widget.LabelColor = string.IsNullOrWhiteSpace(value) ? null : value);
        }

        AddColorRow(style, Loc.T("osd_ed_value_color"), widget.ValueColor ?? string.Empty,
            value => widget.ValueColor = string.IsNullOrWhiteSpace(value) ? null : value);
        AddColorRow(style, Loc.T("osd_ed_unit_color"), widget.UnitColor ?? string.Empty,
            value => widget.UnitColor = string.IsNullOrWhiteSpace(value) ? null : value);
        AddColorRow(style, Loc.T("osd_ed_text_color"), widget.TextColor ?? widget.Color ?? string.Empty,
            value => widget.TextColor = string.IsNullOrWhiteSpace(value) ? null : value);

        var reset = new Button
        {
            Content = Loc.T("osd_ed_reset_colors"),
            Style = (Style)FindResource("FlatButton"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 0),
        };
        reset.Click += (_, _) => ResetColors(selected, widget, chrome);
        style.Children.Add(reset);

        var applyKind = new Button
        {
            Content = Loc.T("osd_ed_apply_kind"),
            Style = (Style)FindResource("FlatButton"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0),
        };
        applyKind.Click += (_, _) => ApplyStyleToKind(selected, widget);
        style.Children.Add(applyKind);

        AddTextBlock(InspectorPanel, Loc.T("osd_ed_selection_hint"));
    }

    private void AddDocumentSummary(Panel panel)
    {
        if (_document is null)
        {
            return;
        }

        var details = Loc.F("osd_ed_document_summary",
            _document.CanvasWidth.ToString("0", CultureInfo.InvariantCulture),
            _document.CanvasHeight.ToString("0", CultureInfo.InvariantCulture),
            TileDocumentEditor.CountWidgets(_document.Root).ToString(CultureInfo.InvariantCulture));
        AddTextBlock(panel, details);
        if (!string.IsNullOrWhiteSpace(_document.Family))
        {
            AddTextBlock(panel, _document.Family);
        }
    }

    private void BuildGeometrySection(TileNode selected)
    {
        if (_document is null)
        {
            return;
        }

        AddTextBlock(InspectorPanel, Loc.T("osd_ed_position"));
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());

        var x = NumberBox(selected.X ?? 0, 0, 4096, value => SetGeometry(selected, x: value));
        var y = NumberBox(selected.Y ?? 0, 0, 4096, value => SetGeometry(selected, y: value));
        var w = NumberBox(selected.FrameWidth ?? selected.Width ?? 0, TileDocumentEditor.MinTileSize,
            4096, value => SetGeometry(selected, width: value));
        var h = NumberBox(selected.FrameHeight ?? selected.Height ?? 0, TileDocumentEditor.MinTileSize,
            4096, value => SetGeometry(selected, height: value));
        System.Windows.Automation.AutomationProperties.SetAutomationId(x, "OsdGeometryX");
        System.Windows.Automation.AutomationProperties.SetAutomationId(y, "OsdGeometryY");
        System.Windows.Automation.AutomationProperties.SetAutomationId(w, "OsdGeometryWidth");
        System.Windows.Automation.AutomationProperties.SetAutomationId(h, "OsdGeometryHeight");

        AddLabelledField(grid, 0, 0, Loc.T("osd_ed_x"), x);
        AddLabelledField(grid, 0, 1, Loc.T("osd_ed_y"), y);
        AddLabelledField(grid, 1, 0, Loc.T("osd_ed_width"), w);
        AddLabelledField(grid, 1, 1, Loc.T("osd_ed_height"), h);
        InspectorPanel.Children.Add(grid);
    }

    private void SetGeometry(TileNode node, double? x = null, double? y = null, double? width = null,
        double? height = null)
    {
        Apply(() => TileDocumentEditor.SetGeometry(node, x, y, width, height));
        FitZoom();
        RefreshTileList();
        UpdateChrome();
    }

    private void AddLabelledField(Grid grid, int row, int column, string label, FrameworkElement field)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 6, 4) };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            Style = (Style)FindResource("FieldLabel"),
        });
        panel.Children.Add(field);
        Grid.SetRow(panel, row);
        Grid.SetColumn(panel, column);
        grid.Children.Add(panel);
    }

    private TextBox NumberBox(double value, double min, double max, Action<double> set)
    {
        var box = new TextBox
        {
            Text = value.ToString("0.##", CultureInfo.InvariantCulture),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 70,
        };
        var committed = value;
        void Commit()
        {
            if (!TryParse(box.Text, out var parsed))
            {
                box.Text = committed.ToString("0.##", CultureInfo.InvariantCulture);
                return;
            }

            parsed = Math.Clamp(parsed, min, max);
            box.Text = parsed.ToString("0.##", CultureInfo.InvariantCulture);
            if (Math.Abs(parsed - committed) < 0.0001)
            {
                return;
            }

            committed = parsed;
            set(parsed);
        }

        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                e.Handled = true;
            }
        };
        return box;
    }

    private void BuildLayoutInspector()
    {
        if (_document is null)
        {
            return;
        }

        AddTextRow(InspectorPanel, Loc.T("osd_ed_design_name"), _document.Name, 80,
            value =>
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return;
                }

                _document!.Name = value.Trim();
                _profile.Name = value.Trim();
                ProfileNameBox.Text = _profile.Name;
                RefreshTileList();
            });

        AddDivider(InspectorPanel);
        AddSectionTitle(InspectorPanel, Loc.T("osd_ed_grid"));
        AddNumberRow(InspectorPanel, Loc.T("osd_ed_grid_size"), _document.GridSize, 1, 128,
            value => _document!.GridSize = value);
        AddCheckRow(InspectorPanel, Loc.T("osd_ed_snap"), _document.SnapToGrid,
            value => _document!.SnapToGrid = value);
        AddCheckRow(InspectorPanel, Loc.T("osd_ed_show_grid_toggle"), _document.ShowGrid,
            value => _document!.ShowGrid = value);
        AddTextBlock(InspectorPanel, Loc.T("osd_ed_layout_hint"));

        AddDivider(InspectorPanel);
        AddSectionTitle(InspectorPanel, Loc.T("osd_ed_canvas"));
        AddNumberRow(InspectorPanel, Loc.T("osd_ed_width"), _document.CanvasWidth,
            TileDocumentEditor.MinDesignWidth, TileDocumentEditor.MaxDesignWidth,
            value =>
            {
                TileDocumentEditor.SetCanvasSize(_document, value, null);
                FitZoom();
            });
        AddNumberRow(InspectorPanel, Loc.T("osd_ed_canvas_height"), _document.CanvasHeight,
            TileDocumentEditor.MinCanvasHeight, TileDocumentEditor.MaxCanvasHeight,
            value =>
            {
                TileDocumentEditor.SetCanvasSize(_document, null, value);
                FitZoom();
            });
        var fit = new Button
        {
            Content = Loc.T("osd_ed_fit_canvas"),
            Style = (Style)FindResource("FlatButton"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0),
        };
        fit.Click += (_, _) =>
        {
            Apply(() => TileDocumentEditor.FitCanvasToContent(_document!));
            FitZoom();
            BuildInspector();
            SetStatus(Loc.T("osd_ed_canvas_fitted"));
        };
        InspectorPanel.Children.Add(fit);

        AddDivider(InspectorPanel);
        AddSectionTitle(InspectorPanel, Loc.T("osd_ed_screen"));
        AddCheckRow(InspectorPanel, Loc.T("osd_ed_full_width"), _document.FullWidth,
            value => _document!.FullWidth = value, "OsdFullWidthToggle");
        AddComboRow(InspectorPanel, Loc.T("osd_ed_edge"), new[]
            {
                (Loc.T("osd_ed_edge_placement"), "placement"),
                (Loc.T("osd_ed_edge_top"), "top"),
                (Loc.T("osd_ed_edge_bottom"), "bottom"),
            }, _document.Edge, value => _document!.Edge = value, "OsdEdgeCombo");
        AddTextBlock(InspectorPanel, Loc.T("osd_ed_full_width_hint"));

        AddDivider(InspectorPanel);
        AddSectionTitle(InspectorPanel, Loc.T("osd_ed_spacing"));
        AddSliderRow(InspectorPanel, Loc.T("osd_ed_scale"), _document.Scale, 0.5, 2,
            value => _document!.Scale = value);
        AddSliderRow(InspectorPanel, Loc.T("osd_ed_gap"), _document.Theme.Gap, 0, 40,
            value => _document!.Theme.Gap = value);
        AddSliderRow(InspectorPanel, Loc.T("osd_ed_padding"), _document.Theme.Pad, 0, 40,
            value => _document!.Theme.Pad = value);
        AddSliderRow(InspectorPanel, Loc.T("osd_ed_radius"), _document.Theme.Radius, 0, 32,
            value => _document!.Theme.Radius = value);
    }

    /// <summary>
    /// Global font-size control for every design, including CapFrameX designs that carry explicit
    /// per-tile sizes. Text and geometry scale together so the layout keeps its proportions; the
    /// per-tile Typography sliders can still fine-tune one widget afterwards.
    /// </summary>
    private void ScaleDocumentTypography(double newSize)
    {
        if (_document is null)
        {
            return;
        }

        var oldSize = _document.Theme.FontSize <= 0 ? 14 : _document.Theme.FontSize;
        var ratio = Math.Clamp(newSize / oldSize, 0.5, 2.5);
        _document.Theme.FontSize = Math.Clamp(newSize, 8, 72);
        _document.Theme.TitleSize = Math.Clamp(_document.Theme.TitleSize * ratio, 8, 72);
        _document.Theme.Pad = Math.Clamp(_document.Theme.Pad * ratio, 0, 80);
        _document.Theme.Gap = Math.Clamp(_document.Theme.Gap * ratio, 0, 80);
        foreach (var node in _document.Root.Descendants().Prepend(_document.Root))
        {
            node.FrameWidth = ScaleNullable(node.FrameWidth, ratio);
            node.Width = ScaleNullable(node.Width, ratio);
            node.FrameHeight = ScaleNullable(node.FrameHeight, ratio);
            node.Height = ScaleNullable(node.Height, ratio);
            node.X = ScaleNullable(node.X, ratio);
            node.Y = ScaleNullable(node.Y, ratio);
            node.Size = ScaleNullable(node.Size, ratio);
            node.ValueSize = ScaleNullable(node.ValueSize, ratio);
            node.LabelSize = ScaleNullable(node.LabelSize, ratio);
            node.UnitSize = ScaleNullable(node.UnitSize, ratio);
            node.Diameter = ScaleNullable(node.Diameter, ratio);
            node.Thickness = ScaleNullable(node.Thickness, ratio);
            node.Padding = ScaleNullable(node.Padding, ratio);
            node.Gap = ScaleNullable(node.Gap, ratio);
            node.Radius = ScaleNullable(node.Radius, ratio);
            node.BorderWidth = ScaleNullable(node.BorderWidth, ratio);
        }
    }

    private static double? ScaleNullable(double? value, double ratio)
        => value is { } number && double.IsFinite(number) ? number * ratio : value;

    private void BuildStyleInspector()
    {
        if (_document is null)
        {
            return;
        }

        AddTextBlock(InspectorPanel, Loc.T("osd_ed_style_hint"));
        AddDivider(InspectorPanel);
        AddSectionTitle(InspectorPanel, Loc.T("osd_ed_font"));
        AddFontRow(InspectorPanel, _document.Theme.FontFamily,
            value => _document!.Theme.FontFamily = value);
        AddSliderRow(InspectorPanel, Loc.T("osd_ed_font_size"), _document.Theme.FontSize, 8, 72,
            value => ScaleDocumentTypography(value));
        AddSliderRow(InspectorPanel, Loc.T("osd_ed_title_size"), _document.Theme.TitleSize, 8, 72,
            value => _document!.Theme.TitleSize = value);
        AddTextBlock(InspectorPanel, Loc.T("osd_ed_font_hint"));

        AddDivider(InspectorPanel);
        AddSectionTitle(InspectorPanel, Loc.T("osd_ed_palette"));
        AddColorRow(InspectorPanel, Loc.T("osd_ed_panel_color"), _document.Theme.Panel,
            value => _document!.Theme.Panel = value);
        AddColorRow(InspectorPanel, Loc.T("osd_ed_accent_color"), _document.Theme.Accent,
            value => _document!.Theme.Accent = value);
        AddColorRow(InspectorPanel, Loc.T("osd_ed_text_default"), _document.Theme.Text,
            value => _document!.Theme.Text = value);
        AddColorRow(InspectorPanel, Loc.T("osd_ed_subtext_color"), _document.Theme.Subtext,
            value => _document!.Theme.Subtext = value);
    }

    private void ChangeWidgetKind(TileNode tile, TileNode widget, TileNodeKind kind)
    {
        if (_document is null || widget.NodeKind == kind)
        {
            return;
        }

        Apply(() =>
        {
            var replacement = TileDocumentEditor.CreateLeaf(kind, _document.Theme);
            replacement.NodeId = widget.NodeId;
            replacement.Label = widget.Label;
            replacement.Unit = widget.Unit;
            if (tile.NodeId == widget.NodeId)
            {
                var owner = TileDocumentEditor.ParentOf(_document, widget.NodeId) ?? _document.Root;
                var position = owner.Children.IndexOf(widget);
                if (position >= 0)
                {
                    owner.Children[position] = replacement;
                }
            }
            else
            {
                var index = tile.Children.IndexOf(widget);
                if (index >= 0)
                {
                    tile.Children[index] = replacement;
                }
            }
        });
        RefreshAll();
        SetStatus(Loc.T("osd_ed_widget_changed"));
    }

    private void AddSourcePicker(Panel panel, TileNode widget)
    {
        AddSectionTitle(panel, Loc.T("osd_ed_source"));
        var search = new TextBox
        {
            Margin = new Thickness(0, 2, 0, 4),
            ToolTip = Loc.T("osd_ed_source_search_tip"),
        };
        var combo = new ComboBox { MaxDropDownHeight = 320 };
        void Fill()
        {
            combo.Items.Clear();
            var query = search.Text.Trim();
            foreach (var source in TileSourceCatalog.Sources.Where(source =>
                         query.Length == 0 ||
                         source.Label.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                         source.Key.Contains(query, StringComparison.OrdinalIgnoreCase)))
            {
                combo.Items.Add(new ComboBoxItem
                {
                    Content = source.Label + (string.IsNullOrEmpty(source.Unit) ? string.Empty : " · " + source.Unit),
                    Tag = source.Key,
                });
            }

            var selected = combo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, widget.Key, StringComparison.Ordinal));
            combo.SelectedItem = selected;
        }

        search.TextChanged += (_, _) => Fill();
        Fill();
        combo.SelectionChanged += (_, _) =>
        {
            if (_loading || combo.SelectedItem is not ComboBoxItem item || item.Tag is not string key ||
                string.Equals(key, widget.Key, StringComparison.Ordinal))
            {
                return;
            }

            var source = TileSourceCatalog.Find(key);
            Apply(() =>
            {
                widget.Key = key;
                if (source is not null)
                {
                    widget.Label = source.Label;
                    if (string.IsNullOrWhiteSpace(widget.Unit))
                    {
                        widget.Unit = source.Unit;
                    }
                }
            });
            RefreshScene();
        };

        panel.Children.Add(search);
        panel.Children.Add(combo);
        var status = string.IsNullOrWhiteSpace(widget.Key)
            ? Loc.T("osd_ed_source_none")
            : Loc.T("osd_ed_source_bound");
        AddTextBlock(panel, status);
    }

    private void AddChartLines(Panel panel, TileNode widget)
    {
        AddSectionTitle(panel, Loc.T("osd_ed_series"));
        AddTextRow(panel, Loc.T("osd_ed_series"), widget.Series ?? "frametimes", 40,
            value => widget.Series = value);

        var lines = widget.Lines ?? new List<TileChartLine>();
        foreach (var line in lines.ToList())
        {
            var captured = line;
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = (captured.Label ?? captured.Series ?? captured.Kind ?? "line") + " · " +
                       (captured.Color ?? "auto"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var remove = new Button
            {
                Content = "×",
                Style = (Style)FindResource("ToolButton"),
                Margin = new Thickness(6, 0, 0, 0),
            };
            remove.Click += (_, _) =>
            {
                Apply(() => widget.Lines?.Remove(captured));
                BuildInspector();
            };
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            panel.Children.Add(row);
        }

        var add = new Button
        {
            Content = Loc.T("osd_ed_add_line"),
            Style = (Style)FindResource("FlatButton"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0),
        };
        add.Click += (_, _) =>
        {
            Apply(() =>
            {
                widget.Lines ??= new List<TileChartLine>();
                widget.Lines.Add(new TileChartLine
                {
                    Series = widget.Series ?? "frametimes",
                    Kind = "value",
                });
            });
            BuildInspector();
        };
        panel.Children.Add(add);
    }

    private void AddImageSection(Panel panel, TileNode widget)
    {
        AddSectionTitle(panel, Loc.T("osd_ed_image"));
        var import = new Button
        {
            Content = Loc.T("osd_ed_import_graphic"),
            Style = (Style)FindResource("FlatButton"),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        import.Click += (_, _) => ImportGraphic(widget);
        panel.Children.Add(import);
        AddComboRow(panel, Loc.T("osd_ed_fit"),
            new List<(string, string)> { ("Contain", "contain"), ("Cover", "cover"), ("Stretch", "stretch") },
            (widget.Fit ?? "contain").ToLowerInvariant(), value => widget.Fit = value);
        AddSliderRow(panel, Loc.T("osd_ed_opacity"), widget.Opacity ?? 1, 0, 1,
            value => widget.Opacity = value);
    }

    private void ImportGraphic(TileNode widget)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var info = new FileInfo(dialog.FileName);
        if (info.Length > 4 * 1024 * 1024)
        {
            SetStatus(Loc.T("osd_ed_image_too_large"));
            return;
        }

        var mime = info.Extension.ToLowerInvariant() is ".jpg" or ".jpeg" ? "image/jpeg" : "image/png";
        var data = Convert.ToBase64String(File.ReadAllBytes(dialog.FileName));
        Apply(() => widget.DataUri = "data:" + mime + ";base64," + data);
        BuildInspector();
    }

    private void ResetColors(TileNode tile, TileNode widget, TileNode chrome)
    {
        Apply(() =>
        {
            chrome.BackgroundColor = null;
            chrome.BorderColor = null;
            chrome.BorderWidth = null;
            chrome.Radius = null;
            widget.LabelColor = null;
            widget.ValueColor = null;
            widget.UnitColor = null;
            widget.TextColor = null;
            widget.Color = null;
        });
        RefreshAll();
        SetStatus(Loc.T("osd_ed_colors_reset"));
    }

    private void ApplyStyleToKind(TileNode tile, TileNode widget)
    {
        if (_document is null)
        {
            return;
        }

        var kind = widget.NodeKind;
        var sourceChrome = tile.NodeKind == TileNodeKind.Panel ? tile : widget;
        var count = 0;
        Apply(() =>
        {
            foreach (var node in _document.Root.Descendants())
            {
                var candidate = TileDocumentEditor.WidgetOf(node);
                if (candidate.NodeKind != kind || node.NodeId == tile.NodeId)
                {
                    continue;
                }

                count++;
                var chrome = node.NodeKind == TileNodeKind.Panel ? node : candidate;
                chrome.BackgroundColor = sourceChrome.BackgroundColor;
                chrome.BorderColor = sourceChrome.BorderColor;
                chrome.BorderWidth = sourceChrome.BorderWidth;
                chrome.Radius = sourceChrome.Radius;
                candidate.LabelColor = widget.LabelColor;
                candidate.ValueColor = widget.ValueColor;
                candidate.UnitColor = widget.UnitColor;
                candidate.TextColor = widget.TextColor;
                candidate.Size = widget.Size;
                candidate.ValueSize = widget.ValueSize;
            }
        });
        RefreshAll();
        SetStatus(Loc.F("osd_ed_style_applied", count));
    }

    // ---------------------------------------------------------------- inspector helpers

    private StackPanel AddExpander(Panel parent, string title, bool expanded)
    {
        var expander = new Expander
        {
            Header = title,
            IsExpanded = expanded,
            Margin = new Thickness(0, 6, 0, 6),
            Foreground = (Brush)FindResource("TextBrush"),
        };
        var content = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        expander.Content = content;
        parent.Children.Add(expander);
        return content;
    }

    private static void AddSectionTitle(Panel parent, string title)
    {
        parent.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 6, 0, 4),
        });
    }

    private static void AddDivider(Panel parent) => parent.Children.Add(new Border
    {
        BorderThickness = new Thickness(0, 1, 0, 0),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
        Margin = new Thickness(0, 8, 0, 8),
    });

    private static void AddTextBlock(Panel parent, string text) => parent.Children.Add(new TextBlock
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.75,
        FontSize = 11,
        Margin = new Thickness(0, 3, 0, 5),
    });

    private void AddTextRow(Panel parent, string label, string value, int maxLength,
        Action<string> set)
    {
        var grid = FieldGrid(label, out _);
        var box = new TextBox
        {
            Text = value,
            MaxLength = maxLength,
            VerticalAlignment = VerticalAlignment.Center,
        };
        void Commit()
        {
            if (string.Equals(box.Text, value, StringComparison.Ordinal))
            {
                return;
            }

            Apply(() => set(box.Text));
            value = box.Text;
        }

        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                e.Handled = true;
            }
        };
        Grid.SetColumn(box, 1);
        Grid.SetColumnSpan(box, 2);
        grid.Children.Add(box);
        parent.Children.Add(grid);
    }

    private void AddMultilineRow(Panel parent, string label, string value, Action<string> set)
    {
        AddSectionTitle(parent, label);
        var box = new TextBox
        {
            Text = value,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 54,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        void Commit()
        {
            if (!string.Equals(box.Text, value, StringComparison.Ordinal))
            {
                Apply(() => set(box.Text));
                value = box.Text;
            }
        }

        box.LostFocus += (_, _) => Commit();
        parent.Children.Add(box);
    }

    private void AddNumberRow(Panel parent, string label, double value, double min, double max,
        Action<double> set, string format = "0.##")
    {
        var grid = FieldGrid(label, out _);
        var box = new TextBox
        {
            Text = value.ToString(format, CultureInfo.InvariantCulture),
            VerticalAlignment = VerticalAlignment.Center,
        };
        void Commit()
        {
            if (!TryParse(box.Text, out var parsed))
            {
                box.Text = value.ToString(format, CultureInfo.InvariantCulture);
                return;
            }

            parsed = Math.Clamp(parsed, min, max);
            box.Text = parsed.ToString(format, CultureInfo.InvariantCulture);
            if (Math.Abs(parsed - value) < 0.0001)
            {
                return;
            }

            value = parsed;
            Apply(() => set(parsed));
        }

        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                e.Handled = true;
            }
        };
        Grid.SetColumn(box, 1);
        Grid.SetColumnSpan(box, 2);
        grid.Children.Add(box);
        parent.Children.Add(grid);
    }

    private void AddSliderRow(Panel parent, string label, double value, double min, double max,
        Action<double> set)
    {
        var grid = FieldGrid(label, out var readout);
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            VerticalAlignment = VerticalAlignment.Center,
            IsMoveToPointEnabled = true,
        };
        readout.Text = slider.Value.ToString("0.##", CultureInfo.InvariantCulture);
        slider.PreviewMouseLeftButtonDown += (_, _) => PushUndoSnapshot();
        slider.PreviewKeyDown += (_, _) => PushUndoSnapshot();
        slider.ValueChanged += (_, e) =>
        {
            readout.Text = e.NewValue.ToString("0.##", CultureInfo.InvariantCulture);
            if (_loading || Math.Abs(e.NewValue - value) < 0.0001)
            {
                return;
            }

            value = e.NewValue;
            set(e.NewValue);
            _dirty = true;
            RefreshScene();
            UpdateChrome();
        };
        Grid.SetColumn(slider, 1);
        grid.Children.Add(slider);
        parent.Children.Add(grid);
    }

    private void AddCheckRow(Panel parent, string label, bool value, Action<bool> set,
        string? automationId = null)
    {
        var grid = FieldGrid(label, out _);
        var check = new CheckBox
        {
            IsChecked = value,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (!string.IsNullOrEmpty(automationId))
        {
            AutomationProperties.SetAutomationId(check, automationId);
            AutomationProperties.SetName(check, label);
        }
        check.Checked += (_, _) =>
        {
            if (!_loading && value != true)
            {
                value = true;
                Apply(() => set(true));
                RefreshScene();
            }
        };
        check.Unchecked += (_, _) =>
        {
            if (!_loading && value)
            {
                value = false;
                Apply(() => set(false));
                RefreshScene();
            }
        };
        Grid.SetColumn(check, 1);
        Grid.SetColumnSpan(check, 2);
        grid.Children.Add(check);
        parent.Children.Add(grid);
    }

    private void AddComboRow<T>(Panel parent, string label, IReadOnlyList<(string Label, T Value)> items,
        T value, Action<T> set, string? automationId = null)
    {
        var grid = FieldGrid(label, out _);
        var combo = new ComboBox();
        if (!string.IsNullOrEmpty(automationId))
        {
            AutomationProperties.SetAutomationId(combo, automationId);
            AutomationProperties.SetName(combo, label);
        }
        foreach (var item in items)
        {
            combo.Items.Add(new ComboBoxItem { Content = item.Label, Tag = item.Value });
        }

        var selected = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => Equals(item.Tag, value));
        combo.SelectedItem = selected;
        combo.SelectionChanged += (_, _) =>
        {
            if (_loading || combo.SelectedItem is not ComboBoxItem item || Equals(item.Tag, value))
            {
                return;
            }

            value = (T)item.Tag!;
            Apply(() => set(value));
            RefreshScene();
        };
        Grid.SetColumn(combo, 1);
        Grid.SetColumnSpan(combo, 2);
        grid.Children.Add(combo);
        parent.Children.Add(grid);
    }

    private void AddFontRow(Panel parent, string value, Action<string> set)
    {
        var grid = FieldGrid(Loc.T("osd_ed_font_family"), out _);
        var combo = new ComboBox { MaxDropDownHeight = 320 };
        foreach (var family in Fonts.SystemFontFamilies.Select(family => family.Source)
                     .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            combo.Items.Add(family);
        }

        if (!combo.Items.Contains(value))
        {
            combo.Items.Add(value);
        }

        combo.SelectedItem = value;
        combo.SelectionChanged += (_, _) =>
        {
            if (_loading || combo.SelectedItem is not string family ||
                string.Equals(family, value, StringComparison.Ordinal))
            {
                return;
            }

            value = family;
            Apply(() => set(family));
            RefreshScene();
        };
        Grid.SetColumn(combo, 1);
        Grid.SetColumnSpan(combo, 2);
        grid.Children.Add(combo);
        parent.Children.Add(grid);
    }

    private void AddColorRow(Panel parent, string label, string value, Action<string> set)
    {
        var grid = FieldGrid(label, out _);
        var box = new TextBox
        {
            Text = value,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var swatch = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(2),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            Background = ParseBrush(value),
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        void Commit()
        {
            var text = box.Text.Trim();
            if (string.Equals(text, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (text.Length > 0 && !IsHexColor(text))
            {
                box.Text = value;
                return;
            }

            value = text;
            swatch.Background = ParseBrush(text);
            Apply(() => set(text));
        }

        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                e.Handled = true;
            }
        };
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);
        Grid.SetColumn(swatch, 2);
        grid.Children.Add(swatch);
        parent.Children.Add(grid);
    }

    private Grid FieldGrid(string label, out TextBlock readout)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = label,
            Style = (Style)FindResource("FieldLabel"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        });
        readout = new TextBlock
        {
            Style = (Style)FindResource("Caption"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(6, 0, 0, 0),
            MinWidth = 30,
        };
        Grid.SetColumn(readout, 2);
        grid.Children.Add(readout);
        return grid;
    }

    private static bool TryParse(string text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
           double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);

    private static bool IsHexColor(string value)
    {
        var text = value.Trim().TrimStart('#');
        return text.Length is 6 or 8 &&
               uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);
    }

    private static Brush ParseBrush(string? value)
    {
        var color = TileColor.Parse(value, TileColor.Transparent);
        var brush = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    // ---------------------------------------------------------------- chrome

    private void UpdateChrome()
    {
        UndoButton.IsEnabled = _undo.Count > 0;
        RedoButton.IsEnabled = _redo.Count > 0;
        SaveButton.IsEnabled = _document is not null && _dirty;
        ImportButton.IsEnabled = _document is not null;
        ExportButton.IsEnabled = _document is not null;
        SavedStateText.Text = _document is null
            ? Loc.T("osd_ed_row_profile")
            : Loc.T(_dirty ? "osd_ed_unsaved" : "osd_ed_saved");
        TileCountText.Text = _document is null
            ? string.Empty
            : Loc.F("osd_ed_tile_count",
                TileDocumentEditor.CountWidgets(_document.Root).ToString(CultureInfo.InvariantCulture));
        SourcesText.Text = Loc.F("osd_ed_sources_status",
            TileSourceCatalog.Sources.Count.ToString(CultureInfo.InvariantCulture));
        TemplatesButton.Content = Loc.F("osd_ed_templates",
            TileTemplateCatalog.All.Count.ToString(CultureInfo.InvariantCulture));
        DeleteElementButton.IsEnabled = _selectedId is not null;
        DuplicateElementButton.IsEnabled = _selectedId is not null;
        SendBackwardButton.IsEnabled = _selectedId is not null;
        BringForwardButton.IsEnabled = _selectedId is not null;
        Title = Loc.T("osd_builder_title") + " — " + _profile.Name;
    }

    private void SetStatus(string message) => StatusText.Text = message;

    // ---------------------------------------------------------------- import / export

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "OSD design (*.json)|*.json|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        string json;
        try
        {
            json = File.ReadAllText(dialog.FileName);
        }
        catch (Exception ex)
        {
            SetStatus(Loc.F("osd_ed_import_failed", ex.Message));
            return;
        }

        var document = TileDesignDocument.Parse(json, out var error);
        if (document is null)
        {
            var profile = OsdProfileSerializer.Deserialize(json, out _);
            if (profile is not null)
            {
                document = profile.TileDesign?.Clone();
            }
        }

        if (document is null)
        {
            SetStatus(Loc.F("osd_ed_import_failed", error ?? "not a tile design"));
            return;
        }

        var problems = document.Validate();
        if (problems.Count > 0)
        {
            SetStatus(Loc.F("osd_ed_import_failed", string.Join("; ", problems)));
            return;
        }

        TileDocumentEditor.PrepareForEditing(document);
        var before = CaptureSnapshot();
        _document = document;
        _document.Name = _profile.Name;
        _undo.Push(before);
        _redo.Clear();
        _scopeId = null;
        _selectedId = _document.Root.Children.FirstOrDefault()?.NodeId;
        _dirty = true;
        RefreshAll();
        SetStatus(Loc.T("osd_ed_imported"));
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "OSD design (*.json)|*.json",
            FileName = SafeFileName(_document.Name) + ".osddesign.json",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, _document.ToJson());
            SetStatus(Loc.T("osd_ed_exported"));
        }
        catch (Exception ex)
        {
            SetStatus(Loc.F("osd_ed_import_failed", ex.Message));
        }
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(character =>
            invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "design" : cleaned.Trim();
    }

    // ---------------------------------------------------------------- left tabs / keyboard

    private void AddTab_Checked(object sender, RoutedEventArgs e) => SetLeftTabFromUi(0);
    private void TilesTab_Checked(object sender, RoutedEventArgs e) => SetLeftTabFromUi(1);
    private void SourcesTab_Checked(object sender, RoutedEventArgs e) => SetLeftTabFromUi(2);

    private void SetLeftTabFromUi(int index)
    {
        if (_settingLeftTab)
        {
            return;
        }

        SetLeftTab(index);
    }

    private void SetLeftTab(int index)
    {
        if (AddPanel is null || TilesPanel is null || SourcesPanel is null)
        {
            return;
        }

        _settingLeftTab = true;
        try
        {
            AddTab.IsChecked = index == 0;
            TilesTab.IsChecked = index == 1;
            SourcesTab.IsChecked = index == 2;
        }
        finally
        {
            _settingLeftTab = false;
        }

        AddPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        TilesPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        SourcesPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F1)
        {
            return;
        }

        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var editingText = Keyboard.FocusedElement is TextBox;
        if (!editingText && control && e.Key == Key.Z)
        {
            Undo();
            e.Handled = true;
        }
        else if (!editingText && control && e.Key == Key.Y)
        {
            Redo();
            e.Handled = true;
        }
        else if (!editingText && control && e.Key == Key.C)
        {
            CopySelected();
            e.Handled = true;
        }
        else if (!editingText && control && e.Key == Key.V)
        {
            PasteClipboard();
            e.Handled = true;
        }
        else if (control && e.Key == Key.S)
        {
            Save_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && StartupChooser.Visibility == Visibility.Visible)
        {
            StartupChooser.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && TemplateGallery.Visibility == Visibility.Visible)
        {
            TemplateGallery.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && _selectedId is not null && !editingText)
        {
            DeleteSelected();
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- QA hooks

    internal int QaTileCount => _document is null ? 0 : TileDocumentEditor.CountWidgets(_document.Root);

    internal string QaState => _document is null
        ? "row-profile"
        : $"{_document.CanvasWidth:0}x{_document.CanvasHeight:0} tiles={QaTileCount} " +
          $"selected={_selectedId ?? "none"} dirty={_dirty}";

    internal string QaSelectionName
    {
        get
        {
            if (Selected is not { } selected)
            {
                return "none";
            }

            var widget = TileDocumentEditor.WidgetOf(selected);
            return (selected.Name ?? widget.Label ?? widget.Text ?? widget.NodeKind.ToString()) +
                   $" @{selected.X:0},{selected.Y:0} {selected.FrameWidth:0}x{selected.FrameHeight:0}";
        }
    }

    internal bool QaLoadProfile(string profileId)
    {
        var profile = _draft.Osd?.FindProfile(profileId);
        if (profile is null)
        {
            return false;
        }

        _loading = true;
        try
        {
            LoadProfile(profile, resetUndo: true);
            BuildProfileChoices();
        }
        finally
        {
            _loading = false;
        }

        return _document is not null;
    }

    internal void QaSetTab(int index) => SetTab(index switch
    {
        1 => InspectorTab.Layout,
        2 => InspectorTab.Style,
        _ => InspectorTab.Tile,
    });

    internal void QaSetLeftTab(int index) => SetLeftTab(index);

    internal void QaOpenGallery()
    {
        if (!_galleryBuilt)
        {
            BuildTemplateGallery();
        }

        TemplateGallery.Visibility = Visibility.Visible;
        UpdateLayout();
    }

    internal void QaCloseGallery() => TemplateGallery.Visibility = Visibility.Collapsed;

    internal void QaApplyTemplate(string templateId) => ApplyTemplate(templateId);

    internal void QaSelectFirstTile()
    {
        _selectedId = Scope?.Children.FirstOrDefault()?.NodeId;
        RefreshAll();
    }

    internal void QaAddWidgetAt(TileNodeKind kind, double x, double y) => AddTile(kind, x, y);

    internal void QaResizeSelected(double width, double height)
    {
        if (Selected is { } selected)
        {
            PushUndoSnapshot();
            TileDocumentEditor.SetGeometry(selected, null, null, width, height);
            _dirty = true;
            RefreshAll();
        }
    }

    internal void QaMoveSelected(double x, double y)
    {
        if (Selected is { } selected)
        {
            PushUndoSnapshot();
            TileDocumentEditor.SetGeometry(selected, x, y, null, null);
            _dirty = true;
            RefreshAll();
        }
    }

    internal void QaUndoOne() => Undo();

    internal void QaRedoOne() => Redo();

    internal void QaClose()
    {
        _dirty = false;
        Close();
    }

    internal void QaSave() => Save_Click(this, new RoutedEventArgs());

    // ---------------------------------------------------------------- misc

    private static string Trim(string value, int max)
        => value.Length <= max ? value : value[..Math.Max(0, max - 1)] + "…";

    private string DefaultTileName(TileNode tile)
    {
        var widget = TileDocumentEditor.WidgetOf(tile);
        return widget.Label ?? widget.Text ?? widget.Title ?? KindName(widget.NodeKind);
    }

    private static string KindName(TileNodeKind kind) => kind switch
    {
        TileNodeKind.Metric => Loc.T("osd_kind_metric"),
        TileNodeKind.Text => Loc.T("osd_kind_text"),
        TileNodeKind.Chart => Loc.T("osd_kind_chart"),
        TileNodeKind.Bar => Loc.T("osd_kind_bar"),
        TileNodeKind.Gauge => Loc.T("osd_kind_gauge"),
        TileNodeKind.Panel => Loc.T("osd_kind_panel"),
        TileNodeKind.Icon => Loc.T("osd_kind_icon"),
        TileNodeKind.Image => Loc.T("osd_kind_image"),
        TileNodeKind.Heatmap => Loc.T("osd_kind_heatmap"),
        TileNodeKind.ClassicRows => Loc.T("osd_kind_rows"),
        _ => kind.ToString(),
    };

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return null;
    }
}
