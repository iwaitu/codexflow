// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Runtime;
using CodexFlow.QueryRuntime.Engine.V2;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.Runtime;

public sealed class QreToolSchemaCompatibilityTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("hs_write")]
    [InlineData("run_command")]
    [InlineData("task_update")]
    [InlineData("workflow")]
    [InlineData("extension_tool")]
    public void KernelFactorySchemas_AreAdmitted(string name)
    {
        var tool = new Mock<ICodexTool>();
        tool.SetupGet(t => t.Capability).Returns(CodexFlow.Core.Execution.ToolCapability.ReadOnly);
        tool.SetupGet(value => value.Name).Returns(name);
        tool.SetupGet(value => value.Description).Returns("Schema inventory tool.");
        var function = CodexToolFunctionAdapterFactory.CreateAIFunction(tool.Object);
        var wrapped = new QreV2AdapterUtilities.TrustedRuntimeArgumentsAIFunction(function, null);
        output.WriteLine($"{name}: {wrapped.JsonSchema}");
        RuntimeToolArgumentNormalizer.ValidateSchema(wrapped.JsonSchema);
    }

    [Theory]
    [InlineData("{\"type\":\"insert_after\",\"newLines\":[\"hello\"]}")]
    [InlineData("[{\"type\":\"insert_after\"}]")]
    [InlineData("\"git status\"")]
    [InlineData("null")]
    [InlineData("true")]
    public async Task UnconstrainedParameter_PreservesArgumentBinding(string json)
    {
        var original = AIFunctionFactory.Create((object value) => value, "inspect");
        var before = original.JsonSchema.GetRawText();
        var wrapped = new QreV2AdapterUtilities.TrustedRuntimeArgumentsAIFunction(original, null);
        output.WriteLine(wrapped.JsonSchema.GetRawText());
        RuntimeToolArgumentNormalizer.ValidateSchema(wrapped.JsonSchema);
        var args = JsonSerializer.SerializeToElement(new { value = JsonSerializer.Deserialize<JsonElement>(json) });
        RuntimeToolArgumentNormalizer.NormalizeAndValidate(wrapped.JsonSchema, args);
        var result = await wrapped.InvokeAsync(new AIFunctionArguments
        {
            ["value"] = JsonSerializer.Deserialize<JsonElement>(json)
        });
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(json), JsonSerializer.SerializeToElement(result)));
        Assert.Equal(before, original.JsonSchema.GetRawText());
    }

    [Fact]
    public async Task WorkerWorkspace_OverridesLocalProjectUrlAndModelSuppliedRoots()
    {
        var root = Path.Combine(Path.GetTempPath(), "cf-root-binding-" + Guid.NewGuid().ToString("N"));
        var main = Path.Combine(root, "main");
        var shadow = Path.Combine(root, "shadow");
        Directory.CreateDirectory(main);
        Directory.CreateDirectory(shadow);
        try
        {
            var session = new CodexFlow.Core.Models.CodexSession
            {
                Id = "session", WorkspacePath = shadow, ProjectUrl = new Uri(main + Path.DirectorySeparatorChar)
            };
            using var bound = new CodexFlow.Core.Execution.TrustedExecutionContextAccessor().Begin(new()
            {
                UserId = "user", SessionId = session.Id, JobId = "job", TaskId = "task", AttemptId = "job:1", WorkspaceRoot = shadow
            });
            var inner = AIFunctionFactory.Create((string workspace_path, string project_root) => workspace_path + "|" + project_root, "inspect_roots");
            var wrapped = new QreV2AdapterUtilities.TrustedRuntimeArgumentsAIFunction(inner, session);
            var result = await wrapped.InvokeAsync(new AIFunctionArguments { ["workspace_path"] = main, ["project_root"] = main });
            Assert.Equal(shadow + "|" + shadow, result?.ToString());
            session.Id = "other-session";
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await wrapped.InvokeAsync(new AIFunctionArguments()));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void GeneratedArrayDictionaryAndJsonElementSchemas_AreAdmitted()
    {
        AIFunction[] tools =
        [
            AIFunctionFactory.Create((object[] operations) => operations.Length, "array"),
            AIFunctionFactory.Create((Dictionary<string, object> args) => args.Count, "dictionary"),
            AIFunctionFactory.Create((JsonElement value) => value.ValueKind.ToString(), "json")
        ];
        foreach (var tool in tools)
        {
            var wrapped = new QreV2AdapterUtilities.TrustedRuntimeArgumentsAIFunction(tool, null);
            output.WriteLine($"{tool.Name}: {wrapped.JsonSchema}");
            RuntimeToolArgumentNormalizer.ValidateSchema(wrapped.JsonSchema);
        }
    }

    [Fact]
    public void Adaptation_PreservesDataBooleansAndTypedConstraints()
    {
        var source = JsonSerializer.Deserialize<JsonElement>("""
            {"type":"object","additionalProperties":false,"required":["enabled","items"],
             "properties":{"enabled":{"type":"boolean","default":true,"enum":[true],"examples":[true]},
                           "items":{"type":"array","items":true}}}
            """);
        var wrapped = new QreV2AdapterUtilities.TrustedRuntimeArgumentsAIFunction(new SchemaFunction(source), null);
        var schema = wrapped.JsonSchema;
        RuntimeToolArgumentNormalizer.ValidateSchema(schema);
        Assert.True(JsonElement.DeepEquals(source.GetProperty("properties").GetProperty("enabled"), schema.GetProperty("properties").GetProperty("enabled")));
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(JsonValueKind.Object, schema.GetProperty("properties").GetProperty("items").GetProperty("items").ValueKind);
        RuntimeToolArgumentNormalizer.NormalizeAndValidate(schema, JsonSerializer.Deserialize<JsonElement>("""{"enabled":true,"items":[null,1,"x",{}]}"""));
        foreach (var invalid in new[] { "{}", """{"enabled":"true","items":[]}""", """{"enabled":false,"items":[]}""", """{"enabled":true,"items":[],"extra":1}""" })
        {
            Assert.Throws<RuntimeToolPreparationException>(() => RuntimeToolArgumentNormalizer.NormalizeAndValidate(schema, JsonSerializer.Deserialize<JsonElement>(invalid)));
        }
    }

    [Theory]
    [InlineData("""{"type":"object","properties":{"value":false}}""")]
    [InlineData("""{"type":"object","properties":{"value":{"type":"array","items":false}}}""")]
    [InlineData("true")]
    public void UnsupportedSchema_RemainsRejected(string json)
    {
        var wrapped = new QreV2AdapterUtilities.TrustedRuntimeArgumentsAIFunction(new SchemaFunction(JsonSerializer.Deserialize<JsonElement>(json)), null);
        Assert.Throws<ArgumentException>(() => RuntimeToolArgumentNormalizer.ValidateSchema(wrapped.JsonSchema));
    }

    [Fact]
    public void FrozenCatalog_SemanticallyEqualSchemaWithDifferentPropertyOrder_IsAccepted()
    {
        var first = new SchemaFunction(JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"path":{"type":"string"}}}"""));
        var reordered = new SchemaFunction(JsonSerializer.Deserialize<JsonElement>("""{"properties":{"path":{"type":"string"}},"type":"object"}"""));
        var request = new QueryRuntimeRequest
        {
            SessionId = "schema-order", EntryPoint = CodexFlow.Core.Telemetry.QueryLoopEntryPoint.GatewayMessageProcessor,
            InitialMessages = [], FrozenToolCatalog = [first], AvailableToolsProvider = () => [reordered]
        };
        var catalog = new FrozenRuntimeToolCatalog(request, [first]);
        Assert.Empty(catalog.SelectTools(null!, [], 0));
    }

    private sealed class SchemaFunction(JsonElement schema) : AIFunction
    {
        public override string Name => "schema_probe";
        public override JsonElement JsonSchema => schema;
        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
