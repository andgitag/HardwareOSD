using HardwareOSD.Core.Config;
using HardwareOSD.Core.Input;

namespace HardwareOSD.Tests;

public class InputPolicyTests
{
    [Fact]
    public void MainWindowToggle_HidesOnlyAnAlreadyActiveWindow()
    {
        Assert.Equal(MainWindowToggleAction.ShowAndActivate,
            MainWindowTogglePolicy.Next(exists: false, visible: false, active: false, minimized: false));
        Assert.Equal(MainWindowToggleAction.ShowAndActivate,
            MainWindowTogglePolicy.Next(exists: true, visible: false, active: false, minimized: false));
        Assert.Equal(MainWindowToggleAction.ShowAndActivate,
            MainWindowTogglePolicy.Next(exists: true, visible: true, active: false, minimized: false));
        Assert.Equal(MainWindowToggleAction.ShowAndActivate,
            MainWindowTogglePolicy.Next(exists: true, visible: true, active: true, minimized: true));
        Assert.Equal(MainWindowToggleAction.Hide,
            MainWindowTogglePolicy.Next(exists: true, visible: true, active: true, minimized: false));
    }

    [Fact]
    public void ModifierDrag_ArmsOnlyForTheConfiguredModifierOverAVisibleIdleOverlay()
    {
        Assert.True(ModifierDragPolicy.ShouldArm(true, OverlayDragModifier.Control,
            controlDown: true, shiftDown: false, cursorInside: true, overlayVisible: true,
            moveMode: false, dragging: false, suppressed: false));
        Assert.False(ModifierDragPolicy.ShouldArm(true, OverlayDragModifier.Control,
            controlDown: false, shiftDown: true, cursorInside: true, overlayVisible: true,
            moveMode: false, dragging: false, suppressed: false));
        Assert.True(ModifierDragPolicy.ShouldArm(true, OverlayDragModifier.Shift,
            controlDown: true, shiftDown: true, cursorInside: true, overlayVisible: true,
            moveMode: false, dragging: false, suppressed: false));
    }

    [Theory]
    [InlineData(true, true, true, true, false, false, false, true)]
    [InlineData(false, true, true, true, false, false, false, false)]
    [InlineData(true, false, true, true, false, false, false, false)]
    [InlineData(true, true, false, true, false, false, false, false)]
    [InlineData(true, true, true, false, false, false, false, false)]
    [InlineData(true, true, true, true, true, false, false, false)]
    [InlineData(true, true, true, true, false, true, false, false)]
    [InlineData(true, true, true, true, false, false, true, false)]
    public void ModifierDrag_DisarmsWhenAnyPreconditionFails(bool enabled, bool controlDown,
        bool cursorInside, bool overlayVisible, bool moveMode, bool dragging, bool suppressed,
        bool expected)
    {
        Assert.Equal(expected, ModifierDragPolicy.ShouldArm(enabled, OverlayDragModifier.Control,
            controlDown, shiftDown: false, cursorInside, overlayVisible, moveMode, dragging, suppressed));
    }

    [Theory]
    [InlineData(false, false, true, true)]   // modifier arming alone must be hit-testable
    [InlineData(true, true, false, true)]    // normal move mode
    [InlineData(true, false, false, false)]  // interactive flag without move mode is not enough
    [InlineData(false, true, false, false)]  // move mode flag without IsInteractive is not enough
    public void ModifierDrag_SurfaceHitTestNeedsModifierArmingOrMoveMode(bool moveModeInteractive,
        bool moveMode, bool modifierInteractive, bool expected)
    {
        Assert.Equal(expected, ModifierDragPolicy.AllowsSurfaceHitTest(
            moveModeInteractive, moveMode, modifierInteractive));
    }

    [Fact]
    public void MainWindowHotkey_DefaultsToCtrlAltF9()
    {
        Assert.Equal("Ctrl+Alt+F9", HotkeyGesture.MainWindowDefaultText);
        Assert.True(HotkeyGesture.TryParse(HotkeyGesture.MainWindowDefaultText, out var parsed));
        Assert.Equal(HotkeyGesture.MainWindowDefault, parsed);
        Assert.NotEqual(HotkeyGesture.Default, parsed);
    }

    [Fact]
    public void Settings_NormalizeKeepsTheTwoHotkeysDistinct()
    {
        var sameAsMain = AppSettings.CreateDefault();
        sameAsMain.Behavior.HotkeyToggleOverlay = "Ctrl+Alt+F9";
        sameAsMain.Behavior.HotkeyToggleMainWindow = "Ctrl+Alt+F9";
        sameAsMain.Normalize();
        Assert.Equal("Ctrl+Alt+F9", sameAsMain.Behavior.HotkeyToggleOverlay);
        Assert.Equal(HotkeyGesture.DefaultText, sameAsMain.Behavior.HotkeyToggleMainWindow);

        var sameAsOverlay = AppSettings.CreateDefault();
        sameAsOverlay.Behavior.HotkeyToggleOverlay = "Ctrl+Alt+F10";
        sameAsOverlay.Behavior.HotkeyToggleMainWindow = "Ctrl+Alt+F10";
        sameAsOverlay.Normalize();
        Assert.Equal(HotkeyGesture.DefaultText, sameAsOverlay.Behavior.HotkeyToggleOverlay);
        Assert.Equal(HotkeyGesture.MainWindowDefaultText, sameAsOverlay.Behavior.HotkeyToggleMainWindow);

        var invalid = AppSettings.CreateDefault();
        invalid.Behavior.HotkeyToggleMainWindow = "not a shortcut";
        invalid.Normalize();
        Assert.Equal(HotkeyGesture.MainWindowDefaultText, invalid.Behavior.HotkeyToggleMainWindow);
    }

    [Fact]
    public void Settings_NormalizeClampsTheDragModifier()
    {
        var settings = AppSettings.CreateDefault();
        settings.Overlay.ModifierDragKey = (OverlayDragModifier)99;
        settings.Normalize();
        Assert.Equal(OverlayDragModifier.Control, settings.Overlay.ModifierDragKey);
    }
}
