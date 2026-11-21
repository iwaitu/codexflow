// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace CodexFlow.Gateway.Http;

internal static class GatewayBinding
{
    public static object EndpointMarker { get; } = new GatewayEndpointMarker();

    public static object OptionalEmptyQuery(string name) => new QueryParameter(name, true, null);
    public static object OptionalInt64Query(string name) => QueryValue<long>(name);
    public static object QueryValue<T>(string name, bool allowEmpty = false) where T : IParsable<T>
        => new QueryParameter(name, allowEmpty, static text => T.TryParse(text, CultureInfo.InvariantCulture, out _));

    public static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<GatewayEndpointMarker>() is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        try
        {
            if (endpoint.Metadata.GetMetadata<JsonBody>() is { } body &&
                (!body.IsOptional || context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true) &&
                !PrepareJsonBody(context.Request))
            {
                await Results.Problem(statusCode: 415, extensions: Trace(context)).ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            foreach (var parameter in endpoint.Metadata.GetOrderedMetadata<QueryParameter>())
            {
                if (!context.Request.Query.TryGetValue(parameter.Name, out var values))
                    continue;

                var value = values.Count > 0 ? values[0] ?? string.Empty : string.Empty;
                if (parameter.AllowEmpty && string.IsNullOrWhiteSpace(value))
                {
                    var query = new Dictionary<string, StringValues>(context.Request.Query, StringComparer.OrdinalIgnoreCase);
                    query.Remove(parameter.Name);
                    context.Request.Query = new QueryCollection(query);
                }
                else if (parameter.Validate is not null && !parameter.Validate(value))
                {
                    await ValidationError(context, parameter.Name, InvalidQueryValue(value))
                        .ExecuteAsync(context).ConfigureAwait(false);
                    return;
                }
                else if (values.Count > 1)
                {
                    var query = new Dictionary<string, StringValues>(context.Request.Query, StringComparer.OrdinalIgnoreCase)
                    {
                        [parameter.Name] = value,
                    };
                    context.Request.Query = new QueryCollection(query);
                }
            }
            await next(context).ConfigureAwait(false);
        }
        catch (BadHttpRequestException error) when (!context.Response.HasStarted)
        {
            await BindingFailure(context, error).ExecuteAsync(context).ConfigureAwait(false);
        }
    }

    private static bool PrepareJsonBody(HttpRequest request)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var media))
            return false;
        var contentType = media.MediaType.Value ?? string.Empty;
        var textJson = contentType.Equals("text/json", StringComparison.OrdinalIgnoreCase);
        if (!textJson && !contentType.Equals("application/json", StringComparison.OrdinalIgnoreCase) &&
            !(contentType.StartsWith("application/", StringComparison.OrdinalIgnoreCase) &&
              contentType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
            return false;

        if (media.Charset.HasValue)
        {
            try
            {
                var encoding = Encoding.GetEncoding(media.Charset.Value!.Trim('"'));
                if (encoding.CodePage != Encoding.UTF8.CodePage && encoding.CodePage != Encoding.Unicode.CodePage)
                    return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        if (textJson)
        {
            media.MediaType = "application/json";
            request.ContentType = media.ToString();
        }
        return true;
    }

    public static EndpointFilterDelegate ValidateBody(EndpointFilterFactoryContext _, EndpointFilterDelegate next)
        => invocation =>
        {
            var context = invocation.HttpContext;
            var body = context.GetEndpoint()?.Metadata.GetMetadata<IAcceptsMetadata>();
            if (body?.RequestType is not { } type)
                return next(invocation);

            var payload = invocation.Arguments.FirstOrDefault(argument => argument?.GetType() == type);
            if (payload is null)
                return next(invocation);

            var json = context.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
            var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
            InspectPayload(json, payload, type, string.Empty, errors, 0);
            return errors.Count == 0 ? next(invocation) : new ValueTask<object?>(ValidationErrors(context, errors));
        };

    public static IResult ValidationError(HttpContext context, string field, string message)
        => ValidationErrors(context, new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] });

    private static void InspectPayload(
        JsonSerializerOptions json, object payload, Type type, string prefix,
        Dictionary<string, string[]> errors, int depth)
    {
        if (depth >= 32 || !json.TryGetTypeInfo(type, out var typeInfo))
            return;

        if (typeInfo.Kind == JsonTypeInfoKind.Enumerable && payload is IEnumerable entries)
        {
            var index = 0;
            foreach (var entry in entries)
            {
                if (entry is not null)
                    InspectPayload(json, entry, entry.GetType(), $"{prefix}[{index}]", errors, depth + 1);
                index++;
            }
            return;
        }

        foreach (var property in typeInfo.Properties)
        {
            if (property.Get is not { } read)
                continue;

            var field = property.Name.Length == 0 ? property.Name : char.ToUpperInvariant(property.Name[0]) + property.Name[1..];
            var path = prefix.Length == 0 ? field : prefix + "." + field;
            var member = read(payload);
            if (member is not null)
                InspectPayload(json, member, property.PropertyType, path, errors, depth + 1);
            else if (!property.IsSetNullable)
                errors[path] = [$"The {field} field is required."];
        }
    }

    private static IResult BindingFailure(HttpContext context, BadHttpRequestException error)
    {
        if (error.StatusCode != StatusCodes.Status400BadRequest)
            return Results.Problem(statusCode: error.StatusCode, extensions: Trace(context));

        var message = error.Message;
        var parameter = ReadParameter(message);
        var body = context.GetEndpoint()?.Metadata.GetMetadata<IAcceptsMetadata>();
        if (error.InnerException is JsonException json)
        {
            var errors = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [json.Path ?? "$"] = [json.Message],
            };
            if (body is { IsOptional: false } && parameter.Length != 0)
                errors[parameter] = [$"The {parameter} field is required."];
            return ValidationErrors(context, errors);
        }

        if (message.Contains("from body", StringComparison.OrdinalIgnoreCase) || parameter.Length == 0 && body is not null)
        {
            var errors = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [string.Empty] = ["A non-empty request body is required."],
            };
            if (parameter.Length != 0)
                errors[parameter] = [$"The {parameter} field is required."];
            return ValidationErrors(context, errors);
        }

        var value = context.Request.Query[parameter].ToString();
        if (value.Length == 0 && context.Request.RouteValues.TryGetValue(parameter, out var routeValue))
            value = routeValue?.ToString() ?? string.Empty;
        return ValidationError(context, parameter, value.Length == 0 && !context.Request.Query.ContainsKey(parameter)
            ? $"The {parameter} field is required."
            : InvalidQueryValue(value));
    }

    private static string ReadParameter(string message)
    {
        var start = message.IndexOf('"');
        if (start < 0) return string.Empty;
        var end = message.IndexOf('"', start + 1);
        if (end < 0) return string.Empty;
        var name = message[(start + 1)..end];
        return name[(name.LastIndexOf(' ') + 1)..];
    }

    private static IResult ValidationErrors(HttpContext context, Dictionary<string, string[]> errors)
        => Results.ValidationProblem(errors, extensions: Trace(context));

    private static string InvalidQueryValue(string value)
        => value.Length == 0 ? "The value '' is invalid." : $"The value '{value}' is not valid.";

    private static Dictionary<string, object?> Trace(HttpContext context) => new(StringComparer.Ordinal)
    {
        ["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier,
    };

    private sealed class GatewayEndpointMarker;
    internal sealed record JsonBody(bool IsOptional);
    private sealed record QueryParameter(string Name, bool AllowEmpty, Func<string, bool>? Validate);
}
