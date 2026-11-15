// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.Execution;

/// <summary>
/// Unified diffs (apply_patch / ivilson_smart_patch) used to run host `git apply` against the worktree. Inside a trusted
/// execution context they are applied in a private copy and published through the identity-checked all-or-nothing writer.
/// A strict mock of IGitService proves host git never touches the worktree in that mode.
/// </summary>
[Collection(SafePublishCollection.Name)]
public class SafeUnifiedPatchTests : IDisposable
{
    private const string A = "line1\nline2\nline3\n";
    private readonly string _root = IsolationTestSupport.NewTempDir("patchws");
    private readonly string _outside = IsolationTestSupport.NewTempDir("patchout");
    private readonly TrustedExecutionContextAccessor _accessor = new();
    private readonly IDisposable _scope;
    private readonly Mock<IGitService> _git = new(MockBehavior.Strict);

    public SafeUnifiedPatchTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "a.txt"), A);
        File.WriteAllText(Path.Combine(_root, "src", "b.txt"), "b1\nb2\n");
        _scope = _accessor.Begin(IsolationTestSupport.Context(_root, ExportScope.Empty));
    }

    public void Dispose()
    {
        SafeTreeWriter.TestHook = null;
        _scope.Dispose();
        IsolationTestSupport.Cleanup(_root);
        IsolationTestSupport.Cleanup(_outside);
    }

    private string P(string rel) => Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));

    private SmartPatchTool Smart() => new(_git.Object, NullLogger<SmartPatchTool>.Instance);

    private ApplyPatchTool Apply() => new(_git.Object, null, NullLogger<ApplyPatchTool>.Instance);

    private Dictionary<string, object?> Args(string patch) => new()
    {
        ["workspace_path"] = _root, ["patch_content"] = patch, ["patch"] = patch, ["reason"] = "test"
    };

    private const string ModifyA = """
        diff --git a/src/a.txt b/src/a.txt
        --- a/src/a.txt
        +++ b/src/a.txt
        @@ -1,3 +1,3 @@
         line1
        -line2
        +LINE2
         line3

        """;

    [Fact]
    public async Task SmartPatch_ShouldApplyInAPrivateCopy_AndNeverCallHostGit()
    {
        var result = await Smart().ExecuteAsync(Args(ModifyA));
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Equal("line1\nLINE2\nline3\n", File.ReadAllText(P("src/a.txt")).Replace("\r\n", "\n"));
        _git.VerifyNoOtherCalls();
        Assert.Empty(Directory.EnumerateFiles(_root, "*.orig", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(_root, ".cfx-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ApplyPatch_UnifiedDiff_ShouldAddModifyDeleteAndRename_AsOneBatch()
    {
        const string patch = """
            diff --git a/src/a.txt b/src/a.txt
            --- a/src/a.txt
            +++ b/src/a.txt
            @@ -1,3 +1,3 @@
             line1
            -line2
            +LINE2
             line3
            diff --git a/src/new.txt b/src/new.txt
            new file mode 100644
            --- /dev/null
            +++ b/src/new.txt
            @@ -0,0 +1,2 @@
            +n1
            +n2
            diff --git a/src/b.txt b/src/b.txt
            deleted file mode 100644
            --- a/src/b.txt
            +++ /dev/null
            @@ -1,2 +0,0 @@
            -b1
            -b2

            """;
        var result = await Apply().ExecuteAsync(Args(patch));

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("LINE2", File.ReadAllText(P("src/a.txt")), StringComparison.Ordinal);
        Assert.Equal("n1\nn2\n", File.ReadAllText(P("src/new.txt")).Replace("\r\n", "\n"));
        Assert.False(File.Exists(P("src/b.txt")));
        _git.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ConflictingPatch_ShouldChangeNothing_EvenWhenAnEarlierFileWouldApply()
    {
        const string patch = """
            diff --git a/src/a.txt b/src/a.txt
            --- a/src/a.txt
            +++ b/src/a.txt
            @@ -1,3 +1,3 @@
             line1
            -line2
            +LINE2
             line3
            diff --git a/src/b.txt b/src/b.txt
            --- a/src/b.txt
            +++ b/src/b.txt
            @@ -1,2 +1,2 @@
             b1
            -DOES-NOT-MATCH
            +x

            """;
        var result = await Smart().ExecuteAsync(Args(patch));

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Equal(A, File.ReadAllText(P("src/a.txt")).Replace("\r\n", "\n"));
        Assert.Equal("b1\nb2\n", File.ReadAllText(P("src/b.txt")).Replace("\r\n", "\n"));
        _git.VerifyNoOtherCalls();   // no `git reset --hard` either: nothing was changed, nothing to revert
    }

    [Theory]
    [InlineData("--- a/../outside.txt\n+++ b/../outside.txt\n@@ -0,0 +1 @@\n+pwn\n")]
    [InlineData("diff --git a/.git/config b/.git/config\n--- a/.git/config\n+++ b/.git/config\n@@ -0,0 +1 @@\n+x\n")]
    [InlineData("diff --git a/src/x.txt b/src/x.txt\nnew file mode 120000\n--- /dev/null\n+++ b/src/x.txt\n@@ -0,0 +1 @@\n+/etc/passwd\n")]
    [InlineData("diff --git a/src/x.bin b/src/x.bin\nGIT binary patch\nliteral 0\nHcmV?d00001\n")]
    [InlineData("--- \"a/src/q uoted.txt\"\n+++ \"b/src/q uoted.txt\"\n@@ -0,0 +1 @@\n+x\n")]
    [InlineData("--- /abs/path.txt\n+++ /abs/path.txt\n@@ -0,0 +1 @@\n+x\n")]
    public async Task HostilePatches_ShouldBeRefusedBeforeAnythingIsCopiedOrWritten(string hostile)
    {
        var patch = hostile.Replace("\\n", "\n");
        var result = await Smart().ExecuteAsync(Args(hostile.Replace("\n", "\n")));
        _ = patch;
        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "outside.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_outside));
        Assert.Equal(A, File.ReadAllText(P("src/a.txt")).Replace("\r\n", "\n"));
        Assert.False(File.Exists(P("src/x.txt")));
        _git.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PathCollector_ShouldRefuseUnsafeHeaders()
    {
        Assert.False(SafeUnifiedPatchApplier.TryCollectPaths("--- a/../x\n+++ b/../x\n", out _, out var e1), e1);
        Assert.False(SafeUnifiedPatchApplier.TryCollectPaths("rename from a.txt\nrename to ../b.txt\n", out _, out _));
        Assert.False(SafeUnifiedPatchApplier.TryCollectPaths("diff --git a/x b/y\nold mode 120000\n", out _, out _));
        Assert.True(SafeUnifiedPatchApplier.TryCollectPaths("diff --git a/src/a.txt b/src/a.txt\n--- a/src/a.txt\n+++ b/src/a.txt\n", out var ok, out _));
        Assert.Equal(["src/a.txt"], ok.ToArray());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task PatchRacingWithAHostWriter_ShouldNeverOverwriteTheNewerVersion()
    {
        var attackerWrote = false;
        SafeTreeWriter.TestHook = _ =>
        {
            try
            {
                File.WriteAllText(P("src/a.txt"), "// newer host version\n");
                attackerWrote = true;
            }
            catch (IOException)
            {
            }
        };

        var result = await Smart().ExecuteAsync(Args(ModifyA));

        Assert.Equal(OperatingSystem.IsWindows(), result.Status == ToolResultStatus.Success);   // Windows excludes the writer, Linux detects and restores
        if (result.Status == ToolResultStatus.Success)
        {
            Assert.False(attackerWrote);
            Assert.Contains("LINE2", File.ReadAllText(P("src/a.txt")), StringComparison.Ordinal);
        }
        else
        {
            Assert.True(attackerWrote);
            Assert.Equal("// newer host version\n", File.ReadAllText(P("src/a.txt")));
        }

        _git.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PatchTargetBehindALinkedDirectory_ShouldBeRefused()
    {
        File.WriteAllText(Path.Combine(_outside, "victim.txt"), "VICTIM\n");
        var linked = P("linked");
        var made = false;
        try
        {
            Directory.CreateSymbolicLink(linked, _outside);
            made = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (OperatingSystem.IsWindows())
            {
                using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linked}\" \"{_outside}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
                p!.WaitForExit();
                made = p.ExitCode == 0;
            }
        }

        Assert.True(made, "could not create a directory link on this host");
        const string patch = """
            diff --git a/linked/victim.txt b/linked/victim.txt
            --- a/linked/victim.txt
            +++ b/linked/victim.txt
            @@ -1 +1 @@
            -VICTIM
            +PWNED

            """;
        var result = await Smart().ExecuteAsync(Args(patch));
        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Equal("VICTIM\n", File.ReadAllText(Path.Combine(_outside, "victim.txt")).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task WithoutATrustedContext_LibraryUseKeepsTheLegacyHostGitPath()
    {
        _scope.Dispose();
        _git.Setup(g => g.CommitAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        _git.Setup(g => g.ApplyPatchAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        var result = await Smart().ExecuteAsync(Args(ModifyA));
        Assert.Equal(ToolResultStatus.Success, result.Status);
        _git.Verify(g => g.ApplyPatchAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }
}
