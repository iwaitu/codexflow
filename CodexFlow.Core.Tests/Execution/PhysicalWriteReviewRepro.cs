// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

// REVIEW REPRO (not compiled into the repo): copy to CodexFlow.Core.Tests/Execution/ to run.
// Every test asserts the SAFE behavior; a failing test is a confirmed defect (see claude-physical-write-independent-review.md).
// Tests that cannot apply on the current OS call Assert.Skip with the reason - they never "pass" for an unsupported OS.
using CodexFlow.Core.Execution;
using Xunit;

namespace CodexFlow.Core.Tests.Execution;

[Collection(SafePublishCollection.Name)]
public class PhysicalWriteReviewRepro : IDisposable
{
    private readonly string _root = IsolationTestSupport.NewTempDir("review");

    public void Dispose()
    {
        SafeTreeWriter.TestHook = null;
        IsolationTestSupport.Cleanup(_root);
    }

    private string P(string rel) => Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
    private static byte[] B(string s) => System.Text.Encoding.UTF8.GetBytes(s);
    private static SafeWriteOp Add(string path, string content) => new(path, GuestChangeKind.Add, B(content), null);
    private static SafeWriteOp Modify(string path, string current, string next) => new(path, GuestChangeKind.Modify, B(next), IsolationTestSupport.Sha(current));
    private void Write(string rel, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!);
        File.WriteAllText(P(rel), content);
    }

    private IEnumerable<string> TempLeftovers() => Directory.EnumerateFiles(_root, ".cfx-*", SearchOption.AllDirectories);

    // R1 (HIGH, both OSes): an exception type outside {SafeWriteAbort, IOException, UnauthorizedAccess} skips rollback.
    [Fact]
    public void UnexpectedExceptionMidBatch_ShouldRollBackEverythingAlreadyApplied()
    {
        Write("b.txt", "B");
        SafeTreeWriter.TestHook = stage =>
        {
            if (stage is "verified:b.txt" or "verified:*")
            {
                throw new InvalidOperationException("injected non-IO failure");
            }
        };

        try { SafeTreeWriter.Apply(_root, [Add("new/dir/a.txt", "A"), Modify("b.txt", "B", "B2")]); } catch (InvalidOperationException) { }

        Assert.False(File.Exists(P("new/dir/a.txt")), "an Add that was applied/created before the failure must be rolled back");
        Assert.False(Directory.Exists(P("new")), "directories created by the failed batch must be removed");
        Assert.Equal("B", File.ReadAllText(P("b.txt")));
        Assert.Empty(TempLeftovers());
    }

    // R2 (MEDIUM, Windows): createdDirs is collected but never removed on rollback.
    [Fact]
    public void FailedBatch_ShouldNotLeaveDirectoriesItCreated()
    {
        Write("b.txt", "B-CHANGED-ON-HOST");
        var res = SafeTreeWriter.Apply(_root, [Add("new/deep/n.txt", "N"), Modify("b.txt", "B", "B2")]);
        Assert.False(res.Success);
        Assert.False(Directory.Exists(P("new")), "failed batch left 'new/deep' behind");
    }

    [Fact]
    public void ModifyOfFileWhoseParentIsMissing_ShouldFailWithoutCreatingTheParent()
    {
        var res = SafeTreeWriter.Apply(_root, [Modify("ghost/x.txt", "X", "Y")]);
        Assert.False(res.Success);
        Assert.False(Directory.Exists(P("ghost")), "a failed Modify created an empty directory");
    }

    // R3 (MEDIUM, Linux): rollback that finds a concurrently changed file leaves the displaced baseline as .cfx-* in the worktree.
    [Fact]
    public void RollbackThatFindsAConcurrentChange_ShouldNotLeaveAnOrphanTempFile()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("exchange-based rollback exists only on Linux; Windows rollback uses in-memory backups (see R1/R2)");
        }

        Write("a.txt", "A");
        Write("b.txt", "B");
        SafeTreeWriter.TestHook = stage =>
        {
            if (stage == "verified:b.txt")   // a.txt is already exchanged; b.txt is verified but not yet replaced
            {
                File.WriteAllText(P("a.txt"), "HOST-WROTE-AFTER-OUR-EXCHANGE");
                File.WriteAllText(P("b.txt"), "B-HOST");   // makes the exchange of b.txt detect a conflict -> batch rollback
            }
        };

        var res = SafeTreeWriter.Apply(_root, [Modify("a.txt", "A", "A2"), Modify("b.txt", "B", "B2")]);

        Assert.False(res.Success);
        Assert.Equal("B-HOST", File.ReadAllText(P("b.txt")));
        Assert.Equal("HOST-WROTE-AFTER-OUR-EXCHANGE", File.ReadAllText(P("a.txt")));   // left as is (documented)
        Assert.Empty(TempLeftovers());   // today: the old baseline of a.txt stays behind as .cfx-<guid> and could be committed
    }

    // R4 (MEDIUM, Linux): cached directory fds keep writing into a directory the host has since renamed away.
    [Fact]
    public void DirectoryRenamedBetweenOps_ShouldNotSilentlyWriteIntoTheMovedDirectory()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Windows pins the directory (rename is refused), so this window does not exist there");
        }

        Write("src/a.txt", "A");
        var moved = false;
        SafeTreeWriter.TestHook = stage =>
        {
            if (stage == "verified:src/b.txt" && !moved)
            {
                moved = true;
                Directory.Move(P("src"), P("src_moved"));
                Directory.CreateDirectory(P("src"));
            }
        };

        var res = SafeTreeWriter.Apply(_root, [Modify("src/a.txt", "A", "A2"), Add("src/b.txt", "B")]);

        Assert.True(moved);
        // Either the batch fails (and nothing is half applied) or the file is where the path says it is.
        Assert.True(!res.Success || File.Exists(P("src/b.txt")), "success was reported but src/b.txt does not exist under 'src' (it went into src_moved)");
    }

    // R5 (HIGH on Windows with 8.3 names enabled): the export path (ScopedExportPublisher) validates path STRINGS only:
    // TryNormalizeStrict + IsProtected(".git") accept "GIT~1/..." which Windows resolves to the real .git directory.
    // (WorkspaceFileWriter is not affected because Path.GetFullPath expands existing short names before the check.)
    [Fact]
    public void ExportOfShortNameOfGitDirectory_ShouldBeRefusedAsProtectedPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("8.3 short names exist only on Windows");
        }

        Directory.CreateDirectory(P(".git/hooks"));
        File.WriteAllText(P(".git/config"), "x");
        if (!Directory.Exists(P("GIT~1")))
        {
            Assert.Skip("8.3 short names are disabled on this volume; the bypass cannot be demonstrated here");
        }

        // Exactly what ScopedExportPublisher.Publish does for a guest-supplied path, then the safe writer.
        const string guestPath = "GIT~1/hooks/pre-commit";
        var accepted = WorkspacePath.TryNormalizeStrict(guestPath, out var norm, out _) && !WorkspacePath.IsProtected(norm);
        var applied = accepted && SafeTreeWriter.Apply(_root, [Add(norm, "#!/bin/sh echo pwned")]).Success;

        Assert.False(File.Exists(P(".git/hooks/pre-commit")), "a guest-controlled path wrote a git hook through the 8.3 alias of .git");
        Assert.False(applied);
    }

    // R6 (LOW): a missing expected hash fails closed but with the misleading 'concurrent modification' reason.
    [Fact]
    public void MissingExpectedHash_ShouldBeRefusedWithADistinctReason()
    {
        Write("a.txt", "A");
        foreach (var kind in new[] { GuestChangeKind.Modify, GuestChangeKind.Delete })
        {
            var op = new SafeWriteOp("a.txt", kind, kind == GuestChangeKind.Delete ? null : B("A2"), null);
            var res = SafeTreeWriter.Apply(_root, [op]);
            Assert.False(res.Success, kind + " without an expected hash must never apply");
            Assert.Equal("A", File.ReadAllText(P("a.txt")));
            Assert.NotEqual(ExecutionReasonCodes.ExportRejectedConcurrentModification, res.ReasonCode);
        }
    }
}
