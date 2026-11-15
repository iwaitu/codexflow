// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Models;

namespace CodexFlow.Application;

/// <summary>
/// Named status codes for <see cref="WorkspaceVectorSyncResult.Status"/>.
/// Phase 0: formalises the observable failure categories for the vector sync pipeline.
/// </summary>
public static class VectorSyncStatusCodes
{
    /// <summary>No eligible documents were found for this session; sync was skipped.</summary>
    public const string SkippedNoDocuments = "skipped_no_documents";

    /// <summary>The embedding service returned an error or an unexpected vector count.</summary>
    public const string EmbeddingFailed = "embedding_failed";

    /// <summary>The returned embedding vectors had inconsistent or zero dimensions.</summary>
    public const string InvalidEmbeddingDimensions = "invalid_embedding_dimensions";

    /// <summary>The Qdrant upsert request returned a non-success status code.</summary>
    public const string QdrantUpsertFailed = "qdrant_upsert_failed";

    /// <summary>All documents were embedded and upserted successfully.</summary>
    public const string Success = "success";
}

public sealed record WorkspaceVectorSyncResult(
    string Status,
    int DocumentCount,
    string? CollectionName = null,
    string? Error = null);

public interface IWorkspaceVectorStoreService
{
    Task EnsureCollectionAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<WorkspaceVectorSyncResult> SyncSessionWithResultAsync(CodexSession session, CancellationToken cancellationToken = default);
    Task SyncSessionAsync(CodexSession session, CancellationToken cancellationToken = default);
    Task DeleteCollectionAsync(string sessionId, CancellationToken cancellationToken = default);
}
