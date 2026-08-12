// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using Android.Views;
using Microsoft.Win32.SafeHandles;
using Org.Sharpemu.Android.Core;
using SharpEmu.HLE.Host;

namespace SharpEmu.Android;

/// <summary>
/// Implements the Kotlin-defined <c>EmulatorBridge</c> interface (from platform/android/app,
/// bound into this project via the AndroidLibrary reference) — this is the entire contact surface
/// between the Kotlin/Compose UI and SharpEmu's managed core. <see cref="SharpEmuApplication"/>
/// installs this as <c>EmulatorBridgeHolder.Instance.Bridge</c> before any Activity in the library runs.
/// </summary>
public sealed class EmulatorBridgeImpl : Java.Lang.Object, IEmulatorBridge
{
    public string Initialize(string packageName, string externalStorageRoot, string internalDataRoot)
    {
        var externalRoot = System.IO.Path.Combine(externalStorageRoot, "Android", "data", packageName, "files", "SharpEmu");
        try
        {
            System.IO.Directory.CreateDirectory(externalRoot);
            AndroidHostPaths.ExternalFilesRoot = externalRoot;
        }
        catch
        {
            // Scoped-storage provisioning failed (shouldn't happen — the Kotlin side already calls
            // Context.getExternalFilesDir(null) before this, forcing the OS to create the parent
            // directory tree with the right ownership) — fall back to internal storage.
        }

        var internalRoot = System.IO.Path.Combine(internalDataRoot, "SharpEmu");
        System.IO.Directory.CreateDirectory(internalRoot);
        AndroidHostPaths.InternalFilesRoot = internalRoot;

        return AndroidHostPaths.ExternalFilesRoot ?? internalRoot;
    }

    // Kotlin's no-arg getXxx() functions bind to C# properties (Java Bean convention), not methods —
    // only parameterized getters (GetGameDetails(id), etc.) stay methods.
    public string AppRoot => AndroidHostPaths.ExternalFilesRoot ?? AndroidHostPaths.InternalFilesRoot ?? string.Empty;

    // --- Settings / library / trophies / patches / cheats / GPU drivers ---------------------
    // Global settings (General/CPU/GPU/Vulkan/Audio/Log/Debug/Android) are now backed by
    // AndroidSettingsStore -- see its doc comment for how the section list drives the category
    // cards the Kotlin Settings screen renders. Per-game settings, trophies, patches, and cheats
    // are still TODO: port from shadPS4's Core::Android::* (android_app_host.cpp) once SharpEmu has
    // the equivalent game-scan/trophy/patch/cheat subsystems wired for Android. Left stubbed with
    // empty-but-valid JSON so the Kotlin UI doesn't crash while those are built out.
    // loadSettings()/loadGameSettings() in EmulatorRepository.kt read these with the UNSAFE
    // root.getJSONArray("settings") (no .opt fallback) — the "settings" key must always be present.
    public string Settings => AndroidSettingsStore.GetSettingsJson();
    public bool UpdateSetting(string section, string key, string value) =>
        AndroidSettingsStore.UpdateSetting(section, key, value);
    public string Games => GameLibraryStore.GetGames();
    public string GetGameDetails(string gameId) => "{}";
    public bool AddGameFolder(string displayName, string uri) => GameLibraryStore.AddGameFolder(displayName, uri);
    public bool DeleteGame(string gameId, bool forceDeleteFolder) => GameLibraryStore.DeleteGame(gameId, forceDeleteFolder);
    public string GetTrophyInfo(string gameId) => "{}";
    public string GetPatchInfo(string gameId) => "{}";
    public bool SetPatchEnabled(string gameId, string patchName, bool enabled) => false;
    public string GetEnabledCheats(string gameId) => "[]";
    public bool SetCheatEnabled(string gameId, string cheatName, bool enabled, string lines) => false;
    public string GetGameSettings(string gameId) => "{\"id\":\"" + gameId + "\",\"settings\":[]}";
    public bool UpdateGameSetting(string gameId, string section, string key, string value) => false;
    public bool ResetGameSetting(string gameId, string section, string key) => false;
    public string InstallPkg(string displayName, int fd, string sourcePath, string installRootPath)
    {
        try
        {
            using var handle = string.IsNullOrEmpty(sourcePath)
                ? new SafeFileHandle((IntPtr)fd, ownsHandle: true)
                : File.OpenHandle(sourcePath, FileMode.Open, FileAccess.Read);

            var result = PkgInstaller.Install(displayName, handle, installRootPath);

            if (result.Ok)
            {
                GameLibraryStore.AddGameFolder(displayName, result.Path);
            }

            return JsonSerializer.Serialize(new { ok = result.Ok, message = result.Message, path = result.Path });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return JsonSerializer.Serialize(new { ok = false, message = $"Could not open PKG: {exception.Message}", path = "" });
        }
    }

    public int InstallProgress => PkgInstaller.Progress;
    public int DeleteProgress => 0;
    public void CancelInstallPkg() => PkgInstaller.Cancel();

    // --- GPU drivers -----------------------------------------------------------------------
    // Lists/installs/selects custom Adreno (Turnip) driver packages -- see AndroidGpuDriverStore's
    // doc comment for what "select" does and doesn't do yet (it records the choice; the renderer
    // doesn't load it yet, so this alone won't fix in-game rendering/crashes).
    public string GpuDrivers => AndroidGpuDriverStore.GetDriversJson();
    public bool SelectGpuDriver(string driverId) => AndroidGpuDriverStore.SelectDriver(driverId);
    public string InstallGpuDriver(string displayName, int fd)
    {
        var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        var result = AndroidGpuDriverStore.Install(displayName, handle);
        return JsonSerializer.Serialize(new { ok = result.Ok, message = result.Message, path = result.Path });
    }

    public string InstallLsfgDll(string displayName, int fd) => "{}";

    // --- Virtual gamepad -----------------------------------------------------------------------
    public void SetPadButton(int button, bool pressed) => GameSession.SetPadButton(button, pressed);
    public void SetPadAxis(int axis, int value) => GameSession.SetPadAxis(axis, value);
    public void RequestRenderDiagCapture() => GameSession.RequestRenderDiagCapture();

    // --- Emulation -----------------------------------------------------------------------------
    // Actual emulation is started from GameActivity.Main() (SDLActivity's own dedicated thread),
    // not from this Surface-ready callback — GameActivity extends SDLActivity directly (SDL3 has to
    // own the Android window itself; see GameActivity's doc comment), so by the time Main() runs,
    // SDL already has a valid window/surface of its own. This callback exists on the interface for
    // API symmetry with the original NativeBridge design and is intentionally a no-op here.
    public void StartEmulation(Surface surface, string gamePath, string titleId, IDictionary<string, string> args) { }

    public void SurfaceDestroyed() { }

    public void StopEmulation() => GameSession.Stop();
}
