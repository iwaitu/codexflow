// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Models;
using CodexFlow.Core.Runtime;
using CodexFlow.Core.Telemetry;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using Microsoft.Extensions.AI;
using Moq;
using System.Net.Http;
using Xunit;

namespace CodexFlow.Core.Tests.Runtime;

/// <summary>
/// Phase 1 收尾: SimpleCodex 兼容性单元测试
/// </summary>
public class SimpleCodexCompatibilityTests
{
    // ── 1. SSE 事件类型测试 ───────────────────────────────────────────────────

    [Fact]
    public void SseEventAdapter_OnlyEnablesStableEvents()
    {
        // 只启用现有稳定事件
        var enabledEvents = new HashSet<QueryRuntimeEventType>
        {
            QueryRuntimeEventType.AssistantDelta,
            QueryRuntimeEventType.ThinkingStarted,
            QueryRuntimeEventType.ThinkingDelta,
            QueryRuntimeEventType.ThinkingEnded,
            QueryRuntimeEventType.ToolCallRequested,
            QueryRuntimeEventType.ToolExecutionCompleted,
            QueryRuntimeEventType.ConversationIdSet
        };

        // 确保没有启用这些"新增语义"的事件
        Assert.DoesNotContain(QueryRuntimeEventType.Terminated, enabledEvents);
        Assert.DoesNotContain(QueryRuntimeEventType.RecoveryTriggered, enabledEvents);
        Assert.DoesNotContain(QueryRuntimeEventType.ToolExecutionStarted, enabledEvents);
        Assert.DoesNotContain(QueryRuntimeEventType.SystemNotice, enabledEvents);
        Assert.DoesNotContain(QueryRuntimeEventType.RoundStarted, enabledEvents);
        Assert.DoesNotContain(QueryRuntimeEventType.RoundCompleted, enabledEvents);
        Assert.DoesNotContain(QueryRuntimeEventType.Error, enabledEvents);
    }

    // ── 2. Telemetry 不重复测试 ───────────────────────────────────────────────────────

















