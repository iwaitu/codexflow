// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Moq;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using CodexFlow.Core.Planning.Committee;
using CodexFlow.Core.Services;
using CodexFlow.Core.TDD;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodexFlow.Core.Tests;

/// <summary>
/// Phase 0 & Phase 1 委员会规划测试。
/// </summary>
public class CommitteePhase0And1Tests
{
    #region Phase 0: 骨架与配置

    [Fact]
    public void CommitteePlanningOptions_ParsesOffMode()
    {
        var options = new CommitteePlanningOptions { CommitteeModeEnabled = "Off" };
        Assert.Equal(CommitteeMode.Off, options.GetMode());
    }

    [Fact]
    public void CommitteePlanningOptions_ParsesOnMode()
    {
        var options = new CommitteePlanningOptions { CommitteeModeEnabled = "On" };
        Assert.Equal(CommitteeMode.On, options.GetMode());
    }

    [Fact]
    public void CommitteePlanningOptions_ParsesShadowMode()
    {
        var options = new CommitteePlanningOptions { CommitteeModeEnabled = "Shadow" };
        Assert.Equal(CommitteeMode.Shadow, options.GetMode());
    }

    [Fact]
    public void CommitteePlanningOptions_DefaultsToOff()
    {
        var options = new CommitteePlanningOptions { CommitteeModeEnabled = "invalid" };
        Assert.Equal(CommitteeMode.Off, options.GetMode());
    }

    [Fact]
    public void CommitteePlanningOptions_HasDefaultRoleBindings()
    {
        var options = new CommitteePlanningOptions();
        Assert.Equal(3, options.CommitteeRoleBindings.Count);
        Assert.Equal("VllmCommitteeAnalyst", options.CommitteeRoleBindings["Analyst"]);
        Assert.Equal("VllmCommitteeArchitect", options.CommitteeRoleBindings["Architect"]);
        Assert.Equal("VllmCommitteeProjectManager", options.CommitteeRoleBindings["ProjectManager"]);
    }

    [Fact]
    public void ReviewerFeedback_IsApproved_WhenStatusApproveAndNoBlockingItems()
    {
        var feedback = new ReviewerFeedback
        {
            Status = "approve",
            BlockingItems = []
        };
        Assert.True(feedback.IsApproved);
    }

    [Fact]
    public void ReviewerFeedback_IsNotApproved_WhenHasBlockingItems()
    {
        var feedback = new ReviewerFeedback
        {
            Status = "approve",
            BlockingItems = ["some issue"]
        };
        Assert.False(feedback.IsApproved);
    }

    [Fact]
    public void ModeratorDecision_IsFinalize_WhenDecisionFinalize()
    {
        var decision = new ModeratorDecision { Decision = "finalize" };
        Assert.True(decision.IsFinalize);
    }

    [Fact]
    public void ModeratorDecision_IsNotFinalize_WhenDecisionContinue()
    {
        var decision = new ModeratorDecision { Decision = "continue" };
        Assert.False(decision.IsFinalize);
    }

    #endregion

    #region Phase 1: 复杂度快速判定

    [Theory]
    [InlineData("修复拼写错误", false)]
    [InlineData("fix typo in readme", false)]
    [InlineData("修改注释", false)]
    [InlineData("rename variable", false)]
    public void QuickClassify_SimpleGoals_ReturnsFalse(string goal, bool expectedComplex)
    {
        var result = DefaultComplexityClassifier.QuickClassify(goal);
        Assert.NotNull(result);
        Assert.Equal(expectedComplex, result.IsComplex);
    }

    [Theory]
    [InlineData("架构设计一个新的认证系统")]
    [InlineData("需要进行蓝图评审")]
    [InlineData("实现数据库迁移方案")]
    [InlineData("设计跨项目的通知链路")]
    [InlineData("设计一套新的状态机")]
    public void QuickClassify_ComplexGoals_ReturnsTrue(string goal)
    {
        var result = DefaultComplexityClassifier.QuickClassify(goal);
        Assert.NotNull(result);
        Assert.True(result.IsComplex);
    }

    [Theory]
    [InlineData("实现用户列表页面的分页功能")]
    [InlineData("优化 API 响应速度")]
    public void QuickClassify_AmbiguousGoals_ReturnsNull(string goal)
    {
        var result = DefaultComplexityClassifier.QuickClassify(goal);
        Assert.Null(result);
    }

    [Fact]
    public void QuickClassify_EmptyGoal_ReturnsFalse()
    {
        var result = DefaultComplexityClassifier.QuickClassify("");
        Assert.NotNull(result);
        Assert.False(result.IsComplex);
    }

