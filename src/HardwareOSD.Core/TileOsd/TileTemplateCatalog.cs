using System.Globalization;

namespace HardwareOSD.Core.TileOsd;

/// <summary>One entry in the builder's template gallery, backed by a real design document.</summary>
public sealed record TileTemplate(
    string Id,
    string Name,
    string Family,
    string Summary,
    double Width,
    double Height,
    int Widgets,
    bool IsBlank)
{
    public string Description => IsBlank
        ? "An empty fine-placement canvas at " + Width.ToString("0", CultureInfo.InvariantCulture) +
          " px wide. Add tiles from the palette."
        : Width.ToString("0", CultureInfo.InvariantCulture) + " × " +
          Height.ToString("0", CultureInfo.InvariantCulture) + " px · " +
          Widgets.ToString(CultureInfo.InvariantCulture) + " tiles";
}

/// <summary>
/// The gallery: the 45 embedded CapFrameX starter designs plus one blank canvas. Applying a
/// template clones the document, so edits never touch the immutable catalog.
/// </summary>
public static class TileTemplateCatalog
{
    public const string BlankId = "blank";

    private static readonly Lazy<IReadOnlyList<TileTemplate>> LazyAll = new(CreateAll);

    public static IReadOnlyList<TileTemplate> All => LazyAll.Value;

    public static IReadOnlyList<string> Families => new[] { "All categories" }
        .Concat(All.Select(template => template.Family).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(family => family, StringComparer.OrdinalIgnoreCase))
        .ToList();

    public static TileTemplate? Find(string? id)
        => id is null
            ? null
            : All.FirstOrDefault(template => string.Equals(template.Id, id, StringComparison.Ordinal));

    /// <summary>Returns an editable copy of the template's document.</summary>
    public static TileDesignDocument? CreateDocument(string templateId, string? name)
    {
        if (TileStarterTemplates.Find(templateId) is not null)
        {
            var starter = TileStarterTemplates.Create(templateId, name);
            if (starter is not null)
            {
                TileDocumentEditor.PrepareForEditing(starter);
                return starter;
            }
        }

        if (string.Equals(templateId, BlankId, StringComparison.Ordinal))
        {
            var blank = TileDocumentEditor.CreateNew(name ?? "Untitled");
            TileDocumentEditor.PrepareForEditing(blank);
            return blank;
        }

        var design = TileDesignCatalog.Find(templateId);
        if (design is null)
        {
            return null;
        }

        var copy = design.Clone();
        copy.Id = TileDocumentEditor.NewNodeId();
        if (!string.IsNullOrWhiteSpace(name))
        {
            copy.Name = name.Trim();
        }

        TileDocumentEditor.PrepareForEditing(copy);
        return copy;
    }

    private static IReadOnlyList<TileTemplate> CreateAll()
    {
        var templates = new List<TileTemplate>(
            TileStarterTemplates.All.Count + TileDesignCatalog.All.Count);

        // Curated starters first: Classic, Minimal, Horizontal, Vertical, Gaming, Dynamic Island.
        // The existing blank canvas below remains the canonical Blank template.
        foreach (var starter in TileStarterTemplates.All.Where(starter => !starter.IsBlank))
        {
            templates.Add(new TileTemplate(
                starter.Id,
                starter.Name,
                "Starter",
                starter.Summary,
                starter.Width,
                starter.Height,
                starter.Widgets,
                false));
        }

        templates.Add(new TileTemplate(BlankId, "Blank canvas", "Custom", string.Empty,
            TileDocumentEditor.DefaultWidth, 200, 0, true));

        foreach (var design in TileDesignCatalog.All.Where(design =>
                     !TileDesignCatalog.IsRetired(design.Id)))
        {
            templates.Add(new TileTemplate(
                design.Id,
                design.Name,
                string.IsNullOrWhiteSpace(design.Family) ? "Everyday" : design.Family,
                string.Empty,
                design.CanvasWidth,
                design.CanvasHeight,
                TileDocumentEditor.CountWidgets(design.Root),
                false));
        }

        return templates;
    }
}
