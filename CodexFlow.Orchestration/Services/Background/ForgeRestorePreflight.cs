// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Contracts;
using CodexFlow.Services.ArtifactAudit;

namespace CodexFlow.Services.Background;

/// <summary>
/// Right after a forge task that changed dependency manifests, restores the result the way final verification will: pinned snapshot, no network,
/// host-fetched packages only. The worker's own sandbox can reach the network, so its restore says nothing about this; without the preflight an
/// unrestorable version is found only after every later task has run.
/// </summary>
internal static class ForgeRestorePreflight
{
    private static readonly string[] ManifestSuffixes = [".csproj", ".fsproj", ".vbproj", ".props", ".targets"];
    private static readonly string[] ManifestFileNames = ["nuget.config", "packages.lock.json", "global.json"];

    public static bool TouchesDependencyManifests(IEnumerable<string> changedPaths) =>
        changedPaths.Any(path =>
        {
            var name = path.Replace('\\', '/').Split('/')[^1];
            return ManifestFileNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                   ManifestSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        });

    /// <summary>Null when the change restores, needs no check, or the check itself could not run; otherwise the failure to put on the task.</summary>
    public static async Task<string?> CheckAsync(IDependencyRiskScanService? scan, ForgeArtifactManifest manifest, CancellationToken ct)
    {
        if (scan is null || !TouchesDependencyManifests(manifest.ChangedPaths)) return null;

        var report = await scan.ScanCommitAsync(manifest.WorktreePath, manifest.HeadCommit, manifest.UserId, manifest.SessionId, "restore-preflight", ct).ConfigureAwait(false);
        // Only an actual restore failure is the change's fault; a missing image, docker or advisory data is not.
        if (report.Available || !report.Message.StartsWith("Offline restore failed", StringComparison.Ordinal)) return null;

        return "依赖清单的改动无法在验收环境离线还原，不能进入验收：" + report.Message +
               " 验收环境无网络，只能还原仓库里已有的包版本，以及依赖风险简报里标注为宿主已取包的目标版本；" +
               "请改回可还原的版本，或把需要的版本告知用户由其决定。";
    }
}
