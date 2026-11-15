// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Execution;
using Xunit;

namespace CodexFlow.Core.Tests.Execution;

/// <summary>
/// The NuGet SDK resolver only reads the GLOBAL packages folder (never fallback folders), so a configured trusted restore
/// cache must also be NUGET_PACKAGES — still mounted read-only, with every other isolation flag unchanged.
/// </summary>
[Collection(SafePublishCollection.Name)]
public class RestoreCacheEnvTests : IDisposable
{
    private const string PrivatePackages = "NUGET_PACKAGES=/tmp/home/.nuget/packages";
    private readonly string _root = IsolationTestSupport.NewTempDir("rcws");
    private readonly string _cache = IsolationTestSupport.NewTempDir("rccache");
    private readonly string _staging = IsolationTestSupport.NewTempDir("rcstg");

    public void Dispose()
    {
        IsolationTestSupport.Cleanup(_root);
        IsolationTestSupport.Cleanup(_cache);
        IsolationTestSupport.Cleanup(_staging);
    }

    private IReadOnlyList<string> Args(IsolationOptions? options) =>
        DockerIsolatedExecutionService.BuildCreateArgs("h", "img:1", IsolationTestSupport.Context(_root), options);

    private static IEnumerable<string> Envs(IReadOnlyList<string> args) =>
        args.Select((a, i) => (a, i)).Where(x => x.a == "-e").Select(x => args[x.i + 1]);

    [Fact]
    public void WithAnExistingCache_NuGetPackagesIsTheReadOnlyCache_AndNothingElseChanges()
    {
        var withCache = Args(new IsolationOptions { RestoreCacheHostPath = _cache });
        var without = Args(null);

        Assert.Single(Envs(withCache), e => e.StartsWith("NUGET_PACKAGES=", StringComparison.Ordinal));
        Assert.Contains("NUGET_PACKAGES=/restore-cache", Envs(withCache));
        Assert.DoesNotContain(PrivatePackages, Envs(withCache));
        var mount = Assert.Single(withCache, a => a.EndsWith(":/restore-cache:ro", StringComparison.Ordinal));
        Assert.StartsWith(Path.GetFullPath(_cache), mount, StringComparison.Ordinal);

        // isolation constraints identical with and without a cache (apart from the cache mount/env themselves)
        foreach (var flag in new[] { "--network", "none", "--cap-drop", "ALL", "--read-only", "--user", "--security-opt", "no-new-privileges" })
        {
            Assert.Contains(flag, withCache);
            Assert.Contains(flag, without);
        }

        Assert.Equal(1, withCache.Count(a => a == "-v"));
        Assert.DoesNotContain("-v", without);
        Assert.DoesNotContain(withCache, a => a.Contains("docker.sock", StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutACache_OrAMissingCacheDirectory_NuGetPackagesStaysPrivateAndWritable()
    {
        foreach (var options in new IsolationOptions?[] { null, new IsolationOptions(), new IsolationOptions { RestoreCacheHostPath = "" },
                     new IsolationOptions { RestoreCacheHostPath = Path.Combine(_cache, "does-not-exist") } })
        {
            var args = Args(options);
            Assert.Single(Envs(args), e => e.StartsWith("NUGET_PACKAGES=", StringComparison.Ordinal));
            Assert.Contains(PrivatePackages, Envs(args));
            Assert.DoesNotContain(args, a => a.Contains("/restore-cache", StringComparison.Ordinal));
        }
    }
}
