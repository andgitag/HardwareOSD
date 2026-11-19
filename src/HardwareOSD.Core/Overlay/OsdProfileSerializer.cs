using System.Text.Json;
using System.Text.Json.Serialization;
using HardwareOSD.Core.Config;

namespace HardwareOSD.Core.Overlay;

public sealed class OsdProfileExport
{
    public int SchemaVersion { get; set; } = OsdSettings.CurrentSchemaVersion;
    public string Product { get; set; } = "HardwareOSD";
    public DateTimeOffset ExportedUtc { get; set; } = DateTimeOffset.UtcNow;
    public OsdProfile? Profile { get; set; }
}

/// <summary>Versioned JSON transport for a single OSD profile (export/import).</summary>
public static class OsdProfileSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(OsdProfile profile)
        => JsonSerializer.Serialize(new OsdProfileExport { Profile = profile.Clone() }, Options);

    public static OsdProfile? Deserialize(string json, out string? error)
    {
        error = null;
        try
        {
            var envelope = JsonSerializer.Deserialize<OsdProfileExport>(json, Options);
            var profile = envelope?.Profile;
            if (profile is null)
            {
                error = "no-profile";
                return null;
            }

            profile.Normalize();
            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                error = "invalid-profile";
                return null;
            }

            return profile;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }
}
