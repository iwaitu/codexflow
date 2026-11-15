// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Reflection;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using Xunit;

namespace CodexFlow.Core.Tests.Agents;

public class TaskDependencySchedulingTests
{
    [Fact]
    public void FindNextExecutableTask_SkipsTask_WhenDependencyNotSatisfied()
    {
        var taskA = new CodexTask { Id = "A", TaskType = "code", Status = CodexTaskStatus.Pending };
        var taskB = new CodexTask { Id = "B", TaskType = "code", Status = CodexTaskStatus.Pending };
        taskB.ReplaceDependencies(new[] { "A" });

        var plan = new List<CodexTask> { taskA, taskB };
        var result = InvokeFindNextExecutableTask(plan);

        Assert.NotNull(result);
        Assert.Equal("A", result.Id);
    }

    [Fact]
    public void FindNextExecutableTask_SelectsTask_WhenDependencySucceeded()
    {
        var taskA = new CodexTask { Id = "A", TaskType = "code", Status = CodexTaskStatus.Success };
        var taskB = new CodexTask { Id = "B", TaskType = "code", Status = CodexTaskStatus.Pending };
        taskB.ReplaceDependencies(new[] { "A" });

        var plan = new List<CodexTask> { taskA, taskB };
        var result = InvokeFindNextExecutableTask(plan);

        Assert.NotNull(result);
        Assert.Equal("B", result.Id);
    }

    [Fact]
    public void FindNextExecutableTask_BlocksTask_WhenDependencyCompletedWithWarnings_AndUnsafeFlag()
    {
        var taskA = new CodexTask { Id = "A", TaskType = "code", Status = CodexTaskStatus.CompletedWithWarnings };
        var taskB = new CodexTask { Id = "B", TaskType = "code", Status = CodexTaskStatus.Pending, UnsafeIfDependencyFallbackPassed = true };
        taskB.ReplaceDependencies(new[] { "A" });

        var plan = new List<CodexTask> { taskA, taskB };
        var result = InvokeFindNextExecutableTask(plan);

        // B should not be selected because A is CompletedWithWarnings and B's UnsafeIfDependencyFallbackPassed is true
        Assert.Null(result);
    }

    [Fact]
    public void FindNextExecutableTask_AllowsTask_WhenDependencyCompletedWithWarnings_AndSafeFlag()
    {
        var taskA = new CodexTask { Id = "A", TaskType = "code", Status = CodexTaskStatus.CompletedWithWarnings };
        taskA.ReplaceRequiredArtifacts(new[]
        {
            new ArtifactAssertion("file_exists", "file.cs")
        });
        taskA.ExecutionEvidence = new TaskExecutionEvidenceResult(
            ChangedFiles: new[] { "file.cs" },
            CreatedFiles: Array.Empty<string>(),
            DeletedFiles: Array.Empty<string>(),
            HasSuccessfulBuildEvidence: true,
            HasSuccessfulTestEvidence: false,
            AssertionResults: new[] { "All 1 artifact assertions passed." },
            TotalToolCalls: 3,
            WriteToolCalls: 1);
        var taskB = new CodexTask { Id = "B", TaskType = "code", Status = CodexTaskStatus.Pending, UnsafeIfDependencyFallbackPassed = false };
        taskB.ReplaceDependencies(new[] { "A" });

        var plan = new List<CodexTask> { taskA, taskB };
        var result = InvokeFindNextExecutableTask(plan);

        // B should be selected because UnsafeIfDependencyFallbackPassed=false and assertions passed
        Assert.NotNull(result);
        Assert.Equal("B", result.Id);
    }

    [Fact]
    public void FindNextExecutableTask_BlocksTask_WhenDependencyCompletedWithWarnings_ButAssertionsFailed()
    {
        var taskA = new CodexTask { Id = "A", TaskType = "code", Status = CodexTaskStatus.CompletedWithWarnings };
        taskA.ReplaceRequiredArtifacts(new[]
        {
            new ArtifactAssertion("file_exists", "OldFile.cs")
        });
        taskA.ExecutionEvidence = new TaskExecutionEvidenceResult(
            ChangedFiles: Array.Empty<string>(),
            CreatedFiles: Array.Empty<string>(),
            DeletedFiles: Array.Empty<string>(),
            HasSuccessfulBuildEvidence: false,
            HasSuccessfulTestEvidence: false,
            AssertionResults: new[] { "断言失败: 文件应存在但未找到: OldFile.cs" },
            TotalToolCalls: 3,
            WriteToolCalls: 1);
        var taskB = new CodexTask { Id = "B", TaskType = "code", Status = CodexTaskStatus.Pending, UnsafeIfDependencyFallbackPassed = false };
        taskB.ReplaceDependencies(new[] { "A" });

        var plan = new List<CodexTask> { taskA, taskB };
        var result = InvokeFindNextExecutableTask(plan);

        // B should not be selected because A's assertions failed
        Assert.Null(result);
    }

