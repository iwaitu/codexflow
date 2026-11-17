// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CodexFlow.Contracts.BusinessSkills;
using Microsoft.Extensions.AI;

namespace CodexFlow.Core.BusinessSkills;

public sealed record SkillRunExecutionContext
{
    public required string RunId { get; init; }
    public required SkillDefinitionV1 Definition { get; init; }
    public required string PluginId { get; init; }
    public required string PluginName { get; init; }
    public required string SourceUrl { get; init; }
    public required string Environment { get; init; }
    public required string ConnectionId { get; init; }
    public required int ConnectionRevision { get; init; }
    public required string CredentialRevision { get; init; }
    public required string CatalogSnapshotHash { get; init; }
    public required EndpointPolicyDto EndpointPolicy { get; init; }
    public required BusinessAuthKind ExecutionAuthKind { get; init; }
    public string? ExecutionSecretJson { get; init; }
    public required IReadOnlyDictionary<string, CapabilityDetailDto> SnapshotCapabilities { get; init; }
    public required IReadOnlyDictionary<string, JsonElement> Inputs { get; init; }
    public bool IsTestRun { get; init; }
    public bool AllowWriteInTestRun { get; init; }
    public bool WriteStepUnverified { get; init; }
    public string? SessionId { get; init; }
}

public sealed record SkillApprovalGrantOutcome(
    bool Approved,
    bool Denied,
    bool ExpiredOrInvalidated,
    string? ErrorCode,
    string? ErrorMessage);

public interface ISkillRunRuntimeHost
{
    Task PersistStepCheckpointAsync(
        string stepId,
        string stepType,
        SkillRunStepState state,
        string? requestSummary,
        string? responseSummary,
        string? evidence,
        string? errorCode,
        CancellationToken ct);

    Task UpdateRunProgressAsync(int externalRequestsConsumed, int readOnlyRetriesConsumed, CancellationToken ct);

    Task<bool> IsStopOrInvalidationRequestedAsync(CancellationToken ct);

    Task<SkillApprovalGrantOutcome> RequestAndAwaitApprovalAsync(
        BusinessApprovalRequestDto approvalRequest,
        BusinessApprovalDigestInput digestInput,
        CancellationToken ct);
}

public sealed record SkillWorkflowRunOutcome(
    SkillRunStatus FinalStatus,
    JsonElement? OutputPayload,
    BusinessSkillErrorDto? Error,
    int ExternalRequestsConsumed,
    int ReadOnlyRetriesConsumed,
    int ActiveExecutionMilliseconds,
    bool AnyWriteSucceeded,
    bool AnyWriteOutcomeUnknown);

/// <summary>
/// 确定性 Skill JSON v1 步骤执行核：统一调度 tool/transform/branch/foreach/generateText/approval/output，
/// 严格实施能力交集、Prepared->Dispatching 检查点、只读隔离、批量写入批准核对及局部完成/OutcomeUnknown 状态机。
/// </summary>
public sealed class SkillWorkflowEngine
{
    private readonly IBusinessCapabilityExecutor _capabilityExecutor;
    private readonly IChatClient? _chatClient;

    public SkillWorkflowEngine(
        IBusinessCapabilityExecutor capabilityExecutor,
        IChatClient? chatClient = null)
    {
        ArgumentNullException.ThrowIfNull(capabilityExecutor);
        _capabilityExecutor = capabilityExecutor;
        _chatClient = chatClient;
    }

