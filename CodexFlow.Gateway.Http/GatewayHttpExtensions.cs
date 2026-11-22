// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CodexFlow.Controllers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CodexFlow.Gateway.Http;

public static class GatewayHttpExtensions
{
    public static IServiceCollection AddCodexFlowGateway(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.ConfigureHttpJsonOptions(options =>
        {
            var json = options.SerializerOptions;
            json.TypeInfoResolverChain.Insert(0, GatewayResponseJsonContext.Default);
            SimpleGatewayEndpoints.ConfigureJson(json);
            StreamingGatewayEndpoints.ConfigureJson(json);
            BusinessGatewayEndpoints.ConfigureJson(json);
            CodexFlow.Gateway.Http.Remote.RemoteInternalEndpoints.ConfigureJson(json);
        });
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
            context.ProblemDetails.Extensions.TryAdd("traceId", Activity.Current?.Id ?? context.HttpContext.TraceIdentifier));
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

        services.TryAddScoped<BackgroundJobController>();
        services.TryAddScoped<BrowserSessionController>();
        services.TryAddScoped<ComputerSessionController>();
        services.TryAddScoped<BusinessPluginsController>();
        services.TryAddScoped<BusinessSkillsController>();
        services.TryAddScoped<CodexGatewayController>();
        services.TryAddScoped<ExecutionSettingsController>();
        services.TryAddScoped<GatewayApprovalController>();
        services.TryAddScoped<GatewayArtifactController>();
        services.TryAddScoped<GatewaySessionWorkspaceController>();
        return services;
    }

    /// <summary>Formats failures from request binding on mapped Gateway endpoints.</summary>
    public static IApplicationBuilder UseCodexFlowGatewayBindingErrors(this IApplicationBuilder app)
        => app.Use(static (context, next) => CodexFlow.Gateway.Http.Remote.RemoteRequestContextMiddleware.InvokeAsync(context, next))
              .Use(static (context, next) => GatewayBinding.InvokeAsync(context, next));

    public static IEndpointRouteBuilder MapCodexFlowGateway(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var gateway = endpoints.MapGroup(string.Empty).WithMetadata(GatewayBinding.EndpointMarker);
        ((IEndpointConventionBuilder)gateway).Finally(endpoint =>
        {
            var accepts = endpoint.Metadata.OfType<IAcceptsMetadata>().LastOrDefault();
            if (accepts?.ContentTypes.Contains("application/json", StringComparer.OrdinalIgnoreCase) != true)
                return;

            // Let the authenticated Gateway endpoint handle media checks, including legacy text/json.
            endpoint.Metadata.Add(new GatewayBinding.JsonBody(accepts.IsOptional));
            endpoint.Metadata.Add(new AcceptsMetadata(["*/*"], accepts.RequestType, accepts.IsOptional));
        });
        gateway.AddEndpointFilterFactory(GatewayBinding.ValidateBody);
        SimpleGatewayEndpoints.Map(gateway);
        StreamingGatewayEndpoints.Map(gateway);
        BusinessGatewayEndpoints.Map(gateway);
        CodexFlow.Gateway.Http.Remote.RemoteInternalEndpoints.MapCodexFlowRemoteInternalEndpoints(endpoints);
        return endpoints;
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(long?))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(HttpValidationProblemDetails))]
internal sealed partial class GatewayResponseJsonContext : JsonSerializerContext;
