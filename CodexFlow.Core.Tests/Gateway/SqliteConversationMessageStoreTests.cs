// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Data;
using CodexFlow.Application;
using CodexFlow.Core.Browser;
using CodexFlow.Domain.Chat;
using CodexFlow.Orchestration.Hosting;
using CodexFlow.Orchestration.Jobs;
using CodexFlow.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.Gateway;

public sealed class SqliteConversationMessageStoreTests
{
    private const string Owner = "owner";
    private const string ConversationId = "conversation-'pilot";
    private static readonly DateTime Cursor = new DateTime(2026, 11, 20, 12, 34, 56, DateTimeKind.Utc).AddTicks(1_234_567);

    [Fact]
    public async Task CursorReadsMatchEfAndPreserveHistoricalFields()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var efScope = database.Ef.CreateAsyncScope();
        var context = efScope.ServiceProvider.GetRequiredService<JobDbContext>();
        context.ChatMessages.AddRange(
            Message("before", ConversationId, Cursor.AddTicks(-1)),
            Message("equal", ConversationId, Cursor),
            new IvilsonChatMessage
            {
                Id = "one-tick-later",
                ConversationId = ConversationId,
                Created = Cursor.AddTicks(1),
                MessageType = "notification",
                NotificationMeta = """{"event_type":"job-finished"}"""
            },
            Message("second-later", ConversationId, Cursor.AddTicks(2)),
            Message("second-tie", ConversationId, Cursor.AddTicks(2)),
            Message("third-later", ConversationId, Cursor.AddTicks(3)),
            Message("other-conversation", "other", Cursor.AddTicks(4)));
        await context.SaveChangesAsync();

        await using var pilotScope = database.Pilot.CreateAsyncScope();
        var ef = efScope.ServiceProvider.GetRequiredService<IChatService>();
        var pilot = pilotScope.ServiceProvider.GetRequiredService<IGatewayChatStore>();
        Assert.Same(ef, efScope.ServiceProvider.GetRequiredService<IGatewayChatStore>());
        Assert.Same(pilot, pilotScope.ServiceProvider.GetRequiredService<IChatService>());

        async Task<IReadOnlyList<IvilsonChatMessage>> Compare(DateTime? after, int? limit, params string[] expectedIds)
        {
            var legacy = await ef.GetConversationMessagesAfterAsync(ConversationId, after, limit);
            var actual = await pilot.GetConversationMessagesAfterAsync(ConversationId, after, limit);
            Assert.Equal(expectedIds, legacy.Select(row => row.Id));
            Assert.Equal(expectedIds, actual.Select(row => row.Id));
            Assert.Equivalent(legacy, actual, strict: true);
            return actual;
        }

