// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Hashline.Infrastructure;
using CodexFlow.Core.Hashline.Models;
using Xunit;

namespace CodexFlow.Core.Tests.Hashline;

/// <summary>
/// FileSystemGuard 单元测试。
/// </summary>
public class FileSystemGuardTests
{
    [Fact]
    public void IsPathAllowed_EmptyAllowedRoots_DeniesAllPaths()
    {
        var guard = new FileSystemGuard();
        var allowedRoots = new List<string>();

        // 空数组应该拒绝所有路径（严格模式）
        Assert.False(guard.IsPathAllowed("/any/path/file.txt", allowedRoots));
        Assert.False(guard.IsPathAllowed("C:\\Any\\Path\\file.txt", allowedRoots));
    }

    [Fact]
    public void IsPathAllowed_WithWorkspaceRoot_AllowsPathsUnderRoot()
    {
        var guard = new FileSystemGuard();
        var allowedRoots = new List<string> { "/workspace" };

        // 允许 workspace 下的路径
        Assert.True(guard.IsPathAllowed("/workspace/project1/file.txt", allowedRoots));
        Assert.True(guard.IsPathAllowed("/workspace/file.txt", allowedRoots));
    }

    [Fact]
    public void IsPathAllowed_NullAllowedRoots_ThrowsArgumentNullException()
    {
        var guard = new FileSystemGuard();

        Assert.Throws<ArgumentNullException>(() => guard.IsPathAllowed("/any/path/file.txt", null!));
    }

    [Fact]
    public void IsPathAllowed_WithAllowedRoots_OnlyAllowsPathsUnderRoots()
    {
        var guard = new FileSystemGuard();
        var allowedRoots = new List<string> { "/workspace", "/projects" };

        Assert.True(guard.IsPathAllowed("/workspace/project1/file.txt", allowedRoots));
        Assert.True(guard.IsPathAllowed("/projects/myapp/src/code.cs", allowedRoots));
        Assert.False(guard.IsPathAllowed("/other/path/file.txt", allowedRoots));
        Assert.False(guard.IsPathAllowed("/workspace2/file.txt", allowedRoots)); // 不应该匹配前缀
    }

    [Fact]
    public void IsPathAllowed_EmptyFilePath_ReturnsFalse()
    {
        var guard = new FileSystemGuard();
        var allowedRoots = new List<string> { "/workspace" };

        Assert.False(guard.IsPathAllowed("", allowedRoots));
        Assert.False(guard.IsPathAllowed(null!, allowedRoots));
    }

    [Fact]
    public void IsPathAllowed_AllowedRootsWithEmptyEntries_SkipsEmptyEntriesAndUsesValidRoots()
    {
        var guard = new FileSystemGuard();
        var allowedRoots = new List<string> { "", "/workspace", null! };

        // 应该跳过空和 null 的条目，仍然允许 /workspace 下的文件
        Assert.True(guard.IsPathAllowed("/workspace/file.txt", allowedRoots));
        // 应该拒绝不在有效 root 下的文件
        Assert.False(guard.IsPathAllowed("/other/file.txt", allowedRoots));
    }

    [Fact]
    public void IsPathAllowed_AllowedRootsWithOnlyEmptyEntries_DeniesAllPaths()
    {
        var guard = new FileSystemGuard();
        var allowedRoots = new List<string> { "", null! };

        // 只有空条目时，应该拒绝所有路径
        Assert.False(guard.IsPathAllowed("/any/file.txt", allowedRoots));
    }

    [Fact]
    public void IsBinaryFile_WithNullContent_ReturnsFalse()
    {
        var guard = new FileSystemGuard();

        Assert.False(guard.IsBinaryFile(null!));
    }

    [Fact]
    public void IsBinaryFile_WithEmptyContent_ReturnsFalse()
    {
        var guard = new FileSystemGuard();

        Assert.False(guard.IsBinaryFile(Array.Empty<byte>()));
    }

    [Fact]
    public void IsBinaryFile_WithTextContent_ReturnsFalse()
    {
        var guard = new FileSystemGuard();
        var textContent = System.Text.Encoding.UTF8.GetBytes("Hello, World!\nThis is text.");

        Assert.False(guard.IsBinaryFile(textContent));
    }

    [Fact]
    public void IsBinaryFile_WithBinaryContent_ReturnsTrue()
    {
        var guard = new FileSystemGuard();
        var binaryContent = new byte[] { 0x00, 0x01, 0x02, 0x03, 0xFF, 0xFE };

        Assert.True(guard.IsBinaryFile(binaryContent));
    }

    [Fact]
    public void IsBinaryExtension_WithBinaryExtension_ReturnsTrue()
    {
        Assert.True(FileSystemGuard.IsBinaryExtension("app.exe"));
        Assert.True(FileSystemGuard.IsBinaryExtension("image.png"));
        Assert.True(FileSystemGuard.IsBinaryExtension("archive.zip"));
        Assert.True(FileSystemGuard.IsBinaryExtension("video.mp4"));
    }

    [Fact]
    public void IsBinaryExtension_WithTextExtension_ReturnsFalse()
    {
        Assert.False(FileSystemGuard.IsBinaryExtension("code.cs"));
        Assert.False(FileSystemGuard.IsBinaryExtension("config.json"));
        Assert.False(FileSystemGuard.IsBinaryExtension("README.md"));
    }

    // Live run: reading an empty requirements.txt reported "0 > 10485760" under FILE_TOO_LARGE.
    [Fact]
    public void ValidateFileForRead_WithAnEmptyFile_SaysItIsEmpty_NotTooLarge()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cf-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "requirements.txt");
            File.WriteAllText(path, string.Empty);

            var result = new FileSystemGuard().ValidateFileForRead(path, [directory], new CodexFlow.Core.Hashline.Models.HashlineOptions());

            var error = Assert.Single(result.Errors);
            Assert.Equal("FILE_EMPTY", error.Code);
            Assert.DoesNotContain("FILE_TOO_LARGE", result.Errors.Select(e => e.Code));
            Assert.DoesNotContain("超过限制", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void IsFileSizeAllowed_WithinLimit_ReturnsTrue()
    {
        var guard = new FileSystemGuard();

        Assert.True(guard.IsFileSizeAllowed(1024, 2048));
        Assert.True(guard.IsFileSizeAllowed(2048, 2048));
    }

    [Fact]
    public void IsFileSizeAllowed_ExceedsLimit_ReturnsFalse()
    {
        var guard = new FileSystemGuard();

        Assert.False(guard.IsFileSizeAllowed(2049, 2048));
        Assert.False(guard.IsFileSizeAllowed(0, 2048));
        Assert.False(guard.IsFileSizeAllowed(-1, 2048));
    }

    [Fact]
    public void IsLineCountAllowed_WithinLimit_ReturnsTrue()
    {
        var guard = new FileSystemGuard();

        Assert.True(guard.IsLineCountAllowed(100, 200));
        Assert.True(guard.IsLineCountAllowed(200, 200));
    }

    [Fact]
    public void IsLineCountAllowed_ExceedsLimit_ReturnsFalse()
    {
        var guard = new FileSystemGuard();

        Assert.False(guard.IsLineCountAllowed(201, 200));
        Assert.False(guard.IsLineCountAllowed(0, 200));
        Assert.False(guard.IsLineCountAllowed(-1, 200));
    }
}