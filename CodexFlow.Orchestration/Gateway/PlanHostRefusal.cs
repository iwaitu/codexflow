// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Planning.Artifacts;

namespace CodexFlow.Gateway;

/// <summary>
/// A plan the host refused (it raises packages the host did not fetch, in a session that allowed only the fetched versions). The refusal is a
/// record on the artifact, tied to the hash of the markdown it was made for: the same plan stays refused whoever asks again, and a plan that
/// was changed is a different plan and can be approved. Marking the artifact Rejected was not enough: the next "approve" message re-opened it
/// through request_plan_approval, which sets ReadyForApproval unconditionally, and approved it.
/// </summary>
public static class PlanHostRefusal
{
    /// <summary>The <see cref="PlanApprovalRecord.Mode"/> of a host refusal.</summary>
    public const string Mode = "host_refused";

    /// <summary>Rejects <paramref name="artifact"/> on the host's behalf and remembers which markdown the refusal is about.</summary>
    public static void Record(PlanArtifact artifact, string reason)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        artifact.Status = PlanArtifactStatus.Rejected;
        artifact.Approval = new PlanApprovalRecord
        {
            ApprovedByUserId = string.Empty,
            Mode = Mode,
            Feedback = reason,
            ApprovedMarkdownHash = PlanArtifactSupport.ComputeMarkdownHash(artifact.Markdown)
        };
    }

    /// <summary>True when the host refused exactly this markdown; <paramref name="reason"/> is what it told the model.</summary>
    public static bool Refuses(PlanArtifact artifact, out string reason)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        reason = string.Empty;
        if (artifact.Approval is not { } record ||
            !string.Equals(record.Mode, Mode, StringComparison.Ordinal) ||
            !string.Equals(record.ApprovedMarkdownHash, PlanArtifactSupport.ComputeMarkdownHash(artifact.Markdown), StringComparison.Ordinal))
        {
            return false;
        }

        reason = record.Feedback ?? string.Empty;
        return true;
    }
}