    public async Task<SkillWorkflowRunOutcome> ExecuteAsync(
        SkillRunExecutionContext ctx,
        ISkillRunRuntimeHost host,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(host);

        var def = ctx.Definition;
        var activeTimer = Stopwatch.StartNew();
        var wallClockTimer = Stopwatch.StartNew();
        var activeLimit = TimeSpan.FromMinutes(BusinessSkillDefaults.ActiveExecutionTimeoutMinutes);
        var wallClockLimit = TimeSpan.FromMinutes(BusinessSkillDefaults.WallClockLimitMinutes);

        var remainingRequestBudget = Math.Min(def.Budgets.MaxExternalRequests, BusinessSkillDefaults.MaxExternalRequestsPerRun);
        var remainingRecordBudget = Math.Min(def.Budgets.MaxReadRecords, BusinessSkillDefaults.MaxReadRecordsPerRun);
        var totalExternalRequests = 0;
        var totalReadOnlyRetries = 0;
        var anyPartialData = false;
        var anyWriteSucceeded = false;
        var anyWriteFailed = false;
        var anyWriteOutcomeUnknown = false;

        var stepOutputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var completedStepIds = new HashSet<string>(StringComparer.Ordinal);
        var skippedStepIds = new HashSet<string>(StringComparer.Ordinal);
        var approvedBatchesByCapability = new Dictionary<string, ApprovedWriteGrant>(StringComparer.Ordinal);

        // 属于 Foreach.BodyStepIds 的步骤仅在对应循环体内逐项触发，不在顶层顺序重复执行
        var loopOwnedStepIds = new HashSet<string>(
            def.Steps.OfType<SkillForeachStep>().SelectMany(static f => f.BodyStepIds),
            StringComparer.Ordinal);
        var stepsById = def.Steps.ToDictionary(static s => s.Id, StringComparer.Ordinal);

        async Task<CapabilityExecutionResult?> CheckBeforeDispatchAbortAsync()
        {
            if (wallClockTimer.Elapsed > wallClockLimit)
            {
                return FailedCapResult(BusinessSkillErrorCodes.BudgetExceeded, "Run exceeded 30-minute wall-clock limit.");
            }

            if (activeTimer.Elapsed > activeLimit)
            {
                return FailedCapResult(BusinessSkillErrorCodes.BudgetExceeded, "Run exceeded 10-minute active execution timeout.");
            }

            if (ct.IsCancellationRequested || await host.IsStopOrInvalidationRequestedAsync(CancellationToken.None).ConfigureAwait(false))
            {
                return FailedCapResult("cancelled", "Run stop or invalidation was requested before dispatch.");
            }

            return null;
        }

        async Task<StepExecOutcome> ExecuteSingleStepAsync(SkillStepDefinition step, JsonElement? loopItem, string stepInstanceId)
        {
            var abortCheck = await CheckBeforeDispatchAbortAsync().ConfigureAwait(false);
            if (abortCheck is not null)
            {
                return new StepExecOutcome(
                    abortCheck.ErrorCode == "cancelled" ? SkillRunStepState.Cancelled : SkillRunStepState.Failed,
                    abortCheck.ErrorCode,
                    abortCheck.ErrorMessage,
                    IsCancelled: abortCheck.ErrorCode == "cancelled",
                    IsOutcomeUnknown: false);
            }

            switch (step)
            {
                case SkillToolStep toolStep:
                    return await ExecuteToolStepAsync(toolStep, loopItem, stepInstanceId).ConfigureAwait(false);

                case SkillTransformStep transformStep:
                    await host.PersistStepCheckpointAsync(stepInstanceId, "transform", SkillRunStepState.Prepared,
                        $"op={transformStep.Operation}", null, null, null, ct).ConfigureAwait(false);

                    var tr = SkillTransformEngine.Execute(transformStep, def.Budgets, ctx.Inputs, stepOutputs);
                    if (!tr.Success)
                    {
                        await host.PersistStepCheckpointAsync(stepInstanceId, "transform", SkillRunStepState.Failed,
                            $"op={transformStep.Operation}", tr.ErrorMessage, null, tr.ErrorCode, ct).ConfigureAwait(false);
                        return new StepExecOutcome(SkillRunStepState.Failed, tr.ErrorCode, tr.ErrorMessage, false, false);
                    }

                    stepOutputs[step.Id] = tr.OutputPayload;
                    completedStepIds.Add(step.Id);
                    await host.PersistStepCheckpointAsync(stepInstanceId, "transform", SkillRunStepState.Succeeded,
                        $"op={transformStep.Operation}", $"rows={tr.OutputRowCount}", null, null, ct).ConfigureAwait(false);
                    return new StepExecOutcome(SkillRunStepState.Succeeded, null, null, false, false);

                case SkillBranchStep branchStep:
                    await host.PersistStepCheckpointAsync(stepInstanceId, "branch", SkillRunStepState.Prepared,
                        $"combinator={branchStep.Condition.Combinator}", null, null, null, ct).ConfigureAwait(false);

                    if (!SkillValueBinder.TryEvaluateCondition(branchStep.Condition, null, ctx.Inputs, stepOutputs, loopItem, out var condMatched, out var branchErr))
                    {
                        await host.PersistStepCheckpointAsync(stepInstanceId, "branch", SkillRunStepState.Failed,
                            null, branchErr, null, BusinessSkillErrorCodes.ValidationFailed, ct).ConfigureAwait(false);
                        return new StepExecOutcome(SkillRunStepState.Failed, BusinessSkillErrorCodes.ValidationFailed, branchErr, false, false);
                    }

                    foreach (var skipId in condMatched ? branchStep.ElseStepIds : branchStep.ThenStepIds)
                    {
                        skippedStepIds.Add(skipId);
                    }

                    stepOutputs[step.Id] = JsonSerializer.SerializeToElement(new { matched = condMatched });
                    completedStepIds.Add(step.Id);
                    await host.PersistStepCheckpointAsync(stepInstanceId, "branch", SkillRunStepState.Succeeded,
                        null, $"matched={condMatched}", null, null, ct).ConfigureAwait(false);
                    return new StepExecOutcome(SkillRunStepState.Succeeded, null, null, false, false);

                case SkillGenerateTextStep genStep:
                    return await ExecuteGenerateTextStepAsync(genStep, stepInstanceId).ConfigureAwait(false);

                case SkillApprovalStep approvalStep:
                    return await ExecuteApprovalStepAsync(approvalStep, stepInstanceId).ConfigureAwait(false);

                case SkillForeachStep foreachStep:
                    return await ExecuteForeachStepAsync(foreachStep, stepInstanceId).ConfigureAwait(false);

                case SkillOutputStep outputStep:
                    return await ExecuteOutputStepAsync(outputStep, stepInstanceId).ConfigureAwait(false);

                default:
                    return new StepExecOutcome(SkillRunStepState.Failed, BusinessSkillErrorCodes.UnsupportedSchema,
                        $"Unsupported step type '{step.GetType().Name}'.", false, false);
            }
        }

        async Task<StepExecOutcome> ExecuteToolStepAsync(SkillToolStep toolStep, JsonElement? loopItem, string stepInstanceId)
        {
            if (!ctx.SnapshotCapabilities.TryGetValue(toolStep.CapabilityId, out var cap))
            {
                return new StepExecOutcome(SkillRunStepState.Failed, BusinessSkillErrorCodes.CatalogChanged,
                    $"Capability '{toolStep.CapabilityId}' does not exist in bound snapshot.", false, false);
            }

            if (cap.Summary.Executability != CapabilityExecutabilityStatus.Executable)
            {
                return new StepExecOutcome(SkillRunStepState.Failed, BusinessSkillErrorCodes.UnsupportedSchema,
                    $"Capability '{toolStep.CapabilityId}' is marked {cap.Summary.Executability}.", false, false);
            }

            var isReadOnly = cap.Summary.Risk == CapabilityRiskLevel.Read;
            if (ctx.IsTestRun && !ctx.AllowWriteInTestRun && !isReadOnly)
            {
                return new StepExecOutcome(SkillRunStepState.Failed, BusinessSkillErrorCodes.SelectionViolation,
                    $"Read-only test run blocked non-read capability '{toolStep.CapabilityId}' (risk={cap.Summary.Risk}).", false, false);
            }

            if (!isReadOnly)
            {
                if (!approvedBatchesByCapability.TryGetValue(toolStep.CapabilityId, out var grant) || grant.IsExpired())
                {
                    return new StepExecOutcome(SkillRunStepState.Failed, BusinessSkillErrorCodes.ApprovalRequired,
                        $"Write capability '{toolStep.CapabilityId}' requires an active allow_once approval.", false, false);
                }

                if (!grant.TryConsumeNextSlot())
                {
                    return new StepExecOutcome(SkillRunStepState.Failed, BusinessSkillErrorCodes.ApprovalRequired,
                        $"Approved write batch count for '{toolStep.CapabilityId}' was exhausted; additional calls cannot proceed without new approval.", false, false);
                }
            }

            // 绑定结构化参数
            var resolvedArgs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var kv in toolStep.Arguments)
            {
                if (!SkillValueBinder.TryResolveBinding(kv.Value, ctx.Inputs, stepOutputs, loopItem, out var argVal, out var bindErr))
                {
                    return new StepExecOutcome(SkillRunStepState.Failed, BusinessSkillErrorCodes.ValidationFailed,
                        $"Failed binding argument '{kv.Key}' for step '{toolStep.Id}': {bindErr}", false, false);
                }

                resolvedArgs[kv.Key] = argVal;
            }

            // 若接口定义了 application/json body 且参数未作为 path/query/header 单独声明，将非路径查询参数聚合为 JSON Body
            JsonElement? bodyPayload = null;
            if (cap.Invocation.SourceType == BusinessPluginSourceType.OpenApi &&
                string.Equals(cap.Invocation.RequestContentType, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                var nonBodyNames = cap.Invocation.Parameters.Select(static p => p.Name).ToHashSet(StringComparer.Ordinal);
                var bodyDict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var kv in resolvedArgs)
                {
                    if (!nonBodyNames.Contains(kv.Key))
                    {
                        bodyDict[kv.Key] = kv.Value;
                    }
                }

                if (bodyDict.Count > 0)
                {
                    if (!string.IsNullOrWhiteSpace(toolStep.ResponseContract?.IdempotencyKeyTemplate))
                    {
                        var idem = SkillValueBinder.RenderBoundedTemplate(toolStep.ResponseContract.IdempotencyKeyTemplate, ctx.Inputs, stepOutputs, loopItem);
                        if (!string.IsNullOrWhiteSpace(idem) && !bodyDict.ContainsKey("idempotencyKey"))
                        {
                            bodyDict["idempotencyKey"] = JsonSerializer.SerializeToElement(idem);
                        }
                    }

                    bodyPayload = JsonSerializer.SerializeToElement(bodyDict);
                }
            }

            var reqSummary = RedactSummary($"{toolStep.CapabilityId} args=[{string.Join(',', resolvedArgs.Keys)}]");

            // 1) 发送前落库 Prepared 检查点；落库失败绝对禁止外发写入
            await host.PersistStepCheckpointAsync(stepInstanceId, "tool", SkillRunStepState.Prepared,
                reqSummary, null, null, null, ct).ConfigureAwait(false);

            // 2) 非只读操作需二次落库 Dispatching 检查点，使进程崩溃恢复可准确判定 OutcomeUnknown
            if (!isReadOnly)
            {
                await host.PersistStepCheckpointAsync(stepInstanceId, "tool", SkillRunStepState.Dispatching,
                    reqSummary, null, null, null, ct).ConfigureAwait(false);
            }

            var execRes = await _capabilityExecutor.ExecuteAsync(new CapabilityExecutionRequest
            {
                PluginId = ctx.PluginId,
                SnapshotId = def.CatalogSnapshotId,
                Capability = cap,
                EffectiveRisk = cap.Summary.Risk,
                EndpointPolicy = ctx.EndpointPolicy,
                SourceUrl = ctx.SourceUrl,
                AuthKind = ctx.ExecutionAuthKind,
                SecretPayloadJson = ctx.ExecutionSecretJson,
                SelectedServerUrl = string.IsNullOrWhiteSpace(def.ConnectionBinding.SelectedServerUrl) ? null : def.ConnectionBinding.SelectedServerUrl,
                Arguments = resolvedArgs,
                RequestBodyJson = bodyPayload,
                Pagination = toolStep.Pagination,
                ResponseContract = toolStep.ResponseContract,
                RemainingRequestBudget = remainingRequestBudget,
                RemainingRecordBudget = remainingRecordBudget,
                MaxReadOnlyRetries = Math.Min(def.Budgets.MaxReadOnlyRetries, BusinessSkillDefaults.MaxReadOnlyRetries),
                RequestTimeoutSeconds = Math.Clamp(def.Budgets.RequestTimeoutSeconds, 1, 300)
            }, ct).ConfigureAwait(false);

            totalExternalRequests += execRes.RequestsConsumed;
            totalReadOnlyRetries += execRes.RetriesConsumed;
            remainingRequestBudget = Math.Max(0, remainingRequestBudget - execRes.RequestsConsumed);
            if (execRes.CollectedItems.Count > 0)
            {
                remainingRecordBudget = Math.Max(0, remainingRecordBudget - execRes.CollectedItems.Count);
            }

            await host.UpdateRunProgressAsync(totalExternalRequests, totalReadOnlyRetries, ct).ConfigureAwait(false);

            if (execRes.PartialData)
            {
                anyPartialData = true;
            }

            if (!execRes.Success)
            {
                var finalStepState = execRes.OutcomeUnknown ? SkillRunStepState.OutcomeUnknown : SkillRunStepState.Failed;
                if (!isReadOnly)
                {
                    if (execRes.OutcomeUnknown)
                    {
                        anyWriteOutcomeUnknown = true;
                    }
                    else
                    {
                        anyWriteFailed = true;
                    }
                }

                await host.PersistStepCheckpointAsync(stepInstanceId, "tool", finalStepState,
                    reqSummary, RedactSummary(execRes.ErrorMessage ?? "Invocation failed."), null, execRes.ErrorCode, ct).ConfigureAwait(false);

                return new StepExecOutcome(finalStepState, execRes.ErrorCode, execRes.ErrorMessage, false, execRes.OutcomeUnknown);
            }

            if (!isReadOnly)
            {
                anyWriteSucceeded = true;
            }

            // 按步骤引用契约存储结果：{ items: [...] }（分页列表）或原始返回对象
            var storedOutput = toolStep.Pagination is not null
                ? JsonSerializer.SerializeToElement(new
                {
                    items = execRes.CollectedItems,
                    count = execRes.CollectedItems.Count,
                    partialData = execRes.PartialData
                })
                : (execRes.PayloadJson ?? JsonSerializer.SerializeToElement(new
                {
                    createdId = execRes.CreatedId,
                    text = execRes.RawText
                }));

            stepOutputs[toolStep.Id] = storedOutput;
            completedStepIds.Add(toolStep.Id);

            var evidence = execRes.CreatedId is not null
                ? $"createdId={execRes.CreatedId}; status={execRes.HttpStatusCode}"
                : $"status={execRes.HttpStatusCode}; items={(toolStep.Pagination is not null ? execRes.CollectedItems.Count : 1)}";

            await host.PersistStepCheckpointAsync(stepInstanceId, "tool", SkillRunStepState.Succeeded,
                reqSummary, "ok", evidence, null, ct).ConfigureAwait(false);

            return new StepExecOutcome(SkillRunStepState.Succeeded, null, null, false, false, storedOutput);
        }

