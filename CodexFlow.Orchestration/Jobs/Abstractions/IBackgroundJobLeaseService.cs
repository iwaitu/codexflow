// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Domain.Jobs;

namespace CodexFlow.Application.Jobs
{
    public interface IBackgroundJobLeaseService
    {
        Task<(string JobId, string ClaimToken)?> TryClaimNextAsync(string runnerId, CancellationToken ct);
        Task<bool> RenewLeaseAsync(string jobId, string claimToken, CancellationToken ct);
        Task ReleaseAsync(string jobId, string claimToken);
        Task MarkExpiredJobsAsync();
    }
}
