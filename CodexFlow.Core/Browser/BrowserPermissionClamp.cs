// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace CodexFlow.Core.Browser;

/// <summary>
/// Enforces tighten-only clamping on hot-reloaded <see cref="BrowserOptions"/> file changes so an untrusted
/// disk edit can never silently enable browser execution, swap in an arbitrary executable, open localhost in
/// daily mode, widen allowed domains, or relax interaction approval policy.
/// Relaxations are applied exclusively through authenticated control-plane updates.
/// </summary>
public sealed class BrowserPermissionClamp :
    IPostConfigureOptions<BrowserOptions>,
    IOptionsChangeTokenSource<BrowserOptions>,
    IDisposable
{
    private readonly object _gate = new();
    private bool _baselineCaptured;

    // Last observed raw values from configuration source (to detect genuine file reloads vs. control-plane notifications).
    private bool _lastRawEnabled;
    private bool _lastRawHeadless;
    private BrowserProfileMode _lastRawProfileMode;
    private BrowserInteractionApprovalPolicy _lastRawApprovalPolicy;
    private bool _lastRawAllowLocalhostInDaily;
    private bool _lastRawAllowControlledDownloads;
    private List<int> _lastRawDevLoopbackPorts = [];
    private List<string> _lastRawAllowedDomains = [];
    private List<string> _lastRawBlockedDomains = [];

    // Authoritative effective values.
    private bool _effectiveEnabled;
    private string? _effectiveExecutablePath;
    private string? _effectiveRemoteCdpWsUrl;
    private bool _effectiveHeadless = true;
    private BrowserProfileMode _effectiveProfileMode = BrowserProfileMode.Ephemeral;
    private BrowserInteractionApprovalPolicy _effectiveApprovalPolicy = BrowserInteractionApprovalPolicy.HighRiskOnly;
    private bool _effectiveAllowLocalhostInDaily;
    private bool _effectiveAllowControlledDownloads;
    private List<int> _effectiveDevLoopbackPorts = [.. BrowserOptions.DefaultDevelopmentLoopbackPorts];
    private List<string> _effectiveAllowedDomains = [];
    private List<string> _effectiveBlockedDomains = [];
    private int _effectiveIdleTimeoutMinutes = 15;
    private int _effectiveMaxConcurrentSessions = 3;
    private CancellationTokenSource _changeCts = new();

    public string Name => Options.DefaultName;

    public IChangeToken GetChangeToken()
    {
        lock (_gate)
        {
            return new CancellationChangeToken(_changeCts.Token);
        }
    }

    public void PostConfigure(string? name, BrowserOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_gate)
        {
            var rawPorts = NormalizePorts(options.DevelopmentAllowedLoopbackPorts);
            var rawAllowed = NormalizeDomains(options.AllowedDomains);
            var rawBlocked = NormalizeDomains(options.BlockedDomains);

            if (!_baselineCaptured)
            {
                _effectiveEnabled = _lastRawEnabled = options.Enabled;
                _effectiveExecutablePath = NormalizePath(options.ExecutablePath);
                _effectiveRemoteCdpWsUrl = NormalizeRemoteCdpUrl(options.RemoteCdpAddress);
                _effectiveHeadless = _lastRawHeadless = options.Headless;
                _effectiveProfileMode = _lastRawProfileMode = options.ProfileMode;
                _effectiveApprovalPolicy = _lastRawApprovalPolicy = options.InteractionApprovalPolicy;
                _effectiveAllowLocalhostInDaily = _lastRawAllowLocalhostInDaily = options.AllowLocalhostInDailyMode;
                _effectiveAllowControlledDownloads = _lastRawAllowControlledDownloads = options.AllowControlledDownloads;
                _effectiveDevLoopbackPorts = [.. rawPorts];
                _lastRawDevLoopbackPorts = [.. rawPorts];
                _effectiveAllowedDomains = [.. rawAllowed];
                _lastRawAllowedDomains = [.. rawAllowed];
                _effectiveBlockedDomains = [.. rawBlocked];
                _lastRawBlockedDomains = [.. rawBlocked];
                _effectiveIdleTimeoutMinutes = Math.Clamp(options.IdleTimeoutMinutes, 1, 120);
                _effectiveMaxConcurrentSessions = Math.Clamp(options.MaxConcurrentSessions, 1, 8);
                _baselineCaptured = true;
            }
            else
            {
                // Apply tighten-only clamping ONLY when the underlying raw configuration genuinely changed.
                if (options.Enabled != _lastRawEnabled)
                {
                    _lastRawEnabled = options.Enabled;
                    if (!options.Enabled)
                    {
                        _effectiveEnabled = false;
                    }
                }

                if (options.Headless != _lastRawHeadless)
                {
                    _lastRawHeadless = options.Headless;
                    if (options.Headless)
                    {
                        _effectiveHeadless = true;
                    }
                }

                if (options.ProfileMode != _lastRawProfileMode)
                {
                    _lastRawProfileMode = options.ProfileMode;
                    if (options.ProfileMode == BrowserProfileMode.Ephemeral)
                    {
                        _effectiveProfileMode = BrowserProfileMode.Ephemeral;
                    }
                }

                if (options.InteractionApprovalPolicy != _lastRawApprovalPolicy)
                {
                    _lastRawApprovalPolicy = options.InteractionApprovalPolicy;
                    if (BrowserOptions.IsStricterOrEqual(options.InteractionApprovalPolicy, _effectiveApprovalPolicy))
                    {
                        _effectiveApprovalPolicy = options.InteractionApprovalPolicy;
                    }
                }

                if (options.AllowLocalhostInDailyMode != _lastRawAllowLocalhostInDaily)
                {
                    _lastRawAllowLocalhostInDaily = options.AllowLocalhostInDailyMode;
                    if (!options.AllowLocalhostInDailyMode)
                    {
                        _effectiveAllowLocalhostInDaily = false;
                    }
                }

                if (options.AllowControlledDownloads != _lastRawAllowControlledDownloads)
                {
                    _lastRawAllowControlledDownloads = options.AllowControlledDownloads;
                    if (!options.AllowControlledDownloads)
                    {
                        _effectiveAllowControlledDownloads = false;
                    }
                }

                if (!rawPorts.SequenceEqual(_lastRawDevLoopbackPorts))
                {
                    _lastRawDevLoopbackPorts = [.. rawPorts];
                    _effectiveDevLoopbackPorts = _effectiveDevLoopbackPorts
                        .Where(rawPorts.Contains)
                        .ToList();
                }

                if (!rawAllowed.SequenceEqual(_lastRawAllowedDomains, StringComparer.OrdinalIgnoreCase))
                {
                    _lastRawAllowedDomains = [.. rawAllowed];
                    _effectiveAllowedDomains = _effectiveAllowedDomains
                        .Where(d => rawAllowed.Contains(d, StringComparer.OrdinalIgnoreCase))
                        .ToList();
                }

                if (!rawBlocked.SequenceEqual(_lastRawBlockedDomains, StringComparer.OrdinalIgnoreCase))
                {
                    _lastRawBlockedDomains = [.. rawBlocked];
                    foreach (var blocked in rawBlocked)
                    {
                        if (!_effectiveBlockedDomains.Contains(blocked, StringComparer.OrdinalIgnoreCase))
                        {
                            _effectiveBlockedDomains.Add(blocked);
                        }
                    }
                }
            }

            ApplyEffectiveTo(options);
        }
    }

    public void ApplyControlPlaneUpdate(BrowserOptions updated)
    {
        ArgumentNullException.ThrowIfNull(updated);
        CancellationTokenSource oldCts;
        lock (_gate)
        {
            // Keep _lastRaw* at the last genuinely observed disk configuration value so an immediate
            // IOptionsMonitor rebuild before FileSystemWatcher's ~250ms debounce finishes sees raw == _lastRaw
            // and never misinterprets the stale disk value as a new tightening edit.
            _effectiveEnabled = updated.Enabled;
            _effectiveExecutablePath = NormalizePath(updated.ExecutablePath);
            _effectiveRemoteCdpWsUrl = NormalizeRemoteCdpUrl(updated.RemoteCdpAddress);
            _effectiveHeadless = updated.Headless;
            _effectiveProfileMode = updated.ProfileMode;
            _effectiveApprovalPolicy = updated.InteractionApprovalPolicy;
            _effectiveAllowLocalhostInDaily = updated.AllowLocalhostInDailyMode;
            _effectiveAllowControlledDownloads = updated.AllowControlledDownloads;
            _effectiveDevLoopbackPorts = NormalizePorts(updated.DevelopmentAllowedLoopbackPorts);
            _effectiveAllowedDomains = NormalizeDomains(updated.AllowedDomains);
            _effectiveBlockedDomains = NormalizeDomains(updated.BlockedDomains);
            _effectiveIdleTimeoutMinutes = Math.Clamp(updated.IdleTimeoutMinutes, 1, 120);
            _effectiveMaxConcurrentSessions = Math.Clamp(updated.MaxConcurrentSessions, 1, 8);
            _baselineCaptured = true;

            oldCts = _changeCts;
            _changeCts = new CancellationTokenSource();
        }

        try
        {
            oldCts.Cancel();
        }
        finally
        {
            oldCts.Dispose();
        }
    }

    private void ApplyEffectiveTo(BrowserOptions options)
    {
        options.Enabled = _effectiveEnabled;
        options.ExecutablePath = _effectiveExecutablePath;
        options.RemoteCdpAddress = _effectiveRemoteCdpWsUrl;
        options.Headless = _effectiveHeadless;
        options.ProfileMode = _effectiveProfileMode;
        options.InteractionApprovalPolicy = _effectiveApprovalPolicy;
        options.AllowLocalhostInDailyMode = _effectiveAllowLocalhostInDaily;
        options.AllowControlledDownloads = _effectiveAllowControlledDownloads;
        options.DevelopmentAllowedLoopbackPorts = [.. _effectiveDevLoopbackPorts];
        options.AllowedDomains = [.. _effectiveAllowedDomains];
        options.BlockedDomains = [.. _effectiveBlockedDomains];
        options.IdleTimeoutMinutes = _effectiveIdleTimeoutMinutes;
        options.MaxConcurrentSessions = _effectiveMaxConcurrentSessions;
    }

    private static string? NormalizePath(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null : Path.GetFullPath(raw.Trim());

    private static string? NormalizeRemoteCdpUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || BrowserRemoteEndpointGuard.ContainsMaskPlaceholder(raw))
        {
            return null;
        }

        var check = BrowserRemoteEndpointGuard.ValidateRemoteCdpWsUrlSyntax(raw);
        return check.Allowed && check.NormalizedUri is not null ? check.NormalizedUri.AbsoluteUri : null;
    }

    private static List<int> NormalizePorts(IEnumerable<int>? ports)
        => ports is null
            ? [.. BrowserOptions.DefaultDevelopmentLoopbackPorts]
            : ports.Where(static p => p is > 0 and <= 65535).Distinct().Order().ToList();

    private static List<string> NormalizeDomains(IEnumerable<string>? domains)
        => domains is null
            ? []
            : domains
                .Where(static d => !string.IsNullOrWhiteSpace(d))
                .Select(static d => d.Trim().TrimEnd('.').ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    public void Dispose()
    {
        lock (_gate)
        {
            _changeCts.Dispose();
        }
    }
}
