// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodexFlow.Core.Tests.Tools;

public sealed class EditFileToolTests
{
    [Fact]
    public async Task ExecuteAsync_WithSingleMatch_UpdatesFile()
    {
        var workspacePath = CreateWorkspace();
        var filePath = Path.Combine(workspacePath, "src", "App.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "public class App { string Name => \"old\"; }");

        try
        {
            var tool = new EditFileTool(NullLogger<EditFileTool>.Instance);
            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["workspace_path"] = workspacePath,
                ["path"] = "src/App.cs",
                ["old_string"] = "\"old\"",
                ["new_string"] = "\"new\""
            });

            Assert.Equal(ToolResultStatus.Success, result.Status);
            Assert.Contains("\"new\"", await File.ReadAllTextAsync(filePath), StringComparison.Ordinal);
            Assert.Contains("1 replacement", result.Summary, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithMultipleMatchesAndNoReplaceAll_ReturnsError()
    {
        var workspacePath = CreateWorkspace();
        var filePath = Path.Combine(workspacePath, "App.txt");
        await File.WriteAllTextAsync(filePath, "old old");

        try
        {
            var tool = new EditFileTool(NullLogger<EditFileTool>.Instance);
            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["workspace_path"] = workspacePath,
                ["path"] = "App.txt",
                ["old_string"] = "old",
                ["new_string"] = "new"
            });

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Contains("matched 2 locations", result.Output, StringComparison.Ordinal);
            Assert.Equal("old old", await File.ReadAllTextAsync(filePath));
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithReplaceAll_ReplacesAllMatches()
    {
        var workspacePath = CreateWorkspace();
        var filePath = Path.Combine(workspacePath, "App.txt");
        await File.WriteAllTextAsync(filePath, "old old");

        try
        {
            var tool = new EditFileTool(NullLogger<EditFileTool>.Instance);
            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["workspace_path"] = workspacePath,
                ["path"] = "App.txt",
                ["old_string"] = "old",
                ["new_string"] = "new",
                ["replace_all"] = true
            });

            Assert.Equal(ToolResultStatus.Success, result.Status);
            Assert.Equal("new new", await File.ReadAllTextAsync(filePath));
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithDryRun_DoesNotWriteFile()
    {
        var workspacePath = CreateWorkspace();
        var filePath = Path.Combine(workspacePath, "App.txt");
        await File.WriteAllTextAsync(filePath, "old");

        try
        {
            var tool = new EditFileTool(NullLogger<EditFileTool>.Instance);
            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["workspace_path"] = workspacePath,
                ["path"] = "App.txt",
                ["old_string"] = "old",
                ["new_string"] = "new",
                ["dry_run"] = true
            });

            Assert.Equal(ToolResultStatus.Success, result.Status);
            Assert.Contains("DRY RUN", result.Output, StringComparison.Ordinal);
            Assert.Equal("old", await File.ReadAllTextAsync(filePath));
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    private static string CreateWorkspace()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "codex-edit-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspacePath);
        return workspacePath;
    }
}
