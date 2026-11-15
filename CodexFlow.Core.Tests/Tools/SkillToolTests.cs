// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.Tools;

public sealed class SkillToolTests
{
    [Fact]
    public async Task SkillTool_List_ShouldFindSkillFiles()
    {
        var root = CreateSkillRoot();
        try
        {
            CreateSkill(root, "python-runner", "# Python Runner");
            var tool = new SkillTool(NullLogger<SkillTool>.Instance, skillRoot: root);

            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["action"] = "list"
            });

            Assert.Equal(ToolResultStatus.Success, result.Status);
            Assert.Contains("python-runner", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SkillTool_Read_ShouldReturnSkillMarkdown()
    {
        var root = CreateSkillRoot();
        try
        {
            CreateSkill(root, "node-runner", "# Node Runner\nUse node.");
            var tool = new SkillTool(NullLogger<SkillTool>.Instance, skillRoot: root);

            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["action"] = "read",
                ["name"] = "node-runner"
            });

            Assert.Equal(ToolResultStatus.Success, result.Status);
            Assert.Contains("# Node Runner", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SkillTool_RunScript_ShouldRunInIsolationWithTheSkillFolderAsInput_AndRejectTraversal()
    {
        var root = CreateSkillRoot();
        try
        {
            var scriptPath = CreateSkill(root, "audit", "# Audit");
            Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(scriptPath)!, "scripts"));
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(scriptPath)!, "scripts", "audit.py"), "print('ok')");

            // The host-process runner must never be touched: a strict mock fails on any call.
            var hostRunner = new Mock<ISkillScriptRunner>(MockBehavior.Strict);
            var fake = new CodexFlow.Core.Tests.Execution.FakeIsolationService
            {
                OnRun = (_, _) => Task.FromResult(new CodexFlow.Core.Execution.IsolatedCommandResult(
                    CodexFlow.Core.Execution.ExecutionDecision.Allow(), CodexFlow.Core.Execution.IsolatedCommandStatus.Completed, 0, "audit ok", string.Empty, false, TimeSpan.Zero, "fake"))
            };
            var accessor = new CodexFlow.Core.Execution.TrustedExecutionContextAccessor();
            var gate = new CodexFlow.Core.Execution.ToolIsolationGate(fake, accessor, new CodexFlow.Core.Execution.IsolationOptions { LanguageImages = { ["python"] = "img-python" } });
            var tool = new SkillTool(NullLogger<SkillTool>.Instance, hostRunner.Object, root, gate);

            using var scope = accessor.Begin(CodexFlow.Core.Tests.Execution.IsolationTestSupport.Context(Path.GetFullPath(root)));
            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["action"] = "run_script",
                ["name"] = "audit",
                ["script_path"] = "scripts/audit.py",
                ["args"] = new[] { "--fast" }
            });
            var traversal = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["action"] = "run_script",
                ["name"] = "audit",
                ["script_path"] = "../outside.py"
            });

            Assert.Equal(ToolResultStatus.Success, result.Status);
            Assert.Contains("audit ok", result.Output, StringComparison.Ordinal);
            Assert.Equal(ToolResultStatus.Failed, traversal.Status);

            Assert.Equal("img-python", fake.Options.Single().Image);
            var input = Assert.Single(fake.Options.Single().Inputs!);
            Assert.Equal("skill-inputs/audit", input.GuestSubPath);
            Assert.Equal(["python3", "/tmp/skill-inputs/audit/scripts/audit.py", "--fast"], fake.Requests.Single().Argv.ToArray());
            Assert.Equal(0, fake.ExportCalls);
            hostRunner.VerifyNoOtherCalls();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SkillTool_RunScript_WithoutTrustedContext_ShouldBeDenied_NotRunOnTheHost()
    {
        var root = CreateSkillRoot();
        try
        {
            var scriptPath = CreateSkill(root, "audit", "# Audit");
            Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(scriptPath)!, "scripts"));
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(scriptPath)!, "scripts", "audit.py"), "print('ok')");
            var hostRunner = new Mock<ISkillScriptRunner>(MockBehavior.Strict);
            var tool = new SkillTool(NullLogger<SkillTool>.Instance, hostRunner.Object, root);

            var result = await tool.ExecuteAsync(new Dictionary<string, object?> { ["action"] = "run_script", ["name"] = "audit", ["script_path"] = "scripts/audit.py" });

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Contains("MissingTrustedContext", result.Output, StringComparison.Ordinal);
            hostRunner.VerifyNoOtherCalls();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateSkillRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "codex-skill-tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateSkill(string root, string name, string content)
    {
        var skillDir = Path.Combine(root, name);
        Directory.CreateDirectory(skillDir);
        var skillPath = Path.Combine(skillDir, "SKILL.md");
        File.WriteAllText(skillPath, content);
        return skillPath;
    }
}
