// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodexFlow.Core.Tests.Tools;

public sealed class NotebookEditToolTests
{
    [Fact]
    public async Task NotebookEdit_ReplaceSource_DryRun_ShouldNotWriteFile()
    {
        var workspace = CreateWorkspace();
        try
        {
            var notebookPath = WriteNotebook(workspace);
            var original = await File.ReadAllTextAsync(notebookPath);
            var tool = new NotebookEditTool(NullLogger<NotebookEditTool>.Instance);

            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["workspace_path"] = workspace,
                ["path"] = "sample.ipynb",
                ["cell_index"] = 0,
                ["source"] = "print('changed')",
                ["dry_run"] = true
            });

            Assert.Equal(ToolResultStatus.Success, result.Status);
            Assert.Contains("DRY RUN", result.Output, StringComparison.Ordinal);
            Assert.Contains("- print('hello')", result.Output, StringComparison.Ordinal);
            Assert.Contains("+ print('changed')", result.Output, StringComparison.Ordinal);
            Assert.Equal(original, await File.ReadAllTextAsync(notebookPath));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task NotebookEdit_ReplaceSource_ShouldWriteValidNotebookJson()
    {
        var workspace = CreateWorkspace();
        try
        {
            var notebookPath = WriteNotebook(workspace);
            var tool = new NotebookEditTool(NullLogger<NotebookEditTool>.Instance);

            var result = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["workspace_path"] = workspace,
                ["path"] = "sample.ipynb",
                ["cell_index"] = 0,
                ["source"] = "x = 1\nprint(x)",
                ["dry_run"] = false
            });

            Assert.Equal(ToolResultStatus.Success, result.Status);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(notebookPath));
            var source = document.RootElement
                .GetProperty("cells")[0]
                .GetProperty("source")
                .EnumerateArray()
                .Select(item => item.GetString() ?? string.Empty)
                .ToArray();

            Assert.Equal(["x = 1\n", "print(x)"], source);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task NotebookEdit_InsertAndDeleteCell_ShouldUpdateCellsArray()
    {
        var workspace = CreateWorkspace();
        try
        {
            var notebookPath = WriteNotebook(workspace);
            var tool = new NotebookEditTool(NullLogger<NotebookEditTool>.Instance);

            var inserted = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["workspace_path"] = workspace,
                ["path"] = "sample.ipynb",
                ["operation"] = "insert_cell",
                ["cell_index"] = 1,
                ["cell_type"] = "markdown",
                ["source"] = "## Notes",
                ["dry_run"] = false
            });
            var deleted = await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["workspace_path"] = workspace,
                ["path"] = "sample.ipynb",
                ["operation"] = "delete_cell",
                ["cell_index"] = 0,
                ["dry_run"] = false
            });

            Assert.Equal(ToolResultStatus.Success, inserted.Status);
            Assert.Equal(ToolResultStatus.Success, deleted.Status);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(notebookPath));
            var cells = document.RootElement.GetProperty("cells");
            Assert.Equal(1, cells.GetArrayLength());
            Assert.Equal("markdown", cells[0].GetProperty("cell_type").GetString());
            Assert.Equal("## Notes", cells[0].GetProperty("source")[0].GetString());
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private static string CreateWorkspace()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "codex-notebook-edit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        return workspace;
    }

    private static string WriteNotebook(string workspace)
    {
        var path = Path.Combine(workspace, "sample.ipynb");
        File.WriteAllText(path, """
            {
              "cells": [
                {
                  "cell_type": "code",
                  "execution_count": null,
                  "metadata": {},
                  "outputs": [],
                  "source": [
                    "print('hello')"
                  ]
                }
              ],
              "metadata": {},
              "nbformat": 4,
              "nbformat_minor": 5
            }
            """);
        return path;
    }
}
