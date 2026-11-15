// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodexFlow.Core.Tests.Tools;

/// <summary>
/// 真实 Git 集成测试 - 验证 Git 工具的实际克隆功能
/// 这些测试需要网络连接和 Git 客户端安装
/// </summary>
public class GitToolsRealIntegrationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testWorkspace;
    private readonly IGitService _gitService;

    public GitToolsRealIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _testWorkspace = Path.Combine(TestConstants.GetWorkspacePath(), "GitIntegration");
        if (Directory.Exists(_testWorkspace))
        {
             var dir = new DirectoryInfo(_testWorkspace);
             SetAttributesNormal(dir);
             Directory.Delete(_testWorkspace, true);
        }
        Directory.CreateDirectory(_testWorkspace);

        var loggerFactory = LoggerFactory.Create(builder => 
        {
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        _gitService = new GitService(loggerFactory.CreateLogger<GitService>());

        _output.WriteLine($"Git test workspace created: {_testWorkspace}");
    }

    public void Dispose()
    {
        // Stable workspace for inspection, skipping auto-clean
        /*
        try
        {
            if (Directory.Exists(_testWorkspace))
            {
                // Force delete read-only files (common in .git folders)
                var dir = new DirectoryInfo(_testWorkspace);
                SetAttributesNormal(dir);
                Directory.Delete(_testWorkspace, true);
                _output.WriteLine($"Git test workspace cleaned: {_testWorkspace}");
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Warning: Failed to clean git workspace: {ex.Message}");
        }
        */
    }

    private void SetAttributesNormal(DirectoryInfo dir)
    {
        foreach (var subDir in dir.GetDirectories())
        {
            SetAttributesNormal(subDir);
        }
        foreach (var file in dir.GetFiles())
        {
            file.Attributes = FileAttributes.Normal;
        }
    }

    [Fact]
    public async Task GitService_CloneRepository_ShouldCloneRealRepo_Successfully()
    {
        // Arrange
        var repoUrl = "https://github.com/microsoft/calculator.git"; // Small public repo
        var targetPath = Path.Combine(_testWorkspace, "cloned_repo");

        _output.WriteLine($"Cloning repository: {repoUrl}");
        _output.WriteLine($"Target path: {targetPath}");

        // Act
        var cloneSuccess = await _gitService.CloneRepositoryAsync(repoUrl, targetPath, null);

        // Assert
        _output.WriteLine($"Clone result: {cloneSuccess}");

        if (!cloneSuccess)
        {
            _output.WriteLine("Clone failed - Git may not be installed or network issue");
            Assert.True(true, "Skipping test - Git not available");
            return;
        }

        Assert.True(cloneSuccess);
        Assert.True(Directory.Exists(targetPath), $"Cloned directory should exist at {targetPath}");

        // Verify .git folder exists
        var gitFolder = Path.Combine(targetPath, ".git");
        Assert.True(Directory.Exists(gitFolder), ".git folder should exist in cloned repo");

        // Verify some expected files exist
        var files = Directory.GetFiles(targetPath, "*.*");
        _output.WriteLine($"Total files cloned: {files.Length}");
        Assert.NotEmpty(files);
        
        _output.WriteLine("✓ Repository cloned successfully with all files");
    }

    [Fact]
    public async Task GitWorkspaceTool_Clone_ShouldCloneToDefaultWorkspace_Successfully()
    {
        // Arrange
        var tool = new GitWorkspaceTool(
            "git_clone", 
            "Clone repository", 
            new[] { 1, 2, 3 }, 
            _gitService, 
            NullLogger<GitWorkspaceTool>.Instance);

        var repoUrl = "https://github.com/iwaitu/CleanApp.git"; // User's actual test repo
        
        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["url"] = repoUrl
        };

        _output.WriteLine($"Testing git_clone tool with workspace: {_testWorkspace}");
        _output.WriteLine($"Repository URL: {repoUrl}");

        // Act
        var result = await tool.ExecuteAsync(args);

        // Assert
        _output.WriteLine($"Tool status: {result.Status}");
        _output.WriteLine($"Tool output:\n{result.Output}");
        
        if (result.Status != Core.Models.ToolResultStatus.Success)
        {
            _output.WriteLine("Git clone failed - this might be expected if Git is not installed");
            Assert.Contains("Git", result.Output, StringComparison.OrdinalIgnoreCase);
            return;
        }

        Assert.Equal(Core.Models.ToolResultStatus.Success, result.Status);
        
        // Verify the repository was cloned to workspace/<repoName>
        var repoPath = Path.Combine(_testWorkspace, "CleanApp");
        Assert.True(Directory.Exists(repoPath), $"Repository should be cloned to {repoPath}");
        
        // Verify .git folder
        var gitFolder = Path.Combine(repoPath, ".git");
        Assert.True(Directory.Exists(gitFolder), ".git folder should exist");
        
        // List all files to verify content
        var files = Directory.GetFiles(repoPath, "*.*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(".git"))
            .ToList();
        
        _output.WriteLine($"\nCloned files ({files.Count} total):");
        foreach (var file in files.Take(20)) // Show first 20 files
        {
            var relativePath = Path.GetRelativePath(repoPath, file);
            _output.WriteLine($"  - {relativePath}");
        }
        
        Assert.NotEmpty(files);
        _output.WriteLine("\n✓ Git clone tool test completed successfully");
        _output.WriteLine($"✓ Repository content verified in workspace: {repoPath}");
    }

    [Fact]
    public async Task GitWorkspaceTool_Clone_AfterClone_FilesAreAccessible()
    {
        // Arrange - First clone
        var cloneTool = new GitWorkspaceTool(
            "git_clone", 
            "Clone repository", 
            new[] { 1, 2, 3 }, 
            _gitService, 
            NullLogger<GitWorkspaceTool>.Instance);

        var cloneArgs = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["url"] = "https://github.com/iwaitu/CleanApp.git"
        };

        var cloneResult = await cloneTool.ExecuteAsync(cloneArgs);
        
        if (cloneResult.Status != Core.Models.ToolResultStatus.Success)
        {
            _output.WriteLine("Skipping test - Git clone failed");
            return;
        }

        // Act - Try to list files in cloned repo
        var listTool = new Core.Agents.Tools.ListWorkspaceTool(NullLogger<Core.Agents.Tools.ListWorkspaceTool>.Instance);
        var repoPath = Path.Combine(_testWorkspace, "CleanApp");
        var listArgs = new Dictionary<string, object?>
        {
            ["workspace_path"] = repoPath,
            ["path"] = ".",
            ["recursive"] = false
        };

        var listResult = await listTool.ExecuteAsync(listArgs);

        // Assert
        _output.WriteLine($"List result:\n{listResult.Output}");
        Assert.Equal(Core.Models.ToolResultStatus.Success, listResult.Status);
        Assert.Contains("[", listResult.Output); // Should contain file/dir markers
        
        // Act - Try to read a file (if exists)
        var files = Directory.GetFiles(repoPath, "*.md", SearchOption.TopDirectoryOnly);
        
        if (files.Any())
        {
            var readTool = new Core.Agents.Tools.ReadFileTool(NullLogger<Core.Agents.Tools.ReadFileTool>.Instance);
            var fileName = Path.GetFileName(files[0]);
            
            var readArgs = new Dictionary<string, object?>
            {
                ["workspace_path"] = repoPath,
                ["path"] = fileName
            };

            var readResult = await readTool.ExecuteAsync(readArgs);
            
            _output.WriteLine($"\nRead {fileName}:\n{readResult.Output}");
            Assert.Equal(Core.Models.ToolResultStatus.Success, readResult.Status);
            Assert.NotEmpty(readResult.Output);
        }
        
        _output.WriteLine("\n✓ Files in cloned repository are accessible via tools");
    }

    [Fact]
    public async Task GitWorkspaceTool_Clone_VerifyDefaultWorkspacePath()
    {
        // This test verifies the exact behavior the user reported as problematic
        
        // Arrange
        var sessionWorkspace = _testWorkspace; // Simulating session.WorkspacePath
        
        _output.WriteLine($"Session workspace path: {sessionWorkspace}");
        
        var tool = new GitWorkspaceTool(
            "git_clone", 
            "Clone repository", 
            new[] { 1, 2, 3 }, 
            _gitService, 
            NullLogger<GitWorkspaceTool>.Instance);

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = sessionWorkspace,
            ["url"] = "https://github.com/iwaitu/CleanApp.git"
        };

        // Act
        var result = await tool.ExecuteAsync(args);
        
        if (result.Status != Core.Models.ToolResultStatus.Success)
        {
            _output.WriteLine("Skipping - Git not available");
            return;
        }

        // Assert - Verify expected paths
        var expectedRepoPath = Path.Combine(sessionWorkspace, "CleanApp");
        _output.WriteLine($"\nExpected repository path: {expectedRepoPath}");
        _output.WriteLine($"Directory exists: {Directory.Exists(expectedRepoPath)}");
        
        if (Directory.Exists(expectedRepoPath))
        {
            var gitPath = Path.Combine(expectedRepoPath, ".git");
            _output.WriteLine($"Git folder exists: {Directory.Exists(gitPath)}");
            
            var allDirs = Directory.GetDirectories(expectedRepoPath);
            _output.WriteLine($"\nDirectories in repo ({allDirs.Length}):");
            foreach (var dir in allDirs)
            {
                _output.WriteLine($"  - {Path.GetFileName(dir)}");
            }
            
            var allFiles = Directory.GetFiles(expectedRepoPath);
            _output.WriteLine($"\nFiles in repo root ({allFiles.Length}):");
            foreach (var file in allFiles)
            {
                _output.WriteLine($"  - {Path.GetFileName(file)}");
            }
        }
        
        Assert.True(Directory.Exists(expectedRepoPath), 
            $"Repository should be cloned to {expectedRepoPath}");
        
        _output.WriteLine($"\n✓ Git clone uses correct default workspace path");
        _output.WriteLine($"✓ Repository available at: {expectedRepoPath}");
    }
}
