// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Newtonsoft.Json.Linq;

namespace CodexFlow.Core.Configuration;

/// <summary>
/// Manages user-level <c>Workspace:AllowedLocalRepositoryRoots</c> in <c>~/.codexflow/appsettings.json</c>.
/// </summary>
public interface IWorkspaceSettingsService
{
    IReadOnlyList<string> ReadAllowedLocalRepositoryRoots();

    /// <summary>
    /// Adds a canonical local workspace root to <c>Workspace:AllowedLocalRepositoryRoots</c> atomically.
    /// Returns <see langword="true"/> if newly added, or <see langword="false"/> if already present.
    /// </summary>
    bool AddAllowedLocalRepositoryRoot(string canonicalPath);
}

public sealed class WorkspaceSettingsService : IWorkspaceSettingsService
{
    public const string SectionName = "Workspace";
    public const string AllowedRootsProperty = "AllowedLocalRepositoryRoots";

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly string _path;

    public WorkspaceSettingsService(string globalSettingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(globalSettingsPath);
        _path = globalSettingsPath;
    }

    public IReadOnlyList<string> ReadAllowedLocalRepositoryRoots()
        => GlobalSettingsFileStore.ReadLocked(_path, ReadListLocked);

    public bool AddAllowedLocalRepositoryRoot(string canonicalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(canonicalPath));

        return GlobalSettingsFileStore.UpdateAtomically(_path, root =>
        {
            var section = root[SectionName] as JObject ?? new JObject();
            var existing = ReadListLocked(root);
            if (existing.Any(item => string.Equals(item, normalized, PathComparison)))
            {
                return (false, false);
            }

            var updated = new JArray(existing.Concat([normalized]).Cast<object>().ToArray());
            section[AllowedRootsProperty] = updated;
            root[SectionName] = section;
            return (true, true);
        });
    }

    private static List<string> ReadListLocked(JObject root)
    {
        if (root[SectionName] is not JObject section || section[AllowedRootsProperty] is not JArray arr)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var token in arr)
        {
            var raw = token?.ToString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            try
            {
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
                if (!result.Any(existing => string.Equals(existing, full, PathComparison)))
                {
                    result.Add(full);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Preserve clean entries only.
            }
        }

        return result;
    }
}
