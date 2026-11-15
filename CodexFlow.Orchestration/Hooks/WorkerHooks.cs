// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Domain.Jobs;

namespace CodexFlow.Application.Hooks;

/// <summary>
/// Worker lifecycle hook for coordinator/worker job events.
/// </summary>
public interface IWorkerHook
{
    /// <summary>Called after a worker completes successfully.</summary>
    ValueTask<WorkerHookResult> OnWorkerCompletedAsync(WorkerCompletedContext context, CancellationToken ct = default);

    /// <summary>Called after a worker completes with failure.</summary>
    ValueTask<WorkerHookResult> OnWorkerFailedAsync(WorkerFailedContext context, CancellationToken ct = default);

    /// <summary>Called when a worker transitions into WaitingUser.</summary>
    ValueTask<WorkerHookResult> OnWorkerWaitingUserAsync(WorkerWaitingUserContext context, CancellationToken ct = default);
}

/// <summary>
/// Dispatches worker lifecycle hooks in a fixed order.
/// </summary>
public interface IWorkerHookDispatcher
{
    /// <summary>Dispatches the completed event to registered hooks.</summary>
    ValueTask<WorkerHookResult> DispatchWorkerCompletedAsync(WorkerCompletedContext context, CancellationToken ct = default);

    /// <summary>Dispatches the failed event to registered hooks.</summary>
    ValueTask<WorkerHookResult> DispatchWorkerFailedAsync(WorkerFailedContext context, CancellationToken ct = default);

    /// <summary>Dispatches the waiting-user event to registered hooks.</summary>
    ValueTask<WorkerHookResult> DispatchWorkerWaitingUserAsync(WorkerWaitingUserContext context, CancellationToken ct = default);
}

/// <summary>
/// Hook result for the current phase. Future phases may extend this contract.
/// </summary>
public sealed record WorkerHookResult
{
    /// <summary>The LLM-facing XML envelope to attach to the worker payload.</summary>
    public string? WorkerNotificationXml { get; init; }

    /// <summary>A no-op result.</summary>
    public static WorkerHookResult None { get; } = new();
}

/// <summary>
/// Base typed context for worker completion/failure events.
/// </summary>
public abstract record WorkerHookPayloadContext
{
    /// <summary>The worker/job that triggered the event.</summary>
    public required BackgroundJob Job { get; init; }

    /// <summary>The JSON payload associated with the lifecycle event.</summary>
    public required string PayloadJson { get; init; }
}

/// <summary>
/// Context for OnWorkerCompleted.
/// </summary>
public sealed record WorkerCompletedContext : WorkerHookPayloadContext;

/// <summary>
/// Context for OnWorkerFailed.
/// </summary>
public sealed record WorkerFailedContext : WorkerHookPayloadContext;

/// <summary>
/// Context for OnWorkerWaitingUser.
/// </summary>
public sealed record WorkerWaitingUserContext
{
    /// <summary>The worker/job that triggered the event.</summary>
    public required BackgroundJob Job { get; init; }

    /// <summary>The JSON payload associated with the lifecycle event.</summary>
    public required string PayloadJson { get; init; }

    /// <summary>The resume token issued for this waiting state.</summary>
    public required string ResumeToken { get; init; }

    /// <summary>The user-facing waiting reason.</summary>
    public required string Reason { get; init; }

    /// <summary>Optional supplemental waiting context.</summary>
    public string? Context { get; init; }
}
