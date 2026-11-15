// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Services;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests;

public class SmartPatchIntegrationTests
{
    private readonly string _testRoot;
    private readonly GitService _gitService;

    public SmartPatchIntegrationTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "IvilsonTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        
        var logger = new Mock<ILogger<GitService>>().Object;
        _gitService = new GitService(logger);
    }

    [Fact]
    public async Task SmartPatch_ShouldApplyValidPatch_AndRollbackOnInvalid()
    {
        // 1. 准备环境：初始化仓库并创建一个文件
        var repoPath = Path.Combine(_testRoot, "SampleProject");
        await _gitService.InitRepositoryAsync(repoPath);
        RunGit(repoPath, "config core.autocrlf false");
        
        var testFile = Path.Combine(repoPath, "hello.txt");
        await File.WriteAllTextAsync(testFile, "Hello World\nLine 2\nLine 3\n");
        await _gitService.CommitAsync(repoPath, "Initial commit");

        // 2. 准备一个合法的 Patch (Unified Diff)
        await File.WriteAllTextAsync(testFile, "Hello Ivilson\nLine 2\nLine 3\n");
        var validPatch = RunGit(repoPath, "diff -- hello.txt");
        RunGit(repoPath, "checkout -- hello.txt");

        // 3. 测试 SmartPatchTool 的成功路径
        var toolLogger = new Mock<ILogger<SmartPatchTool>>().Object;
        var tool = new SmartPatchTool(_gitService, toolLogger);
        
        var args = new Dictionary<string, object?> 
        { 
            { "workspace_path", repoPath },
            { "patch_content", validPatch },
            { "reason", "Testing success" }
        };

        var result = await tool.ExecuteAsync(args);
        
        Assert.True(result.Status == ToolResultStatus.Success, result.Output);
        var content = await File.ReadAllTextAsync(testFile);
        Assert.Contains("Hello Ivilson", content);

        // 4. 准备一个冲突的/非法的 Patch
        var invalidPatch = @"--- a/hello.txt
+++ b/hello.txt
@@ -1,3 +1,3 @@
-Non Existent Line
+Boom
 Line 2
 Line 3
";

        // 5. 测试失败路径与原子回滚
        var argsFail = new Dictionary<string, object?> 
        { 
            { "workspace_path", repoPath },
            { "patch_content", invalidPatch },
            { "reason", "Testing rollback" }
        };

        var resultFail = await tool.ExecuteAsync(argsFail);
        
        Assert.Equal(ToolResultStatus.Failed, resultFail.Status);
        Assert.Contains("回滚", resultFail.Output);

        // 6. 验证回滚是否成功：文件内容应保持为 Patch 前的状态 (即包含 Hello Ivilson)
        var contentAfterRollback = await File.ReadAllTextAsync(testFile);
        Assert.Contains("Hello Ivilson", contentAfterRollback);
        Assert.DoesNotContain("Boom", contentAfterRollback);
        
        // 清理
        try { Directory.Delete(_testRoot, true); } catch {}
    }

    private static string RunGit(string workingDirectory, string arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = System.Diagnostics.Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start git.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments} failed\nstdout: {stdout}\nstderr: {stderr}");
        }

        return stdout;
    }
}