    #endregion

    #region Phase 1: 委员会模式 Off 不触发

    [Fact]
    public async Task Orchestrator_CommitteeModeOff_DoesNotTriggerClassifier()
    {
        // Arrange: CommitteeModeEnabled = Off
        var (orchestrator, mockClassifier, session) = CreateOrchestratorWithCommittee("Off");

        // Simulate Stage 1 → Stage 2 transition by having empty plan
        session.CurrentStage = 3; // Already past planning
        session.Plan.Add(new CodexTask { Id = "t1", Title = "Task1", Status = CodexTaskStatus.Success });

        // Act
        var result = await orchestrator.RunNextStepAsync(
            session.Id, "修复拼写错误", "user1", session.WorkspacePath, null, (Uri?)null, null);

        // Assert: classifier never called
        mockClassifier.Verify(
            c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    #endregion

    #region Phase 1: 复杂需求触发确认

    [Fact]
    public async Task Orchestrator_ComplexGoal_TriggersConfirmation()
    {
        // Arrange: CommitteeModeEnabled = On, classifier returns complex
        var (orchestrator, mockClassifier, session) = CreateOrchestratorWithCommittee("On");

        mockClassifier.Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComplexityClassification
            {
                IsComplex = true,
                Reason = "跨项目改动",
                TriggeredIndicators = ["cross-project"]
            });

        session.CurrentStage = 1;

        // Act
        var result = await orchestrator.RunNextStepAsync(
            session.Id, "设计跨项目的通知链路", "user1", session.WorkspacePath, null, (Uri?)null, null);

        // Assert: confirmation pending
        Assert.True(result.Success);
        Assert.Contains("复杂需求", result.Message);
        Assert.True(session.Metadata.ContainsKey(CommitteeConstants.CommitteeConfirmationPendingKey));
    }

    [Fact]
    public async Task Orchestrator_SimpleGoal_DoesNotTriggerConfirmation()
    {
        // Arrange: CommitteeModeEnabled = On, classifier returns simple
        var (orchestrator, mockClassifier, session) = CreateOrchestratorWithCommittee("On");

        mockClassifier.Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComplexityClassification
            {
                IsComplex = false,
                Reason = "简单需求",
                TriggeredIndicators = []
            });

        // Start at Stage 2 with a plan already generated (post-analysis)
        // to avoid Stage 1 side effects. Committee interception only happens at Stage 1 → 2 transition.
        session.CurrentStage = 1;

        // Act
        var result = await orchestrator.RunNextStepAsync(
            session.Id, "修复一个小 bug", "user1", session.WorkspacePath, null, (Uri?)null, null);

        // Assert: classifier was called but returned simple, so no confirmation
        mockClassifier.Verify(
            c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.False(session.Metadata.ContainsKey(CommitteeConstants.CommitteeConfirmationPendingKey));
    }

    #endregion

    #region Phase 1: 用户确认/拒绝

    [Fact]
    public async Task Orchestrator_UserAcceptsCommittee_ProceedsToPlanning()
    {
        // Arrange: session already has pending confirmation
        var (orchestrator, _, session) = CreateOrchestratorWithCommittee("On");

        session.CurrentStage = 2;
        session.Metadata[CommitteeConstants.CommitteeConfirmationPendingKey] = "true";
        session.Metadata["OriginalGoal"] = "设计跨项目通知系统";

        // Act: user replies "同意"
        var result = await orchestrator.RunNextStepAsync(
            session.Id, "同意", "user1", session.WorkspacePath, null, (Uri?)null, null);

        // Assert: confirmation cleared, result accepted
        Assert.True(result.Success);
        Assert.False(session.Metadata.ContainsKey(CommitteeConstants.CommitteeConfirmationPendingKey));
        Assert.Equal("accepted", session.Metadata[CommitteeConstants.CommitteeConfirmationResultKey]);
    }

    [Fact]
    public async Task Orchestrator_UserRejectsCommittee_FallsBackToRegularPlanning()
    {
        // Arrange: session already has pending confirmation
        var (orchestrator, _, session) = CreateOrchestratorWithCommittee("On");

        session.CurrentStage = 2;
        session.Metadata[CommitteeConstants.CommitteeConfirmationPendingKey] = "true";
        session.Metadata["OriginalGoal"] = "设计跨项目通知系统";

        // Act: user replies "不同意"
        var result = await orchestrator.RunNextStepAsync(
            session.Id, "不需要", "user1", session.WorkspacePath, null, (Uri?)null, null);

        // Assert: confirmation cleared, result rejected
        Assert.True(result.Success);
        Assert.False(session.Metadata.ContainsKey(CommitteeConstants.CommitteeConfirmationPendingKey));
        Assert.Equal("rejected", session.Metadata[CommitteeConstants.CommitteeConfirmationResultKey]);
    }

    [Theory]
    [InlineData("不同意")]
    [InlineData("不要启动")]
    [InlineData("别开始")]
    [InlineData("不需要委员会")]
    [InlineData("不好，跳过")]
    [InlineData("no thanks")]
    [InlineData("disagree")]
    public async Task Orchestrator_RejectKeywordsOverrideAcceptSubstrings(string userReply)
    {
        var (orchestrator, _, session) = CreateOrchestratorWithCommittee("On");

        session.CurrentStage = 2;
        session.Metadata[CommitteeConstants.CommitteeConfirmationPendingKey] = "true";
        session.Metadata["OriginalGoal"] = "设计跨项目通知系统";

        var result = await orchestrator.RunNextStepAsync(
            session.Id, userReply, "user1", session.WorkspacePath, null, (Uri?)null, null);

        Assert.True(result.Success);
        Assert.Equal("rejected", session.Metadata[CommitteeConstants.CommitteeConfirmationResultKey]);
    }

    [Fact]
    public async Task Orchestrator_UserRejectsCommittee_DoesNotCreateMeetingDir()
    {
        var (orchestrator, _, session) = CreateOrchestratorWithCommittee("On");

        session.CurrentStage = 2;
        session.Metadata[CommitteeConstants.CommitteeConfirmationPendingKey] = "true";
        session.Metadata["OriginalGoal"] = "设计跨项目通知系统";

        await orchestrator.RunNextStepAsync(
            session.Id, "不需要委员会", "user1", session.WorkspacePath, null, (Uri?)null, null);

        // Assert: no meeting directory created
        var logDir = Path.Combine(session.WorkspacePath, ".architect-committee-logs");
        Assert.False(Directory.Exists(logDir));
    }

    #endregion

    #region Helpers

    private static (CodexOrchestrator orchestrator, Mock<IComplexityClassifier> mockClassifier, CodexSession session)
        CreateOrchestratorWithCommittee(string committeeMode)
    {
        var tempWorkspace = TestConstants.GetWorkspacePath();
        var session = new CodexSession
        {
            Id = $"committee-test-{Guid.NewGuid():N}",
            CurrentStage = 1,
            WorkspacePath = tempWorkspace,
            ProjectSummary = "测试项目摘要"
        };

        var mockStore = new Mock<ISessionStore>();
        mockStore.Setup(s => s.GetSessionAsync(session.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);

        var mockMemory = new Mock<IMemoryService>();
        var mockKernel = new Mock<ICodexAgentKernel>();
        var mockArchitect = new Mock<ICodexArchitect>();
        var mockClassifier = new Mock<IComplexityClassifier>();

        // Mock architect to return analysis and plan
        mockArchitect.Setup(a => a.AnalyzeAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("项目分析摘要");
        mockArchitect.Setup(a => a.PlanAsync(It.IsAny<CodexSession>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CodexTask>
            {
                new() { Id = "t1", Title = "任务1", Status = CodexTaskStatus.Pending }
            });

        var mockScanner = new Mock<ProjectScanner>(NullLogger<ProjectScanner>.Instance);
        mockScanner.Setup(s => s.ScanAndSummarizeAsync(It.IsAny<string>()))
            .ReturnsAsync("scan report");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Planning:CommitteeModeEnabled"] = committeeMode,
                ["Planning:CommitteeMaxRounds"] = "5",
                ["Planning:CommitteeLogRoot"] = ".architect-committee-logs",
                ["Git:AutoPush"] = "false"
            })
            .Build();

        var orchestrator = new CodexOrchestrator(
            new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance),
            mockKernel.Object,
            mockArchitect.Object,
            Mock.Of<ICodexSecurityAuditor>(),
            Mock.Of<ICodexValidator>(),
            Mock.Of<IGitService>(),
            mockScanner.Object,
            Mock.Of<ICodeAnalysisService>(),
            Mock.Of<IArchitectureService>(),
            Mock.Of<ICodexTestDesigner>(),
            Mock.Of<IPolicyValidator>(),
            Mock.Of<ISemanticDiffService>(),
            Mock.Of<ITaskListService>(),
            projectMemoryService: null,
            complexityClassifier: mockClassifier.Object,
            committeePlanningService: null,
            committeeClientFactory: null,
            configuration,
            NullLogger<CodexOrchestrator>.Instance,
            Enumerable.Empty<ICodexEventSink>());

        return (orchestrator, mockClassifier, session);
    }

    #endregion
}
