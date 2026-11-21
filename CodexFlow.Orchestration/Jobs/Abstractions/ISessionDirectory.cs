// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Orchestration.Jobs;

/// <summary>The session facts a job is checked against before it runs.</summary>
public sealed record SessionRecord(string Id, string? UserId, string? WorkspacePath);

/// <summary>
/// Looks sessions up for the job runner. Sessions belong to the host (the platform keeps them next to its users; the local host
/// has a single user), so the runner asks through this interface and never reads a session table itself.
/// </summary>
public interface ISessionDirectory
{
    /// <summary>The session with this id, or null when there is none.</summary>
    Task<SessionRecord?> FindAsync(string sessionId, CancellationToken ct);
}
