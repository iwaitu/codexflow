// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodexFlow.Application;
using CodexFlow.Application.Jobs;
using CodexFlow.Controllers;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Configuration;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Gateway;
using CodexFlow.Core.Models;
using CodexFlow.Domain.Chat;
using CodexFlow.Gateway;
using CodexFlow.Local;
using CodexFlow.Orchestration.Hosting;
using CodexFlow.Orchestration.Jobs;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.Gateway;

public sealed class ConversationCreationAndAutoTitleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codexflow-conv-title-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            LocalExecutionService.TryDeleteDirectory(_root);
        }
    }

    [Fact]
    public async Task CreateConversationEndpoint_CreatesThreadWithDefaultOrCustomTitle_AndEnforcesOwnership()
    {
        var globalSettings = Path.Combine(_root, "global", "appsettings.json");
        await using var host = await P3TestHost.StartAsync(_root, globalSettings, ExecutionBackend.Local, ExecutionPermissionMode.Ask);
        using var client = host.CreateNormalClient();

        var sessionResp = await client.PostAsync("/api/gateway/session?name=Work1", null);
        Assert.Equal(HttpStatusCode.OK, sessionResp.StatusCode);
        var sessionId = (await sessionResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetString()!;

        string foreignSessionId;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var chat = scope.ServiceProvider.GetRequiredService<IChatService>();
            foreignSessionId = await chat.CreateSession("other-user", "Foreign");
        }

        // Foreign / missing session returns 404
        var foreignCreate = await client.PostAsJsonAsync($"/api/gateway/session/{foreignSessionId}/conversations", new CreateConversationRequest());
        Assert.Equal(HttpStatusCode.NotFound, foreignCreate.StatusCode);

        // Default name falls back to "新会话"
        var defaultCreate = await client.PostAsJsonAsync($"/api/gateway/session/{sessionId}/conversations", new CreateConversationRequest());
        Assert.Equal(HttpStatusCode.Created, defaultCreate.StatusCode);
        var defaultDto = (await defaultCreate.Content.ReadFromJsonAsync<ConversationSummaryDto>())!;
        Assert.Equal(sessionId, defaultDto.SessionId);
        Assert.Equal("新会话", defaultDto.Name);
        Assert.Equal(nameof(ConversationThreadType.Chat), defaultDto.ThreadType);

        // Explicit name is trimmed & preserved
        var namedCreate = await client.PostAsJsonAsync($"/api/gateway/session/{sessionId}/conversations", new CreateConversationRequest("  接口调试  "));
        Assert.Equal(HttpStatusCode.Created, namedCreate.StatusCode);
        var namedDto = (await namedCreate.Content.ReadFromJsonAsync<ConversationSummaryDto>())!;
        Assert.Equal("接口调试", namedDto.Name);

        var list = (await client.GetFromJsonAsync<PagedResponseDto<ConversationSummaryDto>>($"/api/gateway/session/{sessionId}/conversations?page=1&pageSize=10"))!;
        Assert.Equal(2, list.Total);
        Assert.Contains(list.Items, i => i.ConversationId == defaultDto.ConversationId && i.Name == "新会话");
        Assert.Contains(list.Items, i => i.ConversationId == namedDto.ConversationId && i.Name == "接口调试");
    }

    [Theory]
    [InlineData("新会话")]
    [InlineData("新对话")]
    [InlineData("主会话")]
    [InlineData("New Conversation")]
    [InlineData("New Chat")]
    public async Task AutoTitleGeneration_SummarizesDefaultPlaceholderTitles_StripsThinkTags_AndEmitsSse(string initialName)
    {
        var llmMock = new Mock<ILLMExecutor>();
        llmMock.Setup(x => x.ExecuteAsync(It.IsAny<LLMExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "<think>正在概括主题</think>\n\"数据库连接池优化。\"")));

        var globalSettings = Path.Combine(_root, "global-" + Guid.NewGuid().ToString("N"), "appsettings.json");
        await using var host = await P3TestHost.StartAsync(
            _root,
            globalSettings,
            ExecutionBackend.Local,
            ExecutionPermissionMode.Ask,
            configureServices: (services, _) => services.AddSingleton(llmMock.Object));

        string sessionId;
        string conversationId;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var chat = scope.ServiceProvider.GetRequiredService<IChatService>();
            sessionId = await chat.CreateSession(LocalUser.Id, "WS");
            conversationId = await chat.CreateConversationInSession(LocalUser.Id, sessionId, name: initialName);

            var processor = CreateProcessor(scope.ServiceProvider);
            var emitted = new List<GatewaySseEvent>();
            await processor.TryGenerateAndEmitConversationTitleAsync(
                conversationId,
                sessionId,
                "如何优化 SQLite 连接池并发超时问题？",
                "可以使用 WAL 模式并调整忙等待超时时间。",
                evt =>
                {
                    emitted.Add(evt);
                    return ValueTask.CompletedTask;
                },
                CancellationToken.None);

            var titleEvt = Assert.Single(emitted);
            Assert.Equal(GatewaySseEventType.UpdateConversationTitle, titleEvt.Type);
            var payloadJson = JsonSerializer.SerializeToElement(titleEvt.Payload);
            Assert.Equal(conversationId, payloadJson.GetProperty("conversationId").GetString());
            Assert.Equal("数据库连接池优化", payloadJson.GetProperty("title").GetString());
            Assert.Equal(sessionId, payloadJson.GetProperty("sessionId").GetString());

            await using var verifyScope = host.Services.CreateAsyncScope();
            var updated = await verifyScope.ServiceProvider.GetRequiredService<IChatService>().Get(conversationId);
            Assert.NotNull(updated);
            Assert.Equal("数据库连接池优化", updated!.Name);
        }

        llmMock.Verify(x => x.ExecuteAsync(It.IsAny<LLMExecutionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AutoTitleGeneration_SkipsCustomTitle_AndFallsBackToCleanUserPromptOnLlmFailure()
    {
        var llmMock = new Mock<ILLMExecutor>();
        llmMock.Setup(x => x.ExecuteAsync(It.IsAny<LLMExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("model upstream unavailable"));

        var globalSettings = Path.Combine(_root, "global-fallback", "appsettings.json");
        await using var host = await P3TestHost.StartAsync(
            _root,
            globalSettings,
            ExecutionBackend.Local,
            ExecutionPermissionMode.Ask,
            configureServices: (services, _) => services.AddSingleton(llmMock.Object));

        await using var scope = host.Services.CreateAsyncScope();
        var chat = scope.ServiceProvider.GetRequiredService<IChatService>();
        var sessionId = await chat.CreateSession(LocalUser.Id, "WS");
        var customConvId = await chat.CreateConversationInSession(LocalUser.Id, sessionId, name: "已有固定标题");
        var placeholderConvId = await chat.CreateConversationInSession(LocalUser.Id, sessionId, name: "新会话");

        var processor = CreateProcessor(scope.ServiceProvider);
        var emitted = new List<GatewaySseEvent>();
        ValueTask Collect(GatewaySseEvent evt)
        {
            emitted.Add(evt);
            return ValueTask.CompletedTask;
        }

        // Custom title must be preserved and skip LLM invocation
        await processor.TryGenerateAndEmitConversationTitleAsync(
            customConvId,
            sessionId,
            "帮我看一下这个报错",
            "好的",
            Collect,
            CancellationToken.None);

        Assert.Empty(emitted);
        llmMock.Verify(x => x.ExecuteAsync(It.IsAny<LLMExecutionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal("已有固定标题", (await chat.Get(customConvId))!.Name);

        // Placeholder title falls back cleanly to user's prompt when LLM throws
        await processor.TryGenerateAndEmitConversationTitleAsync(
            placeholderConvId,
            sessionId,
            "[附件: report.csv (.codexflow-attachments/r.csv, 1.0 KB)]\n分析季度营收异常波动的原因！",
            "根据报表数据显示...",
            Collect,
            CancellationToken.None);

        var fallbackEvt = Assert.Single(emitted);
        Assert.Equal(GatewaySseEventType.UpdateConversationTitle, fallbackEvt.Type);
        await using var verifyScope = host.Services.CreateAsyncScope();
        var updated = await verifyScope.ServiceProvider.GetRequiredService<IChatService>().Get(placeholderConvId);
        Assert.NotNull(updated);
        Assert.Equal("分析季度营收异常波动的原因", updated!.Name);
    }

    private static GatewayMessageProcessor CreateProcessor(IServiceProvider sp)
        => new(
            sp.GetRequiredService<IJobStore>(),
            sp.GetRequiredService<IJobReadStore>(),
            sp.GetRequiredService<IGatewayChatStore>(),
            sp.GetRequiredService<ILLMExecutor>(),
            Mock.Of<IBackgroundJobService>(),
            sp.GetRequiredService<ISessionStore>(),
            sp.GetRequiredService<CodexSessionManager>(),
            Mock.Of<IToolRegistry>(),
            null!,
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<GatewayMessageProcessor>.Instance);
}
