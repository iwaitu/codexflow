// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Constants;
using CodexFlow.Core.Hashline.Models;
using CodexFlow.Core.Models;
using CodexFlow.Core.Services;
using Xunit;

namespace CodexFlow.Core.Tests.Agents;

public class DefaultAgentRoleRegistryHashlinePromptTests
{
    [Fact]
    public void GetSystemPrompt_ShouldDisableDefaultHashlineInstructions_WhenConfigIsOff()
    {
        IAgentRoleRegistry registry = new DefaultAgentRoleRegistry(new HashlineOptions
        {
            Enabled = false,
            ForceForHighRiskFiles = false
        });

        var prompt = registry.GetSystemPrompt(CodexAgentRole.Forge);

        Assert.Contains("Hashline 默认策略关闭", prompt);
        Assert.Contains("不要默认执行 `hs_read({ \"path\": \"...\" })`", prompt);
        Assert.DoesNotContain("修改前必须先 `hs_read`", prompt);
    }

    [Fact]
    public void GetSystemPrompt_ShouldRequireHashlineForHighRiskFiles_WhenForcedByConfig()
    {
        IAgentRoleRegistry registry = new DefaultAgentRoleRegistry(new HashlineOptions
        {
            Enabled = true,
            ForceForHighRiskFiles = true
        });

        var forgePrompt = registry.GetSystemPrompt(CodexAgentRole.Forge);
        var architectPrompt = registry.GetSystemPrompt(CodexAgentRole.Architect);

        Assert.Contains("高风险文件（Program.cs、*.csproj、appsettings.json 等）修改前必须先 `hs_read`", forgePrompt);
        Assert.Contains("高风险文件修改必须规划为 Hashline 精准编辑，并显式标注 `[HASHLINE_REQUIRED]`", architectPrompt);
        Assert.Contains("`Enabled` = true", architectPrompt);
        Assert.Contains("`ForceForHighRiskFiles` = true", architectPrompt);
    }

    [Fact]
    public void GetSystemPrompt_ShouldIncludeConcreteHashlineExamples_ForCsprojAndProgramEdits()
    {
        IAgentRoleRegistry registry = new DefaultAgentRoleRegistry(new HashlineOptions
        {
            Enabled = true,
            ForceForHighRiskFiles = true
        });

        var forgePrompt = registry.GetSystemPrompt(CodexAgentRole.Forge);

        Assert.Contains("Hashline 编辑 `.csproj`", forgePrompt);
        Assert.Contains("Program.cs", forgePrompt);
        Assert.Contains("至少包含 1 个操作对象", forgePrompt);
        Assert.Contains("不要去修改 `CleanApp.Core.csproj` 添加该包", forgePrompt);
    }

    [Theory]
    [InlineData("csharp", ".NET / C#", "dotnet\", \"build", "dotnet\", \"test")]
    [InlineData("java", "Java", "mvn\", \"test", "gradle\", \"test")]
    [InlineData("node", "Node / TypeScript / JavaScript", "npm\", \"run\", \"build", "npm\", \"test")]
    [InlineData("python", "Python", "pip\", \"install\", \"-e\", \".", "pytest")]
    public void GetForgePrompt_ShouldInjectLanguageSpecificBuildGuide(
        string language,
        string expectedHeading,
        string expectedCommandA,
        string expectedCommandB)
    {
        var session = new CodexSession
        {
            Id = "S1",
            ActiveTaskId = "TASK_001"
        };

        session.ActiveFacts.Add(new MemoryEntry(ProjectMemoryFactKeys.ProjectLanguage, language, MemoryFactCategories.Project, DateTime.UtcNow));
        session.Plan.Add(new CodexTask
        {
            Id = "TASK_001",
            Title = "Build check",
            Description = "执行构建和测试验证"
        });

        var prompt = CodexPrompts.GetForgePrompt(session, new HashlineOptions
        {
            Enabled = false,
            ForceForHighRiskFiles = false
        });

        Assert.Contains(expectedHeading, prompt);
        Assert.Contains(expectedCommandA, prompt);
        Assert.Contains(expectedCommandB, prompt);
        Assert.Contains("exec_cmd", prompt);
    }

    [Fact]
    public void GetArchitectPrompt_ShouldDescribeWorkerDispatchMatrix_AndIndependentVerification()
    {
        IAgentRoleRegistry registry = new DefaultAgentRoleRegistry(new HashlineOptions
        {
            Enabled = false,
            ForceForHighRiskFiles = false
        });

        var prompt = registry.GetSystemPrompt(CodexAgentRole.Architect);

        Assert.Contains("`spawn_worker`：仅用于创建一个新的独立 worker 线程", prompt);
        Assert.Contains("`continue_worker`：仅用于继续一个**已有** worker 的上下文", prompt);
        Assert.Contains("优先 `spawn_worker(worker_type=\"verify\")` 执行独立验证", prompt);
    }

    [Fact]
    public void GetVerifyPrompt_ShouldRequireIndependenceFromImplementer()
    {
        var prompt = CodexPrompts.GetVerifyWorkerPrompt();

        Assert.Contains("验证必须与实施主体保持独立", prompt);
        Assert.Contains("实现者（包括 forge worker 或主协调器）给出的“我已经验证过”只能作为线索", prompt);
        Assert.Contains("如果你正处于对同一实现产物进行自我验证的场景", prompt);
    }
}
