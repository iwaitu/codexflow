// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.InteropServices;
using System.Text;
using CodexFlow.Core.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CodexFlow.Core.Tests.Execution;

public sealed partial class LocalExecutionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codexflow-local-exec-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private LocalExecutionService CreateService(string? stagingRoot = null)
        => new(
            Options.Create(new IsolationOptions { StagingRoot = stagingRoot ?? Path.Combine(_root, "staging") }),
            NullLogger<LocalExecutionService>.Instance);

    private TrustedExecutionContext CreateContext(
        string workspace,
        ExportScope? scope = null,
        ExecutionNetworkMode network = ExecutionNetworkMode.Host,
        ExecutionResourceLimits? limits = null)
        => new()
        {
            UserId = "u1",
            SessionId = "s1",
            JobId = "j1",
            TaskId = "t1",
            AttemptId = "a1",
            WorkspaceRoot = workspace,
            Scope = scope ?? ExportScope.From(null, ["src"]),
            Network = new ExecutionNetworkPolicy(network),
            Limits = limits ?? new ExecutionResourceLimits()
        };

    [Fact]
    public async Task StageWorkspace_RunAndScopedExport_ShouldPublishInScopeAndRejectOutOfScope()
    {
        var workspace = Path.Combine(_root, "ws");
        Directory.CreateDirectory(Path.Combine(workspace, "src"));
        File.WriteAllText(Path.Combine(workspace, "src", "hello.txt"), "v1");

        using var service = CreateService();
        var open = await service.OpenSessionAsync(
            CreateContext(workspace, ExportScope.From(null, ["src"])),
            new IsolatedSessionOptions("local", StageWorkspace: true),
            CancellationToken.None);

        Assert.True(open.Decision.IsAllowed);
        await using var session = open.Session!;

        var dotNetCmd = OperatingSystem.IsWindows()
            ? new[] { "cmd", "/c", "echo v2> src\\hello.txt" }
            : new[] { "sh", "-c", "printf 'v2' > src/hello.txt" };
        var run = await session.RunAsync(new IsolatedCommandRequest(dotNetCmd), CancellationToken.None);
        Assert.Equal(IsolatedCommandStatus.Completed, run.Status);
        Assert.Contains("filesystem=unrestricted", run.Evidence, StringComparison.Ordinal);

        var export = await session.ExportScopedChangesAsync(CancellationToken.None);
        Assert.True(export.Decision.IsAllowed);
        Assert.Contains("v2", File.ReadAllText(Path.Combine(workspace, "src", "hello.txt")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StageWorkspaceFalse_ShouldRefuseExport()
    {
        var workspace = Path.Combine(_root, "ws-no-stage");
        Directory.CreateDirectory(workspace);

        using var service = CreateService();
        var open = await service.OpenSessionAsync(
            CreateContext(workspace),
            new IsolatedSessionOptions("local", StageWorkspace: false),
            CancellationToken.None);

        Assert.True(open.Decision.IsAllowed);
        await using var session = open.Session!;
        var export = await session.ExportScopedChangesAsync(CancellationToken.None);
        Assert.False(export.Decision.IsAllowed);
    }

    [Fact]
    public async Task ExportChannel_ShouldRejectOutOfScopeAndCasConflict()
    {
        var workspace = Path.Combine(_root, "ws-cas");
        Directory.CreateDirectory(Path.Combine(workspace, "src"));
        Directory.CreateDirectory(Path.Combine(workspace, "docs"));
        var target = Path.Combine(workspace, "src", "a.txt");
        File.WriteAllText(target, "original");

        using var service = CreateService();
        var open = await service.OpenSessionAsync(
            CreateContext(workspace, ExportScope.From(null, ["src"])),
            new IsolatedSessionOptions("local", StageWorkspace: true),
            CancellationToken.None);

        await using var session = open.Session!;
        var writeOutside = OperatingSystem.IsWindows()
            ? new[] { "cmd", "/c", "echo bad> docs\\outside.txt" }
            : new[] { "sh", "-c", "printf 'bad' > docs/outside.txt" };
        await session.RunAsync(new IsolatedCommandRequest(writeOutside), CancellationToken.None);

        var outOfScopeExport = await session.ExportScopedChangesAsync(CancellationToken.None);
        Assert.False(outOfScopeExport.Decision.IsAllowed);
        Assert.Equal(ExecutionReasonCodes.ExportRejectedOutOfScope, outOfScopeExport.Decision.ReasonCode);

        // Clean outside file, modify inside scope, then cause host CAS conflict.
        var sessionWorkspace = Path.Combine(_root, "staging", "local", "sessions", service.InstanceId, session.SessionHandle, "workspace");
        File.Delete(Path.Combine(sessionWorkspace, "docs", "outside.txt"));
        File.WriteAllText(Path.Combine(sessionWorkspace, "src", "a.txt"), "staged-edit");
        File.WriteAllText(target, "concurrent-host-edit");

        var casExport = await session.ExportScopedChangesAsync(CancellationToken.None);
        Assert.False(casExport.Decision.IsAllowed);
        Assert.Equal(ExecutionReasonCodes.ExportRejectedConcurrentModification, casExport.Decision.ReasonCode);
        Assert.Equal("concurrent-host-edit", File.ReadAllText(target));
    }

    [Fact]
    public async Task Environment_ShouldStripSensitiveVarsAndUsePrivateHome()
    {
        var workspace = Path.Combine(_root, "ws-env");
        Directory.CreateDirectory(workspace);

        Environment.SetEnvironmentVariable("TEST_SECRET_API_KEY", "secret-val");
        try
        {
            using var service = CreateService();
            var open = await service.OpenSessionAsync(
                CreateContext(workspace),
                new IsolatedSessionOptions("local", StageWorkspace: true),
                CancellationToken.None);

            await using var session = open.Session!;
            var cmd = OperatingSystem.IsWindows()
                ? new[] { "cmd", "/c", "echo HOME=%USERPROFILE% & echo KEY=%TEST_SECRET_API_KEY%" }
                : new[] { "sh", "-c", "echo HOME=$HOME KEY=$TEST_SECRET_API_KEY" };
            var run = await session.RunAsync(
                new IsolatedCommandRequest(cmd, Env: new Dictionary<string, string> { ["VllmAgent__ApiKey"] = "bad" }),
                CancellationToken.None);

            Assert.Equal(IsolatedCommandStatus.Completed, run.Status);
            Assert.Contains(session.SessionHandle, run.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-val", run.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_SECRET_API_KEY", null);
        }
    }

    [Fact]
    public async Task CapabilityTruthfulness_AbsolutePathWriteOutsideStagingHappensAndIsAudited()
    {
        var workspace = Path.Combine(_root, "ws-truth");
        Directory.CreateDirectory(workspace);
        var hostTarget = Path.Combine(_root, "outside-host-file.txt");

        using var service = CreateService();
        var open = await service.OpenSessionAsync(
            CreateContext(workspace, network: ExecutionNetworkMode.Host),
            new IsolatedSessionOptions("local", StageWorkspace: true),
            CancellationToken.None);

        await using var session = open.Session!;
        var cmd = OperatingSystem.IsWindows()
            ? new[] { "powershell", "-NoProfile", "-Command", $"Set-Content -Path '{hostTarget}' -Value 'written'" }
            : new[] { "sh", "-c", $"printf 'written' > \"{hostTarget}\"" };
        var run = await session.RunAsync(new IsolatedCommandRequest(cmd), CancellationToken.None);

        Assert.Equal(IsolatedCommandStatus.Completed, run.Status);
        Assert.True(File.Exists(hostTarget));
        Assert.Contains("network=Host", run.Evidence, StringComparison.Ordinal);
        Assert.Contains("filesystem=unrestricted", run.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalDirectoryTarBuilder_ShouldRejectProtectedCaseVariantAndUnsafeNames()
    {
        var dir = Path.Combine(_root, "tar-attacks");
        Directory.CreateDirectory(Path.Combine(dir, ".GIT"));
        File.WriteAllText(Path.Combine(dir, ".GIT", "config"), "evil");

        using var ms = new MemoryStream();
        var res = LocalDirectoryTarBuilder.BuildTar(dir, ms, new ExecutionResourceLimits());
        Assert.False(res.Success);
        Assert.Equal(ExecutionReasonCodes.ExportRejectedProtectedPath, Assert.Single(res.Rejections).ReasonCode);

        // Windows-specific attacks: junction & 8.3 short-name detection
        if (OperatingSystem.IsWindows())
        {
            var junctionStage = Path.Combine(_root, "tar-junction");
            var outsideTarget = Path.Combine(_root, "junction-target");
            Directory.CreateDirectory(junctionStage);
            Directory.CreateDirectory(outsideTarget);
            File.WriteAllText(Path.Combine(outsideTarget, "secret.txt"), "secret");

            var linkPath = Path.Combine(junctionStage, "linkdir");
            var mklink = await HostProcessRunner.RunAsync(
                new HostProcessInvocation("cmd", ["/c", "mklink", "/J", linkPath, outsideTarget]),
                CancellationToken.None);
            if (mklink.ExitCode == 0)
            {
                try
                {
                    using var jms = new MemoryStream();
                    var jRes = LocalDirectoryTarBuilder.BuildTar(junctionStage, jms, new ExecutionResourceLimits());
                    Assert.False(jRes.Success);
                    Assert.Equal(ExecutionReasonCodes.ExportRejectedLink, Assert.Single(jRes.Rejections).ReasonCode);
                }
                finally
                {
                    if (Directory.Exists(linkPath))
                    {
                        Directory.Delete(linkPath);
                    }
                }
            }

            // Root workspace itself replaced by a junction must also be refused.
            var rootJunction = Path.Combine(_root, "root-junction");
            var mkRoot = await HostProcessRunner.RunAsync(
                new HostProcessInvocation("cmd", ["/c", "mklink", "/J", rootJunction, outsideTarget]),
                CancellationToken.None);
            if (mkRoot.ExitCode == 0)
            {
                try
                {
                    using var rjms = new MemoryStream();
                    var rjRes = LocalDirectoryTarBuilder.BuildTar(rootJunction, rjms, new ExecutionResourceLimits());
                    Assert.False(rjRes.Success);
                    Assert.Equal(ExecutionReasonCodes.ExportRejectedLink, Assert.Single(rjRes.Rejections).ReasonCode);
                }
                finally
                {
                    if (Directory.Exists(rootJunction))
                    {
                        Directory.Delete(rootJunction);
                    }
                }
            }

            // Real NTFS alternate data stream detection.
            var adsDir = Path.Combine(_root, "ads-dir");
            Directory.CreateDirectory(adsDir);
            var adsHostFile = Path.Combine(adsDir, "normal.txt");
            File.WriteAllText(adsHostFile, "visible");
            File.WriteAllText(adsHostFile + ":hidden", "secret-stream");
            using (var adsMs = new MemoryStream())
            {
                var adsRes = LocalDirectoryTarBuilder.BuildTar(adsDir, adsMs, new ExecutionResourceLimits());
                Assert.False(adsRes.Success);
                Assert.Equal(ExecutionReasonCodes.ExportRejectedUnsafePath, Assert.Single(adsRes.Rejections).ReasonCode);
            }

            // Real NTFS hard link detection.
            var hardLinkDir = Path.Combine(_root, "hardlink-dir");
            Directory.CreateDirectory(hardLinkDir);
            var hardLinkTarget = Path.Combine(_root, "hardlink-secret.txt");
            File.WriteAllText(hardLinkTarget, "outside-secret");
            var hardLinkPath = Path.Combine(hardLinkDir, "linked.txt");
            var mkHard = await HostProcessRunner.RunAsync(
                new HostProcessInvocation("cmd", ["/c", "mklink", "/H", hardLinkPath, hardLinkTarget]),
                CancellationToken.None);
            if (mkHard.ExitCode == 0)
            {
                using var hlMs = new MemoryStream();
                var hlRes = LocalDirectoryTarBuilder.BuildTar(hardLinkDir, hlMs, new ExecutionResourceLimits());
                Assert.False(hlRes.Success);
                Assert.Equal(ExecutionReasonCodes.ExportRejectedLink, Assert.Single(hlRes.Rejections).ReasonCode);
            }

            // 8.3 short-name probe if volume generates short names
            var longDir = Path.Combine(_root, "long-name-dir");
            Directory.CreateDirectory(longDir);
            var longFile = Path.Combine(longDir, "verylongfilename.txt");
            File.WriteAllText(longFile, "hi");
            var sb = new char[1024];
            var len = GetShortPathNameW(longFile, sb, (uint)sb.Length);
            if (len > 0)
            {
                var shortPath = new string(sb, 0, (int)len);
                var shortName = Path.GetFileName(shortPath);
                if (!string.Equals(shortName, "verylongfilename.txt", StringComparison.OrdinalIgnoreCase))
                {
                    Assert.True(LocalDirectoryTarBuilder.IsWindowsShortNameAlias(shortPath, shortName));
                }
            }
        }
    }

    [Fact]
    public async Task DisposeAsync_ShouldCleanStagingDirectory()
    {
        var workspace = Path.Combine(_root, "ws-cleanup");
        Directory.CreateDirectory(workspace);

        using var service = CreateService();
        var open = await service.OpenSessionAsync(
            CreateContext(workspace),
            new IsolatedSessionOptions("local", StageWorkspace: true),
            CancellationToken.None);

        var session = open.Session!;
        var sessionDir = Path.Combine(_root, "staging", "local", "sessions", service.InstanceId, session.SessionHandle);
        Assert.True(Directory.Exists(sessionDir));

        await session.DisposeAsync();
        Assert.False(Directory.Exists(sessionDir));
    }

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetShortPathNameW(
        string lpszLongPath,
        [Out] char[] lpszShortPath,
        uint cchBuffer);
}
