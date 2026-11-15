// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Application.Jobs.Dtos;

namespace CodexFlow.Application.Jobs
{
    public interface IJobViewService
    {
        Task<BackgroundJobDto?> GetViewAsync(string jobId);
        Task<IEnumerable<BackgroundJobDto>> GetSessionJobsAsync(string sessionId);
        Task<IEnumerable<CodexFlow.Core.Models.Jobs.JobEventLogEntry>> GetHistoryAsync(string jobId, long? afterSeq = null);
    }
}
