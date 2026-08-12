// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO.Compression;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using SharpEmu.HLE.Host;

namespace SharpEmu.Android;

/// <summary>
/// Backs the "GPU drivers" screen (<c>getGpuDrivers</c>/<c>installGpuDriver</c>/<c>selectGpuDriver</c>
/// in <c>EmulatorBridge</c>). Was previously a hardcoded stub (<c>GpuDrivers => "[]"</c>,
/// <c>InstallGpuDriver => "{}"</c>, <c>SelectGpuDriver => false</c>) — that's why picking a driver
/// zip in the file browser always ended in a failure toast with nothing installed: the extract
/// call was never actually made.
///
/// Installed drivers live under <c>&lt;internalFilesRoot&gt;/gpu_drivers/&lt;id&gt;/</c>, matching
/// the path <c>EmulatorRepository.readDriverMeta</c> already reads
/// (<c>context.filesDir/SharpEmu/gpu_drivers/&lt;id&gt;/meta.json</c>) — <c>InternalFilesRoot</c>
/// is set to <c>&lt;filesDir&gt;/SharpEmu</c> in <see cref="EmulatorBridgeImpl.Initialize"/>, so no
/// path translation is needed on the Kotlin side.
///
/// IMPORTANT — this only stores and selects a driver package; it does not yet make the Vulkan
/// renderer actually load it (that needs an adrenotools-style <c>android_dlopen_ext</c> swap in the
/// native/graphics init path, which SharpEmu doesn't have yet — the emulator still renders with
/// whatever the system driver resolves to regardless of the selection recorded here). Selecting a
/// driver will show as active in the UI but won't change in-game rendering until that loader exists.
/// </summary>
internal static class AndroidGpuDriverStore
{
    private static string DriversDir =>
        Path.Combine(
            AndroidHostPaths.InternalFilesRoot ?? AndroidHostPaths.ExternalFilesRoot ?? Path.GetTempPath(),
            "gpu_drivers");

    public readonly record struct InstallResult(bool Ok, string Message, string Path);

    public static string GetDriversJson()
    {
        var activeId = AndroidSettingsStore.GetValue("Android", "vulkan_driver", "system");
        var entries = new List<Dictionary<string, object>>
        {
            new()
            {
                ["id"] = "system",
                ["title"] = "System driver",
                ["active"] = activeId == "system" || string.IsNullOrEmpty(activeId),
            },
        };

        try
        {
            var dir = DriversDir;
            if (Directory.Exists(dir))
            {
                foreach (var sub in Directory.GetDirectories(dir).OrderBy(d => d))
                {
                    var id = Path.GetFileName(sub);
                    var title = id;
                    var metaPath = Path.Combine(sub, "meta.json");
                    if (File.Exists(metaPath))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                            if (doc.RootElement.TryGetProperty("name", out var nameEl) &&
                                nameEl.GetString() is { Length: > 0 } n)
                            {
                                title = n;
                            }
                        }
                        catch
                        {
                            // Malformed meta.json -- fall back to the folder name as the title.
                        }
                    }

                    entries.Add(new Dictionary<string, object>
                    {
                        ["id"] = id,
                        ["title"] = title,
                        ["active"] = id == activeId,
                    });
                }
            }
        }
        catch
        {
            // If the drivers directory can't be listed, still return at least the system entry.
        }

        return JsonSerializer.Serialize(entries);
    }

    public static bool SelectDriver(string driverId)
    {
        if (string.IsNullOrWhiteSpace(driverId))
        {
            return false;
        }

        if (driverId != "system" && !Directory.Exists(Path.Combine(DriversDir, driverId)))
        {
            return false;
        }

        return AndroidSettingsStore.UpdateSetting("Android", "vulkan_driver", driverId);
    }

    // Takes ownership of handle: it's read via a FileStream constructed from it, which closes the
    // underlying fd when disposed (matches how the fd is a one-shot, already-detached descriptor
    // from ParcelFileDescriptor.detachFd() on the Kotlin side, same as PkgInstaller's contract).
    public static InstallResult Install(string displayName, SafeFileHandle handle)
    {
        try
        {
            using var stream = new FileStream(handle, FileAccess.Read);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

            var id = SanitizeId(Path.GetFileNameWithoutExtension(displayName));
            var destDir = Path.Combine(DriversDir, id);
            var suffix = 1;
            while (Directory.Exists(destDir))
            {
                destDir = Path.Combine(DriversDir, $"{id}_{suffix}");
                suffix++;
            }

            Directory.CreateDirectory(destDir);

            var hasSharedObject = false;
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue; // directory entry
                }

                var entryDest = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
                if (!entryDest.StartsWith(Path.GetFullPath(destDir), StringComparison.Ordinal))
                {
                    continue; // guard against zip-slip
                }

                Directory.CreateDirectory(Path.GetDirectoryName(entryDest)!);
                entry.ExtractToFile(entryDest, overwrite: true);
                if (entryDest.EndsWith(".so", StringComparison.OrdinalIgnoreCase))
                {
                    hasSharedObject = true;
                }
            }

            if (!hasSharedObject)
            {
                Directory.Delete(destDir, recursive: true);
                return new InstallResult(false, "That archive doesn't contain a driver library (.so) \u2014 is this a Turnip/Adreno driver zip?", "");
            }

            var metaPath = Path.Combine(destDir, "meta.json");
            if (!File.Exists(metaPath))
            {
                // No AdrenoTools meta.json bundled -- synthesize a minimal one so the driver still
                // shows a meaningful name/subtitle in the UI instead of just its folder id.
                var synthesized = JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    name = Path.GetFileNameWithoutExtension(displayName),
                    description = "Imported driver",
                    author = "",
                    packageVersion = "1",
                    vendor = "",
                    driverVersion = "",
                    minApi = 27,
                    libraryName = zip.Entries.FirstOrDefault(
                        e => e.Name.EndsWith(".so", StringComparison.OrdinalIgnoreCase))?.Name ?? "",
                });
                File.WriteAllText(metaPath, synthesized);
            }

            return new InstallResult(true, "Installed", destDir);
        }
        catch (InvalidDataException)
        {
            return new InstallResult(false, "Not a valid ZIP archive", "");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new InstallResult(false, $"Could not install driver: {exception.Message}", "");
        }
    }

    private static string SanitizeId(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['_', ' ']).ToHashSet();
        var chars = name.Select(c => invalid.Contains(c) && c != '_' ? '-' : c).ToArray();
        var result = new string(chars).Trim('-').ToLowerInvariant();
        return string.IsNullOrEmpty(result) ? "driver" : result;
    }
}
