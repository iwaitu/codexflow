// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CodexFlow.Core.Tests.Execution;

/// <summary>
/// Real-Docker layer of the tool contract tests: the same tools (exec_cmd, exec_code, command tasks, skill scripts) driven
/// through the real DockerIsolatedExecutionService. They fail, not skip, when Docker is unavailable.
/// </summary>
[Trait("Category", "DockerIsolation")]
[Collection(SafePublishCollection.Name)]
public class IsolatedToolsDockerTests : IDisposable
{
    private readonly string _root = IsolationTestSupport.NewTempDir("toolws");
    private readonly string _staging = IsolationTestSupport.NewTempDir("toolstg");
    private readonly DockerIsolatedExecutionService _service;
    private readonly TrustedExecutionContextAccessor _accessor = new();
    private readonly ToolIsolationGate _gate;

    public IsolatedToolsDockerTests()
    {
        var options = new IsolationOptions
        {
            PullMissingImages = true,
            StagingRoot = _staging,
            CommandImage = "alpine:3.20",
            LanguageImages = { ["python"] = "python:3.12-alpine" },
            ExecutableImages = { ["python"] = "python:3.12-alpine", ["python3"] = "python:3.12-alpine" }
        };
        _service = new DockerIsolatedExecutionService(new ProcessDockerCli(), Options.Create(options), NullLogger<DockerIsolatedExecutionService>.Instance);
        _gate = new ToolIsolationGate(_service, _accessor, options);
    }

    public void Dispose()
    {
        _service.Dispose();
        IsolationTestSupport.Cleanup(_root);
        IsolationTestSupport.Cleanup(_staging);
    }

    private IDisposable Begin(ExportScope? scope = null) => _accessor.Begin(IsolationTestSupport.Context(_root, scope ?? ExportScope.Empty));

    [Fact]
    public async Task ExecCode_ShouldRunPythonInTheContainer_AndNeverTouchTheWorkspace()
    {
        File.WriteAllText(Path.Combine(_root, "keep.txt"), "keep");
        using var scope = Begin(ExportScope.From(["keep.txt"]));
        var tool = new ExecCodeTool(NullLogger<ExecCodeTool>.Instance, _gate);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["code"] = "print('Hello from Python')\nprint(2 + 2)\nopen('/workspace/keep.txt','w').write('CHANGED')\nimport os\nprint(os.getuid())"
        });

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("Hello from Python", result.Output, StringComparison.Ordinal);
        Assert.Contains("4", result.Output, StringComparison.Ordinal);
        Assert.Contains("10001", result.Output, StringComparison.Ordinal);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(_root, "keep.txt")));   // snippets never export
    }

    [Fact]
    public async Task ExecCmd_ShouldRunSynchronously_AndExportOnlyInScopeChanges()
    {
        File.WriteAllText(Path.Combine(_root, "sample.txt"), "IUnitOfWork lives here");
        using var scope = Begin(ExportScope.From(["copy.txt"]));
        var tool = new RunCommandTool(NullLogger<RunCommandTool>.Instance, null, _gate);

        var grep = await tool.ExecuteAsync(new Dictionary<string, object?> { ["command"] = new[] { "grep", "-r", "IUnitOfWork", "." } });
        Assert.Equal(ToolResultStatus.Success, grep.Status);
        Assert.Contains("sample.txt", grep.Output, StringComparison.Ordinal);

        var copy = await tool.ExecuteAsync(new Dictionary<string, object?> { ["command"] = new[] { "cp", "sample.txt", "copy.txt" } });
        Assert.Equal(ToolResultStatus.Success, copy.Status);
        Assert.Contains("exported: Add copy.txt", copy.Output, StringComparison.Ordinal);
        Assert.Equal("IUnitOfWork lives here", File.ReadAllText(Path.Combine(_root, "copy.txt")));

        var outOfScope = await tool.ExecuteAsync(new Dictionary<string, object?> { ["command"] = new[] { "cp", "sample.txt", "evil.txt" } });
        Assert.Contains("ExportRejectedOutOfScope", outOfScope.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
    }

    [Fact]
    public async Task ExecCmd_Background_ShouldRunAsObservableCommandTask()
    {
        using var scope = Begin();
        var tool = new RunCommandTool(NullLogger<RunCommandTool>.Instance, null, _gate);
        var started = await tool.ExecuteAsync(new Dictionary<string, object?> { ["command"] = new[] { "find", "/workspace", "-maxdepth", "1" }, ["background"] = true });
        var id = System.Text.RegularExpressions.Regex.Match(started.Output, "cmd_[0-9a-f]{32}").Value;

        var snapshot = await CommandTaskRegistry.WaitForCompletionAsync(id);
        Assert.NotNull(snapshot);
        Assert.Contains(snapshot!.Events, e => e.Text.StartsWith("started:", StringComparison.Ordinal));
        Assert.Equal(CommandTaskStatus.Completed, snapshot.Status);
    }

    [Fact]
    public async Task SkillScript_ShouldRunInTheContainerWithArgs_FromACopyOfTheSkillFolder()
    {
        var skills = IsolationTestSupport.NewTempDir("skills");
        try
        {
            Directory.CreateDirectory(Path.Combine(skills, "audit", "scripts"));
            File.WriteAllText(Path.Combine(skills, "audit", "SKILL.md"), "# audit");
            File.WriteAllText(Path.Combine(skills, "audit", "scripts", "audit.py"), "import sys, os\nprint('skill-ok', sys.argv[1:], os.getuid())\n");
            using var scope = Begin();
            var tool = new SkillTool(NullLogger<SkillTool>.Instance, null, skills, _gate);

            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["action"] = "run_script", ["name"] = "audit", ["script_path"] = "scripts/audit.py", ["args"] = new[] { "--fast" }
            });

            Assert.Equal(ToolResultStatus.Success, result.Status);
            Assert.Contains("skill-ok", result.Output, StringComparison.Ordinal);
            Assert.Contains("--fast", result.Output, StringComparison.Ordinal);
            Assert.Contains("10001", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            IsolationTestSupport.Cleanup(skills);
        }
    }
}
