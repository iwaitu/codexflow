// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Execution;
using Xunit;

namespace CodexFlow.Core.Tests.Execution;

/// <summary>
/// Follow-ups of the independent physical-write review (W7, W8, L9, P11, A13, plus alias and shape checks) on top of the
/// reproduction tests in PhysicalWriteReviewRepro. OS-specific mechanisms skip with their reason; nothing "passes" because an
/// OS is unsupported.
/// </summary>
[Collection(SafePublishCollection.Name)]
public class PhysicalWriteHardeningTests : IDisposable
{
    private readonly string _root = IsolationTestSupport.NewTempDir("hard");

    public void Dispose()
    {
        SafeTreeWriter.TestHook = null;
        SafeTreeWriter.FaultInjector = null;
        IsolationTestSupport.Cleanup(_root);
    }

    private string P(string rel) => Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));

    private static byte[] B(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    private static SafeWriteOp Modify(string path, string current, string next) => new(path, GuestChangeKind.Modify, B(next), IsolationTestSupport.Sha(current));

    private static SafeWriteOp Delete(string path, string current) => new(path, GuestChangeKind.Delete, null, IsolationTestSupport.Sha(current));

    private void Write(string rel, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!);
        File.WriteAllText(P(rel), content);
    }

    // W7: a failed write AFTER the truncation must restore the original (Windows writes in place under an exclusive lock).
    [Fact]
    public void WriteFailingAfterTruncation_ShouldRestoreTheOriginalContent()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("in-place truncate+write exists only on Windows; Linux writes a temp file and exchanges it (see the L9 tests)");
        }

        Write("a.txt", "ORIGINAL");
        SafeTreeWriter.TestHook = stage =>
        {
            if (stage == "writing:a.txt")
            {
                throw new IOException("disk full (injected)");
            }
        };

        var res = SafeTreeWriter.Apply(_root, [Modify("a.txt", "ORIGINAL", "REPLACEMENT")]);

        Assert.False(res.Success);
        Assert.Equal("ORIGINAL", File.ReadAllText(P("a.txt")));
    }

    // W8: a delete flag already set on an earlier item must be cleared when a later delete fails.
    [Fact]
    public void FailedLaterDelete_ShouldNotLetAnEarlierFlaggedDeleteGoThrough()
    {
        Write("a.txt", "A");
        Write("b.txt", "B");
        SafeTreeWriter.TestHook = stage =>
        {
            // Windows: flags are set after everything is locked; Linux: b.txt is verified after a.txt was already moved aside.
            if (stage is "deleting:b.txt" or "verified:b.txt")
            {
                throw new IOException("delete failed (injected)");
            }
        };

        var res = SafeTreeWriter.Apply(_root, [Delete("a.txt", "A"), Delete("b.txt", "B")]);

        Assert.False(res.Success);
        Assert.Equal("A", File.ReadAllText(P("a.txt")));
        Assert.Equal("B", File.ReadAllText(P("b.txt")));
        Assert.Empty(Directory.EnumerateFiles(_root, ".cfx-*", SearchOption.AllDirectories));
    }

    // L9a: when the exchange-back of a Modify fails, the concurrent writer's file must be preserved, never unlinked.
    [Fact]
    public void FailedExchangeBack_ShouldPreserveTheConcurrentWritersFile()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("renameat2 exchange exists only on Linux");
        }

        Write("a.txt", "A");
        var exchanges = 0;
        SafeTreeWriter.TestHook = stage =>
        {
            if (stage == "verified:a.txt")
            {
                File.WriteAllText(P("a.txt"), "HOST-NEWER");   // lands in the exchange => hash mismatch => exchange back
            }
        };
        SafeTreeWriter.FaultInjector = call => call == "renameat2:2" && ++exchanges == 2;   // the exchange back fails

        var res = SafeTreeWriter.Apply(_root, [Modify("a.txt", "A", "A2")]);

        Assert.False(res.Success);
        Assert.Equal(ExecutionReasonCodes.ExportRejectedConcurrentModification, res.ReasonCode);
        var kept = Assert.Single(Directory.EnumerateFiles(_root, ".cfx-*", SearchOption.AllDirectories));
        Assert.Equal("HOST-NEWER", File.ReadAllText(kept));
        Assert.Contains(Path.GetFileName(kept), res.Message, StringComparison.Ordinal);
    }

    // L9b: when restoring a moved-aside delete target fails, the host's modified file must be preserved.
    [Fact]
    public void FailedRestoreOfADelete_ShouldPreserveTheHostsModifiedFile()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("move-aside delete exists only on Linux");
        }

        Write("a.txt", "A");
        SafeTreeWriter.TestHook = stage =>
        {
            if (stage == "verified:a.txt")
            {
                File.WriteAllText(P("a.txt"), "HOST-MODIFIED-AFTER-VERIFY");
            }
        };
        SafeTreeWriter.FaultInjector = call => call == "renameat2:1" && File.Exists(P("a.txt")) == false;   // restore (NOREPLACE) fails

        var res = SafeTreeWriter.Apply(_root, [Delete("a.txt", "A")]);

        Assert.False(res.Success);
        var kept = Assert.Single(Directory.EnumerateFiles(_root, ".cfx-*", SearchOption.AllDirectories));
        Assert.Equal("HOST-MODIFIED-AFTER-VERIFY", File.ReadAllText(kept));
    }

    // Alias: a path that is not the canonical workspace-relative form never reaches the file system.
    [Theory]
    [InlineData("../x.txt")]
    [InlineData("a/../b.txt")]
    [InlineData("/abs.txt")]
    [InlineData(".git/hooks/pre-commit")]
    [InlineData("a/.GIT/config")]
    [InlineData("a\\b.txt")]
    [InlineData("")]
    public void NonCanonicalOrProtectedPaths_ShouldBeRefusedByTheWriterItself(string path)
    {
        var res = SafeTreeWriter.Apply(_root, [new SafeWriteOp(path, GuestChangeKind.Add, B("x"), null)]);
        Assert.False(res.Success);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public void OperationShape_ShouldBeValidatedWithDistinctReasons()
    {
        Write("a.txt", "A");
        Assert.Equal(ExecutionReasonCodes.MissingExpectedHash, SafeTreeWriter.Apply(_root, [new SafeWriteOp("a.txt", GuestChangeKind.Modify, B("x"), null)]).ReasonCode);
        Assert.Equal(ExecutionReasonCodes.MissingExpectedHash, SafeTreeWriter.Apply(_root, [new SafeWriteOp("a.txt", GuestChangeKind.Delete, null, "not-a-hash")]).ReasonCode);
        Assert.Equal(ExecutionReasonCodes.InvalidOperation, SafeTreeWriter.Apply(_root, [new SafeWriteOp("n.txt", GuestChangeKind.Add, null, null)]).ReasonCode);
        Assert.Equal(ExecutionReasonCodes.InvalidOperation, SafeTreeWriter.Apply(_root, [new SafeWriteOp("n.txt", GuestChangeKind.Add, B("x"), IsolationTestSupport.Sha("A"))]).ReasonCode);
        Assert.Equal(ExecutionReasonCodes.InvalidOperation, SafeTreeWriter.Apply(_root, [new SafeWriteOp("a.txt", GuestChangeKind.Modify, null, IsolationTestSupport.Sha("A"))]).ReasonCode);
        Assert.Equal("A", File.ReadAllText(P("a.txt")));
    }

    [Fact]
    public void LowerCaseExpectedHash_ShouldStillMatch()
    {
        Write("a.txt", "A");
        var res = SafeTreeWriter.Apply(_root, [new SafeWriteOp("a.txt", GuestChangeKind.Modify, B("A2"), IsolationTestSupport.Sha("A").ToLowerInvariant())]);
        Assert.True(res.Success, res.Message);
        Assert.Equal("A2", File.ReadAllText(P("a.txt")));
    }

    // P11: the files are on disk, so an observer failure must not turn the export into a failure.
    [Fact]
    public async Task ObserverFailureAfterPublish_ShouldBeReportedAsAWarning_NotAsAFailedExport()
    {
        var staged = Path.Combine(_root, "staged.bin");
        File.WriteAllText(staged, "NEW");
        var sha = IsolationTestSupport.Sha(File.ReadAllBytes(staged));
        var ws = IsolationTestSupport.NewTempDir("obs");
        try
        {
            var res = await ScopedExportPublisher.PublishAsync(
                ws, [new GuestChange("n.txt", GuestChangeKind.Add, staged, sha, 3)], new Dictionary<string, BaselineEntry>(), new ThrowingObserver(), CancellationToken.None);

            Assert.True(res.Decision.IsAllowed);
            Assert.Contains("observer failed", res.Decision.Message, StringComparison.Ordinal);
            Assert.Equal("NEW", File.ReadAllText(Path.Combine(ws, "n.txt")));
            Assert.Single(res.Applied);
        }
        finally
        {
            IsolationTestSupport.Cleanup(ws);
        }
    }

    private sealed class ThrowingObserver : IWorkspaceExportObserver
    {
        public void OnPublished(string workspaceRoot, IReadOnlyList<string> changedRelativePaths) => throw new InvalidOperationException("snapshot cache offline");
    }

    // A13: the unguarded path-based writer overload must stay unreachable from production code.
    [Fact]
    public void NoProductionCodeShouldCallTheUnguardedAtomicWriterOverload()
    {
        var repo = FindRepositoryRoot();
        var allowed = new[] { "AtomicFileWriter.cs", "IHashlineInterfaces.cs", "HashlineFileService.cs", "HashlineServiceCollectionExtensions.cs" };
        var offenders = new List<string>();
        // The production projects that exist in this checkout (the standalone edition has no "CodexFlow" server project), the shared ones included.
        var productionProjects = new[] { "CodexFlow.Core", "CodexFlow.Orchestration", "CodexFlow.Gateway.Http", "CodexFlow.Storage.Sqlite", "CodexFlow.Local", "CodexFlow" }
            .Where(project => Directory.Exists(Path.Combine(repo, project)));
        foreach (var dir in productionProjects)
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(repo, dir), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(repo, file);
                if (relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    relative.Contains($"{Path.DirectorySeparatorChar}workspaces{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                var text = File.ReadAllText(file);
                if ((text.Contains("IAtomicFileWriter", StringComparison.Ordinal) || text.Contains("AtomicFileWriter", StringComparison.Ordinal)) &&
                    !allowed.Contains(Path.GetFileName(file), StringComparer.Ordinal))
                {
                    offenders.Add(relative);
                }
            }
        }

        Assert.True(offenders.Count == 0, "unexpected users of the atomic file writer: " + string.Join(", ", offenders));

        var service = File.ReadAllText(Path.Combine(repo, "CodexFlow.Core", "Hashline", "Services", "HashlineFileService.cs"));
        Assert.Contains("_fileWriter.WriteVerifiedAsync(", service, StringComparison.Ordinal);
        Assert.DoesNotContain("_fileWriter.WriteAsync(", service, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CodexFlow.Core", "CodexFlow.Core.csproj")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            Assert.Skip("the source tree is not available next to the test binaries in this environment (e.g. a container that only mounts the build output); the check ran on the dev host");
        }

        return dir.FullName;
    }
}
