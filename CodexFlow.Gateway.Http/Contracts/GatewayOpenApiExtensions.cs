// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;

namespace CodexFlow.Gateway.Contracts;

public static class GatewayOpenApiExtensions
{
    public const string DocumentName = "v1";

    public static IServiceCollection AddCodexFlowGatewayOpenApi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOpenApi(DocumentName, options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "CodexFlow Local Gateway API";
                document.Info.Version = SseEventContractSchema.SchemaVersion;
                document.Info.Description = "HTTP & SSE contract for CodexFlow.Local and the Electron desktop client.";
                document.Servers?.Clear();
                return Task.CompletedTask;
            });
        });
        return services;
    }

    public static IEndpointConventionBuilder MapCodexFlowGatewayOpenApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapOpenApi("/openapi/{documentName}.json");
    }

    /// <summary>
    /// Generates the canonical OpenAPI JSON document and SSE event JSON Schema directly from the
    /// live controller metadata without requiring database or model connectivity.
    /// </summary>
    public static async Task<(string OpenApiJson, string SseSchemaJson)> GenerateContractsAsync(CancellationToken ct = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddApplicationPart(typeof(GatewayOpenApiExtensions).Assembly);
        builder.Services.AddCodexFlowGatewayOpenApi();

        await using var app = builder.Build();
        app.MapControllers();
        app.MapCodexFlowGatewayOpenApi();
        await app.StartAsync(ct).ConfigureAwait(false);

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        var openApiJson = await client.GetStringAsync($"/openapi/{DocumentName}.json", ct).ConfigureAwait(false);
        var sseSchemaJson = SseEventContractSchema.BuildSchemaJson();
        return (openApiJson.ReplaceLineEndings("\n"), sseSchemaJson.ReplaceLineEndings("\n"));
    }
}
