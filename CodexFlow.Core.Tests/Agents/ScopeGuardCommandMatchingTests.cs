// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodexFlow.Core.Tests.Agents;

/// <summary>
/// The scope guard blocked commands by looking for "dotnet add", "dotnet new", ... anywhere in the command line. That fired on text that merely
/// contained those words (a commit message, an echo) and missed the same commands when they came as dotnet.exe, behind a chain or inside bash -c.
/// The decision is now made on the parsed command: who is run, and with which subcommand.
/// </summary>
public sealed class ScopeGuardCommandMatchingTests
{
    private static async Task<bool> Blocked(object command)
    {
        var scope = new TaskFileScopeDescriptor();
        scope.AllowedFiles.Add("src/App/FileService.cs");
        var hook = new TaskScopeInterventionHook(scope, NullLogger.Instance);
        var intervention = await hook.OnToolCallRequestedAsync("run_command", new Dictionary<string, object?> { ["command"] = command }, session: null, ct: CancellationToken.None);
        return intervention.ShouldBlock;
    }

    [Theory]
    [InlineData("dotnet add src/App/App.csproj package AutoMapper")]
    [InlineData("dotnet add package Newtonsoft.Json")]
    [InlineData("dotnet remove src/App/App.csproj reference ../Lib/Lib.csproj")]
    [InlineData("dotnet new console -n Tool")]
    [InlineData("dotnet format src/App")]
    [InlineData("dotnet sln add src/App/App.csproj")]
    [InlineData("dotnet ef migrations add Init")]
    [InlineData("dotnet ef database update")]
    [InlineData("DOTNET ADD package X")]
    [InlineData("dotnet.exe new console")]
    [InlineData("C:/tools/dotnet/dotnet.exe add package X")]
    [InlineData("\"C:/Program Files/dotnet/dotnet.exe\" add package X")]
    [InlineData("cd src/App && dotnet add package X")]
    [InlineData("dotnet build && dotnet new classlib")]
    [InlineData("echo start; dotnet format")]
    [InlineData("FOO=1 dotnet new console")]
    [InlineData("bash -c \"dotnet add package X\"")]
    [InlineData("sh -c 'cd app && dotnet new console'")]
    [InlineData("pwsh -Command \"dotnet format\"")]
    public async Task ACommandThatReallyRunsAProjectMutation_IsBlocked(string command) => Assert.True(await Blocked(command));

    [Theory]
    [InlineData("git commit -m \"dotnet add package AutoMapper\"")]
    [InlineData("echo dotnet new console")]
    [InlineData("echo \"run dotnet format later\"")]
    [InlineData("grep -rn \"dotnet ef migrations\" docs")]
    [InlineData("cat notes-about-dotnet-add-package.md")]
    [InlineData("dotnet build")]
    [InlineData("dotnet test src/App.Tests/App.Tests.csproj")]
    [InlineData("dotnet restore && dotnet build --no-restore")]
    [InlineData("dotnet list package --vulnerable --include-transitive")]
    [InlineData("dotnet --info")]
    [InlineData("git log --oneline | grep \"dotnet new\"")]
    public async Task TextThatOnlyMentionsAMutation_IsNotBlocked(string command) => Assert.False(await Blocked(command));

    [Fact]
    public async Task AnArgvArray_IsJudgedByItsExecutableAndSubcommand_NotByWordsInsideItsArguments()
    {
        Assert.True(await Blocked(new[] { "dotnet", "add", "package", "X" }));
        Assert.False(await Blocked(new[] { "git", "commit", "-m", "dotnet add package X" }));
        Assert.False(await Blocked(new[] { "echo", "dotnet", "new", "console" }));
    }

    [Fact]
    public async Task WhenProjectEditsAreAllowed_TheSameCommandsPass()
    {
        var scope = new TaskFileScopeDescriptor { AllowProjectFileEdits = true };
        scope.AllowedFiles.Add("src/App/App.csproj");
        var hook = new TaskScopeInterventionHook(scope, NullLogger.Instance);
        var intervention = await hook.OnToolCallRequestedAsync("run_command",
            new Dictionary<string, object?> { ["command"] = "dotnet add package X" }, session: null, ct: CancellationToken.None);
        Assert.False(intervention.ShouldBlock);
    }
}
