using System.Reflection;
using System.Text.Json;

namespace HardwareOSD.Core.TileOsd;

/// <summary>
/// The 45 saved CapFrameX 1.9.2 starter designs, embedded as immutable scene documents. The
/// catalog is read-only: user edits live in <see cref="TileOverrideSettings"/> on the OSD profile,
/// never in these resources. That keeps settings small and makes design upgrades deterministic.
/// </summary>
public static class TileDesignCatalog
{
    public const int Version = 1;

    private static readonly Lazy<IReadOnlyList<TileDesignDocument>> LazyDesigns = new(Load);
    private static readonly Lazy<IReadOnlyList<TileCatalogEntry>> LazyEntries = new(LoadEntries);

    public static IReadOnlyList<TileDesignDocument> All => LazyDesigns.Value;
    public static IReadOnlyList<TileCatalogEntry> Entries => LazyEntries.Value;

    /// <summary>
    /// Catalog designs that are kept only for existing user profiles. They are hidden from new
    /// profile/template choices but remain resolvable so a saved profile never loses its design.
    /// </summary>
    private static readonly HashSet<string> RetiredIds = new(StringComparer.Ordinal)
    {
        "cfx-blackwell-thermals-rtx-5060-and-5060-ti-8-gb",
        "cfx-blackwell-thermals-rtx-5070",
        "cfx-blackwell-thermals-rtx-5080-and-5070-ti",
        "cfx-blackwell-thermals-rtx-5090",
    };

    public static bool IsRetired(string? id)
        => id is not null && RetiredIds.Contains(id);

    public static TileDesignDocument? Find(string? id)
        => id is null
            ? null
            : All.FirstOrDefault(design => string.Equals(design.Id, id, StringComparison.Ordinal));

    public static TileCatalogEntry? FindEntry(string? id)
        => id is null
            ? null
            : Entries.FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));

    public static IReadOnlyList<TileDesignDocument> ByFamily(string? family)
        => string.IsNullOrWhiteSpace(family) || family.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? All
            : All.Where(design => string.Equals(design.Family, family, StringComparison.OrdinalIgnoreCase)).ToList();

    public static IReadOnlyList<string> Families
        => Entries.Select(entry => entry.Family).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static IReadOnlyList<TileCatalogEntry> LoadEntries()
    {
        var assembly = typeof(TileDesignCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream("HardwareOSD.Core.TileOsd.catalog.json")
                           ?? throw new InvalidOperationException("tile catalog resource is missing");
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        var catalog = JsonSerializer.Deserialize<TileCatalogFile>(json, TileJson.Options)
                      ?? throw new InvalidOperationException("tile catalog is empty");
        return catalog.Designs;
    }

    private static IReadOnlyList<TileDesignDocument> Load()
    {
        var assembly = typeof(TileDesignCatalog).Assembly;
        var designs = new List<TileDesignDocument>(Entries.Count);
        foreach (var entry in Entries)
        {
            var resource = "HardwareOSD.Core.TileOsd.Designs." + entry.File;
            using var stream = assembly.GetManifestResourceStream(resource)
                               ?? throw new InvalidOperationException($"tile design resource missing: {resource}");
            using var reader = new StreamReader(stream);
            var design = TileDesignDocument.Parse(reader.ReadToEnd(), out var error)
                         ?? throw new InvalidOperationException($"tile design invalid: {entry.File}: {error}");
            design.Id = string.IsNullOrWhiteSpace(design.Id) ? entry.Id : design.Id;
            design.Family = string.IsNullOrWhiteSpace(design.Family) ? entry.Family : design.Family;
            var problems = design.Validate();
            if (problems.Count > 0)
            {
                throw new InvalidOperationException($"tile design invalid: {entry.File}: {string.Join("; ", problems)}");
            }

            designs.Add(design);
        }

        return designs;
    }

    private sealed class TileCatalogFile
    {
        [System.Text.Json.Serialization.JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [System.Text.Json.Serialization.JsonPropertyName("designs")]
        public List<TileCatalogEntry> Designs { get; set; } = new();
    }
}
