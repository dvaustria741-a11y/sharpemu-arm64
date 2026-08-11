// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using System.Text.Json;

namespace SharpEmu.Android;

/// <summary>
/// Minimal reader for the PS4 PARAM.SFO key/value format (used by every Sony platform since the
/// PS3 — it's a plain, unencrypted little-endian table, unrelated to PKG's outer-container
/// encryption). Neither this codebase nor SharpEmu.GUI ever actually produced the
/// <c>sce_sys/param.json</c> file that <see cref="GameLibraryStore"/> and the desktop library both
/// read — this fills that gap by parsing param.sfo straight out of an extracted PKG and writing the
/// JSON shape those two call sites already expect (titleId, contentVersion/masterVersion,
/// localizedParameters.&lt;lang&gt;.titleName).
/// </summary>
internal static class ParamSfo
{
    private const uint Magic = 0x46535000; // "\0PSF" little-endian

    /// <summary>Parses a param.sfo file and writes the equivalent param.json next to it.</summary>
    public static bool TryConvertToJson(string sfoPath, string jsonOutPath)
    {
        try
        {
            var entries = Parse(File.ReadAllBytes(sfoPath));
            if (entries is null)
            {
                return false;
            }

            entries.TryGetValue("TITLE_ID", out var titleId);
            entries.TryGetValue("APP_VER", out var appVer);
            entries.TryGetValue("VERSION", out var masterVersion);
            entries.TryGetValue("TITLE", out var title);

            var doc = new Dictionary<string, object?>
            {
                ["titleId"] = titleId,
                ["contentVersion"] = appVer,
                ["masterVersion"] = masterVersion,
                ["localizedParameters"] = new Dictionary<string, object?>
                {
                    ["defaultLanguage"] = "en-US",
                    ["en-US"] = new Dictionary<string, object?> { ["titleName"] = title },
                },
            };

            Directory.CreateDirectory(Path.GetDirectoryName(jsonOutPath)!);
            File.WriteAllText(jsonOutPath, JsonSerializer.Serialize(doc));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Raw string/int key-value entries from a param.sfo, or null if it isn't a valid SFO.</summary>
    private static Dictionary<string, string>? Parse(byte[] data)
    {
        if (data.Length < 20 || BitConverter.ToUInt32(data, 0) != Magic)
        {
            return null;
        }

        var keyTableOffset = BitConverter.ToInt32(data, 8);
        var dataTableOffset = BitConverter.ToInt32(data, 12);
        var entryCount = BitConverter.ToInt32(data, 16);

        var result = new Dictionary<string, string>();
        for (var i = 0; i < entryCount; i++)
        {
            var entryOffset = 20 + (i * 16);
            if (entryOffset + 16 > data.Length)
            {
                break;
            }

            var keyOffset = BitConverter.ToUInt16(data, entryOffset);
            var dataFmt = BitConverter.ToUInt16(data, entryOffset + 2);
            var dataLen = BitConverter.ToInt32(data, entryOffset + 4);
            var dataOffset = BitConverter.ToInt32(data, entryOffset + 12);

            var keyStart = keyTableOffset + keyOffset;
            var keyEnd = keyStart;
            while (keyEnd < data.Length && data[keyEnd] != 0)
            {
                keyEnd++;
            }
            var key = Encoding.UTF8.GetString(data, keyStart, keyEnd - keyStart);

            var valueStart = dataTableOffset + dataOffset;
            if (valueStart < 0 || valueStart + dataLen > data.Length)
            {
                continue;
            }

            string value;
            if (dataFmt == 0x0204) // UTF-8 string, null-terminated
            {
                var strLen = Array.IndexOf(data, (byte)0, valueStart, dataLen);
                var len = strLen >= 0 ? strLen - valueStart : dataLen;
                value = Encoding.UTF8.GetString(data, valueStart, len);
            }
            else if (dataFmt == 0x0404 && dataLen >= 4) // int32
            {
                value = BitConverter.ToInt32(data, valueStart).ToString();
            }
            else
            {
                continue;
            }

            result[key] = value;
        }

        return result;
    }
}
