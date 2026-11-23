// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Constants;
using CodexFlow.Core.Models;
using CodexFlow.Core.Protocols;
using CodexFlow.Core.Workers;
using Xunit;

namespace CodexFlow.Core.Tests.Constants;

public class ToolCatalogPromptComposerTests
{
    [Fact]
    public void AppendRuntimeToolGuidance_IncludesConditionalDiscoveryAndToolUsageRules()
    {
        var tools = new ICodexTool[]
        {
            new TestTool("tool_search", ToolCategory.System),
            new TestTool("ivilson_read", ToolCategory.Read),
            new TestTool("search_in_files", ToolCategory.Read),
            new TestTool("write_file", ToolCategory.Forge),
            new TestTool("exec_cmd", ToolCategory.System)
        };

        var prompt = ToolCatalogPromptComposer.AppendRuntimeToolGuidance("base prompt", tools);

        Assert.Contains("## 工具发现", prompt);
        Assert.Contains("tool_search({\"query\": \"关键词\"})", prompt);
        Assert.Contains("读取文件优先使用 `ivilson_read`", prompt);
        Assert.Contains("定位文件与搜索代码优先使用 `search_in_files`", prompt);
        Assert.Contains("先沿用用户原词调用 `search_in_files`", prompt);
        Assert.Contains("进入陌生仓库时，先从 `.` 或已确认存在的真实目录开始使用 `search_in_files`", prompt);
        Assert.Contains("下一步优先直接使用 `ivilson_read` 读取这些文件", prompt);
        Assert.Contains("修改代码优先使用 `write_file`", prompt);
        Assert.Contains("将 `exec_cmd` 保留给构建、测试、Git", prompt);
    }

    [Fact]
    public void AppendRuntimeToolGuidance_IncludesWorkerConstraints_AndSkipsDiscoveryWithoutToolSearch()
    {
        var workerContext = new WorkerRuntimeContext
        {
            WorkerType = WorkerType.Verify,
            DisplayName = "Ivilson-Verify",
            IsolationMode = WorkerIsolationMode.Workspace,
            OutputContract = WorkerOutputContract.VerificationReportEnvelope,
            LanguageServiceAccess = WorkerLanguageServiceAccess.DiagnosticsEvidenceRead,
            AllowedToolNames = ["search_in_files", "run_tests", "exec_cmd"],
            AllowedToolCategories = [ToolCategory.Read, ToolCategory.Analysis, ToolCategory.Sentry]
        };

        var prompt = ToolCatalogPromptComposer.AppendRuntimeToolGuidance(
            "base prompt",
            ["search_in_files", "run_tests", "exec_cmd"],
            workerContext);

        Assert.DoesNotContain("## 工具发现", prompt);
        Assert.Contains("## 当前工具面约束", prompt);
        Assert.Contains("当前 worker：`Ivilson-Verify`", prompt);
        Assert.Contains("允许工具分类：Read、Analysis、Sentry", prompt);
        Assert.Contains("只读/分析优先 surface", prompt);
        Assert.Contains("验证型输出契约", prompt);
    }

    [Theory]
    [InlineData(ComputerToolNames.Act, ReadBuiltInSkillTool.ToolName, true)]
    [InlineData(ComputerToolNames.Screenshot, ReadBuiltInSkillTool.ToolName, false)]
    [InlineData(ComputerToolNames.Act, ComputerToolNames.Screenshot, false)]
    public void AppendRuntimeToolGuidance_AdvertisesDesktopSkillOnlyWhenCallable(
        string firstTool, string secondTool, bool shouldAdvertise)
    {
        string[] toolNames = [firstTool, secondTool];
        var prompt = ToolCatalogPromptComposer.AppendRuntimeToolGuidance("base prompt", toolNames);

        Assert.Equal(shouldAdvertise, prompt.Contains("read_builtin_skill({\"name\":\"desktop-control\"})", StringComparison.Ordinal));
        Assert.Equal(prompt, ToolCatalogPromptComposer.AppendRuntimeToolGuidance(prompt, toolNames));
    }

    private sealed class TestTool : ICodexTool
    {
        public TestTool(string name, ToolCategory category)
        {
            Name = name;
            Description = name;
            Category = category;
        }

        public string Name { get; }
        public CodexFlow.Core.Execution.ToolCapability Capability => CodexFlow.Core.Execution.ToolCapability.ReadOnly;
        public string Description { get; }
        public ToolCategory Category { get; }
        public IReadOnlyList<int> AllowedStages => [1, 2, 3];

        public Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
            => Task.FromResult(CodexToolResult.Succeeded("ok"));
    }
}
