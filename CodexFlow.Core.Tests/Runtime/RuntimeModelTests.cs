// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Models;
using CodexFlow.Core.Runtime;
using CodexFlow.Core.Telemetry;
using Microsoft.Extensions.AI;
using System.Text.Json;
using Xunit;

namespace CodexFlow.Core.Tests.Runtime;

/// <summary>
/// Phase 0B: Runtime 模型单元测试
/// </summary>
public class QueryRuntimeRequestTests
{
    [Fact]
    public void QueryRuntimeRequest_RequiredFields_AreSet()
    {
        // Arrange
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a helpful assistant."),
            new(ChatRole.User, "Hello!")
        };

        // Act
        var request = new QueryRuntimeRequest
        {
            SessionId = "test-session-123",
            EntryPoint = QueryLoopEntryPoint.SimpleCodexController,
            InitialMessages = messages
        };

        // Assert
        Assert.Equal("test-session-123", request.SessionId);
        Assert.Equal(QueryLoopEntryPoint.SimpleCodexController, request.EntryPoint);
        Assert.Equal(2, request.InitialMessages.Count);
        Assert.Equal(20, request.MaxRounds); // Default value
        Assert.True(request.EnableTools); // Default value
        Assert.True(request.AllowStreaming); // Default value
        Assert.Equal(MemoryInjectionScenario.Chat, request.Scenario); // Default value
    }

    [Fact]
    public void QueryRuntimeRequest_WithOptionalFields_AreSet()
    {
        // Arrange
        var messages = new List<ChatMessage> { new(ChatRole.User, "Test") };
        var hints = new AdapterHints(
            EnableAutoDispatch: true,
            EnableStallDetection: true,
            ContextHardLimit: 600000);

        // Act
        var request = new QueryRuntimeRequest
        {
            SessionId = "test-session",
            EntryPoint = QueryLoopEntryPoint.CodexController,
            InitialMessages = messages,
            MaxRounds = 50,
            EnableTools = false,
            AdapterHints = hints
        };

        // Assert
        Assert.Equal(50, request.MaxRounds);
        Assert.False(request.EnableTools);
        Assert.NotNull(request.AdapterHints);
        Assert.True(request.AdapterHints.EnableAutoDispatch);
        Assert.True(request.AdapterHints.EnableStallDetection);
        Assert.Equal(600000, request.AdapterHints.ContextHardLimit);
    }

    [Fact]
    public void PromptMetadata_CreatesCorrectly()
    {
        // Arrange & Act
        var metadata = new PromptMetadata(
            RolePrompt: "You are an architect.",
            WorkspacePath: "/workspace/project",
            PlanSize: 10,
            InitialStage: 2);

        // Assert
        Assert.Equal("You are an architect.", metadata.RolePrompt);
        Assert.Equal("/workspace/project", metadata.WorkspacePath);
        Assert.Equal(10, metadata.PlanSize);
        Assert.Equal(2, metadata.InitialStage);
    }

    [Fact]
    public void AdapterHints_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var hints = new AdapterHints();

        // Assert - 最保守默认配置
        Assert.False(hints.EnableAutoDispatch);
        Assert.False(hints.EnableStallDetection);
        Assert.False(hints.EnableContextLimitCheck);
        Assert.Null(hints.ContextHardLimit);
        Assert.False(hints.EnableEmptyResponseRecovery);
        Assert.False(hints.EnableMalformedProtocolRecovery);
        Assert.False(hints.EnableToolDeduplication);
        Assert.Equal(3, hints.MaxRecoveryAttempts);
    }
}

public class QueryRuntimeStateTests
{
    [Fact]
    public void QueryRuntimeState_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var state = new QueryRuntimeState { MaxRounds = 20 };

