// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Execution.Approval;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace CodexFlow.Local;

public sealed record LocalHostLayout(
    string? DataDirectory,
    string SqlitePath,
    string StateDirectory,
    string CredentialDirectory,
    string WorkspacesRoot,
    string? StagingRoot,
    string InstanceLockDirectory,
    string[] AllowedOrigins,
    int? ParentPid);

public static class LocalHostRuntime
{
    public const string CorsPolicyName = "CodexFlowAllowedOrigins";

    public static LocalHostLayout ResolveLayout(IConfiguration configuration, string globalSettingsDirectory)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(globalSettingsDirectory);

        var configuredDataDir = configuration["Local:DataDirectory"];
        var dataDirectory = string.IsNullOrWhiteSpace(configuredDataDir)
            ? null
            : Path.GetFullPath(configuredDataDir);

        var configuredSqlite = configuration["Storage:SqlitePath"];
        var sqlitePath = !string.IsNullOrWhiteSpace(configuredSqlite)
            ? configuredSqlite
            : dataDirectory is not null
                ? Path.Combine(dataDirectory, "state.db")
                : Path.Combine(".codexflow", "state.db");

        var stateDirectory = Path.GetDirectoryName(Path.GetFullPath(sqlitePath))!;

        string credentialDirectory;
        if (dataDirectory is not null)
        {
            credentialDirectory = Path.Combine(dataDirectory, "credentials");
        }
        else
        {
            var stateHash = ExecutionRequest.Sha256Text(stateDirectory.ToLowerInvariant())[..16];
            credentialDirectory = Path.Combine(globalSettingsDirectory, "credentials", stateHash);
        }

        var workspacesRoot = dataDirectory is not null
            ? Path.Combine(dataDirectory, "workspaces")
            : Path.Combine(Directory.GetCurrentDirectory(), "workspaces");

        var configuredStaging = configuration["Isolation:StagingRoot"];
        var stagingRoot = !string.IsNullOrWhiteSpace(configuredStaging)
            ? Path.GetFullPath(configuredStaging)
            : dataDirectory is not null
                ? Path.Combine(dataDirectory, "staging")
                : null;

        var lockDirectory = dataDirectory ?? stateDirectory;

        var origins = configuration.GetSection("Local:AllowedOrigins")
            .GetChildren()
            .Select(c => c.Value?.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var parentPid = configuration.GetValue<int?>("Local:ParentPid");

        return new LocalHostLayout(
            DataDirectory: dataDirectory,
            SqlitePath: sqlitePath,
            StateDirectory: stateDirectory,
            CredentialDirectory: credentialDirectory,
            WorkspacesRoot: workspacesRoot,
            StagingRoot: stagingRoot,
            InstanceLockDirectory: lockDirectory,
            AllowedOrigins: origins,
            ParentPid: parentPid);
    }

    public static int ResolveBoundPort(IServiceProvider services, int fallbackPort)
    {
        var server = services.GetService<IServer>();
        var address = server?.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(address) && Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Port > 0)
        {
            return uri.Port;
        }

        return fallbackPort;
    }

    public static string FormatReadyLine(int port, int pid, string instanceId)
    {
        var payload = JsonSerializer.Serialize(new
        {
            port,
            pid,
            instanceId
        });
        return $"CODEXFLOW_READY {payload}";
    }
}
