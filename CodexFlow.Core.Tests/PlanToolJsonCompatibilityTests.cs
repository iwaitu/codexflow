// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Models;
using CodexFlow.Core.Planning.Artifacts;
using CodexFlow.Core.Planning.Patching;
using CodexFlow.Core.Services;
using Newtonsoft.Json;
using Xunit;

namespace CodexFlow.Core.Tests;

public sealed class PlanToolJsonCompatibilityTests
{
    [Fact]
    public async Task SavedPlanKeepsIndependentNestedCollectionsAndOptionalForbiddenPaths()
    {
        var artifact = new PlanArtifact { Goal = "检查计划", Markdown = "### 计划", Projection = new PlanProjectionRecord() };
        artifact.Projection.ReplaceTaskIds(["untouched", "closed"]);
        artifact.Projection.ReplaceValidationErrors(["check"]);
        artifact.Projection.ReplaceValidationWarnings(["warning"]);
        artifact.Reviews.Add(new PlanReviewRecord { Reviewer = "reviewer", Summary = "通过" });
        artifact.Tasks.Add(new CodexTask
        {
            Id = "untouched",
            Title = "修复",
            Status = CodexTaskStatus.CompletedWithWarnings,
            StartedAt = new DateTime(2026, 11, 20, 9, 12, 34, DateTimeKind.Utc).AddTicks(1234560)
        });
        artifact.Tasks.Add(new CodexTask { Id = "closed", ForbiddenPaths = [] });
        artifact.Tasks[0].ReplaceDependencies(["closed"]);
        artifact.Tasks[0].ReplaceInputs(["docs/input.md"]);
        artifact.Tasks[0].ReplaceOutputs(["docs/output.md"]);
        artifact.Tasks[0].RequiredArtifacts.Add(new ArtifactAssertion("file_contains", "docs/output.md", "内容"));
        artifact.Tasks[0].ForbiddenStates.Add(new ArtifactAssertion("file_exists", "obsolete.md"));
        var checklist = new TaskChecklistItem { Id = "check", Text = "检查", Status = TaskChecklistItemStatus.Completed };
        checklist.ReplaceEvidence(["checked"]);
        checklist.ReplaceRelatedAssertions(["content"]);
        artifact.Tasks[0].ChecklistItems.Add(checklist);

        var expected = JsonConvert.SerializeObject(artifact.Tasks, Formatting.Indented);
        Assert.Equal(expected, PlanToolJson.Write(artifact.Tasks, indented: true));

        var store = new InMemoryPlanArtifactStore();
        await store.SaveAsync(artifact);
        checklist.Evidence.Clear();
        artifact.Tasks[0].RequiredArtifacts.Clear();
        artifact.Reviews.Clear();
        artifact.Projection.TaskIds.Clear();

        var copy = Assert.IsType<PlanArtifact>(await store.GetAsync(artifact.Id));
        Assert.Equal(expected, JsonConvert.SerializeObject(copy.Tasks, Formatting.Indented));
        Assert.Equal("checked", Assert.Single(copy.Tasks[0].ChecklistItems[0].Evidence));
        Assert.Equal("content", Assert.Single(copy.Tasks[0].ChecklistItems[0].RelatedAssertions));
        Assert.Null(copy.Tasks[0].ForbiddenPaths);
        Assert.Empty(copy.Tasks[1].ForbiddenPaths!);
        Assert.Equal("reviewer", Assert.Single(copy.Reviews).Reviewer);
        Assert.Equal(new[] { "untouched", "closed" }, copy.Projection!.TaskIds);
        copy.Projection.TaskIds.Clear();
        var second = Assert.IsType<PlanArtifact>(await store.GetAsync(artifact.Id));
        Assert.Equal(new[] { "untouched", "closed" }, second.Projection!.TaskIds);
        Assert.Equal("check", Assert.Single(second.Projection.ValidationErrors));
        Assert.Equal("warning", Assert.Single(second.Projection.ValidationWarnings));
    }

    [Fact]
    public void LegacyPlanReadsCaseInsensitiveFieldsNamesNumericStringsAndCollectionDefaults()
    {
        const string json = """
            [{'id':'t1','title':'任务','status':'completedwithwarnings','StageId':'2','ComplexityLevel':'3',
              'UnsafeIfDependencyFallbackPassed':'false','dependencies':null,'checklistItems':[{
                'id':'c1','text':'验收','Status':'Completed','Evidence':['checked'],'RelatedAssertions':null,
              }],'requiredArtifacts':[{'type':'file_contains','path':'docs/out.md','text':'内容'}],
              'forbiddenPaths':[],
            }]
            """;

        var expected = JsonConvert.DeserializeObject<List<CodexTask>>(json);
        var actual = PlanToolJson.Read<List<CodexTask>>(json);
        Assert.Equal(JsonConvert.SerializeObject(expected), JsonConvert.SerializeObject(actual));
        var task = Assert.Single(actual!);
        Assert.Empty(task.Dependencies);
        Assert.Equal(CodexTaskStatus.CompletedWithWarnings, task.Status);
        Assert.Equal(3, task.ComplexityLevel);
        Assert.False(task.UnsafeIfDependencyFallbackPassed);
        Assert.Empty(task.ForbiddenPaths!);
        Assert.Equal("内容", Assert.Single(task.RequiredArtifacts).Text);
    }

    [Fact]
    public void TaskChecklistKeepsMalformedFallbackAndLegacyNames()
    {
        const string json = """[{'Text':'注','Status':'Blocked','Evidence':[true,123,null],'RelatedAssertions':['done']}]""";
        var expected = JsonConvert.DeserializeObject<List<TaskChecklistItem>>(json);
        Assert.Equal(JsonConvert.SerializeObject(expected), JsonConvert.SerializeObject(TaskCrudToolSupport.ParseChecklist(json)));
        Assert.Empty(TaskCrudToolSupport.ParseChecklist("""[{"Status":"not_an_enum"}]"""));
    }

    [Fact]
    public void PatchAndExecutionEnvelopeKeysStayUnchanged()
    {
        var result = new PlanPatchResult(true, false, [], "已确认", []);
        var cleanup = new List<string> { "清理" };
        var metadata = new Dictionary<string, string> { ["Existing-Key"] = "值" };
        Assert.Equal(
            JsonConvert.SerializeObject(new { result, CleanupWarnings = cleanup, SnapshotJson = (string?)null, Metadata = metadata }, Formatting.Indented),
            PlanToolJson.Write(new PlanPatchResolutionPayload(result, cleanup, null, metadata), indented: true));
        Assert.Equal(
            JsonConvert.SerializeObject(new { Success = true, Message = "完成", TaskId = "t1" }),
            PlanToolJson.Write(new ExecuteCodeTaskPayload(true, "完成", "t1")));
    }
}
