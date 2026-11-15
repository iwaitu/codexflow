// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Core.Constants;

public static class PlanGuardMetadataKeys
{
    public const string PlanResetRequired = "PlanResetRequired";
    public const string PlanResetReason = "PlanResetReason";
    public const string PlanResetAtUtc = "PlanResetAtUtc";

    public const string PlanPatchPending = "PlanPatchPending";
    public const string PlanPatchRevision = "PlanPatchRevision";
    public const string PlanPatchReason = "PlanPatchReason";
    public const string PlanPatchTaskId = "PlanPatchTaskId";
    public const string PlanPatchArtifactPath = "PlanPatchArtifactPath";
    public const string PlanPatchWorkspacePath = "PlanPatchWorkspacePath";
    public const string PlanPatchSuggestedFiles = "PlanPatchSuggestedFiles";
    public const string PlanPatchCreatedAtUtc = "PlanPatchCreatedAtUtc";
    public const string PlanPatchApprovedWorkspacePath = "PlanPatchApprovedWorkspacePath";
    public const string PlanPatchApprovedTaskId = "PlanPatchApprovedTaskId";

    public static readonly string[] PlanPatchKeys =
    [
        PlanPatchPending,
        PlanPatchRevision,
        PlanPatchReason,
        PlanPatchTaskId,
        PlanPatchArtifactPath,
        PlanPatchWorkspacePath,
        PlanPatchSuggestedFiles,
        PlanPatchCreatedAtUtc
    ];
}
