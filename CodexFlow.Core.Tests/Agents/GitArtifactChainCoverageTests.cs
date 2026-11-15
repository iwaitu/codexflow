// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using CodexFlow.Contracts;
using CodexFlow.Core.Services;
using Xunit;

namespace CodexFlow.Core.Tests.Agents;

/// <summary>
/// The model audit reads the final content of every path in <c>parents ∪ manifest</c> ChangedPaths. A change that landed between
/// two task artifacts therefore escapes review unless the validator proves the chain covers every path changed from the
/// chain's starting commit to the final HEAD.
/// </summary>
public sealed class GitArtifactChainCoverageTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cf-artifact-coverage-" + Guid.NewGuid().ToString("N"));
    private string _root = string.Empty;
    private readonly GitArtifactInspector _inspector = new();

    public async ValueTask InitializeAsync()
    {
        _root = Path.Combine(_directory, "repo");
        Directory.CreateDirectory(_root);
        await Git(_root, "init");
        await Git(_root, "config", "user.name", "Artifact Test");
        await Git(_root, "config", "user.email", "test@example.invalid");
        await Commit(_root, "base.txt", "base", "base");
    }

    public ValueTask DisposeAsync()
    {
        foreach (var file in Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_directory, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task LinearChain_CoveringEveryChangedPath_IsAccepted()
    {
        var baseline = await Head(_root);
        var first = await Commit(_root, "first.txt", "one", "first");
        var second = await Commit(_root, "second.txt", "two", "second");

        var (parent, final) = await Manifests(baseline, first, second);

        Assert.Null(await _inspector.ValidateAsync(final, _root, "u", "s", "second-job", 1, [parent]));
    }

    [Fact]
    public async Task CommitLandedBetweenTasks_ThatNoManifestCovers_IsRejected()
    {
        var baseline = await Head(_root);
        var first = await Commit(_root, "first.txt", "one", "first");
        // Not produced by any task artifact (manual commit, leftover of a failed job, ...): nobody reviews rogue.txt.
        var rogue = await Commit(_root, "rogue.txt", "unreviewed", "rogue");
        var second = await Commit(_root, "second.txt", "two", "second");

        var (parent, final) = await Manifests(baseline, first, second, secondBase: rogue);

        // Every per-artifact check passes on its own: the second diff (rogue..second) is exactly second.txt,
        // the first diff (baseline..first) is exactly first.txt, and first is an ancestor of the final head.
        Assert.Equal("ArtifactChainGap", await _inspector.ValidateAsync(final, _root, "u", "s", "second-job", 1, [parent]));
    }

    // A retry runs in the worktree the failed attempt left behind. Its manifest has to start where the TASK started: started at the failed
    // attempt's commit, it lists only the retry's own changes and the failed attempt's commit belongs to no artifact.
    [Fact]
    public async Task ARetrysManifest_StartingAtTheFailedAttemptsCommit_LeavesThatAttemptUncovered()
    {
        var baseline = await Head(_root);
        var first = await Commit(_root, "first.txt", "one", "first");
        var failedAttempt = await Commit(_root, "attempt1.txt", "written by the first attempt", "attempt 1");
        var retry = await Commit(_root, "retry.txt", "written by the retry", "retry");

        var (parent, final) = await Manifests(baseline, first, retry, secondBase: failedAttempt);
        final = final with { ChangedPaths = ["retry.txt"] };

        Assert.Equal("ArtifactChainGap", await _inspector.ValidateAsync(final, _root, "u", "s", "second-job", 1, [parent]));
    }

    [Fact]
    public async Task ARetrysManifest_StartingAtTheTasksBase_CoversEveryAttempt_AndIsAccepted()
    {
        var baseline = await Head(_root);
        var first = await Commit(_root, "first.txt", "one", "first");
        await Commit(_root, "attempt1.txt", "written by the first attempt", "attempt 1");
        var retry = await Commit(_root, "retry.txt", "written by the retry", "retry");

        var (parent, final) = await Manifests(baseline, first, retry, secondBase: first);
        final = final with { ChangedPaths = ["attempt1.txt", "retry.txt"] };

        Assert.Null(await _inspector.ValidateAsync(final, _root, "u", "s", "second-job", 1, [parent]));
    }

    [Fact]
    public async Task CommitBeforeTheFirstTaskBase_IsOutsideTheChainAndIgnored()
    {
        // History that predates the plan is not attributable to any task and must not fail verification.
        var preexisting = await Commit(_root, "preexisting.txt", "old", "preexisting");
        var first = await Commit(_root, "first.txt", "one", "first");
        var second = await Commit(_root, "second.txt", "two", "second");

        var (parent, final) = await Manifests(preexisting, first, second);

        Assert.Null(await _inspector.ValidateAsync(final, _root, "u", "s", "second-job", 1, [parent]));
    }

    [Fact]
    public async Task ChainWhoseBasesDoNotShareAnAncestorOrder_IsRejectedAsDiscontinuous()
    {
        var baseline = await Head(_root);
        await Git(_root, "checkout", "-b", "branch-a");
        var aBase = await Commit(_root, "a-base.txt", "a", "a-base");
        var aHead = await Commit(_root, "a-task.txt", "a-task", "a-task");
        await Git(_root, "checkout", baseline);
        await Git(_root, "checkout", "-b", "branch-b");
        var bBase = await Commit(_root, "b-base.txt", "b", "b-base");
        await Git(_root, "merge", aHead, "--no-edit");
        var final = (await Head(_root));

        var inspector = _inspector;
        var identity = await inspector.GetRepositoryIdentityAsync(_root);
        var parent = new ForgeArtifactManifest
        {
            ArtifactId = "first", UserId = "u", SessionId = "s", JobId = "first-job", TaskId = "first-task", Attempt = 1,
            BaseCommit = aBase, HeadCommit = aHead, RepositoryIdentity = identity, ScopeHash = "scope",
            WorktreePath = _root, ChangedPaths = ["a-task.txt"], CreatedAtUtc = DateTimeOffset.UtcNow
        };
        var second = parent with
        {
            ArtifactId = "second", JobId = "second-job", TaskId = "second-task",
            BaseCommit = bBase, HeadCommit = final, ParentArtifactIds = ["first"],
            ChangedPaths = (await Git(_root, "diff", "--no-renames", "--name-only", "-z", bBase, final, "--"))
                .Split('\0', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).ToArray()
        };

        Assert.Equal("ArtifactChainDiscontinuity", await inspector.ValidateAsync(second, _root, "u", "s", "second-job", 1, [parent]));
    }

    [Fact]
    public async Task FindChainBase_IsTheBaseThatPrecedesEveryOtherBase()
    {
        var baseline = await Head(_root);
        var first = await Commit(_root, "first.txt", "one", "first");
        var second = await Commit(_root, "second.txt", "two", "second");
        var (parent, final) = await Manifests(baseline, first, second);

        Assert.Equal(baseline, await _inspector.FindChainBaseAsync(_root, [parent, final]));
        Assert.Equal(baseline, await _inspector.FindChainBaseAsync(_root, [final, parent])); // order does not matter
        Assert.Equal(first, await _inspector.FindChainBaseAsync(_root, [final]));           // a single artifact is its own chain
    }

    [Fact]
    public async Task FindChainBase_IsNullWhenTheBasesAreNotOneLineage()
    {
        var baseline = await Head(_root);
        await Git(_root, "checkout", "-b", "branch-a");
        var a = await Commit(_root, "a.txt", "a", "a");
        await Git(_root, "checkout", baseline);
        await Git(_root, "checkout", "-b", "branch-b");
        var b = await Commit(_root, "b.txt", "b", "b");
        var (parent, final) = await Manifests(a, a, b);
        final = final with { BaseCommit = b };

        Assert.Null(await _inspector.FindChainBaseAsync(_root, [parent, final]));
    }

    private async Task<(ForgeArtifactManifest Parent, ForgeArtifactManifest Final)> Manifests(
        string baseline, string first, string second, string? secondBase = null)
    {
        var identity = await _inspector.GetRepositoryIdentityAsync(_root);
        var parent = new ForgeArtifactManifest
        {
            ArtifactId = "first", UserId = "u", SessionId = "s", JobId = "first-job", TaskId = "first-task", Attempt = 1,
            BaseCommit = baseline, HeadCommit = first, RepositoryIdentity = identity, ScopeHash = "scope",
            WorktreePath = _root, ChangedPaths = ["first.txt"], CreatedAtUtc = DateTimeOffset.UtcNow
        };
        var final = parent with
        {
            ArtifactId = "second", JobId = "second-job", TaskId = "second-task",
            BaseCommit = secondBase ?? first, HeadCommit = second, ParentArtifactIds = ["first"], ChangedPaths = ["second.txt"]
        };
        return (parent, final);
    }

    private static async Task<string> Commit(string repo, string file, string content, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(repo, file), content);
        await Git(repo, "add", ".");
        await Git(repo, "commit", "-m", message);
        return await Head(repo);
    }

    private static async Task<string> Head(string repo) => (await Git(repo, "rev-parse", "HEAD")).Trim();

    private static async Task<string> Git(string root, params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
        return output;
    }
}
