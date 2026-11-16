// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Execution;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace CodexFlow.Core.Configuration;

public sealed record ExecutionSettingsView(
    ExecutionBackend Backend,
    ExecutionPermissionMode PermissionMode);

public sealed record ExecutionSettingsUpdate(
    ExecutionBackend? Backend = null,
    ExecutionPermissionMode? PermissionMode = null);

public interface IExecutionSettingsService
{
    ExecutionSettingsView Read();

    ExecutionSettingsView Update(ExecutionSettingsUpdate update);

    /// <summary>
    /// Ensures existing installations that already customized <c>Isolation</c> keep <c>Execution:Backend = Docker</c>
    /// when upgrading without an explicit <c>Execution</c> section.
    /// </summary>
    bool MigrateLegacyIsolationIfNeeded(ILogger? logger = null);
}

/// <summary>
/// Atomically reads and writes the <c>Execution</c> section of <c>~/.codexflow/appsettings.json</c>,
/// and enforces runtime tighten-only permission tracking when reloaded from disk.
/// </summary>
public sealed class ExecutionSettingsService : IExecutionSettingsService
{
    public const string SectionName = ExecutionOptions.SectionName;

    private readonly string _path;
    private readonly object _gate = new();
    private ExecutionPermissionMode _activePermissionMode;

    public ExecutionSettingsService(string globalSettingsPath)
    {
        _path = globalSettingsPath;
        _activePermissionMode = Read().PermissionMode;
    }

    public ExecutionPermissionMode EffectivePermissionMode
    {
        get
        {
            lock (_gate)
            {
                return _activePermissionMode;
            }
        }
    }

    public ExecutionSettingsView Read()
    {
        lock (_gate)
        {
            var root = GlobalSettingsFileStore.LoadRoot(_path);
            var section = root[SectionName] as JObject;
            var backend = ParseEnum(section?["Backend"]?.ToString(), ExecutionBackend.Docker);
            var permission = ParseEnum(section?["PermissionMode"]?.ToString(), ExecutionPermissionMode.Ask);
            return new ExecutionSettingsView(backend, permission);
        }
    }

    public ExecutionSettingsView Update(ExecutionSettingsUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        lock (_gate)
        {
            var root = GlobalSettingsFileStore.LoadRoot(_path);
            var section = root[SectionName] as JObject ?? new JObject();

            if (update.Backend is { } backend)
            {
                section["Backend"] = backend.ToString();
            }

            if (update.PermissionMode is { } permission)
            {
                section["PermissionMode"] = permission.ToString();
                _activePermissionMode = permission;
            }

            root[SectionName] = section;
            GlobalSettingsFileStore.WriteAtomically(_path, root.ToString(Newtonsoft.Json.Formatting.Indented));
            return new ExecutionSettingsView(
                ParseEnum(section["Backend"]?.ToString(), ExecutionBackend.Docker),
                ParseEnum(section["PermissionMode"]?.ToString(), ExecutionPermissionMode.Ask));
        }
    }

    /// <summary>
    /// Applies a file-reloaded permission mode only if it is stricter than or equal to the current active mode.
    /// Relaxing the permission mode at runtime is ignored from file reloads and must go through the control plane API.
    /// </summary>
    public ExecutionPermissionMode ApplyFileReloadedPermissionMode(ExecutionPermissionMode reloaded)
    {
        lock (_gate)
        {
            if (ExecutionOptions.IsStricterOrEqual(reloaded, _activePermissionMode))
            {
                _activePermissionMode = reloaded;
            }

            return _activePermissionMode;
        }
    }

    public bool MigrateLegacyIsolationIfNeeded(ILogger? logger = null)
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return false;
            }

            var root = GlobalSettingsFileStore.LoadRoot(_path);
            if (root[SectionName] is not null)
            {
                return false;
            }

            if (root[IsolationOptions.SectionName] is not JObject isolation || !isolation.HasValues)
            {
                return false;
            }

            root[SectionName] = new JObject
            {
                ["Backend"] = ExecutionBackend.Docker.ToString()
            };
            GlobalSettingsFileStore.WriteAtomically(_path, root.ToString(Newtonsoft.Json.Formatting.Indented));
            logger?.LogInformation(
                "Migrated legacy Isolation configuration in {SettingsPath} to explicit Execution:Backend=Docker.",
                _path);
            return true;
        }
    }

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback) where TEnum : struct, Enum
        => !string.IsNullOrWhiteSpace(value) && Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed)
            ? parsed
            : fallback;
}
