// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;  // For SmartPatchTool
using CodexFlow.Core.Agents.Tools;  // For ReadFileTool, ApplyPatchTool
using CodexFlow.Core.Hashline.Abstractions;
using CodexFlow.Core.Hashline.Constants;
using CodexFlow.Core.Hashline.Infrastructure;
using CodexFlow.Core.Hashline.Models;
using CodexFlow.Core.Hashline.Services;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CodexFlow.Core.Tests.Hashline;

/// <summary>
/// 真正的工具链集成测试 - 测试 ReadFileTool、ApplyPatchTool、SmartPatchTool 的 ExecuteAsync 入口。
/// 验证：参数解析、模式选择、高风险文件阻断、错误前缀。
/// </summary>
public class HashlineToolIntegrationTests : IDisposable
{
    private readonly string _testRoot;
    private readonly IHashlineFileService _hashlineService;
    private readonly Mock<IGitService> _gitServiceMock;

    public HashlineToolIntegrationTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"hashline_tool_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);

        // Setup real Hashline service chain
        var options = new HashlineOptions
        {
            MaxFileSizeBytes = 1024 * 1024,
            MaxLineCount = 10000,
            EnableAuditLogging = false
        };

        var normalizer = new TextNormalizer();
        var lineHasher = new Sha256LineHasher();
        var fingerprintProvider = new Sha256FingerprintProvider();
        var fileSystemGuard = new FileSystemGuard();
        var encodingDetector = new EncodingDetector();
        var auditLogger = new NullAuditLogger();
        var atomicWriter = new AtomicFileWriter(NullLogger<AtomicFileWriter>.Instance);

        var snapshotReader = new SnapshotReader(
            normalizer, lineHasher, fingerprintProvider, fileSystemGuard,
            encodingDetector, auditLogger, options, NullLogger<SnapshotReader>.Instance);

        var editRequestValidator = new EditRequestValidator(options);
        var editApplier = new EditApplier();
        var diffRenderer = new DiffRenderer();

        _hashlineService = new HashlineFileService(
            snapshotReader, editRequestValidator, editApplier, diffRenderer,
            atomicWriter, fingerprintProvider, auditLogger, normalizer, options,
            NullLogger<HashlineFileService>.Instance);

