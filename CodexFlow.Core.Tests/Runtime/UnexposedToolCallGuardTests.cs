// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using CodexFlow.Core.Runtime;
using Xunit;
using QreProtocol = CodexFlow.QueryRuntime.Protocol;

namespace CodexFlow.Core.Tests.Runtime;

// Live developer simulation (bugfix-discounts, bugfix-node-slugs): the planning turn called ReadSkillFile/ListSkillFiles, which the chat entry
// does not expose, 31 times in a row; every call was denied and the turn ended on its step budget with no reply.
public sealed class UnexposedToolCallGuardTests
{
    private static readonly JsonElement NoArguments = JsonDocument.Parse("{}").RootElement;
    private static int _nextId;

    private static QreProtocol.RuntimeMessage User(string text)
        => new(QreProtocol.RuntimeMessageRole.User, [new QreProtocol.RuntimeTextItem(text)]);

    private static QreProtocol.RuntimeMessage[] Call(string tool, bool denied)
    {
        var id = new QreProtocol.RuntimeInvocationId("call-" + Interlocked.Increment(ref _nextId));
        var result = denied
            ? new QreProtocol.RuntimeToolResult(
                id,
                null,
                false,
                new QreProtocol.RuntimeError(QreProtocol.RuntimeErrorCategory.UnknownTool, UnexposedToolCallGuard.DeniedCode, "not exposed"))
            : new QreProtocol.RuntimeToolResult(id, "ok", true);
        return
        [
            new QreProtocol.RuntimeMessage(
                QreProtocol.RuntimeMessageRole.Assistant,
                [new QreProtocol.RuntimeToolCallItem(new QreProtocol.RuntimeToolCall(id, tool, NoArguments))]),
            new QreProtocol.RuntimeMessage(QreProtocol.RuntimeMessageRole.Tool, [new QreProtocol.RuntimeToolResultItem(result)])
        ];
    }

    private static List<QreProtocol.RuntimeMessage> Conversation(params QreProtocol.RuntimeMessage[][] turns)
        => [User("给这个缺陷做个计划"), .. turns.SelectMany(static turn => turn)];

    [Fact]
    public void DenialsAreCountedFromTheEnd_AndNamed()
    {
        var messages = Conversation(Call("ReadSkillFile", denied: true), Call("ListSkillFiles", denied: true), Call("ReadSkillFile", denied: true));

        Assert.Equal(["ReadSkillFile", "ListSkillFiles", "ReadSkillFile"], UnexposedToolCallGuard.TrailingDenials(messages));
    }

    [Fact]
    public void AnyOtherToolResult_OrANewUserMessage_StartsTheCountAgain()
    {
        var afterRealCall = Conversation(Call("ReadSkillFile", denied: true), Call("hs_read", denied: false), Call("ReadSkillFile", denied: true));
        var afterUser = Conversation(Call("ReadSkillFile", denied: true), Call("ReadSkillFile", denied: true));
        afterUser.Add(User("继续"));

        Assert.Single(UnexposedToolCallGuard.TrailingDenials(afterRealCall));
        Assert.Empty(UnexposedToolCallGuard.TrailingDenials(afterUser));
        Assert.Empty(UnexposedToolCallGuard.TrailingDenials(Conversation(Call("hs_read", denied: false))));
        Assert.Empty(UnexposedToolCallGuard.TrailingDenials([]));
    }

    [Fact]
    public void OneDenial_IsLeftToTheModel_TheSecondNamesTheToolsThatExist()
    {
        Assert.Null(UnexposedToolCallGuard.Nudge(["ReadSkillFile"], ["hs_read"]));

        var nudge = UnexposedToolCallGuard.Nudge(["ReadSkillFile", "ReadSkillFile"], ["hs_read", "create_session_plan"]);

        Assert.NotNull(nudge);
        Assert.Contains("`ReadSkillFile`", nudge.Text, StringComparison.Ordinal);
        Assert.Contains("`hs_read`", nudge.Text, StringComparison.Ordinal);
        Assert.Contains("`create_session_plan`", nudge.Text, StringComparison.Ordinal);
        Assert.Contains("直接用文字回答", UnexposedToolCallGuard.Nudge(["ReadSkillFile", "ReadSkillFile"], [])!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTurnIsStopped_OnlyAfterTheModelIgnoredTheReminder()
    {
        var below = Enumerable.Repeat("ReadSkillFile", UnexposedToolCallGuard.StopAfterDenials - 1).ToList();
        var at = Enumerable.Repeat("ReadSkillFile", UnexposedToolCallGuard.StopAfterDenials).ToList();

        Assert.True(UnexposedToolCallGuard.NudgeAfterDenials < UnexposedToolCallGuard.StopAfterDenials);
        Assert.False(UnexposedToolCallGuard.ShouldStop(below));
        Assert.True(UnexposedToolCallGuard.ShouldStop(at));
        Assert.Contains("ReadSkillFile", UnexposedToolCallGuard.StopMessage(at), StringComparison.Ordinal);
        Assert.Contains(UnexposedToolCallGuard.StopAfterDenials.ToString(), UnexposedToolCallGuard.StopMessage(at), StringComparison.Ordinal);
    }
}
