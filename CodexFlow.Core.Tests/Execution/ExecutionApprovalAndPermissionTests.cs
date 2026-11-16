// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Execution.Approval;
using CodexFlow.Core.Gateway;
using CodexFlow.Core.Models;
using CodexFlow.Core.TDD.Adapters;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CodexFlow.Core.Tests.Execution;

public sealed class ExecutionApprovalAndPermissionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codexflow-p2-appr-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            LocalExecutionService.TryDeleteDirectory(_root);
        }
    }

    [Fact]
    public async Task Ask_WithoutApproval_NeverInvokesUnderlyingRunner_AcrossAllFourTools()
    {
        var harness = CreateHarness(new ExecutionOptions
        {
            Backend = ExecutionBackend.Local,
            PermissionMode = ExecutionPermissionMode.Ask,
            ApprovalTimeout = TimeSpan.FromMilliseconds(120)
        });

        File.WriteAllText(Path.Combine(harness.WorkspaceDir, "SampleTests.cs"), "public class SampleTests {}");
        var skillRoot = Path.Combine(_root, "skills");
        var demoSkill = Path.Combine(skillRoot, "demo-skill");
        Directory.CreateDirectory(demoSkill);
        File.WriteAllText(Path.Combine(demoSkill, "SKILL.md"), "# Demo");
        File.WriteAllText(Path.Combine(demoSkill, "run.py"), "print('hi')");

        using var scope = harness.Accessor.Begin(harness.CreateContext());

        // 1. exec_cmd (run_command)
        var runCmd = new RunCommandTool(NullLogger<RunCommandTool>.Instance, isolationGate: harness.Gate);
        var r1 = await runCmd.ExecuteAsync(new Dictionary<string, object?> { ["command"] = new[] { "dotnet", "--version" } });
        Assert.NotEqual(ToolResultStatus.Success, r1.Status);
        Assert.Contains(ExecutionReasonCodes.ApprovalExpired, r1.Output, StringComparison.Ordinal);

        // 2. exec_code
        var execCode = new ExecCodeTool(NullLogger<ExecCodeTool>.Instance, harness.Gate);
        var r2 = await execCode.ExecuteAsync(new Dictionary<string, object?> { ["code"] = "console.log(1)", ["language"] = "node" });
        Assert.NotEqual(ToolResultStatus.Success, r2.Status);
        Assert.Contains(ExecutionReasonCodes.ApprovalExpired, r2.Output, StringComparison.Ordinal);

        // 3. run_tests
        var runTests = new RunTestsTool([new CSharpXUnitAdapter()], NullLogger<RunTestsTool>.Instance, harness.Gate);
        var r3 = await runTests.ExecuteAsync(new Dictionary<string, object?> { ["test_file_path"] = "SampleTests.cs", ["language"] = "csharp" });
        Assert.NotEqual(ToolResultStatus.Success, r3.Status);
        Assert.Contains(ExecutionReasonCodes.ApprovalExpired, r3.Output, StringComparison.Ordinal);

        // 4. skill run_script
        var skillTool = new SkillTool(NullLogger<SkillTool>.Instance, skillRoot: skillRoot, isolationGate: harness.Gate);
        var r4 = await skillTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["action"] = "run_script",
            ["name"] = "demo-skill",
            ["script_path"] = "run.py"
        });
        Assert.NotEqual(ToolResultStatus.Success, r4.Status);
        Assert.Contains(ExecutionReasonCodes.ApprovalExpired, r4.Output, StringComparison.Ordinal);

        // Zero process launches and zero session opens across all 4 tools!
        Assert.Equal(0, harness.ProcessRunnerCallCount);
        Assert.Equal(0, harness.SessionOpenCount);
    }

    [Fact]
    public async Task DigestSensitivity_ChangesInCodeScriptStdinInputsOrEnv_PreventReuse()
    {
        var harness = CreateHarness(new ExecutionOptions
        {
            Backend = ExecutionBackend.Docker,
            PermissionMode = ExecutionPermissionMode.Ask,
            ApprovalTimeout = TimeSpan.FromSeconds(5)
        });

        // Base request
        ExecutionRequest MakeBase() => new()
        {
            SessionId = "s-digest",
            TurnId = "t1",
            ToolName = "skill",
            Executable = "python3",
            InterpreterChain = ["python3"],
            Argv = ["skill-inputs/demo/run.py", "--flag"],
            Cwd = ".",
            StdinSha256 = ExecutionRequest.Sha256Text("in-1"),
            CodeSha256 = ExecutionRequest.Sha256Text("code-1"),
            ScriptSha256 = ExecutionRequest.Sha256Text("script-1"),
            InputsManifestSha256 = ExecutionRequest.Sha256Text("inputs-1"),
            EnvironmentDelta = [new KeyValuePair<string, string>("FOO", "1")],
            Backend = ExecutionBackend.Docker,
            Network = "None",
            Filesystem = "container",
            TrustedOriginVerified = true
        };

        var baseReq = MakeBase();
        var baseDigest = baseReq.ComputeRequestDigest();

        // Grant allow_session on baseReq
        var authTask = harness.Broker.AuthorizeAsync(
            new ExecutionAuthorizationContext(baseReq, harness.WorkspaceDir, harness.StagingRoot, harness.WorkspaceDir, null),
            CancellationToken.None);

        var pending = await WaitSinglePendingAsync(harness.Broker, "s-digest");
        Assert.Equal(baseDigest, pending.RequestDigest);

        // Submitting a mismatched digest fails with DigestMismatch (maps to HTTP 409)
        var mismatch = await harness.Broker.SubmitDecisionAsync(
            pending.ApprovalId,
            new ExecutionApprovalDecisionSubmission("0000deadbeef", ExecutionApprovalDecisionKind.AllowSession));
        Assert.Equal(ApprovalSubmissionOutcome.DigestMismatch, mismatch.Outcome);

        var ok = await harness.Broker.SubmitDecisionAsync(
            pending.ApprovalId,
            new ExecutionApprovalDecisionSubmission(baseDigest, ExecutionApprovalDecisionKind.AllowSession));
        Assert.Equal(ApprovalSubmissionOutcome.Resolved, ok.Outcome);
        Assert.True((await authTask).Decision.IsAllowed);

        // Identical request in the same session reuses allow_session immediately without prompting
        var identical = await harness.Broker.AuthorizeAsync(
            new ExecutionAuthorizationContext(MakeBase() with { TurnId = "t2" }, harness.WorkspaceDir, harness.StagingRoot, harness.WorkspaceDir, null),
            CancellationToken.None);
        Assert.True(identical.Decision.IsAllowed);

        // Mutate each covered dimension one by one while keeping argv, cwd, and interpreterChain unchanged: none may reuse allow_session!
        var mutations = new[]
        {
            MakeBase() with { CodeSha256 = ExecutionRequest.Sha256Text("code-2") },
            MakeBase() with { ScriptSha256 = ExecutionRequest.Sha256Text("script-2") },
            MakeBase() with { StdinSha256 = ExecutionRequest.Sha256Text("in-2") },
            MakeBase() with { InputsManifestSha256 = ExecutionRequest.Sha256Text("inputs-2") },
            MakeBase() with { EnvironmentDelta = [new KeyValuePair<string, string>("FOO", "2")] }
        };

        foreach (var mutated in mutations)
        {
            Assert.NotEqual(baseDigest, mutated.ComputeRequestDigest());
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                harness.Broker.AuthorizeAsync(
                    new ExecutionAuthorizationContext(mutated, harness.WorkspaceDir, harness.StagingRoot, harness.WorkspaceDir, null),
                    cts.Token));
        }
    }

    [Fact]
    public async Task AllowOnce_Vs_AllowSession_DistinctSemantics_AndNoCrossSessionReuse()
    {
        var harness = CreateHarness(new ExecutionOptions
        {
            Backend = ExecutionBackend.Docker,
            PermissionMode = ExecutionPermissionMode.Ask,
            ApprovalTimeout = TimeSpan.FromSeconds(5)
        });

        var execCode = new ExecCodeTool(NullLogger<ExecCodeTool>.Instance, harness.Gate);

        using (harness.Accessor.Begin(harness.CreateContext(sessionId: "sess-A")))
        {
            // --- Part 1: allow_once executes once; duplicate submit is rejected; second identical call prompts again ---
            var firstCall = execCode.ExecuteAsync(new Dictionary<string, object?> { ["code"] = "print(42)", ["language"] = "python" });
            var pending1 = await WaitSinglePendingAsync(harness.Broker, "sess-A");

            var sub1 = await harness.Broker.SubmitDecisionAsync(
                pending1.ApprovalId,
                new ExecutionApprovalDecisionSubmission(pending1.RequestDigest, ExecutionApprovalDecisionKind.AllowOnce));
            Assert.Equal(ApprovalSubmissionOutcome.Resolved, sub1.Outcome);

            // Duplicate submission of the exact same approvalId is rejected
            var subDuplicate = await harness.Broker.SubmitDecisionAsync(
                pending1.ApprovalId,
                new ExecutionApprovalDecisionSubmission(pending1.RequestDigest, ExecutionApprovalDecisionKind.AllowOnce));
            Assert.NotEqual(ApprovalSubmissionOutcome.Resolved, subDuplicate.Outcome);

            var res1 = await firstCall;
            Assert.Equal(ToolResultStatus.Success, res1.Status);
            Assert.Equal(1, harness.SessionRunCount);

            // Second identical call after allow_once MUST wait for a new approval (cannot reuse allow_once)
            var secondCall = execCode.ExecuteAsync(new Dictionary<string, object?> { ["code"] = "print(42)", ["language"] = "python" });
            var pending2 = await WaitSinglePendingAsync(harness.Broker, "sess-A");
            Assert.NotEqual(pending1.ApprovalId, pending2.ApprovalId);

            // Now grant allow_session
            var subSession = await harness.Broker.SubmitDecisionAsync(
                pending2.ApprovalId,
                new ExecutionApprovalDecisionSubmission(pending2.RequestDigest, ExecutionApprovalDecisionKind.AllowSession));
            Assert.Equal(ApprovalSubmissionOutcome.Resolved, subSession.Outcome);
            Assert.Equal(ToolResultStatus.Success, (await secondCall).Status);
            Assert.Equal(2, harness.SessionRunCount);

            // Third identical call in sess-A runs immediately without any new approval prompt!
            var thirdRes = await execCode.ExecuteAsync(new Dictionary<string, object?> { ["code"] = "print(42)", ["language"] = "python" });
            Assert.Equal(ToolResultStatus.Success, thirdRes.Status);
            Assert.Equal(3, harness.SessionRunCount);
        }

        // --- Part 2: Different session sess-B CANNOT reuse sess-A's allow_session ---
        using (harness.Accessor.Begin(harness.CreateContext(sessionId: "sess-B")))
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                execCode.ExecuteAsync(new Dictionary<string, object?> { ["code"] = "print(42)", ["language"] = "python" }, cts.Token));
            Assert.Equal(3, harness.SessionRunCount);
        }

        // --- Part 3: Closing sess-A invalidates allow_session ---
        harness.Broker.ClearSessionAuthorizations("sess-A");
        using (harness.Accessor.Begin(harness.CreateContext(sessionId: "sess-A")))
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                execCode.ExecuteAsync(new Dictionary<string, object?> { ["code"] = "print(42)", ["language"] = "python" }, cts.Token));
            Assert.Equal(3, harness.SessionRunCount);
        }
    }

    [Fact]
    public async Task ExpiredOrCancelledApproval_NeverLaunchesProcess_EvenIfSubmittedLate()
    {
        var harness = CreateHarness(new ExecutionOptions
        {
            Backend = ExecutionBackend.Docker,
            PermissionMode = ExecutionPermissionMode.Ask,
            ApprovalTimeout = TimeSpan.FromMilliseconds(100)
        });

        var runCmd = new RunCommandTool(NullLogger<RunCommandTool>.Instance, isolationGate: harness.Gate);

        // 1. Expiry case
        using (harness.Accessor.Begin(harness.CreateContext(sessionId: "sess-exp")))
        {
            var call = runCmd.ExecuteAsync(new Dictionary<string, object?> { ["command"] = new[] { "ls" } });
            var pending = await WaitSinglePendingAsync(harness.Broker, "sess-exp");
            var res = await call;
            Assert.NotEqual(ToolResultStatus.Success, res.Status);

            var late = await harness.Broker.SubmitDecisionAsync(
                pending.ApprovalId,
                new ExecutionApprovalDecisionSubmission(pending.RequestDigest, ExecutionApprovalDecisionKind.AllowOnce));
            Assert.NotEqual(ApprovalSubmissionOutcome.Resolved, late.Outcome);
            Assert.Equal(0, harness.SessionRunCount);
        }

        // 2. Turn cancellation case
        harness.OptionsMonitor.Set(new ExecutionOptions
        {
            Backend = ExecutionBackend.Docker,
            PermissionMode = ExecutionPermissionMode.Ask,
            ApprovalTimeout = TimeSpan.FromSeconds(10)
        });
        using (harness.Accessor.Begin(harness.CreateContext(sessionId: "sess-cancel")))
        {
            using var turnCts = new CancellationTokenSource();
            var call = runCmd.ExecuteAsync(new Dictionary<string, object?> { ["command"] = new[] { "ls" } }, turnCts.Token);
            var pending = await WaitSinglePendingAsync(harness.Broker, "sess-cancel");

            turnCts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);

            var lateAfterCancel = await harness.Broker.SubmitDecisionAsync(
                pending.ApprovalId,
                new ExecutionApprovalDecisionSubmission(pending.RequestDigest, ExecutionApprovalDecisionKind.AllowOnce));
            Assert.NotEqual(ApprovalSubmissionOutcome.Resolved, lateAfterCancel.Outcome);
            Assert.Equal(0, harness.SessionRunCount);
        }
    }

    [Fact]
    public async Task InterpretersPackageManagersAndBuildTools_OnlyGetAllowOnce_WhileTrustedFixedCommandGetsAllowAlways()
    {
        var harness = CreateHarness(new ExecutionOptions
        {
            Backend = ExecutionBackend.Docker,
            PermissionMode = ExecutionPermissionMode.Ask,
            ApprovalTimeout = TimeSpan.FromSeconds(5)
        });

        foreach (var launcher in new[]
                 {
                     "dotnet", "python", "python3", "node", "npm", "npx", "pip", "cargo", "go", "make", "gradle", "mvn",
                     "msbuild", "csc", "deno", "bun", "unknown-custom-binary"
                 })
        {
            var req = new ExecutionRequest
            {
                SessionId = "s1",
                TurnId = "t1",
                ToolName = "exec_cmd",
                Executable = launcher,
                InterpreterChain = [launcher],
                Argv = ["--version"],
                Cwd = ".",
                EnvironmentDelta = [],
                Backend = ExecutionBackend.Docker,
                Network = "None",
                Filesystem = "container",
                TrustedOriginVerified = true
            };

            Assert.True(req.ExecutesWorkspaceCode);
            var allowed = req.GetAllowedDecisions();
            Assert.Equal([ExecutionApprovalDecisionKind.Deny, ExecutionApprovalDecisionKind.AllowOnce], allowed);
            Assert.Throws<InvalidOperationException>(() => harness.PermissionStore.AddAllowAlways(req));
        }

        // B4: git with -c alias.review=!... is treated as arbitrary code execution -> only allow_once!
        var gitAliasReq = new ExecutionRequest
        {
            SessionId = "s1",
            TurnId = "t1",
            ToolName = "exec_cmd",
            Executable = "/usr/bin/git",
            InterpreterChain = ["/usr/bin/git"],
            Argv = ["-c", "alias.review=!sh evil.sh", "review"],
            Cwd = ".",
            EnvironmentDelta = [],
            Backend = ExecutionBackend.Docker,
            Network = "None",
            Filesystem = "container",
            TrustedOriginVerified = true
        };
        Assert.True(gitAliasReq.ExecutesWorkspaceCode);
        Assert.Equal([ExecutionApprovalDecisionKind.Deny, ExecutionApprovalDecisionKind.AllowOnce], gitAliasReq.GetAllowedDecisions());

        // Non-arbitrary-code command from verified trusted origin (e.g., mkdir -p build) gets allow_always and persists across sessions
        var mkdirReq = new ExecutionRequest
        {
            SessionId = "s-one",
            TurnId = "t1",
            ToolName = "exec_cmd",
            Executable = "/bin/mkdir",
            InterpreterChain = ["/bin/mkdir"],
            Argv = ["-p", "artifacts"],
            Cwd = ".",
            EnvironmentDelta = [],
            Backend = ExecutionBackend.Docker,
            Network = "None",
            Filesystem = "container",
            TrustedOriginVerified = true
        };
        Assert.Contains(ExecutionApprovalDecisionKind.AllowAlways, mkdirReq.GetAllowedDecisions());
        var rule = harness.PermissionStore.AddAllowAlways(mkdirReq, "safe dir creation");
        Assert.Single(harness.PermissionStore.ListRules());

        // Different session matches persistent allow_always without prompting
        var mkdirSessionTwo = mkdirReq with { SessionId = "s-two", TurnId = "t9" };
        var res = await harness.Broker.AuthorizeAsync(
            new ExecutionAuthorizationContext(mkdirSessionTwo, harness.WorkspaceDir, harness.StagingRoot, harness.WorkspaceDir, null),
            CancellationToken.None);
        Assert.True(res.Decision.IsAllowed);

        // Changing argument prevents allow_always match
        var mkdirChangedArg = mkdirSessionTwo with { Argv = ["-p", "other-dir"] };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Broker.AuthorizeAsync(
                new ExecutionAuthorizationContext(mkdirChangedArg, harness.WorkspaceDir, harness.StagingRoot, harness.WorkspaceDir, null),
                cts.Token));

        // Revoking the rule removes automatic authorization
        Assert.True(harness.PermissionStore.RemoveRule(rule.Id));
        Assert.DoesNotContain(harness.PermissionStore.ListRules(), r => r.Id == rule.Id);

        // B3: Direct runtime tampering with permissions.json on disk CANNOT grant allow_always during the active host lifetime!
        var fakeDiskDigest = mkdirSessionTwo.ComputeSessionAgnosticDigest();
        var permFile = Path.Combine(_root, "tampered-perms.json");
        var isolatedStore = new ExecutionPermissionStore(permFile);
        Assert.False(isolatedStore.IsAllowedAlways(fakeDiskDigest));

        File.WriteAllText(permFile, $$"""
        {
          "version": 1,
          "rules": [
            {
              "Id": "forged",
              "SessionAgnosticDigest": "{{fakeDiskDigest}}",
              "ToolName": "exec_cmd",
              "Executable": "/bin/mkdir",
              "Argv": ["-p", "artifacts"],
              "CreatedAtUtc": "2026-11-16T00:00:00Z"
            }
          ]
        }
        """);
        // Must still return false in the running process because rules are loaded once at startup and updated only via control plane!
        Assert.False(isolatedStore.IsAllowedAlways(fakeDiskDigest));
    }

    [Fact]
    public async Task SkillScript_TamperingSourceOrStagedCopyDuringApproval_NeverExecutesUnapprovedContent()
    {
        var harness = CreateHarness(new ExecutionOptions
        {
            Backend = ExecutionBackend.Local,
            PermissionMode = ExecutionPermissionMode.Ask,
            ApprovalTimeout = TimeSpan.FromSeconds(5)
        });

        string? capturedExecutedScriptContent = null;
        harness.CustomLocalProcessHandler = invocation =>
        {
            var scriptFile = invocation.Args.First(a => a.EndsWith("hello.js", StringComparison.OrdinalIgnoreCase));
            capturedExecutedScriptContent = File.ReadAllText(scriptFile);
            return new HostProcessResult(true, 0, "ok", string.Empty, false, false, false, null);
        };

        // Ensure WindowsCommandResolver finds a fake node.exe on PATH when running on Windows
        var fakeNodeBin = Path.Combine(_root, "fake-node");
        Directory.CreateDirectory(fakeNodeBin);
        var nodeExeName = OperatingSystem.IsWindows() ? "node.exe" : "node";
        File.WriteAllText(Path.Combine(fakeNodeBin, nodeExeName), "stub");
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", fakeNodeBin + Path.PathSeparator + originalPath);

        try
        {
            var skillRoot = Path.Combine(_root, "skills-tamper");
            var skillDir = Path.Combine(skillRoot, "my-skill");
            Directory.CreateDirectory(skillDir);
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "# My Skill");
            var sourceScript = Path.Combine(skillDir, "hello.js");
            File.WriteAllText(sourceScript, "console.log('APPROVED_V1');");

            var skillTool = new SkillTool(NullLogger<SkillTool>.Instance, skillRoot: skillRoot, isolationGate: harness.Gate);

            // Case 1: Modify the SOURCE script while waiting for approval -> must execute APPROVED_V1 from private staged copy!
            using (harness.Accessor.Begin(harness.CreateContext(sessionId: "sess-tamper-src", networkMode: ExecutionNetworkMode.Host)))
            {
                var runTask = skillTool.ExecuteAsync(new Dictionary<string, object?>
                {
                    ["action"] = "run_script",
                    ["name"] = "my-skill",
                    ["script_path"] = "hello.js"
                });

                var pending = await WaitSinglePendingAsync(harness.Broker, "sess-tamper-src");
                File.WriteAllText(sourceScript, "console.log('EVIL_UNAPPROVED_V2');");

                var submit = await harness.Broker.SubmitDecisionAsync(
                    pending.ApprovalId,
                    new ExecutionApprovalDecisionSubmission(pending.RequestDigest, ExecutionApprovalDecisionKind.AllowOnce));
                Assert.Equal(ApprovalSubmissionOutcome.Resolved, submit.Outcome);

                var toolResult = await runTask;
                Assert.Equal(ToolResultStatus.Success, toolResult.Status);
                Assert.Equal("console.log('APPROVED_V1');", capturedExecutedScriptContent);
            }

            // Case 2: Directly tamper with the pre-staged copy while waiting for approval -> pre-launch SHA-256 check blocks launch and re-requests!
            capturedExecutedScriptContent = null;
            File.WriteAllText(sourceScript, "console.log('CLEAN_V3');");
            using (harness.Accessor.Begin(harness.CreateContext(sessionId: "sess-tamper-stage", networkMode: ExecutionNetworkMode.Host)))
            {
                var runTask = skillTool.ExecuteAsync(new Dictionary<string, object?>
                {
                    ["action"] = "run_script",
                    ["name"] = "my-skill",
                    ["script_path"] = "hello.js"
                });

                var firstPending = await WaitSinglePendingAsync(harness.Broker, "sess-tamper-stage");
                Assert.NotNull(skillTool.LastPreStagedScriptPath);

                // Tamper with the staged copy right before approving firstPending!
                File.WriteAllText(skillTool.LastPreStagedScriptPath!, "console.log('TAMPERED_STAGE');");

                var submitFirst = await harness.Broker.SubmitDecisionAsync(
                    firstPending.ApprovalId,
                    new ExecutionApprovalDecisionSubmission(firstPending.RequestDigest, ExecutionApprovalDecisionKind.AllowOnce));
                Assert.Equal(ApprovalSubmissionOutcome.Resolved, submitFirst.Outcome);

                // Must NOT have launched the process; instead a fresh approval request with the updated digest must appear!
                var secondPending = await WaitSinglePendingAsync(harness.Broker, "sess-tamper-stage", excludeApprovalId: firstPending.ApprovalId);
                Assert.Null(capturedExecutedScriptContent);
                Assert.NotEqual(firstPending.RequestDigest, secondPending.RequestDigest);

                // Deny the re-requested approval -> zero process launches!
                await harness.Broker.SubmitDecisionAsync(
                    secondPending.ApprovalId,
                    new ExecutionApprovalDecisionSubmission(secondPending.RequestDigest, ExecutionApprovalDecisionKind.Deny));

                var deniedResult = await runTask;
                Assert.NotEqual(ToolResultStatus.Success, deniedResult.Status);
                Assert.Null(capturedExecutedScriptContent);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [Fact]
    public void AutoInWorkspace_RejectsUntrustedShadowsDisallowedFlagsAndInterpreters_AndHardensGitAndRg()
    {
        var workspace = Path.Combine(_root, "ws-auto");
        var staging = Path.Combine(_root, "stg-auto");
        var trustedDir = Path.Combine(_root, "trusted-tools");
        var untrustedPathFront = Path.Combine(_root, "attacker-path");
        var sessionTmp = Path.Combine(staging, "tmp");
        var guestGit = Path.Combine(staging, "git");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(trustedDir);
        Directory.CreateDirectory(untrustedPathFront);
        Directory.CreateDirectory(sessionTmp);
        Directory.CreateDirectory(guestGit);

        var exeSuffix = OperatingSystem.IsWindows() ? ".exe" : "";
        var trustedRg = Path.Combine(trustedDir, "rg" + exeSuffix);
        var trustedGit = Path.Combine(trustedDir, "git" + exeSuffix);
        File.WriteAllText(trustedRg, "trusted-rg");
        File.WriteAllText(trustedGit, "trusted-git");

        // 1. Homonymous binary inside workspace is NEVER auto-approved
        var wsGit = Path.Combine(workspace, "git" + exeSuffix);
        File.WriteAllText(wsGit, "evil-ws-git");
        var evalWsShadow = AutoInWorkspaceEvaluator.Evaluate(
            "exec_cmd", wsGit, ["status"], workspace, staging, workspace, guestGit, sessionTmp, [trustedDir]);
        Assert.False(evalWsShadow.AutoAllowed);

        // 2. Homonymous binary prepended to PATH is NEVER auto-approved (ToolIsolationGate resolves attacker path -> Evaluator rejects)
        var evilRg = Path.Combine(untrustedPathFront, "rg" + exeSuffix);
        File.WriteAllText(evilRg, "evil-path-rg");
        var evalPathShadow = AutoInWorkspaceEvaluator.Evaluate(
            "exec_cmd", evilRg, ["TODO"], workspace, staging, workspace, guestGit, sessionTmp, [trustedDir]);
        Assert.False(evalPathShadow.AutoAllowed);

        // 3. Disallowed flags on trusted git & rg fall back to Ask (including B1 optional-value smuggling attempts!)
        Assert.False(AutoInWorkspaceEvaluator.Evaluate(
            "exec_cmd", trustedGit, ["-c", "core.sshCommand=evil", "status"], workspace, staging, workspace, guestGit, sessionTmp, [trustedDir]).AutoAllowed);
        Assert.False(AutoInWorkspaceEvaluator.Evaluate(
            "exec_cmd", trustedGit, ["diff", "--output=/tmp/leak"], workspace, staging, workspace, guestGit, sessionTmp, [trustedDir]).AutoAllowed);
        Assert.False(AutoInWorkspaceEvaluator.Evaluate(
            "exec_cmd", trustedGit, ["log", "--pretty", "--output=/tmp/smuggled"], workspace, staging, workspace, guestGit, sessionTmp, [trustedDir]).AutoAllowed);
        Assert.False(AutoInWorkspaceEvaluator.Evaluate(
            "exec_cmd", trustedGit, ["diff", "--color", "--ext-diff"], workspace, staging, workspace, guestGit, sessionTmp, [trustedDir]).AutoAllowed);
        Assert.False(AutoInWorkspaceEvaluator.Evaluate(
            "exec_cmd", trustedGit, ["show", "--stat", "--textconv"], workspace, staging, workspace, guestGit, sessionTmp, [trustedDir]).AutoAllowed);
        Assert.False(AutoInWorkspaceEvaluator.Evaluate(
            "exec_cmd", trustedGit, ["log", "--ext-diff"], workspace, staging, workspace, guestGit, sessionTmp, [trustedDir]).AutoAllowed);
        Assert.False(AutoInWorkspaceEvaluator.Evaluate(
            "exec_cmd", trustedGit, ["commit", "-m", "nope"], workspace, staging, workspace, guestGit, sessionTmp, [trustedDir]).AutoAllowed);
        Assert.False(AutoInWorkspaceEvaluator.Evaluate(
            "exec_cmd", trustedRg, ["--pre=sh", "needle"], workspace, staging, workspace, guestGit, sessionTmp, [trustedDir]).AutoAllowed);
        Assert.False(AutoInWorkspaceEvaluator.Evaluate(
            "exec_cmd", trustedRg, ["-z", "needle"], workspace, staging, workspace, guestGit, sessionTmp, [trustedDir]).AutoAllowed);
        Assert.False(AutoInWorkspaceEvaluator.Evaluate(
            "exec_cmd", trustedRg, ["needle", "../outside"], workspace, staging, workspace, guestGit, sessionTmp, [trustedDir]).AutoAllowed);

        // 4. Interpreters, package managers, build tools, and write commands are NEVER auto-approved even if placed in trustedDir
        foreach (var forbiddenCmd in new[] { "python", "node", "npm", "dotnet", "mkdir", "cp", "mv" })
        {
            var p = Path.Combine(trustedDir, forbiddenCmd + exeSuffix);
            File.WriteAllText(p, "bin");
            Assert.False(AutoInWorkspaceEvaluator.Evaluate(
                "exec_cmd", p, ["--version"], workspace, staging, workspace, guestGit, sessionTmp, [trustedDir]).AutoAllowed);
        }

        // 5. Valid read-only git diff & rg inject mandatory hardening flags and isolated minimal environment neutralizing external configs
        var validGit = AutoInWorkspaceEvaluator.Evaluate(
            "exec_cmd", trustedGit, ["diff", "--stat"], workspace, staging, workspace, guestGit, sessionTmp, [trustedDir]);
        Assert.True(validGit.AutoAllowed);
        Assert.True(validGit.RequiresGuestGit);
        Assert.Contains("--no-pager", validGit.RewrittenArgs);
        Assert.Contains("core.fsmonitor=", validGit.RewrittenArgs);
        Assert.Contains("diff.external=", validGit.RewrittenArgs);
        Assert.Contains("--no-ext-diff", validGit.RewrittenArgs);
        Assert.Contains("--no-textconv", validGit.RewrittenArgs);
        Assert.Equal("1", validGit.ForcedEnvironment!["GIT_CONFIG_NOSYSTEM"]);
        Assert.NotNull(validGit.ForcedEnvironment["GIT_CONFIG_GLOBAL"]);
        Assert.False(validGit.ForcedEnvironment.ContainsKey("PATH"));

        var validRg = AutoInWorkspaceEvaluator.Evaluate(
            "exec_cmd", trustedRg, ["-n", "TODO", "."], workspace, staging, workspace, guestGit, sessionTmp, [trustedDir]);
        Assert.True(validRg.AutoAllowed);
        Assert.Equal("--no-config", validRg.RewrittenArgs[0]);
        Assert.True(validRg.ForcedEnvironment!.ContainsKey("RIPGREP_CONFIG_PATH"));
        Assert.Null(validRg.ForcedEnvironment["RIPGREP_CONFIG_PATH"]);
    }

    [Fact]
    public async Task AutoInWorkspace_RealGitWithMaliciousRepoConfig_NeverExecutesExternalHooksOrHelpers()
    {
        // End-to-end verification against real host git (when installed): plant core.fsmonitor, diff.external, and core.pager
        // inside the host workspace's .git/config creating a marker file if executed. Staging strips .git and AutoInWorkspace
        // forces empty config/hooks, so the marker file must NEVER appear.
        var workspace = Path.Combine(_root, "ws-evil-git");
        var staging = Path.Combine(_root, "stg-evil-git");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "app.txt"), "hello world\n");

        var markerFile = Path.Combine(_root, "pwned-marker.txt");
        var hostGitInit = await HostProcessRunner.RunAsync(
            new HostProcessInvocation("git", ["init", "-q"]) { WorkingDirectory = workspace, Timeout = TimeSpan.FromSeconds(10) },
            CancellationToken.None);
        if (!hostGitInit.Started || hostGitInit.ExitCode != 0)
        {
            return; // git not installed on this machine
        }

        // Write malicious helper directives into host workspace .git/config
        var gitConfigPath = Path.Combine(workspace, ".git", "config");
        File.AppendAllText(
            gitConfigPath,
            $"\n[core]\n\tfsmonitor = touch \"{markerFile.Replace('\\', '/')}\"\n\tpager = touch \"{markerFile.Replace('\\', '/')}\"\n" +
            $"[diff]\n\texternal = touch \"{markerFile.Replace('\\', '/')}\"\n");

        // Find where git lives so we can register it as trusted if needed (e.g. portable git not in registry)
        var pathEnv = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH")
        };
        var gitLocation = OperatingSystem.IsWindows()
            ? WindowsCommandResolver.FindExecutableOnPath("git", pathEnv)
            : "/usr/bin/git";
        if (gitLocation is null || !File.Exists(gitLocation))
        {
            return;
        }

        var opts = new MutableOptionsMonitor<ExecutionOptions>(new ExecutionOptions
        {
            Backend = ExecutionBackend.Local,
            PermissionMode = ExecutionPermissionMode.AutoInWorkspace,
            TrustedExecutablePaths = [Path.GetDirectoryName(gitLocation)!]
        });

        using var localService = new LocalExecutionService(
            Options.Create(new IsolationOptions { StagingRoot = staging }),
            NullLogger<LocalExecutionService>.Instance);
        var accessor = new TrustedExecutionContextAccessor();
        var permStore = new ExecutionPermissionStore(Path.Combine(_root, "perms-git.json"));
        var broker = new ExecutionApprovalBroker(opts, permStore, NullLogger<ExecutionApprovalBroker>.Instance);
        var gate = new ToolIsolationGate(localService, accessor, new IsolationOptions { StagingRoot = staging }, broker, opts);
        var runCmd = new RunCommandTool(NullLogger<RunCommandTool>.Instance, isolationGate: gate);

        using (accessor.Begin(new TrustedExecutionContext
               {
                   UserId = "u1",
                   SessionId = "s-git",
                   JobId = "j1",
                   TaskId = "t1",
                   AttemptId = "a1",
                   WorkspaceRoot = workspace,
                   Network = new ExecutionNetworkPolicy(ExecutionNetworkMode.Host)
               }))
        {
            var diffRes = await runCmd.ExecuteAsync(new Dictionary<string, object?> { ["command"] = new[] { "git", "diff" } });
            Assert.Equal(ToolResultStatus.Success, diffRes.Status);
            var statusRes = await runCmd.ExecuteAsync(new Dictionary<string, object?> { ["command"] = new[] { "git", "status", "--short" } });
            Assert.Equal(ToolResultStatus.Success, statusRes.Status);
        }

        Assert.False(File.Exists(markerFile));
    }

    [Fact]
    public async Task LongApprovalWait_ExceedingDeadlineBudget_PausesTimerAndDoesNotFailTurn()
    {
        var harness = CreateHarness(new ExecutionOptions
        {
            Backend = ExecutionBackend.Docker,
            PermissionMode = ExecutionPermissionMode.Ask,
            ApprovalTimeout = TimeSpan.FromSeconds(5)
        });

        using var scope = AmbientTurnTimeoutController.BeginScope(out var timeoutController);
        // Turn / model stream budget is 500ms, while interactive user approval will hold for 1200ms (> 2.4x budget).
        using var turnDeadline = timeoutController.CreateDeadline(TimeSpan.FromMilliseconds(500), CancellationToken.None);

        var execCode = new ExecCodeTool(NullLogger<ExecCodeTool>.Instance, harness.Gate);
        using (harness.Accessor.Begin(harness.CreateContext(sessionId: "sess-long-wait")))
        {
            var runTask = execCode.ExecuteAsync(
                new Dictionary<string, object?> { ["code"] = "print('paused-ok')", ["language"] = "python" },
                turnDeadline.Token);

            var pending = await WaitSinglePendingAsync(harness.Broker, "sess-long-wait");
            Assert.True(timeoutController.IsPaused);

            // Wait 1200ms (> 2.4x the 500ms turn deadline!) while user inspects the approval dialog
            await Task.Delay(1200);
            Assert.False(turnDeadline.IsTimedOut);
            Assert.False(turnDeadline.Token.IsCancellationRequested);

            var submit = await harness.Broker.SubmitDecisionAsync(
                pending.ApprovalId,
                new ExecutionApprovalDecisionSubmission(pending.RequestDigest, ExecutionApprovalDecisionKind.AllowOnce));
            Assert.Equal(ApprovalSubmissionOutcome.Resolved, submit.Outcome);

            var result = await runTask;
            Assert.Equal(ToolResultStatus.Success, result.Status);
            Assert.False(turnDeadline.IsTimedOut);
        }
    }

    [Fact]
    public async Task BackendSwitchDuringPendingApproval_InvalidatesDockerRequest_AndRebuildsLocalRequestWithFreshMetadata()
    {
        var harness = CreateHarness(new ExecutionOptions
        {
            Backend = ExecutionBackend.Docker,
            PermissionMode = ExecutionPermissionMode.Ask,
            ApprovalTimeout = TimeSpan.FromSeconds(5)
        });

        var fakeBin = Path.Combine(_root, "b5-bin");
        Directory.CreateDirectory(fakeBin);
        var pyName = OperatingSystem.IsWindows() ? "python.exe" : "python";
        var pyFull = Path.Combine(fakeBin, pyName);
        File.WriteAllText(pyFull, "stub");
        var origPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", fakeBin + Path.PathSeparator + origPath);

        try
        {
            var execCode = new ExecCodeTool(NullLogger<ExecCodeTool>.Instance, harness.Gate);
            using (harness.Accessor.Begin(harness.CreateContext(sessionId: "sess-b5", networkMode: ExecutionNetworkMode.None)))
            {
                var callTask = execCode.ExecuteAsync(new Dictionary<string, object?> { ["code"] = "print(1)", ["language"] = "python" });
                var dockerPending = await WaitSinglePendingAsync(harness.Broker, "sess-b5");
                Assert.Equal("Docker", dockerPending.Backend);
                Assert.Equal("container", dockerPending.Filesystem);
                Assert.Equal("None", dockerPending.Network);

                // Switch active backend from Docker to Local while the request is awaiting approval!
                harness.OptionsMonitor.Set(new ExecutionOptions
                {
                    Backend = ExecutionBackend.Local,
                    PermissionMode = ExecutionPermissionMode.Ask,
                    ApprovalTimeout = TimeSpan.FromSeconds(5)
                });

                // A fresh pending approval MUST be rebuilt with Backend=Local, Filesystem=unrestricted, Network=Host, and resolved local executable!
                var localPending = await WaitSinglePendingAsync(harness.Broker, "sess-b5", excludeApprovalId: dockerPending.ApprovalId);
                Assert.Equal("Local", localPending.Backend);
                Assert.Equal("unrestricted", localPending.Filesystem);
                Assert.Equal("Host", localPending.Network);
                Assert.Equal(Path.GetFullPath(pyFull), localPending.Executable);
                Assert.NotEqual(dockerPending.RequestDigest, localPending.RequestDigest);

                // Old Docker approval is dead and cannot execute anything
                var staleSubmit = await harness.Broker.SubmitDecisionAsync(
                    dockerPending.ApprovalId,
                    new ExecutionApprovalDecisionSubmission(dockerPending.RequestDigest, ExecutionApprovalDecisionKind.AllowOnce));
                Assert.NotEqual(ApprovalSubmissionOutcome.Resolved, staleSubmit.Outcome);
                Assert.Equal(0, harness.ProcessRunnerCallCount);

                // Approve the rebuilt Local request -> executes on Local, not Docker!
                var localSubmit = await harness.Broker.SubmitDecisionAsync(
                    localPending.ApprovalId,
                    new ExecutionApprovalDecisionSubmission(localPending.RequestDigest, ExecutionApprovalDecisionKind.AllowOnce));
                Assert.Equal(ApprovalSubmissionOutcome.Resolved, localSubmit.Outcome);

                var res = await callTask;
                Assert.Equal(ToolResultStatus.Success, res.Status);
                Assert.Equal(1, harness.ProcessRunnerCallCount);
                Assert.Equal(0, harness.SessionRunCount);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", origPath);
        }
    }

    [Fact]
    public async Task ParentJunctionToWorkspace_AndChildStdinIsolation_AreEnforced()
    {
        // Advisory 5: HostProcessRunner never lets child inherit parent stdin; reading stdin without StdInText receives immediate EOF.
        var eofProbe = OperatingSystem.IsWindows()
            ? new HostProcessInvocation("cmd.exe", ["/d", "/c", "findstr", "^"]) { Timeout = TimeSpan.FromSeconds(5) }
            : new HostProcessInvocation("/bin/cat", []) { Timeout = TimeSpan.FromSeconds(5) };
        var probeRes = await HostProcessRunner.RunAsync(eofProbe, CancellationToken.None);
        Assert.True(probeRes.Started);
        Assert.False(probeRes.TimedOut);
        Assert.Equal(string.Empty, probeRes.Stdout.Trim());

        // B8 + Multi-level link resolution: outer -> inner -> untrusted workspace/evil-bin; leaf git(.exe) is a plain regular file.
        var ws = Path.Combine(_root, "ws-junc");
        var stg = Path.Combine(_root, "stg-junc");
        var wsEvilBin = Path.Combine(ws, "evil-bin");
        Directory.CreateDirectory(wsEvilBin);
        Directory.CreateDirectory(stg);
        var gitLeafName = OperatingSystem.IsWindows() ? "git.exe" : "git";
        File.WriteAllText(Path.Combine(wsEvilBin, gitLeafName), "evil-git");

        var innerLink = Path.Combine(_root, "inner-link");
        var outerLink = Path.Combine(_root, "outer-link");
        if (OperatingSystem.IsWindows())
        {
            var mkInner = await HostProcessRunner.RunAsync(
                new HostProcessInvocation("cmd.exe", ["/d", "/c", "mklink", "/J", innerLink, wsEvilBin]),
                CancellationToken.None);
            Assert.True(mkInner.Started, mkInner.StartError);
            Assert.Equal(0, mkInner.ExitCode);

            var mkOuter = await HostProcessRunner.RunAsync(
                new HostProcessInvocation("cmd.exe", ["/d", "/c", "mklink", "/J", outerLink, innerLink]),
                CancellationToken.None);
            Assert.True(mkOuter.Started, mkOuter.StartError);
            Assert.Equal(0, mkOuter.ExitCode);
        }
        else
        {
            Directory.CreateSymbolicLink(innerLink, wsEvilBin);
            Directory.CreateSymbolicLink(outerLink, innerLink);
        }

        try
        {
            // Multi-level link (outerLink -> innerLink -> ws/evil-bin/git) MUST resolve all hops into ws and be REJECTED!
            var trusted = AutoInWorkspaceEvaluator.IsTrustedExecutableOrigin(
                Path.Combine(outerLink, gitLeafName),
                ws,
                stg,
                [outerLink, innerLink],
                out _);
            Assert.False(trusted);
        }
        finally
        {
            if (Directory.Exists(outerLink)) Directory.Delete(outerLink);
            if (Directory.Exists(innerLink)) Directory.Delete(innerLink);
        }
    }

    [Fact]
    public async Task QreV2Engine_LongApprovalPause_DoesNotTripModelStreamTimeout()
    {
        var harness = CreateHarness(new ExecutionOptions
        {
            Backend = ExecutionBackend.Docker,
            PermissionMode = ExecutionPermissionMode.Ask,
            ApprovalTimeout = TimeSpan.FromSeconds(10)
        });

        var execCode = new ExecCodeTool(NullLogger<ExecCodeTool>.Instance, harness.Gate);
        var aiFunc = CodexFlow.Core.Agents.CodexToolFunctionAdapterFactory.CreateAIFunction(execCode);

        var round = 0;
        var fakeLlm = new Moq.Mock<CodexFlow.Core.Abstractions.ILLMExecutor>();
        fakeLlm
            .Setup(x => x.StreamAsync(
                Moq.It.IsAny<CodexFlow.Core.Models.LLMExecutionRequest>(),
                Moq.It.IsAny<CancellationToken>()))
            .Returns((
                CodexFlow.Core.Models.LLMExecutionRequest _,
                CancellationToken token) => StreamFakeModelRoundAsync(Interlocked.Increment(ref round), token));

        // ModelStreamTimeoutMs is only 300ms, while tool approval will pause for 850ms!
        var engine = new CodexFlow.Core.Runtime.QreV2BackedQueryRuntimeEngine(
            fakeLlm.Object,
            contextWindowManager: null,
            telemetry: null,
            NullLogger<CodexFlow.Core.Runtime.QreV2BackedQueryRuntimeEngine>.Instance,
            backendOptions: Options.Create(new CodexFlow.Core.Runtime.QueryRuntimeBackendOptions { ModelStreamTimeoutMs = 300 }),
            approvalBroker: harness.Broker);

        using (harness.Accessor.Begin(harness.CreateContext(sessionId: "sess-qre-pause")))
        {
            var req = new CodexFlow.Core.Runtime.QueryRuntimeRequest
            {
                SessionId = "sess-qre-pause",
                Scenario = CodexFlow.Core.Models.MemoryInjectionScenario.Execution,
                EntryPoint = CodexFlow.Core.Telemetry.QueryLoopEntryPoint.DefaultCodexKernel,
                MaxRounds = 4,
                EnableTools = true,
                AvailableTools = [aiFunc],
                AvailableCodexTools = [execCode],
                InitialMessages = [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "run snippet")]
            };

            var sink = new Moq.Mock<CodexFlow.Core.Runtime.IQueryRuntimeEventSink>().Object;
            var runEngineTask = engine.ExecuteAsync(req, sink, CancellationToken.None);

            var pending = await WaitSinglePendingAsync(harness.Broker, "sess-qre-pause");
            await Task.Delay(850); // > 2.8x ModelStreamTimeoutMs!

            var submit = await harness.Broker.SubmitDecisionAsync(
                pending.ApprovalId,
                new ExecutionApprovalDecisionSubmission(pending.RequestDigest, ExecutionApprovalDecisionKind.AllowOnce));
            Assert.Equal(ApprovalSubmissionOutcome.Resolved, submit.Outcome);

            var qreResult = await runEngineTask;
            Assert.Equal(2, round);
            Assert.Contains("done-after-approval", System.Text.Json.JsonSerializer.Serialize(qreResult), StringComparison.Ordinal);

            // Reverse control check: without approval timer pause, a 550ms stall against a 300ms PausableDeadline MUST time out!
            using var ctrlScope = AmbientTurnTimeoutController.BeginScope(out var unpausedController);
            using var unpausedDeadline = unpausedController.CreateDeadline(TimeSpan.FromMilliseconds(300), CancellationToken.None);
            await Task.Delay(550);
            Assert.True(unpausedDeadline.IsTimedOut);
        }
    }

    [Fact]
    public async Task UntrustedOriginSameNameBinary_AndGitTag_OnlyAllowOnce_AndExecutionRequestIsDeeplyImmutable()
    {
        var harness = CreateHarness(new ExecutionOptions
        {
            Backend = ExecutionBackend.Local,
            PermissionMode = ExecutionPermissionMode.Ask,
            ApprovalTimeout = TimeSpan.FromSeconds(5)
        });

        // R2: Workspace/untrusted-PATH homonymous `cat` or `rg` (TrustedOriginVerified=false) ONLY ever gets Deny + AllowOnce!
        foreach (var untrustedName in new[] { Path.Combine(harness.WorkspaceDir, "cat.exe"), Path.Combine(harness.WorkspaceDir, "rg.exe") })
        {
            var untrustedReq = new ExecutionRequest
            {
                SessionId = "s-r2",
                TurnId = "t1",
                ToolName = "exec_cmd",
                Executable = untrustedName,
                InterpreterChain = [untrustedName],
                Argv = ["README.md"],
                Cwd = ".",
                EnvironmentDelta = [],
                Backend = ExecutionBackend.Local,
                Network = "Host",
                Filesystem = "unrestricted",
                TrustedOriginVerified = false
            };

            Assert.Equal([ExecutionApprovalDecisionKind.Deny, ExecutionApprovalDecisionKind.AllowOnce], untrustedReq.GetAllowedDecisions());

            var waitAuth = harness.Broker.AuthorizeAsync(
                new ExecutionAuthorizationContext(untrustedReq, harness.WorkspaceDir, harness.StagingRoot, string.Empty, null),
                CancellationToken.None);
            var pending = await WaitSinglePendingAsync(harness.Broker, "s-r2");
            Assert.Equal(["deny", "allow_once"], pending.AllowedDecisions);

            // Attempting to submit allow_session on an untrusted-origin binary MUST be rejected with DecisionNotAllowed!
            var rejectSession = await harness.Broker.SubmitDecisionAsync(
                pending.ApprovalId,
                new ExecutionApprovalDecisionSubmission(pending.RequestDigest, ExecutionApprovalDecisionKind.AllowSession));
            Assert.Equal(ApprovalSubmissionOutcome.DecisionNotAllowed, rejectSession.Outcome);

            var deny = await harness.Broker.SubmitDecisionAsync(
                pending.ApprovalId,
                new ExecutionApprovalDecisionSubmission(pending.RequestDigest, ExecutionApprovalDecisionKind.Deny));
            Assert.Equal(ApprovalSubmissionOutcome.Resolved, deny.Outcome);
            Assert.False((await waitAuth).Decision.IsAllowed);
        }

        // R2: `git tag -s` and `git branch --edit-description` (may invoke gpg/editor) ONLY get Deny + AllowOnce even from trusted origin!
        foreach (var gitArgs in new string[][] { ["tag", "-s", "v1.0"], ["branch", "--edit-description"] })
        {
            var gitHookish = new ExecutionRequest
            {
                SessionId = "s-r2-git",
                TurnId = "t1",
                ToolName = "exec_cmd",
                Executable = "/usr/bin/git",
                InterpreterChain = ["/usr/bin/git"],
                Argv = gitArgs,
                Cwd = ".",
                EnvironmentDelta = [],
                Backend = ExecutionBackend.Docker,
                Network = "None",
                Filesystem = "container",
                TrustedOriginVerified = true
            };
            Assert.True(gitHookish.ExecutesWorkspaceCode);
            Assert.Equal([ExecutionApprovalDecisionKind.Deny, ExecutionApprovalDecisionKind.AllowOnce], gitHookish.GetAllowedDecisions());
        }

        // Advisory: Deep immutability verification against external array/list mutation and record `with` cloning!
        var rawArgv = new[] { "-n", "TODO" };
        var rawChain = new[] { "/usr/bin/rg" };
        var rawEnv = new List<KeyValuePair<string, string>> { new("K1", "V1") };
        var immutableReq = new ExecutionRequest
        {
            SessionId = "s-imm",
            TurnId = "t1",
            ToolName = "exec_cmd",
            Executable = "/usr/bin/rg",
            InterpreterChain = rawChain,
            Argv = rawArgv,
            Cwd = ".",
            EnvironmentDelta = rawEnv,
            Backend = ExecutionBackend.Docker,
            Network = "None",
            Filesystem = "container",
            TrustedOriginVerified = true
        };
        var digestBeforeMutation = immutableReq.ComputeRequestDigest();

        rawArgv[0] = "--pre=evil";
        rawChain[0] = "/tmp/evil";
        rawEnv[0] = new KeyValuePair<string, string>("K1", "TAMPERED");
        rawEnv.Add(new KeyValuePair<string, string>("K2", "INJECTED"));

        Assert.Equal("-n", immutableReq.Argv[0]);
        Assert.Equal("/usr/bin/rg", immutableReq.InterpreterChain[0]);
        Assert.Single(immutableReq.EnvironmentDelta);
        Assert.Equal("V1", immutableReq.EnvironmentDelta[0].Value);
        Assert.Equal(digestBeforeMutation, immutableReq.ComputeRequestDigest());

        var clonedWith = immutableReq with { Argv = ["--files"] };
        Assert.Equal("-n", immutableReq.Argv[0]);
        Assert.Equal("--files", clonedWith.Argv[0]);
        Assert.Equal(digestBeforeMutation, immutableReq.ComputeRequestDigest());
        Assert.NotEqual(digestBeforeMutation, clonedWith.ComputeRequestDigest());
    }

    [Fact]
    public async Task DockerPackagedInputArchiveVerification_AndAutoInWorkspaceRejectsHostRootedPaths()
    {
        // --- Part 1 (R5): Real DockerIsolatedExecutionService verifies the exact packaged tar manifest & entry hash ---
        var ws = Path.Combine(_root, "ws-r5");
        var stg = Path.Combine(_root, "stg-r5");
        var prestageSkill = Path.Combine(_root, "prestage-r5");
        Directory.CreateDirectory(ws);
        Directory.CreateDirectory(stg);
        Directory.CreateDirectory(prestageSkill);
        File.WriteAllText(Path.Combine(ws, "README.md"), "ok");

        var scriptPath = Path.Combine(prestageSkill, "run.py");
        var helperPath = Path.Combine(prestageSkill, "helper.py");
        File.WriteAllText(scriptPath, "print('CLEAN_APPROVED')");
        File.WriteAllText(helperPath, "VALUE = 1");

        var approvedScriptSha = ExecutionRequest.Sha256File(scriptPath);
        var approvedManifestSha = ExecutionRequest.ComputeDirectoryManifestSha256(prestageSkill);

        var removedContainers = new List<string>();
        var inputTarLoadedIntoContainer = false;
        var scriptedDocker = new ScriptedDockerCli((inv, _) =>
        {
            if (inv.Args is ["version", ..])
            {
                return Task.FromResult(new DockerRunResult(0, "linux\n", string.Empty, false, false, false));
            }

            if (inv.Args is ["info", ..])
            {
                return Task.FromResult(new DockerRunResult(0, "27.0.0", string.Empty, false, false, false));
            }

            if (inv.Args is ["image", "inspect", ..])
            {
                return Task.FromResult(new DockerRunResult(0, "sha256:testimage123", string.Empty, false, false, false));
            }

            if (inv.Args.Count > 0 && inv.Args[0] == "create")
            {
                return Task.FromResult(new DockerRunResult(0, "cid-r5-test\n", string.Empty, false, false, false));
            }

            if (inv.Args is ["start", "cid-r5-test"])
            {
                return Task.FromResult(new DockerRunResult(0, "cid-r5-test\n", string.Empty, false, false, false));
            }

            if (inv.Args is ["exec", "-i", "cid-r5-test", "tar", "-x", "-C", "/", "-f", "-"])
            {
                // First tar load is workspace snapshot.tar; right after it finishes (just as input0.tar is about to be built!),
                // tamper helper.py so the packaged archive differs from the approved manifest!
                if (File.ReadAllText(helperPath) == "VALUE = 1")
                {
                    File.WriteAllText(helperPath, "VALUE = 999_EVIL");
                    return Task.FromResult(new DockerRunResult(0, string.Empty, string.Empty, false, false, false));
                }

                inputTarLoadedIntoContainer = true;
                return Task.FromResult(new DockerRunResult(0, string.Empty, string.Empty, false, false, false));
            }

            if (inv.Args.Count >= 2 && inv.Args[0] == "rm")
            {
                var cid = inv.Args[^1];
                removedContainers.Add(cid);
                // Even if attacker restores helper.py afterward, OpenSessionAsync has ALREADY caught the tampered archive!
                File.WriteAllText(helperPath, "VALUE = 1");
                return Task.FromResult(new DockerRunResult(0, cid, string.Empty, false, false, false));
            }

            return Task.FromResult(new DockerRunResult(0, string.Empty, string.Empty, false, false, false));
        });

        using var dockerService = new DockerIsolatedExecutionService(
            scriptedDocker,
            Options.Create(new IsolationOptions { StagingRoot = stg, PullMissingImages = false }),
            NullLogger<DockerIsolatedExecutionService>.Instance);

        var openTampered = await dockerService.OpenSessionAsync(
            IsolationTestSupport.Context(ws),
            new IsolatedSessionOptions(
                "python:3.12-alpine",
                Inputs:
                [
                    new IsolatedInputDirectory(
                        prestageSkill,
                        "skill-inputs/my-skill",
                        ExpectedManifestSha256: approvedManifestSha,
                        ExpectedEntryRelativePath: "run.py",
                        ExpectedEntrySha256: approvedScriptSha)
                ]),
            CancellationToken.None);

        Assert.False(openTampered.Decision.IsAllowed);
        Assert.Equal(ExecutionReasonCodes.ApprovalInvalidated, openTampered.Decision.ReasonCode);
        Assert.False(inputTarLoadedIntoContainer);
        Assert.Contains("cid-r5-test", removedContainers);

        // Also verify clean Docker input archive holds an uninterrupted FileShare.None lock during streaming into `docker exec tar -x`!
        var exclusiveLockVerifiedDuringLoad = false;
        var cleanDocker = new ScriptedDockerCli((inv, _) =>
        {
            if (inv.Args is ["version", ..])
                return Task.FromResult(new DockerRunResult(0, "linux\n", string.Empty, false, false, false));
            if (inv.Args is ["image", "inspect", ..])
                return Task.FromResult(new DockerRunResult(0, "sha256:testimage123", string.Empty, false, false, false));
            if (inv.Args.Count > 0 && inv.Args[0] == "create")
                return Task.FromResult(new DockerRunResult(0, "cid-r5-clean\n", string.Empty, false, false, false));
            if (inv.Args is ["start", "cid-r5-clean"])
                return Task.FromResult(new DockerRunResult(0, "cid-r5-clean\n", string.Empty, false, false, false));
            if (inv.Args is ["exec", "-i", "cid-r5-clean", "tar", "-x", "-C", "/", "-f", "-"] &&
                inv.StdIn is FileStream fs &&
                fs.Name.EndsWith("input0.tar", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Throws<IOException>(() =>
                {
                    using var secondHandle = new FileStream(fs.Name, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                });
                exclusiveLockVerifiedDuringLoad = true;
            }

            return Task.FromResult(new DockerRunResult(0, string.Empty, string.Empty, false, false, false));
        });

        using var cleanDockerSvc = new DockerIsolatedExecutionService(
            cleanDocker,
            Options.Create(new IsolationOptions { StagingRoot = stg, PullMissingImages = false }),
            NullLogger<DockerIsolatedExecutionService>.Instance);
        var openClean = await cleanDockerSvc.OpenSessionAsync(
            IsolationTestSupport.Context(ws),
            new IsolatedSessionOptions(
                "python:3.12-alpine",
                Inputs:
                [
                    new IsolatedInputDirectory(
                        prestageSkill,
                        "skill-inputs/my-skill",
                        ExpectedManifestSha256: approvedManifestSha,
                        ExpectedEntryRelativePath: "run.py",
                        ExpectedEntrySha256: approvedScriptSha)
                ]),
            CancellationToken.None);
        Assert.True(openClean.Decision.IsAllowed, openClean.Decision.Message);
        Assert.True(exclusiveLockVerifiedDuringLoad);
        await openClean.Session!.DisposeAsync();

        // --- Part 2 (R6): AutoInWorkspace NEVER auto-allows rooted paths to real host workspace ---
        var trustedDir = Path.Combine(_root, "r6-trusted");
        Directory.CreateDirectory(trustedDir);
        var exeSuffix = OperatingSystem.IsWindows() ? ".exe" : "";
        var trustedRg = Path.Combine(trustedDir, "rg" + exeSuffix);
        var trustedGit = Path.Combine(trustedDir, "git" + exeSuffix);
        File.WriteAllText(trustedRg, "rg");
        File.WriteAllText(trustedGit, "git");

        // Passing empty stagingWorkspaceDir (pre-session ToolIsolationGate state) OR passing real workspaceRoot:
        // rooted path to real host workspace MUST be rejected by AutoInWorkspaceEvaluator!
        var rgHostRootAttempt1 = AutoInWorkspaceEvaluator.Evaluate(
            "exec_cmd",
            trustedRg,
            ["--hidden", "--no-ignore", "secret", ws],
            ws,
            stg,
            stagingWorkspaceDir: string.Empty,
            guestGitDir: null,
            sessionTempDir: Path.Combine(stg, "tmp"),
            [trustedDir]);
        Assert.False(rgHostRootAttempt1.AutoAllowed);

        var rgHostRootAttempt2 = AutoInWorkspaceEvaluator.Evaluate(
            "exec_cmd",
            trustedRg,
            ["--hidden", "--no-ignore", "secret", ws],
            ws,
            stg,
            stagingWorkspaceDir: ws,
            guestGitDir: null,
            sessionTempDir: Path.Combine(stg, "tmp"),
            [trustedDir]);
        Assert.False(rgHostRootAttempt2.AutoAllowed);

        var gitHostRootAttempt = AutoInWorkspaceEvaluator.Evaluate(
            "exec_cmd",
            trustedGit,
            ["status", "--", ws],
            ws,
            stg,
            stagingWorkspaceDir: string.Empty,
            guestGitDir: null,
            sessionTempDir: Path.Combine(stg, "tmp"),
            [trustedDir]);
        Assert.False(gitHostRootAttempt.AutoAllowed);

        // Round 3 B4: ALL long and short git pathspec magic (`:(top)*`, `:/foo`, `:!foo`, `:^foo`) MUST be rejected BOTH before and after `--`!
        foreach (var magicArgs in new string[][]
                 {
                     ["status", ":(top)*"],
                     ["status", "--", ":(top)*"],
                     ["diff", ":(icase)README.md"],
                     ["diff", "--", ":/secret"],
                     ["status", ":!ignored"],
                     ["status", "--", ":^excluded"],
                     ["log", ".."],
                     ["log", "main....HEAD"],
                     ["log", ".HEAD..main"],
                     ["log", "--", "../escape"]
                 })
        {
            var magicEval = AutoInWorkspaceEvaluator.Evaluate(
                "exec_cmd",
                trustedGit,
                magicArgs,
                ws,
                stg,
                stagingWorkspaceDir: string.Empty,
                guestGitDir: null,
                sessionTempDir: Path.Combine(stg, "tmp"),
                [trustedDir]);
            Assert.False(magicEval.AutoAllowed);
        }

        // Genuine revision range `main..HEAD` before `--` remains allowed
        var validRangeEval = AutoInWorkspaceEvaluator.Evaluate(
            "exec_cmd",
            trustedGit,
            ["log", "main..HEAD"],
            ws,
            stg,
            stagingWorkspaceDir: string.Empty,
            guestGitDir: null,
            sessionTempDir: Path.Combine(stg, "tmp"),
            [trustedDir]);
        Assert.True(validRangeEval.AutoAllowed);

        // Round 3 Advisory 4: Oversized single line in ToolOutputEventThrottler is truncated strictly within maxChunkChars,
        // AND ControlPlaneAuthConstants.IsControlPlanePrincipal enforces full principal matrix!
        var lineGateway = new RecordingGateway(hasSubscribers: true);
        await using (var lineThrottler = new ToolOutputEventThrottler(
                         lineGateway,
                         "sess-huge-line",
                         "exec_cmd",
                         "t-huge",
                         minFlushInterval: TimeSpan.FromSeconds(5),
                         maxEventsPerExecution: 5,
                         maxChunkChars: 512))
        {
            lineThrottler.Append("stdout", new string('X', 20_000));
            await lineThrottler.FlushAsync();
        }

        var eventPayloads = lineGateway.Events.Select(static e => System.Text.Json.JsonSerializer.SerializeToElement(e.Event.Payload)).ToArray();
        Assert.NotEmpty(eventPayloads);
        foreach (var ep in eventPayloads)
        {
            if (ep.TryGetProperty("stdout", out var stdoutProp) && stdoutProp.GetString() is { } chunkText)
            {
                Assert.True(chunkText.Length <= 512, $"Expected each stdout chunk <= 512 chars, got {chunkText.Length}");
            }
        }

        var hugeJson = System.Text.Json.JsonSerializer.Serialize(eventPayloads);
        Assert.Contains("...[line truncated]", hugeJson, StringComparison.Ordinal);

        // Full Principal matrix check (11 cases covering local, remote roles, remote plane claims, disabled provider, and claim/role conflict)
        var unauthPrincipal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());
        var spoofedBlankAuth = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "admin")], authenticationType: ""));
        var localTokenMissingPlane = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "u1")], "LocalToken"));
        var localNormalPlane = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim(ControlPlaneAuthConstants.PlaneClaimType, ControlPlaneAuthConstants.PlaneNormal)], "LocalToken"));
        var localControlPlane = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim(ControlPlaneAuthConstants.PlaneClaimType, ControlPlaneAuthConstants.PlaneControl)], "LocalToken"));
        var remoteOrdinaryUser = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "user-42")], "Bearer"));
        var remoteAdminUser = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [
                    new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "admin-1"),
                    new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Admin")
                ],
                "Bearer"));
        var remoteControlRoleUser = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [
                    new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "ops-1"),
                    new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, ControlPlaneAuthConstants.ControlPlanePolicy)
                ],
                "Bearer"));
        var remoteExplicitControlClaim = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [
                    new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "svc-1"),
                    new System.Security.Claims.Claim(ControlPlaneAuthConstants.PlaneClaimType, ControlPlaneAuthConstants.PlaneControl)
                ],
                "Bearer"));
        var conflictingNormalClaimWithAdminRole = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [
                    new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "admin-conflict"),
                    new System.Security.Claims.Claim(ControlPlaneAuthConstants.PlaneClaimType, ControlPlaneAuthConstants.PlaneNormal),
                    new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Admin")
                ],
                "Bearer"));
        var unavailableStateProvider = new Moq.Mock<IControlPlaneStateProvider>();
        unavailableStateProvider.SetupGet(x => x.IsControlPlaneAvailable).Returns(false);

        Assert.False(ControlPlaneAuthConstants.IsControlPlanePrincipal(unauthPrincipal));
        Assert.False(ControlPlaneAuthConstants.IsControlPlanePrincipal(spoofedBlankAuth));
        Assert.False(ControlPlaneAuthConstants.IsControlPlanePrincipal(localTokenMissingPlane));
        Assert.False(ControlPlaneAuthConstants.IsControlPlanePrincipal(localNormalPlane));
        Assert.True(ControlPlaneAuthConstants.IsControlPlanePrincipal(localControlPlane));
        Assert.False(ControlPlaneAuthConstants.IsControlPlanePrincipal(localControlPlane, unavailableStateProvider.Object));
        Assert.False(ControlPlaneAuthConstants.IsControlPlanePrincipal(remoteOrdinaryUser));
        Assert.True(ControlPlaneAuthConstants.IsControlPlanePrincipal(remoteAdminUser));
        Assert.True(ControlPlaneAuthConstants.IsControlPlanePrincipal(remoteControlRoleUser));
        Assert.True(ControlPlaneAuthConstants.IsControlPlanePrincipal(remoteExplicitControlClaim));
        Assert.False(ControlPlaneAuthConstants.IsControlPlanePrincipal(conflictingNormalClaimWithAdminRole));
    }

    private static async IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> StreamFakeModelRoundAsync(
        int roundNumber,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(10, ct);
        if (roundNumber == 1)
        {
            yield return new Microsoft.Extensions.AI.ChatResponseUpdate(
                Microsoft.Extensions.AI.ChatRole.Assistant,
                [new Microsoft.Extensions.AI.FunctionCallContent("call-1", "exec_code", new Dictionary<string, object?> { ["code"] = "print(7)", ["language"] = "python" })]);
        }
        else
        {
            yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, "done-after-approval");
        }
    }

    private static async Task<PendingApprovalItem> WaitSinglePendingAsync(
        IExecutionApprovalBroker broker,
        string sessionId,
        string? excludeApprovalId = null)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var items = broker.GetPendingApprovals(sessionId)
                .Where(i => excludeApprovalId is null || !string.Equals(i.ApprovalId, excludeApprovalId, StringComparison.Ordinal))
                .ToArray();
            if (items.Length > 0)
            {
                return items[0];
            }

            await Task.Delay(15);
        }

        throw new TimeoutException($"Timed out waiting for pending approval in session '{sessionId}'.");
    }

    private ApprovalTestHarness CreateHarness(ExecutionOptions initialOptions)
    {
        var ws = Path.Combine(_root, "ws-" + Guid.NewGuid().ToString("N"));
        var stg = Path.Combine(_root, "stg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ws);
        Directory.CreateDirectory(stg);

        var monitor = new MutableOptionsMonitor<ExecutionOptions>(initialOptions);
        var permStore = new ExecutionPermissionStore(Path.Combine(_root, "perms-" + Guid.NewGuid().ToString("N") + ".json"));
        var fakeGateway = new RecordingGateway(hasSubscribers: true);
        var auditStore = new CodexFlow.Core.Services.InMemoryWorkflowAuditStore();
        var broker = new ExecutionApprovalBroker(
            monitor,
            permStore,
            NullLogger<ExecutionApprovalBroker>.Instance,
            gateway: fakeGateway,
            auditStore: auditStore);

        var harness = new ApprovalTestHarness(ws, stg, monitor, permStore, broker, fakeGateway, auditStore);
        return harness;
    }

    private sealed class ApprovalTestHarness
    {
        private readonly LocalExecutionService _localService;
        private readonly FakeDockerService _dockerService;
        private int _processCalls;
        private int _sessionOpens;
        private int _sessionRuns;

        public ApprovalTestHarness(
            string workspaceDir,
            string stagingRoot,
            MutableOptionsMonitor<ExecutionOptions> optionsMonitor,
            ExecutionPermissionStore permissionStore,
            ExecutionApprovalBroker broker,
            RecordingGateway gateway,
            IWorkflowAuditStore auditStore)
        {
            WorkspaceDir = workspaceDir;
            StagingRoot = stagingRoot;
            OptionsMonitor = optionsMonitor;
            PermissionStore = permissionStore;
            Broker = broker;
            Gateway = gateway;
            AuditStore = auditStore;

            var isoOptions = new IsolationOptions { StagingRoot = stagingRoot };
            _localService = new LocalExecutionService(
                Options.Create(isoOptions),
                NullLogger<LocalExecutionService>.Instance,
                processRunner: (inv, _) =>
                {
                    Interlocked.Increment(ref _processCalls);
                    if (CustomLocalProcessHandler is not null)
                    {
                        return Task.FromResult(CustomLocalProcessHandler(inv));
                    }

                    return Task.FromResult(new HostProcessResult(true, 0, "local-ok", string.Empty, false, false, false, null));
                });

            _dockerService = new FakeDockerService(
                onOpen: () => Interlocked.Increment(ref _sessionOpens),
                onRun: () => Interlocked.Increment(ref _sessionRuns));

            var compositeService = new SwitchingTestExecutionService(optionsMonitor, _dockerService, _localService, () => Interlocked.Increment(ref _sessionOpens));
            Gate = new ToolIsolationGate(compositeService, Accessor, isoOptions, broker, optionsMonitor);
        }

        public string WorkspaceDir { get; }
        public string StagingRoot { get; }
        public MutableOptionsMonitor<ExecutionOptions> OptionsMonitor { get; }
        public ExecutionPermissionStore PermissionStore { get; }
        public ExecutionApprovalBroker Broker { get; }
        public RecordingGateway Gateway { get; }
        public IWorkflowAuditStore AuditStore { get; }
        public TrustedExecutionContextAccessor Accessor { get; } = new();
        public ToolIsolationGate Gate { get; }
        public Func<HostProcessInvocation, HostProcessResult>? CustomLocalProcessHandler { get; set; }

        public int ProcessRunnerCallCount => Volatile.Read(ref _processCalls);
        public int SessionOpenCount => Volatile.Read(ref _sessionOpens);
        public int SessionRunCount => Volatile.Read(ref _sessionRuns);

        public TrustedExecutionContext CreateContext(
            string sessionId = "s1",
            ExecutionNetworkMode networkMode = ExecutionNetworkMode.None)
            => new()
            {
                UserId = "u1",
                SessionId = sessionId,
                JobId = "j1",
                TaskId = "t1",
                AttemptId = "a1",
                WorkspaceRoot = WorkspaceDir,
                Network = new ExecutionNetworkPolicy(networkMode)
            };
    }

    private sealed class SwitchingTestExecutionService(
        IOptionsMonitor<ExecutionOptions> options,
        IIsolatedExecutionService docker,
        IIsolatedExecutionService local,
        Action onOpen) : IIsolatedExecutionService
    {
        public ExecutionBackend ActiveBackend => options.CurrentValue.Backend;

        public bool SupportsNetworkMode(ExecutionNetworkMode mode)
            => ActiveBackend == ExecutionBackend.Local ? local.SupportsNetworkMode(mode) : docker.SupportsNetworkMode(mode);

        public Task<IsolatedSessionOpenResult> OpenSessionAsync(
            TrustedExecutionContext context,
            IsolatedSessionOptions sessionOptions,
            CancellationToken ct)
        {
            onOpen();
            return ActiveBackend == ExecutionBackend.Local
                ? local.OpenSessionAsync(context, sessionOptions, ct)
                : docker.OpenSessionAsync(context, sessionOptions, ct);
        }
    }

    private sealed class FakeDockerService(Action onOpen, Action onRun) : IIsolatedExecutionService
    {
        public ExecutionBackend ActiveBackend => ExecutionBackend.Docker;

        public Task<IsolatedSessionOpenResult> OpenSessionAsync(
            TrustedExecutionContext context,
            IsolatedSessionOptions options,
            CancellationToken ct)
        {
            _ = onOpen;
            return Task.FromResult(new IsolatedSessionOpenResult(ExecutionDecision.Allow(), new FakeSession(onRun)));
        }

        private sealed class FakeSession(Action onRun) : IIsolatedExecutionSession
        {
            public string SessionHandle => "docker-fake";

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
                    TimeSpan.FromMilliseconds(5),
                    "fake-docker"));
            }

            public Task<IsolatedExportResult> ExportScopedChangesAsync(CancellationToken ct)
                => Task.FromResult(new IsolatedExportResult(ExecutionDecision.Allow(), [], []));

            public Task<IReadOnlyList<EvidenceFile>> ReadEvidenceAsync(IReadOnlyList<string> globs, CancellationToken ct)
                => Task.FromResult<IReadOnlyList<EvidenceFile>>([]);

            public Task AbortAsync() => Task.CompletedTask;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    internal sealed class RecordingGateway(bool hasSubscribers) : ICodexGateway
    {
        public ConcurrentQueue<(string SessionId, GatewaySseEvent Event)> Events { get; } = new();
        public bool HasSubscribers { get; set; } = hasSubscribers;
        public Action<string, GatewaySseEvent>? OnPublish { get; set; }

        public ValueTask<string> EnqueueAsync(GatewayMessage message, CancellationToken ct = default)
            => ValueTask.FromResult(message.MessageId);

        public async IAsyncEnumerable<GatewaySseEvent> SubscribeAsync(
            string sessionId,
            string userId,
            long lastEventId = 0,
            [EnumeratorCancellation] CancellationToken ct = default,
            string? channelEpoch = null)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask PublishEventAsync(string sessionId, GatewaySseEvent sseEvent, CancellationToken ct = default)
        {
            Events.Enqueue((sessionId, sseEvent));
            OnPublish?.Invoke(sessionId, sseEvent);
            return ValueTask.CompletedTask;
        }

        public bool IsSessionActive(string sessionId) => false;
        public bool HasActiveSubscribers(string sessionId) => HasSubscribers;
        public ValueTask CloseSessionAsync(string sessionId) => ValueTask.CompletedTask;
        public Task<bool> CancelTurnAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(false);
        public GatewayChannelSnapshotState? GetChannelSnapshotState(string sessionId) => null;
    }

    internal sealed class MutableOptionsMonitor<T>(T initial) : IOptionsMonitor<T>
    {
        private T _current = initial;
        private event Action<T, string?>? Changed;

        public T CurrentValue => _current;
        public T Get(string? name) => _current;

        public void Set(T next)
        {
            _current = next;
            Changed?.Invoke(next, Options.DefaultName);
        }

        public IDisposable? OnChange(Action<T, string?> listener)
        {
            Changed += listener;
            return new Unsub(() => Changed -= listener);
        }

        private sealed class Unsub(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }

    private sealed class ScriptedDockerCli(Func<DockerInvocation, CancellationToken, Task<DockerRunResult>> handler) : IDockerCli
    {
        public Task<DockerRunResult> RunAsync(DockerInvocation invocation, CancellationToken ct)
            => handler(invocation, ct);
    }
}
