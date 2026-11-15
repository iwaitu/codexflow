// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Models.Jobs;

namespace CodexFlow.Application.Jobs
{
    /// <summary>
    /// Append-only history of the events of background jobs, keyed by event id.
    /// </summary>
    public interface IJobEventLog
    {
        /// <summary>
        /// Stores an event. Storing an event whose <see cref="JobEventLogEntry.EventId"/> already exists
        /// replaces it, so projecting the same outbox event twice is harmless.
        /// </summary>
        Task AppendAsync(JobEventLogEntry entry, CancellationToken ct = default);

        /// <summary>
        /// Returns the events of a job ordered by <see cref="JobEventLogEntry.Seq"/>, optionally only
        /// those with a sequence number greater than <paramref name="afterSeq"/>.
        /// </summary>
        Task<IReadOnlyList<JobEventLogEntry>> GetByJobAsync(string jobId, long? afterSeq = null, CancellationToken ct = default);

        /// <summary>
        /// Deletes every event that belongs to a session.
        /// </summary>
        Task DeleteBySessionAsync(string sessionId, CancellationToken ct = default);
    }
}
