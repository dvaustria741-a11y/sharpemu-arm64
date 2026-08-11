// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE.Host;

namespace SharpEmu.Android;

/// <summary>
/// Parses the outer PS4 PKG container (header + big-endian file table — this part of the format is
/// a plain, unencrypted archive index, standardized across every PS4 packaging tool) and extracts
/// what it can: sce_sys metadata (param.sfo → param.json, icon0.png, pic0.png), then registers the
/// title with <see cref="GameLibraryStore"/>.
///
/// What this deliberately does NOT do yet: decode the actual game filesystem. The pkg file table only
/// lists a handful of outer entries (param.sfo, icons, playgo/changeinfo metadata, and one or more
/// "pfs_image" blobs) — eboot.bin and every other game file live *inside* that pfs_image as a separate,
/// normally AES-encrypted mini filesystem (inode/dirent table + per-block crypto) that has to be
/// decrypted and walked on its own. That's a real subsystem (shadPS4 calls it Core::FileSys::PSF /
/// pfs_shared_fs) that hasn't been ported to SharpEmu at all yet, on Android or desktop. Rather than
/// fake success here (which is exactly the bug this file replaces — InstallPkg silently returning
/// "{}"), we surface that honestly via PkgInstallResult.Message so the UI can tell the user what
/// actually happened instead of showing an empty toast.
/// </summary>
internal static class PkgInstaller
{
    private const uint Magic = 0x7F434E54;

    public static volatile int Progress;

    public readonly record struct PkgInstallResult(bool Ok, string Message, string Path);

    public static PkgInstallResult Install(string displayName, Stream source, string installRootPath)
    {
        Progress = 0;
        try
        {
            Span<byte> headerBuf = stackalloc byte[0x80];
            if (source.Read(headerBuf) != headerBuf.Length)
            {
                return new PkgInstallResult(false, "File is too small to be a valid PKG", "");
            }

            if (BinaryPrimitives.ReadUInt32BigEndian(headerBuf) != Magic)
            {
                return new PkgInstallResult(false, "Not a PS4 PKG file (bad magic)", "");
            }

            var entryCount = BinaryPrimitives.ReadUInt32BigEndian(headerBuf[0x10..]);
            var tableOffset = BinaryPrimitives.ReadUInt32BigEndian(headerBuf[0x18..]);
            var contentId = Encoding.ASCII.GetString(headerBuf[0x40..0x64]).TrimEnd('\0');

            if (entryCount == 0 || entryCount > 4096)
            {
                return new PkgInstallResult(false, "PKG file table looks corrupt (bad entry count)", "");
            }

            Progress = 5;

            var entries = ReadFileTable(source, tableOffset, entryCount);
            if (entries.Count == 0)
            {
                return new PkgInstallResult(false, "Could not read the PKG file table", "");
            }

            // Entry id 0x0200 holds the null-terminated name of every other entry, back to back, in
            // table order — that's how we resolve "which entry is param.sfo" without depending on the
            // exact numeric entry-id assignments (which do vary a bit between pkg generator versions).
            var namesEntry = entries.FirstOrDefault(e => e.Id == 0x0200);
            var nameBlob = namesEntry.Size > 0 ? ReadNameTable(source, namesEntry) : [];

            Progress = 15;

            var safeFolderName = string.IsNullOrWhiteSpace(contentId)
                ? SanitizeFileName(Path.GetFileNameWithoutExtension(displayName))
                : SanitizeFileName(contentId);

            var installRoot = string.IsNullOrWhiteSpace(installRootPath)
                ? Path.Combine(AndroidHostPaths.ExternalFilesRoot ?? AndroidHostPaths.InternalFilesRoot ?? Path.GetTempPath(), "Games")
                : installRootPath;

            var gameDir = Path.Combine(installRoot, safeFolderName);
            var sceSysDir = Path.Combine(gameDir, "sce_sys");
            Directory.CreateDirectory(sceSysDir);

            var sfoOnDisk = ExtractNamedEntry(source, entries, nameBlob, "param.sfo", Path.Combine(sceSysDir, "param.sfo"));
            ExtractNamedEntry(source, entries, nameBlob, "icon0.png", Path.Combine(sceSysDir, "icon0.png"));
            ExtractNamedEntry(source, entries, nameBlob, "pic0.png", Path.Combine(sceSysDir, "pic0.png"));

            Progress = 40;

            if (sfoOnDisk)
            {
                ParamSfo.TryConvertToJson(
                    Path.Combine(sceSysDir, "param.sfo"),
                    Path.Combine(sceSysDir, "param.json"));
            }

            Progress = 50;

            var hasPfsImage = entries.Any(e =>
                NameAt(nameBlob, e.NameOffset).Contains("pfs_image", StringComparison.OrdinalIgnoreCase));

            if (!hasPfsImage)
            {
                return new PkgInstallResult(
                    false,
                    "PKG has no pfs_image entry — this doesn't look like a full game package",
                    gameDir);
            }

            // TODO: decrypt + mount pfs_image and extract eboot.bin/sce_module/etc. Needs the PFS
            // filesystem + AES key derivation ported from a real PS4 PKG reference implementation —
            // see the class doc comment. Until then, be honest instead of reporting fake success.
            return new PkgInstallResult(
                false,
                "Metadata extracted (title info + icons), but game-data extraction (PFS image decrypt) " +
                "isn't implemented yet — the game itself wasn't installed.",
                gameDir);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new PkgInstallResult(false, $"Install failed: {exception.Message}", "");
        }
        finally
        {
            Progress = 100;
        }
    }

