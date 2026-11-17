// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.IO.Compression;

namespace CodexFlow.Core.Documents;

/// <summary>
/// Validates OOXML (ZIP-packaged) documents against zip-bomb / excessive entry count attacks before opening.
/// </summary>
internal static class ZipArchiveSafetyGuard
{
    private const int MaxZipEntryCount = 10_000;

    public static bool Validate(string filePath, long maxFileBytes, long maxTotalUncompressedBytes, out string? errorMessage)
    {
        errorMessage = null;
        var info = new FileInfo(filePath);
        if (!info.Exists)
        {
            errorMessage = $"文件不存在: {filePath}";
            return false;
        }

        if (info.Length == 0)
        {
            errorMessage = "文件为空 (0 B)。";
            return false;
        }

        if (info.Length > maxFileBytes)
        {
            errorMessage = $"文件大小 ({info.Length} 字节) 超过解析上限 ({maxFileBytes} 字节)。";
            return false;
        }

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var archive = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count > MaxZipEntryCount)
            {
                errorMessage = $"压缩包条目数过多 ({archive.Entries.Count})，已拒绝解析以防资源耗尽。";
                return false;
            }

            long totalDeclared = 0;
            foreach (var entry in archive.Entries)
            {
                if (entry.Length < 0)
                {
                    continue;
                }

                totalDeclared += entry.Length;
                if (totalDeclared > maxTotalUncompressedBytes)
                {
                    errorMessage = $"文档解压后体积超过安全上限 ({maxTotalUncompressedBytes / (1024 * 1024)} MB)，已拒绝解析。";
                    return false;
                }
            }

            return true;
        }
        catch (InvalidDataException)
        {
            errorMessage = "文档 ZIP 容器已损坏或格式无效。";
            return false;
        }
    }
}
