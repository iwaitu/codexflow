// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.CompilerServices;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Models;
using CodexFlow.QueryRuntime.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using QreProtocol = CodexFlow.QueryRuntime.Protocol;

namespace CodexFlow.Core.Runtime;

/// <summary>
/// CodexFlow outer decorator around QRE's <see cref="MeaiRuntimeModelClient"/>.
/// Preserves dynamic context injection, recovery hints, ForgeProgressGuard,
/// UnexposedToolCallGuard, disabling thinking when a required tool is set,
/// per-attempt timeout, and empty-stream retry with preserved usage, while
/// delegating MEAI translation, finish-reason validation, and provider error
/// classification to <see cref="MeaiRuntimeModelClient"/>.
/// </summary>
internal sealed class CodexFlowRuntimeModelClient(
    ILLMExecutor executor,
    QueryRuntimeRequest coreRequest,
    IReadOnlyList<AIFunction> tools,
    TimeSpan timeout,
    ILogger logger,
    QreV2BackedQueryRuntimeEngine.TurnEvidence? evidence = null) : QreProtocol.IRuntimeModelAttemptClient
{
    private readonly ILLMExecutor _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    private readonly QueryRuntimeRequest _coreRequest = coreRequest ?? throw new ArgumentNullException(nameof(coreRequest));
    private readonly IReadOnlyList<AIFunction> _tools = tools ?? throw new ArgumentNullException(nameof(tools));
    private readonly TimeSpan _timeout = timeout;
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly QreV2BackedQueryRuntimeEngine.TurnEvidence? _evidence = evidence;

    public IAsyncEnumerable<QreProtocol.RuntimeModelStreamEvent> StreamAsync(
        QreProtocol.RuntimeModelRequest request,
        CancellationToken ct = default)
        => StreamCoreAsync(request, attemptContext: null, ct);

    public IAsyncEnumerable<QreProtocol.RuntimeModelStreamEvent> StreamAsync(
        QreProtocol.RuntimeModelRequest request,
        QreProtocol.RuntimeModelAttemptContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return StreamCoreAsync(request, context, ct);
    }

    private async IAsyncEnumerable<QreProtocol.RuntimeModelStreamEvent> StreamCoreAsync(
        QreProtocol.RuntimeModelRequest request,
        QreProtocol.RuntimeModelAttemptContext? attemptContext,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        for (var attempt = 0; ; attempt++)
        {
            var sawContent = false;
            var held = new List<QreProtocol.RuntimeModelStreamEvent>();
            QreProtocol.RuntimeModelCompletedEvent? completion = null;

            await foreach (var runtimeEvent in StreamAttemptAsync(request, attemptContext, ct)
                .WithCancellation(ct)
                .ConfigureAwait(false))
            {
                if (runtimeEvent is QreProtocol.RuntimeModelCompletedEvent completed)
                {
                    completion = completed;
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
                _logger.LogWarning(
                    "The model returned no content for session {SessionId}; asking once more.",
                    _coreRequest.SessionId);
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
        QreProtocol.RuntimeModelAttemptContext? attemptContext,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ValidateExposedTools(request);

        var ambientTimeout = CodexFlow.Core.Execution.Approval.AmbientTurnTimeoutController.Current;
        using var pausableDeadline = ambientTimeout is not null && _timeout > TimeSpan.Zero
            ? ambientTimeout.CreateDeadline(_timeout, ct)
            : null;
        using var fallbackCts = pausableDeadline is null
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (fallbackCts is not null && _timeout > TimeSpan.Zero)
        {
            fallbackCts.CancelAfter(_timeout);
        }

        var attemptToken = pausableDeadline?.Token ?? fallbackCts!.Token;
        bool IsAttemptTimedOut() => pausableDeadline is not null
            ? pausableDeadline.IsTimedOut && !ct.IsCancellationRequested
            : fallbackCts!.IsCancellationRequested && !ct.IsCancellationRequested;

        QreProtocol.RuntimeModelRequest effectiveRequest;
        try
        {
            effectiveRequest = await PrepareEffectiveRequestAsync(request, attemptToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (IsAttemptTimedOut())
        {
            throw new QreProtocol.RuntimeModelClientException(
                new QreProtocol.RuntimeError(
                    QreProtocol.RuntimeErrorCategory.ProviderTransport,
                    "codexflow_model_timeout",
                    "CodexFlow dynamic context preparation timed out.",
                    Retryable: true),
                ex);
        }

        // Do not wrap MeaiRuntimeModelClient in a `using` block: its Dispose() disposes the
        // injected IChatClient. Although ExecutorChatClientBridge.Dispose() is a no-op, we also
        // never dispose MeaiRuntimeModelClient per turn to preserve the shared pipeline.
        var meaiClient = new MeaiRuntimeModelClient(
            new ExecutorChatClientBridge(
                _executor,
                _coreRequest.Scenario,
                _coreRequest.Session,
                nameof(QreV2BackedQueryRuntimeEngine)),
            req =>
            {
                var cloned = QreV2AdapterUtilities.CloneChatOptions(_coreRequest.Options);
                if (!string.IsNullOrWhiteSpace(req.Parameters.RequiredToolName))
                {
                    cloned.ThinkingEnabled = false;
                }

                return cloned;
            },
            new MeaiRuntimeModelClientOptions
            {
                ClassifyProviderErrors = true,
                StreamingMode = MeaiStreamingMode.Streaming,
                ToolStrict = MeaiToolStrictMode.Off
            });

        var stream = attemptContext is null
            ? meaiClient.StreamAsync(effectiveRequest, attemptToken)
            : meaiClient.StreamAsync(effectiveRequest, attemptContext, attemptToken);

        await using var enumerator = stream.GetAsyncEnumerator(attemptToken);
        while (true)
        {
            QreProtocol.RuntimeModelStreamEvent current;
            try
            {
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    yield break;
                }

                current = enumerator.Current;
            }
            catch (OperationCanceledException ex) when (IsAttemptTimedOut())
            {
                throw new QreProtocol.RuntimeModelClientException(
                    new QreProtocol.RuntimeError(
                        QreProtocol.RuntimeErrorCategory.ProviderTransport,
                        "codexflow_model_timeout",
                        "CodexFlow model streaming timed out.",
                        Retryable: true),
                    ex);
            }
            catch (Exception ex) when (
                ex is not QreProtocol.RuntimeModelClientException && TransientFailures.IsConnectionFailure(ex))
            {
                throw new QreProtocol.RuntimeModelClientException(
                    new QreProtocol.RuntimeError(
                        QreProtocol.RuntimeErrorCategory.ProviderTransport,
                        "provider_transport_failed",
                        ex.Message,
                        Retryable: true),
                    ex);
            }

            yield return current;
        }
    }

    private void ValidateExposedTools(QreProtocol.RuntimeModelRequest request)
    {
        var toolMap = new HashSet<string>(_tools.Select(static t => t.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in request.Tools)
        {
            if (!toolMap.Contains(descriptor.CanonicalName))
            {
                throw new QreProtocol.RuntimeModelClientException(new QreProtocol.RuntimeError(
                    QreProtocol.RuntimeErrorCategory.RuntimeInvariantViolation,
                    "codexflow_context_tool_missing",
                    $"Prepared C5 tool '{descriptor.CanonicalName}' has no host function."));
            }
        }
    }

    private async Task<QreProtocol.RuntimeModelRequest> PrepareEffectiveRequestAsync(
        QreProtocol.RuntimeModelRequest request,
        CancellationToken ct)
    {
        var dynamicContext = await ResolveDynamicContextAsync(request, ct).ConfigureAwait(false);
        dynamicContext = MergeProgressGuard(dynamicContext, request);

        var messages = NormalizeToolResults(request.Messages);
        var parameters = request.Parameters;

        if (dynamicContext != null)
        {
            var injected = CollectDynamicInjections(dynamicContext);
            if (injected.Count > 0)
            {
                messages = InsertAfterSystemMessages(messages, QreV2BackedQueryRuntimeEngine.ToProtocolMessages(injected));
            }

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

                // Write into request.Parameters so MeaiRuntimeModelClient derives RequireSpecific from it
                // instead of overwriting ChatOptions.ToolMode.
                parameters = parameters with { RequiredToolName = dynamicRequired };
            }
        }

        var deniedCalls = UnexposedToolCallGuard.TrailingDenials(request.Messages);
        if (UnexposedToolCallGuard.ShouldStop(deniedCalls))
        {
            _logger.LogWarning(
                "[UnexposedToolCalls] SessionId={SessionId} Entry={Entry} Denied={Count} Tools={Tools}; ending the turn.",
                _coreRequest.SessionId,
                _coreRequest.EntryPoint,
                deniedCalls.Count,
                QreV2BackedQueryRuntimeEngine.SafeLogText(
                    string.Join(",", deniedCalls.Distinct(StringComparer.OrdinalIgnoreCase)), 200));
            throw new QreProtocol.RuntimeModelClientException(new QreProtocol.RuntimeError(
                QreProtocol.RuntimeErrorCategory.ProviderProtocol,
                UnexposedToolCallGuard.StopCode,
                UnexposedToolCallGuard.StopMessage(deniedCalls)));
        }

        if (UnexposedToolCallGuard.Nudge(deniedCalls, request.Tools.Select(static tool => tool.CanonicalName)) is { } nudge)
        {
            var protocolNudge = QreV2BackedQueryRuntimeEngine.ToProtocolMessages([nudge]);
            messages = [.. messages, .. protocolNudge];
        }

        return request with
        {
            Messages = messages,
            Parameters = parameters
        };
    }

    private async ValueTask<QueryRuntimeDynamicContextResult?> ResolveDynamicContextAsync(
        QreProtocol.RuntimeModelRequest request,
        CancellationToken ct)
    {
        if (_coreRequest.DynamicContextProvider == null)
        {
            return null;
        }

        var state = new QueryRuntimeState
        {
            Round = ReadStepIndex(request.StepId),
            MaxRounds = Math.Max(1, _coreRequest.MaxRounds)
        };
        state.Messages.AddRange(QreV2BackedQueryRuntimeEngine.ToMeaiMessages(request.Messages));

        try
        {
            return await _coreRequest.DynamicContextProvider(
                new QueryRuntimeDynamicContextRequest(
                    _coreRequest,
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
            _logger.LogWarning(
                ex,
                "QRE v2 C5 dynamic context failed for session {SessionId} on round {Round}; continuing with canonical prepared context.",
                _coreRequest.SessionId,
                state.Round);
            return null;
        }
    }

    private QueryRuntimeDynamicContextResult? MergeProgressGuard(
        QueryRuntimeDynamicContextResult? fromProvider,
        QreProtocol.RuntimeModelRequest request)
    {
        if (_evidence == null)
        {
            return fromProvider;
        }

        var writeTool = ForgeProgressGuard.PickWriteTool(request.Tools.Select(static tool => tool.CanonicalName));
        var guard = ForgeProgressGuard.Build(
            _coreRequest.EntryPoint,
            _evidence.CallsSinceLastWrite,
            writeTool,
            _evidence.ForcedWriteRounds);
        if (guard == null)
        {
            return fromProvider;
        }

        if (guard.RequiredToolName != null && string.IsNullOrWhiteSpace(fromProvider?.RequiredToolName))
        {
            _evidence.NoteForcedWriteRound();
        }

        return fromProvider == null
            ? guard
            : fromProvider with
            {
                Messages = [.. fromProvider.Messages, .. guard.Messages],
                RequiredToolName = string.IsNullOrWhiteSpace(fromProvider.RequiredToolName)
                    ? guard.RequiredToolName
                    : fromProvider.RequiredToolName
            };
    }

    private static List<ChatMessage> CollectDynamicInjections(QueryRuntimeDynamicContextResult dynamicContext)
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

        return injected;
    }

    private static List<QreProtocol.RuntimeMessage> InsertAfterSystemMessages(
        IReadOnlyList<QreProtocol.RuntimeMessage> messages,
        QreProtocol.RuntimeMessage[] injected)
    {
        var result = new List<QreProtocol.RuntimeMessage>(messages.Count + injected.Length);
        result.AddRange(messages);
        var insertion = 0;
        while (insertion < result.Count && result[insertion].Role == QreProtocol.RuntimeMessageRole.System)
        {
            insertion++;
        }

        result.InsertRange(insertion, injected);
        return result;
    }

    private static IReadOnlyList<QreProtocol.RuntimeMessage> NormalizeToolResults(
        IReadOnlyList<QreProtocol.RuntimeMessage> messages)
    {
        List<QreProtocol.RuntimeMessage>? updated = null;
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            List<QreProtocol.RuntimeItem>? updatedItems = null;
            for (var j = 0; j < message.Items.Count; j++)
            {
                if (message.Items[j] is QreProtocol.RuntimeToolResultItem toolResultItem
                    && string.IsNullOrWhiteSpace(toolResultItem.Result.Text))
                {
                    updatedItems ??= [.. message.Items];
                    updatedItems[j] = new QreProtocol.RuntimeToolResultItem(
                        toolResultItem.Result with
                        {
                            Text = QreV2BackedQueryRuntimeEngine.ToolResultModelText(toolResultItem.Result)
                        });
                }
            }

            if (updatedItems != null)
            {
                updated ??= [.. messages];
                updated[i] = message with { Items = updatedItems };
            }
        }

        return updated ?? messages;
    }

    private static int ReadStepIndex(QreProtocol.RuntimeStepId stepId)
    {
        var separator = stepId.Value.LastIndexOf(':');
        return separator >= 0 && int.TryParse(stepId.Value[(separator + 1)..], out var index)
            ? index
            : 0;
    }
}
