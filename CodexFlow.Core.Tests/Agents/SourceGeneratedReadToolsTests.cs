// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using System.Text.Json.Nodes;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Hashline.Abstractions;
using CodexFlow.Core.Hashline.Infrastructure;
using CodexFlow.Core.Hashline.Models;
using CodexFlow.Core.Hashline.Services;
using CodexFlow.Core.Models;
using CodexFlow.Core.Runtime;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodexFlow.Core.Tests.Agents;

public sealed class SourceGeneratedReadToolsTests
{
    private const string Read = "ivilson_read";
    private const string Search = "search_in_files";
    private const string Description = "Read fixture.";

    [Theory]
    [InlineData(Read)]
    [InlineData(Search)]
    public void ToolSchemaAndReturnSchemaMatchTheLegacyFactory(string toolName)
    {
        var options = new AIFunctionFactoryOptions { Name = toolName, Description = Description };
        var legacy = toolName == Read
            ? AIFunctionFactory.Create(
                (string path, string? mode = null, int? start_line = null, int? end_line = null,
                 int? window_start_line = null, int? window_line_count = null,
                 string? session_id = null, string? workspace_path = null, string? project_root = null,
                 CancellationToken ct = default) => Task.FromResult<JsonNode>(new JsonObject()), options)
            : AIFunctionFactory.Create(
                (string pattern, string? path = null, bool? ignore_case = null, int? max_results = null,
                 string? session_id = null, string? workspace_path = null, string? project_root = null,
                 CancellationToken ct = default) => Task.FromResult<JsonNode>(new JsonObject()), options);
        var actual = CreateFunction(toolName, (_, _) => CodexToolResult.Succeeded("ok"));

        Assert.Equal(legacy.Name, actual.Name);
        Assert.Equal(legacy.Description, actual.Description);
        Assert.True(JsonElement.DeepEquals(legacy.JsonSchema, actual.JsonSchema),
            $"Legacy: {legacy.JsonSchema}{Environment.NewLine}Current: {actual.JsonSchema}");
        Assert.True(legacy.ReturnJsonSchema.HasValue);
        Assert.True(actual.ReturnJsonSchema.HasValue);
        Assert.True(JsonElement.DeepEquals(legacy.ReturnJsonSchema.Value, actual.ReturnJsonSchema.Value));
    }

    [Fact]
    public async Task ReadBindsLegacyIntegerFormatsAndOmitsExplicitNullOptions()
    {
        Dictionary<string, object?>? captured = null;
        var function = CreateFunction(Read, (arguments, _) =>
        {
            captured = arguments;
            return CodexToolResult.Succeeded("ok");
        });

        AssertSucceeded(AsJson(await function.InvokeAsync(new AIFunctionArguments
        {
            ["path"] = JsonSerializer.SerializeToElement("input.txt"),
            ["mode"] = JsonSerializer.SerializeToElement<string?>(null),
            ["start_line"] = 2L,
            ["end_line"] = "3",
            ["window_start_line"] = JsonSerializer.SerializeToElement("4"),
            ["window_line_count"] = JsonSerializer.SerializeToElement(5),
            ["session_id"] = "session",
            ["project_root"] = " "
        })));

        Assert.NotNull(captured);
        Assert.Equal("input.txt", captured["path"]);
        Assert.Equal(2, Assert.IsType<int>(captured["start_line"]));
        Assert.Equal(3, Assert.IsType<int>(captured["end_line"]));
        Assert.Equal(4, Assert.IsType<int>(captured["window_start_line"]));
        Assert.Equal(5, Assert.IsType<int>(captured["window_line_count"]));
        Assert.Equal("session", captured["session_id"]);
        Assert.DoesNotContain("mode", captured.Keys);
        Assert.DoesNotContain("project_root", captured.Keys);
    }

