// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using Xunit;

namespace CodexFlow.Core.Tests.Agents;

public class GuardrailTargetPathAndResultMarkerTests
{
    [Fact]
    public void Hashline_HsWrite_UsesFilePathOnly()
    {
        var args = new Dictionary<string, object?>
        {
            ["filePath"] = "src/Real.cs",
            ["path"] = "decoy/ignored-by-hs-write.cs"
        };

        Assert.Equal("src/Real.cs", DefaultCodexGuardrail.ExtractTargetPath("hs_write", args));
        Assert.Equal("src/Real.cs", DefaultCodexGuardrail.ExtractTargetPath("HS_WRITE", args));
        Assert.Null(DefaultCodexGuardrail.ExtractTargetPath("hs_write", new Dictionary<string, object?> { ["path"] = "x.cs" }));
    }

    [Theory]
    [InlineData("write_file")]
    [InlineData("edit_file")]
    [InlineData("apply_patch")]
    [InlineData("delete_file")]
    public void OtherTools_IgnoreDecoyFilePath_AndKeepExistingPrecedence(string tool)
    {
        // A benign filePath decoy must not shadow the protected path the tool will actually consume.
        var args = new Dictionary<string, object?>
        {
            ["filePath"] = "docs/benign.md",
            ["path"] = "src/Core/Protected.cs",
            ["file_path"] = "other.cs",
            ["target_file"] = "t.cs"
        };
        Assert.Equal("src/Core/Protected.cs", DefaultCodexGuardrail.ExtractTargetPath(tool, args));

        Assert.Equal("other.cs", DefaultCodexGuardrail.ExtractTargetPath(tool, new Dictionary<string, object?>
        {
            ["filePath"] = "docs/benign.md",
            ["file_path"] = "other.cs"
        }));
        Assert.Null(DefaultCodexGuardrail.ExtractTargetPath(tool, new Dictionary<string, object?> { ["filePath"] = "docs/benign.md" }));
    }

    [Fact]
    public void FactoryResults_CarryExplicitMarker_AndKeepCodexToolResultShape()
    {
        foreach (var result in new[] { CodexToolResult.Succeeded("ok"), CodexToolResult.Error("bad") })
        {
            var node = CodexToolFunctionAdapterFactory.MarkResult(result);
            using var doc = JsonDocument.Parse(node.ToJsonString());
            var root = doc.RootElement;
            Assert.True(root.GetProperty("__icodex_tool_result").GetBoolean());
            Assert.True(root.TryGetProperty("Status", out _) || root.TryGetProperty("status", out _));
            Assert.True(root.TryGetProperty("Output", out _) || root.TryGetProperty("output", out _));
        }
    }

    [Fact]
    public async Task FactoryCreatedFunction_SerializedOutputIsMarked()
    {
        var tool = new DelegateCodexTool("list_workspace", "list", ToolCategory.Forge, new List<int> { 1 },
            (_, _) => Task.FromResult(CodexToolResult.Error("nope")));
        var function = CodexToolFunctionAdapterFactory.CreateAIFunction(tool);

        var value = await function.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments());
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value));
        Assert.True(doc.RootElement.GetProperty("__icodex_tool_result").GetBoolean());
    }
}