        _gitServiceMock = new Mock<IGitService>();
        _gitServiceMock.Setup(g => g.ApplyPatchAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    #region ReadFileTool Tests

    /// <summary>
    /// ReadFileTool: 显式 mode="hashline" 应返回带锚点的快照。
    /// </summary>
    [Fact]
    public async Task ReadFileTool_ExplicitHashlineMode_ShouldReturnSnapshot()
    {
        // Setup
        var filePath = Path.Combine(_testRoot, "test.cs");
        await File.WriteAllTextAsync(filePath, "line1\nline2\nline3\n");

        var tool = new ReadFileTool(
            NullLogger<ReadFileTool>.Instance,
            _hashlineService,
            new HashlineOptions());

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testRoot,
            ["path"] = "test.cs",
            ["mode"] = "hashline"
        };

        // Execute
        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        // Assert
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("snapshotId", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Fingerprint:", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("#", result.Output); // Anchor format: 1#ANCHORID|
    }

    /// <summary>
    /// ReadFileTool: 配置启用默认 Hashline。
    /// </summary>
    [Fact]
    public async Task ReadFileTool_ConfigEnabled_ShouldDefaultToHashline()
    {
        // Setup
        var filePath = Path.Combine(_testRoot, "config.cs");
        await File.WriteAllTextAsync(filePath, "var x = 1;\n");

        var options = new HashlineOptions { Enabled = true };
        var tool = new ReadFileTool(
            NullLogger<ReadFileTool>.Instance,
            _hashlineService,
            options);

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testRoot,
            ["path"] = "config.cs"
            // 没有 mode 参数
        };

        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("snapshotId", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HashlineReadTool_ShouldForceHashlineMode()
    {
        var filePath = Path.Combine(_testRoot, "forced.cs");
        await File.WriteAllTextAsync(filePath, "line1\nline2\n");

        var innerTool = new ReadFileTool(
            NullLogger<ReadFileTool>.Instance,
            _hashlineService,
            new HashlineOptions { Enabled = false });
        var tool = new HashlineReadTool(innerTool);

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testRoot,
            ["path"] = "forced.cs"
        };

        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("SnapshotId:", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1#", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ReadFileTool: Hashline 分段读取应返回窗口元数据和局部 renderedText。
    /// </summary>
    [Fact]
    public async Task ReadFileTool_HashlineWindow_ShouldReturnWindowMetadata()
    {
        var filePath = Path.Combine(_testRoot, "windowed.cs");
        await File.WriteAllTextAsync(filePath, "line1\nline2\nline3\nline4\nline5\n");

        var tool = new ReadFileTool(
            NullLogger<ReadFileTool>.Instance,
            _hashlineService,
            new HashlineOptions());

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testRoot,
            ["path"] = "windowed.cs",
            ["mode"] = "hashline",
            ["window_start_line"] = 2,
            ["window_line_count"] = 2
        };

        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("Window: 2-3", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TotalLines: 5", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2#", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1#", result.Output, StringComparison.OrdinalIgnoreCase);

        var (snapshotId, fileFingerprint, firstAnchorId, totalLineCount, windowStartLine, windowEndLine, isPartialWindow) =
            ParseHashlineReadMetadata(result);
        Assert.False(string.IsNullOrEmpty(snapshotId));
        Assert.False(string.IsNullOrEmpty(fileFingerprint));
        Assert.False(string.IsNullOrEmpty(firstAnchorId));
        Assert.Equal(5, totalLineCount);
        Assert.Equal(2, windowStartLine);
        Assert.Equal(3, windowEndLine);
        Assert.True(isPartialWindow);
    }

    /// <summary>
    /// ReadFileTool: 超出范围的分段读取应失败。
    /// </summary>
    [Fact]
    public async Task ReadFileTool_HashlineWindowOutOfRange_ShouldFail()
    {
        var filePath = Path.Combine(_testRoot, "windowfail.cs");
        await File.WriteAllTextAsync(filePath, "line1\nline2\n");

        var tool = new ReadFileTool(
            NullLogger<ReadFileTool>.Instance,
            _hashlineService,
            new HashlineOptions());

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testRoot,
            ["path"] = "windowfail.cs",
            ["mode"] = "hashline",
            ["window_start_line"] = 10,
            ["window_line_count"] = 5
        };

        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains(HashlineErrorCodes.WindowOutOfRange, result.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ReadFileTool: mode="plain" 应返回普通内容。
    /// </summary>
    [Fact]
    public async Task ReadFileTool_ExplicitPlainMode_ShouldReturnPlainContent()
    {
        var filePath = Path.Combine(_testRoot, "plain.txt");
        await File.WriteAllTextAsync(filePath, "plain content\n");

        var tool = new ReadFileTool(
            NullLogger<ReadFileTool>.Instance,
            _hashlineService,
            new HashlineOptions { Enabled = true });

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testRoot,
            ["path"] = "plain.txt",
            ["mode"] = "plain"
        };

        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.DoesNotContain("snapshotId", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("plain content", result.Output);
    }

    [Fact]
    public async Task HashlineWriteTool_ShouldApplyHashlineEditFromFlatArguments()
    {
        var filePath = Path.Combine(_testRoot, "writeflat.cs");
        await File.WriteAllTextAsync(filePath, "line1\nline2\nline3\n");

        var snapshot = await _hashlineService.ReadAsync(filePath, _testRoot, ct: CancellationToken.None);

        var applyPatchTool = new ApplyPatchTool(
            _gitServiceMock.Object,
            _hashlineService,
            new HashlineOptions(),
            NullLogger<ApplyPatchTool>.Instance);
        var tool = new HashlineWriteTool(applyPatchTool);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _testRoot,
            ["filePath"] = "writeflat.cs",
            ["snapshotId"] = snapshot.SnapshotId,
            ["fileFingerprint"] = snapshot.FileFingerprint,
            ["operations"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "replace_range",
                    ["startLine"] = 2,
                    ["startAnchorId"] = snapshot.Lines[1].AnchorId,
                    ["endLine"] = 2,
                    ["endAnchorId"] = snapshot.Lines[1].AnchorId,
                    ["newLines"] = new[] { "updated_line2" }
                }
            }
        }, CancellationToken.None);

        Assert.Equal(ToolResultStatus.Success, result.Status);
        var content = await File.ReadAllTextAsync(filePath);
        Assert.Contains("updated_line2", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HashlineWriteTool_ShouldApplySimpleOldNewStringReplacement()
    {
        var filePath = Path.Combine(_testRoot, "writesimple.cs");
        await File.WriteAllTextAsync(filePath, "public string Status => \"Pending\";\n");

        var applyPatchTool = new ApplyPatchTool(
            _gitServiceMock.Object,
            _hashlineService,
            new HashlineOptions(),
            NullLogger<ApplyPatchTool>.Instance);
        var tool = new HashlineWriteTool(applyPatchTool, _hashlineService);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _testRoot,
            ["filePath"] = "writesimple.cs",
            ["oldString"] = "\"Pending\"",
            ["newString"] = "\"Ready\""
        }, CancellationToken.None);

        Assert.Equal(ToolResultStatus.Success, result.Status);
        var content = await File.ReadAllTextAsync(filePath);
        Assert.Contains("\"Ready\"", content, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Pending\"", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HashlineWriteTool_ShouldNormalizeDuplicateRepoPrefixForSimpleReplacement()
    {
        var filePath = Path.Combine(_testRoot, "writesimple_prefixed.cs");
        await File.WriteAllTextAsync(filePath, "public string Status => \"Pending\";\n");

        var applyPatchTool = new ApplyPatchTool(
            _gitServiceMock.Object,
            _hashlineService,
            new HashlineOptions(),
            NullLogger<ApplyPatchTool>.Instance);
        var tool = new HashlineWriteTool(applyPatchTool, _hashlineService);
        var duplicatedPath = $"{Path.GetFileName(_testRoot)}/writesimple_prefixed.cs";

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _testRoot,
            ["filePath"] = duplicatedPath,
            ["oldString"] = "\"Pending\"",
            ["newString"] = "\"Ready\""
        }, CancellationToken.None);

        Assert.Equal(ToolResultStatus.Success, result.Status);
        var content = await File.ReadAllTextAsync(filePath);
        Assert.Contains("\"Ready\"", content, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Pending\"", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HashlineWriteTool_ShouldApplyHashlineEditFromNewtonsoftOperations()
    {
        var filePath = Path.Combine(_testRoot, "write_newtonsoft_ops.cs");
        await File.WriteAllTextAsync(filePath, "line1\nline2\nline3\n");

        var snapshot = await _hashlineService.ReadAsync(filePath, _testRoot, ct: CancellationToken.None);

        var applyPatchTool = new ApplyPatchTool(
            _gitServiceMock.Object,
            _hashlineService,
            new HashlineOptions(),
            NullLogger<ApplyPatchTool>.Instance);
        var tool = new HashlineWriteTool(applyPatchTool, _hashlineService);

        var operations = JArray.FromObject(new[]
        {
            new
            {
                type = "replace_range",
                startLine = 2,
                startAnchorId = snapshot.Lines[1].AnchorId,
                endLine = 2,
                endAnchorId = snapshot.Lines[1].AnchorId,
                newLines = new[] { "updated_line2" }
            }
        });

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _testRoot,
            ["filePath"] = "write_newtonsoft_ops.cs",
            ["snapshotId"] = snapshot.SnapshotId,
            ["fileFingerprint"] = snapshot.FileFingerprint,
            ["operations"] = operations
        }, CancellationToken.None);

        Assert.True(result.Status == ToolResultStatus.Success, result.Output);
        var content = await File.ReadAllTextAsync(filePath);
        Assert.Contains("updated_line2", content, StringComparison.Ordinal);
    }

    #endregion

    #region ApplyPatchTool Tests

    /// <summary>
    /// ApplyPatchTool: edit_mode="hashline" 应进入 Hashline 分支。
    /// </summary>
    [Fact]
    public async Task ApplyPatchTool_ExplicitHashlineMode_ShouldUseHashline()
    {
        var filePath = Path.Combine(_testRoot, "edit.cs");
        await File.WriteAllTextAsync(filePath, "original line\n");

        // 先读取快照
        var snapshot = await _hashlineService.ReadAsync(filePath, _testRoot, ct: CancellationToken.None);

        var tool = new ApplyPatchTool(
            _gitServiceMock.Object,
            _hashlineService,
            new HashlineOptions(),
            NullLogger<ApplyPatchTool>.Instance);

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testRoot,
            ["edit_mode"] = "hashline",
            ["request"] = new Dictionary<string, object?>
            {
                ["filePath"] = filePath,
                ["snapshotId"] = snapshot.SnapshotId,
                ["fileFingerprint"] = snapshot.FileFingerprint,
                ["dryRun"] = false,
                ["operations"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "insert_after",
                        ["targetLine"] = 1,
                        ["targetAnchorId"] = snapshot.Lines[0].AnchorId,
                        ["newLines"] = new List<object?> { "inserted line" }
                    }
                }
            }
        };

        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("Hashline", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ApplyPatchTool: JsonElement 参数解析兼容。
    /// </summary>
    [Fact]
    public async Task ApplyPatchTool_JsonElementParams_ShouldParseCorrectly()
    {
        var filePath = Path.Combine(_testRoot, "json.cs");
        await File.WriteAllTextAsync(filePath, "line1\nline2\n");
        var escapedRoot = _testRoot.Replace("\\", "\\\\", StringComparison.Ordinal);
        var escapedFilePath = filePath.Replace("\\", "\\\\", StringComparison.Ordinal);

        var snapshot = await _hashlineService.ReadAsync(filePath, _testRoot, ct: CancellationToken.None);

        var tool = new ApplyPatchTool(
            _gitServiceMock.Object,
            _hashlineService,
            new HashlineOptions(),
            NullLogger<ApplyPatchTool>.Instance);

        // 模拟 JSON 反序列化后的 JsonElement
        var json = $$"""
        {
            "workspace_path": "{{escapedRoot}}",
            "edit_mode": "hashline",
            "request": {
                "filePath": "{{escapedFilePath}}",
                "snapshotId": "{{snapshot.SnapshotId}}",
                "fileFingerprint": "{{snapshot.FileFingerprint}}",
                "dryRun": false,
                "operations": [
                    {
                        "type": "insert_after",
                        "targetLine": 1,
                        "targetAnchorId": "{{snapshot.Lines[0].AnchorId}}",
                        "newLines": ["comment"]
                    }
                ]
            }
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var args = JsonElementToDictionary(doc.RootElement);

        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.True(result.Status == ToolResultStatus.Success, result.Output);
    }

    [Fact]
    public async Task ApplyPatchTool_RequestJsonString_ShouldParseCorrectly()
    {
        var filePath = Path.Combine(_testRoot, "jsonstring.cs");
        await File.WriteAllTextAsync(filePath, "line1\nline2\n");
        var snapshot = await _hashlineService.ReadAsync(filePath, _testRoot, ct: CancellationToken.None);

        var tool = new ApplyPatchTool(
            _gitServiceMock.Object,
            _hashlineService,
            new HashlineOptions(),
            NullLogger<ApplyPatchTool>.Instance);

        var requestJson = $$"""
        ```json
        {
          "filePath": "{{filePath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
          "snapshotId": "{{snapshot.SnapshotId}}",
          "fileFingerprint": "{{snapshot.FileFingerprint}}",
          "dryRun": false,
          "operations": [
            {
              "type": "insert_after",
              "targetLine": 1,
              "targetAnchorId": "{{snapshot.Lines[0].AnchorId}}",
              "newLines": ["comment"]
            }
          ]
        }
        ```
        """;

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testRoot,
            ["edit_mode"] = "hashline",
            ["request"] = requestJson
        };

        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.True(result.Status == ToolResultStatus.Success, result.Output);
    }

    [Fact]
    public async Task ApplyPatchTool_NewtonsoftOperations_ShouldParseCorrectly()
    {
        var filePath = Path.Combine(_testRoot, "apply_newtonsoft_ops.cs");
        await File.WriteAllTextAsync(filePath, "line1\nline2\n");
        var snapshot = await _hashlineService.ReadAsync(filePath, _testRoot, ct: CancellationToken.None);

        var tool = new ApplyPatchTool(
            _gitServiceMock.Object,
            _hashlineService,
            new HashlineOptions(),
            NullLogger<ApplyPatchTool>.Instance);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _testRoot,
            ["edit_mode"] = "hashline",
            ["request"] = new Dictionary<string, object?>
            {
                ["filePath"] = filePath,
                ["snapshotId"] = snapshot.SnapshotId,
                ["fileFingerprint"] = snapshot.FileFingerprint,
                ["operations"] = JArray.FromObject(new[]
                {
                    new
                    {
                        type = "replace_range",
                        startLine = 2,
                        startAnchorId = snapshot.Lines[1].AnchorId,
                        endLine = 2,
                        endAnchorId = snapshot.Lines[1].AnchorId,
                        newLines = new[] { "updated_line2" }
                    }
                })
            }
        }, CancellationToken.None);

        Assert.True(result.Status == ToolResultStatus.Success, result.Output);
        var content = await File.ReadAllTextAsync(filePath);
        Assert.Contains("updated_line2", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyPatchTool_NestedOperationsArray_ShouldReject()
    {
        var filePath = Path.Combine(_testRoot, "nested_apply.cs");
        await File.WriteAllTextAsync(filePath, "line1\nline2\n");
        var snapshot = await _hashlineService.ReadAsync(filePath, _testRoot, ct: CancellationToken.None);

        var tool = new ApplyPatchTool(
            _gitServiceMock.Object,
            _hashlineService,
            new HashlineOptions { Enabled = true },
            NullLogger<ApplyPatchTool>.Instance);

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testRoot,
            ["edit_mode"] = "hashline",
            ["request"] = new Dictionary<string, object?>
            {
                ["filePath"] = filePath,
                ["snapshotId"] = snapshot.SnapshotId,
                ["fileFingerprint"] = snapshot.FileFingerprint,
                ["dryRun"] = false,
                ["operations"] = new List<object?>
                {
                    new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "insert_after",
                            ["targetLine"] = 1,
                            ["targetAnchorId"] = snapshot.Lines[0].AnchorId,
                            ["newLines"] = new List<object?> { "comment" }
                        }
                    }
                }
            }
        };

        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("嵌套数组", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyPatchTool_InvalidOperationsShape_ShouldReturnConcreteFormatGuidance()
    {
        var filePath = Path.Combine(_testRoot, "invalid_apply_shape.cs");
        await File.WriteAllTextAsync(filePath, "line1\nline2\n");
        var snapshot = await _hashlineService.ReadAsync(filePath, _testRoot, ct: CancellationToken.None);

        var tool = new ApplyPatchTool(
            _gitServiceMock.Object,
            _hashlineService,
            new HashlineOptions { Enabled = true },
            NullLogger<ApplyPatchTool>.Instance);

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testRoot,
            ["edit_mode"] = "hashline",
            ["request"] = new Dictionary<string, object?>
            {
                ["filePath"] = filePath,
                ["snapshotId"] = snapshot.SnapshotId,
                ["fileFingerprint"] = snapshot.FileFingerprint,
                ["dryRun"] = false,
                ["operations"] = new Dictionary<string, object?>
                {
                    ["type"] = "replace_range"
                }
            }
        };

        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("HASHLINE_REQUEST_INVALID", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"operations\": [", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"type\": \"replace_range\"", result.Output, StringComparison.Ordinal);
        Assert.Contains("不要传 `operations:{}`", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// ApplyPatchTool: 高风险文件阻断 - ForceForHighRiskFiles=true 时禁止普通 diff。
    /// </summary>
    [Fact]
    public async Task ApplyPatchTool_HighRiskFile_WithoutHashline_ShouldBlock()
    {
        var programPath = Path.Combine(_testRoot, "Program.cs");
        await File.WriteAllTextAsync(programPath, "var app = WebApplication.Create();\n");

        var tool = new ApplyPatchTool(
            _gitServiceMock.Object,
            _hashlineService,
            new HashlineOptions { Enabled = true, ForceForHighRiskFiles = true },
            NullLogger<ApplyPatchTool>.Instance);

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testRoot,
            ["patch_content"] = "--- a/Program.cs\n+++ b/Program.cs\n@@ -1 +1 @@\n-var app = WebApplication.Create();\n+var modified = true;\n"
        };

        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("HIGH_RISK_FILE_REQUIRES_HASHLINE", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ApplyPatchTool: fingerprint mismatch 应返回 HASHLINE_MISMATCH_FAILURE 前缀。
    /// </summary>
    [Fact]
    public async Task ApplyPatchTool_FingerprintMismatch_ShouldReturnMismatchPrefix()
    {
        var filePath = Path.Combine(_testRoot, "mismatch.cs");
        await File.WriteAllTextAsync(filePath, "content\n");

        var tool = new ApplyPatchTool(
            _gitServiceMock.Object,
            _hashlineService,
            new HashlineOptions(),
            NullLogger<ApplyPatchTool>.Instance);

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testRoot,
            ["edit_mode"] = "hashline",
            ["request"] = new Dictionary<string, object?>
            {
                ["filePath"] = filePath,
                ["snapshotId"] = "snap_fake",
                ["fileFingerprint"] = "fp_wrong_fingerprint", // 错误的指纹
                ["dryRun"] = false,
                ["operations"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "insert_after",
                        ["targetLine"] = 1,
                        ["targetAnchorId"] = "FAKE_ANCHOR",
                        ["newLines"] = new List<object?> { "test" }
                    }
                }
            }
        };

        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("HASHLINE_MISMATCH_FAILURE", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ApplyPatchTool: 配置 EnabledForApplyPatch=true + 提供 request 应自动进入 Hashline。
    /// </summary>
    [Fact]
    public async Task ApplyPatchTool_ConfigEnabled_ShouldAutoUseHashline()
    {
        var filePath = Path.Combine(_testRoot, "auto.cs");
        await File.WriteAllTextAsync(filePath, "auto content\n");

        var snapshot = await _hashlineService.ReadAsync(filePath, _testRoot, ct: CancellationToken.None);

        var tool = new ApplyPatchTool(
            _gitServiceMock.Object,
            _hashlineService,
            new HashlineOptions { Enabled = true },
            NullLogger<ApplyPatchTool>.Instance);

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testRoot,
            // 没有 edit_mode，但提供了 request
            ["request"] = new Dictionary<string, object?>
            {
                ["filePath"] = filePath,
                ["snapshotId"] = snapshot.SnapshotId,
                ["fileFingerprint"] = snapshot.FileFingerprint,
                ["dryRun"] = false,
                ["operations"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "replace_range",
                        ["startLine"] = 1,
                        ["startAnchorId"] = snapshot.Lines[0].AnchorId,
                        ["endLine"] = 1,
                        ["endAnchorId"] = snapshot.Lines[0].AnchorId,
                        ["newLines"] = new List<object?> { "modified auto" }
                    }
                }
            }
        };

        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("Hashline", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region SmartPatchTool Tests

    /// <summary>
    /// SmartPatchTool: edit_mode="hashline" 应进入 Hashline 分支。
    /// </summary>
    [Fact]
    public async Task SmartPatchTool_ExplicitHashlineMode_ShouldUseHashline()
    {
        var filePath = Path.Combine(_testRoot, "smart.cs");
        await File.WriteAllTextAsync(filePath, "smart line\n");

        var snapshot = await _hashlineService.ReadAsync(filePath, _testRoot, ct: CancellationToken.None);

        var tool = new SmartPatchTool(
            _gitServiceMock.Object,
            _hashlineService,
            new HashlineOptions(),
            NullLogger<SmartPatchTool>.Instance);

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testRoot,
            ["edit_mode"] = "hashline",
            ["request"] = new Dictionary<string, object?>
            {
                ["filePath"] = filePath,
                ["snapshotId"] = snapshot.SnapshotId,
                ["fileFingerprint"] = snapshot.FileFingerprint,
                ["dryRun"] = false,
                ["operations"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "replace_range",
                        ["startLine"] = 1,
                        ["startAnchorId"] = snapshot.Lines[0].AnchorId,
                        ["endLine"] = 1,
                        ["endAnchorId"] = snapshot.Lines[0].AnchorId,
                        ["newLines"] = new List<object?> { "modified line" }
                    }
                }
            }
        };

        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Equal(ToolResultStatus.Success, result.Status);
    }

    [Fact]
    public async Task SmartPatchTool_ShouldNormalizeDuplicateRepoPrefixForHashlineRequest()
    {
        var filePath = Path.Combine(_testRoot, "smart_prefixed.cs");
        await File.WriteAllTextAsync(filePath, "public string Status => \"Pending\";\n");

        var snapshot = await _hashlineService.ReadAsync(filePath, _testRoot, ct: CancellationToken.None);

        var tool = new SmartPatchTool(
            _gitServiceMock.Object,
            _hashlineService,
            new HashlineOptions(),
            NullLogger<SmartPatchTool>.Instance);
        var duplicatedPath = $"{Path.GetFileName(_testRoot)}/smart_prefixed.cs";

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _testRoot,
            ["edit_mode"] = "hashline",
            ["request"] = new Dictionary<string, object?>
            {
                ["filePath"] = duplicatedPath,
                ["snapshotId"] = snapshot.SnapshotId,
                ["fileFingerprint"] = snapshot.FileFingerprint,
                ["dryRun"] = false,
                ["operations"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "replace_range",
                        ["startLine"] = 1,
                        ["startAnchorId"] = snapshot.Lines[0].AnchorId,
                        ["endLine"] = 1,
                        ["endAnchorId"] = snapshot.Lines[0].AnchorId,
                        ["newLines"] = new List<object?> { "public string Status => \"Ready\";" }
                    }
                }
            }
        }, CancellationToken.None);

        Assert.Equal(ToolResultStatus.Success, result.Status);
        var content = await File.ReadAllTextAsync(filePath);
        Assert.Contains("\"Ready\"", content, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Pending\"", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SmartPatchTool_FingerprintMismatch_ShouldNotRollback()
    {
        var filePath = Path.Combine(_testRoot, "smart_mismatch.cs");
        await File.WriteAllTextAsync(filePath, "content\n");

        var tool = new SmartPatchTool(
            _gitServiceMock.Object,
            _hashlineService,
            new HashlineOptions(),
            NullLogger<SmartPatchTool>.Instance);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _testRoot,
            ["edit_mode"] = "hashline",
            ["request"] = new Dictionary<string, object?>
            {
                ["filePath"] = filePath,
                ["snapshotId"] = "snap_fake",
                ["fileFingerprint"] = "fp_wrong_fingerprint",
                ["dryRun"] = false,
                ["operations"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "insert_after",
                        ["targetLine"] = 1,
                        ["targetAnchorId"] = "FAKE_ANCHOR",
                        ["newLines"] = new List<object?> { "test" }
                    }
                }
            }
        }, CancellationToken.None);

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("HASHLINE_MISMATCH_FAILURE", result.Output, StringComparison.OrdinalIgnoreCase);
        _gitServiceMock.Verify(g => g.RevertToLastCommitAsync(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// SmartPatchTool: JsonElement 参数解析兼容。
    /// </summary>
    [Fact]
    public async Task SmartPatchTool_JsonElementParams_ShouldParseCorrectly()
    {
        var filePath = Path.Combine(_testRoot, "smartjson.cs");
        await File.WriteAllTextAsync(filePath, "line1\nline2\n");
        var escapedRoot = _testRoot.Replace("\\", "\\\\", StringComparison.Ordinal);
        var escapedFilePath = filePath.Replace("\\", "\\\\", StringComparison.Ordinal);

        var snapshot = await _hashlineService.ReadAsync(filePath, _testRoot, ct: CancellationToken.None);

        var tool = new SmartPatchTool(
            _gitServiceMock.Object,
            _hashlineService,
            new HashlineOptions(),
            NullLogger<SmartPatchTool>.Instance);

        var json = $$"""
        {
            "workspace_path": "{{escapedRoot}}",
            "edit_mode": "hashline",
            "request": {
                "filePath": "{{escapedFilePath}}",
                "snapshotId": "{{snapshot.SnapshotId}}",
                "fileFingerprint": "{{snapshot.FileFingerprint}}",
                "dryRun": false,
                "operations": [
                    {
                        "type": "insert_after",
                        "targetLine": 1,
                        "targetAnchorId": "{{snapshot.Lines[0].AnchorId}}",
                        "newLines": ["new line"]
                    }
                ]
            }
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var args = JsonElementToDictionary(doc.RootElement);

        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Equal(ToolResultStatus.Success, result.Status);
    }

    [Fact]
    public async Task SmartPatchTool_NestedOperationsArray_ShouldReject()
    {
        var filePath = Path.Combine(_testRoot, "nested_smart.cs");
        await File.WriteAllTextAsync(filePath, "line1\nline2\n");
        var snapshot = await _hashlineService.ReadAsync(filePath, _testRoot, ct: CancellationToken.None);

        var tool = new SmartPatchTool(
            _gitServiceMock.Object,
            _hashlineService,
            new HashlineOptions { Enabled = true },
            NullLogger<SmartPatchTool>.Instance);

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testRoot,
            ["edit_mode"] = "hashline",
            ["request"] = new Dictionary<string, object?>
            {
                ["filePath"] = filePath,
                ["snapshotId"] = snapshot.SnapshotId,
                ["fileFingerprint"] = snapshot.FileFingerprint,
                ["dryRun"] = false,
                ["operations"] = new List<object?>
                {
                    new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "insert_after",
                            ["targetLine"] = 1,
                            ["targetAnchorId"] = snapshot.Lines[0].AnchorId,
                            ["newLines"] = new List<object?> { "new line" }
                        }
                    }
                }
            }
        };

        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("嵌套数组", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SmartPatchTool_InvalidOperationsShape_ShouldReturnConcreteFormatGuidance()
    {
        var filePath = Path.Combine(_testRoot, "invalid_smart_shape.cs");
        await File.WriteAllTextAsync(filePath, "line1\nline2\n");
        var snapshot = await _hashlineService.ReadAsync(filePath, _testRoot, ct: CancellationToken.None);

        var tool = new SmartPatchTool(
            _gitServiceMock.Object,
            _hashlineService,
            new HashlineOptions { Enabled = true },
            NullLogger<SmartPatchTool>.Instance);

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testRoot,
            ["edit_mode"] = "hashline",
            ["request"] = new Dictionary<string, object?>
            {
                ["filePath"] = filePath,
                ["snapshotId"] = snapshot.SnapshotId,
                ["fileFingerprint"] = snapshot.FileFingerprint,
                ["dryRun"] = false,
                ["operations"] = new Dictionary<string, object?>
                {
                    ["type"] = "replace_range"
                }
            }
        };

        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("HASHLINE_REQUEST_INVALID", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"operations\": [", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"type\": \"replace_range\"", result.Output, StringComparison.Ordinal);
        Assert.Contains("不要传 `operations:{}`", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SmartPatchTool_RequestJsonString_ShouldParseCorrectly()
    {
        var filePath = Path.Combine(_testRoot, "smartjsonstring.cs");
        await File.WriteAllTextAsync(filePath, "line1\nline2\n");
        var snapshot = await _hashlineService.ReadAsync(filePath, _testRoot, ct: CancellationToken.None);

        var tool = new SmartPatchTool(
            _gitServiceMock.Object,
            _hashlineService,
            new HashlineOptions(),
            NullLogger<SmartPatchTool>.Instance);

        var requestJson = $$"""
        ```json
        {
          "filePath": "{{filePath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
          "snapshotId": "{{snapshot.SnapshotId}}",
          "fileFingerprint": "{{snapshot.FileFingerprint}}",
          "dryRun": false,
          "operations": [
            {
              "type": "insert_after",
              "targetLine": 1,
              "targetAnchorId": "{{snapshot.Lines[0].AnchorId}}",
              "newLines": ["new line"]
            }
          ]
        }
        ```
        """;

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testRoot,
            ["edit_mode"] = "hashline",
            ["request"] = requestJson
        };

        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Equal(ToolResultStatus.Success, result.Status);
    }

    /// <summary>
    /// SmartPatchTool: 高风险文件阻断 - ForceForHighRiskFiles=true。
    /// </summary>
    [Fact]
    public async Task SmartPatchTool_HighRiskFile_WithoutHashline_ShouldBlock()
    {
        var projPath = Path.Combine(_testRoot, "App.csproj");
        await File.WriteAllTextAsync(projPath, "<Project>\n</Project>\n");

        var tool = new SmartPatchTool(
            _gitServiceMock.Object,
            _hashlineService,
            new HashlineOptions { Enabled = true, ForceForHighRiskFiles = true },
            NullLogger<SmartPatchTool>.Instance);

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testRoot,
            ["patch_content"] = "--- a/App.csproj\n+++ b/App.csproj\n@@ -1,2 +1,2 @@\n-<Project>\n+<Project Sdk=\"Microsoft.NET.Sdk\">\n</Project>"
        };

        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("HIGH_RISK_FILE_REQUIRES_HASHLINE", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// SmartPatchTool: 配置 Enabled=true 应自动进入 Hashline。
    /// </summary>
    [Fact]
    public async Task SmartPatchTool_ConfigEnabled_ShouldAutoUseHashline()
    {
        var filePath = Path.Combine(_testRoot, "smartauto.cs");
        await File.WriteAllTextAsync(filePath, "auto content\n");

        var snapshot = await _hashlineService.ReadAsync(filePath, _testRoot, ct: CancellationToken.None);

        var tool = new SmartPatchTool(
            _gitServiceMock.Object,
            _hashlineService,
            new HashlineOptions { Enabled = true },
            NullLogger<SmartPatchTool>.Instance);

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testRoot,
            ["request"] = new Dictionary<string, object?>
            {
                ["filePath"] = filePath,
                ["snapshotId"] = snapshot.SnapshotId,
                ["fileFingerprint"] = snapshot.FileFingerprint,
                ["dryRun"] = false,
                ["operations"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "replace_range",
                        ["startLine"] = 1,
                        ["startAnchorId"] = snapshot.Lines[0].AnchorId,
                        ["endLine"] = 1,
                        ["endAnchorId"] = snapshot.Lines[0].AnchorId,
                        ["newLines"] = new List<object?> { "modified" }
                    }
                }
            }
        };

        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Equal(ToolResultStatus.Success, result.Status);
    }

