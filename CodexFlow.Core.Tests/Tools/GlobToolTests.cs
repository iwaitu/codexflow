// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodexFlow.Core.Tests.Tools;

public sealed class GlobToolTests
{
    [Fact]
    public async Task ExecuteAsync_WithRecursivePattern_ReturnsMatchingWorkspaceRelativePaths()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "codex-glob-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspacePath, "src", "Core"));
        Directory.CreateDirectory(Path.Combine(workspacePath, "src", "bin"));
        Directory.CreateDirectory(Path.Combine(workspacePath, "docs"));

        await File.WriteAllTextAsync(Path.Combine(workspacePath, "src", "Core", "App.cs"), "class App {}");
        await File.WriteAllTextAsync(Path.Combine(workspacePath, "src", "Core", "AppTests.cs"), "class AppTests {}");
        await File.WriteAllTextAsync(Path.Combine(workspacePath, "src", "bin", "Generated.cs"), "class Generated {}");
        await File.WriteAllTextAsync(Path.Combine(workspacePath, "docs", "readme.md"), "# readme");

        try
        {
            var tool = new GlobTool(NullLogger<GlobTool>.Instance);

            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["workspace_path"] = workspacePath,
                ["pattern"] = "**/*.cs",
                ["path"] = "."
            });

            Assert.Equal(ToolResultStatus.Success, result.Status);
            Assert.Contains("src/Core/App.cs", result.Output, StringComparison.Ordinal);
            Assert.Contains("src/Core/AppTests.cs", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("src/bin/Generated.cs", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("docs/readme.md", result.Output, StringComparison.Ordinal);
            Assert.Contains("Glob **/*.cs", result.Summary, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithScopedPath_MatchesPathsRelativeToScope()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "codex-glob-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspacePath, "docs", "feature"));
        await File.WriteAllTextAsync(Path.Combine(workspacePath, "docs", "feature", "tooling.md"), "# tooling");

        try
        {
            var tool = new GlobTool(NullLogger<GlobTool>.Instance);

            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["workspace_path"] = workspacePath,
                ["pattern"] = "*.md",
                ["path"] = "docs/feature"
            });

            Assert.Equal(ToolResultStatus.Success, result.Status);
            Assert.Contains("docs/feature/tooling.md", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }
}
