// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using System.Text.Json.Nodes;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using Xunit;

namespace CodexFlow.Core.Tests.Agents;

/// <summary>
/// Live developer simulation (dependency-upgrade-compliant, local host): nine <c>tool_search</c> calls each put ~156 KB into the run's
/// history (the same result three times: Output, Metadata and Display), and the prompt passed the model's 262144-token window. The size
/// a tool declares for its result (<c>ResultSizeSoftLimitChars</c>) was read by nothing. Every tool result goes through
/// <see cref="CodexToolFunctionAdapterFactory.MarkResult"/>, so that is where its size is bounded.
/// </summary>
public class ToolResultSizeGuardTests
{
    private static string Text(JsonNode node, string name) => node[name]?.GetValue<string>() ?? string.Empty;

    [Fact]
    public void ASmallResult_IsLeftAsItIs()
    {
        var node = CodexToolFunctionAdapterFactory.MarkResult(CodexToolResult.Succeeded("ok", metadata: ToolResultMetadata.FromJson(new JsonObject { ["found"] = 1 }), summary: "s", display: "ok!"));

        Assert.Equal("ok", Text(node, "output"));
        Assert.Equal("ok!", Text(node, "display"));
        Assert.False(node["isOutputTruncated"]!.GetValue<bool>());
        Assert.Equal(1, node["metadata"]!["found"]!.GetValue<int>());
    }

    [Fact]
    public void AHugeOutput_IsCutToTheLimit_KeepingItsStartAndEnd_AndSaysSo()
    {
        var output = "START-" + new string('x', 200_000) + "-END";

        var node = CodexToolFunctionAdapterFactory.MarkResult(CodexToolResult.Succeeded(output));

        var json = node.ToJsonString();
        Assert.True(json.Length <= ToolResultSizeGuard.MaxEnvelopeChars + 1_000, $"envelope is {json.Length} chars");
        var kept = Text(node, "output");
        Assert.StartsWith("START-", kept);
        Assert.EndsWith("-END", kept);
        Assert.Contains("characters omitted", kept);
        Assert.True(node["isOutputTruncated"]!.GetValue<bool>());
    }

    [Fact]
    public void ADisplayThatRepeatsTheOutput_IsNotCarriedASecondTime()
    {
        var body = string.Join('\n', Enumerable.Range(0, 3_000).Select(i => $"line {i} " + new string('y', 20)));

        var node = CodexToolFunctionAdapterFactory.MarkResult(CodexToolResult.Succeeded(body, display: body));

        var json = node.ToJsonString();
        Assert.True(json.Length <= ToolResultSizeGuard.MaxEnvelopeChars + 1_000, $"envelope is {json.Length} chars");
        Assert.True(string.IsNullOrEmpty(Text(node, "display")) || Text(node, "display").Length < 500);
        Assert.Contains("line 0 ", Text(node, "output"));
    }

    [Fact]
    public void HugeMetadata_IsReplacedByANote_WhileTheOutputIsKept()
    {
        var metadata = ToolResultMetadata.FromJson(new JsonObject
        {
            ["tools"] = new JsonArray(Enumerable.Range(0, 400).Select(i =>
                (JsonNode?)new JsonObject { ["name"] = "tool_" + i, ["description"] = new string('d', 200) }).ToArray())
        });

        var node = CodexToolFunctionAdapterFactory.MarkResult(CodexToolResult.Succeeded("short answer", metadata: metadata));

        Assert.Equal("short answer", Text(node, "output"));
        Assert.True(node["metadata"]!["omitted"]!.GetValue<bool>());
        Assert.True(node["metadata"]!["originalChars"]!.GetValue<int>() > ToolResultSizeGuard.MaxMetadataChars);
        Assert.True(node.ToJsonString().Length < ToolResultSizeGuard.MaxMetadataChars);
    }

