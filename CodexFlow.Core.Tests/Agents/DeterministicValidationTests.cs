// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using Xunit;

namespace CodexFlow.Core.Tests.Agents;

public class DeterministicValidationTests
{
    [Fact]
    public void EvaluateArtifactAssertions_file_exists_Fails_WhenFileMissing()
    {
        using var tmp = new TempDir();
        var assertions = new List<ArtifactAssertion>
        {
            new("file_exists", Path.Combine(tmp.Path, "nonexistent.cs"))
        };

        var issues = DefaultCodexValidator.EvaluateArtifactAssertions(tmp.Path, assertions);

        Assert.Single(issues);
        Assert.Contains("应存在但未找到", issues[0]);
    }

    [Fact]
    public void EvaluateArtifactAssertions_file_exists_Passes_WhenFilePresent()
    {
        using var tmp = new TempDir();
        var filePath = Path.Combine(tmp.Path, "existing.cs");
        File.WriteAllText(filePath, "// hello");

        var assertions = new List<ArtifactAssertion>
        {
            new("file_exists", filePath)
        };

        var issues = DefaultCodexValidator.EvaluateArtifactAssertions(tmp.Path, assertions);

        Assert.Empty(issues);
    }

    [Fact]
    public void EvaluateArtifactAssertions_file_not_exists_Fails_WhenFilePresent()
    {
        using var tmp = new TempDir();
        var filePath = Path.Combine(tmp.Path, "old_file.cs");
        File.WriteAllText(filePath, "// old");

        var assertions = new List<ArtifactAssertion>
        {
            new("file_not_exists", filePath)
        };

        var issues = DefaultCodexValidator.EvaluateArtifactAssertions(tmp.Path, assertions);

        Assert.Single(issues);
        Assert.Contains("应不存在但实际存在", issues[0]);
    }

    [Fact]
    public void EvaluateArtifactAssertions_file_not_exists_Passes_WhenFileMissing()
    {
        using var tmp = new TempDir();

        var assertions = new List<ArtifactAssertion>
        {
            new("file_not_exists", Path.Combine(tmp.Path, "gone.cs"))
        };

        var issues = DefaultCodexValidator.EvaluateArtifactAssertions(tmp.Path, assertions);

        Assert.Empty(issues);
    }

    [Fact]
    public void EvaluateArtifactAssertions_file_contains_Fails_WhenTextAbsent()
    {
        using var tmp = new TempDir();
        var filePath = Path.Combine(tmp.Path, "Program.cs");
        File.WriteAllText(filePath, "using System;\n// nothing relevant");

        var assertions = new List<ArtifactAssertion>
        {
            new("file_contains", filePath, "MongoFileService")
        };

        var issues = DefaultCodexValidator.EvaluateArtifactAssertions(tmp.Path, assertions);

        Assert.Single(issues);
        Assert.Contains("应包含", issues[0]);
    }

    [Fact]
    public void EvaluateArtifactAssertions_file_contains_Passes_WhenTextPresent()
    {
        using var tmp = new TempDir();
        var filePath = Path.Combine(tmp.Path, "Program.cs");
        File.WriteAllText(filePath, "services.AddScoped<IMongoFileService, MongoFileService>();");

        var assertions = new List<ArtifactAssertion>
        {
            new("file_contains", filePath, "MongoFileService")
        };

        var issues = DefaultCodexValidator.EvaluateArtifactAssertions(tmp.Path, assertions);

        Assert.Empty(issues);
    }

    [Fact]
    public void EvaluateArtifactAssertions_file_not_contains_Fails_WhenTextPresent()
    {
        using var tmp = new TempDir();
        var filePath = Path.Combine(tmp.Path, "Core.csproj");
        File.WriteAllText(filePath, @"<PackageReference Include=""MongoDB.Driver"" />");

        var assertions = new List<ArtifactAssertion>
        {
            new("file_not_contains", filePath, "MongoDB.Driver")
        };

        var issues = DefaultCodexValidator.EvaluateArtifactAssertions(tmp.Path, assertions);

        Assert.Single(issues);
        Assert.Contains("不应包含", issues[0]);
    }

