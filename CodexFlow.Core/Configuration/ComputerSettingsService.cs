// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Contracts.ComputerUse;
using CodexFlow.Core.ComputerUse;
using Newtonsoft.Json.Linq;

namespace CodexFlow.Core.Configuration;

public interface IComputerSettingsService
{
    ComputerOptions Read();

    ComputerOptions Update(UpdateComputerSettingsRequest request);
}

/// <summary>
/// Persists user-level <c>ComputerUse</c> settings in <c>~/.codexflow/appsettings.json</c>.
/// Uses explicit <see cref="JObject"/> DOM operations only (Native AOT safe; no reflection serialization).
/// </summary>
public sealed class ComputerSettingsService : IComputerSettingsService
{
    private readonly string _path;

    public ComputerSettingsService(string globalSettingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(globalSettingsPath);
        _path = globalSettingsPath;
    }

    public ComputerOptions Read()
        => GlobalSettingsFileStore.ReadLocked(_path, ReadLocked);

    public ComputerOptions Update(UpdateComputerSettingsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return GlobalSettingsFileStore.UpdateAtomically(_path, root =>
        {
            var current = ReadLocked(root);
            var section = root[ComputerOptions.SectionName] as JObject ?? new JObject();

            if (request.Enabled is { } enabled)
            {
                current.Enabled = enabled;
                section[nameof(ComputerOptions.Enabled)] = enabled;
            }

            if (!string.IsNullOrWhiteSpace(request.InteractionApprovalPolicy))
            {
                if (!Enum.TryParse<ComputerInteractionApprovalPolicy>(
                        request.InteractionApprovalPolicy.Trim(),
                        ignoreCase: true,
                        out var parsedPolicy) ||
                    !Enum.IsDefined(parsedPolicy))
                {
                    throw new ArgumentException(
                        "InteractionApprovalPolicy must be AlwaysAsk, AppScopedInSession, or HighRiskOnly.",
                        nameof(request));
                }

                current.InteractionApprovalPolicy = parsedPolicy;
                section[nameof(ComputerOptions.InteractionApprovalPolicy)] = parsedPolicy.ToString();
            }

            if (!string.IsNullOrWhiteSpace(request.CoordinateMode))
            {
                if (!Enum.TryParse<ComputerCoordinateMode>(
                        request.CoordinateMode.Trim(),
                        ignoreCase: true,
                        out var parsedCoord) ||
                    !Enum.IsDefined(parsedCoord))
                {
                    throw new ArgumentException(
                        "CoordinateMode must be Auto, ScreenshotPixels, or Normalized1000.",
                        nameof(request));
                }

                current.CoordinateMode = parsedCoord;
                section[nameof(ComputerOptions.CoordinateMode)] = parsedCoord.ToString();
            }

            if (request.MaxImageLongEdge is { } longEdge)
            {
                var clamped = Math.Clamp(longEdge, 640, 2560);
                current.MaxImageLongEdge = clamped;
                section[nameof(ComputerOptions.MaxImageLongEdge)] = clamped;
            }

            if (request.MaxScreenshotsPerTurn is { } maxShots)
            {
                var clamped = Math.Clamp(maxShots, 1, 30);
                current.MaxScreenshotsPerTurn = clamped;
                section[nameof(ComputerOptions.MaxScreenshotsPerTurn)] = clamped;
            }

            if (request.EnableGridOverlay is { } grid)
            {
                current.EnableGridOverlay = grid;
                section[nameof(ComputerOptions.EnableGridOverlay)] = grid;
            }

            if (request.AutoMinimizeHostWindow is { } autoMin)
            {
                current.AutoMinimizeHostWindow = autoMin;
                section[nameof(ComputerOptions.AutoMinimizeHostWindow)] = autoMin;
            }

            if (request.BlockedProcessNames is not null)
            {
                var cleaned = NormalizeBlockedProcessNames(request.BlockedProcessNames);
                current.BlockedProcessNames = cleaned;
                section[nameof(ComputerOptions.BlockedProcessNames)] = new JArray(cleaned.Cast<object>().ToArray());
            }

            if (request.EmergencyStopShortcut is not null)
            {
                var trimmed = request.EmergencyStopShortcut.Trim();
                if (trimmed.Length == 0 || trimmed.Length > 64)
                {
                    throw new ArgumentException("EmergencyStopShortcut must be between 1 and 64 characters.", nameof(request));
                }

                current.EmergencyStopShortcut = trimmed;
                section[nameof(ComputerOptions.EmergencyStopShortcut)] = trimmed;
            }

            if (request.IdleTimeoutMinutes is { } idleMinutes)
            {
                var clamped = Math.Clamp(idleMinutes, 1, 120);
                current.IdleTimeoutMinutes = clamped;
                section[nameof(ComputerOptions.IdleTimeoutMinutes)] = clamped;
            }

            root[ComputerOptions.SectionName] = section;
            return (true, current);
        });
    }

