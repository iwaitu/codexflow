// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using Xunit;

namespace CodexFlow.Core.Tests.Agents;

public class DefaultCodexValidatorChecklistEvaluationTests
{
    [Fact]
    public void ValidationResult_ShouldDefaultChecklistEvaluationToNull()
    {
        var result = new ValidationResult(true, "ok", []);

        Assert.Null(result.ChecklistEvaluation);
    }

    [Fact]
    public void BuildChecklistEvaluation_ShouldReturnNull_WhenTaskHasNoChecklist()
    {
        using var tempDir = new TempDir();
        var task = new CodexTask
        {
            Title = "No checklist task",
            Description = "No checklist here"
        };

        var evaluation = DefaultCodexValidator.BuildChecklistEvaluation(task, tempDir.Path);

        Assert.Null(evaluation);
    }

    [Fact]
    public void BuildChecklistEvaluation_ShouldClassifyCompletedFailedAndPendingItems()
    {
        using var tempDir = new TempDir();
        var infraServicePath = Path.Combine(tempDir.Path, "src", "CleanApp.Infrastructure", "Services");
        var infraProjectPath = Path.Combine(tempDir.Path, "src", "CleanApp.Infrastructure");
        var appPath = Path.Combine(tempDir.Path, "src", "CleanApp");
        var coreServicePath = Path.Combine(tempDir.Path, "src", "CleanApp.Core", "Services");

        Directory.CreateDirectory(infraServicePath);
        Directory.CreateDirectory(infraProjectPath);
        Directory.CreateDirectory(appPath);
        Directory.CreateDirectory(coreServicePath);

        File.WriteAllText(Path.Combine(infraServicePath, "MongoFileService.cs"), "namespace CleanApp.Infrastructure.Services;");
        File.WriteAllText(Path.Combine(infraProjectPath, "CleanApp.Infrastructure.csproj"), "<PackageReference Include=\"MongoDB.Driver\" />");
        File.WriteAllText(Path.Combine(appPath, "Program.cs"), "services.AddScoped<IMongoFileService, MongoFileService>();");
        File.WriteAllText(Path.Combine(coreServicePath, "MongoFileService.cs"), "legacy implementation");

        var task = new CodexTask
        {
            Id = "TASK_ARCH_003",
            Title = "[Infrastructure] 迁移 MongoFileService 实现并修复架构依赖",
            Description = """
                ## 任务执行顺序
                1. 创建 `src/CleanApp.Infrastructure/Services/MongoFileService.cs` → 完成实现迁移
                2. 更新 `src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj` → 添加 `MongoDB.Driver`
                3. 删除 `src/CleanApp.Core/Services/MongoFileService.cs` → 清理旧实现
                4. 更新 `src/CleanApp/Program.cs` → 修正 DI 注册
                5. 执行 `dotnet build` → 验证编译通过
                6. 执行 `dotnet test` → 验证测试通过
                """
        };
        task.ReplaceRequiredArtifacts(
        [
            new("file_exists", "src/CleanApp.Infrastructure/Services/MongoFileService.cs"),
            new("file_contains", "src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj", "MongoDB.Driver"),
            new("file_contains", "src/CleanApp/Program.cs", "IMongoFileService")
        ]);
        task.ReplaceForbiddenStates(
        [
            new("file_not_exists", "src/CleanApp.Core/Services/MongoFileService.cs")
        ]);
        task.ExecutionEvidence = new TaskExecutionEvidenceResult(
            ChangedFiles: ["src/CleanApp.Infrastructure/Services/MongoFileService.cs", "src/CleanApp/Program.cs"],
            CreatedFiles: ["src/CleanApp.Infrastructure/Services/MongoFileService.cs"],
            DeletedFiles: [],
            HasSuccessfulBuildEvidence: false,
            HasSuccessfulTestEvidence: false,
            AssertionResults: [],
            TotalToolCalls: 4,
            WriteToolCalls: 3);

        CodexTaskClassifier.NormalizeTask(task);
        var evaluation = DefaultCodexValidator.BuildChecklistEvaluation(
            task,
            tempDir.Path,
            ["缺少当前任务对应的 dotnet build 成功证据。"]);

        Assert.NotNull(evaluation);
        Assert.Equal(3, evaluation!.CompletedItems.Count);
        Assert.Equal(2, evaluation.FailedItems.Count);
        Assert.Single(evaluation.PendingItems);

        Assert.Contains(evaluation.CompletedItems, item => item.Text.Contains("MongoFileService.cs", StringComparison.OrdinalIgnoreCase) &&
                                                          item.Text.Contains("创建", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(evaluation.CompletedItems, item => item.Text.Contains("CleanApp.Infrastructure.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(evaluation.CompletedItems, item => item.Text.Contains("Program.cs", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(evaluation.FailedItems, item => item.Text.Contains("删除 `src/CleanApp.Core/Services/MongoFileService.cs`", StringComparison.Ordinal));
        Assert.Contains(evaluation.FailedItems, item => item.Text.Contains("dotnet build", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(evaluation.PendingItems, item => item.Text.Contains("dotnet test", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildChecklistEvaluation_ShouldKeepWeaklyMappedItemsPending_WhenNoStableEvidenceExists()
    {
        using var tempDir = new TempDir();
        var task = new CodexTask
        {
            Title = "[App] 调整配置",
            Description = """
                ## 任务执行顺序
                1. 调整启动配置
                2. 执行配置验证
                """
        };

        CodexTaskClassifier.NormalizeTask(task);
        var evaluation = DefaultCodexValidator.BuildChecklistEvaluation(task, tempDir.Path);

        Assert.NotNull(evaluation);
        Assert.Empty(evaluation!.CompletedItems);
        Assert.Empty(evaluation.FailedItems);
        Assert.Equal(2, evaluation.PendingItems.Count);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"checklist-eval-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }
}