        // Assert
        Assert.Empty(state.Messages);
        Assert.Equal(0, state.Round);
        Assert.Equal(20, state.MaxRounds);
        Assert.Equal(0, state.TotalToolCalls);
        Assert.Equal(0, state.ZeroToolCallRounds);
        Assert.Equal(0, state.EmptyResponseCount);
        Assert.Equal(0, state.MalformedProtocolCount);
        Assert.Equal(0, state.RecoveryCount);
        Assert.Equal(RuntimeState.None, state.Flags);
        Assert.Null(state.TerminalDetailCode);
        Assert.Empty(state.ExecutedToolSignatures);
        Assert.NotNull(state.Stopwatch);
    }

    [Fact]
    public void QueryRuntimeState_CanTrackToolCalls()
    {
        // Arrange
        var state = new QueryRuntimeState { MaxRounds = 10 };

        // Act
        state.TotalToolCalls = 5;
        state.LastToolSignature = "ivilson_read:/path/to/file";
        state.ExecutedToolSignatures.Add(state.LastToolSignature);

        // Assert
        Assert.Equal(5, state.TotalToolCalls);
        Assert.Single(state.ExecutedToolSignatures);
        Assert.Contains("ivilson_read:/path/to/file", state.ExecutedToolSignatures);
    }

    [Fact]
    public void QueryRuntimeState_CanSetFlags()
    {
        // Arrange
        var state = new QueryRuntimeState { MaxRounds = 10 };

        // Act
        state.Flags = RuntimeState.EmptyResponseRecoveryUsed | RuntimeState.ToolDeduplicationApplied;

        // Assert
        Assert.True(state.Flags.HasFlag(RuntimeState.EmptyResponseRecoveryUsed));
        Assert.True(state.Flags.HasFlag(RuntimeState.ToolDeduplicationApplied));
        Assert.False(state.Flags.HasFlag(RuntimeState.StallDetected));
    }
}

public class QueryRuntimeResultTests
{
    [Fact]
    public void QueryRuntimeResult_RequiredFields_AreSet()
    {
        // Arrange & Act
        var result = new QueryRuntimeResult
        {
            TerminationReason = QueryTerminationReason.Normal,
            TotalRounds = 5,
            TotalToolCalls = 10,
            ZeroToolCallRounds = 0,
            EmptyResponseCount = 0,
            RecoveryCount = 0,
            MalformedProtocolCount = 0,
            FinalText = "Task completed successfully.",
            TotalDurationMs = 5000
        };

        // Assert
        Assert.Equal(QueryTerminationReason.Normal, result.TerminationReason);
        Assert.Equal(5, result.TotalRounds);
        Assert.Equal(10, result.TotalToolCalls);
        Assert.Equal("Task completed successfully.", result.FinalText);
        Assert.Equal(5000, result.TotalDurationMs);
        Assert.NotEqual(Guid.Empty, result.QueryId);
    }

    [Fact]
    public void QueryRuntimeResult_WithOptionalFields_AreSet()
    {
        // Arrange & Act
        var result = new QueryRuntimeResult
        {
            TerminationReason = QueryTerminationReason.MaxRoundsReached,
            TotalRounds = 20,
            TotalToolCalls = 50,
            ZeroToolCallRounds = 2,
            EmptyResponseCount = 1,
            RecoveryCount = 3,
            MalformedProtocolCount = 0,
            FinalText = "",
            FinalThinking = "Let me think about this...",
            TotalPromptTokens = 10000,
            TotalCompletionTokens = 5000,
            TotalDurationMs = 30000,
            Flags = RuntimeState.MaxRoundsWarningIssued,
            TerminalDetailCode = "MAX_ROUNDS_WARNING"
        };

        // Assert
        Assert.Equal(QueryTerminationReason.MaxRoundsReached, result.TerminationReason);
        Assert.Equal("Let me think about this...", result.FinalThinking);
        Assert.Equal(10000, result.TotalPromptTokens);
        Assert.Equal(5000, result.TotalCompletionTokens);
        Assert.True(result.Flags.HasFlag(RuntimeState.MaxRoundsWarningIssued));
        Assert.Equal("MAX_ROUNDS_WARNING", result.TerminalDetailCode);
    }
}

