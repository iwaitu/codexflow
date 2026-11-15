// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Execution;
using Xunit;

namespace CodexFlow.Core.Tests.Execution;

public class WorkspacePathTests
{
    [Theory]
    [InlineData("src/a.cs", true)]
    [InlineData("a.txt", true)]
    [InlineData("./src/a.cs", true)]
    [InlineData("../evil.cs", false)]
    [InlineData("src/../../evil.cs", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("C:/Windows/x", false)]
    [InlineData("c:evil", false)]
    [InlineData("src\\a.cs", false)]
    [InlineData("\\\\server\\share\\x", false)]
    [InlineData("a/b:stream", false)]
    [InlineData("a/CON", false)]
    [InlineData("a/nul.txt", false)]
    [InlineData("a/trailing.", false)]
    [InlineData("a/trailing ", false)]
    [InlineData("a//b", false)]
    [InlineData("", false)]
    public void TryNormalizeStrict_ShouldAcceptOnlyRelativeSafePaths(string raw, bool ok)
    {
        Assert.Equal(ok, WorkspacePath.TryNormalizeStrict(raw, out var normalized, out var reason));
        if (ok)
        {
            Assert.DoesNotContain("..", normalized, StringComparison.Ordinal);
        }
        else
        {
            Assert.False(string.IsNullOrEmpty(reason));
        }
    }

    [Theory]
    [InlineData("src/obj/x.dll", true)]
    [InlineData("bin/Debug/a.dll", true)]
    [InlineData("src/OBJ/x", true)]
    [InlineData("node_modules/p/i.js", true)]
    [InlineData("TestResults/a.trx", true)]
    [InlineData("src/objx/a.cs", false)]
    [InlineData("src/binary.cs", false)]
    public void IsGenerated_ShouldMatchWholeSegmentsOnly(string path, bool generated)
        => Assert.Equal(generated, WorkspacePath.IsGenerated(path));

    [Theory]
    [InlineData(".git/config", true)]
    [InlineData("a/.GIT/hooks/x", true)]
    [InlineData(".gitmodules", true)]
    [InlineData(".gitignore", false)]
    [InlineData("src/.github/x", false)]
    public void IsProtected_ShouldCoverGitMetadata(string path, bool prot)
        => Assert.Equal(prot, WorkspacePath.IsProtected(path));

    [Theory]
    [InlineData("**/*.trx", "TestResults/a/b.trx", true)]
    [InlineData("**/*.trx", "x.trx", true)]
    [InlineData("**/*.trx", "x.trx.exe", false)]
    [InlineData("TestResults/*.trx", "TestResults/a/b.trx", false)]
    public void Glob_ShouldMatchExpectedPaths(string glob, string path, bool expected)
        => Assert.Equal(expected, WorkspacePath.GlobToRegex(glob).IsMatch(path));
}

public class GuestTarAnalyzerTests : IDisposable
{
    private readonly string _staging = IsolationTestSupport.NewTempDir("stg");

    public void Dispose() => IsolationTestSupport.Cleanup(_staging);

    private static Dictionary<string, BaselineEntry> Baseline(params (string Path, string Content)[] files)
        => files.ToDictionary(f => f.Path, f => new BaselineEntry(IsolationTestSupport.Sha(f.Content), f.Content.Length), StringComparer.Ordinal);

    private GuestTarAnalysis Analyze(Action<IsolationTestSupport.TarBuilder> build, Dictionary<string, BaselineEntry> baseline, ExportScope scope, ExecutionResourceLimits? limits = null)
    {
        using var tar = IsolationTestSupport.GuestTar(build);
        return GuestTarAnalyzer.Analyze(tar, baseline, scope, limits ?? new ExecutionResourceLimits(), _staging);
    }

    [Fact]
    public void UnchangedWorkspace_ShouldProduceNoChangesOrRejections()
    {
        var r = Analyze(t => t.Dir("workspace").Dir("workspace/src").File("workspace/src/a.cs", "A"), Baseline(("src/a.cs", "A")), ExportScope.Empty);
        Assert.Empty(r.Changes);
        Assert.Empty(r.Rejections);
    }

