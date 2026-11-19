using System.Text.Json;
using System.Text.Json.Serialization;
using HardwareOSD.Core.Config;
using HardwareOSD.Core.Diagnostics;
using HardwareOSD.Core.TileOsd;

namespace HardwareOSD.Tests;

public class RowTileMigrationTests
{
    [Fact]
    public void Defaults_MoveTheFiveClassicProfilesToTileDocuments()
    {
        var settings = AppSettings.CreateDefault();
        var osd = settings.Osd;
        Assert.Equal(1, osd.TileMigrationVersion);
        Assert.Equal(49, osd.Profiles.Count);

        foreach (var id in RowProfileTileMigrator.ClassicProfileIds)
        {
            var profile = osd.FindProfile(id);
            Assert.NotNull(profile);
            Assert.True(profile!.IsTile, id);
            Assert.False(profile.FollowMetricPreferences, id);
            Assert.False(string.IsNullOrWhiteSpace(profile.TileDesignJson), id);
            Assert.NotEmpty(profile.Elements);
            var design = profile.TileDesign;
            Assert.NotNull(design);
            Assert.True(design!.CanvasWidth > 0);
            Assert.True(design.CanvasHeight > 0);
            Assert.NotEmpty(design.Root.Descendants());
            Assert.Empty(design.Validate());
        }

        Assert.Equal(new[] { "Classic", "Green HUD", "Split HUD", "Info HUD", "Minimal Rings" },
            osd.Profiles.Where(profile => profile.Library == OsdDesignLibrary.HardwareOsd)
                .Select(profile => profile.Name));
    }

    [Fact]
    public void Migration_PreservesUserLabelsVisibilityColoursAndRootLayout()
    {
        var settings = AppSettings.CreateDefault();
        var osd = settings.Osd;
        var profile = osd.FindProfile("classic")!;
        profile.Layout = OsdProfileLayout.Rows;
        profile.TileDesignJson = null;
        profile.FollowMetricPreferences = false;
        profile.RootLayout = OsdGroupLayout.Columns;
        osd.TileMigrationVersion = 0;

        var cpu = profile.Elements.Single(element => element.Label == "CPU");
        var temperature = cpu.Children.Single(element => element.SensorId == "cpu.temp");
        temperature.Label = "Junction";
        temperature.Visible = false;
        temperature.ValueColor = "#112233";

        osd.Normalize(settings.Overlay, settings.Metrics, settings.Fps.Enabled);

        Assert.True(profile.IsTile);
        Assert.Equal(OsdGroupLayout.Columns, profile.RootLayout);
        var design = profile.TileDesign!;
        var row = Assert.Single(design.Root.Children);
        Assert.Equal("horizontal", row.Layout);
        var metric = row.Descendants().Single(node =>
            node.NodeKind == TileNodeKind.Metric && node.Key == "cpu.temp");
        Assert.Equal("CPU Junction", metric.Label);
        Assert.False(metric.Visible);
        Assert.Equal("#112233", metric.ValueColor);
    }

    [Fact]
    public void Migration_IsIdempotentAndDoesNotOverwriteBuilderEdits()
    {
        var settings = AppSettings.CreateDefault();
        var osd = settings.Osd;
        var profile = osd.FindProfile("minimal-rings")!;
        var document = profile.TileDesign!.Clone();
        document.Root.Children.Add(new TileNode
        {
            NodeId = "test-keep",
            Type = "text",
            Kind = "text",
            Text = "KEEP",
            FrameWidth = 40,
            FrameHeight = 16,
        });
        profile.SetTileDesign(document);
        var saved = profile.TileDesignJson;

        osd.Normalize(settings.Overlay, settings.Metrics, settings.Fps.Enabled);
        Assert.Equal(saved, profile.TileDesignJson);
        Assert.Contains(profile.TileDesign!.Root.Descendants(),
            node => node.NodeTextForTest() == "KEEP");
    }

    [Fact]
    public void SettingsStore_CreatesPreMigrationBackupBeforeConvertingRows()
    {
        var directory = Path.Combine(Path.GetTempPath(), "HardwareOSD-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var settings = AppSettings.CreateDefault();
            var classic = settings.Osd.FindProfile("classic")!;
            classic.Layout = OsdProfileLayout.Rows;
            classic.TileDesignJson = null;
            settings.Osd.TileMigrationVersion = 0;

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters =
                {
                    new AppThemeModeJsonConverter(),
                    new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
                },
            };
            File.WriteAllText(path, JsonSerializer.Serialize(settings, options));

            var loaded = new SettingsStore(path, NullLog.Instance).Load(out var status);
            Assert.NotNull(status);
            Assert.True(File.Exists(path + ".pre-0.26.bak"));
            var backup = File.ReadAllText(path + ".pre-0.26.bak");
            Assert.Contains("\"layout\": \"rows\"", backup);
            Assert.True(loaded.Osd.FindProfile("classic")!.IsTile);
            Assert.Equal(1, loaded.Osd.TileMigrationVersion);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}

internal static class TileNodeTestExtensions
{
    public static string? NodeTextForTest(this TileNode node)
        => node.Text ?? node.Label ?? node.Name;
}