    [Fact]
    public void TheStatusAndTheHintSurvive_WhateverIsCut()
    {
        var result = CodexToolResult.Error(new string('e', 300_000), systemHint: "retry with a smaller range", requiredToolName: "ivilson_read", toolCallRequired: true);

        var node = CodexToolFunctionAdapterFactory.MarkResult(result);

        Assert.Equal((int)ToolResultStatus.Failed, node["status"]!.GetValue<int>());   // the envelope carries the enum as a number
        Assert.Equal("retry with a smaller range", Text(node, "systemHint"));
        Assert.True(node["__icodex_tool_result"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(ToolResultStatus.Success)]
    [InlineData(ToolResultStatus.Failed)]
    [InlineData(ToolResultStatus.BlockedByGuardrail)]
    public void DisposedMetadataDoesNotChangeTheToolResult(ToolResultStatus status)
    {
        JsonElement borrowed;
        using (var document = JsonDocument.Parse("""{"items":["value",{"other":1}]}"""))
        {
            borrowed = document.RootElement;
        }

        var result = new CodexToolResult
        {
            Status = status,
            Output = "actual output",
            Summary = "short summary",
            Display = "display text",
            IsOutputTruncated = true,
            SystemHint = "inspect the file",
            SystemHintDetail = new ToolSystemHint("inspect the file", "ivilson_read", true),
            Metadata = borrowed
        };

        var envelope = CodexToolFunctionAdapterFactory.MarkResult(result);

        Assert.Equal((int)status, envelope["status"]!.GetValue<int>());
        Assert.Equal(result.Output, Text(envelope, "output"));
        Assert.Equal(result.Summary, Text(envelope, "summary"));
        Assert.Equal(result.Display, Text(envelope, "display"));
        Assert.True(envelope["isOutputTruncated"]!.GetValue<bool>());
        Assert.Equal(result.SystemHint, Text(envelope, "systemHint"));
        Assert.Equal("ivilson_read", Text(envelope["systemHintDetail"]!, "requiredToolName"));
        Assert.True(envelope["systemHintDetail"]!["toolCallRequired"]!.GetValue<bool>());
        Assert.True(envelope["metadata"]!["omitted"]!.GetValue<bool>());
        Assert.Equal("metadata_document_disposed", Text(envelope["metadata"]!, "reason"));
    }

    [Fact]
    public void MetadataOwnsBorrowedJsonAndTreatsUndefinedAsAbsent()
    {
        CodexToolResult result;
        using (var document = JsonDocument.Parse("""{"items":[null,{"arbitrary-Key":7}]}"""))
        {
            result = CodexToolResult.Succeeded("done", document.RootElement);
        }

        var envelope = CodexToolFunctionAdapterFactory.MarkResult(result);
        Assert.Equal(7, envelope["metadata"]!["items"]![1]!["arbitrary-Key"]!.GetValue<int>());
        Assert.Null(envelope["metadata"]!["items"]![0]);

        result.Metadata = default(JsonElement);
        Assert.Null(CodexToolFunctionAdapterFactory.MarkResult(result)["metadata"]);
    }

    [Fact]
    public void InvalidJsonNodeMetadataIsOmittedWithoutChangingTheOutcome()
    {
        var metadata = ToolResultMetadata.FromJson(new JsonObject { ["number"] = double.NaN });
        var envelope = CodexToolFunctionAdapterFactory.MarkResult(CodexToolResult.Succeeded("done", metadata));

        Assert.Equal((int)ToolResultStatus.Success, envelope["status"]!.GetValue<int>());
        Assert.Equal("done", Text(envelope, "output"));
        Assert.True(envelope["metadata"]!["omitted"]!.GetValue<bool>());
        Assert.Equal("serialization_failed", Text(envelope["metadata"]!, "reason"));
    }

    // The gateway wraps its tools' results with BuildWrappedPayload (a string envelope with the output and a display copy of it), not with
    // MarkResult. The audited tool_search results of the failed run had this shape.
    [Fact]
    public void TheGatewaysEnvelope_IsBoundedToo_AndCarriesTheOutputOnce()
    {
        var output = "{\"found\":23,\"tools\":[" + string.Join(',', Enumerable.Range(0, 400).Select(i => $"{{\"name\":\"tool_{i}\",\"description\":\"{new string('d', 150)}\"}}")) + "]}";

        var payload = CodexFlow.Core.Runtime.ToolResultTextFormatter.BuildWrappedPayload(CodexToolResult.Succeeded(output), "tool_search");
        var json = System.Text.Json.JsonSerializer.Serialize(payload);

        Assert.True(json.Length <= ToolResultSizeGuard.MaxEnvelopeChars + 1_000, $"envelope is {json.Length} chars");
        Assert.True((bool)payload["truncated"]!);
        Assert.Contains("characters omitted", (string)payload["output"]!);
        Assert.Equal(true, payload["__icodex_tool_result"]);
        Assert.Equal("Success", payload["status"]);
    }

    [Fact]
    public void TheGatewaysEnvelope_OfASmallResult_IsUnchanged()
    {
        var payload = CodexFlow.Core.Runtime.ToolResultTextFormatter.BuildWrappedPayload(CodexToolResult.Succeeded("done", summary: "s"), "t");

        Assert.Equal("done", payload["output"]);
        Assert.Equal("done", payload["display"]);
        Assert.False((bool)payload["truncated"]!);
    }

    [Fact]
    public void TheGatewaysEnvelope_BoundsEscapedText_AndPreservesControlFields()
    {
        var output = "START-" + new string('中', 30_000) + "-END";
        var result = CodexToolResult.Error(
            output, display: output, systemHint: "retry", requiredToolName: "ivilson_read", toolCallRequired: true);

        var payload = CodexFlow.Core.Runtime.ToolResultTextFormatter.BuildWrappedPayload(result, "t");

        Assert.True(System.Text.Json.JsonSerializer.Serialize(payload).Length <= ToolResultSizeGuard.MaxEnvelopeChars);
        Assert.Equal(true, payload["__ICODEX_TOOL_RESULT"]);
        Assert.Equal("Failed", payload["status"]);
        Assert.Equal("retry", payload["system_hint"]);
        Assert.Equal("ivilson_read", payload["required_tool_name"]);
        Assert.Equal(true, payload["tool_call_required"]);
        Assert.True(Assert.IsType<bool>(payload["truncated"]));
        Assert.Null(payload["display"]);
        var bounded = Assert.IsType<string>(payload["output"]);
        Assert.StartsWith("START-", bounded);
        Assert.EndsWith("-END", bounded);
    }

    [Fact]
    public void ChineseText_IsMeasuredByWhatItCostsOnTheWire_NotByItsLength()
    {
        // A CJK character written as \uXXXX is six characters on the wire; the limit counts that, not the one char in memory.
        var output = new string('中', 30_000);

        var node = CodexToolFunctionAdapterFactory.MarkResult(CodexToolResult.Succeeded(output));

        Assert.True(node.ToJsonString().Length <= ToolResultSizeGuard.MaxEnvelopeChars + 1_000);
        Assert.True(node["isOutputTruncated"]!.GetValue<bool>() || Text(node, "output").Length <= ToolResultSizeGuard.MaxEnvelopeChars);
    }
}
