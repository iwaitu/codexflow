// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace CodexFlow.Core.Security;

/// <summary>
/// Shared IP and hostname security classification used by outbound guards and the managed browser proxy.
/// Strips IPv6 zone-ids, unwraps IPv4-mapped / NAT64 / 6to4 / IPv4-compatible embedded IPv4 addresses,
/// and classifies cloud metadata, loopback, and private/reserved ranges fail-closed.
/// </summary>
public static class NetworkAddressClassifier
{
    private static readonly IPAddress AlibabaMetadataIp = IPAddress.Parse("100.100.100.200");
    private static readonly IPAddress AzureWireServerIp = IPAddress.Parse("168.63.129.16");
    private static readonly IPAddress AwsIpv6MetadataIp = IPAddress.Parse("fd00:ec2::254");

    public static IPAddress Normalize(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        // Strip IPv6 zone-id (ScopeId) so "::1%1" and "fd00:ec2::254%1" cannot evade loopback/metadata checks.
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
        {
            address = new IPAddress(address.GetAddressBytes());
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }

        if (TryExtractEmbeddedIpv4(address, out var embedded))
        {
            return embedded;
        }

        return address;
    }

    public static bool HasForbiddenZoneId(string? host)
        => !string.IsNullOrEmpty(host) && host.Contains('%', StringComparison.Ordinal);

    public static bool IsLoopback(IPAddress rawAddress)
        => IPAddress.IsLoopback(Normalize(rawAddress));

    public static bool IsLoopbackHostname(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || HasForbiddenZoneId(host))
        {
            return false;
        }

        var normalized = host.Trim().TrimEnd('.');
        if (string.Equals(normalized, "localhost", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return TryParseIpLiteral(normalized, out var ip) && IsLoopback(ip);
    }

    public static bool IsForbiddenMetadataHostname(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var normalized = host.Trim().TrimEnd('.');
        return string.Equals(normalized, "metadata.google.internal", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "metadata", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "metadata.goog", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses standard and IPv6 bracketed IP literals as well as integer/hex/octal IPv4 encodings
    /// (e.g., 2130706433, 0x7f000001, 0177.0.0.1). Rejects zone-id suffixes.
    /// </summary>
    public static bool TryParseIpLiteral(string? host, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrWhiteSpace(host) || HasForbiddenZoneId(host))
        {
            return false;
        }

        var trimmed = host.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']')
        {
            trimmed = trimmed[1..^1];
            if (HasForbiddenZoneId(trimmed))
            {
                return false;
            }
        }

        if (IPAddress.TryParse(trimmed, out var parsed))
        {
            address = Normalize(parsed);
            return true;
        }

        // Single 32-bit decimal or hex integer IPv4 (e.g., 2130706433 or 0x7f000001)
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(trimmed.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexValue))
        {
            address = FromUInt32BigEndian(hexValue);
            return true;
        }

        if (uint.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var decValue))
        {
            address = FromUInt32BigEndian(decValue);
            return true;
        }

