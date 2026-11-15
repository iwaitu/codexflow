// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Execution;
using CodexFlow.Core.Hashline.Infrastructure;
using CodexFlow.Core.Hashline.Models;
using CodexFlow.Core.Hashline.Services;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CodexFlow.Core.Tests.Execution;

/// <summary>
/// P0 regression + last-write-point attacks. A job runs in a shadow worktree (trusted context root); the model (or an
/// injected argument) still names the MAIN repository. Every structured read/write tool must treat the trusted root as the
/// physical boundary, and every final write must check the identity of the version it read at the moment of writing.
/// </summary>
[Collection(SafePublishCollection.Name)]
public class WorkspaceBoundaryTests : IDisposable
{
    private const string Original = "public class PriceCalculator { int Rate = 1; }\n";
    private readonly string _main = IsolationTestSupport.NewTempDir("main");
    private readonly string _shadow = IsolationTestSupport.NewTempDir("shadow");
    private readonly string _outside = IsolationTestSupport.NewTempDir("elsewhere");
    private readonly TrustedExecutionContextAccessor _accessor = new();
    private readonly IDisposable _scope;
    private readonly IHashlineFileService _hashline;
    private readonly HashlineOptions _options;

    public WorkspaceBoundaryTests()
    {
        foreach (var root in new[] { _main, _shadow })
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "PriceCalculator.cs"), Original);
        }

        // The configuration even lists the main repository as an allowed root: the trusted context must still win.
        _options = new HashlineOptions { MaxFileSizeBytes = 1024 * 1024, MaxLineCount = 10000, EnableAuditLogging = false, AllowedRoots = [_main] };
        var normalizer = new TextNormalizer();
        var hasher = new Sha256LineHasher();
        var fingerprint = new Sha256FingerprintProvider();
        var guard = new FileSystemGuard();
        var detector = new EncodingDetector();
        var audit = new CodexFlow.Core.Tests.Hashline.NullAuditLogger();
        var reader = new SnapshotReader(normalizer, hasher, fingerprint, guard, detector, audit, _options, NullLogger<SnapshotReader>.Instance);
        _hashline = new HashlineFileService(
            reader, new EditRequestValidator(_options), new EditApplier(), new DiffRenderer(),
            new AtomicFileWriter(NullLogger<AtomicFileWriter>.Instance), fingerprint, audit, normalizer, _options,
            NullLogger<HashlineFileService>.Instance);

        _scope = _accessor.Begin(IsolationTestSupport.Context(_shadow, ExportScope.From(["src/PriceCalculator.cs"])));
    }

    public void Dispose()
    {
        SafeTreeWriter.TestHook = null;
        _scope.Dispose();
        IsolationTestSupport.Cleanup(_main);
        IsolationTestSupport.Cleanup(_shadow);
        IsolationTestSupport.Cleanup(_outside);
    }

    private string Main(string rel = "src/PriceCalculator.cs") => Path.Combine(_main, rel.Replace('/', Path.DirectorySeparatorChar));

    private string Shadow(string rel = "src/PriceCalculator.cs") => Path.Combine(_shadow, rel.Replace('/', Path.DirectorySeparatorChar));

    private static void AssertRefused(CodexToolResult r) => Assert.True(r.Status == ToolResultStatus.Failed, "expected a refusal but got: " + r.Output);

    private void AssertMainUntouched() => Assert.Equal(Original, File.ReadAllText(Main()));

    private HashlineWriteTool HsWrite() => new(
        new ApplyPatchTool(new Mock<IGitService>().Object, _hashline, NullLogger<ApplyPatchTool>.Instance), _hashline);

    private Dictionary<string, object?> ModelArgs(params (string Key, object? Value)[] extra)
    {
        // What the model / an injected argument supplies: the MAIN repository as workspace and project root.
        var args = new Dictionary<string, object?> { ["workspace_path"] = _main, ["project_root"] = _main };
        foreach (var (k, v) in extra)
        {
            args[k] = v;
        }

        return args;
    }

    [Fact]
    public void ResolveBaseRoot_ShouldIgnoreModelRoots_InsideATrustedContext()
    {
        Assert.Equal(Path.GetFullPath(_shadow), ToolPathResolver.ResolveBaseRoot(_main, _main));
        Assert.Equal(Path.GetFullPath(_shadow), ToolPathResolver.ResolveBaseRoot(_main, null));
        Assert.Equal(Path.GetFullPath(_shadow), ToolPathResolver.ResolveProjectRoot(_main, _main, (Uri?)null));
        var nested = Directory.CreateDirectory(Path.Combine(_shadow, "src")).FullName;
        Assert.Equal(nested, ToolPathResolver.ResolveBaseRoot(_main, nested));           // narrower root inside the trusted one is fine
        Assert.Equal(Path.GetFullPath(_shadow), ToolPathResolver.ResolveBaseRoot(_main, _outside));
    }

    [Fact]
    public async Task HsWrite_WithTheMainRepoAsWorkspaceAndAbsoluteMainPath_ShouldNeverWriteTheMainRepo()
    {
        var result = await HsWrite().ExecuteAsync(ModelArgs(("filePath", Main()), ("oldString", "Rate = 1"), ("newString", "Rate = 2")));

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        AssertMainUntouched();
        Assert.Equal(Original, File.ReadAllText(Shadow()));
    }

    [Fact]
    public async Task HsWrite_WithTheMainRepoAsWorkspaceAndARelativePath_ShouldChangeTheShadowOnly()
    {
        var result = await HsWrite().ExecuteAsync(ModelArgs(("filePath", "src/PriceCalculator.cs"), ("oldString", "Rate = 1"), ("newString", "Rate = 2")));

        Assert.Equal(ToolResultStatus.Success, result.Status);
        AssertMainUntouched();
        Assert.Contains("Rate = 2", File.ReadAllText(Shadow()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HsWrite_SnapshotOperationWithAnAbsoluteMainPath_ShouldBeRefused()
    {
        var snapshot = await _hashline.ReadAsync(Shadow(), _shadow);
        var tool = HsWrite();
        var result = await tool.ExecuteAsync(ModelArgs(
            ("filePath", Main()),
            ("snapshotId", snapshot.SnapshotId),
            ("fileFingerprint", snapshot.FileFingerprint),
            ("operations", new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "replace_range", ["startLine"] = 1, ["startAnchorId"] = snapshot.Lines[0].AnchorId,
                    ["endLine"] = 1, ["endAnchorId"] = snapshot.Lines[0].AnchorId, ["newLines"] = new[] { "// pwned" }
                }
            })));

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        AssertMainUntouched();
        Assert.Equal(Original, File.ReadAllText(Shadow()));
    }

    [Fact]
    public async Task HashlineService_EditWithAnAbsoluteMainPath_ShouldBeRefusedEvenIfConfigurationListsTheMainRoot()
    {
        var snapshot = await _hashline.ReadAsync(Shadow(), _shadow);
        var request = new HashlineEditRequest
        {
            FilePath = Main(),
            SnapshotId = snapshot.SnapshotId,
            FileFingerprint = snapshot.FileFingerprint,
            Operations = [new ReplaceRangeOperation { Type = "replace_range", StartLine = 1, StartAnchorId = snapshot.Lines[0].AnchorId, EndLine = 1, EndAnchorId = snapshot.Lines[0].AnchorId, NewLines = ["// pwned"] }]
        };

        var result = await _hashline.EditAsync(request, _main);

        Assert.False(result.Success);
        AssertMainUntouched();
    }

    [Fact]
    public async Task StructuredWriteTools_ShouldAllLandInTheShadow_WhateverRootTheModelNames()
    {
        var write = new WriteFileTool(NullLogger<WriteFileTool>.Instance, null);
        var edit = new EditFileTool(NullLogger<EditFileTool>.Instance, null);
        var delete = new DeleteFileTool(NullLogger<DeleteFileTool>.Instance, null);
        var mkdir = new CreateDirectoryTool(NullLogger<CreateDirectoryTool>.Instance);

        Assert.Equal(ToolResultStatus.Success, (await write.ExecuteAsync(ModelArgs(("path", "src/New.cs"), ("content", "class New{}")))).Status);
        Assert.Equal(ToolResultStatus.Success, (await edit.ExecuteAsync(ModelArgs(("path", "src/PriceCalculator.cs"), ("old_string", "Rate = 1"), ("new_string", "Rate = 3")))).Status);
        Assert.Equal(ToolResultStatus.Success, (await mkdir.ExecuteAsync(ModelArgs(("path", "gen/deep/er")))).Status);
        Assert.Equal(ToolResultStatus.Success, (await delete.ExecuteAsync(ModelArgs(("path", "src/New.cs")))).Status);

        Assert.True(Directory.Exists(Shadow("gen/deep/er")));
        Assert.False(File.Exists(Shadow("src/New.cs")));
        Assert.Contains("Rate = 3", File.ReadAllText(Shadow()), StringComparison.Ordinal);
        Assert.False(File.Exists(Main("src/New.cs")));
        Assert.False(Directory.Exists(Main("gen")));
        AssertMainUntouched();
    }

    [Fact]
    public async Task StructuredWriteTools_ShouldRefuseAbsolutePathsOutsideTheTrustedRoot()
    {
        var write = new WriteFileTool(NullLogger<WriteFileTool>.Instance, null);
        var edit = new EditFileTool(NullLogger<EditFileTool>.Instance, null);
        var delete = new DeleteFileTool(NullLogger<DeleteFileTool>.Instance, null);

        // note: write tools treat a single leading slash as workspace-relative; drive/UNC style absolute paths must fail
        var abs = Main();
        AssertRefused(await write.ExecuteAsync(ModelArgs(("path", abs), ("content", "pwn"))));
        AssertRefused(await edit.ExecuteAsync(ModelArgs(("path", abs), ("old_string", "Rate = 1"), ("new_string", "Rate = 9"))));
        AssertRefused(await delete.ExecuteAsync(ModelArgs(("path", abs))));
        AssertMainUntouched();
    }

    [Fact]
    public async Task ApplyPatch_CodexSections_ShouldWriteTheShadowOnly()
    {
        var tool = new ApplyPatchTool(new Mock<IGitService>().Object, _hashline, NullLogger<ApplyPatchTool>.Instance);
        var patch = "*** Begin Patch\n*** Update File: src/PriceCalculator.cs\n@@\n-public class PriceCalculator { int Rate = 1; }\n+public class PriceCalculator { int Rate = 5; }\n*** Add File: src/Added.cs\n+class Added{}\n*** End Patch\n";
        var result = await tool.ExecuteAsync(ModelArgs(("patch", patch)));

        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Contains("Rate = 5", File.ReadAllText(Shadow()), StringComparison.Ordinal);
        Assert.True(File.Exists(Shadow("src/Added.cs")));
        Assert.False(File.Exists(Main("src/Added.cs")));
        AssertMainUntouched();

        // an absolute path inside the patch that names the main repo is refused, and "Add File" never replaces an existing file
        var escape = "*** Begin Patch\n*** Add File: " + Main("src/Evil.cs").Replace((char)92, '/') + "\n+evil\n*** End Patch\n";
        var refused = await tool.ExecuteAsync(ModelArgs(("patch", escape)));
        Assert.False(File.Exists(Main("src/Evil.cs")));
        Assert.NotEqual(ToolResultStatus.Success, refused.Status);

        var again = "*** Begin Patch\n*** Add File: src/Added.cs\n+replaced\n*** End Patch\n";
        var replaced = await tool.ExecuteAsync(ModelArgs(("patch", again)));
        Assert.NotEqual(ToolResultStatus.Success, replaced.Status);
        Assert.Equal("class Added{}", File.ReadAllText(Shadow("src/Added.cs")).Trim());
    }

    private void AssertNoStrayFiles()
    {
        foreach (var root in new[] { _main, _shadow }.Where(Directory.Exists))
        {
            var stray = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(root, f))
                .Where(f => f.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) || f.Contains(".tmp", StringComparison.OrdinalIgnoreCase) ||
                            f.Contains(".cfx-", StringComparison.Ordinal) || f.Contains(".cfw-", StringComparison.Ordinal) || f.EndsWith(".orig", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.True(stray.Count == 0, "unexpected files next to the sources: " + string.Join(", ", stray));
        }
    }

    [Fact]
    public async Task HsWrite_ShouldLeaveNoBackupOrTempFilesNextToTheSource()
    {
        var result = await HsWrite().ExecuteAsync(ModelArgs(("filePath", "src/PriceCalculator.cs"), ("oldString", "Rate = 1"), ("newString", "Rate = 2")));
        Assert.Equal(ToolResultStatus.Success, result.Status);
        AssertNoStrayFiles();

        var snapshot = await _hashline.ReadAsync(Shadow(), _shadow);
        var edit = await _hashline.EditAsync(new HashlineEditRequest
        {
            FilePath = Shadow(), SnapshotId = snapshot.SnapshotId, FileFingerprint = snapshot.FileFingerprint,
            Operations = [new ReplaceRangeOperation { Type = "replace_range", StartLine = 1, StartAnchorId = snapshot.Lines[0].AnchorId, EndLine = 1, EndAnchorId = snapshot.Lines[0].AnchorId, NewLines = ["// again"] }]
        }, _shadow);
        Assert.True(edit.Success, edit.ErrorMessage ?? "edit failed");
        AssertNoStrayFiles();
    }

    [Fact]
    public async Task LegacyAtomicWriter_ShouldBeRefusedInsideATrustedContext_AndNeverCreateABakFileOutsideOne()
    {
        var writer = new AtomicFileWriter(NullLogger<AtomicFileWriter>.Instance);

        // inside the trusted context the unguarded (root-less, version-less) overload is not callable
        var refused = await Assert.ThrowsAsync<HashlineException>(
            () => writer.WriteAsync(Shadow(), "class Hijacked {}", "utf-8", false, ((char)10).ToString(), true));
        Assert.Contains("WriteVerifiedAsync", refused.Message, StringComparison.Ordinal);
        Assert.Equal(Original, File.ReadAllText(Shadow()));
        AssertMainUntouched();

        // plain library use (no ambient context) keeps working, and never leaves a .bak next to the source
        _scope.Dispose();
        await writer.WriteAsync(Shadow(), "class Changed {}", "utf-8", false, ((char)10).ToString(), true);
        Assert.Contains("Changed", File.ReadAllText(Shadow()), StringComparison.Ordinal);
        AssertNoStrayFiles();
    }

    [Fact]
    public async Task TrustedRootDeleted_ShouldRefuseEverything_AndNeverFallBackToTheMainRepo()
    {
        // The shadow worktree disappears while the trusted context is still bound; the main repository still exists.
        Directory.Delete(_shadow, recursive: true);

        Assert.Equal(string.Empty, ToolPathResolver.ResolveBaseRoot(_main, _main));
        Assert.Equal(string.Empty, ToolPathResolver.ResolveBaseRoot(_main, null));
        Assert.Equal(string.Empty, ToolPathResolver.ResolveProjectRoot(_main, _main, (Uri?)null));
        Assert.True(ToolPathResolver.HasAmbientTrustedContext);
        Assert.Null(ToolPathResolver.TrustedWorkspaceRoot);
        Assert.Empty(SnapshotReader.ComputeEffectiveAllowedRoots(_options, _main));   // config AllowedRoots=[main] must not take over

        AssertRefused(await new WriteFileTool(NullLogger<WriteFileTool>.Instance, null).ExecuteAsync(ModelArgs(("path", "src/PriceCalculator.cs"), ("content", "pwn"))));
        AssertRefused(await new EditFileTool(NullLogger<EditFileTool>.Instance, null).ExecuteAsync(ModelArgs(("path", "src/PriceCalculator.cs"), ("old_string", "Rate = 1"), ("new_string", "Rate = 9"))));
        AssertRefused(await new DeleteFileTool(NullLogger<DeleteFileTool>.Instance, null).ExecuteAsync(ModelArgs(("path", "src/PriceCalculator.cs"))));
        AssertRefused(await new CreateDirectoryTool(NullLogger<CreateDirectoryTool>.Instance).ExecuteAsync(ModelArgs(("path", "gen"))));
        AssertRefused(await HsWrite().ExecuteAsync(ModelArgs(("filePath", "src/PriceCalculator.cs"), ("oldString", "Rate = 1"), ("newString", "Rate = 2"))));
        AssertRefused(await HsWrite().ExecuteAsync(ModelArgs(("filePath", Main()), ("oldString", "Rate = 1"), ("newString", "Rate = 2"))));

        await Assert.ThrowsAsync<HashlineException>(() => _hashline.ReadAsync(Main(), _main));
        AssertMainUntouched();
        Assert.False(Directory.Exists(Main("gen")));
        AssertNoStrayFiles();
    }

    [Fact]
    public async Task AbsoluteMainRepoPath_ShouldBeRefusedByEveryWriteTool_NotReRootedIntoTheShadow()
    {
        var abs = Main("src/PriceCalculator.cs").Replace((char)92, '/');
        var write = await new WriteFileTool(NullLogger<WriteFileTool>.Instance, null).ExecuteAsync(ModelArgs(("path", abs), ("content", "pwn")));
        var edit = await new EditFileTool(NullLogger<EditFileTool>.Instance, null).ExecuteAsync(ModelArgs(("path", abs), ("old_string", "Rate = 1"), ("new_string", "Rate = 9")));
        var del = await new DeleteFileTool(NullLogger<DeleteFileTool>.Instance, null).ExecuteAsync(ModelArgs(("path", abs)));
        var mkdir = await new CreateDirectoryTool(NullLogger<CreateDirectoryTool>.Instance).ExecuteAsync(ModelArgs(("path", Main("gen").Replace((char)92, '/'))));

        foreach (var r in new[] { write, edit, del, mkdir })
        {
            AssertRefused(r);
        }

        AssertMainUntouched();
        Assert.Equal(Original, File.ReadAllText(Shadow()));
        Assert.False(Directory.Exists(Path.Combine(_shadow, "tmp")), "the absolute path must not be re-rooted under the shadow");
        AssertNoStrayFiles();
    }

    // ───────────── last write point: concurrent change / directory swap at the exact TOCTOU window ─────────────

    [Fact]
    public async Task HashlineEdit_RacingWithAHostWriter_ShouldNeverOverwriteTheNewerVersion()
    {
        var snapshot = await _hashline.ReadAsync(Shadow(), _shadow);
        var attackerWrote = false;
        SafeTreeWriter.TestHook = _ =>
        {
            try
            {
                File.WriteAllText(Shadow(), "// somebody else's newer version\n");
                attackerWrote = true;
            }
            catch (IOException)
            {
            }
        };

        var result = await _hashline.EditAsync(new HashlineEditRequest
        {
            FilePath = Shadow(),
            SnapshotId = snapshot.SnapshotId,
            FileFingerprint = snapshot.FileFingerprint,
            Operations = [new ReplaceRangeOperation { Type = "replace_range", StartLine = 1, StartAnchorId = snapshot.Lines[0].AnchorId, EndLine = 1, EndAnchorId = snapshot.Lines[0].AnchorId, NewLines = ["// ours"] }]
        }, _shadow);

        Assert.Equal(OperatingSystem.IsWindows(), result.Success);   // Windows excludes the attacker, Linux detects and restores
        if (result.Success)
        {
            Assert.False(attackerWrote);
            Assert.Contains("// ours", File.ReadAllText(Shadow()), StringComparison.Ordinal);
        }
        else
        {
            Assert.True(attackerWrote);
            Assert.Equal("// somebody else's newer version\n", File.ReadAllText(Shadow()));
        }
    }

    [Fact]
    public async Task EditFile_RacingWithAHostWriter_ShouldNeverOverwriteTheNewerVersion()
    {
        var attackerWrote = false;
        SafeTreeWriter.TestHook = _ =>
        {
            try
            {
                File.WriteAllText(Shadow(), "// newer host version\n");
                attackerWrote = true;
            }
            catch (IOException)
            {
            }
        };

        var edit = new EditFileTool(NullLogger<EditFileTool>.Instance, null);
        var result = await edit.ExecuteAsync(ModelArgs(("path", "src/PriceCalculator.cs"), ("old_string", "Rate = 1"), ("new_string", "Rate = 4")));

        Assert.Equal(OperatingSystem.IsWindows(), result.Status == ToolResultStatus.Success);
        if (result.Status == ToolResultStatus.Success)
        {
            Assert.False(attackerWrote);
        }
        else
        {
            Assert.True(attackerWrote);
            Assert.Equal("// newer host version\n", File.ReadAllText(Shadow()));
        }
    }

    [Fact]
    public async Task DeleteFile_RacingWithAHostWriter_ShouldNeverDeleteNewerHostData()
    {
        var attackerWrote = false;
        SafeTreeWriter.TestHook = _ =>
        {
            try
            {
                File.WriteAllText(Shadow(), "// precious newer data\n");
                attackerWrote = true;
            }
            catch (IOException)
            {
            }
        };

        var result = await new DeleteFileTool(NullLogger<DeleteFileTool>.Instance, null).ExecuteAsync(ModelArgs(("path", "src/PriceCalculator.cs")));

        if (result.Status == ToolResultStatus.Success)
        {
            Assert.False(attackerWrote);
            Assert.False(File.Exists(Shadow()));
        }
        else
        {
            Assert.True(attackerWrote);
            Assert.Equal("// precious newer data\n", File.ReadAllText(Shadow()));
        }
    }

    [Fact]
    public async Task WriteTools_WithTheDirectorySwappedForALinkAtTheLastMoment_ShouldNeverEscapeTheRoot()
    {
        var swapped = false;
        SafeTreeWriter.TestHook = _ =>
        {
            if (swapped)
            {
                return;
            }

            try
            {
                Directory.Move(Shadow("src"), Shadow("src_original"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;   // Windows: the pinned directory cannot be renamed
            }

            swapped = TryDirectoryLink(Shadow("src"), _outside);
        };

        var write = new WriteFileTool(NullLogger<WriteFileTool>.Instance, null);
        await write.ExecuteAsync(ModelArgs(("path", "src/Created.cs"), ("content", "class Created{}")));
        await new EditFileTool(NullLogger<EditFileTool>.Instance, null).ExecuteAsync(ModelArgs(("path", "src/PriceCalculator.cs"), ("old_string", "Rate = 1"), ("new_string", "Rate = 7")));

        Assert.Empty(Directory.EnumerateFileSystemEntries(_outside));
        AssertMainUntouched();
    }

    [Fact]
    public async Task WriteTools_ShouldRefuseALinkedParentDirectory()
    {
        Assert.True(TryDirectoryLink(Shadow("linked"), _outside), "could not create a directory link on this host");
        var write = new WriteFileTool(NullLogger<WriteFileTool>.Instance, null);
        var mkdir = new CreateDirectoryTool(NullLogger<CreateDirectoryTool>.Instance);
        var patch = new ApplyPatchTool(new Mock<IGitService>().Object, _hashline, NullLogger<ApplyPatchTool>.Instance);

        AssertRefused(await write.ExecuteAsync(ModelArgs(("path", "linked/x.txt"), ("content", "x"))));
        AssertRefused(await mkdir.ExecuteAsync(ModelArgs(("path", "linked/sub"))));
        Assert.NotEqual(ToolResultStatus.Success, (await patch.ExecuteAsync(ModelArgs(("patch", "*** Begin Patch\n*** Add File: linked/p.cs\n+x\n*** End Patch\n")))).Status);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_outside));
    }

    private static bool TryDirectoryLink(string link, string target)
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
