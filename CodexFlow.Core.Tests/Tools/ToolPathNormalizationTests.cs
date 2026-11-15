// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Agents;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.Tools;

public class ToolPathNormalizationTests : IDisposable
{
    private readonly string _root;
    private readonly string _repoRoot;

    public ToolPathNormalizationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "codex-pathnorm-" + Guid.NewGuid().ToString("N"));
        _repoRoot = Path.Combine(_root, "CleanApp");
        Directory.CreateDirectory(Path.Combine(_repoRoot, "src", "CleanApp.Core", "Interfaces"));
        File.WriteAllText(Path.Combine(_repoRoot, "src", "CleanApp.Core", "Interfaces", "IMongoFileService.cs"), "public interface IMongoFileService {}");
    }

    [Fact]
    public void ToolPathResolver_ResolveBaseRoot_PrefersProjectRoot()
    {
        var resolved = ToolPathResolver.ResolveBaseRoot(_root, _repoRoot);
        Assert.Equal(Path.GetFullPath(_repoRoot), resolved);
    }

    [Fact]
    public void ToolPathResolver_NormalizeDuplicateRepoPrefix_StripsPrefix()
    {
        var normalized = ToolPathResolver.NormalizeDuplicateRepoPrefix("CleanApp/src/CleanApp.Core/Interfaces/IMongoFileService.cs", _repoRoot);
        Assert.Equal("src/CleanApp.Core/Interfaces/IMongoFileService.cs", normalized.Replace('\\', '/'));
    }

    [Fact]
    public void ToolPathResolver_NormalizeDuplicateRepoPrefix_StripsPrefix_InShadowTaskRoot()
    {
        var shadowTaskRoot = Path.Combine(_root, "shadows", "CleanApp", "TASK_02");
        Directory.CreateDirectory(Path.Combine(shadowTaskRoot, "src", "CleanApp.Core", "Interfaces"));
        var normalized = ToolPathResolver.NormalizeDuplicateRepoPrefix("CleanApp/src/CleanApp.Core/Interfaces/IMongoFileService.cs", shadowTaskRoot);
        Assert.Equal("src/CleanApp.Core/Interfaces/IMongoFileService.cs", normalized.Replace('\\', '/'));
    }

    [Fact]
    public void ToolPathResolver_NormalizeDuplicateRepoPrefix_RepoNameOnly_BecomesCurrentDir()
    {
        var normalized = ToolPathResolver.NormalizeDuplicateRepoPrefix("CleanApp", _repoRoot);
        Assert.Equal(".", normalized);
    }

    [Fact]
    public void ToolPathResolver_NormalizeDuplicateRepoPrefix_DotSlashRepoPrefix_StripsCorrectly()
    {
        var normalized = ToolPathResolver.NormalizeDuplicateRepoPrefix("./CleanApp/src/CleanApp.Core/Interfaces/IMongoFileService.cs", _repoRoot);
        Assert.Equal("src/CleanApp.Core/Interfaces/IMongoFileService.cs", normalized.Replace('\\', '/'));
    }

    [Fact]
    public async Task ReadFileTool_ShouldUseProjectRoot_AndStripDuplicateRepoPrefix()
    {
        var tool = new ReadFileTool(NullLogger<ReadFileTool>.Instance);
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _root,
            ["project_root"] = _repoRoot,
            ["path"] = "CleanApp/src/CleanApp.Core/Interfaces/IMongoFileService.cs"
        });

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("src/CleanApp.Core/Interfaces/IMongoFileService.cs", result.Output);
        Assert.Contains("IMongoFileService", result.Output);
    }

    [Fact]
    public async Task ListWorkspaceTool_ShouldUseProjectRoot_AndStripDuplicateRepoPrefix()
    {
        var tool = new ListWorkspaceTool(NullLogger<ListWorkspaceTool>.Instance);
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _root,
            ["project_root"] = _repoRoot,
            ["path"] = "CleanApp/src"
        });

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("CleanApp.Core", result.Output);
    }

    [Fact]
    public async Task StructuralLsTool_ShouldUseProjectRoot_AndStripDuplicateRepoPrefix()
    {
        var tool = new StructuralLsTool(new Mock<ICodeAnalysisService>().Object, NullLogger<StructuralLsTool>.Instance);
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _root,
            ["project_root"] = _repoRoot,
            ["path"] = "CleanApp/src/CleanApp.Core/Interfaces"
        });

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("IMongoFileService.cs", result.Output);
    }

    [Fact]
    public async Task StructuralLsTool_ShouldFallbackToRoot_WhenPathArgumentIsListPayload()
    {
        var tool = new StructuralLsTool(new Mock<ICodeAnalysisService>().Object, NullLogger<StructuralLsTool>.Instance);
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _root,
            ["project_root"] = _repoRoot,
            ["path"] = new object?[] { new Dictionary<string, object?> { ["bad"] = "payload" } }
        });

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("src", result.Output);
        Assert.DoesNotContain("System.Collections.Generic.List", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListWorkspaceTool_ShouldFallbackToRoot_WhenPathArgumentIsListPayload()
    {
        var tool = new ListWorkspaceTool(NullLogger<ListWorkspaceTool>.Instance);
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _root,
            ["project_root"] = _repoRoot,
            ["path"] = new object?[] { new Dictionary<string, object?> { ["bad"] = "payload" } }
        });

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("src", result.Output);
        Assert.DoesNotContain("System.Collections.Generic.List", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolPathResolver_NormalizeDuplicateRepoPrefixInPatch_StripsPatchHeaderPrefix()
    {
        var patch = """
*** Begin Patch
*** Update File: CleanApp/src/CleanApp.Core/Interfaces/IMongoFileService.cs
@@
-old
+new
*** End Patch
""";
        var normalized = ToolPathResolver.NormalizeDuplicateRepoPrefixInPatch(patch, _repoRoot);
        Assert.Contains("*** Update File: src/CleanApp.Core/Interfaces/IMongoFileService.cs", normalized);
        Assert.DoesNotContain("*** Update File: CleanApp/src/", normalized);
    }

    [Fact]
    public void ToolPathResolver_NormalizeDuplicateRepoPrefixInPatch_StripsPatchHeaderPrefix_InShadowTaskRoot()
    {
        var shadowTaskRoot = Path.Combine(_root, "shadows", "CleanApp", "TASK_02");
        Directory.CreateDirectory(shadowTaskRoot);
        var patch = """
*** Begin Patch
*** Update File: CleanApp/src/CleanApp.Core/Interfaces/IMongoFileService.cs
@@
-old
+new
*** End Patch
""";
        var normalized = ToolPathResolver.NormalizeDuplicateRepoPrefixInPatch(patch, shadowTaskRoot);
        Assert.Contains("*** Update File: src/CleanApp.Core/Interfaces/IMongoFileService.cs", normalized);
        Assert.DoesNotContain("*** Update File: CleanApp/src/", normalized);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
        catch
        {
            // ignore cleanup failures on Windows file locks
        }
    }
}
