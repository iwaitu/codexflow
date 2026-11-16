// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Configuration;
using CodexFlow.Core.Execution.Approval;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodexFlow.Core.Execution;

public static class IsolationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Docker &amp; Local execution backends, permission clamping, interactive approval broker,
    /// ambient trusted-context accessor and the shared tool gate.
    /// </summary>
    public static IServiceCollection AddCodexIsolatedExecution(this IServiceCollection services, IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var section = configuration?.GetSection(IsolationOptions.SectionName);
        services.AddOptions<IsolationOptions>();
        if (section is not null)
        {
            services.Configure<IsolationOptions>(section);
        }

        var executionSection = configuration?.GetSection(ExecutionOptions.SectionName);
        services.AddOptions<ExecutionOptions>();
        if (executionSection is not null)
        {
            services.Configure<ExecutionOptions>(executionSection);
        }

        services.TryAddSingleton<ExecutionPermissionClamp>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<ExecutionOptions>, ExecutionPermissionClamp>(
            sp => sp.GetRequiredService<ExecutionPermissionClamp>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOptionsChangeTokenSource<ExecutionOptions>, ExecutionPermissionClamp>(
            sp => sp.GetRequiredService<ExecutionPermissionClamp>()));

        services.TryAddSingleton<IExecutionPermissionStore>(_ =>
            new ExecutionPermissionStore(CodexFlowPaths.GlobalPermissionsPath(CodexFlowPaths.GlobalDirectory())));
        services.TryAddSingleton<IExecutionApprovalBroker>(sp => new ExecutionApprovalBroker(
            sp.GetRequiredService<IOptionsMonitor<ExecutionOptions>>(),
            sp.GetRequiredService<IExecutionPermissionStore>(),
            sp.GetRequiredService<ILogger<ExecutionApprovalBroker>>(),
            sp));

        services.TryAddSingleton<ITrustedExecutionContextAccessor, TrustedExecutionContextAccessor>();
        services.TryAddSingleton<IDockerCli>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<IsolationOptions>>().Value;
            return new ProcessDockerCli(opts.DockerExecutable);
        });
        services.TryAddSingleton<DockerIsolatedExecutionService>();
        services.TryAddSingleton<LocalExecutionService>();
        services.TryAddSingleton<ExecutionBackendRouter>(sp => new ExecutionBackendRouter(
            sp.GetRequiredService<IOptionsMonitor<ExecutionOptions>>(),
            sp.GetRequiredService<DockerIsolatedExecutionService>(),
            sp.GetRequiredService<LocalExecutionService>()));
        services.TryAddSingleton<IIsolatedExecutionService>(sp => sp.GetRequiredService<ExecutionBackendRouter>());
        services.TryAddSingleton(sp => new ToolIsolationGate(
            sp.GetRequiredService<IIsolatedExecutionService>(),
            sp.GetRequiredService<ITrustedExecutionContextAccessor>(),
            sp.GetRequiredService<IOptions<IsolationOptions>>().Value,
            sp.GetRequiredService<IExecutionApprovalBroker>(),
            sp.GetRequiredService<IOptionsMonitor<ExecutionOptions>>(),
            sp));
        services.TryAddSingleton<IsolationReaper>();
        services.TryAddSingleton<LocalStagingReaper>();
        return services;
    }
}
