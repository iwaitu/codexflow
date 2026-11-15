// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Formats.Tar;
using CodexFlow.Core.Execution;
using Xunit;

namespace CodexFlow.Core.Tests.Execution;

[Collection(SafePublishCollection.Name)]
public class ScopedExportPublisherTests : IDisposable
{
    private readonly string _root = IsolationTestSupport.NewTempDir("pub");
    private readonly string _stage = IsolationTestSupport.NewTempDir("pubstage");

    public void Dispose()
    {
        IsolationTestSupport.Cleanup(_root);
        IsolationTestSupport.Cleanup(_stage);
    }

    private GuestChange Staged(string path, GuestChangeKind kind, string? content)
    {
        string? file = null;
        string? sha = null;
        if (content is not null)
        {
            file = Path.Combine(_stage, Guid.NewGuid().ToString("N") + ".bin");
            File.WriteAllText(file, content);
            sha = IsolationTestSupport.Sha(File.ReadAllBytes(file));
        }

        return new GuestChange(path, kind, file, sha, content?.Length ?? 0);
    }

    private static Dictionary<string, BaselineEntry> Base(string path, string content)
        => new() { [path] = new BaselineEntry(IsolationTestSupport.Sha(content), content.Length) };

    private sealed class Recorder : IWorkspaceExportObserver
    {
        public List<string> Paths { get; } = [];

        public void OnPublished(string workspaceRoot, IReadOnlyList<string> changedRelativePaths) => Paths.AddRange(changedRelativePaths);
    }

    [Fact]
    public async Task Publish_ShouldApplyAddModifyDelete_AndNotifyObserver()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "A");
        File.WriteAllText(Path.Combine(_root, "gone.txt"), "G");
        var baseline = Base("a.txt", "A");
        baseline["gone.txt"] = new BaselineEntry(IsolationTestSupport.Sha("G"), 1);
        var rec = new Recorder();

        var res = await ScopedExportPublisher.PublishAsync(_root,
            [Staged("a.txt", GuestChangeKind.Modify, "A2"), Staged("sub/new.txt", GuestChangeKind.Add, "N"), Staged("gone.txt", GuestChangeKind.Delete, null)],
            baseline, rec, CancellationToken.None);

        Assert.True(res.Decision.IsAllowed, res.Decision.Message);
        Assert.Equal("A2", File.ReadAllText(Path.Combine(_root, "a.txt")));
        Assert.Equal("N", File.ReadAllText(Path.Combine(_root, "sub", "new.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "gone.txt")));
        Assert.Equal(3, rec.Paths.Count);
        Assert.Empty(Directory.EnumerateFiles(_root, ".cfx-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Publish_ShouldRefuseAll_WhenHostFileChangedSinceSnapshot()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "A");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "B");
        var baseline = Base("a.txt", "A");
        baseline["b.txt"] = new BaselineEntry(IsolationTestSupport.Sha("B"), 1);
        File.WriteAllText(Path.Combine(_root, "b.txt"), "B-HOST-EDIT");

        var res = await ScopedExportPublisher.PublishAsync(_root,
            [Staged("a.txt", GuestChangeKind.Modify, "A2"), Staged("b.txt", GuestChangeKind.Modify, "B2")], baseline, null, CancellationToken.None);

        Assert.False(res.Decision.IsAllowed);
        Assert.Contains(res.Rejected, r => r.ReasonCode == ExecutionReasonCodes.ExportRejectedConcurrentModification && r.Path == "b.txt");
        Assert.Equal("A", File.ReadAllText(Path.Combine(_root, "a.txt")));
        Assert.Equal("B-HOST-EDIT", File.ReadAllText(Path.Combine(_root, "b.txt")));
    }

    [Fact]
    public async Task Publish_ShouldRefuse_WhenAddTargetAppearedOnHost()
    {
        File.WriteAllText(Path.Combine(_root, "new.txt"), "HOST");
        var res = await ScopedExportPublisher.PublishAsync(_root, [Staged("new.txt", GuestChangeKind.Add, "GUEST")], new Dictionary<string, BaselineEntry>(), null, CancellationToken.None);
        Assert.False(res.Decision.IsAllowed);
        Assert.Equal("HOST", File.ReadAllText(Path.Combine(_root, "new.txt")));
    }

    [Fact]
    public async Task Publish_ShouldRefuse_WhenStagedContentNoLongerMatchesHash()
    {
        var change = Staged("a.txt", GuestChangeKind.Add, "ok");
        File.WriteAllText(change.StagedFile!, "tampered");
        var res = await ScopedExportPublisher.PublishAsync(_root, [change], new Dictionary<string, BaselineEntry>(), null, CancellationToken.None);
        Assert.False(res.Decision.IsAllowed);
        Assert.False(File.Exists(Path.Combine(_root, "a.txt")));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("a/../../escape.txt")]
    [InlineData("/abs.txt")]
    [InlineData(".git/config")]
    public async Task Publish_ShouldRefuseUnsafeOrProtectedTargets(string path)
    {
        var res = await ScopedExportPublisher.PublishAsync(_root, [Staged(path, GuestChangeKind.Add, "x")], new Dictionary<string, BaselineEntry>(), null, CancellationToken.None);
        Assert.False(res.Decision.IsAllowed);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "escape.txt")));
    }

    [Fact]
    public async Task Publish_ShouldRefuse_WhenParentChainContainsALink()
    {
        var outside = IsolationTestSupport.NewTempDir("outside");
        try
        {
            var link = Path.Combine(_root, "linked");
            if (!TryCreateDirectoryLink(link, outside))
            {
                // The platform refused link creation (no privilege). The assertion below would be vacuous.
                Assert.Fail("Could not create a directory symlink/junction on this host; link-chain protection is untested here.");
            }

            var res = await ScopedExportPublisher.PublishAsync(_root, [Staged("linked/pwn.txt", GuestChangeKind.Add, "x")], new Dictionary<string, BaselineEntry>(), null, CancellationToken.None);
            Assert.False(res.Decision.IsAllowed);
            Assert.Contains(res.Rejected, r => r.ReasonCode == ExecutionReasonCodes.ExportRejectedLink);
            Assert.False(File.Exists(Path.Combine(outside, "pwn.txt")));
        }
        finally
        {
            IsolationTestSupport.Cleanup(outside);
        }
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
            p!.WaitForExit();
            return p.ExitCode == 0;
        }
    }
}

