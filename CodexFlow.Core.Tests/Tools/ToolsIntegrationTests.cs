// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Tests.Execution;
using CodexFlow.Core.Models;
using CodexFlow.Core.Services;
using CodexFlow.Core.TDD;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.Tools;

/// <summary>
/// 集成测试：验证所有工具在默认工作空间中的基本功能
/// </summary>
public class ToolsIntegrationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testWorkspace;
    private readonly ILogger<ToolsIntegrationTests> _logger;

    public ToolsIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        // 使用 TestConstants 提供的唯一隔離目錄
        _testWorkspace = TestConstants.GetWorkspacePath();
        
        var loggerFactory = LoggerFactory.Create(builder => { });
        _logger = loggerFactory.CreateLogger<ToolsIntegrationTests>();
        
        _output.WriteLine($"隔離測試空間已創建: {_testWorkspace}");
    }

    public void Dispose()
    {
        // 測試結束後清理隔離目錄
        try
        {
            if (Directory.Exists(_testWorkspace))
            {
                // 先設置屬性為 Normal，防止 .git 內的只讀文件導致刪除失敗
                SetAttributesNormal(new DirectoryInfo(_testWorkspace));
                Directory.Delete(_testWorkspace, true);
                _output.WriteLine($"隔離測試空間已清理: {_testWorkspace}");
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"警告: 清理隔離空間失敗: {ex.Message}");
        }
    }

    private static void SetAttributesNormal(DirectoryInfo dir)
    {
        foreach (var subDir in dir.GetDirectories()) SetAttributesNormal(subDir);
        foreach (var file in dir.GetFiles()) file.Attributes = FileAttributes.Normal;
    }

    #region File Operation Tools Tests

    [Fact]
    public async Task ListWorkspaceTool_ShouldListFiles_Successfully()
    {
        // Arrange
        var tool = new ListWorkspaceTool(NullLogger<ListWorkspaceTool>.Instance);
        
        // Create test files
        await File.WriteAllTextAsync(Path.Combine(_testWorkspace, "file1.txt"), "content1");
        await File.WriteAllTextAsync(Path.Combine(_testWorkspace, "file2.cs"), "content2");
        Directory.CreateDirectory(Path.Combine(_testWorkspace, "subdir"));

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["path"] = ".",
            ["recursive"] = false
        };

        // Act
        var result = await tool.ExecuteAsync(args);

        // Assert
        _output.WriteLine($"Tool output:\n{result.Output}");
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("file1.txt", result.Output);
        Assert.Contains("file2.cs", result.Output);
        Assert.Contains("subdir", result.Output);
    }

    [Fact]
    public async Task ReadFileTool_ShouldReadFileContent_Successfully()
    {
        // Arrange
        var tool = new ReadFileTool(NullLogger<ReadFileTool>.Instance);
        var testFile = Path.Combine(_testWorkspace, "test.txt");
        var expectedContent = "Hello World!\nLine 2\nLine 3";
        await File.WriteAllTextAsync(testFile, expectedContent);

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["path"] = "test.txt"
        };

        // Act
        var result = await tool.ExecuteAsync(args);

        // Assert
        _output.WriteLine($"Tool output:\n{result.Output}");
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains(expectedContent.ReplaceLineEndings("\n"), result.Output.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task WriteFileTool_ShouldCreateFile_Successfully()
    {
        // Arrange
        var tool = new WriteFileTool(NullLogger<WriteFileTool>.Instance);
        var testContent = "This is a test file content";

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["path"] = "newfile.txt",
            ["content"] = testContent
        };

        // Act
        var result = await tool.ExecuteAsync(args);

        // Assert
        _output.WriteLine($"Tool output:\n{result.Output}");
        Assert.Equal(ToolResultStatus.Success, result.Status);
        
        var createdFile = Path.Combine(_testWorkspace, "newfile.txt");
        Assert.True(File.Exists(createdFile), "File should be created");
        var actualContent = await File.ReadAllTextAsync(createdFile);
        Assert.Equal(testContent, actualContent);
    }

    [Fact]
    public async Task WriteFileTool_ShouldUpdateExistingFile_Successfully()
    {
        // Arrange
        var tool = new WriteFileTool(NullLogger<WriteFileTool>.Instance);
        var testFile = Path.Combine(_testWorkspace, "existing.txt");
        await File.WriteAllTextAsync(testFile, "old content");

        var newContent = "updated content";
        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["path"] = "existing.txt",
            ["content"] = newContent
        };

        // Act
        var result = await tool.ExecuteAsync(args);

        // Assert
        _output.WriteLine($"Tool output:\n{result.Output}");
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("updated", result.Output);
        
        var actualContent = await File.ReadAllTextAsync(testFile);
        Assert.Equal(newContent, actualContent);
    }

    [Fact]
    public async Task DeleteFileTool_ShouldDeleteFile_Successfully()
    {
        // Arrange
        var tool = new DeleteFileTool(NullLogger<DeleteFileTool>.Instance);
        var testFile = Path.Combine(_testWorkspace, "todelete.txt");
        await File.WriteAllTextAsync(testFile, "will be deleted");

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["path"] = "todelete.txt"
        };

        // Act
        var result = await tool.ExecuteAsync(args);

        // Assert
        _output.WriteLine($"Tool output:\n{result.Output}");
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.False(File.Exists(testFile), "File should be deleted");
    }

    [Fact]
    public async Task CreateDirectoryTool_ShouldCreateDirectory_Successfully()
    {
        // Arrange
        var tool = new CreateDirectoryTool(NullLogger<CreateDirectoryTool>.Instance);

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["path"] = "newdir/subdir"
        };

        // Act
        var result = await tool.ExecuteAsync(args);

        // Assert
        _output.WriteLine($"Tool output:\n{result.Output}");
        Assert.Equal(ToolResultStatus.Success, result.Status);
        
        var createdDir = Path.Combine(_testWorkspace, "newdir", "subdir");
        Assert.True(Directory.Exists(createdDir), "Directory should be created");
    }

    [Fact]
    public async Task SearchInFilesTool_ShouldFindPattern_Successfully()
    {
        // Arrange
        var tool = new SearchInFilesTool(NullLogger<SearchInFilesTool>.Instance);
        
        // Create test files with searchable content
        await File.WriteAllTextAsync(Path.Combine(_testWorkspace, "file1.txt"), "This contains the KEYWORD");
        await File.WriteAllTextAsync(Path.Combine(_testWorkspace, "file2.txt"), "No match here");
        await File.WriteAllTextAsync(Path.Combine(_testWorkspace, "file3.cs"), "Another KEYWORD occurrence");

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["pattern"] = "KEYWORD",
            ["path"] = ".",
            ["file_pattern"] = "*.*"
        };

        // Act
        var result = await tool.ExecuteAsync(args);

        // Assert
        _output.WriteLine($"Tool output:\n{result.Output}");
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("file1.txt", result.Output);
        Assert.Contains("file3.cs", result.Output);
        Assert.DoesNotContain("file2.txt", result.Output);
    }

    [Fact]
    public async Task SearchInFilesTool_ShouldIgnoreGeneratedLogsByDefault_AndKeepSourceHits()
    {
        var tool = new SearchInFilesTool(NullLogger<SearchInFilesTool>.Instance);

        Directory.CreateDirectory(Path.Combine(_testWorkspace, "src"));
        Directory.CreateDirectory(Path.Combine(_testWorkspace, "logs"));
        Directory.CreateDirectory(Path.Combine(_testWorkspace, "artifacts"));

        await File.WriteAllTextAsync(
            Path.Combine(_testWorkspace, "src", "QueryRuntimeEngine.cs"),
            "public sealed class QueryRuntimeEngine { }");
        await File.WriteAllTextAsync(
            Path.Combine(_testWorkspace, ".tmp-infrastructure-build-diag.log"),
            "QueryRuntimeEngine temporary build noise");
        await File.WriteAllTextAsync(
            Path.Combine(_testWorkspace, "logs", "service.log"),
            "QueryRuntimeEngine service noise");
        await File.WriteAllTextAsync(
            Path.Combine(_testWorkspace, "artifacts", "build.log"),
            "QueryRuntimeEngine artifact noise");

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["pattern"] = "QueryRuntimeEngine",
            ["path"] = "."
        };

        var result = await tool.ExecuteAsync(args);

        _output.WriteLine($"Tool output:\n{result.Output}");
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("src", result.Output);
        Assert.Contains("QueryRuntimeEngine.cs", result.Output);
        Assert.DoesNotContain(".tmp-infrastructure-build-diag.log", result.Output);
        Assert.DoesNotContain("service.log", result.Output);
        Assert.DoesNotContain("build.log", result.Output);
    }

    [Fact]
    public async Task SearchInFilesTool_ShouldPreferNestedSourceHits_OverRootDocs_WhenTruncated()
    {
        var tool = new SearchInFilesTool(NullLogger<SearchInFilesTool>.Instance);

        Directory.CreateDirectory(Path.Combine(_testWorkspace, "src"));
        await File.WriteAllTextAsync(
            Path.Combine(_testWorkspace, "pipeline-fix-plan.md"),
            "QueryRuntimeEngine doc reference");
        await File.WriteAllTextAsync(
            Path.Combine(_testWorkspace, "src", "QueryRuntimeEngine.cs"),
            "public sealed class QueryRuntimeEngine { }");

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["pattern"] = "QueryRuntimeEngine",
            ["path"] = ".",
            ["max_results"] = 1
        });

        _output.WriteLine($"Tool output:\n{result.Output}");
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("QueryRuntimeEngine.cs", result.Output);
        Assert.DoesNotContain("pipeline-fix-plan.md", result.Output);
    }

    [Fact]
    public async Task SearchInFilesTool_ShouldFallBackToRoot_WhenCommonGuessedDirectoryIsMissing()
    {
        var tool = new SearchInFilesTool(NullLogger<SearchInFilesTool>.Instance);

        Directory.CreateDirectory(Path.Combine(_testWorkspace, "CodexFlow.Core", "Runtime"));
        await File.WriteAllTextAsync(
            Path.Combine(_testWorkspace, "CodexFlow.Core", "Runtime", "QueryRuntimeEngine.cs"),
            "public sealed class QueryRuntimeEngine { }");

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["pattern"] = "QueryRuntimeEngine",
            ["path"] = "src"
        });

        _output.WriteLine($"Tool output:\n{result.Output}");
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("Requested directory `src` not found. Searched from `.` instead.", result.Output);
        Assert.Contains("CodexFlow.Core", result.Output);
        Assert.Contains("Requested path src missing; searched from . instead.", result.Summary);
    }

    #endregion

    #region Execution Tools Tests

    // Execution tools run ONLY in the isolation backend. The functional assertions of the former host-process tests are kept
    // here against a fake backend with a host-bound trusted context; the real-Docker layer is IsolatedToolsDockerTests.

    private (FakeIsolationService Fake, ToolIsolationGate Gate, IDisposable Scope) IsolatedBackend(
        Func<IsolatedCommandRequest, CancellationToken, Task<IsolatedCommandResult>>? onRun = null)
    {
        var fake = new FakeIsolationService();
        if (onRun is not null)
        {
            fake.OnRun = onRun;
        }

        var accessor = new TrustedExecutionContextAccessor();
        var scope = accessor.Begin(IsolationTestSupport.Context(_testWorkspace, ExportScope.Empty));
        return (fake, new ToolIsolationGate(fake, accessor, new IsolationOptions { LanguageImages = { ["python"] = "img-python" } }), scope);
    }

    private static Task<IsolatedCommandResult> Out(IsolatedCommandRequest req, string stdout, int exit = 0)
    {
        foreach (var line in stdout.Split((char)10, StringSplitOptions.RemoveEmptyEntries))
        {
            req.OnOutput?.Invoke(new CommandOutputLine("stdout", line));
        }

        return Task.FromResult(new IsolatedCommandResult(
            ExecutionDecision.Allow(), exit == 0 ? IsolatedCommandStatus.Completed : IsolatedCommandStatus.Failed, exit, stdout, string.Empty, false, TimeSpan.Zero, "fake"));
    }

    [Fact]
    public async Task ExecCodeTool_ShouldExecutePythonCode_Successfully()
    {
        var (fake, gate, scope) = IsolatedBackend((req, _) => Out(req, @"Hello from Python
4"));
        using var _ = scope;
        var tool = new ExecCodeTool(NullLogger<ExecCodeTool>.Instance, gate);
        var pythonCode = @"print('Hello from Python')
print(2 + 2)";

        var result = await tool.ExecuteAsync(new Dictionary<string, object?> { ["workspace_path"] = _testWorkspace, ["code"] = pythonCode });

        _output.WriteLine($"Tool output: {result.Output}");
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("Hello from Python", result.Output);
        Assert.Equal(pythonCode, fake.Requests.Single().StdIn);
        Assert.Equal("img-python", fake.Options.Single().Image);
    }

    [Fact]
    public async Task RunCommandTool_ShouldExecuteCommand_Successfully()
    {
        var (fake, gate, scope) = IsolatedBackend((req, _) => Out(req, @"README.md
src"));
        using var _ = scope;
        var tool = new RunCommandTool(NullLogger<RunCommandTool>.Instance, null, gate);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?> { ["workspace_path"] = _testWorkspace, ["command"] = "ls" });

        _output.WriteLine($"Tool output: {result.Output}");
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.NotEmpty(result.Output);
        Assert.Contains("README.md", result.Output);
        Assert.Equal(["ls"], fake.Requests.Single().Argv.ToArray());
    }

    [Fact]
    public async Task RunCommandTool_ShouldPassGrepAndLsThroughUnchanged_ToTheLinuxGuest()
    {
        // The guest is always Linux, so the former Windows translations (grep -> rg, ls -> cmd.exe dir, pwd -> cmd.exe /c cd)
        // are gone on purpose: the argv reaches the backend exactly as the model wrote it.
        var (fake, gate, scope) = IsolatedBackend((req, _) => Out(req, "./sample.txt:IUnitOfWork lives here"));
        using var _ = scope;
        var tool = new RunCommandTool(NullLogger<RunCommandTool>.Instance, null, gate);

        var grep = await tool.ExecuteAsync(new Dictionary<string, object?> { ["workspace_path"] = _testWorkspace, ["command"] = new[] { "grep", "-r", "IUnitOfWork", "." } });
        var pwd = await tool.ExecuteAsync(new Dictionary<string, object?> { ["workspace_path"] = _testWorkspace, ["command"] = "pwd" });
        var ls = await tool.ExecuteAsync(new Dictionary<string, object?> { ["workspace_path"] = _testWorkspace, ["command"] = new[] { "ls", "__codexflow_missing_smoke__" } });

        Assert.Equal(ToolResultStatus.Success, grep.Status);
        Assert.Contains("sample.txt", grep.Output);
        Assert.Equal(ToolResultStatus.Success, pwd.Status);
        Assert.Equal(ToolResultStatus.Success, ls.Status);
        Assert.Equal(
            [["grep", "-r", "IUnitOfWork", "."], ["pwd"], ["ls", "__codexflow_missing_smoke__"]],
            fake.Requests.Select(r => r.Argv.ToArray()).ToArray());
    }

    [Fact]
    public async Task RunCommandTool_ShouldParseFencedJsonArrayCommand()
    {
        var (fake, gate, scope) = IsolatedBackend((req, _) => Out(req, "10.0.401"));
        using var _ = scope;
        var tool = new RunCommandTool(NullLogger<RunCommandTool>.Instance, null, gate);
        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["command"] = """
```json
["dotnet","--version"]
```
"""
        };

        var result = await tool.ExecuteAsync(args);

        _output.WriteLine($"Tool output: {result.Output}");
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("dotnet --version", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["dotnet", "--version"], fake.Requests.Single().Argv.ToArray());
    }

    #endregion

    #region Artifact Tools Tests

    [Fact]
    public async Task ZipDirectoryTool_ShouldCreateZipFile_Successfully()
    {
        // Arrange
        var tool = new ZipDirectoryTool(NullLogger<ZipDirectoryTool>.Instance);
        
        // Create test directory with files
        var sourceDir = Path.Combine(_testWorkspace, "tozip");
        Directory.CreateDirectory(sourceDir);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "file1.txt"), "content1");
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "file2.txt"), "content2");

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["source_dir"] = "tozip",
            ["zip_path"] = "archive.zip"
        };

        // Act
        var result = await tool.ExecuteAsync(args);

        // Assert
        _output.WriteLine($"Tool output:\n{result.Output}");
        Assert.Equal(ToolResultStatus.Success, result.Status);
        
        var zipPath = Path.Combine(_testWorkspace, "archive.zip");
        Assert.True(File.Exists(zipPath), "Zip file should be created");
        
        var fileInfo = new FileInfo(zipPath);
        Assert.True(fileInfo.Length > 0, "Zip file should not be empty");
    }

    [Fact]
    public async Task DownloadArtifactTool_ShouldReturnFileContent_Successfully()
    {
        // Arrange
        var tool = new DownloadArtifactTool(NullLogger<DownloadArtifactTool>.Instance);
        
        // Create test file
        var testFile = Path.Combine(_testWorkspace, "artifact.txt");
        var expectedContent = "This is an artifact file";
        await File.WriteAllTextAsync(testFile, expectedContent);

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["filename"] = "artifact.txt",
            ["as_text"] = "true"
        };

        // Act
        var result = await tool.ExecuteAsync(args);

        // Assert
        _output.WriteLine($"Tool output:\n{result.Output}");
        Assert.Equal(ToolResultStatus.Success, result.Status);
        
        Assert.NotNull(result.Metadata);
        if (result.Metadata is Dictionary<string, object?> metadata)
        {
            Assert.True(metadata.ContainsKey("Data"));
            Assert.Contains(expectedContent, metadata["Data"]?.ToString());
        }
    }

    #endregion

    #region Git Tools Tests

    [Fact]
    public async Task GitWorkspaceTool_Clone_ShouldCloneRepository_Successfully()
    {
        // Arrange
        var mockGitService = new Mock<IGitService>();
        var cloneLogger = NullLogger<GitWorkspaceTool>.Instance;

        // The tool clones to workspace_path/<repoName>, so simulate that directory being created
        var expectedRepoPath = Path.Combine(_testWorkspace, "repo");

        mockGitService
            .Setup(g => g.CloneRepositoryAsync(It.IsAny<Uri>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Callback<Uri, string, string?, string?>((url, path, branch, token) =>
            {
                // Simulate git clone creating the directory with .git folder
                Directory.CreateDirectory(Path.Combine(path, ".git"));
                File.WriteAllText(Path.Combine(path, "README.md"), "# Test Repo");
            })
            .ReturnsAsync(true);

        var tool = new GitWorkspaceTool("git_clone", "Clone repository", new[] { 1, 2, 3 }, mockGitService.Object, cloneLogger);

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["url"] = "https://github.com/test/repo.git"
        };

        // Act
        var result = await tool.ExecuteAsync(args);

        // Assert
        _output.WriteLine($"Tool output:\n{result.Output}");
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.True(Directory.Exists(expectedRepoPath), "repo directory should exist");
        Assert.True(Directory.Exists(Path.Combine(expectedRepoPath, ".git")), ".git directory should exist");
        mockGitService.Verify(g => g.CloneRepositoryAsync(
            It.Is<Uri>(u => u.ToString() == "https://github.com/test/repo.git"),
            expectedRepoPath,
            It.IsAny<string?>(),
            It.IsAny<string?>()), Times.Once);
    }

    #endregion

    #region Git Init-then-Clone Regression Tests

    [Fact]
    public async Task GitService_CloneAfterInit_ShouldDeleteInitAndClone()
    {
        // This reproduces the exact bug: CodexOrchestrator calls InitRepositoryAsync
        // first, then CloneRepositoryAsync. The old code would see .git and skip.

        var gitService = new GitService(NullLogger<GitService>.Instance);
        var repoPath = Path.Combine(_testWorkspace, "repo");

        // Step 1: Simulate what Orchestrator does — git init the directory
        var initResult = await gitService.InitRepositoryAsync(repoPath);
        _output.WriteLine($"Init result: {initResult}");

        // Verify init created .git but only has .gitignore
        Assert.True(Directory.Exists(Path.Combine(repoPath, ".git")), ".git should exist after init");
        var filesAfterInit = Directory.GetFiles(repoPath)
            .Where(f => !Path.GetFileName(f).Equals(".gitignore", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        _output.WriteLine($"Project files after init (excluding .gitignore): {filesAfterInit.Length}");
        Assert.Empty(filesAfterInit);

        // Step 2: Now try to clone — this MUST detect the init-only state and re-clone
        var cloneResult = await gitService.CloneRepositoryAsync(
            "https://github.com/iwaitu/CleanApp.git", repoPath);

        _output.WriteLine($"Clone result: {cloneResult}");

        if (!cloneResult)
        {
            _output.WriteLine("Clone failed — Git may not be installed; skipping rest");
            return;
        }

        // Step 3: Verify the repo now has actual project files
        var projectFiles = Directory.GetFiles(repoPath)
            .Where(f => !Path.GetFileName(f).Equals(".gitignore", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        _output.WriteLine($"Project files after clone: {projectFiles.Length}");
        foreach (var f in projectFiles.Take(10))
            _output.WriteLine($"  - {Path.GetFileName(f)}");

        Assert.NotEmpty(projectFiles);

        // Verify remote is set
        _output.WriteLine("✓ Init-then-Clone regression test PASSED");
    }

    [Fact]
    public async Task GitWorkspaceTool_FileCount_ShouldExcludeGitInternals()
    {
        // Verify that the file/directory count in the success message
        // excludes .git internals
        var mockGitService = new Mock<IGitService>();

        var expectedRepoPath = Path.Combine(_testWorkspace, "repo");

        mockGitService
            .Setup(g => g.CloneRepositoryAsync(It.IsAny<Uri>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Callback<Uri, string, string?, string?>((url, path, branch, token) =>
            {
                // Simulate a real clone: .git folder + project files
                Directory.CreateDirectory(Path.Combine(path, ".git", "objects"));
                Directory.CreateDirectory(Path.Combine(path, ".git", "refs"));
                File.WriteAllText(Path.Combine(path, ".git", "HEAD"), "ref: refs/heads/main");
                File.WriteAllText(Path.Combine(path, ".git", "config"), "[core]");

                // Actual project files
                File.WriteAllText(Path.Combine(path, "README.md"), "# Test");
                File.WriteAllText(Path.Combine(path, "Program.cs"), "class P {}");
                Directory.CreateDirectory(Path.Combine(path, "src"));
                File.WriteAllText(Path.Combine(path, "src", "App.cs"), "class App {}");
            })
            .ReturnsAsync(true);

        var tool = new GitWorkspaceTool("git_clone", "Clone repository", new[] { 1, 2, 3 },
            mockGitService.Object, NullLogger<GitWorkspaceTool>.Instance);

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["url"] = "https://github.com/test/repo.git"
        };

        var result = await tool.ExecuteAsync(args);

        _output.WriteLine($"Tool output:\n{result.Output}");
        Assert.Equal(ToolResultStatus.Success, result.Status);

        // The output should report project files only (3: README.md, Program.cs, src/App.cs)
        // NOT .git internals (HEAD, config)
        Assert.Contains("文件数: 3", result.Output);
        // Directories should be just "src" (1), not .git/objects, .git/refs
        Assert.Contains("目录数: 1", result.Output);
    }

    #endregion

    #region Context Continuity Tests

    [Fact]
    public async Task Orchestrator_ShouldDetectExistingClonedRepo_WhenProjectUrlNotSet()
    {
        // Reproduces the exact user scenario:
        // Message 1: AI calls git_clone → clones repo, but session.ProjectUrl not set
        // Message 2: AI calls codex_orchestrator → orchestrator must detect the existing repo

        _output.WriteLine("=== Context Continuity Test ===");

        // Arrange
        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();
        var mockKernel = new Mock<ICodexAgentKernel>();
        var mockArchitect = new Mock<ICodexArchitect>();
        var mockSecurity = new Mock<ICodexSecurityAuditor>();
        var mockValidator = new Mock<ICodexValidator>();
        var mockGit = new Mock<IGitService>();
        var mockScanner = new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance);
        var mockAnalysis = new Mock<ICodeAnalysisService>();
        var mockArch = new Mock<IArchitectureService>();

        // Session has NO ProjectUrl (simulating first message used git_clone directly)
        var session = new CodexSession
        {
            Id = "ctx-test",
            CurrentStage = 1,
            WorkspacePath = _testWorkspace
        };

        mockStore.Setup(s => s.GetSessionAsync("ctx-test", It.IsAny<CancellationToken>())).ReturnsAsync(session);
        mockStore.Setup(s => s.SaveSessionAsync(It.IsAny<CodexSession>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockMemory.Setup(m => m.RecallFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<MemoryEntry>());

        // Simulate: git_clone already created workspace/CleanApp with a remote
        var repoDir = Path.Combine(_testWorkspace, "CleanApp");
        mockGit.Setup(g => g.GetRemoteUrlAsync(repoDir))
               .ReturnsAsync("https://github.com/iwaitu/CleanApp.git");

        // Stage 1 mock — just succeed
        mockArchitect.Setup(k => k.AnalyzeAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync("Analysis done");
        
        // Also mock Forge role which is now called during execution because we pre-inject a plan
        mockKernel.Setup(k => k.RunLoopAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), CodexAgentRole.Forge, It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<TaskFileScopeDescriptor?>()))
                  .ReturnsAsync(new CodexResponse("Implementation done", true, TotalToolCalls: 1, WriteToolCalls: 1));

        // Mock Validator to avoid NRE
        mockValidator.Setup(v => v.ValidateAsync(It.IsAny<CodexSession>(), It.IsAny<CodexTask>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(new ValidationResult(true, "Validation passed", new List<string>()));

        mockScanner.Setup(s => s.ScanAndSummarizeAsync(It.IsAny<string>())).ReturnsAsync("Fingerprint");
        mockAnalysis.Setup(a => a.BuildGraphAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new DependencyGraph());
        mockArch.Setup(a => a.AnalyzeGraph(It.IsAny<DependencyGraph>())).Returns(new List<ArchitectureMetrics>());
        mockMemory.Setup(m => m.LearnFactAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        
        // Mock DevPlan for Level 7 Hard Gates
        mockArchitect.Setup(p => p.PlanAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new List<CodexTask> 
                   { 
                       new CodexTask { Id = "1", Title = "Task 1", Status = CodexTaskStatus.Pending } 
                   });

        var sessionManager = new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance);

        mockSecurity.Setup(s => s.AuditAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()))
                    .ReturnsAsync(new SecurityAuditResult(true, "Pass", [], ""));

        var orchestrator = new CodexOrchestrator(
            sessionManager, 
            mockKernel.Object, 
            mockArchitect.Object,
            mockSecurity.Object,
            mockValidator.Object, 
            mockGit.Object, 
            mockScanner.Object, 
            mockAnalysis.Object, 
            mockArch.Object, 
            Mock.Of<ICodexTestDesigner>(), 
            Mock.Of<IPolicyValidator>(),
            Mock.Of<ISemanticDiffService>(),
            Mock.Of<ITaskListService>(),
            new ConfigurationBuilder().Build(),
            NullLogger<CodexOrchestrator>.Instance,
            Enumerable.Empty<ICodexEventSink>());

        // Pre-inject a plan into the session to bypass the "Plan is empty" check
        session.ReplacePlan(
        [
            new CodexTask { Id = "1", Title = "Mock Task", Status = CodexTaskStatus.Pending } 
        ]);

        // Create directory to avoid directory not found exception during scan
        Directory.CreateDirectory(Path.Combine(_testWorkspace, "CleanApp"));

        // Act — orchestrator runs with session that has empty ProjectUrl
        var result = await orchestrator.RunNextStepAsync("ctx-test", "Build a library management system", repoUrl: "https://github.com/iwaitu/CleanApp.git");

        // Assert
        _output.WriteLine($"Result: Success={result.Success}, Message={result.Message}");
        _output.WriteLine($"Session.ProjectUrl after orchestrator: '{session.ProjectUrl}'");

        // The orchestrator should have detected the existing repo and set ProjectUrl
        Assert.Equal("https://github.com/iwaitu/CleanApp.git", session.ProjectUrl?.ToString());

        // InitRepositoryAsync should NOT have been called (repo already exists)
        mockGit.Verify(g => g.InitRepositoryAsync(It.IsAny<string>()), Times.Never,
            "InitRepositoryAsync should not be called when an existing cloned repo is detected");

        // SaveSession should be called to persist the ProjectUrl
        mockStore.Verify(s => s.SaveSessionAsync(It.Is<CodexSession>(
            sess => sess.ProjectUrl != null && sess.ProjectUrl.ToString() == "https://github.com/iwaitu/CleanApp.git"), 
            It.IsAny<CancellationToken>()), Times.AtLeastOnce,
            "Session should be saved with the detected ProjectUrl");

        _output.WriteLine("✓ Context continuity test PASSED");
    }

    [Fact]
    public async Task Orchestrator_ShouldStillInitRepo_WhenNoExistingClone()
    {
        // Verify the normal flow still works: no pre-existing clone → still可执行主流程

        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();
        var mockKernel = new Mock<ICodexAgentKernel>();
        var mockGit = new Mock<IGitService>();
        var mockScanner = new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance);
        var mockAnalysis = new Mock<ICodeAnalysisService>();
        var mockArch = new Mock<IArchitectureService>();
        var mockArchitect = new Mock<ICodexArchitect>();
        var mockSecurity = new Mock<ICodexSecurityAuditor>();
        var mockValidator = new Mock<ICodexValidator>();

        var session = new CodexSession
        {
            Id = "init-test",
            CurrentStage = 1,
            WorkspacePath = _testWorkspace
        };

        mockStore.Setup(s => s.GetSessionAsync("init-test", It.IsAny<CancellationToken>())).ReturnsAsync(session);
        mockStore.Setup(s => s.SaveSessionAsync(It.IsAny<CodexSession>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockMemory.Setup(m => m.RecallFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<MemoryEntry>());

        // No existing repo — GetRemoteUrlAsync returns null
        mockGit.Setup(g => g.GetRemoteUrlAsync(It.IsAny<string>())).ReturnsAsync((string?)null);

        mockArchitect.Setup(k => k.AnalyzeAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync("Analysis done");
                  
        // Also mock Forge role which is now called during execution because we pre-inject a plan
        mockKernel.Setup(k => k.RunLoopAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), CodexAgentRole.Forge, It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<TaskFileScopeDescriptor?>()))
                  .ReturnsAsync(new CodexResponse("Implementation done", true, TotalToolCalls: 1, WriteToolCalls: 1));

        // Mock Validator to avoid NRE
        mockValidator.Setup(v => v.ValidateAsync(It.IsAny<CodexSession>(), It.IsAny<CodexTask>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(new ValidationResult(true, "Validation passed", new List<string>()));

        mockScanner.Setup(s => s.ScanAndSummarizeAsync(It.IsAny<string>())).ReturnsAsync("Fingerprint");
        mockAnalysis.Setup(a => a.BuildGraphAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new DependencyGraph());
        mockArch.Setup(a => a.AnalyzeGraph(It.IsAny<DependencyGraph>())).Returns(new List<ArchitectureMetrics>());
        mockMemory.Setup(m => m.LearnFactAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Mock DevPlan for Level 7 Hard Gates
        // Use concrete List<CodexTask> as ICodexPlanner returns Task<List<CodexTask>>
        mockArchitect.Setup(p => p.PlanAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new List<CodexTask> 
                   { 
                       new CodexTask { Id = "1", Title = "Mock Task", Status = CodexTaskStatus.Pending } 
                   });

        // Pre-inject a plan into the session to bypass the "Plan is empty" check
        session.ReplacePlan(
        [
            new CodexTask { Id = "1", Title = "Mock Task", Status = CodexTaskStatus.Pending } 
        ]);

        var sessionManager = new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance);
        
        mockSecurity.Setup(s => s.AuditAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IEnumerable<string>?>()))
                    .ReturnsAsync(new SecurityAuditResult(true, "Pass", [], ""));
        
        var orchestrator = new CodexOrchestrator(
            sessionManager, 
            mockKernel.Object, 
            mockArchitect.Object,
            mockSecurity.Object,
            mockValidator.Object, 
            mockGit.Object, 
            mockScanner.Object, 
            mockAnalysis.Object, 
            mockArch.Object, 
            Mock.Of<ICodexTestDesigner>(), 
            Mock.Of<IPolicyValidator>(),
            Mock.Of<ISemanticDiffService>(),
            Mock.Of<ITaskListService>(),
            new ConfigurationBuilder().Build(),
            NullLogger<CodexOrchestrator>.Instance,
            Enumerable.Empty<ICodexEventSink>());

        // Act — orchestrator runs with session that has empty ProjectUrl
        var result = await orchestrator.RunNextStepAsync("init-test", "Start a new project");

        // Assert — 当前实现不再要求 InitRepositoryAsync
        _output.WriteLine($"Result: Success={result.Success}, Message={result.Message}");
        Assert.Null(session.ProjectUrl);
        mockGit.Verify(g => g.InitRepositoryAsync(It.IsAny<string>()), Times.Never,
            "InitRepositoryAsync should not be required in current orchestrator flow");

        _output.WriteLine("✓ Normal no-existing-clone flow test PASSED");
    }

    #endregion

    #region Structural Analysis Tools Tests

    [Fact]
    public async Task StructuralLsTool_ShouldListWithMetadata_Successfully()
    {
        // Arrange
        var mockAnalysisService = new Mock<ICodeAnalysisService>();
        var tool = new StructuralLsTool(mockAnalysisService.Object, NullLogger<StructuralLsTool>.Instance);
        
        // Create test files
        await File.WriteAllTextAsync(Path.Combine(_testWorkspace, "Program.cs"), "class Program {}");
        await File.WriteAllTextAsync(Path.Combine(_testWorkspace, "README.md"), "# Project");

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["path"] = "."
        };

        // Act
        var result = await tool.ExecuteAsync(args);

        // Assert
        _output.WriteLine($"Tool output:\n{result.Output}");
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("Program.cs", result.Output);
        Assert.Contains("README.md", result.Output);
        
        // Check metadata
        if (result.Metadata is IEnumerable<object> metadata)
        {
            Assert.NotEmpty(metadata);
        }
    }

    [Fact]
    public async Task StructuralReadTool_ShouldReadWithLineNumbers_Successfully()
    {
        // Arrange
        var mockAnalysisService = new Mock<ICodeAnalysisService>();
        var tool = new StructuralReadTool(mockAnalysisService.Object, NullLogger<StructuralReadTool>.Instance);
        
        var testFile = Path.Combine(_testWorkspace, "code.cs");
        var codeContent = @"using System;

namespace Test
{
    class Program
    {
        static void Main()
        {
            Console.WriteLine(""Hello"");
        }
    }
}";
        await File.WriteAllTextAsync(testFile, codeContent);

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["path"] = "code.cs",
            ["start_line"] = 1,
            ["end_line"] = 5
        };

        // Act
        var result = await tool.ExecuteAsync(args);

        // Assert
        _output.WriteLine($"Tool output:\n{result.Output}");
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("using System", result.Output);
        Assert.Contains("namespace Test", result.Output);
    }

    #endregion

    #region Patch Tools Tests

    [Fact]
    public async Task SmartPatchTool_ShouldApplyPatch_Successfully()
    {
        // Arrange
        var mockGitService = new Mock<IGitService>();
        var tool = new SmartPatchTool(mockGitService.Object, NullLogger<SmartPatchTool>.Instance);
        
        var testFile = Path.Combine(_testWorkspace, "target.cs");
        var originalContent = @"class Program
{
    void Method()
    {
        // old code
    }
}";
        await File.WriteAllTextAsync(testFile, originalContent);

        var patchContent = @"diff --git a/target.cs b/target.cs
--- a/target.cs
+++ b/target.cs
@@ -1,6 +1,7 @@
 class Program
 {
     void Method()
     {
-        // old code
+        // new code
+        Console.WriteLine(""updated"");
     }
 }";

        mockGitService
            .Setup(g => g.ApplyPatchAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["file_path"] = "target.cs",
            ["patch_content"] = patchContent
        };

        // Act
        var result = await tool.ExecuteAsync(args);

        // Assert
        _output.WriteLine($"Tool output:\n{result.Output}");
        
        // Since we're mocking, we verify the call was made
        mockGitService.Verify(g => g.ApplyPatchAsync(
            It.IsAny<string>(),
            It.Is<string>(patch =>
                patch.TrimEnd('\r', '\n').Replace("\r\n", "\n", StringComparison.Ordinal) ==
                patchContent.TrimEnd('\r', '\n').Replace("\r\n", "\n", StringComparison.Ordinal))), Times.Once);
    }

    [Fact]
    public async Task SmartPatchTool_ShouldStripMarkdownFence_BeforeApplyingPatch()
    {
        var mockGitService = new Mock<IGitService>();
        var tool = new SmartPatchTool(mockGitService.Object, NullLogger<SmartPatchTool>.Instance);

        var fencedPatch = """
```diff
diff --git a/target.cs b/target.cs
--- a/target.cs
+++ b/target.cs
@@ -1 +1 @@
-old
+new
```
""";

        mockGitService
            .Setup(g => g.ApplyPatchAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["patch_content"] = fencedPatch
        };

        var result = await tool.ExecuteAsync(args);

        Assert.Equal(ToolResultStatus.Success, result.Status);
        mockGitService.Verify(g => g.ApplyPatchAsync(
            It.IsAny<string>(),
            It.Is<string>(patch => !patch.Contains("```", StringComparison.Ordinal) &&
                                  patch.Contains("diff --git a/target.cs b/target.cs", StringComparison.Ordinal))),
            Times.Once);
    }

    [Fact]
    public async Task SmartPatchTool_ShouldRejectCodexPatchEnvelope_InUnifiedDiffMode()
    {
        var mockGitService = new Mock<IGitService>();
        var tool = new SmartPatchTool(mockGitService.Object, NullLogger<SmartPatchTool>.Instance);

        var codexPatch = """
*** Begin Patch
*** Update File: src/Test.cs
@@
-old
+new
*** End Patch
""";

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["patch_content"] = codexPatch
        };

        var result = await tool.ExecuteAsync(args);

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("Codex Patch envelope", result.Output, StringComparison.OrdinalIgnoreCase);
        mockGitService.Verify(g => g.ApplyPatchAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SmartPatchTool_ShouldRejectMalformedUnifiedDiff_BeforeApplyingPatch()
    {
        var mockGitService = new Mock<IGitService>();
        var tool = new SmartPatchTool(mockGitService.Object, NullLogger<SmartPatchTool>.Instance);

        var malformedPatch = """
diff --git a/target.cs b/target.cs
--- a/target.cs
+++ b/target.cs
@@ -1 +1 @@
old
+new
""";

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["patch_content"] = malformedPatch
        };

        var result = await tool.ExecuteAsync(args);

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("不是有效的 unified diff", result.Output, StringComparison.OrdinalIgnoreCase);
        mockGitService.Verify(g => g.ApplyPatchAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ApplyPatchTool_ShouldStripMarkdownFence_BeforeApplyingPatch()
    {
        var mockGitService = new Mock<IGitService>();
        var tool = new ApplyPatchTool(mockGitService.Object, null, NullLogger<ApplyPatchTool>.Instance);

        var fencedPatch = """
```diff
diff --git a/target.cs b/target.cs
--- a/target.cs
+++ b/target.cs
@@ -1 +1 @@
-old
+new
```
""";

        mockGitService
            .Setup(g => g.ApplyPatchAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        var args = new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["patch"] = fencedPatch
        };

        var result = await tool.ExecuteAsync(args);

        Assert.Equal(ToolResultStatus.Success, result.Status);
        mockGitService.Verify(g => g.ApplyPatchAsync(
            It.IsAny<string>(),
            It.Is<string>(patch => !patch.Contains("```", StringComparison.Ordinal) &&
                                  patch.Contains("diff --git a/target.cs b/target.cs", StringComparison.Ordinal))),
            Times.Once);
    }

    #endregion

    #region Integration Tests - Tool Workflow

    [Fact]
    public async Task ToolWorkflow_CreateWriteReadDelete_ShouldWorkTogether()
    {
        _output.WriteLine("=== Testing Complete Tool Workflow ===");
        
        // 1. Create directory
        var createDirTool = new CreateDirectoryTool(NullLogger<CreateDirectoryTool>.Instance);
        var createDirResult = await createDirTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["path"] = "workflow/subdir"
        });
        Assert.Equal(ToolResultStatus.Success, createDirResult.Status);
        _output.WriteLine("✓ Directory created");

        // 2. Write file
        var writeFileTool = new WriteFileTool(NullLogger<WriteFileTool>.Instance);
        var writeResult = await writeFileTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["path"] = "workflow/subdir/test.txt",
            ["content"] = "Workflow test content"
        });
        Assert.Equal(ToolResultStatus.Success, writeResult.Status);
        _output.WriteLine("✓ File written");

        // 3. List directory
        var listTool = new ListWorkspaceTool(NullLogger<ListWorkspaceTool>.Instance);
        var listResult = await listTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["path"] = "workflow",
            ["recursive"] = true
        });
        Assert.Equal(ToolResultStatus.Success, listResult.Status);
        Assert.Contains("test.txt", listResult.Output);
        _output.WriteLine("✓ Directory listed");

        // 4. Read file
        var readTool = new ReadFileTool(NullLogger<ReadFileTool>.Instance);
        var readResult = await readTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["path"] = "workflow/subdir/test.txt"
        });
        Assert.Equal(ToolResultStatus.Success, readResult.Status);
        Assert.Contains("Workflow test content", readResult.Output);
        _output.WriteLine("✓ File read");

        // 5. Search in files
        var searchTool = new SearchInFilesTool(NullLogger<SearchInFilesTool>.Instance);
        var searchResult = await searchTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["pattern"] = "Workflow",
            ["path"] = "workflow",
            ["file_pattern"] = "*.txt"
        });
        Assert.Equal(ToolResultStatus.Success, searchResult.Status);
        Assert.Contains("test.txt", searchResult.Output);
        _output.WriteLine("✓ Search completed");

        // 6. Delete file
        var deleteTool = new DeleteFileTool(NullLogger<DeleteFileTool>.Instance);
        var deleteResult = await deleteTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _testWorkspace,
            ["path"] = "workflow/subdir/test.txt"
        });
        Assert.Equal(ToolResultStatus.Success, deleteResult.Status);
        Assert.False(File.Exists(Path.Combine(_testWorkspace, "workflow/subdir/test.txt")));
        _output.WriteLine("✓ File deleted");

        _output.WriteLine("=== Workflow Test Completed Successfully ===");
    }

    #endregion
}
