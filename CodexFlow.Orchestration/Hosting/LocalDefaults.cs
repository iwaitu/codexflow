// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application;
using CodexFlow.Application.Services;
using CodexFlow.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CodexFlow.Orchestration.Hosting;

/// <summary>A host with no stored user credentials: repositories are reached with whatever git itself is configured to use.</summary>
public sealed class NullUserCredentialSource : IUserCredentialSource
{
    public Task<string> GetGithubTokenAsync(string userId, CancellationToken ct) => Task.FromResult(string.Empty);
}

/// <summary>A host that does not meter usage.</summary>
public sealed class NullTokenUsageService : ITokenUsageService
{
    public Task RecordTokenUsageAsync(string userId, long promptTokens, long completionTokens, long cachedPromptTokens = 0)
        => Task.CompletedTask;
}

/// <summary>
/// A host whose user is at the terminal: a finished job is not announced through a notification channel, the user sees it in the
/// event stream and the job list.
/// </summary>
public sealed class NullNotificationDispatcher : CodexFlow.Application.Notifications.INotificationDispatcher
{
    public Task DispatchAsync(string jobId, CancellationToken ct, string dispatchSource = "unknown") => Task.CompletedTask;
}

public static class LocalDefaultsServiceCollectionExtensions
{
    /// <summary>
    /// What a host answers when it has none of the platform's optional parts: no browser push channel, no long-term memory, no
    /// vector store, no stored user credentials, no usage metering, no notification channels. Each is registered only if the host has not registered its own.
    /// </summary>
    public static IServiceCollection AddCodexFlowLocalDefaults(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IRealtimeNotifier, NullRealtimeNotifier>();
        services.TryAddScoped<IUserCredentialSource, NullUserCredentialSource>();
        services.TryAddScoped<ITokenUsageService, NullTokenUsageService>();
        services.TryAddScoped<IMemoryService, CodexFlow.Infrastructure.Persistence.NullMemoryService>();
        services.TryAddScoped<IWorkspaceVectorStoreService, NullWorkspaceVectorStoreService>();
        services.TryAddScoped<ISkillScriptRunner, CodexFlow.Infrastructure.Scripting.MockSkillScriptRunner>();
        // A finished or failed job is given back to the conversation (the coordinator tells the user what happened); no notification channels.
        services.TryAddScoped<CodexFlow.Application.Notifications.INotificationDispatcher, CodexFlow.Services.Notifications.MainSessionJobOutcomeDispatcher>();
        services.TryAddSingleton<IPushNotificationService, CodexFlow.Core.Services.InMemoryPushNotificationService>();
        return services;
    }
}
