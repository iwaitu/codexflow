// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using CodexFlow.Contracts;
using CodexFlow.Core.Services;
using Xunit;

namespace CodexFlow.Core.Tests.Agents;

public sealed class GitArtifactInspectorTests
{
    [Fact]
    public async Task ParallelBranches_RequireIntegratedCommitContainingAllParents()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cf-artifact-chain-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(directory, "repo");
        var worktree = Path.Combine(directory, "integration");
        Directory.CreateDirectory(root);
        try
        {
            await Git(root, "init");
            await Git(root, "config", "user.name", "Artifact Test");
            await Git(root, "config", "user.email", "test@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(root, "base.txt"), "base");
            await Git(root, "add", ".");
            await Git(root, "commit", "-m", "base");
            var baseline = (await Git(root, "rev-parse", "HEAD")).Trim();
            await File.WriteAllTextAsync(Path.Combine(root, "first.txt"), "first task");
            await Git(root, "add", ".");
            await Git(root, "commit", "-m", "first");
            var first = (await Git(root, "rev-parse", "HEAD")).Trim();
            await Git(root, "worktree", "add", "-b", "integration", worktree, baseline);
            await File.WriteAllTextAsync(Path.Combine(worktree, "second.txt"), "second task");
            await Git(worktree, "add", ".");
            await Git(worktree, "commit", "-m", "second");
            var inspector = new GitArtifactInspector();
            var identity = await inspector.GetRepositoryIdentityAsync(root);
            var parent = new ForgeArtifactManifest
            {
                ArtifactId = "first", UserId = "u", SessionId = "s", JobId = "first-job", TaskId = "first-task", Attempt = 1,
                BaseCommit = baseline, HeadCommit = first, RepositoryIdentity = identity, ScopeHash = "scope",
                WorktreePath = root, ChangedPaths = ["first.txt"], CreatedAtUtc = DateTimeOffset.UtcNow
            };
            var final = parent with
            {
                ArtifactId = "second", JobId = "second-job", TaskId = "second-task", WorktreePath = worktree,
                HeadCommit = (await Git(worktree, "rev-parse", "HEAD")).Trim(), ParentArtifactIds = ["first"], ChangedPaths = ["second.txt"]
            };
            Assert.Equal("ArtifactGitVerificationFailed", await inspector.ValidateAsync(final, root, "u", "s", "second-job", 1, [parent]));
            await Git(worktree, "merge", first, "--no-edit");
            final = final with { HeadCommit = (await Git(worktree, "rev-parse", "HEAD")).Trim(), ChangedPaths = ["first.txt", "second.txt"] };
            Assert.Null(await inspector.ValidateAsync(final, root, "u", "s", "second-job", 1, [parent]));
            Assert.Equal("ArtifactParentMismatch", await inspector.ValidateAsync(final, root, "u", "s", "second-job", 1, [parent with { SessionId = "other" }]));
            Assert.Equal("ArtifactMissing", await inspector.ValidateAsync(final with { WorktreePath = Path.Combine(directory, "deleted") }, root, "u", "s", "second-job", 1, [parent]));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RealRepository_RejectsDriftDirtyTreeWrongRepositoryAndMissingParent()
    {
        var root = Path.Combine(Path.GetTempPath(), "cf-artifact-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await Git(root, "init");
            await Git(root, "config", "user.email", "test@example.invalid");
            await Git(root, "config", "user.name", "Artifact Test");
            await File.WriteAllTextAsync(Path.Combine(root, "source.txt"), "base");
            await Git(root, "add", ".");
            await Git(root, "commit", "-m", "base");
            var baseline = (await Git(root, "rev-parse", "HEAD")).Trim();
            await File.WriteAllTextAsync(Path.Combine(root, "source.txt"), "fixed");
            await Git(root, "commit", "-am", "fix");
            var head = (await Git(root, "rev-parse", "HEAD")).Trim();
            var inspector = new GitArtifactInspector();
            var manifest = new ForgeArtifactManifest
            {
                ArtifactId = "a", UserId = "u", SessionId = "s", JobId = "j", Attempt = 1,
                RepositoryIdentity = await inspector.GetRepositoryIdentityAsync(root), WorktreePath = root,
                BaseCommit = baseline, HeadCommit = head, ScopeHash = "scope",
                ChangedPaths = ["source.txt"], CreatedAtUtc = DateTimeOffset.UtcNow
            };
            Task<string?> Validate(ForgeArtifactManifest value, IReadOnlyList<ForgeArtifactManifest>? parents = null) =>
                inspector.ValidateAsync(value, root, "u", "s", "j", 1, parents ?? []);
            Assert.Null(await Validate(manifest));
            Assert.Equal("RepositoryIdentityMismatch", await Validate(manifest with { RepositoryIdentity = "other" }));
            Assert.Equal("ArtifactOwnershipMismatch", await Validate(manifest with { Attempt = 0 }));
            Assert.Equal("ArtifactHeadChanged", await Validate(manifest with { HeadCommit = baseline }));
            Assert.Equal("ArtifactChangesMismatch", await Validate(manifest with { ChangedPaths = [] }));
            Assert.Equal("ArtifactParentMismatch", await Validate(manifest, [manifest with { ArtifactId = "missing-parent" }]));
            Assert.Equal("LegacyUnverified", await Validate(manifest with { BaseCommit = "HEAD" }));
            await File.WriteAllTextAsync(Path.Combine(root, "source.txt"), "raced");
            Assert.Equal("ArtifactDirty", await Validate(manifest));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task TryReadDeletedFilePreState_ReturnsTheRemovedText_ForRenamedAwayAndTransientFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "cf-prestate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await Git(root, "init");
            await Git(root, "config", "user.name", "Artifact Test");
            await Git(root, "config", "user.email", "test@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(root, "old.txt"), "v1 baseline");
            await File.WriteAllTextAsync(Path.Combine(root, "keep.txt"), "keep");
            await Git(root, "add", ".");
            await Git(root, "commit", "-m", "base");
            await File.WriteAllTextAsync(Path.Combine(root, "old.txt"), "v2 edited by an earlier task");
            await File.WriteAllTextAsync(Path.Combine(root, "tmp.txt"), "scratch added by an earlier task");
            await Git(root, "add", ".");
            await Git(root, "commit", "-m", "earlier task");
            await Git(root, "mv", "old.txt", "renamed.txt");
            await Git(root, "rm", "-q", "tmp.txt");
            await Git(root, "commit", "-m", "later task");
            var head = (await Git(root, "rev-parse", "HEAD")).Trim();
            var inspector = new GitArtifactInspector();

            // The removed text is what existed right before the deletion, not the original baseline.
            var renamedAway = await inspector.TryReadDeletedFilePreStateAsync(root, head, "old.txt");
            Assert.NotNull(renamedAway);
            Assert.Equal("v2 edited by an earlier task", renamedAway.Content);
            Assert.Equal(head, renamedAway.DeletedInCommit);

            // Added and removed inside the same range: absent from the baseline and from head, but its text is still recoverable.
            var transient = await inspector.TryReadDeletedFilePreStateAsync(root, head, "tmp.txt");
            Assert.NotNull(transient);
            Assert.Equal("scratch added by an earlier task", transient.Content);

            // No evidence (null) for anything that is not a plain deletion.
            Assert.Null(await inspector.TryReadDeletedFilePreStateAsync(root, head, "keep.txt"));        // still present
            Assert.Null(await inspector.TryReadDeletedFilePreStateAsync(root, head, "renamed.txt"));     // the new name exists
            Assert.Null(await inspector.TryReadDeletedFilePreStateAsync(root, head, "never-existed.txt"));
            Assert.Null(await inspector.TryReadDeletedFilePreStateAsync(root, head, "*.txt"));           // never a pattern
            Assert.Null(await inspector.TryReadDeletedFilePreStateAsync(root, head, ":(top)old.txt"));
            Assert.Null(await inspector.TryReadDeletedFilePreStateAsync(root, head, ""));
            Assert.Null(await inspector.TryReadDeletedFilePreStateAsync(root, "not-a-commit", "old.txt"));
            Assert.Null(await inspector.TryReadDeletedFilePreStateAsync(Path.Combine(root, "missing-dir"), head, "old.txt"));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task TryReadDeletedFilePreState_ReturnsNull_WhenTheFileWasRecreatedAfterBeingDeleted()
    {
        var root = Path.Combine(Path.GetTempPath(), "cf-prestate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await Git(root, "init");
            await Git(root, "config", "user.name", "Artifact Test");
            await Git(root, "config", "user.email", "test@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "first");
            await Git(root, "add", ".");
            await Git(root, "commit", "-m", "add");
            await Git(root, "rm", "-q", "a.txt");
            await Git(root, "commit", "-m", "delete");
            await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "second");
            await Git(root, "add", ".");
            await Git(root, "commit", "-m", "recreate");
            var head = (await Git(root, "rev-parse", "HEAD")).Trim();

            Assert.Null(await new GitArtifactInspector().TryReadDeletedFilePreStateAsync(root, head, "a.txt"));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }

    private static async Task<string> Git(string root, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }
}