    [Fact]
    public async Task SearchUsesLegacyDefaultsAndBindsExplicitOptions()
    {
        var invocations = new List<Dictionary<string, object?>>();
        var function = CreateFunction(Search, (arguments, _) =>
        {
            invocations.Add(arguments);
            return CodexToolResult.Succeeded("ok");
        });

        AssertSucceeded(AsJson(await function.InvokeAsync(new AIFunctionArguments
        {
            ["pattern"] = "needle",
            ["path"] = null,
            ["ignore_case"] = JsonSerializer.SerializeToElement<bool?>(null),
            ["max_results"] = null
        })));
        AssertSucceeded(AsJson(await function.InvokeAsync(new AIFunctionArguments
        {
            ["pattern"] = "Needle",
            ["path"] = "source",
            ["ignore_case"] = JsonSerializer.SerializeToElement(false),
            ["max_results"] = JsonSerializer.SerializeToElement("7")
        })));

        Assert.Equal(".", invocations[0]["path"]);
        Assert.True(Assert.IsType<bool>(invocations[0]["ignore_case"]));
        Assert.Equal(50, Assert.IsType<int>(invocations[0]["max_results"]));
        Assert.Equal("source", invocations[1]["path"]);
        Assert.False(Assert.IsType<bool>(invocations[1]["ignore_case"]));
        Assert.Equal(7, Assert.IsType<int>(invocations[1]["max_results"]));
    }

    [Theory]
    [InlineData(Read, "path")]
    [InlineData(Read, "start_line")]
    [InlineData(Read, "window_line_count")]
    [InlineData(Search, "pattern")]
    [InlineData(Search, "ignore_case")]
    [InlineData(Search, "max_results")]
    public async Task InvalidArgumentReturnsMarkedFailureWithoutDispatch(string toolName, string field)
    {
        var called = false;
        var function = CreateFunction(toolName, (_, _) =>
        {
            called = true;
            return CodexToolResult.Succeeded("unexpected");
        });
        var arguments = new AIFunctionArguments { [toolName == Read ? "path" : "pattern"] = "fixture" };
        arguments[field] = field switch
        {
            "path" => null,
            "pattern" => JsonSerializer.SerializeToElement(new[] { "not a string" }),
            "start_line" => (long)int.MaxValue + 1,
            "window_line_count" => JsonSerializer.SerializeToElement("2147483648"),
            "ignore_case" => JsonSerializer.SerializeToElement("false"),
            _ => JsonSerializer.SerializeToElement(new { value = 50 })
        };

        var envelope = AsJson(await function.InvokeAsync(arguments));

        Assert.False(called);
        AssertFailed(envelope);
        Assert.Contains(field, envelope.GetProperty("output").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActualReadAndSearchRespectTrustedRootAndReturnCompatibleMetadata()
    {
        var fixture = Directory.CreateTempSubdirectory("codexflow-aot-read-tools-");
        try
        {
            var workspace = Directory.CreateDirectory(Path.Combine(fixture.FullName, "workspace")).FullName;
            var other = Directory.CreateDirectory(Path.Combine(fixture.FullName, "other")).FullName;
            var file = Path.Combine(workspace, "input.txt");
            await File.WriteAllLinesAsync(file, ["alpha", "NEEDLE", "omega"]);
            await File.WriteAllTextAsync(Path.Combine(other, "input.txt"), "other fixture");

            var session = new CodexSession { Id = "session", WorkspacePath = workspace };
            using var execution = new TrustedExecutionContextAccessor().Begin(new()
            {
                UserId = "user", SessionId = session.Id, JobId = "job", TaskId = "task",
                AttemptId = "job:1", WorkspaceRoot = workspace
            });
            var read = Trusted(new ReadFileTool(NullLogger<ReadFileTool>.Instance), session);
            var search = Trusted(new SearchInFilesTool(NullLogger<SearchInFilesTool>.Instance), session);

            var readResult = AsJson(await read.InvokeAsync(new AIFunctionArguments
            {
                ["path"] = "input.txt", ["start_line"] = 2, ["end_line"] = 2,
                ["workspace_path"] = other, ["project_root"] = other, ["session_id"] = "supplied"
            }));
            AssertSucceeded(readResult);
            Assert.Contains("NEEDLE", readResult.GetProperty("output").GetString()!, StringComparison.Ordinal);
            Assert.DoesNotContain("omega", readResult.GetProperty("output").GetString()!, StringComparison.Ordinal);
            Assert.True(JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(new
                {
                    filePath = "input.txt", lines = 3, size = new FileInfo(file).Length,
                    startLine = 2, endLine = 2, isTruncated = true
                }),
                readResult.GetProperty("metadata")));

            var match = AsJson(await search.InvokeAsync(new AIFunctionArguments
            {
                ["pattern"] = "needle", ["workspace_path"] = other, ["project_root"] = other
            }));
            AssertSucceeded(match);
            Assert.Contains("input.txt:2: NEEDLE", match.GetProperty("output").GetString()!, StringComparison.Ordinal);
            Assert.True(JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(new
                {
                    matchCount = 1, pattern = "needle", requestedPath = ".", effectivePath = ".", fellBackToRoot = false
                }),
                match.GetProperty("metadata")));

            var noMatch = AsJson(await search.InvokeAsync(new AIFunctionArguments
            {
                ["pattern"] = "needle", ["ignore_case"] = false
            }));
            AssertSucceeded(noMatch);
            Assert.Contains("No matches", noMatch.GetProperty("output").GetString()!, StringComparison.Ordinal);
            Assert.Equal(JsonValueKind.Null, noMatch.GetProperty("metadata").ValueKind);

            var escapedRead = AsJson(await read.InvokeAsync(new AIFunctionArguments { ["path"] = "../other/input.txt" }));
            var escapedSearch = AsJson(await search.InvokeAsync(new AIFunctionArguments
            {
                ["pattern"] = "other fixture", ["path"] = "../other"
            }));
            AssertFailed(escapedRead);
            AssertFailed(escapedSearch);
        }
        finally
        {
            LocalExecutionService.TryDeleteDirectory(fixture.FullName);
        }
    }

