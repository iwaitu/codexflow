// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Models;
using CodexFlow.Core.Services;
using Xunit;

namespace CodexFlow.Core.Tests.Tools;

public sealed class PushNotificationToolTests
{
    [Fact]
    public async Task PushNotification_ShouldDeliverToDefaultSignalRChannel()
    {
        var service = new InMemoryPushNotificationService();
        var tool = new PushNotificationTool(service);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["user_id"] = "user-1",
            ["session_id"] = "session-1",
            ["title"] = "完成",
            ["message"] = "后台任务已完成",
            ["metadata"] = new Dictionary<string, object?>
            {
                ["kind"] = "manual"
            }
        });

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("\"pushed\":true", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"signalr_sync\"", result.Output, StringComparison.Ordinal);

        var notification = Assert.Single(service.Notifications);
        Assert.Equal("user-1", notification.UserId);
        Assert.Equal("session-1", notification.SessionId);
        Assert.Equal("后台任务已完成", notification.Message);
        Assert.Equal("manual", notification.Metadata["kind"]);
    }

    [Fact]
    public async Task PushNotification_ShouldRequireUserAndMessage()
    {
        var tool = new PushNotificationTool(new InMemoryPushNotificationService());

        var missingUser = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["message"] = "hello"
        });
        var missingMessage = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["user_id"] = "user-1"
        });

        Assert.Equal(ToolResultStatus.Failed, missingUser.Status);
        Assert.Equal(ToolResultStatus.Failed, missingMessage.Status);
    }
}
