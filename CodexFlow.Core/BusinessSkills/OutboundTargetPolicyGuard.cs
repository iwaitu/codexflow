// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using CodexFlow.Contracts.BusinessSkills;
using Microsoft.Extensions.Options;

namespace CodexFlow.Core.BusinessSkills;

public enum OutboundRequestPurpose
{
    Discovery = 0,
    Execution = 1,
    ExternalRef = 2,
    OAuthMetadata = 3
}

public sealed record TargetPolicyEvaluationResult(
    bool Allowed,
    string? ErrorCode,
    string? Message,
    Uri? NormalizedUri = null,
    bool IsEncryptedTransport = true,
    bool LoopbackAuthorized = false);

/// <summary>
/// P1 出站目标策略守卫：
/// 1. 仅允许授权 Origin 与路径前缀（允许合法企业私网，禁止路径穿越和未授权重定向）；
/// 2. 禁止非 HTTP(S) 协议、Basic 明文 HTTP、云元数据地址与 CodexFlow 本机控制面端口；
/// 3. 在 ConnectCallback 内核对解析后的全部 IP 地址并直接连向已检验 IP，防止 DNS 重绑定。
/// </summary>
public sealed class OutboundTargetPolicyGuard
{
    private static readonly IPAddress AlibabaMetadataIp = IPAddress.Parse("100.100.100.200");
    private static readonly IPAddress AwsIpv6MetadataIp = IPAddress.Parse("fd00:ec2::254");

    private readonly IOptionsMonitor<BusinessSkillsOptions>? _options;
    private readonly ConcurrentDictionary<int, byte> _runtimeProtectedLocalPorts = new();

    public OutboundTargetPolicyGuard(IOptionsMonitor<BusinessSkillsOptions>? options = null)
    {
        _options = options;
    }

    public void RegisterProtectedLocalPort(int port)
    {
        if (port is > 0 and <= 65535)
        {
            _runtimeProtectedLocalPorts[port] = 0;
        }
    }

