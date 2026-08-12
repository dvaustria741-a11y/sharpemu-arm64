// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using SharpEmu.HLE.Host;

namespace SharpEmu.Android;

/// <summary>
/// Backing store for the global settings table exposed to the Kotlin/Compose UI via
/// <see cref="EmulatorBridgeImpl.Settings"/> / <c>UpdateSetting</c>.
///
/// The Kotlin side (<c>SettingsScreen</c> in <c>MainActivity.kt</c>) already renders a category
/// card for every distinct <c>section</c> it sees in the JSON this class returns — "Aparência"
/// (Appearance) is the only category hardcoded client-side; General, CPU (synthesized from
/// General's <c>cpu_</c>-prefixed keys), GPU, Vulkan, Audio, Log, Debug and Android all come
/// entirely from here. Until now <see cref="EmulatorBridgeImpl.Settings"/> returned a hardcoded
/// <c>{"settings":[]}</c>, so only Appearance ever showed up.
///
/// Definitions mirror the exact keys/types/defaults the Kotlin UI already knows how to render
/// (see <c>selectableSettingOptions</c>, <c>intEnumSettingOptions</c>, and the per-category
/// <c>*SettingGroup</c> functions in <c>MainActivity.kt</c>) so every card renders with the
/// correct control (switch, slider, or bottom-sheet picker) and label out of the box.
///
/// Values persist as plain JSON next to the rest of SharpEmu's Android app data, keyed by
/// "section/key" so a corrupt or partially-written file can't silently drop settings from other
/// sections.
/// </summary>
internal static class AndroidSettingsStore
{
    public sealed record SettingDef(string Section, string Key, string Type, string Default, bool Locked = false);

    // Order matters only for JSON output readability -- the Kotlin UI groups/sorts on its own.
    private static readonly SettingDef[] Defs =
    [
        // --- General (plus the single CPU-category entry, cpu_backend) -----------------------
        new("General", "cpu_backend", "string", "x64-interpreter", Locked: true),
        new("General", "show_splash", "bool", "true"),
        new("General", "console_language", "int", "1"),
        new("General", "connected_to_network", "bool", "true"),
        new("General", "neo_mode", "bool", "false"),
        new("General", "dev_kit_mode", "bool", "false"),
        new("General", "is_ps4_pro", "bool", "false"),
        new("General", "is_devkit", "bool", "false"),
        new("General", "extra_dmem_in_mbytes", "int", "0"),
        new("General", "memory_reclaim_enabled", "bool", "true"),
        new("General", "memory_exclusive_swap_enabled", "bool", "false"),
        new("General", "trophy_key", "string", ""),
        new("General", "trophy_popup_disabled", "bool", "false"),
        new("General", "trophy_notification_duration", "int", "5"),
        new("General", "trophy_notification_side", "string", "right"),
        new("General", "show_fps_counter", "bool", "false"),
        new("General", "show_ram_overlay", "bool", "false"),
        new("General", "show_gpu_overlay", "bool", "false"),
        new("General", "show_cpu_usage", "bool", "false"),
        new("General", "show_gpu_usage", "bool", "false"),
        new("General", "fps_overlay_position", "string", "top_left"),
        new("General", "ram_overlay_position", "string", "top_right"),
        new("General", "gpu_overlay_position", "string", "bottom_left"),
        new("General", "cpu_usage_position", "string", "bottom_right"),
        new("General", "gpu_usage_position", "string", "bottom_right"),

        // --- GPU (Graphics) -------------------------------------------------------------------
        new("GPU", "fsr_enabled", "bool", "true"),
        new("GPU", "rcas_enabled", "bool", "true"),
        new("GPU", "rcas_attenuation", "int", "25"),
        new("GPU", "android_performance_resolution_scale", "int", "85"),
        new("GPU", "full_screen", "bool", "true"),
        new("GPU", "full_screen_mode", "string", "Fullscreen"),
        new("GPU", "present_mode", "string", "Mailbox"),
        new("GPU", "vblank_frequency", "int", "1"),
        new("GPU", "hdr_allowed", "bool", "false"),
        new("GPU", "frame_generation_enabled", "bool", "false"),
        new("GPU", "frame_generation_multiplier", "int", "2"),
        new("GPU", "frame_generation_flow_scale", "double", "1.0"),
        new("GPU", "frame_generation_performance_mode", "bool", "false"),
        new("GPU", "null_gpu", "bool", "false"),
        new("GPU", "copy_gpu_buffers", "bool", "false"),
        new("GPU", "readbacks_mode", "int", "0"),
        new("GPU", "readback_linear_images_enabled", "bool", "false"),
        new("GPU", "direct_memory_access_enabled", "bool", "false"),
        new("GPU", "dump_shaders", "bool", "false"),
        new("GPU", "patch_shaders", "bool", "true"),
        // Android only ever exposes a single GPU -- kept for schema completeness, hidden client-side.
        new("GPU", "gpu_id", "int", "0", Locked: true),

        // --- Vulkan -----------------------------------------------------------------------------
        new("Vulkan", "vkvalidation_enabled", "bool", "false"),
        new("Vulkan", "vkvalidation_core_enabled", "bool", "false"),
        new("Vulkan", "vkvalidation_sync_enabled", "bool", "false"),
        new("Vulkan", "vkvalidation_gpu_enabled", "bool", "false"),
        new("Vulkan", "pipeline_cache_enabled", "bool", "true"),
        new("Vulkan", "pipeline_cache_archived", "bool", "true"),
        new("Vulkan", "vkhost_markers", "bool", "false"),
        new("Vulkan", "vkguest_markers", "bool", "false"),
        new("Vulkan", "pm4_dump_enabled", "bool", "false"),
        new("Vulkan", "renderdoc_enabled", "bool", "false"),
        new("Vulkan", "vkcrash_diagnostic_enabled", "bool", "false"),

        // --- Audio ------------------------------------------------------------------------------
        new("Audio", "audio_backend", "int", "0"),

        // --- Log --------------------------------------------------------------------------------
        new("Log", "enable", "bool", "true"),
        new("Log", "append", "bool", "false"),
        new("Log", "sync", "bool", "false"),
        new("Log", "separate", "bool", "false"),
        new("Log", "size_limit", "int", "100"),
        new("Log", "filter", "string", "*:Info"),
        new("Log", "skip_duplicate", "bool", "true"),
        new("Log", "max_skip_duration", "int", "3000"),

        // --- Debug (shown as a flat, ungrouped list) ---------------------------------------------
        new("Debug", "dump_shaders", "bool", "false"),
        new("Debug", "dump_pm4", "bool", "false"),
        new("Debug", "collect_shader_debug_info", "bool", "false"),
        new("Debug", "crash_diagnostics_enabled", "bool", "true"),
        new("Debug", "verbose_logging", "bool", "false"),

        // --- Android ------------------------------------------------------------------------------
        new("Android", "data_root", "string", "", Locked: true),
        new("Android", "picture_in_picture_enabled", "bool", "true"),
        new("Android", "vulkan_driver", "string", "system"),
        new("Android", "adrenotools_turbo", "bool", "false"),
        new("Android", "android_debug_overlay", "bool", "false"),
        new("Android", "android_diagnostics", "bool", "false"),
        new("Android", "texture_format_diagnostics", "bool", "false"),
    ];