    [Fact]
    public void InScopeAddModifyDelete_ShouldBeReportedWithHostComputedHashes()
    {
        var scope = ExportScope.From(["src/a.cs", "src/new.cs", "src/gone.cs"]);
        var r = Analyze(
            t => t.Dir("workspace").File("workspace/src/a.cs", "A2").File("workspace/src/new.cs", "N"),
            Baseline(("src/a.cs", "A"), ("src/gone.cs", "G")), scope);

        Assert.Empty(r.Rejections);
        Assert.Equal(GuestChangeKind.Modify, r.Changes.Single(c => c.Path == "src/a.cs").Kind);
        Assert.Equal(IsolationTestSupport.Sha("A2"), r.Changes.Single(c => c.Path == "src/a.cs").Sha256);
        Assert.Equal(GuestChangeKind.Add, r.Changes.Single(c => c.Path == "src/new.cs").Kind);
        Assert.Equal(GuestChangeKind.Delete, r.Changes.Single(c => c.Path == "src/gone.cs").Kind);
    }

    [Fact]
    public void OutOfScopeChange_ShouldBeRejected()
    {
        var r = Analyze(t => t.File("workspace/src/a.cs", "A2").File("workspace/other.cs", "x"), Baseline(("src/a.cs", "A")), ExportScope.From(["src/a.cs"]));
        var rej = Assert.Single(r.Rejections);
        Assert.Equal(ExecutionReasonCodes.ExportRejectedOutOfScope, rej.ReasonCode);
        Assert.Equal("other.cs", rej.Path);
    }

    [Fact]
    public void DirectoryScope_ShouldAllowFilesBelowButNotSiblingPrefix()
    {
        var scope = ExportScope.From(null, ["src/feature"]);
        var ok = Analyze(t => t.File("workspace/src/feature/a.cs", "x"), Baseline(), scope);
        Assert.Empty(ok.Rejections);
        var bad = Analyze(t => t.File("workspace/src/feature2/a.cs", "x"), Baseline(), scope);
        Assert.Single(bad.Rejections);
    }

    [Fact]
    public void GeneratedDirectories_ShouldBeIgnoredNotExported()
    {
        var r = Analyze(
            t => t.File("workspace/bin/Debug/a.dll", "bin").File("workspace/src/obj/b.dll", "obj").File("workspace/TestResults/r.trx", "trx").File("workspace/node_modules/x/i.js", "n"),
            Baseline(), ExportScope.Empty);
        Assert.Empty(r.Changes);
        Assert.Empty(r.Rejections);
        Assert.Equal(4, r.IgnoredGeneratedEntries);
    }

    [Fact]
    public void DisguisedGeneratedName_ShouldStillBeRejectedOutOfScope()
    {
        // "objx" is not a generated segment, so an out-of-scope source file there must not slip through.
        var r = Analyze(t => t.File("workspace/objx/evil.cs", "x"), Baseline(), ExportScope.From(["src/a.cs"]));
        Assert.Equal(ExecutionReasonCodes.ExportRejectedOutOfScope, Assert.Single(r.Rejections).ReasonCode);
    }

    [Theory]
    [InlineData("workspace/../escape.txt", ExecutionReasonCodes.ExportRejectedUnsafePath)]
    [InlineData("workspace/src/../../escape.txt", ExecutionReasonCodes.ExportRejectedUnsafePath)]
    [InlineData("/etc/passwd", ExecutionReasonCodes.ExportRejectedUnsafePath)]
    [InlineData("workspace/a\\b.txt", ExecutionReasonCodes.ExportRejectedUnsafePath)]
    [InlineData("workspace/a:b.txt", ExecutionReasonCodes.ExportRejectedUnsafePath)]
    [InlineData("workspace/aux", ExecutionReasonCodes.ExportRejectedUnsafePath)]
    [InlineData("workspace/.git/config", ExecutionReasonCodes.ExportRejectedProtectedPath)]
    [InlineData("workspace/sub/.git", ExecutionReasonCodes.ExportRejectedProtectedPath)]
    public void HostileNames_ShouldBeRejected(string name, string reason)
    {
        var r = Analyze(t => t.File(name, "x"), Baseline(), ExportScope.From(["escape.txt", "a\\b.txt", "a:b.txt", "aux", "sub/.git"], ["workspace", ".git"]));
        Assert.Contains(r.Rejections, x => x.ReasonCode == reason);
        Assert.Empty(r.Changes.Where(c => c.Path.Contains("..", StringComparison.Ordinal)));
    }

    [Fact]
    public void TraversalHiddenInsideGeneratedDirName_ShouldStillBeRejected()
    {
        var r = Analyze(t => t.File("workspace/obj/../../escape.txt", "x"), Baseline(), ExportScope.Empty);
        Assert.Equal(ExecutionReasonCodes.ExportRejectedUnsafePath, Assert.Single(r.Rejections).ReasonCode);
    }

