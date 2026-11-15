// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Hashline.Abstractions;
using CodexFlow.Core.Hashline.Constants;
using CodexFlow.Core.Hashline.Infrastructure;
using CodexFlow.Core.Hashline.Models;
using CodexFlow.Core.Hashline.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodexFlow.Core.Tests.Hashline;

/// <summary>
/// Hashline 工具链集成测试 - 验证真实服务调用路径。
/// 测试 read_file_content -> IHashlineFileService -> apply_patch 完整链路。
/// </summary>
public class HashlineToolChainTests : IDisposable
{
    private readonly string _testRoot;
    private readonly IHashlineFileService _hashlineService;
    private readonly HashlineOptions _options;

    public HashlineToolChainTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"hashline_toolchain_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);

        _options = new HashlineOptions
        {
            MaxFileSizeBytes = 1024 * 1024,
            MaxLineCount = 10000,
            EnableAuditLogging = false
        };

        // 构造真实的 Hashline 服务链
        var normalizer = new TextNormalizer();
        var lineHasher = new Sha256LineHasher();
        var fingerprintProvider = new Sha256FingerprintProvider();
        var fileSystemGuard = new FileSystemGuard();
        var encodingDetector = new EncodingDetector();
        var auditLogger = new NullAuditLogger();
        var atomicWriter = new AtomicFileWriter(NullLogger<AtomicFileWriter>.Instance);

        var snapshotReader = new SnapshotReader(
            normalizer,
            lineHasher,
            fingerprintProvider,
            fileSystemGuard,
            encodingDetector,
            auditLogger,
            _options,
            NullLogger<SnapshotReader>.Instance);

        var editRequestValidator = new EditRequestValidator(_options);
        var editApplier = new EditApplier();
        var diffRenderer = new DiffRenderer();