    public TargetPolicyEvaluationResult ValidateUri(
        Uri targetUri,
        EndpointPolicyDto policy,
        BusinessAuthKind authKind,
        OutboundRequestPurpose purpose)
    {
        ArgumentNullException.ThrowIfNull(targetUri);
        ArgumentNullException.ThrowIfNull(policy);

        if (!targetUri.IsAbsoluteUri)
        {
            return Denied(BusinessSkillErrorCodes.ValidationFailed, "Target URI must be absolute.");
        }

        if (!string.IsNullOrEmpty(targetUri.UserInfo))
        {
            return Denied(BusinessSkillErrorCodes.SelectionViolation, "Target URI must not embed userinfo credentials.");
        }

        var scheme = targetUri.Scheme.ToLowerInvariant();
        if (scheme is not ("https" or "http"))
        {
            return Denied(BusinessSkillErrorCodes.UnsupportedProtocol, $"Scheme '{targetUri.Scheme}' is not allowed; only HTTP(S) is permitted.");
        }

        var isHttps = string.Equals(scheme, "https", StringComparison.Ordinal);
        if (!isHttps)
        {
            if (authKind == BusinessAuthKind.BasicTlsOnly)
            {
                return Denied(BusinessSkillErrorCodes.AuthRequired, "Basic authentication requires HTTPS and is refused over plain HTTP.");
            }

            if (!policy.AllowPlainHttpForIntranet)
            {
                return Denied(BusinessSkillErrorCodes.UnsupportedProtocol, "HTTPS is required unless AllowPlainHttpForIntranet is explicitly enabled.");
            }
        }

        if (!TryParseOrigin(policy.AllowedOrigin, out var allowedOriginUri))
        {
            return Denied(BusinessSkillErrorCodes.ValidationFailed, "EndpointPolicy.AllowedOrigin is invalid.");
        }

        var originMatched = OriginsEqual(targetUri, allowedOriginUri);
        if (!originMatched)
        {
            if (purpose == OutboundRequestPurpose.ExternalRef && policy.AllowExternalOpenApiRefs)
            {
                var matchedExtra = policy.AllowedExternalRefOrigins.Any(extra =>
                    TryParseOrigin(extra, out var extraUri) && OriginsEqual(targetUri, extraUri));
                if (!matchedExtra)
                {
                    return Denied(BusinessSkillErrorCodes.SelectionViolation,
                        $"External $ref target '{GetOriginString(targetUri)}' is not in AllowedExternalRefOrigins.");
                }
            }
            else
            {
                return Denied(BusinessSkillErrorCodes.SelectionViolation,
                    $"Target origin '{GetOriginString(targetUri)}' does not match authorized origin '{GetOriginString(allowedOriginUri)}'.");
            }
        }

        if (HasPathTraversal(targetUri))
        {
            return Denied(BusinessSkillErrorCodes.SelectionViolation, "Target URI path contains forbidden traversal segments.");
        }

        if (purpose != OutboundRequestPurpose.ExternalRef)
        {
            var normalizedPrefix = NormalizePathPrefix(policy.AllowedPathPrefix);
            var targetPath = string.IsNullOrEmpty(targetUri.AbsolutePath) ? "/" : targetUri.AbsolutePath;
            if (!IsPathWithinPrefix(targetPath, normalizedPrefix))
            {
                return Denied(BusinessSkillErrorCodes.SelectionViolation,
                    $"Target path '{targetPath}' is outside authorized AllowedPathPrefix '{normalizedPrefix}'.");
            }
        }

        var targetPort = targetUri.IsDefaultPort ? (isHttps ? 443 : 80) : targetUri.Port;
        if (IsProtectedLocalPort(targetPort))
        {
            return Denied(BusinessSkillErrorCodes.SelectionViolation,
                string.Format(CultureInfo.InvariantCulture, "Target port {0} is a protected local control port and cannot be dialed by plugins.", targetPort));
        }

        var loopbackAuthorized = originMatched && IsLoopbackHost(allowedOriginUri);
        if (IPAddress.TryParse(targetUri.IdnHost, out var literalIp) &&
            !IsIpAddressAllowed(literalIp, targetPort, loopbackAuthorized, out var ipReason))
        {
            return Denied(BusinessSkillErrorCodes.SelectionViolation, ipReason);
        }

        if (IsForbiddenMetadataHostname(targetUri.IdnHost))
        {
            return Denied(BusinessSkillErrorCodes.SelectionViolation, "Metadata service hostnames are forbidden.");
        }

        return new TargetPolicyEvaluationResult(
            true,
            null,
            null,
            targetUri,
            isHttps,
            loopbackAuthorized);
    }

    public async Task<TargetPolicyEvaluationResult> ValidateAndResolveAsync(
        Uri targetUri,
        EndpointPolicyDto policy,
        BusinessAuthKind authKind,
        OutboundRequestPurpose purpose,
        CancellationToken ct = default)
    {
        var staticEval = ValidateUri(targetUri, policy, authKind, purpose);
        if (!staticEval.Allowed || staticEval.NormalizedUri is null)
        {
            return staticEval;
        }

        var port = staticEval.NormalizedUri.IsDefaultPort
            ? (staticEval.IsEncryptedTransport ? 443 : 80)
            : staticEval.NormalizedUri.Port;

        var addresses = await ResolveAddressesAsync(staticEval.NormalizedUri.IdnHost, ct).ConfigureAwait(false);
        if (addresses.Length == 0)
        {
            return Denied(BusinessSkillErrorCodes.ConnectionUnavailable,
                $"DNS resolution returned no addresses for '{staticEval.NormalizedUri.IdnHost}'.");
        }

        foreach (var addr in addresses)
        {
            if (!IsIpAddressAllowed(addr, port, staticEval.LoopbackAuthorized, out var reason))
            {
                return Denied(BusinessSkillErrorCodes.SelectionViolation, reason);
            }
        }

        return staticEval;
    }

