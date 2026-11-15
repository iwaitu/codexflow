// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CodexFlow.Core.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CodexFlow.Core.Tests.Execution;

/// <summary>
/// Real-Docker attack suite. These tests FAIL (they are not skipped) when Docker Linux containers are unavailable:
/// isolation that cannot be demonstrated must not be claimed. Images: alpine/python (pulled if missing) and the
/// local verification image for the .NET build scenarios.
/// </summary>
[Trait("Category", "DockerIsolation")]
[Collection(SafePublishCollection.Name)]
public class DockerIsolationAttackTests : IAsyncLifetime
{
    private const string Alpine = "alpine:3.20";
    private const string PythonImage = "python:3.12-alpine";
    private const string VerificationImage = "codexflow-verification:remediation-20261107";

    private readonly string _root = IsolationTestSupport.NewTempDir("ws");
    private readonly string _outside = IsolationTestSupport.NewTempDir("outside");
    private readonly string _staging = IsolationTestSupport.NewTempDir("staging");
    private readonly List<IIsolatedExecutionSession> _sessions = [];
    private const string SentinelText = "UNIQUE-HOST-SENTINEL-7f3a9c";

    public ValueTask InitializeAsync()
    {
        File.WriteAllText(Path.Combine(_outside, "sentinel.txt"), SentinelText);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "A");
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "keep.txt"), "keep");
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _sessions)
        {
            await s.DisposeAsync();
        }

        IsolationTestSupport.Cleanup(_root);
        IsolationTestSupport.Cleanup(_outside);
        IsolationTestSupport.Cleanup(_staging);
    }

    private DockerIsolatedExecutionService NewService(IDockerCli? cli = null)
        => new(cli ?? new ProcessDockerCli(), Options.Create(new IsolationOptions { PullMissingImages = true, StagingRoot = _staging }), NullLogger<DockerIsolatedExecutionService>.Instance);

    private async Task<IIsolatedExecutionSession> OpenAsync(TrustedExecutionContext ctx, string image = Alpine, IsolatedSessionOptions? opts = null, DockerIsolatedExecutionService? svc = null)
    {
        var open = await (svc ?? NewService()).OpenSessionAsync(ctx, opts ?? new IsolatedSessionOptions(image), CancellationToken.None);
        Assert.True(open.Decision.IsAllowed, $"{open.Decision.ReasonCode}: {open.Decision.Message}");
        _sessions.Add(open.Session!);
        return open.Session!;
    }

    private static IsolatedCommandRequest Sh(string script, TimeSpan? timeout = null) => new(["sh", "-c", script], Timeout: timeout);

    private void AssertSentinelIntact()
        => Assert.Equal(SentinelText, File.ReadAllText(Path.Combine(_outside, "sentinel.txt")));

    private static string DockerOut(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("docker", args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return o.Trim();
    }

    [Fact]
    public async Task CleanCommand_ShouldRunAndExportInScopeChangeWithHostComputedHash()
    {
        var ctx = IsolationTestSupport.Context(_root, ExportScope.From(["src/out.txt", "a.txt"]));
        var s = await OpenAsync(ctx);
        var run = await s.RunAsync(Sh("echo hello > src/out.txt && echo A2 > a.txt && cat a.txt"), CancellationToken.None);
        Assert.Equal(IsolatedCommandStatus.Completed, run.Status);
        Assert.Equal(0, run.ExitCode);
        Assert.Contains("A2", run.Stdout, StringComparison.Ordinal);

        var export = await s.ExportScopedChangesAsync(CancellationToken.None);
        Assert.True(export.Decision.IsAllowed, export.Decision.Message);
        Assert.Equal(2, export.Applied.Count);
        Assert.Equal("hello\n", File.ReadAllText(Path.Combine(_root, "src", "out.txt")).Replace("\r\n", "\n"));
        Assert.Equal(IsolationTestSupport.Sha(File.ReadAllBytes(Path.Combine(_root, "a.txt"))), export.Applied.Single(a => a.Path == "a.txt").Sha256);
        Assert.Empty(Directory.EnumerateFiles(_root, ".cfx-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task HostSentinel_ShouldNeverBeReachableOrModified_ByAnyPathTrick()
    {
        var hostPath = Path.Combine(_outside, "sentinel.txt").Replace('\\', '/');
        var ctx = IsolationTestSupport.Context(_root, ExportScope.Empty);
        var s = await OpenAsync(ctx);
        var attack = $"echo pwn > ../sentinel.txt; echo pwn > ../../sentinel.txt; echo pwn > '{hostPath}'; echo pwn > '/mnt/c{hostPath[2..]}'; " +
                     $"echo pwn > /host-sentinel; grep -rl {SentinelText} /workspace /tmp /home /root /mnt /media /opt /srv 2>/dev/null | head -5; echo done";
        var run = await s.RunAsync(Sh(attack), CancellationToken.None);
        Assert.Equal(IsolatedCommandStatus.Completed, run.Status);
        Assert.DoesNotContain(SentinelText, run.Stdout, StringComparison.Ordinal);
        AssertSentinelIntact();

        var export = await s.ExportScopedChangesAsync(CancellationToken.None);
        Assert.True(export.Decision.IsAllowed, export.Decision.Message);   // nothing in the workspace changed
        Assert.Empty(export.Applied);
        AssertSentinelIntact();
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "sentinel.txt")));
    }

    [Fact]
    public async Task OutOfScopeWrite_ShouldRejectWholeExport_AndApplyNothing()
    {
        var ctx = IsolationTestSupport.Context(_root, ExportScope.From(["a.txt"]));
        var s = await OpenAsync(ctx);
        await s.RunAsync(Sh("echo A-GUEST > a.txt; echo evil > evil.cs; echo evil > src/keep.txt"), CancellationToken.None);
        var export = await s.ExportScopedChangesAsync(CancellationToken.None);

        Assert.False(export.Decision.IsAllowed);
        Assert.Equal(ExecutionReasonCodes.ExportRejectedOutOfScope, export.Decision.ReasonCode);
        Assert.Equal(2, export.Rejected.Count);
        Assert.Equal("A", File.ReadAllText(Path.Combine(_root, "a.txt")));       // in-scope change NOT applied either
        Assert.False(File.Exists(Path.Combine(_root, "evil.cs")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(_root, "src", "keep.txt")));
    }

    [Fact]
    public async Task SymlinkGitDirAndSpecialFiles_ShouldBeRejected_EvenWhenNamedInScope()
    {
        var ctx = IsolationTestSupport.Context(_root, ExportScope.From(["lnk.txt", "p", ".git/config"], [".git"]));
        var s = await OpenAsync(ctx);
        await s.RunAsync(Sh("ln -s /etc/passwd lnk.txt; mkfifo p; mkdir .git && echo '[core]' > .git/config"), CancellationToken.None);
        var export = await s.ExportScopedChangesAsync(CancellationToken.None);

        Assert.False(export.Decision.IsAllowed);
        Assert.Contains(export.Rejected, r => r.ReasonCode == ExecutionReasonCodes.ExportRejectedLink);
        Assert.Contains(export.Rejected, r => r.ReasonCode == ExecutionReasonCodes.ExportRejectedSpecialFile);
        Assert.Contains(export.Rejected, r => r.ReasonCode == ExecutionReasonCodes.ExportRejectedProtectedPath);
        Assert.False(File.Exists(Path.Combine(_root, "lnk.txt")));
        Assert.False(Directory.Exists(Path.Combine(_root, ".git")));
    }

    [Fact]
    public async Task ReplacingTrackedFileWithSymlink_ShouldBeRejected_NotAppliedAsDelete()
    {
        var ctx = IsolationTestSupport.Context(_root, ExportScope.From(["a.txt"]));
        var s = await OpenAsync(ctx);
        await s.RunAsync(Sh("rm a.txt && ln -s /etc/shadow a.txt"), CancellationToken.None);
        var export = await s.ExportScopedChangesAsync(CancellationToken.None);
        Assert.False(export.Decision.IsAllowed);
        Assert.Equal("A", File.ReadAllText(Path.Combine(_root, "a.txt")));
    }

    [Fact]
    public async Task Network_ShouldBeUnreachable_IncludingHostServices()
    {
        var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var s = await OpenAsync(IsolationTestSupport.Context(_root));
            var run = await s.RunAsync(Sh(
                $"echo IFACES:$(ls /sys/class/net | tr '\\n' ' '); " +
                $"nc -z -w 2 host.docker.internal {port} && echo HOST-REACHED || echo host-blocked; " +
                $"nc -z -w 2 172.17.0.1 {port} && echo BRIDGE-REACHED || echo bridge-blocked; " +
                "wget -T 2 -qO- http://1.1.1.1 >/dev/null 2>&1 && echo INTERNET-REACHED || echo internet-blocked"), CancellationToken.None);

            Assert.Contains("IFACES:lo", run.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("REACHED", run.Stdout, StringComparison.Ordinal);
            Assert.Contains("host-blocked", run.Stdout, StringComparison.Ordinal);
            Assert.Contains("internet-blocked", run.Stdout, StringComparison.Ordinal);
            Assert.False(listener.Pending(), "the guest must not have connected to the host listener");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Guest_ShouldHaveNoHostCredentialsSocketGitPointerOrPrivileges()
    {
        File.WriteAllText(Path.Combine(_root, ".git"), "gitdir: C:/Users/host/secret/.git/worktrees/w1");
        Environment.SetEnvironmentVariable("CF_ISO_TEST_SECRET", "SECRET-VALUE-123");
        try
        {
            var s = await OpenAsync(IsolationTestSupport.Context(_root));
            var run = await s.RunAsync(Sh(
                "id -u; env; ls -a /workspace; ls /var/run/docker.sock /run/docker.sock 2>&1; grep CapEff /proc/self/status; grep NoNewPrivs /proc/self/status; mount | grep -c -E 'docker.sock|host_mnt|/mnt/'"), CancellationToken.None);
            Assert.StartsWith("10001", run.Stdout.TrimStart(), StringComparison.Ordinal);
            Assert.DoesNotContain("SECRET-VALUE-123", run.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("CF_ISO_TEST_SECRET", run.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("USERPROFILE", run.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain(".git", run.Stdout.Split('\n').Where(l => l.Trim() == ".git").ToArray());
            Assert.Contains("No such file", run.Stdout, StringComparison.Ordinal);
            Assert.Contains("CapEff:\t0000000000000000", run.Stdout, StringComparison.Ordinal);
            Assert.Contains("NoNewPrivs:\t1", run.Stdout, StringComparison.Ordinal);
            Assert.EndsWith("0", run.Stdout.TrimEnd(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CF_ISO_TEST_SECRET", null);
        }
    }

    [Fact]
    public async Task GeneratedBinObj_ShouldStayPrivate_AndNotBeExportedAsSource()
    {
        var ctx = IsolationTestSupport.Context(_root, ExportScope.Empty);
        var s = await OpenAsync(ctx);
        await s.RunAsync(Sh("mkdir -p bin/Debug obj src/obj && echo x > bin/Debug/app.dll && echo y > obj/project.assets.json && echo z > src/obj/z.cache"), CancellationToken.None);
        var export = await s.ExportScopedChangesAsync(CancellationToken.None);
        Assert.True(export.Decision.IsAllowed, export.Decision.Message);
        Assert.Empty(export.Applied);
        Assert.False(Directory.Exists(Path.Combine(_root, "bin")));
        Assert.False(Directory.Exists(Path.Combine(_root, "obj")));
    }

    [Fact]
    public async Task HostEditDuringRun_ShouldWinOverGuestChange_WithConcurrentModificationRejection()
    {
        var ctx = IsolationTestSupport.Context(_root, ExportScope.From(["a.txt"]));
        var s = await OpenAsync(ctx);
        await s.RunAsync(Sh("echo GUEST > a.txt"), CancellationToken.None);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "HOST-CONCURRENT-EDIT");

        var export = await s.ExportScopedChangesAsync(CancellationToken.None);
        Assert.False(export.Decision.IsAllowed);
        Assert.Equal(ExecutionReasonCodes.ExportRejectedConcurrentModification, export.Decision.ReasonCode);
        Assert.Equal("HOST-CONCURRENT-EDIT", File.ReadAllText(Path.Combine(_root, "a.txt")));
    }

    [Fact]
    public async Task Cancel_ShouldKillContainerPromptly_RefuseExport_AndLeaveNoResources()
    {
        var s = await OpenAsync(IsolationTestSupport.Context(_root, ExportScope.From(["a.txt"])));
        var handle = s.SessionHandle;
        using var cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();
        var running = s.RunAsync(Sh("echo GUEST > a.txt; sleep 600"), cts.Token);
        await Task.Delay(TimeSpan.FromSeconds(4));
        await cts.CancelAsync();
        var run = await running;

        Assert.Equal(IsolatedCommandStatus.Canceled, run.Status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(40), $"cancel took {sw.Elapsed}");
        Assert.Equal("false", DockerOut($"inspect -f {{{{.State.Running}}}} cfiso-{handle}"));

        var next = await s.RunAsync(Sh("echo again"), CancellationToken.None);
        Assert.Equal(ExecutionReasonCodes.SessionTerminated, next.Decision.ReasonCode);
        var export = await s.ExportScopedChangesAsync(CancellationToken.None);
        Assert.Equal(ExecutionReasonCodes.SessionTerminated, export.Decision.ReasonCode);
        Assert.Equal("A", File.ReadAllText(Path.Combine(_root, "a.txt")));

        await s.DisposeAsync();
        Assert.Equal(string.Empty, DockerOut($"ps -a -q --filter label=codexflow.isolation.session={handle}"));
        Assert.False(Directory.Exists(Path.Combine(_staging, handle)));
    }

    [Fact]
    public async Task Timeout_ShouldKillAndReportTimedOut()
    {
        var s = await OpenAsync(IsolationTestSupport.Context(_root));
        var run = await s.RunAsync(Sh("sleep 600", TimeSpan.FromSeconds(3)), CancellationToken.None);
        Assert.Equal(IsolatedCommandStatus.TimedOut, run.Status);
        Assert.Equal(ExecutionReasonCodes.Timeout, run.Decision.ReasonCode);
        Assert.Equal("false", DockerOut($"inspect -f {{{{.State.Running}}}} cfiso-{s.SessionHandle}"));
    }

    [Fact]
    public async Task PidLimit_ShouldBoundForkBombs()
    {
        var limits = new ExecutionResourceLimits { Pids = 32, CommandTimeout = TimeSpan.FromMinutes(1) };
        var s = await OpenAsync(IsolationTestSupport.Context(_root, limits: limits), PythonImage);
        const string code = @"import os,time,signal
pids=[]
for i in range(400):
    try:
        pid=os.fork()
    except OSError:
        break
    if pid==0:
        time.sleep(20)
        os._exit(0)
    pids.append(pid)
print('FORKED', len(pids), flush=True)
for p in pids:
    os.kill(p, signal.SIGKILL)
";
        var run = await s.RunAsync(new IsolatedCommandRequest(["python3", "-c", code]), CancellationToken.None);
        var count = int.Parse(run.Stdout.Split("FORKED")[1].Trim().Split((char)10)[0].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(count, 1, 40);
    }

    [Fact]
    public async Task MemoryLimit_ShouldKillRunawayAllocation()
    {
        var limits = new ExecutionResourceLimits { MemoryBytes = 128L * 1024 * 1024, CommandTimeout = TimeSpan.FromMinutes(1) };
        var s = await OpenAsync(IsolationTestSupport.Context(_root, limits: limits), PythonImage);
        var run = await s.RunAsync(new IsolatedCommandRequest(["python3", "-c", "b = bytearray(600*1024*1024); print('ALLOCATED')"]), CancellationToken.None);
        Assert.DoesNotContain("ALLOCATED", run.Stdout, StringComparison.Ordinal);
        Assert.True(run.Status == IsolatedCommandStatus.ResourceExceeded || run.ExitCode is 137 or 1, $"status={run.Status} exit={run.ExitCode}");
    }

    [Fact]
    public async Task FileSizeLimit_ShouldStopHugeWrites()
    {
        var limits = new ExecutionResourceLimits { MaxWorkspaceBytes = 32L * 1024 * 1024, CommandTimeout = TimeSpan.FromMinutes(1) };
        var s = await OpenAsync(IsolationTestSupport.Context(_root, limits: limits));
        var run = await s.RunAsync(Sh("dd if=/dev/zero of=big.bin bs=1M count=200 2>&1; ls -l big.bin"), CancellationToken.None);
        var size = run.Stdout.Split('\n').Where(l => l.Contains("big.bin", StringComparison.Ordinal) && l.StartsWith('-')).Select(l => long.Parse(l.Split(' ', StringSplitOptions.RemoveEmptyEntries)[4], System.Globalization.CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max();
        Assert.True(size <= 32L * 1024 * 1024, $"file grew to {size}");
    }

    [Fact]
    public async Task PythonRedirectionsAndSubprocesses_ShouldNotTouchHostSentinel()
    {
        var hostPath = Path.Combine(_outside, "sentinel.txt");
        var s = await OpenAsync(IsolationTestSupport.Context(_root), PythonImage);
        var code = "import os, subprocess, pathlib\n" +
                   $"for p in [r'{hostPath}', '../sentinel.txt', '/c/' + r'{hostPath.Replace('\\', '/')}', '/mnt/host/x']:\n" +
                   "    try:\n        open(p, 'w').write('pwn')\n    except Exception as e:\n        print('blocked', type(e).__name__)\n" +
                   "subprocess.run(['sh','-c','echo pwn > ../../sentinel.txt'])\n";
        var run = await s.RunAsync(new IsolatedCommandRequest(["python3", "-"], StdIn: code), CancellationToken.None);
        Assert.Equal(IsolatedCommandStatus.Completed, run.Status);
        AssertSentinelIntact();
    }

    [Fact]
    public async Task PinnedSession_ShouldSeeCommittedContentNotWorktree_AndRefuseExport()
    {
        var repo = IsolationTestSupport.NewTempDir("pinrepo");
        try
        {
            void Git(string args)
            {
                var psi = new ProcessStartInfo("git", args) { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                psi.Environment["GIT_AUTHOR_NAME"] = "t"; psi.Environment["GIT_AUTHOR_EMAIL"] = "t@t";
                psi.Environment["GIT_COMMITTER_NAME"] = "t"; psi.Environment["GIT_COMMITTER_EMAIL"] = "t@t";
                using var p = Process.Start(psi)!;
                p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit();
                Assert.Equal(0, p.ExitCode);
            }

            Git("init -q");
            File.WriteAllText(Path.Combine(repo, "a.txt"), "COMMITTED");
            Git("add -A");
            Git("commit -q -m c1");
            string sha;
            using (var p = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD") { WorkingDirectory = repo, RedirectStandardOutput = true, UseShellExecute = false })!)
            {
                sha = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit();
            }

            File.WriteAllText(Path.Combine(repo, "a.txt"), "WORKTREE-RACE-EDIT");
            var ctx = IsolationTestSupport.Context(repo, ExportScope.From(["a.txt"]), baseCommit: sha);
            var s = await OpenAsync(ctx, opts: new IsolatedSessionOptions(Alpine, PinToBaseCommit: true));
            var run = await s.RunAsync(Sh("cat a.txt; ls -a | grep -c '^.git$'"), CancellationToken.None);
            Assert.Contains("COMMITTED", run.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("WORKTREE-RACE-EDIT", run.Stdout, StringComparison.Ordinal);
            var export = await s.ExportScopedChangesAsync(CancellationToken.None);
            Assert.False(export.Decision.IsAllowed);
            Assert.Equal("WORKTREE-RACE-EDIT", File.ReadAllText(Path.Combine(repo, "a.txt")));
        }
        finally
        {
            IsolationTestSupport.Cleanup(repo);
        }
    }

    [Fact]
    public async Task ReadEvidence_ShouldReturnTrxFromGuest_WithoutExportingSource()
    {
        var s = await OpenAsync(IsolationTestSupport.Context(_root, ExportScope.Empty));
        await s.RunAsync(Sh("mkdir -p TestResults && echo '<TestRun/>' > TestResults/r.trx && echo secret > TestResults/r.txt"), CancellationToken.None);
        var ev = await s.ReadEvidenceAsync(["**/*.trx"], CancellationToken.None);
        var f = Assert.Single(ev);
        Assert.Equal("TestResults/r.trx", f.RelativePath);
        Assert.Contains("TestRun", System.Text.Encoding.UTF8.GetString(f.Content.Span), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_root, "TestResults")));
    }

    [Fact]
    public async Task GuestStorage_ShouldBeByteAndInodeBounded_WithReadOnlyRoot_SoTheDockerHostDiskCannotBeFilled()
    {
        var limits = new ExecutionResourceLimits
        {
            GuestWorkspaceBytes = 8L * 1024 * 1024,
            GuestTmpBytes = 4L * 1024 * 1024,
            GuestMaxInodes = 300,
            CommandTimeout = TimeSpan.FromMinutes(2)
        };
        var s = await OpenAsync(IsolationTestSupport.Context(_root, limits: limits));
        var run = await s.RunAsync(Sh(
            "touch /etc/rootfs-write 2>&1; touch /usr/rootfs-write 2>&1; " +
            "i=0; while [ $i -lt 5000 ]; do ( : > /tmp/f$i ) 2>/dev/null || break; i=$((i+1)); done; echo TMPFILES:$i; " +
            "j=0; while [ $j -lt 5000 ]; do ( : > /workspace/g$j ) 2>/dev/null || break; j=$((j+1)); done; echo WSFILES:$j; " +
            "rm -f /workspace/g*; dd if=/dev/zero of=/workspace/big bs=1M count=400 2>&1 | tail -1; echo BIGSIZE:$(wc -c < /workspace/big)"), CancellationToken.None);

        Assert.Contains("Read-only file system", run.Stdout, StringComparison.Ordinal);
        var tmpFiles = int.Parse(run.Stdout.Split("TMPFILES:")[1].Split((char)10)[0], System.Globalization.CultureInfo.InvariantCulture);
        var wsFiles = int.Parse(run.Stdout.Split("WSFILES:")[1].Split((char)10)[0], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(tmpFiles, 1, 300);
        Assert.InRange(wsFiles, 1, 300);
        var big = long.Parse(run.Stdout.Split("BIGSIZE:")[1].Split((char)10)[0].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(big <= 80L * 1024 * 1024, $"workspace volume grew to {big} bytes");

        // Nothing landed in the container's writable layer (there is none to fill): SizeRw stays tiny.
        var sizeRw = long.Parse(DockerOut($"container inspect --size -f {{{{.SizeRw}}}} cfiso-{s.SessionHandle}"), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(sizeRw < 1024 * 1024, $"writable layer grew to {sizeRw} bytes");
    }

    [Fact]
    public async Task Reaper_ShouldRemoveOnlyContainersOfDeadOwners_NeverLiveOrForeignOnes()
    {
        var svcA = NewService();
        var svcB = NewService();
        var sessionA = await OpenAsync(IsolationTestSupport.Context(_root), svc: svcA);
        var sessionB = await OpenAsync(IsolationTestSupport.Context(_root), svc: svcB);

        // A foreign-host container and a legacy (ownerless) container must never be touched.
        var foreign = "cfiso-foreign-" + Guid.NewGuid().ToString("N")[..8];
        var legacy = "cfiso-legacy-" + Guid.NewGuid().ToString("N")[..8];
        DockerOut($"create --name {foreign} --label codexflow.isolation=1 --label codexflow.isolation.owner=deadbeefdeadbeefdeadbeefdeadbeef --label codexflow.isolation.host=SOME-OTHER-HOST --label codexflow.isolation.created=1 {Alpine} sleep 1");
        DockerOut($"create --name {legacy} --label codexflow.isolation=1 --label codexflow.isolation.created=1 {Alpine} sleep 1");
        try
        {
            Assert.Equal(0, await svcB.ReapOrphansAsync(CancellationToken.None));
            Assert.NotEqual(string.Empty, DockerOut($"ps -a -q --filter name=cfiso-{sessionA.SessionHandle}"));   // A is alive: untouched
            Assert.NotEqual(string.Empty, DockerOut($"ps -a -q --filter name=cfiso-{sessionB.SessionHandle}"));   // own live session: untouched

            svcA.Dispose();   // A "dies": its lease is released while its container is still around
            Assert.Equal(1, await svcB.ReapOrphansAsync(CancellationToken.None));
            Assert.Equal(string.Empty, DockerOut($"ps -a -q --filter name=cfiso-{sessionA.SessionHandle}"));
            Assert.NotEqual(string.Empty, DockerOut($"ps -a -q --filter name=cfiso-{sessionB.SessionHandle}"));
            Assert.NotEqual(string.Empty, DockerOut($"ps -a -q --filter name={foreign}"));
            Assert.NotEqual(string.Empty, DockerOut($"ps -a -q --filter name={legacy}"));
            Assert.False(Directory.Exists(Path.Combine(_staging, svcA.InstanceId)));
        }
        finally
        {
            DockerOut($"container rm -f {foreign}");
            DockerOut($"container rm -f {legacy}");
            svcB.Dispose();
        }
    }

    [Fact]
    public async Task Reaper_ShouldNeverJudgeContainersOfAnotherStagingStore()
    {
        // The live Gateway host and any other process use their OWN staging root. A lease that is not visible in the reaper's
        // store says nothing about liveness, so containers of other stores must survive every reaper pass.
        var otherStaging = IsolationTestSupport.NewTempDir("otherstore");
        var hostLike = new DockerIsolatedExecutionService(
            new ProcessDockerCli(),
            Options.Create(new IsolationOptions { PullMissingImages = true, StagingRoot = otherStaging }),
            NullLogger<DockerIsolatedExecutionService>.Instance);
        var mine = NewService();
        try
        {
            var session = await OpenAsync(IsolationTestSupport.Context(_root), svc: mine);
            Assert.NotEqual(mine.StoreId, hostLike.StoreId);

            Assert.Equal(0, await hostLike.ReapOrphansAsync(CancellationToken.None));
            Assert.Equal(0, await hostLike.ReapOrphansAsync(CancellationToken.None));
            Assert.NotEqual(string.Empty, DockerOut($"ps -q --filter name=cfiso-{session.SessionHandle}"));   // still running
            var run = await session.RunAsync(Sh("echo still-alive"), CancellationToken.None);
            Assert.Contains("still-alive", run.Stdout, StringComparison.Ordinal);
            Assert.True(Directory.Exists(Path.Combine(_staging, mine.InstanceId)));
        }
        finally
        {
            hostLike.Dispose();
            mine.Dispose();
            IsolationTestSupport.Cleanup(otherStaging);
        }
    }

    [Fact]
    public async Task GuestGit_PinnedSession_ShouldBeAnIndependentRepoMappedToTheHostCommit_WithoutHostPathsHooksOrCredentials()
    {
        var repo = IsolationTestSupport.NewTempDir("ggrepo");
        try
        {
            string Git(string args)
            {
                var psi = new ProcessStartInfo("git", args) { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                psi.Environment["GIT_AUTHOR_NAME"] = "t"; psi.Environment["GIT_AUTHOR_EMAIL"] = "t@t";
                psi.Environment["GIT_COMMITTER_NAME"] = "t"; psi.Environment["GIT_COMMITTER_EMAIL"] = "t@t";
                using var p = Process.Start(psi)!;
                var o = p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                Assert.Equal(0, p.ExitCode);
                return o.Trim();
            }

            Git("init -q");
            Git("config core.autocrlf false");
            // host-side hostile material: a hook, a credential-bearing remote and a filter attribute that must never reach the guest
            Directory.CreateDirectory(Path.Combine(repo, "src"));
            File.WriteAllText(Path.Combine(repo, "src", "a.cs"), @"class A{}
");
            File.WriteAllText(Path.Combine(repo, "run.sh"), @"echo hi
");
            File.WriteAllText(Path.Combine(repo, ".gitattributes"), @"* filter=pwn
");
            Git("add -A");
            Git("commit -q -m c1");
            Git("remote add origin https://user:SECRETTOKEN123@example.com/private.git");
            File.WriteAllText(Path.Combine(repo, ".git", "hooks", "post-commit"), @"#!/bin/sh
touch /tmp/host-hook-ran
");
            var sha = Git("rev-parse HEAD");

            var ctx = IsolationTestSupport.Context(repo, ExportScope.Empty, baseCommit: sha);
            var s = await OpenAsync(ctx, opts: new IsolatedSessionOptions(VerificationImage, PinToBaseCommit: true, GuestGit: GuestGitMode.Required));

            var info = s.GuestGit;
            Assert.NotNull(info);
            Assert.True(info!.TreeMatchesHostCommit, $"guest tree {info.BaselineTree} vs host {info.HostBaseTree}");
            Assert.Equal(sha, info.HostBaseCommit);
            Assert.NotEqual(sha, info.BaselineCommit);          // a NEW commit of the guest repo, not the host object
            Assert.Equal(64, info.ManifestSha256.Length);

            var run = await s.RunAsync(Sh(
                "git log --format=%s; echo STATUS:$(git status --porcelain | wc -l); echo REMOTES:$(git remote -v | wc -l); " +
                "echo HOOKS:$(ls /tmp/cfgit/hooks 2>/dev/null | wc -l); echo GITDIRS:$(ls -a /workspace | grep -c '^.git$'); " +
                "echo CFG:$(cat /tmp/cfgit/config); git config --show-origin -l | grep -c -i -E 'SECRETTOKEN|example.com|ggrepo' ; env | grep ^GIT_DIR; " +
                "echo X >> src/a.cs; git diff --name-only; ls /tmp/host-hook-ran 2>&1 | head -1"), CancellationToken.None);

            Assert.Contains("codexflow guest baseline", run.Stdout, StringComparison.Ordinal);
            Assert.Contains("STATUS:0", run.Stdout, StringComparison.Ordinal);
            Assert.Contains("REMOTES:0", run.Stdout, StringComparison.Ordinal);
            Assert.Contains("HOOKS:0", run.Stdout, StringComparison.Ordinal);
            Assert.Contains("GITDIRS:0", run.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("SECRETTOKEN123", run.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("example.com", run.Stdout, StringComparison.Ordinal);
            Assert.Contains("GIT_DIR=/tmp/cfgit", run.Stdout, StringComparison.Ordinal);
            Assert.Contains("src/a.cs", run.Stdout, StringComparison.Ordinal);               // guest git sees the guest change
            Assert.Contains("No such file", run.Stdout, StringComparison.Ordinal);            // the host hook never ran
            Assert.False(File.Exists("/tmp/host-hook-ran"));

            // callers cannot redirect git away from the guest repository
            var redirected = await s.RunAsync(new IsolatedCommandRequest(["git", "status"], Env: new Dictionary<string, string> { ["GIT_DIR"] = "/etc" }), CancellationToken.None);
            Assert.Equal(ExecutionReasonCodes.InvalidRequest, redirected.Decision.ReasonCode);
        }
        finally
        {
            IsolationTestSupport.Cleanup(repo);
        }
    }

    [Fact]
    public async Task GuestGit_WorktreeSession_ShouldRecordTheManifestMapping_AndNeverLeakGitIntoTheExport()
    {
        File.WriteAllText(Path.Combine(_root, ".git"), "gitdir: C:/Users/host/secret/.git/worktrees/w1");
        var ctx = IsolationTestSupport.Context(_root, ExportScope.From(["a.txt"]));
        var s = await OpenAsync(ctx, opts: new IsolatedSessionOptions(VerificationImage, GuestGit: GuestGitMode.Required));

        Assert.NotNull(s.GuestGit);
        Assert.Null(s.GuestGit!.HostBaseTree);
        Assert.False(s.GuestGit.TreeMatchesHostCommit);
        Assert.Equal(64, s.GuestGit.ManifestSha256.Length);

        var run = await s.RunAsync(Sh("echo A-NEW > a.txt; git add -A; git commit -q -m inside-guest; git log --format=%s | head -2; ls -a /workspace | grep -c '^.git$'"), CancellationToken.None);
        Assert.Contains("inside-guest", run.Stdout, StringComparison.Ordinal);

        var export = await s.ExportScopedChangesAsync(CancellationToken.None);
        Assert.True(export.Decision.IsAllowed, export.Decision.Message);       // no ".git" entry exists in /workspace to reject or export
        Assert.Equal("a.txt", Assert.Single(export.Applied).Path);
        Assert.Equal("A-NEW", File.ReadAllText(Path.Combine(_root, "a.txt")).Trim());
        Assert.Equal("gitdir: C:/Users/host/secret/.git/worktrees/w1", File.ReadAllText(Path.Combine(_root, ".git")));   // host pointer untouched
    }

    [Fact]
    public async Task GuestGit_Required_WithAnImageWithoutGit_ShouldBeUnavailable_AndIfAvailableShouldDegradeExplicitly()
    {
        var svc = NewService();
        var required = await svc.OpenSessionAsync(IsolationTestSupport.Context(_root), new IsolatedSessionOptions(Alpine, GuestGit: GuestGitMode.Required), CancellationToken.None);
        Assert.Equal(ExecutionDecisionKind.Unavailable, required.Decision.Kind);
        Assert.Equal(ExecutionReasonCodes.ImageUnavailable, required.Decision.ReasonCode);
        Assert.Null(required.Session);

        var optional = await OpenAsync(IsolationTestSupport.Context(_root), opts: new IsolatedSessionOptions(Alpine, GuestGit: GuestGitMode.IfAvailable), svc: svc);
        Assert.Null(optional.GuestGit);   // degraded without a repo, reported as such
        Assert.Equal(string.Empty, DockerOut($"ps -a -q --filter name=cfiso- --filter status=created"));
    }

    [Fact]
    public async Task Unavailable_ShouldRefuseWithoutAnyHostFallback()
    {
        var svc = NewService(new ProcessDockerCli("definitely-not-a-docker-binary-xyz"));
        var open = await svc.OpenSessionAsync(IsolationTestSupport.Context(_root), new IsolatedSessionOptions(Alpine), CancellationToken.None);
        Assert.Equal(ExecutionDecisionKind.Unavailable, open.Decision.Kind);
        Assert.Equal(ExecutionReasonCodes.IsolationUnavailable, open.Decision.ReasonCode);
        Assert.Null(open.Session);
    }

    [Fact]
    public async Task UnsupportedNetworkMode_ShouldBeRefused()
    {
        var ctx = IsolationTestSupport.Context(_root) with { Network = new ExecutionNetworkPolicy(ExecutionNetworkMode.Unrestricted) };
        var open = await NewService().OpenSessionAsync(ctx, new IsolatedSessionOptions(Alpine), CancellationToken.None);
        Assert.Equal(ExecutionReasonCodes.NetworkNotPermitted, open.Decision.ReasonCode);
        Assert.Null(open.Session);
    }

    [Fact]
    public async Task DotnetBuild_ShouldWorkOffline_AndMaliciousBuildTargetsStayInsideTheBoundary()
    {
        File.WriteAllText(Path.Combine(_root, "app.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <Target Name="Evil" BeforeTargets="CoreCompile">
                <Exec Command="echo pwn &gt; /workspace/evil.cs; echo pwn &gt; /workspace/../escape.txt; echo pwn &gt; /sentinel; ln -s /etc/passwd /workspace/src/link.cs; (wget -T 2 -qO- http://1.1.1.1 &amp;&amp; echo NET) || echo nonet &gt; /tmp/net.txt" ContinueOnError="true" IgnoreExitCode="true" />
              </Target>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_root, "Program.cs"), "Console.WriteLine(\"hello-from-guest\");");
        var ctx = IsolationTestSupport.Context(_root, ExportScope.From(["Program.cs"]), new ExecutionResourceLimits { CommandTimeout = TimeSpan.FromMinutes(5) });
        var s = await OpenAsync(ctx, opts: new IsolatedSessionOptions(VerificationImage));

        var build = await s.RunAsync(new IsolatedCommandRequest(["dotnet", "build", "-v:q", "--nologo"]), CancellationToken.None);
        Assert.True(build.Status == IsolatedCommandStatus.Completed, $"{build.Status} exit={build.ExitCode}\n{build.Stdout}\n{build.Stderr}");
        var run = await s.RunAsync(new IsolatedCommandRequest(["dotnet", "bin/Debug/net10.0/app.dll"]), CancellationToken.None);
        Assert.Contains("hello-from-guest", run.Stdout, StringComparison.Ordinal);

        // The build wrote bin/obj privately and the hostile target tried to write sources/links: the export must refuse.
        var export = await s.ExportScopedChangesAsync(CancellationToken.None);
        Assert.False(export.Decision.IsAllowed);
        Assert.Contains(export.Rejected, r => r.Path == "evil.cs");
        Assert.Contains(export.Rejected, r => r.ReasonCode == ExecutionReasonCodes.ExportRejectedLink);
        Assert.False(File.Exists(Path.Combine(_root, "evil.cs")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "escape.txt")));
        Assert.False(Directory.Exists(Path.Combine(_root, "bin")));
        Assert.False(Directory.Exists(Path.Combine(_root, "obj")));
        AssertSentinelIntact();
    }
}
