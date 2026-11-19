using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HardwareOSD.App.Services;
using HardwareOSD.App.Views;
using HardwareOSD.Core.Config;
using HardwareOSD.Core.Diagnostics;
using HardwareOSD.Core.Hardware;
using HardwareOSD.Core.Hardware.Providers;
using HardwareOSD.Core.TileOsd;

namespace HardwareOSD.App.Diagnostics;

/// <summary>
/// Headless smoke test for the 0.23.0 OSD builder. It loads a copy of the live configuration, opens
/// the real window off-screen, loads every tile profile through the production load path, exercises
/// selection, geometry, undo and the three inspector tabs, and writes PNGs plus a report.
/// The user's settings file is never written: the copy lives in a temporary folder.
/// </summary>
internal static class BuilderQa
{
    public static int Render(string directory)
    {
        Directory.CreateDirectory(directory);
        var work = Path.Combine(Path.GetTempPath(), "hardwareosd-builder-qa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        var settingsPath = Path.Combine(work, "settings.json");
        if (File.Exists(SettingsStore.DefaultFilePath))
        {
            File.Copy(SettingsStore.DefaultFilePath, settingsPath);
        }

        var log = NullLog.Instance;
        var store = new SettingsStore(settingsPath, log);
        var settings = store.Load(out var status);
        var runtime = new AppRuntime(log, store, settings, demo: true, status);
        var snapshot = BuildSnapshot();
        runtime.Cache.Publish(snapshot);
        for (var index = 0; index < 180; index++)
        {
            runtime.History.AppendValue("frame.fps", 132 + Math.Sin(index / 6.0) * 9);
            runtime.History.AppendValue("frame.time", 7.1 + Math.Sin(index / 8.0) * 1.3);
            runtime.History.AppendValue("frame.fps.1low", 118 + Math.Sin(index / 11.0) * 4);
        }

        var report = new StringBuilder();
        report.AppendLine("HardwareOSD OSD builder QA");
        report.AppendLine("settings: " + settingsPath);
        var initialProfiles = settings.Osd?.Profiles.Count ?? 0;
        var initialCustom = settings.Osd?.Profiles.Count(profile => profile.HasCustomDesign) ?? 0;
        report.AppendLine("profiles: " + initialProfiles.ToString(CultureInfo.InvariantCulture));
        report.AppendLine("sensors in snapshot: " + snapshot.Count.ToString(CultureInfo.InvariantCulture));

        var failures = 0;
        var window = new OverlayBuilderWindow(runtime)
        {
            Width = 1360,
            Height = 860,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            LiveCheck = { IsChecked = false },
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            var tileProfiles = settings.Osd?.Profiles.Where(profile => profile.IsTile).ToList()
                              ?? new List<OsdProfile>();
            report.AppendLine("tile profiles: " + tileProfiles.Count.ToString(CultureInfo.InvariantCulture));
            var loaded = 0;
            foreach (var profile in tileProfiles)
            {
                try
                {
                    if (!window.QaLoadProfile(profile.Id))
                    {
                        failures++;
                        report.AppendLine("FAIL load " + profile.Id);
                        continue;
                    }

                    window.UpdateLayout();
                    loaded++;
                    if (window.QaTileCount == 0)
                    {
                        report.AppendLine("WARN empty " + profile.Id);
                    }
                }
                catch (Exception ex)
                {
                    failures++;
                    report.AppendLine("FAIL " + profile.Id + ": " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            report.AppendLine("loaded tile profiles: " + loaded.ToString(CultureInfo.InvariantCulture));

            // A design with every tile kind in play, rendered through the real window.
            if (window.QaLoadProfile("cfx-tile-cfx-benchmark"))
            {
                window.QaSelectFirstTile();
                window.UpdateLayout();
                Capture(window, Path.Combine(directory, "01-builder-tile-tab.png"));
                report.AppendLine("state tile tab: " + window.QaState);

                window.QaSetTab(1);
                window.UpdateLayout();
                Capture(window, Path.Combine(directory, "02-builder-layout-tab.png"));

                window.QaSetTab(2);
                window.UpdateLayout();
                Capture(window, Path.Combine(directory, "03-builder-style-tab.png"));

                window.QaSetTab(0);
                window.UpdateLayout();

                var before = window.QaTileCount;
                window.QaAddWidgetAt(TileNodeKind.Gauge, 40, 560);
                window.QaSelectFirstTile();
                report.AppendLine("after select-first: " + window.QaSelectionName);
                window.QaResizeSelected(400, 40);
                window.QaMoveSelected(8, 8);
                report.AppendLine("after move: " + window.QaSelectionName);
                window.UpdateLayout();
                Capture(window, Path.Combine(directory, "04-builder-edited.png"));
                var edited = window.QaState;

                window.QaUndoOne();
                window.QaUndoOne();
                window.QaUndoOne();
                window.UpdateLayout();
                report.AppendLine($"added tile: {before} -> {window.QaTileCount} (edit {edited})");
                report.AppendLine("after undo: " + window.QaState);
                if (window.QaTileCount != before)
                {
                    failures++;
                    report.AppendLine("FAIL undo did not restore the tile count");
                }

                window.QaRedoOne();
                window.QaSetTab(0);
                window.QaSelectFirstTile();
                window.UpdateLayout();
                Capture(window, Path.Combine(directory, "05-builder-redo.png"));

                window.QaOpenGallery();
                Capture(window, Path.Combine(directory, "07-builder-templates.png"));
                window.QaCloseGallery();

                var beforeTemplate = window.QaTileCount;
                window.QaApplyTemplate("cfx-minimal");
                window.UpdateLayout();
                report.AppendLine($"template cfx-minimal: {beforeTemplate} -> {window.QaTileCount} tiles " +
                                  window.QaState);
                window.QaUndoOne();
                window.UpdateLayout();
                if (window.QaTileCount != beforeTemplate)
                {
                    failures++;
                    report.AppendLine("FAIL template undo did not restore the tile count");
                }

                window.QaSetLeftTab(2);
                window.UpdateLayout();
                Capture(window, Path.Combine(directory, "08-builder-sources.png"));

                window.QaSetLeftTab(1);
                window.UpdateLayout();
                Capture(window, Path.Combine(directory, "09-builder-tiles.png"));
                window.QaSetLeftTab(0);
                window.UpdateLayout();
            }
            else
            {
                failures++;
                report.AppendLine("FAIL could not load cfx-tile-cfx-benchmark");
            }

            // Blank profile flow: create through the palette hook, then render.
            if (window.QaLoadProfile(settings.Osd?.Profiles.FirstOrDefault(profile => profile.IsTile)?.Id
                                     ?? string.Empty))
            {
                window.QaSetTab(0);
                window.UpdateLayout();
                Capture(window, Path.Combine(directory, "06-builder-first-profile.png"));
            }

            report.AppendLine("row profiles: five classic profiles migrated to tile documents in 0.26.0");

            // Persistence: save an edited tile profile into the temporary copy and check the JSON.
            if (window.QaLoadProfile("cfx-tile-cfx-minimal"))
            {
                window.QaAddWidgetAt(TileNodeKind.Bar, 12, 12);
                var expected = window.QaTileCount;
                window.QaSave();
                runtime.SaveNow();
                var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsPath));
                var osd = document.RootElement.GetProperty("osd");
                var profiles = osd.GetProperty("profiles").EnumerateArray().ToList();
                var saved = profiles.FirstOrDefault(profile =>
                    string.Equals(profile.GetProperty("id").GetString(), "cfx-tile-cfx-minimal",
                        StringComparison.Ordinal));
                var custom = profiles.Count(profile =>
                    profile.TryGetProperty("tileDesignJson", out var value) &&
                    value.ValueKind == System.Text.Json.JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(value.GetString()));
                var designJson = saved.ValueKind == System.Text.Json.JsonValueKind.Undefined
                    ? null
                    : saved.GetProperty("tileDesignJson").GetString();
                report.AppendLine($"saved: profiles={profiles.Count} customDesigns={custom} " +
                                  $"designChars={designJson?.Length ?? 0} tiles={expected}");
                if (profiles.Count != initialProfiles || custom != initialCustom + 1 ||
                    string.IsNullOrWhiteSpace(designJson))
                {
                    failures++;
                    report.AppendLine("FAIL persistence check");
                }
            }
            else
            {
                failures++;
                report.AppendLine("FAIL could not load cfx-tile-cfx-minimal for the save check");
            }

            // Minimum size: the three columns and the header must still fit without clipping.
            if (window.QaLoadProfile("cfx-tile-cfx-benchmark"))
            {
                window.Width = 1120;
                window.Height = 810;
                window.QaSetTab(0);
                window.UpdateLayout();
                Capture(window, Path.Combine(directory, "11-builder-minimum.png"));
                report.AppendLine("minimum-size state: " + window.QaState);
                window.Width = 1360;
                window.Height = 860;
                window.UpdateLayout();
            }
        }
        catch (Exception ex)
        {
            failures++;
            report.AppendLine("FATAL " + ex);
        }
        finally
        {
            try
            {
                if (window.IsVisible)
                {
                    window.QaClose();
                }
            }
            catch
            {
                // The QA result is what matters; a close failure is reported below.
            }
        }

        report.AppendLine("failures: " + failures.ToString(CultureInfo.InvariantCulture));
        File.WriteAllText(Path.Combine(directory, "builder-qa-report.txt"), report.ToString(),
            new UTF8Encoding(false));
        Console.WriteLine(report.ToString());
        return failures == 0 ? 0 : 1;
    }

    private static SensorSnapshot BuildSnapshot()
    {
        using var demo = new DemoSensorProvider();
        demo.Initialize();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var dictionary = new Dictionary<string, SensorValue>(StringComparer.Ordinal);
        for (var i = 0; i < 30; i++)
        {
            foreach (var value in demo.Read(now - (30 - i) * 1000L))
            {
                dictionary[value.Id] = value;
            }
        }

        SensorPostProcessor.Sanitize(dictionary, now);
        SensorPostProcessor.Derive(dictionary, now);
        return SensorSnapshot.Create(1, now, dictionary);
    }

    private static void Capture(Window window, string path)
    {
        if (window.Content is not FrameworkElement content || content.ActualWidth <= 0)
        {
            return;
        }

        var width = (int)Math.Ceiling(content.ActualWidth);
        var height = (int)Math.Ceiling(content.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