    /// <summary>
    /// 构造受策略保护的 HttpClient：关闭自动重定向和 Cookie，并在 ConnectCallback 中校验并直连安全 IP。
    /// </summary>
    public HttpClient CreateGuardedHttpClient(
        EndpointPolicyDto policy,
        BusinessAuthKind authKind,
        OutboundRequestPurpose purpose,
        TimeSpan? connectTimeout = null,
        TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var hasOrigin = TryParseOrigin(policy.AllowedOrigin, out var allowedOriginUri);
        var loopbackAuthorized = hasOrigin && IsLoopbackHost(allowedOriginUri);

        SocketsHttpHandler? handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(BusinessSkillDefaults.ConnectTimeoutSeconds),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (context, cancellationToken) =>
            {
                var host = context.DnsEndPoint.Host;
                var port = context.DnsEndPoint.Port;

                if (IsProtectedLocalPort(port) || IsForbiddenMetadataHostname(host))
                {
                    throw new HttpRequestException($"Outbound connection to '{host}:{port}' blocked by target policy.");
                }

                // 若连接阶段不是 HTTPS 但 Basic 认证严格要求 TLS，在此兜底防御
                if (authKind == BusinessAuthKind.BasicTlsOnly &&
                    hasOrigin &&
                    !string.Equals(allowedOriginUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    throw new HttpRequestException("Basic authentication is blocked over plain HTTP.");
                }

                var addresses = await ResolveAddressesAsync(host, cancellationToken).ConfigureAwait(false);
                if (addresses.Length == 0)
                {
                    throw new HttpRequestException($"No DNS addresses resolved for '{host}'.");
                }

                // 强制校验解析出的全部 IP（防止 DNS 返回混杂内网/元数据地址的重绑定攻击）
                foreach (var addr in addresses)
                {
                    if (!IsIpAddressAllowed(addr, port, loopbackAuthorized, out var reason))
                    {
                        throw new HttpRequestException(reason);
                    }
                }

                // 直接连接已校验的首个可用 IP，不交给操作系统第二次 DNS 解析
                Exception? lastError = null;
                foreach (var addr in addresses)
                {
                    var socket = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                    {
                        NoDelay = true
                    };
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(addr, port), cancellationToken).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                    {
                        socket.Dispose();
                        if (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }

                        lastError = ex;
                    }
                }

                throw new HttpRequestException($"Unable to connect to validated endpoints for '{host}:{port}'.", lastError);
            }
        };

