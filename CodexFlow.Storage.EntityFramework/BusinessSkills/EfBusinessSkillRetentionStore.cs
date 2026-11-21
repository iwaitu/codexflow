// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Orchestration.Jobs;
using Microsoft.EntityFrameworkCore;

namespace CodexFlow.Orchestration.BusinessSkills;

public sealed class EfBusinessSkillRetentionStore(IJobDbContext db) : IBusinessSkillRetentionStore
{
    public async Task<BusinessSkillRetentionCounts> PruneAsync(BusinessSkillRetentionCutoffs cutoffs, CancellationToken ct)
    {
        var (runCutoff, diagCutoff, opCutoff) = cutoffs;

        // 1. 删除超过 RunHistoryRetentionDays 的已终态运行（绝不自动删除 OutcomeUnknown / WaitingForApproval / Running / Queued）
        var terminalStatuses = new HashSet<string>(BusinessSkillRetentionPolicy.TerminalRunStatuses, StringComparer.Ordinal);
        var outcomeUnknownStatus = BusinessSkillRetentionPolicy.OutcomeUnknownStatus;

        var candidateRuns = await db.SkillRuns
            .Where(r => r.UpdatedAtUtc < runCutoff || r.Status == outcomeUnknownStatus)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var retainedUnknownCount = candidateRuns.Count(r => string.Equals(r.Status, outcomeUnknownStatus, StringComparison.Ordinal));
        var expiredRunIds = candidateRuns
            .Where(r => r.UpdatedAtUtc < runCutoff && terminalStatuses.Contains(r.Status))
            .Select(r => r.RunId)
            .ToList();

        var deletedRunsCount = 0;
        if (expiredRunIds.Count > 0)
        {
            var expiredSteps = await db.SkillRunSteps
                .Where(s => expiredRunIds.Contains(s.RunId))
                .ToListAsync(ct)
                .ConfigureAwait(false);
            if (expiredSteps.Count > 0)
            {
                db.SkillRunSteps.RemoveRange(expiredSteps);
            }

            var expiredRuns = candidateRuns.Where(r => expiredRunIds.Contains(r.RunId)).ToList();
            db.SkillRuns.RemoveRange(expiredRuns);
            deletedRunsCount = expiredRuns.Count;
        }

        // 2. 对未超 30 天但已超 DiagnosticsRetentionDays（默认 7 天）的终态运行裁剪步骤请求/响应诊断摘要
        var diagEligibleRunIds = await db.SkillRuns
            .Where(r => r.UpdatedAtUtc < diagCutoff &&
                        !expiredRunIds.Contains(r.RunId) &&
                        BusinessSkillRetentionPolicy.TerminalRunStatuses.Contains(r.Status))
            .Select(r => r.RunId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var trimmedDiagCount = 0;
        if (diagEligibleRunIds.Count > 0)
        {
            var stepsToTrim = await db.SkillRunSteps
                .Where(s => diagEligibleRunIds.Contains(s.RunId))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var step in stepsToTrim)
            {
                var changed = false;
                if (!string.IsNullOrEmpty(step.RequestSummary) &&
                    !string.Equals(step.RequestSummary, BusinessSkillRetentionService.RedactedDiagnosticsMarker, StringComparison.Ordinal))
                {
                    step.RequestSummary = BusinessSkillRetentionService.RedactedDiagnosticsMarker;
                    changed = true;
                }
                if (!string.IsNullOrEmpty(step.ResponseSummary) &&
                    !string.Equals(step.ResponseSummary, BusinessSkillRetentionService.RedactedDiagnosticsMarker, StringComparison.Ordinal))
                {
                    step.ResponseSummary = BusinessSkillRetentionService.RedactedDiagnosticsMarker;
                    changed = true;
                }
                if (changed)
                {
                    trimmedDiagCount++;
                }
            }
        }

        // 3. 清理已完结且超期的操作记录
        var expiredOps = await db.PluginOperations
            .Where(o => o.UpdatedAtUtc < opCutoff && BusinessSkillRetentionPolicy.TerminalOperationStatuses.Contains(o.Status))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (expiredOps.Count > 0)
        {
            db.PluginOperations.RemoveRange(expiredOps);
        }

        // 4. 仅在引用不存在时清理过期历史目录快照（插件当前快照、草稿快照、不可变已发布版本均受保护）
        var activePluginSnapshots = await db.BusinessPlugins
            .Where(p => p.ActiveSnapshotId != null)
            .Select(p => p.ActiveSnapshotId!)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var draftSnapshots = await db.SkillDrafts
            .Select(d => d.CatalogSnapshotId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var publishedVersionSnapshots = await db.PublishedSkillVersions
            .Select(v => v.CatalogSnapshotId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var protectedSnapshots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var snap in activePluginSnapshots) protectedSnapshots.Add(snap);
        foreach (var snap in draftSnapshots) protectedSnapshots.Add(snap);
        foreach (var snap in publishedVersionSnapshots) protectedSnapshots.Add(snap);

        var oldSnapshots = await db.CatalogSnapshots
            .Where(s => s.FetchedAtUtc < runCutoff)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var orphanSnapshotIds = oldSnapshots
            .Where(s => !protectedSnapshots.Contains(s.Id))
            .Select(s => s.Id)
            .ToList();

        var deletedSnapshotsCount = 0;
        if (orphanSnapshotIds.Count > 0)
        {
            var orphanCaps = await db.CatalogCapabilities
                .Where(c => orphanSnapshotIds.Contains(c.SnapshotId))
                .ToListAsync(ct)
                .ConfigureAwait(false);
            if (orphanCaps.Count > 0)
            {
                db.CatalogCapabilities.RemoveRange(orphanCaps);
            }

            var orphanSnapshots = oldSnapshots.Where(s => orphanSnapshotIds.Contains(s.Id)).ToList();
            db.CatalogSnapshots.RemoveRange(orphanSnapshots);
            deletedSnapshotsCount = orphanSnapshots.Count;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new BusinessSkillRetentionCounts(
            trimmedDiagCount,
            deletedRunsCount,
            deletedSnapshotsCount,
            expiredOps.Count,
            retainedUnknownCount,
            protectedSnapshots.Count);
    }
}
