// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Models;
using CodexFlow.Core.Tests.Gateway;
using CodexFlow.Orchestration.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.Tools;

public sealed class RoslynCodeAnalysisToolTests
{
    [Fact]
    public async Task RegisteredLocalToolUsesBundledCSharpReferencesAndReturnsDiagnostics()
    {
        const string validCode = "using System.Linq;\npublic class Example { public int Read() => new[] { 1, 2 }.Sum(); }";
        var referenceDirectory = Path.Combine(AppContext.BaseDirectory, "code-analysis", "net10.0");
        Assert.True(File.Exists(Path.Combine(referenceDirectory, "System.Runtime.dll")), referenceDirectory);
        Assert.True(File.Exists(Path.Combine(referenceDirectory, "System.Linq.dll")), referenceDirectory);

        var root = Directory.CreateTempSubdirectory("codexflow-roslyn-tool-");
        try
        {
            await using var host = await P3TestHost.StartAsync(
                root.FullName,
                Path.Combine(root.FullName, "global", "appsettings.json"),
                ExecutionBackend.Local,
                ExecutionPermissionMode.Ask,
                configureServices: (services, configuration) =>
                {
                    services.AddHttpClient();
                    services.AddSingleton(Mock.Of<IChatClient>());
                    services.AddCodexFlowRuntime(configuration);
                });
            await using var scope = host.Services.CreateAsyncScope();
            var registry = scope.ServiceProvider.GetRequiredService<IToolRegistry>();
            scope.ServiceProvider.GetRequiredService<ToolRegistryBootstrapper>().EnsureRegistered(registry);
            var tool = Assert.Single(registry.GetAlwaysOnTools(), tool => tool.Name == "analyze_code");

            foreach (var code in new[] { validCode, "System.Console.WriteLine(1);" })
            {
                var valid = await AnalyzeAsync(tool, code);
                Assert.Equal(ToolResultStatus.Success, valid.Status);
                Assert.Null(valid.Metadata);
                Assert.Contains("未发现错误或警告", valid.Output, StringComparison.Ordinal);
            }

            await AssertDiagnosticAsync(
                tool, "public class Example\n{\n    public int Read() => \"wrong\";\n}", "CS0029", line: 3, column: 26);
            await AssertDiagnosticAsync(
                tool, "public class Example\n{\n    public int Read() => ;\n}", "CS1525", line: 3, column: 26);
        }
        finally
        {
            LocalExecutionService.TryDeleteDirectory(root.FullName);
        }
    }

    [Theory]
    [InlineData("blank")]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("invalid")]
    public async Task ToolReturnsFailureWhenReferencesCannotBeLoaded(string fixture)
    {
        var root = Directory.CreateTempSubdirectory("codexflow-roslyn-references-");
        try
        {
            var directory = fixture switch
            {
                "blank" => "",
                "missing" => Path.Combine(root.FullName, "does-not-exist"),
                _ => root.FullName
            };
            if (fixture == "invalid")
            {
                File.WriteAllText(Path.Combine(directory, "System.Runtime.dll"), "Invalid PE data");
                File.WriteAllText(Path.Combine(directory, "System.Linq.dll"), "Invalid PE data");
            }

            var service = new RoslynCodeAnalysisService(NullLogger.Instance, directory);
            var result = await AnalyzeAsync(new RoslynCodeAnalysisTool(service), "public class Example { }");

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Null(result.Metadata);
            Assert.Contains("CODEXFLOW_CSHARP_REFERENCE_DIR", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            LocalExecutionService.TryDeleteDirectory(root.FullName);
        }
    }

    private static Task<CodexToolResult> AnalyzeAsync(ICodexTool tool, string code)
        => tool.ExecuteAsync(new Dictionary<string, object?> { ["code"] = code });

    private static async Task AssertDiagnosticAsync(ICodexTool tool, string code, string id, int line, int column)
    {
        var result = await AnalyzeAsync(tool, code);

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains(id, result.Output, StringComparison.Ordinal);
        var diagnostics = Assert.IsAssignableFrom<IReadOnlyList<CodeDiagnostic>>(result.Metadata);
        var diagnostic = Assert.Single(diagnostics, diagnostic => diagnostic.Id == id);
        Assert.Equal("Error", diagnostic.Severity);
        Assert.Equal(line, diagnostic.Line);
        Assert.Equal(column, diagnostic.Column);
    }
}
