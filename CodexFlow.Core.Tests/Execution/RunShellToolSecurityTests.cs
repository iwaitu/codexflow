// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text.Json;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.Execution;

public sealed class RunShellToolSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codexflow-runshell-sec-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            LocalExecutionService.TryDeleteDirectory(_root);
        }
    }

    [Fact]
    public async Task RunShellTool_ShouldRefuse_WhenOriginInvalidOrPlanModeActiveOrBrokerMissing()
    {
        var ws = Path.Combine(_root, "ws-origin");
        var stg = Path.Combine(_root, "stg-origin");
        Directory.CreateDirectory(ws);
        Directory.CreateDirectory(stg);

        using var permStore = new ExecutionPermissionStore(Path.Combine(_root, "perms.json"));
        var monitor = new ExecutionApprovalAndPermissionTests.MutableOptionsMonitor<ExecutionOptions>(new ExecutionOptions
        {
            Backend = ExecutionBackend.Local,
            PermissionMode = ExecutionPermissionMode.FullAuto,
            ApprovalTimeout = TimeSpan.FromMilliseconds(200)
        });
        var isoOptions = new IsolationOptions { StagingRoot = stg };
        var localSvc = new LocalExecutionService(Options.Create(isoOptions), NullLogger<LocalExecutionService>.Instance);
        var accessor = new TrustedExecutionContextAccessor();
        var broker = new ExecutionApprovalBroker(monitor, permStore, NullLogger<ExecutionApprovalBroker>.Instance);
        var gate = new ToolIsolationGate(localSvc, accessor, isoOptions, broker, monitor);
        var tool = new RunShellTool(NullLogger<RunShellTool>.Instance, gate);

        // 1. Missing TrustedExecutionContext -> MissingTrustedContext
        var noCtx = await tool.ExecuteAsync(new Dictionary<string, object?> { ["script"] = "echo hi" });
        Assert.NotEqual(ToolResultStatus.Success, noCtx.Status);
        Assert.Contains(ExecutionReasonCodes.MissingTrustedContext, noCtx.Output, StringComparison.Ordinal);

        // 2. Wrong origin (DevWorker) -> rejected
        var workerCtx = TrustedExecutionContextFactory.ForJob("u1", "s1", "job-1", "t1", 1, ws, baseCommit: null, taskScope: null);
        using (accessor.Begin(workerCtx))
        {
            var rWorker = await tool.ExecuteAsync(new Dictionary<string, object?> { ["script"] = "echo hi" });
            Assert.NotEqual(ToolResultStatus.Success, rWorker.Status);
            Assert.Contains("DailyChat", rWorker.Output, StringComparison.OrdinalIgnoreCase);
        }

        // 3. PlanModeActive = true -> PlanModeActive
        var dailyCtx = CreateDailyContext(ws, "inv-plan");
        using (accessor.Begin(dailyCtx))
        using (AmbientSessionPolicyContext.BeginScope(new SessionPolicySnapshot(SessionWorkMode.Daily, 1, true, 2)))
        {
            var rPlan = await tool.ExecuteAsync(new Dictionary<string, object?> { ["script"] = "echo hi" });
            Assert.NotEqual(ToolResultStatus.Success, rPlan.Status);
            Assert.Contains(ExecutionReasonCodes.PlanModeActive, rPlan.Output, StringComparison.Ordinal);
        }

        // 4. Null ApprovalBroker -> ApprovalUnavailable (fail-closed even under FullAuto)
        var noBrokerGate = new ToolIsolationGate(localSvc, accessor, isoOptions, null, monitor);
        var noBrokerTool = new RunShellTool(NullLogger<RunShellTool>.Instance, noBrokerGate);
        using (accessor.Begin(CreateDailyContext(ws, "inv-nobroker")))
        {
            var rNoBroker = await noBrokerTool.ExecuteAsync(new Dictionary<string, object?> { ["script"] = "echo hi" });
            Assert.NotEqual(ToolResultStatus.Success, rNoBroker.Status);
            Assert.Contains(ExecutionReasonCodes.ApprovalUnavailable, rNoBroker.Output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RunShellTool_ShouldEnforceSingleUseInvocationId_AndRejectReplay()
    {
        var ws = Path.Combine(_root, "ws-replay");
        var stg = Path.Combine(_root, "stg-replay");
        Directory.CreateDirectory(ws);
        Directory.CreateDirectory(stg);

        using var permStore = new ExecutionPermissionStore(Path.Combine(_root, "perms-replay.json"));
        var monitor = new ExecutionApprovalAndPermissionTests.MutableOptionsMonitor<ExecutionOptions>(new ExecutionOptions
        {
            Backend = ExecutionBackend.Local,
            PermissionMode = ExecutionPermissionMode.FullAuto,
            ApprovalTimeout = TimeSpan.FromMilliseconds(150)
        });
        var isoOptions = new IsolationOptions { StagingRoot = stg };
        var localSvc = new LocalExecutionService(Options.Create(isoOptions), NullLogger<LocalExecutionService>.Instance);
        var accessor = new TrustedExecutionContextAccessor();
        var broker = new ExecutionApprovalBroker(monitor, permStore, NullLogger<ExecutionApprovalBroker>.Instance);
        var gate = new ToolIsolationGate(localSvc, accessor, isoOptions, broker, monitor);
        var tool = new RunShellTool(NullLogger<RunShellTool>.Instance, gate);

        var sameInvocationCtx = CreateDailyContext(ws, "inv-single-use-1");
        using (accessor.Begin(sameInvocationCtx))
        {
            var first = await tool.ExecuteAsync(new Dictionary<string, object?> { ["script"] = "echo 1" });
            // Timeout waiting for Ask approval (proves it entered broker, consumed invocationId)
            Assert.Contains(ExecutionReasonCodes.ApprovalExpired, first.Output, StringComparison.Ordinal);

            var secondReplay = await tool.ExecuteAsync(new Dictionary<string, object?> { ["script"] = "echo 2" });
            Assert.Contains(ExecutionReasonCodes.InvalidOperation, secondReplay.Output, StringComparison.Ordinal);
            Assert.Contains("already consumed", secondReplay.Output, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ExecutionApprovalBroker_ForRunShell_ShouldNeverInheritFullAuto_AndRestrictToAllowOnceOrDeny()
    {
        var permPath = Path.Combine(_root, "perms-broker.json");
        Directory.CreateDirectory(_root);
        using var permStore = new ExecutionPermissionStore(permPath);
        var monitor = new ExecutionApprovalAndPermissionTests.MutableOptionsMonitor<ExecutionOptions>(new ExecutionOptions
        {
            Backend = ExecutionBackend.Local,
            PermissionMode = ExecutionPermissionMode.FullAuto,
            ApprovalTimeout = TimeSpan.FromSeconds(5)
        });
        var broker = new ExecutionApprovalBroker(monitor, permStore, NullLogger<ExecutionApprovalBroker>.Instance);

        var request = new ExecutionRequest
        {
            SessionId = "s-daily",
            TurnId = "t1",
            WorkspaceRoot = _root,
            ToolName = "run_shell",
            Executable = OperatingSystem.IsWindows() ? @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" : "/bin/sh",
            InterpreterChain = [],
            Argv = ["-NoLogo"],
            EnvironmentDelta = [],
            Cwd = _root,
            ScriptSha256 = new string('a', 64),
            Backend = ExecutionBackend.Local,
            Network = "Host",
            Filesystem = "LocalOutputsOnly",
            TrustedOriginVerified = true,
            Origin = ExecutionOrigin.DailyChat,
            PolicyVersion = 1,
            ShellKind = "powershell",
            ScriptText = "Write-Output 'ok'",
            OwnerKind = ApprovalOwnerKind.GatewayTurn,
            OwnerId = "turn-1"
        };

        var authTask = broker.AuthorizeAsync(
            new ExecutionAuthorizationContext(request, _root, _root, _root, null),
            CancellationToken.None);

        PendingApprovalItem? pending = null;
        for (var i = 0; i < 40 && pending is null; i++)
        {
            pending = broker.GetPendingApprovals("s-daily").FirstOrDefault();
            if (pending is null) await Task.Delay(25);
        }

        Assert.NotNull(pending);
        Assert.Equal(["deny", "allow_once"], pending!.AllowedDecisions);
        Assert.True(broker.TryGetPendingApprovalScript(pending.ApprovalId, "s-daily", out var scriptSnap));
        Assert.Equal("Write-Output 'ok'", scriptSnap!.ScriptText);

        var alwaysRejected = await broker.SubmitDecisionAsync(
            pending.ApprovalId,
            new ExecutionApprovalDecisionSubmission(pending.RequestDigest, ExecutionApprovalDecisionKind.AllowAlways, "u1"));
        Assert.Equal(ApprovalSubmissionOutcome.DecisionNotAllowed, alwaysRejected.Outcome);

        var onceAccepted = await broker.SubmitDecisionAsync(
            pending.ApprovalId,
            new ExecutionApprovalDecisionSubmission(pending.RequestDigest, ExecutionApprovalDecisionKind.AllowOnce, "u1"));
        Assert.Equal(ApprovalSubmissionOutcome.Resolved, onceAccepted.Outcome);

        var authResult = await authTask;
        Assert.True(authResult.Decision.IsAllowed);
    }

    [Fact]
    public async Task CancelOwnerApprovals_ShouldCancelOnlyMatchingOwner()
    {
        Directory.CreateDirectory(_root);
        using var permStore = new ExecutionPermissionStore(Path.Combine(_root, "perms-owner.json"));
        var monitor = new ExecutionApprovalAndPermissionTests.MutableOptionsMonitor<ExecutionOptions>(new ExecutionOptions
        {
            Backend = ExecutionBackend.Local,
            PermissionMode = ExecutionPermissionMode.Ask,
            ApprovalTimeout = TimeSpan.FromSeconds(5)
        });
        var broker = new ExecutionApprovalBroker(monitor, permStore, NullLogger<ExecutionApprovalBroker>.Instance);

        ExecutionRequest MakeReq(string script, ApprovalOwnerKind kind, string ownerId) => new()
        {
            SessionId = "s-owner",
            TurnId = "t1",
            WorkspaceRoot = _root,
            ToolName = "run_shell",
            Executable = "bash",
            InterpreterChain = [],
            Argv = ["-s"],
            EnvironmentDelta = [],
            Cwd = _root,
            ScriptSha256 = new string(kind == ApprovalOwnerKind.GatewayTurn ? 'b' : 'c', 64),
            Backend = ExecutionBackend.Local,
            Network = "Host",
            Filesystem = "LocalOutputsOnly",
            TrustedOriginVerified = true,
            Origin = ExecutionOrigin.DailyChat,
            ScriptText = script,
            OwnerKind = kind,
            OwnerId = ownerId
        };

        var t1 = broker.AuthorizeAsync(
            new ExecutionAuthorizationContext(MakeReq("echo 1", ApprovalOwnerKind.GatewayTurn, "turn-1"), _root, _root, _root, null),
            CancellationToken.None);
        var t2 = broker.AuthorizeAsync(
            new ExecutionAuthorizationContext(MakeReq("echo 2", ApprovalOwnerKind.BackgroundJob, "job-9:1"), _root, _root, _root, null),
            CancellationToken.None);

        for (var i = 0; i < 40 && broker.GetPendingApprovalCount("s-owner") < 2; i++)
        {
            await Task.Delay(25);
        }

        Assert.Equal(2, broker.GetPendingApprovalCount("s-owner"));
        broker.CancelOwnerApprovals("s-owner", ApprovalOwnerKind.GatewayTurn, "turn-1", "turn cancelled");

        var r1 = await t1;
        Assert.False(r1.Decision.IsAllowed);
        Assert.Single(broker.GetPendingApprovals("s-owner"));
        Assert.Equal(new string('c', 64), broker.GetPendingApprovals("s-owner")[0].ScriptSha256);

        broker.CancelOwnerApprovals("s-owner", ApprovalOwnerKind.BackgroundJob, "job-9:1", "job cancelled");
        var r2 = await t2;
        Assert.False(r2.Decision.IsAllowed);
        Assert.Empty(broker.GetPendingApprovals("s-owner"));
    }

    [Fact]
    public async Task ListAuthorizedDirectoryTool_ShouldPaginateFilterAndBlockSensitiveOrEscapingPaths()
    {
        var ws = Path.Combine(_root, "ws-list");
        var sub = Path.Combine(ws, "docs");
        var gitDir = Path.Combine(ws, ".git");
        var outside = Path.Combine(_root, "outside-secret");
        Directory.CreateDirectory(sub);
        Directory.CreateDirectory(gitDir);
        Directory.CreateDirectory(outside);

        File.WriteAllText(Path.Combine(sub, "a.csv"), "1,2");
        File.WriteAllText(Path.Combine(sub, "b.csv"), "3,4");
        File.WriteAllText(Path.Combine(sub, "notes.txt"), "hi");
        File.WriteAllText(Path.Combine(sub, ".env"), "SECRET=123");
        File.WriteAllText(Path.Combine(gitDir, "config"), "git");
        File.WriteAllText(Path.Combine(outside, "topsecret.txt"), "nope");

        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var tool = new ListAuthorizedDirectoryTool(cfg);

        var okRes = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = ws,
            ["path"] = "docs",
            ["extension"] = "csv",
            ["limit"] = 1,
            ["cursor"] = 0
        });
        Assert.Equal(ToolResultStatus.Success, okRes.Status);
        using var doc = JsonDocument.Parse(okRes.Output);
        Assert.Equal(2, doc.RootElement.GetProperty("totalMatched").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("nextCursor").GetInt32());
        var entryName = doc.RootElement.GetProperty("entries")[0].GetProperty("name").GetString();
        Assert.Equal("a.csv", entryName);

        // .env & .git must not appear even with no extension filter
        var rootRes = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = ws,
            ["path"] = "."
        });
        Assert.DoesNotContain(".git", rootRes.Output, StringComparison.Ordinal);

        var docsRes = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = ws,
            ["path"] = "docs"
        });
        Assert.DoesNotContain(".env", docsRes.Output, StringComparison.Ordinal);

        // Sensitive dir rejected explicitly
        var gitRes = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = ws,
            ["path"] = ".git"
        });
        Assert.NotEqual(ToolResultStatus.Success, gitRes.Status);

        // Unauthorized outside path rejected
        var outsideRes = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["workspace_path"] = ws,
            ["path"] = outside
        });
        Assert.NotEqual(ToolResultStatus.Success, outsideRes.Status);
        Assert.Contains("directory_not_authorized", outsideRes.Output, StringComparison.Ordinal);
    }

    private static TrustedExecutionContext CreateDailyContext(string workspaceRoot, string invocationId)
        => TrustedExecutionContextFactory.ForDailyInvocation(
            userId: "u1",
            sessionId: "s-daily",
            turnId: "turn-1",
            invocationId: invocationId,
            workspaceRoot: workspaceRoot,
            scope: ExportScope.From(null, ["outputs"], null, [".git", ".codexflow", ".codexflow-attachments"]),
            grantedTools: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "run_shell" },
            policyVersion: 1);
}
