// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Moq;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using CodexFlow.Core.Runtime;
using CodexFlow.Core.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Xunit;

namespace CodexFlow.Core.Tests.Agents;

public class SessionManagerTests
{
    [Fact]
    public async Task GetOrCreateSessionAsync_ShouldReturnExisting_WhenFound()
    {
        // Arrange
        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();
        var existing = new CodexSession { Id = "s1", ProjectUrl = new Uri("git://test") };
        
        mockStore.Setup(s => s.GetSessionAsync("s1", It.IsAny<CancellationToken>()))
                 .ReturnsAsync(existing);

        var manager = new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance);

        // Act
        var result = await manager.GetOrCreateSessionAsync("s1");

        // Assert
        Assert.Equal(new Uri("git://test"), result.ProjectUrl);
        mockStore.Verify(s => s.SaveSessionAsync(It.IsAny<CodexSession>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetOrCreateSessionAsync_ShouldCreateNew_WithUserIdAndWorkspace_WhenNotFound()
    {
        // Arrange
        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();
        
        mockStore.Setup(s => s.GetSessionAsync("s2", It.IsAny<CancellationToken>()))
                 .ReturnsAsync((CodexSession?)null);

        var manager = new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance);

        // Act
        var result = await manager.GetOrCreateSessionAsync("s2", userId: "u123", workspacePath: "/tmp/ws", projectUrl: "git://new");

        // Assert
        Assert.Equal("u123", result.UserId);
        Assert.Equal("/tmp/ws", result.WorkspacePath);
        Assert.Equal(new Uri("git://new"), result.ProjectUrl);
        mockStore.Verify(s => s.SaveSessionAsync(It.Is<CodexSession>(ss => ss.Id == "s2" && ss.UserId == "u123"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetOrCreateSessionAsync_ShouldRefreshWorkspacePath_WhenExistingSessionHasStalePath()
    {
        // Arrange
        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();
        var existing = new CodexSession
        {
            Id = "s3",
            UserId = "u1",
            WorkspacePath = "/old/ws"
        };

        mockStore.Setup(s => s.GetSessionAsync("s3", It.IsAny<CancellationToken>()))
                 .ReturnsAsync(existing);

        var manager = new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance);

        // Act
        var result = await manager.GetOrCreateSessionAsync("s3", userId: "u1", workspacePath: "/new/ws");

        // Assert
        Assert.Equal("/new/ws", result.WorkspacePath);
        mockStore.Verify(s => s.SaveSessionAsync(
            It.Is<CodexSession>(ss => ss.Id == "s3" && ss.WorkspacePath == "/new/ws"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // --------------------------------------------------------------------------
    // Phase 3: Dual Summary Model
    // --------------------------------------------------------------------------

    [Fact]
    public async Task FlushExecutionSummaryAsync_ShouldWriteLastExecutionOutcomeFact_WhenTasksBuffered()
    {
        // Arrange
        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();

        var session = new CodexSession { Id = "s-flush" };
        session.Metadata["PendingExecutionTasks"] = "task1\ntask2\ntask3\ntask4\ntask5";

        mockStore.Setup(s => s.GetSessionAsync("s-flush", It.IsAny<CancellationToken>())).ReturnsAsync(session);
        mockMemory.Setup(m => m.RecallFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockMemory.Setup(m => m.RecallUserFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockMemory.Setup(m => m.SummarizeExecutionAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("Completed tasks: task1, task2, task3, task4, task5.");
        mockMemory.Setup(m => m.ExtractStructuredFactsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var manager = new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance);

        // Act
        await manager.FlushExecutionSummaryAsync("s-flush", session);

        // Assert — LastExecutionOutcome fact was written
        mockMemory.Verify(m => m.LearnFactAsync(
            "s-flush",
            ProjectMemoryFactKeys.LastExecutionOutcome,
            It.IsAny<string>(),
            MemoryFactCategories.Execution,
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // Assert — session.ExecutionSummary was populated
        Assert.NotEmpty(session.ExecutionSummary);

        // Assert — pending buffer was cleared
        Assert.False(session.Metadata.ContainsKey("PendingExecutionTasks"));
    }

    [Fact]
    public async Task OnStageChangedAsync_ShouldFlushExecutionSummaryAndCompressConversation()
    {
        // Arrange
        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();

        var session = new CodexSession { Id = "s-stage" };
        // Simulate buffered conversation turns
        session.RecentTurns.Add(new ChatTurn("user", "hello", DateTime.UtcNow));
        session.RecentTurns.Add(new ChatTurn("assistant", "world", DateTime.UtcNow));

        mockStore.Setup(s => s.GetSessionAsync("s-stage", It.IsAny<CancellationToken>())).ReturnsAsync(session);
        mockMemory.Setup(m => m.RecallFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockMemory.Setup(m => m.RecallUserFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockMemory.Setup(m => m.SummarizeHistoryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<ChatTurn>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Compressed conversation summary.");
        mockMemory.Setup(m => m.ExtractStructuredFactsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var manager = new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance);

        // Act
        await manager.OnStageChangedAsync("s-stage", newStage: 4);

        // Assert — ConversationSummary was compressed
        mockMemory.Verify(m => m.SummarizeHistoryAsync(
            "s-stage",
            It.IsAny<string>(),
            It.IsAny<IReadOnlyList<ChatTurn>>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // Assert — recent turns cleared after compression
        Assert.Empty(session.RecentTurns);
    }

    [Fact]
    public async Task GetFullContextAsync_ShouldRenderExecutionSummarySection_Independently()
    {
        // Arrange
        var session = new CodexSession
        {
            Id = "s-exec-ctx",
            ProjectSummary = "project",
            HistorySummary = "conv summary",
            ExecutionSummary = "executed 3 tasks successfully"
        };

        var mockStore = new Mock<ISessionStore>();
        mockStore.Setup(s => s.GetSessionAsync("s-exec-ctx", It.IsAny<CancellationToken>())).ReturnsAsync(session);
        var mockMemory = new Mock<IMemoryService>();
        mockMemory.Setup(m => m.RecallFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockMemory.Setup(m => m.RecallUserFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var manager = new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance);

        // Act
        var context = await manager.GetFullContextAsync("s-exec-ctx");

        // Assert — both sections appear independently
        Assert.Contains("# 历史进展总结", context, StringComparison.Ordinal);
        Assert.Contains("conv summary", context, StringComparison.Ordinal);
        Assert.Contains("# 执行进展摘要", context, StringComparison.Ordinal);
        Assert.Contains("executed 3 tasks successfully", context, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordExecutionUpdateAsync_ShouldRenderRecentExecutionUpdatesSection_AndDeduplicate()
    {
        var session = new CodexSession
        {
            Id = "s-exec-updates",
            ProjectSummary = "project"
        };

        var mockStore = new Mock<ISessionStore>();
        mockStore.Setup(s => s.GetSessionAsync("s-exec-updates", It.IsAny<CancellationToken>())).ReturnsAsync(session);

        var mockMemory = new Mock<IMemoryService>();
        mockMemory.Setup(m => m.RecallFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockMemory.Setup(m => m.RecallUserFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var manager = new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance);

        await manager.RecordExecutionUpdateAsync("s-exec-updates", "任务 TASK_EXEC_002 失败：dotnet test failed");
        await manager.RecordExecutionUpdateAsync("s-exec-updates", "任务 TASK_EXEC_001 已完成：修复已验证");
        await manager.RecordExecutionUpdateAsync("s-exec-updates", "任务 TASK_EXEC_001 已完成：修复已验证");

        var context = await manager.GetFullContextAsync("s-exec-updates");

        Assert.Contains("# 最近后台执行更新", context, StringComparison.Ordinal);
        Assert.Equal(1, context.Split("TASK_EXEC_001", StringSplitOptions.None).Length - 1);
        Assert.Contains("任务 TASK_EXEC_001 已完成：修复已验证", context, StringComparison.Ordinal);
        Assert.Contains("任务 TASK_EXEC_002 失败：dotnet test failed", context, StringComparison.Ordinal);
        Assert.True(
            context.IndexOf("TASK_EXEC_001", StringComparison.Ordinal) <
            context.IndexOf("TASK_EXEC_002", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecordRuntimeCheckpointAsync_ShouldPersistCompactWorkingSetMetadata()
    {
        var session = new CodexSession { Id = "s-runtime-checkpoint" };
        var mockStore = new Mock<ISessionStore>();
        mockStore.Setup(s => s.GetSessionAsync("s-runtime-checkpoint", It.IsAny<CancellationToken>())).ReturnsAsync(session);

        var mockMemory = new Mock<IMemoryService>();
        mockMemory.Setup(m => m.RecallFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockMemory.Setup(m => m.RecallUserFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var manager = new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance);
        var checkpoint = new QueryRuntimeCheckpoint
        {
            QueryId = Guid.NewGuid(),
            SessionId = session.Id,
            EntryPoint = QueryLoopEntryPoint.ForgeWorker,
            Round = 2,
            CurrentPhase = QueryRuntimeLoopPhase.StopDecision,
            ActiveTaskSummary = "TASK_BUG_001",
            WorkerType = "Forge",
            WorkerDisplayName = "Ivilson-Forge",
            WorkerIsolationMode = "ShadowWorktree",
            RequiredToolContractName = "forge_worker_write_evidence",
            RequiredToolContractToolNames = ["hs_write"],
            RequiredToolContractSatisfied = true,
            TotalToolCalls = 2,
            WriteToolCalls = 1,
            RecoveryCount = 0,
            EvidenceLedger = new QueryEvidenceLedgerSnapshot
            {
                Files =
                [
                    new FileEvidence
                    {
                        FilePath = "src/CleanApp.Domain/AppFile.cs",
                        ToolName = "hs_read",
                        SnapshotId = "snap_1",
                        FileFingerprint = "fp_1"
                    }
                ],
                ToolResults =
                [
                    new ToolEvidence
                    {
                        ToolName = "hs_write",
                        CallId = "call-1",
                        Success = true,
                        Summary = "changed Pending to Ready"
                    }
                ]
            }
        };

        await manager.RecordRuntimeCheckpointAsync(session.Id, checkpoint);

        Assert.True(session.Metadata.TryGetValue("QueryRuntimeCheckpoint", out var serialized));
        Assert.Contains("TASK_BUG_001", serialized, StringComparison.Ordinal);
        Assert.Contains("hs_write", serialized, StringComparison.Ordinal);
        Assert.Contains("AppFile.cs", serialized, StringComparison.Ordinal);
        mockStore.Verify(s => s.SaveSessionAsync(
            It.Is<CodexSession>(value => value.Metadata.ContainsKey("QueryRuntimeCheckpoint")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RecordTaskCompletedAsync_ShouldAutoFlush_WhenFiveTasksAccumulate()
    {
        // Arrange
        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();

        var session = new CodexSession { Id = "s-record" };

        mockStore.Setup(s => s.GetSessionAsync("s-record", It.IsAny<CancellationToken>())).ReturnsAsync(session);
        mockMemory.Setup(m => m.RecallFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockMemory.Setup(m => m.RecallUserFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockMemory.Setup(m => m.SummarizeExecutionAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("auto-flush summary");
        mockMemory.Setup(m => m.ExtractStructuredFactsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var manager = new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance);

        // Act — record 5 tasks; the 5th should trigger auto-flush
        for (var i = 1; i <= 5; i++)
        {
            await manager.RecordTaskCompletedAsync("s-record", $"task-{i}");
        }

        // Assert — SummarizeExecutionAsync was called once (auto-flush triggered)
        mockMemory.Verify(m => m.SummarizeExecutionAsync(
            "s-record",
            It.Is<IReadOnlyList<string>>(tasks => tasks.Count == 5),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetFullContextAsync_ShouldRenderSemanticRecallSection_AndFreshnessWarning()
    {
        var session = new CodexSession
        {
            Id = "s4",
            ProjectSummary = "summary"
        };
        session.Metadata["MemoryFreshnessState"] = "stale";
        session.Metadata["MemoryFreshnessReason"] = "head changed";

        var recallTrace = new SemanticRecallTrace(
            "fix architecture drift",
            "python-vector+heuristic",
            true,
            true,
            false,
            null,
            3,
            2,
            DateTime.UtcNow,
            [
                new SemanticRecallTraceItem("fact-1", "structured_fact", 0.82d, "stale", "IUnitOfWork 已迁移到 Core。", DateTime.UtcNow, MemoryFactCategories.Project, null),
                new SemanticRecallTraceItem("summary-1", "project_summary", 0.64d, "stale", "Core 不应依赖 Infrastructure。", DateTime.UtcNow, MemoryFactCategories.Project, null)
            ]);

        var mockStore = new Mock<ISessionStore>();
        mockStore.Setup(s => s.GetSessionAsync("s4", It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);

        var mockMemory = new Mock<IMemoryService>();
        mockMemory.Setup(m => m.RecallFactsAsync("s4", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new MemoryEntry(ProjectMemoryFactKeys.SemanticRecallTrace, JsonConvert.SerializeObject(recallTrace), MemoryFactCategories.Project, DateTime.UtcNow)
            ]);
        mockMemory.Setup(m => m.RecallUserFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var manager = new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance);

        var context = await manager.GetFullContextAsync("s4");

        Assert.Contains("# 记忆新鲜度警告", context, StringComparison.Ordinal);
        Assert.Contains("head changed", context, StringComparison.Ordinal);
        Assert.Contains("# 语义召回上下文", context, StringComparison.Ordinal);
        Assert.Contains("fix architecture drift", context, StringComparison.Ordinal);
        Assert.Contains("[structured_fact] score=0.82, freshness=stale", context, StringComparison.Ordinal);
        Assert.DoesNotContain("- SemanticRecallTrace:", context, StringComparison.Ordinal);
    }

    // ─── Context Compression Projection Tests ───────────────────────────────────

    [Fact]
    public async Task GetOrCreateSessionAsync_RedisMiss_HydratesCompressionStateFromProjectionStore()
    {
        // Arrange
        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();
        var mockCompression = new Mock<ISessionCompressionStore>();

        var compressedAt = new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc);
        mockStore.Setup(s => s.GetSessionAsync("s-miss", It.IsAny<CancellationToken>())).ReturnsAsync((CodexSession?)null);
        mockMemory.Setup(m => m.RecallFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockMemory.Setup(m => m.RecallUserFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockCompression.Setup(c => c.LoadAsync("s-miss", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionCompressionState("Prior conversation summary.", compressedAt));

        var manager = new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance, mockCompression.Object);

        // Act
        var result = await manager.GetOrCreateSessionAsync("s-miss");

        // Assert — compression state hydrated from PG projection
        Assert.Equal("Prior conversation summary.", result.HistorySummary);
        Assert.Equal(compressedAt, result.LastCompressedAt);
        mockCompression.Verify(c => c.LoadAsync("s-miss", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RecordMessageAsync_CompressionTriggered_PersistsProjectionToStore()
    {
        // Arrange
        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();
        var mockCompression = new Mock<ISessionCompressionStore>();
        var triggerOptions = new ContextCompressionTriggerOptions
        {
            ModelMaxContextTokens = 100,
            AutoCompressionTriggerRatio = 0.50d,
            EstimatedCharsPerToken = 1.0d,
            MinRecentTurnsBeforeCompression = 2
        };

        var session = new CodexSession { Id = "s-compress" };
        session.RecentTurns.Add(new ChatTurn("user", new string('a', 24), DateTime.UtcNow));
        session.RecentTurns.Add(new ChatTurn("assistant", new string('b', 24), DateTime.UtcNow));

        mockStore.Setup(s => s.GetSessionAsync("s-compress", It.IsAny<CancellationToken>())).ReturnsAsync(session);
        mockMemory.Setup(m => m.RecallFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockMemory.Setup(m => m.RecallUserFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockMemory.Setup(m => m.AppendTurnAsync(It.IsAny<string>(), It.IsAny<ChatTurn>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockMemory.Setup(m => m.SummarizeHistoryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<ChatTurn>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("new summary");

        var manager = new CodexSessionManager(
            mockStore.Object,
            mockMemory.Object,
            NullLogger<CodexSessionManager>.Instance,
            mockCompression.Object,
            triggerOptions);

        // Act — estimated conversation context now exceeds 50 tokens
        await manager.RecordMessageAsync("s-compress", "user", new string('c', 24));

        // Assert — SaveAsync called with the new summary
        mockCompression.Verify(c => c.SaveAsync(
            "s-compress",
            "new summary",
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(session.LastCompressedAt);
    }

    [Fact]
    public async Task RecordMessageAsync_BelowThreshold_PersistsRecentTurnsWithoutCompression()
    {
        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();
        var mockCompression = new Mock<ISessionCompressionStore>();
        var triggerOptions = new ContextCompressionTriggerOptions
        {
            ModelMaxContextTokens = 1000,
            AutoCompressionTriggerRatio = 0.85d,
            EstimatedCharsPerToken = 4.0d,
            MinRecentTurnsBeforeCompression = 2
        };

        var session = new CodexSession { Id = "s-below-threshold" };
        mockStore.Setup(s => s.GetSessionAsync("s-below-threshold", It.IsAny<CancellationToken>())).ReturnsAsync(session);
        mockMemory.Setup(m => m.RecallFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockMemory.Setup(m => m.RecallUserFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockMemory.Setup(m => m.AppendTurnAsync(It.IsAny<string>(), It.IsAny<ChatTurn>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var manager = new CodexSessionManager(
            mockStore.Object,
            mockMemory.Object,
            NullLogger<CodexSessionManager>.Instance,
            mockCompression.Object,
            triggerOptions);

        await manager.RecordMessageAsync("s-below-threshold", "user", "short message");

        mockCompression.Verify(c => c.SaveAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Never);
        mockStore.Verify(s => s.SaveSessionAsync(
            It.Is<CodexSession>(ss => ss.Id == "s-below-threshold" && ss.RecentTurns.Count == 1),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        Assert.Single(session.RecentTurns);
        Assert.Null(session.LastCompressedAt);
    }

    [Fact]
    public async Task RecordMessageAsync_OversizedTurn_IsCompactedForContextWindow_ButRawTurnIsAppended()
    {
        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();
        var triggerOptions = new ContextCompressionTriggerOptions
        {
            SingleTurnSoftLimitChars = 640,
            SingleTurnPreserveHeadChars = 160,
            SingleTurnPreserveTailChars = 160,
            ModelMaxContextTokens = 10_000,
            AutoCompressionTriggerRatio = 1.0d
        };

        var session = new CodexSession { Id = "s-oversized-turn" };
        var originalContent = string.Concat(
            new string('A', 320),
            new string('B', 320),
            new string('C', 320));

        mockStore.Setup(s => s.GetSessionAsync("s-oversized-turn", It.IsAny<CancellationToken>())).ReturnsAsync(session);
        mockMemory.Setup(m => m.RecallFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockMemory.Setup(m => m.RecallUserFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockMemory.Setup(m => m.AppendTurnAsync(It.IsAny<string>(), It.IsAny<ChatTurn>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var manager = new CodexSessionManager(
            mockStore.Object,
            mockMemory.Object,
            NullLogger<CodexSessionManager>.Instance,
            compressionStore: null,
            triggerOptions);

        await manager.RecordMessageAsync("s-oversized-turn", "assistant", originalContent);

        mockMemory.Verify(m => m.AppendTurnAsync(
            "s-oversized-turn",
            It.Is<ChatTurn>(turn => turn.Content == originalContent),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Single(session.RecentTurns);
        Assert.NotEqual(originalContent, session.RecentTurns[0].Content);
        Assert.Contains("chars omitted for context window", session.RecentTurns[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordMessagesAsync_RecentTurnOverflow_IsLightTrimmedIntoHistorySummary()
    {
        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();
        var triggerOptions = new ContextCompressionTriggerOptions
        {
            RecentTurnsSoftLimit = 3,
            ModelMaxContextTokens = 10_000,
            AutoCompressionTriggerRatio = 1.0d,
            MinRecentTurnsBeforeCompression = 10
        };

        var session = new CodexSession { Id = "s-light-trim" };
        session.RecentTurns.Add(new ChatTurn("user", "u1", DateTime.UtcNow.AddMinutes(-5)));
        session.RecentTurns.Add(new ChatTurn("assistant", "a1", DateTime.UtcNow.AddMinutes(-4)));
        session.RecentTurns.Add(new ChatTurn("user", "u2", DateTime.UtcNow.AddMinutes(-3)));

        mockStore.Setup(s => s.GetSessionAsync("s-light-trim", It.IsAny<CancellationToken>())).ReturnsAsync(session);
        mockMemory.Setup(m => m.RecallFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockMemory.Setup(m => m.RecallUserFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockMemory.Setup(m => m.AppendTurnAsync(It.IsAny<string>(), It.IsAny<ChatTurn>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockMemory.Setup(m => m.SummarizeHistoryAsync(
                "s-light-trim",
                It.IsAny<string>(),
                It.Is<IReadOnlyList<ChatTurn>>(turns => turns.Count == 2 && turns[0].Content == "u1" && turns[1].Content == "a1"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("trimmed summary");

        var manager = new CodexSessionManager(
            mockStore.Object,
            mockMemory.Object,
            NullLogger<CodexSessionManager>.Instance,
            compressionStore: null,
            triggerOptions);

        await manager.RecordMessagesAsync(
            "s-light-trim",
            ("assistant", "a2"),
            ("user", "u3"));

        Assert.Equal("trimmed summary", session.HistorySummary);
        Assert.Equal(3, session.RecentTurns.Count);
        Assert.Equal(["u2", "a2", "u3"], session.RecentTurns.Select(turn => turn.Content).ToArray());
    }

    [Fact]
    public async Task ApplyContextGovernanceAsync_HardLimit_TriggersFullCompression()
    {
        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();
        var triggerOptions = new ContextCompressionTriggerOptions
        {
            ModelMaxContextTokens = 10_000,
            AutoCompressionTriggerRatio = 1.0d,
            MinRecentTurnsBeforeCompression = 1
        };

        var session = new CodexSession { Id = "s-hard-limit" };
        session.RecentTurns.Add(new ChatTurn("user", new string('a', 40), DateTime.UtcNow.AddMinutes(-2)));
        session.RecentTurns.Add(new ChatTurn("assistant", new string('b', 40), DateTime.UtcNow.AddMinutes(-1)));

        mockStore.Setup(s => s.GetSessionAsync("s-hard-limit", It.IsAny<CancellationToken>())).ReturnsAsync(session);
        mockMemory.Setup(m => m.RecallFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockMemory.Setup(m => m.RecallUserFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockMemory.Setup(m => m.SummarizeHistoryAsync(
                "s-hard-limit",
                It.IsAny<string>(),
                It.Is<IReadOnlyList<ChatTurn>>(turns => turns.Count == 2),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("hard-limit summary");

        var manager = new CodexSessionManager(
            mockStore.Object,
            mockMemory.Object,
            NullLogger<CodexSessionManager>.Instance,
            compressionStore: null,
            triggerOptions);

        await manager.ApplyContextGovernanceAsync("s-hard-limit", warnLimitChars: 60, hardLimitChars: 70);

        Assert.Equal("hard-limit summary", session.HistorySummary);
        Assert.Empty(session.RecentTurns);
        Assert.NotNull(session.LastCompressedAt);
    }

    [Fact]
    public async Task RecordTurnsAsync_ProgressUpdate_IsPersistedButExcludedFromContextWindow()
    {
        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();
        var session = new CodexSession { Id = "s-progress" };

        mockStore.Setup(s => s.GetSessionAsync("s-progress", It.IsAny<CancellationToken>())).ReturnsAsync(session);
        mockMemory.Setup(m => m.RecallFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockMemory.Setup(m => m.RecallUserFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockMemory.Setup(m => m.AppendTurnAsync(It.IsAny<string>(), It.IsAny<ChatTurn>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var manager = new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance);

        await manager.RecordTurnsAsync(
            "s-progress",
            SessionTurnWrite.Progress("build 50% complete"));

        mockMemory.Verify(m => m.AppendTurnAsync(
            "s-progress",
            It.Is<ChatTurn>(turn => turn.Role == "progress" && turn.Content == "build 50% complete"),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Empty(session.RecentTurns);
    }

    [Fact]
    public async Task RecordTurnsAsync_SystemBoundary_EntersContextWindowWithoutPretendingToBeUser()
    {
        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();
        var session = new CodexSession { Id = "s-boundary" };

        mockStore.Setup(s => s.GetSessionAsync("s-boundary", It.IsAny<CancellationToken>())).ReturnsAsync(session);
        mockMemory.Setup(m => m.RecallFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockMemory.Setup(m => m.RecallUserFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockMemory.Setup(m => m.AppendTurnAsync(It.IsAny<string>(), It.IsAny<ChatTurn>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var manager = new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance);

        await manager.RecordTurnsAsync(
            "s-boundary",
            SessionTurnWrite.SystemBoundary("[系统] 任务 worker-1 执行完成"));

        mockMemory.Verify(m => m.AppendTurnAsync(
            "s-boundary",
            It.Is<ChatTurn>(turn => turn.Role == "system_boundary"),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Single(session.RecentTurns);
        Assert.Equal("system_boundary", session.RecentTurns[0].Role);
    }

    [Fact]
    public void SessionMessageDurabilityPolicy_TaskCompleted_MapsToSystemBoundary()
    {
        var turn = SessionMessageDurabilityPolicy.FromEvent(CodexEventType.TaskCompleted, "task done");

        Assert.Equal(SessionMessageKind.SystemBoundary, turn.Kind);
        Assert.Equal("system_boundary", turn.Role);
    }

    [Fact]
    public void SessionMessageDurabilityPolicy_TaskProgress_MapsToProgress()
    {
        var turn = SessionMessageDurabilityPolicy.FromEvent(CodexEventType.TaskProgress, "50%");

        Assert.Equal(SessionMessageKind.Progress, turn.Kind);
        Assert.Equal("progress", turn.Role);
    }

    [Fact]
    public async Task OnStageChangedAsync_CompressionTriggered_PersistsProjectionToStore()
    {
        // Arrange
        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();
        var mockCompression = new Mock<ISessionCompressionStore>();

        var session = new CodexSession { Id = "s-stage-compress" };
        session.RecentTurns.Add(new ChatTurn("user", "work item", DateTime.UtcNow));

        mockStore.Setup(s => s.GetSessionAsync("s-stage-compress", It.IsAny<CancellationToken>())).ReturnsAsync(session);
        mockMemory.Setup(m => m.RecallFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockMemory.Setup(m => m.RecallUserFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockMemory.Setup(m => m.SummarizeHistoryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<ChatTurn>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("stage transition summary");
        mockMemory.Setup(m => m.ExtractStructuredFactsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var manager = new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance, mockCompression.Object);

        // Act
        await manager.OnStageChangedAsync("s-stage-compress", newStage: 3);

        // Assert — compression projection persisted
        mockCompression.Verify(c => c.SaveAsync(
            "s-stage-compress",
            "stage transition summary",
            It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ClearSessionAsync_ClearsProjectionStore()
    {
        // Arrange
        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();
        var mockCompression = new Mock<ISessionCompressionStore>();

        mockStore.Setup(s => s.DeleteSessionAsync("s-clear", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockMemory.Setup(m => m.ClearSessionMemoryAsync("s-clear", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var manager = new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance, mockCompression.Object);

        // Act
        await manager.ClearSessionAsync("s-clear");

        // Assert — PG projection was cleared
        mockCompression.Verify(c => c.ClearAsync("s-clear", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompressContextAsync_EmptySession_ReturnsNothingToCompress()
    {
        var session = new CodexSession { Id = "s-compact-empty", UserId = "u1" };
        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();
        mockStore.Setup(s => s.GetSessionAsync("s-compact-empty", It.IsAny<CancellationToken>())).ReturnsAsync(session);
        mockMemory.Setup(m => m.RecallFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockMemory.Setup(m => m.RecallUserFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var manager = new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance);

        var result = await manager.CompressContextAsync("s-compact-empty");

        Assert.False(result.Compressed);
        Assert.Equal("nothing_to_compress", result.Status);
        Assert.Equal(0, result.CompressedTurnCount);
    }

    [Fact]
    public async Task CompressContextAsync_UsesModelSummarizerWhenMemoryServiceIsNoOp_AndClearsHighWatermark()
    {
        var session = new CodexSession { Id = "s-compact-1", UserId = "u1" };
        session.RecentTurns.Add(new ChatTurn("user", new string('A', 400), DateTime.UtcNow));
        session.RecentTurns.Add(new ChatTurn("assistant", new string('B', 400), DateTime.UtcNow));
        session.Metadata["ContextWindowLastReportedTokens"] = "42000";
        session.Metadata["ContextWindowLastBaselineEstimatedTokens"] = "1000";

        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();
        var mockCompression = new Mock<ISessionCompressionStore>();

        mockStore.Setup(s => s.GetSessionAsync("s-compact-1", It.IsAny<CancellationToken>())).ReturnsAsync(session);
        mockStore.Setup(s => s.SaveSessionAsync(It.IsAny<CodexSession>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockMemory.Setup(m => m.RecallFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockMemory.Setup(m => m.RecallUserFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        // Simulate NullMemoryService returning existing summary unchanged
        mockMemory.Setup(m => m.SummarizeHistoryAsync("s-compact-1", string.Empty, It.IsAny<IReadOnlyList<ChatTurn>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);

        var manager = new CodexSessionManager(
            mockStore.Object,
            mockMemory.Object,
            NullLogger<CodexSessionManager>.Instance,
            mockCompression.Object);

        string? capturedFocus = null;
        var result = await manager.CompressContextAsync(
            "s-compact-1",
            focusInstruction: "保留接口设计",
            modelSummarizer: (_, turns, focus, _) =>
            {
                capturedFocus = focus;
                return Task.FromResult<string?>($"精炼摘要：共 {turns.Count} 条，重点={focus}");
            });

        Assert.True(result.Compressed);
        Assert.Equal("compressed", result.Status);
        Assert.Equal(2, result.CompressedTurnCount);
        Assert.Equal("保留接口设计", capturedFocus);
        Assert.Empty(session.RecentTurns);
        Assert.NotNull(session.LastCompressedAt);
        Assert.Contains("精炼摘要", session.HistorySummary);
        Assert.False(session.Metadata.ContainsKey("ContextWindowLastReportedTokens"));
        Assert.False(session.Metadata.ContainsKey("ContextWindowLastBaselineEstimatedTokens"));
        Assert.True(result.EstimatedTokensBefore >= 42000);
        Assert.True(result.EstimatedTokensAfter < result.EstimatedTokensBefore);
        mockCompression.Verify(c => c.SaveAsync("s-compact-1", session.HistorySummary, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompressContextAsync_FallbackTurnsAndDeterministicDegradation_WhenModelFails()
    {
        var session = new CodexSession { Id = "s-compact-fallback", UserId = "u1" };
        var mockStore = new Mock<ISessionStore>();
        var mockMemory = new Mock<IMemoryService>();

        mockStore.Setup(s => s.GetSessionAsync("s-compact-fallback", It.IsAny<CancellationToken>())).ReturnsAsync(session);
        mockStore.Setup(s => s.SaveSessionAsync(It.IsAny<CodexSession>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockMemory.Setup(m => m.RecallFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockMemory.Setup(m => m.RecallUserFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        mockMemory.Setup(m => m.SummarizeHistoryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<ChatTurn>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);

        var manager = new CodexSessionManager(mockStore.Object, mockMemory.Object, NullLogger<CodexSessionManager>.Instance);
        var fallbackTurns = new List<ChatTurn>
        {
            new("user", "请梳理支付回调幂等方案", DateTime.UtcNow.AddMinutes(-2)),
            new("assistant", "已确认采用幂等键 + 事务去重表", DateTime.UtcNow.AddMinutes(-1)),
        };

        var result = await manager.CompressContextAsync(
            "s-compact-fallback",
            fallbackTurns: fallbackTurns,
            modelSummarizer: (_, _, _, _) => throw new InvalidOperationException("model offline"));

        Assert.True(result.Compressed);
        Assert.Equal(2, result.CompressedTurnCount);
        Assert.Contains("支付回调幂等方案", session.HistorySummary);
        Assert.Contains("事务去重表", session.HistorySummary);
    }
}