        async Task<StepExecOutcome> ExecuteGenerateTextStepAsync(SkillGenerateTextStep genStep, string stepInstanceId)
        {
            await host.PersistStepCheckpointAsync(stepInstanceId, "generateText", SkillRunStepState.Prepared,
                $"allowedFields=[{string.Join(',', genStep.AllowedInputFields)}]", null, null, null, ct).ConfigureAwait(false);

            if (!SkillValueBinder.TryResolveBinding(genStep.Source, ctx.Inputs, stepOutputs, null, out var srcElement, out var bindErr))
            {
                await host.PersistStepCheckpointAsync(stepInstanceId, "generateText", SkillRunStepState.Failed,
                    null, bindErr, null, BusinessSkillErrorCodes.ValidationFailed, ct).ConfigureAwait(false);
                return new StepExecOutcome(SkillRunStepState.Failed, BusinessSkillErrorCodes.ValidationFailed, bindErr, false, false);
            }

            // 严格提取 AllowedInputFields ∩ DataPolicy.AllowedModelFields 白名单子集，防止响应整包进模型
            var allowedSet = genStep.AllowedInputFields
                .Where(f => def.DataPolicy.AllowedModelFields.Contains(f, StringComparer.Ordinal))
                .ToHashSet(StringComparer.Ordinal);

            var sanitizedPayload = ProjectAllowedFieldsForModel(srcElement, allowedSet);
            string generatedText;
            if (_chatClient is not null)
            {
                var msgs = new List<ChatMessage>
                {
                    new(ChatRole.System, "Summarize and draft clear Chinese explanation strictly using ONLY the provided sanitized JSON fields. Never invent numbers, dates, or tool commands."),
                    new(ChatRole.User, $"{genStep.PromptTemplate}\n\nSanitizedData:\n{JsonSerializer.Serialize(sanitizedPayload)}")
                };
                var resp = await _chatClient.GetResponseAsync(msgs, new ChatOptions
                {
                    MaxOutputTokens = Math.Min(2048, BusinessSkillDefaults.MaxModelOutputTokens),
                    Temperature = 0f
                }, ct).ConfigureAwait(false);
                generatedText = resp.Text ?? string.Empty;
            }
            else
            {
                generatedText = $"{genStep.PromptTemplate} (records={CountPayloadRows(sanitizedPayload)})";
            }

            var stepPayload = JsonSerializer.SerializeToElement(new
            {
                text = generatedText,
                disclosedFields = allowedSet.ToArray()
            });
            stepOutputs[genStep.Id] = stepPayload;
            completedStepIds.Add(genStep.Id);

            await host.PersistStepCheckpointAsync(stepInstanceId, "generateText", SkillRunStepState.Succeeded,
                $"disclosed=[{string.Join(',', allowedSet)}]", $"len={generatedText.Length}", null, null, ct).ConfigureAwait(false);

            return new StepExecOutcome(SkillRunStepState.Succeeded, null, null, false, false, stepPayload);
        }

