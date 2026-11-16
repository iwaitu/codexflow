// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Local;
using CodexFlow.Orchestration.Hosting;
using CodexFlow.Orchestration.Jobs;
using CodexFlow.Storage.Sqlite;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Layered configuration: global ~/.codexflow/appsettings.json, then the project's codexflow.yml and
// .codexflow/codexflow.local.yml (only when the project is trusted, and only its "Project" section), then environment variables
// and the command line again on top. Off unless CodexFlow:LayeredConfiguration is true.
var globalSettingsDirectory = CodexFlow.Core.Configuration.CodexFlowPaths.GlobalDirectory();
if (builder.Configuration.GetValue<bool>("CodexFlow:LayeredConfiguration"))
{
    var projectDirectory = builder.Configuration["CodexFlow:ProjectDirectory"] is { Length: > 0 } configuredProject
        ? Path.GetFullPath(configuredProject)
        : Directory.GetCurrentDirectory();
    CodexFlow.Core.Configuration.LayeredConfigurationExtensions.AddCodexFlowLayers(
        builder.Configuration,
        projectDirectory,
        trustProjectConfig: builder.Configuration.GetValue<bool>("CodexFlow:TrustProjectConfig"),
        globalDirectory: globalSettingsDirectory);
    builder.Configuration.AddEnvironmentVariables();
    builder.Configuration.AddCommandLine(args);
}

// Loopback only, whatever ASPNETCORE_URLS says: the local interface is for this machine.
var port = builder.Configuration.GetValue<int?>("Local:Port") ?? 5206;
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

builder.Host.UseDefaultServiceProvider((context, options) =>
{
    options.ValidateScopes = true;
    options.ValidateOnBuild = true;
});

var sqlitePath = builder.Configuration["Storage:SqlitePath"] is { Length: > 0 } configuredSqlitePath
    ? configuredSqlitePath
    : Path.Combine(".codexflow", "state.db");
var stateDirectory = Path.GetDirectoryName(Path.GetFullPath(sqlitePath))!;
var accessToken = LocalAccessToken.Create(stateDirectory);

builder.Services.AddControllers();
builder.Services.AddHttpClient();
builder.Services.AddHealthChecks();
builder.Services.AddSingleton(accessToken);
builder.Services.AddSingleton<CodexFlow.Core.Configuration.IModelSettingsService>(_ =>
    new CodexFlow.Core.Configuration.ModelSettingsService(
        CodexFlow.Core.Configuration.CodexFlowPaths.GlobalSettingsPath(globalSettingsDirectory)));
builder.Services.AddSingleton<CodexFlow.Core.Configuration.IExecutionSettingsService>(_ =>
    new CodexFlow.Core.Configuration.ExecutionSettingsService(
        CodexFlow.Core.Configuration.CodexFlowPaths.GlobalSettingsPath(globalSettingsDirectory)));

// The caller is whoever holds this host's token, and that caller is the local user. There is nothing to deactivate, so the
// "ActiveUser" policy the controllers ask for only requires that.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddScheme<AuthenticationSchemeOptions, LocalTokenAuthenticationHandler>(JwtBearerDefaults.AuthenticationScheme, null);
builder.Services.AddAuthorization(options => options.AddPolicy("ActiveUser", policy => policy.RequireAuthenticatedUser()));

builder.Services.AddCodexFlowModelClient(builder.Configuration);
builder.Services.AddCodexFlowRuntime(builder.Configuration);
builder.Services.AddCodexFlowSqliteStorage(sqlitePath);
builder.Services.AddCodexFlowLocalDefaults();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<JobDbContext>().Database.MigrateAsync();
}

app.Services.GetRequiredService<CodexFlow.Core.Configuration.IExecutionSettingsService>()
    .MigrateLegacyIsolationIfNeeded(app.Logger);

var preflightMode = HostPreflight.ResolvePreflightMode(app.Configuration, app.Logger);
if (preflightMode != LocalPreflightMode.Off)
{
    var issues = await HostPreflight.RunAsync(app.Configuration);
    if (issues.Count > 0)
    {
        foreach (var issue in issues)
        {
            app.Logger.LogWarning("Start-up check issue ({Component}): {Message}", issue.Component, issue.Message);
        }

        if (preflightMode == LocalPreflightMode.Fail)
        {
            await Console.Error.WriteLineAsync("CodexFlow cannot start:" + string.Concat(issues.Select(issue => $"{Environment.NewLine}  - {issue.Message}")));
            return 1;
        }
    }
}

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");

app.Logger.LogInformation(
    "CodexFlow local host on http://127.0.0.1:{Port}. State: {StateDirectory}. Access token: {TokenPath}.",
    port, stateDirectory, accessToken.Path);
await app.RunAsync();
return 0;
