// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodexFlow.Core.Tests.Services;

public sealed class GitServiceShadowWorktreeTests : IDisposable
{
    private readonly string _workspacePath;
    private readonly GitService _gitService;
    private const string TaskId = "TASK_BUGFIX_001";

    public GitServiceShadowWorktreeTests()
    {
        _workspacePath = Path.Combine(
            Path.GetTempPath(),
            "codexflow-shadow-" + Guid.NewGuid().ToString("N"),
            "nested",
            "workspace",
            "cleanapp-bug");
        _gitService = new GitService(NullLogger<GitService>.Instance);
    }

    [Fact]
    public async Task CommitAsync_DoesNotExecuteRepositoryHooks()
    {
        Directory.CreateDirectory(_workspacePath);
        Assert.True(await _gitService.InitRepositoryAsync(_workspacePath));
        var hook = Path.Combine(_workspacePath, ".git", "hooks", "pre-commit");
        await File.WriteAllTextAsync(hook, "#!/bin/sh\nprintf executed > hook-ran.txt\nexit 77\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await File.WriteAllTextAsync(Path.Combine(_workspacePath, "source.txt"), "safe commit");
        Assert.True(await _gitService.CommitAsync(_workspacePath, "commit without invoking project hooks"));
        Assert.False(File.Exists(Path.Combine(_workspacePath, "hook-ran.txt")));
    }

    [Fact]
    public async Task CommitAsync_UsesAgentIdentityWhenTheRepositoryHasNone()
    {
        Directory.CreateDirectory(_workspacePath);
        Assert.True(await _gitService.InitRepositoryAsync(_workspacePath));
        var config = Path.Combine(_workspacePath, ".git", "config");
        var original = File.ReadAllLines(config);
        File.WriteAllLines(config, original.Where(line => !line.TrimStart().StartsWith("name =", StringComparison.Ordinal) &&
                                                         !line.TrimStart().StartsWith("email =", StringComparison.Ordinal)));
        var withoutIdentity = File.ReadAllText(config);
        var before = await _gitService.GetHeadHashAsync(_workspacePath);

        await File.WriteAllTextAsync(Path.Combine(_workspacePath, "candidate.txt"), "candidate");
        Assert.True(await _gitService.CommitAsync(_workspacePath, "publish scoped candidate"));
        Assert.NotEqual(before, await _gitService.GetHeadHashAsync(_workspacePath));
        Assert.Equal(withoutIdentity, File.ReadAllText(config));
    }

    [Fact]
    public async Task CreateShadowWorktreeAsync_UsesShortExternalShadowRoot_ForDeepWorkspace()
    {
        Directory.CreateDirectory(_workspacePath);
        Assert.True(await _gitService.InitRepositoryAsync(_workspacePath));

        var deepDirectory = Path.Combine(
            _workspacePath,
            "tech",
            new string('a', 36),
            new string('b', 36),
            new string('c', 36));
        Directory.CreateDirectory(deepDirectory);

        var deepFile = Path.Combine(
            deepDirectory,
            "very-long-source-file-name-" + new string('d', 32) + ".txt");
        await File.WriteAllTextAsync(deepFile, "shadow worktree path regression");
        Assert.True(await _gitService.CommitAsync(_workspacePath, "add deep file"));

        var shadowPath = await _gitService.CreateShadowWorktreeAsync(_workspacePath, TaskId, "main");

        Assert.False(string.IsNullOrWhiteSpace(shadowPath));
        Assert.True(Directory.Exists(shadowPath));
        Assert.Contains($"{Path.DirectorySeparatorChar}shadows{Path.DirectorySeparatorChar}", shadowPath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            $"{Path.DirectorySeparatorChar}workspace{Path.DirectorySeparatorChar}shadows{Path.DirectorySeparatorChar}",
            shadowPath,
            StringComparison.OrdinalIgnoreCase);

        if (OperatingSystem.IsWindows())
        {
            var expectedRoot = Path.Combine(Path.GetPathRoot(Path.GetFullPath(_workspacePath))!, "cfw", "shadows");
            Assert.StartsWith(expectedRoot, shadowPath, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            var expectedRoot = Path.Combine(Path.GetTempPath(), "codexflow-shadow-root", "shadows");
            Assert.StartsWith(expectedRoot, shadowPath, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task CreateShadowWorktreeAsync_UsesExplicitCommitRef_WhenProvided()
    {
        const string taskId = "TASK_BASE_REF";
        Directory.CreateDirectory(_workspacePath);
        Assert.True(await _gitService.InitRepositoryAsync(_workspacePath));

        var markerFile = Path.Combine(_workspacePath, "marker.txt");
        await File.WriteAllTextAsync(markerFile, "first");
        Assert.True(await _gitService.CommitAsync(_workspacePath, "first commit"));
        var firstCommit = await _gitService.GetHeadHashAsync(_workspacePath);
        Assert.False(string.IsNullOrWhiteSpace(firstCommit));

        await File.WriteAllTextAsync(markerFile, "second");
        Assert.True(await _gitService.CommitAsync(_workspacePath, "second commit"));

        string? shadowPath = null;
        try
        {
            shadowPath = await _gitService.CreateShadowWorktreeAsync(_workspacePath, taskId, firstCommit!);

            Assert.False(string.IsNullOrWhiteSpace(shadowPath));
            var shadowMarker = Path.Combine(shadowPath!, "marker.txt");
            Assert.True(File.Exists(shadowMarker));
            Assert.Equal("first", await File.ReadAllTextAsync(shadowMarker));
        }
        finally
        {
            await _gitService.RemoveShadowWorktreeAsync(_workspacePath, taskId);
        }
    }

    [Theory]
    [InlineData(128, "fatal: detected dubious ownership in repository at 'D:/codeup/repo'\r\nTo add an exception", true)]
    [InlineData(128, "fatal: unsafe repository ('/repo' is owned by someone else)", true)]
    [InlineData(128, "fatal: invalid reference: refs/heads/missing", false)]
    [InlineData(0, "fatal: detected dubious ownership in repository at '/repo'", false)]
    public void IsRepositoryOwnershipError_RecognizesOnlyGitOwnershipFailures(int exitCode, string stderr, bool expected)
    {
        Assert.Equal(expected, GitService.IsRepositoryOwnershipError(exitCode, stderr));
    }

    [Fact]
    public async Task CreateShadowWorktreeWithDiagnosticsAsync_KeepsMissingBaselineDistinctFromOwnership()
    {
        Assert.True(await _gitService.InitRepositoryAsync(_workspacePath));
        var result = await ((IShadowWorktreeCreationDiagnostics)_gitService)
            .CreateShadowWorktreeWithDiagnosticsAsync(_workspacePath, TaskId, "refs/heads/missing-baseline");

        Assert.Null(result.Path);
        Assert.Equal(ShadowWorktreeCreationFailure.GitError, result.Failure);
    }

    [Fact]
    public async Task RestoreFilesAsync_RemovesOutOfScopeUntrackedFileAndEmptyParentDirectories()
    {
        Directory.CreateDirectory(_workspacePath);
        Assert.True(await _gitService.InitRepositoryAsync(_workspacePath));

        var trackedFile = Path.Combine(_workspacePath, "README.md");
        await File.WriteAllTextAsync(trackedFile, "baseline");
        Assert.True(await _gitService.CommitAsync(_workspacePath, "baseline"));
        var baseCommit = await _gitService.GetHeadHashAsync(_workspacePath);
        Assert.False(string.IsNullOrWhiteSpace(baseCommit));

        var outOfScopeFile = Path.Combine(_workspacePath, "tmp", "generated", "leak.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(outOfScopeFile)!);
        await File.WriteAllTextAsync(outOfScopeFile, "should be removed");

        var restored = await _gitService.RestoreFilesAsync(_workspacePath, baseCommit!, ["tmp/generated/leak.txt"]);

        Assert.True(restored);
        Assert.False(File.Exists(outOfScopeFile));
        Assert.False(Directory.Exists(Path.Combine(_workspacePath, "tmp", "generated")));
        Assert.False(Directory.Exists(Path.Combine(_workspacePath, "tmp")));
    }

    public void Dispose()
    {
        try
        {
            _gitService.RemoveShadowWorktreeAsync(_workspacePath, TaskId).GetAwaiter().GetResult();
        }
        catch
        {
            // best effort cleanup
        }

        try
        {
            if (Directory.Exists(_workspacePath))
            {
                Directory.Delete(_workspacePath, true);
            }
        }
        catch
        {
            // best effort cleanup
        }
    }
}
