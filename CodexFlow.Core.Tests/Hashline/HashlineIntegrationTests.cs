// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodexFlow.Core.Hashline.Constants;
using CodexFlow.Core.Hashline.Infrastructure;
using CodexFlow.Core.Hashline.Models;
using CodexFlow.Core.Hashline.Services;
using Xunit;

namespace CodexFlow.Core.Tests.Hashline;

/// <summary>
/// Hashline 集成测试 - 验证核心工作流和错误处理。
/// </summary>
public class HashlineIntegrationTests : IDisposable
{
    private readonly string _testRoot;
    private readonly EditApplier _editApplier;
    private readonly FileSystemGuard _fileSystemGuard;

    public HashlineIntegrationTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"hashline_integration_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
        _editApplier = new EditApplier();
        _fileSystemGuard = new FileSystemGuard();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    /// <summary>
    /// 测试 1: Hashline 工作流 - 创建快照并应用编辑。
    /// </summary>
    [Fact]
    public void HashlineWorkflow_CreateSnapshotAndApplyEdit_ShouldSucceed()
    {
        // Setup: 创建模拟快照
        var snapshot = CreateSnapshot("var builder = WebApplication.CreateBuilder(args);\nvar app = builder.Build();\napp.Run();\n");

        // Step 1: 验证快照结构
        Assert.Equal(3, snapshot.Lines.Count);
        Assert.False(string.IsNullOrEmpty(snapshot.SnapshotId));
        Assert.False(string.IsNullOrEmpty(snapshot.FileFingerprint));

        // Step 2: 使用 Hashline 编辑（在第一行后插入）
        var operation = new InsertAfterOperation
        {
            TargetLine = 1,
            TargetAnchorId = snapshot.Lines[0].AnchorId,
            NewLines = new List<string> { "app.MapGet(\"/health\", () => Results.Ok());" }
        };

        var result = _editApplier.Apply(snapshot, new List<EditOperation> { operation });

        Assert.Contains("app.MapGet", result.NewContent);
        Assert.Contains("var builder", result.NewContent); // 原内容保留
    }

    /// <summary>
    /// 测试 2: 路径安全模型在工作区范围内允许访问。
    /// </summary>
    [Fact]
    public void Hashline_PathSecurity_ShouldAllowWorkspacePaths()
    {
        var allowedRoots = new List<string> { _testRoot };

        // 工作区内路径应被允许
        var internalPath = Path.Combine(_testRoot, "subdir", "file.txt");
        Assert.True(_fileSystemGuard.IsPathAllowed(internalPath, allowedRoots));

        // 工作区根目录本身应被允许
        Assert.True(_fileSystemGuard.IsPathAllowed(_testRoot, allowedRoots));
    }

    /// <summary>
    /// 测试 3: 路径安全模型阻止工作区外访问。
    /// </summary>
    [Fact]
    public void Hashline_PathSecurity_ShouldBlockExternalAccess()
    {
        var allowedRoots = new List<string> { _testRoot };

        // 工作区外路径应被阻止
        var externalPath = Path.Combine(Path.GetTempPath(), "external", "file.txt");
        Assert.False(_fileSystemGuard.IsPathAllowed(externalPath, allowedRoots));

        // 路径遍历攻击应被阻止
        var traversalPath = Path.Combine(_testRoot, "..", "external", "file.txt");
        var normalizedTraversal = Path.GetFullPath(traversalPath);
        Assert.False(_fileSystemGuard.IsPathAllowed(normalizedTraversal, allowedRoots));
    }

    /// <summary>
    /// 测试 4: 空白名单应拒绝所有路径（严格模式）。
    /// </summary>
    [Fact]
    public void Hashline_PathSecurity_EmptyAllowedRoots_DeniesAllPaths()
    {
        var allowedRoots = new List<string>();

        // 空白名单应拒绝所有路径
        Assert.False(_fileSystemGuard.IsPathAllowed("/any/path/file.txt", allowedRoots));
        Assert.False(_fileSystemGuard.IsPathAllowed("C:\\Any\\Path\\file.txt", allowedRoots));
    }

    /// <summary>
    /// 测试 5: 多操作 Hashline 编辑。
    /// </summary>
    [Fact]
    public void Hashline_MultiOperationEdit_ShouldSucceed()
    {
        // Setup: 创建较长的快照
        var lines = Enumerable.Range(1, 50).Select(i => $"public void Method{i}() {{ }}").ToList();
        var content = string.Join("\n", lines) + "\n";
        var snapshot = CreateSnapshot(content);

        Assert.Equal(50, snapshot.Lines.Count);

        // 构造多操作编辑请求
        var operations = new List<EditOperation>
        {
            // 在第 5 行后插入
            new InsertAfterOperation
            {
                TargetLine = 5,
                TargetAnchorId = snapshot.Lines[4].AnchorId,
                NewLines = new List<string> { "// Inserted after method 5" }
            },
            // 替换第 10-12 行
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
        };

        var result = _editApplier.Apply(snapshot, operations);

        Assert.Contains("Inserted after method 5", result.NewContent);
        Assert.Contains("Replaced section", result.NewContent);
    }

