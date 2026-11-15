// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.VllmChatClient.Kimi;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using CodexFlow.Core.Runtime;
using CodexFlow.Core.Telemetry;
using Xunit;
using System.Runtime.CompilerServices;

namespace CodexFlow.Core.Tests.Agents;

public class PlannerTests
{
    [Fact]
    public async Task GeneratePlanAsync_ShouldReturnTaskList_WhenModelOutputsValidJson()
    {
        // Arrange
        var mockChatClient = new Mock<IChatClient>();
        var planner = new DefaultCodexPlanner(mockChatClient.Object, NullLogger<DefaultCodexPlanner>.Instance);
        var session = new CodexSession { Id = "session-1", ProjectSummary = "Simple API", CurrentStage = 3 };
        
        var mockJson = @"
        [
            {
                ""Id"": ""T1"",
                ""Title"": ""Step 1"",
                ""Description"": ""Desc 1"",
                ""Dependencies"": [],
                ""StageId"": 3
            },
            {
                ""Id"": ""T2"",
                ""Title"": ""Step 2"",
                ""Description"": ""Desc 2"",
                ""Dependencies"": [""T1""],
                ""StageId"": 3
            }
        ]";

        mockChatClient.Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> _, ChatOptions _, CancellationToken ct) => StreamText(mockJson, ct));

        // Act
        var result = await planner.GeneratePlanAsync(session, "Create a hello world api");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("T1", result[0].Id);
        Assert.Equal("T2", result[1].Id);
        Assert.Contains("T1", result[1].Dependencies);
        Assert.All(result, t => Assert.Equal(CodexTaskStatus.Pending, t.Status));
    }

    [Fact]
    public async Task GeneratePlanAsync_DoesNotWarnAboutSanitization_WhenModelReturnsTheContractObject()
    {
        // {"tasks":[...]} is the shape the schema asks for; unwrapping it is not a repair and must not be reported as one.
        var logger = new WarningCapturingLogger();
        var mockChatClient = new Mock<IChatClient>();
        var planner = new DefaultCodexPlanner(mockChatClient.Object, logger);
        var session = new CodexSession { Id = "session-1", ProjectSummary = "Simple API", CurrentStage = 3 };
        const string contractJson = "{\"tasks\":[{\"Id\":\"T1\",\"Title\":\"Step 1\",\"Description\":\"Desc 1\",\"Dependencies\":[],\"StageId\":3}]}";
        mockChatClient.Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> _, ChatOptions _, CancellationToken ct) => StreamText(contractJson, ct));

        var result = await planner.GeneratePlanAsync(session, "Create a hello world api");

        Assert.Single(result);
        Assert.DoesNotContain(logger.Warnings, message => message.Contains("sanitization", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GeneratePlanAsync_WarnsAboutSanitization_WhenThePlanJsonHadToBeRepaired()
    {
        var logger = new WarningCapturingLogger();
        var mockChatClient = new Mock<IChatClient>();
        var planner = new DefaultCodexPlanner(mockChatClient.Object, logger);
        var session = new CodexSession { Id = "session-1", ProjectSummary = "Simple API", CurrentStage = 3 };
        const string trailingCommaJson = "{\"tasks\":[{\"Id\":\"T1\",\"Title\":\"Step 1\",\"Description\":\"Desc 1\",\"Dependencies\":[],\"StageId\":3,}]}";
        mockChatClient.Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> _, ChatOptions _, CancellationToken ct) => StreamText(trailingCommaJson, ct));

        var result = await planner.GeneratePlanAsync(session, "Create a hello world api");

        Assert.Single(result);
        Assert.Contains(logger.Warnings, message => message.Contains("sanitization", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class WarningCapturingLogger : Microsoft.Extensions.Logging.ILogger<DefaultCodexPlanner>
    {
        public List<string> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    [Fact]
    public async Task GeneratePlanAsync_ShouldRecoverFromInvalidEscapesInsideJsonStrings()
    {
        var mockChatClient = new Mock<IChatClient>();
        var planner = new DefaultCodexPlanner(mockChatClient.Object, NullLogger<DefaultCodexPlanner>.Instance);
        var session = new CodexSession { Id = "session-2", ProjectSummary = "Simple API", CurrentStage = 3 };

        var invalidJson = """
        [
          {
            "Id": "T1",
            "TaskType": "code",
            "Title": "Step 1",
            "Description": "第一行\n- 非法转义：\- 这里原本会触发 JsonReaderException",
            "Dependencies": [],
            "StageId": 3,
            "RiskLevel": "Low",
            "ComplexityLevel": 1
          }
        ]
        """;

        mockChatClient.Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> _, ChatOptions _, CancellationToken ct) => StreamText(invalidJson, ct));

        var result = await planner.GeneratePlanAsync(session, "Repair planner JSON");

        Assert.Single(result);
        Assert.Equal("T1", result[0].Id);
        Assert.Contains("非法转义：-", result[0].Description);
        Assert.All(result, t => Assert.Equal(CodexTaskStatus.Pending, t.Status));
    }

    [Fact]
    public async Task GeneratePlanAsync_ShouldRecoverWhenPlannerOmitsObjectOpenBracesInArrays()
    {
        var mockChatClient = new Mock<IChatClient>();
        var planner = new DefaultCodexPlanner(mockChatClient.Object, NullLogger<DefaultCodexPlanner>.Instance);
        var session = new CodexSession { Id = "session-missing-braces", ProjectSummary = "Simple API", CurrentStage = 3 };

        var malformedJson = """
        [
          "Id": "T1",
          "TaskType": "code",
          "Title": "修复上传流程",
          "Description": "补偿机制修复",
          "Dependencies": [],
          "RequiredArtifacts": [
            "Type": "file_contains", "Path": "src/App/FileService.cs", "Text": "UploadStatus" }
          ],
          "ForbiddenStates": [
            "Type": "file_contains", "Path": "src/App/FileService.cs", "Text": "fileStream.Length" }
          ],
          "ChecklistItems": [
            "Id": "CHK_001", "Text": "修改 FileService", "Status": "Pending" },
            "Id": "CHK_002", "Text": "执行 dotnet build", "Status": "Pending" }
          ],
          "StageId": 3,
          "RiskLevel": "High",
          "ComplexityLevel": 3,
          "UnsafeIfDependencyFallbackPassed": true
        }
        ]
        """;

        mockChatClient.Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> _, ChatOptions _, CancellationToken ct) => StreamText(malformedJson, ct));

        var result = await planner.GeneratePlanAsync(session, "Repair upload flow");

        var task = Assert.Single(result);
        Assert.Equal("T1", task.Id);
        Assert.Contains(task.RequiredArtifacts, artifact =>
            artifact.Path == "src/App/FileService.cs" &&
            artifact.Text == "UploadStatus");
        Assert.Equal(2, task.ChecklistItems.Count);
        Assert.Equal("CHK_001", task.ChecklistItems[0].Id);
        Assert.Equal(CodexTaskStatus.Pending, task.Status);
    }

    [Fact]
    public async Task GeneratePlanAsync_ShouldRecoverWhenPlannerOmitsLastObjectCloseBraceInArrays()
    {
        var mockChatClient = new Mock<IChatClient>();
        var planner = new DefaultCodexPlanner(mockChatClient.Object, NullLogger<DefaultCodexPlanner>.Instance);
        var session = new CodexSession { Id = "session-missing-last-brace", ProjectSummary = "CleanApp", CurrentStage = 3 };

        var malformedJson = """
        [
          "Id": "TASK_BUG_001",
          "TaskType": "code",
          "Title": "[Core/Infra] 修复 FileService 上传与删除逻辑缺陷",
          "Description": "修复上传删除逻辑",
          "Dependencies": [],
          "RequiredArtifacts": [
            "Type": "file_contains", "Path": "src/CleanApp.Core/Services/FileService.cs", "Text": "stream.Length" },
            "Type": "file_contains", "Path": "src/CleanApp.Core/Services/FileService.cs", "Text": "catch"
          ],
          "ForbiddenStates": [],
          "ChecklistItems": [
            "Id": "CHK_001", "Text": "读取 FileService.cs", "Status": "Pending" },
            "Id": "CHK_002", "Text": "执行 dotnet build", "Status": "Pending"
          ],
          "StageId": 3,
          "RiskLevel": "High",
          "ComplexityLevel": 3,
          "UnsafeIfDependencyFallbackPassed": true
        ]
        """;

        mockChatClient.Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> _, ChatOptions _, CancellationToken ct) => StreamText(malformedJson, ct));

        var result = await planner.GeneratePlanAsync(session, "Repair cleanapp bugs");

        var task = Assert.Single(result);
        Assert.Equal("TASK_BUG_001", task.Id);
        Assert.Equal(2, task.RequiredArtifacts.Count);
        Assert.Equal(2, task.ChecklistItems.Count);
        Assert.Equal("CHK_002", task.ChecklistItems[1].Id);
        Assert.Equal(CodexTaskStatus.Pending, task.Status);
    }

    // ForbiddenPaths must survive the planner's JSON: a list (even empty) is the authority, a missing field means an older plan.
    [Fact]
    public async Task GeneratePlanAsync_CarriesForbiddenPaths_ThroughToTheTask_PresentEmptyAndAbsent()
    {
        var mockChatClient = new Mock<IChatClient>();
        var planner = new DefaultCodexPlanner(mockChatClient.Object, NullLogger<DefaultCodexPlanner>.Instance);
        var session = new CodexSession { Id = "session-forbidden-paths", ProjectSummary = "Simple API", CurrentStage = 3 };
        const string json = """
        [
          { "Id": "T1", "TaskType": "code", "Title": "a", "Description": "d", "Dependencies": [], "Outputs": ["package.json"], "ForbiddenPaths": ["src/Domain/", "package-lock.json"], "StageId": 3, "RiskLevel": "Low", "ComplexityLevel": 1 },
          { "Id": "T2", "TaskType": "code", "Title": "b", "Description": "d", "Dependencies": [], "Outputs": ["package.json"], "ForbiddenPaths": [], "StageId": 3, "RiskLevel": "Low", "ComplexityLevel": 1 },
          { "Id": "T3", "TaskType": "code", "Title": "c", "Description": "d", "Dependencies": [], "Outputs": ["package.json"], "StageId": 3, "RiskLevel": "Low", "ComplexityLevel": 1 }
        ]
        """;
        mockChatClient.Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> _, ChatOptions _, CancellationToken ct) => StreamText(json, ct));

        var result = await planner.GeneratePlanAsync(session, "x");

        Assert.Equal(["src/Domain/", "package-lock.json"], result[0].ForbiddenPaths);
        Assert.NotNull(result[1].ForbiddenPaths);
        Assert.Empty(result[1].ForbiddenPaths!);
        Assert.Null(result[2].ForbiddenPaths);
    }

    [Fact]
    public async Task GeneratePlanAsync_ShouldPreservePlannerProvidedChecklistItems()
    {
        var mockChatClient = new Mock<IChatClient>();
        var planner = new DefaultCodexPlanner(mockChatClient.Object, NullLogger<DefaultCodexPlanner>.Instance);
        var session = new CodexSession { Id = "session-3", ProjectSummary = "Simple API", CurrentStage = 3 };

        var mockJson = """
        [
          {
            "Id": "T1",
            "TaskType": "code",
            "Title": "迁移 MongoFileService",
            "Description": "## 任务执行顺序\n1. 这段描述不应覆盖已有 checklist\n2. 这段描述不应覆盖已有 checklist",
            "Dependencies": [],
            "ChecklistItems": [
              { "Id": "CHK_001", "Text": "创建 src/CleanApp.Infrastructure/Services/MongoFileService.cs", "Status": "Pending" },
              { "Id": "CHK_002", "Text": "更新 src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj", "Status": "Pending" },
              { "Id": "CHK_003", "Text": "执行 dotnet build", "Status": "Pending" }
            ],
            "StageId": 3,
            "RiskLevel": "High",
            "ComplexityLevel": 3
          }
        ]
        """;

        mockChatClient.Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> _, ChatOptions _, CancellationToken ct) => StreamText(mockJson, ct));

        var result = await planner.GeneratePlanAsync(session, "迁移 MongoFileService");

        var task = Assert.Single(result);
        Assert.Equal(3, task.ChecklistItems.Count);
        Assert.Equal("CHK_001", task.ChecklistItems[0].Id);
        Assert.Equal("创建 src/CleanApp.Infrastructure/Services/MongoFileService.cs", task.ChecklistItems[0].Text);
        Assert.Equal("更新 src/CleanApp.Infrastructure/CleanApp.Infrastructure.csproj", task.ChecklistItems[1].Text);
        Assert.Equal("执行 dotnet build", task.ChecklistItems[2].Text);
    }

    [Fact]
    public async Task GeneratePlanAsync_ShouldInstructPlannerToEmitChecklistItems()
    {
        var mockChatClient = new Mock<IChatClient>();
        var planner = new DefaultCodexPlanner(mockChatClient.Object, NullLogger<DefaultCodexPlanner>.Instance);
        var session = new CodexSession { Id = "session-4", ProjectSummary = "Simple API", CurrentStage = 3 };

        mockChatClient.Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> _, ChatOptions _, CancellationToken ct) => StreamText("[]", ct));

        await planner.GeneratePlanAsync(session, "Create a hello world api");

        mockChatClient.Verify(c => c.GetStreamingResponseAsync(
            It.Is<IEnumerable<ChatMessage>>(messages =>
                messages.Any(msg =>
                    msg.Role == ChatRole.System &&
                    msg.Text != null &&
                    msg.Text.Contains("ChecklistItems", StringComparison.Ordinal) &&
                    msg.Text.Contains("执行 dotnet build", StringComparison.Ordinal))),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task GeneratePlanAsync_ShouldInstructPlannerToCoverProgramAndCsprojAssertions()
    {
        var mockChatClient = new Mock<IChatClient>();
        var planner = new DefaultCodexPlanner(mockChatClient.Object, NullLogger<DefaultCodexPlanner>.Instance);
        var session = new CodexSession { Id = "session-5", ProjectSummary = "Simple API", CurrentStage = 3 };

        mockChatClient.Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> _, ChatOptions _, CancellationToken ct) => StreamText("[]", ct));

        await planner.GeneratePlanAsync(session, "Refactor project references");

        mockChatClient.Verify(c => c.GetStreamingResponseAsync(
            It.Is<IEnumerable<ChatMessage>>(messages =>
                messages.Any(msg =>
                    msg.Role == ChatRole.System &&
                    msg.Text != null &&
                    msg.Text.Contains("覆盖性规则", StringComparison.Ordinal) &&
                    msg.Text.Contains("Program.cs", StringComparison.Ordinal) &&
                    msg.Text.Contains("*.csproj", StringComparison.Ordinal) &&
                    msg.Text.Contains("TASK_ARCH_002", StringComparison.Ordinal) &&
                    msg.Text.Contains("GridFS", StringComparison.Ordinal) &&
                    msg.Text.Contains("interface IFileRepository<", StringComparison.Ordinal))),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task GeneratePlanAsync_ShouldRequestJsonSchemaResponseFormat()
    {
        var mockChatClient = new Mock<IChatClient>();
        var planner = new DefaultCodexPlanner(mockChatClient.Object, NullLogger<DefaultCodexPlanner>.Instance);
        var session = new CodexSession { Id = "session-schema-1", ProjectSummary = "Simple API", CurrentStage = 3 };
        ChatOptions? capturedOptions = null;
        string? projectionPrompt = null;

        mockChatClient.Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions, CancellationToken>((messages, options, _) => { capturedOptions = options; projectionPrompt = messages.Last().Text; })
            .Returns((IEnumerable<ChatMessage> _, ChatOptions _, CancellationToken ct) => StreamText("[]", ct));

        await planner.GeneratePlanAsync(session, "Create a hello world api");

        Assert.NotNull(capturedOptions);
        Assert.Contains("ChecklistItems", projectionPrompt);
        Assert.Contains("Create a hello world api", projectionPrompt);
        Assert.Contains("additionalProperties", projectionPrompt);
        var vllmOptions = Assert.IsType<VllmChatOptions>(capturedOptions);
        Assert.False(vllmOptions.ThinkingEnabled);
        var responseFormat = Assert.IsType<ChatResponseFormatJson>(capturedOptions!.ResponseFormat);
        Assert.Equal("codex_task_plan", responseFormat.SchemaName);
        Assert.NotNull(responseFormat.Schema);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, responseFormat.Schema!.Value.ValueKind);
        // An object root: strict structured-output providers (OpenAI, Anthropic) reject an array root.
        Assert.Equal("object", responseFormat.Schema.Value.GetProperty("type").GetString());
        var tasksSchema = responseFormat.Schema.Value.GetProperty("properties").GetProperty("tasks");
        Assert.Equal("array", tasksSchema.GetProperty("type").GetString());
        var itemSchema = tasksSchema.GetProperty("items");
        Assert.Equal("object", itemSchema.GetProperty("type").GetString());
        Assert.True(itemSchema.GetProperty("properties").TryGetProperty("RequiredArtifacts", out _));
        Assert.True(itemSchema.GetProperty("properties").TryGetProperty("ChecklistItems", out _));
    }

    [Fact]
    public async Task GeneratePlanAsync_ShouldAcceptWrappedTasksPayload()
    {
        var mockChatClient = new Mock<IChatClient>();
        var planner = new DefaultCodexPlanner(mockChatClient.Object, NullLogger<DefaultCodexPlanner>.Instance);
        var session = new CodexSession { Id = "session-schema-wrapper", ProjectSummary = "Simple API", CurrentStage = 3 };
        const string json = """
        {
          "tasks": [
            {
              "Id": "T1",
              "TaskType": "code",
              "Title": "Step 1",
              "Description": "Desc 1",
              "Dependencies": [],
              "StageId": 3,
              "RiskLevel": "Low",
              "ComplexityLevel": 1
            }
          ]
        }
        """;

        mockChatClient.Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> _, ChatOptions _, CancellationToken ct) => StreamText(json, ct));

        var result = await planner.GeneratePlanAsync(session, "Create a hello world api");

        var task = Assert.Single(result);
        Assert.Equal("T1", task.Id);
        Assert.Equal(CodexTaskStatus.Pending, task.Status);
    }

    [Fact]
    public async Task GeneratePlanAsync_ShouldUseRuntimeEngine_WhenAvailable()
    {
        var mockChatClient = new Mock<IChatClient>(MockBehavior.Strict);
        var mockRuntime = new Mock<IQueryRuntimeEngine>();
        var mockSummaryPublisher = new Mock<IPlannerSummaryPublisher>();
        var planner = new DefaultCodexPlanner(
            mockChatClient.Object,
            NullLogger<DefaultCodexPlanner>.Instance,
            llmExecutor: null,
            queryRuntimeEngine: mockRuntime.Object,
            summaryPublisher: mockSummaryPublisher.Object);
        var session = new CodexSession { Id = "session-runtime-1", ProjectSummary = "Simple API", CurrentStage = 3 };
        const string json = """
        [
          {
            "Id": "T1",
            "TaskType": "code",
            "Title": "Step 1",
            "Description": "Desc 1",
            "Dependencies": [],
            "StageId": 3,
            "RiskLevel": "Low",
            "ComplexityLevel": 1
          }
        ]
        """;

        mockRuntime
            .Setup(r => r.ExecuteAsync(
                It.Is<QueryRuntimeRequest>(req =>
                    req.EntryPoint == QueryLoopEntryPoint.PlanWorker &&
                    req.EnableTools == false &&
                    req.MaxRounds == 1),
                It.IsAny<IQueryRuntimeEventSink>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryRuntimeResult
            {
                TerminationReason = QueryTerminationReason.NoToolCalls,
                TotalRounds = 1,
                TotalToolCalls = 0,
                ZeroToolCallRounds = 1,
                EmptyResponseCount = 0,
                RecoveryCount = 0,
                MalformedProtocolCount = 0,
                FinalText = json,
                FinalThinking = "planner runtime thinking",
                TotalDurationMs = 123
            });

        var result = await planner.GeneratePlanAsync(session, "Create a hello world api");

        Assert.Single(result);
        Assert.Equal("T1", result[0].Id);
        mockSummaryPublisher.Verify(p => p.PublishAsync(
            session,
            It.Is<PlannerSummaryUpdate>(u =>
                u.Kind == PlannerSummaryKind.Completed &&
                u.TaskCount == 1 &&
                u.Phase == "planning_completed"),
            It.IsAny<CancellationToken>()), Times.Once);
        mockRuntime.VerifyAll();
        mockChatClient.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GeneratePlanAsync_ShouldFallbackToDirectStreaming_WhenRuntimeFails()
    {
        var mockChatClient = new Mock<IChatClient>();
        var mockRuntime = new Mock<IQueryRuntimeEngine>();
        var planner = new DefaultCodexPlanner(
            mockChatClient.Object,
            NullLogger<DefaultCodexPlanner>.Instance,
            llmExecutor: null,
            queryRuntimeEngine: mockRuntime.Object);
        var session = new CodexSession { Id = "session-runtime-2", ProjectSummary = "Simple API", CurrentStage = 3 };
        const string json = """
        [
          {
            "Id": "T2",
            "TaskType": "code",
            "Title": "Step 2",
            "Description": "Desc 2",
            "Dependencies": [],
            "StageId": 3,
            "RiskLevel": "Low",
            "ComplexityLevel": 1
          }
        ]
        """;

        mockRuntime
            .Setup(r => r.ExecuteAsync(
                It.IsAny<QueryRuntimeRequest>(),
                It.IsAny<IQueryRuntimeEventSink>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("runtime failed"));

        mockChatClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> _, ChatOptions _, CancellationToken ct) => StreamText(json, ct));

        var result = await planner.GeneratePlanAsync(session, "Create a hello world api");

        Assert.Single(result);
        Assert.Equal("T2", result[0].Id);
        mockRuntime.Verify(r => r.ExecuteAsync(
            It.IsAny<QueryRuntimeRequest>(),
            It.IsAny<IQueryRuntimeEventSink>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
        mockChatClient.Verify(c => c.GetStreamingResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task GeneratePlanAsync_ContextHardLimit_DoesNotBypassRuntimeThroughFallback()
    {
        var client = new Mock<IChatClient>(MockBehavior.Strict);
        var runtime = new Mock<IQueryRuntimeEngine>();
        runtime.Setup(r => r.ExecuteAsync(It.IsAny<QueryRuntimeRequest>(),
                It.IsAny<IQueryRuntimeEventSink>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryRuntimeResult
            {
                TerminationReason = QueryTerminationReason.ContextHardLimit,
                TotalRounds = 0,
                TotalToolCalls = 0,
                ZeroToolCallRounds = 0,
                EmptyResponseCount = 0,
                RecoveryCount = 0,
                MalformedProtocolCount = 0,
                FinalText = string.Empty,
                TotalDurationMs = 1
            });
        var planner = new DefaultCodexPlanner(client.Object, NullLogger<DefaultCodexPlanner>.Instance,
            queryRuntimeEngine: runtime.Object);

        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            planner.GeneratePlanAsync(new CodexSession { Id = "oversized" }, "A large plan"));

        Assert.Contains("context", error.Message, StringComparison.OrdinalIgnoreCase);
        client.VerifyNoOtherCalls();
        runtime.Verify(r => r.ExecuteAsync(It.IsAny<QueryRuntimeRequest>(),
            It.IsAny<IQueryRuntimeEventSink>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // Anthropic's structured output rejects array length keywords ("For 'array' type, property 'maxItems' is not supported"). Found with
    // claude-sonnet-5-5: the projection round failed, the planner fell back to free text, and the plan came out as placeholders.
    [Fact]
    public async Task GeneratePlanAsync_StructuredOutputSchema_HasNoArrayLengthKeywords_ButThePromptStillStatesTheLimit()
    {
        var chat = new Mock<IChatClient>();
        var requests = new List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)>();
        chat.Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> messages, ChatOptions options, CancellationToken ct) =>
            {
                requests.Add((messages.ToList(), options));
                return StreamText("[{\"Id\":\"T1\",\"Title\":\"t\",\"Description\":\"d\",\"TaskType\":\"analysis\",\"Status\":\"Pending\",\"StageId\":1}]", ct);
            });
        var planner = new DefaultCodexPlanner(chat.Object, NullLogger<DefaultCodexPlanner>.Instance);

        await planner.GeneratePlanAsync(new CodexSession { Id = "s", ProjectSummary = "p", CurrentStage = 1 }, "upgrade packages");

        var structured = Assert.Single(requests, r => r.Options?.ResponseFormat is ChatResponseFormatJson { Schema: not null });
        var schema = ((ChatResponseFormatJson)structured.Options!.ResponseFormat!).Schema!.Value.GetRawText();
        Assert.DoesNotContain("maxItems", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("minItems", schema, StringComparison.Ordinal);
        Assert.Contains("\"Dependencies\"", schema, StringComparison.Ordinal); // the rest of the schema is untouched
        // The prompt carries the full schema, so the task-count limit still reaches the model as text.
        Assert.Contains("\"maxItems\":10", string.Concat(structured.Messages.Select(m => m.Text)), StringComparison.Ordinal);
    }

    // The projection prompt carries a worked example. A weaker model projected the act of planning ("仅制定计划，不修改代码") as the one task, which the
    // read-only rule then turned into an analysis task, so no code worker ran. The example must stay a valid plan in the real schema, or it would
    // teach the wrong shape.
    // Live runs: plans asked the executor for before/after baselines and a separate "--no-restore" run it cannot do (a fresh container per
    // command), and a finished upgrade was reported blocked. The projection must tell the planner what verification a task may ask for.
    [Fact]
    public void TheProjectionPrompt_LimitsTaskVerificationToWhatTheExecutorCanRun()
    {
        var prompt = DefaultCodexPlanner.BuildPlanProjectionPrompt("升级六个包", "草稿");

        Assert.Contains(DefaultCodexPlanner.PlanVerificationScopeRule, prompt, StringComparison.Ordinal);
        Assert.Contains("基线", DefaultCodexPlanner.PlanVerificationScopeRule, StringComparison.Ordinal);
        Assert.Contains("--no-restore", DefaultCodexPlanner.PlanVerificationScopeRule, StringComparison.Ordinal);
        Assert.Contains("宿主", DefaultCodexPlanner.PlanVerificationScopeRule, StringComparison.Ordinal);
    }

    // Live run: "the other 7 vulnerable packages" against 7 records for 6 packages. The plan told the task to ask the user to name the seventh;
    // the worker did all six, could not ask anyone, and reported blocked. A doubt about the request is never a step of a task.
    [Fact]
    public void TheProjectionPrompt_KeepsQuestionsForTheUserOutOfTasks()
    {
        var prompt = DefaultCodexPlanner.BuildPlanProjectionPrompt("其余 7 个有漏洞的包升到目标版本", "草稿");

        Assert.Contains(DefaultCodexPlanner.PlanOpenQuestionRule, prompt, StringComparison.Ordinal);
        Assert.Contains("执行者无法向用户提问", DefaultCodexPlanner.PlanOpenQuestionRule, StringComparison.Ordinal);
        Assert.Contains("以清单为准", DefaultCodexPlanner.PlanOpenQuestionRule, StringComparison.Ordinal);
    }

    // Live run (local Qwen, bugfix-paging): the last of three tasks was "run dotnet test"; it changed nothing and the host failed the job.
    [Fact]
    public void TheProjectionPrompt_NeverMakesVerificationATaskOfItsOwn()
    {
        var prompt = DefaultCodexPlanner.BuildPlanProjectionPrompt("修复分页错误", "草稿");

        Assert.Contains(DefaultCodexPlanner.PlanNoVerificationOnlyTaskRule, prompt, StringComparison.Ordinal);
        Assert.Contains("单独写成一个任务", DefaultCodexPlanner.PlanNoVerificationOnlyTaskRule, StringComparison.Ordinal);
        Assert.Contains("不会改动工作区", DefaultCodexPlanner.PlanNoVerificationOnlyTaskRule, StringComparison.Ordinal);
        Assert.Contains("宿主", DefaultCodexPlanner.PlanNoVerificationOnlyTaskRule, StringComparison.Ordinal);
    }

    private static readonly string[] RequiredTaskFields =
    [
        "Id", "Title", "Description", "TaskType", "Status", "StageId", "Dependencies", "Inputs", "Outputs", "ChecklistItems",
        "RequiredArtifacts", "ForbiddenStates", "ForbiddenPaths", "RiskLevel", "ComplexityLevel", "UnsafeIfDependencyFallbackPassed"
    ];

    // What a task may not touch is data in the plan, not a sentence the host has to interpret.
    [Fact]
    public void ThePlanSchema_RequiresForbiddenPaths_AsAListOfStrings()
    {
        var schema = DefaultCodexPlanner.CreatePlanResponseJsonSchema();
        var text = schema.GetRawText();
        using var document = System.Text.Json.JsonDocument.Parse(text);
        var items = FindTaskItems(document.RootElement);

        Assert.Equal("array", items.GetProperty("properties").GetProperty("ForbiddenPaths").GetProperty("type").GetString());
        Assert.Contains("ForbiddenPaths", items.GetProperty("required").EnumerateArray().Select(static e => e.GetString()));
    }

    private static System.Text.Json.JsonElement FindTaskItems(System.Text.Json.JsonElement node)
    {
        if (node.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            if (node.TryGetProperty("properties", out var props) && props.TryGetProperty("Outputs", out _) && props.TryGetProperty("ForbiddenStates", out _))
            {
                return node;
            }

            foreach (var property in node.EnumerateObject())
            {
                var found = FindTaskItems(property.Value);
                if (found.ValueKind != System.Text.Json.JsonValueKind.Undefined) return found;
            }
        }
        else if (node.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var element in node.EnumerateArray())
            {
                var found = FindTaskItems(element);
                if (found.ValueKind != System.Text.Json.JsonValueKind.Undefined) return found;
            }
        }

        return default;
    }

    [Fact]
    public void TheProjectionPrompt_ExplainsForbiddenPaths_AndKeepsContentLimitsOutOfIt()
    {
        var prompt = DefaultCodexPlanner.BuildPlanProjectionPrompt("升级依赖", "草稿");

        Assert.Contains(DefaultCodexPlanner.PlanForbiddenPathsRule, prompt, StringComparison.Ordinal);
        Assert.Contains("ForbiddenPaths", DefaultCodexPlanner.PlanForbiddenPathsRule, StringComparison.Ordinal);
        Assert.Contains("整个文件或目录", DefaultCodexPlanner.PlanForbiddenPathsRule, StringComparison.Ordinal);
        Assert.Contains("文件里的其他内容", DefaultCodexPlanner.PlanForbiddenPathsRule, StringComparison.Ordinal);   // those limits belong in the Description
    }

    private static System.Text.Json.JsonElement FewShotTasks()
    {
        var text = DefaultCodexPlanner.PlanProjectionFewShot;
        var start = text.IndexOf("{\"tasks\":[", StringComparison.Ordinal);
        var end = text.IndexOf("错误输出", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return System.Text.Json.JsonDocument.Parse(text[start..end].Trim()).RootElement.GetProperty("tasks").Clone();
    }

    [Fact]
    public void ProjectionFewShot_IsAValidPlan_InTheRealSchema()
    {
        var tasks = FewShotTasks();

        Assert.True(tasks.GetArrayLength() >= 2);
        foreach (var task in tasks.EnumerateArray())
        {
            foreach (var field in RequiredTaskFields)
            {
                Assert.True(task.TryGetProperty(field, out _), $"the example task lacks '{field}'");
            }

            Assert.Equal("code", task.GetProperty("TaskType").GetString());
            Assert.Equal("Pending", task.GetProperty("Status").GetString());
            Assert.NotEmpty(task.GetProperty("ChecklistItems").EnumerateArray());
        }
    }

    [Fact]
    public void ProjectionFewShot_Tasks_StayCodeTasks_AfterNormalisation()
    {
        foreach (var task in FewShotTasks().EnumerateArray())
        {
            var normalised = CodexTaskClassifier.NormalizeTaskType(
                task.GetProperty("TaskType").GetString(), task.GetProperty("Title").GetString(), task.GetProperty("Description").GetString());

            Assert.Equal("code", normalised);
        }
    }

    [Fact]
    public async Task GeneratePlanAsync_ProjectionPrompt_ShowsTheExampleAndStatesTheTurnConstraintRule()
    {
        var chat = new Mock<IChatClient>();
        var prompts = new List<string>();
        chat.Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> messages, ChatOptions options, CancellationToken ct) =>
            {
                prompts.Add(messages.Last().Text);
                return StreamText("{\"tasks\":[]}", ct);
            });
        var planner = new DefaultCodexPlanner(chat.Object, NullLogger<DefaultCodexPlanner>.Instance);

        await planner.GeneratePlanAsync(new CodexSession { Id = "s-fewshot", ProjectSummary = "p", CurrentStage = 1 }, "fix the login bug");

        var projection = Assert.Single(prompts, p => p.Contains("最终结构化任务规划", StringComparison.Ordinal));
        Assert.Contains("[示例：把计划投影成任务", projection, StringComparison.Ordinal);
        Assert.Contains("错误输出（禁止）", projection, StringComparison.Ordinal);
        Assert.Contains("批准之后要执行的工作", projection, StringComparison.Ordinal);
    }

    // A plan carried a "confirm what the host prefetched" task whose answer no worker can get, and the next task was told not to change anything until
    // that was confirmed: nothing was ever changed. Facts the host and the coordinator already hold are in the requirement; the tasks use them, and the
    // host's own checks (the offline restore preflight) judge whether they hold.
    [Fact]
    public async Task GeneratePlanAsync_BothPlanningRounds_ForbidGateTasksNoWorkerCanSatisfy()
    {
        var chat = new Mock<IChatClient>();
        var systemPrompts = new List<string>();
        var userPrompts = new List<string>();
        chat.Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> messages, ChatOptions options, CancellationToken ct) =>
            {
                var list = messages.ToList();
                systemPrompts.Add(list.First(m => m.Role == ChatRole.System).Text);
                userPrompts.Add(list.Last().Text);
                return StreamText("{\"tasks\":[]}", ct);
            });
        var planner = new DefaultCodexPlanner(chat.Object, NullLogger<DefaultCodexPlanner>.Instance);

        await planner.GeneratePlanAsync(new CodexSession { Id = "s-gate", ProjectSummary = "p", CurrentStage = 1 }, "upgrade the listed packages");

        var draftSystem = Assert.Single(systemPrompts, p => p.Contains("请先进行开放式任务规划", StringComparison.Ordinal));
        Assert.Contains("不要为“确认”这些事实单独安排任务", draftSystem, StringComparison.Ordinal);
        var projection = Assert.Single(userPrompts, p => p.Contains("最终结构化任务规划", StringComparison.Ordinal));
        Assert.Contains("不要为“确认”这些事实单独安排任务", projection, StringComparison.Ordinal);
        Assert.Contains("错误输出（禁止）：先安排一个任务去“确认", projection, StringComparison.Ordinal);
    }

    // The replanned task after a failed attempt carried "if the packages are not available, stop and wait": the worker obeyed after one edit,
    // reported itself blocked, and the host counted the job as completed. A stop condition on a host fact is a gate with no data source.
    [Fact]
    public async Task GeneratePlanAsync_BothPlanningRounds_ForbidStopOrWaitConditionsInsideATask()
    {
        var chat = new Mock<IChatClient>();
        var systemPrompts = new List<string>();
        var userPrompts = new List<string>();
        chat.Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> messages, ChatOptions options, CancellationToken ct) =>
            {
                var list = messages.ToList();
                systemPrompts.Add(list.First(m => m.Role == ChatRole.System).Text);
                userPrompts.Add(list.Last().Text);
                return StreamText("{\"tasks\":[]}", ct);
            });
        var planner = new DefaultCodexPlanner(chat.Object, NullLogger<DefaultCodexPlanner>.Instance);

        await planner.GeneratePlanAsync(new CodexSession { Id = "s-stop", ProjectSummary = "p", CurrentStage = 1 }, "upgrade the listed packages");

        var draftSystem = Assert.Single(systemPrompts, p => p.Contains("请先进行开放式任务规划", StringComparison.Ordinal));
        Assert.Contains("任务内部也不要写“缺包/不可用就停止、等待”", draftSystem, StringComparison.Ordinal);
        var projection = Assert.Single(userPrompts, p => p.Contains("最终结构化任务规划", StringComparison.Ordinal));
        Assert.Contains("任务内部也不要写“缺包/不可用就停止、等待”", projection, StringComparison.Ordinal);
        Assert.Contains("错误输出（禁止）：任务里写“离线预检缺包则停止并等待", projection, StringComparison.Ordinal);
        // run56: a task said "按前述清单升级" and the list was not in it; the executor could only do the part it could identify.
        Assert.Contains("任务的描述必须自包含", draftSystem, StringComparison.Ordinal);
        Assert.Contains("任务的描述必须自包含", projection, StringComparison.Ordinal);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamText(
        string text,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(text)]);
    }
}
