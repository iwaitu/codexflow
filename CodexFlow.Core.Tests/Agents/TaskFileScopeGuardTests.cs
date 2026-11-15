// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Agents;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Xunit;

namespace CodexFlow.Core.Tests.Agents;

public class TaskFileScopeGuardTests
{
    // The real TASK_FIX_001 description of cleanapp-bug live run 12. Its scope list contained AppDbContext.cs, UnitOfWork.cs and
    // Redis/Program.cs although the task forbids touching them, plus paths that do not exist (e.g. src/CleanApp.Infrastructure/AppFile.cs).
    private const string ForbiddenFilesTaskDescription = """
        ## 阶段目标
        修复软删除读取路径失效缺陷：让 FileService.ListAsync 的查询跳过 IsDeleted == true 的 AppFile，并保证 Total 与列表使用同一过滤集合。

        ## 阶段范围
        仅修改 src/CleanApp.Core/Services/FileService.cs 中 ListAsync 的查询链。先读取 FileService.cs、UnitOfWork.cs、AppDbContext.cs、AppFile.cs、BaseEntity.cs 确认现有结构与过滤位置，然后追加 !x.IsDeleted 条件。禁止修改 AppDbContext.cs（禁止添加全局 HasQueryFilter）、禁止修改 UnitOfWork.cs、禁止改动 IFileService 签名与分层引用、禁止处理 Redis/Program.cs/AppFile 遮蔽 Id/Contracts 等无关技术债；其余读路径（如 FindByIdAsync）不在本次范围。

        ## 任务执行顺序
        1. 读取 src/CleanApp.Core/Services/FileService.cs、src/CleanApp.Infrastructure/UnitOfWork.cs、src/CleanApp.Infrastructure/AppDbContext.cs → 确认过滤缺失位置与查询链结构
        2. 在 ListAsync 查询链起始处追加 !x.IsDeleted 条件，位于 Where/Count/Skip/Take 之前 → 产出 filtered query
        3. 执行 dotnet build → 编译通过无新增错误
        """;

