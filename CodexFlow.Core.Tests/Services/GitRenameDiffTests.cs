// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodexFlow.Core.Tests.Services;

/// <summary>
/// Real-git regression for the rename blind spot: a rename must be reported as delete(old)+add(new), raw (unquoted) and
/// with Unicode/space paths intact, so an out-of-scope source path cannot hide behind an in-scope destination.
/// </summary>
public class GitRenameDiffTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "cf-rename-" + Guid.NewGuid().ToString("N"));

    public GitRenameDiffTests() => Directory.CreateDirectory(_repo);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_repo, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _repo, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-c", "core.autocrlf=false", "-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false" }.Concat(args))
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, p.StandardError.ReadToEnd());
        return o.Trim();
    }

    private (string Base, string Head) RenameCommit(string oldRel, string newRel)
    {
        Git("init", "-q");
        var oldFull = Path.Combine(_repo, oldRel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(oldFull)!);
        File.WriteAllText(oldFull, string.Join('\n', Enumerable.Range(0, 20).Select(i => "line " + i)) + "\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "base");
        var baseCommit = Git("rev-parse", "HEAD");
        var newFull = Path.Combine(_repo, newRel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(newFull)!);
        Git("mv", oldRel, newRel);
        Git("commit", "-q", "-m", "rename");
        return (baseCommit, Git("rev-parse", "HEAD"));
    }

    [Theory]
    [InlineData("secret/old.txt", "src/new.txt")]
    [InlineData("secret/旧文件.txt", "src/新文件 with space.txt")]
    public async Task Rename_ShouldReportBothSourceAndDestination_Raw(string oldRel, string newRel)
    {
        var (b, h) = RenameCommit(oldRel, newRel);
        var changes = await new GitService(NullLogger<GitService>.Instance).GetChangedFilesAsync(_repo, b, h);

        Assert.Contains(changes, c => c.FilePath == oldRel && c.Status == "D");
        Assert.Contains(changes, c => c.FilePath == newRel && c.Status == "A");
        Assert.Equal(2, changes.Count);
    }

    [Fact]
    public async Task OutOfScopeSourceOfARename_ShouldBeRejectedByTheScopeCheck()
    {
        var (b, h) = RenameCommit("secret/old.txt", "src/new.txt");
        var changes = await new GitService(NullLogger<GitService>.Instance).GetChangedFilesAsync(_repo, b, h);
        var scope = new TaskFileScopeDescriptor();
        scope.AllowedFiles.Add("src/new.txt");

        var violations = TaskFileScopeGuard.GetOutOfScopeChanges(changes, scope);

        Assert.Contains("secret/old.txt", violations);
        Assert.DoesNotContain("src/new.txt", violations);
    }

    [Theory]
    [InlineData("secret/old.txt", "src/new.txt", true)]
    [InlineData("secret/旧文件.txt", "src/新文件 with space.txt", true)]
    [InlineData("secret/旧文件.txt", "src/新文件 with space.txt", false)]
    public async Task WorkingTreeRename_ShouldReportBothPaths_AndRejectTheOutOfScopeSource(string oldRel, string newRel, bool staged)
    {
        var (b, _) = RenameCommit(oldRel, "keep/placeholder.txt");
        // undo the committed rename: work on the uncommitted tree against the base commit instead
        Git("reset", "-q", "--hard", b);
        var newFull = Path.Combine(_repo, newRel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(newFull)!);
        if (staged)
        {
            Git("mv", oldRel, newRel);
        }
        else
        {
            File.Move(Path.Combine(_repo, oldRel.Replace('/', Path.DirectorySeparatorChar)), newFull);
        }

        var changes = await new GitService(NullLogger<GitService>.Instance).GetWorkingTreeChangedFilesAsync(_repo, b);

        Assert.Contains(changes, c => c.FilePath == oldRel && c.Status == "D");
        Assert.Contains(changes, c => c.FilePath == newRel);   // staged: A (index) ; unstaged: untracked A
        var scope = new TaskFileScopeDescriptor();
        scope.AllowedFiles.Add(newRel);
        var violations = TaskFileScopeGuard.GetOutOfScopeChanges(changes, scope);
        Assert.Contains(oldRel, violations);
        Assert.DoesNotContain(newRel, violations);
    }

    [Theory]
    [InlineData("secret/old.txt", "src/new.txt")]
    [InlineData("secret/旧文件.txt", "src/新文件 with space.txt")]
    public async Task Inspector_ShouldRejectAManifestListingOnlyTheRenameDestination(string oldRel, string newRel)
    {
        var (b, h) = RenameCommit(oldRel, newRel);
        var inspector = new GitArtifactInspector();
        var manifest = new CodexFlow.Contracts.ForgeArtifactManifest
        {
            ArtifactId = "a", UserId = "u", SessionId = "s", JobId = "j", Attempt = 1,
            RepositoryIdentity = await inspector.GetRepositoryIdentityAsync(_repo), WorktreePath = _repo,
            BaseCommit = b, HeadCommit = h, ScopeHash = "scope",
            ChangedPaths = [newRel], CreatedAtUtc = DateTimeOffset.UtcNow
        };

        // old behaviour (rename-collapsed diff) would have accepted this; the source deletion must be listed too
        Assert.Equal("ArtifactChangesMismatch", await inspector.ValidateAsync(manifest, _repo, "u", "s", "j", 1, []));
        Assert.Null(await inspector.ValidateAsync(manifest with { ChangedPaths = [oldRel, newRel] }, _repo, "u", "s", "j", 1, []));
        Assert.Equal("ArtifactChangesMismatch", await inspector.ValidateAsync(manifest with { ChangedPaths = [oldRel] }, _repo, "u", "s", "j", 1, []));
    }

    [Fact]
    public async Task Inspector_ShouldRejectATamperedParentThatHidesARenameSource()
    {
        var (b, h) = RenameCommit("secret/old.txt", "src/new.txt");
        var inspector = new GitArtifactInspector();
        var identity = await inspector.GetRepositoryIdentityAsync(_repo);
        var parent = new CodexFlow.Contracts.ForgeArtifactManifest
        {
            ArtifactId = "p", UserId = "u", SessionId = "s", JobId = "pj", TaskId = "pt", Attempt = 1,
            RepositoryIdentity = identity, WorktreePath = _repo, BaseCommit = b, HeadCommit = h, ScopeHash = "scope",
            ChangedPaths = ["src/new.txt"], CreatedAtUtc = DateTimeOffset.UtcNow
        };
        var final = parent with { ArtifactId = "f", JobId = "j", TaskId = "t", ParentArtifactIds = ["p"], ChangedPaths = ["secret/old.txt", "src/new.txt"] };

        Assert.Equal("ArtifactParentChangesMismatch", await inspector.ValidateAsync(final, _repo, "u", "s", "j", 1, [parent]));
        Assert.Null(await inspector.ValidateAsync(final, _repo, "u", "s", "j", 1, [parent with { ChangedPaths = ["secret/old.txt", "src/new.txt"] }]));
    }

    [Theory]
    [InlineData("--output={SENTINEL}")]
    [InlineData("HEAD --output={SENTINEL}")]
    [InlineData("-p")]
    [InlineData("HEAD\0--output={SENTINEL}")]
    [InlineData("HEAD\n--output={SENTINEL}")]
    [InlineData("HEAD\"--output={SENTINEL}")]
    [InlineData("")]
    public async Task DiffEntryPoints_ShouldRefuseOptionLikeRefs_AndNeverWriteTheSentinel(string template)
    {
        var (b, h) = RenameCommit("a.txt", "b.txt");
        var sentinel = Path.Combine(Path.GetTempPath(), "cf-sentinel-" + Guid.NewGuid().ToString("N"));
        var evil = template.Replace("{SENTINEL}", sentinel, StringComparison.Ordinal);
        var git = new GitService(NullLogger<GitService>.Instance);
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => git.GetChangedFilesAsync(_repo, evil, h));
            await Assert.ThrowsAsync<ArgumentException>(() => git.GetChangedFilesAsync(_repo, b, evil));
            await Assert.ThrowsAsync<ArgumentException>(() => git.GetWorkingTreeChangedFilesAsync(_repo, evil));
            Assert.False(File.Exists(sentinel), "git option injection wrote a host file");
        }
        finally
        {
            if (File.Exists(sentinel)) File.Delete(sentinel);
        }
    }

    [Fact]
    public async Task DiffEntryPoints_ShouldStillAcceptNormalRefs()
    {
        var (b, h) = RenameCommit("a.txt", "b.txt");
        var git = new GitService(NullLogger<GitService>.Instance);
        Assert.Equal(2, (await git.GetChangedFilesAsync(_repo, b, "HEAD")).Count);
        Assert.Equal(2, (await git.GetChangedFilesAsync(_repo, b[..12], h)).Count);
        Assert.Empty(await git.GetWorkingTreeChangedFilesAsync(_repo, "HEAD"));
    }

    [Fact]
    public async Task WorkingTreeUntrackedUnicodePath_ShouldBeReportedRaw()
    {
        Git("init", "-q");
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "1\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "base");
        var b = Git("rev-parse", "HEAD");
        File.WriteAllText(Path.Combine(_repo, "新 文件.txt"), "x\n");

        var changes = await new GitService(NullLogger<GitService>.Instance).GetWorkingTreeChangedFilesAsync(_repo, b);

        Assert.Contains(changes, c => c.FilePath == "新 文件.txt" && c.Status == "A");
    }

    [Fact]
    public async Task PlainModifyAndAdd_ShouldKeepStatusAndLineStats()
    {
        Git("init", "-q");
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "1\n2\n3\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "base");
        var b = Git("rev-parse", "HEAD");
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "1\n2\n3\n4\n");
        File.WriteAllText(Path.Combine(_repo, "n.txt"), "x\ny\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "next");

        var changes = await new GitService(NullLogger<GitService>.Instance).GetChangedFilesAsync(_repo, b, Git("rev-parse", "HEAD"));

        Assert.Contains(changes, c => c.FilePath == "a.txt" && c.Status == "M" && c.Additions == 1);
        Assert.Contains(changes, c => c.FilePath == "n.txt" && c.Status == "A" && c.Additions == 2);
    }
}
