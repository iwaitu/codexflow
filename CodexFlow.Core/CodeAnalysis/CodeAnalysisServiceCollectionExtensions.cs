// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.CompilerServices;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.CodeAnalysis;

/// <summary>Registers workspace scanning and the snippet analyzer for the current runtime.</summary>
public static class CodeAnalysisServiceCollectionExtensions
{
    public static IServiceCollection AddCodexFlowCodeAnalysis(this IServiceCollection services)
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            services.TryAddSingleton(static _ => new CodeAnalysisWorkerClient());
        }

        services.AddScoped<ICodeAnalysisService>(CreateScanner);
        services.AddScoped<ICodexCritiqueService>(CreateScanner);
        return services;
    }

    private static SemanticDependencyScanner CreateScanner(IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILogger<SemanticDependencyScanner>>();
        return RuntimeFeature.IsDynamicCodeSupported
            ? new SemanticDependencyScanner(logger, new RoslynCodeAnalysisService(logger))
            : new SemanticDependencyScanner(logger, services.GetRequiredService<CodeAnalysisWorkerClient>());
    }
}

internal interface ICancellableSnippetAnalysis
{
    Task<List<CodeDiagnostic>> AnalyzeSnippetAsync(string code, CancellationToken cancellationToken);
}
