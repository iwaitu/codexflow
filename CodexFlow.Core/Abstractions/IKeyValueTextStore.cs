// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace CodexFlow.Core.Abstractions;

/// <summary>
/// Small expiring text store for runtime state that does not deserve its own table: session payloads, task-list
/// snapshots. Redis backs it in the platform edition, a table of the project's SQLite file in the personal edition.
/// </summary>
public interface IKeyValueTextStore
{
    /// <summary>Returns the stored text, or <see langword="null"/> when the key is missing or has expired.</summary>
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    /// <summary>Stores text under a key, replacing any previous value; it expires after <paramref name="ttl"/>.</summary>
    Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>Removes a key. Removing a missing key is not an error.</summary>
    Task DeleteAsync(string key, CancellationToken ct = default);
}
