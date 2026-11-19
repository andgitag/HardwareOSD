using HardwareOSD.Core.Config;
using HardwareOSD.Core.Input;

namespace HardwareOSD.Tests;

public class HoldGestureTests
{
    [Theory]
    [InlineData("Ctrl", "Ctrl")]
    [InlineData("shift+ctrl", "Ctrl+Shift")]
    [InlineData("Alt", "Alt")]
    [InlineData("F8", "F8")]
    [InlineData("Ctrl+F8", "Ctrl+F8")]
    [InlineData("Shift+Ctrl+F8", "Ctrl+Shift+F8")]
    [InlineData("Ctrl+Shift+Alt+Win+A", "Ctrl+Alt+Shift+Win+A")]
    public void ParsesAndFormatsHeldChords(string input, string expected)
    {
        Assert.True(HoldGesture.TryParse(input, out var gesture), input);
        Assert.Equal(expected, gesture.DisplayText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-key")]
    [InlineData("Ctrl+F8+A")]
    [InlineData("Ctrl+")]
    public void RejectsInvalidHeldChords(string input)
    {
        Assert.False(HoldGesture.TryParse(input, out _), input);
    }

    [Fact]
    public void IsDownChecksEveryModifierAndTheOptionalKey()
    {
        Assert.True(HoldGesture.TryParse("Ctrl+Shift", out var chord));
        var down = new HashSet<uint> { 0x11, 0x10 };
        Assert.True(chord.IsDown(down.Contains));
        Assert.False(chord.IsDown(vk => vk == 0x11));

        Assert.True(HoldGesture.TryParse("Ctrl+F8", out var keyChord));
        var keyDown = new HashSet<uint> { 0x11, 0x77 };
        Assert.True(keyChord.IsDown(keyDown.Contains));
        Assert.False(keyChord.IsDown(vk => vk == 0x11));
    }

    [Fact]
    public void Settings_NormaliseMigratesTheLegacyDragModifierOnce()
    {
        var legacy = AppSettings.CreateDefault();
        legacy.Overlay.ModifierDragKey = HardwareOSD.Core.Config.OverlayDragModifier.Shift;
        legacy.Behavior.HotkeyDragOverlayMigrated = false;
        legacy.Behavior.HotkeyDragOverlay = "Ctrl";
        legacy.Normalize();
        Assert.Equal("Shift", legacy.Behavior.HotkeyDragOverlay);
        Assert.True(legacy.Behavior.HotkeyDragOverlayMigrated);

        legacy.Behavior.HotkeyDragOverlay = "Alt+F8";
        legacy.Normalize();
        Assert.Equal("Alt+F8", legacy.Behavior.HotkeyDragOverlay);
    }

    [Fact]
    public void Settings_KeepCustomRecordingAndDragHotkeysAndResolveDuplicates()
    {
        var settings = AppSettings.CreateDefault();
        settings.Behavior.HotkeyStartRecording = "Ctrl+Shift+F6";
        settings.Behavior.HotkeyStopRecording = "Ctrl+Shift+F7";
        settings.Behavior.HotkeyRestartApplication = "Ctrl+Shift+F8";
        settings.Behavior.HotkeyDragOverlay = "Alt";
        settings.Normalize();

        Assert.Equal("Ctrl+Shift+F6", settings.Behavior.HotkeyStartRecording);
        Assert.Equal("Ctrl+Shift+F7", settings.Behavior.HotkeyStopRecording);
        Assert.Equal("Ctrl+Shift+F8", settings.Behavior.HotkeyRestartApplication);
        Assert.Equal("Alt", settings.Behavior.HotkeyDragOverlay);

        var clone = settings.Clone();
        Assert.Equal(settings.Behavior.HotkeyStartRecording, clone.Behavior.HotkeyStartRecording);
        Assert.Equal(settings.Behavior.HotkeyStopRecording, clone.Behavior.HotkeyStopRecording);
        Assert.Equal(settings.Behavior.HotkeyRestartApplication, clone.Behavior.HotkeyRestartApplication);
        Assert.Equal(settings.Behavior.HotkeyDragOverlay, clone.Behavior.HotkeyDragOverlay);

        var duplicate = AppSettings.CreateDefault();
        duplicate.Behavior.HotkeyStartRecording = "Ctrl+Shift+R";
        duplicate.Behavior.HotkeyStopRecording = "Ctrl+Shift+R";
        duplicate.Normalize();
        Assert.NotEqual(duplicate.Behavior.HotkeyStartRecording, duplicate.Behavior.HotkeyStopRecording);
    }
}
