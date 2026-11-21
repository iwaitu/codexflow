// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using System.Text.Json.Nodes;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using Microsoft.Extensions.AI;
using Xunit;

namespace CodexFlow.Core.Tests.Agents;

public sealed class GenericToolAdapterTests
{
    [Fact]
    public async Task WorkspaceSchemaAndDefaultArgumentsMatchTheManagedAdapter()
    {
        Dictionary<string, object?>? arguments = null;
        var tool = CreateTool("list_workspace", input =>
        {
            arguments = input;
            return CodexToolResult.Succeeded("workspace entries");
        });
        var managed = CodexToolFunctionAdapterFactory.CreateAIFunction(tool);
        var generated = SourceGeneratedToolFunction.ListWorkspace(tool, tool.Description);

        Assert.True(JsonElement.DeepEquals(managed.JsonSchema, generated.JsonSchema));
        Assert.Equal(managed.ReturnJsonSchema?.GetRawText(), generated.ReturnJsonSchema?.GetRawText());
        var result = Assert.IsType<JsonElement>(await generated.InvokeAsync(new AIFunctionArguments
        {
            ["path"] = null,
            ["recursive"] = null,
            ["session_id"] = "session",
            ["workspace_path"] = "workspace"
        }));

        var bound = Assert.IsType<Dictionary<string, object?>>(arguments);
        Assert.Equal(".", bound["path"]);
        Assert.Equal(false, bound["recursive"]);
        Assert.Equal("session", bound["session_id"]);
        Assert.Equal("workspace", bound["workspace_path"]);
        Assert.False(bound.ContainsKey("project_root"));
        Assert.True(result.GetProperty("__icodex_tool_result").GetBoolean());
        Assert.Equal("workspace entries", result.GetProperty("output").GetString());
    }

    [Fact]
    public async Task CustomFallbackPreservesNestedSchemaAndManagedMetadata()
    {
        var tool = CreateTool("fixture_read", arguments =>
            CodexToolResult.Succeeded(arguments["path"] as string ?? "", new { ResultLabel = "fixture", Count = 3 }));
        var managed = CodexToolFunctionAdapterFactory.CreateAIFunction(tool);
        var generated = SourceGeneratedToolFunction.Fallback(tool, tool.Name, tool.Description);

        Assert.True(JsonElement.DeepEquals(managed.JsonSchema, generated.JsonSchema));
        Assert.Equal(managed.ReturnJsonSchema?.GetRawText(), generated.ReturnJsonSchema?.GetRawText());
        var input = new AIFunctionArguments { ["parameters"] = """{"filePath":" fixture.txt "}""" };
        var legacy = Assert.IsType<JsonElement>(await managed.InvokeAsync(input));
        var actual = Assert.IsType<JsonElement>(await generated.InvokeAsync(input));

        Assert.True(JsonElement.DeepEquals(legacy, actual));
        Assert.Equal("fixture.txt", actual.GetProperty("output").GetString());
        Assert.Equal("fixture", actual.GetProperty("metadata").GetProperty("resultLabel").GetString());
    }

    [Fact]
    public async Task NullCustomParametersBindEmptyAndKeepJsonMetadata()
    {
        var tool = CreateTool("fixture_json", arguments =>
            CodexToolResult.Succeeded(arguments.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new Dictionary<string, object?> { ["details"] = new JsonArray("fixture", 3) }));
        var generated = SourceGeneratedToolFunction.Fallback(tool, tool.Name, tool.Description);
        var result = Assert.IsType<JsonElement>(await generated.InvokeAsync(new AIFunctionArguments
        {
            ["parameters"] = null
        }));

        Assert.Equal("0", result.GetProperty("output").GetString());
        Assert.Equal("fixture", result.GetProperty("metadata").GetProperty("details")[0].GetString());
        Assert.Equal(3, result.GetProperty("metadata").GetProperty("details")[1].GetInt32());
        Assert.True(result.GetProperty("__icodex_tool_result").GetBoolean());
    }

    private static ICodexTool CreateTool(string name, Func<Dictionary<string, object?>, CodexToolResult> execute)
        => new DelegateCodexTool(name, "Read fixture.", ToolCategory.Read, [0, 1, 2, 3, 4],
            (arguments, _) => Task.FromResult(execute(arguments)));
}
