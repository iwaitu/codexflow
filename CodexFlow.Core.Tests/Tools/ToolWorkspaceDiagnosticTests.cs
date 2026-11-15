// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Models;
using CodexFlow.Core.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CodexFlow.Core.Tests.Tools;

/// <summary>
/// 诊断测试：验证工具的默认工作空间行为
/// 这些测试帮助诊断用户报告的"Git 克隆后找不到文件"问题
/// </summary>
public class ToolWorkspaceDiagnosticTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testWorkspace;
    private readonly ILoggerFactory _loggerFactory;

    public ToolWorkspaceDiagnosticTests(ITestOutputHelper output)
    {
        _output = output;
        _testWorkspace = Path.Combine(TestConstants.GetWorkspacePath(), "Diagnostic");
        if (Directory.Exists(_testWorkspace))
        {
             var dir = new DirectoryInfo(_testWorkspace);
             SetAttributesNormal(dir);
             Directory.Delete(_testWorkspace, true);
        }
        Directory.CreateDirectory(_testWorkspace);
        
        _loggerFactory = LoggerFactory.Create(builder => 
        {
            builder.SetMinimumLevel(LogLevel.Debug);
        });
        
        _output.WriteLine($"=== Diagnostic Test Workspace: {_testWorkspace} ===\n");
    }

    public void Dispose()
    {
        // Stable workspace for inspection, skipping auto-clean
        /*
        try
        {
            if (Directory.Exists(_testWorkspace))
            {
                var dir = new DirectoryInfo(_testWorkspace);
                SetAttributesNormal(dir);
                Directory.Delete(_testWorkspace, true);
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Warning: Cleanup failed: {ex.Message}");
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
    public async Task Diagnostic_GitClone_VerifyFullPath()
    {
        _output.WriteLine("### TEST: Git Clone Full Path Verification ###\n");
        
        // Arrange
        var gitService = new GitService(_loggerFactory.CreateLogger<GitService>());
        var tool = new GitWorkspaceTool(
            "git_clone", 
            "Clone repository", 
            new[] { 1, 2, 3 }, 
            gitService, 
            _loggerFactory.CreateLogger<GitWorkspaceTool>());

        var repoUrl = "https://github.com/iwaitu/CleanApp.git";
        
        _output.WriteLine($"Workspace Path: {_testWorkspace}");
        _output.WriteLine($"Repository URL: {repoUrl}");
        _output.WriteLine($"Expected clone path: {Path.Combine(_testWorkspace, "CleanApp")}\n");

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["url"] = repoUrl
        };

        // Act
        _output.WriteLine("Executing git_clone tool...\n");
        var result = await tool.ExecuteAsync(args);

        // Assert and Diagnose
        _output.WriteLine($"Tool Status: {result.Status}");
        _output.WriteLine($"Tool Output:\n{result.Output}\n");
        
        if (result.Metadata != null && result.Metadata is Dictionary<string, object?> metadata)
        {
            _output.WriteLine("Tool Metadata:");
            foreach (var kv in metadata)
            {
                _output.WriteLine($"  {kv.Key}: {kv.Value}");
            }
            _output.WriteLine("");
        }

        _output.WriteLine("Workspace Directory Structure:");
        PrintDirectoryTree(_testWorkspace, "", 0);
        _output.WriteLine("");

        // Detailed checks
        var repoPath = Path.Combine(_testWorkspace, "CleanApp");
        _output.WriteLine("Verification Results:");
        _output.WriteLine($"  Workspace exists: {Directory.Exists(_testWorkspace)}");
        _output.WriteLine($"  Project directory exists: {Directory.Exists(repoPath)}");

        if (Directory.Exists(repoPath))
        {
            var gitDir = Path.Combine(repoPath, ".git");
            _output.WriteLine($"  .git directory exists: {Directory.Exists(gitDir)}");

            var files = Directory.GetFiles(repoPath, "*.*");
            _output.WriteLine($"  Files in project root: {files.Length}");

            if (files.Length > 0)
            {
                _output.WriteLine("  Sample files:");
                foreach (var file in files.Take(5))
                {
                    _output.WriteLine($"    - {Path.GetFileName(file)}");
                }
            }

            var dirs = Directory.GetDirectories(repoPath, "*")
                .Where(d => !d.EndsWith(".git"));
            _output.WriteLine($"  Directories in project root: {dirs.Count()}");

            if (dirs.Any())
            {
                _output.WriteLine("  Sample directories:");
                foreach (var dir in dirs.Take(5))
                {
                    _output.WriteLine($"    - {Path.GetFileName(dir)}");
                }
            }
        }
        
        _output.WriteLine($"\n### TEST RESULT: {(result.Status == ToolResultStatus.Success ? "PASSED" : "FAILED")} ###");
    }

    [Fact]
    public async Task Diagnostic_AfterClone_ListWorkspace_ShouldShowFiles()
    {
        _output.WriteLine("### TEST: List Workspace After Clone ###\n");
        
        // Step 1: Clone
        var gitService = new GitService(_loggerFactory.CreateLogger<GitService>());
        var cloneTool = new GitWorkspaceTool(
            "git_clone", 
            "Clone repository", 
            new[] { 1, 2, 3 }, 
            gitService, 
            _loggerFactory.CreateLogger<GitWorkspaceTool>());

        var cloneArgs = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["url"] = "https://github.com/iwaitu/CleanApp.git"
        };

        _output.WriteLine("Step 1: Cloning repository...");
        var cloneResult = await cloneTool.ExecuteAsync(cloneArgs);
        _output.WriteLine($"Clone status: {cloneResult.Status}");
        _output.WriteLine($"Clone output: {cloneResult.Output}\n");
        
        if (cloneResult.Status != ToolResultStatus.Success)
        {
            _output.WriteLine("Clone failed - skipping rest of test");
            return;
        }

        // Step 2: List using workspace path
        _output.WriteLine("Step 2: Listing workspace root...");
        var listTool = new ListWorkspaceTool(_loggerFactory.CreateLogger<ListWorkspaceTool>());
        
        var listArgs1 = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["path"] = ".",
            ["recursive"] = false
        };

        var listResult1 = await listTool.ExecuteAsync(listArgs1);
        _output.WriteLine($"List workspace root status: {listResult1.Status}");
        _output.WriteLine($"List workspace root output:\n{listResult1.Output}\n");

        // Step 3: List cloned project directory
        _output.WriteLine("Step 3: Listing cloned project directory...");
        var listArgs2 = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["path"] = "CleanApp",
            ["recursive"] = false
        };

        var listResult2 = await listTool.ExecuteAsync(listArgs2);
        _output.WriteLine($"List project status: {listResult2.Status}");
        _output.WriteLine($"List project output:\n{listResult2.Output}\n");

        // Step 4: Direct filesystem check
        _output.WriteLine("Step 4: Direct filesystem verification...");
        var repoPath = Path.Combine(_testWorkspace, "CleanApp");
        
        if (Directory.Exists(repoPath))
        {
            var allFiles = Directory.GetFiles(repoPath, "*.*", SearchOption.AllDirectories);
            var allDirs = Directory.GetDirectories(repoPath, "*", SearchOption.AllDirectories);
            
            _output.WriteLine($"Direct check: {allFiles.Length} files, {allDirs.Length} directories");
            _output.WriteLine("\nTop-level contents:");
            
            foreach (var file in Directory.GetFiles(repoPath))
            {
                _output.WriteLine($"  [FILE] {Path.GetFileName(file)}");
            }
            
            foreach (var dir in Directory.GetDirectories(repoPath))
            {
                _output.WriteLine($"  [DIR]  {Path.GetFileName(dir)}");
            }
        }
        else
        {
            _output.WriteLine($"ERROR: Project directory does not exist at {repoPath}");
        }

        _output.WriteLine("\n### TEST COMPLETED ###");
    }

    [Fact]
    public async Task Diagnostic_PathResolution_VerifyAllTools()
    {
        _output.WriteLine("### TEST: Path Resolution Verification for All Tools ###\n");
        
        // Create test structure
        var testDir = Path.Combine(_testWorkspace, "testdir");
        Directory.CreateDirectory(testDir);
        await File.WriteAllTextAsync(Path.Combine(testDir, "file1.txt"), "content1");
        await File.WriteAllTextAsync(Path.Combine(testDir, "file2.cs"), "content2");
        
        _output.WriteLine($"Test workspace: {_testWorkspace}");
        _output.WriteLine($"Test directory: testdir");
        _output.WriteLine("Test files: file1.txt, file2.cs\n");

        // Test 1: ListWorkspaceTool
        _output.WriteLine("=== Test 1: ListWorkspaceTool ===");
        var listTool = new ListWorkspaceTool(_loggerFactory.CreateLogger<ListWorkspaceTool>());
        var listResult = await listTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["path"] = "testdir",
            ["recursive"] = false
        });
        _output.WriteLine($"Status: {listResult.Status}");
        _output.WriteLine($"Output:\n{listResult.Output}\n");
        Assert.Equal(ToolResultStatus.Success, listResult.Status);
        Assert.Contains("file1.txt", listResult.Output);

        // Test 2: ReadFileTool
        _output.WriteLine("=== Test 2: ReadFileTool ===");
        var readTool = new ReadFileTool(_loggerFactory.CreateLogger<ReadFileTool>());
        var readResult = await readTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["path"] = "testdir/file1.txt"
        });
        _output.WriteLine($"Status: {readResult.Status}");
        _output.WriteLine($"Output:\n{readResult.Output}\n");
        Assert.Equal(ToolResultStatus.Success, readResult.Status);
        Assert.Contains("content1", readResult.Output);

        // Test 3: WriteFileTool
        _output.WriteLine("=== Test 3: WriteFileTool ===");
        var writeTool = new WriteFileTool(_loggerFactory.CreateLogger<WriteFileTool>());
        var writeResult = await writeTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["path"] = "testdir/newfile.txt",
            ["content"] = "new content"
        });
        _output.WriteLine($"Status: {writeResult.Status}");
        _output.WriteLine($"Output:\n{writeResult.Output}\n");
        Assert.Equal(ToolResultStatus.Success, writeResult.Status);
        Assert.True(File.Exists(Path.Combine(_testWorkspace, "testdir", "newfile.txt")));

        // Test 4: SearchInFilesTool
        _output.WriteLine("=== Test 4: SearchInFilesTool ===");
        var searchTool = new SearchInFilesTool(_loggerFactory.CreateLogger<SearchInFilesTool>());
        var searchResult = await searchTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["pattern"] = "content",
            ["path"] = "testdir",
            ["file_pattern"] = "*.txt"
        });
        _output.WriteLine($"Status: {searchResult.Status}");
        _output.WriteLine($"Output:\n{searchResult.Output}\n");
        Assert.Equal(ToolResultStatus.Success, searchResult.Status);

        // Test 5: DeleteFileTool
        _output.WriteLine("=== Test 5: DeleteFileTool ===");
        var deleteTool = new DeleteFileTool(_loggerFactory.CreateLogger<DeleteFileTool>());
        var deleteResult = await deleteTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["path"] = "testdir/newfile.txt"
        });
        _output.WriteLine($"Status: {deleteResult.Status}");
        _output.WriteLine($"Output:\n{deleteResult.Output}\n");
        Assert.Equal(ToolResultStatus.Success, deleteResult.Status);
        Assert.False(File.Exists(Path.Combine(_testWorkspace, "testdir", "newfile.txt")));

        _output.WriteLine("### ALL TOOLS PATH RESOLUTION: PASSED ###");
    }

    private void PrintDirectoryTree(string path, string indent, int depth)
    {
        if (depth > 3) return; // Limit depth to avoid too much output
        
        try
        {
            var dirInfo = new DirectoryInfo(path);
            _output.WriteLine($"{indent}[DIR] {dirInfo.Name}/");
            
            foreach (var file in dirInfo.GetFiles())
            {
                _output.WriteLine($"{indent}  [FILE] {file.Name}");
            }
            
            foreach (var dir in dirInfo.GetDirectories())
            {
                PrintDirectoryTree(dir.FullName, indent + "  ", depth + 1);
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"{indent}  <Error: {ex.Message}>");
        }
    }
}
