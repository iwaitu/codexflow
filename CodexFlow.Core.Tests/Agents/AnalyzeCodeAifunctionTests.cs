// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using System.Text.Json.Nodes;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodexFlow.Core.Tests.Agents;

public sealed class AnalyzeCodeAifunctionTests
{
    private const string ToolName = "analyze_code";
    private const string CodeWithTypeError = "public class Example\n{\n    public int Read() => \"wrong\";\n}";

    [Fact]
    public void ToolDefinitionMatchesTheLegacyFactorySchema()
    {
        const string description = "Analyze a C# code snippet.";
        var legacy = AIFunctionFactory.Create(
            static (string code, CancellationToken ct = default) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<JsonNode>(new JsonObject { ["output"] = code });
            },
            new AIFunctionFactoryOptions { Name = ToolName, Description = description });
        var current = CreateFunction((_, _) => Task.FromResult(CodexToolResult.Succeeded("ok")), description);

        Assert.Equal(legacy.Name, current.Name);
        Assert.Equal(legacy.Description, current.Description);
        Assert.True(JsonElement.DeepEquals(legacy.JsonSchema, current.JsonSchema),
            $"Legacy: {legacy.JsonSchema}{Environment.NewLine}Current: {current.JsonSchema}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForwardedCodeProducesTheRoslynDiagnosticEnvelope(bool useJsonElement)
    {
        var referenceDirectory = Path.Combine(AppContext.BaseDirectory, "code-analysis", "net10.0");
        var tool = new RoslynCodeAnalysisTool(new RoslynCodeAnalysisService(NullLogger.Instance, referenceDirectory));
        var function = CodexToolFunctionAdapterFactory.CreateAIFunction(tool);
        var arguments = new AIFunctionArguments
        {
            ["code"] = useJsonElement ? (object)JsonSerializer.SerializeToElement(CodeWithTypeError) : CodeWithTypeError
        };

        var envelope = AsJson(await function.InvokeAsync(arguments));

        Assert.True(envelope.GetProperty("__icodex_tool_result").GetBoolean());
        Assert.Equal((int)ToolResultStatus.Success, envelope.GetProperty("status").GetInt32());
        Assert.Contains("CS0029", envelope.GetProperty("output").GetString()!, StringComparison.Ordinal);
        var diagnostic = Assert.Single(envelope.GetProperty("metadata").EnumerateArray());
        Assert.Equal("CS0029", diagnostic.GetProperty("id").GetString());
        Assert.Equal("Error", diagnostic.GetProperty("severity").GetString());
        Assert.Equal("Cannot implicitly convert type 'string' to 'int'", diagnostic.GetProperty("message").GetString());
        Assert.Equal(3, diagnostic.GetProperty("line").GetInt32());
        Assert.Equal(26, diagnostic.GetProperty("column").GetInt32());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("integer")]
    [InlineData("json-integer")]
    public async Task InvalidCodeArgumentReturnsMarkedFailureWithoutInvokingTheTool(string input)
    {
        var invocations = 0;
        var function = CreateFunction((_, _) =>
        {
            invocations++;
            return Task.FromResult(CodexToolResult.Succeeded("unexpected"));
        });
        var arguments = new AIFunctionArguments();
        if (input != "missing")
        {
            arguments["code"] = input switch
            {
                "null" => null,
                "integer" => 17,
                "json-integer" => JsonSerializer.SerializeToElement(17),
                _ => throw new ArgumentOutOfRangeException(nameof(input))
            };
        }

        var envelope = AsJson(await function.InvokeAsync(arguments));

        Assert.Equal(0, invocations);
        Assert.True(envelope.GetProperty("__icodex_tool_result").GetBoolean());
        Assert.Equal((int)ToolResultStatus.Failed, envelope.GetProperty("status").GetInt32());
        Assert.Contains("code", envelope.GetProperty("output").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TypedResultPreservesTheLegacyEnvelopeAndSystemHint()
    {
        using var cancellation = new CancellationTokenSource();
        var result = CodexToolResult.Succeeded(
            "Diagnostic output",
            metadata: new List<CodeDiagnostic> { new("CS0029", "Type mismatch", "Error", 7, 11) },
            summary: "Diagnostic summary",
            display: "Diagnostic display",
            systemHint: "Inspect the reported line.",
            requiredToolName: "analyze_code",
            toolCallRequired: true);
        var function = CreateFunction((arguments, ct) =>
        {
            Assert.Equal(cancellation.Token, ct);
            Assert.Equal("public class C { }", Assert.IsType<string>(arguments["code"]));
            return Task.FromResult(result);
        });
        var expected = JsonSerializer.SerializeToElement(CodexToolFunctionAdapterFactory.MarkResult(result));

        var actual = AsJson(await function.InvokeAsync(
            new AIFunctionArguments { ["code"] = "public class C { }" }, cancellation.Token));

        Assert.True(JsonElement.DeepEquals(expected, actual),
            $"Legacy: {expected}{Environment.NewLine}Current: {actual}");
    }

    [Fact]
    public async Task CanceledInvocationDoesNotRunTheTool()
    {
        var invocations = 0;
        var function = CreateFunction((_, _) =>
        {
            invocations++;
            return Task.FromResult(CodexToolResult.Succeeded("unexpected"));
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await function.InvokeAsync(new AIFunctionArguments { ["code"] = "public class C { }" }, cancellation.Token));

        Assert.Equal(0, invocations);
    }

    [Fact]
    public async Task OversizeDiagnosticOutputIsStillBounded()
    {
        var output = "START-" + new string('x', 2 * ToolResultSizeGuard.MaxEnvelopeChars) + "-END";
        var function = CreateFunction((_, _) => Task.FromResult(CodexToolResult.Succeeded(output)));

        var envelope = AsJson(await function.InvokeAsync(new AIFunctionArguments { ["code"] = "public class C { }" }));

        Assert.True(envelope.GetProperty("__icodex_tool_result").GetBoolean());
        Assert.True(envelope.GetProperty("isOutputTruncated").GetBoolean());
        Assert.True(envelope.GetRawText().Length <= ToolResultSizeGuard.MaxEnvelopeChars + 1_000);
        var shortened = Assert.IsType<string>(envelope.GetProperty("output").GetString());
        Assert.StartsWith("START-", shortened);
        Assert.EndsWith("-END", shortened);
    }

    private static AIFunction CreateFunction(
        Func<Dictionary<string, object?>, CancellationToken, Task<CodexToolResult>> execute,
        string description = "Analyze a C# code snippet.")
        => CodexToolFunctionAdapterFactory.CreateAIFunction(new DelegateCodexTool(
            ToolName, description, ToolCategory.Analysis, [1, 3, 4], execute));

    private static JsonElement AsJson(object? value)
        => value is JsonElement element ? element : JsonSerializer.SerializeToElement(Assert.IsAssignableFrom<JsonNode>(value));
}