    [Fact]
    public void EvaluateArtifactAssertions_file_not_contains_Passes_WhenTextAbsent()
    {
        using var tmp = new TempDir();
        var filePath = Path.Combine(tmp.Path, "Core.csproj");
        File.WriteAllText(filePath, @"<PackageReference Include=""Newtonsoft.Json"" />");

        var assertions = new List<ArtifactAssertion>
        {
            new("file_not_contains", filePath, "MongoDB.Driver")
        };

        var issues = DefaultCodexValidator.EvaluateArtifactAssertions(tmp.Path, assertions);

        Assert.Empty(issues);
    }

    [Fact]
    public void EvaluateArtifactAssertions_UnknownType_ReturnsIssue()
    {
        using var tmp = new TempDir();

        var assertions = new List<ArtifactAssertion>
        {
            new("unknown_type", "some/path")
        };

        var issues = DefaultCodexValidator.EvaluateArtifactAssertions(tmp.Path, assertions);

        Assert.Single(issues);
        Assert.Contains("未知断言类型", issues[0]);
    }

    /// <summary>
    /// Reproduces TASK_ARCH_002 scenario: migration task where old file still exists
    /// and new file is missing — all assertions should fail.
    /// </summary>
    [Fact]
    public void EvaluateArtifactAssertions_MigrationScenario_OldFileExists_NewFileMissing()
    {
        using var tmp = new TempDir();
        // Old file still exists at Core/Services/MongoFileService.cs
        var oldDir = Path.Combine(tmp.Path, "Core", "Services");
        Directory.CreateDirectory(oldDir);
        File.WriteAllText(Path.Combine(oldDir, "MongoFileService.cs"), "namespace Core.Services;");

        // Old csproj still references MongoDB.Driver
        File.WriteAllText(Path.Combine(tmp.Path, "Core", "Core.csproj"),
            @"<PackageReference Include=""MongoDB.Driver"" />");

        var assertions = new List<ArtifactAssertion>
        {
            // New file should exist but doesn't
            new("file_exists", Path.Combine(tmp.Path, "Infrastructure", "Services", "MongoFileService.cs")),
            // Old file should not exist but does
            new("file_not_exists", Path.Combine(oldDir, "MongoFileService.cs")),
            // Old csproj should not reference MongoDB.Driver but does
            new("file_not_contains", Path.Combine(tmp.Path, "Core", "Core.csproj"), "MongoDB.Driver"),
        };

        var issues = DefaultCodexValidator.EvaluateArtifactAssertions(tmp.Path, assertions);

        Assert.Equal(3, issues.Count);
    }