    private readonly record struct PkgEntry(uint Id, uint NameOffset, uint Flags1, uint Offset, uint Size);

    private static List<PkgEntry> ReadFileTable(Stream source, uint tableOffset, uint entryCount)
    {
        var result = new List<PkgEntry>((int)entryCount);
        var buf = new byte[32];
        source.Seek(tableOffset, SeekOrigin.Begin);
        for (var i = 0; i < entryCount; i++)
        {
            if (source.Read(buf, 0, buf.Length) != buf.Length)
            {
                break;
            }

            result.Add(new PkgEntry(
                Id: BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(0)),
                NameOffset: BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(4)),
                Flags1: BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(8)),
                Offset: BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(16)),
                Size: BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(20))));
        }

        return result;
    }

    // Reads the raw name-table bytes as-is (no UTF-8 round-trip) — every PkgEntry.NameOffset is a
    // byte offset into this blob, and round-tripping through a C# string first would corrupt those
    // offsets if the blob contains anything that isn't valid UTF-8.
    private static byte[] ReadNameTable(Stream source, PkgEntry namesEntry)
    {
        if (namesEntry.Size == 0 || namesEntry.Size > 1024 * 1024)
        {
            return [];
        }

        var blob = new byte[namesEntry.Size];
        source.Seek(namesEntry.Offset, SeekOrigin.Begin);
        return source.Read(blob, 0, blob.Length) == blob.Length ? blob : [];
    }

    private static string NameAt(byte[] nameBlob, uint offset)
    {
        if (offset >= nameBlob.Length)
        {
            return "";
        }

        var end = Array.IndexOf(nameBlob, (byte)0, (int)offset);
        var len = end >= 0 ? end - (int)offset : nameBlob.Length - (int)offset;
        return Encoding.UTF8.GetString(nameBlob, (int)offset, len);
    }

    private static bool ExtractNamedEntry(
        Stream source, List<PkgEntry> entries, byte[] nameBlob, string fileName, string destPath)
    {
        foreach (var entry in entries)
        {
            var name = NameAt(nameBlob, entry.NameOffset);
            if (!name.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if ((entry.Flags1 & 0x80000000) != 0)
            {
                // Encrypted entry (rare for these small metadata files, but possible) — skip rather
                // than write garbage bytes to disk.
                return false;
            }

            source.Seek(entry.Offset, SeekOrigin.Begin);
            var buf = new byte[entry.Size];
            if (source.Read(buf, 0, buf.Length) != buf.Length)
            {
                return false;
            }

            File.WriteAllBytes(destPath, buf);
            return true;
        }

        return false;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim();
        return string.IsNullOrEmpty(result) ? "Game" : result;
    }
}
