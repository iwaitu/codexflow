// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application;
using CodexFlow.Core.Browser;
using CodexFlow.Orchestration.Hosting;
using CodexFlow.Orchestration.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Storage.Sqlite;

internal static class TestSqliteStorage
{
    public static IServiceCollection AddTestSqliteStorage(
        this IServiceCollection services, string databasePath, bool? useSqliteConversationMessages = null)
    {
        services.AddCodexFlowSqliteStorage(databasePath);
        services.AddLegacySqliteContext(databasePath);
        if (useSqliteConversationMessages is { } pilot)
        {
            services.AddScoped<IConversationMessageStore>(sp =>
            {
                var context = sp.GetRequiredService<IJobDbContext>();
                return pilot
                    ? new ScopedSqliteConversationMessageStore(context)
                    : new EfConversationMessageStore(context);
            });
            services.AddScoped<IChatService>(sp => new ChatService(
                new EfChatPersistence(sp.GetRequiredService<IJobDbContext>()), sp.GetRequiredService<ILogger<ChatService>>(),
                sp.GetService<IWorkspaceRootProvider>(), sp.GetRequiredService<IConversationMessageStore>(),
                sp.GetService<IBrowserSessionManager>()));
        }

        return services;
    }
}
