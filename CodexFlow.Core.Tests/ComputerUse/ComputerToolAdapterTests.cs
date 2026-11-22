// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.ComputerUse;
using CodexFlow.Core.Configuration;
using CodexFlow.Core.Models;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.ComputerUse;

public sealed class ComputerToolAdapterTests
{
    private sealed class StubModelSettings(bool multimodal) : IModelSettingsService
    {
        public ModelSettingsView Read()
            => new("https://api.example/v1/{1}", "ChatCompletions", "gpt-5", 128000, null, null, true, "1234", multimodal);

        public ModelSettingsView Update(ModelSettingsUpdate update) => Read();
    }

    [Theory]
    [InlineData(ComputerToolNames.ScreenInfo)]
    [InlineData(ComputerToolNames.Screenshot)]
    [InlineData(ComputerToolNames.Act)]
    [InlineData(ComputerToolNames.RequestTakeover)]
    public void GeneratedSchemaHidesInternalContextAndMatchesFactory(string name)
    {
        var tool = CreateStub(name, _ => CodexToolResult.Succeeded("ok"));
        var adapted = CodexToolFunctionAdapterFactory.CreateAIFunction(tool);
        var generated = SourceGeneratedToolFunction.Computer(tool, name, tool.Description);

        Assert.Equal(adapted.Name, generated.Name);
        Assert.Equal(adapted.Description, generated.Description);
        Assert.True(JsonElement.DeepEquals(adapted.JsonSchema, generated.JsonSchema));

        var properties = generated.JsonSchema.GetProperty("properties");
        Assert.False(properties.TryGetProperty("session_id", out _));
        Assert.False(properties.TryGetProperty("workspace_path", out _));
        Assert.False(properties.TryGetProperty("project_root", out _));
    }

    [Fact]
    public async Task GeneratedActBindsDeclaredNumericAndStringArgumentsAndSerializesNamedMetadata()
    {
        Dictionary<string, object?>? captured = null;
        var tool = CreateStub(ComputerToolNames.Act, input =>
        {
            captured = input;
            return CodexToolResult.Succeeded(
                "clicked",
                new ComputerActToolMetadata("click", 7, 1280, 720, "ScreenshotPixels", ScreenshotAttached: true));
        });

        var fn = SourceGeneratedToolFunction.Computer(tool, tool.Name, tool.Description);
        using var xDoc = JsonDocument.Parse("320.5");
        using var yDoc = JsonDocument.Parse("480");
        var result = await Invoke(fn, new AIFunctionArguments
        {
            ["action"] = "click",
            ["x"] = xDoc.RootElement,
            ["y"] = yDoc.RootElement,
            ["button"] = "left",
            ["session_id"] = "injected-session"
        });

        var bound = Assert.IsType<Dictionary<string, object?>>(captured);
        Assert.Equal("click", bound["action"]);
        Assert.Equal(320.5d, bound["x"]);
        Assert.Equal(480d, bound["y"]);
        Assert.Equal("left", bound["button"]);
        Assert.False(bound.ContainsKey("session_id"));

        Assert.True(result.GetProperty("__icodex_tool_result").GetBoolean());
        Assert.Equal("clicked", result.GetProperty("output").GetString());
        Assert.Equal(7, result.GetProperty("metadata").GetProperty("screenshotSeq").GetInt32());
        Assert.True(result.GetProperty("metadata").GetProperty("screenshotAttached").GetBoolean());
    }

    [Theory]
    [InlineData(ComputerToolNames.Act, "action")]
    [InlineData(ComputerToolNames.RequestTakeover, "reason")]
    public async Task GeneratedToolsEnforceTrustedContextPlanModeAndMultimodalGates(string name, string requiredArg)
    {
        var manager = Mock.Of<IComputerSessionManager>(MockBehavior.Strict);
        ICodexTool tool = name == ComputerToolNames.Act
            ? new ComputerActTool(manager, new StubModelSettings(multimodal: true))
            : new ComputerRequestTakeoverTool(manager, new StubModelSettings(multimodal: true));
        var fn = SourceGeneratedToolFunction.Computer(tool, name, tool.Description);
        var args = new AIFunctionArguments
        {
            [requiredArg] = "click",
            ["session_id"] = "forged-session"
        };

        var untrusted = await Invoke(fn, args);
        AssertFailure(untrusted, "missing_trusted_computer_context");

        using var planScope = AmbientComputerCallerContext.BeginScope(
            new ComputerTrustedCallerContext("host-session", SessionWorkMode.Development, PlanModeActive: true));
        var planBlocked = await Invoke(fn, args);
        AssertFailure(planBlocked, "plan_mode_active");

        if (name == ComputerToolNames.Act)
        {
            ICodexTool nonMultimodalAct = new ComputerActTool(manager, new StubModelSettings(multimodal: false));
            var nonMultimodalFn = SourceGeneratedToolFunction.Computer(nonMultimodalAct, name, nonMultimodalAct.Description);
            using var activeScope = AmbientComputerCallerContext.BeginScope(
                new ComputerTrustedCallerContext("host-session", SessionWorkMode.Daily, PlanModeActive: false));
            var rejected = await Invoke(nonMultimodalFn, args);
            AssertFailure(rejected, "multimodal_required_for_computer_use");
        }

        Mock.Get(manager).VerifyNoOtherCalls();
    }

    private static ICodexTool CreateStub(string name, Func<Dictionary<string, object?>, CodexToolResult> execute)
        => new DelegateCodexTool(name, "Computer Use fixture.", ToolCategory.Read, [0],
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
