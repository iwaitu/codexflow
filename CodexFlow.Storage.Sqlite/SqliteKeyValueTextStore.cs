// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using CodexFlow.Core.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CodexFlow.Storage.Sqlite;

/// <summary>
/// <see cref="IKeyValueTextStore"/> in the <c>LocalKv</c> table of the project's SQLite file (created by the
/// <c>LocalKv</c> migration). Expiry is an ISO-8601 UTC text column, so it compares as text.
/// </summary>
public sealed class SqliteKeyValueTextStore : IKeyValueTextStore
{
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    private readonly CodexFlow.Orchestration.Jobs.IJobDbContext _context;

    public SqliteKeyValueTextStore(CodexFlow.Orchestration.Jobs.IJobDbContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var now = Format(DateTime.UtcNow);
        var rows = await _context.Database
            .SqlQuery<string>($@"SELECT ""Value"" AS ""Value"" FROM ""LocalKv"" WHERE ""Key"" = {key} AND ""ExpiresAtUtc"" > {now}")
            .ToListAsync(ct);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public async Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default)
    {
        var expires = Format(DateTime.UtcNow.Add(ttl));
        await _context.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO ""LocalKv"" (""Key"", ""Value"", ""ExpiresAtUtc"") VALUES ({key}, {value}, {expires})
ON CONFLICT(""Key"") DO UPDATE SET ""Value"" = excluded.""Value"", ""ExpiresAtUtc"" = excluded.""ExpiresAtUtc""", ct);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        await _context.Database.ExecuteSqlInterpolatedAsync($@"DELETE FROM ""LocalKv"" WHERE ""Key"" = {key}", ct);
    }

    private static string Format(DateTime utc) => utc.ToUniversalTime().ToString(TimeFormat, CultureInfo.InvariantCulture);
}
