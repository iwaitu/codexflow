// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using CodexFlow.Core.Models;
using CodexFlow.Core.Security;
using Microsoft.Extensions.Options;

namespace CodexFlow.Core.Browser;

public sealed record BrowserPolicyEvaluation(
    bool Allowed,
    string? ReasonCode,
    string? Message,
    Uri? NormalizedUri = null,
    IReadOnlyList<IPAddress>? VerifiedAddresses = null,
    bool RequiresLoopbackPortApproval = false,
    int TargetPort = 0);

/// <summary>
/// Evaluates navigation URLs and outbound proxy connections for managed browser sessions.
/// Enforces protocol, protected local port, cloud metadata, private network, loopback port,
/// domain allow/block lists, and strict DNS rebinding defense (external domains resolving to loopback
/// are ALWAYS denied even if the port is on the development loopback allowlist).
/// </summary>
public sealed class BrowserTargetPolicyGuard
{
    public const string ReasonInvalidUrl = "invalid_url";
    public const string ReasonForbiddenScheme = "forbidden_scheme";
    public const string ReasonProtectedLocalPort = "protected_local_port";
    public const string ReasonForbiddenMetadata = "forbidden_metadata";
    public const string ReasonForbiddenPrivateNetwork = "forbidden_private_network";
    public const string ReasonLoopbackDenied = "loopback_denied";
    public const string ReasonDnsRebindingLoopback = "dns_rebinding_loopback";
    public const string ReasonLoopbackApprovalRequired = "loopback_port_approval_required";
    public const string ReasonDomainBlocked = "domain_blocked";
    public const string ReasonDomainNotAllowed = "domain_not_allowed";
    public const string ReasonDnsResolutionFailed = "dns_resolution_failed";

    private static readonly IdnMapping Idn = new();

    private readonly IOptionsMonitor<BrowserOptions> _options;
    private readonly ProtectedLocalPortRegistry _portRegistry;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _dnsResolver;

    public BrowserTargetPolicyGuard(
        IOptionsMonitor<BrowserOptions> options,
        ProtectedLocalPortRegistry portRegistry,
        Func<string, CancellationToken, Task<IPAddress[]>>? dnsResolver = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(portRegistry);
        _options = options;
        _portRegistry = portRegistry;
        _dnsResolver = dnsResolver ?? DefaultResolveAsync;
    }

