// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using CodexFlow.Core.Security;

namespace CodexFlow.Core.Browser;

public sealed record RemoteCdpEndpointVerification(
    bool Allowed,
    string? ErrorCode,
    string? ErrorMessage,
    Uri? NormalizedUri = null,
    int Port = 0,
    IReadOnlyList<IPAddress>? VerifiedAddresses = null);

/// <summary>
/// Validates and masks remote CDP WebSocket URLs and normalizes domain allow/block patterns.
/// Prevents protocol smuggling (http://), cloud metadata access, protected local port probing,
/// DNS rebinding to loopback, and credential leakage in API/UI settings echoes.
/// </summary>
public static class BrowserRemoteEndpointGuard
{
    private const int MaxRemoteWsUrlLength = 2048;
    private const int MaxDomainPatternLength = 253;
    private static readonly IdnMapping Idn = new();

    public static string? NormalizeAndValidateDomainPattern(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        var isWildcard = false;

        if (trimmed.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) ||
                parsed.Scheme is not ("http" or "https" or "ws" or "wss") ||
                string.IsNullOrWhiteSpace(parsed.IdnHost))
            {
                throw new ArgumentException($"域名规则 '{trimmed}' 不是有效的域名或网址。");
            }

            trimmed = parsed.IdnHost;
        }
        else if (trimmed.StartsWith("*.", StringComparison.Ordinal))
        {
            isWildcard = true;
            trimmed = trimmed[2..];
        }
        else if (trimmed.StartsWith('.'))
        {
            isWildcard = true;
            trimmed = trimmed[1..];
        }

        trimmed = trimmed.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException("域名规则不能为空或仅包含裸通配符（例如 '*' 或 '*.'）。");
        }

        if (trimmed.Length > MaxDomainPatternLength)
        {
            throw new ArgumentException($"域名规则 '{trimmed[..32]}...' 超过最大长度限制。");
        }

        if (trimmed.Contains('*', StringComparison.Ordinal) ||
            trimmed.Contains('/', StringComparison.Ordinal) ||
            trimmed.Contains('\\', StringComparison.Ordinal) ||
            trimmed.Contains('?', StringComparison.Ordinal) ||
            trimmed.Contains('#', StringComparison.Ordinal) ||
            trimmed.Contains('@', StringComparison.Ordinal) ||
            trimmed.Contains(':', StringComparison.Ordinal) ||
            trimmed.Any(static c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            throw new ArgumentException($"域名规则 '{raw.Trim()}' 包含不支持的通配符位置、路径、端口或非法字符。");
        }

        string asciiHost;
        try
        {
            asciiHost = Idn.GetAscii(trimmed).ToLowerInvariant();
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"域名规则 '{raw.Trim()}' 格式无效: {ex.Message}", ex);
        }

        if (isWildcard)
        {
            if (!asciiHost.Contains('.', StringComparison.Ordinal) ||
                asciiHost.StartsWith('.') ||
                asciiHost.EndsWith('.'))
            {
                throw new ArgumentException($"通配符域名规则 '*.{trimmed}' 必须指定明确的多级主域名（例如 '*.example.com'）。");
            }

            return $"*.{asciiHost}";
        }

        return asciiHost;
    }

    public static bool ContainsMaskPlaceholder(string? raw)
        => !string.IsNullOrWhiteSpace(raw) &&
           (raw.Contains("?***", StringComparison.Ordinal) || raw.Contains("***@", StringComparison.Ordinal));

    [SuppressMessage("Design", "CA1055:URI-like return values should not be strings", Justification = "Returns masked display text containing '?***' rather than a valid connectable URI.")]
    public static string? MaskRemoteCdpWsUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return ContainsMaskPlaceholder(trimmed) ? trimmed : "***";
        }

        var hasSecret = !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query);
        var hostPart = uri.IdnHost.Contains(':', StringComparison.Ordinal)
            ? $"[{uri.IdnHost.ToLowerInvariant()}]"
            : uri.IdnHost.ToLowerInvariant();
        var portPart = uri.IsDefaultPort
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $":{uri.Port}");
        var pathPart = string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath;
        var basePart = $"{uri.Scheme.ToLowerInvariant()}://{hostPart}{portPart}{pathPart}";
        return hasSecret ? $"{basePart}?***" : basePart;
    }

    public static RemoteCdpEndpointVerification ValidateRemoteCdpWsUrlSyntax(
        string? raw,
        ProtectedLocalPortRegistry? portRegistry = null)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Fail("remote_cdp_url_missing", "远端 CDP WebSocket 地址不能为空。");
        }

        var trimmed = raw.Trim();
        if (trimmed.Length > MaxRemoteWsUrlLength)
        {
            return Fail("remote_cdp_url_invalid", "远端 CDP 地址长度超过 2048 字符上限。");
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return Fail("remote_cdp_url_invalid", $"远端 CDP WebSocket 地址格式无效: '{MaskRemoteCdpWsUrl(trimmed)}'。");
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is not ("ws" or "wss"))
        {
            return Fail("remote_cdp_scheme_forbidden", $"远端 CDP 地址仅支持 direct ws:// 或 wss:// WebSocket 端点，禁止 '{uri.Scheme}:' 协议。");
        }

        if (NetworkAddressClassifier.HasForbiddenZoneId(uri.Host) ||
            NetworkAddressClassifier.HasForbiddenZoneId(uri.IdnHost) ||
            uri.Authority.Contains("%25", StringComparison.OrdinalIgnoreCase))
        {
            return Fail("remote_cdp_url_invalid", "远端 CDP 主机名不允许包含 IPv6 zone-id (%)。");
        }

        var host = uri.IdnHost;
        if (string.IsNullOrWhiteSpace(host))
        {
            return Fail("remote_cdp_url_invalid", "远端 CDP WebSocket 地址缺少有效主机名。");
        }

        var port = uri.IsDefaultPort ? (scheme == "wss" ? 443 : 80) : uri.Port;
        if (port is <= 0 or > 65535)
        {
            return Fail("remote_cdp_url_invalid", "远端 CDP 端口号无效。");
        }

        if (NetworkAddressClassifier.IsForbiddenMetadataHostname(host))
        {
            return Fail(BrowserTargetPolicyGuard.ReasonForbiddenMetadata, $"禁止将云元数据主机 '{host}' 配置为远端 CDP 端点。");
        }

        if (NetworkAddressClassifier.TryParseIpLiteral(host, out var literalIp))
        {
            var normalized = NetworkAddressClassifier.Normalize(literalIp);
            if (NetworkAddressClassifier.IsAlwaysForbidden(normalized, out var forbiddenReason))
            {
                return Fail(BrowserTargetPolicyGuard.ReasonForbiddenMetadata, forbiddenReason);
            }

            if (NetworkAddressClassifier.IsLoopback(normalized) && portRegistry?.IsProtected(port) == true)
            {
                return Fail(
                    BrowserTargetPolicyGuard.ReasonProtectedLocalPort,
                    string.Create(CultureInfo.InvariantCulture, $"端口 {port} 属于 CodexFlow 受保护控制/调试端口，禁止配置为远端 CDP 端点。"));
            }
        }
        else if (NetworkAddressClassifier.IsLoopbackHostname(host) && portRegistry?.IsProtected(port) == true)
        {
            return Fail(
                BrowserTargetPolicyGuard.ReasonProtectedLocalPort,
                string.Create(CultureInfo.InvariantCulture, $"端口 {port} 属于 CodexFlow 受保护控制/调试端口，禁止配置为远端 CDP 端点。"));
        }

        return new RemoteCdpEndpointVerification(
            Allowed: true,
            ErrorCode: null,
            ErrorMessage: null,
            NormalizedUri: uri,
            Port: port);
    }

    public static async Task<RemoteCdpEndpointVerification> ResolveAndVerifyRemoteCdpEndpointAsync(
        string? raw,
        ProtectedLocalPortRegistry portRegistry,
        Func<string, CancellationToken, Task<IPAddress[]>>? dnsResolver = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(portRegistry);

        var syntax = ValidateRemoteCdpWsUrlSyntax(raw, portRegistry);
        if (!syntax.Allowed || syntax.NormalizedUri is null)
        {
            return syntax;
        }

        var uri = syntax.NormalizedUri;
        var host = uri.IdnHost;
        var port = syntax.Port;
        var isOriginLoopbackHost = NetworkAddressClassifier.IsLoopbackHostname(host);

        IPAddress[] resolved;
        if (NetworkAddressClassifier.TryParseIpLiteral(host, out var literalIp))
        {
            resolved = [literalIp];
        }
        else if (isOriginLoopbackHost)
        {
            resolved = [IPAddress.Loopback];
        }
        else
        {
            try
            {
                var resolver = dnsResolver ?? (static (h, token) => Dns.GetHostAddressesAsync(h, token));
                resolved = await resolver(host, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException or InvalidOperationException)
            {
                return Fail(BrowserTargetPolicyGuard.ReasonDnsResolutionFailed, $"远端 CDP 主机 '{host}' DNS 解析失败: {ex.Message}");
            }
        }

        if (resolved.Length == 0)
        {
            return Fail(BrowserTargetPolicyGuard.ReasonDnsResolutionFailed, $"远端 CDP 主机 '{host}' 未返回任何可用 IP 地址。");
        }

        var verified = new List<IPAddress>(resolved.Length);
        foreach (var rawIp in resolved)
        {
            var ip = NetworkAddressClassifier.Normalize(rawIp);
            if (NetworkAddressClassifier.IsAlwaysForbidden(ip, out var forbiddenReason))
            {
                return Fail(BrowserTargetPolicyGuard.ReasonForbiddenMetadata, forbiddenReason);
            }

            if (NetworkAddressClassifier.IsLoopback(ip))
            {
                if (!isOriginLoopbackHost)
                {
                    if (portRegistry.IsProtected(port))
                    {
                        return Fail(
                            BrowserTargetPolicyGuard.ReasonProtectedLocalPort,
                            string.Create(CultureInfo.InvariantCulture, $"远端 CDP 域名 '{host}' 解析到受保护本机端口 {port}，已阻止。"));
                    }

                    return Fail(
                        BrowserTargetPolicyGuard.ReasonDnsRebindingLoopback,
                        $"外部远端 CDP 域名 '{host}' 解析到本机回环地址 '{ip}'，疑似 DNS 重绑定攻击，已严格禁止。");
                }

                if (portRegistry.IsProtected(port))
                {
                    return Fail(
                        BrowserTargetPolicyGuard.ReasonProtectedLocalPort,
                        string.Create(CultureInfo.InvariantCulture, $"本机端口 {port} 属于 CodexFlow 受保护控制/调试端口，禁止作为远端 CDP 端点。"));
                }
            }

            if (!verified.Contains(ip))
            {
                verified.Add(ip);
            }
        }

        return new RemoteCdpEndpointVerification(
            Allowed: true,
            ErrorCode: null,
            ErrorMessage: null,
            NormalizedUri: uri,
            Port: port,
            VerifiedAddresses: verified);
    }

    private static RemoteCdpEndpointVerification Fail(string code, string message)
        => new(false, code, message);
}
