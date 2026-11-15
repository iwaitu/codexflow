// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Runtime;
using Xunit;

namespace CodexFlow.Core.Tests.Runtime;

public sealed class WorkerStatusReportTests
{
    private static WorkerStatusReport? Parse(string json) => WorkerStatusReport.FromArguments(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Blocked_CarriesItsReasonAndTheUnfinishedItems()
    {
        var report = Parse("{\"status\":\"Blocked\",\"reason\":\" 清单缺失 \",\"unfinished\":[\"A\",\" \",\"B\"]}");

        Assert.NotNull(report);
        Assert.True(report.IsBlocked);
        Assert.Equal("清单缺失", report.Reason);
        Assert.Equal(["A", "B"], report.Unfinished);
    }

    [Fact]
    public void AReportWrappedInTheAdaptersParametersObject_IsRead()
    {
        // The generic tool adapter exposes one `parameters` object, so this is the shape every live call has. Read only at the top level, the
        // report was never recorded and a worker that said "blocked" was treated as one that said nothing.
        var report = Parse("{\"parameters\":{\"status\":\"blocked\",\"reason\":\"离线源缺少四个包\",\"unfinished\":[\"完成离线还原\"]}}");

        Assert.NotNull(report);
        Assert.True(report.IsBlocked);
        Assert.Equal("离线源缺少四个包", report.Reason);
        Assert.Equal(["完成离线还原"], report.Unfinished);
    }

    [Fact]
    public void Completed_IsNotBlocked()
        => Assert.False(Parse("{\"status\":\"completed\"}")!.IsBlocked);

    [Theory]
    [InlineData("{\"status\":\"partial\"}")]
    [InlineData("{\"status\":1}")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void AnythingElse_IsNotAReport(string json)
        => Assert.Null(Parse(json));

    // Live developer-simulation runs: a worker that had written every requested version, restored, built and tested still reported "blocked"
    // because one extra check of the plan could not run in its environment, and a finished change was failed for it.
    [Fact]
    public void Completed_CarriesTheVerificationTheWorkerCouldNotRun()
    {
        var report = Parse("{\"parameters\":{\"status\":\"completed\",\"unverified\":[\"逐项目核对解析版本基线\",\" \"]}}");

        Assert.NotNull(report);
        Assert.False(report.IsBlocked);
        Assert.Equal(["逐项目核对解析版本基线"], report.Unverified);
        Assert.Empty(report.Unfinished);
    }

    // Live run (dependency-upgrade-out-of-scope): the approved plan asked for 1.15.3, which could not be restored offline; the worker wrote 1.17.0
    // instead, left three packages untouched, listed them under unfinished and still said "completed" - and the task was accepted as done.
    [Fact]
    public async Task CompletedWithUnfinishedChanges_IsBlocked_AndTheWorkerIsToldSo()
    {
        var report = Parse("{\"parameters\":{\"status\":\"completed\",\"reason\":\"\",\"unfinished\":[\"Instrumentation 三个包升级到 1.15.3\"],\"unverified\":[\"x\"]}}");

        Assert.NotNull(report);
        Assert.True(report.IsBlocked);
        Assert.Null(report.Reason);

        var result = await new ReportTaskStatusTool().ExecuteAsync(new Dictionary<string, object?>
        {
            ["status"] = "completed",
            ["unfinished"] = new[] { "Instrumentation 三个包升级到 1.15.3" }
        });
        Assert.Contains("受阻", result.Output, StringComparison.Ordinal);

        var honest = await new ReportTaskStatusTool().ExecuteAsync(new Dictionary<string, object?> { ["status"] = "completed", ["unverified"] = new[] { "x" } });
        Assert.Equal("已记录：任务完成。", honest.Output);
    }

    [Fact]
    public void TheToolsContract_SeparatesUnfinishedChangesFromUnfinishedVerification()
    {
        var description = new ReportTaskStatusTool().Description;

        Assert.Contains("unverified", description, StringComparison.Ordinal);
        Assert.Contains("修改都已写入", description, StringComparison.Ordinal);       // what completed means
        Assert.DoesNotContain("都已完成并验证", description, StringComparison.Ordinal); // the wording that made unfinished verification a blocked task
    }

    [Fact]
    public void AnUnfinishedStringIsOneItem()
        => Assert.Equal(["Snappier"], Parse("{\"status\":\"blocked\",\"unfinished\":\"Snappier\"}")!.Unfinished);

    [Theory]
    [InlineData("completed", null, true)]
    [InlineData("blocked", "没有清单", true)]
    [InlineData("blocked", null, false)]
    [InlineData("blocked", "  ", false)]
    [InlineData("done", null, false)]
    public async Task TheTool_AcceptsOnlyAStatusItCanRecord(string status, string? reason, bool accepted)
    {
        var arguments = new Dictionary<string, object?> { ["status"] = status };
        if (reason != null) arguments["reason"] = reason;

        var result = await new ReportTaskStatusTool().ExecuteAsync(arguments);

        Assert.Equal(accepted, result.Status == CodexFlow.Core.Models.ToolResultStatus.Success);
    }

    [Fact]
    public void TheTool_IsReadOnly_NeverAWriteOrExecution()
    {
        Assert.Equal(ToolCapability.ReadOnly, ToolCapabilityCatalog.Resolve(WorkerStatusTool.Name));
        Assert.False(CodexFlow.Core.Agents.ToolClassification.IsWriteTool(WorkerStatusTool.Name));
    }
}
