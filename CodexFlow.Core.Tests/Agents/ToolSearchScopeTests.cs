// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Constants;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using CoreToolSearch = CodexFlow.Core.Agents.Tools.ToolSearchMetaTool;
using CoreLoading = CodexFlow.Core.Abstractions.ToolLoading;
using CoreToolSearchScope = CodexFlow.Core.Agents.Tools.ToolSearchScope;
using CoreToolCallBudget = CodexFlow.Core.Agents.Tools.ToolCallBudget;

namespace CodexFlow.Core.Tests.Agents;

/// <summary>
/// Live developer simulation (dependency-upgrade-compliant, local host): the gateway's <c>tool_search</c> searched the whole registry, said
/// "activated: true / available_now: true" for tools the gateway never exposes, and returned 23 full descriptions (156 KB) per call. The
/// model searched nine times for an editing tool that never appeared. A scoped search reports what the caller's surface really holds.
/// </summary>
public class ToolSearchScopeTests
{
    private sealed class Stub(string name, string description) : ICodexTool
    {
        public string Name { get; } = name;
        public string Description { get; } = description;
        public ToolCategory Category => ToolCategory.Forge;
        public IReadOnlyList<int> AllowedStages => [1, 2, 3, 4, 5];
        public CodexFlow.Core.Execution.ToolCapability Capability => CodexFlow.Core.Execution.ToolCapability.ReadOnly;
        public Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default) => Task.FromResult(CodexToolResult.Succeeded("ok"));
    }

    private static DefaultToolRegistry RegistryWithEditingTools(int count)
    {
        var registry = new DefaultToolRegistry(NullLogger<DefaultToolRegistry>.Instance);
        for (var i = 0; i < count; i++)
        {
            var longDescription = "对工作区文件应用补丁并编辑文件内容。" + string.Concat(Enumerable.Repeat("【参数说明】patch 与 edit 的详细规则，包含大量中文说明文字。", 25));
            registry.RegisterTool(new Stub($"edit_tool_{i:00}", longDescription), CoreLoading.Deferred);
        }

        return registry;
    }

    private static string Search(CoreToolSearch tool, string query)
    {
        var result = tool.ExecuteAsync(new Dictionary<string, object?> { ["query"] = query }).GetAwaiter().GetResult();
        return result.Output;
    }

    private static CoreToolSearch Scoped(DefaultToolRegistry registry, params string[] exposed) =>
        new(registry, NullLogger<CoreToolSearch>.Instance,
            new CoreToolSearchScope(() => exposed));

    [Fact]
    public void AScopedSearch_ReturnsAShortList_WithShortDescriptions_AndNoUnicodeEscapes()
    {
        var output = Search(Scoped(RegistryWithEditingTools(40)), "patch edit");

        Assert.True(output.Length <= 6_000, $"the result is {output.Length} chars");
        Assert.DoesNotContain("\\u", output);
        using var json = JsonDocument.Parse(output);
        Assert.Equal(40, json.RootElement.GetProperty("found").GetInt32());
        Assert.Equal(CoreToolSearchScope.DefaultMaxResults, json.RootElement.GetProperty("tools").GetArrayLength());
        Assert.Equal(CoreToolSearchScope.DefaultMaxResults, json.RootElement.GetProperty("shown").GetInt32());
    }

    [Fact]
    public void AvailableNow_IsWhatTheCallersSurfaceHolds_NotWhatTheRegistryActivated()
    {
        var registry = RegistryWithEditingTools(3);

        var output = Search(Scoped(registry, "edit_tool_01"), "patch");

        using var json = JsonDocument.Parse(output);
        var tools = json.RootElement.GetProperty("tools").EnumerateArray().ToDictionary(
            t => t.GetProperty("name").GetString()!, t => t);
        Assert.True(tools["edit_tool_01"].GetProperty("available_now").GetBoolean());
        Assert.False(tools["edit_tool_00"].GetProperty("available_now").GetBoolean());
        Assert.False(tools["edit_tool_02"].GetProperty("available_now").GetBoolean());
        Assert.Contains("不在你当前的工具面里", tools["edit_tool_00"].GetProperty("note").GetString());
    }

    [Fact]
    public void WhenNothingFoundIsAvailable_TheResultSaysSo_AndSaysWhatToDoInstead()
    {
        var output = Search(Scoped(RegistryWithEditingTools(5)), "patch");

        using var json = JsonDocument.Parse(output);
        var hint = json.RootElement.GetProperty("hint").GetString();
        Assert.Contains("本回合无法调用", hint);
        Assert.Contains("不要为同一目的重复搜索", hint);
        Assert.All(json.RootElement.GetProperty("tools").EnumerateArray(), t => Assert.False(t.GetProperty("available_now").GetBoolean()));
    }

    [Fact]
    public void WhenSomethingFoundIsAvailable_ThereIsNoWarningHint()
    {
        var output = Search(Scoped(RegistryWithEditingTools(5), "edit_tool_02"), "patch");

        using var json = JsonDocument.Parse(output);
        Assert.False(json.RootElement.TryGetProperty("hint", out _));
    }

    [Fact]
    public void WithoutAScope_TheWorkersRichResultIsUnchanged()
    {
        var registry = RegistryWithEditingTools(2);

        var output = Search(new CoreToolSearch(registry, NullLogger<CoreToolSearch>.Instance), "patch");

        using var json = JsonDocument.Parse(output);
        var first = json.RootElement.GetProperty("tools")[0];
        Assert.True(first.TryGetProperty("examples", out _));
        Assert.True(first.TryGetProperty("risk", out _));
    }

    // ── the loop it can start ─────────────────────────────────────────────────────────────────────────────────

    // The nine queries of the failed run were all different ("patch apply file edit", "shell command run", "create directory mkdir"...), so a
    // check for the same call repeated would not have stopped it. The number of searches in one run is what is bounded.
    [Fact]
    public void AfterTheBudgetIsSpent_TheSearchIsClosed_AndTheModelIsToldToAnswerInText()
    {
        var budget = new CoreToolCallBudget(maxCalls: 3);
        var tool = new CoreToolSearch(RegistryWithEditingTools(5), NullLogger<CoreToolSearch>.Instance, new CoreToolSearchScope(() => [], Budget: budget));

        var first = Search(tool, "patch edit apply");
        Search(tool, "shell command run");
        Search(tool, "create directory mkdir");
        var fourth = Search(tool, "write file");

        Assert.Contains("\"found\"", first);
        Assert.DoesNotContain("\"found\"", fourth);
        Assert.Contains("tool_search", fourth);
        Assert.Contains("不要再调用", fourth);
        Assert.Contains("用文字", fourth);
    }

    [Fact]
    public void TheBudget_IsSharedByEveryInstanceThatHoldsIt_AsTheGatewayRebuildsItsToolsEveryRound()
    {
        var budget = new CoreToolCallBudget(maxCalls: 2);
        CoreToolSearch NewRound() => new(RegistryWithEditingTools(2), NullLogger<CoreToolSearch>.Instance, new CoreToolSearchScope(() => [], Budget: budget));

        Search(NewRound(), "patch");
        Search(NewRound(), "patch");

        Assert.DoesNotContain("\"found\"", Search(NewRound(), "patch"));
    }

    [Fact]
    public void ABudget_CountsOnlyWhatIsTaken_AndNeverGoesBelowZero()
    {
        var budget = new CoreToolCallBudget(maxCalls: 1);

        Assert.True(budget.TryTake());
        Assert.False(budget.TryTake());
        Assert.False(budget.TryTake());
        Assert.Equal(0, budget.Remaining);
    }

    // ── the prompt that promises it ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheToolGuidance_AppliedTwice_AppearsOnce()
    {
        string[] tools = ["tool_search", "ivilson_read", "search_in_files"];

        var once = ToolCatalogPromptComposer.AppendRuntimeToolGuidance("base", tools);
        var twice = ToolCatalogPromptComposer.AppendRuntimeToolGuidance(once, tools);

        Assert.Equal(once, twice);
        Assert.Equal(1, CountOf(twice, "## 工具发现"));
        Assert.Equal(1, CountOf(twice, "## 工具使用准则"));
    }

    [Fact]
    public void TheDiscoveryText_NoLongerPromisesThatEverythingFoundIsUsable()
    {
        var prompt = ToolCatalogPromptComposer.AppendRuntimeToolGuidance("base", ["tool_search"]);

        Assert.DoesNotContain("激活结果会在下一轮自动生效", prompt);
        Assert.Contains("available_now", prompt);
        Assert.Contains("不要为同一目的重复搜索", prompt);
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
