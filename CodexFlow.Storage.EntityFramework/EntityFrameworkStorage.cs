// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application;
using CodexFlow.Application.Jobs;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Browser;
using CodexFlow.Infrastructure.Persistence;
using CodexFlow.Orchestration.BusinessSkills;
using CodexFlow.Orchestration.Chat;
using CodexFlow.Orchestration.Hosting;
using CodexFlow.Orchestration.Jobs;
using CodexFlow.Services.Background;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Storage.EntityFramework;

public static class EntityFrameworkStorage
{
    /// <summary>Uses the platform's registered scoped context and model for CodexFlow storage.</summary>
    /// <remarks>The platform continues to supply its event log, key-value store and provider-specific sequence allocation.</remarks>
    public static IServiceCollection AddCodexFlowEfStorage<TContext>(this IServiceCollection services)
        where TContext : DbContext, IJobDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IJobDbContext>(sp => sp.GetRequiredService<TContext>());
        services.TryAddScoped<ISessionDirectory, EfSessionDirectory>();
        services.TryAddScoped<IConversationDirectory, EfConversationDirectory>();
        services.TryAddScoped<IJobStore, EfJobStore>();
        services.TryAddScoped<IOutboxStore, EfOutboxStore>();
        services.TryAddScoped<IJobReadStore, EfJobReadStore>();
        services.TryAddScoped<IJobRunnerStore, EfJobRunnerStore>();
        services.TryAddScoped<IWorkerJobStore, EfWorkerJobStore>();
        services.TryAddScoped<IJobOutcomeStore, EfJobOutcomeStore>();
        services.TryAddScoped<IJobLeaseStore, EfJobLeaseStore>();
        services.TryAddScoped<IJobLeaseRecoveryStore, EfJobLeaseRecoveryStore>();
        services.TryAddScoped<IPlanArtifactRepository, EfPlanArtifactRepository>();
        services.TryAddScoped<IPlanArtifactStore, PlanArtifactStore>();
        services.TryAddScoped<EfSessionStateStore>();
        services.TryAddScoped<ISessionPolicyStore>(sp => sp.GetRequiredService<EfSessionStateStore>());
        services.TryAddScoped<ISessionCompressionPersistence>(sp => sp.GetRequiredService<EfSessionStateStore>());
        services.TryAddScoped<ILateToolOutputStore>(sp => sp.GetRequiredService<EfSessionStateStore>());
        services.TryAddScoped<ISessionCompressionStore>(sp => new SessionCompressionStore(
            sp.GetRequiredService<ISessionCompressionPersistence>(), sp.GetRequiredService<ILogger<SessionCompressionStore>>()));
        services.TryAddScoped<IChatPersistence, EfChatPersistence>();
        services.TryAddScoped<IConversationMessageStore, EfConversationMessageStore>();
        services.TryAddScoped<IChatService>(sp => new ChatService(
            sp.GetRequiredService<IChatPersistence>(), sp.GetRequiredService<ILogger<ChatService>>(),
            sp.GetService<IWorkspaceRootProvider>(), sp.GetRequiredService<IConversationMessageStore>(), sp.GetService<IBrowserSessionManager>()));
        services.TryAddScoped<IGatewayChatStore>(sp => sp.GetRequiredService<IChatService>());
        services.TryAddScoped<IBusinessSkillExecutionStore, EfBusinessSkillExecutionStore>();
        services.TryAddScoped<IBusinessSkillBuilderStore, EfBusinessSkillExecutionStore>();
        services.TryAddScoped<IBusinessSkillRetentionStore, EfBusinessSkillRetentionStore>();
        services.TryAddScoped<IBusinessPluginStore, EfBusinessPluginStore>();
        return services;
    }
}
