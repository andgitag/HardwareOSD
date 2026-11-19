using System.Text.Json;
using System.Text.Json.Serialization;
using HardwareOSD.Core.Diagnostics;
using HardwareOSD.Core.TileOsd;

namespace HardwareOSD.Core.Config;

/// <summary>JSON persistence for <see cref="AppSettings"/> with atomic writes and corruption recovery.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new AppThemeModeJsonConverter(), new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly ILog _log;

    public SettingsStore(string? filePath, ILog log)
    {
        FilePath = filePath ?? DefaultFilePath;
        _log = log;
    }

    public string FilePath { get; }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HardwareOSD");

    public static string DefaultFilePath => Path.Combine(DefaultDirectory, "settings.json");

    /// <summary>Loads settings, falling back to defaults on any error. Never throws.</summary>
    public AppSettings Load(out string? status)
    {
        status = null;
        try
        {
            if (!File.Exists(FilePath))
            {
                status = "Created default settings";
                var defaults = AppSettings.CreateDefault();
                TrySave(defaults);
                return defaults;
            }

            var json = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidDataException("Settings file is empty");
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                           ?? throw new InvalidDataException("Settings file contained null");

            // 0.26.0 converts the five original row profiles into tile documents. Preserve the
            // exact pre-migration file before Normalize can rewrite the element trees.
            var needsTileMigration = settings.Osd is
                { TileMigrationVersion: < OsdSettings.CurrentTileMigrationVersion } osd &&
                osd.Profiles.Any(profile => profile.Layout == OsdProfileLayout.Rows &&
                    RowProfileTileMigrator.ClassicProfileIds.Contains(profile.Id, StringComparer.Ordinal));
            if (needsTileMigration)
            {
                TryBackupBeforeTileMigration();
            }

            settings.Migrate();
            settings.Normalize();
            if (needsTileMigration)
            {
                // Persist the converted documents immediately. Otherwise the next launch would
                // migrate again while the backup would already exist and no builder edit could
                // ever reach disk until the user touched a setting.
                TrySave(settings);
                _log.Info("Config", "Persisted the 0.26.0 row-to-tile migration");
            }

            status = "Settings loaded";
            _log.Info("Config", $"Loaded settings from {FilePath}");
            return settings;
        }
        catch (Exception ex)
        {
            _log.Error("Config", "Failed to load settings, using defaults", ex);
            status = "Settings were invalid; defaults restored (previous file kept as .corrupt)";
            TryPreserveCorruptFile();
            var defaults = AppSettings.CreateDefault();
            TrySave(defaults);
            return defaults;
        }
    }

    /// <summary>Saves settings atomically. Returns false on failure (logged, never thrown).</summary>
    public bool Save(AppSettings settings)
    {
        try
        {
            settings.Normalize();
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(settings, JsonOptions);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);

            if (File.Exists(FilePath))
            {
                // Keep exactly one previous generation for manual recovery.
                File.Copy(FilePath, FilePath + ".bak", overwrite: true);
            }

            File.Move(temp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Config", "Failed to save settings", ex);
            return false;
        }
    }

    private void TrySave(AppSettings settings)
    {
        if (!Save(settings))
        {
            _log.Warn("Config", "Could not persist initial settings");
        }
    }

    private void TryBackupBeforeTileMigration()
    {
        try
        {
            var backup = FilePath + ".pre-0.26.bak";
            if (!File.Exists(backup) && File.Exists(FilePath))
            {
                File.Copy(FilePath, backup, overwrite: false);
                _log.Info("Config", $"Pre-0.26 row-to-tile backup written to {backup}");
            }
        }
        catch (Exception ex)
        {
            _log.Warn("Config", "Could not create the pre-0.26 tile migration backup", ex);
        }
    }

    private void TryPreserveCorruptFile()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var corrupt = FilePath + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json";
                File.Copy(FilePath, corrupt, overwrite: true);
                _log.Warn("Config", $"Corrupt settings preserved at {corrupt}");
            }
        }
        catch (Exception ex)
        {
            _log.Warn("Config", "Could not preserve corrupt settings file", ex);
        }
    }
}
