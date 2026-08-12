// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SharpEmu.HLE.Host;

namespace SharpEmu.Android;

/// <summary>
/// Installs a PS4 PKG by calling into <c>libbachata_pkg.so</c> — a prebuilt native library from the
/// Bachata S4 project (GPL-2.0-or-later, same license as SharpEmu; see
/// jniLibs/arm64-v8a/NOTICE-libbachata_pkg.txt) — via P/Invoke against its plain C API
/// (bachata_pkg_probe / bachata_pkg_extract / bachata_pkg_cancel).
///
/// SharpEmu itself has no PFS filesystem or key-derivation code of its own: that's real,
/// hard-won systems code (custom inode/dirent filesystem behind AES-XTS encryption, with a
/// multi-step RSA+HMAC key derivation) that the Bachata S4 authors already wrote, tested, and
/// shipped against real games — re-deriving it here by hand, un-compiled and un-tested, would be
/// far more likely to introduce silent extraction bugs than to help. We just call it.
///
/// Only the well-known public "fake PKG"/debug-kit key material (also used by LibOrbisPkg and
/// shadPS4 — see PkgRsa.kt in the Bachata S4 source for provenance) is exercised when no passcode
/// is supplied, which is what this class does. That decrypts homebrew/dev-signed packages. A real
/// retail PKG will make the library report <see cref="Status.NeedPasscode"/> instead of silently
/// failing or being force-decrypted — SharpEmu does not attempt to supply or derive a real
/// publisher's retail entitlement key on the user's behalf.
/// </summary>
internal static class PkgInstaller
{
    private enum Status
    {
        Ok = 0,
        NeedPasscode = 1,
        Cancelled = 2,
        Error = 3,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BachataPkgProbe
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 0x30)]
        public byte[] ContentId;
        public ulong PackageSize;
        public ulong PfsImageSize;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 0x80)]
        public byte[] TitleHint;
        public int Status;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public byte[] Message;

        public static BachataPkgProbe Create() => new()
        {
            ContentId = new byte[0x30],
            TitleHint = new byte[0x80],
            Message = new byte[256],
        };
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ProgressCallback(
        IntPtr ctx, ulong done, ulong total, [MarshalAs(UnmanagedType.LPUTF8Str)] string file);

    [DllImport("bachata_pkg", CallingConvention = CallingConvention.Cdecl)]
    private static extern int bachata_pkg_probe(int fd, ref BachataPkgProbe out_probe);

    [DllImport("bachata_pkg", CallingConvention = CallingConvention.Cdecl)]
    private static extern int bachata_pkg_extract(
        int fd,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string out_path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? passcode_or_null,
        ProgressCallback progress,
        IntPtr progress_ctx);

    [DllImport("bachata_pkg", CallingConvention = CallingConvention.Cdecl)]
    private static extern void bachata_pkg_cancel();

    public static volatile int Progress;

    public readonly record struct PkgInstallResult(bool Ok, string Message, string Path);

    public static void Cancel() => bachata_pkg_cancel();

    public static PkgInstallResult Install(string displayName, SafeFileHandle handle, string installRootPath)
    {
        Progress = 0;
        var refAdded = false;
        try
        {
            handle.DangerousAddRef(ref refAdded);
            var fd = (int)handle.DangerousGetHandle();

            var probe = BachataPkgProbe.Create();
            var probeStatus = (Status)bachata_pkg_probe(fd, ref probe);
            if (probeStatus == Status.NeedPasscode)
            {
                return new PkgInstallResult(
                    false,
                    "This PKG needs a real retail entitlement key (passcode) to decrypt \u2014 " +
                    "SharpEmu only auto-decrypts homebrew/dev-signed packages. Installing a legitimate " +
                    "retail dump needs its passcode supplied explicitly.",
                    "");
            }

            if (probeStatus != Status.Ok)
            {
                var probeMessage = TrimNullTerminated(probe.Message);
                return new PkgInstallResult(false, string.IsNullOrEmpty(probeMessage) ? "Not a valid PKG file" : probeMessage, "");
            }

            var contentId = TrimNullTerminated(probe.ContentId);
            var safeFolderName = string.IsNullOrWhiteSpace(contentId)
                ? SanitizeFileName(Path.GetFileNameWithoutExtension(displayName))
                : SanitizeFileName(contentId);

            var installRoot = string.IsNullOrWhiteSpace(installRootPath)
                ? Path.Combine(AndroidHostPaths.ExternalFilesRoot ?? AndroidHostPaths.InternalFilesRoot ?? Path.GetTempPath(), "Games")
                : installRootPath;
            var gameDir = Path.Combine(installRoot, safeFolderName);
            Directory.CreateDirectory(gameDir);

            ProgressCallback onProgress = (_, done, total, _) =>
            {
                Progress = total == 0 ? 0 : (int)Math.Clamp(done * 100UL / total, 0, 100);
            };

            var extractStatus = (Status)bachata_pkg_extract(fd, gameDir, passcode_or_null: null, onProgress, IntPtr.Zero);
            GC.KeepAlive(onProgress);

            if (extractStatus == Status.NeedPasscode)
            {
                return new PkgInstallResult(
                    false,
                    "This PKG needs a real retail entitlement key (passcode) to decrypt \u2014 " +
                    "SharpEmu only auto-decrypts homebrew/dev-signed packages.",
                    gameDir);
            }

            if (extractStatus == Status.Cancelled)
            {
                return new PkgInstallResult(false, "Install cancelled", gameDir);
            }

            if (extractStatus != Status.Ok)
            {
                return new PkgInstallResult(false, "PKG extraction failed \u2014 see logcat for details", gameDir);
            }

            var sfoPath = Path.Combine(gameDir, "sce_sys", "param.sfo");
            if (File.Exists(sfoPath))
            {
                ParamSfo.TryConvertToJson(sfoPath, Path.Combine(gameDir, "sce_sys", "param.json"));
            }

            return new PkgInstallResult(true, "Installed", gameDir);
        }
        finally
        {
            if (refAdded)
            {
                handle.DangerousRelease();
            }

            Progress = 100;
        }
    }

    private static string TrimNullTerminated(byte[] bytes)
    {
        var end = Array.IndexOf(bytes, (byte)0);
        var len = end >= 0 ? end : bytes.Length;
        return System.Text.Encoding.UTF8.GetString(bytes, 0, len);
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim();
        return string.IsNullOrEmpty(result) ? "Game" : result;
    }
}
