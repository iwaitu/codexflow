// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Execution;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Gateway.Contracts;
using CodexFlow.Gateway.Http;
using CodexFlow.Local;
using CodexFlow.Orchestration.Hosting;
using CodexFlow.Storage.Sqlite;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

#if CODEXFLOW_NATIVE_HOST
if (System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported ||
    System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault)
{
    throw new InvalidOperationException("The Native AOT host requires dynamic code and default JSON reflection to be disabled.");
}
#endif

var exportIdx = Array.FindIndex(args, a => string.Equals(a, "--export-contracts", StringComparison.OrdinalIgnoreCase));
if (exportIdx >= 0)
{
    var outputDir = exportIdx + 1 < args.Length
        ? Path.GetFullPath(args[exportIdx + 1])
        : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "docs", "contracts"));
    Directory.CreateDirectory(outputDir);
    var (openApiJson, sseSchemaJson) = await GatewayOpenApiExtensions.GenerateContractsAsync().ConfigureAwait(false);
    await File.WriteAllTextAsync(Path.Combine(outputDir, "openapi.json"), openApiJson + "\n").ConfigureAwait(false);
    await File.WriteAllTextAsync(Path.Combine(outputDir, "sse-events.schema.json"), sseSchemaJson + "\n").ConfigureAwait(false);
    return 0;
}

var builder = WebApplication.CreateSlimBuilder(args);

// Layered configuration: global ~/.codexflow/appsettings.json, then the project's codexflow.yml and
// .codexflow/codexflow.local.yml (only when the project is trusted, and only its "Project" section), then environment variables
// and the command line again on top. Enabled when CodexFlow:LayeredConfiguration is true, or in managed Pipe delivery mode.
var globalSettingsDirectory = CodexFlow.Core.Configuration.CodexFlowPaths.GlobalDirectory();
var globalSettingsPath = CodexFlow.Core.Configuration.CodexFlowPaths.GlobalSettingsPath(globalSettingsDirectory);
var executionSettingsService = new CodexFlow.Core.Configuration.ExecutionSettingsService(globalSettingsPath);
executionSettingsService.MigrateLegacyIsolationIfNeeded();

var deliveryModeEarly = Enum.TryParse<LocalTokenDeliveryMode>(
    builder.Configuration["Local:TokenDelivery"],
    ignoreCase: true,
    out var parsedEarlyDelivery)
    ? parsedEarlyDelivery
    : LocalTokenDeliveryMode.File;

if (builder.Configuration.GetValue<bool>("CodexFlow:LayeredConfiguration") || deliveryModeEarly == LocalTokenDeliveryMode.Pipe)
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
else
{
    // Even in standalone File delivery mode, watch ~/.codexflow/appsettings.json so control-plane updates
    // to model/execution/workspace settings take effect immediately without requiring a host restart.
    builder.Configuration.AddJsonFile(globalSettingsPath, optional: true, reloadOnChange: true);
    builder.Configuration.AddEnvironmentVariables();
    builder.Configuration.AddCommandLine(args);
}

var layout = LocalHostRuntime.ResolveLayout(builder.Configuration, globalSettingsDirectory);
var isolatedOverrides = new Dictionary<string, string?>();
if (layout.StagingRoot is not null && string.IsNullOrWhiteSpace(builder.Configuration["Isolation:StagingRoot"]))
{
    isolatedOverrides["Isolation:StagingRoot"] = layout.StagingRoot;
}

if (layout.DataDirectory is not null)
{
    var currentCacheDir = builder.Configuration["ArtifactAudit:Advisory:CacheDirectory"];
    if (string.IsNullOrWhiteSpace(currentCacheDir)
        || string.Equals(currentCacheDir, ".codexflow-data/nuget-advisories", StringComparison.OrdinalIgnoreCase))
    {
        isolatedOverrides["ArtifactAudit:Advisory:CacheDirectory"] = Path.Combine(layout.DataDirectory, "nuget-advisories");
    }
}

if (builder.Environment.IsDevelopment()
    && string.IsNullOrWhiteSpace(builder.Configuration["Runtime:QueryRuntime:AuditDirectory"]))
{
    isolatedOverrides["Runtime:QueryRuntime:AuditDirectory"] = layout.DataDirectory ?? layout.StateDirectory;
}

if (string.IsNullOrWhiteSpace(builder.Configuration["BusinessSkills:CredentialStoreDirectory"]))
{
    isolatedOverrides["BusinessSkills:CredentialStoreDirectory"] = Path.Combine(layout.CredentialDirectory, "business-plugins");
}

if (isolatedOverrides.Count > 0)
{
    builder.Configuration.AddInMemoryCollection(isolatedOverrides);
}

// Acquire single-instance exclusive lock on the data directory BEFORE touching state.db or credential files.
LocalInstanceGuard instanceGuard;
try
{
    instanceGuard = LocalInstanceGuard.Acquire(layout.InstanceLockDirectory);
}
catch (LocalInstanceAlreadyRunningException ex)
{
    await Console.Error.WriteLineAsync($"CodexFlow.Local failed to start: {ex.Message}").ConfigureAwait(false);
    if (!string.IsNullOrWhiteSpace(ex.ExistingInstanceJson))
    {
        await Console.Error.WriteLineAsync($"Active instance info: {ex.ExistingInstanceJson}").ConfigureAwait(false);
    }

    return 2;
}

using var instanceGuardLifetime = instanceGuard;

