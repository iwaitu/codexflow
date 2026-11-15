// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using CodexFlow.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodexFlow.Core.Tests.Services;

/// <summary>
/// Real-git checks for <see cref="GitService.DiscardUncommittedChangesAsync"/>: it must drop exactly what a dead worker could have
/// left behind (edits, new files, new directories) and nothing that is committed or deliberately ignored.
/// </summary>
public sealed class GitDiscardUncommittedChangesTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "cf-discard-" + Guid.NewGuid().ToString("N"));
    private readonly GitService _git = new(NullLogger<GitService>.Instance);

    public GitDiscardUncommittedChangesTests() => Directory.CreateDirectory(_repo);

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
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
        var start = new ProcessStartInfo("git") { WorkingDirectory = _repo, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "-c", "core.autocrlf=false", "-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false" }.Concat(args))
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
        return stdout.Trim();
    }

    private string Path_(string relative) => Path.Combine(_repo, relative.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public async Task Discard_DropsEditsAndUntrackedFilesAndDirectories_ButKeepsCommitsAndIgnoredFiles()
    {
        Git("init", "-q");
        File.WriteAllText(Path_(".gitignore"), "*.log\n");
        File.WriteAllText(Path_("a.txt"), "v1");
        Directory.CreateDirectory(Path_("src"));
        File.WriteAllText(Path_("src/committed.cs"), "class Committed {}");
        Git("add", "-A");
        Git("commit", "-q", "-m", "base");
        var head = Git("rev-parse", "HEAD");

        // What a worker killed mid-write leaves behind.
        File.WriteAllText(Path_("a.txt"), "dirty edit");
        File.WriteAllText(Path_("src/committed.cs"), "class Committed { void Run(");
        File.WriteAllText(Path_("src/Half.cs"), "class Half {");
        Directory.CreateDirectory(Path_("newdir/nested"));
        File.WriteAllText(Path_("newdir/nested/x.cs"), "partial");
        File.WriteAllText(Path_("build.log"), "ignored output");
        Assert.True(await _git.HasUncommittedChangesAsync(_repo));

        Assert.True(await _git.DiscardUncommittedChangesAsync(_repo));

        Assert.False(await _git.HasUncommittedChangesAsync(_repo));
        Assert.Equal("v1", File.ReadAllText(Path_("a.txt")));
        Assert.Equal("class Committed {}", File.ReadAllText(Path_("src/committed.cs")));
        Assert.False(File.Exists(Path_("src/Half.cs")));
        Assert.False(Directory.Exists(Path_("newdir")));
        Assert.True(File.Exists(Path_("build.log"))); // ignored files are not residue
        Assert.Equal(head, Git("rev-parse", "HEAD")); // history is untouched
    }

    [Fact]
    public async Task Discard_OnACleanRepository_IsANoOp()
    {
        Git("init", "-q");
        File.WriteAllText(Path_("a.txt"), "v1");
        Git("add", "-A");
        Git("commit", "-q", "-m", "base");

        Assert.True(await _git.DiscardUncommittedChangesAsync(_repo));

        Assert.Equal("v1", File.ReadAllText(Path_("a.txt")));
    }

    [Fact]
    public async Task Discard_OutsideARepository_ReportsFailureInsteadOfPretendingToClean()
    {
        var notARepo = Path.Combine(Path.GetTempPath(), "cf-not-a-repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(notARepo);
        try
        {
            File.WriteAllText(Path.Combine(notARepo, "keep.txt"), "must survive");

            Assert.False(await _git.DiscardUncommittedChangesAsync(notARepo));

            Assert.True(File.Exists(Path.Combine(notARepo, "keep.txt")));
        }
        finally
        {
            Directory.Delete(notARepo, recursive: true);
        }
    }
}
