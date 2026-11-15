// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Gateway;
using CodexFlow.Core.Models;
using CodexFlow.Core.Planning.Artifacts;
using CodexFlow.Core.Runtime;
using CodexFlow.Services.ArtifactAudit;

namespace CodexFlow.Gateway;

/// <summary>Which plan-state tool a <see cref="GatewayGuardedPlanTool"/> protects.</summary>
public enum GatewayGuardedPlanToolRole
{
    /// <summary>approve_plan</summary>
    Approve,

    /// <summary>reject_plan</summary>
    Reject,

    /// <summary>request_plan_approval (re-opens approval, so it invalidates any recorded approval)</summary>
    Request
}

/// <summary>
/// Decorator installed by the Gateway tool lookup around approve_plan / reject_plan / request_plan_approval.
/// Because it wraps the tool itself, every path that can reach it - the direct approval path, the AIFunction wrapper,
/// and the query runtime's raw tool list - shares the same intent decision and plan-identity checks. The model can
/// neither bypass the gate by calling the runtime tool directly nor supply a different artifact id to approve.
/// </summary>
public sealed class GatewayGuardedPlanTool : ICodexTool
{
    private readonly ICodexTool _inner;
    private readonly GatewayMessage _message;
    private readonly CodexSession? _session;
    private readonly GatewayApprovalGate _gate;
    private readonly GatewayGuardedPlanToolRole _role;
    private readonly IDependencyUpgradeApprover? _upgradeApprover;

    public GatewayGuardedPlanTool(
        ICodexTool inner,
        GatewayGuardedPlanToolRole role,
        GatewayMessage message,
        CodexSession? session,
        GatewayApprovalGate gate,
        IDependencyUpgradeApprover? upgradeApprover = null)
    {
        _upgradeApprover = upgradeApprover;
        _inner = inner;
        _role = role;
        _message = message;
        _session = session;
        _gate = gate;
    }

    public string Name => _inner.Name;
    public string Description => _inner.Description;
    public ToolCategory Category => _inner.Category;
    public IReadOnlyList<int> AllowedStages => _inner.AllowedStages;
    public ToolExecutionMetadata Metadata => _inner.Metadata;

    public ValueTask<CodexToolValidationResult> ValidateInputAsync(
        Dictionary<string, object?> arguments,
        CancellationToken ct = default)
        => _inner.ValidateInputAsync(arguments, ct);