// Loopback only, whatever ASPNETCORE_URLS says: the local interface is for this machine. Supports Local:Port=0.
var configuredPort = builder.Configuration.GetValue<int?>("Local:Port") ?? 32586;
builder.WebHost.UseUrls($"http://127.0.0.1:{configuredPort}");

builder.Host.UseDefaultServiceProvider((context, options) =>
{
    options.ValidateScopes = true;
    options.ValidateOnBuild = true;
});

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
    : ExecutionBackend.Local;
var unsafeFileControlToken = LocalHostCredentials.ResolveUserLevelUnsafeFileControlToken(globalSettingsPath, args);

using var startupLoggerFactory = LoggerFactory.Create(lb => lb.AddConsole());
var startupLogger = startupLoggerFactory.CreateLogger("CodexFlow.Local.Startup");
var credentials = LocalHostCredentials.Initialize(
    layout.CredentialDirectory,
    deliveryMode,
    initialBackend,
    unsafeFileControlToken,
    Console.In,
    startupLogger);
var accessToken = new LocalAccessToken(credentials.AccessToken, credentials.AccessTokenPath ?? string.Empty);

builder.Services.AddCodexFlowGateway();
builder.Services.AddCodexFlowGatewayOpenApi();
builder.Services.AddHttpClient();
builder.Services.AddHealthChecks();

if (layout.AllowedOrigins.Length > 0)
{
    builder.Services.AddCors(options =>
    {
        options.AddPolicy(LocalHostRuntime.CorsPolicyName, policy =>
        {
            policy.WithOrigins(layout.AllowedOrigins)
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .WithExposedHeaders("X-Screenshot-Seq", "ETag");
        });
    });
}

builder.Services.AddSingleton(credentials);
builder.Services.AddSingleton<IControlPlaneStateProvider>(credentials);
builder.Services.AddSingleton<CodexFlow.Core.Remote.IRemoteBridgeAuthenticator>(credentials);
builder.Services.AddSingleton(accessToken);
builder.Services.AddSingleton<IWorkspaceRootProvider>(_ =>
    new WorkspaceRootProvider(layout.WorkspacesRoot, layout.DataDirectory));
builder.Services.AddSingleton<CodexFlow.Core.Configuration.IModelSettingsService>(_ =>
    new CodexFlow.Core.Configuration.ModelSettingsService(globalSettingsPath));
builder.Services.AddSingleton<CodexFlow.Core.Configuration.IExecutionSettingsService>(executionSettingsService);
builder.Services.AddSingleton<CodexFlow.Core.Configuration.IWorkspaceSettingsService>(_ =>
    new CodexFlow.Core.Configuration.WorkspaceSettingsService(globalSettingsPath));
builder.Services.AddSingleton<CodexFlow.Core.Configuration.IBrowserSettingsService>(_ =>
    new CodexFlow.Core.Configuration.BrowserSettingsService(globalSettingsPath));
builder.Services.AddSingleton<CodexFlow.Core.Configuration.IComputerSettingsService>(_ =>
    new CodexFlow.Core.Configuration.ComputerSettingsService(globalSettingsPath));

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
builder.Services.AddCodexFlowSqliteStorage(layout.SqlitePath);
builder.Services.AddCodexFlowLocalDefaults();

var app = builder.Build();

var execMonitor = app.Services.GetRequiredService<IOptionsMonitor<ExecutionOptions>>();
credentials.UpdateActiveBackend(execMonitor.CurrentValue.Backend);
using var backendWatch = execMonitor.OnChange(updated => credentials.UpdateActiveBackend(updated.Backend));

await app.Services.GetRequiredService<SqliteSchemaMigrator>().MigrateAsync();

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

if (layout.AllowedOrigins.Length > 0)
{
    app.UseCors(LocalHostRuntime.CorsPolicyName);
}

app.UseAuthentication();
app.UseAuthorization();
app.UseCodexFlowGatewayBindingErrors();
app.MapCodexFlowGateway();
if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Local:ExposeOpenApi"))
{
    app.MapCodexFlowGatewayOpenApi().RequireAuthorization("ActiveUser");
}
app.MapHealthChecks("/health");

using var parentWatcher = layout.ParentPid is > 0
    ? new ParentProcessWatcher(layout.ParentPid.Value, () => app.Lifetime.StopApplication(), app.Logger)
    : null;

await app.StartAsync();

var boundPort = LocalHostRuntime.ResolveBoundPort(app.Services, configuredPort);
app.Services.GetRequiredService<CodexFlow.Core.BusinessSkills.OutboundTargetPolicyGuard>().RegisterProtectedLocalPort(boundPort);
instanceGuard.WriteDiscovery(boundPort);

var readyLine = LocalHostRuntime.FormatReadyLine(boundPort, Environment.ProcessId, instanceGuard.InstanceId);
Console.Out.WriteLine(readyLine);
await Console.Out.FlushAsync();

app.Logger.LogInformation(
    "CodexFlow local host on http://127.0.0.1:{Port}. DataDirectory: {DataDirectory}. State: {StateDirectory}. Credentials: {CredentialDirectory}. TokenDelivery: {Delivery}. ControlPlaneAvailable: {ControlAvailable}. SelfApprovalProtection: {SelfProtection}.",
    boundPort,
    layout.DataDirectory ?? "(default)",
    layout.StateDirectory,
    layout.CredentialDirectory,
    credentials.TokenDeliveryMode,
    credentials.IsControlPlaneAvailable,
    credentials.SelfApprovalProtection);

await app.WaitForShutdownAsync();
return 0;