public class SnapshotBuilderTests : IDisposable
{
    private readonly string _root = IsolationTestSupport.NewTempDir("snap");
    private readonly string _out = IsolationTestSupport.NewTempDir("snapout");

    public void Dispose()
    {
        IsolationTestSupport.Cleanup(_root);
        IsolationTestSupport.Cleanup(_out);
    }

    private static List<TarEntry> Read(string tar)
    {
        var list = new List<TarEntry>();
        using var fs = File.OpenRead(tar);
        using var r = new TarReader(fs);
        TarEntry? e;
        while ((e = r.GetNextEntry(copyData: true)) is not null)
        {
            list.Add(e);
        }

        return list;
    }

    [Fact]
    public void Snapshot_ShouldExcludeGitPointerCredentialsGeneratedAndLinks_AndSetGuestOwner()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        Directory.CreateDirectory(Path.Combine(_root, "obj"));
        File.WriteAllText(Path.Combine(_root, "src", "a.cs"), "class A{}");
        File.WriteAllText(Path.Combine(_root, ".git"), "gitdir: C:/host/secret/.git/worktrees/x");
        File.WriteAllText(Path.Combine(_root, ".env"), "SECRET=1");
        File.WriteAllText(Path.Combine(_root, "bin", "x.dll"), "bin");
        File.WriteAllText(Path.Combine(_root, "obj", "y.dll"), "obj");
        var outside = IsolationTestSupport.NewTempDir("secretdir");
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "TOPSECRET");
        var linked = false;
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_root, "link"), outside);
            linked = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // fall back to a junction on Windows
            if (OperatingSystem.IsWindows())
            {
                using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(_root, "link")}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
                p!.WaitForExit();
                linked = p.ExitCode == 0;
            }
        }

        Assert.True(linked, "could not create a link to test link exclusion");

        var snap = WorkspaceSnapshotBuilder.Build(_root, Path.Combine(_out, "s.tar"), new ExecutionResourceLimits());
        IsolationTestSupport.Cleanup(outside);

        var entries = Read(snap.TarPath);
        var names = entries.Select(e => e.Name).ToList();
        Assert.Contains("workspace/src/a.cs", names);
        Assert.DoesNotContain(names, n => n.Contains(".git", StringComparison.Ordinal) || n.Contains(".env", StringComparison.Ordinal) ||
                                           n.Contains("/bin", StringComparison.Ordinal) || n.Contains("/obj", StringComparison.Ordinal) ||
                                           n.Contains("link", StringComparison.Ordinal) || n.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.All(entries, e => Assert.Equal(WorkspaceSnapshotBuilder.GuestUid, e.Uid));
        Assert.Equal(1, snap.SkippedLinks);
        Assert.Equal(["src/a.cs"], snap.Baseline.Keys.ToArray());
    }

    [Fact]
    public void Snapshot_ShouldRefuseOverQuota()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), new string('x', 100));
        var ex = Assert.Throws<WorkspaceSnapshotException>(() =>
            WorkspaceSnapshotBuilder.Build(_root, Path.Combine(_out, "s.tar"), new ExecutionResourceLimits { MaxWorkspaceBytes = 10 }));
        Assert.Equal(ExecutionReasonCodes.WorkspaceTooLarge, ex.ReasonCode);
    }
}

public class PinnedCommitSnapshotTests : IDisposable
{
    private readonly string _repo = IsolationTestSupport.NewTempDir("git");
    private readonly string _out = IsolationTestSupport.NewTempDir("gitout");

    public void Dispose()
    {
        IsolationTestSupport.Cleanup(_repo);
        IsolationTestSupport.Cleanup(_out);
    }