        // Dotted IPv4 with octal or hex octets (e.g. 0177.0.0.1, 0x7f.0.0.1)
        var parts = trimmed.Split('.');
        if (parts.Length is >= 2 and <= 4 && TryParseNonCanonicalIpv4Parts(parts, out var combined))
        {
            address = FromUInt32BigEndian(combined);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true when the IP belongs to unspecified, link-local, cloud metadata, or multicast ranges
    /// that must NEVER be dialed under any mode.
    /// </summary>
    public static bool IsAlwaysForbidden(IPAddress rawAddress, out string reason)
    {
        var address = Normalize(rawAddress);

        if (IPAddress.Any.Equals(address) || IPAddress.IPv6Any.Equals(address))
        {
            reason = "Unspecified wildcard IP addresses (0.0.0.0 / ::) are forbidden.";
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            if (bytes[0] == 0)
            {
                reason = $"Unspecified IP range '{address}' is forbidden.";
                return true;
            }

            if (bytes[0] == 169 && bytes[1] == 254)
            {
                reason = $"Link-local/cloud metadata address '{address}' is forbidden.";
                return true;
            }

            if (bytes[0] >= 224)
            {
                reason = $"Multicast/reserved address '{address}' is forbidden.";
                return true;
            }

            if (AlibabaMetadataIp.Equals(address) || AzureWireServerIp.Equals(address))
            {
                reason = $"Cloud metadata address '{address}' is forbidden.";
                return true;
            }
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal || AwsIpv6MetadataIp.Equals(address))
            {
                reason = $"Link-local/multicast/metadata IPv6 address '{address}' is forbidden.";
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>
    /// Returns true for RFC1918 private networks, RFC6598 CGNAT (100.64.0.0/10), IPv6 ULA (fc00::/7),
    /// Teredo (2001:0000::/32), documentation/test/benchmarking ranges.
    /// </summary>
    public static bool IsPrivateOrInternalNetwork(IPAddress rawAddress)
    {
        var address = Normalize(rawAddress);
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            // 10.0.0.0/8
            if (b[0] == 10) return true;
            // 172.16.0.0/12
            if (b[0] == 172 && b[1] is >= 16 and <= 31) return true;
            // 192.168.0.0/16
            if (b[0] == 192 && b[1] == 168) return true;
            // 100.64.0.0/10 (CGNAT)
            if (b[0] == 100 && (b[1] & 0xC0) == 64) return true;
            // 192.0.0.0/24 (IETF protocol assignments) & 192.0.2.0/24 (TEST-NET-1)
            if (b[0] == 192 && b[1] == 0 && (b[2] == 0 || b[2] == 2)) return true;
            // 198.18.0.0/15 (benchmarking) & 198.51.100.0/24 (TEST-NET-2)
            if (b[0] == 198 && (b[1] is 18 or 19 || (b[1] == 51 && b[2] == 100))) return true;
            // 203.0.113.0/24 (TEST-NET-3)
            if (b[0] == 203 && b[1] == 0 && b[2] == 113) return true;
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            // fc00::/7 (Unique Local Address)
            if ((b[0] & 0xFE) == 0xFC) return true;
            // fe80::/10 (Link-local)
            if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return true;
            // 2001:db8::/32 (documentation) & 2001:0000::/32 (Teredo)
            if (b[0] == 0x20 && b[1] == 0x01 && ((b[2] == 0x0D && b[3] == 0xB8) || (b[2] == 0x00 && b[3] == 0x00))) return true;
        }

        return false;
    }

    private static bool TryExtractEmbeddedIpv4(IPAddress ipv6, out IPAddress ipv4)
    {
        ipv4 = IPAddress.None;
        if (ipv6.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        var b = ipv6.GetAddressBytes();
        if (b.Length != 16)
        {
            return false;
        }

        // NAT64 well-known prefix 64:ff9b::/96 or 64:ff9b:1::/48 (RFC 6052 / RFC 8215)
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B &&
            b[4] == 0x00 && b[5] <= 0x01 && b[6] == 0x00 && b[7] == 0x00 &&
            b[8] == 0x00 && b[9] == 0x00 && b[10] == 0x00 && b[11] == 0x00)
        {
            ipv4 = new IPAddress([b[12], b[13], b[14], b[15]]);
            return true;
        }

        // 6to4 prefix 2002::/16 (embeds IPv4 in bytes 2..5)
        if (b[0] == 0x20 && b[1] == 0x02)
        {
            ipv4 = new IPAddress([b[2], b[3], b[4], b[5]]);
            return true;
        }

        // Deprecated IPv4-compatible ::w.x.y.z (exclude :: and ::1)
        var first12Zero = true;
        for (var i = 0; i < 12; i++)
        {
            if (b[i] != 0)
            {
                first12Zero = false;
                break;
            }
        }

        if (first12Zero && !(b[12] == 0 && b[13] == 0 && b[14] == 0 && b[15] <= 1))
        {
            ipv4 = new IPAddress([b[12], b[13], b[14], b[15]]);
            return true;
        }

        return false;
    }

    private static bool TryParseNonCanonicalIpv4Parts(string[] parts, out uint result)
    {
        result = 0;
        var values = new uint[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!TryParseIpv4Part(parts[i], out values[i]))
            {
                return false;
            }
        }

        switch (parts.Length)
        {
            case 2:
                if (values[0] > 0xFF || values[1] > 0xFFFFFF) return false;
                result = (values[0] << 24) | values[1];
                return true;
            case 3:
                if (values[0] > 0xFF || values[1] > 0xFF || values[2] > 0xFFFF) return false;
                result = (values[0] << 24) | (values[1] << 16) | values[2];
                return true;
            case 4:
                if (values[0] > 0xFF || values[1] > 0xFF || values[2] > 0xFF || values[3] > 0xFF) return false;
                result = (values[0] << 24) | (values[1] << 16) | (values[2] << 8) | values[3];
                return true;
            default:
                return false;
        }
    }

    private static bool TryParseIpv4Part(string part, out uint value)
    {
        value = 0;
        if (string.IsNullOrEmpty(part))
        {
            return false;
        }

        if (part.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return part.Length > 2 && uint.TryParse(part.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        if (part.Length > 1 && part[0] == '0')
        {
            uint acc = 0;
            for (var i = 1; i < part.Length; i++)
            {
                var c = part[i];
                if (c is < '0' or > '7')
                {
                    return false;
                }

                acc = (acc * 8) + (uint)(c - '0');
            }

            value = acc;
            return true;
        }

        return uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static IPAddress FromUInt32BigEndian(uint value)
    {
        var bytes = new byte[4];
        bytes[0] = (byte)((value >> 24) & 0xFF);
        bytes[1] = (byte)((value >> 16) & 0xFF);
        bytes[2] = (byte)((value >> 8) & 0xFF);
        bytes[3] = (byte)(value & 0xFF);
        return new IPAddress(bytes);
    }
}
