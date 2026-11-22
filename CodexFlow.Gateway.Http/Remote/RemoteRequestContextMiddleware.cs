// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Core.Remote;
using Microsoft.AspNetCore.Mvc;

namespace CodexFlow.Gateway.Http.Remote;

/// <summary>
/// Validates managed Electron remote bridge tokens and revocation watermarks, binds verified
/// <see cref="RemoteRequestMetadata"/> onto <see cref="HttpContext.Items"/>, and enters an orthogonal
/// <see cref="RemoteExecutionAmbientContext"/> scope for downstream services and tools.
/// </summary>
public static class RemoteRequestContextMiddleware
{
    private const string MetadataItemKey = "__CodexFlow.RemoteRequestMetadata";

    public static RemoteRequestMetadata? GetMetadata(HttpContext? context)
    {
        if (context is null)
        {
            return null;
        }

        return context.Items.TryGetValue(MetadataItemKey, out var value) && value is RemoteRequestMetadata meta
            ? meta
            : null;
    }

    public static bool IsRemoteRequest(HttpContext? context)
        => GetMetadata(context)?.Channel == ControlChannel.Remote || RemoteExecutionAmbientContext.IsRemote;

    public static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (!context.Request.Headers.TryGetValue(RemoteBridgeHeaderNames.ControlChannelHeader, out var channelValues) ||
            channelValues.Count == 0 ||
            string.IsNullOrWhiteSpace(channelValues[0]))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var rawChannel = channelValues[0]!.Trim();
        if (!string.Equals(rawChannel, "Remote", StringComparison.OrdinalIgnoreCase))
        {
            await WriteForbiddenAsync(context, "remote_channel_invalid", "Unsupported remote control channel header.").ConfigureAwait(false);
            return;
        }

        var bridgeAuth = context.RequestServices.GetService<IRemoteBridgeAuthenticator>();
        var candidateBridgeToken = context.Request.Headers[RemoteBridgeHeaderNames.BridgeToken].ToString();
        if (bridgeAuth is null || !bridgeAuth.IsBridgeAvailable || !bridgeAuth.ValidateBridgeToken(candidateBridgeToken))
        {
            await WriteForbiddenAsync(context, "remote_bridge_unauthorized", "Remote control channel requires a valid managed bridge token.").ConfigureAwait(false);
            return;
        }

        if (!TryParseStamp(context.Request.Headers, out var stamp, out var errorReason))
        {
            await Results.Problem(
                title: "invalid_remote_principal_headers",
                detail: errorReason,
                statusCode: StatusCodes.Status400BadRequest).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        var revocations = context.RequestServices.GetService<IRemoteRevocationStore>();
        if (revocations is not null && await revocations.IsRevokedAsync(stamp, context.RequestAborted).ConfigureAwait(false))
        {
            await WriteForbiddenAsync(context, "remote_principal_revoked", "Remote pairing or device credential has been revoked.").ConfigureAwait(false);
            return;
        }

        var metadata = new RemoteRequestMetadata(
            ControlChannel.Remote,
            stamp,
            NullIfEmpty(context.Request.Headers[RemoteBridgeHeaderNames.UploadId].ToString()),
            NullIfEmpty(context.Request.Headers[RemoteBridgeHeaderNames.FileSha256].ToString()),
            NullIfEmpty(context.Request.Headers[RemoteBridgeHeaderNames.ClientOpId].ToString()),
            NullIfEmpty(context.Request.Headers[RemoteBridgeHeaderNames.VerifiedEnvelopeKind].ToString()),
            NullIfEmpty(context.Request.Headers[RemoteBridgeHeaderNames.VerifiedEnvelopeHash].ToString()));

        context.Items[MetadataItemKey] = metadata;

        string? routeSessionId = context.Request.RouteValues.TryGetValue("sessionId", out var sidObj)
            ? sidObj?.ToString()
            : NullIfEmpty(context.Request.Query["sessionId"].ToString());

        var approvalBroker = context.RequestServices.GetService<IExecutionApprovalBroker>();
        using var _ = RemoteExecutionAmbientContext.BeginScope(
            ControlChannel.Remote,
            stamp,
            sessionId: routeSessionId,
            approvalBroker: approvalBroker);

        await next(context).ConfigureAwait(false);
    }

    private static bool TryParseStamp(IHeaderDictionary headers, out RemotePrincipalStamp stamp, out string reason)
    {
        stamp = null!;
        reason = string.Empty;

        if (!Guid.TryParse(headers[RemoteBridgeHeaderNames.PairingId].ToString(), out var pairingId) || pairingId == Guid.Empty)
        {
            reason = "Missing or invalid X-CF-Pairing-Id header.";
            return false;
        }

        if (!int.TryParse(headers[RemoteBridgeHeaderNames.BindingVersion].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bindingVersion) ||
            bindingVersion < 1)
        {
            reason = "Missing or invalid X-CF-Binding-Version header.";
            return false;
        }

        if (!long.TryParse(headers[RemoteBridgeHeaderNames.AccountRevocationSeq].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var accountRevSeq) ||
            accountRevSeq < 0)
        {
            reason = "Missing or invalid X-CF-Account-Rev-Seq header.";
            return false;
        }

        if (!long.TryParse(headers[RemoteBridgeHeaderNames.DeviceRevocationSeq].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var deviceRevSeq) ||
            deviceRevSeq < 0)
        {
            reason = "Missing or invalid X-CF-Device-Rev-Seq header.";
            return false;
        }

        stamp = new RemotePrincipalStamp(pairingId, bindingVersion, accountRevSeq, deviceRevSeq);
        return true;
    }

    private static Task WriteForbiddenAsync(HttpContext context, string code, string detail)
        => Results.Problem(
            title: code,
            detail: detail,
            statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(context);

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
