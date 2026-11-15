// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using CodexFlow.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Diagnostics;
using Xunit;

namespace CodexFlow.Core.Tests.Tools;

public class GitWorkspaceToolMultiRepoSelectionTests
{
    private readonly ITestOutputHelper _output;

    public GitWorkspaceToolMultiRepoSelectionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task GitClone_ShouldCloneMcpWebApiRepo_ToWorkspaceSubFolder()
    {
        var cloneWorkspace = Path.Combine(TestConstants.GetWorkspacePath(), "mcpwebapi-clone-test");
        if (Directory.Exists(cloneWorkspace))
        {
            Directory.Delete(cloneWorkspace, true);
        }
        Directory.CreateDirectory(cloneWorkspace);

        var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information));
        var gitService = new GitService(loggerFactory.CreateLogger<GitService>());
        var tool = new GitWorkspaceTool(
            "git_clone",
            "从远程 GitHub 仓库拉取代码到工作区子目录。",
            new[] { 1, 2, 3 },
            gitService,
            NullLogger<GitWorkspaceTool>.Instance);

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = cloneWorkspace,
            ["url"] = "https://github.com/iwaitu/mcpwebapi.git"
        };

        var result = await tool.ExecuteAsync(args);
        _output.WriteLine(result.Output);

        Assert.Equal(ToolResultStatus.Success, result.Status);

        var repoDir = Path.Combine(cloneWorkspace, "mcpwebapi");
        Assert.True(Directory.Exists(repoDir), $"仓库目录不存在: {repoDir}");
        Assert.True(Directory.Exists(Path.Combine(repoDir, ".git")), "克隆后应存在 .git 目录");

        var fileCount = Directory
            .GetFiles(repoDir, "*", SearchOption.AllDirectories)
            .Count(f => !f.Replace('\\', '/').Contains("/.git/"));
        Assert.True(fileCount > 0, "克隆后应至少包含一个项目文件");
    }

    [Fact]
    public async Task GitClone_ShouldForwardGithubToken_ToGitService()
    {
        var workspace = TestConstants.GetWorkspacePath();
        Directory.CreateDirectory(workspace);

        var gitMock = new Mock<IGitService>(MockBehavior.Strict);
        gitMock.Setup(g => g.CloneRepositoryAsync(
                It.Is<Uri>(u => u.ToString() == "https://github.com/iwaitu/mcpwebapi.git"),
                It.IsAny<string>(),
                null,
                "ghp_test_token"))
            .ReturnsAsync(true);

        var tool = new GitWorkspaceTool(
            "git_clone",
            "clone",
            new[] { 1, 2, 3 },
            gitMock.Object,
            NullLogger<GitWorkspaceTool>.Instance);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = workspace,
            ["url"] = "https://github.com/iwaitu/mcpwebapi.git",
            ["github_token"] = "ghp_test_token"
        });

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        gitMock.Verify(g => g.CloneRepositoryAsync(
            It.Is<Uri>(u => u.ToString() == "https://github.com/iwaitu/mcpwebapi.git"),
            It.IsAny<string>(),
            null,
            "ghp_test_token"), Times.Once);
    }

    [Fact]
    public async Task GitClone_ShouldPromptForToken_WhenCloneFailsWithoutToken()
    {
        var workspace = TestConstants.GetWorkspacePath();
        Directory.CreateDirectory(workspace);

        var gitMock = new Mock<IGitService>(MockBehavior.Strict);
        gitMock.Setup(g => g.CloneRepositoryAsync(
                It.Is<Uri>(u => u.ToString() == "https://github.com/iwaitu/private-repo.git"),
                It.IsAny<string>(),
                null,
                null))
            .ReturnsAsync(false);

        var tool = new GitWorkspaceTool(
            "git_clone",
            "clone",
            new[] { 1, 2, 3 },
            gitMock.Object,
            NullLogger<GitWorkspaceTool>.Instance);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = workspace,
            ["url"] = "https://github.com/iwaitu/private-repo.git"
            // 注意：没有传 github_token
        });

        _output.WriteLine(result.Output);
        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("私有仓库", result.Output);
        Assert.Contains("GitHub Token", result.Output);
    }

    [Fact]
    public async Task GitClone_ShouldReportTokenInvalid_WhenCloneFailsWithToken()
    {
        var workspace = TestConstants.GetWorkspacePath();
        Directory.CreateDirectory(workspace);

        var gitMock = new Mock<IGitService>(MockBehavior.Strict);
        gitMock.Setup(g => g.CloneRepositoryAsync(
                It.Is<Uri>(u => u.ToString() == "https://github.com/iwaitu/private-repo.git"),
                It.IsAny<string>(),
                null,
                "ghp_invalid_token"))
            .ReturnsAsync(false);

        var tool = new GitWorkspaceTool(
            "git_clone",
            "clone",
            new[] { 1, 2, 3 },
            gitMock.Object,
            NullLogger<GitWorkspaceTool>.Instance);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = workspace,
            ["url"] = "https://github.com/iwaitu/private-repo.git",
            ["github_token"] = "ghp_invalid_token"
        });

        _output.WriteLine(result.Output);
        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("Token", result.Output);
        Assert.Contains("仍然失败", result.Output);
    }

    [Fact]
    public async Task Checkpoint_ShouldCommitOnlySelectedProject_WhenTwoProjectsHaveBlankTxtFiles()
    {
        var baseWorkspace = Path.Combine(TestConstants.GetWorkspacePath(), "multi-project-commit-scope");
        if (Directory.Exists(baseWorkspace))
        {
            Directory.Delete(baseWorkspace, true);
        }
        Directory.CreateDirectory(baseWorkspace);

        var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information));
        var gitService = new GitService(loggerFactory.CreateLogger<GitService>());

        var cloneTool = new GitWorkspaceTool(
            "git_clone",
            "clone",
            new[] { 1, 2, 3 },
            gitService,
            NullLogger<GitWorkspaceTool>.Instance);

        var cloneRepo1 = await cloneTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = baseWorkspace,
            ["url"] = "https://github.com/iwaitu/mcpwebapi.git"
        });
        Assert.Equal(ToolResultStatus.Success, cloneRepo1.Status);

        var cloneRepo2 = await cloneTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = baseWorkspace,
            ["url"] = "https://github.com/iwaitu/vllmchatclient.git"
        });
        Assert.Equal(ToolResultStatus.Success, cloneRepo2.Status);

        var repoA = Path.Combine(baseWorkspace, "mcpwebapi");
        var repoB = Path.Combine(baseWorkspace, "vllmchatclient");
        Assert.True(Directory.Exists(repoA), $"缺少仓库目录: {repoA}");
        Assert.True(Directory.Exists(repoB), $"缺少仓库目录: {repoB}");

        // 配置本地 git identity，确保 commit 可执行
        RunGit(repoA, "config user.email test@local.dev");
        RunGit(repoA, "config user.name test-bot");
        RunGit(repoB, "config user.email test@local.dev");
        RunGit(repoB, "config user.name test-bot");

        // 在两个项目中分别创建空白文件
        var fileInA = Path.Combine(repoA, "blank-a.txt");
        var fileInB = Path.Combine(repoB, "blank-b.txt");
        await File.WriteAllTextAsync(fileInA, string.Empty);
        await File.WriteAllTextAsync(fileInB, string.Empty);

        // 仅对 repoA 执行 checkpoint
        var checkpointTool = new GitWorkspaceTool(
            "openspec_create_checkpoint",
            "checkpoint",
            new[] { 3 },
            gitService,
            NullLogger<GitWorkspaceTool>.Instance);

        var checkpointResult = await checkpointTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = baseWorkspace,
            ["repo_path"] = "mcpwebapi",
            ["reason"] = "blank txt scope test"
        });

        _output.WriteLine(checkpointResult.Output);
        Assert.Equal(ToolResultStatus.Success, checkpointResult.Status);

        // repoA 应该已提交（工作树干净）
        var statusA = RunGit(repoA, "status --porcelain").Trim();
        _output.WriteLine($"repoA status: {statusA}");
        Assert.True(string.IsNullOrEmpty(statusA), "选中的仓库应已提交并保持干净状态");

        // repoB 仍应保留未提交改动
        var statusB = RunGit(repoB, "status --porcelain");
        _output.WriteLine($"repoB status: {statusB}");
        Assert.Contains("blank-b.txt", statusB);
    }

    private static List<string> DiscoverGitRepositories(string workspacePath)
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = new DirectoryInfo(workspacePath);
        var queue = new Queue<(DirectoryInfo Dir, int Depth)>();
        queue.Enqueue((root, 0));

        const int maxDepth = 3;
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", "node_modules", "bin", "obj", ".next", "dist", "build", ".venv"
        };

        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                results.Add(dir.FullName);
                continue;
            }

            if (depth >= maxDepth) continue;

            DirectoryInfo[] children;
            try { children = dir.GetDirectories(); }
            catch { continue; }

            foreach (var child in children)
            {
                if (ignored.Contains(child.Name)) continue;
                queue.Enqueue((child, depth + 1));
            }
        }

        return results.OrderBy(x => x).ToList();
    }

    private static string RunGit(string workingDirectory, string args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 git 进程");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {args} 失败 (exit={process.ExitCode})\nstdout: {stdout}\nstderr: {stderr}");
        }

        return stdout;
    }
}