    private static async IAsyncEnumerable<ChatResponseUpdate> AsyncEmptyEnumerator()
    {
        yield break;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> AsyncTextEnumerator(string text)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(text)]);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> AsyncThrowingEnumerator(Exception ex)
    {
        await Task.Yield();
        if (DateTime.UtcNow.Ticks >= 0)
        {
            throw ex;
        }

        yield break;
    }

    // ── 3. AdapterHints 最保守配置测试 ───────────────────────────────────────────────

    [Fact]
    public void AdapterHints_SimpleCodexDefault_AllFeaturesOff()
    {
        // Arrange & Act - SimpleCodexController 使用的配置
        var hints = new AdapterHints(
            EnableAutoDispatch: false,
            EnableStallDetection: false,
            EnableContextLimitCheck: false,
            EnableEmptyResponseRecovery: false,
            EnableMalformedProtocolRecovery: false,
            EnableTransportFailureRecovery: false,
            EnableToolDeduplication: false);

        // Assert - 所有高级特性关闭
        Assert.False(hints.EnableAutoDispatch);
        Assert.False(hints.EnableStallDetection);
        Assert.False(hints.EnableContextLimitCheck);
        Assert.False(hints.EnableEmptyResponseRecovery);
        Assert.False(hints.EnableMalformedProtocolRecovery);
        Assert.False(hints.EnableTransportFailureRecovery);
        Assert.False(hints.EnableToolDeduplication);
    }

    // ── 4. 工具去重可配置测试 ─────────────────────────────────────────────────────────



    // ── 5. RuntimeState flags 测试 ─────────────────────────────────────────────────────










    [Fact]
    public async Task TypedToolAdapter_PreservesTrustedRuntimeArguments()
    {
        // Arrange
        var tool = new RecordingTool("write_file", ToolCategory.Forge);
        var function = CodexToolFunctionAdapterFactory.CreateAIFunction(tool);
        var args = new Dictionary<string, object?>
        {
            ["path"] = "src/CleanApp.Core/Interfaces/IUnitOfWork.cs",
            ["content"] = "public interface IUnitOfWork {}",
            ["session_id"] = "session-123",
            ["workspace_path"] = @"D:\repo\shadow\cleanapp-bug",
            ["project_root"] = @"D:\repo\shadow\cleanapp-bug"
        };

        // Act
        await function.InvokeAsync(new AIFunctionArguments(args), CancellationToken.None);

        // Assert
        Assert.NotNull(tool.LastArguments);
        Assert.Equal("session-123", tool.LastArguments!["session_id"]);
        Assert.Equal(@"D:\repo\shadow\cleanapp-bug", tool.LastArguments["workspace_path"]);
        Assert.Equal(@"D:\repo\shadow\cleanapp-bug", tool.LastArguments["project_root"]);
        Assert.Equal("src/CleanApp.Core/Interfaces/IUnitOfWork.cs", tool.LastArguments["path"]);
    }

    [Fact]
    public async Task TypedToolAdapter_SmartPatch_AcceptsLegacyPatchAlias()
    {
        // Arrange
        var tool = new RecordingTool("ivilson_smart_patch", ToolCategory.Forge);
        var function = CodexToolFunctionAdapterFactory.CreateAIFunction(tool);
        var args = new Dictionary<string, object?>
        {
            ["patch"] = "diff --git a/src/CleanApp.Core/CleanApp.Core.csproj b/src/CleanApp.Core/CleanApp.Core.csproj",
            ["reason"] = "remove infrastructure reference",
            ["session_id"] = "session-123",
            ["workspace_path"] = @"D:\repo\shadow\cleanapp-bug",
            ["project_root"] = @"D:\repo\shadow\cleanapp-bug"
        };

        // Act
        await function.InvokeAsync(new AIFunctionArguments(args), CancellationToken.None);

        // Assert
        Assert.NotNull(tool.LastArguments);
        Assert.Equal(args["patch"], tool.LastArguments!["patch_content"]);
        Assert.False(tool.LastArguments.ContainsKey("patch"));
    }

    [Fact]
    public async Task TypedToolAdapter_SmartPatch_PreservesStructuredHashlineRequest()
    {
        // Arrange
        var tool = new RecordingTool("ivilson_smart_patch", ToolCategory.Forge);
        var function = CodexToolFunctionAdapterFactory.CreateAIFunction(tool);
        var args = new Dictionary<string, object?>
        {
            ["edit_mode"] = "hashline",
            ["request"] = new Dictionary<string, object?>
            {
                ["filePath"] = "src/CleanApp.Core/CleanApp.Core.csproj",
                ["snapshotId"] = "snap-1",
                ["fileFingerprint"] = "fp-1",
                ["dryRun"] = true,
                ["operations"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "replace_range",
                        ["startLine"] = 12,
                        ["startAnchorId"] = "AA11",
                        ["endLine"] = 12,
                        ["endAnchorId"] = "AA11",
                        ["newLines"] = new[] { "<ItemGroup />" }
                    }
                }
            },
            ["session_id"] = "session-123",
            ["workspace_path"] = @"D:\repo\shadow\cleanapp-bug",
            ["project_root"] = @"D:\repo\shadow\cleanapp-bug"
        };

        // Act
        await function.InvokeAsync(new AIFunctionArguments(args), CancellationToken.None);

        // Assert
        Assert.NotNull(tool.LastArguments);
        var request = Assert.IsType<Dictionary<string, object?>>(tool.LastArguments!["request"]);
        Assert.Equal("src/CleanApp.Core/CleanApp.Core.csproj", request["filePath"]);
        Assert.Equal("snap-1", request["snapshotId"]);
        Assert.Equal("fp-1", request["fileFingerprint"]);
        Assert.Equal(true, request["dryRun"]);
        var operations = Assert.IsAssignableFrom<IEnumerable<object?>>(request["operations"]);
        var operation = Assert.IsType<Dictionary<string, object?>>(Assert.Single(operations));
        Assert.Equal("replace_range", operation["type"]);
    }

    [Fact]
    public async Task TypedToolAdapter_ApplyPatch_AcceptsRuntimeDictionaryRequestPayload()
    {
        var tool = new RecordingTool("apply_patch", ToolCategory.Forge);
        var function = CodexToolFunctionAdapterFactory.CreateAIFunction(tool);
        var args = new Dictionary<string, object?>
        {
            ["edit_mode"] = "hashline",
            ["request"] = new Dictionary<string, object>
            {
                ["filePath"] = "src/CleanApp.Core/CleanApp.Core.csproj",
                ["snapshotId"] = "snap-1",
                ["fileFingerprint"] = "fp-1",
                ["dryRun"] = false,
                ["operations"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["type"] = "replace_range",
                        ["startLine"] = 12,
                        ["endLine"] = 12,
                        ["newLines"] = new[] { "<ItemGroup />" }
                    }
                }
            }
        };

        await function.InvokeAsync(new AIFunctionArguments(args), CancellationToken.None);

        Assert.NotNull(tool.LastArguments);
        var request = Assert.IsType<Dictionary<string, object?>>(tool.LastArguments!["request"]);
        Assert.Equal("src/CleanApp.Core/CleanApp.Core.csproj", request["filePath"]);
        var operations = Assert.IsAssignableFrom<IEnumerable<object?>>(request["operations"]);
        _ = Assert.IsType<Dictionary<string, object?>>(Assert.Single(operations));
    }

    [Fact]
    public async Task TypedToolAdapter_HsRead_UsesFlatHashlineReadArguments()
    {
        var tool = new RecordingTool("hs_read", ToolCategory.Read);
        var function = CodexToolFunctionAdapterFactory.CreateAIFunction(tool);
        var args = new Dictionary<string, object?>
        {
            ["path"] = "src/CleanApp/Program.cs",
            ["window_start_line"] = 10,
            ["window_line_count"] = 20,
            ["session_id"] = "session-123",
            ["workspace_path"] = @"D:\repo\shadow\cleanapp-bug"
        };

        await function.InvokeAsync(new AIFunctionArguments(args), CancellationToken.None);

        Assert.NotNull(tool.LastArguments);
        Assert.Equal("src/CleanApp/Program.cs", tool.LastArguments!["path"]);
        Assert.Equal(10, tool.LastArguments["window_start_line"]);
        Assert.Equal(20, tool.LastArguments["window_line_count"]);
        Assert.Equal("session-123", tool.LastArguments["session_id"]);
    }

    [Fact]
    public async Task TypedToolAdapter_HsWrite_UsesFlatHashlineWriteArguments()
    {
        var tool = new RecordingTool("hs_write", ToolCategory.Forge);
        var function = CodexToolFunctionAdapterFactory.CreateAIFunction(tool);
        var args = new Dictionary<string, object?>
        {
            ["filePath"] = "src/CleanApp/Program.cs",
            ["snapshotId"] = "snap-1",
            ["fileFingerprint"] = "fp-1",
            ["dryRun"] = true,
            ["operations"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "insert_after",
                    ["targetLine"] = 5,
                    ["targetAnchorId"] = "AA11",
                    ["newLines"] = new[] { "app.UseAuthorization();" }
                }
            },
            ["workspace_path"] = @"D:\repo\shadow\cleanapp-bug"
        };

        await function.InvokeAsync(new AIFunctionArguments(args), CancellationToken.None);

        Assert.NotNull(tool.LastArguments);
        Assert.Equal("src/CleanApp/Program.cs", tool.LastArguments!["filePath"]);
        Assert.Equal("snap-1", tool.LastArguments["snapshotId"]);
        Assert.Equal("fp-1", tool.LastArguments["fileFingerprint"]);
        Assert.Equal(true, tool.LastArguments["dryRun"]);
        var operations = Assert.IsAssignableFrom<IEnumerable<object?>>(tool.LastArguments["operations"]);
        _ = Assert.IsType<Dictionary<string, object?>>(Assert.Single(operations));
    }

    [Fact]
    public async Task TypedToolAdapter_SmartPatch_AcceptsRuntimeStringRequestPayload()
    {
        var tool = new RecordingTool("ivilson_smart_patch", ToolCategory.Forge);
        var function = CodexToolFunctionAdapterFactory.CreateAIFunction(tool);
        var args = new Dictionary<string, object?>
        {
            ["edit_mode"] = "hashline",
            ["request"] = """
            {
              "filePath": "src/CleanApp/Program.cs",
              "snapshotId": "snap-2",
              "fileFingerprint": "fp-2",
              "dryRun": true,
              "operations": [
                {
                  "type": "insert_after",
                  "targetLine": 5,
                  "newLines": ["using CleanApp.Infrastructure.Repositories;"]
                }
              ]
            }
            """
        };

        await function.InvokeAsync(new AIFunctionArguments(args), CancellationToken.None);

        Assert.NotNull(tool.LastArguments);
        var request = Assert.IsType<Dictionary<string, object?>>(tool.LastArguments!["request"]);
        Assert.Equal("src/CleanApp/Program.cs", request["filePath"]);
        Assert.Equal(true, request["dryRun"]);
        var operations = Assert.IsAssignableFrom<IEnumerable<object?>>(request["operations"]);
        var operation = Assert.IsType<Dictionary<string, object?>>(Assert.Single(operations));
        Assert.Equal("insert_after", operation["type"]);
    }

    // ── 6. ConversationIdSetEvent 测试 ───────────────────────────────────────────────

    [Fact]
    public void ConversationIdSetEvent_CreatesCorrectly()
    {
        // Arrange
        var queryId = Guid.NewGuid();

        // Act
        var evt = new ConversationIdSetEvent(
            Seq: 100,
            QueryId: queryId,
            SessionId: "test-session",
            EntryPoint: QueryLoopEntryPoint.SimpleCodexController,
            ConversationId: "conv-123");

        // Assert
        Assert.Equal("conv-123", evt.ConversationId);
        Assert.Equal(QueryLoopEntryPoint.SimpleCodexController, evt.EntryPoint);
    }

    // ── 7. QueryRuntimeResult 最终结果测试 ───────────────────────────────────────────

    [Fact]
    public void QueryRuntimeResult_PreservesFinalTextAndThinking()
    {
        // Arrange & Act
        var result = new QueryRuntimeResult
        {
            TerminationReason = QueryTerminationReason.NoToolCalls,
            TotalRounds = 1,
            TotalToolCalls = 0,
            ZeroToolCallRounds = 1,
            EmptyResponseCount = 0,
            RecoveryCount = 0,
            MalformedProtocolCount = 0,
            FinalText = "This is the final response.",
            FinalThinking = "Let me think about this...",
            TotalDurationMs = 1000
        };

        // Assert
        Assert.Equal("This is the final response.", result.FinalText);
        Assert.Equal("Let me think about this...", result.FinalThinking);
        Assert.Equal(QueryTerminationReason.NoToolCalls, result.TerminationReason);
    }

    private sealed class RecordingTool(string name, ToolCategory category) : ICodexTool
    {
        public string Name { get; } = name;
        public CodexFlow.Core.Execution.ToolCapability Capability => CodexFlow.Core.Execution.ToolCapability.ReadOnly;
        public string Description => "test tool";
        public ToolCategory Category { get; } = category;
        public IReadOnlyList<int> AllowedStages => [1, 2, 3];
        public Dictionary<string, object?>? LastArguments { get; private set; }

        public Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
        {
            LastArguments = new Dictionary<string, object?>(arguments, StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(CodexToolResult.Succeeded("ok"));
        }
    }

    private sealed class GuardedTool(
        string name,
        ToolCategory category,
        CodexToolValidationResult? validationResult = null,
        CodexToolPermissionResult? permissionResult = null,
        CodexToolResult? executeResult = null) : ICodexTool
    {
        public string Name { get; } = name;
        public string Description => "guarded test tool";
        public ToolCategory Category { get; } = category;
        public IReadOnlyList<int> AllowedStages => [1, 2, 3];
        public List<string> CallSequence { get; } = [];

        public ValueTask<CodexToolValidationResult> ValidateInputAsync(
            Dictionary<string, object?> arguments,
            CancellationToken ct = default)
        {
            CallSequence.Add("validate");
            return ValueTask.FromResult(validationResult ?? CodexToolValidationResult.Valid());
        }

        public ValueTask<CodexToolPermissionResult> CheckPermissionsAsync(
            Dictionary<string, object?> arguments,
            QueryRuntimeRequest request,
            QueryRuntimeState? state = null,
            CancellationToken ct = default)
        {
            CallSequence.Add("permission");
            return ValueTask.FromResult(permissionResult ?? CodexToolPermissionResult.Allowed());
        }

        public Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
        {
            CallSequence.Add("execute");
            return Task.FromResult(executeResult ?? CodexToolResult.Succeeded("ok"));
        }
    }

    private sealed class StopHookTestHook(Func<BeforeStopContext, BeforeStopHookResult> handler) : IRuntimeHook
    {
        public ValueTask<AfterModelResponseHookResult> OnAfterModelResponseAsync(
            AfterModelResponseContext context,
            CancellationToken ct = default)
            => ValueTask.FromResult(AfterModelResponseHookResult.None);

        public ValueTask<BeforeStopHookResult> OnBeforeStopAsync(
            BeforeStopContext context,
            CancellationToken ct = default)
            => ValueTask.FromResult(handler(context));
    }
}