    private static ComputerOptions ReadLocked(JObject root)
    {
        var opts = new ComputerOptions();
        if (root[ComputerOptions.SectionName] is not JObject section)
        {
            return opts;
        }

        if (section[nameof(ComputerOptions.Enabled)]?.Type == JTokenType.Boolean)
        {
            opts.Enabled = section.Value<bool>(nameof(ComputerOptions.Enabled));
        }

        var policyRaw = section.Value<string>(nameof(ComputerOptions.InteractionApprovalPolicy));
        if (!string.IsNullOrWhiteSpace(policyRaw) &&
            Enum.TryParse<ComputerInteractionApprovalPolicy>(policyRaw, ignoreCase: true, out var parsedPolicy) &&
            Enum.IsDefined(parsedPolicy))
        {
            opts.InteractionApprovalPolicy = parsedPolicy;
        }

        var coordRaw = section.Value<string>(nameof(ComputerOptions.CoordinateMode));
        if (!string.IsNullOrWhiteSpace(coordRaw) &&
            Enum.TryParse<ComputerCoordinateMode>(coordRaw, ignoreCase: true, out var parsedCoord) &&
            Enum.IsDefined(parsedCoord))
        {
            opts.CoordinateMode = parsedCoord;
        }

        if (section[nameof(ComputerOptions.MaxImageLongEdge)]?.Type == JTokenType.Integer)
        {
            opts.MaxImageLongEdge = Math.Clamp(section.Value<int>(nameof(ComputerOptions.MaxImageLongEdge)), 640, 2560);
        }

        if (section[nameof(ComputerOptions.MaxScreenshotsPerTurn)]?.Type == JTokenType.Integer)
        {
            opts.MaxScreenshotsPerTurn = Math.Clamp(section.Value<int>(nameof(ComputerOptions.MaxScreenshotsPerTurn)), 1, 30);
        }

        if (section[nameof(ComputerOptions.EnableGridOverlay)]?.Type == JTokenType.Boolean)
        {
            opts.EnableGridOverlay = section.Value<bool>(nameof(ComputerOptions.EnableGridOverlay));
        }

        if (section[nameof(ComputerOptions.AutoMinimizeHostWindow)]?.Type == JTokenType.Boolean)
        {
            opts.AutoMinimizeHostWindow = section.Value<bool>(nameof(ComputerOptions.AutoMinimizeHostWindow));
        }

        if (section[nameof(ComputerOptions.BlockedProcessNames)] is JArray blockedArr)
        {
            opts.BlockedProcessNames = NormalizeBlockedProcessNames(blockedArr.Select(static t => t.ToString()));
        }

        var shortcut = section.Value<string>(nameof(ComputerOptions.EmergencyStopShortcut));
        if (!string.IsNullOrWhiteSpace(shortcut))
        {
            opts.EmergencyStopShortcut = shortcut.Trim();
        }

        if (section[nameof(ComputerOptions.IdleTimeoutMinutes)]?.Type == JTokenType.Integer)
        {
            opts.IdleTimeoutMinutes = Math.Clamp(section.Value<int>(nameof(ComputerOptions.IdleTimeoutMinutes)), 1, 120);
        }

        return opts;
    }

    private static List<string> NormalizeBlockedProcessNames(IEnumerable<string> raw)
    {
        var result = new List<string>(ComputerOptions.DefaultBlockedProcessNames);
        foreach (var entry in raw)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            var normalized = ComputerPermissionClamp.NormalizeSingleProcessName(entry);
            if (normalized.Length > 0 && !result.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(normalized);
            }
        }

        return result;
    }
}
