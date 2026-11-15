// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Constants;
using CodexFlow.Core.Models;
using CodexFlow.Core.Planning.Deviation;
using CodexFlow.Core.Planning.Patching;
using Xunit;

namespace CodexFlow.Core.Tests.Planning;

public sealed class PlanPatchServiceTests
{
    [Fact]
    public async Task CreatePendingPatchAsync_SetsMetadataWithoutRelaxingFallbackGate()
    {
        var service = new DefaultPlanPatchService();
        var dependency = new CodexTask { Id = "T1", RiskLevel = "High" };
        var downstream = new CodexTask { Id = "T2", RiskLevel = "High", UnsafeIfDependencyFallbackPassed = true };
        downstream.Dependencies.Add("T1");
        var session = new CodexSession { Id = "s1" };
        session.Plan.Add(dependency);
        session.Plan.Add(downstream);

        var decision = new PlanDeviationDecision(
            PlanDeviationSeverity.Recoverable,
            RequiresPlanPatch: true,
            RequiresPlanReset: false,
            "scope drift",
            ["超出当前任务范围"]);

        var result = await service.CreatePendingPatchAsync(new PlanPatchRequest(
            session,
            dependency,
            decision,
            null,
            null,
            "/tmp/patch.json"));

        Assert.True(result.RequiresUserConfirmation);
        Assert.Equal(bool.TrueString, session.Metadata[PlanGuardMetadataKeys.PlanPatchPending]);
        Assert.Equal("T1", session.Metadata[PlanGuardMetadataKeys.PlanPatchTaskId]);
        Assert.True(downstream.UnsafeIfDependencyFallbackPassed);
        Assert.Contains(result.Warnings, warning => warning.Contains("UnsafeIfDependencyFallbackPassed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResolvePendingPatchAsync_ApproveAddsSuggestedFilesAndClearsMetadata()
    {
        var service = new DefaultPlanPatchService();
        var task = new CodexTask { Id = "T1", Status = CodexTaskStatus.Failed };
        var session = new CodexSession { Id = "s1" };
        session.Plan.Add(task);

        var decision = new PlanDeviationDecision(
            PlanDeviationSeverity.Recoverable,
            RequiresPlanPatch: true,
            RequiresPlanReset: false,
            "scope drift",
            ["超出当前任务范围"])
        {
            SuggestedScopeFiles = ["src/NewService.cs"]
        };

        await service.CreatePendingPatchAsync(new PlanPatchRequest(session, task, decision, null, null, ".plan-patches/p1.json"));
        session.Metadata[PlanGuardMetadataKeys.PlanPatchWorkspacePath] = "/tmp/shadow-task-T1";

        var result = await service.ResolvePendingPatchAsync(new PlanPatchResolutionRequest(
            session,
            "T1",
            Approved: true,
            Notes: "expand scope"));

        Assert.True(result.Applied);
        Assert.Equal(CodexTaskStatus.Pending, task.Status);
        Assert.Contains("src/NewService.cs", task.Outputs);
        Assert.DoesNotContain(PlanGuardMetadataKeys.PlanPatchPending, session.Metadata.Keys);
        Assert.DoesNotContain(PlanGuardMetadataKeys.PlanPatchSuggestedFiles, session.Metadata.Keys);
        Assert.Equal("/tmp/shadow-task-T1", session.Metadata[PlanGuardMetadataKeys.PlanPatchApprovedWorkspacePath]);
        Assert.Equal("T1", session.Metadata[PlanGuardMetadataKeys.PlanPatchApprovedTaskId]);
    }

    [Fact]
    public async Task ResolvePendingPatchAsync_RejectClearsMetadataWithoutChangingOutputs()
    {
        var service = new DefaultPlanPatchService();
        var task = new CodexTask { Id = "T1", Status = CodexTaskStatus.Failed };
        task.Outputs.Add("src/Existing.cs");
        var session = new CodexSession { Id = "s1" };
        session.Plan.Add(task);

        var decision = new PlanDeviationDecision(
            PlanDeviationSeverity.Recoverable,
            RequiresPlanPatch: true,
            RequiresPlanReset: false,
            "scope drift",
            ["超出当前任务范围"])
        {
            SuggestedScopeFiles = ["src/NewService.cs"]
        };

        await service.CreatePendingPatchAsync(new PlanPatchRequest(session, task, decision, null, null, ".plan-patches/p1.json"));

        var result = await service.ResolvePendingPatchAsync(new PlanPatchResolutionRequest(
            session,
            "T1",
            Approved: false,
            Notes: "reject"));

        Assert.False(result.Applied);
        Assert.Equal(CodexTaskStatus.Failed, task.Status);
        Assert.Equal(["src/Existing.cs"], task.Outputs);
        Assert.DoesNotContain(PlanGuardMetadataKeys.PlanPatchPending, session.Metadata.Keys);
    }

    [Fact]
    public async Task ResolvePendingPatchAsync_ExplicitEmptyApprovedFilesDoesNotFallbackToSuggestedFiles()
    {
        var service = new DefaultPlanPatchService();
        var task = new CodexTask { Id = "T1", Status = CodexTaskStatus.Failed };
        var session = new CodexSession { Id = "s1" };
        session.Plan.Add(task);

        var decision = new PlanDeviationDecision(
            PlanDeviationSeverity.Recoverable,
            RequiresPlanPatch: true,
            RequiresPlanReset: false,
            "scope drift",
            ["超出当前任务范围"])
        {
            SuggestedScopeFiles = ["src/NewService.cs"]
        };

        await service.CreatePendingPatchAsync(new PlanPatchRequest(session, task, decision, null, null, ".plan-patches/p1.json"));

        var result = await service.ResolvePendingPatchAsync(new PlanPatchResolutionRequest(
            session,
            "T1",
            Approved: true,
            Notes: "approve without expanding files",
            ApprovedFiles: []));

        Assert.True(result.Applied);
        Assert.Empty(task.Outputs);
        Assert.Contains(result.Warnings, warning => warning.Contains("未自动扩展", StringComparison.Ordinal));
    }
}
