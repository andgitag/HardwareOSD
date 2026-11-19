using System.Text.Json;

namespace HardwareOSD.Tests;

/// <summary>
/// Localization table checks. The test project references Core only, so the embedded App resources
/// are read from the source tree; the repository is located by walking up to HardwareOSD.sln.
/// This keeps the check honest for both language files without adding a WPF reference.
/// </summary>
public class LocalizationTests
{
    private static readonly string[] Version020Keys =
    {
        "system_eyebrow_os",
        "system_eyebrow_cpu",
        "system_eyebrow_gpu",
        "system_eyebrow_board",
        "system_eyebrow_memory",
        "system_section_storage",
        "system_section_software",
        "system_section_security",
        "system_section_diagnostics",
        "system_load",
        "system_usage",
        "system_power",
        "system_temperature",
        "system_clock",
        "system_vram",
        "system_in_use",
        "system_available",
        "system_copy_info",
        "system_copied",
        "system_copy_failed",
        "system_cores_threads",
        "system_cores_only",
        "system_threads_only",
        "system_vram_total",
        "system_bios",
        "system_feature_rebar",
        "system_feature_hags",
        "system_feature_game_mode",
        "system_storage_device",
        "system_storage_devices",
        "system_storage_empty",
        "system_storage_used_of",
        "system_software_pawnio",
        "system_software_broker",
        "system_software_nvml",
        "system_software_lhm",
        "system_status_running",
        "system_status_installed",
        "system_status_not_installed",
        "system_status_available",
        "system_status_unavailable",
        "system_status_bundled",
        "system_status_access_denied",
        "system_status_unknown",
        "diagnostics_cpu_sensors",
        "diagnostics_gpu_sensors",
        "diagnostics_fps_access",
        "diagnostics_sensor_value",
    };

    private static readonly string[] Version021Keys =
    {
        "osd_library_hardwareosd",
        "osd_library_capframex",
        "osd_tile_library",
        "osd_tile_scale_opacity",
        "osd_tile_selected",
        "osd_tile_preview",
    };

    private static readonly string[] NewKeys =
    {
        "fps_capture_mode",
        "fps_capture_game_only",
        "fps_capture_all_apps",
        "fps_capture_mode_hint",
        "fps_status_no_presenter",
        "fps_detail_no_presenter",
        "fps_detail_desktop",
        "fps_detail_focus_grace",
        "fps_detail_ambiguous",
        "fps_measure_windowed_all_apps",
        "diagnostics_fps_capture_mode",
        "diagnostics_fps_target",
        "fps_reason_none",
        "fps_reason_foreground",
        "fps_reason_uwpchild",
        "fps_reason_windowowner",
        "fps_reason_siblingrenderer",
        "fps_reason_helperrenderer",
        "fps_reason_sticky",
        "fps_reason_focuschange",
        "fps_reason_etwgap",
        "fps_reason_notpresenting",
        "fps_reason_desktop",
        "fps_reason_noforeground",
        "fps_reason_ambiguous",
    };

    private static readonly string[] Version017Keys =
    {
        "osd_engine_title",
        "osd_open_builder",
        "osd_builder_title",
        "osd_style",
        "osd_apply",
        "osd_preview",
        "osd_kind_group",
        "osd_kind_custom_text",
        "osd_kind_divider",
        "osd_auto_color",
        "osd_group_layout",
        "osd_import",
        "osd_export",
    };

    private static readonly string[] Version016Keys =
    {
        "metric_cpu_power_cores",
        "metric_cpu_power_memory",
        "metric_cpu_power_gt",
        "metric_cpu_voltage",
        "sensor_note_energy_meter_unavailable",
        "sensor_note_energy_meter_channel_unavailable",
        "sensor_note_energy_meter_zero",
        "sensor_note_energy_meter_read_failed",
    };