    public BrowserPolicyEvaluation ValidateNavigationUrl(
        string? rawUrl,
        SessionWorkMode workMode,
        IReadOnlySet<int>? sessionApprovedLoopbackPorts = null)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return Denied(ReasonInvalidUrl, "URL 不能为空。");
        }

        if (!Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return Denied(ReasonInvalidUrl, $"URL 格式无效: '{rawUrl}'。");
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is not ("http" or "https"))
        {
            return Denied(ReasonForbiddenScheme, $"浏览器禁止访问 '{uri.Scheme}:' 协议，仅允许 http:// 与 https://。");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return Denied(ReasonInvalidUrl, "URL 不允许内嵌 userinfo 凭据。");
        }

        // Reject raw IPv6 zone-id encodings (e.g., [::1%251] or [::1%1]) before any host check.
        if (NetworkAddressClassifier.HasForbiddenZoneId(uri.Host) ||
            NetworkAddressClassifier.HasForbiddenZoneId(uri.IdnHost) ||
            uri.Authority.Contains("%25", StringComparison.OrdinalIgnoreCase))
        {
            return Denied(ReasonInvalidUrl, "URL 主机名不允许包含 IPv6 zone-id (%)。");
        }

        var host = uri.IdnHost;
        var port = uri.IsDefaultPort ? (scheme == "https" ? 443 : 80) : uri.Port;
        var staticHostCheck = ValidateHostAndPortSyntax(host, port, workMode, sessionApprovedLoopbackPorts);
        if (!staticHostCheck.Allowed)
        {
            return staticHostCheck with { NormalizedUri = uri };
        }

        return new BrowserPolicyEvaluation(
            Allowed: true,
            ReasonCode: null,
            Message: null,
            NormalizedUri: uri,
            TargetPort: port);
    }

    public async Task<BrowserPolicyEvaluation> ResolveAndValidateEndpointAsync(
        string host,
        int port,
        SessionWorkMode workMode,
        IReadOnlySet<int>? sessionApprovedLoopbackPorts = null,
        CancellationToken ct = default)
    {
        var syntaxCheck = ValidateHostAndPortSyntax(host, port, workMode, sessionApprovedLoopbackPorts);
        if (!syntaxCheck.Allowed)
        {
            return syntaxCheck;
        }

        var normalizedHost = NormalizeHostnameAscii(host);
        var options = _options.CurrentValue;
        var isOriginLoopbackHost = NetworkAddressClassifier.IsLoopbackHostname(normalizedHost);
        var explicitlyAllowedDomain = !isOriginLoopbackHost && MatchesDomainList(normalizedHost, options.AllowedDomains);

        IPAddress[] resolved;
        if (NetworkAddressClassifier.TryParseIpLiteral(normalizedHost, out var literalIp))
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
                resolved = await _dnsResolver(normalizedHost, ct).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                return Denied(ReasonDnsResolutionFailed, $"域名 '{normalizedHost}' DNS 解析失败: {ex.Message}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return Denied(ReasonDnsResolutionFailed, $"域名 '{normalizedHost}' 解析异常: {ex.Message}");
            }
        }

        if (resolved.Length == 0)
        {
            return Denied(ReasonDnsResolutionFailed, $"域名 '{normalizedHost}' 未返回任何可用 IP 地址。");
        }

        var verified = new List<IPAddress>(resolved.Length);
        foreach (var rawIp in resolved)
        {
            var ip = NetworkAddressClassifier.Normalize(rawIp);

            if (NetworkAddressClassifier.IsAlwaysForbidden(ip, out var forbiddenReason))
            {
                return Denied(ReasonForbiddenMetadata, forbiddenReason);
            }

            if (NetworkAddressClassifier.IsLoopback(ip))
            {
                // P0-3: External domains resolving to loopback (DNS rebinding) are ALWAYS denied,
                // regardless of Development loopback port allowlists or AllowedDomains.
                if (!isOriginLoopbackHost)
                {
                    if (_portRegistry.IsProtected(port))
                    {
                        return Denied(
                            ReasonProtectedLocalPort,
                            string.Create(CultureInfo.InvariantCulture, $"域名 '{normalizedHost}' 解析到受保护本机控制端口 {port}，已阻止。"));
                    }

                    return Denied(
                        ReasonDnsRebindingLoopback,
                        $"外部域名 '{normalizedHost}' 解析到本机回环地址 '{ip}'，疑似 DNS 重绑定攻击，已严格禁止。");
                }

                var loopbackEval = EvaluateLoopbackPort(normalizedHost, port, workMode, options, sessionApprovedLoopbackPorts);
                if (!loopbackEval.Allowed)
                {
                    return loopbackEval;
                }
            }
            else if (NetworkAddressClassifier.IsPrivateOrInternalNetwork(ip) && !explicitlyAllowedDomain)
            {
                return Denied(
                    ReasonForbiddenPrivateNetwork,
                    $"目标 '{normalizedHost}' 解析到内网/保留地址 '{ip}'，默认禁止访问（如需访问请通过控制面将其域名加入 AllowedDomains 白名单）。");
            }

            verified.Add(ip);
        }

        return new BrowserPolicyEvaluation(
            Allowed: true,
            ReasonCode: null,
            Message: null,
            VerifiedAddresses: verified,
            TargetPort: port);
    }

    public static string GetOriginString(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var defaultPort = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80;
        var portPart = uri.Port == defaultPort || uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        return $"{uri.Scheme.ToLowerInvariant()}://{uri.IdnHost.ToLowerInvariant()}{portPart}";
    }

    private BrowserPolicyEvaluation ValidateHostAndPortSyntax(
        string? host,
        int port,
        SessionWorkMode workMode,
        IReadOnlySet<int>? sessionApprovedLoopbackPorts)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return Denied(ReasonInvalidUrl, "目标主机名不能为空。");
        }

        if (NetworkAddressClassifier.HasForbiddenZoneId(host))
        {
            return Denied(ReasonInvalidUrl, "目标主机名不允许包含 IPv6 zone-id (%)。");
        }

        if (port is <= 0 or > 65535)
        {
            return Denied(ReasonInvalidUrl, string.Create(CultureInfo.InvariantCulture, $"目标端口无效: {port}。"));
        }

        var normalizedHost = NormalizeHostnameAscii(host);
        if (NetworkAddressClassifier.IsForbiddenMetadataHostname(normalizedHost))
        {
            return Denied(ReasonForbiddenMetadata, $"禁止访问云元数据主机 '{normalizedHost}'。");
        }

        var options = _options.CurrentValue;
        if (MatchesDomainList(normalizedHost, options.BlockedDomains))
        {
            return Denied(ReasonDomainBlocked, $"域名 '{normalizedHost}' 位于浏览器黑名单中，已阻止访问。");
        }

        var isLoopbackLiteral = NetworkAddressClassifier.IsLoopbackHostname(normalizedHost);
        if (isLoopbackLiteral)
        {
            return EvaluateLoopbackPort(normalizedHost, port, workMode, options, sessionApprovedLoopbackPorts);
        }

        var explicitlyAllowed = MatchesDomainList(normalizedHost, options.AllowedDomains);
        if (options.AllowedDomains.Count > 0 && !explicitlyAllowed)
        {
            return Denied(ReasonDomainNotAllowed, $"域名 '{normalizedHost}' 不在浏览器 AllowedDomains 白名单中。");
        }

        if (NetworkAddressClassifier.TryParseIpLiteral(normalizedHost, out var ip))
        {
            if (NetworkAddressClassifier.IsAlwaysForbidden(ip, out var forbiddenReason))
            {
                return Denied(ReasonForbiddenMetadata, forbiddenReason);
            }

            if (NetworkAddressClassifier.IsPrivateOrInternalNetwork(ip) && !explicitlyAllowed)
            {
                return Denied(ReasonForbiddenPrivateNetwork, $"目标地址 '{ip}' 属于内网/保留网段，默认禁止浏览器访问。");
            }
        }

        return new BrowserPolicyEvaluation(true, null, null, TargetPort: port);
    }

    private BrowserPolicyEvaluation EvaluateLoopbackPort(
        string hostDisplay,
        int port,
        SessionWorkMode workMode,
        BrowserOptions options,
        IReadOnlySet<int>? sessionApprovedLoopbackPorts)
    {
        if (_portRegistry.IsProtected(port))
        {
            return Denied(
                ReasonProtectedLocalPort,
                string.Create(CultureInfo.InvariantCulture, $"本机端口 {port} 属于 CodexFlow 受保护控制/调试端口，严禁浏览器访问。"));
        }

        if (workMode == SessionWorkMode.Daily)
        {
            if (options.AllowLocalhostInDailyMode)
            {
                return new BrowserPolicyEvaluation(true, null, null, TargetPort: port);
            }

            return Denied(
                ReasonLoopbackDenied,
                $"日常模式默认禁止访问本机回环地址 '{hostDisplay}:{port}'（如需开放请在「设置 → 浏览器」通过控制面开启）。");
        }

        if (options.DevelopmentAllowedLoopbackPorts.Contains(port) ||
            (sessionApprovedLoopbackPorts is not null && sessionApprovedLoopbackPorts.Contains(port)))
        {
            return new BrowserPolicyEvaluation(true, null, null, TargetPort: port);
        }

        return new BrowserPolicyEvaluation(
            Allowed: false,
            ReasonCode: ReasonLoopbackApprovalRequired,
            Message: string.Create(CultureInfo.InvariantCulture, $"开发模式访问非白名单本机端口 {hostDisplay}:{port} 需要人工审批放行。"),
            RequiresLoopbackPortApproval: true,
            TargetPort: port);
    }

    internal static bool MatchesDomainList(string host, IReadOnlyList<string> patterns)
    {
        if (patterns.Count == 0)
        {
            return false;
        }

        var normalizedHost = NormalizeHostnameAscii(host);
        foreach (var rawPattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(rawPattern))
            {
                continue;
            }

            var trimmed = rawPattern.Trim().TrimEnd('.');
            if (trimmed.StartsWith("*.", StringComparison.Ordinal))
            {
                var root = NormalizeHostnameAscii(trimmed[2..]);
                if (string.Equals(normalizedHost, root, StringComparison.OrdinalIgnoreCase) ||
                    normalizedHost.EndsWith("." + root, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (trimmed.StartsWith('.'))
            {
                var root = NormalizeHostnameAscii(trimmed[1..]);
                if (string.Equals(normalizedHost, root, StringComparison.OrdinalIgnoreCase) ||
                    normalizedHost.EndsWith("." + root, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else
            {
                var pattern = NormalizeHostnameAscii(trimmed);
                if (string.Equals(normalizedHost, pattern, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string NormalizeHostnameAscii(string rawHost)
    {
        var trimmed = rawHost.Trim().TrimEnd('.');
        if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']')
        {
            return trimmed.ToLowerInvariant();
        }

        try
        {
            return Idn.GetAscii(trimmed).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return trimmed.ToLowerInvariant();
        }
    }

    private static Task<IPAddress[]> DefaultResolveAsync(string host, CancellationToken ct)
        => Dns.GetHostAddressesAsync(host, ct);

    private static BrowserPolicyEvaluation Denied(string code, string message)
        => new(false, code, message);
}