public class QueryRuntimeEventTests
{
    [Fact]
    public void RoundStartedEvent_CreatesCorrectly()
    {
        // Arrange
        var queryId = Guid.NewGuid();

        // Act
        var evt = new RoundStartedEvent(
            Seq: 1,
            QueryId: queryId,
            SessionId: "session-123",
            EntryPoint: QueryLoopEntryPoint.SimpleCodexController,
            Round: 0,
            MaxRounds: 20,
            ContextChars: 1000);

        // Assert
        Assert.Equal(1, evt.Seq);
        Assert.Equal(queryId, evt.QueryId);
        Assert.Equal("session-123", evt.SessionId);
        Assert.Equal(0, evt.Round);
        Assert.Equal(20, evt.MaxRounds);
        Assert.Equal(1000, evt.ContextChars);
    }

    [Fact]
    public void AssistantDeltaEvent_CreatesCorrectly()
    {
        // Arrange
        var queryId = Guid.NewGuid();

        // Act
        var evt = new AssistantDeltaEvent(
            Seq: 100,
            QueryId: queryId,
            SessionId: "session-123",
            EntryPoint: QueryLoopEntryPoint.GatewayMessageProcessor,
            Round: 1,
            Delta: "Hello, world!");

        // Assert
        Assert.Equal(100, evt.Seq);
        Assert.Equal("Hello, world!", evt.Delta);
        Assert.Equal(1, evt.Round);
    }

    [Fact]
    public void ToolCallRequestedEvent_CreatesCorrectly()
    {
        // Arrange
        var queryId = Guid.NewGuid();
        var args = new Dictionary<string, object?>
        {
            ["path"] = "/src/file.cs",
            ["encoding"] = "utf-8"
        };

        // Act
        var evt = new ToolCallRequestedEvent(
            Seq: 200,
            QueryId: queryId,
            SessionId: "session-123",
            EntryPoint: QueryLoopEntryPoint.CodexController,
            Round: 2,
            ToolName: "ivilson_read",
            CallId: "call-456",
            Arguments: args);

        // Assert
        Assert.Equal("ivilson_read", evt.ToolName);
        Assert.Equal("call-456", evt.CallId);
        Assert.Equal(2, evt.Arguments.Count);
        Assert.Equal("/src/file.cs", evt.Arguments["path"]);
    }

    [Fact]
    public void TerminatedEvent_CreatesCorrectly()
    {
        // Arrange
        var queryId = Guid.NewGuid();

        // Act
        var evt = new TerminatedEvent(
            Seq: 999,
            QueryId: queryId,
            SessionId: "session-123",
            EntryPoint: QueryLoopEntryPoint.DefaultCodexKernel,
            Reason: QueryTerminationReason.NoToolCalls,
            TotalRounds: 5,
            TotalToolCalls: 0,
            TotalDurationMs: 3000,
            DetailCode: "NO_ACTION_NEEDED");

        // Assert
        Assert.Equal(QueryTerminationReason.NoToolCalls, evt.Reason);
        Assert.Equal(5, evt.TotalRounds);
        Assert.Equal(0, evt.TotalToolCalls);
        Assert.Equal("NO_ACTION_NEEDED", evt.DetailCode);
    }
}

public class RecoveryModelsTests
{
    [Fact]
    public void QueryTerminalDetailCodes_ResolvesRecoveryExhaustedReason()
    {
        var state = new QueryRuntimeState
        {
            MaxRounds = 3,
            TerminationReason = QueryTerminationReason.RecoveryExhausted,
            EmptyResponseCount = 2,
            Flags = RuntimeState.EmptyResponseRecoveryUsed
        };

        var detailCode = QueryTerminalDetailCodes.Resolve(state);

        Assert.Equal(QueryTerminalDetailCodes.RecoveryExhaustedEmptyResponse, detailCode);
    }

