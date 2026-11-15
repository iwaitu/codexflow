// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Services.Background;

/// <summary>
/// The commit a forge task started from. An attempt that runs in the worktree a failed attempt left behind starts at that attempt's commit, so
/// the artifact manifest of the retry would list only the retry's own changes and the failed attempt's commit would belong to no artifact of the
/// chain. The first attempt records its starting commit in the job payload; every later attempt in the same lineage keeps it.
/// </summary>
internal static class TaskBaseCommit
{
    /// <summary>The job payload key that holds the commit the task started from.</summary>
    public const string PayloadKey = "taskBaseCommit";

    /// <param name="recorded">The value in the payload, or null on the first attempt.</param>
    /// <param name="currentHead">HEAD of the worktree at the start of this attempt.</param>
    /// <param name="isAncestor">Asks git whether the first commit is an ancestor of the second.</param>
    public static async Task<string> ResolveAsync(string? recorded, string currentHead, Func<string, string, Task<bool>> isAncestor)
    {
        ArgumentNullException.ThrowIfNull(isAncestor);

        if (!IsFullCommitId(recorded) || string.Equals(recorded, currentHead, StringComparison.Ordinal))
        {
            return currentHead;
        }

        // A worktree rebuilt from another commit is a new lineage: the recorded base is no part of its history.
        return await isAncestor(recorded!, currentHead).ConfigureAwait(false) ? recorded! : currentHead;
    }

    private static bool IsFullCommitId(string? value)
        => value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);
}
