// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using CodexFlow.Application;
using CodexFlow.Application.Jobs;
using CodexFlow.Controllers;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Configuration;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Core.Models;
using CodexFlow.Domain.Chat;
using CodexFlow.Domain.Jobs;
using CodexFlow.Local;
using CodexFlow.Orchestration.Hosting;
using CodexFlow.Orchestration.Jobs;
using CodexFlow.Services.Background;
using CodexFlow.Storage.Sqlite;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.Execution;

public sealed class ControlPlaneAndSelfApprovalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codexflow-p2-ctrl-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            LocalExecutionService.TryDeleteDirectory(_root);
        }
    }

    [Fact]
    public async Task ManagedPipeMode_LeavesNoTokenFilesOrEnvVars_AndLocalSubprocessCannotReadCredentials()
    {
        var stateDir = Path.Combine(_root, "pipe-state");
        Directory.CreateDirectory(stateDir);
        // Even if stale files existed from an old run, Pipe initialization removes them.
        File.WriteAllText(Path.Combine(stateDir, LocalHostCredentials.AccessTokenFileName), "stale");
        File.WriteAllText(Path.Combine(stateDir, LocalHostCredentials.ControlTokenFileName), "stale");

        const string normalSecret = "pipe-normal-credential-00001111";
        const string controlSecret = "pipe-control-credential-22223333";
        using var pipeInput = new StringReader(JsonSerializer.Serialize(new
        {
            accessToken = normalSecret,
            controlToken = controlSecret
        }) + "\n");

        var creds = LocalHostCredentials.Initialize(
            stateDir,
            LocalTokenDeliveryMode.Pipe,
            ExecutionBackend.Local,
            unsafeFileControlToken: false,
            pipeReader: pipeInput);

        Assert.True(creds.IsControlPlaneAvailable);
        Assert.True(creds.SelfApprovalProtection);
        Assert.Equal("Pipe", creds.TokenDeliveryMode);
        Assert.False(File.Exists(Path.Combine(stateDir, LocalHostCredentials.AccessTokenFileName)));
        Assert.False(File.Exists(Path.Combine(stateDir, LocalHostCredentials.ControlTokenFileName)));

        // Run a real local subprocess in LocalExecutionService and inspect its environment & state directory
        var ws = Path.Combine(_root, "ws-pipe");
        var stg = Path.Combine(_root, "stg-pipe");
        Directory.CreateDirectory(ws);
        using var localSvc = new LocalExecutionService(
            Options.Create(new IsolationOptions { StagingRoot = stg }),
            NullLogger<LocalExecutionService>.Instance);

        var open = await localSvc.OpenSessionAsync(
            new TrustedExecutionContext
            {
                UserId = "local",
                SessionId = "s-pipe",
                JobId = "j1",
                TaskId = "t1",
                AttemptId = "a1",
                WorkspaceRoot = ws,
                Network = new ExecutionNetworkPolicy(ExecutionNetworkMode.Host)
            },
            new IsolatedSessionOptions("local"),
            CancellationToken.None);
        await using var session = open.Session!;

        var dumpCmd = OperatingSystem.IsWindows()
            ? new[] { "dotnet", "--info" }
            : new[] { "env" };
        var run = await session.RunAsync(new IsolatedCommandRequest(dumpCmd), CancellationToken.None);
        Assert.DoesNotContain(normalSecret, run.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(controlSecret, run.Stdout, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(stateDir, "*.token"));
    }

    [Fact]
    public async Task StandaloneLocalMode_Default_WritesNoControlToken_EnforcesWindowsAcl_AndFailsFastWithApprovalUnavailable()
    {
        var stateDir = Path.Combine(_root, "standalone-local");
        var ws = Path.Combine(_root, "ws-standalone");
        var stg = Path.Combine(_root, "stg-standalone");
        Directory.CreateDirectory(ws);

        // Verify project-level codexflow.yml CANNOT enable UnsafeFileControlToken
        var projectDir = Path.Combine(_root, "untrusted-repo");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "codexflow.yml"), "Local:\n  UnsafeFileControlToken: true\n");
        var missingGlobalSettings = Path.Combine(_root, "global-empty", "appsettings.json");
        Assert.False(LocalHostCredentials.ResolveUserLevelUnsafeFileControlToken(missingGlobalSettings, []));

        var creds = LocalHostCredentials.Initialize(
            stateDir,
            LocalTokenDeliveryMode.File,
            ExecutionBackend.Local,
            unsafeFileControlToken: false);

        var hostTokenPath = Path.Combine(stateDir, LocalHostCredentials.AccessTokenFileName);
        var controlTokenPath = Path.Combine(stateDir, LocalHostCredentials.ControlTokenFileName);

        Assert.True(File.Exists(hostTokenPath));
        Assert.False(File.Exists(controlTokenPath));
        Assert.False(creds.IsControlPlaneAvailable);
        Assert.True(creds.SelfApprovalProtection);

        if (OperatingSystem.IsWindows())
        {
            var currentSid = WindowsIdentity.GetCurrent().User!;
            var acl = new FileInfo(hostTokenPath).GetAccessControl();
            Assert.True(acl.AreAccessRulesProtected);
            var rules = acl.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .ToArray();
            Assert.Single(rules);
            Assert.Equal(currentSid, rules[0].IdentityReference);
        }

        // Ask mode fails immediately with approval_unavailable and zero runner invocations!
        var runnerCalls = 0;
        using var localSvc = new LocalExecutionService(
            Options.Create(new IsolationOptions { StagingRoot = stg }),
            NullLogger<LocalExecutionService>.Instance,
            processRunner: (_, _) =>
            {
                Interlocked.Increment(ref runnerCalls);
                return Task.FromResult(new HostProcessResult(true, 0, "", "", false, false, false, null));
            });

        var opts = new ExecutionApprovalAndPermissionTests.MutableOptionsMonitor<ExecutionOptions>(new ExecutionOptions
        {
            Backend = ExecutionBackend.Local,
            PermissionMode = ExecutionPermissionMode.Ask
        });
        var permStore = new ExecutionPermissionStore(Path.Combine(_root, "perms-standalone.json"));
        var broker = new ExecutionApprovalBroker(
            opts,
            permStore,
            NullLogger<ExecutionApprovalBroker>.Instance,
            controlPlane: creds);
        var accessor = new TrustedExecutionContextAccessor();
        var gate = new ToolIsolationGate(localSvc, accessor, new IsolationOptions { StagingRoot = stg }, broker, opts);
        var runCmd = new RunCommandTool(NullLogger<RunCommandTool>.Instance, isolationGate: gate);

        using (accessor.Begin(new TrustedExecutionContext
               {
                   UserId = "local",
                   SessionId = "s-standalone",
                   JobId = "j1",
                   TaskId = "t1",
                   AttemptId = "a1",
                   WorkspaceRoot = ws,
                   Network = new ExecutionNetworkPolicy(ExecutionNetworkMode.Host)
               }))
        {
            var res = await runCmd.ExecuteAsync(new Dictionary<string, object?> { ["command"] = new[] { "dotnet", "--version" } });
            Assert.NotEqual(ToolResultStatus.Success, res.Status);
            Assert.Contains(ExecutionReasonCodes.ApprovalUnavailable, res.Output, StringComparison.Ordinal);
            Assert.Contains("托管客户端", res.Output, StringComparison.Ordinal);
            Assert.Contains("Docker", res.Output, StringComparison.Ordinal);
        }

        Assert.Equal(0, runnerCalls);
    }

    [Fact]
    public async Task UnsafeFileControlToken_MarksSelfApprovalProtectionFalse_AcrossStatusSettingsAndAudit()
    {
        var globalSettingsPath = Path.Combine(_root, "user-global", "appsettings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(globalSettingsPath)!);
        File.WriteAllText(globalSettingsPath, """{"Local":{"UnsafeFileControlToken":true},"Execution":{"Backend":"Local","PermissionMode":"FullAuto"}}""");

        Assert.True(LocalHostCredentials.ResolveUserLevelUnsafeFileControlToken(globalSettingsPath, []));

        var stateDir = Path.Combine(_root, "unsafe-state");
        var creds = LocalHostCredentials.Initialize(
            stateDir,
            LocalTokenDeliveryMode.File,
            ExecutionBackend.Local,
            unsafeFileControlToken: true);

        Assert.True(File.Exists(Path.Combine(stateDir, LocalHostCredentials.ControlTokenFileName)));
        Assert.True(creds.IsControlPlaneAvailable);
        Assert.False(creds.SelfApprovalProtection);

        await using var host = await StartTestControlPlaneHostAsync(
            creds,
            new ExecutionOptions { Backend = ExecutionBackend.Local, PermissionMode = ExecutionPermissionMode.FullAuto },
            globalSettingsPath);

        using var normalClient = host.CreateClient(creds.AccessToken);
        var statusResp = await normalClient.GetFromJsonAsync<SystemStatusSummaryDto>("/api/system/status");
        Assert.NotNull(statusResp);
        Assert.False(statusResp!.SelfApprovalProtection);

        var settingsResp = await normalClient.GetFromJsonAsync<ExecutionSettingsDto>("/api/settings/execution");
        Assert.NotNull(settingsResp);
        Assert.False(settingsResp!.SelfApprovalProtection);

        // Trigger an execution authorization so audit is written, and verify selfApprovalProtection: false in audit payload!
        var req = new ExecutionRequest
        {
            SessionId = "s-audit",
            TurnId = "t1",
            ToolName = "exec_cmd",
            Executable = "dotnet",
            InterpreterChain = ["dotnet"],
            Argv = ["--version"],
            Cwd = ".",
            EnvironmentDelta = [],
            Backend = ExecutionBackend.Local,
            Network = "Host",
            Filesystem = "unrestricted"
        };
        var auth = await host.Broker.AuthorizeAsync(
            new ExecutionAuthorizationContext(req, _root, _root, _root, null),
            CancellationToken.None);
        Assert.True(auth.Decision.IsAllowed);

        var audits = await host.AuditStore.ListAsync("s-audit", "execution_approval", CancellationToken.None);
        var approvalAudit = Assert.Single(audits);
        Assert.Contains("\"selfApprovalProtection\":false", approvalAudit.Output ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ControlPlaneEndpoints_Return403ForNormalToken_PreventEndToEndSelfApproval_AndReconcilePendingApprovals()
    {
        using var pipeReader = new StringReader(JsonSerializer.Serialize(new
        {
            accessToken = "normal-plane-secret-token-111111",
            controlToken = "control-plane-secret-token-999999"
        }) + "\n");

        var creds = LocalHostCredentials.Initialize(
            Path.Combine(_root, "e2e-state"),
            LocalTokenDeliveryMode.Pipe,
            ExecutionBackend.Local,
            unsafeFileControlToken: false,
            pipeReader: pipeReader);

        var settingsPath = Path.Combine(_root, "e2e-global", "appsettings.json");
        await using var host = await StartTestControlPlaneHostAsync(
            creds,
            new ExecutionOptions
            {
                Backend = ExecutionBackend.Docker,
                PermissionMode = ExecutionPermissionMode.Ask,
                ApprovalTimeout = TimeSpan.FromSeconds(10)
            },
            settingsPath);

        using var normalClient = host.CreateClient(creds.AccessToken);
        using var controlClient = host.CreateClient(creds.ControlToken!);

        // 1. Normal token calling control-plane endpoints MUST receive 403 Forbidden:
        //    - Relaxing permission mode via PUT /api/settings/execution
        var relaxAttempt = await normalClient.PutAsJsonAsync("/api/settings/execution", new { permissionMode = "FullAuto" });
        Assert.Equal(HttpStatusCode.Forbidden, relaxAttempt.StatusCode);

        //    - Listing persistent permissions via GET /api/settings/permissions
        var listPermsAttempt = await normalClient.GetAsync("/api/settings/permissions");
        Assert.Equal(HttpStatusCode.Forbidden, listPermsAttempt.StatusCode);

        //    - Plan structuredAction approve via POST /api/gateway/chat
        var planSelfApproveAttempt = await normalClient.PostAsJsonAsync("/api/gateway/chat", new
        {
            sessionId = "s-owned",
            message = "approve my own plan",
            structuredAction = new { action = "approve" }
        });
        Assert.Equal(HttpStatusCode.Forbidden, planSelfApproveAttempt.StatusCode);

        // 2. End-to-end Ask self-approval attempt & disconnect reconciliation:
        var runnerCalls = 0;
        var fakeService = new CountingDockerExecutionService(() => Interlocked.Increment(ref runnerCalls));
        var accessor = new TrustedExecutionContextAccessor();
        var gate = new ToolIsolationGate(fakeService, accessor, new IsolationOptions { StagingRoot = _root }, host.Broker, host.OptionsMonitor);
        var execCode = new ExecCodeTool(NullLogger<ExecCodeTool>.Instance, gate);

        Task<CodexToolResult> pendingToolTask;
        using (accessor.Begin(new TrustedExecutionContext
               {
                   UserId = LocalUser.Id,
                   SessionId = "s-owned",
                   JobId = "j1",
                   TaskId = "t1",
                   AttemptId = "a1",
                   WorkspaceRoot = _root
               }))
        {
            pendingToolTask = execCode.ExecuteAsync(new Dictionary<string, object?> { ["code"] = "print('guarded')", ["language"] = "python" });

            // Reconnect reconciliation: GET /api/gateway/session/{id}/approvals works on normal plane and returns the pending item!
            PendingApprovalsResponse? reconciled = null;
            for (var i = 0; i < 40; i++)
            {
                reconciled = await normalClient.GetFromJsonAsync<PendingApprovalsResponse>("/api/gateway/session/s-owned/approvals");
                if (reconciled is { Count: > 0 })
                {
                    break;
                }

                await Task.Delay(20);
            }

            Assert.NotNull(reconciled);
            var item = Assert.Single(reconciled!.Items);

            // Command / model attempting self-approval with normal token gets 403 Forbidden -> runner still at 0 invocations!
            var selfApproveResp = await normalClient.PostAsJsonAsync(
                $"/api/gateway/approvals/{item.ApprovalId}",
                new SubmitExecutionApprovalRequest(item.RequestDigest, "allow_once"));
            Assert.Equal(HttpStatusCode.Forbidden, selfApproveResp.StatusCode);
            Assert.Equal(0, runnerCalls);
            Assert.False(pendingToolTask.IsCompleted);

            // Control token submitting a mismatched digest gets 409 Conflict!
            var wrongDigestResp = await controlClient.PostAsJsonAsync(
                $"/api/gateway/approvals/{item.ApprovalId}",
                new SubmitExecutionApprovalRequest("wrong-digest", "allow_once"));
            Assert.Equal(HttpStatusCode.Conflict, wrongDigestResp.StatusCode);
            Assert.Equal(0, runnerCalls);

            // Control token submitting the exact matching digest gets 200 OK and unblocks execution!
            var validApproveResp = await controlClient.PostAsJsonAsync(
                $"/api/gateway/approvals/{item.ApprovalId}",
                new SubmitExecutionApprovalRequest(item.RequestDigest, "allow_once", "approved by user UI"));
            Assert.Equal(HttpStatusCode.OK, validApproveResp.StatusCode);

            var toolResult = await pendingToolTask;
            Assert.Equal(ToolResultStatus.Success, toolResult.Status);
            Assert.Equal(1, runnerCalls);
        }
    }

    [Fact]
    public async Task BackgroundJob_SuspendedOnApproval_ResumesOnAllowOnce_AndTerminatesCleanlyOnDeny()
    {
        var sqlitePath = Path.Combine(_root, "bg-jobs", "jobs.db");
        Directory.CreateDirectory(Path.GetDirectoryName(sqlitePath)!);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCodexFlowSqliteStorage(sqlitePath);
        services.AddCodexFlowLocalDefaults();
        services.AddSingleton(new Mock<IOutboxSignal>().Object);
        services.AddSingleton(new Mock<IBackgroundJobSignal>().Object);
        services.AddScoped<IBackgroundJobService, BackgroundJobService>();
        services.AddSingleton<IBackgroundJobApprovalResumer, BackgroundJobApprovalResumer>();

        await using var sp = services.BuildServiceProvider();
        await using (var initScope = sp.CreateAsyncScope())
        {
            await initScope.ServiceProvider.GetRequiredService<JobDbContext>().Database.MigrateAsync();
        }

        var unsubscribedGateway = new ExecutionApprovalAndPermissionTests.RecordingGateway(hasSubscribers: false);
        var opts = new ExecutionApprovalAndPermissionTests.MutableOptionsMonitor<ExecutionOptions>(new ExecutionOptions
        {
            Backend = ExecutionBackend.Docker,
            PermissionMode = ExecutionPermissionMode.Ask,
            HeadlessApprovalTimeout = TimeSpan.Zero,
            SuspendUnsubscribedBackgroundJobs = true
        });
        var permStore = new ExecutionPermissionStore(Path.Combine(_root, "bg-perms.json"));
        var broker = new ExecutionApprovalBroker(
            opts,
            permStore,
            NullLogger<ExecutionApprovalBroker>.Instance,
            sp,
            gateway: unsubscribedGateway,
            jobResumer: sp.GetRequiredService<IBackgroundJobApprovalResumer>());

        var runnerCalls = 0;
        var fakeBackend = new CountingDockerExecutionService(() => Interlocked.Increment(ref runnerCalls));
        var accessor = new TrustedExecutionContextAccessor();
        var gate = new ToolIsolationGate(fakeBackend, accessor, new IsolationOptions { StagingRoot = _root }, broker, opts);
        var execCode = new ExecCodeTool(NullLogger<ExecCodeTool>.Instance, gate);

        // --- Part A: Suspend into WaitingUser -> Approve -> Job resumes to Queued -> Re-run consumes allow_once and succeeds ---
        string jobIdA;
        const string resumeTokenA = "resume-tok-a";
        await using (var scope = sp.CreateAsyncScope())
        {
            var jobService = scope.ServiceProvider.GetRequiredService<IBackgroundJobService>();
            jobIdA = await jobService.CreateJobAsync("sess-bg", "T1", "ForgeWorker", "{}", startRunning: true);
        }

        using (AmbientBackgroundJobApprovalContext.BeginScope(jobIdA, resumeTokenA, CancellationToken.None, out var suspensionA))
        using (accessor.Begin(new TrustedExecutionContext
               {
                   UserId = LocalUser.Id,
                   SessionId = "sess-bg",
                   JobId = jobIdA,
                   TaskId = "T1",
                   AttemptId = "1",
                   WorkspaceRoot = _root
               }))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                execCode.ExecuteAsync(
                    new Dictionary<string, object?> { ["code"] = "print('bg-job-a')", ["language"] = "python" },
                    suspensionA.EffectiveToken));
            Assert.NotNull(suspensionA.SuspendedApproval);
            Assert.Equal(0, runnerCalls);

            // Start SubmitDecisionAsync CONCURRENTLY BEFORE WaitingUser is persisted to SQLite (B9 race test!)
            var pendingListA = broker.GetPendingApprovals("sess-bg");
            var pendingItemA = Assert.Single(pendingListA);
            var earlyApproveTask = Task.Run(() => broker.SubmitDecisionAsync(
                pendingItemA.ApprovalId,
                new ExecutionApprovalDecisionSubmission(pendingItemA.RequestDigest, ExecutionApprovalDecisionKind.AllowOnce, "local")));

            await Task.Delay(80); // Simulate slow DB persistence window while SubmitDecisionAsync is already waiting on barrier
            await PersistWaitingUserCheckpointAsync(sp, jobIdA, "sess-bg", resumeTokenA);
            suspensionA.NotifyPersistenceCommitted();

            var approveOutcome = await earlyApproveTask;
            Assert.Equal(ApprovalSubmissionOutcome.Resolved, approveOutcome.Outcome);
        }

        await using (var checkScope = sp.CreateAsyncScope())
        {
            var db = checkScope.ServiceProvider.GetRequiredService<JobDbContext>();
            var jobA = await db.BackgroundJobs.FindAsync(jobIdA);
            Assert.NotNull(jobA);
            Assert.Equal(BackgroundJobStatus.Queued, jobA!.Status);
        }

        // B9: A DIFFERENT job in the same session CANNOT steal jobIdA's resumed allow_once ticket!
        using (accessor.Begin(new TrustedExecutionContext
               {
                   UserId = LocalUser.Id,
                   SessionId = "sess-bg",
                   JobId = "other-job-id",
                   TaskId = "T1",
                   AttemptId = "1",
                   WorkspaceRoot = _root
               }))
        {
            var stolenAttempt = await execCode.ExecuteAsync(
                new Dictionary<string, object?> { ["code"] = "print('bg-job-a')", ["language"] = "python" });
            Assert.NotEqual(ToolResultStatus.Success, stolenAttempt.Status);
            Assert.Equal(0, runnerCalls);
        }

        // Re-execute the resumed job's tool call -> consumes the single-use resumed ticket and runs!
        using (accessor.Begin(new TrustedExecutionContext
               {
                   UserId = LocalUser.Id,
                   SessionId = "sess-bg",
                   JobId = jobIdA,
                   TaskId = "T1",
                   AttemptId = "2",
                   WorkspaceRoot = _root
               }))
        {
            var resumedRun = await execCode.ExecuteAsync(
                new Dictionary<string, object?> { ["code"] = "print('bg-job-a')", ["language"] = "python" });
            Assert.Equal(ToolResultStatus.Success, resumedRun.Status);
            Assert.Equal(1, runnerCalls);
        }

        // --- Part B: Suspend into WaitingUser -> Deny -> Job transitions cleanly to Failed without starting any process ---
        string jobIdB;
        const string resumeTokenB = "resume-tok-b";
        await using (var scope = sp.CreateAsyncScope())
        {
            var jobService = scope.ServiceProvider.GetRequiredService<IBackgroundJobService>();
            jobIdB = await jobService.CreateJobAsync("sess-bg-deny", "T2", "ForgeWorker", "{}", startRunning: true);
        }

        using (AmbientBackgroundJobApprovalContext.BeginScope(jobIdB, resumeTokenB, CancellationToken.None, out var suspensionB))
        using (accessor.Begin(new TrustedExecutionContext
               {
                   UserId = LocalUser.Id,
                   SessionId = "sess-bg-deny",
                   JobId = jobIdB,
                   TaskId = "T2",
                   AttemptId = "1",
                   WorkspaceRoot = _root
               }))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                execCode.ExecuteAsync(
                    new Dictionary<string, object?> { ["code"] = "print('bg-job-b')", ["language"] = "python" },
                    suspensionB.EffectiveToken));
            await PersistWaitingUserCheckpointAsync(sp, jobIdB, "sess-bg-deny", resumeTokenB);
            suspensionB.NotifyPersistenceCommitted();
        }

        var pendingItemB = Assert.Single(broker.GetPendingApprovals("sess-bg-deny"));
        var denyOutcome = await broker.SubmitDecisionAsync(
            pendingItemB.ApprovalId,
            new ExecutionApprovalDecisionSubmission(pendingItemB.RequestDigest, ExecutionApprovalDecisionKind.Deny, "local", "no thanks"));
        Assert.Equal(ApprovalSubmissionOutcome.Resolved, denyOutcome.Outcome);

        await using (var checkScope = sp.CreateAsyncScope())
        {
            var db = checkScope.ServiceProvider.GetRequiredService<JobDbContext>();
            var jobB = await db.BackgroundJobs.FindAsync(jobIdB);
            Assert.NotNull(jobB);
            Assert.Equal(BackgroundJobStatus.Failed, jobB!.Status);
            Assert.Contains(ExecutionReasonCodes.PermissionDenied, jobB.ResultPayloadJson ?? string.Empty, StringComparison.Ordinal);
        }

        // Still 1 call from Part A; Part B launched 0 processes!
        Assert.Equal(1, runnerCalls);
    }

    [Fact]
    public async Task ToolOutputEventThrottler_CoalescesAndCapsHighFrequencyOutput()
    {
        // 1. Rate-limit window check: 10s interval bounds in-memory buffering and suppresses excess lines
        var intervalGateway = new ExecutionApprovalAndPermissionTests.RecordingGateway(hasSubscribers: true);
        await using (var throttler = new ToolOutputEventThrottler(
                         intervalGateway,
                         "sess-burst-interval",
                         "exec_cmd",
                         "task-1",
                         minFlushInterval: TimeSpan.FromSeconds(10),
                         maxEventsPerExecution: 5,
                         maxChunkChars: 512))
        {
            for (var i = 0; i < 2000; i++)
            {
                throttler.Append("stdout", $"burst-output-line-{i:D4}-padding-text");
            }

            // Before explicit end-of-command flush, at most 1 chunk could have been emitted inside the 10s window!
            await Task.Delay(30);
            Assert.InRange(throttler.EmittedEventCount, 0, 1);

            await throttler.FlushAsync();
            Assert.InRange(throttler.EmittedEventCount, 1, 5);
        }

        Assert.InRange(intervalGateway.Events.Count, 2, 6);

        // 2. Hard total-event cap check: even with zero flush interval, 2000 lines strictly cap at maxEventsPerExecution (5)
        var capGateway = new ExecutionApprovalAndPermissionTests.RecordingGateway(hasSubscribers: true);
        await using (var capThrottler = new ToolOutputEventThrottler(
                         capGateway,
                         "sess-burst-cap",
                         "exec_cmd",
                         "task-2",
                         minFlushInterval: TimeSpan.Zero,
                         maxEventsPerExecution: 5,
                         maxChunkChars: 512))
        {
            for (var i = 0; i < 2000; i++)
            {
                capThrottler.Append("stdout", $"burst-output-line-{i:D4}-padding-text");
            }

            await capThrottler.FlushAsync();
            Assert.Equal(5, capThrottler.EmittedEventCount);
        }

        // Max 5 data chunks + 1 throttled summary notification = 6 SSE events for 2000 lines!
        Assert.Equal(6, capGateway.Events.Count);
    }

    [Fact]
    public async Task CredentialsInsideWorkspace_AreExcludedFromSnapshotsInputsExportsAndRealManagedTools()
    {
        var ws = Path.Combine(_root, "ws-with-state");
        var customCredDir = Path.Combine(ws, "custom-creds");
        Directory.CreateDirectory(ws);
        File.WriteAllText(Path.Combine(ws, "README.md"), "# Safe file");
        Directory.CreateDirectory(Path.Combine(ws, ".codexflow"));
        const string dotSecret = "SECRET_DOT_CODEXFLOW_9981";
        File.WriteAllText(Path.Combine(ws, ".codexflow", "control.token"), dotSecret);

        var creds = LocalHostCredentials.Initialize(
            customCredDir,
            LocalTokenDeliveryMode.File,
            ExecutionBackend.Docker,
            unsafeFileControlToken: false);
        Assert.NotNull(creds.ControlToken);
        var controlSecret = creds.ControlToken!;
        // Also place a NON-.token file inside the protected credential directory so filename checks alone CANNOT pass (Round 3 Advisory 1)!
        const string extraSecretContent = "EXTRA_CREDENTIAL_CONFIG_SECRET_7744";
        File.WriteAllText(Path.Combine(customCredDir, "extra-secret.json"), extraSecretContent);

        // Create a junction/symlink alias inside workspace pointing into customCredDir to test alias bypass (R1)!
        var aliasDir = Path.Combine(ws, "innocent-docs");
        if (OperatingSystem.IsWindows())
        {
            var mkAlias = await HostProcessRunner.RunAsync(
                new HostProcessInvocation("cmd.exe", ["/d", "/c", "mklink", "/J", aliasDir, customCredDir]),
                CancellationToken.None);
            Assert.True(mkAlias.Started, mkAlias.StartError);
            Assert.Equal(0, mkAlias.ExitCode);
        }
        else
        {
            Directory.CreateSymbolicLink(aliasDir, customCredDir);
        }

        try
        {
            // 1. WorkspaceSnapshotBuilder NEVER packages .codexflow, customCredDir, or link aliases into container tar!
            var tarPath = Path.Combine(_root, "ws-b2.tar");
            var snap = WorkspaceSnapshotBuilder.Build(ws, tarPath, new ExecutionResourceLimits());
            Assert.Contains("README.md", snap.Baseline.Keys);
            Assert.DoesNotContain(snap.Baseline.Keys, k => k.Contains(".codexflow", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(snap.Baseline.Keys, k => k.Contains("custom-creds", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(snap.Baseline.Keys, k => k.Contains("innocent-docs", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(snap.Baseline.Keys, k => k.EndsWith(".token", StringComparison.OrdinalIgnoreCase));

            // 2. R1: Test EVERY REAL managed read/search/list tool (plain read, hashline read, structural read, grep, glob, ls, index)!
            var hashlineServices = new ServiceCollection();
            hashlineServices.AddLogging();
            CodexFlow.Core.Hashline.HashlineServiceCollectionExtensions.AddHashlineServices(
                hashlineServices,
                opts => opts.Enabled = true);
            await using var hlProvider = hashlineServices.BuildServiceProvider();
            var hlService = hlProvider.GetRequiredService<IHashlineFileService>();

            var readFileTool = new ReadFileTool(
                NullLogger<ReadFileTool>.Instance,
                hlService,
                new CodexFlow.Core.Hashline.Models.HashlineOptions { Enabled = true });
            var hsReadTool = new HashlineReadTool(readFileTool);
            var structuralRead = new CodexFlow.Core.Agents.StructuralReadTool(
                new Mock<ICodeAnalysisService>().Object,
                NullLogger<CodexFlow.Core.Agents.StructuralReadTool>.Instance);
            var searchInFiles = new SearchInFilesTool(NullLogger<SearchInFilesTool>.Instance);
            var globTool = new GlobTool(NullLogger<GlobTool>.Instance);
            var listWorkspace = new ListWorkspaceTool(NullLogger<ListWorkspaceTool>.Instance);
            var structuralLs = new CodexFlow.Core.Agents.StructuralLsTool(
                new Mock<ICodeAnalysisService>().Object,
                NullLogger<CodexFlow.Core.Agents.StructuralLsTool>.Instance);
            var searchIndex = new SearchFileIndexTool(
                new CodexFlow.Core.Agents.CodexSessionManager(
                    new Mock<ISessionStore>().Object,
                    new Mock<IMemoryService>().Object,
                    NullLogger<CodexFlow.Core.Agents.CodexSessionManager>.Instance),
                NullLogger<SearchFileIndexTool>.Instance);

            foreach (var forbiddenRelPath in new[]
                     {
                         ".codexflow/control.token",
                         "custom-creds/control.token",
                         "custom-creds/extra-secret.json",
                         "innocent-docs/control.token",
                         "innocent-docs/extra-secret.json"
                     })
            {
                var plainRes = await readFileTool.ExecuteAsync(new Dictionary<string, object?>
                {
                    ["workspace_path"] = ws,
                    ["path"] = forbiddenRelPath,
                    ["mode"] = "plain"
                });
                Assert.NotEqual(ToolResultStatus.Success, plainRes.Status);
                Assert.DoesNotContain(controlSecret, plainRes.Output, StringComparison.Ordinal);
                Assert.DoesNotContain(dotSecret, plainRes.Output, StringComparison.Ordinal);
                Assert.DoesNotContain(extraSecretContent, plainRes.Output, StringComparison.Ordinal);

                var hlRes = await hsReadTool.ExecuteAsync(new Dictionary<string, object?>
                {
                    ["workspace_path"] = ws,
                    ["path"] = forbiddenRelPath
                });
                Assert.NotEqual(ToolResultStatus.Success, hlRes.Status);
                Assert.DoesNotContain(controlSecret, hlRes.Output, StringComparison.Ordinal);
                Assert.DoesNotContain(extraSecretContent, hlRes.Output, StringComparison.Ordinal);

                var sReadRes = await structuralRead.ExecuteAsync(new Dictionary<string, object?>
                {
                    ["workspace_path"] = ws,
                    ["path"] = forbiddenRelPath
                });
                Assert.NotEqual(ToolResultStatus.Success, sReadRes.Status);
                Assert.DoesNotContain(controlSecret, sReadRes.Output, StringComparison.Ordinal);
                Assert.DoesNotContain(extraSecretContent, sReadRes.Output, StringComparison.Ordinal);
            }

            // Round 3 B1: Passing junction alias AS `project_root` (both with and without ambient TrustedExecutionContext) MUST still be blocked!
            var rootAliasNoCtx = await readFileTool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["workspace_path"] = ws,
                ["project_root"] = aliasDir,
                ["path"] = "extra-secret.json",
                ["mode"] = "plain"
            });
            Assert.NotEqual(ToolResultStatus.Success, rootAliasNoCtx.Status);
            Assert.DoesNotContain(extraSecretContent, rootAliasNoCtx.Output, StringComparison.Ordinal);

            var accessor = new TrustedExecutionContextAccessor();
            using (accessor.Begin(new TrustedExecutionContext
                   {
                       UserId = LocalUser.Id,
                       SessionId = "s-b1-root",
                       JobId = "j1",
                       TaskId = "t1",
                       AttemptId = "1",
                       WorkspaceRoot = ws
                   }))
            {
                var rootAliasWithCtx = await readFileTool.ExecuteAsync(new Dictionary<string, object?>
                {
                    ["workspace_path"] = ws,
                    ["project_root"] = aliasDir,
                    ["path"] = "extra-secret.json",
                    ["mode"] = "plain"
                });
                Assert.NotEqual(ToolResultStatus.Success, rootAliasWithCtx.Status);
                Assert.DoesNotContain(extraSecretContent, rootAliasWithCtx.Output, StringComparison.Ordinal);
            }

            // Directly test FileSystemGuard.ValidateFileForRead & CopyInputDirectories against alias + non-.token protected file!
            var fsGuard = hlProvider.GetRequiredService<CodexFlow.Core.Hashline.Abstractions.IFileSystemGuard>();
            Assert.False(fsGuard.ValidateFileForRead(
                Path.Combine(aliasDir, "extra-secret.json"),
                [ws],
                new CodexFlow.Core.Hashline.Models.HashlineOptions { Enabled = true }).IsValid);
            Assert.False(fsGuard.ValidateFileForRead(
                Path.Combine(customCredDir, "extra-secret.json"),
                [ws],
                new CodexFlow.Core.Hashline.Models.HashlineOptions { Enabled = true }).IsValid);

            var inputsDst = Path.Combine(_root, "copied-inputs");
            LocalExecutionService.CopyInputDirectories(
                [new IsolatedInputDirectory(ws, "ws-copy")],
                inputsDst,
                new ExecutionResourceLimits());
            Assert.True(File.Exists(Path.Combine(inputsDst, "ws-copy", "README.md")));
            Assert.False(File.Exists(Path.Combine(inputsDst, "ws-copy", "custom-creds", "extra-secret.json")));
            Assert.False(Directory.Exists(Path.Combine(inputsDst, "ws-copy", "innocent-docs")));

            // Round 4 Advisory: Passing `HostPath = aliasDir` or `HostPath = customCredDir` directly as input root MUST throw!
            Assert.Throws<WorkspaceSnapshotException>(() =>
                LocalExecutionService.CopyInputDirectories(
                    [new IsolatedInputDirectory(aliasDir, "evil-root")],
                    Path.Combine(_root, "copied-evil-1"),
                    new ExecutionResourceLimits()));
            Assert.Throws<WorkspaceSnapshotException>(() =>
                LocalExecutionService.CopyInputDirectories(
                    [new IsolatedInputDirectory(customCredDir, "evil-root-2")],
                    Path.Combine(_root, "copied-evil-2"),
                    new ExecutionResourceLimits()));

            // Search / Glob / Ls across "." MUST find README.md but NEVER expose secrets or .token files!
            var grepSecret = await searchInFiles.ExecuteAsync(new Dictionary<string, object?>
            {
                ["workspace_path"] = ws,
                ["pattern"] = controlSecret[..12],
                ["path"] = "."
            });
            Assert.DoesNotContain("control.token", grepSecret.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(controlSecret, grepSecret.Output, StringComparison.Ordinal);

            var grepDotSecret = await searchInFiles.ExecuteAsync(new Dictionary<string, object?>
            {
                ["workspace_path"] = ws,
                ["pattern"] = "SECRET_DOT_CODEXFLOW",
                ["path"] = "."
            });
            Assert.DoesNotContain(dotSecret, grepDotSecret.Output, StringComparison.Ordinal);

            var globRes = await globTool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["workspace_path"] = ws,
                ["pattern"] = "**/*",
                ["path"] = "."
            });
            Assert.Contains("README.md", globRes.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("control.token", globRes.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".codexflow", globRes.Output, StringComparison.OrdinalIgnoreCase);

            var lsRes = await listWorkspace.ExecuteAsync(new Dictionary<string, object?>
            {
                ["workspace_path"] = ws,
                ["path"] = ".",
                ["recursive"] = true
            });
            Assert.Contains("README.md", lsRes.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("control.token", lsRes.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("innocent-docs", lsRes.Output, StringComparison.OrdinalIgnoreCase);

            var sLsRes = await structuralLs.ExecuteAsync(new Dictionary<string, object?>
            {
                ["workspace_path"] = ws,
                ["path"] = ".",
                ["recursive"] = true
            });
            Assert.Contains("README.md", sLsRes.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("control.token", sLsRes.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("innocent-docs", sLsRes.Output, StringComparison.OrdinalIgnoreCase);

            var idxRes = await searchIndex.ExecuteAsync(new Dictionary<string, object?>
            {
                ["workspace_path"] = ws,
                ["query"] = "token"
            });
            Assert.DoesNotContain("control.token", idxRes.Output, StringComparison.OrdinalIgnoreCase);

            // Round 3 B2: Cached `ProjectFileIndex` session branch MUST ALSO filter out junction aliases and protected directories!
            var cachedSessionStore = new Mock<ISessionStore>();
            var cachedSession = new CodexSession { Id = "sess-cached-idx", WorkspacePath = ws };
            cachedSession.ActiveFacts.Add(new MemoryEntry(
                "ProjectFileIndex",
                Newtonsoft.Json.JsonConvert.SerializeObject(new[]
                {
                    new { Path = "innocent-docs/extra-secret.json", Type = "JSON", Size = 32L },
                    new { Path = "custom-creds/extra-secret.json", Type = "JSON", Size = 32L },
                    new { Path = "README.md", Type = "Markdown", Size = 12L }
                }),
                "index",
                DateTime.UtcNow));
            cachedSessionStore
                .Setup(s => s.GetSessionAsync("sess-cached-idx", It.IsAny<CancellationToken>()))
                .ReturnsAsync(cachedSession);
            var cachedIndexTool = new SearchFileIndexTool(
                new CodexFlow.Core.Agents.CodexSessionManager(
                    cachedSessionStore.Object,
                    new Mock<IMemoryService>().Object,
                    NullLogger<CodexFlow.Core.Agents.CodexSessionManager>.Instance),
                NullLogger<SearchFileIndexTool>.Instance);

            var cachedSecretIdx = await cachedIndexTool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["session_id"] = "sess-cached-idx",
                ["workspace_path"] = ws,
                ["query"] = "extra-secret"
            });
            Assert.DoesNotContain("extra-secret.json", cachedSecretIdx.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("innocent-docs", cachedSecretIdx.Output, StringComparison.OrdinalIgnoreCase);

            // Round 4 B2: Without any workspace root (empty session.WorkspacePath and no workspace_path/project_root),
            // cached ProjectFileIndex MUST NOT bypass physical root verification to leak alias/protected paths!
            var rootlessSession = new CodexSession { Id = "sess-rootless-idx", WorkspacePath = string.Empty };
            rootlessSession.ActiveFacts.Add(new MemoryEntry(
                "ProjectFileIndex",
                Newtonsoft.Json.JsonConvert.SerializeObject(new[]
                {
                    new { Path = "innocent-docs/extra-secret.json", Type = "JSON", Size = 32L }
                }),
                "index",
                DateTime.UtcNow));
            cachedSessionStore
                .Setup(s => s.GetSessionAsync("sess-rootless-idx", It.IsAny<CancellationToken>()))
                .ReturnsAsync(rootlessSession);
            var rootlessIdxRes = await cachedIndexTool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["session_id"] = "sess-rootless-idx",
                ["query"] = "extra-secret"
            });
            Assert.NotEqual(ToolResultStatus.Success, rootlessIdxRes.Status);
            Assert.DoesNotContain("extra-secret.json", rootlessIdxRes.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("innocent-docs", rootlessIdxRes.Output, StringComparison.OrdinalIgnoreCase);

            // 3. Export path protection rejects .codexflow and *.token!
            Assert.True(WorkspacePath.IsProtected(".codexflow/codexflow.local.yml"));
            Assert.True(WorkspacePath.IsProtected("sub/control.token"));
        }
        finally
        {
            if (Directory.Exists(aliasDir))
            {
                Directory.Delete(aliasDir);
            }
        }
    }

    [Fact]
    public async Task ControlPlanePutExecutionSettings_SynchronouslyUpdatesRealOptionsMonitor_AndRevokesFileControlTokenOnDockerToLocalSwitch()
    {
        var stateDir = Path.Combine(_root, "b10-state");
        var settingsPath = Path.Combine(_root, "b10-global", "appsettings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);

        // Start in File mode + Docker backend -> control.token is created and active
        var creds = LocalHostCredentials.Initialize(
            stateDir,
            LocalTokenDeliveryMode.File,
            ExecutionBackend.Docker,
            unsafeFileControlToken: false);
        Assert.True(creds.IsControlPlaneAvailable);
        Assert.True(File.Exists(creds.ControlTokenPath!));
        var initialControlToken = creds.ControlToken!;

        // Build host using REAL Microsoft.Extensions.Options DI pipeline (NO file reload, NO MutableOptionsMonitor!)
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddApplicationPart(typeof(ExecutionSettingsController).Assembly);
        builder.Services.AddCodexIsolatedExecution();
        builder.Services.AddSingleton(creds);
        builder.Services.AddSingleton<IControlPlaneStateProvider>(creds);
        builder.Services.AddSingleton<IExecutionSettingsService>(new ExecutionSettingsService(settingsPath));
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddScheme<AuthenticationSchemeOptions, LocalTokenAuthenticationHandler>(JwtBearerDefaults.AuthenticationScheme, null);
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("ActiveUser", policy => policy.RequireAuthenticatedUser());
            options.AddPolicy(
                ControlPlaneAuthConstants.ControlPlanePolicy,
                policy => policy.RequireAuthenticatedUser().RequireClaim(ControlPlaneAuthConstants.PlaneClaimType, ControlPlaneAuthConstants.PlaneControl));
        });

        await using var app = builder.Build();
        var realMonitor = app.Services.GetRequiredService<IOptionsMonitor<ExecutionOptions>>();
        using var watch = realMonitor.OnChange(updated => creds.UpdateActiveBackend(updated.Backend));

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();

        var baseUri = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
        using var controlClient = new HttpClient { BaseAddress = baseUri };
        controlClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", initialControlToken);

        // Switch from Docker to Local + AutoInWorkspace via PUT /api/settings/execution
        var putResp = await controlClient.PutAsJsonAsync("/api/settings/execution", new
        {
            backend = "Local",
            permissionMode = "AutoInWorkspace"
        });
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);
        var dto = await putResp.Content.ReadFromJsonAsync<ExecutionSettingsDto>();
        Assert.NotNull(dto);

        // B10: Real IOptionsMonitor updated synchronously AND file-based control token immediately revoked!
        Assert.Equal(ExecutionBackend.Local, realMonitor.CurrentValue.Backend);
        Assert.Equal(ExecutionPermissionMode.AutoInWorkspace, realMonitor.CurrentValue.PermissionMode);
        Assert.Equal("Local", dto!.Backend);
        Assert.Equal("AutoInWorkspace", dto.PermissionMode);
        Assert.False(dto.ControlPlaneAvailable);
        Assert.False(creds.IsControlPlaneAvailable);
        Assert.False(File.Exists(creds.ControlTokenPath!));

        // Subsequent request using the revoked control token MUST fail authentication (401)!
        var afterRevoke = await controlClient.GetAsync("/api/settings/permissions");
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevoke.StatusCode);

        await app.StopAsync();
    }

    [Fact]
    public void ControlPlaneRelax_FollowedByConfigReloadTighten_ImmediatelyTightens_AndRejectsFileRelax()
    {
        // R3: Verify real IConfiguration reload + ExecutionPermissionClamp + IOptionsMonitor<ExecutionOptions>:
        // Ask -> ControlPlane FullAuto -> Config reload Deny (tightens to Deny!) -> Config reload FullAuto (clamped at Deny!)
        var reloadableSource = new ReloadableMemoryConfigSource(new Dictionary<string, string?>
        {
            ["Execution:Backend"] = "Docker",
            ["Execution:PermissionMode"] = "Ask"
        });
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .Add(reloadableSource)
            .Build();

        var services = new ServiceCollection();
        services.Configure<ExecutionOptions>(configuration.GetSection(ExecutionOptions.SectionName));
        services.AddCodexIsolatedExecution();
        using var sp = services.BuildServiceProvider();

        var monitor = sp.GetRequiredService<IOptionsMonitor<ExecutionOptions>>();
        var clamp = sp.GetRequiredService<ExecutionPermissionClamp>();

        Assert.Equal(ExecutionPermissionMode.Ask, monitor.CurrentValue.PermissionMode);

        // 1. Control plane explicitly relaxes to FullAuto -> takes effect synchronously!
        clamp.ApplyControlPlaneUpdate(ExecutionBackend.Docker, ExecutionPermissionMode.FullAuto);
        Assert.Equal(ExecutionPermissionMode.FullAuto, monitor.CurrentValue.PermissionMode);

        // 2. External config reloads to Deny (stricter than FullAuto) -> MUST immediately tighten to Deny!
        reloadableSource.Update("Execution:PermissionMode", "Deny");
        Assert.Equal(ExecutionPermissionMode.Deny, monitor.CurrentValue.PermissionMode);

        // 3. External config reloads back to FullAuto -> tighten-only clamp MUST reject file-driven relaxation!
        reloadableSource.Update("Execution:PermissionMode", "FullAuto");
        Assert.Equal(ExecutionPermissionMode.Deny, monitor.CurrentValue.PermissionMode);
    }

    [Fact]
    public async Task R4_SuspendedJob_PreExposureRace_PostBarrierExpiry_AndRealBackgroundJobRunnerEndToEnd()
    {
        Directory.CreateDirectory(_root);
        var dbPath = Path.Combine(_root, "r4-jobs", "jobs.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCodexFlowSqliteStorage(dbPath);
        services.AddCodexFlowLocalDefaults();
        services.AddSingleton(new Mock<IBackgroundJobSignal>().Object);

        var outboxSignal = new Mock<IOutboxSignal>().Object;
        var workerHookDispatcher = new CodexFlow.Services.Hooks.WorkerHookDispatcher(
            [],
            NullLogger<CodexFlow.Services.Hooks.WorkerHookDispatcher>.Instance);
        var planDispatch = new Mock<IPlanTaskDispatchService>().Object;
        var sessionDir = new Mock<ISessionDirectory>();
        sessionDir
            .Setup(s => s.FindAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string sid, CancellationToken _) => new SessionRecord(sid, LocalUser.Id, _root));

        services.AddSingleton(outboxSignal);
        services.AddSingleton<CodexFlow.Application.Hooks.IWorkerHookDispatcher>(workerHookDispatcher);
        services.AddSingleton(planDispatch);
        services.AddSingleton(sessionDir.Object);
        services.AddScoped<IBackgroundJobService, BackgroundJobService>();
        services.AddSingleton<IBackgroundJobApprovalResumer, BackgroundJobApprovalResumer>();

        var optsMonitor = new ExecutionApprovalAndPermissionTests.MutableOptionsMonitor<ExecutionOptions>(new ExecutionOptions
        {
            Backend = ExecutionBackend.Docker,
            PermissionMode = ExecutionPermissionMode.Ask,
            HeadlessApprovalTimeout = TimeSpan.Zero,
            ApprovalTimeout = TimeSpan.FromMinutes(5),
            SuspendUnsubscribedBackgroundJobs = true
        });
        var permStore = new ExecutionPermissionStore(Path.Combine(_root, "r4-perms.json"));
        var unsubscribedGateway = new ExecutionApprovalAndPermissionTests.RecordingGateway(hasSubscribers: false);
        services.AddSingleton<CodexFlow.Core.Gateway.ICodexGateway>(unsubscribedGateway);
        services.AddSingleton(new CodexFlow.Core.Services.InMemoryWorkflowAuditStore());
        services.AddSingleton<IWorkflowAuditStore>(p => p.GetRequiredService<CodexFlow.Core.Services.InMemoryWorkflowAuditStore>());
        services.AddSingleton<IExecutionApprovalBroker>(p => new ExecutionApprovalBroker(
            optsMonitor,
            permStore,
            NullLogger<ExecutionApprovalBroker>.Instance,
            serviceProvider: p,
            gateway: unsubscribedGateway));

        await using var sp = services.BuildServiceProvider();
        await using (var initScope = sp.CreateAsyncScope())
        {
            var db = initScope.ServiceProvider.GetRequiredService<JobDbContext>();
            await db.Database.MigrateAsync();
        }

        var broker = sp.GetRequiredService<IExecutionApprovalBroker>();
        var accessor = new TrustedExecutionContextAccessor();
        var runnerCalls = 0;
        var fakeDocker = new CountingDockerExecutionService(() => Interlocked.Increment(ref runnerCalls));
        var gate = new ToolIsolationGate(fakeDocker, accessor, new IsolationOptions { StagingRoot = Path.Combine(_root, "r4-stg") }, broker, optsMonitor);
        var execCode = new ExecCodeTool(NullLogger<ExecCodeTool>.Instance, gate);

        // --- Subtest 1: Pre-exposure race (SubmitDecisionAsync fires INSIDE PublishEventAsync before TriggerSuspension) ---
        var runner = new ToolExecutingBackgroundJobRunner(
            sp,
            NullLogger<BackgroundJobRunner>.Instance,
            outboxSignal,
            workerHookDispatcher,
            planDispatch,
            async (job, workerToken) =>
            {
                using var ctxScope = accessor.Begin(new TrustedExecutionContext
                {
                    UserId = LocalUser.Id,
                    SessionId = job.SessionId!,
                    JobId = job.JobId,
                    TaskId = job.TaskId ?? "T1",
                    AttemptId = job.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    WorkspaceRoot = _root
                });
                var toolRes = await execCode.ExecuteAsync(
                    new Dictionary<string, object?> { ["code"] = "print('real-runner-e2e')", ["language"] = "python" },
                    workerToken);
                Assert.Equal(ToolResultStatus.Success, toolRes.Status);
                return "{\"success\":true,\"summary\":\"completed after approval\"}";
            });

        string realJobId;
        await using (var scope = sp.CreateAsyncScope())
        {
            var jobSvc = scope.ServiceProvider.GetRequiredService<IBackgroundJobService>();
            realJobId = await jobSvc.CreateJobAsync("sess-r4-real", "T-R4", "ApprovalTestJob", "{\"sessionId\":\"sess-r4-real\"}");
        }

        Task<ApprovalSubmissionResult>? instantSubmitDuringSse = null;
        unsubscribedGateway.OnPublish = (sid, evt) =>
        {
            if (sid == "sess-r4-real" && evt.Type == CodexFlow.Core.Gateway.GatewaySseEventType.ToolApprovalRequest)
            {
                var item = Assert.Single(broker.GetPendingApprovals("sess-r4-real"));
                // Fire SubmitDecisionAsync IMMEDIATELY during SSE publish (before AuthorizeAsync even throws suspension!)
                instantSubmitDuringSse = Task.Run(() => broker.SubmitDecisionAsync(
                    item.ApprovalId,
                    new ExecutionApprovalDecisionSubmission(item.RequestDigest, ExecutionApprovalDecisionKind.AllowOnce, "ctrl")));
            }
        };

        // Run 1st pass through REAL BackgroundJobRunner -> suspends to WaitingUser, commits SQLite checkpoint, opens barrier!
        await runner.ExecuteInlineAsync(realJobId, CancellationToken.None);
        unsubscribedGateway.OnPublish = null;

        Assert.NotNull(instantSubmitDuringSse);
        var instantResult = await instantSubmitDuringSse!;
        Assert.Equal(ApprovalSubmissionOutcome.Resolved, instantResult.Outcome);
        Assert.Equal(0, runnerCalls); // Still 0 before resumed execution!

        // Resumer already transitioned realJobId from WaitingUser -> Queued; now run 2nd pass through REAL BackgroundJobRunner!
        await runner.ExecuteInlineAsync(realJobId, CancellationToken.None);
        Assert.Equal(1, runnerCalls);

        await using (var verifyScope = sp.CreateAsyncScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<JobDbContext>();
            var completedJob = await db.BackgroundJobs.FindAsync(realJobId);
            Assert.NotNull(completedJob);
            Assert.Equal(BackgroundJobStatus.Completed, completedJob!.Status);
        }

        // --- Subtest 2: Approval expires WHILE SubmitDecisionAsync is waiting on PersistenceReadyTask barrier ---
        optsMonitor.Set(new ExecutionOptions
        {
            Backend = ExecutionBackend.Docker,
            PermissionMode = ExecutionPermissionMode.Ask,
            HeadlessApprovalTimeout = TimeSpan.Zero,
            ApprovalTimeout = TimeSpan.FromMilliseconds(90),
            SuspendUnsubscribedBackgroundJobs = true
        });

        string expiringJobId;
        await using (var scope = sp.CreateAsyncScope())
        {
            var jobSvc = scope.ServiceProvider.GetRequiredService<IBackgroundJobService>();
            expiringJobId = await jobSvc.CreateJobAsync("sess-r4-exp", "T-EXP", "ForgeWorker", "{}", startRunning: true);
        }

        using (AmbientBackgroundJobApprovalContext.BeginScope(expiringJobId, "tok-exp", CancellationToken.None, out var expSuspension))
        using (accessor.Begin(new TrustedExecutionContext
               {
                   UserId = LocalUser.Id,
                   SessionId = "sess-r4-exp",
                   JobId = expiringJobId,
                   TaskId = "T-EXP",
                   AttemptId = "1",
                   WorkspaceRoot = _root
               }))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                execCode.ExecuteAsync(
                    new Dictionary<string, object?> { ["code"] = "print('expire-on-barrier')", ["language"] = "python" },
                    expSuspension.EffectiveToken));

            var itemExp = Assert.Single(broker.GetPendingApprovals("sess-r4-exp"));
            var submitWhileUncommitted = Task.Run(() => broker.SubmitDecisionAsync(
                itemExp.ApprovalId,
                new ExecutionApprovalDecisionSubmission(itemExp.RequestDigest, ExecutionApprovalDecisionKind.AllowOnce, "ctrl")));

            // Hold barrier past the 90ms ApprovalTimeout!
            await Task.Delay(160);
            await PersistWaitingUserCheckpointAsync(sp, expiringJobId, "sess-r4-exp", "tok-exp");
            expSuspension.NotifyPersistenceCommitted();

            var expiredOutcome = await submitWhileUncommitted;
            Assert.Equal(ApprovalSubmissionOutcome.AlreadyResolvedOrExpired, expiredOutcome.Outcome);
        }

        // --- Subtest 3 (Round 3 B3): PermissionMode change AFTER job suspended into WaitingUser invalidates pending approval;
        // AND Backend change WHILE SubmitDecisionAsync is waiting on PersistenceReadyTask invalidates atomic claim! ---
        optsMonitor.Set(new ExecutionOptions
        {
            Backend = ExecutionBackend.Docker,
            PermissionMode = ExecutionPermissionMode.Ask,
            HeadlessApprovalTimeout = TimeSpan.Zero,
            ApprovalTimeout = TimeSpan.FromMinutes(5),
            SuspendUnsubscribedBackgroundJobs = true
        });

        string modeDriftJobId;
        await using (var scope = sp.CreateAsyncScope())
        {
            var jobSvc = scope.ServiceProvider.GetRequiredService<IBackgroundJobService>();
            modeDriftJobId = await jobSvc.CreateJobAsync("sess-r4-modedrift", "T-MD", "ForgeWorker", "{}", startRunning: true);
        }

        using (AmbientBackgroundJobApprovalContext.BeginScope(modeDriftJobId, "tok-md", CancellationToken.None, out var mdSuspension))
        using (accessor.Begin(new TrustedExecutionContext
               {
                   UserId = LocalUser.Id,
                   SessionId = "sess-r4-modedrift",
                   JobId = modeDriftJobId,
                   TaskId = "T-MD",
                   AttemptId = "1",
                   WorkspaceRoot = _root
               }))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                execCode.ExecuteAsync(
                    new Dictionary<string, object?> { ["code"] = "print('mode-drift')", ["language"] = "python" },
                    mdSuspension.EffectiveToken));
            await PersistWaitingUserCheckpointAsync(sp, modeDriftJobId, "sess-r4-modedrift", "tok-md");
            mdSuspension.NotifyPersistenceCommitted();
        }

        var itemModeDrift = Assert.Single(broker.GetPendingApprovals("sess-r4-modedrift"));
        // Change PermissionMode (Ask -> AutoInWorkspace) AFTER AuthorizeAsync exited into WaitingUser!
        optsMonitor.Set(new ExecutionOptions
        {
            Backend = ExecutionBackend.Docker,
            PermissionMode = ExecutionPermissionMode.AutoInWorkspace,
            HeadlessApprovalTimeout = TimeSpan.Zero,
            ApprovalTimeout = TimeSpan.FromMinutes(5),
            SuspendUnsubscribedBackgroundJobs = true
        });
        Assert.Empty(broker.GetPendingApprovals("sess-r4-modedrift"));
        var submitAfterModeDrift = await broker.SubmitDecisionAsync(
            itemModeDrift.ApprovalId,
            new ExecutionApprovalDecisionSubmission(itemModeDrift.RequestDigest, ExecutionApprovalDecisionKind.AllowOnce, "ctrl"));
        Assert.True(submitAfterModeDrift.Outcome is ApprovalSubmissionOutcome.NotFound or ApprovalSubmissionOutcome.AlreadyResolvedOrExpired);

        // Backend drift WHILE blocked on PersistenceReadyTask barrier (deterministic handshake!)
        optsMonitor.Set(new ExecutionOptions
        {
            Backend = ExecutionBackend.Docker,
            PermissionMode = ExecutionPermissionMode.Ask,
            HeadlessApprovalTimeout = TimeSpan.Zero,
            ApprovalTimeout = TimeSpan.FromMinutes(5),
            SuspendUnsubscribedBackgroundJobs = true
        });

        string backendDriftJobId;
        await using (var scope = sp.CreateAsyncScope())
        {
            var jobSvc = scope.ServiceProvider.GetRequiredService<IBackgroundJobService>();
            backendDriftJobId = await jobSvc.CreateJobAsync("sess-r4-bgdrift", "T-BD", "ForgeWorker", "{}", startRunning: true);
        }

        using (AmbientBackgroundJobApprovalContext.BeginScope(backendDriftJobId, "tok-bd", CancellationToken.None, out var bdSuspension))
        using (accessor.Begin(new TrustedExecutionContext
               {
                   UserId = LocalUser.Id,
                   SessionId = "sess-r4-bgdrift",
                   JobId = backendDriftJobId,
                   TaskId = "T-BD",
                   AttemptId = "1",
                   WorkspaceRoot = _root
               }))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                execCode.ExecuteAsync(
                    new Dictionary<string, object?> { ["code"] = "print('backend-drift-barrier')", ["language"] = "python" },
                    bdSuspension.EffectiveToken));

            var itemBd = Assert.Single(broker.GetPendingApprovals("sess-r4-bgdrift"));
            var reachedBarrierTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var concreteBroker = (ExecutionApprovalBroker)broker;
            concreteBroker.OnAwaitingSuspendedPersistenceBarrierForTesting = aid =>
            {
                if (aid == itemBd.ApprovalId)
                {
                    reachedBarrierTcs.TrySetResult();
                }
            };

            var bdSubmitTask = broker.SubmitDecisionAsync(
                itemBd.ApprovalId,
                new ExecutionApprovalDecisionSubmission(itemBd.RequestDigest, ExecutionApprovalDecisionKind.AllowOnce, "ctrl"));
            Assert.False(bdSubmitTask.IsCompleted);

            // Deterministically confirm SubmitDecisionAsync has ACTUALLY arrived at `await PersistenceReadyTask` inside the broker!
            await reachedBarrierTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            concreteBroker.OnAwaitingSuspendedPersistenceBarrierForTesting = null;

            // Flip Backend (Docker -> Local) WHILE SubmitDecisionAsync is parked on the persistence barrier!
            optsMonitor.Set(new ExecutionOptions
            {
                Backend = ExecutionBackend.Local,
                PermissionMode = ExecutionPermissionMode.Ask,
                HeadlessApprovalTimeout = TimeSpan.Zero,
                ApprovalTimeout = TimeSpan.FromMinutes(5),
                SuspendUnsubscribedBackgroundJobs = true
            });
            await PersistWaitingUserCheckpointAsync(sp, backendDriftJobId, "sess-r4-bgdrift", "tok-bd");
            bdSuspension.NotifyPersistenceCommitted();

            var bdOutcome = await bdSubmitTask;
            Assert.Equal(ApprovalSubmissionOutcome.AlreadyResolvedOrExpired, bdOutcome.Outcome);
        }

        // Verify SQLite job state was deterministically terminated to Failed (even though persistence committed AFTER config change)!
        var deadlineCheck = DateTimeOffset.UtcNow.AddSeconds(3);
        BackgroundJobStatus finalBdStatus = BackgroundJobStatus.WaitingUser;
        while (DateTimeOffset.UtcNow < deadlineCheck)
        {
            await using var dbScope = sp.CreateAsyncScope();
            var db = dbScope.ServiceProvider.GetRequiredService<JobDbContext>();
            var driftedJob = await db.BackgroundJobs.FindAsync(backendDriftJobId);
            finalBdStatus = driftedJob!.Status;
            if (finalBdStatus == BackgroundJobStatus.Failed)
            {
                break;
            }

            await Task.Delay(25);
        }

        Assert.Equal(BackgroundJobStatus.Failed, finalBdStatus);

        // No extra ticket may have been minted!
        Assert.Equal(1, runnerCalls);
    }

    [Fact]
    public async Task SuspendedJob_ResumerTransientFailures_Cancellation_AndTerminationRetryQueue_RecoverCleanly()
    {
        Directory.CreateDirectory(_root);
        var dbPath = Path.Combine(_root, "r5-jobs", "jobs.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCodexFlowSqliteStorage(dbPath);
        services.AddCodexFlowLocalDefaults();
        services.AddSingleton(new Mock<IBackgroundJobSignal>().Object);

        var outboxSignal = new Mock<IOutboxSignal>().Object;
        var workerHookDispatcher = new CodexFlow.Services.Hooks.WorkerHookDispatcher(
            [],
            NullLogger<CodexFlow.Services.Hooks.WorkerHookDispatcher>.Instance);
        var planDispatch = new Mock<IPlanTaskDispatchService>().Object;
        var sessionDir = new Mock<ISessionDirectory>();
        sessionDir
            .Setup(s => s.FindAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string sid, CancellationToken _) => new SessionRecord(sid, LocalUser.Id, _root));

        services.AddSingleton(outboxSignal);
        services.AddSingleton<CodexFlow.Application.Hooks.IWorkerHookDispatcher>(workerHookDispatcher);
        services.AddSingleton(planDispatch);
        services.AddSingleton(sessionDir.Object);
        services.AddScoped<IBackgroundJobService, BackgroundJobService>();
        services.AddSingleton<BackgroundJobApprovalResumer>();
        services.AddSingleton<FlakyApprovalResumer>(p => new FlakyApprovalResumer(p.GetRequiredService<BackgroundJobApprovalResumer>()));
        services.AddSingleton<IBackgroundJobApprovalResumer>(p => p.GetRequiredService<FlakyApprovalResumer>());

        var optsMonitor = new ExecutionApprovalAndPermissionTests.MutableOptionsMonitor<ExecutionOptions>(new ExecutionOptions
        {
            Backend = ExecutionBackend.Docker,
            PermissionMode = ExecutionPermissionMode.Ask,
            HeadlessApprovalTimeout = TimeSpan.Zero,
            ApprovalTimeout = TimeSpan.FromMinutes(5),
            SuspendUnsubscribedBackgroundJobs = true
        });
        var permStore = new ExecutionPermissionStore(Path.Combine(_root, "r5-perms.json"));
        var unsubscribedGateway = new ExecutionApprovalAndPermissionTests.RecordingGateway(hasSubscribers: false);
        services.AddSingleton<CodexFlow.Core.Gateway.ICodexGateway>(unsubscribedGateway);
        services.AddSingleton(new CodexFlow.Core.Services.InMemoryWorkflowAuditStore());
        services.AddSingleton<IWorkflowAuditStore>(p => p.GetRequiredService<CodexFlow.Core.Services.InMemoryWorkflowAuditStore>());
        services.AddSingleton<IExecutionApprovalBroker>(p => new ExecutionApprovalBroker(
            optsMonitor,
            permStore,
            NullLogger<ExecutionApprovalBroker>.Instance,
            serviceProvider: p,
            gateway: unsubscribedGateway));

        await using var sp = services.BuildServiceProvider();
        await using (var initScope = sp.CreateAsyncScope())
        {
            var db = initScope.ServiceProvider.GetRequiredService<JobDbContext>();
            await db.Database.MigrateAsync();
        }

        var flakyResumer = sp.GetRequiredService<FlakyApprovalResumer>();
        var broker = sp.GetRequiredService<IExecutionApprovalBroker>();
        var accessor = new TrustedExecutionContextAccessor();
        var dockerRuns = 0;
        var fakeDocker = new CountingDockerExecutionService(() => Interlocked.Increment(ref dockerRuns));
        var gate = new ToolIsolationGate(fakeDocker, accessor, new IsolationOptions { StagingRoot = Path.Combine(_root, "r5-stg") }, broker, optsMonitor);
        var execCode = new ExecCodeTool(NullLogger<ExecCodeTool>.Instance, gate);

        var runner = new ToolExecutingBackgroundJobRunner(
            sp,
            NullLogger<BackgroundJobRunner>.Instance,
            outboxSignal,
            workerHookDispatcher,
            planDispatch,
            async (job, workerToken) =>
            {
                using var ctxScope = accessor.Begin(new TrustedExecutionContext
                {
                    UserId = LocalUser.Id,
                    SessionId = job.SessionId!,
                    JobId = job.JobId,
                    TaskId = job.TaskId ?? "T1",
                    AttemptId = job.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    WorkspaceRoot = _root
                });
                var toolRes = await execCode.ExecuteAsync(
                    new Dictionary<string, object?> { ["code"] = "print('r5-recovery')", ["language"] = "python" },
                    workerToken);
                Assert.Equal(ToolResultStatus.Success, toolRes.Status);
                return "{\"success\":true}";
            });

        // --- Case A: System termination survives > 4 consecutive DB failures (5 injected throws, 6th succeeds) ---
        string sysRetryJobId;
        await using (var scope = sp.CreateAsyncScope())
        {
            var jobSvc = scope.ServiceProvider.GetRequiredService<IBackgroundJobService>();
            sysRetryJobId = await jobSvc.CreateJobAsync("sess-r5-sysretry", "T-SR", "ApprovalTestJob", "{\"sessionId\":\"sess-r5-sysretry\"}");
        }

        await runner.ExecuteInlineAsync(sysRetryJobId, CancellationToken.None);
        Assert.Single(broker.GetPendingApprovals("sess-r5-sysretry"));

        var sysAttemptCount = 0;
        flakyResumer.Interceptor = (_, _, _, _, _) =>
        {
            var n = Interlocked.Increment(ref sysAttemptCount);
            if (n <= 5)
            {
                throw new InvalidOperationException($"Simulated SQLite SQLITE_BUSY #{n}");
            }

            return null;
        };

        // Trigger config invalidation -> EnqueueSystemTermination -> recovery pump must retry past 5 failures and apply on 6th!
        optsMonitor.Set(new ExecutionOptions
        {
            Backend = ExecutionBackend.Local,
            PermissionMode = ExecutionPermissionMode.Ask,
            HeadlessApprovalTimeout = TimeSpan.Zero,
            ApprovalTimeout = TimeSpan.FromMinutes(5),
            SuspendUnsubscribedBackgroundJobs = true
        });

        await WaitForJobStatusAsync(sp, sysRetryJobId, BackgroundJobStatus.Failed);
        Assert.True(Volatile.Read(ref sysAttemptCount) >= 6);
        flakyResumer.Interceptor = null;

        // --- Case B: Request CancellationToken cancellation while awaiting barrier + config drift still terminates DB independently ---
        optsMonitor.Set(new ExecutionOptions
        {
            Backend = ExecutionBackend.Docker,
            PermissionMode = ExecutionPermissionMode.Ask,
            HeadlessApprovalTimeout = TimeSpan.Zero,
            ApprovalTimeout = TimeSpan.FromMinutes(5),
            SuspendUnsubscribedBackgroundJobs = true
        });

        string cancelTermJobId;
        await using (var scope = sp.CreateAsyncScope())
        {
            var jobSvc = scope.ServiceProvider.GetRequiredService<IBackgroundJobService>();
            cancelTermJobId = await jobSvc.CreateJobAsync("sess-r5-cancelterm", "T-CT", "ForgeWorker", "{}", startRunning: true);
        }

        using (AmbientBackgroundJobApprovalContext.BeginScope(cancelTermJobId, "tok-ct", CancellationToken.None, out var ctSuspension))
        using (accessor.Begin(new TrustedExecutionContext
               {
                   UserId = LocalUser.Id,
                   SessionId = "sess-r5-cancelterm",
                   JobId = cancelTermJobId,
                   TaskId = "T-CT",
                   AttemptId = "1",
                   WorkspaceRoot = _root
               }))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                execCode.ExecuteAsync(
                    new Dictionary<string, object?> { ["code"] = "print('ct-term')", ["language"] = "python" },
                    ctSuspension.EffectiveToken));

            var itemCt = Assert.Single(broker.GetPendingApprovals("sess-r5-cancelterm"));
            using var reqCts = new CancellationTokenSource();
            var barrierReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var concreteBroker = (ExecutionApprovalBroker)broker;
            concreteBroker.OnAwaitingSuspendedPersistenceBarrierForTesting = aid =>
            {
                if (aid == itemCt.ApprovalId)
                {
                    barrierReached.TrySetResult();
                }
            };

            var pendingSubmit = broker.SubmitDecisionAsync(
                itemCt.ApprovalId,
                new ExecutionApprovalDecisionSubmission(itemCt.RequestDigest, ExecutionApprovalDecisionKind.AllowOnce, "ctrl"),
                reqCts.Token);
            await barrierReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            concreteBroker.OnAwaitingSuspendedPersistenceBarrierForTesting = null;

            // Drift config AND cancel caller HTTP request token while parked on barrier!
            optsMonitor.Set(new ExecutionOptions
            {
                Backend = ExecutionBackend.Local,
                PermissionMode = ExecutionPermissionMode.Ask,
                HeadlessApprovalTimeout = TimeSpan.Zero,
                ApprovalTimeout = TimeSpan.FromMinutes(5),
                SuspendUnsubscribedBackgroundJobs = true
            });
            await reqCts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingSubmit);

            // Commit DB persistence AFTER caller request already aborted -> host-scoped termination pump MUST still transition to Failed!
            await PersistWaitingUserCheckpointAsync(sp, cancelTermJobId, "sess-r5-cancelterm", "tok-ct");
            ctSuspension.NotifyPersistenceCommitted();
        }

        await WaitForJobStatusAsync(sp, cancelTermJobId, BackgroundJobStatus.Failed);

        // --- Cases C, D, E: User AllowOnce submission survives resumer exception, RetryableFailure (false), and caller CT cancellation,
        // revoking pre-minted ticket on each failure and succeeding cleanly on subsequent retry! ---
        optsMonitor.Set(new ExecutionOptions
        {
            Backend = ExecutionBackend.Docker,
            PermissionMode = ExecutionPermissionMode.Ask,
            HeadlessApprovalTimeout = TimeSpan.Zero,
            ApprovalTimeout = TimeSpan.FromMinutes(5),
            SuspendUnsubscribedBackgroundJobs = true
        });

        string userRetryJobId;
        await using (var scope = sp.CreateAsyncScope())
        {
            var jobSvc = scope.ServiceProvider.GetRequiredService<IBackgroundJobService>();
            userRetryJobId = await jobSvc.CreateJobAsync("sess-r5-userretry", "T-UR", "ApprovalTestJob", "{\"sessionId\":\"sess-r5-userretry\"}");
        }

        await runner.ExecuteInlineAsync(userRetryJobId, CancellationToken.None);
        var itemUr = Assert.Single(broker.GetPendingApprovals("sess-r5-userretry"));
        var allowOnceSub = new ExecutionApprovalDecisionSubmission(itemUr.RequestDigest, ExecutionApprovalDecisionKind.AllowOnce, "ctrl");

        async Task AssertNoLeakedTicketWhileStillPendingAsync()
        {
            Assert.Single(broker.GetPendingApprovals("sess-r5-userretry"));
            using (accessor.Begin(new TrustedExecutionContext
                   {
                       UserId = LocalUser.Id,
                       SessionId = "sess-r5-userretry",
                       JobId = userRetryJobId,
                       TaskId = "T-UR",
                       AttemptId = "1",
                       WorkspaceRoot = _root
                   }))
            {
                optsMonitor.Set(new ExecutionOptions
                {
                    Backend = ExecutionBackend.Docker,
                    PermissionMode = ExecutionPermissionMode.Ask,
                    HeadlessApprovalTimeout = TimeSpan.Zero,
                    ApprovalTimeout = TimeSpan.FromMinutes(5),
                    SuspendUnsubscribedBackgroundJobs = false
                });
                var unapprovedProbe = await execCode.ExecuteAsync(
                    new Dictionary<string, object?> { ["code"] = "print('r5-recovery')", ["language"] = "python" },
                    CancellationToken.None);
                Assert.NotEqual(ToolResultStatus.Success, unapprovedProbe.Status);
                Assert.Equal(0, dockerRuns);

                optsMonitor.Set(new ExecutionOptions
                {
                    Backend = ExecutionBackend.Docker,
                    PermissionMode = ExecutionPermissionMode.Ask,
                    HeadlessApprovalTimeout = TimeSpan.Zero,
                    ApprovalTimeout = TimeSpan.FromMinutes(5),
                    SuspendUnsubscribedBackgroundJobs = true
                });
            }
        }

        // (C) Resumer throws transient exception -> returns TransientFailure, entry stays pending & retryable, ticket independently verified revoked!
        flakyResumer.Interceptor = (_, _, _, _, _) => throw new InvalidOperationException("Simulated transient DB write failure");
        var resException = await broker.SubmitDecisionAsync(itemUr.ApprovalId, allowOnceSub);
        Assert.Equal(ApprovalSubmissionOutcome.TransientFailure, resException.Outcome);
        await AssertNoLeakedTicketWhileStillPendingAsync();

        // (D) Resumer returns RetryableFailure -> returns TransientFailure, entry stays pending & retryable, ticket independently verified revoked!
        flakyResumer.Interceptor = (_, _, _, _, _) => Task.FromResult(SuspendedJobResolutionStatus.RetryableFailure);
        var resFalse = await broker.SubmitDecisionAsync(itemUr.ApprovalId, allowOnceSub);
        Assert.Equal(ApprovalSubmissionOutcome.TransientFailure, resFalse.Outcome);
        await AssertNoLeakedTicketWhileStillPendingAsync();

        // (E) Resumer cancelled by caller HTTP CancellationToken -> throws OperationCanceledException, ticket independently verified revoked!
        using (var midResumerCts = new CancellationTokenSource())
        {
            flakyResumer.Interceptor = async (_, _, _, _, token) =>
            {
                await midResumerCts.CancelAsync();
                token.ThrowIfCancellationRequested();
                return SuspendedJobResolutionStatus.Applied;
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                broker.SubmitDecisionAsync(itemUr.ApprovalId, allowOnceSub, midResumerCts.Token));
        }

        await AssertNoLeakedTicketWhileStillPendingAsync();

        // Now clear fault injection and retry SubmitDecisionAsync(AllowOnce) -> MUST succeed and resume SQLite job to Queued!
        flakyResumer.Interceptor = null;
        var resRecovered = await broker.SubmitDecisionAsync(itemUr.ApprovalId, allowOnceSub);
        Assert.Equal(ApprovalSubmissionOutcome.Resolved, resRecovered.Outcome);
        Assert.Empty(broker.GetPendingApprovals("sess-r5-userretry"));

        await WaitForJobStatusAsync(sp, userRetryJobId, BackgroundJobStatus.Queued);
        await runner.ExecuteInlineAsync(userRetryJobId, CancellationToken.None);
        Assert.Equal(1, dockerRuns);
        await WaitForJobStatusAsync(sp, userRetryJobId, BackgroundJobStatus.Completed);
    }

    [Fact]
    public async Task R6_SettingsTightenRace_ResumeGrantPublishGate_IdleExpiryTimer_AndHttp503TransientFailure()
    {
        Directory.CreateDirectory(_root);

        // =========================================================================
        // 1. B6-1: Normal-plane PUT /api/settings/execution check-then-write race
        // =========================================================================
        using var pipeReader = new StringReader(JsonSerializer.Serialize(new
        {
            accessToken = "r6-normal-token-1111111111",
            controlToken = "r6-control-token-9999999999"
        }) + "\n");
        var creds = LocalHostCredentials.Initialize(
            Path.Combine(_root, "r6-state"),
            LocalTokenDeliveryMode.Pipe,
            ExecutionBackend.Docker,
            unsafeFileControlToken: false,
            pipeReader: pipeReader);

        var settingsPath = Path.Combine(_root, "r6-global", "appsettings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var realSettingsService = new ExecutionSettingsService(settingsPath);
        _ = realSettingsService.Update(new ExecutionSettingsUpdate(ExecutionBackend.Docker, ExecutionPermissionMode.FullAuto));
        var hookableSettings = new HookableSettingsService(realSettingsService);

        var httpFlakyResumer = new Mock<IBackgroundJobApprovalResumer>();
        httpFlakyResumer
            .Setup(r => r.TryApplyResolutionAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<PendingApprovalItem>(),
                It.IsAny<ExecutionApprovalDecisionSubmission>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuspendedJobResolutionStatus.RetryableFailure);

        await using (var host = await StartTestControlPlaneHostAsync(
                         creds,
                         new ExecutionOptions
                         {
                             Backend = ExecutionBackend.Docker,
                             PermissionMode = ExecutionPermissionMode.FullAuto,
                             HeadlessApprovalTimeout = TimeSpan.Zero,
                             ApprovalTimeout = TimeSpan.FromMinutes(5),
                             SuspendUnsubscribedBackgroundJobs = true
                         },
                         settingsPath,
                         hasSubscribers: false,
                         customJobResumer: httpFlakyResumer.Object,
                         customSettingsService: hookableSettings))
        {
            using var normalClient = host.CreateClient(creds.AccessToken);
            using var controlClient = host.CreateClient(creds.ControlToken!);

            // Deterministic handshake: Hold tightening request B inside `settingsService.Update(Deny)` under the atomic lock,
            // launch normal-plane request A (`FullAuto`) concurrently so A queues right on the same gate, then release B!
            // Under the old code without atomic lock, A could read `FullAuto` before B finished and overwrite `Deny` after B!
            var enteredDenyPersist = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDenyPersist = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fullAutoPersistCallsAfterDeny = 0;

            hookableSettings.OnBeforeUpdate = upd =>
            {
                if (upd.PermissionMode == ExecutionPermissionMode.Deny)
                {
                    enteredDenyPersist.TrySetResult();
                    Assert.True(releaseDenyPersist.Task.Wait(TimeSpan.FromSeconds(5)));
                }
                else if (enteredDenyPersist.Task.IsCompleted && upd.PermissionMode == ExecutionPermissionMode.FullAuto)
                {
                    Interlocked.Increment(ref fullAutoPersistCallsAfterDeny);
                }
            };

            var tightenTask = normalClient.PutAsJsonAsync("/api/settings/execution", new { permissionMode = "Deny" });
            await enteredDenyPersist.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Launch normal-plane FullAuto request WHILE B is mid-update, waiting until A actually arrives right before `lock (_gate)` inside TryApplySettingsUpdateAtomically!
            var competingReachedClampGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Clamp.OnEnteringAtomicUpdateForTesting = requestedMode =>
            {
                if (requestedMode == ExecutionPermissionMode.FullAuto)
                {
                    competingReachedClampGate.TrySetResult();
                }
            };
            var competingFullAutoTask = normalClient.PutAsJsonAsync("/api/settings/execution", new { permissionMode = "FullAuto" });
            await competingReachedClampGate.Task.WaitAsync(TimeSpan.FromSeconds(5));
            host.Clamp.OnEnteringAtomicUpdateForTesting = null;
            Assert.False(competingFullAutoTask.IsCompleted);

            releaseDenyPersist.TrySetResult();

            var tightenResp = await tightenTask;
            var competingResp = await competingFullAutoTask;
            hookableSettings.OnBeforeUpdate = null;

            Assert.Equal(HttpStatusCode.OK, tightenResp.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, competingResp.StatusCode);
            Assert.Equal(0, Volatile.Read(ref fullAutoPersistCallsAfterDeny));

            // Final effective state in API AND on disk MUST be Deny (never reverted to FullAuto)!
            var finalSettings = await normalClient.GetFromJsonAsync<ExecutionSettingsDto>("/api/settings/execution");
            Assert.NotNull(finalSettings);
            Assert.Equal("Deny", finalSettings!.PermissionMode);
            Assert.Equal(ExecutionPermissionMode.Deny, realSettingsService.Read().PermissionMode);

            // Subsequent normal-plane FullAuto request strictly receives 403 Forbidden.
            var postTightenRelax = await normalClient.PutAsJsonAsync("/api/settings/execution", new { permissionMode = "FullAuto" });
            Assert.Equal(HttpStatusCode.Forbidden, postTightenRelax.StatusCode);

            // =========================================================================
            // 2. Advisory 3: Real HTTP 503 ServiceUnavailable + "transient_failure" body
            // =========================================================================
            var restoreAsk = await controlClient.PutAsJsonAsync("/api/settings/execution", new { permissionMode = "Ask" });
            Assert.Equal(HttpStatusCode.OK, restoreAsk.StatusCode);
            host.OptionsMonitor.Set(new ExecutionOptions
            {
                Backend = ExecutionBackend.Docker,
                PermissionMode = ExecutionPermissionMode.Ask,
                HeadlessApprovalTimeout = TimeSpan.Zero,
                ApprovalTimeout = TimeSpan.FromMinutes(5),
                SuspendUnsubscribedBackgroundJobs = true
            });

            var httpAccessor = new TrustedExecutionContextAccessor();
            var httpFakeDocker = new CountingDockerExecutionService(() => { });
            var httpGate = new ToolIsolationGate(httpFakeDocker, httpAccessor, new IsolationOptions { StagingRoot = Path.Combine(_root, "r6-http-stg") }, host.Broker, host.OptionsMonitor);
            var httpExec = new ExecCodeTool(NullLogger<ExecCodeTool>.Instance, httpGate);

            using (AmbientBackgroundJobApprovalContext.BeginScope("job-http-503", "tok-503", CancellationToken.None, out var httpSusp))
            using (httpAccessor.Begin(new TrustedExecutionContext
                   {
                       UserId = LocalUser.Id,
                       SessionId = "s-owned",
                       JobId = "job-http-503",
                       TaskId = "T-503",
                       AttemptId = "1",
                       WorkspaceRoot = _root
                   }))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    httpExec.ExecuteAsync(
                        new Dictionary<string, object?> { ["code"] = "print('503')", ["language"] = "python" },
                        httpSusp.EffectiveToken));
                httpSusp.NotifyPersistenceCommitted();

                var pendingHttpItem = Assert.Single(host.Broker.GetPendingApprovals("s-owned"));
                var httpResp = await controlClient.PostAsJsonAsync(
                    $"/api/gateway/approvals/{pendingHttpItem.ApprovalId}",
                    new { requestDigest = pendingHttpItem.RequestDigest, decision = "allow_once" });

                Assert.Equal(HttpStatusCode.ServiceUnavailable, httpResp.StatusCode);
                var bodyText = await httpResp.Content.ReadAsStringAsync();
                Assert.Contains("\"code\":\"transient_failure\"", bodyText, StringComparison.Ordinal);
            }
        }

        // =========================================================================
        // 3. B6-2: AllowSession & AllowAlways worker race BEFORE grant publication
        // =========================================================================
        var dbPath = Path.Combine(_root, "r6-jobs", "jobs.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCodexFlowSqliteStorage(dbPath);
        services.AddCodexFlowLocalDefaults();
        services.AddSingleton(new Mock<IBackgroundJobSignal>().Object);

        var outboxSignal = new Mock<IOutboxSignal>().Object;
        var workerHookDispatcher = new CodexFlow.Services.Hooks.WorkerHookDispatcher(
            [],
            NullLogger<CodexFlow.Services.Hooks.WorkerHookDispatcher>.Instance);
        var planDispatch = new Mock<IPlanTaskDispatchService>().Object;
        var sessionDir = new Mock<ISessionDirectory>();
        sessionDir
            .Setup(s => s.FindAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string sid, CancellationToken _) => new SessionRecord(sid, LocalUser.Id, _root));

        services.AddSingleton(outboxSignal);
        services.AddSingleton<CodexFlow.Application.Hooks.IWorkerHookDispatcher>(workerHookDispatcher);
        services.AddSingleton(planDispatch);
        services.AddSingleton(sessionDir.Object);
        services.AddScoped<IBackgroundJobService, BackgroundJobService>();
        services.AddSingleton<BackgroundJobApprovalResumer>();
        services.AddSingleton<FlakyApprovalResumer>(p => new FlakyApprovalResumer(p.GetRequiredService<BackgroundJobApprovalResumer>()));
        services.AddSingleton<IBackgroundJobApprovalResumer>(p => p.GetRequiredService<FlakyApprovalResumer>());

        var optsMonitor = new ExecutionApprovalAndPermissionTests.MutableOptionsMonitor<ExecutionOptions>(new ExecutionOptions
        {
            Backend = ExecutionBackend.Docker,
            PermissionMode = ExecutionPermissionMode.Ask,
            HeadlessApprovalTimeout = TimeSpan.Zero,
            ApprovalTimeout = TimeSpan.FromMinutes(5),
            SuspendUnsubscribedBackgroundJobs = true
        });
        var permStore = new ExecutionPermissionStore(Path.Combine(_root, "r6-perms.json"));
        var unsubscribedGateway = new ExecutionApprovalAndPermissionTests.RecordingGateway(hasSubscribers: false);
        services.AddSingleton<CodexFlow.Core.Gateway.ICodexGateway>(unsubscribedGateway);
        services.AddSingleton(new CodexFlow.Core.Services.InMemoryWorkflowAuditStore());
        services.AddSingleton<IWorkflowAuditStore>(p => p.GetRequiredService<CodexFlow.Core.Services.InMemoryWorkflowAuditStore>());
        services.AddSingleton<IExecutionApprovalBroker>(p => new ExecutionApprovalBroker(
            optsMonitor,
            permStore,
            NullLogger<ExecutionApprovalBroker>.Instance,
            serviceProvider: p,
            gateway: unsubscribedGateway));

        await using var sp = services.BuildServiceProvider();
        await using (var initScope = sp.CreateAsyncScope())
        {
            var db = initScope.ServiceProvider.GetRequiredService<JobDbContext>();
            await db.Database.MigrateAsync();
        }

        var flakyResumer = sp.GetRequiredService<FlakyApprovalResumer>();
        var broker = sp.GetRequiredService<IExecutionApprovalBroker>();
        var accessor = new TrustedExecutionContextAccessor();
        var cmdRuns = 0;
        var fakeDocker = new CountingDockerExecutionService(() => Interlocked.Increment(ref cmdRuns));
        var gate = new ToolIsolationGate(fakeDocker, accessor, new IsolationOptions { StagingRoot = Path.Combine(_root, "r6-stg") }, broker, optsMonitor);
        var runCmd = new RunCommandTool(NullLogger<RunCommandTool>.Instance, isolationGate: gate);

        var enteringAuthorizeSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string[] currentCommand = ["dotnet", "--info"];
        var runner = new ToolExecutingBackgroundJobRunner(
            sp,
            NullLogger<BackgroundJobRunner>.Instance,
            outboxSignal,
            workerHookDispatcher,
            planDispatch,
            async (job, workerToken) =>
            {
                enteringAuthorizeSignal.TrySetResult();
                using var ctxScope = accessor.Begin(new TrustedExecutionContext
                {
                    UserId = LocalUser.Id,
                    SessionId = job.SessionId!,
                    JobId = job.JobId,
                    TaskId = job.TaskId ?? "T1",
                    AttemptId = job.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    WorkspaceRoot = _root
                });
                var toolRes = await runCmd.ExecuteAsync(
                    new Dictionary<string, object?> { ["command"] = currentCommand },
                    workerToken);
                Assert.Equal(ToolResultStatus.Success, toolRes.Status);
                return "{\"success\":true}";
            });

        foreach (var (decisionKind, cmdVariant, sessionId) in new[]
                 {
                     (ExecutionApprovalDecisionKind.AllowSession, new[] { "git", "status" }, "sess-r6-allow-session"),
                     (ExecutionApprovalDecisionKind.AllowAlways, new[] { "git", "diff" }, "sess-r6-allow-always")
                 })
        {
            currentCommand = cmdVariant;
            string grantJobId;
            await using (var scope = sp.CreateAsyncScope())
            {
                var jobSvc = scope.ServiceProvider.GetRequiredService<IBackgroundJobService>();
                grantJobId = await jobSvc.CreateJobAsync(sessionId, "T-GRANT", "ApprovalTestJob", $"{{\"sessionId\":\"{sessionId}\"}}");
            }

            // 1st run suspends into WaitingUser
            enteringAuthorizeSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await runner.ExecuteInlineAsync(grantJobId, CancellationToken.None);
            var pendingGrantItem = Assert.Single(broker.GetPendingApprovals(sessionId));

            Task? racingWorkerTask = null;
            var realInnerResumer = sp.GetRequiredService<BackgroundJobApprovalResumer>();
            var concreteBroker = (ExecutionApprovalBroker)broker;
            flakyResumer.Interceptor = async (jid, rtok, pitem, sub, ctok) =>
            {
                // Commit Queued to SQLite via real resumer FIRST:
                var status = await realInnerResumer.TryApplyResolutionAsync(jid, rtok, pitem, sub, ctok);
                Assert.Equal(SuspendedJobResolutionStatus.Applied, status);

                // Deterministically start the worker BEFORE returning `Applied` to SubmitDecisionAsync
                // and wait until the worker ACTUALLY arrives at `_inFlightResumePublishGates` inside AuthorizeAsync (no Task.Delay!)
                var reachedPublishGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                concreteBroker.OnAwaitingResumePublishGateForTesting = (sid, parkedJobId) =>
                {
                    if (sid == sessionId && parkedJobId == jid)
                    {
                        reachedPublishGate.TrySetResult();
                    }
                };
                racingWorkerTask = Task.Run(() => runner.ExecuteInlineAsync(jid, CancellationToken.None));
                await reachedPublishGate.Task.WaitAsync(TimeSpan.FromSeconds(5));
                concreteBroker.OnAwaitingResumePublishGateForTesting = null;
                Assert.False(racingWorkerTask.IsCompleted);
                return status;
            };

            var grantOutcome = await broker.SubmitDecisionAsync(
                pendingGrantItem.ApprovalId,
                new ExecutionApprovalDecisionSubmission(pendingGrantItem.RequestDigest, decisionKind, "ctrl"));
            flakyResumer.Interceptor = null;

            Assert.Equal(ApprovalSubmissionOutcome.Resolved, grantOutcome.Outcome);
            Assert.NotNull(racingWorkerTask);
            await racingWorkerTask!;
            Assert.Empty(broker.GetPendingApprovals(sessionId));
            await WaitForJobStatusAsync(sp, grantJobId, BackgroundJobStatus.Completed);
        }

        Assert.Equal(2, cmdRuns);

        // =========================================================================
        // 4. B6-3 & Advisory 1: Zero-client idle expiration timer + non-HOL-blocking pump
        // =========================================================================
        // First enqueue a head-of-line stalled termination item J0 whose PersistenceReadyTask is NOT yet committed:
        string stalledBarrierJobId;
        await using (var scope = sp.CreateAsyncScope())
        {
            var jobSvc = scope.ServiceProvider.GetRequiredService<IBackgroundJobService>();
            stalledBarrierJobId = await jobSvc.CreateJobAsync("sess-r6-hol", "T-HOL", "ForgeWorker", "{}", startRunning: true);
        }

        var stalledScopeDisp = AmbientBackgroundJobApprovalContext.BeginScope(stalledBarrierJobId, "tok-hol", CancellationToken.None, out var stalledSuspension);
        try
        {
            using (accessor.Begin(new TrustedExecutionContext
                   {
                       UserId = LocalUser.Id,
                       SessionId = "sess-r6-hol",
                       JobId = stalledBarrierJobId,
                       TaskId = "T-HOL",
                       AttemptId = "1",
                       WorkspaceRoot = _root
                   }))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    runCmd.ExecuteAsync(
                        new Dictionary<string, object?> { ["command"] = new[] { "dotnet", "--version" } },
                        stalledSuspension.EffectiveToken));
            }

            // Cancel session so J0 enters _pendingTerminations while its PersistenceReadyTask is STILL incomplete!
            broker.CancelSessionApprovals("sess-r6-hol", "hol_test");

            // Use an unapproved command distinct from the `git diff` granted `AllowAlways` in section 3!
            currentCommand = ["git", "log", "-n", "1"];

            // Now configure short ApprovalTimeout (350ms) and run a real WaitingUser job J1 with ZERO subsequent API/SSE calls!
            optsMonitor.Set(new ExecutionOptions
            {
                Backend = ExecutionBackend.Docker,
                PermissionMode = ExecutionPermissionMode.Ask,
                HeadlessApprovalTimeout = TimeSpan.Zero,
                ApprovalTimeout = TimeSpan.FromMilliseconds(350),
                SuspendUnsubscribedBackgroundJobs = true
            });

            string idleExpireJobId;
            await using (var scope = sp.CreateAsyncScope())
            {
                var jobSvc = scope.ServiceProvider.GetRequiredService<IBackgroundJobService>();
                idleExpireJobId = await jobSvc.CreateJobAsync("sess-r6-idleexp", "T-IDLE", "ApprovalTestJob", "{\"sessionId\":\"sess-r6-idleexp\"}");
            }

            await runner.ExecuteInlineAsync(idleExpireJobId, CancellationToken.None);
            await WaitForJobStatusAsync(sp, idleExpireJobId, BackgroundJobStatus.WaitingUser);

            // DO NOT call GetPendingApprovals, GetPendingApprovalCount, SubmitDecisionAsync, or change options!
            // Poll ONLY SQLite JobDbContext: J1 MUST automatically transition to Failed after 350ms despite J0's stalled barrier!
            await WaitForJobStatusAsync(sp, idleExpireJobId, BackgroundJobStatus.Failed);

            // Finally commit J0's barrier and verify continuation wakes the pump to transition J0 to Failed as well!
            await PersistWaitingUserCheckpointAsync(sp, stalledBarrierJobId, "sess-r6-hol", "tok-hol");
            stalledSuspension.NotifyPersistenceCommitted();
            await WaitForJobStatusAsync(sp, stalledBarrierJobId, BackgroundJobStatus.Failed);
        }
        finally
        {
            stalledScopeDisp.Dispose();
        }
    }

    [Fact]
    public async Task R7_RealOptionsMonitorDeadlock_FailedSubmissionFinallyGateOverlap_AndPermissionStoreDiskFailure()
    {
        Directory.CreateDirectory(_root);

        // =========================================================================
        // 1. B7-1: Real IOptionsMonitor<ExecutionOptions> reload Lazy lock vs ExecutionPermissionClamp._gate
        // =========================================================================
        using var manualChangeSource = new ManualOptionsChangeSource();
        var insideConfigureTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseConfigureTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockNextConfigure = 0;

        var optServices = new ServiceCollection();
        optServices.AddOptions<ExecutionOptions>();
        optServices.AddSingleton<IConfigureOptions<ExecutionOptions>>(new ConfigureOptions<ExecutionOptions>(opts =>
        {
            opts.Backend = ExecutionBackend.Docker;
            opts.PermissionMode = ExecutionPermissionMode.FullAuto;
            if (Interlocked.CompareExchange(ref blockNextConfigure, 0, 1) == 1)
            {
                // Thread A holds the real .NET OptionsCache Lazy<ExecutionOptions> lock right here (BEFORE PostConfigure)!
                insideConfigureTcs.TrySetResult();
                Assert.True(releaseConfigureTcs.Task.Wait(TimeSpan.FromSeconds(5)));
            }
        }));
        optServices.AddSingleton<IOptionsChangeTokenSource<ExecutionOptions>>(manualChangeSource);
        optServices.AddSingleton<ExecutionPermissionClamp>();
        optServices.AddSingleton<IPostConfigureOptions<ExecutionOptions>>(p => p.GetRequiredService<ExecutionPermissionClamp>());
        optServices.AddSingleton<IOptionsChangeTokenSource<ExecutionOptions>>(p => p.GetRequiredService<ExecutionPermissionClamp>());

        await using (var optSp = optServices.BuildServiceProvider())
        {
            var realMonitor = optSp.GetRequiredService<IOptionsMonitor<ExecutionOptions>>();
            var realClamp = optSp.GetRequiredService<ExecutionPermissionClamp>();

            // Establish initial baseline (FullAuto)
            Assert.Equal(ExecutionPermissionMode.FullAuto, realMonitor.CurrentValue.PermissionMode);

            // Note: ChangeToken.OnChange invokes OptionsMonitor.InvokeChanged synchronously on the cancelling thread!
            // Run TriggerReload on Thread A so Thread A holds OptionsCache's Lazy<ExecutionOptions> lock inside Configure:
            Interlocked.Exchange(ref blockNextConfigure, 1);
            var threadAReloadTask = Task.Run(() =>
            {
                manualChangeSource.TriggerReload();
                return realMonitor.CurrentValue;
            });
            await insideConfigureTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Thread B updates settings & reads effective state WHILE Thread A holds OptionsCache Lazy lock!
            // Under B7-1 bug (calling CurrentValue inside _gate), this deadlocked permanently!
            var threadBUpdateTask = Task.Run(() =>
            {
                var ok = realClamp.TryApplySettingsUpdateAtomically(
                    realMonitor,
                    backend: null,
                    permissionMode: ExecutionPermissionMode.Deny,
                    isControlPlaneCaller: false,
                    out _,
                    out var effMode);
                var resolved = realClamp.ResolveEffective(realMonitor);
                return (ok, effMode, resolved.PermissionMode);
            });

            var (appliedOk, appliedMode, resolvedMode) = await threadBUpdateTask.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(appliedOk);
            Assert.Equal(ExecutionPermissionMode.Deny, appliedMode);
            Assert.Equal(ExecutionPermissionMode.Deny, resolvedMode);

            // Now release Thread A -> enters PostConfigure cleanly with zero deadlock and sees clamped Deny!
            releaseConfigureTcs.TrySetResult();
            var reloaded = await threadAReloadTask.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(ExecutionPermissionMode.Deny, reloaded.PermissionMode);
        }

        // =========================================================================
        // 2. Advisory 2: ExecutionPermissionStore disk write failure leaves ZERO ghost rules in memory
        // =========================================================================
        var brokenPermsPath = Path.Combine(_root, "r7-broken-perms", "permissions.json");
        var healthyStore = new ExecutionPermissionStore(brokenPermsPath);
        // Replace target file path with a NON-EMPTY DIRECTORY so atomic file replacement deterministically fails I/O!
        Directory.CreateDirectory(brokenPermsPath);
        File.WriteAllText(Path.Combine(brokenPermsPath, "blocker.txt"), "occupied");

        var sampleGitStatusReq = new ExecutionRequest
        {
            SessionId = "s-r7-disk",
            JobId = "j-r7-disk",
            TurnId = "t1",
            ToolName = "exec_cmd",
            Executable = "git",
            InterpreterChain = ["git"],
            Argv = ["status"],
            Cwd = ".",
            EnvironmentDelta = [],
            Backend = ExecutionBackend.Docker,
            Network = "None",
            Filesystem = "workspace_write",
            TrustedOriginVerified = true
        };

        Assert.ThrowsAny<Exception>(() => healthyStore.AddAllowAlways(sampleGitStatusReq, "must-not-leak-in-memory"));
        Assert.False(healthyStore.IsAllowedAlways(sampleGitStatusReq.ComputeSessionAgnosticDigest()));
        Assert.Empty(healthyStore.ListRules());

        // =========================================================================
        // 3. B7-2: Failed submission A's `finally` overlapping with concurrent retry B's new publish gate GB
        // =========================================================================
        var dbPath = Path.Combine(_root, "r7-jobs", "jobs.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCodexFlowSqliteStorage(dbPath);
        services.AddCodexFlowLocalDefaults();
        services.AddSingleton(new Mock<IBackgroundJobSignal>().Object);

        var outboxSignal = new Mock<IOutboxSignal>().Object;
        var workerHookDispatcher = new CodexFlow.Services.Hooks.WorkerHookDispatcher(
            [],
            NullLogger<CodexFlow.Services.Hooks.WorkerHookDispatcher>.Instance);
        var planDispatch = new Mock<IPlanTaskDispatchService>().Object;
        var sessionDir = new Mock<ISessionDirectory>();
        sessionDir
            .Setup(s => s.FindAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string sid, CancellationToken _) => new SessionRecord(sid, LocalUser.Id, _root));

        services.AddSingleton(outboxSignal);
        services.AddSingleton<CodexFlow.Application.Hooks.IWorkerHookDispatcher>(workerHookDispatcher);
        services.AddSingleton(planDispatch);
        services.AddSingleton(sessionDir.Object);
        services.AddScoped<IBackgroundJobService, BackgroundJobService>();
        services.AddSingleton<BackgroundJobApprovalResumer>();
        services.AddSingleton<FlakyApprovalResumer>(p => new FlakyApprovalResumer(p.GetRequiredService<BackgroundJobApprovalResumer>()));
        services.AddSingleton<IBackgroundJobApprovalResumer>(p => p.GetRequiredService<FlakyApprovalResumer>());

        var optsMonitor = new ExecutionApprovalAndPermissionTests.MutableOptionsMonitor<ExecutionOptions>(new ExecutionOptions
        {
            Backend = ExecutionBackend.Docker,
            PermissionMode = ExecutionPermissionMode.Ask,
            HeadlessApprovalTimeout = TimeSpan.Zero,
            ApprovalTimeout = TimeSpan.FromMinutes(5),
            SuspendUnsubscribedBackgroundJobs = true
        });
        var permStore = new ExecutionPermissionStore(Path.Combine(_root, "r7-perms.json"));
        var unsubscribedGateway = new ExecutionApprovalAndPermissionTests.RecordingGateway(hasSubscribers: false);
        services.AddSingleton<CodexFlow.Core.Gateway.ICodexGateway>(unsubscribedGateway);
        services.AddSingleton(new CodexFlow.Core.Services.InMemoryWorkflowAuditStore());
        services.AddSingleton<IWorkflowAuditStore>(p => p.GetRequiredService<CodexFlow.Core.Services.InMemoryWorkflowAuditStore>());
        services.AddSingleton<IExecutionApprovalBroker>(p => new ExecutionApprovalBroker(
            optsMonitor,
            permStore,
            NullLogger<ExecutionApprovalBroker>.Instance,
            serviceProvider: p,
            gateway: unsubscribedGateway));

        await using var sp = services.BuildServiceProvider();
        await using (var initScope = sp.CreateAsyncScope())
        {
            var db = initScope.ServiceProvider.GetRequiredService<JobDbContext>();
            await db.Database.MigrateAsync();
        }

        var flakyResumer = sp.GetRequiredService<FlakyApprovalResumer>();
        var realInnerResumer = sp.GetRequiredService<BackgroundJobApprovalResumer>();
        var broker = (ExecutionApprovalBroker)sp.GetRequiredService<IExecutionApprovalBroker>();
        var accessor = new TrustedExecutionContextAccessor();
        var cmdRuns = 0;
        var fakeDocker = new CountingDockerExecutionService(() => Interlocked.Increment(ref cmdRuns));
        var gate = new ToolIsolationGate(fakeDocker, accessor, new IsolationOptions { StagingRoot = Path.Combine(_root, "r7-stg") }, broker, optsMonitor);
        var runCmd = new RunCommandTool(NullLogger<RunCommandTool>.Instance, isolationGate: gate);

        var runner = new ToolExecutingBackgroundJobRunner(
            sp,
            NullLogger<BackgroundJobRunner>.Instance,
            outboxSignal,
            workerHookDispatcher,
            planDispatch,
            async (job, workerToken) =>
            {
                using var ctxScope = accessor.Begin(new TrustedExecutionContext
                {
                    UserId = LocalUser.Id,
                    SessionId = job.SessionId!,
                    JobId = job.JobId,
                    TaskId = job.TaskId ?? "T1",
                    AttemptId = job.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    WorkspaceRoot = _root
                });
                var toolRes = await runCmd.ExecuteAsync(
                    new Dictionary<string, object?> { ["command"] = new[] { "git", "status" } },
                    workerToken);
                Assert.Equal(ToolResultStatus.Success, toolRes.Status);
                return "{\"success\":true}";
            });

        string b72JobId;
        await using (var scope = sp.CreateAsyncScope())
        {
            var jobSvc = scope.ServiceProvider.GetRequiredService<IBackgroundJobService>();
            b72JobId = await jobSvc.CreateJobAsync("sess-r7-overlap", "T-B72", "ApprovalTestJob", "{\"sessionId\":\"sess-r7-overlap\"}");
        }

        await runner.ExecuteInlineAsync(b72JobId, CancellationToken.None);
        var pendingB72 = Assert.Single(broker.GetPendingApprovals("sess-r7-overlap"));
        var subAllowSession = new ExecutionApprovalDecisionSubmission(pendingB72.RequestDigest, ExecutionApprovalDecisionKind.AllowSession, "ctrl");

        // Exact reproduction of B7-2:
        // 1) Submission A gets RetryableFailure, releases in-flight claim, and pauses RIGHT BEFORE its `finally` block!
        // 2) Submission B claims the approval, registers new publish gate GB, and commits Queued to SQLite, pausing BEFORE publishing AllowSession!
        // 3) Release Submission A so its `finally` runs NOW while GB is active!
        // 4) Start Worker -> Worker MUST STILL hit GB inside AuthorizeAsync (proving A's `finally` did NOT delete GB!)
        var subAPausedBeforeFinally = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSubAFinally = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        broker.OnAfterRollbackBeforeFinallyForTesting = aid =>
        {
            if (aid == pendingB72.ApprovalId)
            {
                broker.OnAfterRollbackBeforeFinallyForTesting = null;
                subAPausedBeforeFinally.TrySetResult();
                Assert.True(releaseSubAFinally.Task.Wait(TimeSpan.FromSeconds(5)));
            }
        };

        var attemptIndex = 0;
        Task? racingWorkerB72 = null;
        Task<ApprovalSubmissionResult>? subATask = null;
        flakyResumer.Interceptor = async (jid, rtok, pitem, sub, ctok) =>
        {
            var idx = Interlocked.Increment(ref attemptIndex);
            if (idx == 1)
            {
                return SuspendedJobResolutionStatus.RetryableFailure;
            }

            // Submission B: commit Queued to SQLite
            var applied = await realInnerResumer.TryApplyResolutionAsync(jid, rtok, pitem, sub, ctok);
            Assert.Equal(SuspendedJobResolutionStatus.Applied, applied);

            // Let Submission A run its `finally` block RIGHT NOW while B has registered GB but NOT published AllowSession yet!
            releaseSubAFinally.TrySetResult();
            var outcomeA = await subATask!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ApprovalSubmissionOutcome.TransientFailure, outcomeA.Outcome);

            // Now start the racing worker: GB MUST STILL BE PRESENT so worker parks on GB instead of re-suspending!
            var workerHitGateGB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            broker.OnAwaitingResumePublishGateForTesting = (sid, parkedJobId) =>
            {
                if (sid == "sess-r7-overlap" && parkedJobId == jid)
                {
                    workerHitGateGB.TrySetResult();
                }
            };
            racingWorkerB72 = Task.Run(() => runner.ExecuteInlineAsync(jid, CancellationToken.None));
            await workerHitGateGB.Task.WaitAsync(TimeSpan.FromSeconds(5));
            broker.OnAwaitingResumePublishGateForTesting = null;
            Assert.False(racingWorkerB72.IsCompleted);

            return applied;
        };

        subATask = Task.Run(() => broker.SubmitDecisionAsync(pendingB72.ApprovalId, subAllowSession));
        await subAPausedBeforeFinally.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var outcomeB = await broker.SubmitDecisionAsync(pendingB72.ApprovalId, subAllowSession);
        flakyResumer.Interceptor = null;

        Assert.Equal(ApprovalSubmissionOutcome.Resolved, outcomeB.Outcome);
        Assert.NotNull(racingWorkerB72);
        await racingWorkerB72!;
        Assert.Equal(1, cmdRuns);
        Assert.Empty(broker.GetPendingApprovals("sess-r7-overlap"));
        await WaitForJobStatusAsync(sp, b72JobId, BackgroundJobStatus.Completed);
    }

    [Fact]
    public async Task R8_AllowAlwaysAbortDiskLockCompensation_AndConcurrentSameDigestIsolation()
    {
        Directory.CreateDirectory(_root);
        var dbPath = Path.Combine(_root, "r8-jobs", "jobs.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var permsPath = Path.Combine(_root, "r8-perms", "permissions.json");
        Directory.CreateDirectory(Path.GetDirectoryName(permsPath)!);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCodexFlowSqliteStorage(dbPath);
        services.AddCodexFlowLocalDefaults();
        services.AddSingleton(new Mock<IBackgroundJobSignal>().Object);

        var outboxSignal = new Mock<IOutboxSignal>().Object;
        var workerHookDispatcher = new CodexFlow.Services.Hooks.WorkerHookDispatcher(
            [],
            NullLogger<CodexFlow.Services.Hooks.WorkerHookDispatcher>.Instance);
        var planDispatch = new Mock<IPlanTaskDispatchService>().Object;
        var sessionDir = new Mock<ISessionDirectory>();
        sessionDir
            .Setup(s => s.FindAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string sid, CancellationToken _) => new SessionRecord(sid, LocalUser.Id, _root));

        services.AddSingleton(outboxSignal);
        services.AddSingleton<CodexFlow.Application.Hooks.IWorkerHookDispatcher>(workerHookDispatcher);
        services.AddSingleton(planDispatch);
        services.AddSingleton(sessionDir.Object);
        services.AddScoped<IBackgroundJobService, BackgroundJobService>();
        services.AddSingleton<BackgroundJobApprovalResumer>();
        services.AddSingleton<FlakyApprovalResumer>(p => new FlakyApprovalResumer(p.GetRequiredService<BackgroundJobApprovalResumer>()));
        services.AddSingleton<IBackgroundJobApprovalResumer>(p => p.GetRequiredService<FlakyApprovalResumer>());

        var optsMonitor = new ExecutionApprovalAndPermissionTests.MutableOptionsMonitor<ExecutionOptions>(new ExecutionOptions
        {
            Backend = ExecutionBackend.Docker,
            PermissionMode = ExecutionPermissionMode.Ask,
            HeadlessApprovalTimeout = TimeSpan.Zero,
            ApprovalTimeout = TimeSpan.FromMinutes(5),
            SuspendUnsubscribedBackgroundJobs = true
        });
        using var permStore = new ExecutionPermissionStore(permsPath);
        var unsubscribedGateway = new ExecutionApprovalAndPermissionTests.RecordingGateway(hasSubscribers: false);
        services.AddSingleton<CodexFlow.Core.Gateway.ICodexGateway>(unsubscribedGateway);
        services.AddSingleton(new CodexFlow.Core.Services.InMemoryWorkflowAuditStore());
        services.AddSingleton<IWorkflowAuditStore>(p => p.GetRequiredService<CodexFlow.Core.Services.InMemoryWorkflowAuditStore>());
        services.AddSingleton<IExecutionApprovalBroker>(p => new ExecutionApprovalBroker(
            optsMonitor,
            permStore,
            NullLogger<ExecutionApprovalBroker>.Instance,
            serviceProvider: p,
            gateway: unsubscribedGateway));

        await using var sp = services.BuildServiceProvider();
        await using (var initScope = sp.CreateAsyncScope())
        {
            var db = initScope.ServiceProvider.GetRequiredService<JobDbContext>();
            await db.Database.MigrateAsync();
        }

        var flakyResumer = sp.GetRequiredService<FlakyApprovalResumer>();
        var realInnerResumer = sp.GetRequiredService<BackgroundJobApprovalResumer>();
        var broker = sp.GetRequiredService<IExecutionApprovalBroker>();
        var accessor = new TrustedExecutionContextAccessor();
        var cmdRuns = 0;
        var fakeDocker = new CountingDockerExecutionService(() => Interlocked.Increment(ref cmdRuns));
        var gate = new ToolIsolationGate(fakeDocker, accessor, new IsolationOptions { StagingRoot = Path.Combine(_root, "r8-stg") }, broker, optsMonitor);
        var runCmd = new RunCommandTool(NullLogger<RunCommandTool>.Instance, isolationGate: gate);

        string[] activeCmd = ["git", "status"];
        var runner = new ToolExecutingBackgroundJobRunner(
            sp,
            NullLogger<BackgroundJobRunner>.Instance,
            outboxSignal,
            workerHookDispatcher,
            planDispatch,
            async (job, workerToken) =>
            {
                using var ctxScope = accessor.Begin(new TrustedExecutionContext
                {
                    UserId = LocalUser.Id,
                    SessionId = job.SessionId!,
                    JobId = job.JobId,
                    TaskId = job.TaskId ?? "T1",
                    AttemptId = job.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    WorkspaceRoot = _root
                });
                var toolRes = await runCmd.ExecuteAsync(
                    new Dictionary<string, object?> { ["command"] = activeCmd },
                    workerToken);
                Assert.Equal(ToolResultStatus.Success, toolRes.Status);
                return "{\"success\":true}";
            });

        // =========================================================================
        // 1. B8-1: StageAllowAlways succeeds -> resumer locks permissions.json without delete sharing & returns RetryableFailure
        //    -> Abort disk flush fails, yet rule NEVER authorizes in memory OR across restarts, and background pump cleans disk once unlocked!
        // =========================================================================
        string b81JobId;
        await using (var scope = sp.CreateAsyncScope())
        {
            var jobSvc = scope.ServiceProvider.GetRequiredService<IBackgroundJobService>();
            b81JobId = await jobSvc.CreateJobAsync("sess-r8-lock", "T-B81", "ApprovalTestJob", "{\"sessionId\":\"sess-r8-lock\"}");
        }

        await runner.ExecuteInlineAsync(b81JobId, CancellationToken.None);
        var pendingB81 = Assert.Single(broker.GetPendingApprovals("sess-r8-lock"));

        FileStream? blockingHandle = null;
        string? lockedPermsDir = null;
        UnixFileMode? origPermsDirMode = null;
        try
        {
            flakyResumer.Interceptor = (_, _, _, _, _) =>
            {
                // StageAllowAlways has already written `stagedRules` to permsPath.
                // On Windows, holding permsPath open with FileShare.Read blocks File.Move replacement.
                // On POSIX (Linux/macOS), rename(2) unlinks open files, so we also remove write permission on the parent directory
                // so SaveSnapshotUnsafe inside AbortStagedAllowAlways deterministically throws while keeping permsPath readable.
                blockingHandle = new FileStream(permsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!OperatingSystem.IsWindows())
                {
                    lockedPermsDir = Path.GetDirectoryName(permsPath)!;
                    origPermsDirMode = File.GetUnixFileMode(lockedPermsDir);
                    File.SetUnixFileMode(lockedPermsDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                }

                return Task.FromResult(SuspendedJobResolutionStatus.RetryableFailure);
            };

            var failedAlwaysAttempt = await broker.SubmitDecisionAsync(
                pendingB81.ApprovalId,
                new ExecutionApprovalDecisionSubmission(pendingB81.RequestDigest, ExecutionApprovalDecisionKind.AllowAlways, "ctrl"));
            flakyResumer.Interceptor = null;

            Assert.Equal(ApprovalSubmissionOutcome.TransientFailure, failedAlwaysAttempt.Outcome);
            Assert.Empty(permStore.ListRules());
            // Prove the disk replacement genuinely failed while locked: cleanup flag is active and `stagedRules` is still on disk!
            Assert.True(permStore.HasPendingDiskCleanup);
            using (var verifyRawReader = new FileStream(permsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sr = new StreamReader(verifyRawReader))
            {
                var rawLockedJson = sr.ReadToEnd();
                Assert.Contains("\"stagedRules\"", rawLockedJson, StringComparison.Ordinal);
                Assert.Contains("\"rules\": []", rawLockedJson, StringComparison.Ordinal);
            }

            // Verify simulated restart reading the STILL-LOCKED file (which still contains `stagedRules` on disk!) NEVER loads it as active!
            using (var restartedWhileLocked = new ExecutionPermissionStore(permsPath))
            {
                Assert.Empty(restartedWhileLocked.ListRules());
            }

            // Verify a subsequent unapproved call with the EXACT SAME command & digest is REFUSED (zero ghost authorization!):
            using (accessor.Begin(new TrustedExecutionContext
                   {
                       UserId = LocalUser.Id,
                       SessionId = "sess-r8-probe",
                       JobId = "j-probe",
                       TaskId = "T-PROBE",
                       AttemptId = "1",
                       WorkspaceRoot = _root
                   }))
            {
                optsMonitor.Set(new ExecutionOptions
                {
                    Backend = ExecutionBackend.Docker,
                    PermissionMode = ExecutionPermissionMode.Ask,
                    HeadlessApprovalTimeout = TimeSpan.Zero,
                    ApprovalTimeout = TimeSpan.FromMinutes(5),
                    SuspendUnsubscribedBackgroundJobs = false
                });
                var probeRes = await runCmd.ExecuteAsync(
                    new Dictionary<string, object?> { ["command"] = activeCmd },
                    CancellationToken.None);
                Assert.NotEqual(ToolResultStatus.Success, probeRes.Status);
                Assert.Equal(0, cmdRuns);

                optsMonitor.Set(new ExecutionOptions
                {
                    Backend = ExecutionBackend.Docker,
                    PermissionMode = ExecutionPermissionMode.Ask,
                    HeadlessApprovalTimeout = TimeSpan.Zero,
                    ApprovalTimeout = TimeSpan.FromMinutes(5),
                    SuspendUnsubscribedBackgroundJobs = true
                });
            }
        }
        finally
        {
            if (!OperatingSystem.IsWindows() && lockedPermsDir is not null)
            {
                File.SetUnixFileMode(
                    lockedPermsDir,
                    origPermsDirMode ?? (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute));
            }

            blockingHandle?.Dispose();
        }

        // Once file lock is released, background cleanup pump flushes `stagedRules` off disk automatically!
        var cleanupDeadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < cleanupDeadline && permStore.HasPendingDiskCleanup)
        {
            await Task.Delay(30);
        }

        Assert.False(permStore.HasPendingDiskCleanup);
        Assert.DoesNotContain("stagedRules", File.ReadAllText(permsPath), StringComparison.Ordinal);

        // Retry AllowAlways now -> succeeds cleanly, activates persistent rule, and completes job!
        var retryB81 = await broker.SubmitDecisionAsync(
            pendingB81.ApprovalId,
            new ExecutionApprovalDecisionSubmission(pendingB81.RequestDigest, ExecutionApprovalDecisionKind.AllowAlways, "ctrl"));
        Assert.Equal(ApprovalSubmissionOutcome.Resolved, retryB81.Outcome);
        Assert.Single(permStore.ListRules());
        await runner.ExecuteInlineAsync(b81JobId, CancellationToken.None);
        Assert.Equal(1, cmdRuns);
        await WaitForJobStatusAsync(sp, b81JobId, BackgroundJobStatus.Completed);

        // =========================================================================
        // 2. Advisory 1 (Round 8): Two concurrent jobs J1 & J2 with the SAME session-agnostic digest submit AllowAlways;
        //    both stage concurrently, J1 commits, J2 fails in resumer & aborts -> J2's abort MUST NOT delete J1's committed rule!
        // =========================================================================
        activeCmd = ["pwd"];
        string j1Id, j2Id;
        await using (var scope = sp.CreateAsyncScope())
        {
            var jobSvc = scope.ServiceProvider.GetRequiredService<IBackgroundJobService>();
            j1Id = await jobSvc.CreateJobAsync("sess-r8-j1", "T-J1", "ApprovalTestJob", "{\"sessionId\":\"sess-r8-j1\"}");
            j2Id = await jobSvc.CreateJobAsync("sess-r8-j2", "T-J2", "ApprovalTestJob", "{\"sessionId\":\"sess-r8-j2\"}");
        }

        await runner.ExecuteInlineAsync(j1Id, CancellationToken.None);
        await runner.ExecuteInlineAsync(j2Id, CancellationToken.None);
        var pJ1 = Assert.Single(broker.GetPendingApprovals("sess-r8-j1"));
        var pJ2 = Assert.Single(broker.GetPendingApprovals("sess-r8-j2"));

        var bothEnteredResumer = new CountdownEvent(2);
        var j1CommittedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        flakyResumer.Interceptor = async (jid, rtok, pitem, sub, ctok) =>
        {
            bothEnteredResumer.Signal();
            Assert.True(bothEnteredResumer.Wait(TimeSpan.FromSeconds(5)));
            if (jid == j1Id)
            {
                return await realInnerResumer.TryApplyResolutionAsync(jid, rtok, pitem, sub, ctok);
            }

            // Wait until J1 has completely finished CommitStagedAllowAlways before J2 returns RetryableFailure!
            await j1CommittedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return SuspendedJobResolutionStatus.RetryableFailure;
        };

        var j1SubmitTask = Task.Run(async () =>
        {
            var r = await broker.SubmitDecisionAsync(
                pJ1.ApprovalId,
                new ExecutionApprovalDecisionSubmission(pJ1.RequestDigest, ExecutionApprovalDecisionKind.AllowAlways, "ctrl"));
            j1CommittedTcs.TrySetResult();
            return r;
        });
        var j2SubmitTask = Task.Run(() => broker.SubmitDecisionAsync(
            pJ2.ApprovalId,
            new ExecutionApprovalDecisionSubmission(pJ2.RequestDigest, ExecutionApprovalDecisionKind.AllowAlways, "ctrl")));

        var rJ1 = await j1SubmitTask;
        var rJ2 = await j2SubmitTask;
        flakyResumer.Interceptor = null;

        Assert.Equal(ApprovalSubmissionOutcome.Resolved, rJ1.Outcome);
        Assert.Equal(ApprovalSubmissionOutcome.TransientFailure, rJ2.Outcome);

        // J1's committed `pwd` rule MUST remain intact in both memory and disk despite J2's abort!
        Assert.Contains(permStore.ListRules(), r => r.Executable == "pwd");
        using (var verifyReloaded = new ExecutionPermissionStore(permsPath))
        {
            Assert.Contains(verifyReloaded.ListRules(), r => r.Executable == "pwd");
        }

        // =========================================================================
        // 3. Advisory 3 (Round 9): resumer returns Applied -> CommitStagedAllowAlways disk write fails
        //    -> single-use ticket executes resumed job ONCE, persistent rule stays inactive, and degraded audit is recorded!
        // =========================================================================
        activeCmd = ["echo", "degraded-check"];
        string degradeJobId;
        await using (var scope = sp.CreateAsyncScope())
        {
            var jobSvc = scope.ServiceProvider.GetRequiredService<IBackgroundJobService>();
            degradeJobId = await jobSvc.CreateJobAsync("sess-r8-degrade", "T-DEG", "ApprovalTestJob", "{\"sessionId\":\"sess-r8-degrade\"}");
        }

        await runner.ExecuteInlineAsync(degradeJobId, CancellationToken.None);
        var pDegrade = Assert.Single(broker.GetPendingApprovals("sess-r8-degrade"));

        FileStream? commitBlocker = null;
        string? commitLockedDir = null;
        UnixFileMode? commitOrigDirMode = null;
        try
        {
            var runsBeforeDegrade = Volatile.Read(ref cmdRuns);
            flakyResumer.Interceptor = async (jid, rtok, pitem, sub, ctok) =>
            {
                var applied = await realInnerResumer.TryApplyResolutionAsync(jid, rtok, pitem, sub, ctok);
                Assert.Equal(SuspendedJobResolutionStatus.Applied, applied);
                // Lock permsPath AFTER DB transitioned to Queued so CommitStagedAllowAlways throws!
                commitBlocker = new FileStream(permsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!OperatingSystem.IsWindows())
                {
                    // On POSIX, rename(2) / File.Move succeeds even with an open read handle;
                    // removing write permission on the parent directory makes atomic commit fail.
                    var dir = Path.GetDirectoryName(permsPath)!;
                    commitOrigDirMode = File.GetUnixFileMode(dir);
                    commitLockedDir = dir;
                    File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                }
                return applied;
            };

            var degradeSubmit = await broker.SubmitDecisionAsync(
                pDegrade.ApprovalId,
                new ExecutionApprovalDecisionSubmission(pDegrade.RequestDigest, ExecutionApprovalDecisionKind.AllowAlways, "ctrl"));
            flakyResumer.Interceptor = null;

            Assert.Equal(ApprovalSubmissionOutcome.Resolved, degradeSubmit.Outcome);
            // Persistent rule MUST NOT be active!
            Assert.DoesNotContain(permStore.ListRules(), r => r.Executable == "echo");

            // Resumed job consumes the degraded single-use ticket ONCE and completes:
            await runner.ExecuteInlineAsync(degradeJobId, CancellationToken.None);
            Assert.Equal(runsBeforeDegrade + 1, Volatile.Read(ref cmdRuns));
            await WaitForJobStatusAsync(sp, degradeJobId, BackgroundJobStatus.Completed);

            // Second execution of `whoami` is NOT persistently authorized:
            using (accessor.Begin(new TrustedExecutionContext
                   {
                       UserId = LocalUser.Id,
                       SessionId = "sess-r8-degrade",
                       JobId = "j-second-whoami",
                       TaskId = "T-DEG2",
                       AttemptId = "1",
                       WorkspaceRoot = _root
                   }))
            {
                optsMonitor.Set(new ExecutionOptions
                {
                    Backend = ExecutionBackend.Docker,
                    PermissionMode = ExecutionPermissionMode.Ask,
                    HeadlessApprovalTimeout = TimeSpan.Zero,
                    ApprovalTimeout = TimeSpan.FromMinutes(5),
                    SuspendUnsubscribedBackgroundJobs = false
                });
                var secondWhoami = await runCmd.ExecuteAsync(
                    new Dictionary<string, object?> { ["command"] = activeCmd },
                    CancellationToken.None);
                Assert.NotEqual(ToolResultStatus.Success, secondWhoami.Status);
                Assert.Equal(runsBeforeDegrade + 1, Volatile.Read(ref cmdRuns));
            }

            // Degraded audit entry MUST be recorded:
            var auditStore = sp.GetRequiredService<IWorkflowAuditStore>();
            var degradeAudits = await auditStore.ListAsync("sess-r8-degrade", "execution_approval", CancellationToken.None);
            Assert.Contains(degradeAudits, a => a.Status == "suspended_job_allow_always_degraded_to_once_on_store_io_error");
        }
        finally
        {
            if (!OperatingSystem.IsWindows() && commitLockedDir is not null && commitOrigDirMode.HasValue)
            {
                File.SetUnixFileMode(commitLockedDir, commitOrigDirMode.Value);
            }
            commitBlocker?.Dispose();
        }
    }

    private static async Task WaitForJobStatusAsync(
        IServiceProvider sp,
        string jobId,
        BackgroundJobStatus expectedStatus)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        BackgroundJobStatus last = BackgroundJobStatus.Queued;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var scope = sp.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<JobDbContext>();
            var job = await db.BackgroundJobs.FindAsync(jobId);
            last = job!.Status;
            if (last == expectedStatus)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Equal(expectedStatus, last);
    }

    private static async Task PersistWaitingUserCheckpointAsync(
        IServiceProvider sp,
        string jobId,
        string sessionId,
        string resumeToken)
    {
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<JobDbContext>();
        var job = await db.BackgroundJobs.FindAsync(jobId);
        job!.Status = BackgroundJobStatus.WaitingUser;
        job.WaitingReason = "Awaiting execution approval";
        var latestSeq = await db.JobCheckpoints
            .Where(c => c.JobId == jobId)
            .Select(c => (int?)c.SequenceNumber)
            .MaxAsync() ?? -1;
        db.JobCheckpoints.Add(new JobCheckpoint
        {
            JobId = jobId,
            SessionId = sessionId,
            SequenceNumber = latestSeq + 1,
            CurrentStep = "AwaitingUserConfirmation",
            PendingActionType = "UserConfirmation",
            ResumeToken = resumeToken,
            CheckpointData = "{}"
        });
        await db.SaveChangesAsync();
    }

    private static async Task<TestControlPlaneHost> StartTestControlPlaneHostAsync(
        LocalHostCredentials credentials,
        ExecutionOptions initialOptions,
        string globalSettingsPath,
        bool hasSubscribers = true,
        IBackgroundJobApprovalResumer? customJobResumer = null,
        IExecutionSettingsService? customSettingsService = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var optionsMonitor = new ExecutionApprovalAndPermissionTests.MutableOptionsMonitor<ExecutionOptions>(initialOptions);
        var clamp = new ExecutionPermissionClamp();
        var permStore = new ExecutionPermissionStore(Path.Combine(Path.GetDirectoryName(globalSettingsPath)!, "permissions.json"));
        var settingsService = customSettingsService ?? new ExecutionSettingsService(globalSettingsPath);
        var auditStore = new CodexFlow.Core.Services.InMemoryWorkflowAuditStore();
        var fakeGateway = new ExecutionApprovalAndPermissionTests.RecordingGateway(hasSubscribers: hasSubscribers);
        var broker = new ExecutionApprovalBroker(
            optionsMonitor,
            permStore,
            NullLogger<ExecutionApprovalBroker>.Instance,
            gateway: fakeGateway,
            auditStore: auditStore,
            controlPlane: credentials,
            jobResumer: customJobResumer);

        builder.Services.AddControllers()
            .AddApplicationPart(typeof(GatewayApprovalController).Assembly);
        builder.Services.AddSingleton(credentials);
        builder.Services.AddSingleton<IControlPlaneStateProvider>(credentials);
        builder.Services.AddSingleton<IOptionsMonitor<ExecutionOptions>>(optionsMonitor);
        builder.Services.AddSingleton(clamp);
        builder.Services.AddSingleton<IExecutionPermissionStore>(permStore);
        builder.Services.AddSingleton<IExecutionSettingsService>(settingsService);
        builder.Services.AddSingleton<IWorkflowAuditStore>(auditStore);
        builder.Services.AddSingleton<IExecutionApprovalBroker>(broker);
        builder.Services.AddSingleton<CodexFlow.Core.Gateway.ICodexGateway>(fakeGateway);
        var chatServiceMock = new Mock<IChatService>();
        chatServiceMock
            .Setup(s => s.GetSession(It.IsAny<string>()))
            .Returns((string sid) => Task.FromResult<IvilsonSession?>(new IvilsonSession { Id = sid, UserId = LocalUser.Id, Name = "test" }));
        builder.Services.AddSingleton(chatServiceMock.Object);
        builder.Services.AddSingleton(new CodexFlow.Core.Agents.CodexSessionManager(
            new Mock<ISessionStore>().Object,
            new Mock<IMemoryService>().Object,
            NullLogger<CodexFlow.Core.Agents.CodexSessionManager>.Instance));

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

        var app = builder.Build();
        TestControlPlaneHost? hostRef = null;
        app.Use(async (ctx, next) =>
        {
            hostRef?.OnRequestEntered?.Invoke(ctx.Request.Path.Value ?? string.Empty);
            await next();
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();

        hostRef = new TestControlPlaneHost(app, new Uri(address), broker, optionsMonitor, clamp, auditStore);
        return hostRef;
    }

    private sealed class TestControlPlaneHost(
        WebApplication app,
        Uri baseUri,
        ExecutionApprovalBroker broker,
        ExecutionApprovalAndPermissionTests.MutableOptionsMonitor<ExecutionOptions> optionsMonitor,
        ExecutionPermissionClamp clamp,
        IWorkflowAuditStore auditStore) : IAsyncDisposable
    {
        public ExecutionApprovalBroker Broker { get; } = broker;
        public ExecutionApprovalAndPermissionTests.MutableOptionsMonitor<ExecutionOptions> OptionsMonitor { get; } = optionsMonitor;
        public ExecutionPermissionClamp Clamp { get; } = clamp;
        public IWorkflowAuditStore AuditStore { get; } = auditStore;
        public Action<string>? OnRequestEntered { get; set; }

        public HttpClient CreateClient(string bearerToken)
        {
            var client = new HttpClient { BaseAddress = baseUri };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class CountingDockerExecutionService(Action onRun) : IIsolatedExecutionService
    {
        public ExecutionBackend ActiveBackend => ExecutionBackend.Docker;

        public Task<IsolatedSessionOpenResult> OpenSessionAsync(
            TrustedExecutionContext context,
            IsolatedSessionOptions options,
            CancellationToken ct)
            => Task.FromResult(new IsolatedSessionOpenResult(ExecutionDecision.Allow(), new Session(onRun)));

        private sealed class Session(Action onRun) : IIsolatedExecutionSession
        {
            public string SessionHandle => "count-docker";

            public Task<IsolatedCommandResult> RunAsync(IsolatedCommandRequest request, CancellationToken ct)
            {
                onRun();
                return Task.FromResult(new IsolatedCommandResult(
                    ExecutionDecision.Allow(),
                    IsolatedCommandStatus.Completed,
                    0,
                    "ok",
                    string.Empty,
                    false,
                    TimeSpan.FromMilliseconds(1),
                    "test"));
            }

            public Task<IsolatedExportResult> ExportScopedChangesAsync(CancellationToken ct)
                => Task.FromResult(new IsolatedExportResult(ExecutionDecision.Allow(), [], []));

            public Task<IReadOnlyList<EvidenceFile>> ReadEvidenceAsync(IReadOnlyList<string> globs, CancellationToken ct)
                => Task.FromResult<IReadOnlyList<EvidenceFile>>([]);

            public Task AbortAsync() => Task.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class ReloadableMemoryConfigSource(IDictionary<string, string?> initial)
        : Microsoft.Extensions.Configuration.IConfigurationSource
    {
        private readonly ReloadableMemoryConfigProvider _provider = new(initial);

        public Microsoft.Extensions.Configuration.IConfigurationProvider Build(
            Microsoft.Extensions.Configuration.IConfigurationBuilder builder) => _provider;

        public void Update(string key, string? value) => _provider.Update(key, value);

        private sealed class ReloadableMemoryConfigProvider : Microsoft.Extensions.Configuration.ConfigurationProvider
        {
            public ReloadableMemoryConfigProvider(IDictionary<string, string?> initial)
            {
                Data = new Dictionary<string, string?>(initial, StringComparer.OrdinalIgnoreCase);
            }

            public void Update(string key, string? value)
            {
                Data[key] = value;
                OnReload();
            }
        }
    }

    private sealed class ToolExecutingBackgroundJobRunner(
        IServiceProvider serviceProvider,
        ILogger<BackgroundJobRunner> logger,
        IOutboxSignal outboxSignal,
        CodexFlow.Application.Hooks.IWorkerHookDispatcher workerHookDispatcher,
        IPlanTaskDispatchService planTaskDispatchService,
        Func<BackgroundJob, CancellationToken, Task<string>> handler)
        : BackgroundJobRunner(serviceProvider, logger, outboxSignal, workerHookDispatcher, planTaskDispatchService)
    {
        protected override Task<string> ExecuteUnhandledJobTypeAsync(
            IServiceProvider scopedServices,
            BackgroundJob job,
            CancellationToken ct)
            => handler(job, ct);
    }

    private sealed class FlakyApprovalResumer(IBackgroundJobApprovalResumer inner) : IBackgroundJobApprovalResumer
    {
        public Func<string, string, PendingApprovalItem, ExecutionApprovalDecisionSubmission, CancellationToken, Task<SuspendedJobResolutionStatus>?>? Interceptor { get; set; }

        public async Task<bool> OnSuspendedApprovalResolvedAsync(
            string jobId,
            string resumeToken,
            PendingApprovalItem item,
            ExecutionApprovalDecisionSubmission submission,
            CancellationToken ct)
            => await TryApplyResolutionAsync(jobId, resumeToken, item, submission, ct) == SuspendedJobResolutionStatus.Applied;

        public async Task<SuspendedJobResolutionStatus> TryApplyResolutionAsync(
            string jobId,
            string resumeToken,
            PendingApprovalItem item,
            ExecutionApprovalDecisionSubmission submission,
            CancellationToken ct)
        {
            var hook = Interceptor;
            if (hook is not null)
            {
                var intercepted = hook(jobId, resumeToken, item, submission, ct);
                if (intercepted is not null)
                {
                    return await intercepted;
                }
            }

            return await inner.TryApplyResolutionAsync(jobId, resumeToken, item, submission, ct);
        }
    }

    private sealed class HookableSettingsService(IExecutionSettingsService inner) : IExecutionSettingsService
    {
        public Action<ExecutionSettingsUpdate>? OnBeforeUpdate { get; set; }

        public ExecutionSettingsView Read() => inner.Read();

        public ExecutionSettingsView Update(ExecutionSettingsUpdate update)
        {
            OnBeforeUpdate?.Invoke(update);
            return inner.Update(update);
        }

        public bool MigrateLegacyIsolationIfNeeded(ILogger? logger = null) => inner.MigrateLegacyIsolationIfNeeded(logger);
    }

    private sealed class ManualOptionsChangeSource : IOptionsChangeTokenSource<ExecutionOptions>, IDisposable
    {
        private readonly object _lock = new();
        private CancellationTokenSource _cts = new();

        public string Name => Options.DefaultName;

        public Microsoft.Extensions.Primitives.IChangeToken GetChangeToken()
        {
            lock (_lock)
            {
                return new Microsoft.Extensions.Primitives.CancellationChangeToken(_cts.Token);
            }
        }

        public void TriggerReload()
        {
            CancellationTokenSource prev;
            lock (_lock)
            {
                prev = _cts;
                _cts = new CancellationTokenSource();
            }

            try
            {
                prev.Cancel();
            }
            finally
            {
                prev.Dispose();
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _cts.Dispose();
            }
        }
    }
}
