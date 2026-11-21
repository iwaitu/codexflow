// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace CodexFlow.Gateway.Http;

/// <summary>Request context and HTTP response helpers shared by the Gateway route handlers.</summary>
public abstract class GatewayEndpointHandler
{
    protected HttpContext HttpContext { get; private set; } = null!;
    protected ClaimsPrincipal User => HttpContext.User;
    protected HttpRequest Request => HttpContext.Request;
    protected HttpResponse Response => HttpContext.Response;

    public void Bind(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        HttpContext = context;
    }

    protected static IResult Ok() => Results.Ok();
    protected static IResult Ok<T>(T value) => StatusCode(200, value);
    protected static IResult Accepted() => Results.Accepted();
    protected static IResult Accepted<T>(T value) => StatusCode(202, value);
    protected static IResult Accepted<T>(string? uri, T value) => new GatewayResponse<T>(202, value, uri);
    protected static IResult NoContent() => Results.NoContent();
    protected static IResult BadRequest() => StatusCode(400);
    protected static IResult BadRequest<T>(T value) => StatusCode(400, value);
    protected static IResult Unauthorized() => StatusCode(401);
    protected static IResult Unauthorized<T>(T value) => StatusCode(401, value);
    protected static IResult Forbid() => Results.Forbid();
    protected static IResult NotFound() => StatusCode(404);
    protected static IResult NotFound<T>(T value) => StatusCode(404, value);
    protected static IResult Conflict() => StatusCode(409);
    protected static IResult Conflict<T>(T value) => StatusCode(409, value);
    protected static IResult UnprocessableEntity<T>(T value) => StatusCode(422, value);
    protected static IResult Content(string value, string? contentType = null) => Results.Content(value, contentType);
    protected static IResult File(byte[] contents, string? contentType, string? fileDownloadName = null)
        => Results.File(contents, contentType, fileDownloadName);

    protected static IResult StatusCode(int statusCode)
        => statusCode is >= 400 and < 600 ? Results.Problem(statusCode: statusCode) : Results.StatusCode(statusCode);

    protected static IResult StatusCode<T>(int statusCode, T value) => new GatewayResponse<T>(statusCode, value);

    protected static IResult Problem(string? detail = null, int? statusCode = null, string? title = null)
        => Results.Problem(detail: detail, statusCode: statusCode, title: title);

    protected static IResult ValidationProblem(string detail)
        => Results.ValidationProblem(new Dictionary<string, string[]>(), detail: detail);

    private sealed class GatewayResponse<T>(int statusCode, T value, string? location = null) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            context.Response.StatusCode = statusCode;
            if (location is not null)
                context.Response.Headers.Location = location;

            if (value is null)
            {
                if (statusCode is >= 200 and < 300)
                    context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            var contentType = SelectContentType(context.Request, value is string);
            if (value is string message && contentType == "text/plain")
            {
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync(message, context.RequestAborted).ConfigureAwait(false);
                return;
            }

            var options = context.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
            var payloadType = typeof(T) == typeof(object) ? value.GetType() : typeof(T);
            JsonTypeInfo typeInfo = options.GetTypeInfo(payloadType);
            await context.Response.WriteAsJsonAsync(value, typeInfo, $"{contentType}; charset=utf-8", context.RequestAborted)
                .ConfigureAwait(false);
        }

        private static string SelectContentType(HttpRequest request, bool isString)
        {
            var defaultType = isString ? "text/plain" : "application/json";
            var accept = request.GetTypedHeaders().Accept;
            // Preserve the browser wildcard behavior of the previous formatters.
            if (accept is null || accept.Any(header => IsMediaType(header.MediaType.Value, "*/*")))
                return defaultType;

            var contentType = defaultType;
            var bestQuality = 0.0;
            foreach (var header in accept)
            {
                var mediaType = header.MediaType.Value;
                var quality = header.Quality ?? 1;
                if (quality <= bestQuality)
                    continue;

                if (isString && (IsMediaType(mediaType, "text/plain") || IsMediaType(mediaType, "text/*")))
                    contentType = "text/plain";
                else if (IsMediaType(mediaType, "text/json") || IsMediaType(mediaType, "text/*"))
                    contentType = "text/json";
                else if (IsMediaType(mediaType, "application/json") || IsMediaType(mediaType, "application/*"))
                    contentType = "application/json";
                else
                    continue;

                bestQuality = quality;
            }
            return contentType;
        }

        private static bool IsMediaType(string? value, string mediaType)
            => string.Equals(value, mediaType, StringComparison.OrdinalIgnoreCase);
    }
}
