// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using CodexFlow.Core.Services;
using Xunit;

namespace CodexFlow.Core.Tests.Agents;

/// <summary>
/// The model audit is only supposed to answer for what a change touched. The host therefore derives the touched line ranges from
/// <c>git diff -U0</c> (never from the model) between the chain's starting commit and the head.
/// </summary>
public sealed class GitChangedLineRangesTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cf-changed-lines-" + Guid.NewGuid().ToString("N"));
    private string _root = string.Empty;
    private readonly GitArtifactInspector _inspector = new();

    public async ValueTask InitializeAsync()
    {
        _root = Path.Combine(_directory, "repo");
        Directory.CreateDirectory(_root);
        await Git("init");
        await Git("config", "user.name", "Artifact Test");
        await Git("config", "user.email", "test@example.invalid");
    }

    public ValueTask DisposeAsync()
    {
        foreach (var file in Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_directory, recursive: true);
        return ValueTask.CompletedTask;
    }

    private static string Lines(int count, params (int Line, string Text)[] replacements)
    {
        var map = replacements.ToDictionary(r => r.Line, r => r.Text);
        return string.Join('\n', Enumerable.Range(1, count).Select(i => map.TryGetValue(i, out var text) ? text : "line" + i)) + "\n";
    }

    [Fact]
    public void Parse_ReadsModifiedAddedAndDeletedHunks()
    {
        const string diff = """
            diff --git a/src/A.cs b/src/A.cs
            index 1..2 100644
            --- a/src/A.cs
            +++ b/src/A.cs
            @@ -55 +55 @@ ns
            -old
            +new
            @@ -59,0 +60,2 @@ ns
            +added1
            +added2
            @@ -70,3 +72,0 @@ ns
            -gone1
            -gone2
            -gone3
            """;

        var ranges = GitArtifactInspector.ParseChangedLineRanges(diff);

        var a = Assert.Single(ranges);
        Assert.Equal("src/A.cs", a.Key);
        // 55 modified, 60-61 added, and a pure deletion shows up as the position between lines 72 and 73.
        Assert.Equal([new ChangedLineRange(55, 55), new ChangedLineRange(60, 61), new ChangedLineRange(72, 73)], a.Value);
    }

    [Fact]
    public void Parse_KeepsWhatAModifiedOrDeletedHunkReadBefore_AndNothingForPureAdditions()
    {
        const string diff = """
            diff --git a/src/A.cs b/src/A.cs
            index 1..2 100644
            --- a/src/A.cs
            +++ b/src/A.cs
            @@ -55 +55 @@ ns
            -return items.Skip(page * pageSize);
            +return items.Skip((page - 1) * pageSize);
            @@ -59,0 +60,2 @@ ns
            +added1
            +added2
            @@ -70,3 +72,0 @@ ns
            -gone1
            --- looks like a header but is a removed line
            -gone3
            diff --git a/src/B.cs b/src/B.cs
            --- a/src/B.cs
            +++ b/src/B.cs
            @@ -1 +1 @@
            -b-old
            +b-new
            """;

        var ranges = GitArtifactInspector.ParseChangedLineRanges(diff);

        var a = ranges["src/A.cs"];
        Assert.Equal("return items.Skip(page * pageSize);", a[0].Before);
        Assert.Null(a[1].Before);
        Assert.Equal("gone1\n-- looks like a header but is a removed line\ngone3", a[2].Before);
        Assert.Equal("b-old", Assert.Single(ranges["src/B.cs"]).Before);
        // The before-text is context, not identity: a range is still equal to one with the same position.
        Assert.Equal(new ChangedLineRange(55, 55), a[0]);
    }

    [Fact]
    public void Parse_NewFileCoversEveryLine_DeletedFileAndUnquotableNamesAreOmitted()
    {
        const string diff = """
            diff --git a/n.cs b/n.cs
            new file mode 100644
            --- /dev/null
            +++ b/n.cs
            @@ -0,0 +1,40 @@
            +x
            diff --git a/d.cs b/d.cs
            deleted file mode 100644
            --- a/d.cs
            +++ /dev/null
            @@ -1,9 +0,0 @@
            -x
            diff --git "a/s p\303\251.cs" "b/s p\303\251.cs"
            --- "a/s p\303\251.cs"
            +++ "b/s p\303\251.cs"
            @@ -1 +1 @@
            -a
            +b
            """;

        var ranges = GitArtifactInspector.ParseChangedLineRanges(diff);

        // Omitted = unknown = the caller must treat every line of that file as changed.
        Assert.Equal(["n.cs"], ranges.Keys);
        Assert.Equal([new ChangedLineRange(1, 40)], ranges["n.cs"]);
    }

    [Fact]
    public async Task RealGit_ReportsTheLinesTheChainTouched_AcrossSeveralCommits()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "A.cs"), Lines(100));
        await File.WriteAllTextAsync(Path.Combine(_root, "B.cs"), Lines(10));
        await Commit("base");
        var baseline = await Head();
        await File.WriteAllTextAsync(Path.Combine(_root, "A.cs"), Lines(100, (10, "changed10")));
        await Commit("one");
        await File.WriteAllTextAsync(Path.Combine(_root, "A.cs"), Lines(100, (10, "changed10"), (80, "changed80")));
        await File.WriteAllTextAsync(Path.Combine(_root, "C.cs"), Lines(5));
        await Commit("two");
        var head = await Head();

        var ranges = await _inspector.GetChangedLineRangesAsync(_root, baseline, head, ["A.cs", "B.cs", "C.cs"]);

        Assert.NotNull(ranges);
        Assert.Equal([new ChangedLineRange(10, 10), new ChangedLineRange(80, 80)], ranges!["A.cs"]);
        Assert.Equal([new ChangedLineRange(1, 5)], ranges["C.cs"]);
        Assert.False(ranges.ContainsKey("B.cs")); // untouched: nothing in it was changed by the chain
    }

    [Fact]
    public async Task UnknownBase_YieldsNull_SoNothingIsSetAside()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "A.cs"), Lines(3));
        await Commit("base");
        var head = await Head();

        Assert.Null(await _inspector.GetChangedLineRangesAsync(_root, new string('0', 40), head, ["A.cs"]));
        Assert.Null(await _inspector.GetChangedLineRangesAsync(_root, "not-a-commit", head, ["A.cs"]));
    }

    [Theory]
    [InlineData(10, 10, 3, 11, 12, true)]    // inside the margin of the changed line
    [InlineData(10, 10, 3, 13, 14, true)]    // exactly at the margin
    [InlineData(10, 10, 3, 14, 20, false)]   // beyond it
    [InlineData(10, 10, 3, 1, 6, false)]
    [InlineData(10, 10, 3, 1, 7, true)]
    [InlineData(10, 12, 0, 12, 12, true)]    // margin 0: only an overlap counts
    [InlineData(10, 12, 0, 13, 13, false)]
    public void Touches_RespectsTheMargin(int rangeStart, int rangeEnd, int margin, int start, int end, bool expected) =>
        Assert.Equal(expected, ChangedLineRange.Touches([new ChangedLineRange(rangeStart, rangeEnd)], start, end, margin));

    private async Task Commit(string message)
    {
        await Git("add", ".");
        await Git("commit", "-m", message);
    }

    private async Task<string> Head() => (await Git("rev-parse", "HEAD")).Trim();

    private async Task<string> Git(params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
        return output;
    }
}
