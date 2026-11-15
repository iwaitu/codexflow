// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Execution;
using Xunit;

namespace CodexFlow.Core.Tests.Execution;

/// <summary>
/// Live run (local Qwen, bugfix-node-pager / bugfix-python-pager): <c>npm test</c> answered "jest: not found" and <c>python -m pytest</c>
/// "No module named pytest" in the worker's container, so a wrong regression test was reported as unverified and failed the host verification.
/// </summary>
public class OfflineToolchainPreparationTests
{
    [Theory]
    [InlineData("npm")]
    [InlineData("npx")]
    [InlineData("node")]
    public void ANodeCommand_InstallsFromTheOfflineNpmCacheFirst(string executable)
    {
        var script = OfflineToolchainPreparation.ScriptFor(executable);

        Assert.NotNull(script);
        Assert.Contains("npm ci --offline --ignore-scripts", script, StringComparison.Ordinal);
        Assert.Contains("/npm-cache", script, StringComparison.Ordinal);
        // no lockfile or no cache: nothing to prepare, and that is not a failure
        Assert.Contains("[ -d /npm-cache ] && [ -f package-lock.json ] || exit 3", script, StringComparison.Ordinal);
        Assert.Null(OfflineToolchainPreparation.EnvFor(executable));
    }

    [Theory]
    [InlineData("python")]
    [InlineData("python3")]
    [InlineData("pip")]
    public void APythonCommand_InstallsPytestAndRequirementsFromTheWheelhouseFirst(string executable)
    {
        var script = OfflineToolchainPreparation.ScriptFor(executable);

        Assert.NotNull(script);
        Assert.Contains("--no-index --find-links /pip-wheels", script, StringComparison.Ordinal);
        Assert.Contains("$pip pytest", script, StringComparison.Ordinal);
        Assert.Contains("-r requirements.txt", script, StringComparison.Ordinal);
        // installed under $HOME, never inside the workspace
        Assert.Contains("--user", script, StringComparison.Ordinal);

        // bytecode and the pytest cache would otherwise be exported as the worker's own changes
        var env = OfflineToolchainPreparation.EnvFor(executable);
        Assert.NotNull(env);
        Assert.Equal("1", env!["PYTHONDONTWRITEBYTECODE"]);
        Assert.Equal("-p no:cacheprovider", env["PYTEST_ADDOPTS"]);
    }

    [Theory]
    [InlineData("dotnet")]
    [InlineData("git")]
    [InlineData("ls")]
    [InlineData(null)]
    public void OtherCommands_PrepareNothing(string? executable)
    {
        Assert.Null(OfflineToolchainPreparation.ScriptFor(executable));
        Assert.Null(OfflineToolchainPreparation.EnvFor(executable));
    }

    [Fact]
    public void ASuccessfulPreparation_TellsTheModelTheTestsCanRunAndNotToInstallAgain()
    {
        var node = OfflineToolchainPreparation.Describe("npm", IsolatedCommandStatus.Completed, 0, "", "added 267 packages");
        var python = OfflineToolchainPreparation.Describe("python", IsolatedCommandStatus.Completed, 0, "", "");

        Assert.Contains("npm test", node, StringComparison.Ordinal);
        Assert.Contains("不要再联网安装", node, StringComparison.Ordinal);
        Assert.Contains("python -m pytest", python, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingToPrepare_SaysNothing()
        => Assert.Null(OfflineToolchainPreparation.Describe("npm", IsolatedCommandStatus.Failed, OfflineToolchainPreparation.NotApplicableExitCode, "", ""));

    [Fact]
    public void AFailedPreparation_SaysWhyWithTheEndOfTheOutput()
    {
        var note = OfflineToolchainPreparation.Describe("python", IsolatedCommandStatus.Failed, 1,
            new string('x', 900) + "ERROR: No matching distribution found for requests==2.99", "");

        Assert.NotNull(note);
        Assert.Contains("没能从离线缓存装好项目依赖", note, StringComparison.Ordinal);
        Assert.Contains("No matching distribution found for requests==2.99", note, StringComparison.Ordinal);
        Assert.True(note!.Length < 800);
    }
}