    private string Git(string args, string? stdin = null)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = _repo, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin is not null, UseShellExecute = false
        };
        psi.Environment["GIT_AUTHOR_NAME"] = "t";
        psi.Environment["GIT_AUTHOR_EMAIL"] = "t@t";
        psi.Environment["GIT_COMMITTER_NAME"] = "t";
        psi.Environment["GIT_COMMITTER_EMAIL"] = "t@t";
        using var p = Process.Start(psi)!;
        if (stdin is not null)
        {
            p.StandardInput.Write(stdin);
            p.StandardInput.Close();
        }

        var o = p.StandardOutput.ReadToEnd();
        var e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {args}: {e}");
        return o.Trim();
    }

    private string Init()
    {
        Git("init -q");
        Git("config core.autocrlf false");
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "committed-A\n");
        Directory.CreateDirectory(Path.Combine(_repo, "src"));
        File.WriteAllText(Path.Combine(_repo, "src", "b.cs"), "class B{}\n");
        File.WriteAllText(Path.Combine(_repo, ".gitattributes"), "a.txt export-ignore\nsrc/b.cs export-subst\n");
        Git("add -A");
        Git("commit -q -m c1");
        return Git("rev-parse HEAD");
    }

    private static Dictionary<string, string> ReadTar(string tar)
    {
        var map = new Dictionary<string, string>();
        using var fs = File.OpenRead(tar);
        using var r = new TarReader(fs);
        TarEntry? e;
        while ((e = r.GetNextEntry(copyData: true)) is not null)
        {
            if (e.EntryType == TarEntryType.RegularFile)
            {
                using var sr = new StreamReader(e.DataStream!);
                map[e.Name] = sr.ReadToEnd();
            }
        }

        return map;
    }

    [Fact]
    public void Pinned_ShouldUseCommittedObjects_NotWorktree_IgnoringExportAttributes()
    {
        var sha = Init();
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "WORKTREE-EDIT\n");
        File.WriteAllText(Path.Combine(_repo, "untracked.txt"), "untracked");

        var snap = PinnedCommitSnapshotBuilder.Build(_repo, sha, Path.Combine(_out, "p.tar"), new ExecutionResourceLimits());
        var files = ReadTar(snap.TarPath);

        Assert.Equal("committed-A\n", files["workspace/a.txt"]);        // export-ignore does not drop it
        Assert.Equal("class B{}\n", files["workspace/src/b.cs"]);        // export-subst does not rewrite it
        Assert.DoesNotContain("workspace/untracked.txt", files.Keys);
        Assert.Equal(IsolationTestSupport.Sha("committed-A\n"), snap.Baseline["a.txt"].Sha256);
    }

    [Fact]
    public void Pinned_ShouldIgnoreReplaceRefs()
    {
        var sha = Init();
        var evilBlob = Git("hash-object -w --stdin", "EVIL\n");
        var origBlob = Git("rev-parse HEAD:a.txt");
        Git($"replace {origBlob} {evilBlob}");

        var snap = PinnedCommitSnapshotBuilder.Build(_repo, sha, Path.Combine(_out, "p.tar"), new ExecutionResourceLimits());
        Assert.Equal("committed-A\n", ReadTar(snap.TarPath)["workspace/a.txt"]);
    }

    [Fact]
    public void Pinned_ShouldRejectSymlinkAndSubmoduleEntries()
    {
        var sha = Init();
        var blob = Git("hash-object -w --stdin", "/etc/passwd");
        Git($"update-index --add --cacheinfo 120000,{blob},evil-link");
        var tree = Git("write-tree");
        var linkCommit = Git($"commit-tree {tree} -p {sha} -m link");
        var ex = Assert.Throws<WorkspaceSnapshotException>(() =>
            PinnedCommitSnapshotBuilder.Build(_repo, linkCommit, Path.Combine(_out, "l.tar"), new ExecutionResourceLimits()));
        Assert.Equal(ExecutionReasonCodes.ExportRejectedLink, ex.ReasonCode);
    }

    [Theory]
    [InlineData("HEAD")]
    [InlineData("master")]
    [InlineData("abc123")]
    [InlineData("")]
    public void Pinned_ShouldRequireFullObjectId(string notAnId)
    {
        Init();
        Assert.Throws<WorkspaceSnapshotException>(() =>
            PinnedCommitSnapshotBuilder.Build(_repo, notAnId, Path.Combine(_out, "x.tar"), new ExecutionResourceLimits()));
    }

    [Fact]
    public void Pinned_ShouldRefuseUnknownCommit_AndQuota()
    {
        var sha = Init();
        Assert.Throws<WorkspaceSnapshotException>(() =>
            PinnedCommitSnapshotBuilder.Build(_repo, new string('0', 40), Path.Combine(_out, "u.tar"), new ExecutionResourceLimits()));
        var ex = Assert.Throws<WorkspaceSnapshotException>(() =>
            PinnedCommitSnapshotBuilder.Build(_repo, sha, Path.Combine(_out, "q.tar"), new ExecutionResourceLimits { MaxWorkspaceBytes = 3 }));
        Assert.Equal(ExecutionReasonCodes.WorkspaceTooLarge, ex.ReasonCode);
    }
}
