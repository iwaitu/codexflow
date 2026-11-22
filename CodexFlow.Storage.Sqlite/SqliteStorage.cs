// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application;
using CodexFlow.Application.Jobs;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Browser;
using CodexFlow.Core.Remote;
using CodexFlow.Infrastructure.Persistence;
using CodexFlow.Orchestration.BusinessSkills;
using CodexFlow.Orchestration.Chat;
using CodexFlow.Orchestration.Hosting;
using CodexFlow.Orchestration.Jobs;
using CodexFlow.Services.Background;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Storage.Sqlite;

public static class SqliteStorage
{
    /// <summary>Registers the Local stores on one SQLite file. Run <see cref="SqliteSchemaMigrator"/> before serving requests.</summary>
    public static IServiceCollection AddCodexFlowSqliteStorage(this IServiceCollection services, string databasePath)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(new SqliteConnectionFactory(databasePath));
        services.AddSingleton<SqliteSchemaMigrator>();
        services.AddScoped<ISessionDirectory, SqliteSessionDirectory>();
        services.AddScoped<IConversationDirectory, SqliteConversationDirectory>();
        services.AddScoped<SqliteSessionStateStore>();
        services.AddScoped<ISessionPolicyStore>(sp => sp.GetRequiredService<SqliteSessionStateStore>());
        services.AddScoped<ISessionCompressionPersistence>(sp => sp.GetRequiredService<SqliteSessionStateStore>());
        services.AddScoped<ILateToolOutputStore>(sp => sp.GetRequiredService<SqliteSessionStateStore>());
        services.AddScoped<ISessionCompressionStore>(sp => new SessionCompressionStore(
            sp.GetRequiredService<ISessionCompressionPersistence>(), sp.GetRequiredService<ILogger<SessionCompressionStore>>()));

        services.AddScoped<IKeyValueTextStore, SqliteKeyValueTextStore>();
        services.AddScoped<ISessionStore, CodexFlow.Infrastructure.Persistence.KeyValueSessionStore>();
        services.AddScoped<IJobEventLog, SqliteJobEventLog>();
        services.AddScoped<IJobLeaseStore, SqliteJobLeaseStore>();
        services.AddScoped<IJobLeaseRecoveryStore, SqliteJobLeaseRecoveryStore>();
        services.AddScoped<IJobStore, SqliteJobStore>();
        services.AddScoped<IJobOutcomeStore, SqliteJobOutcomeStore>();
        services.AddScoped<IWorkerJobStore, SqliteWorkerJobStore>();
        services.AddScoped<IJobReadStore, SqliteJobReadStore>();
        services.AddScoped<IJobRunnerStore, SqliteJobRunnerStore>();
        services.AddScoped<IOutboxStore, SqliteOutboxStore>();
        services.AddScoped<IPlanArtifactRepository, SqlitePlanArtifactRepository>();
        services.AddScoped<IPlanArtifactStore, PlanArtifactStore>();
        services.AddScoped<IBusinessSkillRetentionStore, SqliteBusinessSkillRetentionStore>();
        services.AddScoped<IBusinessPluginStore, SqliteBusinessPluginStore>();
        services.AddScoped<IBusinessSkillExecutionStore, SqliteBusinessSkillExecutionStore>();
        services.AddScoped<IBusinessSkillBuilderStore, SqliteBusinessSkillExecutionStore>();
        services.AddScoped<IJobHotView, NullJobHotView>();
        services.AddScoped<IGatewayChatStore>(sp => sp.GetRequiredService<IChatService>());
        services.AddScoped<IChatPersistence, SqliteChatPersistence>();
        services.AddScoped<IConversationMessageStore>(sp => new SqliteConversationMessageStore(
            sp.GetRequiredService<SqliteConnectionFactory>()));
        services.AddScoped<IChatService>(sp => new ChatService(
            sp.GetRequiredService<IChatPersistence>(), sp.GetRequiredService<ILogger<ChatService>>(),
            sp.GetService<IWorkspaceRootProvider>(), sp.GetRequiredService<IConversationMessageStore>(),
            sp.GetService<IBrowserSessionManager>()));

        services.RemoveAll<SqliteRemoteControlStore>();
        services.AddSingleton<SqliteRemoteControlStore>();
        services.RemoveAll<IRemoteAttachmentUploadStore>();
        services.AddSingleton<IRemoteAttachmentUploadStore>(sp => sp.GetRequiredService<SqliteRemoteControlStore>());
        services.RemoveAll<IRemoteChatOpStore>();
        services.AddSingleton<IRemoteChatOpStore>(sp => sp.GetRequiredService<SqliteRemoteControlStore>());
        services.RemoveAll<IRemoteWorkspaceAuthorizationStore>();
        services.AddSingleton<IRemoteWorkspaceAuthorizationStore>(sp => sp.GetRequiredService<SqliteRemoteControlStore>());
        services.RemoveAll<IRemoteRevocationStore>();
        services.AddSingleton<IRemoteRevocationStore>(sp => sp.GetRequiredService<SqliteRemoteControlStore>());
        services.RemoveAll<IRemoteJobOriginStore>();
        services.AddSingleton<IRemoteJobOriginStore>(sp => sp.GetRequiredService<SqliteRemoteControlStore>());
        return services;
    }
}
