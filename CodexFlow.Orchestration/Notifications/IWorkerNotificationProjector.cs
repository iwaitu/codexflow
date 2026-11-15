// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Protocols;
using CodexFlow.Domain.Jobs;

namespace CodexFlow.Application.Notifications;

public interface IWorkerNotificationProjector
{
    string ProjectJobNotification(BackgroundJob job, string? resultPayloadJson = null);
    string ProjectWaitingUserNotification(BackgroundJob job, string resumeToken, string reason, string? context = null);
    string ProjectVerificationReport(string taskId, string jobId, ValidationResult validationResult, string? context = null, WorkerUsageInfo? usage = null);
}
