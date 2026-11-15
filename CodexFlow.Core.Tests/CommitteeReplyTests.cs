// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Microsoft.Extensions.AI;
using CodexFlow.Core.Planning.Committee;
using Xunit;

namespace CodexFlow.Core.Tests;

public class CommitteeReplyTests
{
    [Theory]
    [InlineData("同意")]
    [InlineData("好的")]
    [InlineData("好的，开始吧")]
    [InlineData("是的")]
    [InlineData("yes")]
    [InlineData("OK")]
    [InlineData("请启动委员会")]
    [InlineData("那就开始")]
    [InlineData("agree, go ahead")]
    public void ClearConsentIsAccepted(string reply) => Assert.True(CommitteeReply.IsAccepted(reply));

    [Theory]
    [InlineData("不同意")]
    [InlineData("不要启动")]
    [InlineData("别开始")]
    [InlineData("不好")]
    [InlineData("no")]
    [InlineData("skip")]
    [InlineData("I know, but skip it")]
    [InlineData("I know the requirement already")]      // "know" contains "no"; contains no consent either
    [InlineData("但是先别开，我想想")]                     // contains "是" mid-sentence
    [InlineData("这两个方案的区别是什么")]                  // a question, not consent
    [InlineData("不错，不过用常规规划就行")]
    [InlineData("先修复登录问题")]                         // a new instruction, not an answer
    [InlineData("")]
    public void NotAClearConsentFallsBackToRegularPlanning(string reply) => Assert.False(CommitteeReply.IsAccepted(reply));
}

public class CommitteeReplyModelTests
{
    private sealed class Fake(Func<string> answer) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer())));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class Recording : IChatClient
    {
        public ChatOptions? Sent { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Sent = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ACCEPT")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    // An 8-token limit is spent on thinking by a reasoning model and the answer comes back empty, which reads as "not consent". The call already
    // has a 20 second timeout, and a failed read falls back to the keyword reading.
    [Fact]
    public async Task TheConsentReadingSetsNoOutputTokenLimit()
    {
        var client = new Recording();

        await CommitteeReply.IsAcceptedAsync("好，不过先别开", client, null, default);

        Assert.NotNull(client.Sent);
        Assert.Null(client.Sent!.MaxOutputTokens);
    }

    [Theory]
    [InlineData("ACCEPT", true)]
    [InlineData("accept.", true)]
    [InlineData("REJECT", false)]
    [InlineData("UNCLEAR", false)]
    [InlineData("I think they accept", false)]   // not the closed answer: never consent
    [InlineData("", false)]
    public async Task OnlyTheExactAcceptAnswerStartsTheCommittee(string modelAnswer, bool expected) =>
        Assert.Equal(expected, await CommitteeReply.IsAcceptedAsync("好，不过先别开", new Fake(() => modelAnswer), null, default));

    [Fact]
    public async Task ModelFailureFallsBackToTheKeywordReading()
    {
        var broken = new Fake(() => throw new InvalidOperationException("down"));
        Assert.True(await CommitteeReply.IsAcceptedAsync("同意", broken, null, default));
        Assert.False(await CommitteeReply.IsAcceptedAsync("I know the requirement already", broken, null, default));
    }

    [Fact]
    public async Task NoModelUsesKeywords() => Assert.True(await CommitteeReply.IsAcceptedAsync("yes", null, null, default));
}
