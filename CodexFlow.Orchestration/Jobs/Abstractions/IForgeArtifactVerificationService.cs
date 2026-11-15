// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Contracts;

namespace CodexFlow.Application.Jobs;

public interface IForgeArtifactVerificationService
{
    Task<ForgeArtifactVerificationReceipt> VerifyAsync(string userId, VerifyForgeArtifactRequest request, CancellationToken ct);
}