        async Task<StepExecOutcome> ExecuteApprovalStepAsync(SkillApprovalStep approvalStep, string stepInstanceId)
        {
            if (!SkillValueBinder.TryResolveBinding(approvalStep.ItemsSource, ctx.Inputs, stepOutputs, null, out var itemsVal, out var itemsErr))
            {
                return new StepExecOutcome(SkillRunStepState.Failed, BusinessSkillErrorCodes.ValidationFailed, itemsErr, false, false);
            }

            var items = itemsVal.ValueKind == JsonValueKind.Array
                ? itemsVal.EnumerateArray().Select(static x => x.Clone()).ToArray()
                : [itemsVal.Clone()];

            var maxBatch = Math.Min(def.Budgets.MaxWriteBatchItems, BusinessSkillDefaults.MaxWriteBatchItems);
            if (items.Length > maxBatch)
            {
                return new StepExecOutcome(SkillRunStepState.Failed, BusinessSkillErrorCodes.BudgetExceeded,
                    $"Write batch item count ({items.Length}) exceeds configured maximum ({maxBatch}).", false, false);
            }

            // 批次为空时无需挂起等待人工写入批准（直接通过 0 条批次）
            if (items.Length == 0)
            {
                approvedBatchesByCapability[approvalStep.TargetCapabilityId] = new ApprovedWriteGrant(0, DateTimeOffset.UtcNow.AddMinutes(BusinessSkillDefaults.ApprovalValidityMinutes));
                completedStepIds.Add(approvalStep.Id);
                await host.PersistStepCheckpointAsync(stepInstanceId, "approval", SkillRunStepState.Succeeded,
                    $"target={approvalStep.TargetCapabilityId}; batch=0", "auto-skipped-empty-batch", null, null, ct).ConfigureAwait(false);
                return new StepExecOutcome(SkillRunStepState.Succeeded, null, null, false, false);
            }

            ctx.SnapshotCapabilities.TryGetValue(approvalStep.TargetCapabilityId, out var targetCap);
            var batchItemIds = new List<string>(items.Length);
            decimal totalKeyAmount = 0m;
            string? currency = null;
            var previews = new List<BusinessApprovalRequestPreviewDto>();

            for (var idx = 0; idx < items.Length; idx++)
            {
                var item = items[idx];
                var idVal = ExtractBatchItemId(item, idx);
                batchItemIds.Add(idVal);

                if (!string.IsNullOrWhiteSpace(approvalStep.KeyAmountField) &&
                    item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty(approvalStep.KeyAmountField, out var amtEl) &&
                    SkillValueBinder.TryTryDecimal(amtEl, out var amtDec))
                {
                    totalKeyAmount += amtDec;
                }

                if (currency is null &&
                    !string.IsNullOrWhiteSpace(approvalStep.CurrencyField) &&
                    item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty(approvalStep.CurrencyField, out var curEl) &&
                    curEl.ValueKind == JsonValueKind.String)
                {
                    currency = curEl.GetString();
                }

                if (previews.Count < 10)
                {
                    var previewObj = BuildTargetFieldPreview(item, approvalStep.TargetRecordFields);
                    previews.Add(new BusinessApprovalRequestPreviewDto(
                        approvalStep.TargetCapabilityId,
                        targetCap?.Invocation.HttpMethod ?? targetCap?.Invocation.McpToolName ?? "WRITE",
                        targetCap?.Invocation.NormalizedPath ?? approvalStep.TargetCapabilityId,
                        JsonSerializer.Serialize(previewObj)));
                }
            }

            var now = DateTimeOffset.UtcNow;
            var expiresAt = now.AddMinutes(BusinessSkillDefaults.ApprovalValidityMinutes);
            var normArgsJson = JsonSerializer.Serialize(new
            {
                targetCapabilityId = approvalStep.TargetCapabilityId,
                inputs = ctx.Inputs,
                batchCount = items.Length
            });

            var digestInput = new BusinessApprovalDigestInput(
                ctx.RunId,
                def.SkillId,
                def.Version,
                def.ContentHash,
                ctx.ConnectionId,
                ctx.ConnectionRevision,
                ctx.CredentialRevision,
                approvalStep.TargetCapabilityId,
                ctx.CatalogSnapshotHash,
                normArgsJson,
                batchItemIds,
                expiresAt);

            var approvalReq = new BusinessApprovalRequestDto
            {
                ApprovalId = $"bapr-{Guid.NewGuid():N}",
                RequestDigest = digestInput.ComputeRequestDigest(),
                SessionId = ctx.SessionId ?? string.Empty,
                RunId = ctx.RunId,
                StepId = approvalStep.Id,
                Kind = BusinessApprovalConstants.BusinessSkillWriteKind,
                AllowedDecisions = BusinessApprovalConstants.AllowedDecisions,
                Summary = new BusinessApprovalSummaryDto
                {
                    SystemName = ctx.PluginName,
                    Environment = ctx.Environment,
                    TargetRecordSummary = $"{items.Length} 条待写入记录 ({string.Join(", ", batchItemIds.Take(5))}{(batchItemIds.Count > 5 ? "..." : string.Empty)})",
                    PlannedMutation = approvalStep.PlannedMutation,
                    BatchCount = items.Length,
                    KeyAmount = string.IsNullOrWhiteSpace(approvalStep.KeyAmountField) ? null : totalKeyAmount,
                    Currency = currency,
                    WriteStepUnverified = ctx.WriteStepUnverified,
                    RequestPreviews = previews
                },
                CreatedAtUtc = now,
                ExpiresAtUtc = expiresAt
            };

            await host.PersistStepCheckpointAsync(stepInstanceId, "approval", SkillRunStepState.Prepared,
                $"approvalId={approvalReq.ApprovalId}; batch={items.Length}", null, null, null, ct).ConfigureAwait(false);

            // 等待人类决定期间暂停主动执行计时（仍受 30 分钟墙钟与 10 分钟批准有效期约束）
            activeTimer.Stop();
            SkillApprovalGrantOutcome decision;
            try
            {
                decision = await host.RequestAndAwaitApprovalAsync(approvalReq, digestInput, ct).ConfigureAwait(false);
            }
            finally
            {
                activeTimer.Start();
            }

            if (!decision.Approved)
            {
                var errCode = decision.Denied ? BusinessSkillErrorCodes.ApprovalRequired : (decision.ErrorCode ?? BusinessSkillErrorCodes.ApprovalExpired);
                await host.PersistStepCheckpointAsync(stepInstanceId, "approval",
                    decision.Denied ? SkillRunStepState.Cancelled : SkillRunStepState.Failed,
                    $"approvalId={approvalReq.ApprovalId}", decision.ErrorMessage, null, errCode, CancellationToken.None).ConfigureAwait(false);

                return new StepExecOutcome(
                    decision.Denied ? SkillRunStepState.Cancelled : SkillRunStepState.Failed,
                    errCode,
                    decision.ErrorMessage ?? "Write approval was denied or invalidated.",
                    IsCancelled: decision.Denied,
                    IsOutcomeUnknown: false);
            }

            approvedBatchesByCapability[approvalStep.TargetCapabilityId] = new ApprovedWriteGrant(items.Length, expiresAt);
            completedStepIds.Add(approvalStep.Id);
            await host.PersistStepCheckpointAsync(stepInstanceId, "approval", SkillRunStepState.Succeeded,
                $"approvalId={approvalReq.ApprovalId}", "allow_once", $"digest={approvalReq.RequestDigest}", null, ct).ConfigureAwait(false);

            return new StepExecOutcome(SkillRunStepState.Succeeded, null, null, false, false);
        }

