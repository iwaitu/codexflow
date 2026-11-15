// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Application.Jobs
{
    /// <summary>
    /// Optional cache of the latest projected state of a job (status, sequence, waiting reason...). The database is
    /// always the source of truth; a hot view only makes reads cheaper and may be empty or absent.
    /// </summary>
    public interface IJobHotView
    {
        /// <summary>Returns the cached fields of a job, or an empty map when nothing is cached.</summary>
        Task<IReadOnlyDictionary<string, string>> GetAsync(string jobId);

        /// <summary>Returns the highest event sequence number applied to the cached view, if any.</summary>
        Task<long?> GetLatestSeqAsync(string jobId);

        /// <summary>Sets and removes fields of a job's cached view and refreshes its expiry.</summary>
        Task ApplyAsync(
            string jobId,
            IReadOnlyDictionary<string, string> set,
            IReadOnlyCollection<string> remove,
            TimeSpan ttl);

        /// <summary>Drops the cached views of jobs (for example when their session is deleted).</summary>
        Task DeleteAsync(IReadOnlyCollection<string> jobIds);
    }
}
