// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using CodexFlow.Core.Telemetry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QreProtocol = CodexFlow.QueryRuntime.Protocol;
using QreV2 = CodexFlow.QueryRuntime.Engine.V2;

namespace CodexFlow.Core.Runtime;

/// <summary>
/// Production adapter for the v2 QueryRuntime hosting facade. CodexFlow remains
/// a host translator while Engine owns the frozen registry, policy,
/// approval binding, scheduling, canonical history, prepared context and audit.
/// </summary>
public sealed class QreV2BackedQueryRuntimeEngine : IQueryRuntimeEngine
{
    private readonly ILLMExecutor _llmExecutor;
    private readonly IContextWindowManager? _contextWindowManager;
    private readonly IQueryLoopTelemetry? _telemetry;
    private readonly ILogger<QreV2BackedQueryRuntimeEngine> _logger;
    private readonly IRuntimeHookDispatcher? _runtimeHookDispatcher;
    private readonly TimeSpan _modelStreamTimeout;
    private readonly ContextCompressionTriggerOptions _contextOptions;
    private readonly QreV2.IRuntimeAuditSink? _auditSink;
    private readonly string? _auditDirectory;
    private readonly CodexFlow.Core.Execution.Approval.IExecutionApprovalBroker? _approvalBroker;

    public QreV2BackedQueryRuntimeEngine(
        ILLMExecutor llmExecutor,
        IContextWindowManager? contextWindowManager,
        IQueryLoopTelemetry? telemetry,
        ILogger<QreV2BackedQueryRuntimeEngine> logger,
        IRuntimeHookDispatcher? runtimeHookDispatcher = null,
        IOptions<QueryRuntimeBackendOptions>? backendOptions = null,
        ContextCompressionTriggerOptions? contextOptions = null,
        QreV2.IRuntimeAuditSink? auditSink = null,
        CodexFlow.Core.Execution.Approval.IExecutionApprovalBroker? approvalBroker = null)
    {
        _llmExecutor = llmExecutor ?? throw new ArgumentNullException(nameof(llmExecutor));
        _contextWindowManager = contextWindowManager;
        _telemetry = telemetry;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _runtimeHookDispatcher = runtimeHookDispatcher;
        _modelStreamTimeout = TimeSpan.FromMilliseconds(
            Math.Max(0, backendOptions?.Value.ModelStreamTimeoutMs ?? new QueryRuntimeBackendOptions().ModelStreamTimeoutMs));
        _contextOptions = contextOptions ?? new ContextCompressionTriggerOptions();
        _auditSink = auditSink;
        _auditDirectory = string.IsNullOrWhiteSpace(backendOptions?.Value.AuditDirectory) ? null : backendOptions!.Value.AuditDirectory;
        _approvalBroker = approvalBroker;
    }

