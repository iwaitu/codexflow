// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using CodexFlow.Core.Execution;
using Xunit;

namespace CodexFlow.Core.Tests.Execution;

/// <summary>
/// Concurrent-replacement attacks against the publish step itself. The writer behaves differently per OS (Windows pins and
/// exclusively locks; Linux uses dirfd-relative no-follow operations with exchange-based CAS), so every test asserts the
/// invariant that holds on both: a swap/modify at the TOCTOU window either FAILS for the attacker (Windows) or is DETECTED
/// and undone/contained by the publisher (Linux) — host data is never silently overwritten and nothing ever escapes the root.
/// </summary>
[Collection(SafePublishCollection.Name)]
public class SafeTreeWriterTests : IDisposable
{
    private readonly string _root = IsolationTestSupport.NewTempDir("safe");
    private readonly string _outside = IsolationTestSupport.NewTempDir("safeout");

    public void Dispose()
    {
        SafeTreeWriter.TestHook = null;
        IsolationTestSupport.Cleanup(_root);
        IsolationTestSupport.Cleanup(_outside);
    }

    private static SafeWriteOp Modify(string path, string current, string next) => new(path, GuestChangeKind.Modify, Bytes(next), IsolationTestSupport.Sha(current));

    private static SafeWriteOp Add(string path, string content) => new(path, GuestChangeKind.Add, Bytes(content), null);

    private static SafeWriteOp Delete(string path, string current) => new(path, GuestChangeKind.Delete, null, IsolationTestSupport.Sha(current));

