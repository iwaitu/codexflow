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

        services.TryAddSingleton<ITrustedExecutionContextAccessor, TrustedExecutionContextAccessor>();
        services.TryAddSingleton<IDockerCli>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<IsolationOptions>>().Value;
            return new ProcessDockerCli(opts.DockerExecutable);
        });
        services.TryAddSingleton<DockerIsolatedExecutionService>();
        services.TryAddSingleton<IIsolatedExecutionService>(sp => sp.GetRequiredService<DockerIsolatedExecutionService>());
        services.TryAddSingleton(sp => new ToolIsolationGate(
            sp.GetRequiredService<IIsolatedExecutionService>(),
            sp.GetRequiredService<ITrustedExecutionContextAccessor>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<IsolationOptions>>().Value));
        services.TryAddSingleton<IsolationReaper>();
        return services;
    }
}
