// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Contracts.Browser;
using CodexFlow.Core.Browser;
using Newtonsoft.Json.Linq;

namespace CodexFlow.Core.Configuration;

public interface IBrowserSettingsService
{
    BrowserOptions Read();

    BrowserOptions Update(UpdateBrowserSettingsRequest request);
}

/// <summary>
/// Persists user-level <c>Browser</c> settings in <c>~/.codexflow/appsettings.json</c>.
/// </summary>
public sealed class BrowserSettingsService : IBrowserSettingsService
{
    private readonly string _path;

    public BrowserSettingsService(string globalSettingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(globalSettingsPath);
        _path = globalSettingsPath;
    }

    public BrowserOptions Read()
        => GlobalSettingsFileStore.ReadLocked(_path, ReadLocked);

    public BrowserOptions Update(UpdateBrowserSettingsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return GlobalSettingsFileStore.UpdateAtomically(_path, root =>
        {
            var current = ReadLocked(root);
            var section = root[BrowserOptions.SectionName] as JObject ?? new JObject();

            if (request.Enabled is { } enabled)
            {
                current.Enabled = enabled;
                section[nameof(BrowserOptions.Enabled)] = enabled;
            }

            if (request.ExecutablePath is not null)
            {
                var trimmed = request.ExecutablePath.Trim();
                if (trimmed.Length == 0)
                {
                    current.ExecutablePath = null;
                    section.Remove(nameof(BrowserOptions.ExecutablePath));
                }
                else
                {
                    if (!Path.IsPathRooted(trimmed))
                    {
                        throw new ArgumentException("ExecutablePath must be an absolute path.", nameof(request));
                    }

                    var full = Path.GetFullPath(trimmed);
                    current.ExecutablePath = full;
                    section[nameof(BrowserOptions.ExecutablePath)] = full;
                }
            }

            if (request.Headless is { } headless)
            {
                current.Headless = headless;
                section[nameof(BrowserOptions.Headless)] = headless;
            }

            if (!string.IsNullOrWhiteSpace(request.ProfileMode))
            {
                if (!Enum.TryParse<BrowserProfileMode>(request.ProfileMode.Trim(), ignoreCase: true, out var parsedMode) ||
                    !Enum.IsDefined(parsedMode))
                {
                    throw new ArgumentException("ProfileMode must be Ephemeral or Persistent.", nameof(request));
                }

                current.ProfileMode = parsedMode;
                section[nameof(BrowserOptions.ProfileMode)] = parsedMode.ToString();
            }

            if (!string.IsNullOrWhiteSpace(request.InteractionApprovalPolicy))
            {
                if (!Enum.TryParse<BrowserInteractionApprovalPolicy>(request.InteractionApprovalPolicy.Trim(), ignoreCase: true, out var parsedPolicy) ||
                    !Enum.IsDefined(parsedPolicy))
                {
                    throw new ArgumentException("InteractionApprovalPolicy must be HighRiskOnly, AlwaysAsk, or AutoInSession.", nameof(request));
                }

                current.InteractionApprovalPolicy = parsedPolicy;
                section[nameof(BrowserOptions.InteractionApprovalPolicy)] = parsedPolicy.ToString();
            }

            if (request.AllowLocalhostInDailyMode is { } allowLocalhost)
            {
                current.AllowLocalhostInDailyMode = allowLocalhost;
                section[nameof(BrowserOptions.AllowLocalhostInDailyMode)] = allowLocalhost;
            }

            if (request.AllowControlledDownloads is { } allowDownloads)
            {
                current.AllowControlledDownloads = allowDownloads;
                section[nameof(BrowserOptions.AllowControlledDownloads)] = allowDownloads;
            }

            if (request.DevelopmentAllowedLoopbackPorts is not null)
            {
                var cleaned = request.DevelopmentAllowedLoopbackPorts
                    .Where(static p => p is > 0 and <= 65535)
                    .Distinct()
                    .Order()
                    .ToList();
                current.DevelopmentAllowedLoopbackPorts = cleaned;
                section[nameof(BrowserOptions.DevelopmentAllowedLoopbackPorts)] = new JArray(cleaned.Cast<object>().ToArray());
            }

            if (request.AllowedDomains is not null)
            {
                var cleaned = NormalizeDomains(request.AllowedDomains);
                current.AllowedDomains = cleaned;
                section[nameof(BrowserOptions.AllowedDomains)] = new JArray(cleaned.Cast<object>().ToArray());
            }

            if (request.BlockedDomains is not null)
            {
                var cleaned = NormalizeDomains(request.BlockedDomains);
                current.BlockedDomains = cleaned;
                section[nameof(BrowserOptions.BlockedDomains)] = new JArray(cleaned.Cast<object>().ToArray());
            }

            if (request.IdleTimeoutMinutes is { } idleMinutes)
            {
                var clamped = Math.Clamp(idleMinutes, 1, 120);
                current.IdleTimeoutMinutes = clamped;
                section[nameof(BrowserOptions.IdleTimeoutMinutes)] = clamped;
            }

            if (request.MaxConcurrentSessions is { } maxSessions)
            {
                var clamped = Math.Clamp(maxSessions, 1, 8);
                current.MaxConcurrentSessions = clamped;
                section[nameof(BrowserOptions.MaxConcurrentSessions)] = clamped;
            }

            root[BrowserOptions.SectionName] = section;
            return (true, current);
        });
    }

