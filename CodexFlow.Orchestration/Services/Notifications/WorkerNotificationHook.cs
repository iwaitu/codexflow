// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application.Hooks;
using CodexFlow.Application.Notifications;

namespace CodexFlow.Services.Notifications;

/// <summary>
/// Worker hook that projects lifecycle notifications into XML envelopes.
/// </summary>
public sealed class WorkerNotificationHook : IWorkerHook
{
    private readonly IWorkerNotificationProjector _projector;

    /// <summary>
    /// Creates the worker notification hook.
    /// </summary>
    /// <param name="projector">Projector used to transform worker lifecycle results into notification XML.</param>
    public WorkerNotificationHook(IWorkerNotificationProjector projector)
    {
        _projector = projector ?? throw new ArgumentNullException(nameof(projector));
    }

    /// <summary>
    /// Projects a completed worker result into a notification payload.
    /// </summary>
    /// <param name="context">Completed worker context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A hook result that may contain a worker notification envelope.</returns>
    public ValueTask<WorkerHookResult> OnWorkerCompletedAsync(WorkerCompletedContext context, CancellationToken ct = default)
        => ValueTask.FromResult(new WorkerHookResult
        {
            WorkerNotificationXml = _projector.ProjectJobNotification(context.Job, context.PayloadJson)
        });

    /// <summary>
    /// Projects a failed worker result into a notification payload.
    /// </summary>
    /// <param name="context">Failed worker context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A hook result that may contain a worker notification envelope.</returns>
    public ValueTask<WorkerHookResult> OnWorkerFailedAsync(WorkerFailedContext context, CancellationToken ct = default)
        => ValueTask.FromResult(new WorkerHookResult
        {
            WorkerNotificationXml = _projector.ProjectJobNotification(context.Job, context.PayloadJson)
        });

    /// <summary>
    /// Projects a waiting-user worker state into a notification payload.
    /// </summary>
    /// <param name="context">Waiting-user worker context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A hook result that may contain a worker notification envelope.</returns>
    public ValueTask<WorkerHookResult> OnWorkerWaitingUserAsync(WorkerWaitingUserContext context, CancellationToken ct = default)
        => ValueTask.FromResult(new WorkerHookResult
        {
            WorkerNotificationXml = _projector.ProjectWaitingUserNotification(
                context.Job,
                context.ResumeToken,
                context.Reason,
                context.Context)
        });
}
