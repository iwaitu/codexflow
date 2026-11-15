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
}