    private static readonly string[] Version015Keys =
    {
        "system_msi_fan_broker",
        "common_yes",
        "common_no",
        "diagnostics_msi_broker",
        "diagnostics_msi_broker_value",
        "diagnostics_msi_broker_not_used",
        "sensor_note_msi_broker_unavailable",
        "sensor_note_msi_broker_access_denied",
        "sensor_note_msi_broker_class_missing",
        "sensor_note_msi_broker_read_failed",
        "broker_reason_none",
        "broker_reason_service_not_installed",
        "broker_reason_service_not_running",
        "broker_reason_pipe_unavailable",
        "broker_reason_timeout",
        "broker_reason_protocol_error",
        "broker_reason_access_denied",
        "broker_reason_class_missing",
        "broker_reason_instance_unavailable",
        "broker_reason_wmi_read_failed",
        "broker_reason_no_fan_channels",
        "broker_reason_internal_error",
        "broker_reason_disabled",
        "broker_reason_unsupported",
        "broker_reason_busy",
        "broker_reason_connection_rejected",
        "broker_reason_not_used",
    };

    private static readonly string[] Version014Keys =
    {
        "common_na",
        "monitoring_summary",
        "system_info_title",
        "system_info_hint",
        "system_info_refresh",
        "system_info_reading",
        "system_info_not_read",
        "system_info_updated",
        "system_info_error",
        "system_info_windows",
        "system_info_cpu",
        "system_info_ram",
        "system_info_motherboard",
        "system_info_gpus",
        "system_info_storage",
        "system_info_gpu_count",
        "system_info_gpu_name",
        "system_info_gpu_vendor",
        "system_info_gpu_driver",
        "system_info_gpu_vram",
        "system_info_storage_name",
        "system_info_storage_type",
        "system_info_storage_total",
        "system_info_storage_free",
        "system_info_storage_used",
        "system_note_wmi_unavailable",
        "system_note_wmi_access_denied",
        "system_note_not_reported",
        "system_note_shared_memory",
        "system_note_vram_not_reported",
        "system_note_storage_volumes_unavailable",
        "system_note_storage_type_unavailable",
        "system_note_rated_clock",
        "system_note_collection_failed",
        "system_msi_acpi",
        "diagnostics_msi_acpi",
        "diagnostics_msi_acpi_absent",
        "sensor_note_msi_acpi_requires_elevation",
        "sensor_note_msi_acpi_unavailable",
        "sensor_note_msi_acpi_read_failed",
        "sensor_note_msi_fan_no_percentage",
        "sensor_note_msi_fan_zero",
        "sensor_note_fan_rpm_only",
        "sensor_note_fan_percent_only",
        "source_fan_no_rpm",
        "source_fan_no_percent",
        "appearance_overlay_moved",
    };

