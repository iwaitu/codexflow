// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Telemetry;
using CodexFlow.Core.Runtime;
using Xunit;

namespace CodexFlow.Core.Tests.Runtime;

public sealed class ForgeProgressGuardTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(ForgeProgressGuard.NudgeAfterCalls - 1)]
    public void BelowTheNudgeThreshold_NothingIsInjected(int calls)
        => Assert.Null(ForgeProgressGuard.Build(QueryLoopEntryPoint.ForgeWorker, calls, "hs_write"));

    [Fact]
    public void AtTheNudgeThreshold_AMessageNamesTheCountAndTheWriteTool_WithoutForcingIt()
    {
        var result = ForgeProgressGuard.Build(QueryLoopEntryPoint.ForgeWorker, ForgeProgressGuard.NudgeAfterCalls, "hs_write");

        Assert.NotNull(result);
        Assert.Null(result.RequiredToolName);
        var text = Assert.Single(result.Messages).Text;
        Assert.Contains(ForgeProgressGuard.NudgeAfterCalls.ToString(), text, StringComparison.Ordinal);
        Assert.Contains("hs_write", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AtTheForceThreshold_TheWriteToolIsRequired()
    {
        var result = ForgeProgressGuard.Build(QueryLoopEntryPoint.ForgeWorker, ForgeProgressGuard.ForceWriteAfterCalls, "hs_write");

        Assert.NotNull(result);
        Assert.Equal("hs_write", result.RequiredToolName);
    }

    // run52: the forced tool was apply_patch (first write-class tool in the list); the model had no patch ready and sent an empty one 40 times in
    // a row until the round budget ran out. The write tool the worker's prompt names comes first, and forcing is bounded.
    [Theory]
    [InlineData("apply_patch,hs_write", "hs_write")]
    [InlineData("hs_read,apply_patch,ivilson_write", "ivilson_write")]
    [InlineData("apply_patch", "apply_patch")]
    [InlineData("hs_read,exec_cmd", null)]
    public void PreferredWriteTool_IsTheHashlineWriter_NotApplyPatch(string tools, string? expected)
        => Assert.Equal(expected, ForgeProgressGuard.PickWriteTool(tools.Split(',')));

    [Fact]
    public void ForcingIsBounded_AfterThatOnlyTheMessageRemains()
    {
        var forced = ForgeProgressGuard.Build(QueryLoopEntryPoint.ForgeWorker, ForgeProgressGuard.ForceWriteAfterCalls, "hs_write", forcedSoFar: ForgeProgressGuard.MaxForcedRounds - 1);
        var spent = ForgeProgressGuard.Build(QueryLoopEntryPoint.ForgeWorker, ForgeProgressGuard.ForceWriteAfterCalls + 5, "hs_write", forcedSoFar: ForgeProgressGuard.MaxForcedRounds);

        Assert.Equal("hs_write", forced!.RequiredToolName);
        Assert.NotNull(spent);
        Assert.Null(spent.RequiredToolName);
    }

    [Fact]
    public void WithoutAWriteTool_NothingCanBeForced_ButTheMessageStillAppears()
    {
        var result = ForgeProgressGuard.Build(QueryLoopEntryPoint.ForgeWorker, ForgeProgressGuard.ForceWriteAfterCalls, null);

        Assert.NotNull(result);
        Assert.Null(result.RequiredToolName);
    }

    [Theory]
    [InlineData(QueryLoopEntryPoint.ExploreWorker)]
    [InlineData(QueryLoopEntryPoint.VerifyWorker)]
    [InlineData(QueryLoopEntryPoint.GatewayMessageProcessor)]
    public void OnlyAForgeWorkerIsExpectedToWrite(QueryLoopEntryPoint entryPoint)
        => Assert.Null(ForgeProgressGuard.Build(entryPoint, ForgeProgressGuard.ForceWriteAfterCalls, "hs_write"));
}
