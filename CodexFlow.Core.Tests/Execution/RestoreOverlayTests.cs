// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Execution;
using Xunit;

namespace CodexFlow.Core.Tests.Execution;

/// <summary>
/// Host-fetched packages (security upgrades the base restore cache does not contain) reach the network-less guest through a SECOND
/// read-only mount, located by the host-trusted session id only. The model can influence neither the path nor the content.
/// </summary>
[Collection(SafePublishCollection.Name)]
public sealed class RestoreOverlayTests : IDisposable
{
    private const string Hash = "0123456789abcdef";
    private readonly string _workspace = IsolationTestSupport.NewTempDir("rowsp");
    private readonly string _cache = IsolationTestSupport.NewTempDir("rocache");
    private readonly string _overlayRoot = IsolationTestSupport.NewTempDir("rooverlay");

    public void Dispose()
    {
        IsolationTestSupport.Cleanup(_workspace);
        IsolationTestSupport.Cleanup(_cache);
        IsolationTestSupport.Cleanup(_overlayRoot);
    }

    private string MakeOverlay(string session = "s1", string name = Hash, bool pointer = true)
    {
        var dir = Path.Combine(_overlayRoot, session, name);
        Directory.CreateDirectory(dir);
        if (pointer) File.WriteAllText(Path.Combine(_overlayRoot, session, "current"), name);
        return dir;
    }

    private IReadOnlyList<string> Args(IsolationOptions options) =>
        DockerIsolatedExecutionService.BuildCreateArgs("h", "img:1", IsolationTestSupport.Context(_workspace), options);

    private static IEnumerable<string> Envs(IReadOnlyList<string> args) =>
        args.Select((a, i) => (a, i)).Where(x => x.a == "-e").Select(x => args[x.i + 1]);

    // ── locating the overlay ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Locator_ReturnsTheDirectoryTheCurrentPointerNames()
    {
        var dir = MakeOverlay();

        Assert.Equal(Path.GetFullPath(dir), RestoreOverlayLocator.Resolve(_overlayRoot, "s1"));
    }

    [Theory]
    [InlineData(null, "s1")]
    [InlineData("", "s1")]
    [InlineData("<root>/missing", "s1")]
    [InlineData("<root>", "")]
    [InlineData("<root>", "../s1")]
    [InlineData("<root>", "s1/../s1")]
    [InlineData("<root>", "a b")]
    [InlineData("<root>", "unknown-session")]
    public void Locator_IsNullForAnythingThatIsNotAnExistingOverlayOfAWellFormedSession(string? root, string session)
    {
        MakeOverlay();
        root = root?.Replace("<root>", _overlayRoot, StringComparison.Ordinal);

        Assert.Null(RestoreOverlayLocator.Resolve(root, session));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData("NOT-HEX-NAME!")]
    public void Locator_RejectsAPointerThatCouldLeaveTheSessionDirectory(string pointerContent)
    {
        MakeOverlay(pointer: false);
        File.WriteAllText(Path.Combine(_overlayRoot, "s1", "current"), pointerContent);

        Assert.Null(RestoreOverlayLocator.Resolve(_overlayRoot, "s1"));
    }

    [Fact]
    public void Locator_IsNullWhenThePointerNamesADirectoryThatDoesNotExist()
    {
        MakeOverlay();
        File.WriteAllText(Path.Combine(_overlayRoot, "s1", "current"), "fedcba9876543210");

        Assert.Null(RestoreOverlayLocator.Resolve(_overlayRoot, "s1"));
    }

    // ── the guest mount ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Guest_GetsTheOverlayAsASecondReadOnlyFallbackNextToTheBaseCache()
    {
        var overlay = MakeOverlay();

        var args = Args(new IsolationOptions { RestoreCacheHostPath = _cache, RestoreOverlayRoot = _overlayRoot });

        Assert.Equal(2, args.Count(a => a == "-v"));
        Assert.Single(args, a => a.EndsWith(":/restore-cache:ro", StringComparison.Ordinal));
        var extra = Assert.Single(args, a => a.EndsWith(":/restore-cache-extra:ro", StringComparison.Ordinal));
        Assert.StartsWith(Path.GetFullPath(overlay), extra, StringComparison.Ordinal);
        Assert.Single(Envs(args), e => e.StartsWith("RestoreAdditionalProjectFallbackFolders=", StringComparison.Ordinal));
        Assert.Contains("RestoreAdditionalProjectFallbackFolders=/restore-cache;/restore-cache-extra", Envs(args));
        // a package asking for "Y (>= a.b.c)" is looked up at exactly that version first; offline that must not fail just because the official source is unreachable
        Assert.Contains("RestoreIgnoreFailedSources=true", Envs(args));
        Assert.Contains("NUGET_PACKAGES=/restore-cache", Envs(args)); // the SDK resolver still reads only the base cache
        Assert.Contains("none", args);                                // network stays off
        Assert.DoesNotContain(args, a => a.Contains("docker.sock", StringComparison.Ordinal));
    }

    [Fact]
    public void Guest_WithOnlyAnOverlay_KeepsAPrivateWritableGlobalFolder()
    {
        MakeOverlay();

        var args = Args(new IsolationOptions { RestoreOverlayRoot = _overlayRoot });

        Assert.Single(args, a => a.EndsWith(":/restore-cache-extra:ro", StringComparison.Ordinal));
        Assert.DoesNotContain(args, a => a.EndsWith(":/restore-cache:ro", StringComparison.Ordinal));
        Assert.Contains("NUGET_PACKAGES=/tmp/home/.nuget/packages", Envs(args));
        Assert.Contains("RestoreAdditionalProjectFallbackFolders=/restore-cache-extra", Envs(args));
    }

    [Fact]
    public void Guest_WithoutAnOverlayForThisSession_IsExactlyWhatItWasBefore()
    {
        MakeOverlay(session: "another-session");
        var withRootButNoOverlay = Args(new IsolationOptions { RestoreCacheHostPath = _cache, RestoreOverlayRoot = _overlayRoot });
        var withoutRoot = Args(new IsolationOptions { RestoreCacheHostPath = _cache });

        Assert.Equal(withoutRoot, withRootButNoOverlay);
        Assert.Equal(1, withRootButNoOverlay.Count(a => a == "-v"));
        Assert.DoesNotContain(withRootButNoOverlay, a => a.Contains("restore-cache-extra", StringComparison.Ordinal));
    }
}
