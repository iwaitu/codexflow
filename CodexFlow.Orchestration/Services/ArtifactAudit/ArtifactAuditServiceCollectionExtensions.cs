// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

#pragma warning disable CS1591 // internal audit surface; documented in .committee-review/claude-audit-api.md
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace CodexFlow.Services.ArtifactAudit;

public static class ArtifactAuditServiceCollectionExtensions
{
    public const string AdvisoryHttpClientName = "ArtifactAuditAdvisory";

    /// <summary>
    /// Registers the artifact audit stack. Uses an <see cref="IChatClient"/> from the container when present
    /// (otherwise the model layer reports NotRun and the audit can never Pass).
    /// </summary>
    public static IServiceCollection AddArtifactAudit(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.Configure<ArtifactAuditOptions>(configuration.GetSection(ArtifactAuditOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);

        // Fixed-endpoint client: no redirects, no cookies, no credentials, no proxy credentials.
        services.AddHttpClient(AdvisoryHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                Credentials = null,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
                ConnectTimeout = TimeSpan.FromSeconds(30)
            });
        services.TryAddSingleton<INuGetAdvisorySnapshotProvider>(sp => new HostManagedNuGetAdvisoryProvider(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(AdvisoryHttpClientName),
            sp.GetRequiredService<IOptions<ArtifactAuditOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<HostManagedNuGetAdvisoryProvider>>()));

        // OSV advisory data for the lockfile-based ecosystems (npm, PyPI); read from a host directory, never from the network or the repository.
        services.TryAddSingleton<CodexFlow.Services.ArtifactAudit.Ecosystems.IOsvSnapshotProvider>(sp =>
            new CodexFlow.Services.ArtifactAudit.Ecosystems.DirectoryOsvSnapshotProvider(
                sp.GetRequiredService<IOptions<ArtifactAuditOptions>>().Value.OsvAdvisoryDirectory));

        services.TryAddSingleton<IArtifactModelAuditor>(sp =>
            sp.GetService<IChatClient>() is { } chat
                ? new ChatClientArtifactModelAuditor(chat, sp.GetRequiredService<IOptions<ArtifactAuditOptions>>().Value)
                : new UnavailableArtifactModelAuditor());
        services.TryAddSingleton<IArtifactAuditService, ArtifactAuditService>();

        // Dependency-risk facts for the analysis turn and for the verification baseline (docs/dependency-baseline.md).
        services.TryAddSingleton<IDependencyRiskScanService>(sp => new DependencyRiskScanService(
            sp.GetRequiredService<CodexFlow.Core.Execution.IIsolatedExecutionService>(),
            sp.GetRequiredService<IOptions<ArtifactAuditOptions>>(),
            sp.GetRequiredService<INuGetAdvisorySnapshotProvider>(),
            configuration,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<DependencyRiskScanService>>(),
            sp.GetService<CodexFlow.Services.ArtifactAudit.Ecosystems.IOsvSnapshotProvider>()));
        // Host-side package acquisition: the reviewed cache builder fetches the upgrade targets the host chose into a per-session overlay.
        services.TryAddSingleton(sp => Microsoft.Extensions.Options.Options.Create(new RestoreOverlayOptions
        {
            Root = configuration["Isolation:RestoreOverlayRoot"],
            PrepareScript = configuration["Isolation:RestoreOverlayPrepareScript"],
            Python = configuration["Isolation:RestoreOverlayPython"] is { Length: > 0 } python ? python : "python"
        }));
        services.TryAddSingleton<IRestoreOverlayRunner, ProcessRestoreOverlayRunner>();
        services.TryAddSingleton<IRestoreOverlayService>(sp => new RestoreOverlayService(
            sp.GetRequiredService<IRestoreOverlayRunner>(),
            sp.GetRequiredService<IOptions<RestoreOverlayOptions>>(),
            configuration,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<RestoreOverlayService>>()));
        services.TryAddSingleton<INuGetVersionCatalog>(sp => new NuGetFlatContainerVersionCatalog(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(AdvisoryHttpClientName)));
        services.TryAddSingleton<IDependencyUpgradeService>(sp => new DependencyUpgradeService(
            sp.GetRequiredService<INuGetVersionCatalog>(),
            sp.GetRequiredService<INuGetAdvisorySnapshotProvider>(),
            sp.GetRequiredService<IRestoreOverlayService>(),
            sp.GetRequiredService<IOptions<ArtifactAuditOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<DependencyUpgradeService>>()));
        services.TryAddSingleton<IDependencyUpgradeApprover>(sp => (IDependencyUpgradeApprover)sp.GetRequiredService<IDependencyUpgradeService>());
        services.TryAddSingleton<IDependencyUpgradeTargets>(sp => (IDependencyUpgradeTargets)sp.GetRequiredService<IDependencyUpgradeService>());
        if (configuration.GetValue("ArtifactAudit:Briefing:Enabled", true))
        {
            // IGitService is scoped and the host validates scopes: a singleton must never capture it (it reads HEAD through a scope per call).
            services.TryAddSingleton<IRepositoryHeadReader>(sp => new ScopedRepositoryHeadReader(sp.GetRequiredService<IServiceScopeFactory>()));
            services.TryAddSingleton<IDependencyRiskBriefingProvider>(sp => new DependencyRiskBriefingProvider(
                sp.GetRequiredService<IDependencyRiskScanService>(),
                sp.GetRequiredService<IRepositoryHeadReader>(),
                TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("ArtifactAudit:Briefing:TimeoutSeconds", 180), 5, 900)),
                sp.GetService<Microsoft.Extensions.Logging.ILogger<DependencyRiskBriefingProvider>>(),
                sp.GetRequiredService<IDependencyUpgradeService>()));
        }

        return services;
    }
}
