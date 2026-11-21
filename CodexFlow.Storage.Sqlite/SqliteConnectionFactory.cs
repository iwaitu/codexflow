// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Microsoft.Data.Sqlite;

namespace CodexFlow.Storage.Sqlite;

/// <summary>Opens a pooled connection to the Local state database.</summary>
public sealed class SqliteConnectionFactory
{
    private readonly string _databasePath;
    private readonly string _connectionString;

    public SqliteConnectionFactory(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 30
        }.ToString();
    }

    /// <summary>Opens a connection owned by the caller.</summary>
    public SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
