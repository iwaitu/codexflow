// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Runtime;
using Xunit;

namespace CodexFlow.Core.Tests.Runtime;

/// <summary>
/// The engine gives every tool one minute unless told otherwise. Planning takes a draft round plus a structured projection round (a minute or more
/// with a real model), so "create_session_plan" was cut at exactly 60.0 s in the live gateway ("Tool execution exceeded 00:01:00") while the
/// headless path happened to finish inside it.
/// </summary>
public sealed class RuntimeToolTimeoutsTests
{
    [Theory]
    [InlineData("create_session_plan")]
    [InlineData("generate_dev_plan")]
    [InlineData("CREATE_SESSION_PLAN")]
    public void PlanningTools_GetFarMoreThanTheDefaultMinute(string tool) =>
        Assert.True(RuntimeToolTimeouts.For(tool) >= TimeSpan.FromMinutes(5));

    [Theory]
    [InlineData("run_tests")]
    [InlineData("exec_cmd")]
    [InlineData("run_command")]
    public void BuildAndTestTools_GetTimeToFinishARealBuild(string tool) =>
        Assert.True(RuntimeToolTimeouts.For(tool) >= TimeSpan.FromMinutes(5));

    [Theory]
    [InlineData("ivilson_read")]
    [InlineData("search_in_files")]
    [InlineData("spawn_worker")]
    [InlineData("")]
    [InlineData(null)]
    public void EverythingElse_KeepsTheEnginesDefault(string? tool) =>
        Assert.Equal(TimeSpan.FromMinutes(1), RuntimeToolTimeouts.For(tool));

    [Fact]
    public void NoToolIsEverUnbounded() =>
        Assert.All(new[] { "create_session_plan", "run_tests", "exec_cmd", "run_command", "x" }, tool =>
            Assert.InRange(RuntimeToolTimeouts.For(tool), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(15)));
}