    /// <summary>
    /// SmartPatchTool: fingerprint mismatch 应返回 HASHLINE_MISMATCH_FAILURE 前缀。
    /// </summary>
    [Fact]
    public async Task SmartPatchTool_FingerprintMismatch_ShouldReturnMismatchPrefix()
    {
        var filePath = Path.Combine(_testRoot, "smartmismatch.cs");
        await File.WriteAllTextAsync(filePath, "content\n");

        var tool = new SmartPatchTool(
            _gitServiceMock.Object,
            _hashlineService,
            new HashlineOptions(),
            NullLogger<SmartPatchTool>.Instance);

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testRoot,
            ["edit_mode"] = "hashline",
            ["request"] = new Dictionary<string, object?>
            {
                ["filePath"] = filePath,
                ["snapshotId"] = "snap_fake",
                ["fileFingerprint"] = "fp_wrong",
                ["dryRun"] = false,
                ["operations"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "insert_after",
                        ["targetLine"] = 1,
                        ["targetAnchorId"] = "FAKE_ANCHOR",
                        ["newLines"] = new List<object?> { "test" }
                    }
                }
            }
        };

        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("HASHLINE_MISMATCH_FAILURE", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SmartPatchTool_NewtonsoftOperations_ShouldParseCorrectly()
    {
        var filePath = Path.Combine(_testRoot, "smart_newtonsoft_ops.cs");
        await File.WriteAllTextAsync(filePath, "line1\nline2\n");
        var snapshot = await _hashlineService.ReadAsync(filePath, _testRoot, ct: CancellationToken.None);

        var tool = new SmartPatchTool(
            _gitServiceMock.Object,
            _hashlineService,
            new HashlineOptions(),
            NullLogger<SmartPatchTool>.Instance);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _testRoot,
            ["edit_mode"] = "hashline",
            ["request"] = new Dictionary<string, object?>
            {
                ["filePath"] = filePath,
                ["snapshotId"] = snapshot.SnapshotId,
                ["fileFingerprint"] = snapshot.FileFingerprint,
                ["operations"] = JArray.FromObject(new[]
                {
                    new
                    {
                        type = "replace_range",
                        startLine = 2,
                        startAnchorId = snapshot.Lines[1].AnchorId,
                        endLine = 2,
                        endAnchorId = snapshot.Lines[1].AnchorId,
                        newLines = new[] { "updated_line2" }
                    }
                })
            }
        }, CancellationToken.None);

        Assert.True(result.Status == ToolResultStatus.Success, result.Output);
        var content = await File.ReadAllTextAsync(filePath);
        Assert.Contains("updated_line2", content, StringComparison.Ordinal);
    }

    #endregion

    #region Tool Chain Workflow Tests

    /// <summary>
    /// 工具链: ReadFileTool → ApplyPatchTool 完整流程。
    /// 验证 ReadFileTool 的输出契约足够稳定，可被下游工具直接消费。
    /// </summary>
    [Fact]
    public async Task ToolChain_ReadFileToApplyPatch_ShouldWorkEndToEnd()
    {
        var filePath = Path.Combine(_testRoot, "chain.cs");
        await File.WriteAllTextAsync(filePath, "original content\nsecond line\n");

        // Step 1: ReadFileTool 读取快照
        var readTool = new ReadFileTool(
            NullLogger<ReadFileTool>.Instance,
            _hashlineService,
            new HashlineOptions());

        var readArgs = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testRoot,
            ["path"] = "chain.cs",
            ["mode"] = "hashline"
        };

        var readResult = await readTool.ExecuteAsync(readArgs, CancellationToken.None);
        Assert.Equal(ToolResultStatus.Success, readResult.Status);

        // 从 ReadFileTool 返回结果中解析快照信息 - 验证返回契约稳定性
        var (snapshotId, fileFingerprint, firstAnchorId) = ParseHashlineReadResult(readResult);
        Assert.False(string.IsNullOrEmpty(snapshotId), "snapshotId should be extracted from ReadFileTool output");
        Assert.False(string.IsNullOrEmpty(fileFingerprint), "fileFingerprint should be extracted from ReadFileTool output");
        Assert.False(string.IsNullOrEmpty(firstAnchorId), "anchorId should be parsed from renderedText");

        // Step 2: ApplyPatchTool 执行编辑 - 使用从 ReadFileTool 解析的数据
        var applyTool = new ApplyPatchTool(
            _gitServiceMock.Object,
            _hashlineService,
            new HashlineOptions(),
            NullLogger<ApplyPatchTool>.Instance);

        var applyArgs = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testRoot,
            ["edit_mode"] = "hashline",
            ["request"] = new Dictionary<string, object?>
            {
                ["filePath"] = filePath,
                ["snapshotId"] = snapshotId,
                ["fileFingerprint"] = fileFingerprint,
                ["dryRun"] = false,
                ["operations"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "replace_range",
                        ["startLine"] = 1,
                        ["startAnchorId"] = firstAnchorId,
                        ["endLine"] = 1,
                        ["endAnchorId"] = firstAnchorId,
                        ["newLines"] = new List<object?> { "modified content" }
                    }
                }
            }
        };

        var applyResult = await applyTool.ExecuteAsync(applyArgs, CancellationToken.None);
        Assert.Equal(ToolResultStatus.Success, applyResult.Status);

        // Verify file was modified
        var finalContent = await File.ReadAllTextAsync(filePath);
        Assert.Contains("modified content", finalContent);
    }

    /// <summary>
    /// 工具链: 窗口化 ReadFileTool -> ApplyPatchTool 完整流程。
    /// </summary>
    [Fact]
    public async Task ToolChain_WindowedReadToApplyPatch_ShouldWorkEndToEnd()
    {
        var filePath = Path.Combine(_testRoot, "windowchain.cs");
        await File.WriteAllTextAsync(filePath, "line1\nline2\nline3\nline4\n");

        var readTool = new ReadFileTool(
            NullLogger<ReadFileTool>.Instance,
            _hashlineService,
            new HashlineOptions());

        var readArgs = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testRoot,
            ["path"] = "windowchain.cs",
            ["mode"] = "hashline",
            ["window_start_line"] = 2,
            ["window_line_count"] = 2
        };

        var readResult = await readTool.ExecuteAsync(readArgs, CancellationToken.None);
        Assert.Equal(ToolResultStatus.Success, readResult.Status);

        var (snapshotId, fileFingerprint, firstAnchorId, _, windowStartLine, windowEndLine, isPartialWindow) =
            ParseHashlineReadMetadata(readResult);
        Assert.Equal(2, windowStartLine);
        Assert.Equal(3, windowEndLine);
        Assert.True(isPartialWindow);

        var applyTool = new ApplyPatchTool(
            _gitServiceMock.Object,
            _hashlineService,
            new HashlineOptions(),
            NullLogger<ApplyPatchTool>.Instance);

        var applyArgs = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testRoot,
            ["edit_mode"] = "hashline",
            ["request"] = new Dictionary<string, object?>
            {
                ["filePath"] = filePath,
                ["snapshotId"] = snapshotId,
                ["fileFingerprint"] = fileFingerprint,
                ["dryRun"] = false,
                ["operations"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "replace_range",
                        ["startLine"] = 2,
                        ["startAnchorId"] = firstAnchorId,
                        ["endLine"] = 2,
                        ["endAnchorId"] = firstAnchorId,
                        ["newLines"] = new List<object?> { "line2-modified" }
                    }
                }
            }
        };

        var applyResult = await applyTool.ExecuteAsync(applyArgs, CancellationToken.None);
        Assert.Equal(ToolResultStatus.Success, applyResult.Status);

        var finalContent = await File.ReadAllTextAsync(filePath);
        Assert.Contains("line2-modified", finalContent);
    }

    /// <summary>
    /// 工具链: ReadFileTool → SmartPatchTool 完整流程。
    /// 验证 ReadFileTool 的输出契约足够稳定，可被下游工具直接消费。
    /// </summary>
    [Fact]
    public async Task ToolChain_ReadFileToSmartPatch_ShouldWorkEndToEnd()
    {
        var filePath = Path.Combine(_testRoot, "smartchain.cs");
        await File.WriteAllTextAsync(filePath, "original\n");

        // Step 1: ReadFileTool 读取快照
        var readTool = new ReadFileTool(
            NullLogger<ReadFileTool>.Instance,
            _hashlineService,
            new HashlineOptions());

        var readArgs = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testRoot,
            ["path"] = "smartchain.cs",
            ["mode"] = "hashline"
        };

        var readResult = await readTool.ExecuteAsync(readArgs, CancellationToken.None);
        Assert.Equal(ToolResultStatus.Success, readResult.Status);

        // 从 ReadFileTool 返回结果中解析快照信息 - 验证返回契约稳定性
        var (snapshotId, fileFingerprint, firstAnchorId) = ParseHashlineReadResult(readResult);
        Assert.False(string.IsNullOrEmpty(snapshotId), "snapshotId should be extracted from ReadFileTool output");
        Assert.False(string.IsNullOrEmpty(fileFingerprint), "fileFingerprint should be extracted from ReadFileTool output");
        Assert.False(string.IsNullOrEmpty(firstAnchorId), "anchorId should be parsed from renderedText");

        // Step 2: SmartPatchTool 执行编辑 - 使用从 ReadFileTool 解析的数据
        var smartTool = new SmartPatchTool(
            _gitServiceMock.Object,
            _hashlineService,
            new HashlineOptions(),
            NullLogger<SmartPatchTool>.Instance);

        var smartArgs = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testRoot,
            ["edit_mode"] = "hashline",
            ["request"] = new Dictionary<string, object?>
            {
                ["filePath"] = filePath,
                ["snapshotId"] = snapshotId,
                ["fileFingerprint"] = fileFingerprint,
                ["dryRun"] = false,
                ["operations"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "replace_range",
                        ["startLine"] = 1,
                        ["startAnchorId"] = firstAnchorId,
                        ["endLine"] = 1,
                        ["endAnchorId"] = firstAnchorId,
                        ["newLines"] = new List<object?> { "smart modified" }
                    }
                }
            }
        };

        var smartResult = await smartTool.ExecuteAsync(smartArgs, CancellationToken.None);
        Assert.Equal(ToolResultStatus.Success, smartResult.Status);

        var finalContent = await File.ReadAllTextAsync(filePath);
        Assert.Contains("smart modified", finalContent);
    }

    /// <summary>
    /// 工具链: stale fingerprint 被拒绝后重新读取再编辑。
    /// </summary>
    [Fact]
    public async Task ToolChain_StaleFingerprint_ReReadAndRetryShouldSucceed()
    {
        var filePath = Path.Combine(_testRoot, "stale.cs");
        await File.WriteAllTextAsync(filePath, "version1\n");

        // Step 1: 第一次读取
        var snapshot1 = await _hashlineService.ReadAsync(filePath, _testRoot, ct: CancellationToken.None);

        // Step 2: 外部修改文件 (模拟并发)
        await File.WriteAllTextAsync(filePath, "version2\n");

        // Step 3: 使用旧 snapshot 编辑应该失败
        var applyTool = new ApplyPatchTool(
            _gitServiceMock.Object,
            _hashlineService,
            new HashlineOptions(),
            NullLogger<ApplyPatchTool>.Instance);

        var staleArgs = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testRoot,
            ["edit_mode"] = "hashline",
            ["request"] = new Dictionary<string, object?>
            {
                ["filePath"] = filePath,
                ["snapshotId"] = snapshot1.SnapshotId,
                ["fileFingerprint"] = snapshot1.FileFingerprint,
                ["dryRun"] = false,
                ["operations"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "replace_range",
                        ["startLine"] = 1,
                        ["startAnchorId"] = snapshot1.Lines[0].AnchorId,
                        ["endLine"] = 1,
                        ["endAnchorId"] = snapshot1.Lines[0].AnchorId,
                        ["newLines"] = new List<object?> { "should fail" }
                    }
                }
            }
        };

        var staleResult = await applyTool.ExecuteAsync(staleArgs, CancellationToken.None);
        Assert.Equal(ToolResultStatus.Failed, staleResult.Status);
        Assert.Contains("HASHLINE_MISMATCH_FAILURE", staleResult.Output);

        // Step 4: 重新读取最新 snapshot
        var snapshot2 = await _hashlineService.ReadAsync(filePath, _testRoot, ct: CancellationToken.None);

        // Step 5: 使用新 snapshot 编辑应该成功
        var retryArgs = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testRoot,
            ["edit_mode"] = "hashline",
            ["request"] = new Dictionary<string, object?>
            {
                ["filePath"] = filePath,
                ["snapshotId"] = snapshot2.SnapshotId,
                ["fileFingerprint"] = snapshot2.FileFingerprint,
                ["dryRun"] = false,
                ["operations"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "replace_range",
                        ["startLine"] = 1,
                        ["startAnchorId"] = snapshot2.Lines[0].AnchorId,
                        ["endLine"] = 1,
                        ["endAnchorId"] = snapshot2.Lines[0].AnchorId,
                        ["newLines"] = new List<object?> { "retry success" }
                    }
                }
            }
        };

        var retryResult = await applyTool.ExecuteAsync(retryArgs, CancellationToken.None);
        Assert.Equal(ToolResultStatus.Success, retryResult.Status);

        var finalContent = await File.ReadAllTextAsync(filePath);
        Assert.Contains("retry success", finalContent);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// 从 ReadFileTool hashline 模式的返回结果中解析快照信息。
    /// 验证工具返回契约的稳定性：Metadata 包含结构化数据，renderedText 包含锚点信息。
    /// </summary>
    private static (string SnapshotId, string FileFingerprint, string FirstAnchorId) ParseHashlineReadResult(CodexToolResult result)
    {
        var (snapshotId, fileFingerprint, firstAnchorId, _, _, _, _) = ParseHashlineReadMetadata(result);
        return (snapshotId, fileFingerprint, firstAnchorId);
    }

    private static (
        string SnapshotId,
        string FileFingerprint,
        string FirstAnchorId,
        int TotalLineCount,
        int WindowStartLine,
        int WindowEndLine,
        bool IsPartialWindow) ParseHashlineReadMetadata(CodexToolResult result)
    {
        var metadata = Assert.IsType<System.Text.Json.JsonElement>(result.Metadata);
        var snapshotId = metadata.GetProperty("snapshotId").GetString() ?? string.Empty;
        var fileFingerprint = metadata.GetProperty("fileFingerprint").GetString() ?? string.Empty;
        var renderedText = metadata.GetProperty("renderedText").GetString() ?? string.Empty;
        var totalLineCount = metadata.GetProperty("totalLineCount").GetInt32();
        var windowStartLine = metadata.GetProperty("windowStartLine").GetInt32();
        var windowEndLine = metadata.GetProperty("windowEndLine").GetInt32();
        var isPartialWindow = metadata.GetProperty("isPartialWindow").GetBoolean();

        // 从 renderedText 解析第一行的锚点 ID
        // 格式: LineNumber#AnchorId|LineContent
        var firstLine = renderedText.Split('\n').FirstOrDefault(line => line.Contains('#'));
        Assert.False(string.IsNullOrEmpty(firstLine), "renderedText should contain at least one line with anchor");

        // 解析格式: "1#ABC12345|content"
        var hashIndex = firstLine!.IndexOf('#');
        var pipeIndex = firstLine.IndexOf('|');
        Assert.True(hashIndex >= 0 && pipeIndex > hashIndex, "Line should have format: LineNumber#AnchorId|Content");

        var anchorId = firstLine.Substring(hashIndex + 1, pipeIndex - hashIndex - 1);

        return (snapshotId, fileFingerprint, anchorId, totalLineCount, windowStartLine, windowEndLine, isPartialWindow);
    }

    /// <summary>
    /// 将 JsonElement 转换为 Dictionary，模拟实际参数传递。
    /// </summary>
    private static Dictionary<string, object?> JsonElementToDictionary(JsonElement element)
    {
        var result = new Dictionary<string, object?>();
        foreach (var prop in element.EnumerateObject())
        {
            result[prop.Name] = JsonElementToObject(prop.Value);
        }
        return result;
    }

    private static object? JsonElementToObject(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                return element.GetInt32();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.Object:
                return JsonElementToDictionary(element);
            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in element.EnumerateArray())
                {
                    list.Add(JsonElementToObject(item));
                }
                return list;
            default:
                return element.ToString();
        }
    }

    #endregion
}