    [Fact]
    public void FindNextExecutableTask_BlocksTask_WhenDependencyCompletedWithWarnings_ButEvidenceMissing()
    {
        var taskA = new CodexTask { Id = "A", TaskType = "code", Status = CodexTaskStatus.CompletedWithWarnings };
        taskA.ReplaceRequiredArtifacts(new[]
        {
            new ArtifactAssertion("file_exists", "Program.cs")
        });
        var taskB = new CodexTask { Id = "B", TaskType = "code", Status = CodexTaskStatus.Pending, UnsafeIfDependencyFallbackPassed = false };
        taskB.ReplaceDependencies(new[] { "A" });

        var plan = new List<CodexTask> { taskA, taskB };
        var result = InvokeFindNextExecutableTask(plan);

        Assert.Null(result);
    }

    [Fact]
    public void FindNextExecutableTask_ReturnsNull_WhenDependencyIsUnknown()
    {
        var taskB = new CodexTask { Id = "B", TaskType = "code", Status = CodexTaskStatus.Pending };
        taskB.ReplaceDependencies(new[] { "UNKNOWN_TASK" });

        var plan = new List<CodexTask> { taskB };
        var result = InvokeFindNextExecutableTask(plan);

        // Unknown dependency = not satisfied (fail-closed)
        Assert.Null(result);
    }

    [Fact]
    public void FindNextExecutableTask_ReturnsNull_WhenDependencyIsPending()
    {
        var taskA = new CodexTask { Id = "A", TaskType = "code", Status = CodexTaskStatus.Pending };
        var taskB = new CodexTask { Id = "B", TaskType = "code", Status = CodexTaskStatus.Pending };
        taskB.ReplaceDependencies(new[] { "A" });

        var plan = new List<CodexTask> { taskA, taskB };
        var result = InvokeFindNextExecutableTask(plan);

        // B depends on A which is still Pending — should select A first
        Assert.NotNull(result);
        Assert.Equal("A", result.Id);
    }

    [Fact]
    public void IsPlanFullyCompleted_ReturnsFalse_WithCompletedWithWarnings()
    {
        var taskA = new CodexTask { Id = "A", Status = CodexTaskStatus.CompletedWithWarnings };
        var plan = new List<CodexTask> { taskA };

        var result = CodexPlanStateGuards.IsPlanFullyCompleted(plan);

        Assert.False(result);
    }

    [Fact]
    public void IsPlanFullyCompleted_ReturnsTrue_WithOnlySuccessAndSkipped()
    {
        var taskA = new CodexTask { Id = "A", Status = CodexTaskStatus.Success };
        var taskB = new CodexTask { Id = "B", Status = CodexTaskStatus.Skipped };
        var plan = new List<CodexTask> { taskA, taskB };

        var result = CodexPlanStateGuards.IsPlanFullyCompleted(plan);

        Assert.True(result);
    }

    [Fact]
    public void IsPlanFullyCompleted_ReturnsFalse_WithBlockedByDependency()
    {
        var taskA = new CodexTask { Id = "A", Status = CodexTaskStatus.BlockedByDependency };
        var plan = new List<CodexTask> { taskA };

        var result = CodexPlanStateGuards.IsPlanFullyCompleted(plan);

        Assert.False(result);
    }

    [Fact]
    public void FindNextExecutableTask_RequeuesBlockedTask_WhenDependenciesBecomeSatisfied()
    {
        var taskA = new CodexTask { Id = "A", TaskType = "code", Status = CodexTaskStatus.Success };
        var taskB = new CodexTask { Id = "B", TaskType = "code", Status = CodexTaskStatus.BlockedByDependency };
        taskB.ReplaceDependencies(new[] { "A" });

        var plan = new List<CodexTask> { taskA, taskB };
        var result = InvokeFindNextExecutableTask(plan);

        Assert.NotNull(result);
        Assert.Equal("B", result.Id);
    }

    private static CodexTask? InvokeFindNextExecutableTask(IEnumerable<CodexTask> plan)
    {
        var method = typeof(CodexOrchestrator).GetMethod("FindNextExecutableTask", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (CodexTask?)method!.Invoke(null, new object?[] { plan });
    }
}
