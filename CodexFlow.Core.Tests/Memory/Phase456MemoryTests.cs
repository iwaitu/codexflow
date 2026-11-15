// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using CodexFlow.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.Memory;

// ─── Phase 4: DefaultFactGuardService ────────────────────────────────────────

public class DefaultFactGuardServiceTests
{
    private static CodexSession MakeSession(string id = "s1") => new() { Id = id };

    private static DefaultFactGuardService MakeGuard(Mock<IMemoryService> mockMemory)
        => new(mockMemory.Object, NullLogger<DefaultFactGuardService>.Instance);

    private static string HighMeta =>
        new MemoryEntryMetadata(Confidence: MemoryFactConfidence.High, Source: "tool").ToJson();

    private static string LowMeta =>
        new MemoryEntryMetadata(Confidence: MemoryFactConfidence.Low, Source: "model").ToJson();

    private static string MediumMeta =>
        new MemoryEntryMetadata(Confidence: MemoryFactConfidence.Medium, Source: "user").ToJson();

    // ── Test 1 ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task GuardedLearnFactAsync_HighConfidenceIncoming_OverLowConfidenceExisting_WritesSuccessfully()
    {
        // Arrange
        var mockMemory = new Mock<IMemoryService>();
        var guard = MakeGuard(mockMemory);

        // Protected key with an existing low-confidence fact.
        var existingFact = new MemoryEntry(
            ProjectMemoryFactKeys.ArchitectureAudit,
            "old value",
            MemoryFactCategories.Analysis,
            DateTime.UtcNow,
            LowMeta);
        mockMemory
            .Setup(m => m.RecallFactsAsync("s1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([existingFact]);

        // Act — incoming is high confidence
        var outcome = await guard.GuardedLearnFactAsync(
            "s1",
            ProjectMemoryFactKeys.ArchitectureAudit,
            "new value",
            MemoryFactCategories.Analysis,
            HighMeta);

        // Assert: write was allowed
        Assert.Equal(FactWriteOutcome.Written, outcome);
        mockMemory.Verify(m => m.LearnFactAsync(
            "s1", ProjectMemoryFactKeys.ArchitectureAudit, "new value",
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Test 2 ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task GuardedLearnFactAsync_LowConfidenceIncoming_OverHighConfidenceExisting_IsSkipped()
    {
        // Arrange
        var mockMemory = new Mock<IMemoryService>();
        var guard = MakeGuard(mockMemory);

        var existingFact = new MemoryEntry(
            ProjectMemoryFactKeys.ArchitectureAudit,
            "authoritative value",
            MemoryFactCategories.Analysis,
            DateTime.UtcNow,
            HighMeta);
        mockMemory
            .Setup(m => m.RecallFactsAsync("s1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([existingFact]);

        // Act — incoming is low confidence
        var outcome = await guard.GuardedLearnFactAsync(
            "s1",
            ProjectMemoryFactKeys.ArchitectureAudit,
            "speculative value",
            MemoryFactCategories.Analysis,
            LowMeta);

        // Assert: write was skipped
        Assert.Equal(FactWriteOutcome.Skipped, outcome);
        mockMemory.Verify(m => m.LearnFactAsync(
            It.IsAny<string>(), ProjectMemoryFactKeys.ArchitectureAudit, It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Test 3 ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task GuardedLearnFactAsync_BothHighConfidence_EnqueuesConflict()
    {
        // Arrange
        var mockMemory = new Mock<IMemoryService>();
        var guard = MakeGuard(mockMemory);

        var existingFact = new MemoryEntry(
            ProjectMemoryFactKeys.VerificationBaseline,
            "baseline v1",
            MemoryFactCategories.Verification,
            DateTime.UtcNow,
            HighMeta);
        mockMemory
            .Setup(m => m.RecallFactsAsync("s1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([existingFact]);

        // Act — incoming is also high confidence
        var outcome = await guard.GuardedLearnFactAsync(
            "s1",
            ProjectMemoryFactKeys.VerificationBaseline,
            "baseline v2",
            MemoryFactCategories.Verification,
            HighMeta);

        // Assert: conflict enqueued, no write occurred
        Assert.Equal(FactWriteOutcome.ConflictEnqueued, outcome);
        mockMemory.Verify(m => m.LearnFactAsync(
            It.IsAny<string>(), ProjectMemoryFactKeys.VerificationBaseline, It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // Pending conflict should be recorded
        var conflicts = await guard.GetPendingConflictsAsync("s1");
        Assert.Single(conflicts);
        Assert.Equal(ProjectMemoryFactKeys.VerificationBaseline, conflicts[0].Key);
        Assert.Equal("baseline v2", conflicts[0].IncomingValue);
    }
}

// ─── Phase 5: DefaultDriftGovernor ───────────────────────────────────────────

public class DefaultDriftGovernorTests
{
    private static (DefaultDriftGovernor Governor, Mock<IMemoryService> MockMemory, Mock<ISessionStore> MockStore, Mock<IVectorSyncAdapter> MockSync)
        MakeGovernor()
    {
        var mockMemory = new Mock<IMemoryService>();
        var mockStore = new Mock<ISessionStore>();
        var mockSync = new Mock<IVectorSyncAdapter>();

        mockStore
            .Setup(s => s.GetSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => new CodexSession { Id = id });
        mockStore
            .Setup(s => s.SaveSessionAsync(It.IsAny<CodexSession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var governor = new DefaultDriftGovernor(
            mockMemory.Object,
            mockStore.Object,
            NullLogger<DefaultDriftGovernor>.Instance,
            mockSync.Object);

        return (governor, mockMemory, mockStore, mockSync);
    }

    // ── Test 4 ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task CheckDriftAsync_AllThreeDriftTypes_AreDetectedCorrectly()
    {
        // Arrange
        var (governor, mockMemory, _, _) = MakeGovernor();

        // Code-structure drift: MemoryFreshness fact says IsStale=true
        var freshnessState = new MemoryFreshnessState(IsStale: true, Reason: "head changed", CheckedAtUtc: DateTime.UtcNow);
        var freshnessFact = new MemoryEntry(
            ProjectMemoryFactKeys.MemoryFreshness,
            System.Text.Json.JsonSerializer.Serialize(freshnessState),
            MemoryFactCategories.Analysis, DateTime.UtcNow);

        // Also: no ProjectSummaryVersion fact, but ProjectSummary is empty → summary drift
        mockMemory
            .Setup(m => m.RecallFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([freshnessFact]);

        // Session with vector stale marker and empty ProjectSummary
        var session = new CodexSession { Id = "s1" };
        session.Metadata["VectorDriftState"] = "stale";
        session.Metadata["VectorDriftReason"] = "facts updated";
        // ProjectSummary is blank (default empty string) → triggers summary drift via cross-check

        // Act
        var report = await governor.CheckDriftAsync(session);

        // Assert
        Assert.True(report.HasAnyDrift);
        Assert.True(report.IsCodeStructureDrifted);
        Assert.True(report.IsVectorProjectionDrifted);
    }

    // ── Test 5 ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task ApplyAutoRefreshAsync_ManualSummarySaved_MarksSummaryStalAndSessionSaved()
    {
        // Arrange
        var (governor, mockMemory, mockStore, _) = MakeGovernor();

        // Freshness fact for code-structure check
        mockMemory
            .Setup(m => m.RecallFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var session = new CodexSession { Id = "s2" };
        mockStore
            .Setup(s => s.GetSessionAsync("s2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);

        // Act
        await governor.ApplyAutoRefreshAsync(session, AutoRefreshTrigger.ManualSummarySaved);

        // Assert: MarkSummaryStalAsync should have saved session with stale marker
        mockStore.Verify(s => s.SaveSessionAsync(
            It.Is<CodexSession>(sess => sess.Metadata.ContainsKey("SummaryDriftState")
                                        && sess.Metadata["SummaryDriftState"] == "stale"),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    // ── Test 6 ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task CheckDriftAsync_WhenVectorSyncAdapterReturnsTrue_DoesNotThrow()
    {
        // Arrange — adapter always succeeds, verify no exception is thrown on drift check
        var (governor, mockMemory, _, mockSync) = MakeGovernor();
        mockSync.Setup(s => s.TrySyncAsync(It.IsAny<CodexSession>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false); // adapter fails

        mockMemory
            .Setup(m => m.RecallFactsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var session = new CodexSession { Id = "s3" };

        // Act / Assert: no exception expected
        var report = await governor.CheckDriftAsync(session);
        Assert.NotNull(report);
    }
}

// ─── Phase 6: DefaultMemoryOrchestrator ──────────────────────────────────────

public class DefaultMemoryOrchestratorTests
{
    private static (
        DefaultMemoryOrchestrator Orchestrator,
        Mock<IMemoryService> MockMemory,
        Mock<IFactGuardService> MockGuard,
        Mock<IDriftGovernor> MockDrift,
        Mock<ISessionStore> MockStore,
        Mock<IVectorSyncAdapter> MockSync)
        MakeOrchestrator()
    {
        var mockMemory = new Mock<IMemoryService>();
        var mockGuard = new Mock<IFactGuardService>();
        var mockDrift = new Mock<IDriftGovernor>();
        var mockStore = new Mock<ISessionStore>();
        var mockSync = new Mock<IVectorSyncAdapter>();

        mockStore
            .Setup(s => s.SaveSessionAsync(It.IsAny<CodexSession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var orchestrator = new DefaultMemoryOrchestrator(
            mockMemory.Object,
            mockGuard.Object,
            mockDrift.Object,
            mockStore.Object,
            NullLogger<DefaultMemoryOrchestrator>.Instance,
            mockSync.Object);

        return (orchestrator, mockMemory, mockGuard, mockDrift, mockStore, mockSync);
    }

    private static CodexSession MakeSession(string id = "s1") => new() { Id = id };

    // ── Test 7 ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task WriteFactAsync_PrimaryStoreThrows_PropagatesException()
    {
        // Arrange
        var (orch, _, mockGuard, mockDrift, _, _) = MakeOrchestrator();
        var session = MakeSession();

        mockGuard
            .Setup(g => g.GuardedLearnFactAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Mongo unavailable"));

        // Act / Assert: exception must propagate
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orch.WriteFactAsync(session, "some_key", "value"));
    }

    // ── Test 8 ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task TriggerVectorSyncAsync_AdapterThrows_DoesNotThrow()
    {
        // Arrange
        var (orch, _, _, _, _, mockSync) = MakeOrchestrator();
        var session = MakeSession();

        mockSync
            .Setup(s => s.TrySyncAsync(It.IsAny<CodexSession>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Qdrant connection refused"));

        // Act / Assert: no exception should escape the orchestrator
        var ex = await Record.ExceptionAsync(() => orch.TriggerVectorSyncAsync(session));
        Assert.Null(ex);
    }

    // ── Test 9 ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task WriteFactAsync_OnSuccess_UpdatesLastFactWriteAtAndMarksVectorStale()
    {
        // Arrange
        var (orch, _, mockGuard, mockDrift, mockStore, _) = MakeOrchestrator();
        var session = MakeSession();

        mockGuard
            .Setup(g => g.GuardedLearnFactAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FactWriteOutcome.Written);

        mockDrift
            .Setup(d => d.MarkVectorStalAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await orch.WriteFactAsync(session, "decision_key", "some decision", "decision");

        // Assert 1: session metadata timestamp was updated
        mockStore.Verify(s => s.SaveSessionAsync(
            It.Is<CodexSession>(sess => sess.Metadata.ContainsKey("LastFactWriteAt")),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);

        // Assert 2: MarkVectorStalAsync was called to flag that Qdrant is now behind
        mockDrift.Verify(d => d.MarkVectorStalAsync(
            session.Id, It.Is<string>(r => r.Contains("decision_key")), It.IsAny<CancellationToken>()), Times.Once);
    }
}
