// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using CodexFlow.Core.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.Memory;

// ─── Phase 7: DefaultMemoryContextAssembler ───────────────────────────────────

public class DefaultMemoryContextAssemblerTests
{
    private static DefaultMemoryContextAssembler MakeAssembler(Mock<IMemoryService> mockMemory)
        => new(mockMemory.Object, NullLogger<DefaultMemoryContextAssembler>.Instance);

    private static CodexSession MakeSession(string id = "s1", string summary = "") =>
        new() { Id = id, ProjectSummary = summary };

    // ── Test 1 ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task AssembleAsync_ChatScenario_WithSummaryAndFact_ReturnsBothSections()
    {
        // Arrange
        var mockMemory = new Mock<IMemoryService>();
        var assembler = MakeAssembler(mockMemory);

        var session = MakeSession(summary: "This is the project summary.");
        session.ReplaceActiveFacts([
            new MemoryEntry(ProjectMemoryFactKeys.ArchitectureAudit, "audit content", "analysis", DateTime.UtcNow)
        ]);

        // Act
        var envelope = await assembler.AssembleAsync(
            new MemoryContextRequest(session, MemoryInjectionScenario.Chat));

        // Assert: summary + architecture audit injected
        Assert.False(envelope.IsEmpty);
        Assert.Contains(envelope.Sections, s => s.Label == "ProjectSummary");
        Assert.Contains(envelope.Sections, s => s.Label == "ArchitectureAudit");
    }

