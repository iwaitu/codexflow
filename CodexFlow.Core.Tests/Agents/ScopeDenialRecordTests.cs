// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using CodexFlow.Core.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodexFlow.Core.Tests.Agents;

/// <summary>
/// The guard refused a write and told the worker; nothing told the host. When the worker then stopped, the job failed with a generic reason
/// and nobody could say that the file it needed was refused. The hook now keeps what it refused, so the job's failure can name the paths.
/// </summary>
public class ScopeDenialRecordTests
{
    private static TaskScopeInterventionHook HookFor(params string[] allowed)
    {
        var scope = new TaskFileScopeDescriptor();
        foreach (var file in allowed)
        {
            scope.AllowedFiles.Add(file);
        }

        return new TaskScopeInterventionHook(scope, NullLogger.Instance);
    }

    private static ValueTask<QueryRuntimeIntervention> Call(TaskScopeInterventionHook hook, string tool, string path)
        => hook.OnToolCallRequestedAsync(tool, new Dictionary<string, object?> { ["path"] = path }, session: null, ct: CancellationToken.None);

    [Fact]
    public async Task ARefusedWrite_IsRecordedWithToolPathAndReason()
    {
        var hook = HookFor("src/A/A.cs");

        await Call(hook, "edit_file", "src/Other/Other.csproj");

        var denial = Assert.Single(hook.Denials);
        Assert.Equal("edit_file", denial.Tool);
        Assert.Equal("src/Other/Other.csproj", denial.Path);
        Assert.Contains("outside allowed scope", denial.Reason);
    }

    [Fact]
    public async Task AllowedWrites_AreNotRecorded()
    {
        var hook = HookFor("src/A/A.cs");

        await Call(hook, "edit_file", "src/A/A.cs");

        Assert.Empty(hook.Denials);
    }

    [Fact]
    public async Task TheSameRefusalRepeated_IsRecordedOnce_ButStillCounted()
    {
        var hook = HookFor("src/A/A.cs");

        await Call(hook, "edit_file", "src/Other/Other.csproj");
        await Call(hook, "edit_file", "src/Other/Other.csproj");
        await Call(hook, "write_file", "src/Other/Other.csproj");

        Assert.Equal(2, hook.Denials.Count);
        Assert.Equal(3, hook.BlockCount);
    }

    [Fact]
    public async Task AnUnsafePath_IsRecorded()
    {
        var hook = HookFor("src/A/A.cs");

        await Call(hook, "write_file", "../outside.txt");

        Assert.Contains(hook.Denials, d => d.Path == "../outside.txt");
    }

    [Fact]
    public async Task AForbiddenProjectCommand_IsRecordedAsACommand_NotAsAPath()
    {
        var hook = HookFor("src/A/A.cs");

        await hook.OnToolCallRequestedAsync(
            "run_command", new Dictionary<string, object?> { ["command"] = "dotnet add package Foo" }, session: null, ct: CancellationToken.None);

        var denial = Assert.Single(hook.Denials);
        Assert.Null(denial.Path);
        Assert.Contains("dotnet add package Foo", denial.Command);
    }

    [Fact]
    public async Task TheRecordIsBounded()
    {
        var hook = HookFor("src/A/A.cs");

        for (var i = 0; i < TaskScopeInterventionHook.MaxRecordedDenials + 10; i++)
        {
            await Call(hook, "edit_file", $"src/Other/File{i}.cs");
        }

        Assert.Equal(TaskScopeInterventionHook.MaxRecordedDenials, hook.Denials.Count);
        Assert.Equal(TaskScopeInterventionHook.MaxRecordedDenials + 10, hook.BlockCount);
    }
}
