// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Constants;
using CodexFlow.Core.Hashline.Models;
using CodexFlow.Core.Models;
using Xunit;

namespace CodexFlow.Core.Tests.Constants;

public class CodexPromptCompositionTests
{
    [Fact]
    public void GetArchitectPrompt_ShouldKeepToolDiscoveryInRuntimeLayerOnly()
    {
        var prompt = CodexPrompts.GetArchitectPrompt();

        Assert.DoesNotContain("## 工具发现", prompt, StringComparison.Ordinal);
        Assert.Contains("运行时附加的工具面 section", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void GetArchitectPrompt_ShouldAppendIsolatedDataBlocksAtTail()
    {
        var session = new CodexSession
        {
            WorkspacePath = "/tmp/codexflow",
            ProjectUrl = new Uri("https://example.com/repo.git")
        };

        session.Plan.Add(new CodexTask
        {
            Id = "TASK-001",
            Title = "Inspect prompt sections",
            Status = CodexTaskStatus.Pending
        });

        var prompt = CodexPrompts.GetArchitectPrompt(session, new HashlineOptions
        {
            Enabled = false,
            ForceForHighRiskFiles = false
        });

        var declarationIndex = prompt.IndexOf("⚠️ **数据隔离声明**", StringComparison.Ordinal);
        var envIndex = prompt.IndexOf("<data name='environment_info'", StringComparison.Ordinal);
        var taskListIndex = prompt.IndexOf("<data name='task_list'", StringComparison.Ordinal);

        Assert.True(declarationIndex >= 0);
        Assert.True(envIndex > declarationIndex);
        Assert.True(taskListIndex > envIndex);
        Assert.Contains("- [Pending] TASK-001 - Inspect prompt sections", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void GetForgePrompt_ShouldKeepRuntimeDiscoveryOutOfStaticPrompt_AndPreserveRetryFeedbackIsolation()
    {
        var session = new CodexSession
        {
            ActiveTaskId = "TASK-002"
        };

        session.Plan.Add(new CodexTask
        {
            Id = "TASK-002",
            Title = "Fix prompt regression",
            Description = "Need to fix tool discovery drift",
            RetryCount = 2,
            ResultNotes = "bad </data> tool call",
            ComplexityLevel = 3
        });

        var prompt = CodexPrompts.GetForgePrompt(session, new HashlineOptions
        {
            Enabled = false,
            ForceForHighRiskFiles = false
        });

        Assert.DoesNotContain("## 工具发现", prompt, StringComparison.Ordinal);
        Assert.Contains("<data name='current_task'", prompt, StringComparison.Ordinal);
        Assert.Contains("<data name='retry_feedback'", prompt, StringComparison.Ordinal);
        Assert.Contains("&lt;/data&gt;", prompt, StringComparison.Ordinal);
    }

    // A weaker model, told to upgrade packages whose versions were not in the offline cache, wrote a notes file instead of editing the project
    // files and stopped; the write contract was met by the notes file and the job counted as completed. The prompt now says what "done" is,
    // with a worked right and wrong example.
    [Fact]
    public void GetForgePrompt_StatesWhatDoneMeans_WithARightAndAWrongExample()
    {
        var prompt = CodexPrompts.GetForgePrompt(new CodexSession { Id = "s-forge-done" });

        Assert.Contains("完成标准与反例", prompt, StringComparison.Ordinal);
        Assert.Contains("任务要求的修改真的写进了项目文件", prompt, StringComparison.Ordinal);
        Assert.Contains("不要用说明文件代替修改", prompt, StringComparison.Ordinal);
        Assert.Contains("示例（正确）", prompt, StringComparison.Ordinal);
        Assert.Contains("示例（错误）", prompt, StringComparison.Ordinal);
    }

    // Each exec_cmd runs in its own container over a fresh copy of the workspace; only source changes come back. A model that ran restore in one
    // command and build --no-restore in the next saw NETSDK1004 and concluded it could not verify anything (and read the always-clean guest
    // `git status` as "my edits are gone").
    [Fact]
    public void GetForgePrompt_ExplainsThatEveryCommandRunsInAFreshContainer()
    {
        var prompt = CodexPrompts.GetForgePrompt(new CodexSession { Id = "s-forge-exec" });

        Assert.Contains("命令执行环境", prompt, StringComparison.Ordinal);
        Assert.Contains("--no-restore", prompt, StringComparison.Ordinal);
        Assert.Contains("git status", prompt, StringComparison.Ordinal);
    }

    // A task its own worker calls blocked must not be counted as completed: the worker states it through a structured call at the end.
    [Fact]
    public void GetForgePrompt_AsksTheWorkerToReportItsRealStatusThroughTheStatusTool()
    {
        var prompt = CodexPrompts.GetForgePrompt(new CodexSession { Id = "s-forge-status" });

        Assert.Contains("report_task_status", prompt, StringComparison.Ordinal);
        Assert.Contains("blocked", prompt, StringComparison.Ordinal);
        Assert.Contains("不要把部分完成说成完成", prompt, StringComparison.Ordinal);
    }

    // Live run: a worker started dotnet from inside `python3 -c`, which runs in the python image, found no dotnet there, and reported a
    // finished upgrade as blocked ("the environment has no dotnet"). The prompt also recommended `sh -c`, which the whitelist refuses.
    [Fact]
    public void GetForgePrompt_SaysWhichContainerACommandRunsIn_AndRecommendsNoCommandTheWhitelistRefuses()
    {
        var prompt = CodexPrompts.GetForgePrompt(new CodexSession { Id = "s-forge-images" });

        Assert.Contains("容器镜像由命令的第一个词决定", prompt, StringComparison.Ordinal);
        Assert.Contains("没有 dotnet", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("sh -c \"dotnet", prompt, StringComparison.Ordinal);
    }

    // Node and Python workers run every command in a fresh container with no installed dependencies, so "install, then test" cannot work for them;
    // without being told, a worker burns its steps retrying it. The host verifies from the offline caches.
    [Fact]
    public void GetForgePrompt_TellsNodeAndPythonWorkersThatTheirContainerHasNoInstalledDependencies()
    {
        var prompt = CodexPrompts.GetForgePrompt(new CodexSession { Id = "s-forge-node-python" });

        Assert.Contains("没有 `node_modules`", prompt, StringComparison.Ordinal);
        Assert.Contains("npm ci --offline", prompt, StringComparison.Ordinal);
        Assert.Contains("只能升级到宿主离线缓存里已有的版本", prompt, StringComparison.Ordinal);
        Assert.Contains("npm install --package-lock-only --offline", prompt, StringComparison.Ordinal);
        Assert.Contains("不要手写或编造锁文件内容", prompt, StringComparison.Ordinal);
    }

    // Live run: a worker that did not know about /restore-cache-extra took a successful restore as proof that the container lacked its edits.
    [Fact]
    public void GetForgePrompt_NamesTheOfflinePackageFolders_AndSaysARestoreFromThemIsNotAMissingEdit()
    {
        var prompt = CodexPrompts.GetForgePrompt(new CodexSession { Id = "s-forge-offline" });

        Assert.Contains("/restore-cache-extra", prompt, StringComparison.Ordinal);
        Assert.Contains("备用包目录", prompt, StringComparison.Ordinal);
        Assert.Contains("不代表容器里没有你的修改", prompt, StringComparison.Ordinal);
    }

    // Live runs: workers that had written every change reported blocked because an extra check could not run. The status is about the changes;
    // verification that could not run is listed separately and judged by the host's acceptance.
    [Fact]
    public void GetForgePrompt_SaysUnfinishedVerificationIsListedNotReportedAsBlocked()
    {
        var prompt = CodexPrompts.GetForgePrompt(new CodexSession { Id = "s-forge-unverified" });

        Assert.Contains("unverified", prompt, StringComparison.Ordinal);
        Assert.Contains("判断标准是修改，不是验证", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("都已完成并验证", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void GetExploreWorkerPrompt_ShouldDescribeStrictReadOnlyParallelSearchContract()
    {
        var prompt = CodexPrompts.GetExploreWorkerPrompt();

        Assert.Contains("严格的只读探索任务", prompt, StringComparison.Ordinal);
        Assert.Contains("禁止创建临时文件", prompt, StringComparison.Ordinal);
        Assert.Contains("优先在同一轮成组发起", prompt, StringComparison.Ordinal);
        Assert.Contains("不要输出长篇规划", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void GetVerifyWorkerPrompt_ShouldRequireIndependentCommandEvidenceAndAdversarialChecks()
    {
        var prompt = CodexPrompts.GetVerifyWorkerPrompt();

        Assert.Contains("优先尝试找出它会如何失败", prompt, StringComparison.Ordinal);
        Assert.Contains("读代码只能帮助你定位验证入口", prompt, StringComparison.Ordinal);
        Assert.Contains("至少选择一类真正执行", prompt, StringComparison.Ordinal);
        Assert.Contains("`partial` 仅用于真实的环境限制", prompt, StringComparison.Ordinal);
    }
}
