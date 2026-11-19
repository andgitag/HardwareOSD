namespace HardwareOSD.Core.TileOsd;

public sealed record TileStarterTemplate(
    string Id,
    string Name,
    string Summary,
    double Width,
    double Height,
    int Widgets,
    bool IsBlank = false);

/// <summary>
/// The seven starter designs shown at the top of the template gallery. They are ordinary editable
/// tile documents, built from real tile sources; applying one clones it and never touches an
/// existing profile. Classic remains the default profile and is also available as a starter.
/// </summary>
public static class TileStarterTemplates
{
    public const string ClassicId = "starter-classic";
    public const string MinimalId = "starter-minimal";
    public const string HorizontalId = "starter-horizontal";
    public const string VerticalId = "starter-vertical";
    public const string GamingId = "starter-gaming";
    public const string DynamicIslandId = "starter-dynamic-island";
    public const string BlankId = "starter-blank";

    public static IReadOnlyList<TileStarterTemplate> All { get; } = new List<TileStarterTemplate>
    {
        new(ClassicId, "Classic", "The familiar six-row HardwareOSD layout.", 520, 430, 22),
        new(MinimalId, "Minimal", "Three compact readings in one pill.", 380, 64, 3),
        new(HorizontalId, "Horizontal", "One long horizontal strip.", 640, 64, 5),
        new(VerticalId, "Vertical", "A compact vertical list.", 260, 300, 5),
        new(GamingId, "Gaming", "CPU, GPU, memory and frame-time monitoring.", 600, 300, 13),
        new(DynamicIslandId, "Dynamic Island", "A small floating pill that grows with its metrics.", 420, 48, 4),
        new(BlankId, "Blank", "An empty canvas for a fully custom design.", 520, 200, 0, true),
    };

    public static TileStarterTemplate? Find(string? id)
        => id is null
            ? null
            : All.FirstOrDefault(template => string.Equals(template.Id, id, StringComparison.Ordinal));

    public static TileDesignDocument? Create(string id, string? name)
    {
        var template = Find(id);
        if (template is null)
        {
            return null;
        }

        var document = TileDocumentEditor.CreateNew(name ?? template.Name);
        document.Family = "Starter";
        document.Root.FrameWidth = template.Width;
        document.Root.Width = template.Width;
        document.Root.FrameHeight = template.Height;
        document.Root.Height = template.Height;
        document.Root.Layout = "canvas";
        document.Root.Background = false;
        document.Theme = CreateTheme();

        switch (id)
        {
            case ClassicId:
                BuildClassic(document);
                break;
            case MinimalId:
                BuildMinimal(document);
                break;
            case HorizontalId:
                BuildHorizontal(document);
                break;
            case VerticalId:
                BuildVertical(document);
                break;
            case GamingId:
                BuildGaming(document);
                break;
            case DynamicIslandId:
                BuildDynamicIsland(document);
                break;
        }

        document.NormalizeEditorSettings();
        return document;
    }

    private static TileTheme CreateTheme() => new()
    {
        Panel = "#12161EE6",
        Accent = "#6FD8CCFF",
        Text = "#F4F7FAFF",
        Subtext = "#9AA3ACFF",
        FontFamily = "Segoe UI",
        FontSize = 13,
        TitleSize = 11,
        Pad = 8,
        Gap = 6,
        Radius = 8,
    };

    private static void BuildClassic(TileDesignDocument document)
    {
        var rows = new (string Label, (string Key, double Width)[] Metrics)[]
        {
            ("CPU", new[] { ("cpuTemp", 86.0), ("cpuLoad", 72.0), ("cpuClock", 104.0), ("cpuPower", 86.0) }),
            ("GPU", new[] { ("gpuTemp", 86.0), ("gpuLoad", 72.0), ("gpuClock", 104.0), ("gpuPower", 86.0) }),
            ("VRAM", new[] { ("vramUsed", 118.0), ("vramLoad", 72.0) }),
            ("RAM", new[] { ("ramUsed", 118.0), ("ramLoad", 72.0) }),
            ("TEMP", new[] { ("vramTemp", 96.0) }),
            ("FPS", new[] { ("fpsCurrent", 88.0), ("frametimeCurrent", 96.0), ("fps1pctLow", 92.0) }),
        };

        var y = 0.0;
        foreach (var row in rows)
        {
            var children = new List<TileNode>
            {
                Label(document, row.Label, 54, 40),
            };
            foreach (var metric in row.Metrics)
            {
                children.Add(Metric(document, metric.Key, metric.Width, 40, 19));
            }

            document.Root.Children.Add(Row(document, 0, y, 512, 60, children));
            y += 68;
        }
    }

