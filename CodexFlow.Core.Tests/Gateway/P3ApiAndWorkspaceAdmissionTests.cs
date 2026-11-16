// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CodexFlow.Application;
using CodexFlow.Application.Jobs;
using CodexFlow.Controllers;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Configuration;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Core.Gateway;
using CodexFlow.Core.Models;
using CodexFlow.Core.Services;
using CodexFlow.Local;
using CodexFlow.Orchestration.Hosting;
using CodexFlow.Orchestration.Jobs;
using CodexFlow.Storage.Sqlite;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.Gateway;

public sealed class P3ApiAndWorkspaceAdmissionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codexflow-p3-api-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            LocalExecutionService.TryDeleteDirectory(_root);
        }
    }

    [Fact]
    public async Task GetSystemStatus_ReportsBackendAvailabilityModelReachabilityAndPreflightIssues()
    {
        var globalSettings = Path.Combine(_root, "global", "appsettings.json");
        var preflightMock = new Mock<IHostPreflightService>();
        preflightMock
            .Setup(p => p.GetStatusAsync(ExecutionBackend.Local, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostPreflightStatus(
                ModelConfigured: true,
                ModelReachable: false,
                ModelProbeStatus: "Unreachable",
                ActiveBackendAvailable: true,
                ActiveBackendReason: null,
                DockerAvailable: false,
                DockerReason: "docker daemon stopped",
                PreflightMode: LocalPreflightMode.Report,
                Issues: [new PreflightIssue("Model", "Connection refused")],
                CheckedAtUtc: DateTimeOffset.UtcNow));

        await using var host = await P3TestHost.StartAsync(
            _root,
            globalSettings,
            ExecutionBackend.Local,
            ExecutionPermissionMode.Ask,
            preflightService: preflightMock.Object);

        using var client = host.CreateNormalClient();
        var status = await client.GetFromJsonAsync<SystemStatusSummaryDto>("/api/system/status");

        Assert.NotNull(status);
        Assert.Equal("Local", status!.ExecutionBackend);
        Assert.Equal("Ask", status.PermissionMode);
        Assert.True(status.BackendAvailable);
        Assert.False(status.DockerAvailable);
        Assert.Equal("docker daemon stopped", status.DockerAvailabilityReason);
        Assert.True(status.ModelConfigured);
        Assert.False(status.ModelReachable);
        Assert.Equal("Unreachable", status.ModelProbeStatus);
        Assert.Equal("Report", status.PreflightMode);
        var issue = Assert.Single(status.PreflightIssues!);
        Assert.Equal("Model", issue.Component);
        Assert.Equal("Connection refused", issue.Message);
    }

    [Fact]
    public async Task ModelSettings_GetMasksApiKey_AndPutRequiresControlPlaneAndPersistsAtomically()
    {
        var globalSettings = Path.Combine(_root, "global", "appsettings.json");
        await using var host = await P3TestHost.StartAsync(
            _root,
            globalSettings,
            ExecutionBackend.Local,
            ExecutionPermissionMode.Ask);

        using var normalClient = host.CreateNormalClient();
        using var controlClient = host.CreateControlClient();

        // Normal plane cannot mutate model endpoint or key (403)
        var forbiddenResp = await normalClient.PutAsJsonAsync("/api/settings/model", new UpdateModelSettingsRequest(
            ApiUrl: "https://api.example.com/v1/{1}",
            ApiMode: "ChatCompletions",
            Model: "gpt-test",
            ApiKey: "sk-super-secret-9876"));
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenResp.StatusCode);

        // Control plane updates settings
        var putResp = await controlClient.PutAsJsonAsync("/api/settings/model", new UpdateModelSettingsRequest(
            ApiUrl: "https://api.example.com/v1/{1}",
            ApiMode: "ChatCompletions",
            Model: "gpt-test",
            MaxTokensLength: 64000,
            Temperature: 0.2f,
            TopP: 0.9f,
            ApiKey: "sk-super-secret-9876"));
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);
        var updated = await putResp.Content.ReadFromJsonAsync<ModelSettingsDto>();
        Assert.NotNull(updated);
        Assert.Equal("https://api.example.com/v1/{1}", updated!.ApiUrl);
        Assert.Equal("gpt-test", updated.Model);
        Assert.True(updated.ApiKeySet);
        Assert.Equal("9876", updated.ApiKeyLast4);

        // Normal plane GET never leaks the full secret
        var getResp = await normalClient.GetAsync("/api/settings/model");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var rawJson = await getResp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("sk-super-secret", rawJson, StringComparison.Ordinal);
        var fetched = await getResp.Content.ReadFromJsonAsync<ModelSettingsDto>();
        Assert.Equal("9876", fetched!.ApiKeyLast4);
    }

    [Fact]
    public void LocalWorkspaceAdmissionPolicy_RejectsRootsSystemDirsUserHomeDataDirGlobalDirAndLinks()
    {
        var dataDir = Path.Combine(_root, "data-dir");
        var globalDir = Path.Combine(_root, "home", ".codexflow");
        var userHome = Path.Combine(_root, "home");
        var sysDir = Path.Combine(_root, "sysroot");
        var validProject = Path.Combine(_root, "home", "projects", "my-app");
        var dataChild = Path.Combine(dataDir, "workspaces");

        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(dataChild);
        Directory.CreateDirectory(globalDir);
        Directory.CreateDirectory(sysDir);
        Directory.CreateDirectory(validProject);

        var env = new WorkspaceAdmissionEnvironment(
            DataDirectory: dataDir,
            GlobalDirectory: globalDir,
            UserHomeDirectory: userHome,
            SystemDirectories: [sysDir]);

        // Root / drive root
        var fsRoot = Path.GetPathRoot(validProject)!;
        Assert.False(LocalWorkspaceAdmissionPolicy.Evaluate(fsRoot, env).Allowed);

        // System directory and its subdirectory
        Assert.Equal(WorkspaceAdmissionDenial.SystemDirectory, LocalWorkspaceAdmissionPolicy.Evaluate(sysDir, env).Reason);
        var sysSub = Path.Combine(sysDir, "sub");
        Directory.CreateDirectory(sysSub);
        Assert.Equal(WorkspaceAdmissionDenial.SystemDirectory, LocalWorkspaceAdmissionPolicy.Evaluate(sysSub, env).Reason);

        // User home itself
        Assert.Equal(WorkspaceAdmissionDenial.UserHomeDirectory, LocalWorkspaceAdmissionPolicy.Evaluate(userHome, env).Reason);

        // DataDirectory, ~/.codexflow, descendant, and ancestor of DataDirectory
        Assert.Equal(WorkspaceAdmissionDenial.ProtectedCodexFlowDirectory, LocalWorkspaceAdmissionPolicy.Evaluate(dataDir, env).Reason);
        Assert.Equal(WorkspaceAdmissionDenial.ProtectedCodexFlowDirectory, LocalWorkspaceAdmissionPolicy.Evaluate(dataChild, env).Reason);
        Assert.Equal(WorkspaceAdmissionDenial.ProtectedCodexFlowDirectory, LocalWorkspaceAdmissionPolicy.Evaluate(globalDir, env).Reason);
        Assert.Equal(WorkspaceAdmissionDenial.ProtectedCodexFlowDirectory, LocalWorkspaceAdmissionPolicy.Evaluate(_root, env).Reason);

        // Dot-dot traversal into protected dir
        var dotDotIntoGlobal = Path.Combine(validProject, "..", "..", ".codexflow");
        Assert.Equal(WorkspaceAdmissionDenial.ProtectedCodexFlowDirectory, LocalWorkspaceAdmissionPolicy.Evaluate(dotDotIntoGlobal, env).Reason);

        // Junction / Symlink pointing into protected directory
        var linkToGlobal = Path.Combine(_root, "link-to-global");
        if (TryCreateDirectoryLinkOrJunction(linkToGlobal, globalDir))
        {
            var linkDecision = LocalWorkspaceAdmissionPolicy.Evaluate(linkToGlobal, env);
            Assert.False(linkDecision.Allowed);
        }

        // Valid project subdirectory is allowed
        var validDecision = LocalWorkspaceAdmissionPolicy.Evaluate(validProject, env);
        Assert.True(validDecision.Allowed);
        Assert.Equal(Path.GetFullPath(validProject), validDecision.CanonicalPath);
    }

    [Fact]
    public async Task OpenLocalWorkspace_RequiresControlPlane_PersistsAllowedRoot_Audits_AndPreservesDiskOnDelete()
    {
        var globalSettings = Path.Combine(_root, "global", "appsettings.json");
        var projectDir = Path.Combine(_root, "external-repo");
        var sentinelFile = Path.Combine(projectDir, "README.md");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(sentinelFile, "do not delete me");

        await using var host = await P3TestHost.StartAsync(
            _root,
            globalSettings,
            ExecutionBackend.Local,
            ExecutionPermissionMode.Ask);

        using var normalClient = host.CreateNormalClient();
        using var controlClient = host.CreateControlClient();

        // Normal token -> 403 Forbidden
        var normalAttempt = await normalClient.PostAsJsonAsync("/api/gateway/workspaces", new OpenLocalWorkspaceRequest(projectDir, "My Repo"));
        Assert.Equal(HttpStatusCode.Forbidden, normalAttempt.StatusCode);

        // Forbidden path -> 400 BadRequest
        var forbiddenPath = await controlClient.PostAsJsonAsync("/api/gateway/workspaces", new OpenLocalWorkspaceRequest(Path.GetPathRoot(projectDir)!));
        Assert.Equal(HttpStatusCode.BadRequest, forbiddenPath.StatusCode);

        // Control token opens valid external directory
        var openResp = await controlClient.PostAsJsonAsync("/api/gateway/workspaces", new OpenLocalWorkspaceRequest(projectDir, "My Repo"));
        Assert.Equal(HttpStatusCode.OK, openResp.StatusCode);
        var opened = await openResp.Content.ReadFromJsonAsync<OpenLocalWorkspaceResponse>();
        Assert.NotNull(opened);
        Assert.True(opened!.Created);
        Assert.True(opened.AddedToAllowedRoots);
        Assert.Equal("My Repo", opened.WorkspaceName);
        Assert.Equal(Path.GetFullPath(projectDir), opened.WorkspacePath);

        // Persisted in AllowedLocalRepositoryRoots
        var roots = host.WorkspaceSettings.ReadAllowedLocalRepositoryRoots();
        Assert.Contains(Path.GetFullPath(projectDir), roots);

        // Recorded in audit store
        var audits = await host.AuditStore.ListAsync(opened.SessionId, "workspace_opened", CancellationToken.None);
        Assert.Single(audits);

        // Idempotent second open reuses existing session
        var reopenResp = await controlClient.PostAsJsonAsync("/api/gateway/workspaces", new OpenLocalWorkspaceRequest(projectDir));
        Assert.Equal(HttpStatusCode.OK, reopenResp.StatusCode);
        var reopened = await reopenResp.Content.ReadFromJsonAsync<OpenLocalWorkspaceResponse>();
        Assert.False(reopened!.Created);
        Assert.Equal(opened.SessionId, reopened.SessionId);

        // Deleting external workspace session removes DB record but NEVER deletes external folder on disk!
        var delResp = await normalClient.DeleteAsync($"/api/gateway/session/{opened.SessionId}");
        Assert.Equal(HttpStatusCode.OK, delResp.StatusCode);
        Assert.True(File.Exists(sentinelFile));
    }

    [Fact]
    public async Task SessionAndConversationManagement_SupportsPagingRenameArchiveDeleteAndOwnershipChecks()
    {
        var globalSettings = Path.Combine(_root, "global", "appsettings.json");
        await using var host = await P3TestHost.StartAsync(
            _root,
            globalSettings,
            ExecutionBackend.Local,
            ExecutionPermissionMode.Ask);

        using var client = host.CreateNormalClient();

        // Create default workspace + 2 managed workspaces
        var bootResp = await client.PostAsJsonAsync("/api/gateway/session/bootstrap", new BootstrapRequest());
        Assert.Equal(HttpStatusCode.OK, bootResp.StatusCode);
        var defaultSession = (await bootResp.Content.ReadFromJsonAsync<SessionBootstrapResponse>())!;

        var s1Id = (await (await client.PostAsync("/api/gateway/session?name=Alpha", null)).Content.ReadFromJsonAsync<Dictionary<string, string>>())!["sessionId"];
        var s2Id = (await (await client.PostAsync("/api/gateway/session?name=Beta", null)).Content.ReadFromJsonAsync<Dictionary<string, string>>())!["sessionId"];

        // Default workspace protected from rename/archive/delete
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsJsonAsync($"/api/gateway/session/{defaultSession.SessionId}", new UpdateSessionRequest(Name: "X"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"/api/gateway/session/{defaultSession.SessionId}")).StatusCode);

        // Rename & archive s1
        var patchS1 = await client.PatchAsJsonAsync($"/api/gateway/session/{s1Id}", new UpdateSessionRequest(Name: "Alpha Renamed", IsArchived: true));
        Assert.Equal(HttpStatusCode.OK, patchS1.StatusCode);
        var s1Dto = (await patchS1.Content.ReadFromJsonAsync<SessionSummaryDto>())!;
        Assert.Equal("Alpha Renamed", s1Dto.Name);
        Assert.True(s1Dto.IsArchived);

        // List active sessions excludes archived s1; includeArchived=true includes it
        var activePage = (await client.GetFromJsonAsync<PagedResponseDto<SessionSummaryDto>>("/api/gateway/sessions?page=1&pageSize=10&includeArchived=false"))!;
        Assert.DoesNotContain(activePage.Items, x => x.SessionId == s1Id);
        Assert.Contains(activePage.Items, x => x.SessionId == s2Id);

        var allPage = (await client.GetFromJsonAsync<PagedResponseDto<SessionSummaryDto>>("/api/gateway/sessions?page=1&pageSize=10&includeArchived=true"))!;
        Assert.Contains(allPage.Items, x => x.SessionId == s1Id);

        // Create conversations directly via ChatService for s2 (and a foreign conversation owned by another user)
        string conv1, conv2, foreignConv;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var chat = scope.ServiceProvider.GetRequiredService<IChatService>();
            conv1 = await chat.CreateConversationInSession(LocalUser.Id, s2Id, name: "Thread 1");
            conv2 = await chat.CreateConversationInSession(LocalUser.Id, s2Id, name: "Thread 2");
            var foreignSession = await chat.CreateSession("other-user", "Other");
            foreignConv = await chat.CreateConversationInSession("other-user", foreignSession, name: "Secret");
        }

        // Ownership enforcement (404 on foreign session / conversation)
        Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync($"/api/gateway/conversations/{foreignConv}", new UpdateConversationRequest(Name: "Hack"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/gateway/conversations/{foreignConv}")).StatusCode);

        // Rename & archive conv1, paginate conversations
        var patchConv = await client.PatchAsJsonAsync($"/api/gateway/conversations/{conv1}", new UpdateConversationRequest(Name: "Thread 1 Updated", IsArchived: true));
        Assert.Equal(HttpStatusCode.OK, patchConv.StatusCode);
        var activeConvs = (await client.GetFromJsonAsync<PagedResponseDto<ConversationSummaryDto>>($"/api/gateway/session/{s2Id}/conversations?page=1&pageSize=10&includeArchived=false"))!;
        var onlyConv = Assert.Single(activeConvs.Items);
        Assert.Equal(conv2, onlyConv.ConversationId);

        // Delete conv2 & s2 (internal workspace folder gets cleaned up)
        string s2WorkspacePath;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var chat = scope.ServiceProvider.GetRequiredService<IChatService>();
            s2WorkspacePath = (await chat.GetSession(s2Id))!.WorkspacePath;
        }
        Assert.True(Directory.Exists(s2WorkspacePath));
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/gateway/conversations/{conv2}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/gateway/session/{s2Id}")).StatusCode);
        Assert.False(Directory.Exists(s2WorkspacePath));
    }

    [Fact]
    public async Task LocalWorkspaceAdmission_RejectsAncestorLinkProtectedDir_DefaultReservedName_AndAuditsSelfApprovalProtection()
    {
        var realProtected = Path.Combine(_root, "real-data-root");
        var realSubData = Path.Combine(realProtected, "sub-data");
        var repoUnderReal = Path.Combine(realSubData, "repo");
        Directory.CreateDirectory(repoUnderReal);

        var aliasParent = Path.Combine(_root, "alias-data-parent");
        if (TryCreateDirectoryLinkOrJunction(aliasParent, realProtected))
        {
            var env = new WorkspaceAdmissionEnvironment(
                DataDirectory: Path.Combine(aliasParent, "sub-data"),
                GlobalDirectory: Path.Combine(_root, "global-dir"));
            var decision = LocalWorkspaceAdmissionPolicy.Evaluate(repoUnderReal, env);
            Assert.False(decision.Allowed);
            Assert.Equal(WorkspaceAdmissionDenial.ProtectedCodexFlowDirectory, decision.Reason);
        }

        // Control plane POST /api/gateway/workspaces rejects reserved default name & logs selfApprovalProtection
        var globalSettings = Path.Combine(_root, "global-reserved", "appsettings.json");
        await using var host = await P3TestHost.StartAsync(
            _root,
            globalSettings,
            ExecutionBackend.Local,
            ExecutionPermissionMode.Ask);
        using var controlClient = host.CreateControlClient();

        var externalRepo = Path.Combine(_root, "external-reserved-check");
        Directory.CreateDirectory(externalRepo);
        var reservedResp = await controlClient.PostAsJsonAsync(
            "/api/gateway/workspaces",
            new OpenLocalWorkspaceRequest(externalRepo, Name: "默认工作区"));
        Assert.Equal(HttpStatusCode.BadRequest, reservedResp.StatusCode);

        var okResp = await controlClient.PostAsJsonAsync(
            "/api/gateway/workspaces",
            new OpenLocalWorkspaceRequest(externalRepo, Name: "Valid External"));
        Assert.Equal(HttpStatusCode.OK, okResp.StatusCode);
        var opened = await okResp.Content.ReadFromJsonAsync<OpenLocalWorkspaceResponse>();
        var audits = await host.AuditStore.ListAsync(opened!.SessionId, "workspace_opened", CancellationToken.None);
        var openedAudit = Assert.Single(audits);
        Assert.Contains("selfApprovalProtection", openedAudit.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GlobalSettingsServices_ConcurrentUpdatesAcrossModelExecutionAndWorkspace_DoNotLoseWrites()
    {
        var globalSettings = Path.Combine(_root, "concurrent-global", "appsettings.json");
        var modelSvc = new ModelSettingsService(globalSettings);
        var execSvc = new ExecutionSettingsService(globalSettings);
        var wsSvc = new WorkspaceSettingsService(globalSettings);

        var tasks = new List<Task>();
        for (var i = 0; i < 15; i++)
        {
            var idx = i;
            tasks.Add(Task.Run(() => modelSvc.Update(new ModelSettingsUpdate(
                ApiUrl: "https://api.example.com/v1",
                Model: $"model-{idx}"))));
            tasks.Add(Task.Run(() => execSvc.Update(new ExecutionSettingsUpdate(
                Backend: idx % 2 == 0 ? ExecutionBackend.Local : ExecutionBackend.Docker,
                PermissionMode: ExecutionPermissionMode.Ask))));
            var rootPath = Path.Combine(_root, $"ws-root-{idx}");
            Directory.CreateDirectory(rootPath);
            tasks.Add(Task.Run(() => wsSvc.AddAllowedLocalRepositoryRoot(rootPath)));
        }

        await Task.WhenAll(tasks);

        var jsonText = await File.ReadAllTextAsync(globalSettings);
        using var doc = System.Text.Json.JsonDocument.Parse(jsonText);
        Assert.True(doc.RootElement.TryGetProperty("VllmAgent", out var vllm));
        Assert.StartsWith("model-", vllm.GetProperty("Model").GetString(), StringComparison.Ordinal);
        Assert.True(doc.RootElement.TryGetProperty("Execution", out _));
        Assert.Equal(15, wsSvc.ReadAllowedLocalRepositoryRoots().Count);

        // N3: External sidecar OS lock file serializes cross-process mutations
        var lockFile = Path.Combine(Path.GetDirectoryName(globalSettings)!, ".appsettings.json.lock");
        Assert.True(File.Exists(lockFile));
        var updateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task concurrentUpdateTask;
        using (var externalOsLock = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            concurrentUpdateTask = Task.Run(() =>
            {
                updateEntered.TrySetResult();
                modelSvc.Update(new ModelSettingsUpdate(Model: "post-cross-process-lock"));
            });
            await updateEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(80);
            Assert.False(concurrentUpdateTask.IsCompleted);
        }

        await concurrentUpdateTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("post-cross-process-lock", modelSvc.Read().Model);
    }

    [Fact]
    public async Task HostPreflightService_UsesEffectiveBackendOverlay_DistinguishesResponsesNotProbed_AndCoalescesProbes()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Execution:Backend"] = "Docker",
                ["Local:Preflight"] = "Report",
                ["VllmAgent:ApiUrl"] = "https://mock-llm.invalid/v1",
                ["VllmAgent:ApiKey"] = "sk-test-1234567890",
                ["VllmAgent:Model"] = "gpt-test",
                ["VllmAgent:ApiMode"] = "Responses"
            })
            .Build();

        var service = new HostPreflightService(cfg);
        var responsesStatus = await service.GetStatusAsync(ExecutionBackend.Local, forceRefresh: true);
        Assert.True(responsesStatus.ModelConfigured);
        Assert.Null(responsesStatus.ModelReachable);
        Assert.Equal("NotProbed", responsesStatus.ModelProbeStatus);
        Assert.DoesNotContain(responsesStatus.Issues, i => string.Equals(i.Component, "ExecutionBackend", StringComparison.OrdinalIgnoreCase));

        // ChatCompletions mode uses httpHandler & coalesces concurrent calls
        var calls = 0;
        var handler = new CountingHttpMessageHandler(async () =>
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(50);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[]}") };
        });

        var chatCfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Execution:Backend"] = "Local",
                ["VllmAgent:ApiUrl"] = "https://mock-llm.invalid/v1",
                ["VllmAgent:ApiKey"] = "sk-test-1234567890",
                ["VllmAgent:Model"] = "gpt-test",
                ["VllmAgent:ApiMode"] = "ChatCompletions"
            })
            .Build();

        var chatService = new HostPreflightService(chatCfg, httpHandler: handler);
        var concurrent = await Task.WhenAll(
            chatService.GetStatusAsync(ExecutionBackend.Local, forceRefresh: true),
            chatService.GetStatusAsync(ExecutionBackend.Local, forceRefresh: true));
        Assert.All(concurrent, s =>
        {
            Assert.True(s.ModelReachable);
            Assert.Equal("Reachable", s.ModelProbeStatus);
        });
        Assert.Equal(1, calls);

        // Unconfigured Execution:Backend defaults to Local, so HostPreflight.RunAsync does not fail without Docker.
        var defaultPreflightCfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["VllmAgent:ApiUrl"] = "https://mock-llm.invalid/v1",
                ["VllmAgent:ApiKey"] = "sk-test-1234567890",
                ["VllmAgent:Model"] = "gpt-test",
                ["VllmAgent:ApiMode"] = "Responses"
            })
            .Build();
        var defaultIssues = await HostPreflight.RunAsync(
            defaultPreflightCfg,
            dockerProbe: _ => Task.FromResult<string?>("docker unavailable"));
        Assert.Empty(defaultIssues);

        var dockerPreflightCfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Execution:Backend"] = "Docker",
                ["VllmAgent:ApiUrl"] = "https://mock-llm.invalid/v1",
                ["VllmAgent:ApiKey"] = "sk-test-1234567890",
                ["VllmAgent:Model"] = "gpt-test",
                ["VllmAgent:ApiMode"] = "Responses"
            })
            .Build();
        var dockerIssues = await HostPreflight.RunAsync(
            dockerPreflightCfg,
            dockerProbe: _ => Task.FromResult<string?>("docker unavailable"));
        Assert.Contains(dockerIssues, i => string.Equals(i.Component, "Docker", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class CountingHttpMessageHandler(Func<Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond();
    }

    private static bool TryCreateDirectoryLinkOrJunction(string linkPath, string targetPath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var proc = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                proc?.WaitForExit(3000);
                return Directory.Exists(linkPath);
            }

            Directory.CreateSymbolicLink(linkPath, targetPath);
            return Directory.Exists(linkPath);
        }
        catch
        {
            return false;
        }
    }
}

