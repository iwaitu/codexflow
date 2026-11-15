// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Models;
using Xunit;

namespace CodexFlow.Core.Tests.Models;

public class CodexTaskProgressNormalizerTests
{
    [Fact]
    public void NormalizeTask_ShouldInitializeChecklist_FromTaskExecutionOrderSection()
    {
        var task = new CodexTask
        {
            Title = "[Infrastructure] 迁移 MongoFileService 实现并修复架构依赖",
            Description = """
                ## 阶段目标
                完成服务迁移。

                ## 任务执行顺序
                1. 创建 `src/CleanApp.Infrastructure/Services/MongoFileService.cs` → 完成实现迁移
                2. 更新 `src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj` → 添加 `MongoDB.Driver`
                3. 更新 `src/CleanApp/Program.cs` → 修正 DI 注册

                ## 影响范围
                - 代码目录：src/CleanApp.Infrastructure/*
                """
        };
        task.ReplaceRequiredArtifacts(
        [
            new("file_exists", "src/CleanApp.Infrastructure/Services/MongoFileService.cs"),
            new("file_contains", "src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj", "MongoDB.Driver"),
            new("file_contains", "src/CleanApp/Program.cs", "IMongoFileService")
        ]);

        CodexTaskClassifier.NormalizeTask(task);

        Assert.Equal(3, task.ChecklistItems.Count);
        Assert.Equal("CHK_001", task.ChecklistItems[0].Id);
        Assert.Equal(TaskChecklistItemStatus.Pending, task.ChecklistItems[0].Status);
        Assert.Contains(task.ChecklistItems[0].RelatedAssertions, key => key.Contains("src/CleanApp.Infrastructure/Services/MongoFileService.cs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(task.ChecklistItems[1].RelatedAssertions, key => key.Contains("src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NormalizeTask_ShouldSupportMarkdownVariants_InTaskExecutionOrderSection()
    {
        var task = new CodexTask
        {
            Title = "[Refactor] 更新接口与配置",
            Description = """
                ## 任务执行顺序
                **1.** 更新 `src/CleanApp.Core/Interfaces/IFileService.cs` 以抽离接口
                - 更新 `src/CleanApp/Program.cs` 注册新接口
                * 执行 `dotnet build` 验证编译
                """
        };

        CodexTaskClassifier.NormalizeTask(task);

        Assert.Equal(3, task.ChecklistItems.Count);
        Assert.Equal("更新 `src/CleanApp.Core/Interfaces/IFileService.cs` 以抽离接口", task.ChecklistItems[0].Text);
        Assert.Equal("更新 `src/CleanApp/Program.cs` 注册新接口", task.ChecklistItems[1].Text);
        Assert.Equal("执行 `dotnet build` 验证编译", task.ChecklistItems[2].Text);
    }

    [Fact]
    public void NormalizeTask_ShouldFallbackToEmptyChecklist_WhenFewerThanTwoItemsParsed()
    {
        var task = new CodexTask
        {
            Title = "[Single] 只做一个动作",
            Description = """
                ## 任务执行顺序
                1. 更新 `src/CleanApp/Program.cs`
                """
        };

        CodexTaskClassifier.NormalizeTask(task);

        Assert.Empty(task.ChecklistItems);
    }

    [Fact]
    public void NormalizeTask_ShouldBeIdempotent_ForChecklistInitialization()
    {
        var task = new CodexTask
        {
            Title = "[Infrastructure] 初始化 checklist",
            Description = """
                ## 任务执行顺序
                1. 创建 `src/CleanApp.Infrastructure/Services/MongoFileService.cs`
                2. 删除 `src/CleanApp.Core/Services/MongoFileService.cs`
                """
        };

        CodexTaskClassifier.NormalizeTask(task);
        CodexTaskClassifier.NormalizeTask(task);

        Assert.Equal(2, task.ChecklistItems.Count);
        Assert.Equal("CHK_001", task.ChecklistItems[0].Id);
        Assert.Equal("CHK_002", task.ChecklistItems[1].Id);
    }

    [Fact]
    public void NormalizeTask_ShouldNormalizePlannerProvidedChecklist_WithoutReparsing()
    {
        var task = new CodexTask
        {
            Title = "[Planner] 使用原生 checklist",
            Description = """
                ## 任务执行顺序
                1. 这段描述不应覆盖已有 checklist
                2. 这段描述不应覆盖已有 checklist
                """
        };
        task.ChecklistItems.Add(new TaskChecklistItem
        {
            Text = " 更新 `src/CleanApp/Program.cs` 注册 MongoFileService "
        });
        task.ReplaceRequiredArtifacts(
        [
            new("file_contains", "src/CleanApp/Program.cs", "MongoFileService")
        ]);

        CodexTaskClassifier.NormalizeTask(task);

        Assert.Single(task.ChecklistItems);
        Assert.Equal("CHK_001", task.ChecklistItems[0].Id);
        Assert.Equal("更新 `src/CleanApp/Program.cs` 注册 MongoFileService", task.ChecklistItems[0].Text);
        Assert.Contains(task.ChecklistItems[0].RelatedAssertions, key => key.Contains("src/CleanApp/Program.cs", StringComparison.OrdinalIgnoreCase));
    }
}
