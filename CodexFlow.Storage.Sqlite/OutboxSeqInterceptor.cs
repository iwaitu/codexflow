// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Data;
using System.Data.Common;
using CodexFlow.Domain.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace CodexFlow.Storage.Sqlite;

/// <summary>
/// Gives every new <see cref="OutboxEvent"/> the next number of the <c>OutboxSeq</c> counter. SQLite cannot
/// auto-number a column that is not the primary key, and the outbox projector relies on <c>Seq</c> increasing.
/// The counter row is updated with a single atomic statement, so contexts racing each other never share a number;
/// a failed save may leave a gap, which is harmless.
/// </summary>
public sealed class OutboxSeqInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        AssignAsync(eventData.Context, async: false, CancellationToken.None).GetAwaiter().GetResult();
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await AssignAsync(eventData.Context, async: true, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async Task AssignAsync(DbContext? context, bool async, CancellationToken ct)
    {
        if (context is null)
        {
            return;
        }

        var pending = context.ChangeTracker.Entries<OutboxEvent>()
            .Where(entry => entry.State == EntityState.Added && entry.Entity.Seq == 0)
            .ToList();
        if (pending.Count == 0)
        {
            return;
        }

        var connection = context.Database.GetDbConnection();
        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed)
        {
            if (async) await connection.OpenAsync(ct).ConfigureAwait(false); else connection.Open();
        }

        try
        {
            foreach (var entry in pending)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE \"OutboxSeq\" SET \"Value\" = \"Value\" + 1 WHERE \"Id\" = 1 RETURNING \"Value\";";
                command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
                var value = async
                    ? await command.ExecuteScalarAsync(ct).ConfigureAwait(false)
                    : command.ExecuteScalar();
                entry.Entity.Seq = Convert.ToInt64(value);
            }
        }
        finally
        {
            if (wasClosed)
            {
                if (async) await connection.CloseAsync().ConfigureAwait(false); else connection.Close();
            }
        }
    }
}