        _hashlineService = new HashlineFileService(
            snapshotReader,
            editRequestValidator,
            editApplier,
            diffRenderer,
            atomicWriter,
            fingerprintProvider,
            auditLogger,
            normalizer,
            _options,
            NullLogger<HashlineFileService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    /// <summary>
    /// 测试 1: 完整工具链 - read -> edit (insert_after)
    /// </summary>
    [Fact]
    public async Task ToolChain_ReadThenInsertAfter_ShouldSucceed()
    {
        // Setup: 创建测试文件
        var filePath = Path.Combine(_testRoot, "Program.cs");
        var originalContent = "var builder = WebApplication.CreateBuilder(args);\nvar app = builder.Build();\napp.Run();\n";
        await File.WriteAllTextAsync(filePath, originalContent);

        // Step 1: 通过服务读取快照 (模拟 read_file_content mode="hashline")
        var snapshot = await _hashlineService.ReadAsync(filePath, _testRoot, ct: CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal(3, snapshot.Lines.Count);
        Assert.False(string.IsNullOrEmpty(snapshot.SnapshotId));
        Assert.False(string.IsNullOrEmpty(snapshot.FileFingerprint));

        // Step 2: 构造编辑请求 (模拟 apply_patch edit_mode="hashline")
        var editRequest = new HashlineEditRequest
        {
            FilePath = filePath,
            SnapshotId = snapshot.SnapshotId,
            FileFingerprint = snapshot.FileFingerprint,
            DryRun = false,
            Operations = new List<EditOperation>
            {
                new InsertAfterOperation
                {
                    TargetLine = 1,
                    TargetAnchorId = snapshot.Lines[0].AnchorId,
                    NewLines = new List<string> { "app.MapGet(\"/health\", () => Results.Ok());" }
                }
            }
        };

        // Step 3: 执行编辑
        var result = await _hashlineService.EditAsync(editRequest, _testRoot, CancellationToken.None);

        Assert.True(result.Success, $"Edit failed: {result.ErrorCode} - {result.ErrorMessage}");

        // Step 4: 验证文件内容
        var newContent = await File.ReadAllTextAsync(filePath);
        Assert.Contains("app.MapGet", newContent);
        Assert.Contains("var builder", newContent);
    }

    /// <summary>
    /// 测试 2: fingerprint mismatch 检测
    /// </summary>
    [Fact]
    public async Task ToolChain_FingerprintMismatch_ShouldFail()
    {
        // Setup
        var filePath = Path.Combine(_testRoot, "test.txt");
        await File.WriteAllTextAsync(filePath, "line1\nline2\nline3\n");

        // Step 1: 读取快照
        var snapshot = await _hashlineService.ReadAsync(filePath, _testRoot, ct: CancellationToken.None);

        // Step 2: 模拟并发修改
        await File.WriteAllTextAsync(filePath, "line1-modified\nline2\nline3\n");

        // Step 3: 使用旧 fingerprint 尝试编辑
        var editRequest = new HashlineEditRequest
        {
            FilePath = filePath,
            SnapshotId = snapshot.SnapshotId,
            FileFingerprint = snapshot.FileFingerprint, // 旧指纹
            DryRun = false,
            Operations = new List<EditOperation>
            {
                new InsertAfterOperation
                {
                    TargetLine = 1,
                    TargetAnchorId = snapshot.Lines[0].AnchorId,
                    NewLines = new List<string> { "new line" }
                }
            }
        };

        var result = await _hashlineService.EditAsync(editRequest, _testRoot, CancellationToken.None);

        // 验证失败
        Assert.False(result.Success);
        Assert.Equal(HashlineErrorCodes.FileFingerprintMismatch, result.ErrorCode);
    }

    /// <summary>
    /// 测试 3: anchor mismatch 检测
    /// </summary>
    [Fact]
    public async Task ToolChain_AnchorMismatch_ShouldFail()
    {
        // Setup
        var filePath = Path.Combine(_testRoot, "test.cs");
        await File.WriteAllTextAsync(filePath, "public void Method1() { }\npublic void Method2() { }\n");

        // Step 1: 读取快照
        var snapshot = await _hashlineService.ReadAsync(filePath, _testRoot, ct: CancellationToken.None);

        // Step 2: 修改目标行（但保持整体 fingerprint 可能不变）
        var modifiedLines = snapshot.Lines.Select((l, i) =>
            i == 0 ? "public void Method1Modified() { }" : l.RawText).ToList();
        await File.WriteAllTextAsync(filePath, string.Join("\n", modifiedLines) + "\n");

        // Step 3: 使用旧 anchor 尝试编辑
        var editRequest = new HashlineEditRequest
        {
            FilePath = filePath,
            SnapshotId = snapshot.SnapshotId,
            FileFingerprint = snapshot.FileFingerprint,
            DryRun = false,
            Operations = new List<EditOperation>
            {
                new InsertAfterOperation
                {
                    TargetLine = 1,
                    TargetAnchorId = snapshot.Lines[0].AnchorId, // 旧 anchor
                    NewLines = new List<string> { "// comment" }
                }
            }
        };

        var result = await _hashlineService.EditAsync(editRequest, _testRoot, CancellationToken.None);

        // 验证失败（可能是 anchor mismatch 或 fingerprint mismatch）
        Assert.False(result.Success);
        Assert.True(
            result.ErrorCode == HashlineErrorCodes.AnchorMismatch ||
            result.ErrorCode == HashlineErrorCodes.FileFingerprintMismatch,
            $"Expected anchor or fingerprint mismatch, got: {result.ErrorCode}");
    }

    /// <summary>
    /// 测试 4: 路径安全 - 阻止工作区外访问
    /// </summary>
    [Fact]
    public async Task ToolChain_PathSecurity_ShouldBlockExternalAccess()
    {
        // 创建工作区外的文件
        var externalPath = Path.Combine(Path.GetTempPath(), $"external_{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(externalPath, "external content");

        try
        {
            // 尝试读取工作区外的文件
            var ex = await Assert.ThrowsAsync<HashlineException>(() =>
                _hashlineService.ReadAsync(externalPath, _testRoot, ct: CancellationToken.None));

            Assert.Equal(HashlineErrorCodes.FilePathNotAllowed, ex.ErrorCode);
        }
        finally
        {
            File.Delete(externalPath);
        }
    }

    /// <summary>
    /// 测试 5: 分段读取应返回局部窗口，但保持全文件 fingerprint
    /// </summary>
    [Fact]
    public async Task ToolChain_WindowedRead_ShouldReturnPartialWindowWithStableFingerprint()
    {
        var filePath = Path.Combine(_testRoot, "window.txt");
        await File.WriteAllTextAsync(filePath, "line1\nline2\nline3\nline4\nline5\n");

        var fullSnapshot = await _hashlineService.ReadAsync(filePath, _testRoot, ct: CancellationToken.None);
        var windowSnapshot = await _hashlineService.ReadAsync(
            filePath,
            _testRoot,
            windowStartLine: 2,
            windowLineCount: 2,
            ct: CancellationToken.None);

        Assert.Equal(fullSnapshot.FileFingerprint, windowSnapshot.FileFingerprint);
        Assert.Equal(5, windowSnapshot.TotalLineCount);
        Assert.Equal(2, windowSnapshot.WindowStartLine);
        Assert.Equal(3, windowSnapshot.WindowEndLine);
        Assert.True(windowSnapshot.IsPartialWindow);
        Assert.Equal(2, windowSnapshot.Lines.Count);
        Assert.Equal(2, windowSnapshot.Lines[0].LineNumber);
        Assert.Equal(3, windowSnapshot.Lines[1].LineNumber);
    }

    /// <summary>
    /// 测试 6: 超出范围的分段读取应失败
    /// </summary>
    [Fact]
    public async Task ToolChain_WindowedRead_OutOfRange_ShouldFail()
    {
        var filePath = Path.Combine(_testRoot, "windowfail.txt");
        await File.WriteAllTextAsync(filePath, "line1\nline2\n");

        var ex = await Assert.ThrowsAsync<HashlineException>(() =>
            _hashlineService.ReadAsync(
                filePath,
                _testRoot,
                windowStartLine: 10,
                windowLineCount: 3,
                ct: CancellationToken.None));

        Assert.Equal(HashlineErrorCodes.WindowOutOfRange, ex.ErrorCode);
    }

    /// <summary>
    /// 测试 7: 多操作编辑
    /// </summary>
    [Fact]
    public async Task ToolChain_MultiOperationEdit_ShouldSucceed()
    {
        // Setup: 创建较长的测试文件
        var filePath = Path.Combine(_testRoot, "Service.cs");
        var lines = Enumerable.Range(1, 50).Select(i => $"public void Method{i}() {{ }}").ToList();
        await File.WriteAllTextAsync(filePath, string.Join("\n", lines) + "\n");

        // Step 1: 读取快照
        var snapshot = await _hashlineService.ReadAsync(filePath, _testRoot, ct: CancellationToken.None);
        Assert.Equal(50, snapshot.Lines.Count);

        // Step 2: 构造多操作编辑请求
        var editRequest = new HashlineEditRequest
        {
            FilePath = filePath,
            SnapshotId = snapshot.SnapshotId,
            FileFingerprint = snapshot.FileFingerprint,
            DryRun = false,
            Operations = new List<EditOperation>
            {
                new InsertAfterOperation
                {
                    TargetLine = 5,
                    TargetAnchorId = snapshot.Lines[4].AnchorId,
                    NewLines = new List<string> { "// Inserted after method 5" }
                },
                new ReplaceRangeOperation
                {
                    StartLine = 10,
                    StartAnchorId = snapshot.Lines[9].AnchorId,
                    EndLine = 12,
                    EndAnchorId = snapshot.Lines[11].AnchorId,
                    NewLines = new List<string>
                    {
                        "// Replaced section",
                        "public void ReplacedMethod() { }"
                    }
                }
            }
        };

        var result = await _hashlineService.EditAsync(editRequest, _testRoot, CancellationToken.None);

        Assert.True(result.Success, $"Multi-operation edit failed: {result.ErrorCode} - {result.ErrorMessage}");

        // 验证文件内容
        var newContent = await File.ReadAllTextAsync(filePath);
        Assert.Contains("Inserted after method 5", newContent);
        Assert.Contains("Replaced section", newContent);
    }

    /// <summary>
    /// 测试 8: DryRun 模式不修改文件
    /// </summary>
    [Fact]
    public async Task ToolChain_DryRunMode_ShouldNotModifyFile()
    {
        // Setup
        var filePath = Path.Combine(_testRoot, "dryrun.txt");
        var originalContent = "original content\n";
        await File.WriteAllTextAsync(filePath, originalContent);

        // 读取快照
        var snapshot = await _hashlineService.ReadAsync(filePath, _testRoot, ct: CancellationToken.None);

        // DryRun 编辑
        var editRequest = new HashlineEditRequest
        {
            FilePath = filePath,
            SnapshotId = snapshot.SnapshotId,
            FileFingerprint = snapshot.FileFingerprint,
            DryRun = true,
            Operations = new List<EditOperation>
            {
                new InsertAfterOperation
                {
                    TargetLine = 1,
                    TargetAnchorId = snapshot.Lines[0].AnchorId,
                    NewLines = new List<string> { "new line" }
                }
            }
        };

        var result = await _hashlineService.EditAsync(editRequest, _testRoot, CancellationToken.None);

        Assert.True(result.Success);

        // 验证文件未被修改
        var currentContent = await File.ReadAllTextAsync(filePath);
        Assert.Equal(originalContent, currentContent);
    }

    /// <summary>
    /// 测试 9: 尾部换行保留
    /// </summary>
    [Fact]
    public async Task ToolChain_TrailingNewline_ShouldBePreserved()
    {
        // Setup: 文件有尾部换行
        var filePath = Path.Combine(_testRoot, "newline.txt");
        await File.WriteAllTextAsync(filePath, "line1\nline2\n");

        // 读取快照
        var snapshot = await _hashlineService.ReadAsync(filePath, _testRoot, ct: CancellationToken.None);
        Assert.True(snapshot.HasTrailingNewline);

        // 编辑
        var editRequest = new HashlineEditRequest
        {
            FilePath = filePath,
            SnapshotId = snapshot.SnapshotId,
            FileFingerprint = snapshot.FileFingerprint,
            DryRun = false,
            Operations = new List<EditOperation>
            {
                new InsertAfterOperation
                {
                    TargetLine = 1,
                    TargetAnchorId = snapshot.Lines[0].AnchorId,
                    NewLines = new List<string> { "inserted" }
                }
            }
        };

        var result = await _hashlineService.EditAsync(editRequest, _testRoot, CancellationToken.None);
        Assert.True(result.Success);

        // 验证尾部换行仍然存在
        var bytes = await File.ReadAllBytesAsync(filePath);
        Assert.Equal((byte)'\n', bytes[^1]);
    }

    /// <summary>
    /// 测试 10: DeleteRange 操作
    /// </summary>
    [Fact]
    public async Task ToolChain_DeleteRange_ShouldSucceed()
    {
        // Setup
        var filePath = Path.Combine(_testRoot, "delete.txt");
        await File.WriteAllTextAsync(filePath, "line1\nline2\nline3\nline4\nline5\n");

        // 读取快照
        var snapshot = await _hashlineService.ReadAsync(filePath, _testRoot, ct: CancellationToken.None);
        Assert.Equal(5, snapshot.Lines.Count);

        // 删除第 2-3 行
        var editRequest = new HashlineEditRequest
        {
            FilePath = filePath,
            SnapshotId = snapshot.SnapshotId,
            FileFingerprint = snapshot.FileFingerprint,
            DryRun = false,
            Operations = new List<EditOperation>
            {
                new DeleteRangeOperation
                {
                    StartLine = 2,
                    StartAnchorId = snapshot.Lines[1].AnchorId,
                    EndLine = 3,
                    EndAnchorId = snapshot.Lines[2].AnchorId
                }
            }
        };

        var result = await _hashlineService.EditAsync(editRequest, _testRoot, CancellationToken.None);

        Assert.True(result.Success, $"Delete failed: {result.ErrorCode}");

        // 验证结果
        var newContent = await File.ReadAllTextAsync(filePath);
        var lines = newContent.Split('\n').Where(l => !string.IsNullOrEmpty(l)).ToList();
        Assert.Equal(3, lines.Count);
        Assert.Equal("line1", lines[0]);
        Assert.Equal("line4", lines[1]);
        Assert.Equal("line5", lines[2]);
    }

    /// <summary>
    /// 测试 11: 完整 read -> edit -> re-read 循环
    /// </summary>
    [Fact]
    public async Task ToolChain_ReadEditReread_ShouldUpdateFingerprint()
    {
        // Setup
        var filePath = Path.Combine(_testRoot, "cycle.txt");
        await File.WriteAllTextAsync(filePath, "initial content\n");

        // First read
        var snapshot1 = await _hashlineService.ReadAsync(filePath, _testRoot, ct: CancellationToken.None);
        var fingerprint1 = snapshot1.FileFingerprint;

        // Edit
        var editRequest = new HashlineEditRequest
        {
            FilePath = filePath,
            SnapshotId = snapshot1.SnapshotId,
            FileFingerprint = snapshot1.FileFingerprint,
            DryRun = false,
            Operations = new List<EditOperation>
            {
                new InsertAfterOperation
                {
                    TargetLine = 1,
                    TargetAnchorId = snapshot1.Lines[0].AnchorId,
                    NewLines = new List<string> { "added line" }
                }
            }
        };

        await _hashlineService.EditAsync(editRequest, _testRoot, CancellationToken.None);

        // Re-read
        var snapshot2 = await _hashlineService.ReadAsync(filePath, _testRoot, ct: CancellationToken.None);
        var fingerprint2 = snapshot2.FileFingerprint;

        // Fingerprint 应该改变
        Assert.NotEqual(fingerprint1, fingerprint2);
        Assert.Equal(2, snapshot2.Lines.Count);
    }
}

/// <summary>
/// 空审计日志实现。
/// </summary>
public sealed class NullAuditLogger : IAuditLogger
{
    public Task LogReadAsync(FileSnapshot snapshot, CancellationToken ct = default) => Task.CompletedTask;
    public Task LogEditRequestAsync(HashlineEditRequest request, CancellationToken ct = default) => Task.CompletedTask;
    public Task LogEditResultAsync(HashlineEditResult result, CancellationToken ct = default) => Task.CompletedTask;
}
