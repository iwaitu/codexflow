// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application;
using CodexFlow.Application.Jobs;
using CodexFlow.Core.Abstractions;
using CodexFlow.Orchestration.Hosting;
using CodexFlow.Orchestration.Jobs;
using CodexFlow.Services.Background;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Storage.Sqlite;

public static class SqliteStorage
{
    /// <summary>
    /// Points a context that maps the job tables at one SQLite file.
    /// </summary>
    /// <param name="options">Options builder.</param>
    /// <param name="databasePath">Path of the database file; its directory is created when missing.</param>
    /// <param name="migrationsAssembly">Name of the assembly that holds the migrations of that context.</param>
    public static DbContextOptionsBuilder UseSqliteFile(this DbContextOptionsBuilder options, string databasePath, string? migrationsAssembly)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            // Waiting for a lock (a second terminal, a background job) instead of failing with SQLITE_BUSY.
            DefaultTimeout = 30
        }.ToString();

        return options
            .UseSqlite(connectionString, sqlite => sqlite.MigrationsAssembly(migrationsAssembly))
            .ReplaceService<IModelCacheKeyFactory, ProviderModelCacheKeyFactory>()
            .AddInterceptors(new OutboxSeqInterceptor());
    }

    /// <summary>
    /// The storage of the local host: <see cref="JobDbContext"/> on one SQLite file, and on top of it every store the gateway and
    /// the job runner ask their host for. Apply the migrations (<c>Database.MigrateAsync</c>) before the host starts serving.
    /// </summary>
    public static IServiceCollection AddCodexFlowSqliteStorage(this IServiceCollection services, string databasePath)
        => services.AddCodexFlowSqliteStorage(databasePath, useSqliteConversationMessages: false);

    public static IServiceCollection AddCodexFlowSqliteStorage(
        this IServiceCollection services,
        string databasePath,
        bool useSqliteConversationMessages)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddDbContext<JobDbContext>(options => options.UseSqliteFile(databasePath, typeof(SqliteStorage).Assembly.GetName().Name));
        services.AddScoped<IJobDbContext>(sp => sp.GetRequiredService<JobDbContext>());

        services.AddScoped<ISessionDirectory, EfSessionDirectory>();
        services.AddScoped<IConversationDirectory, EfConversationDirectory>();
        if (useSqliteConversationMessages)
        {
            services.AddScoped<IConversationMessageStore, ScopedSqliteConversationMessageStore>();
            services.AddScoped<IChatService>(sp => new ChatService(
                sp.GetRequiredService<IJobDbContext>(),
                sp.GetRequiredService<ILogger<ChatService>>(),
                sp.GetService<IWorkspaceRootProvider>(),
                sp.GetRequiredService<IConversationMessageStore>()));
        }
        else
        {
            services.AddScoped<IChatService, ChatService>();
        }
        services.AddScoped<IGatewayChatStore>(sp => sp.GetRequiredService<IChatService>());
        services.AddScoped<ISessionCompressionStore, SessionCompressionStore>();

        services.AddScoped<IKeyValueTextStore, SqliteKeyValueTextStore>();
        services.AddScoped<ISessionStore, CodexFlow.Infrastructure.Persistence.KeyValueSessionStore>();
        services.AddScoped<IJobEventLog, SqliteJobEventLog>();
        services.AddScoped<IJobLeaseStore>(sp => new EfJobLeaseStore(sp.GetRequiredService<IJobDbContext>()));
        services.AddScoped<IJobHotView, NullJobHotView>();
        return services;
    }
}
