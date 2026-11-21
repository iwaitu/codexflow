// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace CodexFlow.Storage.Sqlite;

/// <summary>
/// A context that maps the job tables builds a slightly different model on SQLite (text instead of jsonb, no auto-numbered
/// <c>Seq</c>). EF caches one model per context type by default, so SQLite contexts get their own cache entry.
/// </summary>
public sealed class ProviderModelCacheKeyFactory(ModelCacheKeyFactoryDependencies dependencies)
    : ModelCacheKeyFactory(dependencies)
{
    public override object Create(DbContext context, bool designTime)
        => (context.GetType(), context.Database.ProviderName, designTime);
}
