// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodexFlow.Core.Tests.Tools;

public class SearchInFilesToolTests
{
    [Fact]
    public async Task ExecuteAsync_WithMatches_ReturnsConcreteFileSummaryAndReadHint()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "codex-search-in-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspacePath);
        Directory.CreateDirectory(Path.Combine(workspacePath, "CodexFlow.Core", "Runtime"));
        Directory.CreateDirectory(Path.Combine(workspacePath, "CodexFlow.Core", "Constants"));

        await File.WriteAllTextAsync(
            Path.Combine(workspacePath, "CodexFlow.Core", "Runtime", "QueryRuntimeEngine.cs"),
            "public sealed class QueryRuntimeEngine { }");
        await File.WriteAllTextAsync(
            Path.Combine(workspacePath, "CodexFlow.Core", "Constants", "CodexPrompts.cs"),
            "public static class CodexPrompts { public const string QueryRuntimeHint = \"QueryRuntime\"; }");

        try
        {
            var tool = new SearchInFilesTool(NullLogger<SearchInFilesTool>.Instance);

            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["workspace_path"] = workspacePath,
                ["pattern"] = "QueryRuntime",
                ["path"] = "."
            });

            Assert.Equal(ToolResultStatus.Success, result.Status);
            Assert.Contains("QueryRuntimeEngine.cs", result.Output, StringComparison.Ordinal);
            Assert.Contains("CodexPrompts.cs", result.Output, StringComparison.Ordinal);
            Assert.Contains("优先直接读取相关文件片段", result.Output, StringComparison.Ordinal);
            Assert.NotNull(result.Summary);
            Assert.Contains("Matches for QueryRuntime", result.Summary!, StringComparison.Ordinal);
            Assert.Contains("CodexFlow.Core/Runtime/QueryRuntimeEngine.cs", result.Summary!, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }
}