    private static void BuildMinimal(TileDesignDocument document)
    {
        var panel = Row(document, 0, 0, 372, 56, new List<TileNode>
        {
            Metric(document, "cpuTemp", 108, 40, 19),
            Metric(document, "gpuTemp", 108, 40, 19),
            Metric(document, "fpsCurrent", 96, 40, 19),
        });
        panel.Padding = 8;
        panel.Gap = 12;
        panel.Radius = 12;
        document.Root.Children.Add(panel);
    }

    private static void BuildHorizontal(TileDesignDocument document)
    {
        var panel = Row(document, 0, 0, 628, 56, new List<TileNode>
        {
            Metric(document, "cpuTemp", 112, 40, 19),
            Metric(document, "gpuTemp", 112, 40, 19),
            Metric(document, "ramLoad", 100, 40, 19),
            Metric(document, "vramLoad", 100, 40, 19),
            Metric(document, "fpsCurrent", 100, 40, 19),
        });
        panel.Padding = 8;
        panel.Gap = 10;
        panel.Radius = 10;
        document.Root.Children.Add(panel);
    }

    private static void BuildVertical(TileDesignDocument document)
    {
        var panel = Column(document, 0, 0, 248, 292, new List<TileNode>
        {
            Metric(document, "cpuTemp", 228, 44, 19),
            Metric(document, "cpuLoad", 228, 44, 19),
            Metric(document, "gpuTemp", 228, 44, 19),
            Metric(document, "gpuLoad", 228, 44, 19),
            Metric(document, "fpsCurrent", 228, 44, 19),
        });
        panel.Padding = 8;
        panel.Gap = 6;
        panel.Radius = 10;
        document.Root.Children.Add(panel);
    }

    private static void BuildGaming(TileDesignDocument document)
    {
        document.Root.Children.Add(Row(document, 0, 0, 584, 60, new List<TileNode>
        {
            Label(document, "CPU", 44, 40),
            Metric(document, "cpuTemp", 90, 40, 19),
            Metric(document, "cpuLoad", 76, 40, 19),
            Label(document, "GPU", 48, 40),
            Metric(document, "gpuTemp", 92, 40, 19),
            Metric(document, "gpuLoad", 78, 40, 19),
        }));
        document.Root.Children.Add(Row(document, 0, 68, 584, 60, new List<TileNode>
        {
            Metric(document, "cpuClock", 108, 40, 19),
            Metric(document, "cpuPower", 92, 40, 19),
            Metric(document, "gpuClock", 108, 40, 19),
            Metric(document, "gpuPower", 92, 40, 19),
            Metric(document, "ramLoad", 78, 40, 19),
            Metric(document, "vramLoad", 78, 40, 19),
        }));
        document.Root.Children.Add(Row(document, 0, 136, 584, 60, new List<TileNode>
        {
            Metric(document, "fpsCurrent", 92, 40, 19),
            Metric(document, "frametimeCurrent", 104, 40, 19),
            Metric(document, "fps1pctLow", 96, 40, 19),
            Bar(document, "gpuLoad", 150, 36),
        }));
        document.Root.Children.Add(Row(document, 0, 204, 584, 84, new List<TileNode>
        {
            Chart(document, "frame.fps", "FPS", "FPS", 270, 76),
            Chart(document, "gpu.temp", "GPU temperature", "°C", 270, 76),
        }));
    }