        await Compare(null, null, "before", "equal", "one-tick-later", "second-later", "second-tie", "third-later");
        await Compare(null, 2, "second-tie", "third-later");
        await Compare(null, 0);
        var afterCursor = await Compare(Cursor, null, "one-tick-later", "second-later", "second-tie", "third-later");
        Assert.Equal(1L, afterCursor[0].Created.Ticks - Cursor.Ticks);
        Assert.Equal("notification", afterCursor[0].MessageType);
        Assert.Equal("""{"event_type":"job-finished"}""", afterCursor[0].NotificationMeta);
        Assert.Null(afterCursor[0].SystemPrompt);
        Assert.Null(afterCursor[0].ToolCallResult);
        await Compare(Cursor, 2, "second-tie", "third-later");
        await Compare(Cursor, 0);
        await Compare(Cursor.AddTicks(3), 2);
        Assert.Empty(await pilot.GetConversationMessagesAfterAsync("absent", null));
    }

    [Fact]
    public async Task ExistingGatewayEntryPointsCanReadBothKindsOfAppendAcrossImplementations()
    {
        await using var database = await TestDatabase.CreateAsync();
        const string longTitle = "01234567890123456789 continuation";
        await using (var scope = database.Pilot.CreateAsyncScope())
        {
            var pilot = scope.ServiceProvider.GetRequiredService<IGatewayChatStore>();
            await pilot.AddMessage(Owner, ConversationId, longTitle, "raw response", 7, "pilot-model",
                "pilot-reply", "initial prompt", "reasoning");
        }

        await using (var scope = database.Ef.CreateAsyncScope())
        {
            var ef = scope.ServiceProvider.GetRequiredService<IChatService>();
            var conversation = Assert.IsType<IvilsonConversation>(await ef.Get(ConversationId));
            Assert.Equal("01234567890123456789", conversation.Name);
            Assert.Equal("pilot-model", conversation.Model);
            Assert.Equal(Owner, conversation.UserId);
            var ordinary = Assert.Single(conversation.ChatMessages);
            Assert.Equal(longTitle, ordinary.UserContent);
            Assert.Equal("raw response", ordinary.AiContent);
            Assert.Equal("reasoning", ordinary.ThinkingContent);
            Assert.Equal("initial prompt", ordinary.SystemPrompt);
            Assert.Equal("pilot-reply", ordinary.ReplyId);
            Assert.Equal(7, ordinary.TokenUsed);
            Assert.Null(ordinary.ToolCallResult);
            Assert.Null(ordinary.MessageType);
            await ef.AddMessageWithToolcall(Owner, ConversationId, "EF user", "EF response", 13, """{"provider":"ef"}""",
                "ignored-model", "ef-reply");
        }

        await using (var scope = database.Pilot.CreateAsyncScope())
        {
            var pilot = scope.ServiceProvider.GetRequiredService<IChatService>();
            var efMessage = Assert.Single(await pilot.GetConversationMessagesAfterAsync(ConversationId, null),
                row => row.ReplyId == "ef-reply");
            Assert.Equal("""{"provider":"ef"}""", efMessage.ToolCallResult);
            Assert.Equal("EF response", efMessage.AiContent);
            Assert.Equal(13, efMessage.TokenUsed);

            await pilot.AddMessageWithToolcall(Owner, ConversationId, "pilot user", "pilot tool response", 17,
                """{"provider":"pilot"}""", "ignored-model", "pilot-tool-reply");
        }

        await using (var scope = database.Ef.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<JobDbContext>();
            var direct = new ChatService(new EfChatPersistence(context), NullLogger<ChatService>.Instance, null, new EfConversationMessageStore(context));
            var persisted = Assert.IsType<IvilsonConversation>(await direct.Get(ConversationId));
            Assert.Equal(3, persisted.ChatMessages.Count);
            Assert.Equal("01234567890123456789", persisted.Name);
            Assert.Equal("pilot-model", persisted.Model);
            Assert.Equal(Owner, persisted.UserId);
            var pilotTool = Assert.Single(persisted.ChatMessages, row => row.ReplyId == "pilot-tool-reply");
            Assert.Equal("""{"provider":"pilot"}""", pilotTool.ToolCallResult);
            Assert.Equal("pilot tool response", pilotTool.AiContent);
            Assert.Equal(17, pilotTool.TokenUsed);
        }
    }

    [Theory]
    [InlineData("conversation-'pilot")]
    [InlineData("missing-conversation")]
    public async Task DuplicateMessageRollsBackConversationCreationOrTimestamp(string target)
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var seed = database.Ef.CreateAsyncScope())
        {
            var context = seed.ServiceProvider.GetRequiredService<JobDbContext>();
            context.Conversations.Add(Conversation());
            context.ChatMessages.Add(Message("duplicate", ConversationId, Cursor));
            await context.SaveChangesAsync();
        }

        await using (var scope = database.Pilot.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IConversationMessageStore>();
            var failure = await Assert.ThrowsAsync<SqliteException>(
                () => store.AppendAsync(Owner, "ignored-model", Message("duplicate", target, Cursor.AddSeconds(1))));
            Assert.Equal(19, failure.SqliteErrorCode);
        }

        await using var check = database.Ef.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<JobDbContext>();
        var conversation = Assert.Single(await db.Conversations.AsNoTracking().ToListAsync());
        Assert.Equal(ConversationId, conversation.Id);
        Assert.Equal(Cursor, conversation.UpdateAt);
        var message = Assert.Single(await db.ChatMessages.AsNoTracking().ToListAsync());
        Assert.Equal(ConversationId, message.ConversationId);
        Assert.Equal("duplicate", message.Id);
    }

    [Fact]
    public async Task ScopedConnectionsCanAppendConcurrentlyWithoutLosingMessagesOrMetadata()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var seed = database.Ef.CreateAsyncScope())
        {
            var context = seed.ServiceProvider.GetRequiredService<JobDbContext>();
            context.Conversations.Add(Conversation());
            await context.SaveChangesAsync();
        }

        const int count = 8;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var appends = Enumerable.Range(0, count).Select(index => Task.Run(async () =>
        {
            await start.Task;
            await using var scope = database.Pilot.CreateAsyncScope();
            var chat = scope.ServiceProvider.GetRequiredService<IGatewayChatStore>();
            await chat.AddMessage(Owner, ConversationId, $"request {index}", "response", index, "other-model",
                $"reply-{index}");
        })).ToArray();

        start.SetResult();
        await Task.WhenAll(appends);

        await using var check = database.Ef.CreateAsyncScope();
        var ef = check.ServiceProvider.GetRequiredService<IChatService>();
        var conversation = Assert.IsType<IvilsonConversation>(await ef.Get(ConversationId));
        Assert.Equal("original name", conversation.Name);
        Assert.Equal("original model", conversation.Model);
        Assert.Equal(Owner, conversation.UserId);
        Assert.Equal(Enumerable.Range(0, count), conversation.ChatMessages.Select(row => row.TokenUsed).Order());
        Assert.Equal(count, conversation.ChatMessages.Select(row => row.Id).Distinct().Count());
    }

    [Fact]
    public async Task SqliteAppendRefreshesTrackedTimestampAndPreservesOpenConnection()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var scope = database.Pilot.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<JobDbContext>();
        var conversation = Conversation();
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();
        await db.Database.OpenConnectionAsync();

        var message = Message("tracked", ConversationId, Cursor.AddSeconds(1));
        var store = scope.ServiceProvider.GetRequiredService<IConversationMessageStore>();
        await store.AppendAsync(Owner, null, message);
        var chat = scope.ServiceProvider.GetRequiredService<IChatService>();
        Assert.Single(await chat.GetConversationMessagesAfterAsync(ConversationId, null));

        Assert.Equal(ConnectionState.Open, db.Database.GetDbConnection().State);
        Assert.Same(conversation, await chat.Get(ConversationId));
        Assert.Equal(message.Created, conversation.UpdateAt);
        Assert.Equal(message.Created, db.Entry(conversation).Property(row => row.UpdateAt).OriginalValue);
        Assert.Equal(EntityState.Unchanged, db.Entry(conversation).State);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task AppendHonorsPendingChangesOrOuterTransaction(bool useTransaction, bool hasPendingChange)
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var seed = database.Ef.CreateAsyncScope())
        {
            var context = seed.ServiceProvider.GetRequiredService<JobDbContext>();
            context.Conversations.Add(Conversation());
            await context.SaveChangesAsync();
        }

        await using (var scope = database.Pilot.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<JobDbContext>();
            await using var transaction = useTransaction ? await context.Database.BeginTransactionAsync() : null;
            var tracked = await context.Conversations.SingleAsync();
            if (hasPendingChange)
                tracked.Name = "pending name";

            var store = scope.ServiceProvider.GetRequiredService<IConversationMessageStore>();
            await store.AppendAsync(Owner, null, Message("with-changes", ConversationId, Cursor.AddSeconds(1)));
            var chat = scope.ServiceProvider.GetRequiredService<IGatewayChatStore>();
            Assert.Single(await chat.GetConversationMessagesAfterAsync(ConversationId, null));
            Assert.Equal(EntityState.Unchanged, context.Entry(tracked).State);

            if (transaction is not null)
                await transaction.RollbackAsync();
        }

        await using var check = database.Ef.CreateAsyncScope();
        var persisted = Assert.IsType<IvilsonConversation>(
            await check.ServiceProvider.GetRequiredService<IChatService>().Get(ConversationId));
        Assert.Equal(!useTransaction && hasPendingChange ? "pending name" : "original name", persisted.Name);
        Assert.Equal(useTransaction ? 0 : 1, persisted.ChatMessages.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionDeletionClosesBrowserContextInBothStorageModes(bool usePilot)
    {
        const string sessionId = "browser-session";
        var browser = new Mock<IBrowserSessionManager>();
        await using var database = await TestDatabase.CreateAsync(browser.Object);
        var provider = usePilot ? database.Pilot : database.Ef;
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<JobDbContext>();
        context.Sessions.Add(new IvilsonSession { Id = sessionId, UserId = Owner, Name = "Browser session" });
        await context.SaveChangesAsync();
        var wasPersistedBeforeClose = false;
        browser.Setup(manager => manager.CloseSessionAsync(sessionId, "session_deleted", It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                wasPersistedBeforeClose = !await context.Sessions.AsNoTracking().AnyAsync(item => item.Id == sessionId);
            });
        var chat = scope.ServiceProvider.GetRequiredService<IChatService>();

        await chat.DeleteSession(sessionId, "another-user");
        Assert.True(await context.Sessions.AsNoTracking().AnyAsync(item => item.Id == sessionId));
        browser.Verify(manager => manager.CloseSessionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        await chat.DeleteSession(sessionId, Owner);
        Assert.True(wasPersistedBeforeClose);
        browser.Verify(manager => manager.CloseSessionAsync(sessionId, "session_deleted", It.IsAny<CancellationToken>()), Times.Once);
    }

    private static IvilsonChatMessage Message(string id, string conversationId, DateTime created) => new()
    {
        Id = id,
        ConversationId = conversationId,
        Created = created
    };

    private static IvilsonConversation Conversation() => new()
    {
        Id = ConversationId,
        UserId = Owner,
        Name = "original name",
        Model = "original model",
        Created = Cursor,
        UpdateAt = Cursor
    };

    private sealed class TestDatabase(DirectoryInfo directory, ServiceProvider ef, ServiceProvider pilot) : IAsyncDisposable
    {
        public ServiceProvider Ef { get; } = ef;
        public ServiceProvider Pilot { get; } = pilot;

        public static async Task<TestDatabase> CreateAsync(IBrowserSessionManager? browserSessionManager = null)
        {
            var directory = Directory.CreateTempSubdirectory("codexflow-sqlite-messages-");
            var path = Path.Combine(directory.FullName, "state.db");
            var efServices = new ServiceCollection().AddLogging().AddTestSqliteStorage(path, useSqliteConversationMessages: false);
            var pilotServices = new ServiceCollection().AddLogging().AddTestSqliteStorage(path, true);
            if (browserSessionManager is not null)
            {
                efServices.AddSingleton(browserSessionManager);
                pilotServices.AddSingleton(browserSessionManager);
            }

            var ef = efServices.BuildServiceProvider();
            var pilot = pilotServices.BuildServiceProvider();
            var database = new TestDatabase(directory, ef, pilot);
            try
            {
                await using var scope = ef.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<JobDbContext>();
                await context.Database.MigrateAsync();
                return database;
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await using (var scope = Ef.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<JobDbContext>();
                SqliteConnection.ClearPool((SqliteConnection)db.Database.GetDbConnection());
            }
            await using (var connection = Ef.GetRequiredService<SqliteConnectionFactory>().Open())
            {
                SqliteConnection.ClearPool(connection);
            }

            await Pilot.DisposeAsync();
            await Ef.DisposeAsync();
            directory.Delete(recursive: true);
        }
    }
}
