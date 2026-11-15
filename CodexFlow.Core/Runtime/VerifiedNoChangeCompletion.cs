// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using CodexFlow.Core.Telemetry;

namespace CodexFlow.Core.Runtime;

/// <summary>
/// A forge task that needed no change. The rule "a forge worker must leave a change" exists because a weaker model says "done" having done
/// nothing; applied to a task that really needs no change it made workers invent one (seen live: an empty test project created for a task whose
/// condition did not hold, which then broke the host's test evidence; a marker comment added by a task that only ran the tests).
/// Finishing without a change is accepted only on evidence, never on the worker's prose:
/// the worker stated "completed" through the status tool and listed nothing unfinished, a build or test it ran in this turn exited cleanly and
/// none of its verification commands is left failing, and (checked by the host against the workspace) everything the task declares it must
/// produce is already there.
/// </summary>
public static class VerifiedNoChangeCompletion
{
    /// <summary>The name of the forge worker's "must write" contract, the only contract this evidence can stand in for.</summary>
    public const string ForgeWriteContractName = "forge_worker_write_evidence";

    public const string Feedback =
        "Forge worker cannot stop before successfully applying a workspace change. Call a write tool now; do not summarize first. " +
        "If the task truly needs no change (what it asks for is already in the project, or its condition does not hold): do NOT invent one. " +
        "Run the build or the tests, then call `report_task_status` with status=completed and say in `reason` why nothing had to change.";

    public static bool IsStated(QueryEvidenceLedger? ledger)
        => ledger is { WorkerReport: { IsBlocked: false }, UnappliedPreviews.Count: 0, FailedVerifications.Count: 0 } &&
           ledger.Verifications.Any(static verification => TestCommandEvidence.ProvesBehaviour(verification.Kind));

    /// <summary>True when this evidence may stand in for the given unmet contract of a forge worker.</summary>
    public static bool Waives(RequiredToolExecutionContract? contract, QueryLoopEntryPoint entryPoint, QueryEvidenceLedger? ledger)
        => contract != null &&
           entryPoint == QueryLoopEntryPoint.ForgeWorker &&
           string.Equals(contract.Name, ForgeWriteContractName, StringComparison.Ordinal) &&
           IsStated(ledger);

    /// <summary>The task's declared file assertions that the workspace does not meet; empty when it declares none or all hold.</summary>
    public static IReadOnlyList<string> UnmetAssertions(string projectRoot, CodexTask? task)
        => task == null
            ? []
            : DefaultCodexValidator.EvaluateArtifactAssertions(projectRoot, task.RequiredArtifacts.Concat(task.ForbiddenStates).ToList());
}