    private static readonly object Lock = new();
    private static Dictionary<string, string>? _values;

    private static string StorePath =>
        Path.Combine(
            AndroidHostPaths.InternalFilesRoot ?? AndroidHostPaths.ExternalFilesRoot ?? Path.GetTempPath(),
            "android_settings.json");

    private static string DefKey(string section, string key) => section + "/" + key;

    private static Dictionary<string, string> LoadValues()
    {
        if (_values != null)
        {
            return _values;
        }

        lock (Lock)
        {
            if (_values != null)
            {
                return _values;
            }

            var loaded = new Dictionary<string, string>();
            try
            {
                var path = StorePath;
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (parsed != null)
                    {
                        loaded = parsed;
                    }
                }
            }
            catch
            {
                // Corrupt or unreadable store -- fall back to built-in defaults rather than crash.
            }

            _values = loaded;
            return _values;
        }
    }

    private static void Persist()
    {
        try
        {
            var path = StorePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(_values));
        }
        catch
        {
            // Best-effort persistence -- an in-memory value is still better than crashing the UI.
        }
    }

    /// <summary>Builds the JSON payload consumed by <c>EmulatorRepository.loadSettings()</c>.</summary>
    public static string GetSettingsJson()
    {
        var values = LoadValues();
        var settings = Defs.Select(def => new
        {
            section = def.Section,
            key = def.Key,
            type = def.Type,
            value = values.TryGetValue(DefKey(def.Section, def.Key), out var stored) ? stored : def.Default,
            locked = def.Locked,
            default_ = def.Default,
        }).Select(s => new Dictionary<string, object>
        {
            ["section"] = s.section,
            ["key"] = s.key,
            ["type"] = s.type,
            ["value"] = s.value,
            ["locked"] = s.locked,
            ["default"] = s.default_,
        });

        return JsonSerializer.Serialize(new { root = AndroidHostPaths.ExternalFilesRoot ?? "", settings });
    }

    /// <summary>Applies an update from <c>updateSetting(section, key, value)</c>.</summary>
    public static bool UpdateSetting(string section, string key, string value)
    {
        var def = Defs.FirstOrDefault(d => d.Section == section && d.Key == key);
        if (def == null || def.Locked)
        {
            return false;
        }

        lock (Lock)
        {
            var values = LoadValues();
            values[DefKey(section, key)] = value;
            Persist();
        }

        return true;
    }
}
