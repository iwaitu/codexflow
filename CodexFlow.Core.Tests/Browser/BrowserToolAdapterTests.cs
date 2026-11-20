// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Browser;
using CodexFlow.Core.Models;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.Browser;

public sealed class BrowserToolAdapterTests
{
    [Theory]
    [InlineData(BrowserToolNames.Navigate)]
    [InlineData(BrowserToolNames.Snapshot)]
    [InlineData(BrowserToolNames.Act)]
    [InlineData(BrowserToolNames.Tabs)]
    [InlineData(BrowserToolNames.RequestTakeover)]
    [InlineData(BrowserToolNames.Evaluate)]
    public void GeneratedSchemaMatchesTheManagedFactory(string name)
    {
        var tool = CreateStub(name, _ => CodexToolResult.Succeeded("ok"));
        var managed = CodexToolFunctionAdapterFactory.CreateAIFunction(tool);
        var generated = SourceGeneratedToolFunction.Browser(tool, name, tool.Description);

        Assert.Equal(managed.Name, generated.Name);
        Assert.Equal(managed.Description, generated.Description);
        Assert.True(JsonElement.DeepEquals(managed.JsonSchema, generated.JsonSchema));
        Assert.Equal(managed.ReturnJsonSchema?.GetRawText(), generated.ReturnJsonSchema?.GetRawText());
        var properties = generated.JsonSchema.GetProperty("properties");
        Assert.Equal(managed.JsonSchema.GetProperty("properties").EnumerateObject().Select(property => property.Name),
            properties.EnumerateObject().Select(property => property.Name));
        Assert.False(properties.TryGetProperty("session_id", out _));
        Assert.False(properties.TryGetProperty("workspace_path", out _));
        Assert.False(properties.TryGetProperty("project_root", out _));
    }

    [Fact]
    public async Task GeneratedSnapshotBindsDeclaredArgumentsOnly()
    {
        Dictionary<string, object?>? captured = null;
        var tool = CreateStub(BrowserToolNames.Snapshot, input =>
        {
            captured = input;
            return CodexToolResult.Succeeded("snapshot");
        });
        var generated = SourceGeneratedToolFunction.Browser(tool, tool.Name, tool.Description);
        using var json = JsonDocument.Parse("512");
        var result = await Invoke(generated, new AIFunctionArguments
        {
            ["max_chars"] = json.RootElement,
            ["include_console"] = false,
            ["include_network_errors"] = null,
            ["session_id"] = "model-session",
            ["workspace_path"] = "model-workspace",
            ["plan_mode_active"] = false
        });

        var bound = Assert.IsType<Dictionary<string, object?>>(captured);
        Assert.Equal(2, bound.Count);
        Assert.Equal(512, bound["max_chars"]);
        Assert.Equal(false, bound["include_console"]);
        Assert.True(result.GetProperty("__icodex_tool_result").GetBoolean());
        Assert.Equal("snapshot", result.GetProperty("output").GetString());
    }

    [Theory]
    [InlineData(BrowserToolNames.RequestTakeover, "reason")]
    [InlineData(BrowserToolNames.Evaluate, "expression")]
    public async Task GeneratedInteractionPreservesTrustedContextAndPlanModeChecks(string name, string argument)
    {
        var manager = Mock.Of<IBrowserSessionManager>(MockBehavior.Strict);
        ICodexTool tool = name == BrowserToolNames.Evaluate
            ? new BrowserEvaluateTool(manager)
            : new BrowserRequestTakeoverTool(manager);
        var generated = SourceGeneratedToolFunction.Browser(tool, name, tool.Description);
        var arguments = new AIFunctionArguments
        {
            [argument] = "fixture",
            ["session_id"] = "model-session",
            ["plan_mode_active"] = false
        };

        var untrusted = await Invoke(generated, arguments);
        AssertFailure(untrusted, "missing_trusted_browser_context");

        using var caller = AmbientBrowserCallerContext.BeginScope(
            new BrowserTrustedCallerContext("host-session", SessionWorkMode.Development, PlanModeActive: true));
        var planMode = await Invoke(generated, arguments);
        AssertFailure(planMode, "plan_mode_active");
        Mock.Get(manager).VerifyNoOtherCalls();
    }

    private static ICodexTool CreateStub(string name, Func<Dictionary<string, object?>, CodexToolResult> execute)
        => new DelegateCodexTool(name, "Browser fixture.", ToolCategory.Read, [0],
            (arguments, _) => Task.FromResult(execute(arguments)));

    private static async Task<JsonElement> Invoke(AIFunction function, AIFunctionArguments arguments)
        => Assert.IsType<JsonElement>(await function.InvokeAsync(arguments));

    private static void AssertFailure(JsonElement result, string code)
    {
        Assert.True(result.GetProperty("__icodex_tool_result").GetBoolean());
        Assert.Equal((int)ToolResultStatus.Failed, result.GetProperty("status").GetInt32());
        Assert.Contains(code, result.GetProperty("output").GetString()!, StringComparison.Ordinal);
    }
}
