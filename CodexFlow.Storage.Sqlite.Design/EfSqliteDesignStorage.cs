// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Orchestration.Jobs;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CodexFlow.Storage.Sqlite;

/// <summary>Opens the historical EF model for design-time operations and interoperability tests.</summary>
public static class EfSqliteDesignStorage
{
    public static DbContextOptionsBuilder UseLegacySqliteFile(this DbContextOptionsBuilder options, string databasePath)
    {
        var path = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 30
        }.ToString();

        return options.UseSqlite(connectionString, sqlite => sqlite.MigrationsAssembly(typeof(EfSqliteDesignStorage).Assembly.GetName().Name))
            .ReplaceService<IModelCacheKeyFactory, ProviderModelCacheKeyFactory>()
            .AddInterceptors(new OutboxSeqInterceptor());
    }

    public static IServiceCollection AddLegacySqliteContext(this IServiceCollection services, string databasePath)
    {
        services.AddDbContext<JobDbContext>(options => options.UseLegacySqliteFile(databasePath));
        services.AddScoped<IJobDbContext>(sp => sp.GetRequiredService<JobDbContext>());
        return services;
    }
}
