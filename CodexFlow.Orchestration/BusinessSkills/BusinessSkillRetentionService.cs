// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Contracts.BusinessSkills;
using CodexFlow.Core.BusinessSkills;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodexFlow.Orchestration.BusinessSkills;

/// <summary>
/// 业务运行历史、步骤诊断报文及孤立目录快照的保留期治理服务：
/// 遵循设计第 12 节约定，默认保留运行记录 30 天、步骤诊断摘要 7 天，
/// 严格保护活跃草稿/已发布版本/运行引用的不可变版本与快照，且永不自动删除 OutcomeUnknown 运行。
/// </summary>
public interface IBusinessSkillRetentionService
{
    Task<BusinessSkillRetentionPruneResultDto> PruneExpiredAsync(
        BusinessSkillRetentionPruneRequest? request = null,
        DateTimeOffset? utcNowOverride = null,
        CancellationToken ct = default);
}

public sealed class BusinessSkillRetentionService(
    IBusinessSkillRetentionStore store,
    IOptionsMonitor<BusinessSkillsOptions> options,
    ILogger<BusinessSkillRetentionService> logger) : IBusinessSkillRetentionService
{
    public const string RedactedDiagnosticsMarker = "[REDACTED_EXPIRED_DIAGNOSTICS]";

    public async Task<BusinessSkillRetentionPruneResultDto> PruneExpiredAsync(
        BusinessSkillRetentionPruneRequest? request = null,
        DateTimeOffset? utcNowOverride = null,
        CancellationToken ct = default)
    {
        var opts = options.CurrentValue;
        var runDays = Math.Max(1, request?.RunHistoryRetentionDays ?? opts.RunHistoryRetentionDays);
        var diagDays = Math.Max(1, request?.DiagnosticsRetentionDays ?? opts.DiagnosticsRetentionDays);
        var opDays = Math.Max(1, request?.OperationHistoryRetentionDays ?? opts.OperationHistoryRetentionDays);
        var now = (utcNowOverride ?? DateTimeOffset.UtcNow).UtcDateTime;
        var cutoffs = new BusinessSkillRetentionCutoffs(
            now.AddDays(-runDays), now.AddDays(-diagDays), now.AddDays(-opDays));

        var counts = await store.PruneAsync(cutoffs, ct).ConfigureAwait(false);
        logger.LogInformation(
            "BusinessSkill retention prune completed: trimmedSteps={TrimmedSteps}, deletedRuns={DeletedRuns}, deletedSnapshots={DeletedSnapshots}, deletedOps={DeletedOps}, retainedUnknownRuns={UnknownRuns}, retainedProtectedSnapshots={ProtectedSnapshots}",
            counts.TrimmedStepDiagnosticsCount,
            counts.DeletedTerminalRunsCount,
            counts.DeletedOrphanSnapshotsCount,
            counts.DeletedOperationsCount,
            counts.RetainedOutcomeUnknownRunsCount,
            counts.RetainedProtectedSnapshotsCount);

        return new BusinessSkillRetentionPruneResultDto(
            counts.TrimmedStepDiagnosticsCount,
            counts.DeletedTerminalRunsCount,
            counts.DeletedOrphanSnapshotsCount,
            counts.DeletedOperationsCount,
            counts.RetainedOutcomeUnknownRunsCount,
            counts.RetainedProtectedSnapshotsCount,
            new DateTimeOffset(now, TimeSpan.Zero));
    }
}
