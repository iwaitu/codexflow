// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using Xunit;

namespace CodexFlow.Core.Tests.Agents;

/// <summary>
/// The validator demanded a successful build/test run whenever the task text contained "构建", "编译" or "测试" anywhere, so "不修改测试文件",
/// "无需构建" and read-only surveys that mention a baseline run were failed for evidence they were never meant to produce.
/// </summary>
public sealed class ValidatorRequiredEvidenceTests
{
    private static CodexTask Task(string title, string description, string? type = "code") =>
        new() { Id = "T", Title = title, Description = description, TaskType = type! };

    [Theory]
    [InlineData("[Fix] 修复列表过滤", "修改 FileService.cs，然后执行 dotnet build 验证编译通过。")]
    [InlineData("[Fix] 修复列表过滤", "修改后构建整个解决方案。")]
    [InlineData("[Fix] 修复列表过滤", "修改后编译并确认无错误。")]
    [InlineData("[Deps] 升级", "升级版本并执行 dotnet test，确认测试通过。")]
    [InlineData("[Test] 新增回归测试", "新增测试并运行，确认通过。")]
    [InlineData("[Fix] 修复", "不仅要修复缺陷，还要补测试并运行。")]          // "不仅" is not a negation
    public void ATaskThatAsksForABuildOrTest_StillRequiresTheEvidence(string title, string description)
    {
        var task = Task(title, description);
        Assert.True(DefaultCodexValidator.RequiresBuildEvidence(task) || DefaultCodexValidator.RequiresTestEvidence(task));
    }

    [Theory]
    [InlineData("[Fix] 修复列表过滤", "只改 FileService.cs，不修改测试文件，不需要构建验证。")]
    [InlineData("[Fix] 修复", "无需运行测试，也不涉及构建配置。")]
    [InlineData("[Fix] 修复", "不要改动测试代码，不跑测试。")]
    [InlineData("[Fix] 修复", "只修改 README，构建和测试除外。")]
    [InlineData("[Fix] 修复", "Do not run the tests and no need to build.")]
    public void ATaskThatRulesOutBuildingAndTesting_DoesNotRequireThem(string title, string description)
    {
        var task = Task(title, description);
        Assert.False(DefaultCodexValidator.RequiresBuildEvidence(task));
        Assert.False(DefaultCodexValidator.RequiresTestEvidence(task));
    }

    [Fact]
    public void AReadOnlySurvey_NeverRequiresBuildOrTestEvidence()
    {
        // The real survey task from a live run: it talks about recording a restore/build/test baseline but changes nothing.
        var task = Task("[Analysis] 调研依赖图并确认受影响项目与基线",
            "仅做只读调研，不修改任何文件。记录改动前 restore、build、test 基线；执行构建并测试现状。", type: "code");

        Assert.False(DefaultCodexValidator.RequiresBuildEvidence(task));
        Assert.False(DefaultCodexValidator.RequiresTestEvidence(task));
    }

    [Fact]
    public void ADeclaredAnalysisTask_NeverRequiresThem() =>
        Assert.False(DefaultCodexValidator.RequiresBuildEvidence(Task("梳理", "阅读代码并构建依赖图，运行测试查看现状", type: "analysis")));
}

/// <summary>
/// The validator carries two checks written for one demo project (no `Ulid.NewUlid()` in AppFile.cs; FileService.cs must not `new AppFile`). They were
/// switched on by the mere words "ulid" / "工厂"+"fileservice", so a task about that same project that only talks about ULIDs got them applied.
/// </summary>
public sealed class ValidatorProjectSpecificChecksTests
{
    [Theory]
    [InlineData("[Refactor] 移除 Ulid 依赖", "AppFile 改用 Guid，去掉对 Ulid 的引用。", true)]
    [InlineData("[Refactor] remove the Ulid package", "Replace Ulid with Guid in AppFile.", true)]
    [InlineData("[Fix] AppFile.Id 属性遮蔽 BaseEntity.Id", "BaseEntity.Id 是 ULID，AppFile 又定义了一个 Id，修复遮蔽。", false)]
    [InlineData("[Fix] 修复", "保留 Ulid，不移除 Ulid 依赖。", false)]
    [InlineData("[Fix] 修复", "与 ulid 无关。", false)]
    public void TheUlidCheck_OnlyAppliesToATaskThatAsksToRemoveIt(string title, string description, bool expected) =>
        Assert.Equal(expected, DefaultCodexValidator.RequiresUlidRemovalCheck($"{title}\n{description}"));

    [Theory]
    [InlineData("[Refactor] FileService 改用工厂方法创建 AppFile", "不再直接 new AppFile，使用工厂。", true)]
    [InlineData("[Refactor] 通过工厂创建实体", "FileService 使用 AppFile 工厂方法。", true)]
    [InlineData("[Fix] 修复 FileService 列表过滤", "提到工厂模式只是背景说明，不涉及工厂改造。", false)]
    [InlineData("[Fix] 修复列表", "FileService.ListAsync 增加 IsDeleted 过滤。", false)]
    public void TheFactoryCheck_OnlyAppliesToATaskThatAsksForTheFactory(string title, string description, bool expected) =>
        Assert.Equal(expected, DefaultCodexValidator.RequiresFactoryCheck($"{title}\n{description}"));
}
