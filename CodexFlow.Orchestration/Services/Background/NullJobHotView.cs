// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application.Jobs;

namespace CodexFlow.Services.Background
{
    /// <summary>An <see cref="IJobHotView"/> that caches nothing: reads fall back to the database.</summary>
    public sealed class NullJobHotView : IJobHotView
    {
        private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

        public Task<IReadOnlyDictionary<string, string>> GetAsync(string jobId) => Task.FromResult(Empty);

        public Task<long?> GetLatestSeqAsync(string jobId) => Task.FromResult<long?>(null);

        public Task ApplyAsync(string jobId, IReadOnlyDictionary<string, string> set, IReadOnlyCollection<string> remove, TimeSpan ttl)
            => Task.CompletedTask;

        public Task DeleteAsync(IReadOnlyCollection<string> jobIds) => Task.CompletedTask;
    }
}
