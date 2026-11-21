// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Domain.Planning;

namespace CodexFlow.Infrastructure.Persistence;

/// <summary>Persists plans and their current selection independently of the database provider.</summary>
public interface IPlanArtifactRepository
{
    Task<PlanArtifactEntity?> GetAsync(string planArtifactId, CancellationToken ct = default);
    Task<PlanArtifactEntity?> GetCurrentAsync(string sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<PlanArtifactEntity>> ListBySessionAsync(string sessionId, CancellationToken ct = default);

    /// <summary>Inserts a non-current plan or updates an existing plan without changing its selection.</summary>
    Task SaveAsync(PlanArtifactEntity artifact, CancellationToken ct = default);

    /// <summary>Selects the matching plan in this session. A missing selection clears the current plan for the session.</summary>
    Task SetCurrentAsync(string sessionId, string planArtifactId, CancellationToken ct = default);
}
