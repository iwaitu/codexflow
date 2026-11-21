// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Orchestration.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CodexFlow.Storage.Sqlite;

/// <summary>Creates the SQLite context for the EF command-line tools.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<JobDbContext>
{
    public JobDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<JobDbContext>();
        builder.UseLegacySqliteFile(Path.Combine(Path.GetTempPath(), "codexflow-local-design-time.db"));
        return new JobDbContext(builder.Options);
    }
}