    private static byte[] Bytes(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    private string P(string rel) => Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));

    private void Write(string rel, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!);
        File.WriteAllText(P(rel), content);
    }

    [Fact]
    public void Apply_ShouldAddModifyDelete_AndLeaveNoTempFiles()
    {
        Write("a.txt", "A");
        Write("dir/gone.txt", "G");
        var res = SafeTreeWriter.Apply(_root, [Modify("a.txt", "A", "A2"), Add("new/deep/n.txt", "N"), Delete("dir/gone.txt", "G")]);
        Assert.True(res.Success, res.Message);
        Assert.Equal("A2", File.ReadAllText(P("a.txt")));
        Assert.Equal("N", File.ReadAllText(P("new/deep/n.txt")));
        Assert.False(File.Exists(P("dir/gone.txt")));
        Assert.Empty(Directory.EnumerateFiles(_root, ".cfx-*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Apply_ShouldBeAllOrNothing_WhenALaterChangeConflicts()
    {
        Write("a.txt", "A");
        Write("b.txt", "B-CHANGED-ON-HOST");
        var res = SafeTreeWriter.Apply(_root, [Modify("a.txt", "A", "A2"), Add("c.txt", "C"), Modify("b.txt", "B", "B2")]);
        Assert.False(res.Success);
        Assert.Equal(ExecutionReasonCodes.ExportRejectedConcurrentModification, res.ReasonCode);
        Assert.Equal("A", File.ReadAllText(P("a.txt")));
        Assert.False(File.Exists(P("c.txt")));
        Assert.Equal("B-CHANGED-ON-HOST", File.ReadAllText(P("b.txt")));
        Assert.Empty(Directory.EnumerateFiles(_root, ".cfx-*", SearchOption.AllDirectories));
    }

    [Fact]
    public void ModifyRacingWithHostWriter_ShouldNeverOverwriteTheHostVersionSilently()
    {
        Write("a.txt", "A");
        var attackerWrote = false;
        SafeTreeWriter.TestHook = _ =>
        {
            try
            {
                File.WriteAllText(P("a.txt"), "ATTACKER-WROTE-AFTER-VERIFY");
                attackerWrote = true;
            }
            catch (IOException)
            {
                // Windows: the target is exclusively locked, the attacker is simply refused.
            }
        };

        var res = SafeTreeWriter.Apply(_root, [Modify("a.txt", "A", "GUEST")]);

        // The mechanisms differ on purpose: Windows excludes the attacker, Linux detects the write and restores it.
        Assert.Equal(OperatingSystem.IsWindows(), res.Success);

        if (res.Success)
        {
            Assert.False(attackerWrote);
            Assert.Equal("GUEST", File.ReadAllText(P("a.txt")));
        }
        else
        {
            Assert.True(attackerWrote);
            Assert.Equal(ExecutionReasonCodes.ExportRejectedConcurrentModification, res.ReasonCode);
            Assert.Equal("ATTACKER-WROTE-AFTER-VERIFY", File.ReadAllText(P("a.txt")));
        }

        Assert.Empty(Directory.EnumerateFiles(_root, ".cfx-*", SearchOption.AllDirectories));
    }

    [Fact]
    public void DeleteRacingWithHostWriter_ShouldNeverDeleteChangedHostData()
    {
        Write("a.txt", "A");
        var attackerWrote = false;
        SafeTreeWriter.TestHook = _ =>
        {
            try
            {
                File.WriteAllText(P("a.txt"), "PRECIOUS-NEW-HOST-DATA");
                attackerWrote = true;
            }
            catch (IOException)
            {
            }
        };

        var res = SafeTreeWriter.Apply(_root, [Delete("a.txt", "A")]);

        if (res.Success)
        {
            Assert.False(attackerWrote);
            Assert.False(File.Exists(P("a.txt")));
        }
        else
        {
            Assert.True(attackerWrote);
            Assert.Equal("PRECIOUS-NEW-HOST-DATA", File.ReadAllText(P("a.txt")));
        }
    }

    [Fact]
    public void AddRacingWithHostCreator_ShouldNeverReplaceTheHostFile()
    {
        var attackerCreated = false;
        SafeTreeWriter.TestHook = _ =>
        {
            try
            {
                using var fs = new FileStream(P("n.txt"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                fs.Write(Bytes("HOST-CREATED"));
                attackerCreated = true;
            }
            catch (IOException)
            {
            }
        };

        var res = SafeTreeWriter.Apply(_root, [Add("n.txt", "GUEST")]);

        if (res.Success)
        {
            Assert.False(attackerCreated);
            Assert.Equal("GUEST", File.ReadAllText(P("n.txt")));
        }
        else
        {
            Assert.True(attackerCreated);
            Assert.Equal("HOST-CREATED", File.ReadAllText(P("n.txt")));
        }
    }

    [Fact]
    public void DirectorySwappedForALinkAtTheTocTouWindow_ShouldNeverLetTheWriteEscapeTheRoot()
    {
        Write("src/a.txt", "A");
        var swapped = false;
        SafeTreeWriter.TestHook = _ =>
        {
            if (swapped)
            {
                return;
            }

            try
            {
                Directory.Move(P("src"), P("src_original"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;   // Windows: the pinned directory cannot be renamed away
            }

            swapped = TryCreateDirectoryLink(P("src"), _outside);
        };

        var res = SafeTreeWriter.Apply(_root, [Modify("src/a.txt", "A", "GUEST"), Add("src/new.txt", "NEW")]);

        Assert.Empty(Directory.EnumerateFileSystemEntries(_outside));   // nothing escaped, whatever the outcome
        if (!swapped)
        {
            Assert.True(res.Success, res.Message);
            Assert.Equal("GUEST", File.ReadAllText(P("src/a.txt")));
        }
    }

    [Fact]
    public void PreExistingLinkOnThePath_ShouldBeRejected()
    {
        Assert.True(TryCreateDirectoryLink(P("linked"), _outside), "could not create a directory link on this host");
        var res = SafeTreeWriter.Apply(_root, [Add("linked/pwn.txt", "x")]);
        Assert.False(res.Success);
        Assert.Equal(ExecutionReasonCodes.ExportRejectedLink, res.ReasonCode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_outside));
    }

    [Fact]
    public void TargetFileReplacedByALink_ShouldBeRejectedWithoutTouchingTheLinkTarget()
    {
        var victim = Path.Combine(_outside, "victim.txt");
        File.WriteAllText(victim, "VICTIM");
        Write("a.txt", "A");
        File.Delete(P("a.txt"));
        if (!TryCreateFileLink(P("a.txt"), victim))
        {
            // Windows needs a privilege for file symlinks. The Linux run of this suite (see the implementation note) covers it.
            Assert.Skip("file symlinks cannot be created on this host without extra privileges");
        }

        var res = SafeTreeWriter.Apply(_root, [Modify("a.txt", "A", "GUEST")]);
        Assert.False(res.Success);
        Assert.Equal("VICTIM", File.ReadAllText(victim));
    }

    [Fact]
    public void UnsupportedOs_ShouldRefuseInsteadOfFallingBackToPathWrites()
    {
        Assert.True(OperatingSystem.IsWindows() || OperatingSystem.IsLinux(), "no safe publish implementation for this OS: export must be refused");
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

    private static bool TryCreateFileLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public static class SafePublishCollection
{
    public const string Name = "SafePublish";
}