    /// <summary>
    /// Uses relative paths (the common case from planner-generated assertions).
    /// </summary>
    [Fact]
    public void EvaluateArtifactAssertions_RelativePaths_ResolvedCorrectly()
    {
        using var tmp = new TempDir();
        var srcDir = Path.Combine(tmp.Path, "src", "MyApp");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "Program.cs"), "using System;");

        var assertions = new List<ArtifactAssertion>
        {
            new("file_exists", Path.Combine("src", "MyApp", "Program.cs"))
        };

        var issues = DefaultCodexValidator.EvaluateArtifactAssertions(tmp.Path, assertions);

        Assert.Empty(issues);
    }

    // Live run (dependency-upgrade-compliant, replayed with qre): the planner added "T7 执行 CleanAppCoreTests 回归测试验证 … 仅执行 dotnet test 命令 …
    // 不进行代码修改" as a code task; its forge worker ran the tests, was asked for a write, created an empty directory and the job failed.
    [Fact]
    public void NormalizePlan_FoldsATestOnlyTask_IntoTheLastCodeTaskBeforeIt()
    {
        var upgrade = new CodexTask { Id = "T6", Title = "升级 Aspire.Hosting.AppHost 到 13.6.5", Description = "修改 CleanApp.AppHost.csproj 中的版本。", TaskType = "code" };
        var verify = new CodexTask
        {
            Id = "T7",
            Title = "执行 CleanAppCoreTests 回归测试验证",
            Description = "## 阶段范围\n仅执行 dotnet test 命令，过滤 CleanAppCoreTests。不进行代码修改。",
            TaskType = "code"
        };
        verify.ReplaceDependencies(["T6"]);
        verify.ReplaceChecklistItems([new TaskChecklistItem { Id = "CHK_T7_1", Text = "执行 dotnet test --filter FullyQualifiedName~CleanAppCoreTests" }]);
        var tasks = new List<CodexTask> { upgrade, verify };

        CodexTaskClassifier.NormalizePlan(tasks);

        var only = Assert.Single(tasks);
        Assert.Equal("T6", only.Id);
        Assert.Contains("收尾验证（原任务 T7", only.Description, StringComparison.Ordinal);
        Assert.Contains("dotnet test --filter FullyQualifiedName~CleanAppCoreTests", only.Description, StringComparison.Ordinal);
    }

    // Live run (same scenario, later): "T6 运行核心测试验证 … 执行测试命令" said nothing about not changing code, declared no output, and its forge worker
    // inserted a marker comment into a csproj to have something to show.
    [Fact]
    public void NormalizePlan_FoldsATaskThatDeclaresNoOutput_UnderATitleThatOnlyRunsTheTests()
    {
        var pin = new CodexTask { Id = "T5", Title = "固定 Snappier 与 SQLitePCLRaw 版本", Description = "修改三个 csproj。", TaskType = "code" };
        var verify = new CodexTask
        {
            Id = "T6", Title = "运行核心测试验证", TaskType = "code",
            Description = "## 阶段目标\n运行 CleanAppCoreTests 确保业务逻辑不受依赖升级影响。\n\n## 关键任务\n- 执行 dotnet test"
        };
        // the same title on a task that declares what it produces is a code task
        var declares = new CodexTask { Id = "T7", Title = "运行核心测试验证", Description = "执行 dotnet test", TaskType = "code" };
        declares.Outputs.Add("test/CleanAppCoreTests/SlugTests.cs");
        var tasks = new List<CodexTask> { pin, verify, declares };

        CodexTaskClassifier.NormalizePlan(tasks);

        Assert.Equal(["T5", "T7"], tasks.Select(task => task.Id));
        Assert.Contains("收尾验证（原任务 T6", tasks[0].Description, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizePlan_KeepsATestOnlyTask_ThatAnotherTaskDependsOn_OrThatHasNoCodeTaskBeforeIt()
    {
        CodexTask Verify(string id) => new() { Id = id, Title = "回归测试验证", Description = "仅执行 dotnet test。不进行代码修改。", TaskType = "code" };
        var later = new CodexTask { Id = "T3", Title = "修复登录大小写比较", Description = "修改 LoginService.cs。", TaskType = "code" };
        later.ReplaceDependencies(["T2"]);
        var dependedOn = new List<CodexTask>
        {
            new() { Id = "T1", Title = "升级包版本", Description = "修改 csproj。", TaskType = "code" }, Verify("T2"), later
        };
        var first = new List<CodexTask> { Verify("T1"), new() { Id = "T2", Title = "升级包版本", Description = "修改 csproj。", TaskType = "code" } };
        // a scope limit on a task that does change code is not a statement that it changes none
        var scoped = new List<CodexTask>
        {
            new() { Id = "T1", Title = "升级包版本", Description = "修改 csproj。", TaskType = "code" },
            new() { Id = "T2", Title = "固定 Snappier 版本并运行测试", Description = "修改 csproj，执行 dotnet test。不进行业务代码修改。", TaskType = "code" }
        };

        CodexTaskClassifier.NormalizePlan(dependedOn);
        CodexTaskClassifier.NormalizePlan(first);
        CodexTaskClassifier.NormalizePlan(scoped);

        Assert.Equal(3, dependedOn.Count);
        Assert.Equal(2, first.Count);
        Assert.Equal(2, scoped.Count);
    }

    [Fact]
    public void NormalizePlan_ShouldFlipDeletionAssertion_ForMigratedOldFile()
    {
        var task = new CodexTask
        {
            Id = "TASK_ARCH_003",
            Title = "[Infrastructure] 迁移 MongoFileService 实现并修复架构依赖",
            Description =
                "1. 创建 `src/CleanApp.Infrastructure/Services/MongoFileService.cs`\n" +
                "2. 删除 `src/CleanApp.Core/Services/MongoFileService.cs`\n" +
                "3. 更新 `src/CleanApp/Program.cs`"
        };
        task.ReplaceRequiredArtifacts([
            new("file_exists", "src/CleanApp.Infrastructure/Services/MongoFileService.cs"),
            new("file_exists", "src/CleanApp.Core/Services/MongoFileService.cs")
        ]);

        CodexTaskClassifier.NormalizePlan([task]);

        Assert.Contains(task.RequiredArtifacts, a =>
            a.Type == "file_exists" &&
            a.Path == "src/CleanApp.Infrastructure/Services/MongoFileService.cs");
        Assert.DoesNotContain(task.RequiredArtifacts, a =>
            a.Path == "src/CleanApp.Core/Services/MongoFileService.cs");
        Assert.Contains(task.ForbiddenStates, a =>
            a.Type == "file_not_exists" &&
            a.Path == "src/CleanApp.Core/Services/MongoFileService.cs");
    }

    [Fact]
    public void NormalizeTask_ShouldDeduplicateContradictoryExistenceAssertions()
    {
        var task = new CodexTask
        {
            Title = "[Cleanup] 删除旧文件",
            Description = "删除 `src/Legacy/OldFile.cs`，保留新实现。"
        };
        task.ReplaceRequiredArtifacts([
            new("file_exists", "src/Legacy/OldFile.cs"),
            new("file_not_exists", "src/Legacy/OldFile.cs")
        ]);

        CodexTaskClassifier.NormalizeTask(task);

        Assert.Empty(task.RequiredArtifacts.Where(a =>
            string.Equals(a.Path, "src/Legacy/OldFile.cs", StringComparison.OrdinalIgnoreCase)));
        Assert.Single(task.ForbiddenStates.Where(a =>
            string.Equals(a.Path, "src/Legacy/OldFile.cs", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Type, "file_not_exists", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void NormalizeTask_ShouldAddCsprojReferenceAssertions_FromTaskDescription()
    {
        var task = new CodexTask
        {
            Title = "[Core] 解耦项目引用",
            Description =
                "1. 修改 src/CleanApp.Core/CleanApp.Core.csproj → 移除 src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj 的 ProjectReference\n" +
                "2. 更新 src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj → 添加 src/CleanApp.Core/CleanApp.Core.csproj 的 ProjectReference"
        };

        CodexTaskClassifier.NormalizeTask(task);

        Assert.Contains(task.RequiredArtifacts, a =>
            a.Type == "file_not_contains" &&
            a.Path == "src/CleanApp.Core/CleanApp.Core.csproj" &&
            a.Text == "CleanApp.Infrastructure");
        Assert.DoesNotContain(task.RequiredArtifacts, a =>
            a.Type == "file_contains" &&
            a.Path == "src/CleanApp.Core/CleanApp.Core.csproj" &&
            a.Text == "CleanApp.Infrastructure");
    }

    [Fact]
    public void NormalizeTask_ShouldFlipCircularCoreInfrastructureContract_WhenDependencyInversionTaskDeclaresBothDirections()
    {
        var task = new CodexTask
        {
            Title = "[Core] 架构依赖倒置修复",
            Description =
                "## 阶段目标\n修复 Core 对 Infrastructure 的反向依赖。\n" +
                "## 关键任务\n- 将 IUnitOfWork 从 Infrastructure 层上移到 Core 层\n" +
                "- 修改 src/CleanApp.Core/CleanApp.Core.csproj 移除对 CleanApp.Infrastructure 的引用\n" +
                "- 修改 src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj 添加对 CleanApp.Core 的引用"
        };
        task.ReplaceRequiredArtifacts([
            new("file_contains", "src/CleanApp.Core/CleanApp.Core.csproj", "CleanApp.Infrastructure"),
            new("file_contains", "src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj", "CleanApp.Core")
        ]);

        CodexTaskClassifier.NormalizeTask(task);

        Assert.DoesNotContain(task.RequiredArtifacts, a =>
            a.Type == "file_contains" &&
            a.Path == "src/CleanApp.Core/CleanApp.Core.csproj" &&
            a.Text == "CleanApp.Infrastructure");
        Assert.Contains(task.RequiredArtifacts, a =>
            a.Type == "file_not_contains" &&
            a.Path == "src/CleanApp.Core/CleanApp.Core.csproj" &&
            a.Text == "CleanApp.Infrastructure");
        Assert.Contains(task.RequiredArtifacts, a =>
            a.Type == "file_contains" &&
            a.Path == "src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj" &&
            a.Text == "CleanApp.Core");
    }

    [Fact]
    public void NormalizeTask_ShouldFlipOldLayerContract_WhenDescriptionMovesInterfaceToCore()
    {
        var task = new CodexTask
        {
            Title = "【严重】Core层架构重构：接口抽象与依赖解耦",
            Description =
                "## 阶段范围\n- 将 IUnitOfWork 接口从 Infrastructure 层移至 Core 层\n" +
                "## 任务执行顺序\n1. 创建 src/CleanApp.Core/Interfaces/IUnitOfWork.cs\n2. 删除 src/CleanApp.Infrastructure/IUnitOfWork.cs"
        };
        task.ReplaceRequiredArtifacts([
            new("file_exists", "src/CleanApp.Core/Interfaces/IUnitOfWork.cs"),
            new("file_contains", "src/CleanApp.Infrastructure/IUnitOfWork.cs", "interface IUnitOfWork")
        ]);

        CodexTaskClassifier.NormalizeTask(task);

        Assert.DoesNotContain(task.RequiredArtifacts, a =>
            a.Path == "src/CleanApp.Infrastructure/IUnitOfWork.cs" &&
            a.Type == "file_contains");
        Assert.Contains(task.ForbiddenStates, a =>
            a.Path == "src/CleanApp.Infrastructure/IUnitOfWork.cs" &&
            a.Type == "file_not_exists");
    }

    [Fact]
    public void NormalizeTask_ShouldRelocateMongoDriverAssertion_FromCoreToInfrastructureProject()
    {
        var task = new CodexTask
        {
            Title = "[Core/Infrastructure] 修复依赖倒置违规并迁移 MongoDB 服务",
            Description =
                "1. 修改 src/CleanApp.Core/CleanApp.Core.csproj → 移除旧的 MongoDB.Driver 引用\n" +
                "2. 修改 src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj → 添加 MongoDB.Driver 包引用\n" +
                "3. 迁移 MongoDB 服务到 Infrastructure 层"
        };
        task.ReplaceRequiredArtifacts([
            new("file_contains", "src/CleanApp.Core/CleanApp.Core.csproj", "MongoDB.Driver")
        ]);

        CodexTaskClassifier.NormalizeTask(task);

        Assert.DoesNotContain(task.RequiredArtifacts, a =>
            a.Type == "file_contains" &&
            a.Path == "src/CleanApp.Core/CleanApp.Core.csproj" &&
            a.Text == "MongoDB.Driver");
        Assert.Contains(task.RequiredArtifacts, a =>
            a.Type == "file_not_contains" &&
            a.Path == "src/CleanApp.Core/CleanApp.Core.csproj" &&
            a.Text == "MongoDB.Driver");
        Assert.Contains(task.RequiredArtifacts, a =>
            a.Type == "file_contains" &&
            a.Path == "src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj" &&
            a.Text == "MongoDB.Driver");
    }

    [Fact]
    public void NormalizeTask_ShouldFlipCoreServiceAssertion_ForInfrastructureLeakageToken()
    {
        var task = new CodexTask
        {
            Title = "[Core] 依赖倒置修复",
            Description =
                "1. 修改 src/CleanApp.Core/Services/FileService.cs → 移除对 AppDbContext 的直接依赖\n" +
                "2. 通过接口抽象解耦 Core 与 Infrastructure"
        };
        task.ReplaceRequiredArtifacts([
            new("file_contains", "src/CleanApp.Core/Services/FileService.cs", "AppDbContext")
        ]);

        CodexTaskClassifier.NormalizeTask(task);

        Assert.DoesNotContain(task.RequiredArtifacts, a =>
            a.Type == "file_contains" &&
            a.Path == "src/CleanApp.Core/Services/FileService.cs" &&
            a.Text == "AppDbContext");
        Assert.Contains(task.RequiredArtifacts, a =>
            a.Type == "file_not_contains" &&
            a.Path == "src/CleanApp.Core/Services/FileService.cs" &&
            a.Text == "AppDbContext");
    }

    [Fact]
    public void NormalizeTask_ShouldAddProgramRegistrationAssertion_WhenProgramRegistrationIsMentioned()
    {
        var task = new CodexTask
        {
            Title = "[Core] 修复依赖注入",
            Description =
                "## 关键任务\n- 创建 src/CleanApp.Core/Interfaces/IFileRepository.cs\n- 创建 src/CleanApp.Infrastructure/Repositories/FileRepository.cs\n- 更新 src/CleanApp/Program.cs → 注册 IFileRepository 和 FileRepository"
        };

        CodexTaskClassifier.NormalizeTask(task);

        Assert.Contains(task.RequiredArtifacts, a =>
            a.Type == "file_contains" &&
            a.Path == "src/CleanApp/Program.cs" &&
            a.Text == "IFileRepository");
    }

    [Fact]
    public void NormalizeTask_ShouldAddGenericInterfaceSignatureAssertion_WhenTaskDeclaresGenericRepository()
    {
        var task = new CodexTask
        {
            Title = "[Core] 引入泛型仓储接口",
            Description =
                "1. 创建 src/CleanApp.Core/Interfaces/IFileRepository.cs → 定义 IFileRepository<TEntity> 抽象接口\n" +
                "2. 创建 src/CleanApp.Infrastructure/Repositories/FileRepository.cs → 实现仓储接口"
        };

        CodexTaskClassifier.NormalizeTask(task);

        Assert.Contains(task.RequiredArtifacts, a =>
            a.Type == "file_contains" &&
            a.Path == "src/CleanApp.Core/Interfaces/IFileRepository.cs" &&
            a.Text == "interface IFileRepository<");
    }

    [Fact]
    public void NormalizeTask_ShouldAddInfrastructureProjectReferenceAssertion_WhenCoreInterfaceAndInfrastructureImplementationAreDeclared()
    {
        var task = new CodexTask
        {
            Title = "[Core] 引入仓储抽象",
            Description =
                "1. 创建 src/CleanApp.Core/Interfaces/IFileRepository.cs\n" +
                "2. 创建 src/CleanApp.Infrastructure/Repositories/FileRepository.cs\n" +
                "3. 更新 src/CleanApp/Program.cs → 注册 IFileRepository 到 FileRepository"
        };

        CodexTaskClassifier.NormalizeTask(task);

        Assert.Contains(task.RequiredArtifacts, a =>
            a.Type == "file_contains" &&
            a.Path == "src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj" &&
            a.Text == "CleanApp.Core");
    }

    [Fact]
    public void NormalizeTask_ShouldNotForceRepositoryAssertion_OnMongoGatewayService()
    {
        var task = new CodexTask
        {
            Title = "[Core] 依赖倒置重构",
            Description =
                "1. 修改 src/CleanApp.Core/Services/MongoFileService.cs 使用新接口\n" +
                "2. 保持 MongoFileService 作为 GridFS 网关服务"
        };
        task.ReplaceRequiredArtifacts([
            new("file_contains", "src/CleanApp.Core/Services/MongoFileService.cs", "IFileRepository")
        ]);

        CodexTaskClassifier.NormalizeTask(task);

        Assert.DoesNotContain(task.RequiredArtifacts, a =>
            a.Path == "src/CleanApp.Core/Services/MongoFileService.cs" &&
            a.Text == "IFileRepository");
    }

    [Fact]
    public void NormalizeTask_ShouldNotEmitSelfProjectCsprojAssertion()
    {
        var task = new CodexTask
        {
            Title = "[Core] 清理项目引用",
            Description = "修改 src/CleanApp.Core/CleanApp.Core.csproj → 移除对 CleanApp.Infrastructure 的 ProjectReference"
        };

        CodexTaskClassifier.NormalizeTask(task);

        Assert.DoesNotContain(task.RequiredArtifacts, a =>
            a.Path == "src/CleanApp.Core/CleanApp.Core.csproj" &&
            a.Text == "CleanApp.Core");
        Assert.Contains(task.RequiredArtifacts, a =>
            a.Path == "src/CleanApp.Core/CleanApp.Core.csproj" &&
            a.Type == "file_not_contains" &&
            a.Text == "CleanApp.Infrastructure");
    }

    [Fact]
    public void GetContractLintWarnings_ShouldWarnOnCircularCoreInfrastructureContract()
    {
        var task = new CodexTask
        {
            Title = "[Core] 架构依赖倒置修复",
            Description = "修复 Core 与 Infrastructure 的依赖方向。"
        };
        task.ReplaceRequiredArtifacts([
            new("file_contains", "src/CleanApp.Core/CleanApp.Core.csproj", "CleanApp.Infrastructure"),
            new("file_contains", "src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj", "CleanApp.Core")
        ]);

        var warnings = CodexTaskClassifier.GetContractLintWarnings(task);

        Assert.Contains(warnings, warning => warning.Contains("循环依赖", StringComparison.Ordinal));
    }

    [Fact]
    public void EvaluateExecutionSpecConformance_ShouldReportGenericInterfaceAndMissingInfrastructureReference()
    {
        using var tempDir = new TempDir();
        Directory.CreateDirectory(Path.Combine(tempDir.Path, "src", "CleanApp.Core", "Interfaces"));
        Directory.CreateDirectory(Path.Combine(tempDir.Path, "src", "CleanApp.Infrastructure"));

        File.WriteAllText(
            Path.Combine(tempDir.Path, "src", "CleanApp.Core", "Interfaces", "IFileRepository.cs"),
            "namespace CleanApp.Core.Interfaces; public interface IFileRepository { }");
        File.WriteAllText(
            Path.Combine(tempDir.Path, "src", "CleanApp.Infrastructure", "CleanApp.Infrastructure.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup></ItemGroup></Project>");

        var task = new CodexTask
        {
            Title = "[Core] 引入泛型仓储接口",
            Description =
                "1. 创建 src/CleanApp.Core/Interfaces/IFileRepository.cs → 定义 IFileRepository<TEntity> 抽象接口\n" +
                "2. 创建 src/CleanApp.Infrastructure/Repositories/FileRepository.cs\n" +
                "3. 修改 src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj → 添加 src/CleanApp.Core/CleanApp.Core.csproj 的 ProjectReference"
        };

        CodexTaskClassifier.NormalizeTask(task);
        var issues = CodexTaskClassifier.EvaluateExecutionSpecConformance(tempDir.Path, task);

        Assert.Contains(issues, issue => issue.Contains("IFileRepository", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Contains("CleanApp.Core", StringComparison.Ordinal));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bug002-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }
}
