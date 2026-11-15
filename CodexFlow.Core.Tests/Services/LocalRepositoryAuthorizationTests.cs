// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using CodexFlow.Core.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CodexFlow.Core.Tests.Services;

/// <summary>Positive and negative acceptance of the shared local-repository policy (default deny, explicit shared roots, aliases).</summary>
public sealed class LocalRepositoryAuthorizationTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "lra-" + Guid.NewGuid().ToString("N"));

    public LocalRepositoryAuthorizationTests() => Directory.CreateDirectory(_base);

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string Dir(string relative)
    {
        var path = Path.Combine(_base, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    private static bool TryAlias(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            if (!OperatingSystem.IsWindows()) return false;
            var start = new ProcessStartInfo("cmd", $"/c mklink /J \"{link}\" \"{target}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(start)!;
            p.WaitForExit();
            return p.ExitCode == 0 && Directory.Exists(link);
        }
    }

    private static bool RequireAlias(string link, string target)
    {
        var created = TryAlias(link, target);
        Assert.True(created || !OperatingSystem.IsWindows(), "could not create a directory alias on Windows");
        return created;
    }

    private static readonly string[] None = [];

    // ---- default (nothing configured): only the current session's own root --------------------------------------

    [Fact]
    public void SessionRootItself_AndSubdirectories_AreAllowed()
    {
        var session = Dir("sessions/s1");
        var repo = Dir("sessions/s1/repo");
        Assert.True(LocalRepositoryAuthorization.IsAllowed(repo, session, None));
        Assert.True(LocalRepositoryAuthorization.IsAllowed(session, session, None));
        Assert.True(LocalRepositoryAuthorization.IsAllowed(Dir("sessions/s1/a/b/repo"), session, None));
    }

    [Fact]
    public void AnotherSessionsDirectory_IsDenied_ByDefault_EvenThoughItExistsOnTheSameHost()
    {
        var mine = Dir("sessions/s1");
        var theirs = Dir("sessions/s2/repo");
        var decision = LocalRepositoryAuthorization.Evaluate(theirs, mine, None);
        Assert.False(decision.Allowed);
        Assert.Equal(LocalRepositoryDenial.OutsideSessionAndAllowedRoots, decision.Reason);
    }

    [Fact]
    public void SiblingWithTheSamePrefix_AndTheParentOfTheSessionRoot_AreDenied()
    {
        var session = Dir("sessions/s1");
        Assert.False(LocalRepositoryAuthorization.IsAllowed(Dir("sessions/s1-evil/repo"), session, None));
        Assert.False(LocalRepositoryAuthorization.IsAllowed(Dir("sessions"), session, None));
    }

    [Fact]
    public void UnconfiguredExternalRepository_IsDenied()
    {
        var session = Dir("sessions/s1");
        var shared = Dir("shared/repo");
        Assert.False(LocalRepositoryAuthorization.IsAllowed(shared, session, None));
        Assert.False(LocalRepositoryAuthorization.Evaluate(shared, session, (IConfiguration?)null).Allowed);
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("")]
    [InlineData("   ")]
    public void NonAbsoluteRepositoryPaths_AreDenied(string repo)
    {
        var decision = LocalRepositoryAuthorization.Evaluate(repo, Dir("sessions/s1"), None);
        Assert.Equal(LocalRepositoryDenial.NotRooted, decision.Reason);
    }

    [Fact]
    public void MissingRepository_IsDenied()
        => Assert.Equal(
            LocalRepositoryDenial.NotFound,
            LocalRepositoryAuthorization.Evaluate(Path.Combine(_base, "sessions", "s1", "nope"), Dir("sessions/s1"), None).Reason);

    // ---- explicit administrator allow-list -----------------------------------------------------------------------

    [Fact]
    public void ConfiguredSharedRoot_AllowsItsSubdirectories_AndTheRootItself()
    {
        var session = Dir("sessions/s1");
        var shared = Dir("shared");
        var repo = Dir("shared/team/repo");
        Assert.True(LocalRepositoryAuthorization.IsAllowed(repo, session, [shared]));
        Assert.True(LocalRepositoryAuthorization.IsAllowed(shared, session, [shared + Path.DirectorySeparatorChar]));
    }

    [Fact]
    public void ConfiguredSharedRoot_DoesNotAllowSiblingsOrOtherSessions()
    {
        var session = Dir("sessions/s1");
        var shared = Dir("shared");
        Assert.False(LocalRepositoryAuthorization.IsAllowed(Dir("shared-other/repo"), session, [shared]));
        Assert.False(LocalRepositoryAuthorization.IsAllowed(Dir("sessions/s2/repo"), session, [shared]));
    }

    [Fact]
    public void ConfigurationBinding_ReadsTheDocumentedKey_AndAllowsOnlyThoseRoots()
    {
        var session = Dir("sessions/s1");
        var shared = Dir("shared");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Workspace:AllowedLocalRepositoryRoots:0"] = shared
        }).Build();

        Assert.True(LocalRepositoryAuthorization.Evaluate(Dir("shared/repo"), session, configuration).Allowed);
        Assert.False(LocalRepositoryAuthorization.Evaluate(Dir("other/repo"), session, configuration).Allowed);
        Assert.Equal("Workspace:AllowedLocalRepositoryRoots", LocalRepositoryAuthorization.ConfigurationKey);
    }

    // ---- invalid configuration fails closed ----------------------------------------------------------------------

    [Theory]
    [InlineData("relative/root")]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("C:/*")]
    public void InvalidConfiguredEntry_MakesTheSharedAllowanceFailClosed(string badEntry)
    {
        var session = Dir("sessions/s1");
        var shared = Dir("shared");
        var repo = Dir("shared/repo");
        var decision = LocalRepositoryAuthorization.Evaluate(repo, session, [shared, badEntry]); // a valid entry next to a bad one
        Assert.False(decision.Allowed);
        Assert.Equal(LocalRepositoryDenial.InvalidConfiguration, decision.Reason);
        // The session-root rule is unaffected by configuration, good or bad.
        Assert.True(LocalRepositoryAuthorization.IsAllowed(Dir("sessions/s1/repo"), session, [shared, badEntry]));
    }

    [Fact]
    public void FilesystemRoot_IsNeverAcceptableAsAConfiguredRoot()
    {
        var session = Dir("sessions/s1");
        var fsRoot = Path.GetPathRoot(_base)!;
        var validation = LocalRepositoryAuthorization.ValidateAllowedRoots([fsRoot]);
        Assert.False(validation.IsValid);
        Assert.False(LocalRepositoryAuthorization.IsAllowed(Dir("anything/repo"), session, [fsRoot]));
    }

    [Fact]
    public void NullAllowList_IsRejected()
        => Assert.Throws<ArgumentNullException>(() => LocalRepositoryAuthorization.IsAllowed(_base, _base, null!));

    // ---- aliases ---------------------------------------------------------------------------------------------------

    [Fact]
    public void AliasInsideTheSessionRootPointingOutside_IsDenied()
    {
        var session = Dir("sessions/s1");
        var foreign = Dir("sessions/s2/repo");
        var alias = Path.Combine(session, "looks-local");
        if (!RequireAlias(alias, foreign)) return;
        var decision = LocalRepositoryAuthorization.Evaluate(alias, session, None);
        Assert.Equal(LocalRepositoryDenial.LinkedPath, decision.Reason);
    }

    [Fact]
    public void AliasedAncestorOfARepositoryUnderASharedRoot_IsDenied()
    {
        var session = Dir("sessions/s1");
        var realShared = Dir("real-shared");
        Dir("real-shared/repo");
        var aliasShared = Path.Combine(_base, "alias-shared");
        if (!RequireAlias(aliasShared, realShared)) return;
        var viaAlias = Path.Combine(aliasShared, "repo");
        var decision = LocalRepositoryAuthorization.Evaluate(viaAlias, session, [aliasShared]);
        Assert.False(decision.Allowed); // either the repository chain or the configured entry is a link
    }

    [Fact]
    public void ConfiguredRootThatIsAnAlias_IsInvalidConfiguration()
    {
        var realShared = Dir("real-shared");
        var aliasShared = Path.Combine(_base, "alias-shared");
        if (!RequireAlias(aliasShared, realShared)) return;
        Assert.False(LocalRepositoryAuthorization.ValidateAllowedRoots([aliasShared]).IsValid);
    }

    [Fact]
    public void SessionRootThatIsAFilesystemRoot_AuthorizesNothing()
    {
        var fsRoot = Path.GetPathRoot(_base)!;
        Assert.False(LocalRepositoryAuthorization.IsAllowed(Dir("anything/repo"), fsRoot, None));
    }
}
