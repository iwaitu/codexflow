// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Services.Background;

/// <summary>
/// The detail code a forge result carries, decided in one place. The host's own verdicts come first, in a fixed order of precedence; an
/// attempt that ran tools and left the project untouched, with nothing else to name it, is <see cref="NoPersistedChanges"/>.
/// </summary>
internal static class ForgeDetailCode
{
    /// <summary>The attempt ran tools, reported nothing blocked, and no file of the project changed.</summary>
    public const string NoPersistedChanges = "no_persisted_changes";

    public static string? Resolve(
        bool restorePreflightFailed,
        bool workerBlocked,
        bool noRelevantChange,
        int unmetPackageCount,
        bool success,
        int changedFileCount,
        int toolCalls,
        string? runtimeDetailCode)
    {
        if (restorePreflightFailed) return "offline_restore_unavailable";
        if (workerBlocked) return "worker_reported_blocked";
        if (noRelevantChange) return "no_relevant_change";
        if (unmetPackageCount > 0) return "dependency_contract_unmet";
        if (!string.IsNullOrWhiteSpace(runtimeDetailCode)) return runtimeDetailCode;
        return !success && changedFileCount == 0 && toolCalls > 0 ? NoPersistedChanges : null;
    }
}