    private static void BuildDynamicIsland(TileDesignDocument document)
    {
        var panel = Row(document, 0, 0, 412, 44, new List<TileNode>
        {
            Metric(document, "cpuTemp", 88, 30, 17),
            Metric(document, "gpuTemp", 88, 30, 17),
            Metric(document, "ramLoad", 72, 30, 17),
            Metric(document, "fpsCurrent", 72, 30, 17),
        });
        panel.Padding = 8;
        panel.Gap = 10;
        panel.Radius = 22;
        document.Root.Children.Add(panel);
    }

    private static TileNode Row(TileDesignDocument document, double x, double y, double width,
        double height, List<TileNode> children)
        => Panel(document, x, y, width, height, "horizontal", children);

    private static TileNode Column(TileDesignDocument document, double x, double y, double width,
        double height, List<TileNode> children)
        => Panel(document, x, y, width, height, "vertical", children);

    private static TileNode Panel(TileDesignDocument document, double x, double y, double width,
        double height, string layout, List<TileNode> children) => new()
    {
        NodeId = TileDocumentEditor.NewNodeId(),
        Name = layout == "horizontal" ? "Row" : "Column",
        Type = "panel",
        Kind = "panel",
        Layout = layout,
        X = x,
        Y = y,
        FrameWidth = width,
        FrameHeight = height,
        Width = width,
        Height = height,
        Padding = 6,
        Gap = 6,
        Background = true,
        Color = document.Theme.Panel,
        BorderColor = "#FFFFFF1F",
        BorderWidth = 1,
        Radius = document.Theme.Radius,
        Clip = true,
        Children = children,
    };

    private static TileNode Label(TileDesignDocument document, string text, double width,
        double height)
    {
        var leaf = TileDocumentEditor.CreateLeaf(TileNodeKind.Text, document.Theme);
        leaf.Text = text;
        leaf.Size = 11;
        leaf.Weight = "semibold";
        leaf.Color = document.Theme.Subtext;
        Frame(leaf, width, height);
        return leaf;
    }

    private static TileNode Metric(TileDesignDocument document, string key, double width,
        double height, double valueSize)
    {
        var source = TileSourceCatalog.Find(key);
        var leaf = TileDocumentEditor.CreateLeaf(TileNodeKind.Metric, document.Theme);
        leaf.Key = key;
        leaf.Label = source?.Label ?? key;
        leaf.Unit = source?.Unit ?? string.Empty;
        leaf.Digits = string.Equals(source?.Unit, "%", StringComparison.Ordinal) ? 0 : 0;
        leaf.ValueSize = valueSize;
        leaf.LabelSize = 10;
        leaf.LabelPosition = "top";
        leaf.Format = null;
        Frame(leaf, width, height);
        return leaf;
    }

    private static TileNode Bar(TileDesignDocument document, string key, double width, double height)
    {
        var source = TileSourceCatalog.Find(key);
        var leaf = TileDocumentEditor.CreateLeaf(TileNodeKind.Bar, document.Theme);
        leaf.Key = key;
        leaf.Label = source?.Label ?? key;
        leaf.Unit = source?.Unit ?? "%";
        leaf.Min = 0;
        leaf.Max = 100;
        leaf.Digits = 0;
        leaf.ShowValue = true;
        leaf.Orientation = "horizontal";
        Frame(leaf, width, height);
        return leaf;
    }

    private static TileNode Chart(TileDesignDocument document, string series, string title,
        string unit, double width, double height)
    {
        var leaf = TileDocumentEditor.CreateLeaf(TileNodeKind.Chart, document.Theme);
        leaf.Series = series;
        leaf.Title = title;
        leaf.Unit = unit;
        leaf.ShowGridLines = true;
        leaf.ShowLabels = true;
        leaf.CurrentValue = true;
        leaf.Thickness = 2;
        leaf.Opacity = 1;
        leaf.HistoryPoints = 90;
        leaf.Color = document.Theme.Accent;
        Frame(leaf, width, height);
        return leaf;
    }

    private static void Frame(TileNode node, double width, double height)
    {
        node.FrameWidth = width;
        node.Width = width;
        node.FrameHeight = height;
        node.Height = height;
    }
}