    [Fact]
    public void BuildTaskFileScope_FilesTheTaskForbidsOrOnlyReadsAreNotWritable()
    {
        var scope = TaskFileScopeGuard.BuildTaskFileScope(new CodexTask
        {
            Id = "TASK_FIX_001",
            Title = "[Core] 在 FileService.ListAsync 读路径过滤软删除记录",
            Description = ForbiddenFilesTaskDescription
        });

        Assert.Contains("src/CleanApp.Core/Services/FileService.cs", scope.AllowedFiles);
        Assert.DoesNotContain(scope.AllowedFiles, f => f.EndsWith("AppDbContext.cs", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(scope.AllowedFiles, f => f.EndsWith("UnitOfWork.cs", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(scope.AllowedFiles, f => f.EndsWith("Program.cs", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(scope.AllowedFiles, f => f.EndsWith("AppFile.cs", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("AppDbContext.cs", scope.AllowedFileNames);
        Assert.DoesNotContain("UnitOfWork.cs", scope.AllowedFileNames);
        Assert.DoesNotContain("IFileService.cs", scope.AllowedFileNames);
        Assert.DoesNotContain("AppFile.cs", scope.AllowedFileNames);
        Assert.DoesNotContain("Redis", scope.AllowedDirectories);
        Assert.DoesNotContain("src/CleanApp.Infrastructure", scope.AllowedDirectories);

        Assert.True(TaskFileScopeGuard.IsPathInScope("src/CleanApp.Core/Services/FileService.cs", scope));
        Assert.False(TaskFileScopeGuard.IsPathInScope("src/CleanApp.Infrastructure/AppDbContext.cs", scope));
        Assert.False(TaskFileScopeGuard.IsPathInScope("src/CleanApp.Infrastructure/UnitOfWork.cs", scope));
        Assert.False(TaskFileScopeGuard.IsPathInScope("Redis/Program.cs", scope));
    }

    // The real TASK_FIX_001 description of cleanapp-bug live run 18. "(仅读不改)" belongs to the item before it; the item after "、" is
    // another read ("读取 FileService.cs ..."). The first version of the clause rule let the negation run to the end of the clause, put the
    // very file the task exists to change on the forbidden list, and the worker could not write it (11 writes refused).
    private const string ReadOnlyParentheticalTaskDescription = """
        ## 阶段目标
        在 `FileService.ListAsync` 的查询链起始处附加 `!x.IsDeleted` 过滤条件，使分页 Items 与 Total 计数同时排除软删记录（最小改动）。

        ## 阶段范围
        仅修改 `src/CleanApp.Core/Services/FileService.cs` 中 `ListAsync` 一个方法的查询表达式。先读取 `src/CleanApp.Domain/BaseEntity.cs` 确认 `IsDeleted` 属性语义、读取 `src/CleanApp.Infrastructure/UnitOfWork.cs` 确认 `RemoveEntity` 只做软删（仅读不改）、读取 `src/CleanApp.Core/Services/FileService.cs` 现有 `ListAsync` 结构，然后在构建 `IQueryable` 的最早阶段附加过滤。
        不做：不新增仓储方法/扩展方法/抽象层；不改 `UnitOfWork`、`IUnitOfWork`、`IRepository` 签名；不加 EF Core 全局查询过滤器（不碰 `AppDbContext`）；不新建迁移；不动 Controller。

        ## 关键任务
        - 读取 `BaseEntity.cs`、`UnitOfWork.cs`、`FileService.cs` 确认现状与语义
        - 在 `ListAsync` 查询表达式起始处添加 `!x.IsDeleted` 过滤
        """;

    [Fact]
    public void BuildTaskFileScope_ANegationInsideAReadItem_DoesNotForbidTheNextItemThatIsTheFileToChange()
    {
        var scope = TaskFileScopeGuard.BuildTaskFileScope(new CodexTask
        {
            Id = "TASK_FIX_001",
            Title = "[Core] FileService.ListAsync 排除已软删文件记录",
            Description = ReadOnlyParentheticalTaskDescription
        });

        Assert.Contains("src/CleanApp.Core/Services/FileService.cs", scope.AllowedFiles);
        Assert.True(TaskFileScopeGuard.IsPathInScope("src/CleanApp.Core/Services/FileService.cs", scope));
        // what the task only reads or forbids stays unwritable
        foreach (var path in new[] { "src/CleanApp.Infrastructure/UnitOfWork.cs", "src/CleanApp.Domain/BaseEntity.cs", "src/CleanApp.Infrastructure/AppDbContext.cs" })
        {
            Assert.False(TaskFileScopeGuard.IsPathInScope(path, scope),
                $"{path} must stay unwritable. files=[{string.Join(", ", scope.AllowedFiles)}] names=[{string.Join(", ", scope.AllowedFileNames)}] dirs=[{string.Join(", ", scope.AllowedDirectories)}]");
        }
    }

    [Theory]
    [InlineData("读取 src/A/Bar.cs（仅读不改）、src/A/Baz.cs，然后仅修改 src/A/Foo.cs。")]
    [InlineData("不修改 src/A/Bar.cs、src/A/Baz.cs，只修改 src/A/Foo.cs。")]
    [InlineData("禁止修改 src/A/Bar.cs、禁止修改 src/A/Baz.cs，修改 src/A/Foo.cs。")]
    public void BuildTaskFileScope_AnEnumerationKeepsItsPolarity_UntilAnItemSetsItsOwn(string description)
    {
        var scope = TaskFileScopeGuard.BuildTaskFileScope(new CodexTask { Id = "T", Title = "t", Description = description });

        Assert.Contains("src/A/Foo.cs", scope.AllowedFiles);
        Assert.DoesNotContain("src/A/Bar.cs", scope.AllowedFiles);
        Assert.DoesNotContain("src/A/Baz.cs", scope.AllowedFiles);   // the later items inherit the cue of the item before them
    }

    [Theory]
    [InlineData("不做")]
    [InlineData("不加")]
    [InlineData("不碰")]
    [InlineData("不动")]
    [InlineData("不新增")]
    [InlineData("不新建")]
    [InlineData("不引入")]
    [InlineData("不更改")]
    [InlineData("不触碰")]
    public void BuildTaskFileScope_TheCommonChineseProhibitionsAreRecognised(string cue)
    {
        var scope = TaskFileScopeGuard.BuildTaskFileScope(new CodexTask
        {
            Id = "T", Title = "t", Description = $"仅修改 src/A/Foo.cs，{cue} src/A/Bar.cs 与 `BazContext`。"
        });

        Assert.Contains("src/A/Foo.cs", scope.AllowedFiles);
        Assert.DoesNotContain("src/A/Bar.cs", scope.AllowedFiles);
        Assert.DoesNotContain("Bar.cs", scope.AllowedFileNames);
        Assert.DoesNotContain("BazContext.cs", scope.AllowedFileNames);
    }

    [Fact]
    public void BuildTaskFileScope_AnItemWithItsOwnWriteVerbEndsAnInheritedNegationOrReadOnly()
    {
        var scope = TaskFileScopeGuard.BuildTaskFileScope(new CodexTask
        {
            Id = "T", Title = "t", Description = "禁止修改 src/A/Bar.cs、修改 src/A/Foo.cs。"
        });

        Assert.Contains("src/A/Foo.cs", scope.AllowedFiles);
        Assert.DoesNotContain("src/A/Bar.cs", scope.AllowedFiles);
    }

    [Fact]
    public void BuildTaskFileScope_ForbiddenMentionOverridesAnEarlierPositiveMentionOfTheSameFile()
    {
        var scope = TaskFileScopeGuard.BuildTaskFileScope(new CodexTask
        {
            Id = "T",
            Title = "修改 src/A/Foo.cs",
            Description = "修改 src/A/Foo.cs 和 src/A/Bar.cs；禁止修改 src/A/Bar.cs。"
        });

        Assert.Contains("src/A/Foo.cs", scope.AllowedFiles);
        Assert.DoesNotContain("src/A/Bar.cs", scope.AllowedFiles);
    }

    [Theory]
    [InlineData("不修改 src/A/Bar.cs，只修改 src/A/Foo.cs。")]
    [InlineData("Do not touch src/A/Bar.cs, only edit src/A/Foo.cs.")]
    [InlineData("Read src/A/Bar.cs first, then update src/A/Foo.cs.")]
    public void BuildTaskFileScope_AFileAfterTheNegationOrReadClauseStaysWritable(string description)
    {
        var scope = TaskFileScopeGuard.BuildTaskFileScope(new CodexTask { Id = "T", Title = "t", Description = description });

        Assert.Contains("src/A/Foo.cs", scope.AllowedFiles);
        Assert.DoesNotContain("src/A/Bar.cs", scope.AllowedFiles);
    }

    [Fact]
    public void BuildTaskFileScope_AReadVerbWithAWriteVerbInTheSameClauseStillGrantsTheFile()
    {
        var scope = TaskFileScopeGuard.BuildTaskFileScope(new CodexTask
        {
            Id = "T", Title = "t", Description = "读取并修改 src/A/Foo.cs 的 ListAsync。"
        });

        Assert.Contains("src/A/Foo.cs", scope.AllowedFiles);
    }

    [Fact]
    public void BuildTaskFileScope_AllowsBareFilenameMentionInsideSeedDirectory()
    {
        var task = new CodexTask
        {
            Id = "TASK_ARCH_001",
            Title = "[Core] 上移 IUnitOfWork 接口并修复依赖方向",
            Description = """
                ## 关键任务
                - 创建 src/CleanApp.Core/Interfaces/IUnitOfWork.cs 新接口文件
                - 删除 src/CleanApp.Infrastructure/IUnitOfWork.cs 旧接口定义
                - 修改 src/CleanApp.Core/CleanApp.Core.csproj 移除错误项目引用
                - 更新 src/CleanApp/Program.cs 修复依赖注入注册

                ## 任务执行顺序
                1. 读取 src/CleanApp.Infrastructure/IUnitOfWork.cs → 确认接口签名和命名空间
                2. 创建 src/CleanApp.Core/Interfaces/IUnitOfWork.cs
                3. 更新 UnitOfWork.cs 实现 → 修改命名空间引用至 Core.Interfaces
                4. 删除 src/CleanApp.Infrastructure/IUnitOfWork.cs
                """,
        };

        var scope = TaskFileScopeGuard.BuildTaskFileScope(task);

        Assert.Contains("src/CleanApp.Infrastructure/UnitOfWork.cs", scope.AllowedFiles);

        var violations = TaskFileScopeGuard.GetOutOfScopeChanges(
        [
            new GitFileChange("src/CleanApp.Infrastructure/UnitOfWork.cs", "M"),
        ],
        scope);

        Assert.DoesNotContain("src/CleanApp.Infrastructure/UnitOfWork.cs", violations);
    }

    [Fact]
    public void GetOutOfScopeChanges_StillRejectsUnmentionedExistingFileInAllowedDirectory()
    {
        var task = new CodexTask
        {
            Id = "TASK_ARCH_001",
            Title = "[Core] 上移 IUnitOfWork 接口并修复依赖方向",
            Description = """
                - 创建 src/CleanApp.Core/Interfaces/IUnitOfWork.cs 新接口文件
                - 删除 src/CleanApp.Infrastructure/IUnitOfWork.cs 旧接口定义
                - 修改 src/CleanApp.Core/CleanApp.Core.csproj 移除错误项目引用
                - 更新 src/CleanApp/Program.cs 修复依赖注入注册
                """,
        };

        var scope = TaskFileScopeGuard.BuildTaskFileScope(task);

        var violations = TaskFileScopeGuard.GetOutOfScopeChanges(
        [
            new GitFileChange("src/CleanApp.Infrastructure/RepositoryFactory.cs", "M"),
        ],
        scope);

        Assert.Contains("src/CleanApp.Infrastructure/RepositoryFactory.cs", violations);
    }

    [Fact]
    public void BuildTaskFileScope_AllowsPascalCaseTypeNameMentionAsFileName()
    {
        var task = new CodexTask
        {
            Id = "TASK_FIX_001",
            Title = "[核心] 修复 AppFile Id 隐藏与 FileService 软删除一致性",
            Description = """
                - 修改 src/CleanApp.Domain/AppFile.cs，避免隐藏 BaseEntity.Id
                - 保持 FileService 软删除逻辑一致
                """,
        };

        var scope = TaskFileScopeGuard.BuildTaskFileScope(task);

        Assert.Contains("FileService.cs", scope.AllowedFileNames);

        var violations = TaskFileScopeGuard.GetOutOfScopeChanges(
        [
            new GitFileChange("src/CleanApp.Core/Services/FileService.cs", "M"),
        ],
        scope);

        Assert.DoesNotContain("src/CleanApp.Core/Services/FileService.cs", violations);
    }

    [Fact]
    public void BuildTaskFileScope_DoesNotOpenProjectAndStartupFilesForGenericArchitectureText()
    {
        var task = new CodexTask
        {
            Id = "TASK_DOMAIN_001",
            Title = "[领域] 修复 AppFile 领域模型并保持架构边界",
            Description = """
                - 修改 src/CleanApp.Domain/AppFile.cs，避免隐藏 BaseEntity.Id
                - 保持 FileService 软删除逻辑一致
                - 不处理项目引用或依赖注入注册
                """,
        };

        var scope = TaskFileScopeGuard.BuildTaskFileScope(task);

        Assert.False(scope.AllowProjectFileEdits);
        Assert.False(scope.AllowDiRegistrationFiles);

        var violations = TaskFileScopeGuard.GetOutOfScopeChanges(
        [
            new GitFileChange("src/CleanApp/Program.cs", "M"),
            new GitFileChange("src/CleanApp.Core/CleanApp.Core.csproj", "M"),
        ],
        scope);

        Assert.Contains("src/CleanApp/Program.cs", violations);
        Assert.Contains("src/CleanApp.Core/CleanApp.Core.csproj", violations);
    }

    [Fact]
    public void BuildTaskFileScope_AllowsOnlyExplicitProjectFileMention()
    {
        var task = new CodexTask
        {
            Id = "TASK_REF_001",
            Title = "[Core] 修复项目引用",
            Description = """
                - 修改 src/CleanApp.Core/CleanApp.Core.csproj 移除错误项目引用
                - 不修改其他项目文件
                """,
        };

        var scope = TaskFileScopeGuard.BuildTaskFileScope(task);

        var violations = TaskFileScopeGuard.GetOutOfScopeChanges(
        [
            new GitFileChange("src/CleanApp.Core/CleanApp.Core.csproj", "M"),
            new GitFileChange("src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj", "M"),
        ],
        scope);

        Assert.DoesNotContain("src/CleanApp.Core/CleanApp.Core.csproj", violations);
        Assert.Contains("src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj", violations);
    }

    [Fact]
    public async Task TaskScopeInterventionHook_BlocksOutOfScopeJsonElementPath()
    {
        var scope = new TaskFileScopeDescriptor();
        scope.AllowedFiles.Add("src/CleanApp.Domain/BaseEntity.cs");

        using var document = JsonDocument.Parse("""{"path":"src/CleanApp.Core/Services/FileService.cs"}""");
        var hook = new TaskScopeInterventionHook(scope, NullLogger.Instance);

        var intervention = await hook.OnToolCallRequestedAsync(
            "edit_file",
            new Dictionary<string, object?>
            {
                ["path"] = document.RootElement.GetProperty("path")
            },
            session: null,
            ct: CancellationToken.None);

        Assert.True(intervention.ShouldBlock);
        Assert.Contains("FileService.cs", intervention.Reason);
    }

    [Fact]
    public async Task TaskScopeInterventionHook_BlocksOutOfScopeFilePathArgument()
    {
        var scope = new TaskFileScopeDescriptor();
        scope.AllowedFiles.Add("src/CleanApp.Domain/BaseEntity.cs");
        scope.AllowedFiles.Add("src/CleanApp.Domain/AppFile.cs");

        var hook = new TaskScopeInterventionHook(scope, NullLogger.Instance);

        var intervention = await hook.OnToolCallRequestedAsync(
            "hs_write",
            new Dictionary<string, object?>
            {
                ["filePath"] = "src/CleanApp.ServiceDefaults/CleanApp.ServiceDefaults.csproj"
            },
            session: null,
            ct: CancellationToken.None);

        Assert.True(intervention.ShouldBlock);
        Assert.Contains("CleanApp.ServiceDefaults.csproj", intervention.Reason);
    }

    [Fact]
    public async Task TaskScopeInterventionHook_BlocksMigrationCommandWhenMigrationsAreOutOfScope()
    {
        var scope = new TaskFileScopeDescriptor();
        scope.AllowedFiles.Add("src/CleanApp.Domain/BaseEntity.cs");
        scope.AllowedFiles.Add("src/CleanApp.Domain/AppFile.cs");

        var hook = new TaskScopeInterventionHook(scope, NullLogger.Instance);

        var intervention = await hook.OnToolCallRequestedAsync(
            "run_command",
            new Dictionary<string, object?>
            {
                ["command"] = new[] { "dotnet", "ef", "migrations", "add", "FixAppFileId" }
            },
            session: null,
            ct: CancellationToken.None);

        Assert.True(intervention.ShouldBlock);
        Assert.Contains("migration", intervention.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TaskScopeInterventionHook_AllowsValidationCommandInConstrainedScope()
    {
        var scope = new TaskFileScopeDescriptor();
        scope.AllowedFiles.Add("src/CleanApp.Domain/BaseEntity.cs");
        scope.AllowedFiles.Add("src/CleanApp.Domain/AppFile.cs");

        var hook = new TaskScopeInterventionHook(scope, NullLogger.Instance);

        var intervention = await hook.OnToolCallRequestedAsync(
            "run_command",
            new Dictionary<string, object?>
            {
                ["command"] = "dotnet test src/CleanApp.Tests/CleanApp.Tests.csproj"
            },
            session: null,
            ct: CancellationToken.None);

        Assert.False(intervention.ShouldBlock);
    }

    [Fact]
    public async Task TaskScopeInterventionHook_BlocksProjectReferenceCommandWhenProjectFilesAreOutOfScope()
    {
        var scope = new TaskFileScopeDescriptor();
        scope.AllowedFiles.Add("src/CleanApp.Domain/BaseEntity.cs");
        scope.AllowedFiles.Add("src/CleanApp.Domain/AppFile.cs");

        var hook = new TaskScopeInterventionHook(scope, NullLogger.Instance);

        var intervention = await hook.OnToolCallRequestedAsync(
            "run_command",
            new Dictionary<string, object?>
            {
                ["command"] = "dotnet add src/CleanApp.Core/CleanApp.Core.csproj package AutoMapper"
            },
            session: null,
            ct: CancellationToken.None);

        Assert.True(intervention.ShouldBlock);
        Assert.Contains("Project/package/reference", intervention.Reason);
    }

    [Fact]
    public async Task TaskScopeInterventionHook_BlocksOutOfScopeApplyPatchTarget()
    {
        var scope = new TaskFileScopeDescriptor();
        scope.AllowedFiles.Add("src/CleanApp.Domain/BaseEntity.cs");
        scope.AllowedFiles.Add("src/CleanApp.Domain/AppFile.cs");

        var hook = new TaskScopeInterventionHook(scope, NullLogger.Instance);

        var intervention = await hook.OnToolCallRequestedAsync(
            "apply_patch",
            new Dictionary<string, object?>
            {
                ["patch"] = """
                    *** Begin Patch
                    *** Update File: src/CleanApp.Domain/BaseEntity.cs
                    @@
                    -    public string Id { get; set; } = string.Empty;
                    +    public Guid Id { get; set; }
                    *** Update File: src/CleanApp.Core/CleanApp.Core.csproj
                    @@
                    -  <PropertyGroup>
                    +  <PropertyGroup>
                    *** End Patch
                    """
            },
            session: null,
            ct: CancellationToken.None);

        Assert.True(intervention.ShouldBlock);
        Assert.Contains("CleanApp.Core.csproj", intervention.Reason);
    }

    [Fact]
    public async Task TaskScopeInterventionHook_AllowsInScopeApplyPatchTargets()
    {
        var scope = new TaskFileScopeDescriptor();
        scope.AllowedFiles.Add("src/CleanApp.Domain/BaseEntity.cs");
        scope.AllowedFiles.Add("src/CleanApp.Domain/AppFile.cs");

        var hook = new TaskScopeInterventionHook(scope, NullLogger.Instance);

        var intervention = await hook.OnToolCallRequestedAsync(
            "apply_patch",
            new Dictionary<string, object?>
            {
                ["patch"] = """
                    *** Begin Patch
                    *** Update File: src/CleanApp.Domain/BaseEntity.cs
                    @@
                    -    public string Id { get; set; } = string.Empty;
                    +    public Guid Id { get; set; }
                    *** Update File: src/CleanApp.Domain/AppFile.cs
                    @@
                    -    public string Id { get; set; } = string.Empty;
                    *** End Patch
                    """
            },
            session: null,
            ct: CancellationToken.None);

        Assert.False(intervention.ShouldBlock);
    }
    // Live developer simulation (dependency-upgrade-node): the task whose only output is package-lock.json could not get the regenerated
    // lockfile back from `npm install --package-lock-only`; the export called it out of scope.
    [Fact]
    public void BuildTaskFileScope_TheFilesATaskDeclaresAsOutputs_AreWritable_AlsoInTheRepositoryRoot()
    {
        var task = new CodexTask
        {
            Id = "T2", Title = "重新生成 package-lock.json 同步 minimist 1.2.6",
            Description = "仅生成 package-lock.json；不修改其他文件，不手写锁文件。"
        };
        foreach (var output in (string[])["package-lock.json", "requirements.txt", "src/app/pager.py", "更新后的锁文件", "../outside.json", "/etc/passwd.txt", "*.json"]) task.Outputs.Add(output);

        var scope = TaskFileScopeGuard.BuildTaskFileScope(task);

        Assert.Contains("package-lock.json", scope.DeclaredOutputFiles);
        Assert.Contains("requirements.txt", scope.DeclaredOutputFiles);
        Assert.Contains("src/app/pager.py", scope.DeclaredOutputFiles);
        Assert.DoesNotContain(scope.DeclaredOutputFiles, static file => file.Contains("outside", StringComparison.Ordinal) || file.Contains("passwd", StringComparison.Ordinal) || file.Contains('*') || file.Contains("锁文件", StringComparison.Ordinal));
        Assert.True(CodexFlow.Core.Execution.TrustedExecutionContextFactory.ToExportScope(scope).Allows("package-lock.json"));
    }

    [Fact]
    public void BuildTaskFileScope_AnOutputTheTaskForbidsTouching_StaysForbidden()
    {
        var task = new CodexTask { Id = "T1", Title = "升级 minimist", Description = "修改 package.json。禁止修改 package-lock.json。" };
        foreach (var output in (string[])["package.json", "package-lock.json"]) task.Outputs.Add(output);

        var scope = TaskFileScopeGuard.BuildTaskFileScope(task);

        Assert.Contains("package.json", scope.DeclaredOutputFiles);
        Assert.DoesNotContain("package-lock.json", scope.DeclaredOutputFiles);
        Assert.False(TaskFileScopeGuard.IsPathInScope("package-lock.json", scope) && scope.HasConstraints);
    }
    // Live developer simulation (dependency-upgrade-node, third run): the plan said "禁止手动编辑 package-lock.json" - because the host's own
    // briefing says the lockfile must be regenerated by npm, never hand-written - and that removed the lockfile from the task's scope.
    [Theory]
    [InlineData("严格限定为修改 minimist 依赖版本并触发锁文件同步。禁止手动编辑 package-lock.json。")]
    [InlineData("更新 package.json，不要手写 package-lock.json，由 npm 重新生成。")]
    [InlineData("Update package.json. Do not manually edit package-lock.json; let npm regenerate it.")]
    public void BuildTaskFileScope_ForbiddingToHandEditAFile_DoesNotForbidTheFile(string description)
    {
        var task = new CodexTask { Id = "TASK_002", Title = "升级 minimist 至 1.2.6", Description = description };
        task.Outputs.Add("package.json");
        task.Outputs.Add("package-lock.json");

        var scope = TaskFileScopeGuard.BuildTaskFileScope(task);

        Assert.Contains("package.json", scope.DeclaredOutputFiles);
        Assert.Contains("package-lock.json", scope.DeclaredOutputFiles);
    }

    // Live developer simulation (dependency-upgrade-node, platform host): the briefing said "禁止修改 package.json 或 package-lock.json 中的其他
    // 依赖版本" - a limit on WHAT in the files may change (other dependencies' versions), not a ban on the files - and both files, the
    // task's declared outputs, were forbidden: the host refused npm's regenerated lockfile and the task ended blocked.
    [Theory]
    [InlineData("将 minimist 升级到 1.2.6 并同步锁文件。禁止修改 package.json 或 package-lock.json 中的其他依赖版本。")]
    [InlineData("升级 minimist，不得改动 package-lock.json 里的其他依赖。")]
    [InlineData("Upgrade minimist to 1.2.6. Do not change other dependency versions in package.json or package-lock.json.")]
    public void BuildTaskFileScope_AProhibitionOnOtherContentOfAFile_DoesNotForbidTheFile(string description)
    {
        var task = new CodexTask { Id = "TASK_002", Title = "升级 minimist 至 1.2.6", Description = description };
        task.Outputs.Add("package.json");
        task.Outputs.Add("package-lock.json");

        var scope = TaskFileScopeGuard.BuildTaskFileScope(task);

        Assert.Contains("package.json", scope.DeclaredOutputFiles);
        Assert.Contains("package-lock.json", scope.DeclaredOutputFiles);
        Assert.True(CodexFlow.Core.Execution.TrustedExecutionContextFactory.ToExportScope(scope).Allows("package-lock.json"));
    }

    // ── the manifests the host's own scan names ───────────────────────────────────────────────────────────────
    //
    // Live developer simulation (dependency-upgrade-compliant, after the six fixes): the plan declared a dependency task's outputs as
    // "CleanApp/CleanApp.csproj", "OtherProject1/*.csproj" and "OtherProject2/*.csproj" - the model had not read the repository - and the files
    // the task really had to change (CleanApp.Core.csproj, the test project) were refused as out of scope: "worker_reported_blocked".
    // The host's scan knows which projects a package reaches. What a dependency task may write is that fact, not the model's guess.

    private static readonly (string Package, IReadOnlyList<string> Projects)[] Scan =
    [
        ("Snappier", ["src/CleanApp.Core/CleanApp.Core.csproj", "test/CleanAppCoreTests/CleanAppCoreTests.csproj"]),
        ("OpenTelemetry.Api", ["src/CleanApp.ServiceDefaults/CleanApp.ServiceDefaults.csproj"])
    ];

    private static CodexTask DependencyTask(string description, params string[] outputs)
    {
        var task = new CodexTask { Id = "T4", Title = "固定 Snappier", Description = description };
        foreach (var output in outputs) task.Outputs.Add(output);
        return task;
    }

    [Fact]
    public void AnUpgradeTaskMayWriteTheManifestsOfTheProjectsTheHostScanSaysTheNamedPackageReaches()
    {
        var task = DependencyTask("在 src/CleanApp/CleanApp.csproj 添加 Snappier 1.3.1 的直接引用。", "CleanApp/CleanApp.csproj", "OtherProject1/*.csproj");
        var scope = TaskFileScopeGuard.BuildTaskFileScope(task);

        var added = TaskFileScopeGuard.AddHostKnownManifests(scope, task, Scan);

        var export = CodexFlow.Core.Execution.TrustedExecutionContextFactory.ToExportScope(scope);
        Assert.Equal(2, added);
        Assert.True(export.Allows("src/CleanApp.Core/CleanApp.Core.csproj"));
        Assert.True(export.Allows("test/CleanAppCoreTests/CleanAppCoreTests.csproj"));
        Assert.False(export.Allows("src/CleanApp.Core/Program.cs"));              // a manifest, not the project
        Assert.False(export.Allows("src/CleanApp.ServiceDefaults/CleanApp.ServiceDefaults.csproj"));   // another package's project
    }

    // Live developer simulation (dependency-upgrade-retry-after-unmet, run 9): the last code task's first attempt left three packages below the
    // host's version, in three projects, and the host queued a retry that NAMED those packages and projects. The task's own scope was its one
    // csproj, the packages are not in its text, so the host's scope guard refused every edit the host's feedback asked for ("Task scope violation
    // ... outside allowed scope") and the retry could not finish. What the retry must change is what the host's own check found unmet: a fact the
    // host holds, granted on the same terms as the host's scan.
    [Fact]
    public void ManifestsTheHostNamesForARetry_AreWritable_WithoutTheTaskTextNamingTheirPackages()
    {
        var task = DependencyTask("只改 README。", "README.md");
        task.Title = "更新文档";
        var scope = TaskFileScopeGuard.BuildTaskFileScope(task);

        var added = TaskFileScopeGuard.AddHostGrantedManifests(scope, ["src/CleanApp/CleanApp.csproj", "src/CleanApp.ServiceDefaults/CleanApp.ServiceDefaults.csproj"]);

        var export = CodexFlow.Core.Execution.TrustedExecutionContextFactory.ToExportScope(scope);
        Assert.Equal(2, added);
        Assert.True(export.Allows("src/CleanApp/CleanApp.csproj"));
        Assert.True(export.Allows("src/CleanApp.ServiceDefaults/CleanApp.ServiceDefaults.csproj"));
        Assert.False(export.Allows("src/CleanApp/Program.cs"));                    // a manifest, not the project
        Assert.False(export.Allows("src/CleanApp.Core/CleanApp.Core.csproj"));      // a project the host did not name
    }

    [Fact]
    public void AGrantedManifest_NeverClimbsOut_IsNeverAbsolute_AndAForbiddenFileStillWins()
    {
        var task = DependencyTask("只改 README。", "README.md");
        task.Title = "更新文档";
        task.ForbiddenPaths = ["src/CleanApp.AppHost/CleanApp.AppHost.csproj"];
        var scope = TaskFileScopeGuard.BuildTaskFileScope(task);

        var added = TaskFileScopeGuard.AddHostGrantedManifests(scope,
            ["../outside/x.csproj", "/etc/x.csproj", "C:/x/x.csproj", "src/./x/x.csproj", "", "src/CleanApp.AppHost/CleanApp.AppHost.csproj"]);

        var export = CodexFlow.Core.Execution.TrustedExecutionContextFactory.ToExportScope(scope);
        Assert.Equal(0, added);
        Assert.False(export.Allows("src/CleanApp.AppHost/CleanApp.AppHost.csproj"));
        Assert.False(export.Allows("src/x/x.csproj"));
    }

    [Fact]
    public void APackageTheTaskDoesNotMention_AddsNothing()
    {
        var task = DependencyTask("只改 README。", "README.md");
        task.Title = "更新文档";   // the helper's title names a package
        var scope = TaskFileScopeGuard.BuildTaskFileScope(task);

        Assert.Equal(0, TaskFileScopeGuard.AddHostKnownManifests(scope, task, Scan));
    }

    [Fact]
    public void ALongerPackageIdThatStartsWithTheName_IsNotTheNamedPackage()
    {
        var task = DependencyTask("固定 OpenTelemetry.Api.ProviderBuilderExtensions 的版本。");
        task.Title = "固定版本";
        var scope = TaskFileScopeGuard.BuildTaskFileScope(task);

        Assert.Equal(0, TaskFileScopeGuard.AddHostKnownManifests(scope, task, Scan));
        var sentenceEnd = DependencyTask("固定 OpenTelemetry.Api。");
        sentenceEnd.Title = "固定版本";
        Assert.Equal(1, TaskFileScopeGuard.AddHostKnownManifests(scope, sentenceEnd, Scan));   // a sentence end is not a longer id
    }

    // Live developer simulation (dependency-upgrade-compliant, x5 run 1, T3): the plan grouped three packages into a task it called
    // "CleanApp.Infrastructure", declared ONLY that project's csproj as its outputs, and - given a ForbiddenPaths field - filled it with every
    // other project directory (AppHost, Core, CoreTests, CleanApp ...). The host's scan says MessagePack and Snappier live in exactly those
    // projects, so the worker was told to change them and forbidden to: "worker_reported_blocked". The plan's author guessed where packages
    // live; the host knows. A directory the plan forbids does not take away a manifest the host's scan names for a package the task names;
    // a FILE the plan forbids stays forbidden.
    private static readonly string[] LiveForbiddenDirectories =
    [
        "src/CleanApp.AppHost/", "src/CleanApp.Contracts/", "src/CleanApp.Core/", "src/CleanApp.Domain/", "src/CleanApp.ServiceDefaults/",
        "test/CleanAppCoreTests/", "src/CleanApp/"
    ];

    private const string InfrastructureTaskText =
        "## 阶段目标\n核对 8 个 csproj 中 MessagePack, Snappier, SQLitePCLRaw.lib.e_sqlite3 的引用情况，并升级至目标版本。\n\n" +
        "## 阶段范围\n仅修改 src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj 等文件中存在的显式版本声明。不修改 SharpCompress，不新增包引用，不修改其他文件。\n\n" +
        "## 涉及模块或服务\n- CleanApp.Infrastructure\n\n## 影响范围\n- 代码目录：src/CleanApp.Infrastructure/\n";

    private static readonly (string Package, IReadOnlyList<string> Projects)[] LiveScan =
    [
        ("MessagePack", ["src/CleanApp.AppHost/CleanApp.AppHost.csproj", "src/CleanApp/CleanApp.csproj"]),
        ("Snappier", ["src/CleanApp.Core/CleanApp.Core.csproj", "src/CleanApp/CleanApp.csproj", "test/CleanAppCoreTests/CleanAppCoreTests.csproj"]),
        ("SQLitePCLRaw.lib.e_sqlite3", ["src/CleanApp.Core/CleanApp.Core.csproj", "src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj"])
    ];

    [Fact]
    public void ADirectoryForbiddenOnlyByTheLayerGuess_DoesNotTakeAwayAManifestTheHostScanNamesForANamedPackage()
    {
        var task = new CodexTask { Id = "T3", Title = "核对并升级序列化与本地库相关包版本", Description = InfrastructureTaskText, ForbiddenPaths = [.. LiveForbiddenDirectories] };
        task.Outputs.Add("src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj");
        var scope = TaskFileScopeGuard.BuildTaskFileScope(task);

        TaskFileScopeGuard.AddHostKnownManifests(scope, task, LiveScan);

        var export = CodexFlow.Core.Execution.TrustedExecutionContextFactory.ToExportScope(scope);
        Assert.True(export.Allows("src/CleanApp.AppHost/CleanApp.AppHost.csproj"));
        Assert.True(export.Allows("src/CleanApp/CleanApp.csproj"));
        Assert.True(export.Allows("src/CleanApp.Core/CleanApp.Core.csproj"));
        Assert.True(export.Allows("test/CleanAppCoreTests/CleanAppCoreTests.csproj"));
        Assert.True(export.Allows("src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj"));
        // only the named manifests: nothing else in those projects, and no project the scan does not name
        Assert.False(export.Allows("src/CleanApp.Core/Program.cs"));
        Assert.False(export.Allows("src/CleanApp.ServiceDefaults/CleanApp.ServiceDefaults.csproj"));
    }

    [Fact]
    public void ACarvedOutManifest_OpensNothingElseInTheForbiddenDirectory_AndAForbiddenFileStillWins()
    {
        var task = new CodexTask
        {
            Id = "T3", Title = "核对并升级序列化与本地库相关包版本", Description = InfrastructureTaskText,
            ForbiddenPaths = [.. LiveForbiddenDirectories, "src/CleanApp.AppHost/CleanApp.AppHost.csproj"]
        };
        task.Outputs.Add("src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj");
        var scope = TaskFileScopeGuard.BuildTaskFileScope(task);

        TaskFileScopeGuard.AddHostKnownManifests(scope, task, LiveScan);

        var export = CodexFlow.Core.Execution.TrustedExecutionContextFactory.ToExportScope(scope);
        Assert.True(export.Allows("src/CleanApp.Core/CleanApp.Core.csproj"));          // a directory forbade it; the host's scan names it
        Assert.False(export.Allows("src/CleanApp.Core/Services/Anything.cs"));         // the rest of the directory stays closed
        Assert.False(export.Allows("src/CleanApp.AppHost/CleanApp.AppHost.csproj"));   // a file forbidden by name stays forbidden
    }

    [Fact]
    public void AForbiddenManifest_StaysForbidden_EvenWhenTheHostScanNamesIt()
    {
        var task = DependencyTask("给 Snappier 加直接引用。", "src/CleanApp/CleanApp.csproj");
        task.ForbiddenPaths = ["test/CleanAppCoreTests/CleanAppCoreTests.csproj"];
        var scope = TaskFileScopeGuard.BuildTaskFileScope(task);

        TaskFileScopeGuard.AddHostKnownManifests(scope, task, Scan);

        var export = CodexFlow.Core.Execution.TrustedExecutionContextFactory.ToExportScope(scope);
        Assert.True(export.Allows("src/CleanApp.Core/CleanApp.Core.csproj"));
        Assert.False(export.Allows("test/CleanAppCoreTests/CleanAppCoreTests.csproj"));
    }

    [Fact]
    public void APathThatClimbsOutOrIsAbsolute_IsNeverAddedFromTheScan()
    {
        var task = DependencyTask("给 Snappier 加直接引用。");
        var scope = TaskFileScopeGuard.BuildTaskFileScope(task);
        (string Package, IReadOnlyList<string> Projects)[] hostile = [("Snappier", ["../outside/Evil.csproj", "/etc/x.csproj", "C:/x/Evil.csproj", "src/A/A.csproj"])];

        var added = TaskFileScopeGuard.AddHostKnownManifests(scope, task, hostile);

        Assert.Equal(1, added);
        Assert.Contains("src/A/A.csproj", scope.DeclaredOutputFiles);
    }

    // ── a plan that states what may not be touched, as data ───────────────────────────────────────────────────
    //
    // Six live failures in this guard came from reading write permission out of Chinese prose with regular expressions. The plan now carries
    // ForbiddenPaths: when it is there (even empty) it is the authority on what is forbidden, and the prose no longer takes files away.

    private const string MisreadableProhibition = "将 minimist 升级到 1.2.6。禁止改动 package-lock.json 的 integrity 字段，也不要改动 node_modules。";

    private static CodexTask TaskWith(string description, string[]? forbidden, params string[] outputs)
    {
        var task = new CodexTask { Id = "T1", Title = "升级 minimist", Description = description, ForbiddenPaths = forbidden?.ToList() };
        foreach (var output in outputs) task.Outputs.Add(output);
        return task;
    }

    [Fact]
    public void ADeclaredForbiddenList_IsTheAuthority_TheProseNoLongerTakesADeclaredOutputAway()
    {
        var scope = TaskFileScopeGuard.BuildTaskFileScope(TaskWith(MisreadableProhibition, forbidden: [], "package.json", "package-lock.json"));

        Assert.Contains("package.json", scope.DeclaredOutputFiles);
        Assert.Contains("package-lock.json", scope.DeclaredOutputFiles);
        Assert.True(CodexFlow.Core.Execution.TrustedExecutionContextFactory.ToExportScope(scope).Allows("package-lock.json"));
    }

    [Fact]
    public void AForbiddenPath_WinsOverADeclaredOutput_AndOverEveryOtherRule()
    {
        var scope = TaskFileScopeGuard.BuildTaskFileScope(TaskWith("升级 minimist", forbidden: ["package-lock.json"], "package.json", "package-lock.json"));
        var export = CodexFlow.Core.Execution.TrustedExecutionContextFactory.ToExportScope(scope);

        Assert.Contains("package.json", scope.DeclaredOutputFiles);
        Assert.DoesNotContain("package-lock.json", scope.DeclaredOutputFiles);
        Assert.False(export.Allows("package-lock.json"));
        Assert.True(export.Allows("package.json"));
        Assert.False(TaskFileScopeGuard.IsPathInScope("package-lock.json", scope));
    }

    [Fact]
    public void AForbiddenDirectory_ClosesEverythingBelowIt_AndNothingElse()
    {
        // User.cs is even a declared output: the forbidden directory still closes it.
        var scope = TaskFileScopeGuard.BuildTaskFileScope(TaskWith("改 src/App/Service.cs", forbidden: ["src/Domain/", "node_modules"], "src/App/Service.cs", "src/Domain/User.cs"));
        var export = CodexFlow.Core.Execution.TrustedExecutionContextFactory.ToExportScope(scope);

        Assert.False(export.Allows("src/Domain/User.cs"));
        Assert.False(export.Allows("node_modules/x/index.js"));
        Assert.True(export.Allows("src/App/Service.cs"));
        Assert.False(TaskFileScopeGuard.IsPathInScope("src/Domain/User.cs", scope));
    }

    [Fact]
    public void AForbiddenPath_ClosesTheFile_EvenWhenTheProseNamesNoFileAtAll()
    {
        // No allowed file at all (the scope is not narrowed): until now a forbidden file only left the allow lists, and there were none.
        var scope = TaskFileScopeGuard.BuildTaskFileScope(TaskWith("升级依赖", forbidden: ["package-lock.json"]));

        Assert.False(CodexFlow.Core.Execution.TrustedExecutionContextFactory.ToExportScope(scope).Allows("package-lock.json"));
    }

    [Fact]
    public void ForbiddenPaths_AreNormalised_AndAPathThatClimbsOutOrIsAbsoluteIsIgnored()
    {
        var scope = TaskFileScopeGuard.BuildTaskFileScope(TaskWith("x", forbidden: [".\\package-lock.json", "src\\Domain\\", "../outside", "/etc/passwd", "  "], "package.json"));

        Assert.Contains("package-lock.json", scope.DisallowedDirectories);
        Assert.Contains("src/Domain", scope.DisallowedDirectories);
        Assert.DoesNotContain(scope.DisallowedDirectories, static d => d.Contains("outside", StringComparison.Ordinal) || d.Contains("passwd", StringComparison.Ordinal) || d.Trim().Length == 0);
    }

    [Fact]
    public void ADeclaredEmptyList_DoesNotGrantWhatTheProseForbids()
    {
        var scope = TaskFileScopeGuard.BuildTaskFileScope(TaskWith("改 src/App/Service.cs。禁止修改 src/App/Secrets.cs。", forbidden: []));

        Assert.Contains("src/App/Service.cs", scope.AllowedFiles);
        Assert.DoesNotContain("src/App/Secrets.cs", scope.AllowedFiles);   // it was never granted: the clause that names it is a prohibition
    }

    [Fact]
    public void WithoutAForbiddenList_TheProseDecidesAsBefore()
    {
        var scope = TaskFileScopeGuard.BuildTaskFileScope(TaskWith("修改 package.json。禁止修改 package-lock.json。", forbidden: null, "package.json", "package-lock.json"));

        Assert.Contains("package.json", scope.DeclaredOutputFiles);
        Assert.DoesNotContain("package-lock.json", scope.DeclaredOutputFiles);
    }

    // Live developer simulation (bugfix-rounding, full run of 18): the plan declared the test PROJECT DIRECTORY as the task's output.
    // "tests/Rounding.Tests" was read as a file with the extension "Tests", became the scope's only allowed "file", and that switched the
    // scope on: every write to tests/Rounding.Tests/MoneyTests.cs was refused and the worker reported the task blocked.
    [Fact]
    public void BuildTaskFileScope_ADeclaredOutputThatIsAProjectDirectory_IsNotTakenForAFile()
    {
        var task = new CodexTask
        {
            Id = "TASK_001", Title = "新增回归测试以复现 Money.Round 舍入缺陷",
            Description = "## 阶段范围\n仅限修改 tests/Rounding.Tests 下的测试代码，禁止修改生产代码、项目文件或文档。\n\n## 影响范围\n- 代码目录：tests/Rounding.Tests/"
        };
        task.Outputs.Add("tests/Rounding.Tests");

        var scope = TaskFileScopeGuard.BuildTaskFileScope(task);

        Assert.DoesNotContain("tests/Rounding.Tests", scope.DeclaredOutputFiles);
        Assert.False(scope.HasConstraints);   // as before the declared outputs were read: a task that names no file is not narrowed to one
    }

    // Live developer simulation (dependency-upgrade-out-of-scope, full run of 18): the plan guessed the task's output as "WebApi.csproj";
    // the reference really lives in src/CleanApp/CleanApp.csproj. The guessed name became the scope's only allowed file, the scope
    // switched on, and the one file the task had to change was refused. A declared output widens what may be written, never narrows it.
    [Fact]
    public void BuildTaskFileScope_AGuessedOutputName_DoesNotNarrowTheTaskToAFileThatDoesNotExist()
    {
        var task = new CodexTask
        {
            Id = "T3", Title = "升级 SignalR.StackExchangeRedis 至 10.0.15",
            Description = "将 Microsoft.AspNetCore.SignalR.StackExchangeRedis 的 PackageReference 版本改为 10.0.15，运行 dotnet build。"
        };
        task.Outputs.Add("WebApi.csproj");

        var scope = TaskFileScopeGuard.BuildTaskFileScope(task);

        Assert.False(scope.HasConstraints);
        Assert.Contains("WebApi.csproj", scope.DeclaredOutputFiles);
        Assert.True(CodexFlow.Core.Execution.TrustedExecutionContextFactory.ToExportScope(scope).Allows("WebApi.csproj"));
    }

    // Live developer simulation (dependency-upgrade-node, second full run of 18): the plan wrote the outputs as absolute host paths and
    // told the worker what to do if the npm command failed. The absolute paths granted nothing and the contingency sentence forbade both
    // files, so npm's regenerated lockfile was refused at export once more.
    [Fact]
    public void BuildTaskFileScope_AbsoluteOutputsUnderTheProjectRoot_AndAContingencySentence_LeaveTheTaskItsFiles()
    {
        const string root = @"D:\work\_fixtures\node-upgrade-5";
        var task = new CodexTask
        {
            Id = "T1", Title = "升级 minimist 依赖至 1.2.6 并重新生成锁文件",
            Description = "## 任务执行顺序\n1. 修改 " + root + @"\package.json：将 minimist 改为 1.2.6。" +
                          "\n2. 执行 npm install --package-lock-only --offline。\n3. 验证 " + root + @"\package-lock.json：确认 minimist 锁定版本为 1.2.6。" +
                          "如果命令执行失败，如实记录失败原因，不修改 package.json 或 package-lock.json。"
        };
        task.Outputs.Add(root + @"\package.json");
        task.Outputs.Add(root + @"\package-lock.json");
        task.Outputs.Add(@"D:\elsewhere\secrets.json");

        var scope = TaskFileScopeGuard.BuildTaskFileScope(task, root);
        var export = CodexFlow.Core.Execution.TrustedExecutionContextFactory.ToExportScope(scope);

        Assert.True(export.Allows("package.json"));
        Assert.True(export.Allows("package-lock.json"));
        Assert.True(TaskFileScopeGuard.IsPathInScope("package-lock.json", scope));
        Assert.DoesNotContain(scope.DeclaredOutputFiles, static file => file.Contains("secrets", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildTaskFileScope_AProhibitionOutsideAContingency_StillForbids()
    {
        var task = new CodexTask { Id = "T1", Title = "升级 minimist", Description = "如果测试失败，如实记录原因。修改 package.json。不修改 package-lock.json。" };
        task.Outputs.Add("package.json");
        task.Outputs.Add("package-lock.json");

        var scope = TaskFileScopeGuard.BuildTaskFileScope(task);

        Assert.Contains("package.json", scope.DeclaredOutputFiles);
        Assert.DoesNotContain("package-lock.json", scope.DeclaredOutputFiles);
    }

    [Fact]
    public void BuildTaskFileScope_ADeclaredOutput_IsInScope_WhenTheProseAlreadyNarrowedTheTask()
    {
        var task = new CodexTask { Id = "T1", Title = "升级 minimist", Description = "修改 src/app/package.json 中的 minimist 版本并重新生成锁文件。" };
        task.Outputs.Add("package-lock.json");

        var scope = TaskFileScopeGuard.BuildTaskFileScope(task);

        Assert.True(scope.HasConstraints);
        Assert.True(TaskFileScopeGuard.IsPathInScope("package-lock.json", scope));
        Assert.False(TaskFileScopeGuard.IsPathInScope("src/other/index.js", scope));
    }
}
