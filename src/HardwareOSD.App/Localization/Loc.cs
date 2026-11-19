using System.ComponentModel;
using System.Reflection;
using System.Text.Json;

namespace HardwareOSD.App.Localization;

/// <summary>
/// Centralized UI string table. Strings live in embedded JSON resources; XAML binds through the
/// LocText markup extension, so a language change raises one property-changed notification and the
/// whole interface updates immediately. No per-control language checks exist in the UI.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    private Dictionary<string, string> _strings = new(StringComparer.Ordinal);
    private Dictionary<string, string> _fallback = new(StringComparer.Ordinal);

    private Loc()
    {
        _fallback = Load("en");
        _strings = _fallback;
    }

    public string Language { get; private set; } = "en";

    public string this[string key]
        => _strings.TryGetValue(key, out var value)
            ? value
            : _fallback.TryGetValue(key, out var fallback) ? fallback : key;

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? LanguageChanged;

    public static string T(string key) => Instance[key];

    public static string F(string key, params object?[] args)
    {
        try
        {
            return string.Format(Instance[key], args);
        }
        catch
        {
            return Instance[key];
        }
    }

    public static string Normalize(string? language)
        => string.Equals(language?.Trim(), "uk", StringComparison.OrdinalIgnoreCase) ? "uk" : "en";

    public void Initialize(string? language)
    {
        var normalized = Normalize(language);
        _fallback = Load("en");
        _strings = normalized == "en" ? _fallback : Load("uk");
        Language = normalized;
    }

    public void SetLanguage(string? language)
    {
        var normalized = Normalize(language);
        if (normalized == Language)
        {
            return;
        }

        _fallback = Load("en");
        _strings = normalized == "en" ? _fallback : Load("uk");
        Language = normalized;

        // "Item[]" invalidates every indexer binding at once.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    private static Dictionary<string, string> Load(string language)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var name = $"HardwareOSD.App.Localization.strings-{language}.json";
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
            return map is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(map, StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
