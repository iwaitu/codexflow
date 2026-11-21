// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Contracts.BusinessSkills;

namespace CodexFlow.Orchestration.BusinessSkills;

public sealed record BusinessSkillRetentionCutoffs(
    DateTime RunHistoryUtc, DateTime DiagnosticsUtc, DateTime OperationHistoryUtc);

public sealed record BusinessSkillRetentionCounts(
    int TrimmedStepDiagnosticsCount,
    int DeletedTerminalRunsCount,
    int DeletedOrphanSnapshotsCount,
    int DeletedOperationsCount,
    int RetainedOutcomeUnknownRunsCount,
    int RetainedProtectedSnapshotsCount);

/// <summary>Prunes expired business skill data while honoring the retention exemptions.</summary>
public interface IBusinessSkillRetentionStore
{
    Task<BusinessSkillRetentionCounts> PruneAsync(BusinessSkillRetentionCutoffs cutoffs, CancellationToken ct);
}

internal static class BusinessSkillRetentionPolicy
{
    public static readonly string OutcomeUnknownStatus = SkillRunStatus.OutcomeUnknown.ToString();

    public static readonly string[] TerminalRunStatuses =
    [
        SkillRunStatus.Succeeded.ToString(),
        SkillRunStatus.PartialSucceeded.ToString(),
        SkillRunStatus.Failed.ToString(),
        SkillRunStatus.Cancelled.ToString(),
    ];

    public static readonly string[] TerminalOperationStatuses =
    [
        PluginOperationStatus.Succeeded.ToString(),
        PluginOperationStatus.Failed.ToString(),
        PluginOperationStatus.Cancelled.ToString(),
    ];
}
