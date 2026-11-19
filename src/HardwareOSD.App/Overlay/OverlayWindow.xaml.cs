using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using HardwareOSD.App.Localization;
using HardwareOSD.App.Native;
using HardwareOSD.App.Services;
using HardwareOSD.Core.Config;
using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.Input;
using HardwareOSD.Core.Overlay;
using HardwareOSD.Core.TileOsd;

namespace HardwareOSD.App.Overlay;

/// <summary>
/// The external overlay window: borderless, per-pixel transparent, always on top, click-through by
/// default and excluded from the taskbar/Alt-Tab. It renders exclusively from cached snapshots and
/// never performs hardware access.
///
/// Positioning uses physical pixels via SetWindowPos, so multiple monitors and mixed DPI layouts are
/// handled by the OS. Move mode temporarily disables click-through and lets the user drag the window.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly AppRuntime _runtime;
    private readonly DispatcherTimer _modifierTimer;
    private long _lastSequence = -1;
    private long _lastFpsSequence = -1;
    private bool _dirty = true;
    private IntPtr _handle;
    private bool _isClickThrough = true;

    private bool _moveMode;
    private bool _dragging;
    private bool _modifierArmed;
    private bool _modifierDragging;
    private bool _modifierSuppressed;
    private int _dragOffsetX;
    private int _dragOffsetY;
    private int _lastX;
    private int _lastY;
    private int _windowWidthPx;
    private int _windowHeightPx;

    private const int VkShift = 0x10;
    private const int VkControl = 0x11;

    public OverlayWindow(AppRuntime runtime)
    {
        _runtime = runtime;
        InitializeComponent();
        _modifierTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(45),
        };
        _modifierTimer.Tick += OnModifierTick;
        SizeChanged += (_, _) => PositionOnScreen();
        DpiChanged += (_, _) =>
        {
            // The window may move to a monitor with a different scale factor; rebuild text
            // metrics for the new DPI and re-anchor.
            _dirty = true;
            RenderFrame(force: true);
            PositionOnScreen();
        };

        Surface.MouseLeftButtonDown += OnSurfaceMouseDown;
        Surface.MouseMove += OnSurfaceMouseMove;
        Surface.MouseLeftButtonUp += OnSurfaceMouseUp;
        Surface.LostMouseCapture += (_, _) =>
        {
            if (_dragging)
            {
                FinishDrag(save: false);
            }
        };
    }

    public bool IsMoving => _moveMode;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new WindowInteropHelper(this).Handle;
        ApplyWindowStyles();
        PositionOnScreen();
        _modifierTimer.Start();
    }

    /// <summary>Re-applies theme, click-through behaviour and position after a settings change.</summary>
    public void ApplySettings()
    {
        _dirty = true;
        if (!_runtime.Settings.Overlay.ModifierDragEnabled && _modifierArmed && !_modifierDragging)
        {
            DisarmModifierDrag();
        }

        ApplyWindowStyles();
        PositionOnScreen();
        InvalidateVisual();
    }

    public void ReassertTopmost()
    {
        if (IsVisible)
        {
            NativeMethods.ReassertTopmost(_handle);
        }
    }

    public void BeginMoveMode()
    {
        if (_moveMode || _handle == IntPtr.Zero)
        {
            return;
        }

        _moveMode = true;
        DisarmModifierDrag();
        Surface.IsInteractive = true;
        Surface.SetMoveMode(true, Loc.T("overlay_move_badge"));
        Cursor = Cursors.SizeAll;
        ApplyWindowStyles();
        if (!IsVisible)
        {
            RenderFrame(force: true);
        }

        NativeMethods.ReassertTopmost(_handle);
        _runtime.NotifyOverlayMoveStateChanged();
    }

    public void CancelMoveMode()
    {
        if (!_moveMode)
        {
            return;
        }

        _dragging = false;
        _modifierDragging = false;
        Surface.ReleaseMouseCapture();
        _moveMode = false;
        _modifierArmed = false;
        Surface.IsInteractive = false;
        Surface.SetModifierInteraction(false);
        Surface.SetMoveMode(false, null);
        Cursor = Cursors.Arrow;
        ApplyWindowStyles();
        _dirty = true;
        PositionOnScreen();
        _runtime.NotifyOverlayMoveStateChanged();
    }

    private void OnSurfaceMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_moveMode && !_modifierArmed)
        {
            return;
        }

        if (!_moveMode && !_modifierDown())
        {
            // The modifier was released between the timer tick and the click; restore normal
            // click-through and let the click fall through instead of starting a drag.
            DisarmModifierDrag();
            return;
        }

        if (!NativeMethods.GetCursorPosition(out var cursorX, out var cursorY))
        {
            return;
        }

        _dragOffsetX = cursorX - _lastX;
        _dragOffsetY = cursorY - _lastY;
        _dragging = true;
        _modifierDragging = !_moveMode;
        _modifierSuppressed = false;
        Surface.CaptureMouse();
        e.Handled = true;
    }

    private void OnSurfaceMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging ||
            !NativeMethods.GetCursorPosition(out var cursorX, out var cursorY))
        {
            return;
        }

        var x = cursorX - _dragOffsetX;
        var y = cursorY - _dragOffsetY;
        NativeMethods.MoveOverlay(_handle, x, y);
        _lastX = x;
        _lastY = y;
        e.Handled = true;
    }

    private void OnSurfaceMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        FinishDrag(save: true);
        e.Handled = true;
    }

    private void FinishDrag(bool save)
    {
        var wasModifierDrag = _modifierDragging;
        _dragging = false;
        _modifierDragging = false;
        if (Surface.IsMouseCaptured)
        {
            Surface.ReleaseMouseCapture();
        }

        if (!save)
        {
            if (wasModifierDrag)
            {
                _modifierSuppressed = _modifierDown();
                DisarmModifierDrag();
            }
            else
            {
                CancelMoveMode();
            }

            return;
        }

        var centerX = _lastX + Math.Max(1, _windowWidthPx) / 2;
        var centerY = _lastY + Math.Max(1, _windowHeightPx) / 2;
        var monitor = Monitors.FromPoint(centerX, centerY);

        _moveMode = false;
        _modifierArmed = false;
        _modifierSuppressed = _modifierDown();
        Surface.IsInteractive = false;
        Surface.SetModifierInteraction(false);
        Surface.SetMoveMode(false, null);
        Cursor = Cursors.Arrow;

        _runtime.CommitOverlayPosition(_lastX, _lastY, monitor.DeviceName);
        ApplyWindowStyles();
        _runtime.NotifyOverlayMoveStateChanged();
    }

    private Action<DrawingContext, TileRect>? BuildClassicRowsCallback(TileDesignDocument? design,
        SensorSnapshot snapshot)
    {
        if (design is null || !design.Root.Descendants().Any(node =>
                node.NodeKind == TileNodeKind.ClassicRows))
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

    public void RenderFrame(bool force = false)
    {
        var snapshot = _runtime.Cache.Latest;
        var fps = _runtime.FpsCache.Latest;
        if (!force && !_dirty && snapshot.Sequence == _lastSequence && fps.Sequence == _lastFpsSequence)
        {
            return;
        }

        _lastSequence = snapshot.Sequence;
        _lastFpsSequence = fps.Sequence;
        _dirty = false;

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var profile = _runtime.Settings.Osd?.ActiveProfile;
        if (profile is { IsTile: true })
        {
            var design = profile.TileDesign;
            var monitor = ActiveMonitor();
            double? screenWidthDip = monitor is null
                ? null
                : monitor.WorkWidth / (monitor.Scale <= 0 ? 1.0 : monitor.Scale);
            var scene = _runtime.BuildTileScene(snapshot,
                design?.FullWidth == true ? screenWidthDip : null);
            if (scene.IsEmpty)
            {
                if (IsVisible)
                {
                    Hide();
                }

                return;
            }

            Surface.SetTileContent(scene, Math.Clamp(profile.TileDesign?.Scale ?? profile.Tile.Scale, 0.5, 2),
                _runtime.Theme, BuildClassicRowsCallback(profile.TileDesign, snapshot));
            if (!IsVisible)
            {
                Show();
            }

            Dispatcher.BeginInvoke(PositionOnScreen, DispatcherPriority.Render);
            return;
        }

        var layout = _runtime.BuildLayout(snapshot);
        var visual = OverlayVisualBuilder.Build(
            layout,
            _runtime.Theme,
            _runtime.History,
            _runtime.Settings.Overlay.Layout,
            pixelsPerDip,
            _runtime.DemoMode);

        Surface.SetContent(visual, _runtime.Theme);

        if (visual.IsEmpty)
        {
            // Nothing to show yet (startup or every provider offline): stay invisible rather than
            // painting an empty translucent box over the game.
            if (IsVisible)
            {
                Hide();
            }

            return;
        }

        if (!IsVisible)
        {
            Show();
        }

        // SizeToContent updates automatically; nudge positioning after layout settles.
        Dispatcher.BeginInvoke(PositionOnScreen, System.Windows.Threading.DispatcherPriority.Render);
    }

    private void ApplyWindowStyles()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        var settings = _runtime.Settings;
        if (_modifierArmed || _modifierDragging)
        {
            // Keep WS_EX_NOACTIVATE while removing only WS_EX_TRANSPARENT: the drag works but the
            // overlay still cannot take focus from the game.
            NativeMethods.SetOverlayClickThrough(_handle, clickThrough: false);
            _isClickThrough = false;
        }
        else
        {
            var clickThrough = settings.Overlay.ClickThrough && !_moveMode;
            NativeMethods.ApplyOverlayStyles(_handle, clickThrough);
            _isClickThrough = clickThrough;
        }

        if (settings.Overlay.Enabled && IsVisible)
        {
            NativeMethods.ReassertTopmost(_handle);
        }
    }

    private bool _modifierDown()
    {
        var gesture = HoldGesture.TryParse(_runtime.Settings.Behavior.HotkeyDragOverlay, out var parsed)
            ? parsed
            : new HoldGesture(HotkeyGesture.ModControl, null);
        return gesture.IsDown(vk => NativeMethods.IsKeyDown((int)vk));
    }

    private bool _cursorInsideOverlay()
    {
        if (!NativeMethods.GetCursorPosition(out var x, out var y))
        {
            return false;
        }

        return x >= _lastX && x < _lastX + Math.Max(1, _windowWidthPx) &&
               y >= _lastY && y < _lastY + Math.Max(1, _windowHeightPx);
    }

    private void OnModifierTick(object? sender, EventArgs e)
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        var overlay = _runtime.Settings.Overlay;
        var modifierDown = _modifierDown();
        if (!modifierDown)
        {
            _modifierSuppressed = false;
        }

        if (_modifierDragging)
        {
            return;
        }

        var shouldArm = ModifierDragPolicy.ShouldArm(
            overlay.ModifierDragEnabled,
            modifierDown,
            _cursorInsideOverlay(),
            IsVisible && overlay.Enabled,
            _moveMode,
            _modifierDragging,
            _modifierSuppressed);

        if (shouldArm == _modifierArmed)
        {
            return;
        }

        if (shouldArm)
        {
            _modifierArmed = true;
            Surface.SetModifierInteraction(true);
            ApplyWindowStyles();
            Cursor = Cursors.SizeAll;
        }
        else
        {
            DisarmModifierDrag();
        }
    }

    private void DisarmModifierDrag()
    {
        if (!_modifierArmed)
        {
            return;
        }

        _modifierArmed = false;
        Surface.SetModifierInteraction(false);
        Cursor = Cursors.Arrow;
        ApplyWindowStyles();
    }

    private MonitorInfo ActiveMonitor()
    {
        var overlay = _runtime.Settings.Overlay;
        var monitors = Monitors.All();
        return Monitors.Find(overlay.MonitorDevice, monitors) ?? Monitors.Primary(monitors);
    }

    private void PositionOnScreen()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        var overlay = _runtime.Settings.Overlay;
        var monitors = Monitors.All();
        var monitor = Monitors.Find(overlay.MonitorDevice, monitors) ?? Monitors.Primary(monitors);

        var widthDip = ActualWidth > 0 ? ActualWidth : Surface.DesiredSize.Width;
        var heightDip = ActualHeight > 0 ? ActualHeight : Surface.DesiredSize.Height;
        if (widthDip <= 0 || heightDip <= 0)
        {
            return;
        }

        var scale = monitor.Scale <= 0 ? 1.0 : monitor.Scale;
        var widthPx = (int)Math.Ceiling(widthDip * scale);
        var heightPx = (int)Math.Ceiling(heightDip * scale);

        int x;
        int y;
        if (overlay.UseCustomPosition)
        {
            x = overlay.CustomX;
            y = overlay.CustomY;

            // A saved position may refer to a monitor that is no longer attached. Fall back to the
            // primary monitor instead of placing the overlay in an unreachable virtual-screen gap.
            var centerX = x + widthPx / 2;
            var centerY = y + heightPx / 2;
            var centerOnMonitor = monitors.Any(m =>
                centerX >= m.Left && centerX < m.Left + m.Width &&
                centerY >= m.Top && centerY < m.Top + m.Height);
            if (!centerOnMonitor)
            {
                var fallback = Monitors.Primary(monitors);
                x = fallback.WorkLeft + 24;
                y = fallback.WorkTop + 24;
            }
        }
        else
        {
            var offsetX = (int)Math.Round(overlay.OffsetX * scale);
            var offsetY = (int)Math.Round(overlay.OffsetY * scale);
            var workLeft = monitor.WorkLeft;
            var workTop = monitor.WorkTop;
            var workWidth = monitor.WorkWidth;
            var workHeight = monitor.WorkHeight;

            x = overlay.Anchor switch
            {
                OverlayAnchor.TopLeft or OverlayAnchor.MiddleLeft or OverlayAnchor.BottomLeft => workLeft + offsetX,
                OverlayAnchor.TopCenter or OverlayAnchor.Center or OverlayAnchor.BottomCenter => workLeft + (workWidth - widthPx) / 2 + offsetX,
                _ => workLeft + workWidth - widthPx - offsetX,
            };

            y = overlay.Anchor switch
            {
                OverlayAnchor.TopLeft or OverlayAnchor.TopCenter or OverlayAnchor.TopRight => workTop + offsetY,
                OverlayAnchor.MiddleLeft or OverlayAnchor.Center or OverlayAnchor.MiddleRight => workTop + (workHeight - heightPx) / 2 + offsetY,
                _ => workTop + workHeight - heightPx - offsetY,
            };

            // Keep at least part of the overlay reachable for extreme offsets.
            x = Math.Clamp(x, workLeft - widthPx + 80, workLeft + workWidth - 80);
            y = Math.Clamp(y, workTop - heightPx + 40, workTop + workHeight - 40);
        }

        // A full-width scene always sits between its left and right margins, and the design's edge
        // overrides the host anchor, matching CapFrameX's full-width scenes.
        var design = _runtime.Settings.Osd?.ActiveProfile?.TileDesign;
        if (design is not null)
        {
            if (design.FullWidth)
            {
                var marginPx = (int)Math.Round(Math.Max(0, design.MarginX) * scale);
                x = monitor.WorkLeft + marginPx;
            }

            if (string.Equals(design.Edge, "top", StringComparison.OrdinalIgnoreCase))
            {
                y = monitor.WorkTop;
            }
            else if (string.Equals(design.Edge, "bottom", StringComparison.OrdinalIgnoreCase))
            {
                y = monitor.WorkTop + monitor.WorkHeight - heightPx;
            }
        }

        _lastX = x;
        _lastY = y;
        _windowWidthPx = widthPx;
        _windowHeightPx = heightPx;
        NativeMethods.MoveOverlay(_handle, x, y);
    }

    public bool IsClickThrough => _isClickThrough;

    public IntPtr Handle => _handle;

    protected override void OnClosed(EventArgs e)
    {
        _modifierTimer.Stop();
        _modifierTimer.Tick -= OnModifierTick;
        _handle = IntPtr.Zero;
        _runtime.NotifyOverlayClosed(this);
        base.OnClosed(e);
    }
}