        async Task<StepExecOutcome> ExecuteForeachStepAsync(SkillForeachStep foreachStep, string stepInstanceId)
        {
            if (!SkillValueBinder.TryResolveBinding(foreachStep.Source, ctx.Inputs, stepOutputs, null, out var srcVal, out var err))
            {
                return new StepExecOutcome(SkillRunStepState.Failed, BusinessSkillErrorCodes.ValidationFailed, err, false, false);
            }

            if (srcVal.ValueKind != JsonValueKind.Array)
            {
                return new StepExecOutcome(SkillRunStepState.Failed, BusinessSkillErrorCodes.UnsupportedSchema,
                    $"Foreach '{foreachStep.Id}' source must be a JSON array.", false, false);
            }

            var items = srcVal.EnumerateArray().Select(static x => x.Clone()).ToArray();
            var limit = Math.Min(foreachStep.MaxItems, Math.Min(def.Budgets.MaxWriteBatchItems, BusinessSkillDefaults.MaxWriteBatchItems));
            if (items.Length > limit)
            {
                return new StepExecOutcome(SkillRunStepState.Failed, BusinessSkillErrorCodes.BudgetExceeded,
                    $"Foreach '{foreachStep.Id}' item count ({items.Length}) exceeds limit ({limit}).", false, false);
            }

            await host.PersistStepCheckpointAsync(stepInstanceId, "foreach", SkillRunStepState.Prepared,
                $"items={items.Length}; sequential={foreachStep.Sequential}", null, null, null, ct).ConfigureAwait(false);

            var iterationResults = new List<JsonElement>(items.Length);
            for (var idx = 0; idx < items.Length; idx++)
            {
                var abort = await CheckBeforeDispatchAbortAsync().ConfigureAwait(false);
                if (abort is not null)
                {
                    var cancelled = abort.ErrorCode == "cancelled";
                    await host.PersistStepCheckpointAsync(stepInstanceId, "foreach",
                        cancelled ? SkillRunStepState.Cancelled : SkillRunStepState.Failed,
                        $"completed={idx}/{items.Length}", abort.ErrorMessage, null, abort.ErrorCode, CancellationToken.None).ConfigureAwait(false);

                    stepOutputs[foreachStep.Id] = JsonSerializer.SerializeToElement(new { results = iterationResults, partial = true });
                    return new StepExecOutcome(
                        cancelled ? SkillRunStepState.Cancelled : SkillRunStepState.Failed,
                        abort.ErrorCode,
                        abort.ErrorMessage,
                        IsCancelled: cancelled,
                        IsOutcomeUnknown: false);
                }

                JsonElement? lastBodyPayload = null;
                foreach (var bodyId in foreachStep.BodyStepIds)
                {
                    if (!stepsById.TryGetValue(bodyId, out var bodyStep))
                    {
                        return new StepExecOutcome(SkillRunStepState.Failed, BusinessSkillErrorCodes.ValidationFailed,
                            $"Foreach body step '{bodyId}' does not exist.", false, false);
                    }

                    var bodyInstanceId = $"{bodyId}#{idx}";
                    var bodyOutcome = await ExecuteSingleStepAsync(bodyStep, items[idx], bodyInstanceId).ConfigureAwait(false);
                    if (bodyOutcome.State != SkillRunStepState.Succeeded)
                    {
                        stepOutputs[foreachStep.Id] = JsonSerializer.SerializeToElement(new { results = iterationResults, partial = true });
                        await host.PersistStepCheckpointAsync(stepInstanceId, "foreach", bodyOutcome.State,
                            $"failedAt={idx}/{items.Length}", bodyOutcome.ErrorMessage, null, bodyOutcome.ErrorCode, CancellationToken.None).ConfigureAwait(false);
                        return bodyOutcome;
                    }

                    lastBodyPayload = bodyOutcome.ProducedPayload;
                }

                if (lastBodyPayload.HasValue)
                {
                    iterationResults.Add(lastBodyPayload.Value);
                }
            }

            var loopPayload = JsonSerializer.SerializeToElement(new { results = iterationResults, count = iterationResults.Count });
            stepOutputs[foreachStep.Id] = loopPayload;
            completedStepIds.Add(foreachStep.Id);
            await host.PersistStepCheckpointAsync(stepInstanceId, "foreach", SkillRunStepState.Succeeded,
                $"items={items.Length}", $"completed={iterationResults.Count}", null, null, ct).ConfigureAwait(false);

            return new StepExecOutcome(SkillRunStepState.Succeeded, null, null, false, false, loopPayload);
        }

