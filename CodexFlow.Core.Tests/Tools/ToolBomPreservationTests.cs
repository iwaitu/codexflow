// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.Tools;

/// <summary>
/// Regression for the cleanapp-bug live runs 12 and 13: every agent commit stripped the UTF-8 BOM from the first line of
/// FileService.cs (an unrelated one-line diff). The write tools read text through a BOM-detecting reader and wrote it back
/// without a BOM, so an edit that never touched line 1 still rewrote it.
/// </summary>
public sealed class ToolBomPreservationTests : IDisposable
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "codexflow-bom-" + Guid.NewGuid().ToString("N"));

    public ToolBomPreservationTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task EditFile_OnFileWithBom_KeepsTheBom()
    {
        var path = WriteFile("App.cs", "using System;\nclass App { string Name => \"old\"; }\n", bom: true);

        var result = await new EditFileTool(NullLogger<EditFileTool>.Instance).ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _workspace,
            ["path"] = "App.cs",
            ["old_string"] = "\"old\"",
            ["new_string"] = "\"new\""
        });

        Assert.Equal(ToolResultStatus.Success, result.Status);
        AssertBom(path, expected: true);
        Assert.Contains("\"new\"", ReadText(path), StringComparison.Ordinal);
        Assert.StartsWith("using System;", ReadText(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EditFile_OnFileWithoutBom_DoesNotAddOne()
    {
        var path = WriteFile("Plain.cs", "using System;\nclass App { string Name => \"old\"; }\n", bom: false);

        var result = await new EditFileTool(NullLogger<EditFileTool>.Instance).ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _workspace,
            ["path"] = "Plain.cs",
            ["old_string"] = "\"old\"",
            ["new_string"] = "\"new\""
        });

        Assert.Equal(ToolResultStatus.Success, result.Status);
        AssertBom(path, expected: false);
    }

    [Fact]
    public async Task WriteFile_OverwritingFileWithBom_KeepsTheBom()
    {
        var path = WriteFile("Overwrite.cs", "old\n", bom: true);

        var result = await new WriteFileTool(NullLogger<WriteFileTool>.Instance).ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _workspace,
            ["path"] = "Overwrite.cs",
            ["content"] = "new\n"
        });

        Assert.Equal(ToolResultStatus.Success, result.Status);
        AssertBom(path, expected: true);
        Assert.Equal("new\n", ReadText(path));
    }

    [Fact]
    public async Task WriteFile_CreatingNewFile_DoesNotAddBom()
    {
        var result = await new WriteFileTool(NullLogger<WriteFileTool>.Instance).ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _workspace,
            ["path"] = "Created.cs",
            ["content"] = "class Created { }\n"
        });

        Assert.Equal(ToolResultStatus.Success, result.Status);
        AssertBom(Path.Combine(_workspace, "Created.cs"), expected: false);
    }

    [Fact]
    public async Task ApplyPatch_CodexUpdateOnFileWithBom_KeepsTheBom()
    {
        var path = WriteFile("Service.cs", "using System;\nclass Service\n{\n    int Count() => 1;\n}\n", bom: true);
        var patch = string.Join("\n",
            "*** Begin Patch",
            "*** Update File: Service.cs",
            "@@",
            "-    int Count() => 1;",
            "+    int Count() => 2;",
            "*** End Patch",
            "");

        var tool = new ApplyPatchTool(new Mock<IGitService>().Object, null, NullLogger<ApplyPatchTool>.Instance);
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = _workspace,
            ["patch"] = patch
        });

        Assert.True(result.Status == ToolResultStatus.Success, result.Output);
        Assert.Contains("Count() => 2", ReadText(path), StringComparison.Ordinal);
        AssertBom(path, expected: true);
    }

    [Fact]
    public void WriteAllTextVerified_TextAlreadyStartingWithBomChar_DoesNotDoubleTheBom()
    {
        var path = WriteFile("Double.cs", "old\n", bom: true);
        var (_, version) = ToolPathResolver.ReadTextWithVersion(path);

        ToolPathResolver.WriteAllTextVerifiedAsync(path, _workspace, "﻿new\n", version, CancellationToken.None)
            .GetAwaiter().GetResult();

        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.AsSpan(0, 3).SequenceEqual(Bom));
        Assert.False(bytes.Length >= 6 && bytes.AsSpan(3, 3).SequenceEqual(Bom), "BOM was written twice");
    }

    private string WriteFile(string name, string text, bool bom)
    {
        var path = Path.Combine(_workspace, name);
        var body = new System.Text.UTF8Encoding(false).GetBytes(text);
        File.WriteAllBytes(path, bom ? [.. Bom, .. body] : body);
        return path;
    }

    private static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var start = bytes.Length >= 3 && bytes.AsSpan(0, 3).SequenceEqual(Bom) ? 3 : 0;
        return System.Text.Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
    }

    private static void AssertBom(string path, bool expected)
    {
        var bytes = File.ReadAllBytes(path);
        var hasBom = bytes.Length >= 3 && bytes.AsSpan(0, 3).SequenceEqual(Bom);
        Assert.Equal(expected, hasBom);
    }
}
