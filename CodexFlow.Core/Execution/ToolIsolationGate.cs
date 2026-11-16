// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Models;

namespace CodexFlow.Core.Execution;

/// <summary>
/// Shared gate for tools that execute untrusted code. The trusted context comes only from the host-bound accessor;
/// model arguments (workspace_path, project_root, session_id, ...) are never consulted. Missing context, missing
/// backend or an Unknown capability all refuse, and there is no host fallback.
/// </summary>
public sealed class ToolIsolationGate(
    IIsolatedExecutionService? service,
    ITrustedExecutionContextAccessor? accessor,
    IsolationOptions? options)
{
    private readonly ITrustedExecutionContextAccessor _accessor = accessor ?? new TrustedExecutionContextAccessor();

    public IsolationOptions Options { get; } = options ?? new IsolationOptions();

    public IIsolatedExecutionService? Service => service;

    public static ToolIsolationGate Unconfigured { get; } = new(null, null, null);

    /// <summary>Returns null when execution may proceed, otherwise a failed tool result carrying the reason code.</summary>
    public CodexToolResult? TryAcquire(string toolName, ToolCapability capability, out TrustedExecutionContext context, out IIsolatedExecutionService backend)
    {
        context = null!;
        backend = null!;

        var capabilityDecision = ToolCapabilityCatalog.Evaluate(toolName, capability);
        if (!capabilityDecision.IsAllowed)
        {
            return Refuse(toolName, capabilityDecision);
        }

        if (capability != ToolCapability.ExecuteUntrustedCode)
        {
            return Refuse(toolName, ExecutionDecision.Deny(ExecutionReasonCodes.UnknownToolCapability, $"Tool '{toolName}' is not declared as an isolated-execution tool."));
        }

        var current = _accessor.Current;
        if (current is null)
        {
            return Refuse(toolName, ExecutionDecision.Deny(
                ExecutionReasonCodes.MissingTrustedContext,
                "No host-provided execution context is bound to this call; untrusted code is never run without it."));
        }

        var valid = current.Validate();
        if (!valid.IsAllowed)
        {
            return Refuse(toolName, valid);
        }

        if (service is null)
        {
            return Refuse(toolName, ExecutionDecision.Unavailable(
                ExecutionReasonCodes.IsolationNotConfigured,
                "No isolated execution backend is configured; refusing to run on the host."));
        }

        if (!service.SupportsNetworkMode(current.Network.Mode))
        {
            return Refuse(toolName, ExecutionDecision.Unavailable(
                ExecutionReasonCodes.NetworkNotPermitted,
                $"Execution backend '{service.GetType().Name}' does not support network mode '{current.Network.Mode}'."));
        }

        context = current;
        backend = service;
        return null;
    }

    public static CodexToolResult Refuse(string toolName, ExecutionDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var verb = decision.Kind == ExecutionDecisionKind.Unavailable ? "unavailable" : "denied";
        return CodexToolResult.Error(
            $"{toolName} {verb}: [{decision.ReasonCode}] {decision.Message}",
            new { decision.Kind, decision.ReasonCode, decision.Message });
    }
}