    [Fact]
    public void LinksAndSpecialFiles_ShouldBeRejectedEvenInScope()
    {
        var scope = ExportScope.From(["l1", "l2", "p"]);
        var r = Analyze(t => t.Symlink("workspace/l1", "/etc/passwd").Hardlink("workspace/l2", "workspace/a.cs").Fifo("workspace/p"), Baseline(("a.cs", "A")), scope);
        Assert.Equal(2, r.Rejections.Count(x => x.ReasonCode == ExecutionReasonCodes.ExportRejectedLink));
        Assert.Single(r.Rejections, x => x.ReasonCode == ExecutionReasonCodes.ExportRejectedSpecialFile);
        Assert.Empty(r.Changes.Where(c => c.Kind != GuestChangeKind.Delete));
    }

    [Fact]
    public void LinkOnExistingTrackedFile_ShouldNotBeTreatedAsDeleteSilently()
    {
        // a tracked file replaced by a symlink: rejection (link) must be reported; nothing exportable.
        var r = Analyze(t => t.Symlink("workspace/a.cs", "/etc/shadow"), Baseline(("a.cs", "A")), ExportScope.From(["a.cs"]));
        Assert.Single(r.Rejections, x => x.ReasonCode == ExecutionReasonCodes.ExportRejectedLink);
    }

    [Fact]
    public void CaseCollisionWithBaselineOrWithinGuest_ShouldBeRejected()
    {
        var scope = ExportScope.From(["src/a.cs", "src/A.cs", "Dup.txt", "dup.txt"]);
        var changedCase = Analyze(t => t.File("workspace/src/A.cs", "x"), Baseline(("src/a.cs", "A")), scope);
        Assert.Contains(changedCase.Rejections, x => x.ReasonCode == ExecutionReasonCodes.ExportRejectedUnsafePath);

        var dup = Analyze(t => t.File("workspace/Dup.txt", "1").File("workspace/dup.txt", "2"), Baseline(), scope);
        Assert.Contains(dup.Rejections, x => x.ReasonCode == ExecutionReasonCodes.ExportRejectedUnsafePath);
    }

    [Fact]
    public void ChangedFileOverQuota_ShouldBeRejectedAsQuota()
    {
        var limits = new ExecutionResourceLimits { MaxExportBytes = 8 };
        var r = Analyze(t => t.File("workspace/big.txt", new string('x', 100)), Baseline(), ExportScope.From(["big.txt"]), limits);
        Assert.Equal(ExecutionReasonCodes.ExportRejectedQuota, Assert.Single(r.Rejections).ReasonCode);
    }

    [Fact]
    public void TooManyChangedFiles_ShouldBeRejectedAsQuota()
    {
        var limits = new ExecutionResourceLimits { MaxExportFiles = 2 };
        var r = Analyze(t => { for (var i = 0; i < 5; i++) t.File($"workspace/f{i}.txt", "x" + i); }, Baseline(), ExportScope.From(null, ["."]), limits);
        Assert.Contains(r.Rejections, x => x.ReasonCode == ExecutionReasonCodes.ExportRejectedQuota);
    }

    [Fact]
    public void EntryOutsideWorkspaceRoot_ShouldBeRejected()
    {
        var r = Analyze(t => t.File("etc/passwd", "x"), Baseline(), ExportScope.Empty);
        Assert.Equal(ExecutionReasonCodes.ExportRejectedUnsafePath, Assert.Single(r.Rejections).ReasonCode);
    }

    [Fact]
    public void ReadEvidence_ShouldReturnBoundedRegularMatchingFilesOnly()
    {
        using var tar = IsolationTestSupport.GuestTar(t => t
            .File("workspace/TestResults/a.trx", "<trx/>")
            .File("workspace/src/code.cs", "code")
            .Symlink("workspace/TestResults/l.trx", "/etc/passwd")
            .File("workspace/.git/x.trx", "git")
            .File("workspace/TestResults/huge.trx", new string('x', (int)GuestTarAnalyzer.MaxEvidenceFileBytes + 1)));
        var files = GuestTarAnalyzer.ReadEvidence(tar, ["**/*.trx"]);
        var f = Assert.Single(files);
        Assert.Equal("TestResults/a.trx", f.RelativePath);
        Assert.Equal(IsolationTestSupport.Sha("<trx/>"), f.Sha256);
    }
}
