// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.LanguageServices;
using CodexFlow.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Xunit;

namespace CodexFlow.Core.Tests.LanguageServices;

public class LspToolAdapterTests
{
    [Fact]
    public async Task TypedLspAdapter_ShouldPreserveFlatArguments()
    {
        var tool = new RecordingTool("lsp_get_diagnostics");
        var function = CodexToolFunctionAdapterFactory.CreateAIFunction(tool);
        var args = new Dictionary<string, object?>
        {
            ["path"] = "src/App.cs",
            ["language"] = "csharp",
            ["worker_id"] = "forge-worker",
            ["session_id"] = "session-123",
            ["workspace_path"] = "/tmp/ws",
            ["project_root"] = "/tmp/ws"
        };

        await function.InvokeAsync(new AIFunctionArguments(args), CancellationToken.None);

        Assert.NotNull(tool.LastArguments);
        Assert.Equal("src/App.cs", tool.LastArguments!["path"]);
        Assert.Equal("csharp", tool.LastArguments["language"]);
        Assert.Equal("forge-worker", tool.LastArguments["worker_id"]);
        Assert.Equal("/tmp/ws", tool.LastArguments["workspace_path"]);
        Assert.Equal("/tmp/ws", tool.LastArguments["project_root"]);
    }

    [Fact]
    public async Task LspGetDiagnosticsTool_ShouldTruncateLongDiagnosticOutput()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "codexflow-lsp-tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspacePath, "src"));
        var filePath = Path.Combine(workspacePath, "src", "App.cs");
        var lines = Enumerable.Range(1, 30)
            .Select(index => $"// BROKEN_DIAGNOSTIC #{index}")
            .ToList();
        lines.Insert(0, "public class App");
        await File.WriteAllTextAsync(filePath, string.Join(Environment.NewLine, lines));

        try
        {
            var registry = CreateRegistry();
            var factory = new DefaultLanguageServiceSessionFactory(registry, NullLogger<DefaultLanguageServiceSessionFactory>.Instance);
            var tool = new LspGetDiagnosticsTool(registry, factory, NullLogger<LspGetDiagnosticsTool>.Instance);
            var function = CodexToolFunctionAdapterFactory.CreateAIFunction(tool);

            var result = await function.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["path"] = "src/App.cs",
                ["language"] = "csharp",
                ["worker_id"] = "explore-worker",
                ["workspace_path"] = workspacePath,
                ["project_root"] = workspacePath
            }), CancellationToken.None);

            var json = Assert.IsType<JsonElement>(result);
            var output = json.GetProperty("output").GetString();
            Assert.NotNull(output);
            Assert.Contains("diagnostics_count: 30", output);
            Assert.Contains("truncated 10 additional diagnostics", output);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public async Task LspGetDiagnosticsTool_ShouldKeepOnlyFirstTwentyDiagnosticEntries()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "codexflow-lsp-tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspacePath, "src"));
        var filePath = Path.Combine(workspacePath, "src", "App.cs");
        var lines = Enumerable.Range(1, 30)
            .Select(index => $"// BROKEN_DIAGNOSTIC #{index}")
            .ToList();
        lines.Insert(0, "public class App");
        await File.WriteAllTextAsync(filePath, string.Join(Environment.NewLine, lines));

        try
        {
            var output = await InvokeDiagnosticsToolAsync(workspacePath, "src/App.cs");

            Assert.Contains("src/App.cs:2:4", output);
            Assert.Contains("src/App.cs:21:4", output);
            Assert.DoesNotContain("src/App.cs:22:4", output);
            Assert.DoesNotContain("src/App.cs:31:4", output);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public async Task LspGetDiagnosticsTool_ShouldEmitZeroDiagnosticSummary()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "codexflow-lsp-tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspacePath, "src"));
        var filePath = Path.Combine(workspacePath, "src", "App.cs");
        await File.WriteAllTextAsync(filePath, "public class App\n{\n}\n");

        try
        {
            var output = await InvokeDiagnosticsToolAsync(workspacePath, "src/App.cs");

            Assert.Contains("diagnostics_count: 0", output);
            Assert.Contains("diagnostics: none", output);
            Assert.DoesNotContain("truncated", output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    private static async Task<string> InvokeDiagnosticsToolAsync(string workspacePath, string relativePath)
    {
        var registry = CreateRegistry();
        var factory = new DefaultLanguageServiceSessionFactory(registry, NullLogger<DefaultLanguageServiceSessionFactory>.Instance);
        var tool = new LspGetDiagnosticsTool(registry, factory, NullLogger<LspGetDiagnosticsTool>.Instance);
        var function = CodexToolFunctionAdapterFactory.CreateAIFunction(tool);

        var result = await function.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["path"] = relativePath,
            ["language"] = "csharp",
            ["worker_id"] = "explore-worker",
            ["workspace_path"] = workspacePath,
            ["project_root"] = workspacePath
        }), CancellationToken.None);

        var json = Assert.IsType<JsonElement>(result);
        var output = json.GetProperty("output").GetString();
        return Assert.IsType<string>(output);
    }

    private static DefaultLanguageServiceRegistry CreateRegistry()
        => new(
        [
            new CSharpLanguageServiceProvider(NullLogger<CSharpLanguageServiceProvider>.Instance),
            new PythonLanguageServiceProvider(new PassThroughProbe(), NullLogger<PythonLanguageServiceProvider>.Instance),
            new TypeScriptLanguageServiceProvider(new PassThroughProbe(), NullLogger<TypeScriptLanguageServiceProvider>.Instance),
            new JavaLanguageServiceProvider(new PassThroughProbe(), NullLogger<JavaLanguageServiceProvider>.Instance)
        ]);

    private sealed class RecordingTool(string name) : ICodexTool
    {
        public string Name { get; } = name;
        public CodexFlow.Core.Execution.ToolCapability Capability => CodexFlow.Core.Execution.ToolCapability.ReadOnly;
        public string Description => "recording tool";
        public ToolCategory Category => ToolCategory.Analysis;
        public IReadOnlyList<int> AllowedStages => [1, 2, 3, 4];
        public Dictionary<string, object?>? LastArguments { get; private set; }

        public Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
        {
            LastArguments = new Dictionary<string, object?>(arguments, StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(CodexToolResult.Succeeded("ok"));
        }
    }

    private sealed class PassThroughProbe : ILanguageServiceRuntimeProbe
    {
        public ValueTask<LanguageServiceRuntimeProbeResult> ProbeAsync(
            IReadOnlyList<string> commandCandidates,
            CancellationToken ct = default)
            => ValueTask.FromResult(new LanguageServiceRuntimeProbeResult
            {
                Available = true,
                Command = commandCandidates.FirstOrDefault(),
                DegradedReason = LanguageServiceDegradedReason.None
            });
    }
}