    private static BrowserOptions ReadLocked(JObject root)
    {
        var opts = new BrowserOptions();
        if (root[BrowserOptions.SectionName] is not JObject section)
        {
            return opts;
        }

        if (section[nameof(BrowserOptions.Enabled)]?.Type == JTokenType.Boolean)
        {
            opts.Enabled = section.Value<bool>(nameof(BrowserOptions.Enabled));
        }

        var execPath = section.Value<string>(nameof(BrowserOptions.ExecutablePath));
        if (!string.IsNullOrWhiteSpace(execPath))
        {
            opts.ExecutablePath = execPath.Trim();
        }

        if (section[nameof(BrowserOptions.Headless)]?.Type == JTokenType.Boolean)
        {
            opts.Headless = section.Value<bool>(nameof(BrowserOptions.Headless));
        }

        var profileModeRaw = section.Value<string>(nameof(BrowserOptions.ProfileMode));
        if (!string.IsNullOrWhiteSpace(profileModeRaw) &&
            Enum.TryParse<BrowserProfileMode>(profileModeRaw, ignoreCase: true, out var parsedProfile) &&
            Enum.IsDefined(parsedProfile))
        {
            opts.ProfileMode = parsedProfile;
        }

        var policyRaw = section.Value<string>(nameof(BrowserOptions.InteractionApprovalPolicy));
        if (!string.IsNullOrWhiteSpace(policyRaw) &&
            Enum.TryParse<BrowserInteractionApprovalPolicy>(policyRaw, ignoreCase: true, out var parsedPolicy) &&
            Enum.IsDefined(parsedPolicy))
        {
            opts.InteractionApprovalPolicy = parsedPolicy;
        }

        if (section[nameof(BrowserOptions.AllowLocalhostInDailyMode)]?.Type == JTokenType.Boolean)
        {
            opts.AllowLocalhostInDailyMode = section.Value<bool>(nameof(BrowserOptions.AllowLocalhostInDailyMode));
        }

        if (section[nameof(BrowserOptions.AllowControlledDownloads)]?.Type == JTokenType.Boolean)
        {
            opts.AllowControlledDownloads = section.Value<bool>(nameof(BrowserOptions.AllowControlledDownloads));
        }

        if (section[nameof(BrowserOptions.DevelopmentAllowedLoopbackPorts)] is JArray portsArr)
        {
            opts.DevelopmentAllowedLoopbackPorts = portsArr
                .Select(static t => t.Type == JTokenType.Integer ? t.Value<int>() : 0)
                .Where(static p => p is > 0 and <= 65535)
                .Distinct()
                .Order()
                .ToList();
        }

        if (section[nameof(BrowserOptions.AllowedDomains)] is JArray allowedArr)
        {
            opts.AllowedDomains = NormalizeDomains(allowedArr.Select(static t => t.ToString()));
        }

        if (section[nameof(BrowserOptions.BlockedDomains)] is JArray blockedArr)
        {
            opts.BlockedDomains = NormalizeDomains(blockedArr.Select(static t => t.ToString()));
        }

        if (section[nameof(BrowserOptions.IdleTimeoutMinutes)]?.Type == JTokenType.Integer)
        {
            opts.IdleTimeoutMinutes = Math.Clamp(section.Value<int>(nameof(BrowserOptions.IdleTimeoutMinutes)), 1, 120);
        }

        if (section[nameof(BrowserOptions.MaxConcurrentSessions)]?.Type == JTokenType.Integer)
        {
            opts.MaxConcurrentSessions = Math.Clamp(section.Value<int>(nameof(BrowserOptions.MaxConcurrentSessions)), 1, 8);
        }

        return opts;
    }

    private static List<string> NormalizeDomains(IEnumerable<string> raw)
        => raw
            .Where(static d => !string.IsNullOrWhiteSpace(d))
            .Select(static d => d.Trim().TrimEnd('.').ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