    /// <summary>
    /// 测试 6: 删除操作。
    /// </summary>
    [Fact]
    public void Hashline_DeleteRange_ShouldSucceed()
    {
        // Setup
        var snapshot = CreateSnapshot("line1\nline2\nline3\nline4\nline5\n");
        Assert.Equal(5, snapshot.Lines.Count);

        // 删除第 2-3 行
        var operation = new DeleteRangeOperation
        {
            StartLine = 2,
            StartAnchorId = snapshot.Lines[1].AnchorId,
            EndLine = 3,
            EndAnchorId = snapshot.Lines[2].AnchorId
        };

        var result = _editApplier.Apply(snapshot, new List<EditOperation> { operation });

        // 验证结果
        var lines = result.NewContent.Split('\n').Where(l => !string.IsNullOrEmpty(l)).ToList();
        Assert.Equal(3, lines.Count); // 5 - 2 = 3
        Assert.Equal("line1", lines[0]);
        Assert.Equal("line4", lines[1]);
        Assert.Equal("line5", lines[2]);
    }

    /// <summary>
    /// 测试 7: RepairGuidance 字典包含所有关键错误码。
    /// </summary>
    [Fact]
    public void HashlineErrorCodes_RepairGuidance_ShouldContainCriticalCodes()
    {
        var criticalCodes = new[]
        {
            HashlineErrorCodes.FileFingerprintMismatch,
            HashlineErrorCodes.AnchorMismatch,
            HashlineErrorCodes.AnchorNotFound,
            HashlineErrorCodes.LineOutOfRange,
            HashlineErrorCodes.WindowOutOfRange,
            HashlineErrorCodes.OverlappingOperations
        };

        foreach (var code in criticalCodes)
        {
            Assert.True(HashlineErrorCodes.RepairGuidance.ContainsKey(code),
                $"RepairGuidance missing entry for: {code}");

            var guidance = HashlineErrorCodes.GetGuidance(code);
            Assert.False(string.IsNullOrEmpty(guidance));
            Assert.DoesNotContain("检查错误详情", guidance); // 不应该是默认值
        }
    }

    /// <summary>
    /// 测试 8: 验证 HasTrailingNewline 被正确保留。
    /// </summary>
    [Fact]
    public void Hashline_TrailingNewline_ShouldBePreserved()
    {
        // Setup: 文件有尾部换行
        var snapshot = CreateSnapshot("line1\nline2\n"); // 有尾部换行
        Assert.True(snapshot.HasTrailingNewline);

        // 编辑
        var operation = new InsertAfterOperation
        {
            TargetLine = 1,
            TargetAnchorId = snapshot.Lines[0].AnchorId,
            NewLines = new List<string> { "inserted" }
        };

        var result = _editApplier.Apply(snapshot, new List<EditOperation> { operation });

        // 验证尾部换行仍然存在
        Assert.EndsWith("\n", result.NewContent);
    }

    /// <summary>
    /// 测试 9: 无效操作类型错误码存在。
    /// </summary>
    [Fact]
    public void HashlineErrorCodes_InvalidOperationType_ShouldHaveGuidance()
    {
        Assert.True(HashlineErrorCodes.RepairGuidance.ContainsKey(HashlineErrorCodes.InvalidOperationType));
        var guidance = HashlineErrorCodes.GetGuidance(HashlineErrorCodes.InvalidOperationType);
        Assert.Contains("replace_range", guidance);
    }

    /// <summary>
    /// 测试 10: 高风险文件错误码存在。
    /// </summary>
    [Fact]
    public void HashlineErrorCodes_HighRiskFileCode_ShouldExist()
    {
        Assert.Equal("HIGH_RISK_FILE_REQUIRES_GUARDED_PATH", HashlineErrorCodes.HighRiskFileRequiresGuardedPath);
        Assert.True(HashlineErrorCodes.RepairGuidance.ContainsKey(HashlineErrorCodes.HighRiskFileRequiresGuardedPath));
    }

    private static FileSnapshot CreateSnapshot(string content)
    {
        // 检测是否有尾部换行
        var hasTrailingNewline = content.Length > 0 && content.EndsWith("\n", StringComparison.Ordinal);

        var rawLines = content.Split('\n');
        var lines = new List<HashlineRecord>();

        // 处理最后一行（如果内容以换行结尾，最后一行是空的，不需要处理）
        var lineCount = hasTrailingNewline ? rawLines.Length - 1 : rawLines.Length;

        for (var i = 0; i < lineCount; i++)
        {
            lines.Add(new HashlineRecord
            {
                LineNumber = i + 1,
                AnchorId = $"ANCHOR{i + 1}",
                RawText = rawLines[i],
                NormalizedText = rawLines[i]
            });
        }

        return new FileSnapshot
        {
            SnapshotId = $"snap_{Guid.NewGuid():N}",
            FilePath = "/test/file.txt",
            FileFingerprint = $"fp_{Guid.NewGuid():N}",
            EncodingName = "utf-8",
            HasBom = false,
            NewLineStyle = "\n",
            HasTrailingNewline = hasTrailingNewline,
            ReadAtUtc = DateTimeOffset.UtcNow,
            Lines = lines,
            RenderedText = content
        };
    }
}
