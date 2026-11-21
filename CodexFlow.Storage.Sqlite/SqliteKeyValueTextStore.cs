// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using CodexFlow.Core.Abstractions;

namespace CodexFlow.Storage.Sqlite;

/// <summary>
/// <see cref="IKeyValueTextStore"/> in the <c>LocalKv</c> table. Expiry is stored and compared as UTC text.
/// </summary>
public sealed class SqliteKeyValueTextStore(SqliteConnectionFactory connections) : IKeyValueTextStore
{
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    /// <inheritdoc />
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var now = Format(DateTime.UtcNow);
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """SELECT "Value" FROM "LocalKv" WHERE "Key" = $key AND "ExpiresAtUtc" > $now""";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$now", now);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    /// <inheritdoc />
    public async Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var expires = Format(DateTime.UtcNow.Add(ttl));
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "LocalKv" ("Key", "Value", "ExpiresAtUtc") VALUES ($key, $value, $expires)
            ON CONFLICT("Key") DO UPDATE SET "Value" = excluded."Value", "ExpiresAtUtc" = excluded."ExpiresAtUtc"
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$expires", expires);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = connections.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """DELETE FROM "LocalKv" WHERE "Key" = $key""";
        command.Parameters.AddWithValue("$key", key);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string Format(DateTime utc) => utc.ToUniversalTime().ToString(TimeFormat, CultureInfo.InvariantCulture);
}
