// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Models;
using Xunit;

namespace CodexFlow.Core.Tests;

/// <summary>
/// Live developer-simulation run: a task titled "[Analysis]" whose description said "仅做只读调研，不修改任何文件" was still classed as a code task
/// (the description also mentions restore/build/test), went to a forge worker, which must produce changes, and failed for having made none.
/// An explicit read-only statement outranks counting code-sounding words.
/// </summary>
public sealed class CodexTaskClassifierReadOnlyTests
{
    // The real task text from that run (shortened only where it listed packages).
    private const string RealTitle = "[Analysis] 调研依赖图并确认受影响项目与基线";
    private const string RealDescription = """
        ## 阶段目标
        基于实际依赖图确认每个有漏洞包在哪些 csproj 中被引入，并记录改动前的 restore/build/test 基线。

        ## 阶段范围
        仅做只读调研，不修改任何文件；不扩大也不缩小需求范围。

        ## 关键任务
        - 枚举仓库内所有 csproj
        - 使用 dotnet list package --include-transitive 确认各包引入项目
        - 记录改动前 restore、build、test 基线（含已有失败用例）

        ## 主要技术改动点
        - 无代码改动，仅产出调研清单与基线
        """;

    [Theory]
    [InlineData(null)]
    [InlineData("code")]       // the planner labelled it code although the text says read-only
    [InlineData("analysis")]
    public void ATaskThatStatesItIsReadOnly_IsAnAnalysisTask_WhateverTheTypeFieldSays(string? declared)
    {
        var task = new CodexTask { Id = "T1", Title = RealTitle, Description = RealDescription, TaskType = declared! };

        Assert.True(CodexTaskClassifier.IsAnalysisTask(task));
        Assert.False(CodexTaskClassifier.IsCodeExecutionTask(task));
    }

    [Theory]
    [InlineData("[分析] 梳理依赖", "本任务只读，不修改任何文件。运行 dotnet build 记录基线。")]
    [InlineData("[Research] check feeds", "Read-only investigation: do not modify any file. Run dotnet test for a baseline.")]
    [InlineData("[勘察] 定位入口", "无代码改动，仅产出清单；需要构建并测试现状。")]
    public void OtherPhrasingsOfTheSameStatement_AreRecognisedToo(string title, string description)
    {
        Assert.True(CodexTaskClassifier.IsAnalysisTask(new CodexTask { Id = "T", Title = title, Description = description, TaskType = "code" }));
    }

    [Theory]
    [InlineData("[Deps] 升级依赖版本", "修改 csproj，把 MessagePack 升到 2.5.303，然后 restore、build、test。")]
    [InlineData("[Analysis] 调研并修复", "调研原因后修改 FileService.cs 修复软删除问题，并补测试。")]    // a tag alone, with real edits requested, does not make it read-only
    [InlineData("[Fix] 修复列表过滤", "不修改任何测试文件，只改 FileService.cs。")]                      // "any TEST file" is a scope limit, not "any file"
    public void RealCodeTasks_StayCodeTasks(string title, string description)
    {
        Assert.True(CodexTaskClassifier.IsCodeExecutionTask(new CodexTask { Id = "T", Title = title, Description = description, TaskType = "code" }));
    }

    // Live developer-simulation run (replayed with `qre replay`): the planner wrote "TaskType":"code" for a dependency upgrade whose description
    // only MENTIONED a read-only step ("具体路径由只读探索确定"). The rule read that as "this task is read-only", the task went to an explore
    // worker, which changed nothing, and was marked completed. Text shortened only where it listed packages.
    private const string RealUpgradeTitle = "限定受影响项目的依赖版本并离线验证";
    private const string RealUpgradeDescription = """
        ## 阶段目标
        在实际受影响项目中完成指定的六个包版本调整，并验证解析、构建和相关测试结果。

        ## 阶段范围
        MessagePack 的传递依赖固定为 2.5.303；SharpCompress 和其他所有包不动，不做系列包对齐，不联网查询。

        ## 主要技术改动点
        - 在受影响项目中添加直接引用或固定传递依赖版本

        ## 影响范围
        - 代码目录：实际受影响项目的依赖声明或仅作用于这些项目的版本配置，具体路径由只读探索确定
        """;

    [Fact]
    public void ACodeTaskThatOnlyMentionsAReadOnlyStep_StaysACodeTask()
    {
        var task = new CodexTask { Id = "TASK_001", Title = RealUpgradeTitle, Description = RealUpgradeDescription, TaskType = "code" };

        Assert.True(CodexTaskClassifier.IsCodeExecutionTask(task));
        Assert.False(CodexTaskClassifier.IsAnalysisTask(task));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("analysis")]
    public void AMentionOfAReadOnlyStep_StillDecides_WhenThePlannerDidNotDeclareACodeTask(string? declared)
    {
        var task = new CodexTask { Id = "T", Title = "只读调研依赖图", Description = "运行 dotnet build 记录基线，并测试现状。", TaskType = declared! };

        Assert.True(CodexTaskClassifier.IsAnalysisTask(task));
    }

    [Fact]
    public void WithoutAnyExplicitStatement_TheOldKeywordRuleStillApplies()
    {
        Assert.True(CodexTaskClassifier.IsAnalysisTask(new CodexTask { Id = "T", Title = "梳理模块依赖", Description = "阅读并盘点各层引用关系", TaskType = null! }));
        Assert.True(CodexTaskClassifier.IsCodeExecutionTask(new CodexTask { Id = "T", Title = "实现分页", Description = "新增分页参数并修改查询", TaskType = null! }));
    }
}