    private static readonly string[] Version013Keys =
    {
        "nav_recording",
        "recording_page_title",
        "recording_config_title",
        "recording_sensors",
        "recording_start",
        "recording_stop",
        "recording_open_folder",
        "recording_state_recording",
        "recording_error_disk_full",
        "recording_sensor_summary",
        "recording_fan_name",
        "general_modifier_drag_enable",
        "general_modifier_drag_ctrl",
        "general_modifier_drag_shift",
        "hotkeys_toggle_main",
        "hotkeys_duplicate",
        "diagnostics_sensor_access",
        "diagnostics_pawnio",
        "diagnostics_fan_channels_value",
        "sensor_note_pawnio_not_installed",
        "pawnio_state_access_denied",
    };

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && directory is not null; i++, directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HardwareOSD.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not locate HardwareOSD.sln from " + AppContext.BaseDirectory);
    }

    private static Dictionary<string, string> Load(string language)
    {
        var path = Path.Combine(Root(), "src", "HardwareOSD.App", "Localization",
            $"strings-{language}.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
               ?? throw new InvalidOperationException("Empty string table: " + path);
    }

    [Fact]
    public void EnglishAndUkrainian_HaveIdenticalKeys()
    {
        var en = Load("en");
        var uk = Load("uk");

        Assert.Equal(en.Keys.OrderBy(k => k, StringComparer.Ordinal),
            uk.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.True(en.Count >= 580, $"expected at least 580 keys, found {en.Count}");
    }

    [Fact]
    public void Version021Keys_ArePresentAndNonEmptyInBothLanguages()
    {
        var en = Load("en");
        var uk = Load("uk");
        foreach (var key in Version021Keys)
        {
            Assert.True(en.TryGetValue(key, out var english) && !string.IsNullOrWhiteSpace(english),
                "missing English key " + key);
            Assert.True(uk.TryGetValue(key, out var ukrainian) && !string.IsNullOrWhiteSpace(ukrainian),
                "missing Ukrainian key " + key);
        }
    }

    [Fact]
    public void Version020Keys_ArePresentAndNonEmptyInBothLanguages()
    {
        var en = Load("en");
        var uk = Load("uk");
        foreach (var key in Version020Keys)
        {
            Assert.True(en.TryGetValue(key, out var english) && !string.IsNullOrWhiteSpace(english),
                "missing English key " + key);
            Assert.True(uk.TryGetValue(key, out var ukrainian) && !string.IsNullOrWhiteSpace(ukrainian),
                "missing Ukrainian key " + key);
        }
    }

    [Fact]
    public void Version017Keys_ArePresentAndNonEmptyInBothLanguages()
    {
        var en = Load("en");
        var uk = Load("uk");
        foreach (var key in Version017Keys)
        {
            Assert.True(en.TryGetValue(key, out var english) && !string.IsNullOrWhiteSpace(english),
                "missing English key " + key);
            Assert.True(uk.TryGetValue(key, out var ukrainian) && !string.IsNullOrWhiteSpace(ukrainian),
                "missing Ukrainian key " + key);
        }
    }

    [Fact]
    public void Version016Keys_ArePresentAndNonEmptyInBothLanguages()
    {
        var en = Load("en");
        var uk = Load("uk");
        foreach (var key in Version016Keys)
        {
            Assert.True(en.TryGetValue(key, out var english) && !string.IsNullOrWhiteSpace(english),
                "missing English key " + key);
            Assert.True(uk.TryGetValue(key, out var ukrainian) && !string.IsNullOrWhiteSpace(ukrainian),
                "missing Ukrainian key " + key);
        }
    }

    [Fact]
    public void Version015Keys_ArePresentAndNonEmptyInBothLanguages()
    {
        var en = Load("en");
        var uk = Load("uk");
        foreach (var key in Version015Keys)
        {
            Assert.True(en.TryGetValue(key, out var english) && !string.IsNullOrWhiteSpace(english),
                "missing English key " + key);
            Assert.True(uk.TryGetValue(key, out var ukrainian) && !string.IsNullOrWhiteSpace(ukrainian),
                "missing Ukrainian key " + key);
        }
    }

    [Fact]
    public void Version014Keys_ArePresentAndNonEmptyInBothLanguages()
    {
        var en = Load("en");
        var uk = Load("uk");
        foreach (var key in Version014Keys)
        {
            Assert.True(en.TryGetValue(key, out var english) && !string.IsNullOrWhiteSpace(english),
                "missing English key " + key);
            Assert.True(uk.TryGetValue(key, out var ukrainian) && !string.IsNullOrWhiteSpace(ukrainian),
                "missing Ukrainian key " + key);
        }
    }

    [Fact]
    public void Version013Keys_ArePresentAndNonEmptyInBothLanguages()
    {
        var en = Load("en");
        var uk = Load("uk");
        foreach (var key in Version013Keys)
        {
            Assert.True(en.TryGetValue(key, out var english) && !string.IsNullOrWhiteSpace(english),
                "missing English key " + key);
            Assert.True(uk.TryGetValue(key, out var ukrainian) && !string.IsNullOrWhiteSpace(ukrainian),
                "missing Ukrainian key " + key);
        }
    }

    [Fact]
    public void NewCaptureModeKeys_ArePresentAndNonEmptyInBothLanguages()
    {
        var en = Load("en");
        var uk = Load("uk");

        foreach (var key in NewKeys)
        {
            Assert.True(en.TryGetValue(key, out var english) && !string.IsNullOrWhiteSpace(english),
                "missing English key " + key);
            Assert.True(uk.TryGetValue(key, out var ukrainian) && !string.IsNullOrWhiteSpace(ukrainian),
                "missing Ukrainian key " + key);
        }

        Assert.Equal("Game Only", en["fps_capture_game_only"]);
        Assert.Equal("All Applications", en["fps_capture_all_apps"]);
        Assert.Equal("Лише ігри", uk["fps_capture_game_only"]);
        Assert.Equal("Усі програми", uk["fps_capture_all_apps"]);
    }

    [Fact]
    public void ThemeKeys_AreLocalizedAndUseTheRequestedNames()
    {
        var en = Load("en");
        var uk = Load("uk");
        foreach (var key in new[]
                 {
                     "appearance_app_white", "appearance_app_black", "appearance_app_warm",
                     "appearance_app_custom", "appearance_custom_accent", "appearance_custom_choose",
                     "appearance_custom_hint", "appearance_custom_applied",
                 })
        {
            Assert.True(en.TryGetValue(key, out var english) && !string.IsNullOrWhiteSpace(english),
                "missing English key " + key);
            Assert.True(uk.TryGetValue(key, out var ukrainian) && !string.IsNullOrWhiteSpace(ukrainian),
                "missing Ukrainian key " + key);
        }

        Assert.Equal("White", en["appearance_app_white"]);
        Assert.Equal("Black", en["appearance_app_black"]);
        Assert.Equal("New Design", en["osd_library_capframex"]);
        Assert.Equal("Light", en["appearance_app_warm"]);
        Assert.Equal("Custom", en["appearance_app_custom"]);
        Assert.Equal("Біла", uk["appearance_app_white"]);
        Assert.Equal("Чорна", uk["appearance_app_black"]);
        Assert.Equal("Світлий", uk["appearance_app_warm"]);
        Assert.Equal("Власний", uk["appearance_app_custom"]);
    }

    [Fact]
    public void DynamicSystemInfoKeys_ExistInBothLanguages()
    {
        var en = Load("en");
        var uk = Load("uk");
        foreach (var key in new[]
                 {
                     "os.name", "os.edition", "os.version", "os.build", "os.arch",
                     "cpu.name", "cpu.vendor", "cpu.generation", "cpu.cores", "cpu.threads",
                     "cpu.max_clock", "cpu.socket",
                     "ram.total", "ram.manufacturer", "ram.type", "ram.speed", "ram.modules",
                     "ram.module_config",
                     "board.vendor", "board.model", "board.version",
                     "bios.vendor", "bios.version", "bios.date",
                 })
        {
            var localizationKey = "system_info_" + key.Replace('.', '_');
            Assert.True(en.TryGetValue(localizationKey, out var english) && !string.IsNullOrWhiteSpace(english),
                "missing English key " + localizationKey);
            Assert.True(uk.TryGetValue(localizationKey, out var ukrainian) && !string.IsNullOrWhiteSpace(ukrainian),
                "missing Ukrainian key " + localizationKey);
        }
    }

    [Fact]
    public void UpdatedFpsText_MentionsCaptureModes()
    {
        var en = Load("en");
        Assert.Contains("All Applications", en["fps_hint"]);
        Assert.Contains("Game Only", en["fps_detail_no_game"]);

        var uk = Load("uk");
        Assert.Contains("Усі програми", uk["fps_hint"]);
        Assert.Contains("Лише ігри", uk["fps_detail_no_game"]);
    }
}