        async Task<StepExecOutcome> ExecuteOutputStepAsync(SkillOutputStep outputStep, string stepInstanceId)
        {
            var outputDict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (outputStep.NamedOutputs.Count > 0)
            {
                foreach (var kv in outputStep.NamedOutputs)
                {
                    if (!SkillValueBinder.TryResolveBinding(kv.Value, ctx.Inputs, stepOutputs, null, out var val, out var err))
                    {
                        return new StepExecOutcome(SkillRunStepState.Failed, BusinessSkillErrorCodes.ValidationFailed,
                            $"Output '{kv.Key}' failed: {err}", false, false);
                    }

                    outputDict[kv.Key] = val;
                }
            }
            else if (SkillValueBinder.TryResolveBinding(outputStep.Source, ctx.Inputs, stepOutputs, null, out var singleOut, out _))
            {
                outputDict["result"] = singleOut;
            }

            var outPayload = JsonSerializer.SerializeToElement(outputDict);
            stepOutputs[outputStep.Id] = outPayload;
            completedStepIds.Add(outputStep.Id);
            await host.PersistStepCheckpointAsync(stepInstanceId, "output", SkillRunStepState.Succeeded,
                null, $"keys=[{string.Join(',', outputDict.Keys)}]", null, null, ct).ConfigureAwait(false);

            return new StepExecOutcome(SkillRunStepState.Succeeded, null, null, false, false, outPayload);
        }

