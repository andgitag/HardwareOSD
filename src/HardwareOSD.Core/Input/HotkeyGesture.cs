using System.Text;

namespace HardwareOSD.Core.Input;

/// <summary>
/// A normalized system-wide hotkey (modifiers + virtual key). Kept in Core so the parser and
/// formatter can be unit tested without WPF. Modifier bit values match Win32 MOD_* and WPF
/// ModifierKeys (Alt=1, Control=2, Shift=4, Windows=8).
/// </summary>
public readonly record struct HotkeyGesture(uint Modifiers, uint VirtualKey)
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWindows = 0x0008;
    private const uint AllModifiers = ModAlt | ModControl | ModShift | ModWindows;

    public const string DefaultText = "Ctrl+Alt+F10";
    public const string MainWindowDefaultText = "Ctrl+Alt+F9";

    public static readonly HotkeyGesture Default = new(ModControl | ModAlt, 0x79); // F10
    public static readonly HotkeyGesture MainWindowDefault = new(ModControl | ModAlt, 0x78); // F9

    public bool IsValid => (Modifiers & AllModifiers) != 0 && IsSupportedKey(VirtualKey);

    public HotkeyGesture Normalized() => new(Modifiers & AllModifiers, VirtualKey);

    public string DisplayText
    {
        get
        {
            var builder = new StringBuilder(24);
            if ((Modifiers & ModControl) != 0) builder.Append("Ctrl+");
            if ((Modifiers & ModAlt) != 0) builder.Append("Alt+");
            if ((Modifiers & ModShift) != 0) builder.Append("Shift+");
            if ((Modifiers & ModWindows) != 0) builder.Append("Win+");
            builder.Append(KeyName(VirtualKey));
            return builder.ToString();
        }
    }

    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        uint modifiers = 0;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= ModControl;
                    break;
                case "alt":
                    modifiers |= ModAlt;
                    break;
                case "shift":
                    modifiers |= ModShift;
                    break;
                case "win":
                case "windows":
                    modifiers |= ModWindows;
                    break;
                default:
                    return false;
            }
        }

        if (!TryParseKey(parts[^1], out var virtualKey))
        {
            return false;
        }

        var candidate = new HotkeyGesture(modifiers, virtualKey);
        if (!candidate.IsValid)
        {
            return false;
        }

        gesture = candidate;
        return true;
    }

    public static bool IsSupportedKey(uint virtualKey) => virtualKey switch
    {
        >= 0x70 and <= 0x87 => true, // F1..F24
        >= 0x41 and <= 0x5A => true, // A..Z
        >= 0x30 and <= 0x39 => true, // 0..9
        0x20 => true,                // Space
        0x21 => true,                // PageUp
        0x22 => true,                // PageDown
        0x23 => true,                // End
        0x24 => true,                // Home
        0x25 => true,                // Left
        0x26 => true,                // Up
        0x27 => true,                // Right
        0x28 => true,                // Down
        0x2D => true,                // Insert
        0x2E => true,                // Delete
        _ => false,
    };

    public static string KeyName(uint virtualKey)
    {
        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return "F" + (virtualKey - 0x70 + 1);
        }

        if (virtualKey is >= 0x41 and <= 0x5A)
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= 0x30 and <= 0x39)
        {
            return ((char)virtualKey).ToString();
        }

        return virtualKey switch
        {
            0x20 => "Space",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2D => "Insert",
            0x2E => "Delete",
            _ => "?",
        };
    }

    internal static bool TryParseKeyToken(string token, out uint virtualKey)
        => TryParseKey(token, out virtualKey);

    private static bool TryParseKey(string token, out uint virtualKey)
    {
        virtualKey = 0;
        var upper = token.ToUpperInvariant();

        if (upper.Length == 1)
        {
            var ch = upper[0];
            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                virtualKey = ch;
                return true;
            }
        }

        if (upper.StartsWith('F') && int.TryParse(upper.AsSpan(1), out var functionNumber) &&
            functionNumber is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + functionNumber - 1);
            return true;
        }

        virtualKey = upper switch
        {
            "SPACE" => 0x20,
            "PAGEUP" => 0x21,
            "PAGEDOWN" => 0x22,
            "END" => 0x23,
            "HOME" => 0x24,
            "LEFT" => 0x25,
            "UP" => 0x26,
            "RIGHT" => 0x27,
            "DOWN" => 0x28,
            "INSERT" => 0x2D,
            "DELETE" => 0x2E,
            _ => 0,
        };
        return virtualKey != 0;
    }
}
