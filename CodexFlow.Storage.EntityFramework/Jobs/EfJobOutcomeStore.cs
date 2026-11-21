// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application.Jobs;
using CodexFlow.Domain.Chat;
using CodexFlow.Domain.Jobs;
using Microsoft.EntityFrameworkCore;

namespace CodexFlow.Orchestration.Jobs;

public sealed class EfJobOutcomeStore(IJobDbContext db) : IJobOutcomeStore
{
    public Task<string?> FindAnnouncementConversationAsync(string sessionId, string userId, string? taskId, CancellationToken ct)
    {
        var useWorker = !string.IsNullOrWhiteSpace(taskId);
        return db.Conversations.AsNoTracking().Where(conversation => conversation.SessionId == sessionId &&
                conversation.UserId == userId && !conversation.IsArchived &&
                (conversation.ThreadType == ConversationThreadType.Master || conversation.ThreadType == ConversationThreadType.Chat ||
                    (useWorker && conversation.ThreadType == ConversationThreadType.Worker && conversation.TaskId == taskId)))
            .OrderBy(conversation => conversation.ThreadType == ConversationThreadType.Worker ? 0 :
                conversation.ThreadType == ConversationThreadType.Master ? 1 : 2)
            .ThenByDescending(conversation => conversation.UpdateAt).Select(conversation => (string?)conversation.Id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task RecordAnnouncementAsync(BackgroundJob job, string? messageId, string conversationId, DateTime now, CancellationToken ct)
        => await db.BackgroundJobs.Where(row => row.JobId == job.JobId && row.Attempt == job.Attempt && row.Status == job.Status &&
                row.NotificationStatus != NotificationStatus.SummaryDelivered && row.NotificationStatus != NotificationStatus.SyncDelivered &&
                row.NotificationStatus != NotificationStatus.Suppressed)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.NotificationStatus, NotificationStatus.SummaryDelivered)
                .SetProperty(row => row.NotificationChannels, "main_session").SetProperty(row => row.SummaryMessageId, messageId)
                .SetProperty(row => row.SummaryConversationId, conversationId).SetProperty(row => row.Notified, true)
                .SetProperty(row => row.NotifiedAtUtc, now).SetProperty(row => row.UpdatedAtUtc, now), ct).ConfigureAwait(false);
}
