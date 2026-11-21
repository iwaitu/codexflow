// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexFlow.Contracts.BusinessSkills;

namespace CodexFlow.Core.BusinessSkills;

public sealed record SkillValidationResult(
    bool IsValid,
    IReadOnlyList<BusinessSkillFieldError> Errors,
    string ContentHash);

/// <summary>
/// P0 冻结的 Skill JSON v1 规范化哈希与静态校验器。
/// </summary>
public static class SkillDefinitionV1ValidatorAndCanonicalizer
{
    private static readonly BusinessSkillDefinitionJsonContext CanonicalJson = new(new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    });

    private static readonly HashSet<string> AllowedTransformJoinMissingPolicies = new(StringComparer.Ordinal)
    {
        "drop",
        "null",
        "zero_or_empty",
        "fail"
    };

    private static readonly HashSet<string> AllowedAggregateFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "sum",
        "count",
        "min",
        "max"
    };

    private static readonly HashSet<string> AllowedJoinKeyTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "string",
        "integer"
    };

    private static readonly HashSet<string> AllowedConditionOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "eq",
        "ne",
        "gt",
        "gte",
        "lt",
        "lte",
        "in",
        "contains"
    };

    public static string ComputeContentHash(SkillDefinitionV1 definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        // 规范化哈希排除动态时间戳与非行为性验证证据字段。
        var normalized = definition with
        {
            ContentHash = string.Empty,
            ValidationEvidence = new SkillValidationEvidenceDto()
        };

        var json = JsonSerializer.Serialize(normalized, CanonicalJson.SkillDefinitionV1);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static SkillValidationResult ValidateStatic(SkillDefinitionV1 definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var errors = new List<BusinessSkillFieldError>();

        ValidateTopLevel(definition, errors);
        ValidateCapabilitySelection(definition, errors);
        ValidateBudgets(definition.Budgets, errors);
        ValidateSteps(definition, errors);

        var contentHash = ComputeContentHash(definition);
        return new SkillValidationResult(errors.Count == 0, errors, contentHash);
    }

    /// <summary>
    /// 结合插件目录快照、能力可执行性、风险等级与绑定环境对 Skill 定义做上下文核对（静态校验通过后再核对目录）。
    /// </summary>
    public static SkillValidationResult ValidateAgainstCatalog(
        SkillDefinitionV1 definition,
        string expectedPluginId,
        string expectedSnapshotId,
        string expectedEnvironment,
        IReadOnlyDictionary<string, CapabilityDetailDto> snapshotCapabilities,
        bool isReadOnlyTestRun = false)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(snapshotCapabilities);

        var baseResult = ValidateStatic(definition);
        var errors = new List<BusinessSkillFieldError>(baseResult.Errors);

        if (!string.Equals(definition.PluginId, expectedPluginId, StringComparison.Ordinal))
        {
            errors.Add(new("pluginId", BusinessSkillErrorCodes.SelectionViolation,
                $"Skill pluginId '{definition.PluginId}' does not match plugin '{expectedPluginId}'."));
        }

        if (!string.Equals(definition.CatalogSnapshotId, expectedSnapshotId, StringComparison.Ordinal))
        {
            errors.Add(new("catalogSnapshotId", BusinessSkillErrorCodes.CatalogChanged,
                $"Skill catalogSnapshotId '{definition.CatalogSnapshotId}' does not match active snapshot '{expectedSnapshotId}'."));
        }

        if (!string.IsNullOrWhiteSpace(definition.ConnectionBinding.Environment) &&
            !string.Equals(definition.ConnectionBinding.Environment, expectedEnvironment, StringComparison.Ordinal))
        {
            errors.Add(new("connectionBinding.environment", BusinessSkillErrorCodes.SelectionViolation,
                $"Skill connection environment '{definition.ConnectionBinding.Environment}' does not match '{expectedEnvironment}'."));
        }

        foreach (var capId in definition.SelectedCapabilityIds)
        {
            if (!snapshotCapabilities.ContainsKey(capId))
            {
                errors.Add(new("selectedCapabilityIds", BusinessSkillErrorCodes.CatalogChanged,
                    $"Selected capability '{capId}' does not exist in snapshot '{expectedSnapshotId}'."));
            }
        }

        foreach (var capId in definition.UsedCapabilityIds)
        {
            if (!snapshotCapabilities.TryGetValue(capId, out var cap))
            {
                errors.Add(new("usedCapabilityIds", BusinessSkillErrorCodes.CatalogChanged,
                    $"Used capability '{capId}' does not exist in snapshot '{expectedSnapshotId}'."));
                continue;
            }

            if (cap.Summary.Executability != CapabilityExecutabilityStatus.Executable)
            {
                errors.Add(new("usedCapabilityIds", BusinessSkillErrorCodes.UnsupportedSchema,
                    $"Used capability '{capId}' has executability '{cap.Summary.Executability}' and cannot be executed."));
            }
        }

        var approvedCapabilitiesSeen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < definition.Steps.Count; i++)
        {
            var step = definition.Steps[i];
            if (step is SkillApprovalStep approval)
            {
                approvedCapabilitiesSeen.Add(approval.TargetCapabilityId);
                continue;
            }

            if (step is SkillToolStep toolStep && snapshotCapabilities.TryGetValue(toolStep.CapabilityId, out var cap))
            {
                var risk = cap.Summary.Risk;
                if (isReadOnlyTestRun && risk != CapabilityRiskLevel.Read)
                {
                    errors.Add(new($"steps[{i}].capabilityId", BusinessSkillErrorCodes.SelectionViolation,
                        $"Capability '{toolStep.CapabilityId}' has risk '{risk}'; only confirmed Read capabilities are allowed in read-only test runs."));
                }
                else if (risk != CapabilityRiskLevel.Read && !approvedCapabilitiesSeen.Contains(toolStep.CapabilityId))
                {
                    errors.Add(new($"steps[{i}].capabilityId", BusinessSkillErrorCodes.ApprovalRequired,
                        $"Non-read capability '{toolStep.CapabilityId}' (risk '{risk}') must be preceded by a matching SkillApprovalStep."));
                }

                foreach (var param in cap.Invocation.Parameters.Where(static p => p.Required))
                {
                    if (!toolStep.Arguments.ContainsKey(param.Name))
                    {
                        errors.Add(new($"steps[{i}].arguments.{param.Name}", BusinessSkillErrorCodes.ValidationFailed,
                            $"Required capability parameter '{param.Name}' is missing binding."));
                    }
                }
            }
        }

        return new SkillValidationResult(errors.Count == 0, errors, baseResult.ContentHash);
    }

    private static void ValidateTopLevel(SkillDefinitionV1 definition, List<BusinessSkillFieldError> errors)
    {
        if (definition.SchemaVersion != 1)
        {
            errors.Add(new("schemaVersion", BusinessSkillErrorCodes.ValidationFailed, "schemaVersion must equal 1."));
        }

        if (string.IsNullOrWhiteSpace(definition.SkillId))
        {
            errors.Add(new("skillId", BusinessSkillErrorCodes.ValidationFailed, "skillId is required."));
        }

        if (definition.Version <= 0)
        {
            errors.Add(new("version", BusinessSkillErrorCodes.ValidationFailed, "version must be >= 1."));
        }

        if (string.IsNullOrWhiteSpace(definition.PluginId))
        {
            errors.Add(new("pluginId", BusinessSkillErrorCodes.ValidationFailed, "pluginId is required."));
        }

        if (string.IsNullOrWhiteSpace(definition.CatalogSnapshotId))
        {
            errors.Add(new("catalogSnapshotId", BusinessSkillErrorCodes.CatalogIncomplete, "catalogSnapshotId is required."));
        }

        if (definition.Steps.Count == 0)
        {
            errors.Add(new("steps", BusinessSkillErrorCodes.ValidationFailed, "Skill must contain at least one step."));
        }
        else if (definition.Steps.Count > BusinessSkillDefaults.MaxSkillSteps)
        {
            errors.Add(new("steps", BusinessSkillErrorCodes.BudgetExceeded,
                string.Format(CultureInfo.InvariantCulture, "Skill step count cannot exceed {0}.", BusinessSkillDefaults.MaxSkillSteps)));
        }
    }

    private static void ValidateCapabilitySelection(SkillDefinitionV1 definition, List<BusinessSkillFieldError> errors)
    {
        if (definition.UsedCapabilityIds.Count > BusinessSkillDefaults.MaxUsedCapabilities)
        {
            errors.Add(new("usedCapabilityIds", BusinessSkillErrorCodes.BudgetExceeded,
                string.Format(CultureInfo.InvariantCulture, "Used capability count cannot exceed {0}.", BusinessSkillDefaults.MaxUsedCapabilities)));
        }

        var selectedSet = new HashSet<string>(definition.SelectedCapabilityIds, StringComparer.Ordinal);
        foreach (var used in definition.UsedCapabilityIds)
        {
            if (!selectedSet.Contains(used))
            {
                errors.Add(new("usedCapabilityIds", BusinessSkillErrorCodes.SelectionViolation,
                    $"Used capability '{used}' is outside the frozen selectedCapabilityIds set."));
            }
        }
    }

    private static void ValidateBudgets(SkillBudgetPolicyDto budgets, List<BusinessSkillFieldError> errors)
    {
        if (budgets.MaxExternalRequests <= 0 || budgets.MaxExternalRequests > BusinessSkillDefaults.MaxExternalRequestsPerRun)
        {
            errors.Add(new("budgets.maxExternalRequests", BusinessSkillErrorCodes.BudgetExceeded,
                string.Format(CultureInfo.InvariantCulture, "maxExternalRequests must be within 1..{0}.", BusinessSkillDefaults.MaxExternalRequestsPerRun)));
        }

        if (budgets.MaxReadRecords <= 0 || budgets.MaxReadRecords > BusinessSkillDefaults.MaxReadRecordsPerRun)
        {
            errors.Add(new("budgets.maxReadRecords", BusinessSkillErrorCodes.BudgetExceeded,
                string.Format(CultureInfo.InvariantCulture, "maxReadRecords must be within 1..{0}.", BusinessSkillDefaults.MaxReadRecordsPerRun)));
        }

        if (budgets.MaxJoinOutputRows <= 0 || budgets.MaxJoinOutputRows > BusinessSkillDefaults.MaxJoinOutputRows)
        {
            errors.Add(new("budgets.maxJoinOutputRows", BusinessSkillErrorCodes.BudgetExceeded,
                string.Format(CultureInfo.InvariantCulture, "maxJoinOutputRows must be within 1..{0}.", BusinessSkillDefaults.MaxJoinOutputRows)));
        }

        if (budgets.MaxWriteBatchItems <= 0 || budgets.MaxWriteBatchItems > BusinessSkillDefaults.MaxWriteBatchItems)
        {
            errors.Add(new("budgets.maxWriteBatchItems", BusinessSkillErrorCodes.BudgetExceeded,
                string.Format(CultureInfo.InvariantCulture, "maxWriteBatchItems must be within 1..{0}.", BusinessSkillDefaults.MaxWriteBatchItems)));
        }
    }

    private static void ValidateSteps(SkillDefinitionV1 definition, List<BusinessSkillFieldError> errors)
    {
        var seenSteps = new HashSet<string>(StringComparer.Ordinal);
        var selectedSet = new HashSet<string>(definition.SelectedCapabilityIds, StringComparer.Ordinal);
        var usedSet = new HashSet<string>(definition.UsedCapabilityIds, StringComparer.Ordinal);
        var allStepIds = definition.Steps.Select(static s => s.Id).ToHashSet(StringComparer.Ordinal);

        for (var i = 0; i < definition.Steps.Count; i++)
        {
            var step = definition.Steps[i];
            var fieldPrefix = $"steps[{i}]";

            if (string.IsNullOrWhiteSpace(step.Id))
            {
                errors.Add(new($"{fieldPrefix}.id", BusinessSkillErrorCodes.ValidationFailed, "Step id is required."));
                continue;
            }

            if (!seenSteps.Add(step.Id))
            {
                errors.Add(new($"{fieldPrefix}.id", BusinessSkillErrorCodes.ValidationFailed, $"Duplicate step id '{step.Id}'."));
                continue;
            }

            switch (step)
            {
                case SkillToolStep toolStep:
                    ValidateToolStep(toolStep, fieldPrefix, selectedSet, usedSet, seenSteps, errors);
                    break;
                case SkillTransformStep transformStep:
                    ValidateTransformStep(transformStep, fieldPrefix, seenSteps, errors);
                    break;
                case SkillBranchStep branchStep:
                    ValidateConditionExpr(branchStep.Condition, $"{fieldPrefix}.condition", seenSteps, errors);
                    ValidateBranchStep(branchStep, fieldPrefix, allStepIds, step.Id, errors);
                    break;
                case SkillForeachStep foreachStep:
                    ValidateForeachStep(foreachStep, fieldPrefix, seenSteps, allStepIds, step.Id, errors);
                    break;
                case SkillGenerateTextStep generateTextStep:
                    ValidateBindingReference(generateTextStep.Source, $"{fieldPrefix}.source", seenSteps, errors);
                    if (generateTextStep.AllowedInputFields.Count == 0)
                    {
                        errors.Add(new($"{fieldPrefix}.allowedInputFields", BusinessSkillErrorCodes.ValidationFailed,
                            "generateText step must explicitly declare allowedInputFields."));
                    }
                    else
                    {
                        var policyFields = new HashSet<string>(definition.DataPolicy.AllowedModelFields, StringComparer.Ordinal);
                        foreach (var field in generateTextStep.AllowedInputFields)
                        {
                            if (!policyFields.Contains(field))
                            {
                                errors.Add(new($"{fieldPrefix}.allowedInputFields", BusinessSkillErrorCodes.SelectionViolation,
                                    $"generateText field '{field}' is not included in dataPolicy.allowedModelFields."));
                            }
                        }
                    }
                    break;
                case SkillApprovalStep approvalStep:
                    if (!selectedSet.Contains(approvalStep.TargetCapabilityId))
                    {
                        errors.Add(new($"{fieldPrefix}.targetCapabilityId", BusinessSkillErrorCodes.SelectionViolation,
                            $"Approval target capability '{approvalStep.TargetCapabilityId}' is not selected."));
                    }
                    ValidateBindingReference(approvalStep.ItemsSource, $"{fieldPrefix}.itemsSource", seenSteps, errors);
                    break;
                case SkillOutputStep outputStep:
                    ValidateBindingReference(outputStep.Source, $"{fieldPrefix}.source", seenSteps, errors);
                    foreach (var named in outputStep.NamedOutputs)
                    {
                        ValidateBindingReference(named.Value, $"{fieldPrefix}.namedOutputs.{named.Key}", seenSteps, errors);
                    }
                    break;
            }
        }

        ValidateControlFlowAcyclic(definition.Steps, errors);

        foreach (var reqStep in definition.SuccessCriteria.RequiredSteps)
        {
            if (!seenSteps.Contains(reqStep))
            {
                errors.Add(new("successCriteria.requiredSteps", BusinessSkillErrorCodes.ValidationFailed,
                    $"Required step '{reqStep}' does not exist."));
            }
        }
    }

    private static void ValidateToolStep(
        SkillToolStep step,
        string fieldPrefix,
        HashSet<string> selectedSet,
        HashSet<string> usedSet,
        HashSet<string> seenSteps,
        List<BusinessSkillFieldError> errors)
    {
        if (string.IsNullOrWhiteSpace(step.CapabilityId))
        {
            errors.Add(new($"{fieldPrefix}.capabilityId", BusinessSkillErrorCodes.ValidationFailed, "Tool capabilityId is required."));
        }
        else
        {
            if (!selectedSet.Contains(step.CapabilityId))
            {
                errors.Add(new($"{fieldPrefix}.capabilityId", BusinessSkillErrorCodes.SelectionViolation,
                    $"Tool capability '{step.CapabilityId}' is not in selectedCapabilityIds."));
            }

            if (!usedSet.Contains(step.CapabilityId))
            {
                errors.Add(new($"{fieldPrefix}.capabilityId", BusinessSkillErrorCodes.ValidationFailed,
                    $"Tool capability '{step.CapabilityId}' must be listed in usedCapabilityIds."));
            }
        }

        foreach (var arg in step.Arguments)
        {
            ValidateBindingReference(arg.Value, $"{fieldPrefix}.arguments.{arg.Key}", seenSteps, errors, allowItemScope: true);
        }

        if (step.Pagination is not null && (step.Pagination.MaxPages <= 0 || step.Pagination.PageSize <= 0))
        {
            errors.Add(new($"{fieldPrefix}.pagination", BusinessSkillErrorCodes.ValidationFailed,
                "Pagination MaxPages and PageSize must be > 0."));
        }
    }

    private static void ValidateTransformStep(
        SkillTransformStep step,
        string fieldPrefix,
        HashSet<string> seenSteps,
        List<BusinessSkillFieldError> errors)
    {
        ValidateBindingReference(step.Source, $"{fieldPrefix}.source", seenSteps, errors);

        if (step.OutputLimit <= 0 || step.OutputLimit > BusinessSkillDefaults.MaxJoinOutputRows)
        {
            errors.Add(new($"{fieldPrefix}.outputLimit", BusinessSkillErrorCodes.BudgetExceeded,
                string.Format(CultureInfo.InvariantCulture, "outputLimit must be within 1..{0}.", BusinessSkillDefaults.MaxJoinOutputRows)));
        }

        if (step.Predicate is not null)
        {
            ValidateConditionExpr(step.Predicate, $"{fieldPrefix}.predicate", seenSteps, errors);
        }

        if (step.Operation == SkillTransformOperation.Aggregate)
        {
            foreach (var metric in step.Aggregations)
            {
                if (!AllowedAggregateFunctions.Contains(metric.Function))
                {
                    errors.Add(new($"{fieldPrefix}.aggregations.{metric.OutputField}", BusinessSkillErrorCodes.ValidationFailed,
                        $"Unsupported aggregation function '{metric.Function}'."));
                }
                else if (!string.Equals(metric.Function, "count", StringComparison.OrdinalIgnoreCase) &&
                         string.IsNullOrWhiteSpace(metric.SourceField))
                {
                    errors.Add(new($"{fieldPrefix}.aggregations.{metric.OutputField}", BusinessSkillErrorCodes.ValidationFailed,
                        $"Aggregation '{metric.Function}' requires SourceField."));
                }
            }
        }

        if (step.Operation is SkillTransformOperation.InnerJoin or SkillTransformOperation.LeftJoin or SkillTransformOperation.Lookup)
        {
            if (step.SecondarySource is null)
            {
                errors.Add(new($"{fieldPrefix}.secondarySource", BusinessSkillErrorCodes.ValidationFailed,
                    "Join/lookup transform requires secondarySource."));
            }
            else
            {
                ValidateBindingReference(step.SecondarySource, $"{fieldPrefix}.secondarySource", seenSteps, errors);
            }

            if (step.JoinSpec is null ||
                string.IsNullOrWhiteSpace(step.JoinSpec.LeftKey) ||
                string.IsNullOrWhiteSpace(step.JoinSpec.RightKey))
            {
                errors.Add(new($"{fieldPrefix}.joinSpec", BusinessSkillErrorCodes.ValidationFailed,
                    "Join/lookup transform requires explicit LeftKey and RightKey."));
            }
            else
            {
                if (!AllowedJoinKeyTypes.Contains(step.JoinSpec.KeyType))
                {
                    errors.Add(new($"{fieldPrefix}.joinSpec.keyType", BusinessSkillErrorCodes.ValidationFailed,
                        $"Unsupported join KeyType '{step.JoinSpec.KeyType}'."));
                }

                if (!AllowedTransformJoinMissingPolicies.Contains(step.JoinSpec.MissingMatchPolicy))
                {
                    errors.Add(new($"{fieldPrefix}.joinSpec.missingMatchPolicy", BusinessSkillErrorCodes.ValidationFailed,
                        $"Unsupported missingMatchPolicy '{step.JoinSpec.MissingMatchPolicy}'."));
                }
            }
        }
    }

    private static void ValidateBranchStep(
        SkillBranchStep step,
        string fieldPrefix,
        HashSet<string> allStepIds,
        string currentStepId,
        List<BusinessSkillFieldError> errors)
    {
        foreach (var targetId in step.ThenStepIds.Concat(step.ElseStepIds))
        {
            if (string.Equals(targetId, currentStepId, StringComparison.Ordinal))
            {
                errors.Add(new(fieldPrefix, BusinessSkillErrorCodes.ValidationFailed,
                    $"Branch step '{currentStepId}' cannot reference itself."));
            }
            else if (!allStepIds.Contains(targetId))
            {
                errors.Add(new(fieldPrefix, BusinessSkillErrorCodes.ValidationFailed,
                    $"Branch target step '{targetId}' does not exist."));
            }
        }
    }

    private static void ValidateForeachStep(
        SkillForeachStep step,
        string fieldPrefix,
        HashSet<string> seenSteps,
        HashSet<string> allStepIds,
        string currentStepId,
        List<BusinessSkillFieldError> errors)
    {
        ValidateBindingReference(step.Source, $"{fieldPrefix}.source", seenSteps, errors);

        if (step.MaxItems <= 0 || step.MaxItems > BusinessSkillDefaults.MaxWriteBatchItems)
        {
            errors.Add(new($"{fieldPrefix}.maxItems", BusinessSkillErrorCodes.BudgetExceeded,
                string.Format(CultureInfo.InvariantCulture, "foreach maxItems must be within 1..{0}.", BusinessSkillDefaults.MaxWriteBatchItems)));
        }

        foreach (var bodyId in step.BodyStepIds)
        {
            if (string.Equals(bodyId, currentStepId, StringComparison.Ordinal))
            {
                errors.Add(new(fieldPrefix, BusinessSkillErrorCodes.ValidationFailed,
                    $"Foreach step '{currentStepId}' cannot reference itself."));
            }
            else if (!allStepIds.Contains(bodyId))
            {
                errors.Add(new(fieldPrefix, BusinessSkillErrorCodes.ValidationFailed,
                    $"Foreach body step '{bodyId}' does not exist."));
            }
        }
    }

    private static void ValidateBindingReference(
        SkillValueBinding? binding,
        string fieldPrefix,
        HashSet<string> seenSteps,
        List<BusinessSkillFieldError> errors,
        bool allowItemScope = false)
    {
        if (binding is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(binding.Step) && !seenSteps.Contains(binding.Step))
        {
            errors.Add(new(fieldPrefix, BusinessSkillErrorCodes.ValidationFailed,
                $"Binding references step '{binding.Step}', which has not executed prior to this step."));
        }

        if (!allowItemScope && !string.IsNullOrWhiteSpace(binding.Item))
        {
            errors.Add(new(fieldPrefix, BusinessSkillErrorCodes.ValidationFailed,
                $"Item-scoped binding '{binding.Item}' is only permitted inside foreach tool arguments."));
        }
    }

    private static void ValidateConditionExpr(
        SkillConditionExpr expr,
        string fieldPrefix,
        HashSet<string> seenSteps,
        List<BusinessSkillFieldError> errors)
    {
        if (!string.Equals(expr.Combinator, "and", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(expr.Combinator, "or", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new($"{fieldPrefix}.combinator", BusinessSkillErrorCodes.ValidationFailed,
                $"Unsupported condition combinator '{expr.Combinator}'."));
        }

        for (var i = 0; i < expr.Clauses.Count; i++)
        {
            var clause = expr.Clauses[i];
            var clausePrefix = $"{fieldPrefix}.clauses[{i}]";
            if (string.IsNullOrWhiteSpace(clause.Field))
            {
                errors.Add(new($"{clausePrefix}.field", BusinessSkillErrorCodes.ValidationFailed, "Condition field is required."));
            }

            if (!AllowedConditionOperators.Contains(clause.Operator))
            {
                errors.Add(new($"{clausePrefix}.operator", BusinessSkillErrorCodes.ValidationFailed,
                    $"Unsupported condition operator '{clause.Operator}'."));
            }

            ValidateBindingReference(clause.Value, $"{clausePrefix}.value", seenSteps, errors);
        }
    }

    private static void ValidateControlFlowAcyclic(IReadOnlyList<SkillStepDefinition> steps, List<BusinessSkillFieldError> errors)
    {
        var indexById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < steps.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(steps[i].Id) && !indexById.ContainsKey(steps[i].Id))
            {
                indexById[steps[i].Id] = i;
            }
        }

        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            if (string.IsNullOrWhiteSpace(step.Id))
            {
                continue;
            }

            var targets = new List<string>();
            if (step is SkillBranchStep branch)
            {
                targets.AddRange(branch.ThenStepIds);
                targets.AddRange(branch.ElseStepIds);
            }
            else if (step is SkillForeachStep loop)
            {
                targets.AddRange(loop.BodyStepIds);
            }

            foreach (var target in targets)
            {
                if (indexById.TryGetValue(target, out var targetIdx) &&
                    indexById.TryGetValue(step.Id, out var srcIdx) &&
                    targetIdx < srcIdx)
                {
                    errors.Add(new($"steps.{step.Id}", BusinessSkillErrorCodes.ValidationFailed,
                        $"Control-flow target '{target}' precedes step '{step.Id}' and creates a backward edge."));
                }
            }

            edges[step.Id] = targets;
        }

        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 0=unvisited, 1=visiting, 2=visited
        foreach (var node in edges.Keys)
        {
            if (HasCycle(node, edges, state))
            {
                errors.Add(new("steps", BusinessSkillErrorCodes.ValidationFailed,
                    $"Cycle detected in Skill step graph involving '{node}'."));
                break;
            }
        }
    }

    private static bool HasCycle(
        string node,
        Dictionary<string, List<string>> edges,
        Dictionary<string, int> state)
    {
        if (state.TryGetValue(node, out var s))
        {
            return s == 1;
        }

        state[node] = 1;
        if (edges.TryGetValue(node, out var nextList))
        {
            foreach (var next in nextList)
            {
                if (HasCycle(next, edges, state))
                {
                    return true;
                }
            }
        }

        state[node] = 2;
        return false;
    }
}
