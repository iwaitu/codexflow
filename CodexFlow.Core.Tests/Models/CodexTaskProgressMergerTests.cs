// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Models;
using Xunit;

namespace CodexFlow.Core.Tests.Models;

public class CodexTaskProgressMergerTests
{
    [Fact]
    public void MergeChecklistEvaluation_ShouldOverwriteLatestStatusAndEvidence()
    {
        var task = new CodexTask { Id = "TASK_MERGE_001", Title = "Merge Test" };
        task.ReplaceChecklistItems([
            new TaskChecklistItem { Id = "CHK_001", Text = "Create service" },
            new TaskChecklistItem { Id = "CHK_002", Text = "Update csproj" },
            new TaskChecklistItem { Id = "CHK_003", Text = "Run tests", Status = TaskChecklistItemStatus.Completed }
        ]);

        var evaluation = new ChecklistEvaluation
        {
            CompletedItems =
            [
                new ChecklistItemEvaluation
                {
                    ItemId = "CHK_001",
                    Text = "Create service",
                    Status = TaskChecklistItemStatus.Completed,
                    Evidence = ["Created src/App/Service.cs"]
                }
            ],
            FailedItems =
            [
                new ChecklistItemEvaluation
                {
                    ItemId = "CHK_002",
                    Text = "Update csproj",
                    Status = TaskChecklistItemStatus.Failed,
                    Evidence = ["Missing MongoDB.Driver reference"]
                }
            ],
            PendingItems =
            [
                new ChecklistItemEvaluation
                {
                    ItemId = "CHK_003",
                    Text = "Run tests",
                    Status = TaskChecklistItemStatus.Pending,
                    Evidence = []
                }
            ]
        };

        CodexTaskProgressMerger.MergeChecklistEvaluation(task, evaluation);

        Assert.Equal(TaskChecklistItemStatus.Completed, task.ChecklistItems[0].Status);
        Assert.Equal(["Created src/App/Service.cs"], task.ChecklistItems[0].Evidence);

        Assert.Equal(TaskChecklistItemStatus.Failed, task.ChecklistItems[1].Status);
        Assert.Equal(["Missing MongoDB.Driver reference"], task.ChecklistItems[1].Evidence);

        Assert.Equal(TaskChecklistItemStatus.Pending, task.ChecklistItems[2].Status);
        Assert.Empty(task.ChecklistItems[2].Evidence);
    }

    [Fact]
    public void MergeChecklistEvaluation_ShouldBeIdempotent_AndAllowStatusRegression()
    {
        var task = new CodexTask { Id = "TASK_MERGE_002", Title = "Merge Test" };
        task.ReplaceChecklistItems([
            new TaskChecklistItem { Id = "CHK_001", Text = "Update Program.cs" }
        ]);

        var first = new ChecklistEvaluation
        {
            CompletedItems =
            [
                new ChecklistItemEvaluation
                {
                    ItemId = "CHK_001",
                    Text = "Update Program.cs",
                    Status = TaskChecklistItemStatus.Completed,
                    Evidence = ["Program.cs contains IMongoFileService registration"]
                }
            ]
        };

        CodexTaskProgressMerger.MergeChecklistEvaluation(task, first);
        CodexTaskProgressMerger.MergeChecklistEvaluation(task, first);

        Assert.Equal(TaskChecklistItemStatus.Completed, task.ChecklistItems[0].Status);
        Assert.Equal(["Program.cs contains IMongoFileService registration"], task.ChecklistItems[0].Evidence);

        var second = new ChecklistEvaluation
        {
            FailedItems =
            [
                new ChecklistItemEvaluation
                {
                    ItemId = "CHK_001",
                    Text = "Update Program.cs",
                    Status = TaskChecklistItemStatus.Failed,
                    Evidence = ["dotnet build failed after Program.cs change"]
                }
            ]
        };

        CodexTaskProgressMerger.MergeChecklistEvaluation(task, second);

        Assert.Equal(TaskChecklistItemStatus.Failed, task.ChecklistItems[0].Status);
        Assert.Equal(["dotnet build failed after Program.cs change"], task.ChecklistItems[0].Evidence);
    }
}