    public async Task<QueryRuntimeResult> ExecuteAsync(
        QueryRuntimeRequest request,
        IQueryRuntimeEventSink eventSink,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(eventSink);

        var queryId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _telemetry?.RecordStart(new QueryLoopStarted(
            queryId,
            request.SessionId,
            request.EntryPoint,
            startedAt,
            Math.Max(1, request.MaxRounds),
            EstimateContextChars(request.InitialMessages)));

        using var turnTimeoutScope = CodexFlow.Core.Execution.Approval.AmbientTurnTimeoutController.Current is null
            ? CodexFlow.Core.Execution.Approval.AmbientTurnTimeoutController.BeginScope(out _)
            : null;

        try
        {
            ValidateV2Request(request);
            var tools = WrapTools(request, ResolveTools(request));
            // Evidence from what the tools really returned this turn (the stop hook used to be given an empty ledger).
            var evidence = new TurnEvidence(request);
            var catalog = request.FrozenToolCatalog == null || !request.EnableTools
                ? null : new FrozenRuntimeToolCatalog(request, tools);
            if (catalog != null) tools = catalog.ExecutionTools;
            var requiredToolName = ResolveRequiredToolName(request, tools);
            // Enforce the contract at termination from real successful invocations, independent of the hook
            // dispatcher, but only when at least one of its tools is actually available this turn.
            var enforcedContract = ResolveContract(request) is { } contractCandidate &&
                contractCandidate.ResolveRequiredToolName(tools) != null
                    ? contractCandidate
                    : null;
            var sessionId = new QreProtocol.RuntimeSessionId(request.SessionId);
            var turnId = new QreProtocol.RuntimeTurnId($"codexflow-{queryId:N}");
            var toolPipeline = new QreV2.RuntimeToolExecutionPipeline(
                new QreV2.RuntimeToolRegistry(tools.Select(tool =>
                    (QreV2.IRuntimeTool)new CodexFlowV2RuntimeTool(
                        tool,
                        request.InterventionHook,
                        request.Session,
                        result =>
                        {
                            LogToolOutcome(_logger, request, result);
                            evidence.Record(result);
                        },
                        eventSink,
                        request,
                        queryId)), allowMixedCaseNames: true),
                new CodexFlowV2ToolPolicy(request, _logger));
            var modelClient = new CodexFlowRuntimeModelClient(
                _llmExecutor,
                request,
                tools,
                _modelStreamTimeout,
                _logger,
                evidence);
            var runtime = new QreV2.AgentRuntime(modelClient);
            await using var recording = OpenRecording(request);
            QreV2.IRuntimeAuditSink auditSink = _auditSink ?? (QreV2.IRuntimeAuditSink?)recording ?? new QreV2.InMemoryRuntimeAuditSink();
            using var handle = new QreV2.RuntimeTurnHandle();
            var bridge = new CodexFlowV2EventSink(
                eventSink,
                request,
                queryId);
            var loopRequest = new QreV2.RuntimeAgentLoopRequest(
                sessionId,
                turnId,
                ResolveObjective(request),
                ToProtocolMessages(request.InitialMessages),
                toolPipeline.Descriptors,
                new QreProtocol.RuntimeModelParameters(
                    Model: request.Options?.ModelId,
                    Temperature: request.Options?.Temperature,
                    MaxOutputTokens: request.Options?.MaxOutputTokens,
                    RequireJsonObject: request.Options?.ResponseFormat == ChatResponseFormat.Json,
                    RequiredToolName: requiredToolName),
                new QreV2.RuntimePolicySnapshot("codexflow-v2", "codexflow"),
                new QreV2.RuntimeEnvironmentSnapshot(
                    "codexflow-in-process",
                    request.PromptMetadata?.WorkspacePath,
                    "codexflow-v2-audited-context-pipeline"),
                new QreV2.RuntimeBudgetSnapshot(
                    Math.Max(1, request.MaxRounds),
                    maxToolCalls: tools.Length == 0
                        ? 0
                        : Math.Max(tools.Length, request.MaxRounds * tools.Length),
                    maxModelRetries: Math.Max(0, request.AdapterHints?.MaxRecoveryAttempts ?? 1),
                    maxContinuations: ResolveMaxContinuations(request)))
            {
                ToolPipeline = toolPipeline,
                ToolCatalogSelector = catalog,
                ContextManager = new QreV2.RuntimeContextManager(CreateContextOptions(_contextOptions)),
                AuditSink = auditSink,
                AuditFailureMode = QreV2.RuntimeAuditFailureMode.FailClosed,
                ToolApproval = request.InterventionHook == null
                    ? null
                    : new CodexFlowV2ToolApproval(),
                TerminationPolicy = _runtimeHookDispatcher == null && enforcedContract == null
                    ? null
                    : new CodexFlowV2TerminationPolicy(request, _runtimeHookDispatcher, enforcedContract, evidence)
            };

            if (_contextWindowManager != null)
            {
                await _contextWindowManager.OnTurnStartedAsync(request, ct).ConfigureAwait(false);
            }

            var runtimeResult = await runtime.RunAsync(
                new QreV2.RuntimeRunRequest(loopRequest) { Handle = handle },
                bridge,
                ct).ConfigureAwait(false);
            stopwatch.Stop();
            var result = BuildResult(request, runtimeResult, queryId, stopwatch.ElapsedMilliseconds, modelClient) with
            {
                WorkerReport = evidence.WorkerReport,
                VerifiedWithoutChange = VerifiedNoChangeCompletion.IsStated(evidence.SnapshotForStopHook())
            };
            _logger.LogInformation(
                "QRE v2 audit completed for session {SessionId}: schema {SchemaVersion}, events {EventCount}, warnings {WarningCount}",
                request.SessionId,
                QreV2.RuntimeAuditSchema.CurrentVersion,
                runtimeResult.LoopResult.AuditEvents.Count,
                runtimeResult.LoopResult.AuditWarnings.Count);

            if (_contextWindowManager != null)
            {
                await _contextWindowManager.OnTurnCompletedAsync(request, result, ct).ConfigureAwait(false);
            }

            _telemetry?.RecordTermination(new QueryLoopTerminated(
                queryId,
                request.SessionId,
                request.EntryPoint,
                result.TerminationReason,
                result.TerminalDetailCode,
                result.TotalRounds,
                result.TotalToolCalls,
                result.ZeroToolCallRounds,
                result.MalformedProtocolCount,
                result.EmptyResponseCount,
                result.RecoveryCount,
                result.TotalDurationMs,
                result.TotalPromptTokens,
                result.TotalCompletionTokens,
                result.TotalCachedPromptTokens));
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "QRE v2 adapter failed for session {SessionId}", request.SessionId);
            if (eventSink.IsEnabled(QueryRuntimeEventType.Error))
            {
                await eventSink.OnEventAsync(new ErrorEvent(
                    999999,
                    queryId,
                    request.SessionId,
                    request.EntryPoint,
                    ex.GetType().Name,
                    ex.Message,
                    ex)).ConfigureAwait(false);
            }

            return BuildFailureResult(request, queryId, stopwatch.ElapsedMilliseconds, ex);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            if (CodexFlow.Core.Execution.Approval.AmbientBackgroundJobApprovalContext.Current?.SuspendedApproval is not null)
            {
                throw;
            }

            if (request.ApprovalOwnerKind != CodexFlow.Core.Execution.Approval.ApprovalOwnerKind.Unspecified &&
                !string.IsNullOrWhiteSpace(request.ApprovalOwnerId))
            {
                _approvalBroker?.CancelOwnerApprovals(
                    request.SessionId,
                    request.ApprovalOwnerKind,
                    request.ApprovalOwnerId,
                    "Turn cancelled.");
            }
            else
            {
                _approvalBroker?.CancelSessionApprovals(request.SessionId, "Turn cancelled.");
            }
            return BuildFailureResult(
                request,
                queryId,
                stopwatch.ElapsedMilliseconds,
                new OperationCanceledException("QRE v2 execution was cancelled.", ct));
        }
    }

    private static void ValidateV2Request(QueryRuntimeRequest request)
    {
        if ((request.AvailableToolsProvider != null || request.AvailableCodexToolsProvider != null) && request.FrozenToolCatalog == null)
        {
            throw new InvalidOperationException(
                "qre_v2_c5_unbounded_dynamic_catalog_unsupported: provide a frozen superset registry plus a C5 ToolCatalogSelector instead of mutating executable tools after Turn start.");
        }
    }

    /// <summary>
    /// Maps the model window to C5 budgets. QRE's MaxItemTokens bounds EVERY history item (system and user
    /// messages as well as tool arguments), so it must not come from the history-storage trim
    /// (SingleTurnSoftLimitChars) or large planner system prompts lose goals/schema. A single item may use the
    /// whole projection window; the overall window is still enforced, and tool results stay separately bounded
    /// with the standard window ratios. Initial messages are never silently truncated or compacted away: the
    /// turn fails closed (ContextOverflow) before the model is called.
    /// </summary>
    private static QreV2.RuntimeContextOptions CreateContextOptions(ContextCompressionTriggerOptions options)
    {
        var maxContext = Math.Max(2_048, options.ResolveTriggerThresholdTokens());
        return new QreV2.RuntimeContextOptions
        {
            MaxContextTokens = maxContext,
            MaxItemTokens = maxContext,
            MaxToolResultTokens = Math.Max(256, maxContext / 8),
            LargeToolResultTokens = Math.Max(128, maxContext / 16),
            SummaryTokens = Math.Clamp(maxContext / 16, 256, maxContext),
            RecentTrajectoryMessages = checked(options.ResolveRecentTurnsSoftLimit() * 2),
            TruncationPolicy = QreV2.RuntimeContextTruncationPolicy.FailClosedOnInitialMessages
        };
    }

    private static bool IsContextOverflow(QreProtocol.RuntimeError? error)
        => error != null &&
           (error.Category == QreProtocol.RuntimeErrorCategory.ContextOverflow ||
            string.Equals(error.Code, "tool_catalog_context_budget_exhausted", StringComparison.Ordinal) ||
            string.Equals(error.Code, "context_truncation_rejected", StringComparison.Ordinal));

    private static IReadOnlyList<AIFunction> ResolveTools(QueryRuntimeRequest request)
        => request.EnableTools ? request.FrozenToolCatalog ?? request.AvailableTools ?? [] : [];

    private static AIFunction[] WrapTools(
        QueryRuntimeRequest request,
        IReadOnlyList<AIFunction> tools)
        => tools.Select(tool => tool is QreV2AdapterUtilities.TrustedRuntimeArgumentsAIFunction
            ? tool
            : new QreV2AdapterUtilities.TrustedRuntimeArgumentsAIFunction(tool, request.Session)).ToArray();

    private static RequiredToolExecutionContract? ResolveContract(QueryRuntimeRequest request)
        => request.RequiredToolContract ?? request.WorkerContext?.RequiredToolContract;

    /// <summary>
    /// Initial (round-1) forced tool. A multi-tool AnyOf contract is a BEFORE-STOP requirement, never a
    /// first-tool requirement, so it must not collapse to its preferred tool here: the worker may need to
    /// read/build first or satisfy it with an alternative write tool. Only a single-tool contract keeps the
    /// legacy initial requirement.
    /// </summary>
    private static string? ResolveRequiredToolName(
        QueryRuntimeRequest request,
        IReadOnlyList<AIFunction> tools)
    {
        var contract = ResolveContract(request);
        if (contract == null || contract.AnyOfToolNames.Count != 1)
        {
            return null;
        }

        return contract.ResolveRequiredToolName(tools);
    }

    /// <summary>
    /// Opt-in diagnostic recording (see <see cref="QueryRuntimeBackendOptions.AuditDirectory"/>). Recording must never break a run: if the store
    /// cannot be opened the run goes on with the in-memory audit.
    /// </summary>
    private QreV2.RuntimeJsonlAuditStore? OpenRecording(QueryRuntimeRequest request)
    {
        if (_auditDirectory is null || _auditSink is not null)
        {
            return null;
        }

        try
        {
            var store = QreV2.RuntimeJsonlAuditStore.Create(
                _auditDirectory,
                request.SessionId,
                new QreV2.RuntimeAuditStoreOptions { DataMode = QreV2.RuntimeAuditDataMode.PrivateDiagnostic });
            _logger.LogInformation(
                "[AuditRecording] SessionId={SessionId} Entry={Entry} Manifest={Manifest}",
                request.SessionId, request.EntryPoint, store.ManifestPath);
            return store;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "The QRE audit recording could not be opened for session {SessionId}; continuing without it.", request.SessionId);
            return null;
        }
    }

    private static int ResolveMaxContinuations(QueryRuntimeRequest request)
    {
        var required = request.RequiredToolContract?.MaxContinuationAttempts
            ?? request.WorkerContext?.RequiredToolContract?.MaxContinuationAttempts
            ?? 1;
        return Math.Max(required, request.AdapterHints?.MaxRecoveryAttempts ?? 1);
    }

    private static string ResolveObjective(QueryRuntimeRequest request)
        => request.InitialMessages
            .LastOrDefault(static message => message.Role == ChatRole.User)?.Text
            ?? request.EntryPoint.ToString();

    private static QueryRuntimeResult BuildResult(
        QueryRuntimeRequest request,
        QreV2.RuntimeTurnResult runtimeResult,
        Guid queryId,
        long elapsedMs,
        CodexFlowRuntimeModelClient? modelClient = null)
    {
        var invocations = runtimeResult.Turn.Steps
            .SelectMany(static step => step.ToolInvocations ?? [])
            .ToArray();
        var thinking = string.Concat(runtimeResult.Turn.Steps
            .Select(static step => step.Output?.Reasoning ?? string.Empty));
        var finalMessages = ToMeaiMessages(runtimeResult.History);
        var termination = runtimeResult.TerminationReason switch
        {
            QreProtocol.RuntimeTerminationReason.Completed when runtimeResult.Turn.Progress.ToolCallCount == 0
                => QueryTerminationReason.NoToolCalls,
            QreProtocol.RuntimeTerminationReason.Completed => QueryTerminationReason.Normal,
            QreProtocol.RuntimeTerminationReason.MaxSteps => QueryTerminationReason.MaxRoundsReached,
            QreProtocol.RuntimeTerminationReason.RequiredToolMissing => QueryTerminationReason.RecoveryExhausted,
            QreProtocol.RuntimeTerminationReason.StopGateRejected => QueryTerminationReason.RecoveryExhausted,
            // Context budget/truncation rejections are a hard limit, not a generic runtime exception, so callers
            // (e.g. the planner) must not bypass the budget through fallbacks.
            _ when IsContextOverflow(runtimeResult.Error) => QueryTerminationReason.ContextHardLimit,
            _ => QueryTerminationReason.Exception
        };

        var lastStepPrompt = modelClient?.LastStepInputTokens is > 0
            ? ToNullableInt(modelClient.LastStepInputTokens)
            : (runtimeResult.Turn.Steps.Count <= 1 ? ToNullableInt(runtimeResult.Usage.InputTokens) : null);
        var lastStepCompletion = modelClient?.LastStepOutputTokens is > 0
            ? ToNullableInt(modelClient.LastStepOutputTokens)
            : (runtimeResult.Turn.Steps.Count <= 1 ? ToNullableInt(runtimeResult.Usage.OutputTokens) : null);

        return new QueryRuntimeResult
        {
            TerminationReason = termination,
            TotalRounds = runtimeResult.Turn.Steps.Count,
            TotalToolCalls = runtimeResult.Turn.Progress.ToolCallCount,
            WriteToolCalls = invocations.Count(static invocation =>
                invocation.Status is QreV2.RuntimeToolInvocationStatus.Succeeded or
                    QreV2.RuntimeToolInvocationStatus.Failed &&
                ToolClassification.IsWriteTool(invocation.Call.Name)),
            ZeroToolCallRounds = runtimeResult.Turn.Steps.Count(static step =>
                step.ToolInvocations == null || step.ToolInvocations.Count == 0),
            EmptyResponseCount = string.IsNullOrWhiteSpace(runtimeResult.FinalText) ? 1 : 0,
            RecoveryCount = runtimeResult.Turn.Progress.ContinuationCount,
            MalformedProtocolCount = runtimeResult.Error?.Category == QreProtocol.RuntimeErrorCategory.ProviderProtocol ? 1 : 0,
            FinalText = runtimeResult.FinalText,
            FinalThinking = string.IsNullOrEmpty(thinking) ? null : thinking,
            TotalPromptTokens = ToNullableInt(runtimeResult.Usage.InputTokens),
            TotalCompletionTokens = ToNullableInt(runtimeResult.Usage.OutputTokens),
            LastStepPromptTokens = lastStepPrompt,
            LastStepCompletionTokens = lastStepCompletion,
            TotalCachedPromptTokens = runtimeResult.Usage.CachedInputTokens > 0
                ? ToNullableInt(runtimeResult.Usage.CachedInputTokens)
                : null,
            TotalDurationMs = elapsedMs,
            TerminalDetailCode = runtimeResult.Error?.Code,
            TerminalDetailMessage = runtimeResult.Error?.Message,
            LastFunctionCall = invocations.LastOrDefault()?.Call.Name,
            FinalMessages = finalMessages,
            LastPromptAssemblySnapshot = BuildPromptSnapshot(request, runtimeResult, queryId),
            QueryId = queryId
        };
    }

    private static QueryRuntimeResult BuildFailureResult(
        QueryRuntimeRequest request,
        Guid queryId,
        long elapsedMs,
        Exception error)
        => new()
        {
            TerminationReason = QueryTerminationReason.Exception,
            TotalRounds = 0,
            TotalToolCalls = 0,
            WriteToolCalls = 0,
            ZeroToolCallRounds = 0,
            EmptyResponseCount = 0,
            RecoveryCount = 0,
            MalformedProtocolCount = 0,
            FinalText = string.Empty,
            TotalDurationMs = elapsedMs,
            TerminalDetailCode = error.Message,
            FinalMessages = request.InitialMessages,
            QueryId = queryId
        };

    private static PromptAssemblySnapshot? BuildPromptSnapshot(
        QueryRuntimeRequest request,
        QreV2.RuntimeTurnResult result,
        Guid queryId)
    {
        var step = result.Turn.Steps.Count == 0 ? null : result.Turn.Steps[^1];
        if (step == null)
        {
            return null;
        }

        return new PromptAssemblySnapshot
        {
            QueryId = queryId,
            SessionId = request.SessionId,
            Round = step.Context.Index,
            EntryPoint = request.EntryPoint.ToString(),
            Frames = [],
            ToolNames = step.Context.ModelRequest.Tools.Select(static tool => tool.CanonicalName).ToArray(),
            RequiredToolName = step.Context.ModelRequest.Parameters.RequiredToolName,
            ToolsEnabled = step.Context.ModelRequest.Tools.Count > 0,
            ToolCallsAllowed = step.Context.ModelRequest.Tools.Count > 0,
            MessageCount = step.Context.ModelRequest.Messages.Count,
            EstimatedContextChars = EstimateProtocolChars(step.Context.ModelRequest.Messages),
            // The engine's estimator (UTF-8 bytes / 4, tool calls included), so this number matches the budget
            // the context was prepared against. Characters / 4 undercounts CJK text about threefold.
            EstimatedPromptTokens = Math.Max(1, step.Context.ModelRequest.Messages.Sum(QreV2.RuntimeTokenEstimator.Estimate))
        };
    }

    private static int? ToNullableInt(long? value)
        => value.HasValue ? checked((int)Math.Min(value.Value, int.MaxValue)) : null;

    private static int EstimateContextChars(IReadOnlyList<ChatMessage> messages)
        => messages.Sum(static message => message.Text?.Length ?? 0);

    private static int EstimateProtocolChars(IReadOnlyList<QreProtocol.RuntimeMessage> messages)
        => messages.Sum(static message => message.Items.Sum(static item => item switch
        {
            QreProtocol.RuntimeTextItem text => text.Text.Length,
            QreProtocol.RuntimeReasoningItem reasoning => reasoning.Text.Length,
            QreProtocol.RuntimeToolResultItem result => result.Result.Text?.Length ?? 0,
            _ => 0
        }));

    internal sealed class CodexFlowV2ModelClient(
        ILLMExecutor executor,
        QueryRuntimeRequest coreRequest,
        IReadOnlyList<AIFunction> tools,
        TimeSpan timeout,
        ILogger logger,
        TurnEvidence? evidence = null) : QreProtocol.IRuntimeModelClient
    {
        /// <summary>
        /// A model answer with no content at all (no text, reasoning or tool call; only usage and the finish marker) has changed nothing, so asking
        /// again is safe. It is asked once more, with the same input; a second empty answer stands and the turn fails as before. Decided by what
        /// the stream carried, not by any wording.
        /// </summary>
        public async IAsyncEnumerable<QreProtocol.RuntimeModelStreamEvent> StreamAsync(
            QreProtocol.RuntimeModelRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var attempt = 0; ; attempt++)
            {
                var sawContent = false;
                var held = new List<QreProtocol.RuntimeModelStreamEvent>(); // usage/warnings before any content; an attempt that is repeated drops its warnings but keeps its usage
                QreProtocol.RuntimeModelCompletedEvent? completion = null;
                await foreach (var runtimeEvent in StreamAttemptAsync(request, ct).WithCancellation(ct).ConfigureAwait(false))
                {
                    if (runtimeEvent is QreProtocol.RuntimeModelCompletedEvent completed)
                    {
                        completion = completed; // always the last event of an attempt; held until we know whether to ask again
                        continue;
                    }

                    if (!sawContent && runtimeEvent is QreProtocol.RuntimeUsageEvent or QreProtocol.RuntimeWarningEvent)
                    {
                        held.Add(runtimeEvent);
                        continue;
                    }

                    if (!sawContent)
                    {
                        sawContent = true;
                        foreach (var early in held)
                        {
                            yield return early;
                        }

                        held.Clear();
                    }

                    yield return runtimeEvent;
                }

                if (!sawContent && attempt == 0)
                {
                    logger.LogWarning(
                        "The model returned no content for session {SessionId}; asking once more.",
                        coreRequest.SessionId);
                    // The provider still billed the empty attempt, so its usage counts towards the turn.
                    foreach (var spent in held.OfType<QreProtocol.RuntimeUsageEvent>())
                    {
                        yield return spent;
                    }
                    continue;
                }

                foreach (var early in held)
                {
                    yield return early;
                }

                if (completion != null)
                {
                    yield return completion;
                }

                yield break;
            }
        }

        private async IAsyncEnumerable<QreProtocol.RuntimeModelStreamEvent> StreamAttemptAsync(
            QreProtocol.RuntimeModelRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var options = QreV2AdapterUtilities.CloneChatOptions(coreRequest.Options);
            var toolMap = tools.ToDictionary(static tool => tool.Name, StringComparer.OrdinalIgnoreCase);
            options.Tools = request.Tools.Select(descriptor =>
                (AITool)(toolMap.TryGetValue(descriptor.CanonicalName, out var function)
                    ? function
                    : throw new QreProtocol.RuntimeModelClientException(new QreProtocol.RuntimeError(
                        QreProtocol.RuntimeErrorCategory.RuntimeInvariantViolation,
                        "codexflow_context_tool_missing",
                        $"Prepared C5 tool '{descriptor.CanonicalName}' has no host function."))))
                .ToList();
            if (!string.IsNullOrWhiteSpace(request.Parameters.RequiredToolName))
            {
                options.ToolMode = ChatToolMode.RequireSpecific(request.Parameters.RequiredToolName);
                options.ThinkingEnabled = false;
            }
            else if (request.Tools.Count == 0)
            {
                options.ToolMode = ChatToolMode.None;
            }

            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeout > TimeSpan.Zero)
            {
                timeoutCancellation.CancelAfter(timeout);
            }

            var modelMessages = ToMeaiMessages(request.Messages);
            var dynamicContext = await ResolveDynamicContextAsync(
                coreRequest,
                request,
                modelMessages,
                logger,
                timeoutCancellation.Token).ConfigureAwait(false);
            dynamicContext = MergeProgressGuard(dynamicContext, request);
            if (dynamicContext != null)
            {
                modelMessages = ApplyDynamicContext(modelMessages, dynamicContext);
                var dynamicRequired = dynamicContext.RequiredToolName?.Trim();
                if (!string.IsNullOrWhiteSpace(dynamicRequired))
                {
                    if (!request.Tools.Any(tool => string.Equals(
                            tool.CanonicalName,
                            dynamicRequired,
                            StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new QreProtocol.RuntimeModelClientException(new QreProtocol.RuntimeError(
                            QreProtocol.RuntimeErrorCategory.RuntimeInvariantViolation,
                            "dynamic_required_tool_not_exposed",
                            $"Dynamic context required tool '{dynamicRequired}' outside the frozen C5 Step catalog."));
                    }
                    options.ToolMode = ChatToolMode.RequireSpecific(dynamicRequired);
                    options.ThinkingEnabled = false;
                }
            }

            // A model that keeps calling a tool this turn does not expose: first told which tools exist, then stopped with a reason.
            var deniedCalls = UnexposedToolCallGuard.TrailingDenials(request.Messages);
            if (UnexposedToolCallGuard.ShouldStop(deniedCalls))
            {
                logger.LogWarning(
                    "[UnexposedToolCalls] SessionId={SessionId} Entry={Entry} Denied={Count} Tools={Tools}; ending the turn.",
                    coreRequest.SessionId, coreRequest.EntryPoint, deniedCalls.Count,
                    SafeLogText(string.Join(",", deniedCalls.Distinct(StringComparer.OrdinalIgnoreCase)), 200));
                throw new QreProtocol.RuntimeModelClientException(new QreProtocol.RuntimeError(
                    QreProtocol.RuntimeErrorCategory.ProviderProtocol,
                    UnexposedToolCallGuard.StopCode,
                    UnexposedToolCallGuard.StopMessage(deniedCalls)));
            }

            if (UnexposedToolCallGuard.Nudge(deniedCalls, request.Tools.Select(static tool => tool.CanonicalName)) is { } unexposedNudge)
            {
                modelMessages = [.. modelMessages, unexposedNudge];
            }

            var stream = executor.StreamAsync(
                new LLMExecutionRequest(
                    modelMessages,
                    options,
                    coreRequest.Scenario,
                    coreRequest.Session,
                    nameof(QreV2BackedQueryRuntimeEngine)),
                timeoutCancellation.Token);
            QreProtocol.RuntimeModelCompletedEvent? pendingCompletion = null;
            await using var enumerator = stream.GetAsyncEnumerator(timeoutCancellation.Token);
            while (true)
            {
                ChatResponseUpdate update;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }
                    update = enumerator.Current;
                }
                catch (OperationCanceledException ex) when (
                    timeoutCancellation.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    throw new QreProtocol.RuntimeModelClientException(
                        new QreProtocol.RuntimeError(
                            QreProtocol.RuntimeErrorCategory.ProviderTransport,
                            "codexflow_model_timeout",
                            "CodexFlow model streaming timed out.",
                            Retryable: true),
                        ex);
                }
                catch (Exception ex) when (ex is not QreProtocol.RuntimeModelClientException && TransientFailures.IsConnectionFailure(ex))
                {
                    // Live run (local vLLM, bugfix-paging and dependency-upgrade-compliant): "An error occurred while sending the request." reached
                    // the runtime as a plain exception, which it reports as a NON-retryable model_stream_failed, so one dropped connection ended
                    // the whole turn or worker. A connection-level failure is the provider's transport, and the runtime may retry it.
                    throw new QreProtocol.RuntimeModelClientException(
                        new QreProtocol.RuntimeError(
                            QreProtocol.RuntimeErrorCategory.ProviderTransport,
                            "provider_transport_failed",
                            ex.Message,
                            Retryable: true),
                        ex);
                }

                foreach (var runtimeEvent in ToProtocolEvents(update))
                {
                    if (runtimeEvent is QreProtocol.RuntimeModelCompletedEvent completion)
                    {
                        if (pendingCompletion != null && pendingCompletion.StopReason != completion.StopReason)
                        {
                            throw new QreProtocol.RuntimeModelClientException(new QreProtocol.RuntimeError(
                                QreProtocol.RuntimeErrorCategory.ProviderProtocol,
                                "conflicting_provider_finish_reason",
                                "The CodexFlow provider emitted conflicting finish reasons."));
                        }
                        pendingCompletion = completion;
                        continue;
                    }
                    if (pendingCompletion != null && runtimeEvent is not (
                            QreProtocol.RuntimeUsageEvent or QreProtocol.RuntimeWarningEvent))
                    {
                        throw new QreProtocol.RuntimeModelClientException(new QreProtocol.RuntimeError(
                            QreProtocol.RuntimeErrorCategory.ProviderProtocol,
                            "provider_content_after_finish",
                            "The CodexFlow provider emitted model content after its finish reason."));
                    }
                    yield return runtimeEvent;
                }
            }

            if (pendingCompletion == null)
            {
                yield return new QreProtocol.RuntimeWarningEvent(new QreProtocol.RuntimeWarning(
                    "missing_provider_finish_reason",
                    "The CodexFlow provider stream ended without a finish reason."));
                pendingCompletion = new QreProtocol.RuntimeModelCompletedEvent(QreProtocol.RuntimeModelStopReason.Unknown);
            }
            yield return pendingCompletion;
        }

        // The runtime's own progress guard (a forge worker that only reads) is merged with whatever the request's provider asked for; the
        // provider's required tool wins.
        private QueryRuntimeDynamicContextResult? MergeProgressGuard(
            QueryRuntimeDynamicContextResult? fromProvider,
            QreProtocol.RuntimeModelRequest request)
        {
            if (evidence == null)
            {
                return fromProvider;
            }

            var writeTool = ForgeProgressGuard.PickWriteTool(request.Tools.Select(static tool => tool.CanonicalName));
            var guard = ForgeProgressGuard.Build(coreRequest.EntryPoint, evidence.CallsSinceLastWrite, writeTool, evidence.ForcedWriteRounds);
            if (guard == null)
            {
                return fromProvider;
            }

            if (guard.RequiredToolName != null && string.IsNullOrWhiteSpace(fromProvider?.RequiredToolName))
            {
                evidence.NoteForcedWriteRound();
            }

            return fromProvider == null
                ? guard
                : fromProvider with
                {
                    Messages = [.. fromProvider.Messages, .. guard.Messages],
                    RequiredToolName = string.IsNullOrWhiteSpace(fromProvider.RequiredToolName) ? guard.RequiredToolName : fromProvider.RequiredToolName
                };
        }

        private static async ValueTask<QueryRuntimeDynamicContextResult?> ResolveDynamicContextAsync(
            QueryRuntimeRequest coreRequest,
            QreProtocol.RuntimeModelRequest request,
            IReadOnlyList<ChatMessage> messages,
            ILogger logger,
            CancellationToken ct)
        {
            if (coreRequest.DynamicContextProvider == null)
            {
                return null;
            }
            var state = new QueryRuntimeState
            {
                Round = ReadStepIndex(request.StepId),
                MaxRounds = Math.Max(1, coreRequest.MaxRounds)
            };
            state.Messages.AddRange(messages);
            try
            {
                return await coreRequest.DynamicContextProvider(
                    new QueryRuntimeDynamicContextRequest(
                        coreRequest,
                        state,
                        state.Round,
                        request.Tools.Count > 0,
                        request.Parameters.RequiredToolName),
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "QRE v2 C5 dynamic context failed for session {SessionId} on round {Round}; continuing with canonical prepared context.",
                    coreRequest.SessionId,
                    state.Round);
                return null;
            }
        }

        private static ChatMessage[] ApplyDynamicContext(
            IReadOnlyList<ChatMessage> messages,
            QueryRuntimeDynamicContextResult dynamicContext)
        {
            var injected = dynamicContext.Messages
                .Where(static message => !string.IsNullOrWhiteSpace(message.Text))
                .ToList();
            if (dynamicContext.RecoveryHints.Count > 0)
            {
                injected.Add(new ChatMessage(
                    ChatRole.User,
                    "[SYSTEM] Runtime recovery hints:\n" + string.Join('\n', dynamicContext.RecoveryHints.Select(static hint =>
                        $"- {hint.Source}: {hint.Message ?? hint.RequiredToolName ?? "retry"}"))));
            }
            if (injected.Count == 0)
            {
                return messages.ToArray();
            }
            var result = messages.ToList();
            var insertion = 0;
            while (insertion < result.Count && result[insertion].Role == ChatRole.System)
            {
                insertion++;
            }
            result.InsertRange(insertion, injected);
            return result.ToArray();
        }

        private static int ReadStepIndex(QreProtocol.RuntimeStepId stepId)
        {
            var separator = stepId.Value.LastIndexOf(':');
            return separator >= 0 && int.TryParse(stepId.Value[(separator + 1)..], out var index)
                ? index
                : 0;
        }
    }

    private sealed class CodexFlowV2ToolPolicy(QueryRuntimeRequest request, ILogger logger) : QreV2.IRuntimeToolPolicyEvaluator
    {
        /// <summary>
        /// Every denial is logged with a stable Source code and the (sanitized, bounded) reason so an operator can see which
        /// gate refused a tool call. Model-supplied arguments and credentials are never logged: only session id, tool name,
        /// source and the gate's own message.
        /// </summary>
        private QreV2.RuntimeToolPolicyDecision Deny(string toolName, string source, string? reason, string fallback)
        {
            var message = string.IsNullOrWhiteSpace(reason) ? fallback : reason!;
            logger.LogWarning(
                "[ToolPolicyDenied] SessionId={SessionId} Tool={Tool} Source={Source} Reason={Reason}",
                request.SessionId, SafeLogText(toolName, 80), source, SafeLogText(message, 300));
            return QreV2.RuntimeToolPolicyDecision.Deny(message);
        }

        public async ValueTask<QreV2.RuntimeToolPolicyDecision> EvaluateAsync(
            QreV2.RuntimeToolPolicyContext context,
            CancellationToken ct)
        {
            var hook = request.InterventionHook;
            var session = request.Session;
            var arguments = ToObjectDictionary(context.Arguments.Value);
            if (session != null)
            {
                arguments["session_id"] = session.Id;
                arguments["workspace_path"] = new CodexFlow.Core.Execution.TrustedExecutionContextAccessor().Current is null
                    ? session.WorkspacePath : QreV2AdapterUtilities.ResolveTrustedWorkspace(session);
                arguments["project_root"] = QreV2AdapterUtilities.ResolveTrustedWorkspace(session);
            }
            var rawTool = (request.AvailableCodexToolsProvider?.Invoke() ?? request.AvailableCodexTools ?? [])
                .FirstOrDefault(tool => string.Equals(tool.Name, context.Tool.Descriptor.CanonicalName, StringComparison.OrdinalIgnoreCase));
            if (rawTool != null)
            {
                var validation = await rawTool.ValidateInputAsync(arguments, ct).ConfigureAwait(false);
                if (!validation.IsValid) return Deny(context.Tool.Descriptor.CanonicalName, "InputValidation", validation.Message, "Tool input rejected.");
                var permission = await rawTool.CheckPermissionsAsync(arguments, request, ct: ct).ConfigureAwait(false);
                if (!permission.IsAllowed) return Deny(context.Tool.Descriptor.CanonicalName, "ToolPermission", permission.Message, "Tool permission denied.");
            }
            var isWrite = context.Tool.Descriptor.SideEffect == QreProtocol.RuntimeToolSideEffect.WorkspaceWrite;
            if (hook == null)
            {
                return isWrite
                    ? Deny(context.Tool.Descriptor.CanonicalName, "NoInterventionHook", null,
                        "CodexFlow write tools require an intervention/approval hook.")
                    : new QreV2.RuntimeToolPolicyDecision(
                        QreV2.RuntimeToolPolicyDecisionKind.Allow,
                        "CodexFlow read tool allowed.",
                        context.Tool.Capabilities,
                        context.Tool.Sandbox);
            }

            var decision = await hook.OnToolCallRequestedAsync(
                context.Tool.Descriptor.CanonicalName,
                arguments,
                session,
                ct).ConfigureAwait(false);
            if (decision.ShouldBlock)
            {
                return Deny(context.Tool.Descriptor.CanonicalName, "InterventionHook", decision.Reason ?? decision.InjectedMessage?.Text,
                    "CodexFlow intervention blocked the tool.");
            }
            return isWrite
                ? new QreV2.RuntimeToolPolicyDecision(
                    QreV2.RuntimeToolPolicyDecisionKind.RequireApproval,
                    decision.Reason ?? "CodexFlow intervention approved the write plan.",
                    context.Tool.Capabilities,
                    context.Tool.Sandbox,
                    TimeSpan.FromMinutes(5))
                : new QreV2.RuntimeToolPolicyDecision(
                    QreV2.RuntimeToolPolicyDecisionKind.Allow,
                    decision.Reason ?? "CodexFlow intervention allowed the tool.",
                    context.Tool.Capabilities,
                    context.Tool.Sandbox);
        }
    }

    /// <summary>
    /// What the tools of one turn really returned, folded into evidence by the same processor the legacy loop used (a command that exited
    /// non-zero is not recorded as passed). The stop hook is handed the verification outcomes (passed AND failed, latest run of each kind), the
    /// changed files and diff lines a real <c>git status</c>/<c>git diff</c> reported, so it judges a worker by what happened, not by what
    /// the worker wrote about itself. Tool-call history stays out: it would make the hook demand a fresh <c>git status</c> after every write.
    /// </summary>
    internal sealed class TurnEvidence(QueryRuntimeRequest request)
    {
        private readonly object _gate = new();
        private readonly QueryRuntimeState _state = new();
        private readonly Dictionary<string, RuntimeVerificationEvidence> _failed = new(StringComparer.Ordinal);
        private readonly List<UnappliedPreviewEvidence> _previews = [];
        private int _callsSinceWrite;
        private int _forcedWriteRounds;
        private WorkerStatusReport? _workerReport;
        private bool _wroteSinceReport;

        public WorkerStatusReport? WorkerReport
        {
            get { lock (_gate) { return _workerReport; } }
        }

        public int ForcedWriteRounds
        {
            get { lock (_gate) { return _forcedWriteRounds; } }
        }

        public void NoteForcedWriteRound()
        {
            lock (_gate) { _forcedWriteRounds++; }
        }

        /// <summary>Tool calls since the last real (not dryRun) successful write; a preview changes nothing, so it does not reset the count.</summary>
        public int CallsSinceLastWrite
        {
            get { lock (_gate) { return _callsSinceWrite; } }
        }

        public void Record(ToolExecutionResult result)
        {
            lock (_gate)
            {
                _callsSinceWrite = result.Success && !result.DryRun && ToolClassification.IsWriteTool(result.ToolName)
                    ? 0
                    : _callsSinceWrite + 1;

                if (result.Success && !result.DryRun && ToolClassification.IsWriteTool(result.ToolName))
                {
                    _wroteSinceReport = true;
                }

                // The worker's own structured statement (its call's arguments, never its prose). The latest successful call wins, except that
                // "blocked" cannot be reworded into "completed": only a write made after the blocked report can have finished what was left
                // (seen live: told its unfinished list meant blocked, the worker moved the same items to `unverified` and reported again).
                if (result.Success && string.Equals(result.ToolName, WorkerStatusTool.Name, StringComparison.OrdinalIgnoreCase) &&
                    result.Metadata is JsonElement reportArguments &&
                    WorkerStatusReport.FromArguments(reportArguments) is { } report)
                {
                    if (report.IsBlocked || _workerReport is not { IsBlocked: true } || _wroteSinceReport)
                    {
                        _workerReport = report;
                    }

                    _wroteSinceReport = false;
                }

                DefaultToolObservationProcessor.Instance.Observe(new ToolObservationRequest
                {
                    ToolCalls = [],
                    ToolResults = [result],
                    RuntimeRequest = request,
                    State = _state
                });

                // The LATEST outcome of a verification command is the evidence: a run that did not exit cleanly retires the earlier pass of
                // its kind and is itself recorded as a failure; a clean run retires the earlier failure. (Decided by the exit, not by whether
                // the ledger grew: identical passes are de-duplicated there.)
                if (result.Success && ToolClassification.IsWriteTool(result.ToolName))
                {
                    if (result.DryRun)
                    {
                        _previews.Add(new UnappliedPreviewEvidence { ToolName = result.ToolName });
                    }
                    else
                    {
                        _previews.Clear(); // a real write after the preview: the edit was applied
                    }
                }

                var text = result.Result ?? string.Empty;
                (string Kind, string Command)? run = VerificationCommand.Match(text) is { Success: true } command
                    ? ("dotnet_" + command.Groups[1].Value.ToLowerInvariant(), "dotnet " + command.Groups[1].Value.ToLowerInvariant())
                    : TestCommandEvidence.Classify(text);
                if (run is { } verificationRun)
                {
                    var kind = verificationRun.Kind;
                    if (DefaultToolObservationProcessor.ExitedCleanly(text, null))
                    {
                        _failed.Remove(kind);
                    }
                    else
                    {
                        _state.EvidenceLedger.Verifications.RemoveAll(verification => verification.Kind == kind);
                        _failed[kind] = new RuntimeVerificationEvidence
                        {
                            Kind = kind,
                            Summary = $"`{verificationRun.Command}`",
                            SourceToolName = result.ToolName,
                            SourceCommand = verificationRun.Command,
                            EnvironmentLimit = NuGetEnvironmentLimit.IsMatch(text) ||
                                               (MissingAssetsFile.IsMatch(text) && _failed.TryGetValue("dotnet_restore", out var restore) && restore.EnvironmentLimit)
                        };
                    }
                }
            }
        }

        // NuGet's own diagnostic codes: package id/version not found in the configured source(s), the source cannot be reached, the audit feed cannot
        // be reached. They are identifiers the toolchain emits, not wording.
        private static readonly System.Text.RegularExpressions.Regex NuGetEnvironmentLimit = new(
            @"\bNU(?:1101|1102|1103|1301|1900)\b",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        // NETSDK1004: the project has no assets file because the restore did not produce one. A consequence, and only counted as one when an
        // environment-limited restore failure is still standing in the same turn.
        private static readonly System.Text.RegularExpressions.Regex MissingAssetsFile = new(
            @"\bNETSDK1004\b",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        private static readonly System.Text.RegularExpressions.Regex VerificationCommand = new(
            @"\$\s*dotnet\s+(restore|build|test)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        public QueryEvidenceLedger SnapshotForStopHook()
        {
            var ledger = new QueryEvidenceLedger();
            lock (_gate)
            {
                ledger.Verifications.AddRange(_state.EvidenceLedger.Verifications);
                ledger.FailedVerifications.AddRange(_failed.Values);
                ledger.UnappliedPreviews.AddRange(_previews);
                ledger.WorkerReport = _workerReport;
                ledger.ChangedFiles.AddRange(_state.EvidenceLedger.ChangedFiles);
                ledger.DiffChanges.AddRange(_state.EvidenceLedger.DiffChanges);
            }

            return ledger;
        }
    }

    private sealed class CodexFlowV2RuntimeTool(
        AIFunction tool,
        IQueryRuntimeInterventionHook? intervention,
        CodexSession? session,
        Action<ToolExecutionResult>? record = null,
        IQueryRuntimeEventSink? eventSink = null,
        QueryRuntimeRequest? request = null,
        Guid queryId = default) : QreV2.IRuntimeTool
    {
        private readonly bool _isWrite = ToolClassification.IsWriteTool(tool.Name);

        public QreV2.RuntimeToolDefinition Definition { get; } = CreateDefinition(tool);

        public async ValueTask<QreProtocol.RuntimeToolResult> InvokeAsync(
            QreV2.RuntimeToolInvocation invocation,
            QreV2.RuntimeToolExecutionContext context,
            CancellationToken ct)
        {
            var call = invocation.OriginalCall;
            if (!string.Equals(tool.Name, invocation.Plan.ToolCanonicalName, StringComparison.Ordinal))
            {
                return Failure(call, "codexflow_tool_identity_mismatch", "The requested tool is not the frozen C4 registry entry.");
            }

            if (eventSink is not null &&
                request is not null &&
                eventSink.IsEnabled(QueryRuntimeEventType.ToolExecutionStarted))
            {
                await eventSink.OnEventAsync(new ToolExecutionStartedEvent(
                    0,
                    queryId,
                    request.SessionId,
                    request.EntryPoint,
                    0,
                    tool.Name,
                    call.InvocationId.Value)).ConfigureAwait(false);
            }

            try
            {
                var value = await tool.InvokeAsync(
                        new AIFunctionArguments(ToObjectDictionary(invocation.NormalizedArguments)),
                        ct)
                    .ConfigureAwait(false);
                var text = value?.ToString() ?? string.Empty;
                var businessFailed = IsFailedToolResult(value, text);
                var previewOnly = invocation.NormalizedArguments.ValueKind == JsonValueKind.Object &&
                                  invocation.NormalizedArguments.TryGetProperty("dryRun", out var dryRun) &&
                                  dryRun.ValueKind == JsonValueKind.True;
                record?.Invoke(new ToolExecutionResult(
                    tool.Name,
                    call.InvocationId.Value,
                    text,
                    !businessFailed,
                    Metadata: string.Equals(tool.Name, WorkerStatusTool.Name, StringComparison.OrdinalIgnoreCase)
                        ? invocation.NormalizedArguments.Clone()
                        : null,
                    DryRun: previewOnly));
                if (businessFailed)
                    return Failure(call, "codexflow_tool_business_failed", text);
                if (intervention != null)
                {
                    var decision = await intervention.OnToolExecutionCompletedAsync(
                        call.Name,
                        text,
                        success: true,
                        session,
                        ct).ConfigureAwait(false);
                    if (decision.ShouldBlock || decision.ShouldSkipToolResult)
                    {
                        return Failure(
                            call,
                            "codexflow_tool_result_rejected",
                            decision.Reason ?? decision.InjectedMessage?.Text ?? "CodexFlow intervention rejected the tool result.");
                    }
                }
                return new QreProtocol.RuntimeToolResult(
                    call.InvocationId,
                    text,
                    true,
                    Details: new QreProtocol.RuntimeToolResultDetails(
                        QreProtocol.RuntimeToolOutcome.Succeeded,
                        StandardOutput: text,
                        WorkspaceChangeEvidence: _isWrite ? "codexflow-host-write-tool-completed" : null));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Failure(call, "codexflow_tool_failed", ex.Message);
            }
        }

        private static QreProtocol.RuntimeToolResult Failure(
            QreProtocol.RuntimeToolCall call,
            string code,
            string message)
            => new(
                call.InvocationId,
                null,
                false,
                new QreProtocol.RuntimeError(
                    QreProtocol.RuntimeErrorCategory.ToolFailed,
                    code,
                    message),
                Details: new QreProtocol.RuntimeToolResultDetails(
                    QreProtocol.RuntimeToolOutcome.Failed,
                    StandardError: message));

        /// <summary>
        /// Only typed <see cref="CodexToolResult"/> values or JSON explicitly marked with
        /// <c>__icodex_tool_result: true</c> are interpreted; arbitrary JSON (even with status/output keys) is
        /// never treated as a business failure. Any non-Success status, including PartialSuccess, is
        /// conservatively a failure. A malformed status on a marked envelope is a failure and never throws.
        /// </summary>
        internal static bool IsFailedToolResult(object? value, string text)
        {
            if (value is CodexToolResult result) return result.Status != ToolResultStatus.Success;
            if (string.IsNullOrWhiteSpace(text)) return false;
            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return false;
                if (!root.TryGetProperty("__icodex_tool_result", out var marker) || marker.ValueKind != JsonValueKind.True)
                    return false;
                if (!root.TryGetProperty("status", out var status) && !root.TryGetProperty("Status", out status))
                    return true;
                return status.ValueKind switch
                {
                    JsonValueKind.Number => !(status.TryGetInt32(out var code) && code == (int)ToolResultStatus.Success),
                    JsonValueKind.String => !string.Equals(status.GetString(), nameof(ToolResultStatus.Success), StringComparison.OrdinalIgnoreCase),
                    _ => true
                };
            }
            catch (JsonException) { }
            return false;
        }

        private static QreV2.RuntimeToolDefinition CreateDefinition(AIFunction function)
        {
            var isWrite = ToolClassification.IsWriteTool(function.Name);
            return new QreV2.RuntimeToolDefinition(
                new QreProtocol.RuntimeToolDescriptor(
                    function.Name,
                    "1.0.0-codexflow-c4",
                    function.Description,
                    function.JsonSchema.Clone(),
                    isWrite
                        ? QreProtocol.RuntimeToolSideEffect.WorkspaceWrite
                        : QreProtocol.RuntimeToolSideEffect.ReadOnly,
                    isWrite
                        ? QreProtocol.RuntimeToolIdempotency.Unknown
                        : QreProtocol.RuntimeToolIdempotency.Idempotent),
                new HashSet<string>(
                    isWrite ? ["workspace.write"] : ["workspace.read"],
                    StringComparer.Ordinal),
                isWrite
                    ? QreV2.RuntimeToolConcurrency.ExclusiveWorkspace
                    : QreV2.RuntimeToolConcurrency.Serial,
                new QreV2.RuntimeSandboxRequirements(
                    QreV2.RuntimeSandboxKind.None,
                    QreV2.RuntimeNetworkMode.Deny,
                    isWrite
                        ? QreV2.RuntimeWorkspaceMountMode.ReadWrite
                        : QreV2.RuntimeWorkspaceMountMode.ReadOnly),
                new QreV2.RuntimeToolLimits(RuntimeToolTimeouts.For(function.Name), QreV2.RuntimeToolLimits.Default.MaxOutputBytes));
        }
    }

    private sealed class CodexFlowV2ToolApproval : QreV2.IRuntimeToolApproval
    {
        public ValueTask<QreV2.RuntimeToolApprovalDecision> DecideAsync(
            QreV2.ResolvedExecutionPlan plan,
            QreProtocol.RuntimeToolCall call,
            QreV2.RuntimeToolExecutionContext context,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                plan.Approval == null || plan.Approval.ExpiresAt <= DateTimeOffset.UtcNow
                    ? QreV2.RuntimeToolApprovalDecision.Decline(
                        "The CodexFlow intervention did not receive a live frozen approval binding.")
                    : QreV2.RuntimeToolApprovalDecision.Approve(
                        "The CodexFlow intervention approved this frozen C4 execution plan."));
        }
    }

    private sealed class CodexFlowV2TerminationPolicy(
        QueryRuntimeRequest request,
        IRuntimeHookDispatcher? dispatcher,
        RequiredToolExecutionContract? contract,
        TurnEvidence? evidence = null) : QreV2.IRuntimeTerminationPolicy
    {
        public async ValueTask<QreV2.RuntimeTerminationDecision> DecideAsync(
            QreV2.RuntimeTerminationContext context,
            CancellationToken ct)
        {
            var invocations = context.Session.ActiveTurn!.Steps
                .SelectMany(static step => step.ToolInvocations ?? [])
                .ToArray();
            var executed = new HashSet<string>(
                invocations.Where(static invocation => invocation.Status is
                        QreV2.RuntimeToolInvocationStatus.Executing or
                        QreV2.RuntimeToolInvocationStatus.Succeeded or
                        QreV2.RuntimeToolInvocationStatus.Failed)
                    .Select(static invocation => invocation.Call.Name),
                StringComparer.OrdinalIgnoreCase);
            // Only invocations that actually succeeded count; a failed (e.g. business-failed) call never does.
            var successful = new HashSet<string>(
                invocations.Where(static invocation =>
                        invocation.Status == QreV2.RuntimeToolInvocationStatus.Succeeded)
                    .Select(static invocation => invocation.Call.Name),
                StringComparer.OrdinalIgnoreCase);

            var decision = BeforeStopHookResult.None;
            if (dispatcher != null)
            {
                decision = await dispatcher.DispatchBeforeStopAsync(
                    new BeforeStopContext
                    {
                        Request = request,
                        Round = context.Step.Context.Index,
                        LastAssistantMessage = context.Step.Output?.Text ?? string.Empty,
                        ThinkingText = context.Step.Output?.Reasoning ?? string.Empty,
                        StopHookActive = context.Session.ActiveTurn.Progress.ContinuationCount > 0,
                        ContinuationCount = context.Session.ActiveTurn.Progress.ContinuationCount,
                        ExecutedToolNames = executed,
                        SuccessfulToolNames = successful,
                        TotalToolCalls = context.Session.ActiveTurn.Progress.ToolCallCount,
                        EvidenceLedger = evidence?.SnapshotForStopHook() ?? new QueryEvidenceLedger()
                    },
                    ct).ConfigureAwait(false) ?? BeforeStopHookResult.None;
            }

            // A forge worker's "must write" contract is also met by a verified statement that the task needed no change.
            var contractUnsatisfied = contract != null && !contract.IsSatisfiedBy(executed, successful) &&
                                      !VerifiedNoChangeCompletion.Waives(contract, request.EntryPoint, evidence?.SnapshotForStopHook());
            if (!decision.Continue && !contractUnsatisfied)
            {
                return QreV2.RuntimeTerminationDecision.Accept();
            }
            if (!context.CanContinue)
            {
                return QreV2.RuntimeTerminationDecision.FailClosed(new QreProtocol.RuntimeError(
                    QreProtocol.RuntimeErrorCategory.ResourceExhausted,
                    decision.ExhaustionDetailCode ?? QueryTerminalDetailCodes.RecoveryExhausted,
                    decision.Reason
                        ?? (contractUnsatisfied ? contract!.ExhaustionMessage : null)
                        ?? "CodexFlow stop hook exhausted the v2 continuation budget."));
            }

            var multiToolContract = contract is { AnyOfToolNames.Count: > 1 };
            var message = decision.Message ?? decision.Reason;
            if (string.IsNullOrWhiteSpace(message) && contractUnsatisfied)
            {
                message = contract!.Feedback
                    ?? $"Before stopping, successfully call one of: {contract.FormatToolList()}.";
            }

            // After a premature stop a single tool may be forced; a multi-tool AnyOf contract only SUGGESTS the
            // preferred tool so an equally valid alternative write tool and prerequisite reads stay available.
            var requiredNext = decision.RequiredToolNameForNextRound;
            if (string.IsNullOrWhiteSpace(requiredNext) && contractUnsatisfied && !multiToolContract)
            {
                requiredNext = contract!.PreferredRecoveryToolName;
            }
            if (!string.IsNullOrWhiteSpace(requiredNext) && !multiToolContract)
            {
                return QreV2.RuntimeTerminationDecision.RequireTool(requiredNext, message);
            }
            if (multiToolContract && contractUnsatisfied &&
                !string.IsNullOrWhiteSpace(contract!.PreferredRecoveryToolName) &&
                message?.Contains(contract.PreferredRecoveryToolName, StringComparison.OrdinalIgnoreCase) != true)
            {
                message = $"{message} (Preferred: `{contract.PreferredRecoveryToolName}`; any of {contract.FormatToolList()} is acceptable.)";
            }

            return QreV2.RuntimeTerminationDecision.Continue(
                message ?? "CodexFlow requested another verification step.");
        }
    }

    private sealed class CodexFlowV2EventSink(
        IQueryRuntimeEventSink inner,
        QueryRuntimeRequest request,
        Guid queryId) : QreV2.IRuntimeEventSink
    {
        public async ValueTask OnEventAsync(
            QreV2.RuntimePresentationEvent runtimeEvent,
            CancellationToken ct)
        {
            var round = ReadStepIndex(runtimeEvent.StepId);
            QueryRuntimeEvent? mapped = runtimeEvent.Type switch
            {
                QreV2.RuntimePresentationEventType.StepStarted when inner.IsEnabled(QueryRuntimeEventType.RoundStarted)
                    => new RoundStartedEvent(
                        runtimeEvent.Sequence,
                        queryId,
                        request.SessionId,
                        request.EntryPoint,
                        round,
                        Math.Max(1, request.MaxRounds),
                        EstimateContextChars(request.InitialMessages)),
                QreV2.RuntimePresentationEventType.TextDelta when inner.IsEnabled(QueryRuntimeEventType.AssistantDelta)
                    => new AssistantDeltaEvent(
                        runtimeEvent.Sequence,
                        queryId,
                        request.SessionId,
                        request.EntryPoint,
                        round,
                        runtimeEvent.Text ?? string.Empty),
                QreV2.RuntimePresentationEventType.ReasoningDelta when inner.IsEnabled(QueryRuntimeEventType.ThinkingDelta)
                    => new ThinkingDeltaEvent(
                        runtimeEvent.Sequence,
                        queryId,
                        request.SessionId,
                        request.EntryPoint,
                        round,
                        runtimeEvent.Text ?? string.Empty),
                QreV2.RuntimePresentationEventType.ToolCallRequested when
                    inner.IsEnabled(QueryRuntimeEventType.ToolCallRequested) && runtimeEvent.ToolCall != null
                    => new ToolCallRequestedEvent(
                        runtimeEvent.Sequence,
                        queryId,
                        request.SessionId,
                        request.EntryPoint,
                        round,
                        runtimeEvent.ToolCall.Name,
                        runtimeEvent.ToolCall.InvocationId.Value,
                        ToObjectDictionary(runtimeEvent.ToolCall.Arguments)),
                QreV2.RuntimePresentationEventType.ToolExecutionCompleted when
                    inner.IsEnabled(QueryRuntimeEventType.ToolExecutionCompleted) && runtimeEvent.ToolResult != null
                    => new ToolExecutionCompletedEvent(
                        runtimeEvent.Sequence,
                        queryId,
                        request.SessionId,
                        request.EntryPoint,
                        round,
                        runtimeEvent.ToolName ?? string.Empty,
                        runtimeEvent.InvocationId?.Value ?? string.Empty,
                        runtimeEvent.ToolResult.Text ?? runtimeEvent.ToolResult.Error?.Message ?? string.Empty,
                        runtimeEvent.ToolResult.Success,
                        runtimeEvent.ToolResult.Text?.Length),
                QreV2.RuntimePresentationEventType.HistoryCompacted when
                    inner.IsEnabled(QueryRuntimeEventType.ContextCompactionCompleted)
                    => new ContextCompactionCompletedEvent(
                        runtimeEvent.Sequence,
                        queryId,
                        request.SessionId,
                        request.EntryPoint,
                        round,
                        Success: true,
                        ErrorType: null,
                        ErrorMessage: runtimeEvent.ContextEvent?.Detail,
                        PromptTokens: null,
                        CompletionTokens: null,
                        FinalMessageCount: request.InitialMessages.Count,
                        FileEvidenceCount: 0,
                        PendingModificationCount: 0),
                QreV2.RuntimePresentationEventType.TurnCompleted or
                    QreV2.RuntimePresentationEventType.TurnFailed or
                    QreV2.RuntimePresentationEventType.TurnCancelled
                    when inner.IsEnabled(QueryRuntimeEventType.Terminated)
                    => new TerminatedEvent(
                        runtimeEvent.Sequence,
                        queryId,
                        request.SessionId,
                        request.EntryPoint,
                        runtimeEvent.Type == QreV2.RuntimePresentationEventType.TurnCompleted
                            ? runtimeEvent.TotalToolCalls > 0
                                ? QueryTerminationReason.Normal
                                : QueryTerminationReason.NoToolCalls
                            : QueryTerminationReason.Exception,
                        runtimeEvent.TotalSteps ?? round + 1,
                        runtimeEvent.TotalToolCalls ?? 0,
                        0,
                        runtimeEvent.Error?.Code),
                _ => null
            };

            if (mapped != null)
            {
                await inner.OnEventAsync(mapped).ConfigureAwait(false);
            }
        }

        private static int ReadStepIndex(QreProtocol.RuntimeStepId? stepId)
        {
            var value = stepId?.Value;
            var separator = value?.LastIndexOf(':') ?? -1;
            return separator >= 0 && int.TryParse(value![(separator + 1)..], out var index)
                ? index
                : 0;
        }
    }

    internal static QreProtocol.RuntimeMessage[] ToProtocolMessages(
        IReadOnlyList<ChatMessage> messages)
        => messages.Select(static message => new QreProtocol.RuntimeMessage(
            ToProtocolRole(message.Role),
            message.Contents.Select(ToProtocolItem).ToArray())).ToArray();

    internal static ChatMessage[] ToMeaiMessages(
        IReadOnlyList<QreProtocol.RuntimeMessage> messages)
        => messages.Select(static message => new ChatMessage(
            ToMeaiRole(message.Role),
            message.Items.Select(ToMeaiContent).ToList())).ToArray();

    private static QreProtocol.RuntimeMessageRole ToProtocolRole(ChatRole role)
        => role == ChatRole.System
            ? QreProtocol.RuntimeMessageRole.System
            : role == ChatRole.User
                ? QreProtocol.RuntimeMessageRole.User
                : role == ChatRole.Assistant
                    ? QreProtocol.RuntimeMessageRole.Assistant
                    : role == ChatRole.Tool
                        ? QreProtocol.RuntimeMessageRole.Tool
                        : throw new InvalidOperationException($"Unsupported ChatRole '{role}'.");

    private static ChatRole ToMeaiRole(QreProtocol.RuntimeMessageRole role)
        => role switch
        {
            QreProtocol.RuntimeMessageRole.System => ChatRole.System,
            QreProtocol.RuntimeMessageRole.User => ChatRole.User,
            QreProtocol.RuntimeMessageRole.Assistant => ChatRole.Assistant,
            QreProtocol.RuntimeMessageRole.Tool => ChatRole.Tool,
            _ => throw new InvalidOperationException($"Unsupported runtime role '{role}'.")
        };

    private static QreProtocol.RuntimeItem ToProtocolItem(AIContent content)
        => content switch
        {
            TextContent text => new QreProtocol.RuntimeTextItem(text.Text),
            TextReasoningContent reasoning => new QreProtocol.RuntimeReasoningItem(reasoning.Text, reasoning.ProtectedData),
            FunctionCallContent call => new QreProtocol.RuntimeToolCallItem(ToProtocolToolCall(call)),
            FunctionResultContent result => new QreProtocol.RuntimeToolResultItem(new QreProtocol.RuntimeToolResult(
                new QreProtocol.RuntimeInvocationId(result.CallId),
                result.Result?.ToString(),
                result.Exception == null,
                result.Exception == null
                    ? null
                    : new QreProtocol.RuntimeError(
                        QreProtocol.RuntimeErrorCategory.ToolFailed,
                        "codexflow_tool_result_error",
                        result.Exception.Message))),
            _ => throw new InvalidOperationException($"Unsupported MEAI content '{content.GetType().Name}'.")
        };

    /// <summary>
    /// A failed/denied/blocked tool result carries its reason in Error/StandardError and has a null Text. Passing the
    /// null Text through made the model see an EMPTY tool_result (it could not tell "blocked by policy" from "ran, no
    /// output") and loop on other commands. Always surface the reason; success text is passed through unchanged.
    /// </summary>
    internal static string ToolResultModelText(QreProtocol.RuntimeToolResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Text)) return result.Text!;
        var reason = result.Error?.Message;
        if (string.IsNullOrWhiteSpace(reason)) reason = result.Details?.StandardError;
        var code = result.Error?.Code;
        if (string.IsNullOrWhiteSpace(reason))
        {
            // Never hand the model an empty tool_result: say explicitly that nothing (neither output nor a reason) came back.
            return string.IsNullOrWhiteSpace(code)
                ? "[tool returned no output and no error detail; verify by other means before assuming it succeeded.]"
                : $"[tool failed: {code}; no further detail was provided]";
        }
        return string.IsNullOrWhiteSpace(code) ? $"[tool failed] {reason}" : $"[tool failed: {code}] {reason}";
    }

    /// <summary>
    /// One line per tool call: which tool, whether it worked and, for a failure, the start of the tool's own message. Without it a worker that
    /// ends with "N tool calls and no change" cannot be diagnosed afterwards (the job history only holds lifecycle events). Arguments and
    /// results of successful calls are never logged.
    /// </summary>
    internal static void LogToolOutcome(ILogger logger, QueryRuntimeRequest request, ToolExecutionResult result)
    {
        if (result.Success)
        {
            logger.LogInformation(
                "[ToolOutcome] SessionId={SessionId} Entry={Entry} Tool={Tool} Success=true",
                request.SessionId, request.EntryPoint, SafeLogText(result.ToolName, 80));
            return;
        }

        logger.LogInformation(
            "[ToolOutcome] SessionId={SessionId} Entry={Entry} Tool={Tool} Success=false Detail={Detail}",
            request.SessionId, request.EntryPoint, SafeLogText(result.ToolName, 80), SafeLogText(result.Result, 240));
    }

    internal static string SafeLogText(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            var ch = char.IsControl(c) ? ' ' : c;
            if (ch == ' ' && sb.Length > 0 && sb[^1] == ' ') continue;
            sb.Append(ch);
        }
        var clean = sb.ToString().Trim();
        return clean.Length <= max ? clean : clean[..max] + "...";
    }

    private static AIContent ToMeaiContent(QreProtocol.RuntimeItem item)
        => item switch
        {
            QreProtocol.RuntimeTextItem text => new TextContent(text.Text),
            QreProtocol.RuntimeReasoningItem reasoning => new TextReasoningContent(reasoning.Text)
            {
                ProtectedData = reasoning.ProtectedData
            },
            QreProtocol.RuntimeToolCallItem call => new FunctionCallContent(
                call.Call.InvocationId.Value,
                call.Call.Name,
                ToObjectDictionary(call.Call.Arguments)),
            QreProtocol.RuntimeToolResultItem result => new FunctionResultContent(
                result.Result.InvocationId.Value,
                ToolResultModelText(result.Result)),
            _ => throw new InvalidOperationException($"Unsupported runtime item '{item.GetType().Name}'.")
        };

    private static IEnumerable<QreProtocol.RuntimeModelStreamEvent> ToProtocolEvents(ChatResponseUpdate update)
    {
        var reasoningUpdate = update as ReasoningChatResponseUpdate;
        var emittedReasoning = false;
        var emittedUsage = false;
        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case TextContent text when reasoningUpdate?.Thinking == true:
                    emittedReasoning = true;
                    yield return new QreProtocol.RuntimeReasoningDeltaEvent(text.Text);
                    break;
                case TextContent text:
                    yield return new QreProtocol.RuntimeTextDeltaEvent(text.Text);
                    break;
                case TextReasoningContent reasoning:
                    emittedReasoning = true;
                    yield return new QreProtocol.RuntimeReasoningDeltaEvent(reasoning.Text, reasoning.ProtectedData);
                    break;
                case FunctionCallContent call:
                    yield return new QreProtocol.RuntimeToolCallEvent(ToProtocolToolCall(call));
                    break;
                case UsageContent usage:
                    emittedUsage = true;
                    yield return new QreProtocol.RuntimeUsageEvent(ToProtocolUsage(usage.Details));
                    break;
                case ErrorContent error:
                    yield return new QreProtocol.RuntimeWarningEvent(new QreProtocol.RuntimeWarning(
                        "codexflow_model_error_content",
                        error.Message));
                    break;
            }
        }

        if (reasoningUpdate?.Thinking == true &&
            !emittedReasoning &&
            !string.IsNullOrEmpty(reasoningUpdate.Reasoning))
        {
            yield return new QreProtocol.RuntimeReasoningDeltaEvent(reasoningUpdate.Reasoning);
        }
        if (update is UsageChatResponseUpdate { Usage: not null } usageUpdate && !emittedUsage)
        {
            yield return new QreProtocol.RuntimeUsageEvent(ToProtocolUsage(usageUpdate.Usage));
        }
        if (update.FinishReason.HasValue)
        {
            yield return new QreProtocol.RuntimeModelCompletedEvent(ToStopReason(update.FinishReason.Value));
        }
    }

    private static QreProtocol.RuntimeToolCall ToProtocolToolCall(FunctionCallContent call)
        => new(
            new QreProtocol.RuntimeInvocationId(call.CallId),
            call.Name,
            ToJsonElement(call.Arguments),
            call.InformationalOnly);

    private static QreProtocol.RuntimeUsage ToProtocolUsage(UsageDetails usage)
    {
        var additional = usage.AdditionalCounts == null
            ? null
            : new Dictionary<string, long>(usage.AdditionalCounts, StringComparer.Ordinal);
        if (usage.CachedInputTokenCount is { } cached &&
            additional?.ContainsKey(QreProtocol.RuntimeUsageKeys.CachedInputTokens) != true)
        {
            // Some clients report cache hits only on the typed property; the engine reads them from the counts.
            (additional ??= new Dictionary<string, long>(StringComparer.Ordinal))[QreProtocol.RuntimeUsageKeys.CachedInputTokens] = cached;
        }

        return new QreProtocol.RuntimeUsage(
            usage.InputTokenCount,
            usage.OutputTokenCount,
            usage.TotalTokenCount,
            additional);
    }

    private static QreProtocol.RuntimeModelStopReason ToStopReason(ChatFinishReason reason)
        => reason == ChatFinishReason.Stop
            ? QreProtocol.RuntimeModelStopReason.EndTurn
            : reason == ChatFinishReason.ToolCalls
                ? QreProtocol.RuntimeModelStopReason.ToolCall
                : reason == ChatFinishReason.Length
                    ? QreProtocol.RuntimeModelStopReason.MaxOutputTokens
                    : reason == ChatFinishReason.ContentFilter
                        ? QreProtocol.RuntimeModelStopReason.ContentFilter
                        : QreProtocol.RuntimeModelStopReason.Unknown;

    private static JsonElement ToJsonElement(IDictionary<string, object?>? arguments)
    {
        var root = new JsonObject();
        if (arguments != null)
        {
            foreach (var (name, value) in arguments)
            {
                root.Add(name, ToJsonNode(value));
            }
        }
        using var document = JsonDocument.Parse(root.ToJsonString());
        return document.RootElement.Clone();
    }

    private static JsonNode? ToJsonNode(object? value)
        => value switch
        {
            null => null,
            JsonElement element => JsonNode.Parse(element.GetRawText()),
            JsonNode node => node.DeepClone(),
            string text => JsonValue.Create(text),
            bool boolean => JsonValue.Create(boolean),
            byte number => JsonValue.Create(number),
            sbyte number => JsonValue.Create(number),
            short number => JsonValue.Create(number),
            ushort number => JsonValue.Create(number),
            int number => JsonValue.Create(number),
            uint number => JsonValue.Create(number),
            long number => JsonValue.Create(number),
            ulong number => JsonValue.Create(number),
            float number => JsonValue.Create(number),
            double number => JsonValue.Create(number),
            decimal number => JsonValue.Create(number),
            IDictionary<string, object?> dictionary => new JsonObject(
                dictionary.Select(static pair => KeyValuePair.Create(pair.Key, ToJsonNode(pair.Value)))),
            System.Collections.IEnumerable sequence when value is not string => new JsonArray(
                sequence.Cast<object?>().Select(ToJsonNode).ToArray()),
            _ => throw new InvalidOperationException($"Unsupported tool argument type '{value.GetType().FullName}'.")
        };

    private static Dictionary<string, object?> ToObjectDictionary(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Tool arguments must be a JSON object.");
        }
        return arguments.EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => ToObject(property.Value),
            StringComparer.Ordinal);
    }

    private static object? ToObject(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.Array => value.EnumerateArray().Select(ToObject).ToArray(),
            JsonValueKind.Object => value.EnumerateObject().ToDictionary(
                static property => property.Name,
                static property => ToObject(property.Value),
                StringComparer.Ordinal),
            _ => throw new InvalidOperationException($"Unsupported JSON value kind '{value.ValueKind}'.")
        };
}