    [Fact]
    public void QueryTerminalDetailCodes_ResolvesUnexecutedWriteIntentBeforeGenericZeroToolCall()
    {
        var state = new QueryRuntimeState
        {
            MaxRounds = 3,
            TerminationReason = QueryTerminationReason.RecoveryExhausted,
            ZeroToolCallRounds = 3,
            Flags = RuntimeState.ZeroToolCallRecoveryUsed | RuntimeState.UnexecutedWriteIntentRecoveryUsed
        };

        var detailCode = QueryTerminalDetailCodes.Resolve(state);

        Assert.Equal(QueryTerminalDetailCodes.RecoveryExhaustedUnexecutedWriteIntent, detailCode);
    }

    [Fact]
    public void QueryTerminalDetailCodes_ResolvesTerminalReasonWithoutState()
    {
        var detailCode = QueryTerminalDetailCodes.Resolve(QueryTerminationReason.MaxRoundsReached);

        Assert.Equal(QueryTerminalDetailCodes.MaxRoundsReached, detailCode);
    }




}

public class ToolExecutionModelsTests
{

    [Fact]
    public void ToolExecutionResult_CreatesCorrectly()
    {
        // Arrange & Act
        var result = new ToolExecutionResult(
            ToolName: "ivilson_read",
            CallId: "call-123",
            Result: "File content...",
            Success: true,
            ResultLength: 100);

        // Assert
        Assert.Equal("ivilson_read", result.ToolName);
        Assert.Equal("call-123", result.CallId);
        Assert.True(result.Success);
        Assert.Equal(100, result.ResultLength);
    }

    [Fact]
    public void ToolExecutionResult_WithException_CreatesCorrectly()
    {
        // Arrange
        var ex = new InvalidOperationException("Tool failed");

        // Act
        var result = new ToolExecutionResult(
            ToolName: "run_command",
            CallId: "call-456",
            Result: "Error: Tool failed",
            Success: false,
            Exception: ex);

        // Assert
        Assert.False(result.Success);
        Assert.NotNull(result.Exception);
        Assert.Equal("Tool failed", result.Exception.Message);
    }
}

public class RuntimeStateEnumTests
{
    [Fact]
    public void RuntimeState_HasExpectedValues()
    {
        // Assert - Verify all expected flag values exist
        Assert.Equal(0, (int)RuntimeState.None);
        Assert.Equal(1, (int)RuntimeState.EmptyResponseRecoveryUsed);
        Assert.Equal(2, (int)RuntimeState.ZeroToolCallRecoveryUsed);
        Assert.Equal(4, (int)RuntimeState.ContextCompactionUsed);
        Assert.Equal(8, (int)RuntimeState.AutoDispatchUsed);
        Assert.Equal(16, (int)RuntimeState.StallDetected);
        Assert.Equal(32, (int)RuntimeState.ContextHardLimitReached);
        Assert.Equal(64, (int)RuntimeState.MalformedProtocolRecoveryUsed);
        Assert.Equal(128, (int)RuntimeState.TransportFailureRecoveryUsed);
        Assert.Equal(2048, (int)RuntimeState.UnexecutedWriteIntentRecoveryUsed);
    }

    [Fact]
    public void RuntimeState_CanCombine()
    {
        // Arrange
        var combined = RuntimeState.EmptyResponseRecoveryUsed |
                       RuntimeState.ToolDeduplicationApplied |
                       RuntimeState.StallDetected;

        // Assert
        Assert.True(combined.HasFlag(RuntimeState.EmptyResponseRecoveryUsed));
        Assert.True(combined.HasFlag(RuntimeState.ToolDeduplicationApplied));
        Assert.True(combined.HasFlag(RuntimeState.StallDetected));
        Assert.False(combined.HasFlag(RuntimeState.ContextHardLimitReached));
    }
}
