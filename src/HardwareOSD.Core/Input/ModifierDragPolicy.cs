using HardwareOSD.Core.Config;

namespace HardwareOSD.Core.Input;

/// <summary>
/// Decides whether the overlay may temporarily become hit-testable for modifier-based dragging.
/// It is deliberately a pure policy so the click-through restoration rules are unit-testable
/// without a WPF window.
/// </summary>
public static class ModifierDragPolicy
{
    /// <summary>True only while the configured modifier is down, the cursor is over the overlay and no drag is active.</summary>
    public static bool ShouldArm(
        bool enabled,
        bool gestureDown,
        bool cursorInside,
        bool overlayVisible,
        bool moveMode,
        bool dragging,
        bool suppressed)
    {
        if (!enabled || !overlayVisible || moveMode || dragging || suppressed || !cursorInside)
        {
            return false;
        }

        return gestureDown;
    }

    /// <summary>Legacy modifier overload kept for callers that still use the old enum.</summary>
    public static bool ShouldArm(
        bool enabled,
        OverlayDragModifier configured,
        bool controlDown,
        bool shiftDown,
        bool cursorInside,
        bool overlayVisible,
        bool moveMode,
        bool dragging,
        bool suppressed)
        => ShouldArm(enabled,
            configured == OverlayDragModifier.Shift ? shiftDown : controlDown,
            cursorInside, overlayVisible, moveMode, dragging, suppressed);

    /// <summary>True while a drag begun by the modifier policy is in progress.</summary>
    public static bool IsDragging(bool armed, bool leftButtonDown, bool leftButtonWasDown)
        => armed && leftButtonDown && leftButtonWasDown;

    /// <summary>
    /// WPF hit testing is allowed by either explicit move mode or modifier arming. Keeping this
    /// decision in Core makes the "style cleared but no mouse events arrive" regression testable.
    /// </summary>
    public static bool AllowsSurfaceHitTest(bool moveModeInteractive, bool moveMode, bool modifierInteractive)
        => modifierInteractive || (moveModeInteractive && moveMode);

    /// <summary>
    /// After a drag the overlay must return to click-through even when the modifier is still held.
    /// The caller keeps this suppression until the modifier is released.
    /// </summary>
    public static bool KeepSuppressed(bool modifierDown) => modifierDown;
}
