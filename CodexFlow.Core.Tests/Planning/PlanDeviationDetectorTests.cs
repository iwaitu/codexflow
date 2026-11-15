// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using CodexFlow.Core.Planning.Deviation;
using Xunit;

namespace CodexFlow.Core.Tests.Planning;

public sealed class PlanDeviationDetectorTests
{
    [Fact]
    public async Task DetectAsync_TaskMissingFromPlan_ReturnsPlanInvalidated()
    {
        var detector = new DefaultPlanDeviationDetector();
        var session = new CodexSession { Id = "s1" };
        var task = new CodexTask { Id = "missing", Title = "Missing" };

        var decision = await detector.DetectAsync(new PlanDeviationRequest(
            session,
            task,
            "/repo",
            "/repo",
            Array.Empty<GitFileChange>(),
            null,
            null,
            null));

        Assert.Equal(PlanDeviationSeverity.PlanInvalidated, decision.Severity);
        Assert.True(decision.RequiresPlanReset);
    }

    [Fact]
    public async Task DetectAsync_OutOfScopeChange_ReturnsRecoverablePatch()
    {
        var detector = new DefaultPlanDeviationDetector();
        var task = new CodexTask { Id = "T1", Title = "Update allowed file", Description = "修改 src/App/Allowed.cs" };
        task.ReplaceRequiredArtifacts([new ArtifactAssertion("file_exists", "src/App/Allowed.cs")]);
        var session = new CodexSession { Id = "s1" };
        session.Plan.Add(task);
        var scope = TaskFileScopeGuard.BuildTaskFileScope(task);

        var decision = await detector.DetectAsync(new PlanDeviationRequest(
            session,
            task,
            "/repo",
            "/repo",
            [new GitFileChange("src/App/Other.cs", "M")],
            scope,
            null,
            null));

        Assert.Equal(PlanDeviationSeverity.Recoverable, decision.Severity);
        Assert.True(decision.RequiresPlanPatch);
        Assert.False(decision.RequiresPlanReset);
    }
}