        // --- 主拓扑流水线执行 ---
        BusinessSkillErrorDto? lastError = null;
        var haltedByCancellation = false;

        foreach (var step in def.Steps)
        {
            if (loopOwnedStepIds.Contains(step.Id) || skippedStepIds.Contains(step.Id))
            {
                continue;
            }

            var outcome = await ExecuteSingleStepAsync(step, loopItem: null, stepInstanceId: step.Id).ConfigureAwait(false);
            if (outcome.State != SkillRunStepState.Succeeded)
            {
                lastError = new BusinessSkillErrorDto(
                    outcome.ErrorCode ?? BusinessSkillErrorCodes.ConnectionUnavailable,
                    outcome.ErrorMessage ?? "Step execution did not succeed.",
                    [],
                    false,
                    ctx.RunId);
                haltedByCancellation = outcome.IsCancelled;
                break;
            }
        }

        // 依据第 8.3 节优先级判定最终状态：OutcomeUnknown > PartialSucceeded > Cancelled > Failed > Succeeded
        JsonElement? finalOutput = null;
        var lastOutputStep = def.Steps.OfType<SkillOutputStep>().LastOrDefault(s => stepOutputs.ContainsKey(s.Id));
        if (lastOutputStep is not null && stepOutputs.TryGetValue(lastOutputStep.Id, out var resolvedOut))
        {
            finalOutput = resolvedOut;
        }
        else if (stepOutputs.Count > 0)
        {
            finalOutput = JsonSerializer.SerializeToElement(stepOutputs);
        }

        if (anyWriteOutcomeUnknown)
        {
            return new SkillWorkflowRunOutcome(
                SkillRunStatus.OutcomeUnknown,
                finalOutput,
                lastError ?? new BusinessSkillErrorDto(BusinessSkillErrorCodes.OutcomeUnknown, "One or more dispatched writes may have taken effect on target system; manual reconciliation required.", [], false, ctx.RunId),
                totalExternalRequests,
                totalReadOnlyRetries,
                (int)activeTimer.ElapsedMilliseconds,
                anyWriteSucceeded,
                true);
        }

        if (anyWriteSucceeded && (haltedByCancellation || anyWriteFailed || lastError is not null))
        {
            return new SkillWorkflowRunOutcome(
                SkillRunStatus.PartialSucceeded,
                finalOutput,
                lastError ?? new BusinessSkillErrorDto(BusinessSkillErrorCodes.PartialData, "Batch write partially succeeded before stopping.", [], false, ctx.RunId),
                totalExternalRequests,
                totalReadOnlyRetries,
                (int)activeTimer.ElapsedMilliseconds,
                true,
                false);
        }