    public ValueTask<CodexToolPermissionResult> CheckPermissionsAsync(
        Dictionary<string, object?> arguments,
        QueryRuntimeRequest request,
        QueryRuntimeState? state = null,
        CancellationToken ct = default)
        => _inner.CheckPermissionsAsync(arguments, request, state, ct);

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var requestedPlanId = ReadString(arguments, "plan_artifact_id");
        switch (_role)
        {
            case GatewayGuardedPlanToolRole.Approve:
                return await ExecuteApproveAsync(arguments, requestedPlanId, ct).ConfigureAwait(false);

            case GatewayGuardedPlanToolRole.Reject:
            {
                var gate = await _gate.EvaluateRejectionAsync(_message, requestedPlanId, _session, ct).ConfigureAwait(false);
                if (!gate.IsAllowed)
                {
                    return Denied(gate);
                }

                return await _inner.ExecuteAsync(Pin(arguments, gate.Identity), ct).ConfigureAwait(false);
            }

            default:
            {
                var result = await _inner.ExecuteAsync(Pin(arguments, null), ct).ConfigureAwait(false);
                if (result.Status is ToolResultStatus.Success or ToolResultStatus.PartialSuccess)
                {
                    // Re-opening approval means any earlier approval no longer describes the plan state.
                    _gate.Ledger.Invalidate(_message.SessionId);
                }

                return result;
            }
        }
    }

    private async Task<CodexToolResult> ExecuteApproveAsync(
        Dictionary<string, object?> arguments,
        string? requestedPlanId,
        CancellationToken ct)
    {
        var gate = await _gate.EvaluateApprovalAsync(_message, requestedPlanId, _session, ct).ConfigureAwait(false);
        if (!gate.IsAllowed)
        {
            return Denied(gate);
        }

        if (gate.Status == GatewayGateStatus.AlreadyApproved)
        {
            return CodexToolResult.Succeeded(
                "PLAN_ALREADY_APPROVED",
                metadata: new
                {
                    plan_artifact_id = gate.Identity?.PlanArtifactId,
                    plan_version = gate.Identity?.PlanVersion,
                    markdown_hash = gate.Identity?.PlanHash,
                    idempotent = true
                },
                summary: gate.Message);
        }

        // The approval always targets the plan whose identity the gate verified, never a model-chosen id.
        var result = await _inner.ExecuteAsync(Pin(arguments, gate.Identity), ct).ConfigureAwait(false);
        if (result.Status is ToolResultStatus.Success or ToolResultStatus.PartialSuccess)
        {
            _gate.RecordApproval(_message, gate.Identity);
            await ApproveDependencyUpgradesAsync(result, ct).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// The user's approval of major-version upgrades comes ONLY from the authenticated structured approve action (never from text or model
    /// arguments). The plan is already approved: a fetch problem is reported in the result, it never turns the approval into a failure.
    /// </summary>
    private async Task ApproveDependencyUpgradesAsync(CodexToolResult result, CancellationToken ct)
    {
        if (_upgradeApprover is null ||
            !GatewayStructuredActions.TryGet(_message, out var structured) ||
            structured is not { Kind: GatewayStructuredActionKind.Approve, ApproveDependencyUpgrades: { Count: > 0 } packages })
        {
            return;
        }

        string note;
        try
        {
            var approval = await _upgradeApprover.ApproveAsync(_message.SessionId, _message.UserId ?? string.Empty, packages, ct).ConfigureAwait(false);
            note = $"依赖升级批准：已批准 {(approval.Approved.Count == 0 ? "无" : string.Join("、", approval.Approved))}"
                   + (approval.Ignored.Count > 0 ? $"；已忽略 {string.Join("、", approval.Ignored)}（不是宿主扫描解析出的待批准项）" : string.Empty)
                   + (approval.Overlay is null ? string.Empty : approval.Overlay.Success ? "；宿主已取包，升级任务可在验收沙箱中还原。" : $"；宿主取包失败：{approval.Overlay.Message}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            note = "依赖升级批准：宿主取包时出错（" + ex.GetType().Name + "），计划批准不受影响。";
        }

        result.Output = string.IsNullOrEmpty(result.Output) ? note : result.Output + "\n\n" + note;
    }

    private Dictionary<string, object?> Pin(Dictionary<string, object?> arguments, PlanApprovalIdentity? identity)
    {
        var pinned = new Dictionary<string, object?>(arguments, StringComparer.Ordinal)
        {
            // Never trust model-supplied session/user ids (runtime tool path passes raw model arguments).
            ["session_id"] = _message.SessionId,
            ["user_id"] = _message.UserId ?? string.Empty,
            // Accepting existing dependency debt is the user's decision on the approval screen, never the model's: whatever the
            // model passed is replaced by what the authenticated structured action carried (false when there is none).
            [ApprovalArguments.AcceptExistingDependencyDebt] =
                GatewayStructuredActions.TryGet(_message, out var structured) && structured is { Kind: GatewayStructuredActionKind.Approve, AcceptExistingDependencyDebt: true }
        };
        if (identity != null && identity.PlanArtifactId != "session-plan")
        {
            pinned["plan_artifact_id"] = identity.PlanArtifactId;
        }

        return pinned;
    }

    private static CodexToolResult Denied(GatewayGateResult gate)
        => CodexToolResult.Error(
            $"[{gate.ReasonCode}] {gate.Message}",
            metadata: new { reason_code = gate.ReasonCode, gateway_gate_denial = true });

    /// <summary>True when the result is a refusal produced by this gate (structured marker, not output text).</summary>
    public static bool IsGateDenial(CodexToolResult result)
        => result.Status == ToolResultStatus.Failed &&
           result.Metadata?.GetType().GetProperty("gateway_gate_denial")?.GetValue(result.Metadata) is true;

    private static string? ReadString(Dictionary<string, object?> arguments, string key)
    {
        if (arguments.TryGetValue(key, out var value) && value != null)
        {
            var text = value.ToString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        return null;
    }
}
