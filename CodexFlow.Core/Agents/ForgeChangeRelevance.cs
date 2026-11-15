// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;

namespace CodexFlow.Core.Agents;

/// <summary>
/// Whether what a forge worker left in its worktree is a change to the project or only the worker's own notes. The write contract is met by the name
/// of a tool ("a write tool ran"), so a weaker model satisfied it with a throwaway notes file next to an unchanged project and the job counted as
/// completed. The judgment is made on the real change set (git status), never on what the worker says it did: a change set made only of newly
/// created documentation files (.md, .txt, .log, .rst) is notes, unless the task itself names one of those files.
/// </summary>
public static class ForgeChangeRelevance
{
    private static readonly string[] NoteExtensions = [".md", ".txt", ".log", ".rst"];

    /// <param name="changes">The worktree's changes against the base commit (git status letters: A added or untracked, M modified, D deleted).</param>
    /// <param name="taskNamedFiles">Files the task itself names (the scope the task description declares); a change to one of them is always relevant.</param>
    public static bool HasRelevantChange(IReadOnlyList<GitFileChange> changes, IReadOnlyCollection<string>? taskNamedFiles = null)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            return false;
        }

        var named = (taskNamedFiles ?? [])
            .Select(Normalize)
            .Where(static path => path.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return changes.Any(change => !IsWorkerNote(change) || NamedByTask(Normalize(change.FilePath), named));
    }

    /// <summary>The changes that are only notes, for the failure message.</summary>
    public static IReadOnlyList<string> NoteOnlyPaths(IReadOnlyList<GitFileChange> changes)
        => [.. changes.Where(IsWorkerNote).Select(static change => change.FilePath)];

    private static bool IsWorkerNote(GitFileChange change)
        => string.Equals(change.Status, "A", StringComparison.Ordinal) &&
           NoteExtensions.Contains(Path.GetExtension(change.FilePath), StringComparer.OrdinalIgnoreCase);

    private static bool NamedByTask(string path, HashSet<string> named)
        => named.Count > 0 && named.Any(file => path.Equals(file, StringComparison.OrdinalIgnoreCase) ||
                                               path.EndsWith("/" + file, StringComparison.OrdinalIgnoreCase) ||
                                               file.EndsWith("/" + path, StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string? path)
        => (path ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');
}
