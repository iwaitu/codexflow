// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Domain.Jobs;

namespace CodexFlow.Application.Jobs;

public interface IJobOutcomeStore
{
    Task<string?> FindAnnouncementConversationAsync(string sessionId, string userId, string? taskId, CancellationToken ct);
    Task RecordAnnouncementAsync(BackgroundJob job, string? messageId, string conversationId, DateTime now, CancellationToken ct);
}
