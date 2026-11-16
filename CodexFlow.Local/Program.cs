// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Execution;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Local;
using CodexFlow.Orchestration.Hosting;
using CodexFlow.Orchestration.Jobs;
using CodexFlow.Storage.Sqlite;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Layered configuration: global ~/.codexflow/appsettings.json, then the project's codexflow.yml and
// .codexflow/codexflow.local.yml (only when the project is trusted, and only its "Project" section), then environment variables
// and the command line again on top. Off unless CodexFlow:LayeredConfiguration is true.
var globalSettingsDirectory = CodexFlow.Core.Configuration.CodexFlowPaths.GlobalDirectory();
var globalSettingsPath = CodexFlow.Core.Configuration.CodexFlowPaths.GlobalSettingsPath(globalSettingsDirectory);
var executionSettingsService = new CodexFlow.Core.Configuration.ExecutionSettingsService(globalSettingsPath);
executionSettingsService.MigrateLegacyIsolationIfNeeded();

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

// Keep credential token files outside executable project workspaces by default so neither container snapshots
// nor workspace tools ever share a directory with host/control tokens (B2).
string credentialDirectory;
if (builder.Configuration["Local:DataDirectory"] is { Length: > 0 } configuredDataDir)
{
    credentialDirectory = Path.GetFullPath(configuredDataDir);
}
else
{
    var stateHash = ExecutionRequest.Sha256Text(stateDirectory.ToLowerInvariant())[..16];
    credentialDirectory = Path.Combine(globalSettingsDirectory, "credentials", stateHash);
}

var deliveryMode = Enum.TryParse<LocalTokenDeliveryMode>(
    builder.Configuration["Local:TokenDelivery"],
    ignoreCase: true,
    out var parsedDelivery)
    ? parsedDelivery
    : LocalTokenDeliveryMode.File;
var initialBackend = Enum.TryParse<ExecutionBackend>(
    builder.Configuration[$"{ExecutionOptions.SectionName}:Backend"],
    ignoreCase: true,
    out var parsedBackend)
    ? parsedBackend
    : ExecutionBackend.Docker;
var unsafeFileControlToken = LocalHostCredentials.ResolveUserLevelUnsafeFileControlToken(globalSettingsPath, args);

using var startupLoggerFactory = LoggerFactory.Create(lb => lb.AddConsole());
var startupLogger = startupLoggerFactory.CreateLogger("CodexFlow.Local.Startup");
var credentials = LocalHostCredentials.Initialize(
    credentialDirectory,
    deliveryMode,
    initialBackend,
    unsafeFileControlToken,
    Console.In,
    startupLogger);
var accessToken = new LocalAccessToken(credentials.AccessToken, credentials.AccessTokenPath ?? string.Empty);

builder.Services.AddControllers();
builder.Services.AddHttpClient();
builder.Services.AddHealthChecks();
builder.Services.AddSingleton(credentials);
builder.Services.AddSingleton<IControlPlaneStateProvider>(credentials);
builder.Services.AddSingleton(accessToken);
builder.Services.AddSingleton<CodexFlow.Core.Configuration.IModelSettingsService>(_ =>
    new CodexFlow.Core.Configuration.ModelSettingsService(globalSettingsPath));
builder.Services.AddSingleton<CodexFlow.Core.Configuration.IExecutionSettingsService>(executionSettingsService);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddScheme<AuthenticationSchemeOptions, LocalTokenAuthenticationHandler>(JwtBearerDefaults.AuthenticationScheme, null);
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ActiveUser", policy => policy.RequireAuthenticatedUser());
    options.AddPolicy(
        ControlPlaneAuthConstants.ControlPlanePolicy,
        policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim(ControlPlaneAuthConstants.PlaneClaimType, ControlPlaneAuthConstants.PlaneControl));
});

builder.Services.AddCodexFlowModelClient(builder.Configuration);
builder.Services.AddCodexFlowRuntime(builder.Configuration);
builder.Services.AddCodexFlowSqliteStorage(sqlitePath);
builder.Services.AddCodexFlowLocalDefaults();

var app = builder.Build();

var execMonitor = app.Services.GetRequiredService<IOptionsMonitor<ExecutionOptions>>();
credentials.UpdateActiveBackend(execMonitor.CurrentValue.Backend);
using var backendWatch = execMonitor.OnChange(updated => credentials.UpdateActiveBackend(updated.Backend));

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<JobDbContext>().Database.MigrateAsync();
}

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
    "CodexFlow local host on http://127.0.0.1:{Port}. State: {StateDirectory}. Credentials: {CredentialDirectory}. TokenDelivery: {Delivery}. ControlPlaneAvailable: {ControlAvailable}. SelfApprovalProtection: {SelfProtection}.",
    port,
    stateDirectory,
    credentialDirectory,
    credentials.TokenDeliveryMode,
    credentials.IsControlPlaneAvailable,
    credentials.SelfApprovalProtection);
await app.RunAsync();
return 0;
