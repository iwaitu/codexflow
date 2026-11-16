// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CodexFlow.Core.Execution;

public static class IsolationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Docker isolation backend, the ambient trusted-context accessor and the shared tool gate.
    /// Options bind from the "Isolation" configuration section (defaults are secure: no network, bounded resources).
    /// If Docker is unreachable at run time every execution tool reports Unavailable; nothing runs on the host.
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
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Options.IPostConfigureOptions<ExecutionOptions>, ExecutionPermissionClamp>(
            sp => sp.GetRequiredService<ExecutionPermissionClamp>()));

        services.TryAddSingleton<ITrustedExecutionContextAccessor, TrustedExecutionContextAccessor>();
        services.TryAddSingleton<IDockerCli>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<IsolationOptions>>().Value;
            return new ProcessDockerCli(opts.DockerExecutable);
        });
        services.TryAddSingleton<DockerIsolatedExecutionService>();
        services.TryAddSingleton<LocalExecutionService>();
        services.TryAddSingleton<ExecutionBackendRouter>(sp => new ExecutionBackendRouter(
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<ExecutionOptions>>(),
            sp.GetRequiredService<DockerIsolatedExecutionService>(),
            sp.GetRequiredService<LocalExecutionService>()));
        services.TryAddSingleton<IIsolatedExecutionService>(sp => sp.GetRequiredService<ExecutionBackendRouter>());
        services.TryAddSingleton(sp => new ToolIsolationGate(
            sp.GetRequiredService<IIsolatedExecutionService>(),
            sp.GetRequiredService<ITrustedExecutionContextAccessor>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<IsolationOptions>>().Value));
        services.TryAddSingleton<IsolationReaper>();
        services.TryAddSingleton<LocalStagingReaper>();
        return services;
    }
}
