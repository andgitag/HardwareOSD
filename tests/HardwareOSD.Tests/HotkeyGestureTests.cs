using HardwareOSD.Core.Config;
using HardwareOSD.Core.Input;

namespace HardwareOSD.Tests;

public class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Alt+F10", "Ctrl+Alt+F10")]
    [InlineData("cntrl+F9", "F9")] // invalid modifier token
    [InlineData("Shift+Ctrl+A", "Ctrl+Shift+A")]
    [InlineData("alt+shift+5", "Alt+Shift+5")]
    [InlineData("Win+Space", "Win+Space")]
    [InlineData("Control+Alt+Delete", "Ctrl+Alt+Delete")]
    public void Parsing_RoundTripsSupportedGestures(string input, string expectedDisplay)
    {
        if (expectedDisplay == "F9")
        {
            Assert.False(HotkeyGesture.TryParse(input, out _));
            return;
        }

        Assert.True(HotkeyGesture.TryParse(input, out var gesture), input);
        Assert.Equal(expectedDisplay, gesture.DisplayText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("F10")]              // no modifier
    [InlineData("Ctrl")]             // no key
    [InlineData("Ctrl+")]            // no key
    [InlineData("Ctrl+F25")]         // unsupported function key
    [InlineData("Ctrl+!")]           // unsupported key
    [InlineData("Ctrl+Alt+Unknown")] // unknown key name
    public void InvalidGestures_AreRejected(string? input)
    {
        Assert.False(HotkeyGesture.TryParse(input, out _));
    }

    [Fact]
    public void DefaultGesture_IsCtrlAltF10()
    {
        Assert.Equal("Ctrl+Alt+F10", HotkeyGesture.Default.DisplayText);
        Assert.True(HotkeyGesture.TryParse(HotkeyGesture.DefaultText, out var parsed));
        Assert.Equal(HotkeyGesture.Default, parsed);
        Assert.True(parsed.IsValid);
    }

    [Fact]
    public void ModifierOrder_IsCanonical()
    {
        Assert.True(HotkeyGesture.TryParse("Shift+Win+Ctrl+Alt+B", out var gesture));
        Assert.Equal("Ctrl+Alt+Shift+Win+B", gesture.DisplayText);
    }

    [Theory]
    [InlineData(0x70, true)]  // F1
    [InlineData(0x87, true)]  // F24
    [InlineData(0x41, true)]  // A
    [InlineData(0x39, true)]  // 9
    [InlineData(0x25, true)]  // Left
    [InlineData(0x0D, false)] // Enter (not offered)
    public void SupportedKeys_AreLimitedToSafeCombinations(uint key, bool supported)
    {
        Assert.Equal(supported, HotkeyGesture.IsSupportedKey(key));
    }

    [Fact]
    public void Settings_RoundTripCustomHotkey()
    {
        var settings = AppSettings.CreateDefault();
        settings.Behavior.HotkeyToggleOverlay = "Shift+Ctrl+F8";
        settings.Normalize();
        Assert.Equal("Ctrl+Shift+F8", settings.Behavior.HotkeyToggleOverlay);

        var clone = settings.Clone();
        Assert.Equal("Ctrl+Shift+F8", clone.Behavior.HotkeyToggleOverlay);

        var invalid = AppSettings.CreateDefault();
        invalid.Behavior.HotkeyToggleOverlay = "not a shortcut";
        invalid.Normalize();
        Assert.Equal(HotkeyGesture.DefaultText, invalid.Behavior.HotkeyToggleOverlay);
    }
}