        PolicyEnforcingHandler? policyHandler = null;
        try
        {
            policyHandler = new PolicyEnforcingHandler(this, policy, authKind, purpose, handler);
            handler = null;
            var client = new HttpClient(policyHandler, disposeHandler: true)
            {
                Timeout = requestTimeout ?? TimeSpan.FromSeconds(BusinessSkillDefaults.RequestTimeoutSeconds)
            };
            policyHandler = null;
            return client;
        }
        finally
        {
            policyHandler?.Dispose();
            handler?.Dispose();
        }
    }

    private sealed class PolicyEnforcingHandler(
        OutboundTargetPolicyGuard guard,
        EndpointPolicyDto policy,
        BusinessAuthKind authKind,
        OutboundRequestPurpose purpose,
        HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.RequestUri is null)
            {
                throw new HttpRequestException("Outbound request URI is missing.");
            }

            var eval = guard.ValidateUri(request.RequestUri, policy, authKind, purpose);
            if (!eval.Allowed)
            {
                throw new HttpRequestException(eval.Message ?? "Outbound request denied by target policy.");
            }

            return base.SendAsync(request, cancellationToken);
        }
    }

    public bool IsProtectedLocalPort(int port)
    {
        if (_runtimeProtectedLocalPorts.ContainsKey(port))
        {
            return true;
        }

        var configured = _options?.CurrentValue.ForbiddenLocalPorts;
        return configured is not null && configured.Contains(port);
    }

    public bool IsIpAddressAllowed(IPAddress rawAddress, int port, bool loopbackAuthorized, out string reason)
    {
        ArgumentNullException.ThrowIfNull(rawAddress);

        var address = rawAddress.IsIPv4MappedToIPv6 ? rawAddress.MapToIPv4() : rawAddress;

        if (IPAddress.Any.Equals(address) || IPAddress.IPv6Any.Equals(address))
        {
            reason = "Unspecified wildcard IP addresses (0.0.0.0 / ::) are forbidden.";
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            // 0.0.0.0/8
            if (bytes[0] == 0)
            {
                reason = $"Unspecified IP range '{address}' is forbidden.";
                return false;
            }

            // 169.254.0.0/16 云元数据及链路本地地址
            if (bytes[0] == 169 && bytes[1] == 254)
            {
                reason = $"Link-local/cloud metadata address '{address}' is forbidden.";
                return false;
            }

            // 224.0.0.0/4 多播
            if ((bytes[0] & 0xF0) == 224)
            {
                reason = $"Multicast address '{address}' is forbidden.";
                return false;
            }

            // 阿里云元数据 100.100.100.200
            if (AlibabaMetadataIp.Equals(address))
            {
                reason = $"Cloud metadata address '{address}' is forbidden.";
                return false;
            }
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6Multicast || AwsIpv6MetadataIp.Equals(address))
            {
                reason = $"Link-local/multicast/metadata IPv6 address '{address}' is forbidden.";
                return false;
            }
        }

        if (IPAddress.IsLoopback(address))
        {
            if (IsProtectedLocalPort(port))
            {
                reason = string.Format(CultureInfo.InvariantCulture, "Loopback access to CodexFlow local control port {0} is forbidden.", port);
                return false;
            }

            if (!loopbackAuthorized)
            {
                reason = $"Loopback target '{address}' requires explicit loopback EndpointPolicy authorization.";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    public static string GetOriginString(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var defaultPort = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80;
        var portPart = uri.Port == defaultPort || uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        return $"{uri.Scheme.ToLowerInvariant()}://{uri.IdnHost.ToLowerInvariant()}{portPart}";
    }

    public static bool TryParseOrigin(string? rawOrigin, out Uri originUri)
    {
        originUri = default!;
        if (string.IsNullOrWhiteSpace(rawOrigin) || !Uri.TryCreate(rawOrigin.Trim(), UriKind.Absolute, out var parsed))
        {
            return false;
        }

        var scheme = parsed.Scheme.ToLowerInvariant();
        if (scheme is not ("http" or "https"))
        {
            return false;
        }

        originUri = parsed;
        return true;
    }

    private static async Task<IPAddress[]> ResolveAddressesAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var ip))
        {
            return [ip];
        }

        return await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
    }

    private static bool OriginsEqual(Uri left, Uri right)
        => string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase) &&
           ResolveEffectivePort(left) == ResolveEffectivePort(right);

    private static int ResolveEffectivePort(Uri uri)
    {
        if (!uri.IsDefaultPort && uri.Port > 0)
        {
            return uri.Port;
        }

        return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80;
    }

    private static bool IsLoopbackHost(Uri uri)
        => string.Equals(uri.IdnHost, "localhost", StringComparison.OrdinalIgnoreCase) ||
           (IPAddress.TryParse(uri.IdnHost, out var ip) && IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip));

    private static bool IsForbiddenMetadataHostname(string host)
        => string.Equals(host, "metadata.google.internal", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(host, "metadata", StringComparison.OrdinalIgnoreCase);

    private static bool HasPathTraversal(Uri uri)
    {
        var original = uri.OriginalString;
        if (original.Contains("..", StringComparison.Ordinal))
        {
            var decoded = Uri.UnescapeDataString(uri.AbsolutePath);
            if (decoded.Split('/', '\\').Any(static s => s == ".."))
            {
                return true;
            }
        }

        var path = uri.AbsolutePath;
        return path.Split('/').Any(static s => s == "..");
    }

    private static string NormalizePathPrefix(string? rawPrefix)
    {
        if (string.IsNullOrWhiteSpace(rawPrefix))
        {
            return "/";
        }

        var trimmed = rawPrefix.Trim();
        if (!trimmed.StartsWith('/'))
        {
            trimmed = "/" + trimmed;
        }

        return trimmed.Length > 1 ? trimmed.TrimEnd('/') : trimmed;
    }

    private static bool IsPathWithinPrefix(string path, string normalizedPrefix)
    {
        if (normalizedPrefix == "/")
        {
            return true;
        }

        return string.Equals(path, normalizedPrefix, StringComparison.Ordinal) ||
               path.StartsWith(normalizedPrefix + "/", StringComparison.Ordinal);
    }

    private static TargetPolicyEvaluationResult Denied(string errorCode, string message)
        => new(false, errorCode, message);
}