    [Fact]
    public async Task ActualHashlineReadRetainsSnapshotMetadata()
    {
        var workspace = Directory.CreateTempSubdirectory("codexflow-aot-hashline-");
        try
        {
            await File.WriteAllLinesAsync(Path.Combine(workspace.FullName, "input.txt"), ["alpha", "beta", "gamma"]);
            var options = new HashlineOptions { Enabled = true, EnableAuditLogging = false };
            var read = CodexToolFunctionAdapterFactory.CreateAIFunction(new ReadFileTool(
                NullLogger<ReadFileTool>.Instance, CreateHashlineService(options), options));

            var result = AsJson(await read.InvokeAsync(new AIFunctionArguments
            {
                ["path"] = "input.txt", ["mode"] = "hashline", ["workspace_path"] = workspace.FullName,
                ["window_start_line"] = 2, ["window_line_count"] = 1
            }));

            AssertSucceeded(result);
            var metadata = result.GetProperty("metadata");
            Assert.Equal("input.txt", metadata.GetProperty("filePath").GetString());
            Assert.False(string.IsNullOrWhiteSpace(metadata.GetProperty("snapshotId").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(metadata.GetProperty("fileFingerprint").GetString()));
            Assert.Equal(1, metadata.GetProperty("lines").GetInt32());
            Assert.Equal(3, metadata.GetProperty("totalLineCount").GetInt32());
            Assert.Equal(2, metadata.GetProperty("windowStartLine").GetInt32());
            Assert.Equal(2, metadata.GetProperty("windowEndLine").GetInt32());
            Assert.True(metadata.GetProperty("isPartialWindow").GetBoolean());
            Assert.Contains("beta", metadata.GetProperty("renderedText").GetString()!, StringComparison.Ordinal);
        }
        finally
        {
            LocalExecutionService.TryDeleteDirectory(workspace.FullName);
        }
    }

    [Theory]
    [InlineData(Read, "path")]
    [InlineData(Search, "pattern")]
    public async Task JitCustomMetadataKeepsTheLegacyResultShape(string toolName, string requiredField)
    {
        var result = CodexToolResult.Succeeded(
            "custom output",
            metadata: new { FilePath = "fixture.txt", MatchDetails = new[] { new { Line = 2, Text = "needle" } } });
        var function = CreateFunction(toolName, (_, _) => result);

        var legacy = JsonSerializer.SerializeToElement(CodexToolFunctionAdapterFactory.MarkResult(result));
        var actual = AsJson(await function.InvokeAsync(new AIFunctionArguments { [requiredField] = "fixture" }));

        AssertSucceeded(actual);
        Assert.True(JsonElement.DeepEquals(legacy, actual),
            $"Legacy: {legacy}{Environment.NewLine}Current: {actual}");
    }

    [Theory]
    [InlineData(Read, "path")]
    [InlineData(Search, "pattern")]
    public async Task CancellationAndResultSizeGuardStillApply(string toolName, string requiredField)
    {
        var invocations = 0;
        var output = "START-" + new string('x', 2 * ToolResultSizeGuard.MaxEnvelopeChars) + "-END";
        var function = CreateFunction(toolName, (_, _) =>
        {
            invocations++;
            return CodexToolResult.Succeeded(output);
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var arguments = new AIFunctionArguments { [requiredField] = "fixture" };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await function.InvokeAsync(arguments, cancellation.Token));
        Assert.Equal(0, invocations);

        var result = AsJson(await function.InvokeAsync(arguments));
        AssertSucceeded(result);
        Assert.Equal(1, invocations);
        Assert.True(result.GetProperty("isOutputTruncated").GetBoolean());
        Assert.True(result.GetRawText().Length <= ToolResultSizeGuard.MaxEnvelopeChars + 1_000);
        var shortened = Assert.IsType<string>(result.GetProperty("output").GetString());
        Assert.StartsWith("START-", shortened);
        Assert.EndsWith("-END", shortened);
    }

    private static AIFunction CreateFunction(
        string name,
        Func<Dictionary<string, object?>, CancellationToken, CodexToolResult> execute)
        => CodexToolFunctionAdapterFactory.CreateAIFunction(new DelegateCodexTool(
            name, Description, ToolCategory.Read, [0, 1, 2, 3, 4],
            (arguments, ct) => Task.FromResult(execute(arguments, ct))));

    private static AIFunction Trusted(ICodexTool tool, CodexSession session)
        => new QreV2AdapterUtilities.TrustedRuntimeArgumentsAIFunction(
            CodexToolFunctionAdapterFactory.CreateAIFunction(tool), session);

    private static JsonElement AsJson(object? value)
        => value is JsonElement element ? element : JsonSerializer.SerializeToElement(Assert.IsAssignableFrom<JsonNode>(value));

    private static void AssertSucceeded(JsonElement result)
    {
        Assert.True(result.GetProperty("__icodex_tool_result").GetBoolean());
        Assert.Equal((int)ToolResultStatus.Success, result.GetProperty("status").GetInt32());
    }

    private static void AssertFailed(JsonElement result)
    {
        Assert.True(result.GetProperty("__icodex_tool_result").GetBoolean());
        Assert.Equal((int)ToolResultStatus.Failed, result.GetProperty("status").GetInt32());
    }

    private static IHashlineFileService CreateHashlineService(HashlineOptions options)
    {
        var normalizer = new TextNormalizer();
        var fingerprint = new Sha256FingerprintProvider();
        var auditLogger = new CodexFlow.Core.Tests.Hashline.NullAuditLogger();
        var reader = new SnapshotReader(
            normalizer, new Sha256LineHasher(), fingerprint, new FileSystemGuard(),
            new EncodingDetector(), auditLogger, options, NullLogger<SnapshotReader>.Instance);

        return new HashlineFileService(
            reader, new EditRequestValidator(options), new EditApplier(), new DiffRenderer(),
            new AtomicFileWriter(NullLogger<AtomicFileWriter>.Instance), fingerprint, auditLogger,
            normalizer, options, NullLogger<HashlineFileService>.Instance);
    }
}
