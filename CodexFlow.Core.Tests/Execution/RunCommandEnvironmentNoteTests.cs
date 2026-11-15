// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Execution;
using Xunit;

namespace CodexFlow.Core.Tests.Execution;

/// <summary>
/// Live developer-simulation run: a forge worker ran <c>python3 -c "... subprocess.run(['dotnet', ...])"</c>. exec_cmd picks the image from the
/// first word, so that ran in the python image, where dotnet does not exist; the worker concluded "the environment has no dotnet" and reported
/// a finished upgrade as blocked. A command that ran outside the default image says so in its own result.
/// </summary>
public sealed class RunCommandEnvironmentNoteTests
{
    private static readonly IsolationOptions Options = new();

    [Theory]
    [InlineData("python3")]
    [InlineData("python")]
    [InlineData("node")]
    [InlineData("npm")]
    public void ACommandThatRunsInItsOwnImage_SaysThatDotnetIsNotThere(string executable)
    {
        var note = RunCommandTool.BuildEnvironmentNote(executable, Options.ExecutableImages[executable], Options.CommandImage);

        Assert.NotNull(note);
        Assert.Contains(executable, note, StringComparison.Ordinal);
        Assert.Contains(Options.ExecutableImages[executable], note, StringComparison.Ordinal);
        Assert.Contains("没有 dotnet", note, StringComparison.Ordinal);
        Assert.Contains("不代表环境里没有它", note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dotnet")]
    [InlineData("git")]
    [InlineData("ls")]
    public void ACommandInTheDefaultImage_CarriesNoNote(string executable)
    {
        var image = Options.ExecutableImages.TryGetValue(executable, out var mapped) ? mapped : Options.CommandImage;

        Assert.Null(RunCommandTool.BuildEnvironmentNote(executable, image, Options.CommandImage));
    }

    [Fact]
    public void TheToolDescription_TellsTheModelWhichImageACommandRunsIn()
    {
        var description = new RunCommandTool(Microsoft.Extensions.Logging.Abstractions.NullLogger<RunCommandTool>.Instance).Description;

        Assert.Contains("容器镜像由命令的第一个词决定", description, StringComparison.Ordinal);
    }
}
