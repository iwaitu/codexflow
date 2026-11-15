// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using Xunit;
using Newtonsoft.Json;
using System.Runtime.CompilerServices;

namespace CodexFlow.Core.Tests.Agents;

public class ValidatorTests
{
    private static readonly SemaphoreSlim LogMutex = new(1, 1);

    [Fact]
    public async Task ValidateAsync_ShouldReturnSuccess_WhenModelConfirmsCompletion()
    {
        // Arrange
        var mockChatClient = new Mock<IChatClient>();
        var toolRegistry = new DefaultToolRegistry(NullLogger<DefaultToolRegistry>.Instance);
        var validator = new DefaultCodexValidator(mockChatClient.Object, toolRegistry, NullLogger<DefaultCodexValidator>.Instance);
        
        var session = new CodexSession { Id = "s1", ProjectSummary = "Context" };
        var task = new CodexTask { Id = "t1", Title = "Test Task", ResultNotes = "Code is written" };

        var mockJson = "{\"Success\": true, \"Summary\": \"Verified\", \"Issues\": []}";

        mockChatClient.Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> _, ChatOptions _, CancellationToken ct) => StreamText(mockJson, ct));

        // Act
        var result = await validator.ValidateAsync(session, task);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("Verified", result.Summary);
    }

    // The context length is controlled (and compressed) for the whole run; a hard output limit here cuts a reasoning model off in the middle of its
    // thinking and the verdict never arrives. The main kernel leaves the output unlimited for the same reason.
    [Fact]
    public async Task ValidateAsync_SetsNoOutputTokenLimit()
    {
        var mockChatClient = new Mock<IChatClient>();
        var validator = new DefaultCodexValidator(mockChatClient.Object, new DefaultToolRegistry(NullLogger<DefaultToolRegistry>.Instance), NullLogger<DefaultCodexValidator>.Instance);
        ChatOptions? sent = null;
        mockChatClient.Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions, CancellationToken>((_, options, _) => sent = options)
            .Returns((IEnumerable<ChatMessage> _, ChatOptions _, CancellationToken ct) => StreamText("{\"Success\": true, \"Summary\": \"Verified\", \"Issues\": []}", ct));

        await validator.ValidateAsync(new CodexSession { Id = "s1", ProjectSummary = "Context" }, new CodexTask { Id = "t1", Title = "Test Task", ResultNotes = "Code is written" });

        Assert.NotNull(sent);
        Assert.Null(sent!.MaxOutputTokens);
    }

    [Fact]
    public async Task ValidateAsync_ShouldCallAnalyzeCode_WhenModelRequestsIt()
    {
        // Arrange
        var mockChatClient = new Mock<IChatClient>();
        var toolRegistry = new DefaultToolRegistry(NullLogger<DefaultToolRegistry>.Instance);
        var mockAnalyzeTool = new Mock<ICodexTool>();
        mockAnalyzeTool.SetupGet(t => t.Capability).Returns(CodexFlow.Core.Execution.ToolCapability.ReadOnly);
        mockAnalyzeTool.Setup(t => t.Name).Returns("analyze_code");
        mockAnalyzeTool.Setup(t => t.ExecuteAsync(It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync(CodexToolResult.Succeeded("✅ No errors"));
        
        toolRegistry.RegisterTool(mockAnalyzeTool.Object);
        
        var validator = new DefaultCodexValidator(mockChatClient.Object, toolRegistry, NullLogger<DefaultCodexValidator>.Instance);
        var session = new CodexSession { Id = "s1" };
        var task = new CodexTask { Id = "t1" };

        var finalJson = "{\"Success\": true, \"Summary\": \"Code is clean\", \"Issues\": []}";

        var invocationCount = 0;
        mockChatClient.Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> _, ChatOptions _, CancellationToken ct) =>
            {
                invocationCount++;
                return invocationCount == 1
                    ? StreamFunctionCall("c1", "analyze_code", new Dictionary<string, object?> { ["code"] = "int a=1;" }, ct)
                    : StreamText(finalJson, ct);
            });

        // Act
        var result = await validator.ValidateAsync(session, task);

        // Assert
        Assert.True(result.Success);
        mockAnalyzeTool.Verify(t => t.ExecuteAsync(It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ValidateAsync_ShouldSurfaceQualityWarnings_FromBuildLogs()
    {
        await LogMutex.WaitAsync();
        try
        {
            var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logsDir);
            var toolLogPath = Path.Combine(logsDir, "toolscall.log");
            var serviceLogPath = Path.Combine(logsDir, "service-2099-01-01.log");
            var originalToolLog = File.Exists(toolLogPath) ? await File.ReadAllTextAsync(toolLogPath) : null;
            var originalServiceLog = File.Exists(serviceLogPath) ? await File.ReadAllTextAsync(serviceLogPath) : null;

            try
            {
                await File.WriteAllTextAsync(toolLogPath, """
2026-03-11 10:00:00.0000|INFO|ValidatorTest|TOOLCALL|tool=run_command|source=execute|elapsedMs=1200|failed=False|args={}|output=✅ $ dotnet build
2026-03-11 10:00:01.0000|INFO|ValidatorTest|D:\temp\Sample\AppFile.cs(5,23): warning CS0108: AppFile.Id hides BaseEntity.Id [D:\temp\Sample\Sample.csproj]
""");
                await File.WriteAllTextAsync(serviceLogPath, """
2026-03-11 10:00:02.0000|INFO|CodexFlow.Core.Agents.DefaultCodexValidator|Validating task: T-warning - Test Task
""");

                var mockChatClient = new Mock<IChatClient>();
                var toolRegistry = new DefaultToolRegistry(NullLogger<DefaultToolRegistry>.Instance);
                var validator = new DefaultCodexValidator(mockChatClient.Object, toolRegistry, NullLogger<DefaultCodexValidator>.Instance);
                var session = new CodexSession { Id = "s-warning", ProjectSummary = "Context", WorkspacePath = TestConstants.GetWorkspacePath() };
                var task = new CodexTask { Id = "T-warning", Title = "Build task", Description = "需要 dotnet build", ResultNotes = "Build completed" };

                mockChatClient.Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
                    .Returns((IEnumerable<ChatMessage> _, ChatOptions _, CancellationToken ct) => StreamText("{\"Success\": true, \"Summary\": \"Verified\", \"Issues\": []}", ct));

                var result = await validator.ValidateAsync(session, task);

                Assert.True(result.Success);
                Assert.True(result.HasQualityWarnings);
                Assert.NotNull(result.WarningDetails);
                Assert.Contains(result.WarningDetails!, warning => warning.Contains("warning CS0108", StringComparison.OrdinalIgnoreCase));
                Assert.Contains("质量门禁提示", result.Summary, StringComparison.Ordinal);
            }
            finally
            {
                if (originalToolLog == null)
                {
                    File.Delete(toolLogPath);
                }
                else
                {
                    await File.WriteAllTextAsync(toolLogPath, originalToolLog);
                }

                if (originalServiceLog == null)
                {
                    File.Delete(serviceLogPath);
                }
                else
                {
                    await File.WriteAllTextAsync(serviceLogPath, originalServiceLog);
                }
            }
        }
        finally
        {
            LogMutex.Release();
        }
    }

    [Fact]
    public async Task ValidateAsync_ShouldEmbedToolCatalogInPrompt()
    {
        var mockChatClient = new Mock<IChatClient>();
        var toolRegistry = new DefaultToolRegistry(NullLogger<DefaultToolRegistry>.Instance);
        var validator = new DefaultCodexValidator(mockChatClient.Object, toolRegistry, NullLogger<DefaultCodexValidator>.Instance);

        var alwaysOnTool = new Mock<ICodexTool>();
        alwaysOnTool.SetupGet(t => t.Capability).Returns(CodexFlow.Core.Execution.ToolCapability.ReadOnly);
        alwaysOnTool.Setup(t => t.Name).Returns("analyze_code");
        alwaysOnTool.Setup(t => t.Description).Returns("Analyze code for issues");
        alwaysOnTool.Setup(t => t.Category).Returns(ToolCategory.Analysis);
        alwaysOnTool.Setup(t => t.AllowedStages).Returns(Array.Empty<int>());
        toolRegistry.RegisterTool(alwaysOnTool.Object, CodexFlow.Core.Abstractions.ToolLoading.AlwaysOn);

        var session = new CodexSession { Id = "s-validator", ProjectSummary = "Context" };
        var task = new CodexTask { Id = "t-validator", Title = "Validate", ResultNotes = "Completed" };

        var capturedMessages = new List<ChatMessage>();
        mockChatClient.Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions, CancellationToken>((messages, _, _) =>
            {
                capturedMessages = messages.ToList();
            })
            .Returns((IEnumerable<ChatMessage> _, ChatOptions _, CancellationToken ct) => StreamText("{\"Success\": true, \"Summary\": \"Verified\", \"Issues\": []}", ct));

        var result = await validator.ValidateAsync(session, task);

        Assert.True(result.Success);
        var firstUserPrompt = capturedMessages.First().Text ?? string.Empty;
        Assert.Contains("## 工具使用准则", firstUserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("## 工具发现", firstUserPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsync_ShouldUseDeterministicFallback_WhenFinalStreamingContentIsEmpty()
    {
        using var tempDir = new TempDir();
        var createdFile = Path.Combine(tempDir.Path, "src", "CleanApp.Infrastructure", "Services", "MongoFileService.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(createdFile)!);
        await File.WriteAllTextAsync(createdFile, "namespace CleanApp.Infrastructure.Services;");

        var mockChatClient = new Mock<IChatClient>();
        var toolRegistry = new DefaultToolRegistry(NullLogger<DefaultToolRegistry>.Instance);
        var validator = new DefaultCodexValidator(mockChatClient.Object, toolRegistry, NullLogger<DefaultCodexValidator>.Instance);

        var session = new CodexSession
        {
            Id = "s-empty-final",
            WorkspacePath = tempDir.Path
        };

        var task = new CodexTask
        {
            Id = "TASK_ARCH_003",
            Title = "[Infrastructure] 迁移 MongoFileService",
            Description = "创建 `src/CleanApp.Infrastructure/Services/MongoFileService.cs` 并执行 dotnet build 验证编译通过。"
        };
        task.ReplaceRequiredArtifacts(
        [
            new("file_exists", "src/CleanApp.Infrastructure/Services/MongoFileService.cs")
        ]);
        task.ExecutionEvidence = new TaskExecutionEvidenceResult(
            ChangedFiles: ["src/CleanApp.Infrastructure/Services/MongoFileService.cs"],
            CreatedFiles: ["src/CleanApp.Infrastructure/Services/MongoFileService.cs"],
            DeletedFiles: [],
            HasSuccessfulBuildEvidence: true,
            HasSuccessfulTestEvidence: false,
            AssertionResults: [],
            TotalToolCalls: 2,
            WriteToolCalls: 1);

        mockChatClient.Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> _, ChatOptions _, CancellationToken ct) => StreamEmptyAssistantResponse(ct));

        var result = await validator.ValidateAsync(session, task);

        Assert.True(result.Success);
        Assert.True(result.IsFallback);
        Assert.Contains("规则兜底验证通过", result.Summary, StringComparison.Ordinal);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamEmptyAssistantResponse(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        yield return new ChatResponseUpdate(ChatRole.Assistant, []);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamText(
        string text,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(text)]);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamFunctionCall(
        string callId,
        string toolName,
        IDictionary<string, object?> arguments,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent(callId, toolName, arguments)]);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"validator-empty-final-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }
}
