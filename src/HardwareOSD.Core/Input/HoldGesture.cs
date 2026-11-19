using System.Text;

namespace HardwareOSD.Core.Input;

/// <summary>
/// A held key or chord used for the overlay drag gesture. Unlike a global hotkey it may consist of
/// modifiers only (Ctrl, Shift+Alt) or a single key (F8). The overlay polls the held state while the
/// cursor is over it, so no global key registration is required.
/// </summary>
public readonly record struct HoldGesture(uint Modifiers, uint? VirtualKey)
{
    public const string DefaultText = "Ctrl";

    public bool IsValid => (Modifiers & (HotkeyGesture.ModAlt | HotkeyGesture.ModControl |
                                         HotkeyGesture.ModShift | HotkeyGesture.ModWindows)) != 0 ||
                           (VirtualKey is { } key && HotkeyGesture.IsSupportedKey(key));

    public string DisplayText
    {
        get
        {
            var builder = new StringBuilder(24);
            if ((Modifiers & HotkeyGesture.ModControl) != 0) builder.Append("Ctrl+");
            if ((Modifiers & HotkeyGesture.ModAlt) != 0) builder.Append("Alt+");
            if ((Modifiers & HotkeyGesture.ModShift) != 0) builder.Append("Shift+");
            if ((Modifiers & HotkeyGesture.ModWindows) != 0) builder.Append("Win+");
            if (VirtualKey is { } key)
            {
                builder.Append(HotkeyGesture.KeyName(key));
            }
            else if (builder.Length > 0)
            {
                builder.Length--;
            }

            return builder.ToString();
        }
    }

    public static bool TryParse(string? text, out HoldGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.EndsWith('+'))
        {
            return false;
        }

        var parts = trimmed.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        uint modifiers = 0;
        uint? key = null;
        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= HotkeyGesture.ModControl;
                    break;
                case "alt":
                    modifiers |= HotkeyGesture.ModAlt;
                    break;
                case "shift":
                    modifiers |= HotkeyGesture.ModShift;
                    break;
                case "win":
                case "windows":
                    modifiers |= HotkeyGesture.ModWindows;
                    break;
                default:
                    if (key is not null || !HotkeyGesture.TryParseKeyToken(part, out var virtualKey))
                    {
                        return false;
                    }

                    key = virtualKey;
                    break;
            }
        }

        var candidate = new HoldGesture(modifiers, key);
        if (!candidate.IsValid)
        {
            return false;
        }

        gesture = candidate;
        return true;
    }

    public bool IsDown(Func<uint, bool> keyDown)
    {
        if ((Modifiers & HotkeyGesture.ModControl) != 0 && !keyDown(0x11)) return false;
        if ((Modifiers & HotkeyGesture.ModShift) != 0 && !keyDown(0x10)) return false;
        if ((Modifiers & HotkeyGesture.ModAlt) != 0 && !keyDown(0x12)) return false;
        if ((Modifiers & HotkeyGesture.ModWindows) != 0 && !keyDown(0x5B) && !keyDown(0x5C)) return false;
        return VirtualKey is not { } key || keyDown(key);
    }
}