internal sealed class P3TestHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private P3TestHost(
        WebApplication app,
        Uri baseUri,
        LocalHostCredentials credentials,
        WorkspaceSettingsService workspaceSettings,
        InMemoryWorkflowAuditStore auditStore,
        ExecutionApprovalBroker broker,
        ICodexGateway gateway,
        IPlanArtifactStore planStore)
    {
        _app = app;
        BaseUri = baseUri;
        Credentials = credentials;
        WorkspaceSettings = workspaceSettings;
        AuditStore = auditStore;
        Broker = broker;
        Gateway = gateway;
        PlanStore = planStore;
    }

    public Uri BaseUri { get; }
    public LocalHostCredentials Credentials { get; }
    public WorkspaceSettingsService WorkspaceSettings { get; }
    public InMemoryWorkflowAuditStore AuditStore { get; }
    public ExecutionApprovalBroker Broker { get; }
    public ICodexGateway Gateway { get; }
    public IPlanArtifactStore PlanStore { get; }
    public IServiceProvider Services => _app.Services;

    public HttpClient CreateNormalClient()
    {
        var c = new HttpClient { BaseAddress = BaseUri };
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Credentials.AccessToken);
        return c;
    }

    public HttpClient CreateControlClient()
    {
        var c = new HttpClient { BaseAddress = BaseUri };
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Credentials.ControlToken);
        return c;
    }

    public static async Task<P3TestHost> StartAsync(
        string root,
        string globalSettingsPath,
        ExecutionBackend backend,
        ExecutionPermissionMode mode,
        IHostPreflightService? preflightService = null,
        IGatewayMessageProcessor? customProcessor = null,
        IGitService? gitService = null,
        IJobViewService? jobViewService = null,
        Action<IServiceCollection, IConfiguration>? configureServices = null)
    {
        var dataDir = Path.Combine(root, "host-data");
        var credDir = Path.Combine(dataDir, "creds");
        var workspacesRoot = Path.Combine(dataDir, "workspaces");
        var dbPath = Path.Combine(dataDir, "state.db");
        Directory.CreateDirectory(workspacesRoot);

        using var pipe = new StringReader(System.Text.Json.JsonSerializer.Serialize(new
        {
            accessToken = "p3-normal-secret-token-11111111",
            controlToken = "p3-control-secret-token-22222222"
        }) + "\n");

        var creds = LocalHostCredentials.Initialize(
            credDir,
            LocalTokenDeliveryMode.Pipe,
            backend,
            unsafeFileControlToken: false,
            pipeReader: pipe);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var optionsMonitor = new CodexFlow.Core.Tests.Execution.ExecutionApprovalAndPermissionTests.MutableOptionsMonitor<ExecutionOptions>(
            new ExecutionOptions { Backend = backend, PermissionMode = mode });
        var clamp = new ExecutionPermissionClamp();
        var permStore = new ExecutionPermissionStore(Path.Combine(Path.GetDirectoryName(globalSettingsPath)!, "permissions.json"));
        var execSettings = new ExecutionSettingsService(globalSettingsPath);
        var modelSettings = new ModelSettingsService(globalSettingsPath);
        var workspaceSettings = new WorkspaceSettingsService(globalSettingsPath);
        var auditStore = new InMemoryWorkflowAuditStore();

        builder.Services.AddControllers().AddApplicationPart(typeof(CodexGatewayController).Assembly);
        builder.Services.AddSingleton(creds);
        builder.Services.AddSingleton<IControlPlaneStateProvider>(creds);
        builder.Services.AddSingleton<IOptionsMonitor<ExecutionOptions>>(optionsMonitor);
        builder.Services.AddSingleton(clamp);
        builder.Services.AddSingleton<IExecutionPermissionStore>(permStore);
        builder.Services.AddSingleton<IExecutionSettingsService>(execSettings);
        builder.Services.AddSingleton<IModelSettingsService>(modelSettings);
        builder.Services.AddSingleton<IWorkspaceSettingsService>(workspaceSettings);
        builder.Services.AddSingleton<IWorkspaceRootProvider>(new WorkspaceRootProvider(workspacesRoot, dataDir));
        builder.Services.AddSingleton<IWorkflowAuditStore>(auditStore);
        builder.Services.AddSingleton<IPlanArtifactStore, InMemoryPlanArtifactStore>();
        builder.Services.AddScoped<ITaskListService>(sp =>
            new CodexFlow.Infrastructure.Services.RedisTaskListService(
                sp.GetRequiredService<IKeyValueTextStore>(),
                NullLogger<CodexFlow.Infrastructure.Services.RedisTaskListService>.Instance));

        if (preflightService is not null)
        {
            builder.Services.AddSingleton(preflightService);
        }

        builder.Services.AddSingleton<IExecutionApprovalBroker>(sp => new ExecutionApprovalBroker(
            optionsMonitor,
            permStore,
            NullLogger<ExecutionApprovalBroker>.Instance,
            gateway: sp.GetRequiredService<ICodexGateway>(),
            auditStore: auditStore,
            controlPlane: creds));

        builder.Services.AddCodexFlowSqliteStorage(dbPath);
        builder.Services.AddCodexFlowLocalDefaults();
        builder.Services.AddScoped<CodexSessionManager>();
        builder.Services.AddScoped<IProjectMemoryService, ProjectMemoryService>();
        builder.Services.AddSingleton<ICodexGateway, global::CodexFlow.Gateway.CodexGateway>();

        if (customProcessor is not null)
        {
            builder.Services.AddScoped(_ => customProcessor);
        }
        else
        {
            builder.Services.AddScoped<IGatewayMessageProcessor>(_ => Mock.Of<IGatewayMessageProcessor>());
        }

        if (gitService is not null)
        {
            builder.Services.AddSingleton(gitService);
        }
        else
        {
            builder.Services.AddScoped<IGitService, GitService>();
        }

        if (jobViewService is not null)
        {
            builder.Services.AddScoped(_ => jobViewService);
        }

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddScheme<AuthenticationSchemeOptions, LocalTokenAuthenticationHandler>(JwtBearerDefaults.AuthenticationScheme, null);
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("ActiveUser", p => p.RequireAuthenticatedUser());
            options.AddPolicy(
                ControlPlaneAuthConstants.ControlPlanePolicy,
                p => p.RequireAuthenticatedUser().RequireClaim(ControlPlaneAuthConstants.PlaneClaimType, ControlPlaneAuthConstants.PlaneControl));
        });

        configureServices?.Invoke(builder.Services, builder.Configuration);

        var app = builder.Build();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<JobDbContext>().Database.MigrateAsync();
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();

        var baseUri = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
        return new P3TestHost(
            app,
            baseUri,
            creds,
            workspaceSettings,
            auditStore,
            (ExecutionApprovalBroker)app.Services.GetRequiredService<IExecutionApprovalBroker>(),
            app.Services.GetRequiredService<ICodexGateway>(),
            app.Services.GetRequiredService<IPlanArtifactStore>());
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
    }
}
