// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application.Hooks;

namespace CodexFlow.Services.Hooks;

/// <summary>
/// Default worker-hook dispatcher with fail-log-and-continue semantics.
/// </summary>
public sealed class WorkerHookDispatcher : IWorkerHookDispatcher
{
    private readonly IReadOnlyList<IWorkerHook> _hooks;
    private readonly ILogger<WorkerHookDispatcher> _logger;

    /// <summary>
    /// Creates a dispatcher over the registered worker hooks.
    /// </summary>
    /// <param name="hooks">Registered worker hooks that should observe lifecycle events.</param>
    /// <param name="logger">Logger used when hooks fail or emit envelopes.</param>
    public WorkerHookDispatcher(IEnumerable<IWorkerHook> hooks, ILogger<WorkerHookDispatcher> logger)
    {
        _hooks = (hooks ?? throw new ArgumentNullException(nameof(hooks))).ToArray();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Dispatches a completed-worker event to all registered hooks and merges their results.
    /// </summary>
    /// <param name="context">Completed worker context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The aggregated hook result.</returns>
    public ValueTask<WorkerHookResult> DispatchWorkerCompletedAsync(WorkerCompletedContext context, CancellationToken ct = default)
        => DispatchAsync("OnWorkerCompleted", context.Job.JobId, hook => hook.OnWorkerCompletedAsync(context, ct));

    /// <summary>
    /// Dispatches a failed-worker event to all registered hooks and merges their results.
    /// </summary>
    /// <param name="context">Failed worker context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The aggregated hook result.</returns>
    public ValueTask<WorkerHookResult> DispatchWorkerFailedAsync(WorkerFailedContext context, CancellationToken ct = default)
        => DispatchAsync("OnWorkerFailed", context.Job.JobId, hook => hook.OnWorkerFailedAsync(context, ct));

    /// <summary>
    /// Dispatches a waiting-user event to all registered hooks and merges their results.
    /// </summary>
    /// <param name="context">Waiting-user worker context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The aggregated hook result.</returns>
    public ValueTask<WorkerHookResult> DispatchWorkerWaitingUserAsync(WorkerWaitingUserContext context, CancellationToken ct = default)
        => DispatchAsync("OnWorkerWaitingUser", context.Job.JobId, hook => hook.OnWorkerWaitingUserAsync(context, ct));

    private async ValueTask<WorkerHookResult> DispatchAsync(
        string eventName,
        string jobId,
        Func<IWorkerHook, ValueTask<WorkerHookResult>> invokeAsync)
    {
        WorkerHookResult? aggregated = null;

        foreach (var hook in _hooks)
        {
            var startedAt = DateTime.UtcNow;
            try
            {
                var result = await invokeAsync(hook).ConfigureAwait(false) ?? WorkerHookResult.None;
                var producedXml = !string.IsNullOrWhiteSpace(result.WorkerNotificationXml);
                _logger.LogInformation(
                    "Worker hook executed. Event={EventName} Hook={HookType} JobId={JobId} ProducedXml={ProducedXml} DurationMs={DurationMs}",
                    eventName,
                    hook.GetType().Name,
                    jobId,
                    producedXml,
                    (DateTime.UtcNow - startedAt).TotalMilliseconds);

                if (aggregated == null)
                {
                    aggregated = result;
                }
                else if (string.IsNullOrWhiteSpace(aggregated.WorkerNotificationXml) && producedXml)
                {
                    aggregated = aggregated with { WorkerNotificationXml = result.WorkerNotificationXml };
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Worker hook failed. Event={EventName} Hook={HookType} JobId={JobId}. Continuing.",
                    eventName,
                    hook.GetType().Name,
                    jobId);
            }
        }

        return aggregated ?? WorkerHookResult.None;
    }
}
