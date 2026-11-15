// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using Xunit;

namespace CodexFlow.Core.Tests.Agents;

public sealed class ForgeChangeRelevanceTests
{
    private static GitFileChange Change(string path, string status) => new(path, status);

    [Fact]
    public void NoChanges_AreNotRelevant()
        => Assert.False(ForgeChangeRelevance.HasRelevantChange([]));

    [Theory]
    [InlineData("T1-offline-verification.md")]
    [InlineData("notes/progress.txt")]
    [InlineData("run.log")]
    [InlineData("NOTES.MD")]
    [InlineData("docs/analysis.rst")]
    public void OnlyNewDocumentationFiles_AreWorkerNotes_NotAChangeToTheProject(string path)
        => Assert.False(ForgeChangeRelevance.HasRelevantChange([Change(path, "A")]));

    [Fact]
    public void NotesNextToARealChange_AreFine()
        => Assert.True(ForgeChangeRelevance.HasRelevantChange([Change("T1-notes.md", "A"), Change("src/App/App.csproj", "M")]));

    [Theory]
    [InlineData("src/App/App.csproj", "M")]
    [InlineData("src/NewService.cs", "A")]
    [InlineData("README.md", "M")]   // editing an existing document is a change to the project
    [InlineData("old/Gone.md", "D")] // so is deleting one
    [InlineData("config/settings.json", "A")]
    public void AChangeToTheProject_IsRelevant(string path, string status)
        => Assert.True(ForgeChangeRelevance.HasRelevantChange([Change(path, status)]));

    [Theory]
    [InlineData("docs/guide.md", "docs/guide.md")]
    [InlineData("docs/guide.md", "guide.md")]
    [InlineData("docs\\guide.md", "docs/guide.md")]
    public void ADocumentTheTaskNamesItself_IsTheRequestedChange(string changed, string named)
        => Assert.True(ForgeChangeRelevance.HasRelevantChange([Change(changed, "A")], [named]));

    [Fact]
    public void ADocumentTheTaskDoesNotName_StaysNotes_EvenWhenOthersAreNamed()
        => Assert.False(ForgeChangeRelevance.HasRelevantChange([Change("T1-notes.md", "A")], ["docs/guide.md"]));

    [Fact]
    public void NoteOnlyPaths_ListsOnlyTheNotes()
        => Assert.Equal(["a.md"], ForgeChangeRelevance.NoteOnlyPaths([Change("a.md", "A"), Change("b.cs", "A")]));
}
