// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Xunit;
using CodexFlow.Core.Agents;

namespace CodexFlow.Core.Tests.Planning;

/// <summary>
/// A repository bound as the project root is "already there": the model is told not to clone and has nothing to say about it unless it calls a
/// tool. Some runs called none and wrote a directory tree out of habit (tests/, main.py, .env.example) that did not exist. The host lists what is
/// really in the root, so that what the model says about the layout has a real source.
/// </summary>
public class ProjectRootListingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codexflow-rootlisting-" + Guid.NewGuid().ToString("N"));

    public ProjectRootListingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public void ListsWhatIsReallyInTheTopLevel_DirectoriesMarked_AndNothingThatIsNotThere()
    {
        File.WriteAllText(Path.Combine(_root, "pager.py"), "x");
        File.WriteAllText(Path.Combine(_root, "test_pager.py"), "x");
        File.WriteAllText(Path.Combine(_root, "README.md"), "x");
        Directory.CreateDirectory(Path.Combine(_root, ".git"));

        var text = ProjectRootListing.Describe(_root)!;

        Assert.Contains("- pager.py", text);
        Assert.Contains("- test_pager.py", text);
        Assert.Contains("- README.md", text);
        Assert.Contains("- .git/", text);
        Assert.DoesNotContain("main.py", text);
        Assert.DoesNotContain("tests/", text);
    }

    [Fact]
    public void ListsOnlyTheTopLevel_TheContentsOfADirectoryAreLeftToTheTools()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "inner.cs"), "x");

        var text = ProjectRootListing.Describe(_root)!;

        Assert.Contains("- src/", text);
        Assert.DoesNotContain("inner.cs", text);
    }

    [Fact]
    public void IsBounded_TheRestIsOnlyCounted()
    {
        for (var i = 0; i < ProjectRootListing.MaxEntries + 15; i++)
        {
            File.WriteAllText(Path.Combine(_root, $"f{i:D3}.txt"), "x");
        }

        var text = ProjectRootListing.Describe(_root)!;

        Assert.Equal(ProjectRootListing.MaxEntries, text.Split('\n').Count(static line => line.StartsWith("- ", StringComparison.Ordinal)));
        Assert.Contains("另有 15 项未列出", text);
    }

    [Fact]
    public void ThereIsNothingToSayAboutAMissingEmptyOrUnnamedRoot()
    {
        Assert.Null(ProjectRootListing.Describe(Path.Combine(_root, "nope")));
        Assert.Null(ProjectRootListing.Describe(null));
        Assert.Null(ProjectRootListing.Describe("  "));
        Assert.Null(ProjectRootListing.Describe(_root));   // empty
    }

    [Fact]
    public void ANameIsDataNotText_ControlCharactersAreDroppedAndALongNameIsCut()
    {
        Assert.Equal("a b c", ProjectRootListing.SafeName("a\nb\r\tc"));
        var cut = ProjectRootListing.SafeName(new string('x', ProjectRootListing.MaxNameLength + 50));
        Assert.True(cut.Length <= ProjectRootListing.MaxNameLength + 1);   // the cut plus the ellipsis
        Assert.EndsWith("…", cut, StringComparison.Ordinal);
    }
}