        if (haltedByCancellation)
        {
            return new SkillWorkflowRunOutcome(
                SkillRunStatus.Cancelled,
                finalOutput,
                lastError,
                totalExternalRequests,
                totalReadOnlyRetries,
                (int)activeTimer.ElapsedMilliseconds,
                false,
                false);
        }

        if (lastError is not null)
        {
            return new SkillWorkflowRunOutcome(
                SkillRunStatus.Failed,
                finalOutput,
                lastError,
                totalExternalRequests,
                totalReadOnlyRetries,
                (int)activeTimer.ElapsedMilliseconds,
                anyWriteSucceeded,
                false);
        }

        // 成功完成必须满足 SuccessCriteria（必需步骤全成功 + 禁止 PartialData 伪装全量成功）
        foreach (var reqStep in def.SuccessCriteria.RequiredSteps)
        {
            if (!completedStepIds.Contains(reqStep) && !skippedStepIds.Contains(reqStep))
            {
                return new SkillWorkflowRunOutcome(
                    SkillRunStatus.Failed,
                    finalOutput,
                    new BusinessSkillErrorDto(BusinessSkillErrorCodes.ValidationFailed, $"Required step '{reqStep}' did not complete.", [], false, ctx.RunId),
                    totalExternalRequests,
                    totalReadOnlyRetries,
                    (int)activeTimer.ElapsedMilliseconds,
                    anyWriteSucceeded,
                    false);
            }
        }

        if (anyPartialData && (def.SuccessCriteria.FailOnPartialData || def.SuccessCriteria.RequireCompletePagination))
        {
            return new SkillWorkflowRunOutcome(
                SkillRunStatus.Failed,
                finalOutput,
                new BusinessSkillErrorDto(BusinessSkillErrorCodes.PartialData, "Pagination/read data was incomplete; strict SuccessCriteria disallows reporting Succeeded on partial data.", [], false, ctx.RunId),
                totalExternalRequests,
                totalReadOnlyRetries,
                (int)activeTimer.ElapsedMilliseconds,
                anyWriteSucceeded,
                false);
        }

        return new SkillWorkflowRunOutcome(
            SkillRunStatus.Succeeded,
            finalOutput,
            null,
            totalExternalRequests,
            totalReadOnlyRetries,
            (int)activeTimer.ElapsedMilliseconds,
            anyWriteSucceeded,
            false);
    }

    private static object ProjectAllowedFieldsForModel(JsonElement source, HashSet<string> allowedFields)
    {
        if (allowedFields.Count == 0)
        {
            return new { omitted = true };
        }

        if (source.ValueKind == JsonValueKind.Array)
        {
            var rows = new List<Dictionary<string, JsonElement>>();
            foreach (var item in source.EnumerateArray().Take(50))
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    var d = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    foreach (var p in item.EnumerateObject())
                    {
                        if (allowedFields.Contains(p.Name))
                        {
                            d[p.Name] = p.Value.Clone();
                        }
                    }

                    rows.Add(d);
                }
            }

            return rows;
        }

        if (source.ValueKind == JsonValueKind.Object)
        {
            var d = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var p in source.EnumerateObject())
            {
                if (allowedFields.Contains(p.Name))
                {
                    d[p.Name] = p.Value.Clone();
                }
            }

            return d;
        }

        return new { omitted = true };
    }

    private static int CountPayloadRows(object payload)
        => payload is ICollection<Dictionary<string, JsonElement>> list ? list.Count : 1;

    private static string ExtractBatchItemId(JsonElement item, int fallbackIdx)
    {
        if (item.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "invoiceId", "id", "recordId", "customerId", "taskId", "code" })
            {
                if (item.TryGetProperty(key, out var val) && val.ValueKind != JsonValueKind.Null)
                {
                    return BusinessCapabilityResponseHelpers.JsonElementToScalarString(val);
                }
            }
        }

        return string.Format(CultureInfo.InvariantCulture, "row-{0}", fallbackIdx + 1);
    }

    private static Dictionary<string, JsonElement> BuildTargetFieldPreview(JsonElement item, IReadOnlyList<string> targetFields)
    {
        var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (item.ValueKind != JsonValueKind.Object)
        {
            return dict;
        }

        if (targetFields.Count == 0)
        {
            foreach (var p in item.EnumerateObject().Take(6))
            {
                dict[p.Name] = p.Value.Clone();
            }

            return dict;
        }

        foreach (var f in targetFields)
        {
            if (item.TryGetProperty(f, out var val))
            {
                dict[f] = val.Clone();
            }
        }

        return dict;
    }

    private static string RedactSummary(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return text.Length > 512 ? text[..512] + "..." : text;
    }

    private static CapabilityExecutionResult FailedCapResult(string code, string message)
        => new()
        {
            Success = false,
            ErrorCode = code,
            ErrorMessage = message
        };

    private sealed class ApprovedWriteGrant(int totalCount, DateTimeOffset expiresAtUtc)
    {
        private int _remaining = totalCount;

        public bool IsExpired() => DateTimeOffset.UtcNow >= expiresAtUtc;

        public bool TryConsumeNextSlot()
        {
            if (_remaining <= 0)
            {
                return false;
            }

            _remaining--;
            return true;
        }
    }

    private sealed record StepExecOutcome(
        SkillRunStepState State,
        string? ErrorCode,
        string? ErrorMessage,
        bool IsCancelled,
        bool IsOutcomeUnknown,
        JsonElement? ProducedPayload = null);
}
