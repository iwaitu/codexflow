// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Browser;
using CodexFlow.Core.ComputerUse;
using CodexFlow.Core.Execution.Approval;

namespace CodexFlow.Orchestration.ComputerUse;

/// <summary>
/// Checks whether ANY approval subsystem (execution/shell, browser, or computer use) currently
/// has pending approvals for a session so desktop input remains frozen during active approval prompts.
/// </summary>
public sealed class CompositeComputerApprovalFreezeProbe(
    IExecutionApprovalBroker? executionApprovalBroker = null,
    IBrowserApprovalCoordinator? browserApprovalCoordinator = null,
    IComputerApprovalCoordinator? computerApprovalCoordinator = null) : IComputerSessionApprovalFreezeProbe
{
    public bool HasAnyPendingApproval(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        if (executionApprovalBroker is not null && executionApprovalBroker.GetPendingApprovalCount(sessionId) > 0)
        {
            return true;
        }

        if (browserApprovalCoordinator is not null && browserApprovalCoordinator.GetPendingApprovalsForSession(sessionId).Count > 0)
        {
            return true;
        }

        if (computerApprovalCoordinator is not null && computerApprovalCoordinator.GetPendingApprovalsForSession(sessionId).Count > 0)
        {
            return true;
        }

        return false;
    }
}
