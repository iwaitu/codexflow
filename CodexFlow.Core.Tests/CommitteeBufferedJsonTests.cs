// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Planning.Committee;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests;

public class CommitteeBufferedJsonTests
{
    [Fact]
    public async Task RepairJsonAsync_ReturnsCleanedJson()
    {
        var mockChatClient = new Mock<IChatClient>();
        mockChatClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse([new ChatMessage(ChatRole.Assistant, "```json\n{\"status\":\"approve\"}\n```")]));

        var parser = new CommitteeJsonParser(NullLogger.Instance);

        var repaired = await parser.RepairJsonAsync("bad json", mockChatClient.Object, CancellationToken.None);

        Assert.Equal("{\"status\":\"approve\"}", repaired);
    }

    [Fact]
    public async Task ProjectAsync_ParsesJsonArrayFromBufferedHelper()
    {
        var mockChatClient = new Mock<IChatClient>();
        mockChatClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse([
                new ChatMessage(
                    ChatRole.Assistant,
                    """
                    ```json
                    [
                      {
                        "id": "task-01",
                        "title": "实现分页",
                        "description": "补齐分页实现",
                        "task_type": "code",
                        "dependencies": [],
                        "risk_level": "Low",
                        "complexity_level": 2,
                        "verification_steps": ["运行分页相关测试"]
                      }
                    ]
                    ```
                    """)
            ]));

        var service = new PlanProjectionService(NullLogger.Instance);

        var result = await service.ProjectAsync("# final plan", mockChatClient.Object, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Single(result!);
        Assert.Equal("task-01", result[0].Id);
        Assert.Equal("实现分页", result[0].Title);
    }

    [Fact]
    public async Task ProjectAsync_ParsesWrappedObjectArray()
    {
        var mockChatClient = new Mock<IChatClient>();
        mockChatClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse([
                new ChatMessage(
                    ChatRole.Assistant,
                    """
                    {
                      "tasks": [
                        {
                          "id": "task-02",
                          "title": "更新服务层",
                          "description": "补齐 service 改动",
                          "task_type": "code",
                          "dependencies": ["task-01"],
                          "risk_level": "Medium",
                          "complexity_level": 2,
                          "verification_steps": ["运行集成测试"]
                        }
                      ]
                    }
                    """)
            ]));

        var service = new PlanProjectionService(NullLogger.Instance);

        var result = await service.ProjectAsync("# final plan", mockChatClient.Object, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Single(result!);
        Assert.Equal("task-02", result[0].Id);
        Assert.Equal(["task-01"], result[0].Dependencies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectAsync_PreservesLegacySyntaxAndNestedMappings(bool wrapped)
    {
        const string tasks = """
            [{
                ID: 'task-03',
                TITLE: 'Legacy task',
                description: 'Exercise old input',
                TASK_TYPE: 'analysis',
                dependencies: ['task-01'],
                RISK_LEVEL: 'High',
                COMPLEXITY_LEVEL: '3',
                verification_steps: ['dotnet test'],
                required_artifacts: [{ TYPE: 'file_contains', PATH: 'src/example.txt', TEXT: 'old format', }],
                forbidden_states: [{ type: 'file_not_exists', path: 'tmp/example.txt', text: null }],
            },]
            """;
        var input = wrapped ? "{/* metadata */ counts: 1, tasks: " + tasks + ",}" : tasks;
        var mockChatClient = new Mock<IChatClient>();
        mockChatClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse([new ChatMessage(ChatRole.Assistant, input)]));
        var service = new PlanProjectionService(NullLogger.Instance);

        var task = Assert.Single((await service.ProjectAsync("# final plan", mockChatClient.Object, CancellationToken.None))!);

        Assert.Equal("task-03", task.Id);
        Assert.Equal("Legacy task", task.Title);
        Assert.Equal("analysis", task.TaskType);
        Assert.Equal("High", task.RiskLevel);
        Assert.Equal(3, task.ComplexityLevel);
        Assert.Equal(["task-01"], task.Dependencies);
        Assert.Equal(["dotnet test"], task.VerificationSteps);
        var required = Assert.Single(task.RequiredArtifacts);
        Assert.Equal("file_contains", required.Type);
        Assert.Equal("src/example.txt", required.Path);
        Assert.Equal("old format", required.Text);
        Assert.Null(Assert.Single(task.ForbiddenStates).Text);
    }

    [Fact]
    public void ReviewReaders_KeepLegacyDefaultsAndExplicitNulls()
    {
        var parser = new CommitteeJsonParser(NullLogger.Instance);
        var feedback = parser.ParseReviewerFeedback("""
            {status: 'changes_requested', /* last case-insensitive value wins */ STATUS: 'approve',
             summary: null, non_blocking_items: [1.50, true, null,],
             issue_statuses: [{ ITEM: 'Legacy item', STATUS: 'resolved', }],}
            """);
        Assert.NotNull(feedback);
        Assert.True(feedback.IsApproved);
        Assert.Null(feedback.Summary);
        Assert.Equal("1.50", feedback.NonBlockingItems[0]);
        Assert.Equal("true", feedback.NonBlockingItems[1]);
        Assert.Null(feedback.NonBlockingItems[2]);
        Assert.Equal(string.Empty, Assert.Single(feedback.IssueStatuses).Note);

        var decision = parser.ParseModeratorDecision("{DECISION:'continue', reason:true, unresolved_items:null,}");
        Assert.NotNull(decision);
        Assert.Equal("true", decision.Reason);
        Assert.Equal(string.Empty, decision.UpdatedPlan);
        Assert.Null(decision.UnresolvedItems);
        Assert.Null(parser.ParseReviewerFeedback("{status:'approve'"));
    }
}
