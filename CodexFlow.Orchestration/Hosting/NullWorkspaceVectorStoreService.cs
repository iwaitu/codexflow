// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Models;

namespace CodexFlow.Application;

/// <summary>
/// <see cref="IWorkspaceVectorStoreService"/> with semantic recall switched off, for hosts without Qdrant and a
/// text-embedding service: no collection is created and nothing is synced.
/// </summary>
public sealed class NullWorkspaceVectorStoreService : IWorkspaceVectorStoreService
{
    public Task EnsureCollectionAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<WorkspaceVectorSyncResult> SyncSessionWithResultAsync(CodexSession session, CancellationToken cancellationToken = default)
        => Task.FromResult(new WorkspaceVectorSyncResult(VectorSyncStatusCodes.SkippedNoDocuments, 0));

    public Task SyncSessionAsync(CodexSession session, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task DeleteCollectionAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
