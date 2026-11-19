using System.Text.Json;
using System.Text.Json.Serialization;

namespace HardwareOSD.Core.Config;

/// <summary>
/// Stable persistence names for <see cref="AppThemeMode"/>. The old Light theme keeps the
/// <c>"light"</c> value even though the UI now calls it White, so existing settings files load
/// without migration and older QA tools keep working.
/// </summary>
public sealed class AppThemeModeJsonConverter : JsonConverter<AppThemeMode>
{
    public override AppThemeMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number))
        {
            return (AppThemeMode)number;
        }

        var value = reader.GetString()?.Trim();
        return value?.ToLowerInvariant() switch
        {
            "dark" => AppThemeMode.Dark,
            "light" or "white" => AppThemeMode.Light,
            "black" => AppThemeMode.Black,
            "warmlight" or "warm-light" or "warm" or "milk" or "milky" => AppThemeMode.WarmLight,
            "custom" => AppThemeMode.Custom,
            _ => throw new JsonException($"Unknown application theme '{value}'."),
        };
    }

    public override void Write(Utf8JsonWriter writer, AppThemeMode value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            AppThemeMode.Light => "light",
            AppThemeMode.Black => "black",
            AppThemeMode.WarmLight => "warmLight",
            AppThemeMode.Custom => "custom",
            _ => "dark",
        });
    }
}