    // ── Test 2 ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task AssembleAsync_PlanningScenario_ReturnsEmpty()
    {
        // Planning agents already embed full context inline — assembler should not add extra noise.
        var mockMemory = new Mock<IMemoryService>();
        var assembler = MakeAssembler(mockMemory);

        var session = MakeSession(summary: "summary");

        var envelope = await assembler.AssembleAsync(
            new MemoryContextRequest(session, MemoryInjectionScenario.Planning));

        Assert.True(envelope.IsEmpty);
        mockMemory.Verify(m => m.RecallFactsAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Test 3 ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task AssembleAsync_ValidationScenario_ReturnsEmpty()
    {
        var mockMemory = new Mock<IMemoryService>();
        var assembler = MakeAssembler(mockMemory);

        var envelope = await assembler.AssembleAsync(
            new MemoryContextRequest(MakeSession(), MemoryInjectionScenario.Validation));

        Assert.True(envelope.IsEmpty);
    }

    // ── Test 4 ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task AssembleAsync_BackgroundResume_LoadsFactsFromMemoryService()
    {
        // Arrange
        var mockMemory = new Mock<IMemoryService>();
        var facts = new List<MemoryEntry>
        {
            new("key1", "value1", "general", DateTime.UtcNow),
            new("key2", "value2", "general", DateTime.UtcNow.AddSeconds(-1)),
        };
        mockMemory
            .Setup(m => m.RecallFactsAsync("s1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(facts);

        var assembler = MakeAssembler(mockMemory);
        var session = MakeSession("s1");

        // Act
        var envelope = await assembler.AssembleAsync(
            new MemoryContextRequest(session, MemoryInjectionScenario.BackgroundResume));

        // Assert: facts loaded from IMemoryService
        Assert.False(envelope.IsEmpty);
        Assert.Contains(envelope.Sections, s => s.Label == "key1");
        Assert.Contains(envelope.Sections, s => s.Label == "key2");
        mockMemory.Verify(m => m.RecallFactsAsync("s1", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Test 5 ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task MemoryContextEnvelope_AsSystemPromptBlock_ContainsSectionLabels()
    {
        var sections = new[]
        {
            new MemoryContextSection("ProjectSummary", "My project does X."),
            new MemoryContextSection("ArchitectureAudit", "Circular dependency detected."),
        };
        var envelope = new MemoryContextEnvelope(sections);

        var block = envelope.AsSystemPromptBlock();

        Assert.Contains("ProjectSummary", block, StringComparison.Ordinal);
        Assert.Contains("My project does X.", block, StringComparison.Ordinal);
        Assert.Contains("ArchitectureAudit", block, StringComparison.Ordinal);
        Assert.Contains("=== Memory Context ===", block, StringComparison.Ordinal);
    }
}

// ─── Phase 7: DefaultLLMExecutor ─────────────────────────────────────────────

public class DefaultLLMExecutorTests
{
    private static DefaultLLMExecutor MakeExecutor(
        Mock<IChatClient> mockClient,
        Mock<IMemoryContextAssembler> mockAssembler)
        => new(mockClient.Object, mockAssembler.Object, NullLogger<DefaultLLMExecutor>.Instance);

    private static CodexSession MakeSession(string id = "s1") => new() { Id = id };

    // ── Test 6 ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task ExecuteAsync_NoSession_ForwardsDirectlyToChatClient()
    {
        // Arrange
        var mockClient = new Mock<IChatClient>();
        var mockAssembler = new Mock<IMemoryContextAssembler>();
        var executor = MakeExecutor(mockClient, mockAssembler);

        var messages = new List<ChatMessage> { new(ChatRole.User, "Hello") };
        var options = new ChatOptions();
        var expectedResponse = new ChatResponse([new ChatMessage(ChatRole.Assistant, "Hi")]);

        mockClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        // Act
        var request = new LLMExecutionRequest(messages, options, MemoryInjectionScenario.Chat, Session: null);
        var result = await executor.ExecuteAsync(request);

        // Assert: assembler was never called, request forwarded as-is
        Assert.Equal(expectedResponse, result);
        mockAssembler.Verify(a => a.AssembleAsync(It.IsAny<MemoryContextRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Test 7 ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task ExecuteAsync_WithSession_EmptyEnvelope_ForwardsOriginalMessages()
    {
        // Arrange
        var mockClient = new Mock<IChatClient>();
        var mockAssembler = new Mock<IMemoryContextAssembler>();
        var executor = MakeExecutor(mockClient, mockAssembler);

        mockAssembler
            .Setup(a => a.AssembleAsync(It.IsAny<MemoryContextRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MemoryContextEnvelope.Empty);

        var messages = new List<ChatMessage> { new(ChatRole.User, "Hello") };
        var expectedResponse = new ChatResponse([new ChatMessage(ChatRole.Assistant, "Hi")]);
        mockClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        // Act
        var request = new LLMExecutionRequest(messages, null, MemoryInjectionScenario.Chat, MakeSession());
        var result = await executor.ExecuteAsync(request);

        // Assert: no message mutation, single user message forwarded
        Assert.Equal(expectedResponse, result);
        mockClient.Verify(c => c.GetResponseAsync(
            It.Is<IList<ChatMessage>>(m => m.Count == 1 && m[0].Role == ChatRole.User),
            It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Test 8 ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task ExecuteAsync_WithSession_NonEmptyEnvelope_PrependsSectionToSystemMessage()
    {
        // Arrange
        var mockClient = new Mock<IChatClient>();
        var mockAssembler = new Mock<IMemoryContextAssembler>();
        var executor = MakeExecutor(mockClient, mockAssembler);

        var sections = new[] { new MemoryContextSection("ProjectSummary", "My project.") };
        var envelope = new MemoryContextEnvelope(sections);
        mockAssembler
            .Setup(a => a.AssembleAsync(It.IsAny<MemoryContextRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(envelope);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "Original system prompt."),
            new(ChatRole.User, "Question"),
        };
        var expectedResponse = new ChatResponse([new ChatMessage(ChatRole.Assistant, "Answer")]);
        mockClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        // Act
        var request = new LLMExecutionRequest(messages, null, MemoryInjectionScenario.Chat, MakeSession());
        await executor.ExecuteAsync(request);

        // Assert: system message was enriched with memory block prefix
        mockClient.Verify(c => c.GetResponseAsync(
            It.Is<IList<ChatMessage>>(m =>
                m.Count == 2 &&
                m[0].Role == ChatRole.System &&
                (m[0].Text ?? string.Empty).Contains("ProjectSummary", StringComparison.Ordinal) &&
                (m[0].Text ?? string.Empty).Contains("Original system prompt.", StringComparison.Ordinal)),
            It.IsAny<ChatOptions?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Test 9 ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task ExecuteAsync_WithSession_NoExistingSystemMessage_InsertsNewSystemMessage()
    {
        // Arrange
        var mockClient = new Mock<IChatClient>();
        var mockAssembler = new Mock<IMemoryContextAssembler>();
        var executor = MakeExecutor(mockClient, mockAssembler);

        var sections = new[] { new MemoryContextSection("Key", "Value") };
        var envelope = new MemoryContextEnvelope(sections);
        mockAssembler
            .Setup(a => a.AssembleAsync(It.IsAny<MemoryContextRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(envelope);

        var messages = new List<ChatMessage> { new(ChatRole.User, "User message") };
        var expectedResponse = new ChatResponse([new ChatMessage(ChatRole.Assistant, "OK")]);
        mockClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        // Act
        var request = new LLMExecutionRequest(messages, null, MemoryInjectionScenario.Execution, MakeSession());
        await executor.ExecuteAsync(request);

        // Assert: a new system message was inserted before the user message
        mockClient.Verify(c => c.GetResponseAsync(
            It.Is<IList<ChatMessage>>(m =>
                m.Count == 2 &&
                m[0].Role == ChatRole.System &&
                m[1].Role == ChatRole.User),
            It.IsAny<ChatOptions?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
